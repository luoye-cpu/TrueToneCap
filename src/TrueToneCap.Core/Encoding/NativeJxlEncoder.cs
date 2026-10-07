// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/NativeJxlEncoder.cs
// JPEG XL 原生编码器 — 通过 cjxl.exe 命令行工具编码
// 内嵌在 Resources/cjxl.exe，启动时自动提取到 native/ 目录
// 与 avifenc/cwebp/cjpegli 相同的子进程模式，崩溃隔离 + 自动回退

using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>JPEG XL 原生编码器 — 通过 cjxl.exe 命令行工具编码。</summary>
public static class NativeJxlEncoder
{
    private static bool? _available;

    /// <summary>检测 cjxl 是否可用（先在 native 目录查找，再回退到 PATH）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                NativeLibraryResolver.Initialize();
                var exePath = GetExePath();
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
                _available = NativeProcess.DrainProbe(proc, NativeProcess.ProbeTimeoutMs,
                    out int ec, out _, out _) && ec == 0;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    private static string GetExePath()
    {
        try { return NativeLibraryResolver.GetExePath("cjxl.exe"); }
        catch
        {
            // 回退到 PATH
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cjxl.exe", "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null
                    && NativeProcess.DrainProbe(proc, NativeProcess.ProbeTimeoutMs, out int ec, out _, out _)
                    && ec == 0) return "cjxl.exe";
            }
            catch { }
            throw new DllNotFoundException("[JXL] cjxl.exe 未找到。请将 cjxl.exe 放入 native/ 目录或系统 PATH。");
        }
    }

    /// <summary>
    /// 把设置里的色度映射为 cjxl 的 <c>--resampling</c> 参数。
    /// <para>纯函数：无任何 IO／进程／探测，输入 <paramref name="chroma"/> 决定唯一输出，
    /// 与 <c>NativeAvifEncoder.BuildCicpArgs</c> 同型，便于单测直接断言映射表。</para>
    /// <para><b>为什么只有 1 与 2 两档</b>：cjxl v0.11.2 的
    /// <c>--resampling=-1|1|2|4|8</c> 里 1 = 1x1（不下采样，即 4:4:4）、2 = 2x2（即 4:2:0），
    /// <b>4:2:2 不存在可选路径</b>。原因不是"命令行没暴露"那么轻：JPEG XL 码流<b>有</b>逐轴的
    /// <c>YCbCrChromaSubsampling</c>（含 <c>Is422()</c> 判据），但编码器只在<b>转码 JPEG</b> 时才写它
    /// （<c>enc_frame.cc</c>："Chroma subsampling is not supported in VarDCT mode when not
    /// recompressing JPEGs"）—— 我们的产品路径喂的是 PPM / 16-bit PNG，永远走不到那条分支。
    /// 所以 "422" 与一切未知值返回空串让编码器自选（-1），而不是挑一个"看着最近"的 ——
    /// 4 = 4x4 是比 4:2:0 更狠的降采样，用它冒充 4:2:2 属于新的静默改参数。</para>
    /// <para>⚠ 这一档也不是"色度抽样"：<c>--resampling</c> 是<b>整幅图（含三通道）</b>的盒式降采样、
    /// 解码时再上采样回原尺寸（<c>encode.h</c> 原话），而界面那一行的标签写的是"色度采样" ——
    /// 两者的差别已写进 JXL 卡片的灰字说明，别只留在这段注释里。</para>
    /// <para>🔬 实测判据（2026-09-19，内嵌 cjxl v0.11.2，输入 256×256 cs_png_srgb.png，
    /// 参数与 SDR 路径一致 <c>-d 1.0000 -e 9</c>）：不传 879 B、<c>--resampling 1</c> 879 B 且
    /// 与前者<b>逐字节相同</b>、<c>--resampling 2</c> 492 B（空格式与 <c>=2</c> 式产物亦逐字节相同）。
    /// ⇒ 444→1 与编码器自选在常用画质下同解（cjxl 的 -1 = "仅极低画质才降采样"），
    /// 420→2 真实改变产物 —— 修复前该开关改了产物字节完全不变，这一段即为反证。</para>
    /// </summary>
    /// <summary>
    /// JPEG XL 真正能表达的色度档位 —— <b>唯一事实源</b>：界面下拉的 Tag、设置读盘的白名单、
    /// 门禁判据都读这里，不许再各抄一份。
    /// <para>为什么必须是单源：设置层原来用的是跨格式共享的 <c>FixChroma</c>（放行 444/422/420），
    /// 而 JXL 下拉只有 444/420 ⇒ 盘上一旦出现 <c>"422"</c>（手改 JSON 或历史配置），
    /// 按 Tag 选中时找不到项 ⇒ <b>下拉显示空白</b>，且下次保存会被 <c>?? "444"</c> 静默改掉。
    /// 2026-09-26 用户报的"默认选项缺失"里，格式下拉那一条是同族症状（见 <c>RebuildFormatCombo</c>）。</para>
    /// </summary>
    public static readonly string[] ChromaOptions = { "444", "420" };

    /// <summary>色度值归一化：不在 <see cref="ChromaOptions"/> 里的一律回到默认档 4:4:4（不抛、不冒充 4:2:2）。</summary>
    public static string NormalizeChroma(string? chroma) =>
        ChromaOptions.Contains((chroma ?? "").Trim()) ? (chroma ?? "").Trim() : ChromaOptions[0];

    internal static string BuildResamplingArg(string? chroma) => chroma?.Trim() switch
    {
        "444" => "--resampling 1",
        "420" => "--resampling 2",
        _ => "",          // null / "" / "422" / 未知值：不传，保持编码器自选
    };

    /// <summary>可选命令行片段的前置空格（空片段不留多余空格，纯排版）。</summary>
    private static string WithSpace(string arg) => arg.Length > 0 ? " " + arg : "";

    /// <summary>编码 BGRA 像素为 JXL 文件 (SDR)。</summary>
    /// <param name="bitDepth">目标位深 (8/10/12/16)。cjxl **没有** -b 参数（见下方 2026-08-09 记录），
    /// 位深只由**输入容器**决定：≤8 走 PPM（真 8-bit），>8 走 16-bit PNG 中转（容器恒 16-bit，
    /// 10 与 12 无法区分）。
    /// 注意: 输入为 8-bit byte[]，像素数据本身只有 8-bit 精度，位深仅影响容器声明。
    /// 若需真正 >8-bit 精度，请使用 EncodeHdr（接收 16-bit 数据）。</param>
    /// <param name="chroma">设置里的色度（"444" / "420"）→ cjxl 的 <c>--resampling</c>；
    /// 其它值（含 null、"422"）不传参数。映射表与实测判据见 <see cref="BuildResamplingArg"/>。</param>
    /// <summary>
    /// 目标色域标签 → cjxl <c>-x color_space=</c> 的 ColorEncoding IDE 名。
    /// <para><b>2026-09-22 实测的三条前提（cjxl v0.11.2，本机）</b>：</para>
    /// <list type="number">
    /// <item><c>-x</c> 是 **解码提示**（help 原文：'dec-hints'，用于"PPM 这类存不下色彩信息的输入，
    /// 或要改写已有元数据的输入"）。所以输入若自带色彩元数据，这个提示会被**完全忽略**：
    /// 同一张带 iCCP 的 PNG，喂 sRGB / DisplayP3 / Rec2100PQ / 完整 IDE 名 / 故意编的假值，
    /// 产出的 8 枚 .jxl <b>字节完全相同</b>（md5 一致）。
    /// 本仓库两条产品路径喂的都是无元数据的中转文件（8-bit 走 PPM，≥10-bit 走不带 ICC 的 16-bit PNG），
    /// ⇒ 提示在这里是生效的：换成 PPM 输入后同样的 5 个取值产出 5 个不同文件。</item>
    /// <item><b>未识别的值被静默忽略</b>：假值照样退出码 0、照样出文件。⇒ "没报错"不是"生效了"的证据，
    /// 这条判据写在 <c>scripts/precision/jxl_colr_discrimination.py</c> 的对照组里，不许靠退出码。</item>
    /// <item>help 只列了 4 个 shorthand（sRGB / DisplayP3 / Rec2100PQ / Rec2100HLG），
    /// 完整 IDE 名 <c>RGB_&lt;白点&gt;_&lt;原色&gt;_&lt;渲染意图&gt;_&lt;传递函数&gt;</c> 同样被接受 ——
    /// 这里用 <c>RGB_D65_202_Rel_SRG</c> 表达"BT.2020 原色 + sRGB 传递"，正是广色域 SDR 那一档。</item>
    /// </list>
    /// <para>⚠ 有可嵌 ICC 时调用方走的是 ICC 那条分支，不看这里：libjxl 里 ICC 会覆盖 color fields
    /// （HDR 路径注释已记：嵌了带 sRGB TRC 的 ICC 会把 PQ 传输吃掉）。</para>
    /// </summary>
    internal static string JxlColorSpaceValue(string? tag) => tag switch
    {
        // null/空/System 到这里都该已被 ColorDecision 解析过；兜 sRGB 而不是兜"不传参"，
        // 因为不传参 = 由 cjxl 按输入猜，而我们的中转文件（PPM）根本没有可猜的元数据。
        null or "" or "System" or "sRGB" => "sRGB",
        "BT2020" => "RGB_D65_202_Rel_SRG",
        // 2026-09-27（用户口径）：AdobeRGB 一律收口进 Rec.2020，不再兜到 sRGB。
        // 与 CollapseSelectableTag（界面只有 Rec.709 / Rec.2020 两档）和 HDR 侧的
        // HdrDeclarableTag 同向 —— 以前只有这条 SDR 路往反方向收，是同一件事的第三份口径。
        // 写在这里是防"上游没收口就直接调编码"的旁路：宁可声明成 2020，也不许悄悄写成 sRGB
        // （那等于把广色域像素报成窄色域，比不声明更糟）。
        "AdobeRGB" => "RGB_D65_202_Rel_SRG",
        "DisplayP3" or "DCI_P3" => "DisplayP3",
        _ => "sRGB",
    };

    public static void Encode(byte[] bgra, int w, int h, string path,
        float distance = 1.0f, int bitDepth = 10, byte[]? iccProfile = null, string? chroma = null,
        string? colorSpaceTag = null, CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        // cjxl v0.11.2 读取某些 PNG 文件失败，原本改用 PPM 中间格式（与 JpegLiNative 相同策略）。
        // ═══ 2026-09-16 实现 10bit（用户要求：JXL 与 AVIF 都默认 10bit）═══
        // PPM 只有 8-bit 容器，bitDepth>8 会被静默降为 8-bit。现在 bitDepth>8 时改用
        // 16-bit PNG 中转（8-bit 像素升采样到 16-bit，无损映射），cjxl 按输入声明位深编码，
        // 输出为 16-bit 容器（满足 ≥10bit 的要求）。8-bit 请求仍走 PPM（更快）。
        var use16Bit = bitDepth > 8;
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jxl_{Guid.NewGuid():N}.{(use16Bit ? "png" : "ppm")}");
        string? tmpIcc = null;

        try
        {
            if (use16Bit)
            {
                // ═══ 2026-09-17 修复：旧的内联实现把 R 写到了 offset 0、B 写到 offset 4 ═══
                // 而 ManagedPngEncoder.Encode16 约定的是 **B16 G16 R16 A16 大端**
                // ⇒ 10/12-bit JXL 输出的 R/B 通道被互换。JXL 默认位深正是 10
                // （AppSettingsData.BitDepthJpegXl = 10），即默认路径一直在产出红蓝颠倒的文件。
                // 现改用 FormatHelper.Bgra8ToBgra16BE（与 HDR 路径的 Rgba16ToBgra16Bytes 同源）。
                var bgra16 = FormatHelper.Bgra8ToBgra16BE(bgra);
                // fastest: true —— 这是用完即删的中转文件，SmallestSize 在 4K 上要多花数秒
                ManagedPngEncoder.Encode16(bgra16, w, h, tmpPpm, fastest: true);
            }
            else
            {
                // 写入临时 PPM 文件
                WriteBgraToPpm(bgra, w, h, tmpPpm);
            }

            var exePath = GetExePath();

            // ═══ 2026-08-09 修复: clamp butteraugli distance 到 cjxl 合法范围 [0,25] ═══
            // 调用方可能传 quality 值 (0-100 百分比), 但 cjxl 的 -d 只接受 [0,25],
            // 传 90 → EncodeImageJXL() failed. 与 JpegLiNative 的 clamp 一致。
            float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);

            // 色彩空间参数
            // 能不能嵌只问一个地方：FormatHelper.IsEmbeddableIcc（cjxl / avifenc 同判据，见 §31.3-9）
            // 有 ICC ⇒ 只给 ICC（ICC 会覆盖 color fields，同时给两个是自相矛盾）
            // 没 ICC ⇒ 按【目标标签】选 IDE 名，不再一律回落 sRGB（回落 sRGB 曾把 BT.2020 的产物
            //           主动声明成 sRGB，是"写错"而不是"没写"）
            string colorArg;
            if (FormatHelper.IsEmbeddableIcc(iccProfile))
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                colorArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }
            else
            {
                colorArg = $"-x color_space={JxlColorSpaceValue(colorSpaceTag)}";
            }

            // ═══ 2026-08-09 修复: 删除无效的 -b 参数 ═══
            // cjxl v0.11.2 无 -b 参数 (Unknown argument: -b → exit 1), 位深由输入格式决定。
            // ⚠ 之前传 -b {bitDepth>8} → 无感截图 JXL 失败 (Unknown argument: -b)
            //
            // 2026-09-17 注释勘误：下面这段"此处忽略 bitDepth (8-bit)"是**过期**的 ——
            // 现在 bitDepth>8 时上方已改用 16-bit PNG 中转（use16Bit），cjxl 按输入容器编码，
            // 输出为 16-bit 容器。即：8-bit 请求走 PPM 输出 8-bit；10/12/16-bit 请求走 16-bit PNG
            // 中转，输出 16-bit 容器（cjxl 无法区分 10 与 12，二者产物相同）。
            string bitsArg = "";

            // ═══ 2026-10-07：effort 由「压缩强度」三档决定（原为硬编码 `-e 9`）═══
            // 实测（4K 真实截图）：e9 要 25.0 s 且产物 541 KB，而 e5 只要 0.82 s / 491 KB
            // —— 即旧的硬编码值既慢得多、体积**还更大**。三档取 7/5/3，理由见 CompressionEffort.cs 文件头。
            var effortArg = CompressionEffortMap.JxlEffort(effort);

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {clampedDist:F4} -e {effortArg}{bitsArg} {colorArg}{WithSpace(BuildResamplingArg(chroma))} \"{tmpPpm}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            // 2026-08-10 诊断代码（无条件写 %TEMP%\ttc_encoder_cmd.log）已移除：
            // 该日志在 Release 下也会持续写入并最终无限增长，且命令行只在排查时才有意义。
            CoreLog.Debug("JXL", $"{psi.FileName} {psi.Arguments}", LogCategory.Encoding);

            var result = NativeEncoderGuard.TryEncode("JXL", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
                var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 60_000, "cjxl");

                if (exitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[JXL] cjxl 编码失败 (exit={exitCode}) {stderr.Trim()}");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[JXL] cjxl 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>
    /// 16-bit BGRA（大端，同 ManagedPngEncoder.Encode16 输入布局：每像素 8 字节 B16 G16 R16 A16）
    /// → 16-bit PNG 中转 → cjxl。用于 scRGB→真 10-bit SDR 通路：
    /// Float16 源量化到 10-bit 后装入 16-bit 容器（值已左对齐，如 10-bit 数据 &lt;&lt; 6）。
    /// </summary>
    /// <param name="bitDepth">声明的实际位深（10/12/16）。PNG IHDR 恒 16-bit 容器，
    /// 实际位深由 ManagedPngEncoder.Encode16 写入 sBIT chunk。</param>
    /// <param name="chroma">设置里的色度 → cjxl 的 <c>--resampling</c>，映射同
    /// <see cref="BuildResamplingArg"/>。此前该路径（JXL 的默认位深档）完全不读色度设置，
    /// 界面上的 4:4:4 / 4:2:0 下拉是纯装饰。</param>
    /// <remarks>
    /// cjxl 无 16-bit 命令行像素输入，故必须经 16-bit PNG 中转。
    /// 结构照抄 <see cref="Encode"/> 的 use16Bit 分支（2026-09-16 引入的 16-bit PNG 中转路径），
    /// 差异仅两点：① 像素已是 16-bit BGRA，无需 Bgra8ToBgra16BE 升采样；
    /// ② 透传 bitDepth 给 Encode16 以写入正确的 sBIT。
    /// distance clamp / ICC 参数 / 色度参数 / 进程调用均复用 Encode 的既有逻辑（本文件内最小复制，来源已标注）。
    /// </remarks>
    public static void Encode16Bgra16(byte[] bgra16BigEndian, int w, int h, string path,
        float distance = 1.0f, int bitDepth = 10, byte[]? iccProfile = null, string? chroma = null,
        string? colorSpaceTag = null, CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");
        if (bitDepth is not (10 or 12 or 16))
            throw new ArgumentOutOfRangeException(nameof(bitDepth), $"[JXL] Encode16Bgra16 仅支持 10/12/16-bit，收到 {bitDepth}。");

        // 16-bit PNG 中转文件（同 Encode 的 use16Bit 路径）
        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_jxl16_{Guid.NewGuid():N}.png");
        string? tmpIcc = null;

        try
        {
            // 输入即 Encode16 约定的 B16 G16 R16 A16 大端布局，直接落盘，不做任何转换。
            // bitDepth 传给 Encode16 → sBIT 写实际位深（10→10, 12→12），IHDR 恒 16。
            // fastest: true —— 用完即删的中转文件（同 Encode 既有注释）。
            ManagedPngEncoder.Encode16(bgra16BigEndian, w, h, tmpPng, bitDepth: bitDepth, fastest: true);

            var exePath = GetExePath();

            // ═══ 照抄 Encode：clamp butteraugli distance 到 cjxl 合法范围 [0,25] ═══
            float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);

            // ═══ 与 Encode 同一份规则（helper 只有一份）═══
            string colorArg;
            if (FormatHelper.IsEmbeddableIcc(iccProfile))
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                colorArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }
            else
            {
                colorArg = $"-x color_space={JxlColorSpaceValue(colorSpaceTag)}";
            }

            // 无 -b 参数（cjxl v0.11.2 不支持，位深由输入格式决定 —— 见 Encode 同段注释）
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {clampedDist:F4} -e {CompressionEffortMap.JxlEffort(effort)} {colorArg}{WithSpace(BuildResamplingArg(chroma))} \"{tmpPng}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            CoreLog.Debug("JXL16", $"{psi.FileName} {psi.Arguments}", LogCategory.Encoding);

            // ═══ 照抄 Encode 的进程调用 + NativeEncoderGuard 隔离 ═══
            var result = NativeEncoderGuard.TryEncode("JXL16", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
                var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 60_000, "cjxl");

                if (exitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[JXL] cjxl 编码失败 (exit={exitCode}) {stderr.Trim()}");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[JXL] cjxl 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>编码 16-bit HDR RGBA 像素为 JXL 文件 (PQ, Rec2100)。</summary>
    /// <remarks>
    /// ⚠ 关键: 输入 <paramref name="rgba16"/> 必须是 **PQ 编码** 后的值（由 FormatHelper.HdrToPq16 生成）。
    /// cjxl 的 `-x color_space=Rec2100PQ` 将输入视为 display-referred PQ 值，
    /// 直接存储，不会再做 PQ 转换。因此必须用 16-bit PNG 中间文件（整数，无歧义）。
    /// 不要改用 PFM 浮点容器：PFM 会被 cjxl 当作线性光重新做 PQ 编码，导致双重编码、高光丢失。
    /// </remarks>
    /// <param name="colorSpaceTag">HDR 色域标签（此前硬编码 Rec2100PQ）。
    /// cjxl 的 <c>-x color_space=</c> **简写名**确实没有"P3 原色 + PQ"这一枚（无 Rec2100P3_PQ
    /// 之类可解析值），所以 BT2020 家族走简写名；但"P3+PQ 表达不了"只对简写名成立 ——
    /// 容器本身表达得了：递一份【PQ 曲线的】ICC，libjxl 会归约成 color fields 的
    /// Custom 原色 + Transfer=PQ（2026-09-27 实测：随包 cjxl v0.11.2，产物 209 B，
    /// jxlinfo 读回 <c>Primaries=[Custom red(0.682054,0.319340)…]</c> + <c>Transfer=[PQ]</c>）。
    /// ⚠ 反过来说，递【SDR 曲线】的 ICC 是有害的：ICC 压过 color fields，PQ 那一维就此丢失
    ///   （见 <see cref="TrueToneCap.Core.Encoding.JpegXlEncoder"/> HDR 分支同段说明）。</param>
    /// <param name="chroma">设置里的色度 → cjxl 的 <c>--resampling</c>，映射同
    /// <see cref="BuildResamplingArg"/>（默认 444 产出与不传逐字节相同，故既有 HDR 产物不变）。</param>
    public static void EncodeHdr(ushort[] rgba16, int w, int h, string path,
        float distance = 1.0f, byte[]? iccProfile = null, float intensityTarget = 10000f,
        string colorSpaceTag = "BT2020", string? chroma = null,
        CompressionEffort effort = CompressionEffort.Balanced)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("[JXL] cjxl.exe 不可用，请将 cjxl.exe 放入 native/ 目录或系统 PATH。");

        // HDR 需要 16-bit 精度，用 16-bit PNG 中间文件（RGBA16 → BGRA16 大端）。
        // 不使用 PFM：PFM 浮点容器会被 cjxl 当作线性光，导致 PQ 双重编码回归。
        var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_jxl_hdr_{Guid.NewGuid():N}.png");
        string? tmpIcc = null;

        try
        {
            var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(rgba16, w, h);
            ManagedPngEncoder.Encode16(bgra16, w, h, tmpPng);

            var exePath = GetExePath();

            // ═══ 2026-08-09: clamp distance 到 cjxl 合法范围 [0,25] (同 Encode) ═══
            float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);

            // ICC 参数
            string iccArg = "";
            if (FormatHelper.IsEmbeddableIcc(iccProfile))
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile);
                iccArg = $"-x icc_pathname=\"{tmpIcc}\"";
            }

            // ═══ ICC 与 color fields 在 JXL 里是【二选一】，两个都给时 color fields 会被整个丢弃 ═══
            // 实测（cjxl v0.11.2，同一输入）：`-x icc_pathname=sRGB.icc -x color_space=Rec2100PQ`
            // 与反过来写，产物都是 sRGB 声明 —— PQ 没了；cjxl 见到 icc_pathname 就置
            // kIccIsPrimary，只调 JxlEncoderSetICCProfile，另一个提示根本不进码流（与先后顺序无关）。
            // 所以下面这行以前是"死参数"：iccArg 在时它不产生任何效果，却让人以为 HDR 声明写的是 PQ。
            // 现在按编码器自己的规矩来：有可嵌 ICC 就只交 ICC，没有才交 color_space=Rec2100PQ。
            string colorArg = iccArg.Length > 0 ? iccArg : "-x color_space=Rec2100PQ";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"-d {clampedDist:F4} -e {CompressionEffortMap.JxlEffort(effort)} {colorArg} --intensity_target={intensityTarget:F0}{WithSpace(BuildResamplingArg(chroma))} \"{tmpPng}\" \"{path}\"".Trim(),
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            // 2026-08-10 诊断代码（无条件写 %TEMP%\ttc_encoder_cmd.log）已移除：
            // 该日志在 Release 下也会持续写入并最终无限增长。
            CoreLog.Debug("JXL_HDR", $"{psi.FileName} {psi.Arguments}", LogCategory.Encoding);

            var result = NativeEncoderGuard.TryEncode("JXL_HDR", () =>
            {
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("无法启动 cjxl");
                var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 60_000, "cjxl(HDR)");

                if (exitCode != 0 || !File.Exists(path))
                    throw new InvalidOperationException($"[JXL] cjxl HDR 编码失败 (exit={exitCode}) {stderr.Trim()}");
                return File.ReadAllBytes(path);
            });
            if (!result.Success)
                throw new InvalidOperationException($"[JXL] cjxl HDR 编码失败: {result.Error?.Message}");
        }
        finally
        {
            try { File.Delete(tmpPng); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    // ═══════════════════════════════════════
    //  中间格式写入辅助
    // ═══════════════════════════════════════

    /// <summary>BGRA8 → PPM (用于 SDR cjxl 输入)。</summary>
    private static void WriteBgraToPpm(byte[] bgra, int w, int h, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new System.IO.StreamWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write($"P6\n{w} {h}\n255\n");
        writer.Flush();

        int pixelCount = w * h;
        for (int i = 0; i < pixelCount; i++)
        {
            int si = i * 4;
            fs.WriteByte(bgra[si + 2]); // R
            fs.WriteByte(bgra[si + 1]); // G
            fs.WriteByte(bgra[si]);     // B
        }
    }
}
