// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/FormatStructureTests.cs
// 输出文件"结构合法性"测试 — 用独立解析器回读产物，校验容器结构而非仅"文件非空"
// 运行: dotnet run --project src/TrueToneCap.Test -- --format-tests
//
// 背景：既有集成测试只断言"文件存在且非空"，因此以下缺陷长期存活而测试全绿：
//   · TIFF IFD 短条目只写 10 字节（应 12）→ 后续条目整体错位
//   · TIFF 16-bit 路径按 4 字节步进读 8 字节/像素 → 通道错位
//   · TIFF Alpha 未做 8→16 位扩展 → 整图近乎全透明
//   · TIFF 像素数据起点与值区回填区重叠 → Seek 覆盖压缩数据前 24 字节
//   · AVIF mdat box 长度按小端写入 → 长度被解析成天文数字
//   · AVIF iloc 回填偏移少算 2 字节 → 覆盖 extent_count
// 本文件针对上述每一条给出可复现的结构断言，防止回归。

using System.IO.Compression;
using System.Text;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.Test;

/// <summary>输出文件结构合法性测试（TIFF / AVIF 容器级校验）。</summary>
public static class FormatStructureTests
{
    private static int _passed, _failed;
    /// <summary>因环境不满足而【显式跳过】的用例数 —— 既不算通过也不算失败。</summary>
    private static int _skipped;
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_FmtTest");

    /// <summary>显式跳过一条用例：不计入通过/失败，但必须留下原因（禁止静默通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    public static int RunAll()
    {
        _passed = 0; _failed = 0; _skipped = 0;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 输出结构合法性测试");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("══════════════════════════════════════\n");

        Test_Tiff8Bit();
        Test_Tiff16Bit();
        Test_TiffWithIcc();
        Test_TiffInvalidBitDepthClamped();
        Test_TiffHdrPinsDepthAndNotifies();
        Test_AvifBoxStructure();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--format-tests", _passed, _failed, 0, null, _skipped);
        Console.WriteLine($"══════════════════════════════════════");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  TIFF
    // ═══════════════════════════════════════

    /// <summary>8-bit TIFF：验证 IFD 结构、值区不与像素数据重叠、Alpha 正确。</summary>
    static void Test_Tiff8Bit()
    {
        const int w = 64, h = 48;
        var path = Path.Combine(OutDir, "struct_tiff8.tif");
        try
        {
            var bgra = MakeBgra8(w, h);
            ManagedTiffEncoder.Encode(bgra, w, h, path, bitDepth: 8);

            var tif = ParseTiff(path);
            Assert("TIFF8/头部", tif.Ok, tif.Error ?? "II + magic 42 + IFD offset 8");
            if (!tif.Ok) return;

            Assert("TIFF8/必需标签齐备",
                tif.HasAll(new ushort[] { 256, 257, 258, 259, 262, 273, 277, 278, 279, 282, 283, 296 }),
                $"实际 {tif.Tags.Count} 个标签");
            AssertExtraSamplesContract(tif, "TIFF8", 13);

            Assert("TIFF8/尺寸", tif.Width == w && tif.Height == h, $"{tif.Width}x{tif.Height}");
            Assert("TIFF8/压缩=Deflate(8)", tif.Compression == 8, $"compression={tif.Compression}");
            Assert("TIFF8/光度解释=RGB(2)", tif.Photometric == 2, $"photometric={tif.Photometric}");
            Assert("TIFF8/每像素样数=4", tif.Samples == 4, $"samples={tif.Samples}");

            Assert("TIFF8/BitsPerSample 全为 8",
                tif.BitsPerSample.Length == 4 && tif.BitsPerSample.All(b => b == 8),
                string.Join(",", tif.BitsPerSample));

            // 回归要点：StripOffsets 必须落在"值区之后"，否则回填会覆盖压缩数据
            Assert("TIFF8/像素数据不与值区重叠",
                tif.StripOffset >= tif.ValueAreaEnd,
                $"stripOffset={tif.StripOffset} 值区终点={tif.ValueAreaEnd}");

            // 回归要点（2026-09-19）：tag 8 承诺的是 zlib 封装的 deflate。
            // 写裸 deflate 时 .NET 自己解得开、libtiff/ffmpeg 解不开 → 用户拿到全黑图。
            Assert("TIFF8/压缩流是 zlib 封装（tag 8 的规范含义）", tif.StripHasZlibHeader(),
                "strip 前两字节不是合法 zlib CMF/FLG");

            Assert("TIFF8/像素数据落在文件范围内",
                (long)tif.StripOffset + tif.StripByteCount <= tif.FileLength,
                $"{tif.StripOffset}+{tif.StripByteCount} <= {tif.FileLength}");

            // 解出像素并校验内容与 Alpha
            var raw = tif.DecompressStrip();
            int expected = w * h * 4;
            Assert("TIFF8/解压后长度正确", raw.Length == expected, $"{raw.Length} / 期望 {expected}");

            // 输入为 BGRA(0,64,128,255)，输出应为 RGBA(128,64,0,255)
            bool pxOk = raw[0] == 128 && raw[1] == 64 && raw[2] == 0 && raw[3] == 255;
            Assert("TIFF8/首像素通道顺序与 Alpha", pxOk,
                $"R={raw[0]} G={raw[1]} B={raw[2]} A={raw[3]} (期望 128,64,0,255)");

            bool alphaOk = true;
            for (int i = 3; i < raw.Length; i += 4 * 97) if (raw[i] != 255) { alphaOk = false; break; }
            Assert("TIFF8/Alpha 全不透明", alphaOk, "抽样校验");
        }
        catch (Exception ex)
        {
            Assert("TIFF8", false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>16-bit TIFF：验证 8 字节/像素输入被正确解析、Alpha 扩展到 65535。</summary>
    static void Test_Tiff16Bit()
    {
        const int w = 32, h = 24;
        var path = Path.Combine(OutDir, "struct_tiff16.tif");
        try
        {
            // 16-bit 输入格式: 每像素 8 字节, 通道顺序 BGRA, 每通道大端
            var bgra16 = new byte[w * h * 8];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 8;
                WriteU16BE(bgra16, o, 0x4000);      // B
                WriteU16BE(bgra16, o + 2, 0x8000);  // G
                WriteU16BE(bgra16, o + 4, 0xC000);  // R
                WriteU16BE(bgra16, o + 6, 0xFFFF);  // A
            }
            ManagedTiffEncoder.Encode(bgra16, w, h, path, bitDepth: 16);

            var tif = ParseTiff(path);
            Assert("TIFF16/头部", tif.Ok, tif.Error ?? "");
            if (!tif.Ok) return;

            AssertExtraSamplesContract(tif, "TIFF16", 13);
            Assert("TIFF16/BitsPerSample 全为 16",
                tif.BitsPerSample.Length == 4 && tif.BitsPerSample.All(b => b == 16),
                string.Join(",", tif.BitsPerSample));

            Assert("TIFF16/像素数据不与值区重叠",
                tif.StripOffset >= tif.ValueAreaEnd,
                $"stripOffset={tif.StripOffset} 值区终点={tif.ValueAreaEnd}");

            var raw = tif.DecompressStrip();
            Assert("TIFF16/解压后长度正确", raw.Length == w * h * 8, $"{raw.Length} / 期望 {w * h * 8}");

            // TIFF 是小端(II)，输出为 RGBA 顺序
            ushort r = (ushort)(raw[0] | (raw[1] << 8));
            ushort g = (ushort)(raw[2] | (raw[3] << 8));
            ushort b = (ushort)(raw[4] | (raw[5] << 8));
            ushort a = (ushort)(raw[6] | (raw[7] << 8));
            Assert("TIFF16/首像素通道顺序",
                r == 0xC000 && g == 0x8000 && b == 0x4000,
                $"R={r:X4} G={g:X4} B={b:X4} (期望 C000,8000,4000)");

            // 回归要点：16-bit Alpha 必须是 65535（此前写成 255 → 近乎全透明）
            Assert("TIFF16/Alpha 已扩展到 65535", a == 0xFFFF, $"A={a:X4} (期望 FFFF)");
        }
        catch (Exception ex)
        {
            Assert("TIFF16", false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// TIFF 只接受 BitsPerSample = 8 或 16 —— 其余值必须被钳制，不能原样写进 IFD。
    /// <para>回归背景（审查 P2-7）：设置层曾对所有格式统一放行 12-bit，而编码器以
    /// <c>bitDepth &gt; 8</c> 为界按【每通道 2 字节】写采样数据，却把 BitsPerSample 原样写成 12。
    /// 解码器按 12-bit 打包去读 16-bit 未打包的数据 ⇒ 图像错位、多数查看器报"文件损坏"，
    /// 且全过程不抛异常、无日志。故钳制必须落在编码器入口，任何调用方都绕不过去。</para>
    /// </summary>
    static void Test_TiffInvalidBitDepthClamped()
    {
        const int w = 32, h = 24;
        var path = Path.Combine(OutDir, "struct_tiff_bps12.tif");
        try
        {
            ManagedTiffEncoder.Encode(MakeBgra8(w, h), w, h, path, bitDepth: 12);

            var tif = ParseTiff(path);
            Assert("TIFF-BPS/头部", tif.Ok, tif.Error ?? "");
            if (!tif.Ok) return;

            Assert("TIFF-BPS: 请求 12-bit 被钳到 16（BPS 只有 8/16 合法）",
                tif.BitsPerSample.Length == 4 && tif.BitsPerSample.All(b => b == 16),
                string.Join(",", tif.BitsPerSample));

            // 数据宽度必须与声明的 BPS 一致：16-bit × 4 通道 × w×h
            var raw = tif.DecompressStrip();
            Assert("TIFF-BPS: 采样数据宽度与 BitsPerSample=16 一致",
                raw.Length == w * h * 8, $"{raw.Length} / 期望 {w * h * 8}");
        }
        catch (Exception ex)
        {
            Assert("TIFF-BPS", false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// HDR TIFF 把位深钉在 16-bit 是【允许的】，但必须【说出来】（2026-09-24 §48 #22）。
    /// <para>背景：TIFF 的 BitsPerSample 只有 8/16 合法（上面那条测试就是钉这个），而 8-bit 装不下 PQ
    /// ⇒ HDR 分支硬写 16-bit、从不读 <c>OutputBitDepth</c>。界面却仍给着"8-bit / 16-bit"两档
    /// ⇒ 用户选 8 拿到 16，除了"文件大一倍"之外没有任何线索可查。这与 PNG 那条是同一种病的两面
    /// （PNG 遇到 8-bit 是【降成 SDR】丢 HDR，TIFF 是【升到 16】保 HDR）——两侧现在都留了痕。</para>
    /// <para>两条断言各盯一个方向：① 选 8 时产物确实是 16【且】通用降级通道里有原因；
    /// ② 选 16 时通道必须是空的 —— 否则那个提示就成了无条件噪声，
    /// 而"每次都报的提示"与"不报"在用户端等价（都会被当成误报忽略）。</para>
    /// </summary>
    static void Test_TiffHdrPinsDepthAndNotifies()
    {
        const int w = 32, h = 24;
        // 线性中灰 + 不透明：不夹任何色域信息，只验位深与留痕这两件事
        var lin = new float[w * h * 4];
        for (int i = 0; i < lin.Length; i += 4)
        { lin[i] = lin[i + 1] = lin[i + 2] = 0.2f; lin[i + 3] = 1f; }
        var frame = new HdrFrameData { Pixels = lin, Width = w, Height = h };
        var enc = EncoderFactory.Create(OutputFormat.TIFF);

        foreach (var (requested, expectNotice) in new[] { (8, true), (16, false) })
        {
            var path = Path.Combine(OutDir, $"struct_tiff_hdr_bps{requested}.tif");
            var s = new EncodingSettings
            { Format = OutputFormat.TIFF, HdrOutput = true, OutputBitDepth = requested, ColorSpaceTag = "sRGB" };
            EncoderFallbackNotifier.LastReason = null;      // 读前先清，防上一条残留
            try { enc.EncodeAsync(frame, s, path).GetAwaiter().GetResult(); }
            catch (Exception ex)
            {
                Assert($"HDR TIFF/请求 {requested}-bit: 编码未抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var tif = ParseTiff(path);
            // Assert 是 void（只记账不返回值），所以"能不能继续"要自己拿条件判断
            bool headerOk = tif.Ok;
            Assert($"HDR TIFF/请求 {requested}-bit: 头部可解析", headerOk, tif.Error ?? "");
            if (!headerOk) continue;
            Assert($"HDR TIFF/请求 {requested}-bit: BPS 恒 16",
                tif.BitsPerSample.Length == 4 && tif.BitsPerSample.All(b => b == 16),
                string.Join(",", tif.BitsPerSample));

            var reason = EncoderFallbackNotifier.LastReason;
            bool has = !string.IsNullOrEmpty(reason);
            if (expectNotice)
            {
                Assert("HDR TIFF/选 8-bit ⇒ 必须登记位深被改动（不许静默改参数）", has, reason ?? "（通道为空）");
                Assert("HDR TIFF/提示文本要点名 16-bit，否则用户无从知道自己拿到了什么",
                    has && reason!.Contains("16-bit"), reason ?? "");
            }
            else
            {
                Assert("HDR TIFF/选 16-bit ⇒ 不该有提示（无条件提示等于没提示）", !has, reason ?? "");
            }
        }
    }

    /// <summary>带 ICC 的 TIFF：值区因多一个条目而变长，验证像素数据偏移随之正确。</summary>
    static void Test_TiffWithIcc()
    {
        const int w = 16, h = 16;
        var path = Path.Combine(OutDir, "struct_tiff_icc.tif");
        try
        {
            // 本条测的是"多一个 ICC 条目之后，值区变长、像素偏移仍要正确"（历史缺陷：回填时
            // Seek 覆盖了压缩数据前 24 字节）。⚠ 原先这里塞的是一块 512 B 随机字节，
            // 注释写着"够长的假 ICC (>128 字节才会写入 34675 标签)"——那是**照旧长度阈值反造**的夹具：
            // §31.3-9 把判据换成结构判据（IsEmbeddableIcc）之后它被正确拒收，本条立刻变红，
            // 而红的恰恰是"标签根本没写进去 ⇒ 后面三条布局断言全在量一个没有 ICC 的文件"。
            // ⇒ 换成一份真件（3144 B sRGB，结构齐），布局判据才真的还在测布局。
            var icc = TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetDefaultSRgbIcc();
            Assert($"TIFF-ICC/夹具是一份结构合法的 ICC ({icc.Length} B)",
                TrueToneCap.Core.Encoding.FormatHelper.IsEmbeddableIcc(icc),
                "夹具不合格 ⇒ 下面三条布局断言量的是没有 ICC 的文件，全绿也不能证明偏移正确");

            ManagedTiffEncoder.Encode(MakeBgra8(w, h), w, h, path, bitDepth: 8, iccProfile: icc);

            var tif = ParseTiff(path);
            Assert("TIFF-ICC/头部", tif.Ok, tif.Error ?? "");
            if (!tif.Ok) return;

            Assert("TIFF-ICC/含 ICC 标签(34675)", tif.Tags.ContainsKey(34675), "");
            AssertExtraSamplesContract(tif, "TIFF-ICC", 14);
            Assert("TIFF-ICC/像素数据与值区不重叠(ICC 计入值区)",
                tif.StripOffset >= tif.ValueAreaEnd,
                $"stripOffset={tif.StripOffset} 值区终点={tif.ValueAreaEnd}");

            var raw = tif.DecompressStrip();
            Assert("TIFF-ICC/解压后长度正确", raw.Length == w * h * 4, $"{raw.Length}");
            Assert("TIFF-ICC/首像素未损坏",
                raw[0] == 128 && raw[1] == 64 && raw[2] == 0 && raw[3] == 255,
                $"R={raw[0]} G={raw[1]} B={raw[2]} A={raw[3]}");
        }
        catch (Exception ex)
        {
            Assert("TIFF-ICC", false, $"异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  AVIF / ISOBMFF
    // ═══════════════════════════════════════

    /// <summary>AVIF 顶层 box 结构：验证 mdat 长度为大端、box 链完整覆盖文件。</summary>
    static void Test_AvifBoxStructure()
    {
        const int w = 64, h = 64;
        var path = Path.Combine(OutDir, "struct_avif.avif");

        // ═══ 2026-09-19 修假绿：环境前提只在【编码之前】判一次 ═══
        // 仓库范式是先判可用性再 Skip（CorePipelineTests.Test_AvifProbe_MeasuredIfAvailable、
        // EncodingIntegrationTests），之后一律裸跑。原实现用一个包住整个结构校验体的
        // catch → Skip("AVIF 编码不可用")，把"容器写坏到让独立解析器抛异常"登记成了环境问题 ——
        // 而本文件头列出的两条动机缺陷（mdat 长度按小端写、iloc 回填少算 2 字节）恰恰是这一类。
        if (!NativeAvifEncoder.IsAvailable)
        {
            Skip("AVIF/box 结构", "avifenc 不可用");
            return;
        }

        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.AVIF);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.AVIF, Quality = 40f, HdrOutput = false,
                ChromaSubsampling = "444", OutputBitDepth = 8,
            };
            if (File.Exists(path)) File.Delete(path);
            encoder.EncodeSdrAsync(MakeBgra8(w, h), w, h, settings, path).GetAwaiter().GetResult();

            if (!File.Exists(path))
            {
                // 可用性刚判过 ⇒ "编码器可用却没落盘"是缺陷，不再是环境限制
                Assert("AVIF/编码产出文件", false, "avifenc 判定可用却没写出文件");
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var boxes = ParseBoxes(bytes);

            Assert("AVIF/顶层 box 解析", boxes.Count >= 3,
                string.Join(",", boxes.Select(b => b.Type)));

            Assert("AVIF/含 ftyp", boxes.Any(b => b.Type == "ftyp"), "");
            Assert("AVIF/含 meta", boxes.Any(b => b.Type == "meta"), "");
            Assert("AVIF/含 mdat", boxes.Any(b => b.Type == "mdat"), "");

            // 回归要点：mdat 的 box size 必须按大端解析。
            // 若按小端写入，此处会得到远大于文件长度的荒谬值。
            var mdat = boxes.FirstOrDefault(b => b.Type == "mdat");
            if (mdat is not null)
            {
                bool sizeSane = mdat.HeaderSize + mdat.PayloadLength <= bytes.Length
                                && mdat.PayloadLength > 0;
                Assert("AVIF/mdat 长度可解析且合理", sizeSane,
                    $"mdat size={mdat.TotalSize} payload={mdat.PayloadLength} 文件={bytes.Length}");
            }

            // 顶层 box 链应精确覆盖整个文件（无残余字节、无越界）
            long covered = boxes.Sum(b => (long)b.TotalSize);
            Assert("AVIF/box 链覆盖整个文件", covered == bytes.Length,
                $"覆盖 {covered} / 文件 {bytes.Length}");

            // meta 内应有 iloc，且其 extent_offset 指向 mdat 数据区
            var meta = boxes.FirstOrDefault(b => b.Type == "meta");
            if (meta is not null)
            {
                long ilocDataOffset = FindIlocExtentOffset(bytes, meta);
                // ═══ 2026-09-19 修假绿：定位失败本身就是容器缺陷，必须发一条断言 ═══
                // 原写法把唯一的断言圈在 if (ilocDataOffset >= 0) 里 ⇒ FindIlocExtentOffset
                // 返回 -1（meta 里找不到 iloc、或盒结构错位到读不动）时一条都不发、passed 计数不变。
                Assert("AVIF/meta 内解析到 iloc extent_offset", ilocDataOffset >= 0,
                    "extent_offset=-1 ⇒ 定位失败即容器又不合规");
                if (mdat is not null)
                {
                    // mdat 数据起点 = mdat.Offset + mdat.HeaderSize。
                    // 原式 mdat.HeaderSize + meta.TotalSize 把"尺寸"当"偏移"用，漏掉了 mdat
                    // 之前所有盒的总长 ⇒ 下界比真实起点小约一个 ftyp，是个宽松到判不出问题的绿。
                    long mdatDataStart = mdat.Offset + mdat.HeaderSize;
                    Assert("AVIF/iloc 偏移落在 mdat 数据区内",
                        ilocDataOffset >= mdatDataStart && ilocDataOffset < mdatDataStart + mdat.PayloadLength,
                        $"extent_offset={ilocDataOffset}, mdat 数据区 [{mdatDataStart}, {mdatDataStart + mdat.PayloadLength})");
                }
            }
        }
        catch (Exception ex)
        {
            // 可用性已在编码之前判过 ⇒ 到这里抛异常（编码失败、或独立解析器读不动容器）
            // 一律判红，绝不再记 Skip。
            Assert("AVIF/box 结构校验未抛异常（独立解析器抛异常 = 容器不合规）", false,
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  TIFF 解析（独立实现，不复用被测代码）
    // ═══════════════════════════════════════

    private sealed class TiffInfo
    {
        public bool Ok;
        public string? Error;
        public long FileLength;
        public int Width, Height, Compression, Photometric, Samples;
        public ushort[] BitsPerSample = [];
        public uint StripOffset, StripByteCount;
        public long ValueAreaEnd;                 // 值区终点（像素数据必须在其后）
        public Dictionary<ushort, (ushort Type, uint Count, uint Value)> Tags = [];
        /// <summary>IFD 里条目的【实际先后】（Tags 是字典，看不出顺序；升序是 TIFF 6.0 的硬要求）。</summary>
        public List<ushort> TagOrder = [];
        private byte[] _data = [];
        private int _bpsField, _xresField, _yresField;
        private uint _iccLen;

        public bool HasAll(ushort[] required) => required.All(t => Tags.ContainsKey(t));

        public byte[] DecompressStrip()
        {
            // ⚠ Tag 8 = "Adobe Deflate" 按规范是【zlib 封装】的流；DeflateStream 读的是裸流，
            //   用它解会把 libtiff/ffmpeg 解不出来的文件判为"正常"。用 ZLibStream 对齐规范读者。
            using var ms = new MemoryStream(_data, (int)StripOffset, (int)StripByteCount, false);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            zs.CopyTo(outMs);
            return outMs.ToArray();
        }

        /// <summary>strip 是否以 zlib 头开头（CMF/FLG：低 4 位 CM=8，且 (CMF&lt;&lt;8|FLG) % 31 == 0）。</summary>
        public bool StripHasZlibHeader()
            => StripOffset + 2 <= FileLength
               && (_data[(int)StripOffset] & 0x0F) == 8
               && ((_data[(int)StripOffset] << 8 | _data[(int)StripOffset + 1]) % 31 == 0);

        public static TiffInfo Parse(string path)
        {
            var info = new TiffInfo { _data = File.ReadAllBytes(path) };
            var d = info._data;
            info.FileLength = d.Length;
            try
            {
                if (d.Length < 8) { info.Error = "文件过短"; return info; }
                if (d[0] != 'I' || d[1] != 'I') { info.Error = "非小端 TIFF"; return info; }
                ushort magic = U16(d, 2);
                if (magic != 42) { info.Error = $"magic={magic} (期望 42)"; return info; }
                uint ifdOff = U32(d, 4);
                if (ifdOff + 2 > d.Length) { info.Error = "IFD 偏移越界"; return info; }

                int count = U16(d, (int)ifdOff);
                long entryStart = ifdOff + 2;
                if (entryStart + (long)count * 12 + 4 > d.Length)
                { info.Error = $"IFD 条目区越界 (count={count})"; return info; }

                for (int i = 0; i < count; i++)
                {
                    int p = (int)(entryStart + i * 12);
                    ushort tag = U16(d, p);
                    ushort type = U16(d, p + 2);
                    uint cnt = U32(d, p + 4);
                    uint val = U32(d, p + 8);
                    info.Tags[tag] = (type, cnt, val);
                    info.TagOrder.Add(tag);
                }

                // 解析关键标签。count=1 且 type=3(SHORT) 时值在低 2 字节（小端）
                static uint Short1(uint v) => v & 0xFFFF;

                if (info.Tags.TryGetValue(256, out var tw)) info.Width = (int)tw.Value;
                if (info.Tags.TryGetValue(257, out var th)) info.Height = (int)th.Value;
                if (info.Tags.TryGetValue(259, out var tc)) info.Compression = (int)Short1(tc.Value);
                if (info.Tags.TryGetValue(262, out var tp)) info.Photometric = (int)Short1(tp.Value);
                if (info.Tags.TryGetValue(277, out var ts)) info.Samples = (int)Short1(ts.Value);

                if (info.Tags.TryGetValue(258, out var bps))
                {
                    info._bpsField = (int)bps.Value;
                    info.BitsPerSample = new ushort[4];
                    for (int i = 0; i < 4; i++) info.BitsPerSample[i] = U16(d, info._bpsField + i * 2);
                }
                // ═══ TIFF 6.0 §2：值的字节数 ≤ 4 时【必须内联在 value 域】，不是指针 ═══
                // 273/279 都是 LONG count=1 = 4 字节 ⇒ 直接取值。
                // 本解析器此前按"值区指针"读它们，等于把写入器的错误假设复刻了一遍 ——
                // 于是"TIFF8/解压后长度正确"一直是绿的，而 libtiff/ffmpeg 解同样的文件得到全黑
                // （2026-09-19 实测）。判据必须来自规范，不能来自我们自己写的第二份实现。
                if (info.Tags.TryGetValue(273, out var so)) info.StripOffset = so.Value;
                if (info.Tags.TryGetValue(279, out var bc)) info.StripByteCount = bc.Value;
                if (info.Tags.TryGetValue(282, out var xr)) info._xresField = (int)xr.Value;
                if (info.Tags.TryGetValue(283, out var yr)) info._yresField = (int)yr.Value;
                if (info.Tags.TryGetValue(34675, out var ic)) info._iccLen = ic.Count;

                // 值区终点 = max(各【真正间接】的值字段末尾)。像素数据必须在其后。
                // 273/279 现在是内联值，不再占值区槽位，故不参与计算。
                info.ValueAreaEnd = new[]
                {
                    (long)info._bpsField + 8,
                    (long)info._xresField + 8,
                    (long)info._yresField + 8,
                    info._iccLen > 0 ? (long)info._yresField + 8 + info._iccLen : 0,
                }.Max();

                info.Ok = true;
            }
            catch (Exception ex) { info.Error = ex.Message; }
            return info;
        }
    }

    private static TiffInfo ParseTiff(string path) => TiffInfo.Parse(path);

    /// <summary>
    /// Alpha 语义的三条硬判据（2026-10-31 补）。
    /// <para>为什么值得单独一条：本仓一直写 <c>SamplesPerPixel=4</c> 却不发 Tag 338 ExtraSamples，
    /// 第 4 个样本的含义是【留给读者猜】。libtiff/tifffile/Pillow 都默认按 alpha 收了，
    /// 所以"外部解码器读着对"并不能证明我们声明过 —— 它只证明别人肯猜。规范口径是
    /// Photometric=2 且 SamplesPerPixel=4 时必须带 338；我们的 alpha 未与颜色预乘 ⇒ 只能取 2
    /// （取 1=Associated 就是在撒谎，读方会按预乘反乘回去）。</para>
    /// <para>再带一条 IFD 升序：漏发/错位的标签最容易在"补一个标签"时被插错位置，
    /// 而插错位置的老 TIFF 阅读器不一定报错，它会【安静地少读一个标签】。</para>
    /// </summary>
    static void AssertExtraSamplesContract(TiffInfo tif, string tag, int expectCount)
    {
        Assert($"{tag}/338 ExtraSamples 在场且=2（未预乘 alpha）",
            tif.Tags.TryGetValue(338, out var ex) && ex.Type == 3 && ex.Count == 1 && ex.Value == 2,
            tif.Tags.TryGetValue(338, out var e2)
                ? $"type={e2.Type} count={e2.Count} value={e2.Value}（3=SHORT/1=条数/2=Unassociated alpha）"
                : "标签缺席 ⇒ 第 4 样本的语义仍由读者猜");
        Assert($"{tag}/标签数 {expectCount}（338 补进来之后总数是写死的契约）",
            tif.Tags.Count == expectCount, $"实得 {tif.Tags.Count}");
        Assert($"{tag}/IFD 条目按 tag 升序（插错位置的读者会安静少读一个标签）",
            tif.TagOrder.SequenceEqual(tif.TagOrder.Order()), string.Join(",", tif.TagOrder));
    }

    // ═══════════════════════════════════════
    //  ISOBMFF box 解析
    // ═══════════════════════════════════════

    private sealed record Box(string Type, long Offset, uint HeaderSize, long PayloadLength)
    {
        public long TotalSize => HeaderSize + PayloadLength;
    }

    private static List<Box> ParseBoxes(byte[] d)
    {
        var list = new List<Box>();
        long pos = 0;
        while (pos + 8 <= d.Length)
        {
            uint size = ReadU32BE(d, (int)pos);
            string type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
            uint header = 8;
            if (size == 1) // 64-bit largesize
            {
                if (pos + 16 > d.Length) break;
                ulong large = ReadU64BE(d, (int)pos + 8);
                header = 16;
                list.Add(new Box(type, pos, header, (long)large - header));
                pos += (long)large;
                continue;
            }
            if (size == 0) { list.Add(new Box(type, pos, header, d.Length - pos - header)); break; }
            if (size < 8) break; // 非法
            list.Add(new Box(type, pos, header, size - header));
            pos += size;
        }
        return list;
    }

    /// <summary>在 meta 内查找 iloc 并返回其 extent_offset（失败返回 -1）。</summary>
    private static long FindIlocExtentOffset(byte[] d, Box meta)
    {
        long end = meta.Offset + meta.TotalSize;
        long pos = meta.Offset + meta.HeaderSize + 4; // meta 是 FullBox: +version/flags
        while (pos + 8 <= end)
        {
            uint size = ReadU32BE(d, (int)pos);
            string type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
            if (size < 8 || pos + size > end) break;
            if (type == "iloc")
            {
                // [size(4)][type(4)][version(1)][flags(3)][sizes(2)][item_count(2)]
                // [item_ID(2)][data_ref(2)][extent_count(2)][extent_offset(4)]
                int off = (int)(pos + 22);
                if (off + 4 <= d.Length) return ReadU32BE(d, off);
                return -1;
            }
            pos += size;
        }
        return -1;
    }

    // ═══════════════════════════════════════
    //  工具
    // ═══════════════════════════════════════

    /// <summary>生成 BGRA8 测试图案：B=0, G=64, R=128, A=255。</summary>
    static byte[] MakeBgra8(int w, int h)
    {
        var b = new byte[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            b[i * 4] = 0;       // B
            b[i * 4 + 1] = 64;  // G
            b[i * 4 + 2] = 128; // R
            b[i * 4 + 3] = 255; // A
        }
        return b;
    }

    static ushort U16(byte[] d, int o) => (ushort)(d[o] | (d[o + 1] << 8));

    static uint U32(byte[] d, int o) =>
        (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));

    static uint ReadU32BE(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    static ulong ReadU64BE(byte[] d, int o) =>
        ((ulong)d[o] << 56) | ((ulong)d[o + 1] << 48) | ((ulong)d[o + 2] << 40) |
        ((ulong)d[o + 3] << 32) | ((ulong)d[o + 4] << 24) | ((ulong)d[o + 5] << 16) |
        ((ulong)d[o + 6] << 8) | d[o + 7];

    static void WriteU16BE(byte[] d, int o, ushort v)
    {
        d[o] = (byte)(v >> 8); d[o + 1] = (byte)v;
    }

    static void Assert(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name} — {detail}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name} — {detail}"); }
    }
}
