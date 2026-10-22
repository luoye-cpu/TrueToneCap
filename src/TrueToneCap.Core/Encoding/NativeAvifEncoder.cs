// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/NativeAvifEncoder.cs
// AVIF 原生编码器 — 通过 avifenc 命令行工具编码 (libaom 软件路径)
// 硬件编码路径 (MFT/NVENC/QSV) 保持不变

using System.Runtime.InteropServices;
using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>AVIF 原生编码器 — avifenc 命令行 (libaom)，内嵌在 Resources/avifenc.exe。</summary>
public static class NativeAvifEncoder
{
    // 静态构造器：确保嵌入的 avifenc.exe 已提取到 native/ 目录
    static NativeAvifEncoder()
    {
        NativeLibraryResolver.Initialize();
    }

    private static bool? _available;

    // ═══ AVIF 软件编码器选择（2026-09-16）═══
    // 对应 avifenc 的 `--codec` 参数：
    //   Auto   —— 交给 avifenc 自行选择（默认）
    //   LibAom —— libaom：参考实现，兼容性最好，本程序内嵌的 avifenc 默认带的就是它
    //   SvtAv1 —— SVT-AV1：面向多核与吞吐优化，同画质下通常更快；
    //             但**需要 avifenc 在编译时启用 SVT**。自 2026-09-19 起内嵌的是自编译的 1.4.2 构建
    //             （aom + svt v4.1.0 + libpng/zlib，见 scripts/build-avifenc.py），
    //             `--version` 含 `svt [enc]` ⇒ 本机可用；实测能力仅 4:2:0、≤10-bit
    //             （见 AvifEncoderCapabilities 的 SvtAv1 条目）。
    //             ⚠ 若换回官方 aom-only 构建，GetCodecArg 会静默回落 aom，届时"SVT 的实测值"
    //             其实全是 aom 的 —— 2026-09-18 踩过这个误归因。
    // 若指定的编码器在当前构建中不可用，avifenc 会报错，由调用方回退（见 EncodeWithFallback）。
    // P2-5 修复：不再使用全局静态 Codec，改为按调用传参（避免并发竞态）。

    private static string _versionText = "";

    /// <summary>
    /// 当前 avifenc 是否编译进了 SVT-AV1。
    /// <para>依据：`avifenc --version` 会列出可用编解码器，形如
    /// `Version: 1.4.2 (dav1d [dec]:1.5.3, aom [enc]:3.14.1)` —— 只出现 aom 即表示未启用 SVT。</para>
    /// </summary>
    public static bool IsSvtAvailable =>
        IsAvailable && _versionText.Contains("svt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// `avifenc --version` 的原始输出（列出**实际编译进该二进制**的编解码器）。
    /// 例：`Version: 1.4.2 (dav1d [dec]:1.5.3-0-gb546257, aom [enc]:3.14.1)`
    /// —— 只有 `aom [enc]` 即表示**未启用 SVT-AV1**，这是"SVT-AV1 选项不可用"的权威本机依据。
    /// </summary>
    public static string VersionText => _versionText;

    /// <summary>
    /// SVT-AV1 不可用的**具体原因**（可用时返回 null）。
    /// <para>为什么要有它：只写"不可用"用户无法判断是"本机没编进来"还是"驱动/调用失败"，
    /// 也就无从决定要不要换一个 avifenc 构建。实测探测与 UI 提示都用它给出可行动的信息。</para>
    /// </summary>
    public static string? SvtUnavailableReason => !IsAvailable
        ? "avifenc 不可用（未找到/无法执行 native/avifenc.exe）"
        : IsSvtAvailable
            ? null
            : $"当前内嵌 avifenc 未编译 SVT-AV1（--version 只有 aom：[{VersionText.Replace("\n", " ").Replace("\r", "").Trim()}]）";

    /// <summary>生成 `--codec` 参数；Auto 时不传，沿用 avifenc 默认。
    /// <para>P2-5 修复：改为按调用传参（避免全局静态竞态）。
    /// 原为全局可变字段 `Codec`，并发编码任务会互相覆盖。</para></summary>
    private static string GetCodecArg(string codec) => codec switch
    {
        // 选了 SVT 但当前 avifenc 未启用它 → 不传 --codec，回落到默认（即 libaom）。
        // 否则会因为不支持的 codec 直接编码失败。
        // ⚠ 这一支是"用户手选的编码器被静默换掉"，必须配套 CodecDowngradeNote 留痕（见下）。
        "SvtAv1" when !IsSvtAvailable => "",
        "LibAom" => "--codec aom",
        "SvtAv1" => "--codec svt",
        _ => ""
    };

    /// <summary>「最小体积」档要下发给 libaom 的高级选项（<c>-a key=value</c>）。
    /// <para>实测（4K，2026-10-07）：<c>tune=ssim</c> 是唯一真正减小体积的小参 ——
    /// <b>110.2 → 93.7 KB（−15%）</b>且不额外变慢；其余（aq-mode / sharpness /
    /// enable-chroma-deltaq）在本内容上零效果，全开还互相干扰（102.5 KB，比只开 ssim 更差）。</para>
    /// <para>⚠ <b>只在确实会走 libaom 时下发</b>：
    /// <list type="bullet">
    /// <item><c>codec == "SvtAv1"</c> 且本机有 SVT ⇒ 走 SVT，aom 的 key 无效（avifenc 会告警）；</item>
    /// <item><c>codec == "SvtAv1"</c> 但本机无 SVT ⇒ <c>GetCodecArg</c> 返回空串、实际回落 libaom
    ///   ⇒ 这种"静默降级到 aom"的情形<b>应当</b>下发（否则用户选了最小体积却没拿到收益）；</item>
    /// <item><c>Auto</c> ⇒ avifenc 默认即 aom ⇒ 下发。</item>
    /// </list></para></summary>
    private static string[] BuildAomAdvancedArgs(string codec, CompressionEffort effort)
    {
        // 只有"确实会由 aom 编码"时才加。SVT 可用时用户手选的 SVT 走 svt，不认 aom 的 key。
        bool willUseAom = !(codec == "SvtAv1" && IsSvtAvailable);
        if (!willUseAom) return [];

        var keys = CompressionEffortMap.AvifencAdvancedArgs(effort);
        if (keys.Count == 0) return [];

        // CompressionEffortMap 给的是 ("-a","tune=ssim") 这种扁平对，此处逐项透传即可。
        return [.. keys];
    }

    /// <summary>
    /// 手选 SVT-AV1 但本机 avifenc 没编它时的降级说明；其余情形返回 null。
    /// <para>为什么要有这一条（2026-09-20，交接 §31.3-6）：<c>GetCodecArg</c> 在那种情况下
    /// 返回<b>空串</b>，avifenc 于是按自己的默认（AOM）编码 —— 文件名、画质、速度都"看着正常"，
    /// 全仓却没有任何对外痕迹。这与 <c>NvencAvifBackend.ChromaDowngradeNote</c>
    /// （"请求 444 实际恒 420 且不报错"）是同一类问题：<b>只记录、不改行为</b>。</para>
    /// </summary>
    internal static string? CodecDowngradeNote(string codec) =>
        codec == "SvtAv1" && !IsSvtAvailable
            ? "本机 avifenc 未编译 SVT-AV1 ⇒ 手选的 \"Svt 已被省略，本张按 avifenc 默认的 AOM 编码"
            : null;

    /// <summary>取 <c>--codec</c> 参数，并顺手把"手选编码器被省略"记进对外痕迹。</summary>
    private static string CodecArgNoting(string codec)
    {
        if (CodecDowngradeNote(codec) is { } note) EncoderFallbackNotifier.LastReason = note;
        return GetCodecArg(codec);
    }

    // ═══ speed：实测后由 4 改为 6 ═══
    // 依据 scripts/precision/avif_bench.py 实测（1080p, q=60）：
    //   speed 4 → 1.53s / 322.5 KB
    //   speed 6 → 0.50s / 310.9 KB   ← 又快又小（非单调，调低 speed 并无收益）
    // 4K 下 speed 6 为 0.95s。故 6 是甜点档。
    //
    // ═══ 2026-10-07：改由「压缩强度」三档决定（均衡档仍是 6，故默认行为不变）═══
    // 4K 真实截图复测（4.66 MB 样张，-q 60 --codec aom）：
    //   s0 8.46s/127KB   s2 5.71s/128.5KB  s4 2.79s/134.4KB
    //   s6 0.94s/144.3KB s8 0.61s/157.3KB  s10 0.59s/180.1KB
    // ⚠ 上面 1080p 那句"非单调"在 4K 上**不成立**：4K 下 speed 越小体积越小、时间越长，是单调的。
    //   旧结论只对 1080p 成立，故保留原文（历史依据）并在此补 4K 读数，不覆盖。
    private const string SpeedArgLegacyBalanced = "-s 6";

    /// <summary>检测 avifenc 是否可用（先在提取目录查找，再回退到 PATH）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeLibraryResolver.Initialize();
                var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) { _available = false; return false; }
                // stdout 要留着判断编译进了哪些编码器（见 IsSvtAvailable）；排空与超时见 NativeProcess.DrainProbe
                bool probed = NativeProcess.DrainProbe(proc, NativeProcess.ProbeTimeoutMs,
                    out int ec, out string versionText, out _);
                _versionText = versionText;
                _available = probed && ec == 0;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>编码 BGRA 像素为 AVIF 文件。</summary>
    /// <param name="codec">avifenc 内部编码器（"LibAom"/"SvtAv1"/"Auto"）。默认 Auto 由 avifenc 自选。</param>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf = 30, string chroma = "444", int bitDepth = 10, byte[]? iccProfile = null,
        string codec = "Auto")
    {
        Encode(bgra, w, h, path, crf, isHdr: false, chroma, bitDepth, iccProfile, null, codec);
    }

    /// <summary>编码 BGRA 像素为 AVIF 文件，通过临时 PNG 文件。</summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf, string chroma = "444", int bitDepth = 10, byte[]? iccProfile = null,
        string? colorSpaceTag = null, string codec = "Auto")
    {
        Encode(bgra, w, h, path, crf, isHdr: false, chroma, bitDepth, iccProfile, colorSpaceTag, codec);
    }

    /// <summary>
    /// 编码 BGRA 像素为 AVIF 文件。
    /// SDR 路径：PNG → avifenc stdin 管道（避免临时文件 I/O）
    /// <para>
    /// 2026-09-17 修复：<c>bitDepth</c> 不再是死参数 —— 命令行按请求拼 <c>--depth</c>，
    /// avifenc 会自行把 8-bit PNG 输入升采样到 10/12-bit 再编码。
    /// （修复前无论传 8/10/12，输出恒为 8-bit，而设置默认与 UI 都写着 10-bit。）
    /// </para>
    /// </summary>
    public static void Encode(byte[] bgra, int w, int h, string path,
        int crf, bool isHdr, string chroma = "444", int bitDepth = 10, byte[]? iccProfile = null,
        string? colorSpaceTag = null, string codec = "Auto",
        CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用 请将 avifenc.exe 放入 native/ 目录");

        var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
        int q = CrfToQuality(crf);
        string cicpArgs = BuildCicpArgs(isHdr, colorSpaceTag);
        string chromaArg = chroma switch { "420" => "--yuv 420", "422" => "--yuv 422", _ => "--yuv 444" };

        // ═══ 2026-09-17：SDR 路径的位深参数传递 ═══
        // avifenc 的 `-d,--depth D` = 输出位深，D 只能取 8/10/12，且**仅对 JPEG/PNG 输入做转换**
        // （libavif apps/avifenc.c；y4m 要求 D 与输入一致、不支持转换）。
        // 本路径输入是 8-bit PNG，故传 --depth 10/12 即可让 avifenc 升采样输出高位深。
        // HDR 路径走 y4m（头已声明 C444p10），BuildCicpArgs 内已固定 --depth 10，此处不再叠加。
        string depthArg = isHdr ? "" : GetDepthArg(bitDepth);

        // ICC 参数（仍需临时文件，avifenc 不支持 stdin ICC）
        // 能不能嵌只问一个地方：FormatHelper.IsEmbeddableIcc（与 cjxl 路径同判据，见 §31.3-9）
        string iccArg = "";
        string? tmpIccPath = null;
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            tmpIccPath = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
            try { File.WriteAllBytes(tmpIccPath, iccProfile); iccArg = $"--icc \"{tmpIccPath}\""; }
            catch { }
        }

        FormatHelper.ValidateNativePath(path, nameof(path));

        // SDR 路径：PNG → avifenc stdin（--stdin --input-format png）
        // 消除临时 PNG 文件 I/O（4K 约 8-16MB 写+读 → 零磁盘 I/O）
        // ⚠ 「最小体积」档额外下发的 libaom 高级选项（tune=ssim）**只在该 codec 解析为 aom 时加**：
        //   SVT-AV1 / NVENC 不认 aom 的 key，avifenc 会对未使用的 key 报警告（其帮助文档明说）。
        string advArgs = JoinArgs(BuildAomAdvancedArgs(codec, effort));
        string commonArgs = JoinArgs(
            $"-q {q}", $"-s {CompressionEffortMap.AvifencSpeed(effort)}", CodecArgNoting(codec), chromaArg, cicpArgs, iccArg, advArgs, "--stdin --input-format png");

        try
        {
            RunPngViaStdin(exePath, JoinArgs(commonArgs, depthArg, $"\"{path}\""), bgra, w, h, path);
        }
        catch (Exception ex) when (depthArg.Length > 0)
        {
            // 本机 avifenc 不接受所请求位深（老版本可能没有 -d/--depth，或该 codec 不支持 12-bit）
            // → 回退 8-bit 重试，最坏情况等价于修复前的行为；重试仍失败则原样抛出，绝不吞掉。
            CoreLog.Warn("AVIF", $"{bitDepth}-bit 编码失败，回退 8-bit", ex, LogCategory.Encoding);
            // ═══ 2026-09-19 留痕（只记录，不改行为）═══
            // 容器位深被换掉而文件名与画质看起来都"正常"，是典型的静默改参数；
            // 重试若仍失败会照常抛出（用户仍会看到错误），成功时才有必要告知。
            EncoderFallbackNotifier.LastReason =
                $"avifenc 拒绝 {bitDepth}-bit 输出（{ex.Message}）⇒ 本次已按 8-bit 重新编码，"
                + "容器位深降为 8";
            RunPngViaStdin(exePath, JoinArgs(commonArgs, $"\"{path}\""), bgra, w, h, path);
        }
        finally
        {
            // 清理临时 ICC 文件（成功/超时/失败路径均清理）
            if (tmpIccPath is not null)
                try { File.Delete(tmpIccPath); } catch { }
        }
    }

    /// <summary>拼接命令行片段，自动跳过空片段（否则会出现连续空格）。</summary>
    private static string JoinArgs(params string?[] parts)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in parts)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(p);
        }
        return sb.ToString();
    }

    /// <summary>
    /// SDR 路径的 <c>--depth</c> 参数（avifenc 只接受 8 / 10 / 12）。
    /// <para>8-bit 不传参（沿用 avifenc 默认）；&gt;12 的请求（设置里可能出现 16）一律钳到 12。</para>
    /// <para>钳制口径交给 <see cref="FormatHelper.AvifContainerDepth"/> 作单一事实源：
    /// 2026-09-19 起 16-bit PNG 中转也要按<b>同一档</b>量化，两处各算一次会前后不一致。</para>
    /// </summary>
    public static string GetDepthArg(int bitDepth)
    {
        int depth = FormatHelper.AvifContainerDepth(bitDepth);
        return depth <= 8 ? "" : $"--depth {depth}";   // 8-bit：avifenc 默认，无需显式指定
    }

    /// <summary>把 BGRA 像素编码成 PNG 流写入 avifenc 的 stdin，并等待其完成。</summary>
    private static void RunPngViaStdin(string exePath, string arguments, byte[] bgra, int w, int h, string path)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("无法启动 avifenc");

        // 异步读取 stderr/stdout 防止管道缓冲区满导致死锁
        var stderrTask = proc.StandardError.ReadToEndAsync();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();

        // 管道流式写入 PNG 数据到 avifenc stdin
        try
        {
            var stdin = proc.StandardInput.BaseStream;
            // 注意: ICC 通过命令行 --icc 传递，PNG 流中不嵌 iCCP (避免重复)
            ManagedPngEncoder.EncodeFast(bgra, w, h, stdin, null);
            proc.StandardInput.Flush();
            proc.StandardInput.Close();
        }
        catch (Exception ex)
        {
            try { if (!proc.HasExited) proc.Kill(); } catch { }
            throw new InvalidOperationException($"[AVIF] 管道写入失败: {ex.Message}", ex);
        }

        if (!proc.WaitForExit(30_000))
        {
            proc.Kill();
            throw new TimeoutException("[AVIF] 编码超时 (30s)");
        }

        var stderr = stderrTask.Result;
        _ = stdoutTask.Result;

        if (proc.ExitCode != 0 || !File.Exists(path))
            throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={proc.ExitCode}) {stderr.Trim()}");
    }

    // WriteBgraToY4mFile 已移除 — 改用 PNG 中间文件，兼容性更佳、编码质量无损

    /// <summary>从 CRF (0-63) 转换为 avifenc 质量值 (0-100, 100=无损)。</summary>
    private static int CrfToQuality(int crf) =>
        (int)Math.Clamp(100 - (crf * 100.0 / 63.0), 0, 100);

    /// <summary>根据 isHdr 和 colorSpaceTag 构建 CICP 参数。
    /// <para>matrix 槽的取值统一问 <see cref="FormatHelper.AvifMatrixCoefficients"/>（含"为什么不用
    /// identity"的说明）；transfer 用 <see cref="FormatHelper.CicpTransferPq"/> /
    /// <see cref="FormatHelper.CicpTransferSrgb"/>，与 PNG cICP、NVENC colr(nclx) 同一套常量。</para></summary>
    private static string BuildCicpArgs(bool isHdr, string? colorSpaceTag)
    {
        if (isHdr)
        {
            byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(colorSpaceTag ?? "BT2020");
            return $"--cicp {primaries}/{FormatHelper.CicpTransferPq}/{FormatHelper.AvifMatrixCoefficients(primaries)}"
                 + $" {GetDepthArg(10)}";   // HDR 恒 10-bit（PQ，与 AvifEncoderCapabilities.HdrBitDepth 同档）
        }

        // ═══ 2026-09-19 缺陷 B（交接 §30.9 / 本批 B2）：SDR 侧"无码点 ⇒ 不下发码点"必须与 EncodeFile 同源 ═══
        // 原先这一支无条件取 GetCicpPrimaries(tag) 拼 --cicp，而它对 AdobeRGB 返回 **1 = BT.709 原色**。
        // H.273 Table 2 根本没有 AdobeRGB 的 primaries 码点 ⇒ "--cicp 1/13/1" 是在宣称"像素是 sRGB 原色"，
        // 而像素已被 PrepareSdrLinearFloat 的 SrgbToAdobeRgb 矩阵搬到 Adobe 原色 —— 采信码点的查看器
        // 会按 BT.709 解释 ⇒ 系统性偏色。FormatHelper.BuildSdrCicp 对同一事实返回 null
        // （PNG cICP 与 NVENC colr(nclx) 都据它跳过），三条路径从此共用这一个判据，不再各写各的。
        // 省略 --cicp 后由容器侧的 ICC 承担声明（Adobe 标准 profile 由 PrepareSdrLinearFloat 兜底注入，
        // 恒 >400B）；本机 avifenc 实测：输入带 iCCP/--icc 而不传 --cicp ⇒ 写 2/2/6（unspecified）+ prof，
        // 即"不猜码点"而不是"猜错码点"。HDR 分支不经此处（恒 BT.2020 家族，且 tag 已在源头归一）。
        if (FormatHelper.BuildSdrCicp(colorSpaceTag) is null) return "";

        // SDR: 根据 colorSpaceTag 选择原色；transfer = sRGB(13)（图片/截图标准，非 BT.709 视频 OETF）
        byte prim = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(colorSpaceTag ?? "sRGB");
        return $"--cicp {prim}/{FormatHelper.CicpTransferSrgb}/{FormatHelper.AvifMatrixCoefficients(prim)}";
    }

    /// <summary>直接从 PNG 文件编码为 AVIF，支持 CICP 参数。</summary>
    /// <param name="chroma">色度采样：420 / 422 / 444，未知值按 444 处理
    /// （与 SDR 路径使用同一套映射，保证两条路径对同一设置字符串行为一致）。</param>
    /// <param name="codec">avifenc 内部编码器（"LibAom" / "SvtAv1" / "Auto"）。</param>
    public static void EncodeFile(string pngPath, string avifPath, int crf, byte[]? cicp = null,
        string? colorSpaceTag = null, float peakNits = 0, string chroma = "444", string codec = "Auto",
        int bitDepth = 8, byte[]? iccProfile = null,
        CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用");

        var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
        int q = CrfToQuality(crf);

        // cicp 参数缺省值（sRGB）：下面两条分支会按需覆盖 —— 有 cicp 四元组 ⇒ 按数组拼（HDR/SDR 看 transfer 槽）；
        // 只有 colorSpaceTag ⇒ 走 BuildCicpArgs。三处 transfer/matrix 取值 2026-09-19 起统一取自
        // FormatHelper 的常量与 AvifMatrixCoefficients（此前这里硬编码 "--cicp 1/13/1"，是第三处写死矩阵的地方）。
        string cicpArgs = $"--cicp {FormatHelper.CicpPrimariesBt709}/{FormatHelper.CicpTransferSrgb}"
                        + $"/{FormatHelper.AvifMatrixCoefficients(FormatHelper.CicpPrimariesBt709)}";
        if (cicp is { Length: 4 })
        {
            // ═══ 上游的 CICP 四元组是 PNG cICP 形态（matrix 槽 = identity，因为 PNG 存 RGB 采样），
            //     AV1 是 YUV 码流 ⇒ matrix 槽必须换成真实 YUV 矩阵（按 primaries 取，见该函数）。
            //     取值口径单一来自 FormatHelper.AvifMatrixCoefficients。═══
            bool hdr = cicp[1] == FormatHelper.CicpTransferPq;
            byte m = (byte)(cicp[2] == FormatHelper.CicpMatrixIdentity
                ? FormatHelper.AvifMatrixCoefficients(cicp[0]) : cicp[2]);
            cicpArgs = $"--cicp {cicp[0]}/{cicp[1]}/{m}";
            // PQ 的位深**下限**是 10（8-bit PQ 分层严重），上限按调用方请求（libaom 可到 12）。
            // 2026-09-25 前这里无条件写死 GetDepthArg(10)，与上游 NegotiateBitDepth 的协商结果无关
            // ⇒ 会出现"管线算出 12-bit、命令行仍给 10-bit"的静默改参数。
            // 形参 bitDepth 默认 8 ⇒ Math.Max 兜到 10，不传位深的老调用行为不变（刻意保留的兼容点）。
            if (hdr)
                cicpArgs += $" {GetDepthArg(Math.Max(bitDepth, AvifEncoderCapabilities.HdrBitDepth))}";
        }
        else if (!string.IsNullOrEmpty(colorSpaceTag))
        {
            // ═══ 2026-10-08 修复(P1③): AdobeRGB 不拼 --cicp（与 GetColorMetadata 修复同源）═══
            // AdobeRGB 在 ITU-T H.273 Table 2 无标准 CICP primaries code；
            // GetCicpPrimaries("AdobeRGB") 返回 1 (BT.709) → cicp=null 兜底仍写出
            // --cicp 1/13/1，误标 sRGB 且优先于 ICC → 色彩丢失。与 PNG GetColorMetadata
            // 的既有修复一致：AdobeRGB 仅靠 ICC 声明，跳过 --cicp 参数。
            if (colorSpaceTag == "AdobeRGB")
                cicpArgs = "";
            else
                // 没有 CICP 字节数组但有 colorSpaceTag 时动态构建。
                // ⚠ isHdr 这里恒 false：走到本分支说明四元组缺失（上面 Length:4 的判定不成立），
                //   HDR 无从判定 ⇒ 按标签拼 SDR 声明。旧写法读 cicp?[1]（非 null 但长度≠4 时会
                //   直接 IndexOutOfRange），现在由 Length 判定保证只走安全路径。
                //   要输出 HDR 的调用方必须同时给出 cicp 四元组（见 FormatEncoders 的 AVIF HDR 分支）。
                cicpArgs = BuildCicpArgs(isHdr: false, colorSpaceTag);
        }

        // ═══ 2026-08-10 修复: HDR 写 clli (内容亮度级别)；2026-09-20 取值收进 FormatHelper ═══
        // avifenc --clli MaxCLL,MaxPALL。恒 10000 让 tone-map 查看器把 SDR 桌面
        // 内容显示极暗 → 传内容实际峰值。
        // 规则本身（clamp 203..10000、FALL = 峰值一半）现在只有一份：
        // 硬件路（IvfWriter 写容器 clli 盒）问的是同一个函数 ⇒ 同一张图两条路声明同一亮度范围。
        string clliArg = FormatHelper.AvifContentLightLevel(peakNits) is { } cll
            ? $" --clli {cll.MaxCLL},{cll.MaxFALL}" : "";

        // ═══ 2026-09-17：HDR 路径的色度参数 ═══
        // 此前 EncodeFile 完全不传 --yuv ⇒ HDR 输出的实际色度由 avifenc 默认策略决定，
        // 与设置里的 ChromaSubsampling 无关（典型的"改了不生效"）。
        // 这里与 SDR 路径使用**逐字相同**的映射，未知值同样落到 444。
        string chromaArg = chroma switch { "420" => "--yuv 420", "422" => "--yuv 422", _ => "--yuv 444" };

        // ═══ 2026-10-08 修复(P1①): SDR ≥10-bit 分支补传 --depth ═══
        // 此前 16-bit PNG 中转后未传位深 → avifenc 默认按 8-bit 处理高位深输入。
        // 复用 GetDepthArg：8-bit 不传（默认），10/12 钳到 avifenc 上限 12。
        string depthArg = bitDepth > 8 ? " " + GetDepthArg(bitDepth).Trim() : "";

        // 「最小体积」档的 libaom 高级选项（tune=ssim）。前导空格按本文件既有约定手工加
        // （iccArg/clliArg/depthArg 都是这个形状），空串时不留多余空格。
        var advArgs = BuildAomAdvancedArgs(codec, effort);
        string advArg = advArgs.Length > 0 ? " " + string.Join(' ', advArgs) : "";

        // ═══ 2026-09-20：EncodeFile 也要能把 ICC 交给 avifenc ═══
        // 实测（publish/…/native/avifenc.exe 1.4.2，同一张带 iCCP 的 P3 PNG）：
        //   不给任何色彩参数 → 输出只有 nclx(12,13,6)、**PNG 里的 iCCP 被完全丢弃**；
        //   只给 --cicp      → nclx(12,13,1)，无 ICC；
        //   只给 --icc       → colr(prof) + nclx(2,2,6)；
        //   --cicp + --icc   → colr(prof) + nclx(12,13,1)  ← 与硬件路的容器完全一致
        // ⇒ avifenc【不会】自己从中转 PNG 里继承 ICC。此前本方法根本没有 ICC 形参，
        //   所以"≥10-bit 走 16-bit PNG 中转"那条 SDR 路的广色域产物只有 nclx 声明，
        //   而硬件路同一设置写的是 prof + nclx —— 同一张图两条路声明不同，采信 ICC 的
        //   查看器（Windows 照片等）对软件产物就只能按码点猜。
        string iccArg = "";
        string? tmpIccPath = null;
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            tmpIccPath = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
            try
            {
                File.WriteAllBytes(tmpIccPath, iccProfile);
                iccArg = $" --icc \"{tmpIccPath}\"";
            }
            catch
            {
                tmpIccPath = null;   // 写不出去就退回"只有 nclx"，不能因此让整次编码失败
            }
        }

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            Arguments = $"-q {q} -s {CompressionEffortMap.AvifencSpeed(effort)} {CodecArgNoting(codec)} {chromaArg} {cicpArgs}{iccArg}{clliArg}{depthArg}{advArg} \"{pngPath}\" \"{avifPath}\"".Trim(),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        try
        {
        using var proc = System.Diagnostics.Process.Start(psi);
        if (proc is null) throw new InvalidOperationException("无法启动 avifenc");
        // 排空两条管道与等超时同时发生（缓冲区满不死锁，进程不退出也拿得到超时）
        var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 60_000, "avifenc");
        if (exitCode != 0 || !File.Exists(avifPath))
            throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={exitCode}) {stderr.Trim()}");
        }
        finally
        {
            if (tmpIccPath is not null) try { File.Delete(tmpIccPath); } catch { }
        }
    }

    /// <summary>从 scRGB linear float[] 编码 HDR AVIF 文件（PQ + BT.2020 + 10-bit）。</summary>
    /// <remarks>
    /// <b>死代码，没有调用方</b>（评估记录见 docs/reviews/encoder/avif-encoder-benchmark-2026-09-16.md 的"Y4M 直传"一节）。
    /// ⚠ 若将来要启用：下面那句 <c>--cicp 9/16/9</c> 是<b>写死</b>的，而现行 HDR 路一律问
    /// <see cref="FormatHelper.BuildCicpArgs"/> / <see cref="FormatHelper.AvifMatrixCoefficients"/>
    /// 取值 —— 直接启用本方法会重新造出第二个事实源（交接 §31.3-10 同型问题）。
    /// </remarks>
    public static void EncodeHdrFromScrgb(float[] scRgbLinear, int w, int h, string path, int crf, string codec = "Auto",
        CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new DllNotFoundException("[AVIF] avifenc 不可用");

        // scRGB linear → PQ 10-bit → 临时 Y4M 文件 → avifenc
        var tmpY4m = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.y4m");
        try
        {
            // 写入 Y4M 文件（10-bit 大端序）
            using (var fs = new FileStream(tmpY4m, FileMode.Create, FileAccess.Write))
            {
                // 写入 Y4M 头（使用二进制写入避免 StreamWriter 缓冲问题）
                byte[] header = System.Text.Encoding.ASCII.GetBytes($"YUV4MPEG2 W{w} H{h} F1:1 Ip A0:0 C444p10\nFRAME\n");
                fs.Write(header, 0, header.Length);
                int pixelCount = w * h;
                for (int i = 0; i < pixelCount; i++)
                {
                    int si = i * 4;
                    float rLin = scRgbLinear[si];
                    float gLin = scRgbLinear[si + 1];
                    float bLin = scRgbLinear[si + 2];
                    int y10 = FloatToPq10(rLin, gLin, bLin, out int cb10, out int cr10);
                    WriteU16BE(fs, y10);
                    WriteU16BE(fs, cb10);
                    WriteU16BE(fs, cr10);
                }
            }

            var exePath = NativeLibraryResolver.GetExePath("avifenc.exe");
            int q = CrfToQuality(crf);
            // HDR 路的 libaom 高级选项（同 EncodeFile：只在确实走 aom 时下发）
            var advArgsY4m = BuildAomAdvancedArgs(codec, effort);
            string advArgY4m = advArgsY4m.Length > 0 ? " " + string.Join(' ', advArgsY4m) : "";
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                // ═══ 2026-08-10 修复: matrix 9 (BT.2020 NCL) 替代 identity (兼容性) ═══
                Arguments = $"-q {q} -s {CompressionEffortMap.AvifencSpeed(effort)} {CodecArgNoting(codec)} --depth 10 --cicp 9/16/9{advArgY4m} \"{tmpY4m}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 avifenc");
            var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 60_000, "avifenc(y4m)");
            if (exitCode != 0 || !File.Exists(path))
                throw new InvalidOperationException($"[AVIF] avifenc 失败 (exit={exitCode}) {stderr.Trim()}");
        }
        finally
        {
            try { File.Delete(tmpY4m); } catch { }
        }
    }

    private static void WriteU16BE(Stream s, int val)
    {
        s.WriteByte((byte)(val >> 8));
        s.WriteByte((byte)val);
    }

    /// <summary>scRGB linear RGB → BT.2020 PQ 10-bit YCbCr (BT.2020 矩阵)。</summary>
    private static int FloatToPq10(float r, float g, float b, out int cb, out int cr)
    {
        // scRGB linear (BT.709 原色 + 线性 gamma) → BT.2020 RGB 色域转换
        // 矩阵: ITU-R BT.709 linear → ITU-R BT.2020 linear (D65→D65)
        float r2020 = 0.627404f * r + 0.329283f * g + 0.043313f * b;
        float g2020 = 0.069097f * r + 0.919541f * g + 0.011362f * b;
        float b2020 = 0.016391f * r + 0.088013f * g + 0.895596f * b;

        // PQ 编码 (ST.2084)
        float pqR = LinearToPq(r2020);
        float pqG = LinearToPq(g2020);
        float pqB = LinearToPq(b2020);

        // BT.2020 非恒定亮度 YCbCr (10-bit 范围)
        float yVal  = 0.2627f * pqR + 0.6780f * pqG + 0.0593f * pqB;
        float cbVal = (-0.2627f * pqR - 0.6780f * pqG + 0.9407f * pqB) / 1.8814f;
        float crVal = (0.9373f * pqR - 0.6780f * pqG - 0.0593f * pqB) / 1.4746f;

        // 量化到 10-bit (0-1023)
        int yInt  = (int)Math.Round(Math.Clamp(yVal, 0f, 1f) * 1023f);
        cb = (int)Math.Round(Math.Clamp(cbVal * 0.5f + 0.5f, 0f, 1f) * 1023f);
        cr = (int)Math.Round(Math.Clamp(crVal * 0.5f + 0.5f, 0f, 1f) * 1023f);
        return yInt;
    }

    /// <summary>scRGB linear → PQ (ST.2084) 编码。</summary>
    private static float LinearToPq(float linear)
    {
        float nits = Math.Max(linear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, 2610f / 16384f);
        float num = 3424f / 4096f + (2413f / 128f) * Lp;
        float den = 1f + (2392f / 128f) * Lp;
        return MathF.Pow(num / den, 2523f / 32f);
    }
}
