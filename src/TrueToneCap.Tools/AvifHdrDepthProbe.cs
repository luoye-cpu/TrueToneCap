// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/AvifHdrDepthProbe.cs
// HDR AVIF 位深 A/B —— 证明"选 12-bit 真的产出 12-bit"，而不是只在能力表里写着 12。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release -- --avif-hdr-depth
//
// 为什么要有这条（2026-09-25）：用户要求"libaom + HDR 应可选 10/12 追求更高精度"。
// 改动的四个落点（协商函数 / 管线 / PQ 量化 / avifenc 命令行）任何一处漏改，
// 结果都是"界面能选 12、产物仍是 10"—— 而那正好是本仓库反复踩的"声明与实现矛盾"形态，
// 单看代码 diff 看不出来。所以判据必须落在【产物】上：
//   ① 请求 10 → ffprobe pix_fmt 含 p10；请求 12 → 含 p12；
//   ② 两次的字节必须不同（相同 ⇒ 位深参数根本没进命令行，直接判 FAIL）；
//   ③ 两次的色彩声明必须都仍是 PQ + BT.2020（放开位深不许把 HDR 声明弄丢）。
// ②③ 是刻意加的"反证"：只断 ① 的话，一个"恒输出 12-bit"的错实现也能同时满足两条。

using System.Diagnostics;
using System.Security.Cryptography;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;

// ⚠ 放在全局命名空间（FormatBench 是 top-level statements，分发处引用全局类型）
public static class AvifHdrDepthProbe
{
    private const int W = 256, H = 256;

    private sealed record Case(string Name, AvifEncoderKind Kind, int Requested);

    public static void Run()
    {
        Console.WriteLine("══════════ AVIF HDR 位深 A/B（10 vs 12）══════════");

        // 本机 ffprobe：与 AvifHdrDiag 同一份默认路径，但允许门禁用 TTC_FFPROBE 把它
        // 已解析出的那一份传进来（两处各写一个绝对路径，换机器时必有一处是过期的）。
        // 找不到就整体判 FAIL —— "跳过交叉验证然后打印完成"等于没测，这里不留 SKIP 这条路。
        string? ffprobe = Environment.GetEnvironmentVariable("TTC_FFPROBE");
        if (string.IsNullOrWhiteSpace(ffprobe))
            ffprobe = @"C:\Windows\ffmpeg-8.0-full_build\bin\ffprobe.exe";
        if (!File.Exists(ffprobe))
        {
            Console.WriteLine($"❌ 未找到 ffprobe（{ffprobe}）⇒ 无法验证产物位深，本次判 FAIL");
            Environment.ExitCode = 1;
            return;
        }

        var cases = new[]
        {
            new Case("libaom-10", AvifEncoderKind.LibAom, 10),
            new Case("libaom-12", AvifEncoderKind.LibAom, 12),
        };

        var outDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_HdrDepth");
        Directory.CreateDirectory(outDir);
        var results = new List<(Case C, string Path, string PixFmt, string Transfer, string Primaries, string Sha, long Bytes)>();

        foreach (var c in cases)
        {
            string path = Path.Combine(outDir, $"{c.Name}.avif");
            try
            {
                Encode(c, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ {c.Name} 编码失败: {ex.Message}");
                Environment.ExitCode = 1;
                return;
            }
            var (pixFmt, transfer, primaries) = Probe(ffprobe, path);
            results.Add((c, path, pixFmt, transfer, primaries,
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                new FileInfo(path).Length));
            Console.WriteLine($"  {c.Name}: pix_fmt={pixFmt} transfer={transfer} primaries={primaries} "
                + $"{results[^1].Bytes / 1024.0:F1} KB");
        }

        var (a10, a12) = (results[0], results[1]);
        bool ok10 = a10.PixFmt.Contains("p10", StringComparison.Ordinal);
        bool ok12 = a12.PixFmt.Contains("p12", StringComparison.Ordinal);
        bool differ = a10.Sha != a12.Sha;
        bool hdrKept = new[] { a10, a12 }.All(r =>
            r.Transfer.Contains("16", StringComparison.Ordinal)
            || r.Transfer.Contains("smpte2084", StringComparison.OrdinalIgnoreCase))
            && new[] { a10, a12 }.All(r =>
                r.Primaries.Contains("9", StringComparison.Ordinal)
                || r.Primaries.Contains("bt2020", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("────────── 判据 ──────────");
        Say(ok10, "请求 10-bit ⇒ 产物 pix_fmt 含 p10", a10.PixFmt);
        Say(ok12, "请求 12-bit ⇒ 产物 pix_fmt 含 p12（本条就是用户要的那档）", a12.PixFmt);
        Say(differ, "两次产物字节不同（相同 ⇒ 位深参数没进 avifenc 命令行）",
            $"{a10.Sha[..12]}… vs {a12.Sha[..12]}…");
        Say(hdrKept, "两次的色彩声明都仍是 PQ(16) + BT.2020(9)",
            $"{a10.Transfer}/{a10.Primaries} vs {a12.Transfer}/{a12.Primaries}");

        bool pass = ok10 && ok12 && differ && hdrKept;
        Console.WriteLine($"══════════ {(pass ? "✅ 12-bit 在 HDR 下真实产出" : "❌ HDR 位深未真正生效")} ══════════");
        Environment.ExitCode = pass ? 0 : 1;
    }

    private static void Say(bool ok, string what, string readout) =>
        Console.WriteLine($"  {(ok ? "✅" : "❌")} {what} —— 实得 {readout}");

    private static void Encode(Case c, string path)
    {
        if (File.Exists(path)) File.Delete(path);

        // 合成 Float16 scRGB 源：横向渐变 + 竖条纹理，含 >1.0 的高光（HDR 头部余量）
        var hdr = new float[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                float baseV = 0.05f + 0.9f * x / (W - 1);
                float tex = ((x >> 2) & 1) == 1 ? 3.5f : 1.0f;
                hdr[i] = hdr[i + 1] = hdr[i + 2] = baseV * tex;
                hdr[i + 3] = 1f;
            }

        var settings = new EncodingSettings
        {
            Format = OutputFormat.AVIF,
            Quality = 15f,
            HdrOutput = true,
            AvifEncoderKind = c.Kind,
            ColorSpaceTag = "BT2020",
            OutputBitDepth = c.Requested,
            ChromaSubsampling = "444",   // 软件编码器可达全色度，且与位深正交
            ToneMappingParams = new ToneMappingParams
            {
                Mode = ToneMapMode.SegmentedReinhard,
                PaperWhiteNits = 200f,
                DisplayMaxNits = 1000f,
            },
        };

        var encoder = EncoderFactory.Create(settings.Format);
        encoder.EncodeAsync(new HdrFrameData { Pixels = hdr, Width = W, Height = H },
            settings, path).GetAwaiter().GetResult();
    }

    private static (string PixFmt, string Transfer, string Primaries) Probe(string ffprobe, string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = $"-v error -select_streams v:0 "
                + "-show_entries stream=pix_fmt,color_transfer,color_primaries -of csv=p=0:s=, \"{PATH}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.Arguments = psi.Arguments.Replace("{PATH}", path.Replace('\\', '/'));
        using var p = Process.Start(psi);
        if (p is null) return ("", "", "");
        string output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15000);
        var f = output.Split(',');
        return (f.Length > 0 ? f[0].Trim() : "", f.Length > 1 ? f[1].Trim() : "",
            f.Length > 2 ? f[2].Trim() : "");
    }
}
