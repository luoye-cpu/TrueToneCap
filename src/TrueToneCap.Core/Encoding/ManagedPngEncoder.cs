// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/ManagedPngEncoder.cs
// 托管 PNG 编码器 — 零原生依赖，使用 System.IO.Compression
// 支持: 8-bit / 16-bit, RGBA, iCCP chunk, cICP chunk, 最大压缩
// 2026-10-31：IDAT 改成流式分块写出（压缩字节直接落成多条 chunk，不再物化整帧压缩结果）。

using System.IO.Compression;
using System.Buffers.Binary;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 PNG 编码器 — 完全替代 Magick.NET 的 PNG 路径。</summary>
public static class ManagedPngEncoder
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// 一条 IDAT 的载荷上限（PNG 允许多条 IDAT，把各条数据首尾相接才是完整 zlib 流）。
    /// <para>为什么取 64 KiB（本仓可指出的两条理由，不是"别人也这么干"）：
    /// ① 每条 chunk 的固定开销是 12 字节（4 长度 + 4 类型 + 4 CRC），64 KiB 时它占产物的
    ///    0.018% —— 再小到 4 KiB 就变 0.3%，纯 UI 帧那种 ~200 KB 产物上会被看出来；
    /// ② 65,536 B 恰好在 x64 的 LOH 门槛（85,000 B，见 SavePathBench 顶部同一口径）之下，
    ///    于是 sink 那份【整帧复用】的缓冲不进大对象堆，一次保存只分配它一份。</para>
    /// <para>与 libpng / 常见编码器的写出粒度同量级；PNG 3.0 对单条 chunk 只设
    /// "长度 &lt; 2^31" 的上界，故这个数远够不着规范限制。改这个常数会改产物字节（chunk 边界变了），
    /// 但【不会】改解压结果 —— 承诺是"拼接后的 zlib 码流逐字节相同"。</para>
    /// </summary>
    private const int IdatPayloadBytes = 64 * 1024;

    /// <summary>
    /// 「优」档额外的固定滤波候选（PNG filter type）：1=Sub、4=Paeth。
    /// <para>除这两条固定候选外，<c>Optimal</c> 还会试一条 Entropy 打分扫描线和一条
    /// 8 行分带、带内实际 level-1 压缩比大小的 brute 候选；三条都是整帧候选，最终统一压
    /// level 9 取最小。候选里本来还包含当前自适应结果，因此本档不会比 Smallest 更大。</para>
    /// <para>不吸收 oxipng 的整帧多策略 + 行/块 brute 全量搜索：那会把 4K 从数秒推到十几秒；
    /// 这里的 brute 只做 8 行带、带内 5 种 PNG 滤波，成本约为一次额外 level-1 压缩。</para>
    /// </summary>
    private static readonly byte[] OptimalExtraFilters = [1, 4];

    /// <summary>逐行滤波打分策略。SumAbs 是历史默认；Entropy 是「优」档额外候选，
    /// 按该行字节分布的集中度估计 deflate 的编码成本（越集中越好）。</summary>
    private enum FilterScoreMode { SumAbs, Entropy }

    /// <summary>编码 BGRA 像素为 PNG 文件。</summary>
    /// <param name="bgra">BGRA 像素数据。</param>
    /// <param name="w">宽度。</param>
    /// <param name="h">高度。</param>
    /// <param name="path">输出路径。</param>
    /// <param name="bitDepth">输出位深 (8/10/12/16)。10/12-bit 写入真位深 + sBIT, 16-bit 存储为 16。</param>
    /// <param name="iccProfile">可选 ICC profile (写入 iCCP chunk)。</param>
    /// <param name="cicp">可选 CICP 4 字节 [primaries, transfer, matrix, range]。</param>
    /// <param name="effort">压缩强度档（2026-10-29）。默认 Smallest = 本文件历史上硬编码的
    /// level 9；2026-10-31 起压缩字节可能来自 libdeflate，承诺改"解压后逐字节相同"。</param>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int bitDepth = 8, byte[]? iccProfile = null, byte[]? cicp = null,
        CompressionEffort effort = CompressionEffort.Smallest)
    {
        // 2026-09-19：原为 File.Create(path) 直写 —— 中途任何异常都会在最终路径留下一份
        // 已写了签名/IHDR 却没有 IEND 的坏 PNG，而上层照样按 ResolveActualPath 回来的那个路径报告"已保存"
        // （见 CapturePipelineService 的 File.Exists/长度统计）。现走同目录临时文件 + 成功后替换，
        // 理由与失败语义见 AtomicFileWriter。
        AtomicFileWriter.Write(path, fs => Encode(bgra, w, h, fs, bitDepth, iccProfile, cicp, effort));
    }

    /// <summary>快速编码 8-bit PNG（Filter=None + Fastest 压缩）。用于临时中间文件。</summary>
    /// <param name="cicp">可选 CICP 4 字节 [primaries, transfer, matrix, range]（2026-11-02：动图
    /// 中间帧也带声明 —— 像素已被 PreparePixelsWithIcc 烘焙到目标色域，中间文件不该"裸奔"）。</param>
    public static void EncodeFast(byte[] bgra, int w, int h, string path,
        byte[]? iccProfile = null, byte[]? cicp = null)
    {
        AtomicFileWriter.Write(path, fs => EncodeFast(bgra, w, h, fs, iccProfile, cicp));
    }

    /// <summary>快速编码 8-bit PNG 到 Stream（Filter=None + Fastest 压缩）。用于 stdin 管道传输。</summary>
    public static void EncodeFast(byte[] bgra, int w, int h, Stream output,
        byte[]? iccProfile = null, byte[]? cicp = null)
    {
        int rowBytes = w * 4;
        output.Write(PngSignature);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        WriteChunk(output, "IHDR"u8, ihdr);

        // chunk 顺序与 Encode 保持同一约定：cICP 在前、iCCP 在后（两者都在 IDAT 之前的保留区）
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
            WriteChunk(output, "iCCP"u8, BuildIccpChunk(iccProfile));

        // Filter=None 扫描线（无自适应选择开销）
        var raw = new byte[h * (1 + rowBytes)];
        for (int y = 0; y < h; y++)
        {
            int rawOff = y * (1 + rowBytes);
            raw[rawOff] = 0; // None
            int srcOff = y * w * 4;
            int dstOff = rawOff + 1;
            for (int x = 0; x < w; x++)
            {
                int si = srcOff + x * 4, di = dstOff + x * 4;
                raw[di] = bgra[si + 2]; raw[di + 1] = bgra[si + 1];
                raw[di + 2] = bgra[si]; raw[di + 3] = bgra[si + 3];
            }
        }

        // Fastest 压缩 (ZLibStream 已包含 zlib 头 + Adler32)
        WriteIdatFastest(output, raw);

        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>编码 BGRA 像素为 PNG 流。</summary>
    public static void Encode(byte[] bgra, int w, int h, Stream output,
        int bitDepth = 8, byte[]? iccProfile = null, byte[]? cicp = null,
        CompressionEffort effort = CompressionEffort.Smallest)
    {
        // PNG 3.0 (ISO/IEC 15948:2023) Table 11: Color type 6 (RGBA) 只允许 8 和 16
        // 10/12-bit 源数据使用 16-bit 容器 + sBIT 标记实际有效位
        // 参见 12.4 Sample depth scaling
        bool isHighBitDepth = bitDepth > 8;
        int outDepth = isHighBitDepth ? 16 : 8;
        int bytesPerChannel = outDepth > 8 ? 2 : 1;
        int channels = 4;
        int stride = w * channels * bytesPerChannel;

        output.Write(PngSignature);

        // IHDR
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;       // bit depth: 8 或 16 (Color type 6 只允许这俩值)
        ihdr[9] = 6;                     // color type: RGBA
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当实际位深不足 16 时标记有效位（PNG 3.0 11.3.2.4）
        // 注意: SDR 输入的 byte[] 只有 8-bit 精度，sBIT 标记实际精度而非容器位深
        if (isHighBitDepth)
        {
            // byte[] 输入只有 8-bit 精度，用 *257 扩展到 16-bit 容器
            byte[] sbit = [8, 8, 8, 8]; // RGBA 各通道: 实际精度 8-bit
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP / iCCP — 两者都写（PNG 3.0 RFC 9327 推荐）
        // cICP 为现代解码器提供紧凑色彩描述，iCCP 为老旧解码器提供回退
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            var iccpData = BuildIccpChunk(iccProfile);
            WriteChunk(output, "iCCP"u8, iccpData);
        }

        // IDAT
        // 2026-10-31：分块流式写出（见 WriteIdat）—— 整帧压缩字节不再落成一份 byte[]。
        var rawData = BuildRawScanlines(bgra, w, h, outDepth);
        if (effort == CompressionEffort.Optimal)
            WriteIdatOptimal(output, rawData, effort,
                filter => BuildRawScanlines(bgra, w, h, outDepth, filter),
                mode => BuildRawScanlines(bgra, w, h, outDepth, null, mode),
                () => BuildRawScanlinesBrute(bgra, w, h, outDepth));
        else
            WriteIdat(output, rawData, effort);

        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>
    /// C1 fix: 编码真 16-bit BGRA 像素为 16-bit PNG（HDR 路径专用）。
    /// 输入: 每像素 8 字节 (B16 G16 R16 A16, big-endian)，由 Rgba16ToBgra16Bytes 生成。
    /// 支持 IHDR 10/12/16-bit depth（PNG 3.0）。
    /// </summary>
    /// <param name="fastest">true = 用最快压缩（用于临时中转文件；压缩率不影响最终产物）。</param>
    /// <param name="effort">压缩强度档（2026-10-29）。只在 <paramref name="fastest"/> 为 false 时生效；
    /// 默认 Smallest = 本文件历史上硬编码的 CompressionLevel.SmallestSize。</param>
    public static void Encode16(byte[] bgra16, int w, int h, string path, byte[]? cicp = null, byte[]? iccProfile = null, int bitDepth = 16, bool fastest = false,
        CompressionEffort effort = CompressionEffort.Smallest, bool omitAlpha = false)
    {
        AtomicFileWriter.Write(path, fs => Encode16(bgra16, w, h, fs, cicp, iccProfile, bitDepth, fastest, effort, omitAlpha));
    }

    /// <summary>编码真 16-bit BGRA 像素为 16-bit PNG 流。</summary>
    public static void Encode16(byte[] bgra16, int w, int h, Stream output, byte[]? cicp = null, byte[]? iccProfile = null, int bitDepth = 16, bool fastest = false,
        CompressionEffort effort = CompressionEffort.Smallest, bool omitAlpha = false)
    {
        // PNG 3.0 (ISO/IEC 15948:2023) Table 11: Color type 6 (RGBA) 只允许 8 和 16
        // 10/12-bit 源数据使用 16-bit 容器 + sBIT 标记实际有效位
        // 参见 12.4 Sample depth scaling
        int outDepth = 16;

        // PNG Signature
        output.Write(PngSignature);

        // IHDR
        // ⚠ omitAlpha（2026-11-04）：写 color type 2（RGB，无 alpha 通道）而不是 6（RGBA）。
        //   存在的理由是 avifenc 的行为实测：**16-bit PNG 输入一律被当成"带 alpha"**，
        //   即便 alpha 全为满值也照样产出 alpha 副 item（infe=2 + auxC）——
        //   拿 8-bit RGB（color type 2）输入则只有一个 item。增益图容器只搬主图，
        //   底图若带 alpha 副 item 就被写出器的"底图含副 item"门拒写。
        //   截图恒不透明 ⇒ 丢掉 alpha 通道不损失任何信息。
        byte colorType = (byte)(omitAlpha ? 2 : 6);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = (byte)outDepth;        // bit depth: 16 (Color type 2/6 只允许 8/16)
        ihdr[9] = colorType;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WriteChunk(output, "IHDR"u8, ihdr);

        // sBIT — 当实际位深不足 16 时标记有效位（PNG 3.0 11.3.2.4；每通道一个字节，RGB 三份）
        if (bitDepth < 16)
        {
            byte sbitVal = bitDepth switch { 12 => 12, 10 => 10, _ => 8 };
            byte[] sbit = omitAlpha
                ? [sbitVal, sbitVal, sbitVal]
                : [sbitVal, sbitVal, sbitVal, sbitVal];
            WriteChunk(output, "sBIT"u8, sbit);
        }

        // cICP
        if (cicp is { Length: 4 })
            WriteChunk(output, "cICP"u8, cicp);

        // iCCP（与 cICP 共存，RFC 9327 推荐）
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            var iccpData = BuildIccpChunk(iccProfile);
            WriteChunk(output, "iCCP"u8, iccpData);
        }

        // IDAT
        // ═══ 2026-09-16：中转用途可走快速压缩 ═══
        // 16-bit PNG 也被当作 HDR 编码的**临时中转**（如 AVIF HDR：scRGB→PQ16→PNG→avifenc）。
        // 那种场景文件用完即删，压缩率毫无意义，而 SmallestSize 在 4K 上要多花数秒 ——
        // 实测这正是 4K HDR AVIF 前处理的主要耗时。故提供 fastest 开关。
        var rawData = BuildRawScanlines16(bgra16, w, h, bitDepth, omitAlpha);
        if (fastest) WriteIdatFastest(output, rawData);
        else if (effort == CompressionEffort.Optimal)
            WriteIdatOptimal(output, rawData, effort,
                filter => BuildRawScanlines16(bgra16, w, h, bitDepth, omitAlpha, filter),
                mode => BuildRawScanlines16(bgra16, w, h, bitDepth, omitAlpha, null, mode),
                () => BuildRawScanlines16Brute(bgra16, w, h, bitDepth, omitAlpha));
        else WriteIdat(output, rawData, effort);

        // IEND
        WriteChunk(output, "IEND"u8, []);
    }

    /// <summary>
    /// IDAT（档位路线）：把扫描线【增量】压缩并直接落成多条 IDAT chunk。
    /// <para>与旧形状（整帧压进 MemoryStream → <c>ToArray()</c> → 一条 IDAT）的差别只在
    /// <b>chunk 边界</b>：zlib 码流逐字节相同（同 level、同一次 <c>Write(rawData)</c>、同一后端判定），
    /// 少掉的是一整份压缩字节的物化 + 裁剪拷贝。承诺口径仍是"解压后逐字节相同"。</para>
    /// <para>后端判定仍是【整帧一次】（<see cref="DeflateZlib.SelectNativeBackend"/>）：判的是这一帧
    /// 可不可压缩，不是某一块；结论顺带进 Debug 日志（一次保存一行，见该方法的留痕说明）。</para>
    /// </summary>
    private static void WriteIdat(Stream output, byte[] rawData, CompressionEffort effort)
    {
        int level = CompressionEffortMap.PngTiffDeflateLevel(effort);
        bool useNative = DeflateZlib.SelectNativeBackend(rawData, level);
        var sink = new IdatChunkSink(output);
        DeflateZlib.CompressTo(sink, rawData, level, useNative);
        sink.FinishIdat();
    }

    /// <summary>PNG「优」档：在当前自适应扫描线之外，再试 Entropy 打分、8 行分带 brute 和少量固定滤波，
    /// 每条候选都实际压一次，取压缩字节最短者。
    /// <para>候选生成仍是【先把 BGRA 转成扫描线、再按固定 filter 压】，不经过 PNG 解码/反滤波；
    /// 每个候选压完只保留压缩字节（几百 KB 量级），候选 raw 用完即弃。分带 brute 需要一份
    /// filter=0 的 base 和一份目标 raw，因此峰值会比默认路径多约 2 份 33/66 MB 扫描线 ——
    /// 这是「优」档用内存换体积的明确代价，不会把五份候选 raw 同时留在内存里。</para>
    /// <para>压缩级别与 <see cref="CompressionEffort.Smallest"/> 相同（level 9）；本档的体积收益来自
    /// 滤波候选，不靠把 deflate 从 9 拉到 12。</para></summary>
    private static void WriteIdatOptimal(Stream output, byte[] adaptiveRaw, CompressionEffort effort,
        Func<byte, byte[]> buildFixed, Func<FilterScoreMode, byte[]> buildScored, Func<byte[]> buildBrute)
    {
        int level = CompressionEffortMap.PngTiffDeflateLevel(effort);
        byte[] best = CompressToBytes(adaptiveRaw, level);

        var entropy = CompressToBytes(buildScored(FilterScoreMode.Entropy), level);
        if (entropy.Length < best.Length) best = entropy;

        var brute = CompressToBytes(buildBrute(), level);
        if (brute.Length < best.Length) best = brute;

        foreach (byte filter in OptimalExtraFilters)
        {
            var candidateRaw = buildFixed(filter);
            var candidate = CompressToBytes(candidateRaw, level);
            if (candidate.Length < best.Length) best = candidate;
        }
        WriteIdatBytes(output, best);
    }

    /// <summary>把一整条 zlib 码流物化成 byte[]，只给「优」档的候选比较使用。
    /// 默认路径仍是 <see cref="WriteIdat"/> 的流式写出，不因本档多一次整帧物化。</summary>
    private static byte[] CompressToBytes(byte[] rawData, int level)
    {
        bool useNative = DeflateZlib.SelectNativeBackend(rawData, level);
        using var ms = new MemoryStream();
        DeflateZlib.CompressTo(ms, rawData, level, useNative);
        return ms.ToArray();
    }

    /// <summary>把已压好的 zlib 码流按 64 KiB 切成 IDAT；与流式路径共用同一个
    /// <see cref="IdatChunkSink"/>，因此 chunk 边界/CRC 口径不变。</summary>
    private static void WriteIdatBytes(Stream output, byte[] compressed)
    {
        var sink = new IdatChunkSink(output);
        sink.Write(compressed);
        sink.FinishIdat();
    }

    /// <summary>
    /// IDAT（中转/临时文件路线）：固定内置 Fastest，不参与后端分流 ——
    /// 这条的产物是"用完即删"的中转件（AVIF/JXL/WebP 的 stdin/临时 PNG），压缩率无意义，
    /// 旧实现走的也是 <c>CompressionLevel.Fastest</c> 而不是 DeflateZlib，故码流口径不变。
    /// </summary>
    private static void WriteIdatFastest(Stream output, byte[] rawData)
    {
        var sink = new IdatChunkSink(output);
        using (var zlib = new ZLibStream(sink, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(rawData);
        sink.FinishIdat();
    }

    /// <summary>
    /// IDAT 分块写出器：一条只缓冲 <see cref="IdatPayloadBytes"/> 字节，凑满立刻带
    /// [长度][类型][数据][CRC] 落到目标流并复用缓冲；整帧压缩字节从头到尾不聚成一块。
    /// <para><b>CRC 口径</b>（PNG 3.0 §5.3）：CRC 覆盖的是<b>本 chunk</b> 的类型码 + 数据，
    /// 长度域与 CRC 自身不在内 ⇒ 每条 chunk 一个独立 CRC，状态在块首（喂入 "IDAT"）开始、
    /// 块尾（取反）结束，不需要回溯任何已写出的字节。这就是分块能把 CRC 也做成增量的原因。</para>
    /// <para>不实现读/定位：本类只被 ZLibStream 当目的地写，<c>Flush()</c> 刻意<b>不</b>把半满的块
    /// 提前写成一条 IDAT（合法但会把产物打成大量小块），半块只在 <see cref="FinishIdat"/> 收尾。</para>
    /// </summary>
    private sealed class IdatChunkSink : Stream
    {
        private readonly Stream _output;
        private readonly byte[] _payload = new byte[IdatPayloadBytes];
        private int _used;
        private uint _crc = CrcSeed;
        private long _totalBytes;
        private int _chunkCount;

        public IdatChunkSink(Stream output) => _output = output;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _totalBytes;
        public override long Position { get => _totalBytes; set => throw new NotSupportedException(); }

        public override void Flush() { /* 见类说明：半块不许提前落成一条 IDAT */ }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> source)
        {
            _totalBytes += source.Length;
            while (!source.IsEmpty)
            {
                if (_used == 0) BeginChunk();
                int take = Math.Min(IdatPayloadBytes - _used, source.Length);
                var slice = source[..take];
                slice.CopyTo(_payload.AsSpan(_used));
                _crc = Crc32Update(_crc, slice);
                _used += take;
                source = source[take..];
                if (_used == IdatPayloadBytes) EndChunk();
            }
        }

        /// <summary>收尾：写出未满的最后一块。整帧恰好是 64 KiB 整数倍时【不】补一条 0 长度 IDAT
        /// （已有块就够"至少一条"这条规范前提）；一块都没凑满过但确有压缩字节时补一条空的。</summary>
        public void FinishIdat()
        {
            // 一个压缩字节都没有 = 上面那条压缩调用根本没写东西（ deflate/zlib 对任何输入都至少产出
            // 头 + 空块 + Adler ≈ 7 字节），这种产物是"没有 IDAT 的 PNG"，与 2026-09-19 那次
            // "值区布局分叉"同族 ⇒ 按 TIFF 的先例做成 Release 也生效的【拒绝写出】，
            // 不要留一条只有 Debug.Assert 挡得住的坏件（AtomicFileWriter 会把临时文件删掉，
            // 最终路径不落半成品）。
            if (_totalBytes == 0)
                throw new InvalidOperationException(
                    $"IDAT 压缩字节为 0（{_chunkCount} 块）—— 拒绝写出没有图像数据的坏 PNG");

            if (_used > 0 || _chunkCount == 0)
            {
                if (_used == 0) BeginChunk();
                EndChunk();
            }
        }

        private void BeginChunk()
        {
            // 块首：CRC 状态从 0xFFFFFFFF 起，先把 4 字节类型码喂进去（长度域不参与 CRC）。
            _crc = Crc32Update(CrcSeed, "IDAT"u8);
        }

        private void EndChunk()
        {
            // 栈上小端头：每条 chunk 两次 4 字节小数组在 4K 帧 ≈ 80 条 = 白付的 gen0，
            // 整个 sink 一辈子只该有那一份 64 KiB 缓冲。
            Span<byte> header = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(header, _used);
            _output.Write(header);
            _output.Write("IDAT"u8);
            _output.Write(_payload.AsSpan(0, _used));
            Span<byte> crcBuf = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBuf, Crc32Finish(_crc));
            _output.Write(crcBuf);
            _used = 0;
            _chunkCount++;
        }
    }

    /// <summary>构建 16-bit 扫描线: 输入 BGRA16 BE → 输出 RGBA16 BE + filter byte。
    /// 支持 10/12/16-bit 位深。10/12-bit 时 IHDR 标记为实际位深。
    /// 使用自适应滤波选择（同 SDR 路径），5 种滤波器中选绝对值最小和。</summary>
    /// <param name="actualBitDepth">实际位深 (10/12/16)，用于缩放和 IHDR。</param>
    private static byte[] BuildRawScanlines16(byte[] bgra16, int w, int h, int actualBitDepth = 16,
        bool omitAlpha = false, byte? fixedFilter = null, FilterScoreMode scoreMode = FilterScoreMode.SumAbs)
    {
        const int bytesPerChannel = 2;
        // omitAlpha ⇒ PNG color type 2（RGB）：每像素 6 字节，滤波的 bpp 也跟着变成 6。
        // 滤波算法（Sub/Up/Average/Paeth 的先前像素距离）以 bpp 为单位，改这里即可自适应。
        int channels = omitAlpha ? 3 : 4;
        int bpp = channels * bytesPerChannel; // 8 BPP (RGBA) / 6 BPP (RGB)
        int rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];
        if (h <= 0) return raw;

        // ── 阶段 1：BGRA16 BE → RGBA16/RGB16 BE，逐行并行 ──
        // 转换只依赖本行，先写进 raw 的原始区（filter 字节之后），供阶段 2 读取。
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            int srcOff = y * 8 * w;
            int dstOff = y * (1 + rowBytes) + 1;
            for (int x = 0; x < w; x++)
            {
                int si = srcOff + x * 8;
                int di = dstOff + x * bpp;
                // 读取 16-bit big-endian 值
                ushort bVal = (ushort)((bgra16[si] << 8) | bgra16[si + 1]);
                ushort gVal = (ushort)((bgra16[si + 2] << 8) | bgra16[si + 3]);
                ushort rVal = (ushort)((bgra16[si + 4] << 8) | bgra16[si + 5]);
                ushort aVal = (ushort)((bgra16[si + 6] << 8) | bgra16[si + 7]);

                // 按实际位深缩放：16-bit 容器中存储 10/12-bit 值时左对齐
                //
                // ═══ 2026-09-19：截断改四舍五入 ═══
                // 旧写法 (v >> shift) << shift 是"向下取整"，注释还自称"仅做安全验证"，
                // 但它根本不是无操作：SDR ≥10-bit 路径（PrepareSdr16Pixels 用默认
                // quantizeBits=16，容器被 16-bit 精度写满）每一位都要经过这里，
                // 于是整张图被系统性压暗 0~(2^shift-1) 个台阶（10-bit 平均 -0.5 LSB、
                // 最坏 -63 LSB ≈ 半档），且 sBIT 声明的正是这个被压过的位深 ⇒ 偏差被固化进文件。
                // 现在取"最近的合法台阶"：期望偏差为 0，最大误差从 1 个台阶降到半个台阶。
                // 低位仍然清零 —— 那是 sBIT 的容器合法性要求（PNG 3.0 12.4 左对齐），与取整方向无关。
                if (actualBitDepth < 16)
                {
                    int shift = 16 - actualBitDepth;
                    rVal = RoundToSampleDepth(rVal, shift);
                    gVal = RoundToSampleDepth(gVal, shift);
                    bVal = RoundToSampleDepth(bVal, shift);
                    aVal = RoundToSampleDepth(aVal, shift);
                }

                // 写入 big-endian 16-bit 容器（RGB 或 RGBA 顺序）
                Write16(raw, di, rVal);
                Write16(raw, di + 2, gVal);
                Write16(raw, di + 4, bVal);
                if (!omitAlpha) Write16(raw, di + 6, aVal);
            }
        });

        // ── 阶段 2：分带并行滤波 ──
        // 每行滤波只依赖「上一行的原始字节」（prevRow 是输入行，不是上一行的滤波结果），
        // 带与带之间因此完全独立。带内保留上一行原始副本串行推进；带首行需要的边界原始行
        // 在并行开始前先复制，避免被前一个带改写后读到。输出与逐行串行版本逐字节一致。
        int bandCount = h < 32 ? 1 : Math.Min(h, Environment.ProcessorCount);
        int bandRows = (h + bandCount - 1) / bandCount;
        var bandPrev = new byte[bandCount][];
        for (int b = 1; b < bandCount; b++)
        {
            int y0 = b * bandRows;
            if (y0 >= h) break;
            var prev = new byte[rowBytes];
            Array.Copy(raw, (y0 - 1) * (1 + rowBytes) + 1, prev, 0, rowBytes);
            bandPrev[b] = prev;
        }

        System.Threading.Tasks.Parallel.For(0, bandCount, b =>
        {
            int y0 = b * bandRows;
            int y1 = Math.Min(h, y0 + bandRows);
            if (y0 >= y1) return;

            var curRow = new byte[rowBytes];
            var prevRow = bandPrev[b] ?? new byte[rowBytes]; // 带 0 的虚拟上一行 = 全零
            var filtered = new byte[rowBytes];

            for (int y = y0; y < y1; y++)
            {
                int rawOff = y * (1 + rowBytes);
                Buffer.BlockCopy(raw, rawOff + 1, curRow, 0, rowBytes);

                if (fixedFilter is byte ff)
                {
                    // 「优」档候选：整帧固定一种滤波，跳过逐行打分。
                    raw[rawOff] = ff;
                    ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, ff);
                    Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);
                }
                else
                {
                    // 自适应滤波选择：尝试 5 种滤波器。
                    // SumAbs 是历史默认；Entropy 是「优」档的额外候选策略。
                    // 两者都只是“选一条整帧扫描线”，最终由 WriteIdatOptimal 实际压缩后比大小。
                    byte bestFilter = 0;
                    bool maximize = scoreMode == FilterScoreMode.Entropy;
                    double bestScore = maximize ? double.MinValue : double.MaxValue;

                    for (byte f = 0; f <= 4; f++)
                    {
                        ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, f);
                        double score = scoreMode == FilterScoreMode.Entropy
                            ? EntropyScore(filtered)
                            : SumAbsScore(filtered);
                        bool win = maximize ? score > bestScore : score < bestScore;
                        if (win)
                        {
                            bestScore = score;
                            bestFilter = f;
                        }
                    }

                    // 写入最佳滤波结果
                    raw[rawOff] = bestFilter;
                    ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, bestFilter);
                    Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);
                }

                // 当前行变为上一行
                (curRow, prevRow) = (prevRow, curRow);
            }
        });
        return raw;
    }

    /// <summary>构建原始扫描线数据（含 filter byte）— 自适应滤波优化压缩率。
    /// SDR 路径固定 8-bit。</summary>
    private static byte[] BuildRawScanlines(byte[] bgra, int w, int h, int outDepth, byte? fixedFilter = null, FilterScoreMode scoreMode = FilterScoreMode.SumAbs)
    {
        int channels = 4;
        int bytesPerChannel = outDepth <= 8 ? 1 : 2; // 8-bit → 1B, 10/12/16-bit → 2B
        int bpp = channels * bytesPerChannel;
        int rowBytes = w * bpp;
        var raw = new byte[h * (1 + rowBytes)];

        // ═══ 2026-08-25 P2：行并行化；2026-10-29：滤波也分带并行 ═══
        // 历史：先是全串行；2026-08-25 把 BGRA→RGBA 拆成并行、滤波选择仍串行
        // （Up/Average/Paeth 要上一行，看着不能并行）；2026-10-29 照 BuildRawScanlines16
        // 的办法把滤波也拆开 —— 每行只依赖上一行的【原始字节】，带与带之间因此完全独立。
        // 同时去掉「每行先分配 byte[rowBytes] 再整行拷进 raw」这一整帧多余拷贝与 h 次分配。

        // 阶段1: 并行 BGRA→RGBA + 16-bit 扩展，直接写进 raw 的扫描线区（filter 字节之后）
        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            int srcOff = y * w * 4;
            int dstOff = y * (1 + rowBytes) + 1;
            if (outDepth == 8)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = dstOff + x * 4;
                    raw[di] = bgra[si + 2];     // R
                    raw[di + 1] = bgra[si + 1]; // G
                    raw[di + 2] = bgra[si];     // B
                    raw[di + 3] = bgra[si + 3]; // A
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int si = srcOff + x * 4;
                    int di = dstOff + x * 8;
                    Write16(raw, di, (ushort)(bgra[si + 2] * 257));
                    Write16(raw, di + 2, (ushort)(bgra[si + 1] * 257));
                    Write16(raw, di + 4, (ushort)(bgra[si] * 257));
                    Write16(raw, di + 6, (ushort)(bgra[si + 3] * 257));
                }
            }
        });

        // 阶段2: 分带并行滤波（与 BuildRawScanlines16 同法）
        // 带首行需要的边界原始行必须在并行开始前复制：带内会把自己的行改写为滤波结果，
        // 后一个带若现读 raw[y0-1] 就会读到别人写过的字节。
        // 仍只试 Sub/Up/Average/Paeth 四种（跳过 None），与改前逐字节一致。
        int bandCount = h < 32 ? 1 : Math.Min(h, Environment.ProcessorCount);
        int bandRows = (h + bandCount - 1) / bandCount;
        var bandPrev = new byte[bandCount][];
        for (int b = 1; b < bandCount; b++)
        {
            int y0 = b * bandRows;
            if (y0 >= h) break;
            var prev = new byte[rowBytes];
            Array.Copy(raw, (y0 - 1) * (1 + rowBytes) + 1, prev, 0, rowBytes);
            bandPrev[b] = prev;
        }

        System.Threading.Tasks.Parallel.For(0, bandCount, b =>
        {
            int y0 = b * bandRows;
            int y1 = Math.Min(h, y0 + bandRows);
            if (y0 >= y1) return;

            var curRow = new byte[rowBytes];
            var prevRow = bandPrev[b] ?? new byte[rowBytes]; // 带 0 的虚拟上一行 = 全零
            var filtered = new byte[rowBytes];

            for (int y = y0; y < y1; y++)
            {
                int rawOff = y * (1 + rowBytes);
                Buffer.BlockCopy(raw, rawOff + 1, curRow, 0, rowBytes);

                if (fixedFilter is byte ff)
                {
                    // 「优」档候选：整帧固定一种滤波，跳过逐行打分。
                    raw[rawOff] = ff;
                    ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, ff);
                    Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);
                }
                else
                {
                    // 自适应滤波选择：SumAbs 默认跳过 None（平坦区域交给 deflate 处理）；
                    // Entropy 候选则把 None 也纳入。
                    byte bestFilter = 1; // Sub 默认
                    bool maximize = scoreMode == FilterScoreMode.Entropy;
                    double bestScore = maximize ? double.MinValue : double.MaxValue;
                    int firstFilter = scoreMode == FilterScoreMode.SumAbs ? 1 : 0;

                    for (byte f = (byte)firstFilter; f <= 4; f++)
                    {
                        ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, f);
                        double score = scoreMode == FilterScoreMode.Entropy
                            ? EntropyScore(filtered)
                            : SumAbsScore(filtered);
                        bool win = maximize ? score > bestScore : score < bestScore;
                        if (win)
                        {
                            bestScore = score;
                            bestFilter = f;
                        }
                    }

                    // 写入最佳滤波结果
                    raw[rawOff] = bestFilter;
                    ApplyFilter(curRow, prevRow, filtered, rowBytes, bpp, bestFilter);
                    Array.Copy(filtered, 0, raw, rawOff + 1, rowBytes);
                }

                // 当前行变为上一行（交换缓冲，避免每行分配）
                (curRow, prevRow) = (prevRow, curRow);
            }
        });
        return raw;
    }

    /// <summary>逐行滤波得分：绝对值和（历史默认）。</summary>
    private static double SumAbsScore(ReadOnlySpan<byte> row)
    {
        double score = 0;
        foreach (byte b in row) score += b < 128 ? b : 256 - b;
        return score;
    }

    /// <summary>逐行滤波得分：字节分布集中度（熵的反向近似）。
    /// <para>对每个过滤后的行做 256 桶直方图，返回 Σ c·log2(c)；分布越集中该值越大。
    /// 这不是 deflate 的真实码长，只是“更容易被 LZ/Huffman 压”的廉价代理；最终仍由
    /// <see cref="WriteIdatOptimal"/> 实际压一遍再决定是否采纳。</para></summary>
    private static double EntropyScore(ReadOnlySpan<byte> row)
    {
        Span<int> hist = stackalloc int[256];
        foreach (byte b in row) hist[b]++;
        double score = 0;
        for (int i = 0; i < 256; i++)
        {
            int c = hist[i];
            if (c > 1) score += c * Math.Log2(c);
        }
        return score;
    }

    /// <summary>「优」档的 8-bit 分带滤波候选：先得到一份 filter=0 的原始扫描线，
    /// 再按 8 行一带、对每条带实际压 5 种固定滤波，选该带压缩字节最短者。
    /// <para>这是 oxipng `brute-lines` 的极小化版本：只在一帧内部做“带内选滤波”，
    /// 不改变像素、不做整帧多份候选；带间独立，所以可以并行。</para></summary>
    private static byte[] BuildRawScanlinesBrute(byte[] bgra, int w, int h, int outDepth)
    {
        var baseRaw = BuildRawScanlines(bgra, w, h, outDepth, fixedFilter: 0);
        int bpp = outDepth <= 8 ? 4 : 8;
        return BuildBandedRaw(baseRaw, w, h, bpp, 8);
    }

    /// <summary>「优」档的 16-bit 分带滤波候选，语义同 8-bit 版本。</summary>
    private static byte[] BuildRawScanlines16Brute(byte[] bgra16, int w, int h, int bitDepth, bool omitAlpha)
    {
        var baseRaw = BuildRawScanlines16(bgra16, w, h, bitDepth, omitAlpha, fixedFilter: 0);
        int bpp = omitAlpha ? 6 : 8;   // color type 2 = RGB16 (6B/px)，color type 6 = RGBA16 (8B/px)
        return BuildBandedRaw(baseRaw, w, h, bpp, 8);
    }

    /// <summary>把一份 filter=0 的原始扫描线按带重滤波；每条带用 level 1 实际压缩比大小。</summary>
    private static byte[] BuildBandedRaw(byte[] baseRaw, int w, int h, int bpp, int bandRows)
    {
        int rowBytes = w * bpp;
        int stride = 1 + rowBytes;
        var result = (byte[])baseRaw.Clone();
        int bandCount = (h + bandRows - 1) / bandRows;

        System.Threading.Tasks.Parallel.For(0, bandCount, b =>
        {
            int y0 = b * bandRows;
            int y1 = Math.Min(h, y0 + bandRows);
            if (y0 >= y1) return;

            var prev = new byte[rowBytes];
            if (y0 > 0)
                Array.Copy(baseRaw, (y0 - 1) * stride + 1, prev, 0, rowBytes);

            var cur = new byte[rowBytes];
            var filtered = new byte[rowBytes];
            byte[]? best = null;
            int bestPacked = int.MaxValue;

            for (byte f = 0; f <= 4; f++)
            {
                var candidate = new byte[(y1 - y0) * stride];
                if (y0 == 0) Array.Clear(prev);
                else Array.Copy(baseRaw, (y0 - 1) * stride + 1, prev, 0, rowBytes);

                for (int y = y0; y < y1; y++)
                {
                    int off = (y - y0) * stride;
                    Array.Copy(baseRaw, y * stride + 1, cur, 0, rowBytes);
                    candidate[off] = f;
                    ApplyFilter(cur, prev, filtered, rowBytes, bpp, f);
                    Array.Copy(filtered, 0, candidate, off + 1, rowBytes);
                    Array.Copy(cur, 0, prev, 0, rowBytes);   // 下一行的“上一行”仍是原始字节
                }

                // 用最小 deflate 档估“这一带哪种滤波更省”；最终整帧还会再压一次 level 9。
                var packed = DeflateZlib.Compress(candidate, 1);
                if (packed.Length < bestPacked)
                {
                    bestPacked = packed.Length;
                    best = candidate;
                }
            }

            if (best is not null)
                Array.Copy(best, 0, result, y0 * stride, best.Length);
        });
        return result;
    }

    /// <summary>应用 PNG 行滤波器。</summary>
    private static void ApplyFilter(byte[] cur, byte[] prev, byte[] dst, int rowBytes, int bpp, byte filter)
    {
        switch (filter)
        {
            case 0: // None
                Array.Copy(cur, dst, rowBytes);
                break;
            case 1: // Sub: diff = cur[x] - cur[x-bpp]
                for (int i = 0; i < rowBytes; i++)
                    dst[i] = (byte)(cur[i] - (i >= bpp ? cur[i - bpp] : 0));
                break;
            case 2: // Up: diff = cur[x] - prev[x]
                for (int i = 0; i < rowBytes; i++)
                    dst[i] = (byte)(cur[i] - prev[i]);
                break;
            case 3: // Average: diff = cur[x] - floor((cur[x-bpp] + prev[x]) / 2)
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    dst[i] = (byte)(cur[i] - ((a + prev[i]) >> 1));
                }
                break;
            case 4: // Paeth
                for (int i = 0; i < rowBytes; i++)
                {
                    int a = i >= bpp ? cur[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    dst[i] = (byte)(cur[i] - PaethPredictor(a, b, c));
                }
                break;
        }
    }

    /// <summary>Paeth 预测器 (PNG 规范 RFC 2083)。</summary>
    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        if (pb <= pc) return b;
        return c;
    }

    /// <summary>8-bit 值扩展到 16-bit (val * 257 = val << 8 | val)。</summary>
    private static void Write16(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)val;
    }

    /// <summary>
    /// 2026-09-19：把 16-bit 采样值就近取整到 actualBitDepth（= 16 - shift）能表示的最近台阶，低位保持清零。
    /// <para>为什么必须就近而不是向下取整：见 <see cref="BuildRawScanlines16"/> 的 2026-09-19 备注 ——
    /// 向下取整会让整图单向变暗，是偏置；就近取整的偏置期望为 0。</para>
    /// <para>为什么要钳上界：v 接近 65535 时"最近的台阶"是 65536，容器放不下，
    /// 直接左移会溢出回绕成 0（纯白变纯黑）。故钳到容器内最高台阶 step*(65535/step)。</para>
    /// </summary>
    private static ushort RoundToSampleDepth(ushort v, int shift)
    {
        int step = 1 << shift;
        int q = (v + step / 2) / step;
        int max = 65535 / step;
        if (q > max) q = max;
        return (ushort)(q * step);
    }

    // ═══ 2026-10-31：IDAT 的整帧压缩形态（CompressDeflate / CompressDeflateFast）已收进
    //     WriteIdat / WriteIdatFastest 两条流式路径；档位→level 的映射仍由
    //     CompressionEffortMap.PngTiffDeflateLevel 唯一决定，后端选择仍由 DeflateZlib 决定。
    //     ⚠ 口径两条没变：① 无损档必须走 9/8/6/1 这组 level，而不是 CompressionLevel 枚举
    //     （枚举只有 0/1/6/9 四个点，表达不了用户要的"稍微放宽"那一档 = level 8）；
    //     ② 产物承诺是"zlib 解压后逐字节相同"而不是"压缩字节逐字节不变"，因为 libdeflate 与
    //     zlib-ng 的码流本来就不同（实测体积 +0.23%）。

    /// <summary>构建 iCCP chunk 数据: profile_name\0 compression_method deflate(profile)。</summary>
    private static byte[] BuildIccpChunk(byte[] iccProfile)
    {
        var name = "ICC\0"u8.ToArray();
        using var ms = new MemoryStream();
        ms.Write(name);
        ms.WriteByte(0); // compression method: deflate

        using (var zlib = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            zlib.Write(iccProfile);
        }
        return ms.ToArray();
    }

    /// <summary>写入 PNG chunk: [4B length][4B type][data][4B CRC32]。</summary>
    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, byte[] data)
    {
        var lenBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(lenBuf, data.Length);
        output.Write(lenBuf);
        output.Write(type);
        output.Write(data);

        // CRC32 over type + data —— 增量推进必须一路传【未取反】的状态，只在最后取反一次。
        // ⚠ 2026-10-31 流式化时这里多取反了一次（先 Finish 再喂 data 再 Finish），产物只有 IDAT 的
        //   CRC 是对的，IHDR/sBIT/cICP/IEND 全错 ⇒ PIL 直接拒收整张图。判据在
        //   EncodingIntegrationTests 的「每条 chunk 的 CRC 用测试自带的另一份 CRC32 复算」那一格，
        //   它不复用本文件的实现，所以这类错不会自洽地蒙过去。
        uint crc = Crc32Update(CrcSeed, type);
        crc = Crc32Update(crc, data);
        crc = Crc32Finish(crc);
        var crcBuf = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        output.Write(crcBuf);
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>CRC 的起始状态（PNG 3.0 §5.3：crcReg 从全 1 开始）。
    /// 分块写出（<see cref="IdatChunkSink"/>）与这里的整块写法共用同一套推进函数，
    /// 所以"chunk 边界变了"不可能把 CRC 算成两种结果。</summary>
    private const uint CrcSeed = 0xFFFFFFFF;

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    /// <summary>CRC32 推进一段字节：传入上一段结束的【未取反】状态，返回下一段的输入状态。
    /// <para>为什么留着手写表法而不上 <c>System.IO.Hashing.Crc32</c>：该包在本仓
    /// <c>TrueToneCap.Core.csproj</c> / <c>Directory.Packages.props</c> / 各 packages.lock.json
    /// 里 0 处引用（实测），要吃得先加一条 PackageReference 并重生锁文件 —— 为一个每保存一次的
    /// CRC 给零原生依赖的托管编码器加一条随包分发的依赖不值当。分块之后 CRC 本来就是增量的，
    /// 表法这一趟的字节数与旧版逐字节法完全相同（只是不再有一次整帧物化）。</para>
    /// <para>⚠ 若将来换实现：必须替换 <see cref="Crc32Finish"/>/<see cref="CrcSeed"/> 这一对
    /// 口径（初值全 1 + 末位取反 + 反射多项式 0xEDB88320），而不是只换一处 —— 否则
    /// IDAT 与其它 chunk 会算出两套 CRC。</para></summary>
    private static uint Crc32Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    /// <summary>CRC 收尾：取反得到写进文件的那 4 字节。</summary>
    private static uint Crc32Finish(uint crc) => crc ^ 0xFFFFFFFF;
}
