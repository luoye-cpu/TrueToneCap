// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/AvifBackendLadder.cs
// AVIF 后端 × CRF 实测阶梯 (--avif-ladder)
//
// 为什么需要它（交接 §48，2026-09-24）：质量阶梯的前两轮都是用 avifenc 命令行直接跑的，
// 而 avifenc 只代表【软件分支】。产品的默认「Auto」在 4:2:0 下会优先选 NVENC
// （AvifEncoderCapabilities.PickAuto 的 Order() 硬件在前），而 NVENC 吃的是同一个
// `crf` 整数、走的是完全不同的码控（NvencAvifBackend → nv.EncodeAv1(…, crf, …)）。
// ⇒ 拿 avifenc 的 CRF–质量曲线去定产品默认 CRF，是在给一条用户根本不走的路标定。
//
// 本探针【不复制任何参数拼接逻辑】：它只调产品自己的
// AvifEncoder.EncodeSdrAsync(px, w, h, settings, path)，所以色度、位深、cicp、
// 后端选择、回退路径全部与真实截图保存同一条路。
//
// ⚠ "分派到哪个后端"与"哪个后端真的编了"是两件事，本探针把后者当一等公民来验：
//   NVENC 的降级原因写在 NvencAvifBackend.LastFallbackReason（internal，Core 的
//   InternalsVisibleTo 只给 TrueToneCap.Test），通用通道 EncoderFallbackNotifier 又不被
//   那几个回退分支写入 ⇒ 光读 LastReason 会把"整批退回 libaom"报成"无降级"（实测踩过）。
//   所以每档都再走一次【强制 libaom】作对照，产物逐字节相同即判定"没走硬件"。
//   这条对照同时探测到：CRF 0 的 InitEncoder 失败会粘滞住 s_nvencFailed，
//   让同进程后续所有档位都静默退回软件 —— 见 --crf 2,0,5 的顺序实验。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release --
//           --avif-ladder <png> [outDir] [--backend Auto|Nvenc|Qsv|LibAom|SvtAv1]
//                             [--chroma 420|422|444] [--depth 8|10] [--crf 0,2,5,...]

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using TrueToneCap.Core.Encoding;

public static class AvifBackendLadder
{
    public static int Run(string[] args)
    {
        string png = args.Length > 1 ? args[1] : throw new ArgumentException("--avif-ladder 需要 <png>");
        if (!File.Exists(png)) { Console.WriteLine($"❌ 找不到输入: {png}"); return 2; }

        string outDir = args.Length > 2 && !args[2].StartsWith("--")
            ? args[2]
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(png)) ?? ".", "avif-ladder");
        Directory.CreateDirectory(outDir);

        var kind = ParseEnum(args, "--backend", AvifEncoderKind.Auto);
        string chroma = ParseStr(args, "--chroma") ?? "420";
        int depth = ParseInt(args, "--depth") ?? 8;
        var crfs = ParseList(args, "--crf") ?? new[] { 0f, 2f, 5f, 8f, 10f, 12f, 15f, 18f, 22f, 26f, 30f };

        var (px, w, h) = LoadBgra(png);

        Console.WriteLine($"输入 {Path.GetFileName(png)} {w}x{h}  色度={chroma}  位深请求={depth}  "
                          + $"请求后端={kind}");

        // 先把"产品会选谁"讲清楚：这一步不编码，只复现分派结果。
        // 注意 kind/chroma/depth 在全程固定 ⇒ 分派结果也固定，所以它是一列常数；
        // 真正区分"选了硬件"与"硬件半路退回软件"的是【降级原因】那一列
        // （NvencAvifBackend 的失败分支会调 AvifFallbackHelper.FallbackToLibAom 并写 LastReason）。
        var resolved = AvifEncoderSelector.SelectByKind(kind, chroma, depth).Backend;
        Console.WriteLine($"SelectByKind({kind}, {chroma}, {depth}) → 分派到后端 {resolved}");
        Console.WriteLine();
        Console.WriteLine($"{"CRF",4} {"分派后端",-10} {"硬件真的参与?",-15} {"ms",6} {"KB",9}  文件");
        Console.WriteLine(new string('-', 100));

        int fail = 0;
        foreach (float crf in crfs)
        {
            string path = Path.Combine(outDir, $"avif_{kind}_{crf.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}_c{chroma}_d{depth}.avif");
            var s = new EncodingSettings
            {
                Format = OutputFormat.AVIF,
                Quality = crf,
                HdrOutput = false,
                ChromaSubsampling = chroma,
                OutputBitDepth = depth,
                AvifEncoderKind = kind,
                ColorSpaceTag = "sRGB",
            };
            EncoderFallbackNotifier.LastReason = null;      // 读完即清，避免陈旧原因串到下一档
            var sw = Stopwatch.StartNew();
            string note;
            try
            {
                new AvifEncoder().EncodeSdrAsync(px, w, h, s, path, CancellationToken.None)
                                 .GetAwaiter().GetResult();
                note = EncoderFallbackNotifier.LastReason is { } r ? Trunc(r) : "";
            }
            catch (Exception ex)
            {
                fail++;
                note = Trunc("❌ " + ex.Message);
            }
            sw.Stop();
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;

            // ★ 硬件到底有没有参与，不能问 EncoderFallbackNotifier：
            //   NVENC 的降级原因走的是 NvencAvifBackend.LastFallbackReason（internal，且 Core 的
            //   InternalsVisibleTo 只授予 TrueToneCap.Test），Tools 读不到 ⇒ 上一列恒为"（无）"，
            //   实测就把"整批其实都退回了 libaom"报成了无降级。改用可观测判据：
            //   同一帧再走一次【强制 libaom】，产物逐字节相同 ⇔ 本次根本没走硬件。
            //   这条判据同时也是 s_nvencFailed 粘滞的探测器：CRF 0 失败污染后续档位时，
            //   只有这里会诚实地说"否"。
            string used = "n/a（请求即软件）";
            if (kind != AvifEncoderKind.LibAom && File.Exists(path))
            {
                string ctl = path + ".ctl";
                try
                {
                    var cs = new EncodingSettings
                    {
                        Format = OutputFormat.AVIF,
                        Quality = crf,
                        HdrOutput = false,
                        ChromaSubsampling = chroma,
                        OutputBitDepth = depth,
                        AvifEncoderKind = AvifEncoderKind.LibAom,
                        ColorSpaceTag = "sRGB",
                    };
                    new AvifEncoder().EncodeSdrAsync(px, w, h, cs, ctl, CancellationToken.None)
                                     .GetAwaiter().GetResult();
                    bool same = new FileInfo(path).Length == new FileInfo(ctl).Length
                        && File.ReadAllBytes(path).SequenceEqual(File.ReadAllBytes(ctl));
                    used = same ? "否：与 libaom 逐字节相同" : "是：与 libaom 产物不同";
                }
                catch (Exception ex) { used = "判据失败:" + Trunc(ex.Message); }
                finally { try { File.Delete(ctl); } catch { } }
            }
            Console.WriteLine($"{crf,4} {resolved,-10} {used,-15} {sw.ElapsedMilliseconds,6} "
                              + $"{bytes / 1024.0,9:F1}  {Path.GetFileName(path)}  {note}");
        }
        Console.WriteLine($"\n明细文件目录 → {outDir}");
        return fail == 0 ? 0 : 1;
    }

    // ── 工具 ──
    private static string Trunc(string s)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= 32 ? s : s[..32];
    }

    private static (byte[], int, int) LoadBgra(string png)
    {
        using var bmp = new Bitmap(png);
        int w = bmp.Width, h = bmp.Height;
        using var bmp32 = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp32))
            g.DrawImage(bmp, 0, 0, new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);
        var rect = new Rectangle(0, 0, w, h);
        var data = bmp32.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Stride 可能含填充 ⇒ 逐行拷，不能一次 memcpy 整块
            var buf = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy(
                    data.Scan0 + y * data.Stride, buf, y * w * 4, w * 4);
            return (buf, w, h);
        }
        finally { bmp32.UnlockBits(data); }
    }

    private static T ParseEnum<T>(string[] args, string flag, T dft) where T : struct
        => ParseStr(args, flag) is { } v && Enum.TryParse<T>(v, true, out var r) ? r : dft;

    private static string? ParseStr(string[] args, string flag)
    {
        int i = Array.FindIndex(args, a => a == flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static int? ParseInt(string[] args, string flag)
        => ParseStr(args, flag) is { } v && int.TryParse(v, out var r) ? r : null;

    private static float[]? ParseList(string[] args, string flag)
    {
        var v = ParseStr(args, flag);
        if (v is null) return null;
        var parts = v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var arr = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            arr[i] = float.Parse(parts[i], System.Globalization.CultureInfo.InvariantCulture);
        return arr;
    }
}
