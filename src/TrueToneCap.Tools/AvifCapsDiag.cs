// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/AvifCapsDiag.cs
// AVIF 编码器实测能力诊断 —— 逐个编码器真编码小样张，解析产物 av1C 读回真实位深/色度。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release -- --avif-caps [--ffprobe]
//   --ffprobe  附带用外部 ffprobe 交叉验证（独立于本仓库的解析器，见交接 §3 教训）。
//
// 为什么要有它：AvifEncoderCapabilities 是手写静态表；本工具给出**本机实测值**，
// 两者不一致时以实测为准（UI 亦按实测过滤可选项）。

using System.Diagnostics;
using TrueToneCap.Core.Encoding;

// ⚠ 与 PngCheck / IccCheck 同型：放在全局命名空间（FormatBench.cs 是 top-level statements，
// 也在全局命名空间），否则分发处引用不到本类型。
public static class AvifCapsDiag
{
    public static void Run(string[] args)
    {
        bool useFfprobe = args.Any(a => a.Equals("--ffprobe", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("════ AVIF 编码器实测能力 ════");
        Console.WriteLine("判据: 真实编码 256×256 样张 → 解析产物 av1C → 读回实际位深/色度");
        Console.WriteLine();
        // ═══ 本机软件编码器的"编进来没有"是实测的前提 ═══
        // 内嵌 avifenc 若未编译某个 codec（如 SVT），对该 codec 的任何"实测"都是在测别的
        // 编码器（会被误归因）。故先把 --version 的 codec 清单摆在最前面。
        Console.WriteLine($"[本机 avifenc] {(NativeAvifEncoder.IsAvailable ? NativeAvifEncoder.VersionText.Trim() : "不可用")}");
        Console.WriteLine($"  SVT-AV1 是否编译进本二进制: {(NativeAvifEncoder.IsSvtAvailable ? "是" : "否")}");
        if (NativeAvifEncoder.SvtUnavailableReason is { } why) Console.WriteLine($"  原因: {why}");
        Console.WriteLine();

        Console.WriteLine("[静态能力表]（手写，供对比）");
        foreach (var kind in Kinds())
        {
            var c = AvifEncoderCapabilities.Caps(kind);
            string chroma = (c.Supports444, c.Supports422) switch
            {
                (true, true) => "420/422/444",
                (false, true) => "420/422",
                _ => "仅420",
            };
            Console.WriteLine($"  {kind,-8} 色度 {chroma,-11} SDR ≤{AvifEncoderCapabilities.SdrBitDepthFor(kind)}-bit"
                + $"  HDR {(AvifEncoderCapabilities.SupportsHdrPath(kind) ? "可用" : "不可用")}");
        }
        Console.WriteLine();

        Console.WriteLine("[实测]");
        var sw = Stopwatch.StartNew();
        foreach (var kind in Kinds())
        {
            var m = AvifEncoderProbe.EnsureMeasured(kind);
            Console.WriteLine($"  {m}");
            if (m.Measured && kind != AvifEncoderKind.Auto)
            {
                var st = AvifEncoderCapabilities.Caps(kind);
                int declaredSdr = AvifEncoderCapabilities.SdrBitDepthFor(kind);
                if (declaredSdr != m.MaxBitDepth)
                    Console.WriteLine($"    ⚠ 与静态表不一致: 声明 ≤{declaredSdr}-bit，实测 ≤{m.MaxBitDepth}-bit");
                bool declared444 = st.Supports444;
                if (declared444 != m.Chroma444)
                    Console.WriteLine($"    ⚠ 与静态表不一致: 声明 444={(declared444 ? "支持" : "不支持")}，实测 {(m.Chroma444 ? "支持" : "不支持")}");
            }
        }
        sw.Stop();
        Console.WriteLine($"  耗时 {sw.Elapsed.TotalSeconds:F1}s");

        if (useFfprobe) CrossCheckWithFfprobe();

        Console.WriteLine();
        Console.WriteLine("[官方来源结论]（本机无法实测时以此为准）");
        Console.WriteLine("  · libavif src/codec_svt.c: 444→EB_YUV444 / 422→EB_YUV422 / 420→EB_YUV420；");
        Console.WriteLine("    encoder_bit_depth = image->depth，depth>10 或 422 → PROFESSIONAL_PROFILE、444 → HIGH_PROFILE");
        Console.WriteLine("    ⇒ avifenc --codec svt 路径对 420/422/444 与 >10-bit 均有代码支撑");
        Console.WriteLine("  · SVT-AV1 官方 Encoder User Guide: 独立 app 仅声明 8-bit yuv420p 与 10-bit yuv420p10le 输入");
        Console.WriteLine("    （app 的输入限制 ≠ 库能力，故 SvtAv1 位深在能力表保守取 10）");
        Console.WriteLine();
        Console.WriteLine("注: 静态表与实测不一致时以实测为准（UI 亦按实测过滤可选项）。");
    }

    private static AvifEncoderKind[] Kinds() =>
    [
        AvifEncoderKind.Auto, AvifEncoderKind.Nvenc, AvifEncoderKind.Qsv,
        AvifEncoderKind.SvtAv1, AvifEncoderKind.LibAom, AvifEncoderKind.Avifenc,
    ];

    /// <summary>
    /// 用**外部** ffprobe 交叉验证 —— 交接 §3 的教训：绝不能用自己的第二份实现做交叉验证。
    /// 本方法独立于 AvifEncoderProbe.TryParseAv1C（后者是本仓库的解析代码），
    /// 两者结论一致才能确认实测判据可信。
    /// </summary>
    private static void CrossCheckWithFfprobe()
    {
        string ffprobe = @"C:\Windows\ffmpeg-8.0-full_build\bin\ffprobe.exe";
        if (!File.Exists(ffprobe))
        {
            Console.WriteLine();
            Console.WriteLine("[ffprobe 交叉验证] 跳过：未找到 " + ffprobe);
            return;
        }

        Console.WriteLine();
        Console.WriteLine("[ffprobe 交叉验证]（外部独立实现）");
        foreach (var kind in new[] { AvifEncoderKind.LibAom, AvifEncoderKind.Nvenc })
        {
            foreach (var (chroma, depth) in new[] { ("420", 8), ("444", 8), ("420", 10), ("420", 12) })
            {
                var be = AvifEncoderSelector.SelectByKind(kind, chroma, depth);
                if (!be.IsAvailable) { Console.WriteLine($"  {kind}: 不可用"); continue; }

                var path = Path.Combine(Path.GetTempPath(), $"ttc_ffprobe_{Guid.NewGuid():N}.avif");
                int size = 256;
                var px = new byte[size * size * 4];
                for (int i = 0; i < px.Length; i += 4)
                { px[i] = 128; px[i + 1] = (byte)(i % 255); px[i + 2] = 200; px[i + 3] = 255; }

                try
                {
                    Task.Run(() => be.EncodeAsync(px, size, size, 30, path,
                        CancellationToken.None, chroma, depth)).Wait(TimeSpan.FromSeconds(30));
                    if (!File.Exists(path)) { Console.WriteLine($"  {kind} {chroma}/{depth}: 无产物"); continue; }

                    var mine = AvifEncoderProbe.TryParseAv1C(File.ReadAllBytes(path), out var info)
                        ? $"{info.BitDepth}-bit/{info.Chroma}" : "解析失败";

                    var psi = new ProcessStartInfo
                    {
                        FileName = ffprobe,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 \"{path}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true,
                    };
                    using var p = Process.Start(psi);
                    string? outp = p?.StandardOutput.ReadToEnd().Trim();
                    p?.WaitForExit(15000);

                    Console.WriteLine($"  {kind,-7} 请求 {chroma}/{depth}-bit → 本仓库解析 {mine,-12} | ffprobe pix_fmt={outp}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {kind} {chroma}/{depth}: {ex.Message}");
                }
                finally { try { File.Delete(path); } catch { } }
            }
        }
    }
}
