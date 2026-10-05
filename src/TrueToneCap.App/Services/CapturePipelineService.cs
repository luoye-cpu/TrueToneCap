// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/CapturePipelineService.cs
// 截图编码管线服务 — 从 MainWindow 提取
// 负责: ICC 烘焙 + 编码调度 + 输出路径构建

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Metadata;
using TrueToneCap.Core.Processing;
using TrueToneCap.App.Models;
using Vortice.Direct3D11;

namespace TrueToneCap.App.Services;

/// <summary>截图编码管线服务：ICC 色彩管理 + 格式编码 + 文件输出。</summary>
public sealed class CapturePipelineService
{
    private readonly SettingsService _settings;

    // 审查 P2-6：降级 Toast 去重 —— 同一降级组合只弹一次（跨线程读写，volatile 与文件内既有模式一致）
    private static volatile string? s_lastDegradedToast;

    public CapturePipelineService(SettingsService settings)
    {
        _settings = settings;
    }

    /// <summary>ICC 色彩管理准备（后台线程安全，不访问 UI 控件）。</summary>
    /// <param name="decision">本次截图的<b>唯一色彩决定</b>（Core 的 <c>ColorDecision</c>）。
    /// 本函数【不再自己解析用户下拉】—— 那是 2026-09-22 审查轮（交接 §41）要消灭的形态：
    /// 像素路与声明路过去各自解析一次、各问一次光标屏、各用一套原生色域阈值，
    /// 于是同一块窄色域副屏能同时"被判成窄色域"与"被按 P3 搬色"。
    /// <see cref="ColorDecision.SourceTag"/> 就是这里的源，<see cref="ColorDecision.TargetTag"/>
    /// 就是这里的目标，声明侧写的也是同一个值。</param>
    /// <returns>三元组：<c>pixels</c> 可能被搬运到目标色域；<c>iccProfile</c> 是与之配套的声明；
    /// <paramref name="degradedToTag"/> 非 null 表示<b>目标色域没能达成</b>（无显示器 ICC / 矩阵解析失败 /
    /// 标准 profile 取不到），像素仍留在该标签所述色域 ⇒ 调用方必须把声明改到它上面，不能继续按请求声明。
    /// <para>2026-09-19 缺陷 D-1：此前"未转换"与"转换成功"都返回"目标 ICC"，二者在返回值上不可分辨，
    /// 于是产物出现"sRGB 像素 + P3 的 ICC/cICP"这种自相矛盾的声明，且 Release 下完全无声。</para></returns>
    public static (byte[] pixels, byte[]? iccProfile, string? degradedToTag) PreparePixelsWithIcc(
        byte[] bgra, int w, int h, ColorDecision decision)
    {
        // 2026-09-22 用户拍板：色彩管理不再有开关 —— 默认就把像素映射进标准色域（sRGB / BT.2020），
        // 于是"声明与像素不符"这一整族缺陷（含"烘焙关 ⇒ 像素不动、标签照请求写"）从入口上消失。
        // 需要搬色与否由决定自己说（<see cref="ColorDecision.NeedsMapping"/>），不由 UI 布尔说。
        bool acm = decision.SystemAcm;
        var resolvedTag = decision.TargetTag;
        var targetCs = ColorProfileProvider.MapColorSpaceTag(resolvedTag);
        bool isSRgbTarget = resolvedTag is "sRGB";
        // 决定里的源标签已经过 EDID ΔG 闸门：确证不宽的面板这里就是 "sRGB"，
        // 不会再出现"三档判窄色域、这里按 P3 解矩阵"的分叉（§41.1 的错位 B）。
        // ⚠ 后面那个 `isSRgbTarget ? "sRGB" : null` 是【照抄旧语义】的默认值，不是可有可无的：
        //   旧实现在"ACM 开着 + EDID 探测失败 + 目标本来就是 sRGB"时把源当 sRGB 直接原样返回，
        //   不去碰显示器 ICC。若这里一律给 null，就会掉进下方的"传统烘焙"分支、
        //   拿用户校准 ICC 多做一次矩阵 —— 那是我这轮差点引进的行为变化。
        string? nativeGamutTag = !acm ? null
            : decision.NativeGamutKnown ? decision.SourceTag
            : (isSRgbTarget ? "sRGB" : null);

        // ═══ 降级出口（2026-09-19 D-1）：转换没做成时，声明必须退回"像素真实所在色域" ═══
        // 依据换成决定里那份源标签（同源），不再在这里第二次探测原生色域。
        // 目标 ICC 一律不带回来 —— 带回来就是谎（调用方据此会嵌 iCCP）。
        (byte[], byte[]?, string?) NotConverted(byte[] px)
        {
            string actual = acm && nativeGamutTag is not null and not "sRGB" ? nativeGamutTag : "sRGB";
            if (actual == resolvedTag)
                return (px, null, null);   // 像素本就在目标色域 ⇒ 无需降级、也无需 ICC（sRGB）或配套 ICC
            if (actual != "sRGB")
            {
                var nativeIcc = ColorProfileProvider.GetStandardIccProfile(actual);
                if (FormatHelper.IsEmbeddableIcc(nativeIcc))
                    return (px, nativeIcc, actual);
            }
            return (px, null, actual);
        }

        void NoteDegraded(string? degradedToTag, string why)
        {
            if (degradedToTag is null) return;
            // 两条通道都要留：日志给排查用，Notifier 经 NotifyEncoderFallbackIfAny 变 Toast 给用户
            LogService.Warn("Pipeline",
                $"ICC 色彩转换未完成（目标={resolvedTag}，{why}）⇒ 本张按像素实际色域 {degradedToTag} 声明，未做色域搬运");
            EncoderFallbackNotifier.LastReason =
                $"ICC 色彩转换未完成（目标={resolvedTag}，{why}）⇒ 本张按 {degradedToTag} 声明，未做色域搬运";
        }

        // ═══ ACM 模式 (2026-08-09 方案A): 系统已接管显示器色彩管理 ═══
        // ACM 启用时, WGC SDR 捕获的 BGRA8 像素是 DWM 合成后的显示器色域 (ACM 已把
        // 所有应用内容映射到显示器色域)。因此:
        // - 目标 = 显示器原生色域 (或 sRGB): 像素已在该色域, 跳过烘焙, 嵌显示器原生
        //   色域的标准 ICC (或 sRGB 不嵌) 即可 → 无双重转换, 色彩准确
        // - 目标 ≠ 显示器原生色域: 像素在显示器色域但用户要输出目标色域, 必须烘焙
        //   (从显示器色域转换到目标), 跳过烘焙会产生像素/ICC 矛盾
        if (acm && nativeGamutTag is not null)
        {
            // 目标色域 = 显示器原生色域(或 sRGB) → 跳过烘焙, 嵌对应标准 ICC
            if (isSRgbTarget || targetCs == nativeGamutTag)
                return (bgra, isSRgbTarget ? null : ColorProfileProvider.GetStandardIccProfile(targetCs), null);

            // ═══ 2026-08-09 修复: 目标 ≠ 显示器色域 → 用标准原生色域 ICC 作源烘焙 ═══
            // ACM 下 GetDisplayIccProfile 可能返回 null 或已被 ACM 修改的 ICC (非原生色域),
            // 用它作烘焙源 → 矩阵错误/烘焙失败 → 像素未转换却嵌目标 ICC → 色彩错误。
            // 改用标准原生色域 ICC (可靠矩阵): 像素 = 显示器色域 + 标准原生 ICC 解析
            // → BakeIccToTarget 正确转换像素到目标色域 + 嵌目标标准 ICC。
            if (nativeGamutTag is not null)
            {
                var nativeIcc = ColorProfileProvider.GetStandardIccProfile(nativeGamutTag);
                if (FormatHelper.IsEmbeddableIcc(nativeIcc))
                {
                    var (baked, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, nativeIcc, targetCs);
                    // 广色域目标必须拿到配套 ICC 才算真转换成了（矩阵解析失败时 ICC 为 null）
                    if (baked is not null && (isSRgbTarget || FormatHelper.IsEmbeddableIcc(targetIcc)))
                    {
                        if (isSRgbTarget)
                            return (baked, null, null);
                        return (baked, targetIcc, null);
                    }
                    var (px0, icc0, deg0) = NotConverted(baked ?? bgra);
                    NoteDegraded(deg0, "原生→目标矩阵不可解析");
                    return (px0, icc0, deg0);
                }
            }
            // 原生色域未知 → fallthrough 到下方传统烘焙 (可能失败, 兜底)
        }

        // ── 非 ACM 模式（或 ACM 开着但这块屏的 EDID 读不到）: 传统烘焙 (显示器 ICC → 目标色域) ──
        byte[]? displayIcc = null;
        try
        {
            // 问的是【被截那块屏】；决定里没有源屏时才退回光标屏（旧行为，且只在无源屏信息时发生）。
            var srcMonitor = decision.Source?.MonitorHandle ?? DisplayEnumerator.GetMonitorUnderCursor();
            displayIcc = ColorProfileProvider.GetDisplayIccProfile(srcMonitor);
        }
        catch { }

        // 有显示器 ICC → 烘焙到目标色域 (广色域 SDR)
        // 「这份显示器 ICC 值不值得拿去烘焙」也只问同一个结构判据
        // （原先这里另立一个字面量 500，与 Core 的 400 / 128 三套并存 —— 就是 §31.3-9）
        if (FormatHelper.IsEmbeddableIcc(displayIcc))
        {
            var (baked, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, displayIcc, targetCs);
            if (baked is not null && (isSRgbTarget || FormatHelper.IsEmbeddableIcc(targetIcc)))
            {
                if (isSRgbTarget)
                    return (baked, null, null);
                return (baked, targetIcc, null);
            }
            var (px0, icc0, deg0) = NotConverted(baked ?? bgra);
            NoteDegraded(deg0, "显示器 ICC→目标矩阵不可解析");
            return (px0, icc0, deg0);
        }

        // ═══ 2026-09-19 缺陷 D-1 的第二处同形：无显示器 ICC ⇒ 连源色域都无从解析 ═══
        // 旧写法在这里 `return (bgra, GetStandardIccProfile(目标))` —— 一步转换都没做，却把目标广色域
        // 的 ICC 发了出去（与 ColorProfileProvider.BakeIccToTarget 修掉的那处是同一个形状）。
        // 现在退回"像素真实所在色域"的声明，并走 NoteDegraded 留痕。
        var (px, icc, degraded) = NotConverted(bgra);
        NoteDegraded(degraded, "本机无可用显示器 ICC");
        return (px, icc, degraded);
    }

    /// <summary>读取系统当前 ACM 状态 (供 static 方法默认值)。</summary>
    private static bool ReadSystemAcm()
    {
        try { return AppServices.Settings.Current.AcmeDetected; }
        catch { return false; }
    }

    /// <summary>
    /// 构建编码设置。
    /// <para><paramref name="settingsSnapshot"/> 必须是 UI 线程在按下截图时取的
    /// <see cref="AppSettingsData.Snapshot"/>：本方法【可能运行在后台编码线程】，
    /// 而下面的位深/色度/AVIF 编码器选择/每格式质量等十余项都要从设置里读 ——
    /// 直接读活对象 <c>_settings.Current</c> 会与 UI 事件争写（JXL / libaom AVIF 编码 0.5–2s，
    /// 足够用户改一次下拉），产物就可能与"按下那一刻"的选择无关（§11.3 快照纪律的由来）。
    /// 所以快照是必传参数：本方法不再留"没传就读活对象"的静默旁路。</para>
    /// </summary>
    public EncodingSettings BuildEncodingSettings(OutputFormat format, bool hdrOutput, ImageMetadata? meta,
        AppSettingsData settingsSnapshot, string? colorSpaceTag = null, bool? acmEnabled = null,
        ColorDecision? decisionOrNull = null)
    {
        var s = settingsSnapshot;
        bool acm = acmEnabled ?? ReadSystemAcm();
        var avifKind = AvifEncoderCapabilities.Parse(s.AvifEncoder);
        // 记录用户是否【显式】选了某个编码器。Auto 是产品默认值，其解析结果（含"硬件不适用
        // ⇒ 改走软件"）属于自动协商的正常结果，不应以 Warning Toast 形式惊扰用户 ——
        // 只有用户亲手选了硬件编码器却用不了，才需要明确告知。
        bool avifAutoResolved = avifKind == AvifEncoderKind.Auto;
        if (format == OutputFormat.AVIF && avifKind == AvifEncoderKind.Auto)
            avifKind = AvifEncoderSelector.PickAvailableKind(s.ChromaAvif, s.BitDepthAvif);

        // 每格式参数映射（与 MainWindow.BuildEncodingSettings 保持一致）
        // ═══ 2026-09-19：WebP / TIFF 的色度改为常量（字段已按实测删除）═══
        // 判据：同一样张只改色度，产物【字节完全相同】；再用 ffprobe 读回佐证（详见交接 §27）。
        //   · WebP：NativeWebPEncoder 没有色度形参 ⇒ 有损恒 4:2:0（格式定义）、无损为 RGB。
        //     这里给 420 只为与真实码流一致（写 444 会与新加的界面说明自相矛盾）。
        //   · TIFF：ManagedTiffEncoder 写死 Photometric=RGB / SamplesPerPixel=4 的无损 RGBA ⇒ 无色度概念。
        // GainMap 一度也被写成常量 420（当时主路径硬编码 444、只有旁路读设置 ⇒ 那是假配置）。
        // 用户 2026-09-19 定为"默认 420、可手动 444"后，JpegGainMapEncoder 的主路径与两条旁路
        // 全部改读 settings.ChromaSubsampling ⇒ 本行恢复读字段，走哪条路都是同一个色度。
        var (bitDepth, chroma) = format switch
        {
            OutputFormat.PNG => (s.BitDepthPng, "444"),
            OutputFormat.JPEG_LI => (s.BitDepthJpegLi, s.ChromaJpegLi),
            OutputFormat.JPEG_XL => (s.BitDepthJpegXl, s.ChromaJpegXl),
            OutputFormat.AVIF => (s.BitDepthAvif, s.ChromaAvif),
            OutputFormat.WebP => (s.BitDepthWebP, "420"),
            OutputFormat.TIFF => (s.BitDepthTiff, "444"),
            // 2026-09-26：增益图不再是独立格式，而是 JPEG LI 的一个开关 ⇒ 参数与 JPEG LI 同源。
            // 位深沿用 JPEG LI 侧（GainMap 侧历史上恒 8 且界面上从来没有控件，合并后不留第二份）。
            OutputFormat.JPEG_GAINMAP => (s.BitDepthJpegLi, s.ChromaJpegLi),
            _ => (s.OutputBitDepth, s.ChromaAvif),
        };

        // ⚠️ AVIF 的能力协商【不在这里】—— 必须等 settings.HdrOutput 最终确定之后再做，
        //    原因见下方「AVIF 能力协商」处（:264 起）。此处若提前协商会用错场景（HDR/SDR）。

        var settings = new EncodingSettings
        {
            Format = format,
            Quality = (float)GetPerFormatQuality(s, format),
            HdrOutput = hdrOutput,
            AvifEncoderKind = avifKind,
            AvifPngSuffix = s.AvifPngSuffix,
            JxlPngSuffix = s.JxlPngSuffix,
            // 换格式保底的开关（2026-09-29 用户口径）。唯一写者就是这里 —— MainWindow 那份
            // BuildEncodingSettings 委托本方法之后再覆盖自己的 UI 特有位，【不】再覆盖这个字段，
            // 免得"开关"长出两个事实源（AvifPngSuffix 那种控件+快照双写就是前车之鉴）。
            PngFallbackEnabled = s.PngFallbackEnabled,
            ChromaSubsampling = chroma,
            OutputBitDepth = bitDepth,
            GainMapMode = s.GainMapMode == "Gray" ? GainMapMode.Gray : GainMapMode.Rgb,
            Metadata = meta,
            // ── SDR 白点: 系统检测值优先 (DISPLAYCONFIG_SDR_WHITE_LEVEL), 用户手动设置回退 ──
            // 关键: GainMap 的 SDR 直通阈值必须与系统实际 SdrWhiteLevel 一致,
            // 否则 Base 亮度错误 (SDR 发暗/过曝)。系统值 > 0 时优先。
            // ── HDR 峰值: 系统检测值优先 (DXGI MaxLuminance), 用户设置回退 ──
            // 关键: GainMap headroom (XMP/ISO GainMapMax) 依赖此值,
            // 错误值会导致解码端 weight_factor 偏差 → HDR 还原过亮/过暗。
            // ── 色调映射模式: 分段映射 (与 GainMap 同一条曲线) ──
            // 2026-08-08: 所有格式 HDR→SDR 统一走同一条曲线，保证各格式输出一致。
            // ⚠ 2026-09-19 勘误：本注释长期写作"smoothstep 过渡 + 高光 Reinhard 压缩"，而
            //   ToneMapper.SegmentedReinhardMap 早已改成 **y>1 恒饱和 1.0**（headroom/eps 有意不参与，
            //   推导见其 XML 备注：旧曲线在 y=1 处从 1.0 骤降到 ~0.52 再缓慢回升 ⇒ 非单调 ⇒ 高光暗环）。
            //   直接后果：DisplayMaxNits【不】参与 SDR 曲线，它只喂 GainMap 的 headroom、JXL 的
            //   intensity_target、AVIF 的 --clli。别照这段旧描述去"恢复压缩段"。
            ToneMappingParams = new ToneMappingParams
            {
                Mode = ToneMapMode.SegmentedReinhard,
                PaperWhiteNits = (s.SystemSdrWhiteLevel > 0 ? s.SystemSdrWhiteLevel : s.PaperWhiteNits),
                DisplayMaxNits = (s.SystemMaxNits > 0 ? s.SystemMaxNits : s.DisplayMaxNits)
            },
        };

        // ═══ 系统 nits 覆盖用户设置时必须留痕（2026-09-19 审查 P2）═══
        // 上面用 SystemSdrWhiteLevel / SystemMaxNits 优先于用户的 PaperWhiteNits / DisplayMaxNits，
        // 方向是对的（HDR 显示器实测值比手填值可靠），但原先【完全静默】：
        // 用户改了 nits 却看不到效果、也无从判断"到底用了哪个值"。日志是这条链路上唯一的可查证据。
        if (s.SystemSdrWhiteLevel > 0 || s.SystemMaxNits > 0)
            LogService.Info("Pipeline",
                $"色调映射 nits: 纸白 {s.PaperWhiteNits}→{settings.ToneMappingParams.PaperWhiteNits}" +
                $"，峰值 {s.DisplayMaxNits}→{settings.ToneMappingParams.DisplayMaxNits}（系统检测值优先）");

        // ═══ 2026-09-22（审查轮 §41 收口）：输出形态取 ColorDecision 的【同一份】目标 ═══
        // 三档策略（用户口径）：sRGB 屏 ⇒ 8-bit SDR；广色域 SDR 屏 ⇒ BT.2020 + SDR；HDR 屏 ⇒ BT.2020 + HDR。
        // 解析 + 两项钳制现在都在 Core 的 ColorDecision.Build 里做【一次】，像素路（PreparePixelsWithIcc）
        // 与这里消费同一个实例 ⇒ 不可能再出现"像素搬到 A、容器写 B"。
        // ⚠ 本函数不许再调 ResolveColorSpaceTag，也不许在这里补钳制：那正是旧实现长出的两个洞 ——
        //   ①钳制写在解析之前且显式放过 "System" ⇒ 默认档"跟随显示器"完全不经钳制（§41.2）；
        //   ②像素路拿的是未钳制的 ui.ColorSpaceTag ⇒ 窄屏上按 P3 搬色却声明 sRGB（§41.3 的错位 A）。
        // 调用方没给决定时按"无源屏信息"构造（分类 ⇒ Unknown ⇒ 不授权声明、也不施加钳制），
        // 不再退回去猜光标屏。
        var decision = decisionOrNull ?? ColorDecision.Build(
            null, colorSpaceTag, hdrOutput, acm);
        hdrOutput = decision.HdrOutput;
        settings.HdrOutput = hdrOutput;
        if (decision.HdrClamped)
            LogService.Info("Pipeline",
                $"不给 HDR 声称，本次按 SDR 色调映射编码（HDR 开关仍按你的设置保留，只是容器不会写 PQ）⇒ {decision.Evidence}");
        if (decision.GamutClamped)
            LogService.Info("Pipeline",
                $"色域声明由 {ColorProfileProvider.CollapseSelectableTag(colorSpaceTag)} 收口为 sRGB ⇒ {decision.Evidence}");

        // 关键修复: HDR 开启 + sRGB 目标 → 色调映射到 SDR 输出
        // 用户显式选择 sRGB 意味着需要 SDR 兼容输出，而非 HDR PQ 编码
        // 此时 HdrOutput 设为 false，触发色调映射降级路径
        var resolvedTag = decision.TargetTag;
        settings.ColorSpaceTag = resolvedTag;
        // GainMap 要区分"用户手动选 2020"与"跟随显示器在 P3 屏上解析出 2020"⇒ 这个标记只能在这里传，
        // 编码器自己看不到下拉（也绝不该去猜）。
        settings.ExplicitWideTarget = decision.ExplicitWideTarget;
        // ═══ 2026-09-26："HDR 开 + 目标是 sRGB" 这条收口改由 Core 的 FormatColorFacts.Delivery 回答 ═══
        // 挪走有两个理由，都不是为了好看：
        //  ① 界面（色域映射卡片）要说同一句话。它原先自己再推一遍，且推的是"开关为真 ⇒ HDR 直通"，
        //     于是这条降级在日志里有、在产物里有、在界面上【没有】。
        //  ② 它原先无条件生效，把增益图也一起打死了 —— 而 sRGB base 正是 Ultra HDR 的约定形状：
        //     HdrOutput=false 会让 JpegGainMapEncoder.EncodeAsync 命中 :58 那条"降级成普通 JPEG、
        //     没有增益图"的路。界面上那个开关写着"HDR 的 JPEG 载体"，选个 sRGB 底图就整条功能失效，
        //     这不是降级而是消失，且除了日志没有任何地方告知。
        var delivery = FormatColorFacts.Delivery(decision, format);
        if (delivery == HdrDelivery.BlockedBySrgbTarget)
        {
            settings.HdrOutput = false;
            hdrOutput = false;
            LogService.Info("Pipeline",
                $"HDR 开启 + sRGB 目标 → 自动降级为 SDR 色调映射输出（sRGB 容器声明不了 PQ；增益图不受此限制）");
        }
        // HDR 开启 + 广色域目标 (P3/AdobeRGB/BT.2020) → 保留 HDR 直通编码
        // 用户选择广色域目标意味着要保留 HDR 动态范围

        // ═══ 2026-09-24（§48 #21）：HDR 开启时 PNG 的容器位深至少 10-bit ═══
        // 旧行为：HdrEnabled 默认 true、BitDepthPng 默认 8 ⇒ FormatEncoders.cs:25-36 命中
        // `s.OutputBitDepth <= 8` ⇒ 整次输出走 ToSdrAndDegrade。也就是说【默认设置下 PNG 的 HDR
        // 截图根本不是 HDR】，只在编码器里提示一次。旁证很硬：D:\TTC 里 66 张真实 PNG 全部
        // bd=8、零 sBIT、没有一张带 PQ。这不是"8-bit 装不下 PQ"的技术限制，是默认值与默认意图打架。
        // 与 AVIF 的 HDR 协商同构（HDR ⇒ 恒 10）：升的是【容器位深】这个次要意图，
        // 保住的是【HDR】这个主要意图；且必须像 AVIF 那样显式告知，不许静默改参数。
        // ⚠ TIFF 不在这里 —— 它的 HDR 分支固定 16-bit 全精度（FormatEncoders.cs:503），
        //   压根不读 OutputBitDepth，所以没有降级问题（代价是位深下拉对 TIFF+HDR 形同摆设，
        //   那是"假配置"另一类缺陷，记在 §48 待办，不在这里顺手改）。
        if (hdrOutput && format == OutputFormat.PNG && bitDepth < 10)
        {
            string pngHdrDepth =
                $"PNG 的 HDR 输出需要 ≥10-bit 容器 ⇒ 本次位深从 {bitDepth}-bit 升为 10-bit"
                + "（旧实现遇到 8-bit 会把整张图退回 SDR 色调映射，即「看起来在出 HDR、实际出的是 SDR」）";
            LogService.Warn("Pipeline", $"PNG HDR 位深协商: {pngHdrDepth}");
            if (s_lastDegradedToast != pngHdrDepth)
            {
                s_lastDegradedToast = pngHdrDepth;
                ToastService.ShowToast("PNG HDR 位深提示", pngHdrDepth, ToastTone.Warning);
            }
            bitDepth = 10;
            settings.OutputBitDepth = 10;
        }

        // ═══ 2026-09-17 修复：AVIF 能力协商必须在 HdrOutput【最终确定之后】，并分 SDR / HDR 场景 ═══
        // 旧实现把协商放在 HdrOutput 被改写之前，且只看 format==AVIF、不看 HDR，导致两处失真：
        //  ① HDR 场景：HDR 路径根本不走 SelectByKind —— AvifEncoder.EncodeAsync 的 HdrOutput 分支
        //     直接 HdrToPq16(…, 10) + 16-bit PNG 中转 + avifenc（--depth 10 硬编码，不带 --yuv）。
        //     于是"协商出的 420/8-bit"完全不生效，用户却照样收到降级 Toast（假警报）。
        //  ② SDR 场景：能力表给软件编码器声明 12/10-bit，硬件（NVENC）2026-09-17 实测亦可达
        //     10-bit（outputBitDepth 控制，8-bit 输入硬件内部升位深；QSV 未验证，仍声明 8）。
        //     旧实现不区分编码器、一律按"恒 8"钳制，于是软件侧请求 10-bit 时协商前后相等，
        //     连降级提示都不触发，日志却打"位深=10"（真正的静默改参数）。
        //     现统一按 SdrBitDepthFor 分场景钳制（avifenc --depth 已对 PNG 输入生效）。
        // 又：HdrOutput 可能刚被上面改写（HDR 开启 + sRGB 目标 → 降级 SDR），
        // 所以这里必须用 settings.HdrOutput 判定场景，不能用入参 hdrOutput。
        if (format == OutputFormat.AVIF)
        {
            var want = (bitDepth, chroma);

            if (settings.HdrOutput)
            {
                // HDR：位深**下限** 10（PQ 在 8-bit 下分层明显），上限按编码器（libaom 可到 12）。
                // 真正的钳制放在下面"编码器改写"之后 —— 先定最终用哪个编码器、再按它的能力钳，
                // 否则会拿被弃用的那档能力算上限（QSV→avifenc 回落时上限从 8 变 12 就是这种错）。
                // ═══ 2026-09-18：HDR 硬件支持已扩至 NVENC（真 10-bit PQ 输入 + colr PQ 声明）═══
                // 仍不支持的只剩 QSV（NV12 8-bit 输入）⇒ 管线层改写为 avifenc 并告知。
                if (!AvifEncoderCapabilities.SupportsHdrPath(avifKind))
                {
                    // 先取名再改写：改写后 Caps(avifKind) 就变成 avifenc 了
                    string hwName = AvifEncoderCapabilities.Caps(avifKind).DisplayName;
                    avifKind = AvifEncoderKind.Avifenc;

                    // ═══ 2026-09-17 修复：Auto 解析到硬件 ≠ 用户显式选择了硬件 ═══
                    // 旧实现不区分二者，一律弹 Warning Toast。而 Auto 是产品默认值，
                    // 于是"默认设置 + HDR"第一次截图必然弹出"NVENC 不支持 HDR"的警告 ——
                    // 用户从未选过 NVENC，这条提示既不可操作也让人困惑（假警报）。
                    // 现在：Auto 的自动回落只记 Info 日志；只有用户亲手选了硬件才提示。
                    if (avifAutoResolved)
                    {
                        LogService.Info("Pipeline",
                            $"AVIF HDR：Auto 解析到 {hwName}，硬件编码器不支持 HDR 输出，改用 avifenc 软件编码");
                    }
                    else
                    {
                        string hdrHwMsg =
                            $"{hwName} 不支持 HDR 输出"
                            + "（容器层不写 PQ / Rec.2020 码点，HDR 会被误判为 SDR）"
                            + "，已改用 avifenc 软件编码";
                        LogService.Warn("Pipeline", $"AVIF HDR 编码器回落: {hdrHwMsg}");
                        if (s_lastDegradedToast != hdrHwMsg)
                        {
                            s_lastDegradedToast = hdrHwMsg;
                            ToastService.ShowToast("AVIF HDR 编码器提示", hdrHwMsg, ToastTone.Warning);
                        }
                    }
                }

                // 色度：2026-09-17 起 HDR 路径会真实传递 --yuv（EncodeFile 新增 chroma 形参）
                // ⇒ 设置生效。这里仍走一遍协商以保持契约诚实：将来若某条 HDR 路径引入
                // 能力受限的编码器，会立刻降级并提示，而不是"改了设置没反应"。
                chroma = AvifEncoderCapabilities.NegotiateChroma(avifKind, chroma);
                if (want.Item2 != chroma)
                {
                    string avifDegraded =
                        $"{AvifEncoderCapabilities.Caps(avifKind).DisplayName} 能力限制："
                        + $"色度从 {want.Item2} 降级为 {chroma}";
                    LogService.Warn("Pipeline", $"AVIF HDR 参数降级: {avifDegraded}");
                    if (s_lastDegradedToast != avifDegraded)
                    {
                        s_lastDegradedToast = avifDegraded;
                        ToastService.ShowToast("AVIF 编码器能力提示", avifDegraded, ToastTone.Warning);
                    }
                }
                // 注意：未降级时【不要】清空 s_lastDegradedToast。
                // 旧实现这么做，于是"降级→提示 → 下次未降级→清空 → 再下次降级→又提示"，
                // 同一组合会被反复弹出，去重形同虚设。去重键就是提示文本本身：
                // 编码器或参数一变，文本自然不同，会自动重新提示，无需靠清空来"重置"。

                // ═══ 2026-09-25：HDR 位深改为"按编码器能力协商"，不再无条件钉在 10 ═══
                // 之前这里写死 HdrBitDepth，而软件编码器（libaom）能力表本来就声明 12-bit ⇒
                // 用户想要更高精度时没有任何入口，且这个 10 在管线、量化、avifenc 命令行各写一遍。
                // 现在：下限 10（PQ 要求）、上限 HdrBitDepthFor(avifKind)，且必须在 avifKind
                // 定稿（含 QSV→avifenc 回落）之后才算。
                int hdrCeil = AvifEncoderCapabilities.HdrBitDepthFor(avifKind);
                bitDepth = AvifEncoderCapabilities.NegotiateBitDepth(avifKind, bitDepth, hdr: true);
                if (want.Item1 != bitDepth)
                {
                    string depthMsg =
                        $"{AvifEncoderCapabilities.Caps(avifKind).DisplayName} 的 HDR 位深："
                        + $"{want.Item1}-bit 调整为 {bitDepth}-bit"
                        + $"（该编码器 HDR 可用区间 {AvifEncoderCapabilities.HdrBitDepth}–{hdrCeil}）";
                    LogService.Warn("Pipeline", $"AVIF HDR 参数协商: {depthMsg}");
                    if (s_lastDegradedToast != depthMsg)
                    {
                        s_lastDegradedToast = depthMsg;
                        ToastService.ShowToast("AVIF HDR 位深提示", depthMsg, ToastTone.Warning);
                    }
                }

                settings.AvifEncoderKind = avifKind;
                settings.ChromaSubsampling = chroma;
                settings.OutputBitDepth = bitDepth;
                LogService.Info("Pipeline",
                    $"AVIF HDR 输出：{bitDepth}-bit（PQ）；NVENC 支持 HDR 硬件直通，QSV 将回落 avifenc");
            }
            else
            {
                // SDR：按所选编码器能力表协商色度，位深按 SDR 真实上限钳制；降级必须告知用户
                bitDepth = AvifEncoderCapabilities.NegotiateBitDepth(avifKind, bitDepth, hdr: false);
                chroma = AvifEncoderCapabilities.NegotiateChroma(avifKind, chroma);
                if (want != (bitDepth, chroma))
                {
                    string avifDegraded =
                        $"{AvifEncoderCapabilities.Caps(avifKind).DisplayName} 能力限制："
                        + $"参数从 {want.Item2} / {want.Item1}-bit 降级为 {chroma} / {bitDepth}-bit";
                    LogService.Warn("Pipeline", $"AVIF 参数降级: {avifDegraded}");
                    // 同一降级组合只提示一次（去重）；参数已匹配则重置，便于切换编码器后再次提示
                    if (s_lastDegradedToast != avifDegraded)
                    {
                        s_lastDegradedToast = avifDegraded;
                        ToastService.ShowToast("AVIF 编码器能力提示", avifDegraded, ToastTone.Warning);
                    }
                }
                // 注意：未降级时【不要】清空 s_lastDegradedToast。
                // 旧实现这么做，于是"降级→提示 → 下次未降级→清空 → 再下次降级→又提示"，
                // 同一组合会被反复弹出，去重形同虚设。去重键就是提示文本本身：
                // 编码器或参数一变，文本自然不同，会自动重新提示，无需靠清空来"重置"。

                settings.AvifEncoderKind = avifKind;
                settings.ChromaSubsampling = chroma;
                settings.OutputBitDepth = bitDepth;
            }
        }

        // HDR 这一栏写"交付形态"而不是只写布尔：JPEG LI / WebP 上 settings.HdrOutput 仍为真（编码器
        // 自己在内部降级），只打布尔就会留下"HDR=True 的 JPEG"这种对不上账的日志。
        LogService.Info("Pipeline", $"编码设置: {format} HDR={settings.HdrOutput}/{delivery} 质量={settings.Quality:F1} 位深={settings.OutputBitDepth} 色度={settings.ChromaSubsampling} AVIF编码器={settings.AvifEncoderKind} 色域={resolvedTag}");
        return settings;
    }

    /// <summary>
    /// 按格式获取每格式独立质量（避免切换格式互相覆盖）。
    /// <para>2026-09-26：这里原本是自己的一份 <c>format switch</c> —— 与
    /// <c>AppSettingsQuality.GetQuality</c>（AppSettingsData.cs）以及 UI 的 <c>_formats</c> 列表
    /// 构成同一张表的<b>三份抄写</b>。现在统一走那个扩展方法，增益图态由它内部映射到 JPEG LI 那一格。</para>
    /// </summary>
    private static double GetPerFormatQuality(AppSettingsData s, OutputFormat format) =>
        s.GetQuality(format);

    /// <summary>构建输出文件路径。</summary>
    /// <remarks>
    /// ".png 后缀"两个开关取自传入的 <paramref name="settings"/>（UI 线程快照的产物），
    /// 不再读活的 <c>_settings.Current</c>：编码器拿到的后缀是快照值、文件名后缀却是另一时刻
    /// 的活值，二者会分叉（静默截图路径里快照与这里之间隔着一次 WGC 采集）。
    /// </remarks>
    public string BuildOutputPath(OutputFormat format, string outDir, EncodingSettings settings)
    {
        var s = _settings.Current;
        var ext = format switch
        {
            OutputFormat.JPEG_LI => ".jpg",
            OutputFormat.JPEG_GAINMAP => ".jpg",
            OutputFormat.JPEG_XL => ".jxl",
            OutputFormat.AVIF => ".avif",
            OutputFormat.WebP => ".webp",
            OutputFormat.TIFF => ".tiff",
            _ => ".png"
        };
        if (format == OutputFormat.AVIF && settings.AvifPngSuffix) ext += ".png";
        else if (format == OutputFormat.JPEG_XL && settings.JxlPngSuffix) ext += ".png";
        return Path.Combine(outDir, $"{s.FileNamePrefix}{DateTime.Now:yyyyMMdd_HHmmssfff}{ext}");
    }

    /// <summary>获取有效的输出目录（含归档子目录）。</summary>
    public string GetEffectiveOutputDir()
    {
        var s = _settings.Current;
        var outDir = s.OutputPath;
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "TrueToneCap");
        if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
        if (s.ArchiveEnabled) outDir = GetArchivePath(outDir, s.ArchiveMode);
        return outDir;
    }

    /// <summary>根据归档模式生成子目录路径。</summary>
    public static string GetArchivePath(string baseDir, string mode)
    {
        var now = DateTime.Now;
        string sub = mode switch
        {
            "Year" => now.ToString("yyyy"),
            "Day" => now.ToString("yyyy-MM-dd"),
            _ => now.ToString("yyyy-MM"),
        };
        string full = Path.Combine(baseDir, sub);
        Directory.CreateDirectory(full);
        return full;
    }

    // ═══ 2026-09-19 修复（§26.4 P1）：删除"内部自建设置"的 EncodeAndSaveAsync 重载 ═══
    // 原重载在【后台编码线程】调用 BuildEncodingSettings 且不给快照 ⇒ 读的是活的
    // _settings.Current，绕过 §11.3 快照纪律：JXL / libaom AVIF 编码 0.5–2s 足够用户改一次
    // 下拉，产物就可能与"按下截图那一刻"的选择无关。
    // MainWindow 的全部 5 个调用点一直走下面的 EncodingSettings 重载（设置已在 UI 线程
    // 从快照构建），本重载无任何调用者 ⇒ 直接删除，而不是留一条静默的旁路。

    // ═══ 2026-08-30 修复: 移除 EncodeSync / EncodeSyncSdr 两个同步包装 ═══
    // 原实现用 encoder.EncodeXxxAsync(...).GetAwaiter().GetResult() 在 Task.Run 内阻塞等待，
    // 而编码器内部又各自包了一层 Task.Run —— 一次编码同时占用 2 个线程池线程
    // （1 个阻塞在 GetResult + 1 个真正执行）。连续/并发截图时有线程池饥饿风险。
    // 现改为在 Task.Run 内直接 await，外层线程在等待期间归还线程池。

    /// <summary>编码并保存（使用调用方提供的显式设置，供 MainWindow UI 路径使用）。</summary>
    /// <remarks>
    /// 注意: 输入为 byte[] BGRA8 像素，始终走 SDR 编码路径，忽略 settings.HdrOutput。
    /// 真正的 HDR 编码请使用 EncodeHdrFrameAsync。
    /// </remarks>
    public async Task<string> EncodeAndSaveAsync(
        byte[] bgra, int w, int h, EncodingSettings settings,
        ColorDecision decision,
        CancellationToken ct = default, ID3D11Texture2D? gpuTexture = null,
        string? outputDir = null)
    {
        var encoder = EncoderFactory.Create(settings.Format);
        // ═══ 2026-09-17 双路径策略：本入口源恒为 8-bit BGRA ⇒ 恒 8-bit 输出 ═══
        // 源只有 8-bit，升位深不产生信息、只徒增体积 ⇒ 无论用户设置什么位深都钳到 8
        // （诚实做法，交接 §4.3 Case B）。scRGB Float16 源走 EncodeHdrFrameAsync，那边才有真 10-bit 通路。
        if (settings.OutputBitDepth > 8)
        {
            LogService.Info("Pipeline",
                $"双路径策略: 8-bit 源不升位深，输出位深 {settings.OutputBitDepth} → 8（源只有 8-bit，升位深不产生信息）");
            settings.OutputBitDepth = 8;
        }
        // GPU 纹理直通会【绕开】下面那次 CPU 矩阵 ⇒ 只有"这次根本不需要搬色"时才许直通。
        // 判据放在管线里而不是各调用点：调用点传不传纹理是性能选择，"搬色必须过 CPU"是正确性约束，
        // 后者不该被调用方各自决定。（NVENC 下发的 H.273 matrixCoefficients 是 YUV↔RGB 系数，
        // 不是色域转换 —— 见 NvEncoderNative.cs:96 与 §32.7，所以硬件路补不了这一刀。）
        if (gpuTexture is not null && decision.NeedsMapping)
        {
            LogService.Info("Pipeline",
                $"本次需要色域搬运（{decision.SourceTag} → {decision.TargetTag}）⇒ 弃用 GPU 纹理直通，走 CPU 矩阵");
        }
        else if (gpuTexture is not null)
            settings.GpuTexture = gpuTexture;
        var outDir = outputDir ?? GetEffectiveOutputDir();
        var path = BuildOutputPath(settings.Format, outDir, settings);

        LogService.Info("Pipeline", $"开始编码: {settings.Format} {w}x{h} → {Path.GetFileName(path)}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var (pixels, iccProfile, degradedToTag) = PreparePixelsWithIcc(bgra, w, h, decision);
            if (iccProfile is not null)
                settings.IccProfile = iccProfile;
            // ═══ 2026-09-19 缺陷 D-1：烘焙没做成 ⇒ 声明跟着像素走，不跟着请求走 ═══
            // tag 与 ICC 必须【一起】改：只清 ICC 会留下 cICP=目标色域（PNG/AVIF 的码点直接取自 tag），
            // 那和刚被清掉的 ICC 是同一个谎。反之只改 tag 而留着目标 ICC 也是谎。
            // settings.IccProfile 在这里无条件覆盖，是因为 MainWindow 可能已塞进捕获 ICC
            // （`settings.IccProfile ??= captureResult.IccProfile`）—— 降级时它必须一起走。
            if (degradedToTag is not null)
            {
                settings.ColorSpaceTag = degradedToTag;
                settings.IccProfile = iccProfile;
            }

            ct.ThrowIfCancellationRequested();
            // 始终走 SDR 路径
            await encoder.EncodeSdrAsync(pixels, w, h, settings, path, ct);
        }, ct);
        }
        finally
        {
            PowerManager.AllowSleep();
            // ═══ 消费降级留痕必须排在 finally（2026-09-30 任务 #10，报告 §17.6 第 5 条）═══
            // 原先这一句只写在成功路径末尾 ⇒ 编码整条失败时那次原因留在单槽里，
            // 下一次【成功】的截图会把它当成自己的降级弹出来 —— 本文件下面那句注释
            // （"不清空会把上一次的原因报到下一次截图上"）警告过的正是这件事，但清槽只做了一半路径。
            // 被取消的那一次不再弹提示（用户知道自己在取消），照样清槽 + 落日志：事实不许泄漏到下一张。
            // ⚠ 这条口径刚被变异 W2 验过：把这一句挪回成功路径，CorePipelineTests 那条
            //   "两条编码腿都在 finally 里消费"当场红（实得 1）。
            NotifyEncoderFallbackIfAny(quiet: ct.IsCancellationRequested);
        }

        // 同上：校正末端保底（如 AVIF → PNG）后的真实落盘路径
        var actualPath = FormatHelper.ResolveActualPath(path);
        var fileSize = File.Exists(actualPath) ? new FileInfo(actualPath).Length : 0;
        LogService.Info("Pipeline", $"编码完成: {Path.GetFileName(actualPath)} ({fileSize / 1024.0:F1} KB)");
        return actualPath;
    }

    /// <summary>编码并保存 HDR 帧数据（WGC 直接捕获的 scRGB float 像素）。</summary>
    public async Task<string> EncodeHdrFrameAsync(
        HdrFrameData hdrFrame, EncodingSettings settings,
        CancellationToken ct = default)
    {
        var encoder = EncoderFactory.Create(settings.Format);
        var outDir = GetEffectiveOutputDir();
        var path = BuildOutputPath(settings.Format, outDir, settings);

        // ═══ 2026-09-16：分段计时 ═══
        // 实测 avifenc 编码 4K 10-bit 444 只要约 1.3s，而整条 HDR AVIF 链路要 24s
        // ⇒ 绝大部分耗时在**编码之前**（scRGB→PQ / 位深转换 / PNG 中转）。
        // 这里把"编码调用"单独计时，与总耗时对比即可定位瓶颈在哪一段。
        var swTotal = System.Diagnostics.Stopwatch.StartNew();

        // ═══ 内存留痕（2026-09-20）：一条 4K HDR 链路会造出多份 132MB 级的 float[]/byte[] ═══
        // 实测连拍 4 张后私有字节从 1.5GB 爬到 3.3GB、静置 40s 不回落。但 WorkingSet/PrivateMemory
        // 分不出"对象还被活着引用"和"GC 已回收却没把段还给 OS"（发布包的 runtimeconfig 里没有任何
        // GC 设置 ⇒ 默认工作站 GC，会保留已提交段）。所以两个数一起打：
        //   托管堆 ≈ 私有字节 ⇒ 真持有，该去查谁还引用着帧缓冲；
        //   托管堆 ≪ 私有字节 ⇒ 只是没归还，属 GC 策略而非泄漏。
        // GetTotalMemory(forceFullCollection: false) 不触发收集，不影响时延。
        static string MemText()
        {
            long heap = System.GC.GetTotalMemory(forceFullCollection: false) / (1024 * 1024);
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return $"托管堆={heap}MB 私有={self.PrivateMemorySize64 / (1024 * 1024)}MB "
                 + $"工作集={self.WorkingSet64 / (1024 * 1024)}MB";
        }

        LogService.Info("Pipeline",
            $"HDR 编码启动: {settings.Format} {hdrFrame.Width}x{hdrFrame.Height} HDR={settings.HdrOutput} 色域={settings.ColorSpaceTag} → {Path.GetFileName(path)} | 内存(起) {MemText()}");

        PowerManager.PreventSleep();
        try
        {
        await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();

            // HDR 关闭 + scRGB 数据 → 色调映射到 SDR
            // 场景: HDR ON + sRGB 目标 (已在 BuildEncodingSettings 中设为 HdrOutput=false)
            //       或编码器不支持 HDR 的降级
            if (!settings.HdrOutput)
            {
                // ═══ 2026-09-19 解耦：删掉"Float16 源 ⇒ 把 OutputBitDepth 强提到 16"（2026-09-18 指令的副作用）═══
                // 那条改写让【用户请求的 8-bit 永远不可达】：位深下拉同时决定了"输出容器位深"和
                // "内部走不走高精度链"，于是想要 8-bit 也得吃 16-bit 产物的体积。
                // 现在的分工（与 FormatEncoders 的 FormatHelper.IsHighPrecisionSource 对接）：
                //   · 源精度 = 本入口的事实：HdrFrameData.Pixels 是 WGC Float16 scRGB（Windows 给得出 >8-bit）
                //     ⇒ 编码器内部恒走"色域矩阵 → 色调映射 → gamma → 量化一次"的高精度浮点链；
                //   · 容器位深 = 用户请求的 OutputBitDepth，不再被改写（8 就真出 8-bit 容器）。
                // 8-bit BGRA 源（EncodeAndSaveAsync）那条"钳 8"保留 —— 那是源精度的诚实陈述，与此无关。
                LogService.Info("Pipeline",
                    $"scRGB(Float16) 源 ⇒ 内部走高精度浮点链；输出容器位深按用户请求 = {settings.OutputBitDepth}-bit"
                    + $"，委托 {settings.Format} 编码器 SDR 分派（矩阵/色调映射/gamma 不因 8-bit 请求跳过）");
                await encoder.EncodeAsync(hdrFrame, settings, path, ct);
                return;
            }

            if (encoder.SupportsHdr)
            {
                // ═══ 2026-08-09 修复: HDR 直通 ICC 策略 ═══
                // 关键教训: 标准 ICC (GetStandardIccProfile) 是 sRGB TRC,
                // 而 HDR 像素是 PQ 编码 → 嵌入会覆盖/冲突 → 解码器按 sRGB gamma
                // 解码 PQ 值 → 图像错误 (JXL 实测: cjxl 的 icc_pathname 覆盖 color_space,
                // 最终 codestream 声明 sRGB transfer 而像素是 PQ!)。
                // - JXL: 用 `-x color_space=Rec2100PQ` color fields 声明 BT.2020+PQ (最优)
                // - PNG/AVIF: 用 CICP (BT.2020 + PQ) 声明 (PNG 3.0/AV1 规范优先)
                // - TIFF: 无 CICP 机制 → 必须嵌 PQ TRC ICC
                if (settings.Format == OutputFormat.TIFF)
                {
                    var hdrCsTag = settings.ColorSpaceTag;
                    settings.IccProfile = (hdrCsTag is not (null or "System" or "sRGB"))
                        ? ColorProfileProvider.GetHdrStandardIccProfile(hdrCsTag)
                        : null;
                }
                else
                {
                    settings.IccProfile = null;
                }
                LogService.Debug("Pipeline", $"HDR 直通编码: {settings.Format}");
                await encoder.EncodeAsync(hdrFrame, settings, path, ct);
            }
            else
            {
                // 编码器不支持 HDR → 色调映射到 SDR
                LogService.Info("Pipeline", $"编码器不支持 HDR，色调映射到 SDR: {settings.Format}");
                var sdrPixels = FormatHelper.ToSdr(hdrFrame, settings);
                // ═══ 2026-08-16 P1-2 修复: 派生像素禁止带 GPU 纹理 (同上) ═══
                settings.GpuTexture = null;
                // ⚠ 2026-09-19：这里不再无条件把 ColorSpaceTag 覆写成 "sRGB"。
                //   ToSdr 现在与高位深通路共用同一条浮点链（含色域矩阵），标签/ICC 记账已在其中完成：
                //   广色域时像素已被搬到目标色域 ⇒ 必须保留 tag 让容器同色域声明，
                //   再覆写成 sRGB 就成了"声明与像素矛盾"（查看器按 sRGB 解释广色域像素 → 偏色）。
                settings.HdrOutput = false;
                await encoder.EncodeSdrAsync(sdrPixels, hdrFrame.Width, hdrFrame.Height, settings, path, ct);
            }
        }, ct);
        }
        finally
        {
            PowerManager.AllowSleep();
            // 与 SDR 那条腿同一个口径：消费必须在 finally（见上面那段注释），两条腿不许只有一条记得清槽。
            NotifyEncoderFallbackIfAny(quiet: ct.IsCancellationRequested);
        }

        // 同上：校正末端保底（如 AVIF → PNG）后的真实落盘路径
        var actualPath = FormatHelper.ResolveActualPath(path);
        var fileSize = File.Exists(actualPath) ? new FileInfo(actualPath).Length : 0;
        LogService.Info("Pipeline",
            $"HDR 编码完成: {Path.GetFileName(actualPath)} ({fileSize / 1024.0:F1} KB) 总耗时={swTotal.ElapsedMilliseconds}ms | 内存(终) {MemText()}");
        return actualPath;
    }

    // 降级提示的去重只有【一条】规则：与上一次弹过的文案比内容（s_lastDegradedToast）。
    // 原先这里另有一个 `s_hwFallbackNotified` 静态布尔 —— "整个进程只弹一次"。两种后果都很糟：
    //   ① 第一条降级（往往是 NVENC→libaom）之后，本进程内所有【其他种类】的降级永久无声，
    //      包括 HDR+PNG 的位深协商(#21)与 HDR TIFF 被改成 16-bit(#22)；
    //   ② 用户要再看到原因只能去翻日志，而"弹过一次就不弹"在用户端等价于"这个功能坏了"。
    // 同一件事在本文件里已经有正确做法（4 处协商点都走 s_lastDegradedToast），不该再有第二套。

    /// <summary>
    /// 若本次编码发生过<b>任何形式的后端降级</b>（硬件→软件、cwebp 缺失→PNG 保底、
    /// avifenc 拒位深→按 8-bit 重试），给出一次性提示。
    /// <para>4K AVIF：硬件约 0.12s vs 软件约 22s（约 180 倍）。原实现全程只有 Debug.WriteLine，
    /// Release 下完全无声 —— 用户只会觉得"软件变慢了"，既不知道原因，也不知道能否恢复。
    /// <para>这里复用已有的 ShowCaptureDegraded 通道（原本用于捕获降级）。</para>
    /// 去重只按【文案内容】：连续截图时同一种降级不重复弹，换了种类的降级必须再弹一次
    /// （原先那句"每会话只提示一次"是旧实现 <c>s_hwFallbackNotified</c> 的说法，已废，见上面那段）。
    /// </summary>
    /// <param name="quiet">true = 只落日志、不弹提示（本次是被用户取消的那一次）。
    /// <b>不许</b>因为安静就不清槽，也不许占用去重名额：清槽管的是"上一次的原因泄漏到下一张"，
    /// 而这一次没弹，下一次同样的降级仍然要弹得出来。</param>
    private static void NotifyEncoderFallbackIfAny(bool quiet = false)
    {
        // ═══ 2026-09-19（全编码器审计）：两条通道都要读，且读一次即清空 ═══
        //   · NvencAvifBackend.LastFallbackReason —— NVENC 专用（硬件→软件）
        //   · EncoderFallbackNotifier.LastReason  —— Core 侧通用：MFT→libaom、cwebp 缺失→PNG 保底、
        //     avifenc 拒位深→按 8-bit 重试。这三处原先只有 Debug.WriteLine，Release 下完全无声。
        // 不清空会把上一次的原因报到下一次截图上；两条都有原因时，被展示的那条之外
        // 另一条仍要进日志，否则只剩一个原因可见。
        // ⚠ 这一句现在由两条编码腿的 finally 各调一次（2026-09-30 任务 #10）：失败路径同样要消费，
        //   否则那次原因会躺在单槽里，被下一次【成功】的截图认领。
        var hw = NvencAvifBackend.LastFallbackReason;
        NvencAvifBackend.LastFallbackReason = null;
        var gen = EncoderFallbackNotifier.LastReason;
        EncoderFallbackNotifier.LastReason = null;
        if (hw is { Length: > 0 } && gen is { Length: > 0 })
            LogService.Warn("Pipeline", gen);
        var reason = string.IsNullOrEmpty(hw) ? gen : hw;
        if (string.IsNullOrEmpty(reason)) return;

        LogService.Warn("Pipeline", reason);          // UI 日志面板可见
        if (quiet) return;                            // 取消的那一次：事实进日志，不再弹给用户
        // 与上面 4 处位深/色度协商共用同一条规则：只有"和上一条弹过的文案完全相同"才压，
        // 换了种类的降级必须再告诉用户一次 —— 降级不许静默。
        if (s_lastDegradedToast == reason) return;
        s_lastDegradedToast = reason;
        ToastService.ShowCaptureDegraded(reason);
    }
}
