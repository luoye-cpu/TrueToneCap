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
//   （2026-09-24 成文时）NVENC 的降级原因写在 NvencAvifBackend.LastFallbackReason（当时是 internal，
//   Core 的 InternalsVisibleTo 只给 TrueToneCap.Test），通用通道 EncoderFallbackNotifier 又不被
//   那几个回退分支写入 ⇒ 光读 LastReason 会把"整批退回 libaom"报成"无降级"（实测踩过）。
//   所以每档都再走一次【强制 libaom】作对照，产物逐字节相同即判定"没走硬件"。
//   这条对照同时探测到：CRF 0 的 InitEncoder 失败会粘滞住 s_nvencFailed，
//   让同进程后续所有档位都静默退回软件 —— 见 --crf 2,0,5 的顺序实验。
//   ⚠ 该字段的可见性后来变了（现为 public，理由见下方 ③），但**判据不变**：
//     只报"逐字节对照"，不拿"降级原因"当"有没有走硬件"的依据。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release --
//           --avif-ladder <png> [outDir] [--backend Auto|Nvenc|Qsv|LibAom|SvtAv1]
//                             [--chroma 420|422|444] [--depth 8|10] [--crf 0,2,5,...]
//
// ═══ 2026-09-27（#20 补 SVT-AV1 / 系统 MFT(QSV) 两条分支）本文件新增三件事 ═══
// ① **进程开场先自报家底**：avifenc 的 --version 与"SVT 有没有编进这颗二进制"、
//    GpuCapability 看到的适配器、MFT 的 CLSID 能不能 CoCreateInstance、每个 kind 的
//    IsKindAvailable / SelectByKind 落点 / MinCrf。理由：SVT 那一条如果内嵌 avifenc 根本
//    没有 svt，`--codec svt` 会静默回落 aom ⇒ 整列都是在测 libaom 却记成 SVT（§26 踩过）。
// ② **每行落 md5 + 机器可读的 rows.tsv**：p1 由外部尺子（ffmpeg 解码 + 分块）算，
//    C# 侧只负责"产物是什么、谁产的、和强制 libaom 对照是否逐字节相同"。
//    带 md5 是为了让"两条不同后端在同一 CRF 上产出同一码流"这种事无法靠肉眼看体积蒙过去。
// ③ **降级原因按通道如实标注**，不许拿"（无）"当"没降级"：
//    · EncoderFallbackNotifier.LastReason —— MFT/QSV 的回退**会**写这里（实测原文
//      "系统 MFT 硬件编码（QSV_MFT）本次失败：MFT 编码器创建失败: 0x80040154"），
//      所以 QSV 这一列的"没跑起来"是有原文可查的，不是靠反推。
//    · NvencAvifBackend.LastFallbackReason —— 该字段**现为 public**（App 层要读它提示用户），
//      本探针一并打印。⚠ 48.4 成文时它是 internal ⇒ 那条"恒报（无）"的教训对今天的 Tools
//      已不成立，但**判据不变**：原因字段只说明"后端自己声称发生了什么"，
//      逐字节对照才说明"产物到底是谁编的"。两者不一致时以字节对照为准。
//    · NvencAvifBackend / MftAvifEncode 的 LastEncodeUsedHardware 仍是 internal
//      （Core 的 InternalsVisibleTo 只给 TrueToneCap.Test）⇒ Tools 读不到，不假装读得到。

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;
using TrueToneCap.Core.Encoding;

public static class AvifBackendLadder
{
    private const string TsvName = "ladder_rows.tsv";

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
        Console.WriteLine($"输入文件 md5 = {Md5(png)}   （换一张源就换了一整批读数，故先钉住它）");
        Console.WriteLine();
        Console.WriteLine("── 开场家底（这几行决定'某条分支到底是没跑起来，还是跑起来了但产物与别的分支相同'）──");
        foreach (var line in EnvironmentLines(kind, chroma, depth))
            Console.WriteLine(line);
        Console.WriteLine();

        // 先把"产品会选谁"讲清楚：这一步不编码，只复现分派结果。
        // 注意 kind/chroma/depth 在全程固定 ⇒ 分派结果也固定，所以它是一列常数；
        // 真正区分"选了硬件"与"硬件半路退回软件"的是【降级原因】那一列
        // （NvencAvifBackend 的失败分支会调 AvifFallbackHelper.FallbackToLibAom 并写 LastReason）。
        var resolved = AvifEncoderSelector.SelectByKind(kind, chroma, depth).Backend;
        Console.WriteLine($"SelectByKind({kind}, {chroma}, {depth}) → 分派到后端 {resolved}");
        Console.WriteLine();
        Console.WriteLine($"{"CRF",4} {"分派后端",-10} {"本行是否真由请求后端产出?",-24} {"KB",9} {"md5(产物)",-34}  文件");
        Console.WriteLine(new string('-', 130));

        var rows = new List<string>();
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
            NvencAvifBackend.LastFallbackReason = null;     // 同上（NVENC 专用通道，现为 public）
            var sw = Stopwatch.StartNew();
            string note, err = "";
            try
            {
                new AvifEncoder().EncodeSdrAsync(px, w, h, s, path, CancellationToken.None)
                                 .GetAwaiter().GetResult();
                note = EncoderFallbackNotifier.LastReason ?? "";
            }
            catch (Exception ex)
            {
                fail++;
                err = ex.GetType().Name + ": " + ex.Message;
                note = "❌ " + err;
            }
            if (NvencAvifBackend.LastFallbackReason is { } nv)
                note = (note.Length > 0 ? note + " | " : "") + "NVENC通道: " + nv;
            sw.Stop();
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            string md5 = bytes > 0 ? Md5(path) : "";

            // ★ 后端到底有没有参与，不能问任何"降级原因"通道（NVENC 的 LastFallbackReason 是
            //   后端自己写的，MFT 的更是只在失败分支置位）：
            //   同一帧再走一次【强制 libaom】，产物逐字节相同 ⇔ 本次根本没走请求的那个后端。
            //   这条判据同时也是 s_nvencFailed 粘滞的探测器：CRF 0 失败污染后续档位时，
            //   只有这里会诚实地说"否"。
            string used = "n/a（请求即 libaom，无需对照）";
            string ctlMd5 = ""; long ctlBytes = 0;
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
                    ctlBytes = new FileInfo(ctl).Length;
                    ctlMd5 = Md5(ctl);
                    bool same = ctlBytes == bytes && ctlMd5 == md5;
                    used = same ? "否：与强制 libaom 逐字节相同" : "是：与强制 libaom 产物不同";
                }
                catch (Exception ex) { used = "判据失败:" + ex.Message; }
                finally { try { File.Delete(ctl); } catch { } }
            }
            rows.Add(string.Join('\t',
                kind.ToString(), Fmt(crf), Path.GetFileName(path), bytes, md5, ctlBytes, ctlMd5,
                used, resolved.ToString(), note.Replace('\t', ' ').Replace('\n', ' '), err));
            Console.WriteLine($"{crf,4} {resolved,-10} {used,-24} {bytes / 1024.0,9:F1} "
                              + $"{md5,-34}  {Path.GetFileName(path)}");
            // 原因列单独成行：原文可能上百字，截断就等于把"0x80040154 未注册"这类
            // 唯一能区分"没跑起来"与"跑起来了但同字节"的证据丢掉。
            if (note.Length > 0) Console.WriteLine($"       ↳ 原因原文：{note}");
        }
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join('\t', "kind", "crf", "file", "bytes", "md5", "ctl_bytes",
                "ctl_md5", "requested_backend_actually_used", "dispatched_backend", "reason", "error"));
            foreach (var r in rows) sb.AppendLine(r);
            File.WriteAllText(Path.Combine(outDir, $"{kind}_{TsvName}"), sb.ToString(), new UTF8Encoding(false));
        }
        catch (Exception ex) { Console.WriteLine("⚠ rows.tsv 写入失败：" + ex.Message); }
        Console.WriteLine($"\n明细文件目录 → {outDir}");
        return fail == 0 ? 0 : 1;
    }

    // ── 开场家底 ──
    // 只做探测与打印，不改任何编码行为；每一项都写清"读不到"与"读到 false"的差别。
    private static IEnumerable<string> EnvironmentLines(AvifEncoderKind kind, string chroma, int depth)
    {
        yield return $"avifenc（软件分支的真身）: IsAvailable={NativeAvifEncoder.IsAvailable}  "
                     + $"版本={Safe(() => NativeAvifEncoder.VersionText.Trim())}";
        yield return $"  SVT-AV1 是否编进这颗 avifenc: {NativeAvifEncoder.IsSvtAvailable}"
                     + (NativeAvifEncoder.SvtUnavailableReason is { } why ? $"  原因: {why}" : "");
        yield return "GpuCapability.DetectEncoders(): " + Safe(() =>
            string.Join("; ", GpuCapability.DetectEncoders()
                .Select(e => $"{e.Type}/{e.AdapterName} Available={e.Available} SupportsAv1={e.SupportsAv1}")));
        yield return $"系统 MFT(AV1) CoCreateInstance: IsAv1MftAvailable={Safe(() => MftEncoderNative.IsAv1MftAvailable)}"
                     + $"  本机有 Intel 适配器={Safe(() => MftEncoderNative.IsIntelGpuAvailable)}"
                     + "   ← Qsv 档走的就是这条（见 QsvAvifBackend.IsAvailable），false ⇒ 必然回退 libaom";
        yield return "AvifHardwareProbe（产品自己的实测探测，256×256 真编码）: "
                     + Safe(() => AvifHardwareProbe.GetSummary());
        foreach (var k in new[] { AvifEncoderKind.Nvenc, AvifEncoderKind.Qsv, AvifEncoderKind.LibAom,
                                  AvifEncoderKind.SvtAv1, AvifEncoderKind.Avifenc })
        {
            var caps = AvifEncoderCapabilities.Caps(k);
            yield return $"  {k,-7} IsKindAvailable={Safe(() => AvifEncoderSelector.IsKindAvailable(k).ToString())}"
                         + $"  SelectByKind(_, \"{chroma}\", {depth})→{Safe(() => AvifEncoderSelector.SelectByKind(k, chroma, depth).Backend.ToString())}"
                         + $"  分派后端实例={Safe(() => AvifEncoderSelector.SelectByKind(k, chroma, depth).GetType().Name)}"
                         + $"  avifenc --codec={AvifEncoderSelector.ResolveAvifencCodec(k)}"
                         + $"  MinCrf={AvifEncoderCapabilities.MinCrf(k)}"
                         + $"  SDR 位深上限={AvifEncoderCapabilities.SdrBitDepthFor(k)}"
                         + $"  能力表显示名={caps.DisplayName}";
        }
    }

    private static string Safe(Func<string> f)
    {
        try { return f(); } catch (Exception ex) { return "读取失败(" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    private static string Safe(Func<bool> f)
    {
        try { return f().ToString(); } catch (Exception ex) { return "读取失败(" + ex.GetType().Name + ": " + ex.Message + ")"; }
    }

    // ── 工具 ──
    private static string Fmt(float v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private static string Md5(string path)
    {
        using var md5 = MD5.Create();
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
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
