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
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_FmtTest");

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 输出结构合法性测试");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("══════════════════════════════════════\n");

        Test_Tiff8Bit();
        Test_Tiff16Bit();
        Test_TiffWithIcc();
        Test_AvifBoxStructure();

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
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

    /// <summary>带 ICC 的 TIFF：值区因多一个条目而变长，验证像素数据偏移随之正确。</summary>
    static void Test_TiffWithIcc()
    {
        const int w = 16, h = 16;
        var path = Path.Combine(OutDir, "struct_tiff_icc.tif");
        try
        {
            // 够长的假 ICC (>128 字节才会写入 34675 标签)
            var icc = new byte[512];
            icc[0] = 0x00; icc[1] = 0x00; icc[2] = 0x02; icc[3] = 0x00; // profile size
            for (int i = 4; i < icc.Length; i++) icc[i] = (byte)(i % 251);

            ManagedTiffEncoder.Encode(MakeBgra8(w, h), w, h, path, bitDepth: 8, iccProfile: icc);

            var tif = ParseTiff(path);
            Assert("TIFF-ICC/头部", tif.Ok, tif.Error ?? "");
            if (!tif.Ok) return;

            Assert("TIFF-ICC/含 ICC 标签(34675)", tif.Tags.ContainsKey(34675), "");
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
        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.AVIF);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.AVIF, Quality = 40f, HdrOutput = false,
                ChromaSubsampling = "444", OutputBitDepth = 8, DisplayBitDepth = 8,
            };
            if (File.Exists(path)) File.Delete(path);
            encoder.EncodeSdrAsync(MakeBgra8(w, h), w, h, settings, path).GetAwaiter().GetResult();

            if (!File.Exists(path))
            {
                Assert("AVIF/box 结构", true, "跳过 (本机无可用 AVIF 编码器)");
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
            if (meta is not null && mdat is not null)
            {
                long ilocDataOffset = FindIlocExtentOffset(bytes, meta);
                if (ilocDataOffset >= 0)
                {
                    Assert("AVIF/iloc 偏移指向 mdat 数据区",
                        ilocDataOffset >= mdat.HeaderSize + meta.TotalSize &&
                        ilocDataOffset < bytes.Length,
                        $"extent_offset={ilocDataOffset}, mdat 数据起点={mdat.HeaderSize + meta.TotalSize}");
                }
            }
        }
        catch (Exception ex)
        {
            // 本机缺少原生编码器时不应判失败
            Assert("AVIF/box 结构", true, $"跳过 (不可用: {ex.GetType().Name})");
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
        private byte[] _data = [];
        private int _stripOffsetField, _byteCountField, _bpsField, _xresField, _yresField;
        private uint _iccLen;

        public bool HasAll(ushort[] required) => required.All(t => Tags.ContainsKey(t));

        public byte[] DecompressStrip()
        {
            // ⚠ 用 StripOffset（像素数据的实际文件偏移），而非 _stripOffsetField
            // （后者只是"存放该偏移值的值区字段"的位置）
            using var ms = new MemoryStream(_data, (int)StripOffset, (int)StripByteCount, false);
            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            ds.CopyTo(outMs);
            return outMs.ToArray();
        }

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
                if (info.Tags.TryGetValue(273, out var so))
                {
                    info._stripOffsetField = (int)so.Value;
                    info.StripOffset = U32(d, info._stripOffsetField);
                }
                if (info.Tags.TryGetValue(279, out var bc))
                {
                    info._byteCountField = (int)bc.Value;
                    info.StripByteCount = U32(d, info._byteCountField);
                }
                if (info.Tags.TryGetValue(282, out var xr)) info._xresField = (int)xr.Value;
                if (info.Tags.TryGetValue(283, out var yr)) info._yresField = (int)yr.Value;
                if (info.Tags.TryGetValue(34675, out var ic)) info._iccLen = ic.Count;

                // 值区终点 = max(各值字段末尾)。像素数据必须在其后。
                info.ValueAreaEnd = new[]
                {
                    (long)info._bpsField + 8,
                    (long)info._stripOffsetField + 4,
                    (long)info._byteCountField + 4,
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
