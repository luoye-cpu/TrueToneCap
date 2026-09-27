// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/CorePipelineTests.cs
// 核心管线单元测试 — PixelOps / ToneMapper / ColorProfileProvider / Annotation / Encoders
// 运行: dotnet run --project src/TrueToneCap.Test -- --unit-tests

using System.Text.RegularExpressions;
using TrueToneCap.Core;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Logging;   // CoreLog 档位模型（Test_LogDetailLevelIsReachable）
using TrueToneCap.Core.Services;
using DEnum = TrueToneCap.Core.Capture.DisplayEnumerator;   // 三档判据的别名（方法体内不能用 var 给类型起别名）
using CDec = TrueToneCap.Core.Capture.ColorDecision;        // 一次截图的唯一色彩决定（§41 收口的落点）
using FOrder = TrueToneCap.Core.Settings.FormatOrder;       // 格式下拉顺序 + 旧表重映射（§52）

namespace TrueToneCap.Test;

/// <summary>核心管线单元测试集。</summary>
public static class CorePipelineTests
{
    private static int _passed, _failed, _skipped;

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 核心管线单元测试");
        Console.WriteLine("══════════════════════════════════════\n");

        // PixelOps
        Test_FixAlphaChannel_AllZero();
        Test_FixAlphaChannel_PreservesRgb();
        Test_FixAlphaChannel_LargeBuffer();
        Test_ConvertFloatToHalf_Roundtrip();
        Test_ConvertFloatToHalf_SpecialValues();
        Test_BgraToScrgbLinear_KnownValues();

        // ToneMapper
        Test_ToneMapper_Reinhard_BlackStaysBlack();
        Test_ToneMapper_Hable_WhiteClamps();
        Test_ToneMapper_FusedKernel_MatchesSeparate();
        Test_ToneMapper_SwizzleOrder();
        Test_ToneMapper_SrgbLut_Accuracy();
        Test_ToneMapper_SrgbLut_Endpoints();

        // ColorProfileProvider
        Test_SrgbIcc_ValidHeader();
        Test_SrgbIcc_MinimumSize();
        Test_SrgbIcc_Cached();
        Test_ColorProfile_SrgbIcc_Valid();
        Test_ColorProfile_ResolveColorSpaceTag();
        Test_ColorProfile_MapColorSpaceTag();
        Test_ColorProfile_GetStandardIcc_AllSpaces();
        Test_ColorProfile_D50Primaries_Accuracy();

        // PixelOps 扩展
        Test_DownsampleToGray_Dimensions();
        Test_DownsampleToGray_UniformColor();
        Test_ComputeEdgeProjections_FlatImage();
        Test_ComputeEdgeProjections_EdgeDetection();

        // AnnotationManager
        Test_AnnotationManager_AddLayer();
        Test_AnnotationManager_UndoRedo();
        Test_AnnotationManager_RemoveLayer();

        // FormatEncoders
        Test_EncoderRegistry_AllFormatsRegistered();
        Test_EncoderRegistry_QualityRanges();
        Test_EncodingSettings_Defaults();

        // EncoderCrashSentinel（CSE 不可捕获时的"崩溃后自愈降级"机制）
        Test_EncoderCrashSentinel_ArmDisarm();
        Test_EncoderCrashSentinel_BackendScoped();
        Test_EncoderCrashSentinel_ConcurrentArm();
        Test_MftCrashSentinel_IsWired();
        Test_AvifSoftwarePath_HandsIccToAvifenc();
        Test_OcrLanguageMapping_KnownIds();
        Test_OcrLangForWindows();
        Test_OcrResultTrustworthy();
        Test_OcrTimeoutStillReachesFallbackEngine();
        Test_OcrHangulGarbageTriggersFallback();
        Test_OcrUserCancelIsNotReportedAsTimeout();

        // IccStore
        Test_IccStore_AllSpaces_Valid();
        Test_IccStore_Srgb_HasAcspSignature();

        // ColorSpaceConverter
        Test_ColorSpaceConverter_MatrixRowSum();
        Test_ColorSpaceConverter_NoNegativeValues();
        Test_ColorSpaceConverter_GetCicpPrimaries();

        // ═══ 防漂移契约（2026-09-15 新增）═══
        // 这组用例针对的是一类特殊缺陷：代码能编译、测试能跑、但用户拿到错误结果 ——
        //   ① 字符串常量与另一处枚举不同步（P0-D：P3/DCI-P3 矩阵恒匹配失败 → 静默偏色）
        //   ② CPU 与 HLSL 两份实现漂移（P0-C：预览走旧曲线、输出走新曲线）
        //   ③ 编码器返回的路径与实际落盘路径不一致（P1-1：Toast 指向不存在的文件）
        // 它们都不抛异常，只会"悄悄错"，因此必须有契约级断言兜底。
        Test_ColorSpaceTag_MatrixCoverage();
        Test_NormalizeColorSpaceKey();
        Test_ToneMapper_SegmentedReinhard_Monotonic();
        Test_Hlsl_ToneMapping_MatchesCpu();
        Test_FormatHelper_ResolveActualPath();

        // 跨项目源码契约（App 层的"两处常量必须同步"类约束）
        Test_SettingsWhitelist_MatchesXamlTags();
        Test_SettingsDiffPruner_Semantics();
        Test_SettingsPersistence_Sentinels();
        Test_LlmConfig_InjectsYoudaoCredentials();
        Test_StartupManager_ReadHandlesQuotedCommand();

        // D3D 设备丢失判定（重建逻辑的判据，必须可单测）
        Test_D3DDeviceLost_HResultClassification();
        Test_D3DDeviceLost_ExceptionChain();
        Test_D3DDeviceLost_HResultName();

        // NVENC 失败分类（防"一张小图失败 → 后续大图永久退软件"）
        Test_NvencFailureClassification();
        Test_LosslessQualityRowCollapsed_SourceSentinels();
        Test_QualityPanelProgrammaticWrite_SourceSentinels();
        Test_SettingsWriteSites_AllPersist();
        Test_SettingsWriteProbe_RealProbe();
        Test_SettingsPersistence_BlockedPath_Sentinels();
        Test_GainMapMerged_FormatIndexMigration();
        Test_HdrPngDepthNegotiation_SourceSentinels();
        Test_AvifCrfFloorTable();
        Test_DegradationToast_SingleDedupRule();

        // MFT（QSV 选项的实际实现路径）真实编码验证
        Test_MftAv1EncodeIfAvailable();

        // AVIF 编码器【实测】能力探测（UI 下拉可选项的事实来源）
        Test_AvifProbe_UnmeasuredFallsBack();
        Test_AvifProbe_Av1CParse();
        Test_AvifProbe_MeasuredIfAvailable();
        Test_AvifProbe_SvtNotCompiledIn();

        // AVIF 编码器能力表（单一「AVIF 编码器」下拉 + Auto 硬件优先的核心）
        Test_AvifCaps_NegotiateChroma();
        Test_AvifCaps_NegotiateBitDepth();
        Test_AvifCaps_PickAuto();
        Test_AvifCaps_Parse();
        Test_AvifCaps_ChromaExposureMatrix();
        Test_AvifCaps_SupportsHdrPath();
        Test_AvifSdr_DepthArg();

        // 防"静默改参数"的调用顺序 / 按格式钳制契约（审查 P1-1 / P2-7）
        Test_Pipeline_AutoResolvedBeforeNegotiation();
        Test_Settings_BitDepthClampedPerFormat();
        Test_Settings_ChromaSingleSource();
        Test_AvifChroma_DefaultIs420();
        Test_AvifOptions_ExposedNotDisabled();
        Test_AvifOptions_ReselectBeforeHide();
        // 默认值三处事实源 & HDR 下 Auto 不弹假警报
        Test_Settings_BitDepthOptionsSurviveNormalize();
        Test_Settings_QualityDefaultsSingleSource();
        Test_Pipeline_HdrAutoNoFalseAlarm();
        Test_Pipeline_EncodeSettingsRequireSnapshot();
        Test_EncoderHonesty_Audit20260919();
        Test_GainMap_ChromaIsRealSetting();
        Test_Pipeline_SourcePrecisionIsCapabilityDriven();
        Test_WgcInteropIidsAreDistinct();
        // 上一条只看源码形态；这两条补行为：逐屏能力真值表（Core 纯函数）+ 探针判据"会红"哨兵
        Test_Display_PerMonitorCapabilityTruthTable();
        Test_Edid_ReadIsHandleDriven_NotFirstMatch();
        Test_ColorDecision_SingleSourceOfTruth();
        Test_HdrDelivery_FiveShapesAndPerFormatChannel();
        Test_Xaml_HardcodedChineseIsLocalized();
        Test_FormatLabels_AreUserFacingNames();
        Test_VisibleToastAndHintStrings_UseCurrentNames();
        Test_Xaml_CollapsedCards_HaveVisibilityOwner();
        Test_Jxl_ColorSpaceMapping();
        Test_HdrProbeJudge_NotDead();
        Test_HdrAdobeTag_NormalizedBeforeMatrix();
        Test_SourcePrecisionNotUsedAsScene();
        Test_NativeEncodeTimeoutIsJudged();
        Test_NativeProbeTimeoutIsReachable();   // 上一条钉形态，这一条真拿挂死的子进程量时间
        Test_IccEmbeddableIsStructuralNotLength();   // §31.3-9：ICC 判据换成结构判据的三面反证
        Test_UnofficialTranslateEndpointsAreOptIn();  // B-8：非官方翻译端点必须默认不发请求
        Test_TranslationBackendAvailabilityIsHonest();
        Test_LlmFailureReasonIsClassified();
        Test_VisionTranslationIsWired();
        Test_BingWebEndpointShapeAndOptIn();            // 必应网页端点：同一条闸门 + 三个"看着像成功"的坑
        Test_OpIdScopeSemanticsAndWiring();             // 每拍关联 ID：语义 + "App 侧真的开了作用域"的接线判据
        Test_NoRawDebugWritesOutsideTheWhitelistedSinks(); // 防回潮：白名单外不许有裸 Debug.WriteLine
        Test_ManifestLicenseFilesAreComplete();   // 许可清单三处（manifest / INDEX / 磁盘）必须互相闭合
        Test_LogDetailLevelGatesDebugAndIsWired(); // 日志档位真的能挡住/放行 Debug，且设置页接到上
        Test_LogPrivacyFloorScrubbing();            // 隐私地板：OCR 原文 / 密钥进不了日志，且不刮坏正常行
        Test_ColorSpaceOptions_NarrowedToTwo();
        Test_NvencColorIntent_SharedByEncoderAndContainer();
        Test_Jxl_ChromaActuallyChangesOutput();
        Test_Nvenc_Av1TenBit();
        Test_Nvenc_ChromaDowngradeNote();
        Test_Av1SequenceHeader_Parse();
        Test_Nvenc_Argb10PrecisionProbe();
        Test_FormatHelper_Bgra8ToBgra16BE();

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--unit-tests", _passed, _failed, 0, null, _skipped);
        Console.WriteLine($"══════════════════════════════════════");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  EncoderCrashSentinel 测试
    // ═══════════════════════════════════════

    /// <summary>
    /// 说明：这两条用例会读写 %LOCALAPPDATA%\TrueToneCap\encoder-crash.sentinel。
    /// 结束时均主动 ClearAll 复原。若用户此前确有崩溃残留，会被用例清除 ——
    /// 影响仅为"重新启用一次硬件编码"，属无害副作用。
    /// </summary>
    static void Test_EncoderCrashSentinel_ArmDisarm()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            bool before = EncoderCrashSentinel.IsBlocked("NVENC");
            EncoderCrashSentinel.Arm("NVENC");
            bool armed = EncoderCrashSentinel.IsBlocked("NVENC");
            EncoderCrashSentinel.Disarm();
            bool after = EncoderCrashSentinel.IsBlocked("NVENC");

            Assert("EncoderCrashSentinel: 初始未阻塞", !before);
            Assert("EncoderCrashSentinel: Arm 后处于阻塞态（模拟进程崩溃残留）", armed);
            Assert("EncoderCrashSentinel: Disarm 后解除（模拟正常返回）", !after);
        }
        finally
        {
            EncoderCrashSentinel.ClearAll();
        }
    }

    static void Test_EncoderCrashSentinel_BackendScoped()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            EncoderCrashSentinel.Arm("NVENC");
            bool nvenc = EncoderCrashSentinel.IsBlocked("NVENC");
            bool mft = EncoderCrashSentinel.IsBlocked("MFT");

            Assert("EncoderCrashSentinel: 同名后端被正确识别", nvenc);
            Assert("EncoderCrashSentinel: 其它后端不受牵连（按后端隔离降级）", !mft);
        }
        finally
        {
            EncoderCrashSentinel.ClearAll();
        }
    }

    /// <summary>
    /// 并发编码：两个线程同时 Arm，先返回的那个【不得】清除哨兵，
    /// 否则仍在原生调用中的另一个线程就失去了保护 —— 崩溃将不再被记录。
    /// </summary>
    static void Test_EncoderCrashSentinel_ConcurrentArm()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            EncoderCrashSentinel.Arm("NVENC");   // 线程 A 进入原生编码
            EncoderCrashSentinel.Arm("NVENC");   // 线程 B 进入原生编码
            EncoderCrashSentinel.Disarm();       // A 正常返回 —— B 仍在编码中

            bool stillArmed = EncoderCrashSentinel.IsBlocked("NVENC");
            EncoderCrashSentinel.Disarm();       // B 也返回 —— 此时才应清除
            bool cleared = !EncoderCrashSentinel.IsBlocked("NVENC");

            Assert("EncoderCrashSentinel: 并发时先返回者不清除哨兵（后返回者仍受保护）", stillArmed);
            Assert("EncoderCrashSentinel: 最后一个返回者才清除哨兵", cleared);
        }
        finally
        {
            EncoderCrashSentinel.ClearAll();
        }
    }

    /// <summary>
    /// 两个 avifenc 入口都必须【自己】把 ICC 交给编码器 —— 不能指望中转 PNG 替我们带。
    /// <para>回归背景（2026-09-20，交接 §32.10）：实测 avifenc 1.4.2 读一张带 iCCP 的 P3 PNG，
    /// 输出容器里 <b>只有 nclx、没有 ICC</b> ⇒ 它根本不继承输入 PNG 的 profile。
    /// 而 <c>EncodeFile</c>（≥10-bit 的 16-bit PNG 中转分支用）当时连 <c>iccProfile</c> 形参都没有，
    /// 于是"广色域 + ≥10-bit + 软件编码"这一档的 AVIF 只剩 nclx 声明，
    /// 硬件路同一设置写的却是 <c>prof + nclx</c> —— 两条路给同一张图两种声明。</para>
    /// <para>行为层面的判据在 <c>scripts/precision/icc_readback_matrix.py</c>（跨格式/跨后端比对
    /// ICC 字节，已进门禁）；这里只钉结构，防止重构时把形参或实参又默默删掉。</para>
    /// </summary>
    static void Test_AvifSoftwarePath_HandsIccToAvifenc()
    {
        var encPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "NativeAvifEncoder.cs"));
        var fePath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"));
        if (encPath is null || fePath is null)
        {
            Skip("avifenc 两个入口都带 ICC", "未找到 NativeAvifEncoder.cs / FormatEncoders.cs");
            return;
        }
        var enc = CodeOnly(encPath);
        var fe = CodeOnly(fePath);

        // 两个入口：Encode（stdin）与 EncodeFile（PNG 文件）都要有 iccProfile 形参并拼 --icc
        Assert("EncodeFile 有 iccProfile 形参（不能只靠中转 PNG 带 ICC）",
            enc.Contains("int bitDepth = 8, byte[]? iccProfile = null)")
            || enc.Contains("byte[]? iccProfile = null"),
            "找不到形参 ⇒ avifenc 不会继承 PNG 的 iCCP，软件路广色域 AVIF 会只剩 nclx");
        Assert("两个 avifenc 入口都拼 --icc（≥2 处）",
            enc.Split("--icc", StringSplitOptions.None).Length - 1 >= 2,
            $"实得 {enc.Split("--icc", StringSplitOptions.None).Length - 1} 处");
        Assert("临时 ICC 文件用完即删（两处都要，不能只删一处）",
            enc.Split("File.Delete(tmpIccPath)", StringSplitOptions.None).Length - 1 >= 2);
        // 16-bit 中转那条 SDR 分支必须真的把 icc 传下去
        Assert("≥10-bit SDR 分支把 icc 传进 EncodeFile",
            fe.Contains("iccProfile: icc"),
            "漏传就等于该档 AVIF 无 ICC，与硬件路不一致");
    }

    /// <summary>
    /// MFT/QSV 必须也在崩溃哨兵的保护范围内，且崩溃残留只能禁用【一次】启动（交接 §31.3-7）。
    /// <para>为什么算缺陷而不是"防御性加固"：MFT 与 NVENC 一样是<b>进程内</b> COM/P-Invoke 调用，
    /// AccessViolation 一类 CSE 在 .NET 上 catch 不住（<c>NativeEncoderGuard</c> 文件头自己写明
    /// 对它无效）。而 Arm/Disarm/IsBlocked 此前只出现在 NVENC 路径 ⇒ MFT 崩一次，下次启动还会
    /// 走进同一条路再崩一次，永远不自愈；哨兵的设计目标本来含 MFT/QSV。</para>
    /// <para>另一半是本轮顺手发现的：被禁用的后端再也不会 Arm/Disarm，于是哨兵文件永远没人删
    /// ⇒ 一次偶发崩溃 = <b>永久</b>关掉硬件编码，重启也不恢复（界面上的"硬件编码"复选框
    /// 已于 2026-09-18 按用户指令移除，连手动复位的路都没有了）。这与本文件另一处
    /// "一次偶发失败 → 全局永久退化"是同一种病，只是它跨进程存活、更难发现。</para>
    /// </summary>
    static void Test_MftCrashSentinel_IsWired()
    {
        var fePath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"));
        var sentPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "EncoderCrashSentinel.cs"));
        if (fePath is null || sentPath is null)
        {
            Skip("MFT 崩溃哨兵与自愈", "未找到 FormatEncoders.cs / EncoderCrashSentinel.cs");
            return;
        }
        var fe = CodeOnly(fePath);
        var sent = CodeOnly(sentPath);
        int N(string t, string n) => t.Split(n, StringSplitOptions.None).Length - 1;

        Assert("MFT 路径 Arm + Disarm + IsBlocked 三件齐全",
            fe.Contains("EncoderCrashSentinel.Arm(\"MFT\")")
            && fe.Contains("EncoderCrashSentinel.Disarm(\"MFT\")")
            && fe.Contains("EncoderCrashSentinel.IsBlocked(\"MFT\")"));
        Assert("哨兵 key 统一 \"MFT\"（QSV_MFT 与 MFT 是同一条原生实现，拆两个 key 会各崩各禁）",
            N(fe, "Sentinel.Arm(\"MFT\")") == 1 && !fe.Contains("Arm(tag") && !fe.Contains("Arm(\"QSV_MFT\")"),
            $"Arm(\"MFT\") {N(fe, "Sentinel.Arm(\"MFT\")")} 处");
        Assert("Disarm 只一处且在 finally 里（异常路径也必须解除）",
            N(fe, "EncoderCrashSentinel.Disarm(\"MFT\")") == 1);
        Assert("NVENC 仍是另一个独立 key（MFT 被禁不牵连它）",
            N(fe, "IsBlocked(\"NVENC\")") >= 3 && N(fe, "IsBlocked(\"MFT\")") == 1,
            $"NVENC {N(fe, "IsBlocked(\"NVENC\")")} 处 / MFT {N(fe, "IsBlocked(\"MFT\")")} 处");

        // 自愈：崩溃残留只能禁用一次启动，且一次成功的原生返回就忘掉它
        Assert("IsBlocked 读后即消费哨兵文件（否则被禁后端永无复位路径）",
            sent.Contains("s_consumedThisSession.Add(backend)") && sent.Contains("File.Delete(p)"));
        Assert("一次成功的原生返回即解除会话级禁用",
            sent.Contains("s_consumedThisSession.Remove(backend)"));
        Assert("ClearAll 连会话级记一起清（手动复位不许只删文件）",
            sent.Contains("s_consumedThisSession.Clear()"));
    }

    /// <summary>
    /// OCR 语言标签映射（ONNX 短标签 → Windows OCR BCP-47）。
    ///
    /// 2026-09-15：原映射表缺 ko / ja / nl / pl / th / vi / ar，这些 Id 会落到
    /// <c>_ =&gt; null</c>，随后被默认成 zh-Hans-CN —— 即<b>用中文引擎去识别其他语言</b>。
    /// 这类"映射表缺项"不会抛异常、不会报警，只表现为识别率归零，
    /// 因此必须用断言把每个受支持的 Id 钉住，防止日后再次漂移。
    /// </summary>
    static void Test_OcrLanguageMapping_KnownIds()
    {
        // 与 OcrLanguages.OnnxLanguages 的 Id 一一对齐
        Assert("语言映射: ja → ja-JP", MultiOcrService.MapLangToBcp47("ja") == "ja-JP");
        Assert("语言映射: ko → ko-KR（修复前落到 null 被当成中文）", MultiOcrService.MapLangToBcp47("ko") == "ko-KR");
        Assert("语言映射: ch → zh-Hans-CN", MultiOcrService.MapLangToBcp47("ch") == "zh-Hans-CN");
        Assert("语言映射: en → en-US", MultiOcrService.MapLangToBcp47("en") == "en-US");
        Assert("语言映射: nl → nl-NL", MultiOcrService.MapLangToBcp47("nl") == "nl-NL");
        Assert("语言映射: pl → pl-PL", MultiOcrService.MapLangToBcp47("pl") == "pl-PL");
        Assert("语言映射: th → th-TH", MultiOcrService.MapLangToBcp47("th") == "th-TH");
        Assert("语言映射: vi → vi-VN", MultiOcrService.MapLangToBcp47("vi") == "vi-VN");
        Assert("语言映射: ar → ar-SA", MultiOcrService.MapLangToBcp47("ar") == "ar-SA");

        // zh-en 是 PP-OCRv6「中英混合」的 Id，不是合法 BCP-47 ——
        // 必须被转换，绝不能落进"含 '-' 则原样返回"分支。
        // ⚠ 但路由器并不直接调本函数：主选/兜底两条路都走 LangForWindows（见下一个用例），
        //   那里 zh-en 是原样递给 OcrService 的双引擎分派值。改任何一侧都要核对另一侧。
        Assert("语言映射: zh-en → zh-Hans-CN（不得原样返回）", MultiOcrService.MapLangToBcp47("zh-en") == "zh-Hans-CN");

        // 空值返回 null，交由调用方走默认语言逻辑
        Assert("语言映射: 空 → null", MultiOcrService.MapLangToBcp47("") is null);
    }

    /// <summary>
    /// 主选 Windows OCR 时语言标签的归一化。
    /// <para>历史缺陷：映射只写在兜底分支里，主选路径把设置中的 ONNX 短码原样传给
    /// <c>OcrService</c> ⇒ <c>TryCreateFromLanguage("ch")</c> 失败，用户看到一句
    /// "无法创建 OCR 引擎（请安装语言包）"，而真实原因只是标签形态不对（zh-Hans-CN 明明装着）。
    /// 真机实测复现过。</para>
    /// </summary>
    static void Test_OcrLangForWindows()
    {
        Assert("短码 ch 在主选路径也要映射", MultiOcrService.LangForWindows("ch") == "zh-Hans-CN");
        Assert("短码 ko → ko-KR", MultiOcrService.LangForWindows("ko") == "ko-KR");
        Assert("已是 BCP-47 的原样保留", MultiOcrService.LangForWindows("zh-Hant-TW") == "zh-Hant-TW");
        // "zh-en" 由 OcrService.RunOcrAsync 在建引擎之前拦截（中英双引擎合并），
        // 原样传下去才有中英混排可救；这里若改成映射，会把那条分支彻底封死。
        Assert("zh-en 原样下发，保住 OcrService 的双引擎分支", MultiOcrService.LangForWindows("zh-en") == "zh-en");
        Assert("空 → null（走默认语言）", MultiOcrService.LangForWindows("") is null);

        // 配对判据：zh-en 之所以能原样下发，前提是 OcrService 在【建引擎之前】把它分派给
        // 中英双引擎合并分支。谁删掉那条拦截，这里必须红 —— 否则 "zh-en" 会直接撞上
        // TryCreateFromLanguage 失败，而 LangForWindows 的注释就成了假话。
        var ocrSvc = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "OcrService.cs"));
        if (ocrSvc is null)
        {
            Skip("OcrService 的 zh-en 拦截分支", "未找到 OcrService.cs");
            return;
        }
        var code = string.Join("\n", File.ReadAllLines(ocrSvc)
            .Where(l => !l.TrimStart().StartsWith("//")));   // 滤注释：说明文字会命中关键词断言
        Assert("OcrService 在建引擎之前拦截 zh-en（双引擎分支在）",
            code.Contains("languageTag == \"zh-en\"") && code.Contains("RunMixedLanguageOcrAsync"),
            "删掉这条拦截就不该再原样下发 zh-en");
    }

    /// <summary>兜底判据：什么时候 ONNX 的结果"不可信"。</summary>
    static void Test_OcrResultTrustworthy()
    {
        static OcrResult R(string? text, string? err = null) =>
            new() { Text = text ?? "", Error = err };

        Assert("空文本不可信", !MultiOcrService.IsResultTrustworthy(R(""), "ch"));
        Assert("全空白不可信", !MultiOcrService.IsResultTrustworthy(R("  \n "), "ch"));
        Assert("带 error 不可信", !MultiOcrService.IsResultTrustworthy(R("有字", "崩了"), "ch"));
        Assert("正常中文结果可信", MultiOcrService.IsResultTrustworthy(R("你好世界"), "ch"));
        // 韩文整页被误认成汉字/拉丁（实测 "E三\n是"，0/15 字符）—— 非空但一个谚文都没有 ⇒ 必须兜底
        Assert("请求韩文而结果无谚文 ⇒ 不可信",
            !MultiOcrService.IsResultTrustworthy(R("E\u4e09\n\u662f"), "ko"));
        Assert("请求韩文且结果含谚文 ⇒ 可信",
            MultiOcrService.IsResultTrustworthy(R("\uc548\ub155\ud558\uc138\uc694"), "ko"));
        // 反证：同样这批汉字在请求中文时是正常结果，不许误触发兜底
        Assert("请求中文时同样的汉字不算乱码",
            MultiOcrService.IsResultTrustworthy(R("E\u4e09\n\u662f"), "ch"));
        Assert("只按谚文码点判存在，不按字数猜",
            MultiOcrService.ContainsHangul("\uc548") && !MultiOcrService.ContainsHangul("\u4e09\u662f"));
    }

    /// <summary>
    /// 主引擎超时后，Windows OCR 兜底必须真的拿到一份新的时间预算。
    /// <para>历史缺陷（一次活了一个月的静默失效）：兜底复用主引擎那份【已经过期】的 token ⇒
    /// <c>RunWithTimeout</c> 一进去就发现 delay 已完成，当场返回"OCR 引擎超时"，
    /// Windows OCR 一毫秒都没跑到。旧代码里"超时之后由 Windows OCR 兜底"这句注释
    /// 从来没有兑现过。真机探针实测：ONNX 基线 753ms，预算压到 376ms ⇒ 返回超时错、
    /// 文本为空、FallbackFrom 为空；同一张图 Windows OCR 只要 308ms 就出全文。</para>
    /// </summary>
    static void Test_OcrTimeoutStillReachesFallbackEngine()
    {
        var fallbackCalls = 0;
        var hang = new FakeOcrEngine(OcrEngineType.OnnxGpu, async ct =>
        {
            await Task.Delay(30_000, ct);   // 诚实：取消要传出去，不许吞掉再返回文本
            return new OcrResult { Text = "" };
        });
        var win = new FakeOcrEngine(OcrEngineType.WindowsOcr, async ct =>
        {
            ct.ThrowIfCancellationRequested();        // 诚实：拿到过期 token 就该失败而不是交卷
            Interlocked.Increment(ref fallbackCalls);
            await Task.Delay(10, ct);
            return new OcrResult { Text = "\u515c\u5e95\u6587\u672c" };   // 兜底文本
        });

        MultiOcrService.UseEnginesForTest(hang, win,
            TimeSpan.FromMilliseconds(80), TimeSpan.FromSeconds(5));
        try
        {
            var r = MultiOcrService.RecognizeAsync(new byte[4], 1, 1, "zh-Hans-CN")
                .GetAwaiter().GetResult();
            Assert("超时后 Windows OCR 真的被调用（修复前恒为 0）", fallbackCalls == 1, $"calls={fallbackCalls}");
            Assert("超时兜底的文本被采纳", r.Text == "\u515c\u5e95\u6587\u672c", r.Text);
            Assert("兜底结果标了 FallbackFrom", r.FallbackFrom == "OnnxGpu", r.FallbackFrom ?? "-");
            Assert("兜底成功时不再报超时", string.IsNullOrEmpty(r.Error), r.Error ?? "-");
        }
        finally { ResetOcrBudgets(); }
    }

    /// <summary>ONNX 吐出"非空但明显不是请求语言"的结果时也要兜底（旧判据只认空文本）。</summary>
    static void Test_OcrHangulGarbageTriggersFallback()
    {
        var fallbackCalls = 0;
        var onnx = new FakeOcrEngine(OcrEngineType.OnnxGpu, _ =>
            Task.FromResult(new OcrResult { Text = "E\u4e09\n\u662f" }));   // 实测韩文整页输出
        var win = new FakeOcrEngine(OcrEngineType.WindowsOcr, _ =>
        {
            Interlocked.Increment(ref fallbackCalls);
            return Task.FromResult(new OcrResult { Text = "\uc548\ub155\ud558\uc138\uc694" });
        });

        MultiOcrService.UseEnginesForTest(onnx, win,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        try
        {
            var ko = MultiOcrService.RecognizeAsync(new byte[4], 1, 1, "ko").GetAwaiter().GetResult();
            Assert("请求韩文拿到汉字乱码 ⇒ 走兜底", fallbackCalls == 1, $"calls={fallbackCalls}");
            Assert("兜底文本被采纳", ko.Text == "\uc548\ub155\ud558\uc138\uc694", ko.Text);

            fallbackCalls = 0;
            var ch = MultiOcrService.RecognizeAsync(new byte[4], 1, 1, "ch").GetAwaiter().GetResult();
            Assert("反证：请求中文时同一批汉字不许误触发兜底",
                fallbackCalls == 0 && ch.Text == "E\u4e09\n\u662f", $"calls={fallbackCalls} text={ch.Text}");
        }
        finally { ResetOcrBudgets(); }
    }

    /// <summary>用户取消必须报"已取消"，不能被预算逻辑误报成"引擎 N 秒未返回"。</summary>
    static void Test_OcrUserCancelIsNotReportedAsTimeout()
    {
        var hang = new FakeOcrEngine(OcrEngineType.OnnxGpu, async ct =>
        {
            await Task.Delay(30_000, ct);   // 诚实：取消要传出去，不许吞掉再返回文本
            return new OcrResult { Text = "" };
        });
        var win = new FakeOcrEngine(OcrEngineType.WindowsOcr, _ =>
            Task.FromResult(new OcrResult { Text = "\u4e0d\u8be5\u88ab\u7528\u5230" }));

        MultiOcrService.UseEnginesForTest(hang, win,
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        try
        {
            using var cts = new CancellationTokenSource(30);
            OcrResult r;
            try
            {
                r = MultiOcrService.RecognizeAsync(new byte[4], 1, 1, "zh-Hans-CN", cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Assert("取消不抛给调用方（App 层只能显示英文原文）", false, "TaskCanceledException escaped");
                return;
            }
            Assert("取消返回的是 OcrResult 而不是异常", r.Error == "OCR 识别已取消", r.Error ?? "-");
            Assert("取消不被误报成超时",
                r.Error is not null && !r.Error.Contains("未返回") && !r.Error.Contains("超时"),
                r.Error ?? "-");
        }
        finally { ResetOcrBudgets(); }
    }

    static void ResetOcrBudgets()
    {
        MultiOcrService.OcrTimeout = TimeSpan.FromSeconds(45);
        MultiOcrService.FallbackTimeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>只驱动路由语义的假引擎（不碰模型、不碰系统 OCR）。</summary>
    private sealed class FakeOcrEngine : IOcrEngine
    {
        private readonly Func<CancellationToken, Task<OcrResult>> _body;
        public FakeOcrEngine(OcrEngineType type, Func<CancellationToken, Task<OcrResult>> body)
        {
            Info = new OcrEngineInfo($"fake-{type}", OcrEngineMode.Cpu, true, type, "test");
            _body = body;
        }
        public OcrEngineInfo Info { get; }
        public Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h,
            string? lang = null, CancellationToken ct = default) => _body(ct);
    }


    // ═══════════════════════════════════════
    //  PixelOps 测试
    // ═══════════════════════════════════════

    static void Test_FixAlphaChannel_AllZero()
    {
        var pixels = new byte[16]; // 4 pixels, all zero
        PixelOps.FixAlphaChannel(pixels);
        bool ok = pixels[3] == 0xFF && pixels[7] == 0xFF && pixels[11] == 0xFF && pixels[15] == 0xFF;
        Assert("FixAlphaChannel: 全零 → alpha=0xFF", ok);
    }

    static void Test_FixAlphaChannel_PreservesRgb()
    {
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 128 };
        PixelOps.FixAlphaChannel(pixels);
        bool ok = pixels[0] == 10 && pixels[1] == 20 && pixels[2] == 30 && pixels[3] == 0xFF
               && pixels[4] == 40 && pixels[5] == 50 && pixels[6] == 60 && pixels[7] == 0xFF;
        Assert("FixAlphaChannel: RGB 通道不受影响", ok);
    }

    static void Test_FixAlphaChannel_LargeBuffer()
    {
        // 4K 大小缓冲区，验证 SIMD 路径正确性
        var pixels = new byte[3840 * 2160 * 4];
        new Random(42).NextBytes(pixels);
        var copy = (byte[])pixels.Clone();
        PixelOps.FixAlphaChannel(pixels);

        bool ok = true;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] != copy[i] || pixels[i + 1] != copy[i + 1] || pixels[i + 2] != copy[i + 2])
            { ok = false; break; }
            if (pixels[i + 3] != 0xFF) { ok = false; break; }
        }
        Assert("FixAlphaChannel: 4K 缓冲区 SIMD 路径正确", ok);
    }

    /// <summary>
    /// 用【生产实现】<see cref="PixelOps.ConvertFloatToHalfRow"/> 转换一整行。
    /// 档位由 count 决定：count≥16 且 CPU 支持 → AVX2；否则 count≥8 且支持 SSE4.1 → SSE4.1；
    /// 否则通用标量档。
    /// </summary>
    static ushort[] ConvertRowViaProduction(float[] src)
    {
        var dst = new ushort[src.Length];
        unsafe
        {
            fixed (float* s = src)
            fixed (ushort* d = dst)
                PixelOps.ConvertFloatToHalfRow(s, d, src.Length);
        }
        return dst;
    }

    /// <summary>
    /// 生产 <c>ConvertFloatToHalfRow</c> 在给定 count 下是否走 SIMD「无分支简化」档
    /// （subnormal 只剩符号位 / 溢出饱和到 inf）。count 不足门槛或非 x86 时走通用 BCL 档，
    /// 语义不同 —— 边界用例的期望值必须按档位取，否则会在别的机器上假红。
    /// </summary>
    static bool UsesSimdHalfSimplification(int count)
        => (System.Runtime.Intrinsics.X86.Avx2.IsSupported && count >= 16)
        || (System.Runtime.Intrinsics.X86.Sse41.IsSupported && count >= 8);

    static void Test_ConvertFloatToHalf_Roundtrip()
    {
        // ═══ 被测对象是生产实现 PixelOps.ConvertFloatToHalfRow（PixelOps.cs:363，
        // 手写 AVX2 / SSE4.1 / 通用标量三层 + 手写 RNE 进位），BCL 的 (Half)v 在这里
        // 只是【外部参照】。原用例全程只调 BCL，生产代码一次都没执行 ⇒ 假绿。 ═══
        // n=32 → AVX2 档整批（4 轮 8-wide，无尾块）；n=17 → AVX2 档 + 1 个尾块标量；
        // n=6  → 两条 SIMD 档的 count 门槛都不满足 → 通用标量档。
        float[] vals =
        [
            0f, -0f, 1f, -1f, 0.5f, 100f, 0.001f, -0.001f,
            1f / 16384f,                    // 2^-14 = half 最小正规数（SIMD 档 e==1，仍走正常路径）
            1f / 8192f,                     // 2^-13
            65504f, -65504f,                // half 最大有限值
            1f + 1f / 2048f,                // RNE：正好半程，结果最低位 0 → 舍
            1f + 3f / 2048f,                // RNE：正好半程，结果最低位 1 → 进位（手写进位的要害）
            1f + 1f / 4096f,                // RNE：不足半程 → 舍
            -2.71828f,
            3.14159f,                       // ← 索引 16：n=17 时落进尾块标量的那个元素
            123.456f, 2048f, 0.25f, 7.5f, 32000f, -45.25f,
            1.9999f, 512f, 3.5f, 1.25f, 2.5f, -0.125f, 6.25f,
            float.PositiveInfinity, float.NegativeInfinity,
        ];

        foreach (int n in new[] { 32, 17, 6 })
        {
            var src = new float[n];
            Array.Copy(vals, src, n);
            var got = ConvertRowViaProduction(src);
            string tier = n == 32 ? "AVX2 整批" : n == 17 ? "AVX2 + 尾块标量" : "通用标量档";

            bool ok = true;
            string first = "";
            for (int i = 0; i < n; i++)
            {
                ushort exp = (ushort)BitConverter.HalfToInt16Bits((Half)src[i]);   // 外部参照
                if (got[i] != exp)
                {
                    ok = false;
                    first = $"i={i} 输入 {src[i]} 生产 0x{got[i]:X4} 参照 0x{exp:X4}";
                    break;
                }
            }
            Assert($"ConvertFloatToHalfRow(n={n}/{tier}): 逐元素等于 BCL 参照", ok, first);
        }
    }

    static void Test_ConvertFloatToHalf_SpecialValues()
    {
        // 文档承诺的三条边界，同样只经【生产实现】走一遍（原用例只调 BCL ⇒ 什么都没测）。
        // 期望值按档位取：SIMD 档做"无分支简化"，通用档退回 BCL 语义（见 helper 注释）。
        const int n = 32;
        bool simd = UsesSimdHalfSimplification(n);

        // ① ±0 → 只剩符号位（两档一致：BCL 也给出 0x0000 / 0x8000）
        var zeros = new float[n];
        for (int i = 0; i < n; i++) zeros[i] = (i & 1) == 0 ? 0f : -0f;
        var z = ConvertRowViaProduction(zeros);
        bool zeroOk = true;
        for (int i = 0; i < n; i++)
            if (z[i] != ((i & 1) == 0 ? (ushort)0x0000 : (ushort)0x8000)) { zeroOk = false; break; }
        Assert("ConvertFloatToHalf: ±0 → 0x0000 / 0x8000", zeroOk, $"实际 0x{z[0]:X4} / 0x{z[1]:X4}");

        // ② subnormal（|v| < 2⁻¹⁴ ≈ 6.1e-5）→ SIMD 档只剩符号位；通用档 = BCL 的真 half subnormal
        var sub = new float[n];
        for (int i = 0; i < n; i++) sub[i] = (i & 1) == 0 ? 3e-5f : -3e-5f;
        var s = ConvertRowViaProduction(sub);
        bool subOk = true; string subFirst = "";
        for (int i = 0; i < n; i++)
        {
            ushort exp = simd
                ? (ushort)((BitConverter.SingleToInt32Bits(sub[i]) >> 16) & 0x8000)
                : (ushort)BitConverter.HalfToInt16Bits((Half)sub[i]);
            if (s[i] != exp)
            {
                subOk = false;
                subFirst = $"i={i} 输入 {sub[i]} 生产 0x{s[i]:X4} 期望 0x{exp:X4}（{(simd ? "SIMD 简化档" : "通用档")}）";
                break;
            }
        }
        Assert("ConvertFloatToHalf: |v|<2⁻¹⁴ 的 subnormal 只剩符号位（文档承诺的简化）", subOk, subFirst);

        // ③ 有限溢出 → 0x7C00（±inf）。两档一致：BCL 对 >65520 的有限值同样给 inf。
        var big = new float[n];
        for (int i = 0; i < n; i++) big[i] = (i & 1) == 0 ? 70000f : -1e30f;
        var b = ConvertRowViaProduction(big);
        bool bigOk = true; string bigFirst = "";
        for (int i = 0; i < n; i++)
        {
            ushort exp = (ushort)(((BitConverter.SingleToInt32Bits(big[i]) >> 16) & 0x8000) | 0x7C00);
            if (b[i] != exp) { bigOk = false; bigFirst = $"i={i} 输入 {big[i]} 生产 0x{b[i]:X4} 期望 0x{exp:X4}"; break; }
        }
        Assert("ConvertFloatToHalf: 有限溢出饱和到 inf（0x7C00 / 0xFC00）", bigOk, bigFirst);

        // ④ ±inf 保持 inf（与 ③ 同一条位路径，但输入是真正的 inf）
        var inf = new float[n];
        for (int i = 0; i < n; i++) inf[i] = (i & 1) == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        var f = ConvertRowViaProduction(inf);
        Assert("ConvertFloatToHalf: ±inf → 0x7C00 / 0xFC00",
            f[0] == 0x7C00 && f[1] == 0xFC00, $"实际 0x{f[0]:X4} / 0x{f[1]:X4}");
        // NaN 不在此比对：SIMD 档按设计饱和成 inf，BCL 保留 NaN 载荷，二者本就不等价。
    }

    static void Test_BgraToScrgbLinear_KnownValues()
    {
        // BGRA(0,0,0,255) → linear(0,0,0,1)
        var bgra = new byte[] { 0, 0, 0, 255 };
        var linear = PixelOps.BgraToScrgbLinearFast(bgra, 1, 1);
        bool ok = linear[0] == 0f && linear[1] == 0f && linear[2] == 0f && Math.Abs(linear[3] - 1f) < 0.01f;
        Assert("BgraToScrgbLinear: 黑色 → linear(0,0,0,1)", ok);
    }

    // ═══════════════════════════════════════
    //  ToneMapper 测试
    // ═══════════════════════════════════════

    static void Test_ToneMapper_Reinhard_BlackStaysBlack()
    {
        var pixels = new float[] { 0f, 0f, 0f, 1f }; // 黑色 RGBA
        ToneMapper.ReinhardToneMapCpu(pixels, 1, 1);
        bool ok = pixels[0] == 0f && pixels[1] == 0f && pixels[2] == 0f;
        Assert("ToneMapper.Reinhard: 黑色保持黑色", ok);
    }

    static void Test_ToneMapper_Hable_WhiteClamps()
    {
        var pixels = new float[] { 100f, 100f, 100f, 1f }; // 超亮白色
        ToneMapper.HableToneMapCpu(pixels, 1, 1);
        bool ok = pixels[0] <= 1f && pixels[1] <= 1f && pixels[2] <= 1f
               && pixels[0] > 0.5f; // 应该接近白色
        Assert("ToneMapper.Hable: 超亮值钳制到 [0,1]", ok);
    }

    static void Test_ToneMapper_FusedKernel_MatchesSeparate()
    {
        // 验证融合内核 FloatToSRgbBytes 与分步处理结果一致
        var rng = new Random(123);
        int w = 64, h = 64;
        var hdr = new float[w * h * 4];
        for (int i = 0; i < hdr.Length; i += 4)
        {
            hdr[i] = (float)(rng.NextDouble() * 5);     // R
            hdr[i + 1] = (float)(rng.NextDouble() * 5); // G
            hdr[i + 2] = (float)(rng.NextDouble() * 5); // B
            hdr[i + 3] = 1f;                             // A
        }

        var p = new ToneMappingParams { Mode = ToneMapMode.Hable };
        var fused = ToneMapper.FloatToSRgbBytes(hdr, w, h, p);

        // 分步: copy → tone map → gamma → swizzle
        var copy = new float[hdr.Length];
        Array.Copy(hdr, copy, hdr.Length);
        ToneMapper.ApplyToneMapping(copy, w, h, p);
        ToneMapper.LinearToSRgb(copy);
        var separate = new byte[w * h * 4];
        for (int pi = 0; pi < w * h; pi++)
        {
            int i = pi * 4;
            separate[i] = (byte)Math.Clamp((int)(copy[i + 2] * 255f + 0.5f), 0, 255);
            separate[i + 1] = (byte)Math.Clamp((int)(copy[i + 1] * 255f + 0.5f), 0, 255);
            separate[i + 2] = (byte)Math.Clamp((int)(copy[i] * 255f + 0.5f), 0, 255);
            separate[i + 3] = (byte)Math.Clamp((int)(copy[i + 3] * 255f + 0.5f), 0, 255);
        }

        int maxDiff = 0;
        for (int i = 0; i < fused.Length; i++)
            maxDiff = Math.Max(maxDiff, Math.Abs(fused[i] - separate[i]));

        Assert($"ToneMapper.FusedKernel: 与分步结果最大差异={maxDiff} (允许≤1)", maxDiff <= 1);
    }

    static void Test_ToneMapper_SwizzleOrder()
    {
        // 验证 RGBA→BGRA swizzle: 纯红 HDR → BGRA 中 B=0,G=0,R=255
        var hdr = new float[] { 10f, 0f, 0f, 1f }; // 纯红 HDR
        var p = new ToneMappingParams { Mode = ToneMapMode.Reinhard };
        var bytes = ToneMapper.FloatToSRgbBytes(hdr, 1, 1, p);
        // BGRA: [B, G, R, A]
        bool ok = bytes[0] < 10 && bytes[1] < 10 && bytes[2] > 100 && bytes[3] > 200;
        Assert("ToneMapper.Swizzle: 纯红 → BGRA[2] 高", ok);
    }

    static void Test_ToneMapper_SrgbLut_Accuracy()
    {
        // 验证 LinearToSrgbLut (4096 项) 与精确 MathF.Pow 误差 < 0.5 LSB (8-bit)
        float maxErr = 0;
        for (int i = 0; i <= 20000; i++)
        {
            float lin = i / 20000f;
            float lut = ToneMapper.LinearToSrgbLut(lin);
            float exact = lin <= 0.0031308f ? 12.92f * lin : 1.055f * MathF.Pow(lin, 1f / 2.4f) - 0.055f;
            maxErr = Math.Max(maxErr, Math.Abs(lut - exact));
        }
        // 8-bit 量化区间 = 1/255 ≈ 0.00392; LUT 误差应远小于半 LSB
        bool ok = maxErr < 0.001f;
        Assert($"ToneMapper.LUT: 最大误差 {maxErr:F6} < 0.001 (8-bit LSB={1f/255f:F6})", ok);
    }

    static void Test_ToneMapper_SrgbLut_Endpoints()
    {
        // LUT 端点: 黑→0, 白→1
        float black = ToneMapper.LinearToSrgbLut(0f);
        float white = ToneMapper.LinearToSrgbLut(1f);
        Assert($"ToneMapper.LUT: 黑={black:F4}≈0 白={white:F4}≈1", black < 0.001f && Math.Abs(white - 1f) < 0.001f);
    }

    // ═══════════════════════════════════════
    //  ColorProfileProvider 测试
    // ═══════════════════════════════════════

    static void Test_SrgbIcc_ValidHeader()
    {
        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        // ICC 文件头: bytes 36-39 = 'acsp' 签名
        bool ok = icc.Length > 128
            && icc[36] == (byte)'a' && icc[37] == (byte)'c'
            && icc[38] == (byte)'s' && icc[39] == (byte)'p';
        Assert("sRGB ICC: 有效 'acsp' 签名", ok);
    }

    static void Test_SrgbIcc_MinimumSize()
    {
        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        bool ok = icc.Length >= 256; // 有效 ICC 至少 256 字节
        Assert($"sRGB ICC: 大小 {icc.Length} >= 256", ok);
    }

    static void Test_SrgbIcc_Cached()
    {
        var icc1 = ColorProfileProvider.GetDefaultSRgbIcc();
        var icc2 = ColorProfileProvider.GetDefaultSRgbIcc();
        bool ok = ReferenceEquals(icc1, icc2); // 缓存应返回同一实例
        Assert("sRGB ICC: 缓存返回同一实例", ok);
    }

    // ═══════════════════════════════════════
    //  PixelOps 扩展测试
    // ═══════════════════════════════════════

    static void Test_DownsampleToGray_Dimensions()
    {
        // 验证降采样输出尺寸正确
        var bgra = new byte[640 * 480 * 4];
        new Random(7).NextBytes(bgra);
        var gray = PixelOps.DownsampleToGraySimd(bgra, 640, 480, 160, 120);
        Assert($"DownsampleToGray: 输出尺寸 {gray.Length} == {160 * 120}", gray.Length == 160 * 120);
    }

    static void Test_DownsampleToGray_UniformColor()
    {
        // 纯白 BGRA → 灰度应接近 255
        int w = 100, h = 100;
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 255; bgra[i + 1] = 255; bgra[i + 2] = 255; bgra[i + 3] = 255; }
        var gray = PixelOps.DownsampleToGraySimd(bgra, w, h, 10, 10);
        bool ok = gray.All(v => v >= 250); // 允许微小舍入误差
        Assert("DownsampleToGray: 纯白 → 灰度≈255", ok);
    }

    static void Test_ComputeEdgeProjections_FlatImage()
    {
        // 平坦图像（无边缘）→ 梯度投影应全为 0
        int w = 64, h = 64;
        var gray = new byte[w * h];
        Array.Fill(gray, (byte)128);
        var hEdges = new float[h];
        var vEdges = new float[w];
        PixelOps.ComputeEdgeProjectionsSimd(gray, w, h, hEdges, vEdges);
        bool ok = hEdges.All(v => v == 0f) && vEdges.All(v => v == 0f);
        Assert("ComputeEdgeProjections: 平坦图像 → 零梯度", ok);
    }

    static void Test_ComputeEdgeProjections_EdgeDetection()
    {
        // 左半黑右半白 → 垂直边缘在 x=32
        // hEdges[y] = 水平投影 (检测 x 方向梯度) → 每行都有大值
        // vEdges[x] = 垂直投影 (检测 y 方向梯度) → 应为 0 (无 y 方向变化)
        int w = 64, h = 64;
        var gray = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                gray[y * w + x] = x < 32 ? (byte)0 : (byte)255;
        var hEdges = new float[h];
        var vEdges = new float[w];
        PixelOps.ComputeEdgeProjectionsSimd(gray, w, h, hEdges, vEdges);
        // hEdges 应全部 > 0 (每行在 x=32 处有 255 的跳变, 归一化后 ≈ 255/64 ≈ 3.98)
        // vEdges 应全部 == 0 (列内无变化)
        bool ok = hEdges.All(v => v > 3f) && vEdges.All(v => v == 0f);
        Assert("ComputeEdgeProjections: 垂直边缘 → hEdges 大, vEdges 零", ok);
    }

    // ═══════════════════════════════════════
    //  AnnotationManager 测试
    // ═══════════════════════════════════════

    static void Test_AnnotationManager_AddLayer()
    {
        var mgr = new AnnotationManager();
        var layer = new RectangleLayer
        {
            X = 10, Y = 10, Width = 90, Height = 90,
            Style = new BrushStyle { StrokeColor = new Color4(1, 0, 0, 1), StrokeWidth = 2f }
        };
        mgr.AddLayer(layer);
        bool ok = mgr.Layers.Count == 1 && mgr.Layers[0].Type == ShapeType.Rectangle;
        Assert("AnnotationManager.AddLayer: 添加矩形图层", ok);
    }

    static void Test_AnnotationManager_UndoRedo()
    {
        var mgr = new AnnotationManager();
        var layer = new EllipseLayer
        {
            CenterX = 25, CenterY = 25, RadiusX = 25, RadiusY = 25,
            Style = new BrushStyle { StrokeColor = new Color4(0, 1, 0, 1), StrokeWidth = 3f }
        };
        mgr.AddLayer(layer);
        Assert("AnnotationManager: 添加后 CanUndo=true", mgr.CanUndo);

        mgr.Undo();
        bool undone = mgr.Layers.Count == 0 && mgr.CanRedo;
        Assert("AnnotationManager.Undo: 图层被移除 + CanRedo=true", undone);

        mgr.Redo();
        bool redone = mgr.Layers.Count == 1 && !mgr.CanRedo;
        Assert("AnnotationManager.Redo: 图层恢复 + CanRedo=false", redone);
    }

    static void Test_AnnotationManager_RemoveLayer()
    {
        var mgr = new AnnotationManager();
        var layer = new TextLayer
        {
            X = 5, Y = 5, Text = "测试文字", FontSize = 16f,
            TextColor = new Color4(1, 1, 1, 1)
        };
        mgr.AddLayer(layer);
        mgr.RemoveLayer(layer.Id);
        bool ok = mgr.Layers.Count == 0;
        Assert("AnnotationManager.RemoveLayer: 删除后图层为空", ok);

        // 撤销删除
        mgr.Undo();
        Assert("AnnotationManager: 撤销删除后图层恢复", mgr.Layers.Count == 1);
    }

    // ═══════════════════════════════════════
    //  FormatEncoders 测试
    // ═══════════════════════════════════════

    static void Test_EncoderRegistry_AllFormatsRegistered()
    {
        // 验证所有 OutputFormat 枚举值都能创建编码器
        var formats = Enum.GetValues<OutputFormat>();
        bool ok = true;
        foreach (var f in formats)
        {
            try
            {
                var enc = EncoderFactory.Create(f);
                if (enc is null) { ok = false; break; }
            }
            catch { ok = false; break; }
        }
        Assert($"EncoderFactory: 所有 {formats.Length} 种格式可创建编码器", ok);
    }

    static void Test_EncoderRegistry_QualityRanges()
    {
        // 验证编码器质量范围合法性 (min <= default <= max)
        var formats = Enum.GetValues<OutputFormat>();
        bool ok = true;
        foreach (var f in formats)
        {
            var enc = EncoderFactory.Create(f);
            var (min, max, def, _) = enc.GetQualityRange();
            if (min > max || def < min || def > max) { ok = false; break; }
        }
        Assert("EncoderFactory: 所有编码器质量范围合法 (min≤default≤max)", ok);

        // ★ 哪些格式【没有质量参数】必须是一张明写的表，而不是"界面上滑块划不动"这种观感。
        //   这两个是无损格式：滑块恒 100、输入框只能填 100 ⇒ App 侧据此把整行收起（#26）。
        //   反向断言同样重要：哪天有人给 PNG 真加了压缩级别之类的可调项，或把某个有损格式的
        //   区间写退化，这里会立刻指出"UI 该不该显示这根滑块"的判断变了。
        var expectFixed = new (OutputFormat F, bool Fixed)[]
        {
            (OutputFormat.PNG, true), (OutputFormat.TIFF, true),
            (OutputFormat.JPEG_LI, false), (OutputFormat.JPEG_XL, false),
            (OutputFormat.JPEG_GAINMAP, false), (OutputFormat.AVIF, false),
            (OutputFormat.WebP, false),
        };
        foreach (var (f, expect) in expectFixed)
            Assert($"质量参数可调性: {f} 的区间{(expect ? "退化为固定值" : "确有可调范围")}",
                EncoderFactory.Create(f).QualityIsFixed == expect);
    }

    static void Test_EncodingSettings_Defaults()
    {
        var s = new EncodingSettings();
        bool ok = s.Format == OutputFormat.PNG
            && s.Quality == 90f
            && !s.HdrOutput
            && s.AvifEncoderKind == AvifEncoderKind.Auto
            && s.ChromaSubsampling == "444";
        Assert("EncodingSettings: 默认值正确", ok);
    }

    // ═══════════════════════════════════════
    //  IccStore 测试
    // ═══════════════════════════════════════

    static void Test_IccStore_AllSpaces_Valid()
    {
        // 验证所有标准 ICC 配置文件有效（大小 > 128 字节）
        var spaces = new[] { "sRGB", "AdobeRGB", "DisplayP3", "BT2020" };
        bool ok = true;
        foreach (var s in spaces)
        {
            var icc = IccStore.GetByName(s);
            if (icc is null || icc.Length < 128) { ok = false; break; }
        }
        Assert("IccStore: 所有标准 ICC 有效", ok);
    }

    static void Test_IccStore_Srgb_HasAcspSignature()
    {
        var icc = IccStore.SRGB;
        // ICC 签名 "acsp" 在偏移 36 处
        bool ok = icc.Length >= 40
            && icc[36] == 'a' && icc[37] == 'c' && icc[38] == 's' && icc[39] == 'p';
        Assert("IccStore: sRGB ICC 签名 'acsp'", ok);
    }

    // ═══════════════════════════════════════
    //  ColorSpaceConverter 测试
    // ═══════════════════════════════════════

    static void Test_ColorSpaceConverter_MatrixRowSum()
    {
        // 验证色域转换矩阵行和 ≈ 1.0 (白色保持白色)
        var matrices = new[]
        {
            ("sRGB→BT.2020", ColorSpaceConverter.SrgbToBt2020),
            ("sRGB→P3", ColorSpaceConverter.SrgbToDisplayP3),
            ("sRGB→AdobeRGB", ColorSpaceConverter.SrgbToAdobeRgb),
        };
        bool ok = true;
        foreach (var (name, m) in matrices)
        {
            // 行和: 第 0 行 R 贡献
            float row0 = m[0, 0] + m[0, 1] + m[0, 2];
            float row1 = m[1, 0] + m[1, 1] + m[1, 2];
            float row2 = m[2, 0] + m[2, 1] + m[2, 2];
            if (Math.Abs(row0 - 1.0f) > 0.01f || Math.Abs(row1 - 1.0f) > 0.01f || Math.Abs(row2 - 1.0f) > 0.01f)
                { ok = false; break; }
        }
        Assert("ColorSpaceConverter: 矩阵行和 ≈ 1.0", ok);
    }

    static void Test_ColorSpaceConverter_NoNegativeValues()
    {
        // 验证矩阵没有负值列（对于 [0,1]^3 输入，输出不应有负值）
        var matrices = new[]
        {
            ColorSpaceConverter.SrgbToBt2020,
            ColorSpaceConverter.SrgbToDisplayP3,
            ColorSpaceConverter.SrgbToAdobeRgb,
        };
        bool ok = true;
        foreach (var m in matrices)
        {
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    if (m[r, c] < -0.01f) { ok = false; break; }
        }
        Assert("ColorSpaceConverter: 矩阵无负值", ok);
    }

    static void Test_ColorSpaceConverter_GetCicpPrimaries()
    {
        // 验证 CICP 主色索引正确
        Assert("CICP BT.2020 → 9", ColorSpaceConverter.GetCicpPrimaries("BT2020") == 9);
        Assert("CICP sRGB → 1", ColorSpaceConverter.GetCicpPrimaries("sRGB") == 1);
        Assert("CICP P3 → 12", ColorSpaceConverter.GetCicpPrimaries("DisplayP3") == 12);
        Assert("CICP AdobeRGB → 1", ColorSpaceConverter.GetCicpPrimaries("AdobeRGB") == 1);
    }

    // ═══════════════════════════════════════
    //  ColorProfileProvider 逻辑测试
    // ═══════════════════════════════════════

    static void Test_ColorProfile_SrgbIcc_Valid()
    {
        // sRGB ICC 应有效
        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        bool ok = icc is { Length: > 128 };
        Assert("ColorProfile: sRGB ICC 有效", ok);
    }

    static void Test_ColorProfile_ResolveColorSpaceTag()
    {
        // 非 ACM 下 System 解析为 sRGB
        var tag = ColorProfileProvider.ResolveColorSpaceTag("System", false, false);
        Assert("ColorProfile: System→sRGB(非ACM)", tag == "sRGB");

        // 已知色域直通
        tag = ColorProfileProvider.ResolveColorSpaceTag("BT2020", false, false);
        Assert("ColorProfile: BT2020→BT2020", tag == "BT2020");
    }

    static void Test_ColorProfile_MapColorSpaceTag()
    {
        Assert("MapColorSpace: sRGB→sRGB", ColorProfileProvider.MapColorSpaceTag("sRGB") == "sRGB");
        Assert("MapColorSpace: BT2020→BT2020", ColorProfileProvider.MapColorSpaceTag("BT2020") == "BT2020");
        Assert("MapColorSpace: DisplayP3→DisplayP3", ColorProfileProvider.MapColorSpaceTag("DisplayP3") == "DisplayP3");
    }

    static void Test_ColorProfile_GetStandardIcc_AllSpaces()
    {
        var spaces = new[] { "sRGB", "BT2020", "DisplayP3", "AdobeRGB" };
        bool ok = true;
        foreach (var s in spaces)
        {
            var icc = ColorProfileProvider.GetStandardIccProfile(s);
            if (icc is null || icc.Length < 128) { ok = false; break; }
        }
        Assert("ColorProfile: 所有标准空间 ICC 有效", ok);
    }

    static void Test_ColorProfile_D50Primaries_Accuracy()
    {
        // 验证 PatchSrgbPrimaries 生成的广色域 ICC primaries 为 D50-adapted 标准值
        // (2026-08-09 修复: 旧 XyToXyzPublic Y=1 无 D50 适应 → P3 红 2.125 错误)
        // 正确 D50-adapted XYZ (s15Fixed16):
        //   DisplayP3 rXYZ ≈ (0.5151, 0.2412, -0.0011)
        //   BT2020    rXYZ ≈ (0.6735, 0.2791, -0.0019)
        //   AdobeRGB  rXYZ ≈ (0.6098, 0.3111, 0.0195)
        var expected = new (string Cs, float Rx, float Ry, float Rz)[]
        {
            ("DisplayP3", 0.5152f, 0.2412f, -0.0011f),
            ("BT2020",    0.6735f, 0.2791f, -0.0019f),
            ("AdobeRGB",  0.6098f, 0.3111f, 0.0195f),
        };
        bool ok = true;
        foreach (var (cs, eRx, eRy, eRz) in expected)
        {
            var icc = ColorProfileProvider.GetStandardIccProfile(cs);
            if (icc is null) { ok = false; Assert($"{cs}: ICC 为 null", false); continue; }
            var (rX, rY, rZ) = ExtractRgbXyz(icc);
            bool match = Math.Abs(rX - eRx) < 0.001f && Math.Abs(rY - eRy) < 0.001f && Math.Abs(rZ - eRz) < 0.001f;
            if (!match) ok = false;
            Assert($"{cs} rXYZ D50: ({rX:F4},{rY:F4},{rZ:F4}) 匹配标准值 ({eRx:F4},{eRy:F4},{eRz:F4})", match);
        }
        Assert("ColorProfile: 广色域 ICC primaries 均为 D50-adapted 标准值", ok);
    }

    static (float X, float Y, float Z) ExtractRgbXyz(byte[] icc)
    {
        int tagCount = icc[128] << 24 | icc[129] << 16 | icc[130] << 8 | icc[131];
        for (int i = 0; i < tagCount; i++)
        {
            int e = 132 + i * 12;
            uint sig = (uint)(icc[e] << 24 | icc[e + 1] << 16 | icc[e + 2] << 8 | icc[e + 3]);
            if (sig == 0x7258595Au) // rXYZ
            {
                int off = icc[e + 4] << 24 | icc[e + 5] << 16 | icc[e + 6] << 8 | icc[e + 7];
                float X = ReadS15F16(icc, off + 8);
                float Y = ReadS15F16(icc, off + 12);
                float Z = ReadS15F16(icc, off + 16);
                return (X, Y, Z);
            }
        }
        return (0, 0, 0);
    }

    static float ReadS15F16(byte[] d, int off)
        => ((d[off] << 24) | (d[off + 1] << 16) | (d[off + 2] << 8) | d[off + 3]) / 65536f;

    // ═══════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════

    /// <summary>显式跳过（环境不满足时使用 —— 绝不用"判 true"伪装通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⚠️ {name} — 跳过: {reason}");
    }

    // ═══════════════════════════════════════
    //  防漂移契约（2026-09-15）
    // ═══════════════════════════════════════

    /// <summary>
    /// P0-D 回归：UI / 工具侧实际流通的每一个色彩空间 Tag，都必须能解析出 XYZ→RGB 矩阵。
    /// <para>历史坑：GetTargetXyzToRgbMatrix 用 ToUpperInvariant 去匹配 "DISPLAY P3"（含空格）
    /// 与 "DCI-P3"（连字符），而全仓实际流通的 Tag 是 "DisplayP3" / "DCI_P3" ⇒ 恒返回 null
    /// ⇒ 像素直通不转换、但 ICC 仍按 P3 嵌入 ⇒ 静默偏色且无任何提示。</para>
    /// 这类"枚举全集覆盖"断言正是能防住该漏项的形态。
    /// </summary>
    static void Test_ColorSpaceTag_MatrixCoverage()
    {
        // 取值来自 MainWindow.ColorManagement.cs / PngCheck.cs / ColorProfileProvider 自身，
        // 即全仓真实流通的标签，而非本测试臆造。
        string[] tags = { "sRGB", "DisplayP3", "DCI_P3", "AdobeRGB", "BT2020" };
        foreach (var tag in tags)
            Assert($"色域矩阵覆盖: {tag} 可解析", ColorProfileProvider.GetTargetXyzToRgbMatrix(tag) is { Length: 9 });

        // ═══ "等价"必须等价在【数值】上 ═══
        // 原来这三条只断 GetTargetXyzToRgbMatrix(...) is { Length: 9 } —— 换成 BT.709 矩阵、
        // 甚至换成任意 9 个乱数都照样绿。基准取 "DisplayP3" 那份，逐元素比（容差 1e-6）。
        static bool SameMatrix(float[]? a, float[]? b, float tol)
            => a is { Length: 9 } && b is { Length: 9 }
               && !a.Where((v, i) => Math.Abs(v - b[i]) > tol).Any();

        var p3 = ColorProfileProvider.GetTargetXyzToRgbMatrix("DisplayP3");
        Assert("色域矩阵覆盖: 'DisplayP3' 基准矩阵可解析", p3 is { Length: 9 });
        foreach (var alias in new[] { "Display P3", "DCI-P3", "displayp3" })
        {
            var got = ColorProfileProvider.GetTargetXyzToRgbMatrix(alias);
            Assert($"色域矩阵覆盖: '{alias}' 与 DisplayP3 逐元素相等（不是只'返回 9 个数'）",
                SameMatrix(p3, got, 1e-6f),
                got is null ? "返回 null" : string.Join(", ", got.Select(v => v.ToString("F6"))));
        }

        // 反向：未知色域仍须返回 null —— 放宽匹配不能把垃圾也放过
        Assert("色域矩阵覆盖: 未知色域返回 null", ColorProfileProvider.GetTargetXyzToRgbMatrix("NotAColorSpace") is null);
    }

    static void Test_NormalizeColorSpaceKey()
    {
        Assert("NormalizeColorSpaceKey: 去空格", ColorProfileProvider.NormalizeColorSpaceKey("Display P3") == "DISPLAYP3");
        Assert("NormalizeColorSpaceKey: 去连字符", ColorProfileProvider.NormalizeColorSpaceKey("DCI-P3") == "DCIP3");
        Assert("NormalizeColorSpaceKey: 去下划线", ColorProfileProvider.NormalizeColorSpaceKey("DCI_P3") == "DCIP3");
        Assert("NormalizeColorSpaceKey: 大小写无关", ColorProfileProvider.NormalizeColorSpaceKey("displayp3") == "DISPLAYP3");
        Assert("NormalizeColorSpaceKey: 空串安全", ColorProfileProvider.NormalizeColorSpaceKey("") == "");
    }

    /// <summary>
    /// 分段 Reinhard 三重约束：SDR 白点保真 f(1)=1、输出不超上界 f≤1、单调不减。
    /// 2026-08-30 曾因 ReinhardMap 分支在 y=1 处非单调而产生"暗环"伪影，此用例防其回归。
    /// </summary>
    static void Test_ToneMapper_SegmentedReinhard_Monotonic()
    {
        const float headroom = 5.0f;   // 典型值：1000 nits 峰值 / 200 nits 纸白
        bool monotonic = true, inRange = true;
        float prev = -1f;
        // y=1 附近加密采样（步长 0.01）—— 断崖正是发生在这里
        for (int i = 0; i <= 400; i++)
        {
            float y = i / 100.0f;                                  // 0 → 4.0
            float f = ToneMapper.SegmentedReinhardMap(y, headroom);
            if (f < prev - 1e-6f) monotonic = false;
            if (f < 0f || f > 1f + 1e-6f) inRange = false;
            prev = f;
        }

        // SDR 白点【及以内】为恒等映射：f(y) = y（y ≤ 1）。
        // 注意：不可把区间延伸到 y>1 —— 那里按设计恒为 1.0（饱和），f(y) ≠ y。
        bool identityBelowWhite = true;
        for (float y = 0.98f; y <= 1.0f + 1e-6f; y += 0.001f)
            if (Math.Abs(ToneMapper.SegmentedReinhardMap(y, headroom) - y) > 1e-5f) identityBelowWhite = false;

        Assert("ToneMapper: 分段 Reinhard 单调不减（无暗环）", monotonic);
        Assert("ToneMapper: 输出恒在 [0,1] 内", inRange);
        Assert("ToneMapper: y≤1 恒等映射（SDR 内容 100% 保真）", identityBelowWhite);
        Assert("ToneMapper: SDR 白点保真 f(1)=1", Math.Abs(ToneMapper.SegmentedReinhardMap(1.0f, headroom) - 1.0f) < 1e-6f);
        Assert("ToneMapper: 高光饱和到 1.0", Math.Abs(ToneMapper.SegmentedReinhardMap(2.0f, headroom) - 1.0f) < 1e-6f);
    }

    /// <summary>
    /// B-8（2026-09-20 许可合规审计）：Google 的两个<b>非官方</b>端点（网页版 <c>client=gtx</c>、
    /// Chrome 划词 <c>client=dict-chrome-ex</c>）必须在开关关闭时<b>一个请求都不发</b>，
    /// 而不是"照样发出去但忽略结果"。
    /// <para>修之前 README 写"只走官方开放平台 API"，而这两个端点一直是默认降级链的一环
    /// ⇒ 声明与实现矛盾；且 OCR 原文（可能含 token / 个人信息）会未经同意外发。</para>
    /// <para>① ② ③ ④ 是<b>行为</b>判据（直接调用取端点的函数拿真值表）；
    /// ⑤ 是源码级判据，防止"函数改好了但没人调用它"—— 这类漂移本仓真实踩过
    /// （有道凭据从未注入，见 MainWindow.xaml.cs 的 2026-09-15 注释）。</para>
    /// </summary>
    static void Test_UnofficialTranslateEndpointsAreOptIn()
    {
        // ① 默认值就是"关"
        var cfg = new LlmConfig();
        Assert("B-8/LlmConfig: 默认不启用非官方端点", !cfg.AllowUnofficialTranslateEndpoints);

        // ② 关 ⇒ 连 URL 都不构造 ⇒ 客观上发不出请求
        var off = TranslationService.UnofficialGoogleEndpoints(false, "你好", "en", null);
        Assert("B-8/开关关闭: 一个端点都不返回", off.Length == 0, $"返回 {off.Length} 条");

        // ③ 开 ⇒ 两条都在，且都是带 client= 的非官方接口
        var on = TranslationService.UnofficialGoogleEndpoints(true, "你好", "en", "zh-CN");
        Assert("B-8/显式开启: 回到两条端点", on.Length == 2, $"返回 {on.Length} 条");
        Assert("B-8/两条都是 client= 形式的非官方接口（不是官方 API 路径）",
            on.All(e => e.url.Contains("client="))
            && on.Count(e => e.url.Contains("translate.googleapis.com")) == 1
            && on.Count(e => e.url.Contains("clients5.google.com")) == 1,
            string.Join(" | ", on.Select(e => e.label)));

        // ④ 原文必须 URL 编码：含 & ? # 的 OCR 文本不得破坏查询串（也就不可能"逃逸"成别的参数）
        //    注：HttpUtility.UrlEncode 输出**小写**十六进制转义（%3f 而非 %3F），故先转小写再比。
        var tricky = TranslationService.UnofficialGoogleEndpoints(true, "a&b?c#d", "en", null);
        Assert("B-8/原文按 URL 编码注入", tricky.Length == 2
            && tricky.All(e => !e.url.Contains("a&b?c#d")
                               && e.url.ToLowerInvariant().Contains("%26")
                               && e.url.ToLowerInvariant().Contains("%3f")
                               && e.url.ToLowerInvariant().Contains("%23")),
            tricky.Length > 0 ? tricky[0].url : "<空>");

        // ⑤ 生产路径必须真的读这个开关（行为判据本身测不到这条）
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "TranslationService.cs"));
        if (path is null) { Skip("B-8/调用侧接线", "未找到 TranslationService.cs"); return; }
        // 逐行扫，不按跨行字面量匹配 —— 仓库源码是 CRLF，任何含 \n 的模式都会静默匹配失败。
        // 命中行要跳过【声明】本身（第一处出现就是 internal static ... UnofficialGoogleEndpoints(），
        // 所以看的是"每个出现点之后的 3 行里有没有读 _config 的那个开关"。
        var lines = File.ReadAllLines(path);
        bool wired = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("UnofficialGoogleEndpoints(")) continue;
            for (int j = i; j < Math.Min(i + 3, lines.Length); j++)
                if (lines[j].Contains("_config.AllowUnofficialTranslateEndpoints")) { wired = true; break; }
            if (wired) break;
        }
        Assert("B-8/TryGoogleMultiEndpointAsync 用 _config 的开关取端点", wired);
        // 端点字面量只能出现在这一个受控函数里：别处再拼一份就绕过了开关
        string src = string.Join("\n", lines);
        int occurrences = src.Split("translate.googleapis.com").Length - 1
                        + src.Split("clients5.google.com").Length - 1;
        Assert("B-8/端点字面量只出现在受控函数内（共 2 处）", occurrences == 2, $"实得 {occurrences} 处");
    }

    /// <summary>
    /// 必应网页版端点（2026-09-21 接入）与 Google 共用同一条 opt-in 闸门，且请求形状要钉住
    /// 当天实测到的三个坑 —— 三个都是"看着像成功"的形态：
    /// ① <c>www.bing.com/ttranslatev3</c> 会 302，跟随重定向时 form body 被丢掉 ⇒ "HTTP 200 + 空 body"，
    ///    与"端点根本没服务"同形（所以必须打在抓凭据页时落地的子域上）；
    /// ② 凭据被拒回的是 <c>HTTP 200 + {"statusCode":205}</c> ⇒ 真状态码在体内，不在头上；
    /// ③ 三个同名 <c>text</c> 参数只翻第一条，另两句**静默丢失**。
    /// <para>响应样本是 2026-09-21 本机实抓的（取证脚本 <c>scripts/probes/bing_free_endpoint_probe.py</c>），
    /// 不是照文档想象出来的形状 —— 这条区别本身就是本轮踩过的坑（见交接 §35.2）。</para>
    /// </summary>
    static void Test_BingWebEndpointShapeAndOptIn()
    {
        var creds = new TranslationService.BingCreds("cn.bing.com", "D5B49C9200000000000000000000ABCD",
            "translator.5023", "1789961309334", "fxAtrZdExampleToken32CharsAAAAA", DateTimeOffset.UtcNow.AddMinutes(50));

        // ① 闸门关 ⇒ 连 URL 都不组装（也就抓不到凭据页，客观上发不出请求）
        Assert("必应/开关关闭: 不组装任何请求",
            TranslationService.BingWebRequest(false, creds, "你好", "en", null) is null);

        // ② 闸门开 ⇒ 打在落地子域上，不是 www.bing.com
        var on = TranslationService.BingWebRequest(true, creds, "你好", "zh-CN", null);
        Assert("必应/URL 用抓凭据时落地的子域，而不是 www.bing.com（否则 302 会丢掉 form body）",
            on is not null && on.Value.url.StartsWith("https://cn.bing.com/ttranslatev3")
            && !on.Value.url.Contains("www.bing.com"), on?.url ?? "<null>");

        // ③ text 只许出现一次
        Assert("必应/form 里 text 只出现一次（同名多条只翻第一条）",
            on is not null && on.Value.form.Count(f => f.Key == "text") == 1,
            on is null ? "<null>" : string.Join(",", on.Value.form.Select(f => f.Key)));

        // ④ 原文与凭据都要进 form/URL，且未编码的原文不许出现在 URL 里
        Assert("必应/原文走 form 而不是拼进 URL",
            on is not null && !on.Value.url.Contains("你好")
            && on.Value.form.Any(f => f.Key == "text" && f.Value == "你好")
            && on.Value.url.Contains("IG=") && on.Value.form.Any(f => f.Key == "token"),
            on?.url ?? "<null>");

        // ⑤ 语言码：只收 zh 一支，其余原样传
        Assert("必应/语言码映射 zh-CN→zh-Hans、zh-TW/zh-HK→zh-Hant、其余原样",
            TranslationService.MapToBingLang("zh-CN") == "zh-Hans"
            && TranslationService.MapToBingLang("zh-TW") == "zh-Hant"
            && TranslationService.MapToBingLang("zh-HK") == "zh-Hant"
            && TranslationService.MapToBingLang("en") == "en"
            && TranslationService.MapToBingLang("ja") == "ja",
            $"{TranslationService.MapToBingLang("zh-CN")} / {TranslationService.MapToBingLang("zh-TW")}");

        // ⑥ 真样本 ⇒ 取到译文（样本原样来自本机实抓的响应体）
        const string realBody = """[{"translations":[{"text":"请在退出前保存文件。","to":"zh-Hans"}],"detectedLanguage":{"language":"en"}}]""";
        var (btext, bnote) = TranslationService.ParseBingResponse(realBody);
        Assert("必应/真样本解析出译文", btext == "请在退出前保存文件。", $"{btext} / {bnote}");

        // ⑦ 体内 statusCode ⇒ 判失败并点名（那一刻 HTTP 是 200，只看状态码会当成成功）
        var (badText, badNote) = TranslationService.ParseBingResponse("""{"statusCode":205}""");
        Assert("必应/体内 205 判失败且点名 statusCode", badText is null && badNote.Contains("statusCode"), badNote);

        // ⑧ 空 body ⇒ 判失败并点名 302
        var (emptyText, emptyNote) = TranslationService.ParseBingResponse("   ");
        Assert("必应/空 body 判失败并点名 302 丢 body", emptyText is null && emptyNote.Contains("302"), emptyNote);

        // ⑨ HTML ⇒ 判失败。注意这条样本里**故意**放了 trans_result 字样：
        //    百度 /translate 返回的网页壳里就有这个词，按"body 含关键字"判会把它读成"接口给了译文"
        //    （本轮另一条判据正是这样假红的，见交接 §35.2）。
        var (htmlText, htmlNote) = TranslationService.ParseBingResponse("<!DOCTYPE html><html>trans_result</html>");
        Assert("必应/HTML 判失败而不是按关键字认译文", htmlText is null && htmlNote.Contains("不是 JSON"), htmlNote);

        // ⑩ 接线：生产路径真的调它，且**排在 Google 前面**（本机实测必应 1.1–2.4 s、Google 各挂 5 s 超时，
        //     反序会让国内用户每行白等 10 秒才轮得到能用的那条）
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "TranslationService.cs"));
        if (path is null) { Skip("必应/调用侧接线", "未找到 TranslationService.cs"); return; }
        string code = CodeOnly(path);
        int bingAt = code.IndexOf("await TryBingWebEndpointAsync(", StringComparison.Ordinal);
        int googleAt = code.IndexOf("await TryGoogleMultiEndpointAsync(", StringComparison.Ordinal);
        Assert("必应/TryBingWebEndpointAsync 被降级链调用且排在 Google 前",
            bingAt >= 0 && googleAt >= 0 && bingAt < googleAt, $"bing@{bingAt} google@{googleAt}");

        // ⑪ 闸门关着 + 什么都没配 ⇒ 抛的是"没配后端"，一条请求都发不出去。
        //     这条是整轮整改的落点：以前这种情况会真的往外发请求，然后回一句"翻译失败"。
        var offline = new TranslationService(new LlmConfig());
        string? thrown = null;
        try { offline.TranslateAsync("Hello", "zh-CN").GetAwaiter().GetResult(); }
        catch (TranslationNotConfiguredException ex) { thrown = ex.Message; }
        catch (Exception ex) { thrown = "WrongType:" + ex.GetType().Name; }
        Assert("闸门关且无凭据 ⇒ 抛 TranslationNotConfiguredException（不是网络文案、也不发请求）",
            thrown is not null && !thrown.StartsWith("WrongType:") && thrown.Contains("非官方"), thrown ?? "<没抛>");
    }

    /// <summary>
    /// 每拍关联 ID（OpId）的语义与接线（2026-09-21，交接 §36.5 第 1 条销账）。
    /// <para>为什么既测语义又测接线：机制在 2026-09-20 就"建好了"，但全仓无人开作用域 ⇒
    /// 日志行的 <c>#opId</c> 段恒空，整整一天没人发现。<b>"基础设施齐备"从来不等于"接上了"</b>，
    /// 这是本仓同族错误的第三次（前两次：档位没有入口、凭据没有输入框）。所以 ⑥ 那类源码接线判据
    /// 是这个套件里最该有的一条，不是形式主义。</para>
    /// <para>⚠ 语义判据不假设环境里 <c>CurrentOpId</c> 恒 null：先记下进入时的值，
    /// 退出时断言"回到进入时的值"。否则同一次运行里别的用例若漏了一次 Dispose，
    /// 这条会以与本bug无关的方式红掉（反过来也挡住它真的漏了）。</para>
    /// </summary>
    static void Test_OpIdScopeSemanticsAndWiring()
    {
        string? before = CoreLog.CurrentOpId;

        // ① 开作用域 ⇒ 有 ID；离开 ⇒ 回到进入时的值
        string? inside;
        using (var outer = CoreLog.BeginOp())
        {
            inside = CoreLog.CurrentOpId;
            Assert("OpId ①：作用域内有非空 ID", !string.IsNullOrEmpty(inside), $"实得 {inside ?? "null"}");

            // ② 嵌套 ⇒ 内层是新 ID，内层离开后恢复的是**外层 ID**而不是 null
            using (var inner = CoreLog.BeginOp())
            {
                Assert("OpId ②：嵌套时内层另开新 ID",
                    CoreLog.CurrentOpId == inner.Id && inner.Id != outer.Id,
                    $"outer={outer.Id} inner={CoreLog.CurrentOpId}");
            }
            Assert("OpId ②b：内层离开后回到外层 ID（不是 null）", CoreLog.CurrentOpId == outer.Id,
                $"实得 {CoreLog.CurrentOpId ?? "null"}，期望 {outer.Id}");

            // ③ 续用：跨 UI 根把同一次操作接上
            const string pinned = "abc123";
            using (var again = CoreLog.ContinueOp(pinned))
                Assert("OpId ③：ContinueOp 用的是传进来的 ID", CoreLog.CurrentOpId == pinned,
                    $"实得 {CoreLog.CurrentOpId}");
            Assert("OpId ③b：续用离开后回到外层 ID", CoreLog.CurrentOpId == outer.Id,
                $"实得 {CoreLog.CurrentOpId ?? "null"}");

            // ④ 传空 ⇒ 宁可另开一个新 ID，也不把这一拍错连到上一拍
            using var fresh = CoreLog.ContinueOp(null);
            Assert("OpId ④：ContinueOp(null) 另开新 ID 而不是沿用/置空",
                !string.IsNullOrEmpty(fresh.Id) && fresh.Id != outer.Id, $"实得 {fresh.Id}");
        }
        Assert("OpId ⑤：全部离开后回到进入时的值", CoreLog.CurrentOpId == before,
            $"实得 {CoreLog.CurrentOpId ?? "null"}，期望 {before ?? "null"}");

        // ⑥ 接线：App 侧真有根在开作用域、真有下游在续用（行为判据看不到这一层）
        var mw = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var ocr = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "OcrPreviewWindow.xaml.cs"));
        if (mw is null || ocr is null) { Skip("OpId/接线", "找不到 MainWindow.xaml.cs 或 OcrPreviewWindow.xaml.cs"); return; }
        string mwCode = CodeOnly(mw), ocrCode = CodeOnly(ocr);
        int begins = CountOccurrences(mwCode, "CoreLog.BeginOp(");
        int continues = CountOccurrences(mwCode, "CoreLog.ContinueOp(");
        Assert("OpId ⑥：MainWindow 至少两条截图根各开一拍（选区 + 无感）", begins >= 2, $"BeginOp {begins} 处");
        Assert("OpId ⑥b：选区动作回调续用外层 ID", continues >= 1, $"ContinueOp {continues} 处");
        Assert("OpId ⑥c：建 OCR 预览窗口时把 ID 交下去",
            mwCode.Contains("CaptureOpId = _captureOpId"), "未见赋值");
        Assert("OpId ⑥d：预览窗口的复制/翻译/保存三个根都续用",
            CountOccurrences(ocrCode, "CoreLog.ContinueOp(") >= 3,
            $"实得 {CountOccurrences(ocrCode, "CoreLog.ContinueOp(")} 处");

        // ⑦ 裸调 SetOpId 不还原上下文，会把值漏给后续不相干的根 ⇒ 只许通过 BeginOp/ContinueOp 用
        int raw = 0;
        foreach (string f in new[] { mw, ocr })
            raw += CountOccurrences(CodeOnly(f), "CoreLog.SetOpId(");
        Assert("OpId ⑦：App 侧不裸调 SetOpId（它不还原，容易污染下一个根）", raw == 0, $"实得 {raw} 处");
    }

    static int CountOccurrences(string haystack, string needle)
    {
        int n = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>
    /// 翻译后端的"可用性判断"与"告知"必须诚实（2026-09-21 告知整改）。
    /// <para>修前的表现：一条后端都没配时，N 行各自注定失败，而行级容错把异常全吞了，
    /// 状态栏打的是"⚠ 翻译完成（N/N 行失败，已保留原文）"——把故障说成完成，还一个原因都不给。</para>
    /// <para>判据要能区分"没配"与"配了但失败"，否则用户会去查网线。</para>
    /// </summary>
    /// <summary>
    /// 「视觉翻译」曾经是个死开关：下拉里有第三档、提示文案承诺"图片直传一步识别+翻译"，
    /// 而 Core 里零实现 —— 选它等于选 LLM 文本路，用户看到的说明是假的。
    /// 2026-09-21 接上实现后，这里钉三件事：行数映射不许错位、超限必须当场拒发、App 侧真的在调它。
    /// </summary>
    /// <summary>
    /// 「视觉翻译」曾经是个死开关：下拉里有第三档、提示文案承诺"图片直传一步识别+翻译"，
    /// 而 Core 里零实现 —— 选它等于选 LLM 文本路，用户看到的说明是假的。
    /// 2026-09-21 接上实现后，这里钉三件事：行数映射不许错位、超限必须当场拒发、App 侧真的在调它。
    /// </summary>
    static void Test_VisionTranslationIsWired()
    {
        // 测试里不用反斜杠转义：Git Bash 的 heredoc 会把 \n 变成真换行，
        // 造出的 C# 字面量直接是"常量中有换行符"。用 char 码拼最稳。
        string LF = ((char)10).ToString();
        string CR = ((char)13).ToString();
        string Q = ((char)34).ToString();

        var cfg = new LlmConfig { UseCustomLlm = true, ApiEndpoint = "https://api.deepseek.com/v1", UseVision = true };
        Assert("只有选了 Vision 模式才算走视觉路", TranslationService.VisionConfigured(cfg));
        Assert("没选 Vision 就是文本路（不得抢视觉请求）",
            !TranslationService.VisionConfigured(new LlmConfig { UseCustomLlm = true, ApiEndpoint = "https://x/v1" }));
        Assert("选了 Vision 但没有端点 ⇒ 视觉路不可用",
            !TranslationService.VisionConfigured(new LlmConfig { UseVision = true }));

        var three = TranslationService.MapVisionLines("你好" + LF + "世界" + LF + "再见", 3);
        Assert("行数正好相等才按顺序回填", three is { Length: 3 } && three[1] == "世界",
            string.Join("|", three ?? []));
        Assert("模型自带的编号前缀被剥掉且不改变行数",
            TranslationService.MapVisionLines("1. 你好" + LF + "2、世界" + LF + "3) 再见", 3)?[2] == "再见");
        Assert("多一行不给回填（错位覆盖比失败更难发现）",
            TranslationService.MapVisionLines("你好" + LF + "世界" + LF + "再见" + LF + "多余", 3) is null);
        Assert("少一行同样拒收", TranslationService.MapVisionLines("你好", 3) is null);
        Assert("CRLF 与空行不影响行数",
            TranslationService.MapVisionLines(CR + LF + "你好" + CR + LF + CR + LF + "世界" + CR + LF + "再见" + CR + LF, 3) is { Length: 3 });
        Assert("期望行数非法时不猜", TranslationService.MapVisionLines("你好", 0) is null);

        // 请求体形状：这条链路从没在真机上发过请求（本机没有可用的视觉端点密钥），
        // 所以更要把它序列化成字节钉住 —— OpenAI 兼容端点对 image_url / type 的字段名零容忍，
        // 拼错了只会回 400，而用户看到的是一句"模型不支持图片"。
        var probe = new TrueToneCap.Core.Services.LlmVisionRequest(
            "m", [new TrueToneCap.Core.Services.LlmVisionMessage("user",
            [
                new TrueToneCap.Core.Services.LlmContentPart("text", "hi", null),
                new TrueToneCap.Core.Services.LlmContentPart("image_url", null,
                    new TrueToneCap.Core.Services.LlmImageUrl("data:image/png;base64,AAA"))
            ])], 0.1, 128);
        var json = System.Text.Json.JsonSerializer.Serialize(probe,
            TrueToneCap.Core.Services.LlmJsonContext.Default.LlmVisionRequest);
        Assert("请求体是 OpenAI 兼容形状（model/messages/content/image_url.url）",
            json.Contains(Q + "model" + Q) && json.Contains(Q + "messages" + Q)
            && json.Contains(Q + "content" + Q) && json.Contains(Q + "image_url" + Q)
            && json.Contains(Q + "url" + Q) && json.Contains("data:image/png;base64,AAA"),
            json.Length > 200 ? json[..200] : json);
        Assert("content 序列化成数组而不是字符串（视觉请求的硬要求）",
            json.IndexOf(Q + "content" + Q, StringComparison.Ordinal) >= 0
            && json.Split(Q + "content" + Q + ':')[1].TrimStart().StartsWith('['),
            json.Length > 200 ? json[..200] : json);
        Assert("空字段不写出 null（严格端点会把 image_url:null 当成一张空图）",
            !json.Contains(":null"), json.Length > 240 ? json[..240] : json);

        // 同一条判据套到文本路上：它历史上一直发的是 PascalCase 键名。
        var textReq = new TrueToneCap.Core.Services.LlmChatRequest(
            "m", [new TrueToneCap.Core.Services.LlmChatMessage("user", "hi")], 0.3, 64);
        var textJson = System.Text.Json.JsonSerializer.Serialize(textReq,
            TrueToneCap.Core.Services.LlmJsonContext.Default.LlmChatRequest);
        Assert("文本路请求体同样是小写键（model/messages/role/content）",
            textJson.Contains(Q + "model" + Q) && textJson.Contains(Q + "messages" + Q)
            && textJson.Contains(Q + "role" + Q) && textJson.Contains(Q + "content" + Q)
            && !textJson.Contains(Q + "Model" + Q) && !textJson.Contains(Q + "Messages" + Q),
            textJson);
        Assert("两条路的 max_tokens 都按端点要求拼写",
            json.Contains(Q + "max_tokens" + Q) && textJson.Contains(Q + "max_tokens" + Q));

        // 端点拼接：两路共用一处之后，把它钉住（尾斜杠 / 已经带后缀 / 大小写）
        Assert("端点 URL 拼接对三种写法都收敛到同一个值",
            TranslationService.ChatCompletionsUrl("https://api.deepseek.com/v1") == "https://api.deepseek.com/v1/chat/completions"
            && TranslationService.ChatCompletionsUrl("https://api.deepseek.com/v1/") == "https://api.deepseek.com/v1/chat/completions"
            && TranslationService.ChatCompletionsUrl("https://api.deepseek.com/v1/chat/completions") == "https://api.deepseek.com/v1/chat/completions",
            TranslationService.ChatCompletionsUrl("https://api.deepseek.com/v1/"));

        // 超限必须在【发请求之前】拒，且不许偷偷缩图或退回文本路
        var tooBig = new byte[TranslationService.VisionPngMaxBytes + 1];
        string over;
        try
        {
            new TranslationService(cfg).TranslateImageWithVisionAsync(tooBig, "zh-CN", 3)
                .GetAwaiter().GetResult();
            over = "NO-THROW";
        }
        catch (TranslationException ex) { over = ex.Message; }
        Assert("超大截图直接报障并给出路（缩小选区 / 切回文字路）",
            over.Contains("MB") && (over.Contains("缩小选区") || over.Contains("切回")), over);

        // 接线判据（源码形态，滤注释）：App 侧必须在 Vision 分支里调这条实现
        var win = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "OcrPreviewWindow.xaml.cs"));
        var mw = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (win is null || mw is null)
        {
            Skip("视觉路调用侧接线", "未找到 OcrPreviewWindow.xaml.cs / MainWindow.xaml.cs");
            return;
        }
        string CodeOf(string path) => string.Join(LF,
            File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("*")));
        var winCode = CodeOf(win);
        var mwCode = CodeOf(mw);
        Assert("预览窗口按 VisionConfigured 分派到视觉实现",
            winCode.Contains("VisionConfigured(") && winCode.Contains("TranslateImageWithVisionAsync("),
            "删掉分派分支会让 Vision 重新变成死开关");
        Assert("LlmConfig.UseVision 由设置里的 TranslationMode 驱动",
            mwCode.Contains("UseVision =") && mwCode.Contains(Q + "Vision" + Q),
            "App 侧不再下发 UseVision ⇒ 视觉路又变回不可达");
    }

    /// <summary>
    /// LLM 后端的失败原因要分类：401/403 是配置问题、429 是额度、只有没有状态码才算连不上。
    /// <para>历史缺陷：LLM 是【唯一】不把原因写进 _lastBackendNote 的后端，最终提示又固定缀一句
    /// "多半是网络、额度或接口变更，不是没配置" ⇒ 密钥填错的用户被告知去查网线。
    /// 真机实测复现：配好 DeepSeek 端点 + 无效 key，133ms 返回 401，
    /// 而界面末行写的是"Google 非官方端点未启用"——401 那句真话被后面的"跳过"覆盖掉了。</para>
    /// </summary>
    static void Test_LlmFailureReasonIsClassified()
    {
        static string C(System.Exception ex) => TranslationService.ClassifyLlmFailure(ex);

        var unauth = C(new System.Net.Http.HttpRequestException(
            "Response status code does not indicate success: 401 (Authorization Required).",
            null, System.Net.HttpStatusCode.Unauthorized));
        Assert("401 归成密钥/模型权限问题，并明说不是网络",
            unauth.Contains("401") && unauth.Contains("密钥") && unauth.Contains("不是网络问题"), unauth);

        var quota = C(new System.Net.Http.HttpRequestException(
            "429", null, System.Net.HttpStatusCode.TooManyRequests));
        Assert("429 归成额度/频率", quota.Contains("429") && quota.Contains("额度"), quota);

        var badreq = C(new System.Net.Http.HttpRequestException(
            "400", null, System.Net.HttpStatusCode.BadRequest));
        Assert("400 提示模型名填错或该端点不支持图片", badreq.Contains("400") && badreq.Contains("模型名"), badreq);

        var net = C(new System.Net.Http.HttpRequestException("no such host", null, null));
        Assert("只有拿不到状态码才说连不上", net.Contains("连不上") && !net.Contains("密钥"), net);

        var configured = TranslationService.DescribeBackends(
            new LlmConfig { UseCustomLlm = true, ApiEndpoint = "https://api.deepseek.com/v1" });
        Assert("已配置分支不再抢答原因（原因由 _lastBackendNote 附在后面）",
            configured.Contains("没返回结果") && !configured.Contains("多半是网络"), configured);
    }

    static void Test_TranslationBackendAvailabilityIsHonest()
    {
        Assert("空配置 ⇒ 判定为无可用后端", !TranslationService.AnyBackendAvailable(new LlmConfig()));
        Assert("只有 AppKey 没有 AppSecret ⇒ 仍算没有（有道会被跳过，不许声称可用）",
            !TranslationService.AnyBackendAvailable(new LlmConfig { YoudaoAppKey = "k" }));
        Assert("AppKey + AppSecret ⇒ 可用",
            TranslationService.AnyBackendAvailable(new LlmConfig { YoudaoAppKey = "k", YoudaoAppSecret = "s" }));
        Assert("开了 LLM 但没填端点 ⇒ 不算可用",
            !TranslationService.AnyBackendAvailable(new LlmConfig { UseCustomLlm = true }));
        Assert("LLM + 端点 ⇒ 可用",
            TranslationService.AnyBackendAvailable(new LlmConfig { UseCustomLlm = true, ApiEndpoint = "https://api.deepseek.com/v1" }));
        Assert("只勾非官方端点 ⇒ 可用（这条本身就是用户显式同意过的）",
            TranslationService.AnyBackendAvailable(new LlmConfig { AllowUnofficialTranslateEndpoints = true }));

        string empty = TranslationService.DescribeBackends(new LlmConfig());
        Assert("没配时的说明给出三条出路（有道 / LLM / Google 非官方）",
            empty.Contains("有道") && empty.Contains("LLM") && empty.Contains("非官方"), empty);
        Assert("没配时的说明不甩锅网络", !empty.Contains("网络问题"), empty);
        Assert("非官方那条写清两项代价（非公开 API + 文字发给第三方）",
            empty.Contains("不是公开 API") || empty.Contains("第三方"), empty);
        string configured = TranslationService.DescribeBackends(
            new LlmConfig { YoudaoAppKey = "k", YoudaoAppSecret = "s" });
        Assert("配了之后的说明切成「已启用哪些 + 那是真失败」口径",
            configured.Contains("有道官方 API") && configured.Contains("没返回结果"), configured);

        // 空配置必须在**发请求之前**就抛，而不是悄悄联网试一次
        string outcome;
        try
        {
            new TranslationService(new LlmConfig()).TranslateAsync("hello", "zh-CN")
                .GetAwaiter().GetResult();
            outcome = "NO-THROW";
        }
        catch (TranslationNotConfiguredException ex) { outcome = ex.Message; }
        catch (Exception ex) { outcome = "WRONG-TYPE:" + ex.GetType().Name; }
        Assert("空配置直接抛 TranslationNotConfiguredException（带三条出路）",
            outcome.StartsWith("还没有任何可用") , outcome.Length > 60 ? outcome[..60] : outcome);

        // ── 有道错误码必须翻成人话，但**只翻有据的那几个** ──
        Assert("101/108/202 有解释（101、108 为 2026-09-21 本机实测；202 沿用仓库既有注释）",
            TranslationService.ExplainYoudaoError("101").Contains("AppID")
            && TranslationService.ExplainYoudaoError("108").Contains("不存在")
            && TranslationService.ExplainYoudaoError("202").Contains("签名"));
        Assert("未收录的码不许编造含义（105/103 一类网上流传解释未实测）",
            TranslationService.ExplainYoudaoError("105").Contains("未收录该码的解释"),
            TranslationService.ExplainYoudaoError("105"));
        Assert("响应里没有 errorCode 时说明形态异常，不猜",
            TranslationService.ExplainYoudaoError(null).Contains("无法判定原因"));
    }

    /// <summary>
    /// 防回潮：除两个白名单文件外，<c>src</c> 下不许再有裸 <c>Debug.WriteLine</c>。
    /// <para>为什么值得钉一条：这类调用的问题不是"吵"，而是**在 Release 下整条消失**
    /// （<c>[Conditional("DEBUG")]</c>），于是"降级发生了没有"这件事在用户的日志里根本不存在。
    /// 2026-09-20~21 把 Core/App 两侧约 190 处迁到 CoreLog/LogService 之后，
    /// 没有这条判据，半年内一定会再长回来。</para>
    /// <para>白名单是两处**设计内**的用法：<c>LogService.cs</c> 的控制台镜像与写文件失败时的自举兜底、
    /// <c>CoreLog.cs</c> 未挂载 sink 时的调试器输出。第二条判据反过来盯住它们，防止哪天被当噪声删掉。</para>
    /// </summary>
    static void Test_NoRawDebugWritesOutsideTheWhitelistedSinks()
    {
        var anchor = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Logging", "CoreLog.cs"));
        if (anchor is null) { Skip("Debug.WriteLine 防回潮", "定位不到 CoreLog.cs（目录结构变了？）"); return; }
        string srcDir = Directory.GetParent(Path.GetDirectoryName(anchor)!)!.Parent!.FullName;

        string[] whitelist = ["LogService.cs", "CoreLog.cs"];
        // needle 运行时拼装：写死成字面量的话，本行自己就会被下面这段扫到（首跑就是被自己绊倒的）
        string needle = "Debug" + ".WriteLine(";
        var offenders = new List<string>();
        int whitelisted = 0;
        foreach (string f in Directory.EnumerateFiles(srcDir, "*.cs", SearchOption.AllDirectories))
        {
            string norm = f.Replace('/', Path.DirectorySeparatorChar);
            if (norm.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || norm.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            int n = CodeOnly(f).Split(needle).Length - 1;   // 注释里提到的不算
            if (n == 0) continue;
            if (Array.Exists(whitelist, w => string.Equals(Path.GetFileName(f), w, StringComparison.Ordinal)))
                whitelisted += n;
            else offenders.Add($"{Path.GetRelativePath(srcDir, f)}: {n} 处");
        }

        Assert("白名单外 0 处裸 Debug.WriteLine（Release 下等于写了条没人看得见的日志）",
            offenders.Count == 0, string.Join(" | ", offenders));
        Assert("白名单内仍在用（控制台镜像 + 未挂载兜底，不是漏迁）", whitelisted >= 5, $"实得 {whitelisted} 处");
    }


    /// <summary>
    /// 许可清单三处必须互相闭合：<c>tools-manifest.json</c>（每个原生工具引用哪些许可全文）、
    /// <c>licenses/INDEX.json</c>（每个许可全文属于哪个 shippedIn）、以及 <c>licenses/</c> 目录本身。
    /// <para>为什么要有这一条而不是"人工记得对齐"：2026-09-20 实测就漏了 —— sjpeg 静态链入
    /// cjxl/cjpegli（二进制里有 <c>sjpeg::Encode</c> 符号）、INDEX 里有它，但两个工具的
    /// <c>licenseFiles</c> 都没列 ⇒ 出包时会少发一份 Apache-2.0 全文。这类"三处各写一份、
    /// 改一处忘两处"的漂移正是闭集判据该拦的。</para>
    /// <para>顺带钉两件事：INDEX 记的 sha256 必须等于盘上文件（防"改了许可文本但清单还旧"），
    /// 以及每条许可都要有 spdx 与义务说明（防空壳行）。</para>
    /// </summary>
    static void Test_ManifestLicenseFilesAreComplete()
    {
        var manifestPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Resources", "tools-manifest.json"));
        var indexPath = FindRepoFile(Path.Combine("licenses", "INDEX.json"));
        if (manifestPath is null || indexPath is null)
        {
            Assert("许可清单文件存在", false,
                $"manifest={manifestPath is not null} INDEX={indexPath is not null}");
            return;
        }

        using var manifest = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
        using var index = System.Text.Json.JsonDocument.Parse(File.ReadAllText(indexPath));
        Assert("两份清单都是合法 JSON", true);

        string licDir = Path.GetDirectoryName(indexPath)!;
        var byShipped = new Dictionary<string, HashSet<string>>();
        int entries = 0, missingField = 0, badHash = 0;
        bool missingFile = false;
        foreach (var e in index.RootElement.GetProperty("licenses").EnumerateArray())
        {
            entries++;
            string file = e.GetProperty("file").GetString()!;
            if (string.IsNullOrWhiteSpace(e.GetProperty("spdx").GetString())
                || string.IsNullOrWhiteSpace(e.GetProperty("obligationNote").GetString()))
                missingField++;
            var onDisk = Path.Combine(licDir, file);
            if (!File.Exists(onDisk)) { missingFile = true; continue; }
            string actual = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(onDisk))).ToLowerInvariant();
            if (actual != e.GetProperty("sha256").GetString()) badHash++;
            foreach (var s in e.GetProperty("shippedIn").EnumerateArray())
            {
                string key = s.GetString()!;
                if (!byShipped.TryGetValue(key, out HashSet<string>? set))
                    byShipped[key] = set = new HashSet<string>();
                set.Add(file);
            }
        }
        Assert($"INDEX：{entries} 条许可全部在盘上", !missingFile);
        Assert("INDEX：sha256 与盘上文件逐条一致（改了文本就必须同步清单）", badHash == 0, $"不符 {badHash} 条");
        Assert("INDEX：每条都有 spdx 与义务说明（无空壳行）", missingField == 0, $"缺字段 {missingField} 条");
        Assert("INDEX：runtime 与 models 两个桶都非空",
            byShipped.GetValueOrDefault("runtime")?.Count > 0 && byShipped.GetValueOrDefault("models")?.Count > 0,
            $"runtime={byShipped.GetValueOrDefault("runtime")?.Count ?? 0} models={byShipped.GetValueOrDefault("models")?.Count ?? 0}");

        int drift = 0, absent = 0;
        foreach (var t in manifest.RootElement.GetProperty("tools").EnumerateArray())
        {
            string tool = t.GetProperty("file").GetString()!;
            var listed = new HashSet<string>(
                t.GetProperty("licenseFiles").EnumerateArray().Select(x => x.GetString()!));
            var expected = byShipped.GetValueOrDefault(tool) ?? new HashSet<string>();
            if (!listed.SetEquals(expected))
            {
                drift++;
                Console.WriteLine($"  · {tool}: manifest 有而 INDEX 无 = [{string.Join(",", listed.Except(expected))}]"
                    + $" / INDEX 有而 manifest 无 = [{string.Join(",", expected.Except(listed))}]");
            }
            if (string.IsNullOrWhiteSpace(t.GetProperty("license").GetString())) absent++;
            foreach (string f in listed)
                if (!File.Exists(Path.Combine(licDir, f))) { absent++; Console.WriteLine($"  · {tool}: 引用了不存在的 {f}"); }
        }
        Assert("原生工具的 licenseFiles 与 INDEX 双向闭合（无遗漏、无孤儿）", drift == 0,
            $"{drift} 个工具不一致 —— 漏一个就等于出包时少发一份许可全文");
        Assert("每个工具都有 SPDX 表达式且引用的文件都在盘上", absent == 0, $"异常 {absent} 处");
    }

    /// <summary>
    /// 日志档位必须真的能放行/挡住 Debug，且必须有人在运行时改它。
    /// <para>背景：<c>FileDebugEnabled</c> 被删时的理由是"档位模型是它的替代者"，
    /// 但那时 <c>CoreLog.SetDetail</c> 的唯一调用方 <c>LogService.ApplyDetailLevel</c>
    /// <b>零调用方</b>，设置页也没有注释承诺的那个下拉 —— 于是一个"更好的开关"
    /// 实际上比它替代的那个更不可达。2026-09-20 把 设置 → 行为 → 日志详细度 接上之后，
    /// 这条判据同时钉住"模型有效"与"有人调它"两件事。</para>
    /// </summary>
    static void Test_LogDetailLevelGatesDebugAndIsWired()
    {
        var prev = CoreLog.Detail;
        try
        {
            CoreLog.SetDetail(LogDetail.Brief);
            bool briefBlocksDebug = !CoreLog.IsEnabled(LogCategory.Encoding, LogLevel.Debug);
            bool briefKeepsWarn = CoreLog.IsEnabled(LogCategory.Encoding, LogLevel.Warning);

            CoreLog.SetDetail(LogDetail.Diagnose);
            bool diagnoseLetsDebug = CoreLog.IsEnabled(LogCategory.Encoding, LogLevel.Debug);

            Assert("档位 Brief 挡住 Debug（默认日志不被刷屏）", briefBlocksDebug);
            Assert("档位 Brief 不挡 Warning（降级/失败事实永远要落盘）", briefKeepsWarn);
            Assert("切到 Diagnose 后 Debug 放行", diagnoseLetsDebug);
        }
        finally
        {
            CoreLog.SetDetail(prev);   // 静态全局：不许把档位泄漏给后续用例
        }

        // 运行时是否真的有人应用档位 —— 行为判据测不到"接线"，只能回看调用方
        var wire = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Settings.cs"));
        if (wire is null) { Skip("档位接线", "未找到 MainWindow.Settings.cs"); return; }
        string code = CodeOnly(wire);
        Assert("设置页调用 LogService.ApplyDetailLevel（档位不是死代码）",
            code.Contains("LogService.ApplyDetailLevel("));
        var store = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        Assert("档位有持久化落点（AppSettingsData.LogVerbosity）",
            store is not null && CodeOnly(store).Contains("public string LogVerbosity"));

        // OpId 只做观察，不做断言：这条测的是"档位判据跑起来时上下文里有没有 ID"，
        // 而"有没有人开作用域"由 Test_OpIdScopeSemanticsAndWiring 的 ⑥ 那组接线判据钉（2026-09-21 起已接上）。
        Console.WriteLine(CoreLog.CurrentOpId is null
            ? "  ℹ  此处不在任何 Op 作用域内（正常：套件自己没开），#opId 段不会出现"
            : $"  ℹ  OpId 正在生效：#{CoreLog.CurrentOpId}");
    }

    /// <summary>
    /// 隐私地板（<c>CoreLog.Scrub</c>）：翻译/LLM 请求的 URL 里挂着 OCR 原文，异常文本又可能带 URI，
    /// 所以"日志不记敏感内容"必须是**机制**而不是一句注释 —— 设置页那句承诺也依赖它成立。
    /// <para>三组判据各管一头：</para>
    /// ① 该刮的刮掉：<c>q=&lt;原文&gt;</c>、<c>key=sk-…</c>、大小写数字齐备的长 base64；
    /// ② 不该刮的一动不动：普通诊断行，以及**日志里合法存在的 64 位小写 SHA256**
    ///    （发布校验与工具链哈希全靠它；刮了就等于自毁诊断能力 —— 这正是 Scrub 里
    ///    "三类字符齐全才遮"那个条件存在的理由）；
    /// ③ 收口点生效：从 <c>CoreLog.Info</c> 发进去、在 sink 收到的必须已经是刮过的。
    ///    只测 <c>Scrub()</c> 函数等于没测 —— 真正的入口是 <c>Emit</c>。
    /// </summary>
    static void Test_LogPrivacyFloorScrubbing()
    {
        const string secret = "身份证号11010119900307abc";
        string encoded = Uri.EscapeDataString(secret);          // 真正出现在 URL 里的是这个形态
        string url = "HTTP 500 https://translate.googleapis.com/translate_a/single"
                   + $"?client=gtx&sl=zh&tl=en&dt=t&q={encoded}";
        string scrubbed = CoreLog.Scrub(url);

        // ⚠ 断言必须拿「URL 里实际出现过的那个串」比。首版拿的是解码后的原文，
        //   而输入里根本没有它 ⇒ 判据【必然通过】，于是"值被追加而非替换"这个真 bug 从它眼皮下过了。
        Assert("① 翻译 URL 里的 q= 值被刮掉",
            !scrubbed.Contains(encoded) && scrubbed.Contains("q="), scrubbed);
        Assert("① 只刮值、不改写整行（同行非敏感参数保留）",
            scrubbed.Contains("client=gtx") && scrubbed.Contains("tl=en") && scrubbed.Contains("HTTP 500"));
        Assert("① 密钥赋值被刮掉且留下标记",
            CoreLog.Scrub("请求失败 key=sk-abcdef0123456789") == "请求失败 key=‹redacted›");
        const string b64ish = "Ab1cDeFgHiJkLmNoPqRsTuVwXyZ0123456789";
        Assert("① 大小写数字齐备的长串被遮", !CoreLog.Scrub("blob=" + b64ish).Contains(b64ish));

        // ── ② 反向控制：证明它不是"见长的就刮"，否则判据本身就是在自伤 ──
        string hashLine = "工具链校验 a5b8697c566fe911e9ec7bcbca82b04327d4b5135c47524db0969fad8a184830 通过";
        Assert("② 64 位小写 SHA256 原样保留（发布校验日志不许被刮）",
            CoreLog.Scrub(hashLine) == hashLine, CoreLog.Scrub(hashLine));
        string benign = "AVIF 后端选择 = NVENC，4K 耗时 120ms，色度 4:2:0";
        Assert("② 普通诊断行原样通过", CoreLog.Scrub(benign) == benign, CoreLog.Scrub(benign));
        Assert("② 幂等（刮过的文本再刮一次不变）", CoreLog.Scrub(scrubbed) == scrubbed);

        // ── ③ 收口：走公开写入口，看 sink 实际收到了什么 ──
        // 只在"本来就没有 sink"时接管：此时消息原本会进早期缓冲、永不落盘，
        // 我把它接走不改变任何人的既有行为（测试进程也不会因此少一份日志文件）。
        if (CoreLog.HasSink)
        {
            Console.WriteLine("  ℹ  已有 sink 挂载，跳过 ③（不劫持别人的日志出口）");
            return;
        }
        var caught = new List<string>();
        CoreLog.Attach((lvl, cat, tag, msg, file, line, op) => caught.Add(msg));
        CoreLog.Info("Probe", url, LogCategory.Network);
        Assert("③ sink 收到的是刮过的文本（收口在 Emit，不靠调用方自觉）",
            caught.Any(c => c.Contains("client=gtx")) && !caught.Any(c => c.Contains(encoded)),
            $"捕获 {caught.Count} 条");
    }

    /// <summary>
    /// P0-C 回归：HLSL 与 CPU 的分段 Reinhard 必须一致。
    /// <para>HLSL 无法在 C# 中直接执行，故做<b>源码级</b>校验：断言 ToneMapping.hlsl 的
    /// SegmentedReinhardMap 函数体内【不再出现】旧的 ReinhardMap / smoothstep 公式。
    /// 属文本级检查，相对脆弱，但能抓住"CPU 改了、HLSL 没跟"这类跨实现漂移 ——
    /// 而该类漂移曾真实导致 HDR 预览与最终输出长期不一致。</para>
    /// </summary>
    static void Test_Hlsl_ToneMapping_MatchesCpu()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Shaders", "ToneMapping.hlsl"));
        if (path is null) { Skip("HLSL/CPU 曲线一致性", "未找到 ToneMapping.hlsl"); return; }

        var src = File.ReadAllText(path);
        int idx = src.IndexOf("float SegmentedReinhardMap", StringComparison.Ordinal);
        if (idx < 0) { Assert("HLSL: 找到 SegmentedReinhardMap", false); return; }

        // 只取函数体（到下一个 "\n}" 为止），避免把前置说明性注释里的旧公式名算进来
        int end = src.IndexOf("\n}", idx, StringComparison.Ordinal);
        var body = end > idx ? src[idx..end] : src[idx..];

        Assert("HLSL: 已移除旧 ReinhardMap 公式 (y / h2)", !body.Contains("y / h2"));
        Assert("HLSL: 已移除 smoothstep 过渡（暗环来源）", !body.Contains("smoothstep") && !body.Contains("3.0f - 2.0f * t"));
        Assert("HLSL: y>1 时饱和到 1.0（与 CPU 一致）", body.Contains("return 1.0f"));
        Assert("HLSL: 保留 y<=1 直通分支", body.Contains("y <= 1.0f"));
    }

    /// <summary>从测试运行目录向上查找仓库内文件；找不到返回 null（由调用方 Skip）。</summary>
    static string? FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var cand = Path.Combine(dir.FullName, relative);
            if (File.Exists(cand)) return cand;
        }
        return null;
    }

    /// <summary>
    /// P1-1 回归：AVIF 末端保底写 .png 后，管线必须返回【实际写入】的路径，
    /// 而不是原 .avif —— 否则 Toast / 剪贴板 / 归档全部指向不存在的文件，
    /// 而用户看到的是"✅ 已保存"。
    /// </summary>
    static void Test_FormatHelper_ResolveActualPath()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "TrueToneCap_ResolvePath_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var requested = Path.Combine(tmp, "shot.avif");
            var actual = Path.Combine(tmp, "shot.png");
            File.WriteAllText(actual, "png-bytes");   // 模拟保底写出的 PNG

            // 未登记回退 → 原样返回（正常路径零开销）
            Assert("ResolveActualPath: 未登记时原样返回", FormatHelper.ResolveActualPath(requested) == requested);

            // 登记后 → 校正为实际落盘路径
            FormatHelper.RecordFallbackPath(requested, actual);
            Assert("ResolveActualPath: 登记后校正为实际落盘路径", FormatHelper.ResolveActualPath(requested) == actual);

            // 一次性消费：再次调用回到原路径（避免长驻会话下字典无界增长）
            Assert("ResolveActualPath: 命中后即消费（不重复生效）", FormatHelper.ResolveActualPath(requested) == requested);

            // 登记了但实际文件并不存在 → 不误导，返回原路径
            FormatHelper.RecordFallbackPath(requested, Path.Combine(tmp, "missing.png"));
            Assert("ResolveActualPath: 实际文件不存在时返回原路径", FormatHelper.ResolveActualPath(requested) == requested);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }

    // ═══════════════════════════════════════
    //  跨项目源码契约（2026-09-15）
    //
    //  测试工程只引用 Core，不引用 App，因此 App 层的"两处常量必须同步"类约束
    //  无法用普通单测覆盖 —— 改为源码级校验（与上面的 HLSL 一致性同思路）。
    //  目标缺陷形态：代码能编译、功能不报错，但用户的选择存不住 / 配了不生效。
    // ═══════════════════════════════════════

    /// <summary>
    /// 设置白名单必须与 XAML 下拉框的实际 Tag 完全一致。
    /// <para>历史缺陷：SettingsService 的 OcrEngineMode 白名单写作 "Windows"/"Acme"，
    /// 而 MainWindow.xaml 的 ComboBoxItem Tag 是 "OnnxGpu"/"OnnxCpu"/"WindowsOcr"
    /// ⇒ 用户选"Windows OCR"存下的值恒不合法，每次启动被改回默认，
    /// 且下拉框停在第一项，用户完全无法察觉。白名单里的 "Acme" 则是全仓仅此一处的占位残留。</para>
    /// <para>2026-09-19（交接 §26.4 第 6 项）补上 <c>AvifEncoder</c>：白名单一直是 6 个值，
    /// XAML 却只有 5 项 ⇒ 手改 settings.json 写 "Avifenc" 能通过校验、也真的生效，
    /// 界面仍显示 Auto（"改了不生效"的反方向：生效了却看不见）。</para>
    /// </summary>
    static void Test_SettingsWhitelist_MatchesXamlTags()
    {
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        var srvPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        if (xamlPath is null || srvPath is null) { Skip("设置白名单 vs XAML Tag", "未找到源文件"); return; }

        var xaml = File.ReadAllText(xamlPath);
        var srv = File.ReadAllText(srvPath);
        CheckWhitelistEqualsTags(xaml, srv, "OcrEngineCbo", "s.OcrEngineMode", expectTags: 3);
        CheckWhitelistEqualsTags(xaml, srv, "AvifCodecCbo", "s.AvifEncoder", expectTags: 5);

        // ═══ "Avifenc" 档 2026-09-24 从界面移除，但旧值必须迁到 LibAom 而不是 Auto ═══
        // Auto 优先走硬件 ⇒ 拿它当迁移目标会翻转用户"选了软件编码器"的语义。
        // 这条判据防的是将来有人"顺手简化"成一句白名单回落。
        Assert("旧值 Avifenc 显式迁到 LibAom（不是悄悄落到 Auto）",
            System.Text.RegularExpressions.Regex.IsMatch(srv,
                "s\\.AvifEncoder == \"Avifenc\"[\\s\\S]{0,200}s\\.AvifEncoder = \"LibAom\""));
    }

    static void CheckWhitelistEqualsTags(string xaml, string srv, string combo, string prop, int expectTags)
    {
        var tags = ExtractComboItemTags(xaml, combo);
        var whitelist = ExtractIsOneOfArgs(srv, prop);

        Assert($"XAML: {combo} 解析到 {expectTags} 个 Tag", tags.Count == expectTags, $"实际 {tags.Count}");
        Assert($"白名单: 解析到 {prop} 的合法值", whitelist.Count > 0, $"实际 {whitelist.Count}");
        if (tags.Count == 0 || whitelist.Count == 0) return;

        bool same = whitelist.Count == tags.Count
                 && !whitelist.Except(tags, StringComparer.Ordinal).Any()
                 && !tags.Except(whitelist, StringComparer.Ordinal).Any();
        Assert($"设置白名单[{string.Join(",", whitelist)}] == {combo} Tag[{string.Join(",", tags)}]", same);
    }

    /// <summary>
    /// 设置"只写差集"的<b>语义</b>测试（2026-09-24）。
    /// <para>要修的原始缺陷：<c>Save()</c> 全量序列化 ⇒ 文件里 64 个键一个不缺 ⇒ "缺键才回落代码默认"
    /// 这条兜底从未生效 ⇒ 改代码默认值对任何老用户都不生效。这就是"默认值修了很久修不好"的机制。</para>
    /// <para>为什么测算法本体而不是源码文本：哨兵只能证明调用点还在，证明不了剪枝真的在剪。
    /// 本算法最常见的失效形态是"两份 JSON 因格式化差异被判不等 ⇒ 一个键都删不掉"，
    /// 那是不报错、不失败的空转 —— 恰好是原缺陷的另一种表现。算法因此放在 Core（Test 只引用 Core）。</para>
    /// </summary>
    static void Test_SettingsDiffPruner_Semantics()
    {
        var P = typeof(TrueToneCap.Core.Settings.SettingsDiffPruner);
        var prune = P.GetMethod("PruneToJson")!;
        string Run(System.Text.Json.Nodes.JsonObject live, System.Text.Json.Nodes.JsonObject defs,
                   string[]? keep = null)
            => (string)prune.Invoke(null, [live, defs, keep])!;

        var live = new System.Text.Json.Nodes.JsonObject
        {
            ["QualityAvif"] = 15.0, ["QualityWebp"] = 50.0, ["ChromaGainMap"] = "420",
            ["ColorSpaceIndex"] = 2
        };
        var defs = new System.Text.Json.Nodes.JsonObject
        {
            ["QualityAvif"] = 15.0, ["QualityWebp"] = 90.0, ["ChromaGainMap"] = "420",
            ["ColorSpaceIndex"] = 0
        };
        var json = Run(live, defs);
        Assert("等于默认的键被剪掉 ⇒ 下次读盘自动跟随新默认",
            !json.Contains("QualityAvif") && !json.Contains("ChromaGainMap"), json);
        Assert("用户真正改过的键必须保留（WebP 50、色域 2）",
            json.Contains("QualityWebp") && json.Contains("ColorSpaceIndex"), json);

        // double 判等：源生成写出的数字是 15 vs 15.0 这类形态，格式化不一致就会一个键都剪不掉
        var a = new System.Text.Json.Nodes.JsonObject { ["q"] = System.Text.Json.Nodes.JsonValue.Create(15.0) };
        var b = new System.Text.Json.Nodes.JsonObject { ["q"] = System.Text.Json.Nodes.JsonValue.Create(15.0) };
        Assert("double 相等要判等（否则整个机制空转）", !Run(a, b).Contains("q"), "空转：一个键都没剪掉");

        // 版本号被剪掉 ⇒ 下次读成 0 ⇒ 迁移反复触发，所以它必须永远落盘
        var v = new System.Text.Json.Nodes.JsonObject { ["SettingsRevision"] = 1, ["q"] = 15 };
        var vd = new System.Text.Json.Nodes.JsonObject { ["SettingsRevision"] = 1, ["q"] = 15 };
        Assert("alwaysKeep 的键即使等于默认也留下",
            Run(v, vd, ["SettingsRevision"]).Contains("SettingsRevision"));

        Assert("默认表里没有的键（老版本写的未知字段）一律保留，删了等于丢用户数据",
            TrueToneCap.Core.Settings.SettingsDiffPruner.ShouldKeepUnknownKey(
                "LegacyThing", new System.Text.Json.Nodes.JsonObject()));
    }

    /// <summary>App 侧接线：Test 引用不到 App，只能钉调用点与"不许再出现第二处事实源"。</summary>
    static void Test_SettingsPersistence_Sentinels()
    {
        var srvPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        var mdlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        if (srvPath is null || mdlPath is null)
        {
            Skip("设置持久化接线", "未找到 SettingsService.cs / AppSettingsData.cs");
            return;
        }
        var srv = File.ReadAllText(srvPath);
        var mdl = File.ReadAllText(mdlPath);
        Assert("Save 走差集序列化，不再全量写回", srv.Contains("SerializeDiffs(Current)"));
        Assert("默认表唯一事实源是 CreateDefaults()（每次新建，不做可变单例）",
            mdl.Contains("public static AppSettingsData CreateDefaults() => new()")
            && !mdl.Contains("static readonly AppSettingsData"));
        // 注：ClampTo 不许写数字兜底，由 Test_Settings_QualityDefaultsSingleSource 统一测
        //（它同时管接线齐全与指对字段，这里再解析一遍只会多一处会漂移的正则）。
        Assert("修订迁移只碰质量与色域；色度/位深是 2026-09-19 拍板的产品决定，不许顺手动",
            !System.Text.RegularExpressions.Regex.IsMatch(srv, @"s\.(Chroma|BitDepth)\w+ = dft?\.")
            && srv.Contains("MigrateStaleDefaults"));

        // ═══ 迁移门的两个坏形态，各钉一处（2026-09-25 §49：门对"无修订号的老文件"从未触发过）═══
        // 行为级判据在 App 侧 SelfTest.RunSettingsMigrationCheck（Test 引用不到 App，只能钉形态），
        // 两条各自咬一种：① 初值写成当前号 ⇒ 缺键被读成"已迁移"；② 只在初值上做文章、
        // 写盘不盖章 ⇒ 新装机存一次就得 0，下次启动把用户刚挑的档清成默认。
        var mdlCode = StripLineComments(mdl);
        var srvCode = StripLineComments(srv);
        Assert("SettingsRevision 的初值不许是【非 0】的任何值（缺键必须读作未迁移；写 0 等价于不写）",
            !System.Text.RegularExpressions.Regex.IsMatch(
                mdlCode, @"SettingsRevision\s*\{\s*get;\s*set;\s*\}\s*=(?!\s*0)"),
            "初值带非 0 ⇒ STJ 用它填缺键 ⇒ MigrateStaleDefaults 恒不触发");
        Assert("Save() 必须在序列化前把修订号钉成当前号（唯一的写盘出口）",
            srvCode.IndexOf("Current.SettingsRevision = AppSettingsData.CurrentSettingsRevision;",
                System.StringComparison.Ordinal) >= 0
            && srvCode.IndexOf("Current.SettingsRevision = AppSettingsData.CurrentSettingsRevision;",
                   System.StringComparison.Ordinal)
               < srvCode.IndexOf("SerializeDiffs(Current)", System.StringComparison.Ordinal),
            "缺这一行 ⇒ 新装机的文件盖 0 号章，重启即被迁移门清成默认档");
        Assert("迁移结果必须驱动写盘（否则交还的默认值只活在内存里，重启即复原）",
            srvCode.Contains("migrated |= MigrateStaleDefaults(") && srvCode.Contains("if (migrated) Save();"),
            "返回值没人接 ⇒ 门开了也不落盘");
    }

    /// <summary>
    /// 「JPEG Gain Map 并入 JPEG LI」的重映射与接线（2026-09-26 用户口径）。
    /// <para>为什么这是本轮最需要判据的一处：<c>FormatIndex</c> 存的是<b>裸索引</b>，
    /// 旧表 1=GainMap、2=JPEG LI，新表 1=JPEG LI。而全仓对它的唯一旧防线是越界钳制
    /// （<c>is &lt; 0 or &gt; 6</c>）—— 删掉一项之后，旧值 1 与新默认值都仍在合法区间内 ⇒
    /// 钳制【永不触发】，用户静默换了输出格式，界面上看不出任何异常。
    /// 同型缺陷在 ColorSpaceIndex 上已经发生过一次。</para>
    /// <para>所以整张重映射表逐格对撞，且每格都额外核对<b>翻译后的格式名</b>——
    /// 只比数字的话，"旧 1→新 1"这格（同号不同义，最危险的一格）看起来毫无变化，
    /// 而它恰恰是 GainMap 用户会不会被静默降成普通 JPEG LI 的分界。</para>
    /// </summary>
    static void Test_GainMapMerged_FormatIndexMigration()
    {
        var table = new (int Legacy, string OldName, int NewIdx, string NewName, bool Gm)[]
        {
            (0, "PNG",          0, "PNG",     false),
            (1, "JPEG_GAINMAP", 1, "JPEG_LI", true),    // 同号不同义：整次迁移最危险的一格
            (2, "JPEG_LI",      1, "JPEG_LI", false),
            (3, "JPEG_XL",      2, "JPEG_XL", false),
            (4, "AVIF",         3, "AVIF",    false),
            (5, "WebP",         4, "WebP",    false),
            (6, "TIFF",         5, "TIFF",    false),
        };
        foreach (var c in table)
        {
            var (idx, gm) = FOrder.RemapLegacy(c.Legacy);
            Assert($"旧索引 {c.Legacy}（{c.OldName}）→ {c.NewIdx}（{c.NewName}）+ 增益图={(c.Gm ? "开" : "关")}",
                idx == c.NewIdx && gm == c.Gm && FOrder.FormatAt(idx).ToString() == c.NewName,
                $"实得 index={idx} 翻译后={FOrder.FormatAt(idx)} gainmap={gm}");
        }

        Assert("新表不含 JPEG_GAINMAP（它不再是用户可选项）",
            !FOrder.UiFormats.Contains(OutputFormat.JPEG_GAINMAP));
        Assert("新表 6 项 / 上界 5 / 默认落在 PNG（2026-09-26 产品口径：默认编码器 = PNG，失败模式要「无损但大」）",
            FOrder.UiFormats.Length == 6 && FOrder.MaxIndex == 5
            && FOrder.FormatAt(FOrder.DefaultIndex) == OutputFormat.PNG
            && FOrder.DefaultIndex == 0);
        Assert("旧表 7 项、1 号确为 GainMap、2 号确为 JPEG LI（这张表是「盘上那个数字原本指谁」的证词）",
            FOrder.LegacyUiFormats.Length == 7
            && FOrder.LegacyUiFormats[1] == OutputFormat.JPEG_GAINMAP
            && FOrder.LegacyUiFormats[2] == OutputFormat.JPEG_LI);
        Assert("越界索引：FormatAt 回落默认格式（PNG）、RemapLegacy 回落默认且不开增益图（不得抛）",
            FOrder.FormatAt(9) == OutputFormat.PNG && FOrder.FormatAt(-1) == OutputFormat.PNG
            && FOrder.RemapLegacy(9).Index == FOrder.DefaultIndex && !FOrder.RemapLegacy(9).GainMapEnabled
            && FOrder.RemapLegacy(-1).Index == FOrder.DefaultIndex);

        // 缺陷判据（2026-09-26 用户报"默认选项缺失"）：换 ItemsSource 会把 SelectedIndex 打回 -1，
        // 而 RebuildFormatCombo() 每次 ApplyLocale() 都跑、构造期又排在 ApplySettingsToUI() 之后
        // ⇒ 每次开机格式下拉都是空的。判据只能钉形态（Test 工程引用不到 App）。
        // ⚠ 形态匹配必须先在**剥掉注释**的文本上做：这个函数体内就有一段解释 WinUI 行为的注释，
        //   里面写着 "SelectedIndex" —— 拿原文找锚点会先命中那行注释，于是"把读取挪到赋值之后"
        //   这种真实退化【照样绿】（2026-09-26 变异实测：挪序后 --unit-tests 851 全过）。
        var mxcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var mxcBody = StripComments(File.ReadAllText(mxcPath!));
        int rbAt = mxcBody.IndexOf("private void RebuildFormatCombo()", System.StringComparison.Ordinal);
        var rbBody = rbAt < 0 ? "" : mxcBody.Substring(rbAt, mxcBody.IndexOf("\n    }", rbAt, System.StringComparison.Ordinal) - rbAt);
        int firstSel = rbBody.IndexOf("SelectedIndex", System.StringComparison.Ordinal);
        int assignSel = rbBody.IndexOf("SelectedIndex =", System.StringComparison.Ordinal);
        int itemsAt = rbBody.IndexOf("ItemsSource =", System.StringComparison.Ordinal);
        Assert("RebuildFormatCombo 必须在换 ItemsSource 前读回选中项、换完后重新赋值"
               + "（否则格式下拉在每次开机/每次切语言后是空的）",
            rbBody.Length > 0
            && firstSel >= 0 && firstSel < itemsAt          // 先读
            && assignSel > itemsAt                           // 后写
            && rbBody.Contains("FormatOrder.DefaultIndex"),   // 未选中时的落点 = 产品默认
            $"读选中@{firstSel} / ItemsSource@{itemsAt} / 写选中@{assignSel}");

        // ── App 侧接线（Test 引用不到 App，只能钉形态；行为级在 SelfTest --settings-migration）──
        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var pipePath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (svcPath is null || mwPath is null || pipePath is null)
        { Skip("增益图合并的 App 侧接线", "未找到源文件"); return; }
        var svc = StripLineComments(File.ReadAllText(svcPath));
        var mw = StripLineComments(File.ReadAllText(mwPath));
        var pipe = StripLineComments(File.ReadAllText(pipePath));

        Assert("迁移被 Load 调用，且盖章发生在唯一的写盘出口 Save()",
            svc.Contains("migrated |= MigrateGainMapMerge(Current);")
            && svc.Contains("Current.GainMapMerged = true;"));
        Assert("门用 GainMapMerged，不借 SettingsRevision（借默认表版本触发会再清一次用户质量档）",
            svc.Contains("if (s.GainMapMerged) return false;"));
        Assert("FormatIndex 的合法上界取自 FormatOrder，不许再写死 6",
            !svc.Contains("s.FormatIndex is < 0 or > 6") && svc.Contains("FormatOrder.MaxIndex"));
        Assert("开关→格式的翻译只有一个出口，编码快照走它",
            mw.Contains("private OutputFormat CurrentFormat()") && mw.Contains("Format: CurrentFormat()"));
        Assert("增益图开关的落盘必须挡住程序赋值（§49 那条缺陷在复选框上的同一形态）",
            mw.Contains("if (!_uiReady || _qualityPanelUpdating) return;"));
        Assert("管线里不得再有第二份「格式→质量字段」switch（三份抄写就是这次合并要消掉的东西）",
            !pipe.Contains("OutputFormat.JPEG_GAINMAP => s.QualityGainMap")
            && pipe.Contains("s.GetQuality(format)"));

        // ═══ 2026-09-26 补：格式下拉的标签曾经有【第二份清单】，摘掉 GainMap 后整列错位 ═══
        // 现场：Localization 里另抄的 7 项标签数组在构造期就把 ItemsSource 换掉，
        // 于是「JPEG Gain Map (HDR)」编成 JPEG LI、「JPEG LI」编成 JPEG XL、「WebP」编成 TIFF、
        // 末尾「BMP」被钳成 TIFF，而真正的 TIFF 选不到 —— 7 个可见项里 5 个点谁都不是谁。
        // 上面那条"三份抄写"的判据管的是字段映射，管不到标签表，所以这里补一条针对项序的锁。
        var locPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Localization.cs"));
        var lmPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (locPath is null || lmPath is null) { Skip("格式下拉的标签来源", "未找到源文件"); return; }
        var loc = StripLineComments(File.ReadAllText(locPath));
        var lm = StripLineComments(File.ReadAllText(lmPath));
        Assert("格式下拉的 ItemsSource 只能由一个构建口生成（标签数组不许另抄一份）",
            loc.Contains("RebuildFormatCombo()")
            && !loc.Contains("FormatCbo.ItemsSource")
            && !loc.Contains("fmtLabels = new[]")
            && !lm.Contains("FmtJpegGainMap") && !lm.Contains("FmtBmp"));
        // 构建口本身：**整个 App 工程**里 `FormatCbo.ItemsSource` 只能被赋值一次，且紧跟一次契约自检。
        // 扫描范围故意不限定在 xaml.cs —— 这类"另抄一份清单"的缺陷正是从别的文件里长出来的，
        // 只钉一个文件等于给下一次留了个别的文件。
        var appDir = Path.GetDirectoryName(mwPath)!;
        var assignSites = new System.Collections.Generic.List<string>();
        foreach (var cs in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories))
        {
            if (cs.Contains("\\obj\\") || cs.Contains("\\bin\\")
                || cs.Contains("/obj/") || cs.Contains("/bin/")) continue;
            int hits = Regex.Matches(StripLineComments(File.ReadAllText(cs)),
                "FormatCbo\\.ItemsSource\\s*=").Count;
            for (int k = 0; k < hits; k++) assignSites.Add(Path.GetFileName(cs));
        }
        Assert("整个 App 只有一处能给格式下拉赋值（两处以上就等于允许第二份清单）",
            assignSites.Count == 1, $"实得 {assignSites.Count} 处：{string.Join(",", assignSites)}");
        Assert("唯一的构建口自带显示层↔编码层↔契约表三方自检，且错位只报 Error 不静默修复",
            Regex.IsMatch(mw, "private void RebuildFormatCombo\\(\\)[\\s\\S]{0,400}CheckFormatComboContract\\(\\);")
            && mw.Contains("private void CheckFormatComboContract()")
            && mw.Contains("LogService.Error(\"MainWindow\""));
        // 界面格式表 vs Core 的 FormatIndex 契约表：逐项同序。这两张表任何一张单独漂了，
        // 老配置里的裸索引就会"静默指向别的格式"——§52 与本轮的错位都是这么来的。
        var fmtBlock = Regex.Match(mw, "_formats\\s*=\\s*\\[([\\s\\S]*?)\\];");
        Assert("能解析到界面格式表（解析不到就是改了写法，判据会静默失效）", fmtBlock.Success);
        var uiList = Regex.Matches(fmtBlock.Groups[1].Value, @"OutputFormat\.(\w+)")
                         .Select(m => m.Groups[1].Value).ToList();
        var contract = FOrder.UiFormats.Select(f => f.ToString()).ToList();
        Assert("_formats 与 FormatOrder.UiFormats 逐项同序同数（FormatIndex 的两侧必须是同一张表）",
            string.Join(",", uiList) == string.Join(",", contract),
            $"界面={string.Join(",", uiList)} 契约={string.Join(",", contract)}");
        Assert("格式表不许再自带标签（标签只有一个来源：LocaleManager.GetFormatLabel）",
            !Regex.IsMatch(fmtBlock.Value, "\"[^\"]+\""));
        var missingLabel = FOrder.UiFormats
            .Where(f => !lm.Contains($"OutputFormat.{f} =>"))
            .Select(f => f.ToString());
        Assert("每个界面格式都必须有显式标签档（漏一格就在下拉里显示裸枚举名）",
            !missingLabel.Any(), string.Join(",", missingLabel));
        Assert("增益图不许回到格式标签里（它是 JPEG LI 面板里的开关）",
            !lm.Contains("OutputFormat.JPEG_GAINMAP =>"));

        // 同一类毛病的另一处：驱动脚本把"要驱动哪个 exe"和"去哪儿读日志"写成两条独立路径，
        // 跑 Release 树时它还在看 Debug 目录 ⇒ 观察不到任何变化却照样跑完（§e2e 那轮就是这么判红的）。
        var manualDir = FindRepoFile(Path.Combine("tests", "manual", "smoke-anno-test.ps1"));
        if (manualDir is not null)
        {
            var badPathHardcode = new System.Collections.Generic.List<string>();
            foreach (var ps in Directory.GetFiles(Path.GetDirectoryName(manualDir)!, "*.ps1", SearchOption.AllDirectories))
                if (Regex.IsMatch(File.ReadAllText(ps), "bin\\\\x64\\\\(Debug|Release)[^\"']*\\\\log\""))
                    badPathHardcode.Add(Path.GetFileName(ps));
            Assert("tests/manual 的驱动脚本不许把日志目录写成第二条硬路径（必须从被驱动进程推）",
                badPathHardcode.Count == 0, string.Join(",", badPathHardcode));
        }

        // ═══ BMP：一个没有产品入口的格式不构成"支持的格式"（2026-09-26 删除）═══
        // `OutputFormat` 无 BMP、EncoderFactory 无 BMP 档、界面下拉无 BMP —— 唯一调用点是
        // 门禁自己的两个专项测试。测一个用户拿不到的格式，只会让它看起来仍然被支持。
        var bmpSrc = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "ManagedBmpEncoder.cs"));
        Assert("BMP 托管编码器已随格式摘除（源码里不再存在该文件与类型）",
            bmpSrc is null && !lm.Contains("\"BMP\"") && !mw.Contains("ManagedBmpEncoder"));
    }

    /// <summary>
    /// LlmConfig 必须注入有道凭据 —— 否则 TranslationService 的判空必然成立，
    /// 有道后端永不执行，且被静默降级到 Google、界面无任何提示。
    /// </summary>
    static void Test_LlmConfig_InjectsYoudaoCredentials()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (path is null) { Skip("LlmConfig 有道凭据注入", "未找到源文件"); return; }

        var text = File.ReadAllText(path);
        int idx = text.IndexOf("new LlmConfig", StringComparison.Ordinal);
        if (idx < 0) { Assert("找到 LlmConfig 构造", false); return; }

        int end = text.IndexOf('}', idx);
        var block = end > idx ? text[idx..end] : text[idx..];
        Assert("LlmConfig 注入了 YoudaoAppKey（缺失则有道后端永不执行）", block.Contains("YoudaoAppKey"));
        Assert("LlmConfig 注入了 YoudaoAppSecret", block.Contains("YoudaoAppSecret"));
    }

    /// <summary>
    /// StartupManager 读取必须能解析"带引号 + 参数"的命令行。
    /// 写入的是 "\"path\" --autostart"，若读取仍与裸路径严格相等 ⇒ IsEnabled 恒 false。
    /// </summary>
    static void Test_StartupManager_ReadHandlesQuotedCommand()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "StartupManager.cs"));
        if (path is null) { Skip("StartupManager 命令行解析", "未找到源文件"); return; }

        var text = File.ReadAllText(path);
        Assert("StartupManager: 写入的是带引号+参数的命令行", text.Contains("--autostart"));

        // ═══ 负向收紧为正则 ═══
        // 原写法 !text.Contains("val == GetAppPath()") 只要改写成 value ==、加个空格、
        // 或换成 val.Equals(GetAppPath()) 就绕过 —— 而那三种写法同样会让 IsEnabled 恒 false。
        Assert("StartupManager: 读取端不得把注册表原值直接与 GetAppPath() 比较（否则恒为 false）",
            !Regex.IsMatch(text, @"(val|value)\s*(?:==|\.Equals\s*\()\s*GetAppPath\s*\(\s*\)"));

        // ═══ 正向锚点 ═══
        // 光有负向检查，随便换成第三种错写法也能绿；App 层类型 Test 工程引用不到，
        // 文本级是本类唯一选项 ⇒ 那就必须把【修好之后的那个表达式】本身钉住：
        // 比较发生在"从命令行还原出的 exe 路径"与 GetAppPath() 之间，且忽略大小写。
        Assert("StartupManager: 读取端把还原后的路径与 GetAppPath() 做 OrdinalIgnoreCase 比较",
            Regex.IsMatch(text, @"Equals\s*\(\s*GetAppPath\s*\(\s*\)\s*,\s*StringComparison\.OrdinalIgnoreCase"));
        Assert("StartupManager: 读取端先从命令行还原 exe 路径（带引号与裸路径两种写法都要处理）",
            text.Contains("IndexOf('\"', 1)")     // "C:\a b.exe" --autostart → C:\a b.exe
            && text.Contains("\" --\""));          // C:\a.exe --autostart     → C:\a.exe
    }

    /// <summary>提取 XAML 中指定 ComboBox 块内的 ComboBoxItem Tag。</summary>
    static List<string> ExtractComboItemTags(string xaml, string comboBoxName)
    {
        int idx = xaml.IndexOf(comboBoxName, StringComparison.Ordinal);
        if (idx < 0) return new List<string>();
        int end = xaml.IndexOf("</ComboBox>", idx, StringComparison.Ordinal);
        var block = end > idx ? xaml[idx..end] : xaml[idx..];
        return ExtractQuotedValues(block, "Tag=\"");
    }

    /// <summary>提取 IsOneOf(s.Xxx, "a", "b", ...) 括号内的合法值。</summary>
    static List<string> ExtractIsOneOfArgs(string cs, string propertyRef)
    {
        int idx = cs.IndexOf("IsOneOf(" + propertyRef, StringComparison.Ordinal);
        if (idx < 0) return new List<string>();
        int close = cs.IndexOf(')', idx);
        var seg = close > idx ? cs[idx..close] : cs[idx..];
        return ExtractQuotedValues(seg, "\"");
    }

    /// <summary>提取形如 prefix"值" 的所有去重复字符串。</summary>
    static List<string> ExtractQuotedValues(string text, string prefix)
    {
        var list = new List<string>();
        int i = 0;
        while ((i = text.IndexOf(prefix, i, StringComparison.Ordinal)) >= 0)
        {
            int s = i + prefix.Length;
            if (s >= text.Length) break;
            int e = text.IndexOf('"', s);
            if (e < 0) break;
            var v = text[s..e];
            if (v.Length > 0 && !list.Contains(v, StringComparer.Ordinal)) list.Add(v);
            i = e + 1;
        }
        return list;
    }

    // ═══════════════════════════════════════
    //  D3D 设备丢失判定（2026-09-15）
    // ═══════════════════════════════════════

    /// <summary>
    /// 设备丢失的 HRESULT 分类。
    /// <para>此前全仓没有任何 DeviceRemoved 判定：GPU 驱动重置（TDR）、显卡切换、
    /// 远程桌面会话变更之后，渲染每帧都在同一个已死的设备上失败 —— 日志刷屏、
    /// 画面永久黑屏，而用户拿不到任何提示，只能杀进程重开。</para>
    /// 本组用例锁定判定语义：误判会让"普通错误"也被当成设备丢失，触发无谓的重建。
    /// </summary>
    static void Test_D3DDeviceLost_HResultClassification()
    {
        // 常量值本身（写错一位就完全失效，且极难发现）
        Assert("D3DDeviceLost: DXGI_ERROR_DEVICE_REMOVED == 0x887A0005",
            D3DDeviceLost.DXGI_ERROR_DEVICE_REMOVED == unchecked((int)0x887A0005));
        Assert("D3DDeviceLost: DXGI_ERROR_DEVICE_RESET == 0x887A0007",
            D3DDeviceLost.DXGI_ERROR_DEVICE_RESET == unchecked((int)0x887A0007));

        // 应判定为设备丢失
        Assert("D3DDeviceLost: DEVICE_REMOVED 判为丢失",
            D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.DXGI_ERROR_DEVICE_REMOVED));
        Assert("D3DDeviceLost: DEVICE_RESET 判为丢失",
            D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.DXGI_ERROR_DEVICE_RESET));
        Assert("D3DDeviceLost: DRIVER_INTERNAL_ERROR 判为丢失",
            D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.DXGI_ERROR_DRIVER_INTERNAL_ERROR));
        Assert("D3DDeviceLost: D3DDDIERR_DEVICEREMOVED 判为丢失",
            D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.D3DDDIERR_DEVICEREMOVED));

        // 不应误判（过度重建同样有害）
        Assert("D3DDeviceLost: S_OK 不判为丢失", !D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.S_OK));
        Assert("D3DDeviceLost: E_OUTOFMEMORY 不判为丢失（应通过缩小资源恢复，而非重建设备）",
            !D3DDeviceLost.IsDeviceLostHResult(D3DDeviceLost.E_OUTOFMEMORY));
        Assert("D3DDeviceLost: DXGI_ERROR_INVALID_CALL 不误判",
            !D3DDeviceLost.IsDeviceLostHResult(unchecked((int)0x887A0001)));
    }

    static void Test_D3DDeviceLost_ExceptionChain()
    {
        Assert("D3DDeviceLost: COMException(DEVICE_REMOVED) 判为丢失",
            D3DDeviceLost.IsDeviceLostException(new System.Runtime.InteropServices.COMException(
                "device removed", D3DDeviceLost.DXGI_ERROR_DEVICE_REMOVED)));

        // 包装在内部异常链中（Vortice / 第三方封装常见）
        var inner = new System.Runtime.InteropServices.COMException("reset", D3DDeviceLost.DXGI_ERROR_DEVICE_RESET);
        Assert("D3DDeviceLost: 内部异常链中的 DEVICE_RESET 判为丢失",
            D3DDeviceLost.IsDeviceLostException(new InvalidOperationException("wrap", inner)));

        Assert("D3DDeviceLost: 普通异常不判为丢失",
            !D3DDeviceLost.IsDeviceLostException(new InvalidOperationException("boom")));
        Assert("D3DDeviceLost: null 不判为丢失", !D3DDeviceLost.IsDeviceLostException(null));

        // 未设置 HResult 的异常不应导致误判
        Assert("D3DDeviceLost: 无 HResult 的异常不判为丢失",
            !D3DDeviceLost.IsDeviceLostException(new Exception("plain")));
    }

    static void Test_D3DDeviceLost_HResultName()
    {
        Assert("D3DDeviceLost: 名称映射 DEVICE_REMOVED",
            D3DDeviceLost.GetHResultName(D3DDeviceLost.DXGI_ERROR_DEVICE_REMOVED) == "DXGI_ERROR_DEVICE_REMOVED");
        Assert("D3DDeviceLost: 名称映射 S_OK",
            D3DDeviceLost.GetHResultName(D3DDeviceLost.S_OK) == "S_OK");
        Assert("D3DDeviceLost: 未知码回落为十六进制（便于日志排查）",
            D3DDeviceLost.GetHResultName(0x12345678) == "0x12345678");
    }

    // ═══════════════════════════════════════
    //  NVENC 失败分类（2026-09-15）
    // ═══════════════════════════════════════

    /// <summary>
    /// NVENC 失败必须区分"本次输入不适用"与"硬件不可用"。
    /// <para>本地发布时实测：96×96 的图会触发
    /// <c>hr=0x00000008 "Frame dimensions are less than the minimum supported value"</c>。
    /// 原实现把任何失败都缓存成 <c>s_nvencFailed = true</c>，
    /// 于是<b>一张小图失败后，后续所有 4K 大图都永久退回 libaom</b>
    ///（实测 4K AVIF 硬件 ~0.12s vs 软件 ~22s，约 180 倍），
    /// 用户只会觉得"变慢了"，只能靠重启恢复。</para>
    /// </summary>
    /// <summary>
    /// 无损格式那根质量滑块的收起方式（#26），四条源码哨兵。
    /// <para>为什么只能测源码、不能测行为：这是界面可见性，Test 项目不引用 App，
    /// 也没有可渲染的宿主 ⇒ 只能把"写法"钉住。三条各防一种退化：</para>
    /// <list type="number">
    /// <item><b>判据必须是"区间退化"而不是"点名格式"</b>。旧写法
    /// <c>QualitySld.IsEnabled = format != OutputFormat.PNG</c> 就是点名 PNG，结果同为 (100,100)
    /// 的 TIFF 漏在外面 —— 一根 enabled 却划不动的滑块。同一条事实写两遍必漏一遍。</item>
    /// <item><b>描述行只能有一个写入点</b>。<c>OnQualityChanged</c> 原先无条件写
    /// <c>GetQualityDescription</c>，会把无损格式那句"无损 (固定 100%)"冲成一个孤零零的 100；
    /// 当时不出事纯靠赋值顺序，而"靠顺序才对"正是本仓库反复踩的隐性耦合。</item>
    /// <item><b>XAML 那行必须还叫这个名字</b>：代码里按 <c>QualityRow</c> 引用，
    /// 改名会让编译期就断，但反过来"名字在、却忘了收"只能靠这条盯。</item>
    /// </list>
    /// </summary>
    static void Test_LosslessQualityRowCollapsed_SourceSentinels()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var xaPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        var encPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "ImageEncoder.cs"));
        if (mwPath is null || xaPath is null || encPath is null)
        { Skip("无损格式收起质量行", "未找到源文件"); return; }
        var mw = StripLineComments(File.ReadAllText(mwPath));
        var xa = File.ReadAllText(xaPath);
        var enc = File.ReadAllText(encPath);

        Assert("界面按共享判据 QualityIsFixed 决定收起（不是点名某个格式）",
            mw.Contains("encoder.QualityIsFixed")
            && mw.Contains("QualityRow.Visibility = noQualityKnob"));
        Assert("判据本身在 Core 且写作『区间退化』（max<=min），界面不许再算第二遍",
            enc.Contains("max <= min"));
        Assert("旧写法不得复活：QualitySld.IsEnabled = format != OutputFormat.PNG（那条漏掉 TIFF）",
            !System.Text.RegularExpressions.Regex.IsMatch(
                mw, @"QualitySld\.IsEnabled\s*=\s*format\s*!="));
        Assert("质量描述行只有一个写入点（两处各写就会互相覆盖）",
            System.Text.RegularExpressions.Regex.Matches(mw, @"QualityDescTxt\.Text\s*=").Count == 1,
            $"实得 {System.Text.RegularExpressions.Regex.Matches(mw, @"QualityDescTxt\.Text\s*=").Count} 处");
        Assert("XAML 里那行仍带 x:Name=QualityRow（代码按它收起整行）",
            xa.Contains("x:Name=\"QualityRow\""));
    }

    /// <summary>
    /// 程序性刷新质量面板<b>不得</b>回写设置（#29 的第二条，与迁移门相互独立）。源码哨兵。
    /// <para>实机回归（2026-09-25）：夹具存 <c>QualityGainMap=2.0</c>（区间 0.5–3.0 的中段，
    /// 既非端点也不越界），启动后<b>界面与文件双双变成 0.5</b>。机制：
    /// <c>UpdateQualityPanel</c> 赋 <c>Minimum=0.5</c> 时 <c>RangeBase</c> 就地钳制 <c>Value</c>
    /// 并抛 <c>ValueChanged</c>，而 <c>OnQualityChanged</c> 把这条程序事件当成"用户拖了滑块"
    /// 写进 <c>_settings</c>；同一方法后半段又要 <c>GetQuality()</c> 从 <c>_settings</c>
    /// 读回"用户存的值" ⇒ 前半段的副作用被后半段当输入读走，一个方法改掉了自己的输入。</para>
    /// <para>为什么只能钉源码：这条藏在 WinUI 控件的事件时序里，Test 不引用 App、无可渲染宿主。</para>
    /// <para>三条各咬一种退化：① 守卫被摘掉（改回只判 <c>_uiReady</c>）；② 复位没走
    /// <c>finally</c>（Core 一旦抛异常，标志永久为 true ⇒ 用户此后拖动再也不落盘，
    /// 那是比原缺陷更坏的静默失效）；③ 面板主体被搬回 <c>UpdateQualityPanel</c> 本体
    /// （包装失去作用域 ⇒ 等价于①）。</para>
    /// </summary>
    static void Test_QualityPanelProgrammaticWrite_SourceSentinels()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (mwPath is null) { Skip("质量面板程序赋值不回写", "未找到 MainWindow.xaml.cs"); return; }
        var mw = StripLineComments(File.ReadAllText(mwPath));

        Assert("OnQualityChanged 的入口守卫必须认这个标志（程序赋值≠用户意图）",
            System.Text.RegularExpressions.Regex.IsMatch(
                mw, @"OnQualityChanged[\s\S]{0,220}?if \([^\)]*_qualityPanelUpdating[^\)]*\)\s*return;"),
            "守卫里没有 _qualityPanelUpdating ⇒ 程序钳制会被写成用户选择");
        Assert("标志的复位必须走 finally（否则 Core 抛异常后用户拖动永久不落盘）",
            mw.Contains("_qualityPanelUpdating = true;")
            && mw.Contains("finally { _qualityPanelUpdating = false; }"));
        Assert("面板主体在 UpdateQualityPanelCore 里（包装才有作用域可言）",
            mw.Contains("private void UpdateQualityPanelCore()")
            && mw.Contains("try { UpdateQualityPanelCore(); }"));
    }

    /// <summary>
    /// 「改了设置就要落盘」的全仓扫描（用户口径：设置 JSON 与软件同目录，改设置时更新它）。
    /// <para>背景（2026-09-25 审计）：写 <c>_settings.X = ...</c> 的站点共 116 处，而落盘调用点只有 16 处，
    /// 逐行看不出来谁漏了。扫到 4 个真漏网，其中两个是<b>纯用户选择</b>：
    /// <c>OnOcrLangChanged</c>（OCR 语言）与 <c>OnTranslationModeChanged</c>（翻译模式）——
    /// 它们只改内存对象，全量 <c>SaveSettings()</c> 又只挂在托盘<b>退出</b>那一条路径上
    /// （<c>MainWindow.xaml.cs:234</c>），而本程序按"开机自启 + 最小化到托盘"设计要连续跑好几天
    /// ⇒ 用户选的这两项在崩溃/注销/直接关机时静默丢失，症状是"我明明改过，重启又变回去"且日志无线索。
    /// 另两处是<b>注释承诺了却没做</b>：<c>ApplySettingsToUI</c> 里那句"立刻落回设置对象，
    /// 否则文件里长期留着旧索引"——只改对象不改文件，旧索引照样在盘上。</para>
    /// <para>规则写成扫描而不是点名几个方法：新加一个 handler 忘记落盘，这条要当场红。
    /// 豁免必须<b>点名并给理由</b>，不许留空子。</para>
    /// </summary>
    static void Test_SettingsWriteSites_AllPersist()
    {
        var names = new[] { "MainWindow.xaml.cs", "MainWindow.Settings.cs", "MainWindow.OcrSettings.cs" };
        // 派生归一化：值来自硬件能力而非用户选择，且随调用方 handler 末尾的 Save 一并落盘 ⇒ 不属本类
        var derived = new[] { "UpdateAvifOptionAvailability" };
        var sig = new System.Text.RegularExpressions.Regex(
            @"^    (private|public|internal|protected|static|async|override|partial|unsafe)[^=;]*\(");
        var write = new System.Text.RegularExpressions.Regex(@"_settings\.[A-Za-z]\w* *= (?!/)");
        var save = new System.Text.RegularExpressions.Regex(
            @"Settings\.Save(Quiet)?\(\)|SaveSettings(Quiet)?\(\)");

        int sites = 0, methods = 0;
        var offenders = new List<string>();
        foreach (var n in names)
        {
            var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", n));
            if (p is null) { Skip("设置写入点全部落盘", $"未找到 {n}"); return; }
            string cur = "(文件级)"; bool hasWrite = false, hasSave = false;
            void Flush()
            {
                if (!hasWrite) return;
                methods++;
                if (!hasSave && !derived.Any(d => cur.Contains(d))) offenders.Add($"{n}:{cur}");
            }
            foreach (var raw in File.ReadAllLines(p))
            {
                if (sig.IsMatch(raw)) { Flush(); cur = raw.Trim(); hasWrite = hasSave = false; }
                var line = raw.TrimStart();
                if (line.StartsWith("//")) continue;          // 注释里的示例不算写入点
                if (write.IsMatch(line)) { sites++; hasWrite = true; }
                if (save.IsMatch(line)) hasSave = true;
            }
            Flush();
        }

        Assert("确实扫到了写入点（扫描本身不许空过）", sites >= 100, $"实得 {sites} 处赋值 / {methods} 个方法");
        Assert("每个给 _settings 赋值的方法都必须自己落盘（豁免需点名）",
            offenders.Count == 0, offenders.Count == 0 ? "" : string.Join("，", offenders));
    }

    /// <summary>
    /// 「设置存得下来吗」必须用<b>真写一次</b>来判（#31 第二条，判据在 Core ⇒ 可行为级测，不必读源码）。
    /// <para>背景：原实现的探测只看"文件不存在 ⇒ 同目录可用；存在且带只读位 ⇒ 才降级"。
    /// 而真实世界存不下去的主因（装进 Program Files 被 ACL 拒写、只读介质、策略拦写）
    /// 恰恰是<b>文件还不存在</b>的形态 ⇒ 探测一路放行，随后每次 Save 抛异常，
    /// 调用方又全走 <c>SaveQuiet()</c> 的空 <c>catch { }</c> ⇒ 用户改什么都没保存、日志里一个字都没有。
    /// 目录的只读属性位在 Windows 上不代表不可写（系统忽略它），所以属性位那条路根本测不到这件事。</para>
    /// <para>下面每条都标了性质：<b>独立参照</b>=对 ACL 拒写目录必须判 false（旧实现在这里必然误判 true，
    /// 这是本判据存在的唯一理由）；<b>会动的读数</b>=同一个目录摘掉只读位后必须翻回 true
    /// （否则"判 false"可能只是恒假，抓不住任何东西）。</para>
    /// </summary>
    static void Test_SettingsWriteProbe_RealProbe()
    {
        bool CanPersist(string path) =>
            TrueToneCap.Core.Settings.SettingsWriteProbe.CanPersistSettings(path);
        bool CanWrite(string dir) =>
            TrueToneCap.Core.Settings.SettingsWriteProbe.CanWriteNewFile(dir);

        var dir = Path.Combine(Path.GetTempPath(), "ttc-probe-" + Guid.NewGuid().ToString("N")[..8]);
        var f = Path.Combine(dir, "TrueToneCap.settings.json");
        Directory.CreateDirectory(dir);
        try
        {
            Assert("空目录（真的可写）判为可持久化", CanPersist(f));
            Assert("探针不留垃圾：跑完目录必须还是空的", Directory.GetFiles(dir).Length == 0,
                string.Join("，", Directory.GetFiles(dir)));

            File.WriteAllText(f, "{}");
            File.SetAttributes(f, FileAttributes.ReadOnly);
            Assert("已存在且只读的文件判为不可持久化", !CanPersist(f));
            File.SetAttributes(f, FileAttributes.Normal);
            Assert("同一目录摘掉只读位后翻回可写（判据不是恒假）", CanPersist(f));

            // 独立参照 + 旧实现的误判点：目录在、里面本来就没有这个文件、但 ACL 拒写。
            // ⚠ 夹具必须挑"连管理员都拒"的目录：本机 shell 是【提权】的（实测 elevated=True），
            //   所以 System32 / Program Files 这些"常规不可写"的直觉答案在这里统统可写，
            //   拿它们当反例会让这条判据悄悄变成恒真。WindowsApps 属 TrustedInstaller，
            //   Administrators 没有写 ACE ⇒ 提权与不提权都判 false。
            var denied = @"C:\Program Files\WindowsApps";
            var deniedFile = Path.Combine(denied, "TrueToneCap.settings.json");
            Assert("反例夹具成立：WindowsApps 存在且里面没有这个文件（旧属性位判据会放行）",
                Directory.Exists(denied) && !File.Exists(deniedFile), denied);
            Assert("ACL 拒写的目录必须判为不可持久化",
                !CanPersist(deniedFile), $"{denied} 竟然可写 ⇒ 探针没有真的写一次");
            Assert("CanWriteNewFile 对拒写目录同样判 false", !CanWrite(denied));
            Assert("不存在的目录判为不可持久化",
                !CanPersist(Path.Combine(dir, "no-such-sub", "x.json")));
        }
        finally
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    /// <summary>
    /// App 侧接线：不可写时的口径是「记日志 + 按默认值」，且<b>只认同目录这一个位置</b>。
    /// <para>为什么还要钉源码：这套判定发生在进程启动早期（静态属性初始化），Test 引用的 Core
    /// 只能测判据本身，测不到 App 有没有照它办。三条各咬一种退化。</para>
    /// </summary>
    static void Test_SettingsPersistence_BlockedPath_Sentinels()
    {
        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        var appPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "App.xaml.cs"));
        if (svcPath is null || appPath is null)
        { Skip("不可写时的落盘口径", "未找到 SettingsService.cs / App.xaml.cs"); return; }
        var svc = StripLineComments(File.ReadAllText(svcPath));

        Assert("路径唯一事实源 = 软件同目录（不再有「降级到别处」的第二答案）",
            svc.Contains("Path.Combine(AppContext.BaseDirectory, SettingsFileName)")
            && !svc.Contains("ResolveSettingsPath"));
        Assert("Load/Save 都必须先问 CanPersist（存不下就不读也不写，整体按默认值）",
            svc.Contains("if (!CanPersist)") && svc.Contains("if (!CanPersist) { LogPersistBlocked(); return; }"));
        Assert("SaveQuiet 必须把失败写进日志（不许再有空吞）",
            svc.Contains("catch (Exception ex) { LogOnce($\"设置保存失败"),
            "SaveQuiet 又变回 catch { } ⇒ 改了设置没保存且零线索");
        Assert("失败日志按内容去重（同一种失败不刷屏，不同失败各留一条）",
            svc.Contains("if (s_lastLoggedIssue == msg) return;"));
        Assert("早于窗口的主题加载也要认同一个口径（不可写时直接默认主题）",
            File.ReadAllText(appPath).Contains("if (!SettingsService.CanPersist) return AppThemeMode.Default;"));
    }

    /// <summary>
    /// 降级提示的去重只能有【一条】规则：按消息内容比（<c>s_lastDegradedToast</c>），
    /// 不许再有"整个进程只弹一次"的布尔开关。
    /// <para>回归背景（2026-09-24，#22 的副产物）：本文件原先并存两套机制 —— 4 处位深/色度
    /// 协商按内容去重，而 <c>NotifyEncoderFallbackIfAny</c> 用一个 <c>s_hwFallbackNotified</c>
    /// 静态布尔做到"弹过一次就永久不弹"。后果是<b>第一条</b>降级（通常是 NVENC→libaom）
    /// 之后，本进程内<b>所有其他种类</b>的降级全部无声，包括 HDR+PNG 的位深协商(#21)
    /// 与 HDR TIFF 被改成 16-bit(#22)。"这次没有再提示"与"这个提示坏了"在用户端无法区分。</para>
    /// <para>第二条断言刻意写成模式匹配而不是点名那个变量：换个名字重犯同样要红。</para>
    /// </summary>
    static void Test_DegradationToast_SingleDedupRule()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (p is null) { Skip("降级提示只有一条去重规则", "未找到 CapturePipelineService.cs"); return; }
        var cps = StripLineComments(File.ReadAllText(p));

        Assert("降级提示按【消息内容】去重（与位深/色度协商同一规则）",
            cps.Contains("if (s_lastDegradedToast == reason) return;"));
        Assert("不许再出现『整个进程只弹一次』这类一次性布尔开关（改名重犯也算）",
            !System.Text.RegularExpressions.Regex.IsMatch(
                cps, @"private\s+static\s+(volatile\s+)?bool\s+s_\w*(Notified|Shown|Warned)"));
        // 这里原本还有第三条"源码里不得出现 s_hwFallbackNotified 字样"——写错了，删掉：
        // 本文件有一段注释正是用那个名字解释这次修复，断言不该去禁文档措辞（它当场把注释判红）。
        // 防重犯已由上面那条正则负责，第三条没有增量价值。
    }

    /// <summary>
    /// 源码哨兵在匹配前必须先去掉**整行注释**。
    /// <para>2026-09-24 实测出来的洞：把 `settings.OutputBitDepth = 10;` 注释掉做成半修变异，
    /// "必须含该语句"的断言照样绿 —— 因为 `Contains` 看见的是注释里的字面量。
    /// 同一个洞有两个方向：`must contain` 会被注释【喂饱】（假绿），
    /// `must not contain` 会被解释性注释【冤枉】（假红，本轮另一条断言就是这么误伤自己的）。</para>
    /// <para>只剥"以 // 或 /// 开头的整行"：不动行内内容，避免把字符串字面量里的
    /// `http://`、路径之类切掉。行尾注释（`code; // 说明`）保留代码部分，不影响判据。</para>
    /// </summary>
    /// <summary>读源码文本判据用的文件：一律先把 CRLF 归一成 LF。
    /// <para>2026-09-27 实测踩到的坑：几条"切方法体"的断言拿 <c>Environment.NewLine + "    }"</c>
    /// 当收尾锚点，而工作树里源码的行尾并不统一（<c>core.autocrlf=true</c> 之下检出的是 CRLF，
    /// 被脚本重写过的是 LF）。锚点找不到时旧写法退化成"拿整份文件继续判"，
    /// 于是一次无关的行尾变化报出三条红 —— 红得毫无道理，但暴露的是判据自己会失焦。</para></summary>
    static string ReadSourceNormalized(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    /// <summary>从 <paramref name="from"/> 起切出方法体（到缩进四空格的收尾 <c>}</c> 为止）。
    /// <para>锚点找不到 ⇒ 返回 <c>false</c> 且 <paramref name="body"/> 为空：调用方必须判红，
    /// 不许退化成"整份文件都算方法体"—— 那会让否定断言（"方法体里不许有 X"）恒假、
    /// 让肯定断言（"必须有 Y"）恒真，两个方向都是假绿灯。</para></summary>
    static bool TrySliceMethodBody(string text, int from, out string body)
    {
        int end = text.IndexOf("\n    }", from, StringComparison.Ordinal);
        body = end > from ? text[from..end] : "";
        return end > from;
    }

    static string StripLineComments(string src)
    {
        var kept = new List<string>();
        foreach (var line in src.Replace("\r\n", "\n").Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("//")) continue;
            kept.Add(line);
        }
        return string.Join("\n", kept);
    }

    /// <summary>
    /// HDR + PNG 的位深协商（#21）—— 源码哨兵，因为<b>这台机器根本走不到那条路</b>。
    /// <para>为什么只能钉源码：`--display-audit` 实测本机 <c>HDR 硬件支持=no</c>、
    /// 唯一那块屏 <c>分类=NarrowSdr</c>（ΔG=0.0246）、<c>实得源 Float16=无</c>、
    /// <c>多屏拼接的 HDR 源 →(无)</c> ⇒ 本机产不出任何一张真 HDR 截图，端到端实机验收
    /// 在这里对谁都做不到（先前把它记成"等用户给一张截图"是错的，2026-09-24 更正）。
    /// 编码器那一侧已有 <c>EncodedPng_HdrCicp_10BitSbit</c> 逐块读回取证；缺的是 App 决策层，
    /// 而它在 App 程序集内（Test 只引用 Core）⇒ 用源码哨兵钉写法。</para>
    /// <para>四条各防一种真发生过的半修：
    /// ① 守卫必须同时看 hdrOutput、格式、以及"位深确实不足"；
    /// ② <b>局部变量与 settings 字段必须一起改</b>——只改 <c>bitDepth</c> 的话，
    ///    后面 <c>settings.OutputBitDepth = bitDepth</c> 早已赋过，编码器仍拿 8；
    /// ③ 位置必须在 <c>settings.HdrOutput = hdrOutput</c> 之后（协商依赖场景已定）；
    /// ④ 必须留痕（日志 + Toast），且走内容去重而不是进程级一次性布尔。</para>
    /// </summary>
    static void Test_HdrPngDepthNegotiation_SourceSentinels()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (p is null) { Skip("HDR+PNG 位深协商", "未找到 CapturePipelineService.cs"); return; }
        var cps = StripLineComments(File.ReadAllText(p));

        Assert("协商守卫同时看 hdrOutput / 格式 / 位深不足（三样少一样就会错伤或漏判）",
            cps.Contains("if (hdrOutput && format == OutputFormat.PNG && bitDepth < 10)"));
        Assert("局部变量与 settings 字段必须一起抬到 10（只改一个就是半修，编码器照旧拿 8）",
            cps.Contains("bitDepth = 10;") && cps.Contains("settings.OutputBitDepth = 10;"));
        int atHdrFlag = cps.IndexOf("settings.HdrOutput = hdrOutput", StringComparison.Ordinal);
        int atNegot = cps.IndexOf("format == OutputFormat.PNG && bitDepth < 10", StringComparison.Ordinal);
        Assert("协商必须排在场景(HdrOutput)最终确定之后（否则按错的场景协商）",
            atHdrFlag >= 0 && atNegot > atHdrFlag, $"HdrOutput@{atHdrFlag} 协商@{atNegot}");
        Assert("升档要留痕：日志 + Toast + 按内容去重（不许退回进程级只弹一次）",
            cps.Contains("PNG HDR 位深协商") && cps.Contains("ToastService.ShowToast(\"PNG HDR 位深提示\"")
            && cps.Contains("if (s_lastDegradedToast != pngHdrDepth)"));
    }

    static void Test_NvencFailureClassification()
    {
        // ① 输入不适用（不应永久禁用硬件）
        Assert("NVENC 分类: 最小尺寸错误属『输入不适用』",
            NvencAvifBackend.IsInputSpecificNvencFailure(
                new InvalidOperationException("[NVENC] InitEncoder: 0x00000008 (96x96; lastErr=Frame dimensions are less than the minimum supported value.)")));
        Assert("NVENC 分类: 嵌套异常中的分辨率错误也能识别",
            NvencAvifBackend.IsInputSpecificNvencFailure(
                new InvalidOperationException("wrap", new Exception("unsupported resolution"))));

        // ② 硬件/驱动不可用（应当永久禁用）
        Assert("NVENC 分类: 创建实例失败属『硬件不可用』",
            !NvencAvifBackend.IsInputSpecificNvencFailure(
                new InvalidOperationException("NvEncodeAPICreateInstance failed")));
        Assert("NVENC 分类: 无编码能力属『硬件不可用』",
            !NvencAvifBackend.IsInputSpecificNvencFailure(
                new InvalidOperationException("NO_ENCODE_CAPABILITY")));

        // ③ 边界
        Assert("NVENC 分类: null 不属输入不适用",
            !NvencAvifBackend.IsInputSpecificNvencFailure(null));
        Assert("NVENC 分类: 空消息异常不误判",
            !NvencAvifBackend.IsInputSpecificNvencFailure(new Exception("")));

        // ④ 2026-09-24 实测签名：CRF 0 让 NVENC 请求 lossless 被拒（RTX 4060，
        //   InitEncoder hr=0x00000008）。漏这一条时它会被缓存成"整卡不可用"，
        //   此后同进程所有 AVIF 都退软件 —— 顺序实验 --crf 2,0,5,8,15 只有 2 走了硬件。
        Assert("NVENC 分类: CRF 0 的『Lossless 模式不支持』属『输入不适用』，不得粘滞禁用",
            NvencAvifBackend.IsInputSpecificNvencFailure(
                new InvalidOperationException("[NVENC] InitEncoder: 0x00000008 "
                    + "(presetCfg=0x00000000; 1920x1080; sdk=0x0100000D; lastErr=Lossless Coding mode not supported.)")));
    }

    /// <summary>
    /// CRF 下限表：只有硬件档到不了 0，软件档必须到得了。
    /// <para>为什么要单独钉这张表：同一个数字有三个消费点（UI 抬滑块下限、
    /// <c>NvEncoderNative</c> 写 constQP 前的兜底、本测试），事实源是
    /// <c>AvifEncoderCapabilities.MinCrf</c> 一处。三条断言各自防的是：</para>
    /// <list type="number">
    /// <item>硬件档回退成 0 ⇒ 用户能划到 0，每张图白吃一次 InitEncoder 失败再退软件；</item>
    /// <item>软件档被抬成 1 ⇒ libaom 的无损档凭空消失，而且是在"修硬件"时顺手发生的；</item>
    /// <item><c>Auto</c> 返回非 0 ⇒ 说明有人把 Auto 的解析塞进了能力表，
    /// Auto 会在别处被解析成具体档，两处解析迟早分叉。</item>
    /// </list>
    /// </summary>
    static void Test_AvifCrfFloorTable()
    {
        Assert("CRF 下限: NVENC/QSV 为 1（AV1 的 QP0 = 请求硬件没有的 lossless 模式）",
            AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Nvenc) == 1
            && AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Qsv) == 1);
        Assert("CRF 下限: 软件档（libaom/SVT/avifenc）保持 0，不许被硬件那条规则带高",
            AvifEncoderCapabilities.MinCrf(AvifEncoderKind.LibAom) == 0
            && AvifEncoderCapabilities.MinCrf(AvifEncoderKind.SvtAv1) == 0
            && AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Avifenc) == 0);
        Assert("CRF 下限: Auto 不在此表内回答（由调用方解析成具体档后再问）",
            AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Auto) == 0);

        // 下限必须落在该格式声明的质量区间内，否则滑块会被抬成一个空区间
        var (min, max, _, _) = EncoderFactory.Create(OutputFormat.AVIF).GetQualityRange();
        Assert("CRF 下限: 抬到 1 后仍在 AVIF 声明区间内（不会把滑块抬空）",
            AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Nvenc) >= min
            && AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Nvenc) <= max,
            $"区间 [{min}, {max}]");
    }

    /// <summary>
    /// MFT（Windows 系统硬件编码）可用性 —— "Intel QSV" 选项现已改走 MFT。
    /// <para>环境自适应：本机没有 AV1 MFT 编码器时跳过；有则做一次<b>真实编码</b>验证，
    /// 而不是只检查 IsAvailable（那正是此前"探测恒假绿"的教训：
    /// 标记可用 ≠ 真能编码）。</para>
    /// </summary>
    static void Test_MftAv1EncodeIfAvailable()
    {
        if (!MftEncoderNative.IsAv1MftAvailable)
        {
            Skip("MFT AV1 编码", "本机无 AV1 MFT 编码器（QSV 选项将回退软件编码）");
            return;
        }

        const int w = 256, h = 256;   // 与 NVENC 一样避开过小尺寸
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }

        byte[]? bs = null;
        try
        {
            using var mft = new MftEncoderNative(w, h, useAv1: true);
            bs = mft.Encode(bgra);
        }
        catch (Exception ex)
        {
            Assert($"MFT AV1 编码抛出异常: {ex.Message}", false);
            return;
        }

        Assert($"MFT AV1 编码产出非空码流（{bs?.Length ?? 0} 字节）", bs is { Length: > 0 });
        if (bs is not { Length: > 0 }) return;

        // ═══ "非空"根本不是判据：全零垃圾缓冲区也满足 Length > 0 ═══
        // 原用例号称"真实编码验证"，实际只断到字节数 ⇒ MFT 返回什么废数据都是绿的。
        // 这里改用同文件已在用的独立解析器（见 Test_Av1SequenceHeader_Parse / NVENC 那组）：
        // 先看首字节是不是一个合法的 OBU 头，再解析序列头取位深与色度。
        int obuType = (bs[0] >> 3) & 0b1111;
        Assert($"MFT AV1 码流首字节是合法 OBU（type=2 TEMPORAL_DELIMITER，实际 type={obuType}）",
            obuType == 2, $"首字节 0x{bs[0]:X2} ⇒ OBU type {obuType}（0x00 开头的垃圾缓冲区在此即红）");

        var hdr = IvfWriter.ParseAv1SequenceHeader(bs);
        // ⚠ 解析失败时 ParseAv1SequenceHeader 返回默认值（8-bit / 4:2:0）——
        // 所以"位深落在 8/10"必须配一条"序列头真的被读出来了"才不是恒真断言。
        Assert("MFT AV1 码流可解析出序列头 OBU", hdr.seqHeaderObu is { Length: > 0 },
            "seqHeaderObu=null ⇒ 码流里没有可解析的 sequence_header");
        Assert("MFT AV1 序列头位深落在 8/10", hdr.bitDepth is 8 or 10, $"实际 {hdr.bitDepth}-bit");
        // 输入被转成 NV12（8-bit 4:2:0，见 MftEncoderNative.ConfigureMft 与能力表注释），
        // 产出 4:2:2 / 4:4:4 只会是"声明与实际不符"。
        Assert("MFT AV1 序列头色度为 4:2:0 且非单色（与 NV12 输入一致）",
            !hdr.mono && hdr.chromaSubX == 1 && hdr.chromaSubY == 1,
            $"实际 mono={hdr.mono} sub_x={hdr.chromaSubX} sub_y={hdr.chromaSubY}");
    }

    // ═══════════════════════════════════════
    //  AVIF 编码器【实测】能力探测（2026-09-18）
    // ═══════════════════════════════════════

    /// <summary>
    /// 未实测时的回落语义：**必须按静态能力表放行，而不是当成"不支持"**。
    /// <para>反例即缺陷：若 <c>Get()</c> 在未探测时返回"色度全不支持 / 位深 ≤8"，
    /// UI 会在后台探测完成前把可用选项全部隐藏 —— 用户看到的是"选项凭空消失"，
    /// 且探测完成后又冒出来。回落方向必须是宽松（沿用静态表）。</para>
    /// <para>另一条硬约束：<c>Get()</c> 绝不能触发 I/O（它会被 UI 线程高频调用）。</para>
    /// </summary>
    static void Test_AvifProbe_UnmeasuredFallsBack()
    {
        // 用一个不会被任何调用方探测过的 kind 之前先清空缓存，确保处于"未实测"态
        AvifEncoderProbe.Invalidate();
        var m = AvifEncoderProbe.Get(AvifEncoderKind.Nvenc);

        Assert("实测探测: 未探测时 Measured=false", !m.Measured);
        Assert("实测探测: 未探测时按静态表放行 420", m.SupportsChroma("420"));
        Assert("实测探测: 未探测时按静态表放行 422/444（宽松回落）",
            m.SupportsChroma("422") && m.SupportsChroma("444"));
        Assert("实测探测: 未探测时位深上限取静态 SDR 上限",
            m.MaxBitDepth == AvifEncoderCapabilities.SdrMaxBitDepth);
        Assert("实测探测: Get() 不触发探测（缓存仍为空）", !AvifEncoderProbe.IsMeasured(AvifEncoderKind.Nvenc));
    }

    /// <summary>
    /// av1C 解析：位深与色度的位布局必须正确（这是"实测判据"的读数来源）。
    /// <para>byte2 = seq_tier_0(1) | high_bitdepth(1) | twelve_bit(1) | monochrome(1) |
    /// chroma_subsampling_x(1) | chroma_subsampling_y(1) | chroma_sample_position(2)。</para>
    /// </summary>
    static void Test_AvifProbe_Av1CParse()
    {
        // 构造一个最小 AVIF 容器：ftyp + meta(FullBox) > iprp > ipco > av1C
        static byte[] Build(byte flags)
        {
            var ms = new MemoryStream();
            void Box(string type, byte[] payload)
            {
                ms.Write(BitConverter.GetBytes((uint)(8 + payload.Length)).Reverse().ToArray());
                ms.Write(System.Text.Encoding.ASCII.GetBytes(type));
                ms.Write(payload);
            }
            Box("ftyp", new byte[16]);
            var av1c = new byte[] { 0x81, 0x00, flags, 0x00 };
            var ipco = new MemoryStream();
            Box2(ipco, "av1C", av1c);
            var iprp = new MemoryStream();
            Box2(iprp, "ipco", ipco.ToArray());
            var meta = new MemoryStream();
            meta.Write(new byte[4]);           // FullBox: version/flags
            Box2(meta, "iprp", iprp.ToArray());
            Box("meta", meta.ToArray());
            return ms.ToArray();

            static void Box2(MemoryStream s, string type, byte[] payload)
            {
                s.Write(BitConverter.GetBytes((uint)(8 + payload.Length)).Reverse().ToArray());
                s.Write(System.Text.Encoding.ASCII.GetBytes(type));
                s.Write(payload);
            }
        }

        // 8-bit 4:2:0：subX=1 subY=1
        Assert("av1C: 8-bit/420 解析正确",
            AvifEncoderProbe.TryParseAv1C(Build((1 << 3) | (1 << 2)), out var a) && a.BitDepth == 8 && a.Chroma == "420");
        // 10-bit 4:2:0：high_bitdepth=1
        Assert("av1C: 10-bit/420 解析正确",
            AvifEncoderProbe.TryParseAv1C(Build((1 << 6) | (1 << 3) | (1 << 2)), out var b) && b.BitDepth == 10 && b.Chroma == "420");
        // 12-bit 4:4:4：high=1 twelve=1, subX=0 subY=0
        Assert("av1C: 12-bit/444 解析正确",
            AvifEncoderProbe.TryParseAv1C(Build((1 << 6) | (1 << 5)), out var c) && c.BitDepth == 12 && c.Chroma == "444");
        // 10-bit 4:2:2：subX=1 subY=0
        Assert("av1C: 10-bit/422 解析正确",
            AvifEncoderProbe.TryParseAv1C(Build((1 << 6) | (1 << 3)), out var d) && d.BitDepth == 10 && d.Chroma == "422");
        // 非 AVIF / 无 av1C ⇒ 解析失败（不得猜）
        Assert("av1C: 无 av1C 盒时解析失败", !AvifEncoderProbe.TryParseAv1C(new byte[64], out _));
    }

    /// <summary>
    /// 真机实测（需要 avifenc / 硬件后端，缺失即跳过）：
    /// 判据必须自洽 —— 4:2:0 一定实测可用，位深落在 8/10/12，且 10/12 是"请求后确认产出"才计入。
    /// </summary>
    static void Test_AvifProbe_MeasuredIfAvailable()
    {
        if (!NativeAvifEncoder.IsAvailable) { Skip("AVIF 实测能力探测", "avifenc 不可用"); return; }

        var m = AvifEncoderProbe.EnsureMeasured(AvifEncoderKind.LibAom);
        Assert("实测: libaom 完成探测", m.Measured, m.ToString());
        Assert("实测: libaom 4:2:0 可用", m.Chroma420);
        Assert("实测: 位深上限落在 8/10/12", m.MaxBitDepth is 8 or 10 or 12, $"实测 {m.MaxBitDepth}-bit");
        // 12-bit 若被上报，必须是"请求 12 后产物 av1C 真的写着 12"——由探测内部保证，
        // 这里只固化"不会给出 8/10/12 之外的值"这一不变量。
        Assert("实测: 结果被缓存（二次调用同一实例）",
            ReferenceEquals(m, AvifEncoderProbe.Get(AvifEncoderKind.LibAom)));
    }

    /// <summary>
    /// SVT-AV1 的"可用/可实测"必须按 **avifenc 实际编译进了哪些 codec** 判定。
    /// <para>2026-09-18 实测发现：本机内嵌 avifenc 1.4.2 的 <c>--version</c> 只有
    /// <c>aom [enc]:3.14.1</c>（未编译 svt）。此时若照常"实测" SVT-AV1，
    /// 实际测到的是 aom 的能力，会被误归因成 SVT-AV1（曾实测出"svt ≤12-bit"的假结论）。
    /// 故：svt 未编译 ⇒ 该 kind 既不可用、也**不允许**给出实测值。</para>
    /// </summary>
    static void Test_AvifProbe_SvtNotCompiledIn()
    {
        if (!NativeAvifEncoder.IsAvailable) { Skip("SVT-AV1 编译判定", "avifenc 不可用"); return; }

        bool compiled = NativeAvifEncoder.IsSvtAvailable;
        Assert("SVT: IsSvtAvailable 与 --version 文本自洽",
            compiled == NativeAvifEncoder.VersionText.Contains("svt", StringComparison.OrdinalIgnoreCase),
            NativeAvifEncoder.VersionText.Trim());

        if (compiled)
        {
            // 编进来了 ⇒ 允许实测，且必须真的完成探测
            var m = AvifEncoderProbe.EnsureMeasured(AvifEncoderKind.SvtAv1);
            Assert("SVT: 已编译时完成实测", m.Measured, m.ToString());
            return;
        }

        // 未编译 ⇒ 不可用 + 必须有原因 + 绝不能给出实测值（否则就是在拿 aom 冒充 svt）
        Assert("SVT: 未编译时 IsKindAvailable=false",
            !AvifEncoderSelector.IsKindAvailable(AvifEncoderKind.SvtAv1));
        Assert("SVT: 未编译时给出可行动的原因",
            NativeAvifEncoder.SvtUnavailableReason is { Length: > 0 });
        var caps = AvifEncoderProbe.EnsureMeasured(AvifEncoderKind.SvtAv1);
        Assert("SVT: 未编译时不得给出实测值（防 aom 结果被误归因）", !caps.Measured);
        Assert("SVT: 未编译时标注具体原因", caps.Error is { Length: > 0 }, caps.Error);
    }

    // ═══════════════════════════════════════
    //  AVIF 编码器能力表（2026-09-16 重构的核心）
    // ═══════════════════════════════════════

    /// <summary>
    /// 色度协商：硬件编码器只支持 420，用户的 444 请求必须被【显式降级并提示】，
    /// 而不是像重构前那样"默默忽略请求、产出与预期不符的文件"。
    /// </summary>
    static void Test_AvifCaps_NegotiateChroma()
    {
        Assert("能力表: NVENC 444 → 420", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.Nvenc, "444") == "420");
        Assert("能力表: NVENC 422 → 420", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.Nvenc, "422") == "420");
        Assert("能力表: QSV 444 → 420", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.Qsv, "444") == "420");
        Assert("能力表: libaom 保留 444", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.LibAom, "444") == "444");
        Assert("能力表: libaom 保留 422", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.LibAom, "422") == "422");
        // SVT 444/422 → 420：2026-09-19 用带 svt 的自编译 avifenc 实测（ffprobe 读回 pix_fmt），
        // codec=svt 的 422/444 九宫格全部 "Encoding of color planes failed"，只有 4:2:0 产出。
        Assert("能力表: SVT 444 → 420（实测 422/444 编不出来）", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.SvtAv1, "444") == "420");
        Assert("能力表: SVT 422 → 420", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.SvtAv1, "422") == "420");
        Assert("能力表: avifenc 保留 444（默认 codec 解析到 aom）", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.Avifenc, "444") == "444");
        Assert("能力表: 未知色度回落 420", AvifEncoderCapabilities.NegotiateChroma(AvifEncoderKind.Nvenc, "400") == "420");
    }

    /// <summary>
    /// 位深协商必须按 **SDR / HDR 分场景**，而不是按编码器种类一刀切。
    /// <para>SDR：按各编码器能力表的 MaxBitDepth 钳制。软件侧 avifenc <c>--depth</c> 支持 8/10/12；
    /// 硬件侧经 2026-09-17 实测已确认可产出真 10-bit（由 NV_ENC_CONFIG_AV1.outputBitDepth 控制，
    /// 输入仍是 8-bit ARGB，硬件内部做 8→10 转换，ffprobe 报 yuv420p10le）⇒ NVENC 上限 10；
    /// QSV 未做同等实测，仍按 8 保守声明。</para>
    /// <para>HDR：由 avifenc 的 <c>--depth 10</c> 硬编码 ⇒ 恒 10，与编码器、请求值都无关。</para>
    /// </summary>
    static void Test_AvifCaps_NegotiateBitDepth()
    {
        // ── SDR：硬件按能力表（NVENC 实测 10 / QSV 未验证仍 8），软件按能力表 ──
        Assert("位深: SDR NVENC 10-bit → 10（硬件 outputBitDepth 实测可产 10-bit）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 10, hdr: false) == 10);
        Assert("位深: SDR NVENC 12-bit → 10（硬件上限 10）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 12, hdr: false) == 10);
        Assert("位深: SDR NVENC 8-bit 原样保留（不强制升位深）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 8, hdr: false) == 8);
        Assert("位深: SDR QSV 12-bit → 8",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Qsv, 12, hdr: false) == 8);
        Assert("位深: SDR SVT 12-bit → 10（能力表上限 10）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.SvtAv1, 12, hdr: false) == 10);
        Assert("位深: SDR avifenc 16-bit → 12（不传 --codec，默认解析到 aom，实测 12-bit）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Avifenc, 16, hdr: false) == 12);
        Assert("位深: SDR libaom 12-bit → 12（软件支持 12-bit）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 12, hdr: false) == 12);
        Assert("位深: SDR libaom 10-bit 原样保留（默认位深，不再被静默钳成 8）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 10, hdr: false) == 10);
        Assert("位深: SDR 8-bit 原样保留",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 8, hdr: false) == 8);

        // ── HDR：10 是**下限**（PQ 分层要求），上限按编码器 ──
        // 2026-09-25（用户要求"libaom+HDR 应可选 10/12"）：原先这里写的是"HDR 恒 10，与编码器无关"，
        // 于是能力表给 libaom 声明的 MaxBitDepth:12 在 HDR 下永远选不到。
        // 下面刻意成对写：同一请求在 libaom 得 12、在 NVENC/SVT 得 10 —— 只放开软件编码器，
        // 不是"HDR 一律允许 12"（那会把硬件能力谎报进 UI）。
        Assert("位深: HDR NVENC 8-bit → 10（下限）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 8, hdr: true) == 10);
        Assert("位深: HDR QSV 8-bit → 10（下限）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Qsv, 8, hdr: true) == 10);
        Assert("位深: HDR SVT 12-bit → 10（SVT 上限就是 10）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.SvtAv1, 12, hdr: true) == 10);
        Assert("位深: HDR NVENC 12-bit → 10（硬件上限，不得跟着放开）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 12, hdr: true) == 10);
        Assert("位深: HDR libaom 12-bit → 12（本条就是用户要的那档）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 12, hdr: true) == 12);
        Assert("位深: HDR libaom 10-bit → 10（不被抬高）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 10, hdr: true) == 10);
        Assert("位深: HDR libaom 16-bit → 12（超过编码器能力仍要钳）",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 16, hdr: true) == 12);
        Assert("位深: HDR 上限表 = max(10, 该编码器 SDR 上限)",
            AvifEncoderCapabilities.HdrBitDepthFor(AvifEncoderKind.LibAom) == 12
            && AvifEncoderCapabilities.HdrBitDepthFor(AvifEncoderKind.Nvenc) == 10
            && AvifEncoderCapabilities.HdrBitDepthFor(AvifEncoderKind.SvtAv1) == 10
            && AvifEncoderCapabilities.HdrBitDepthFor(AvifEncoderKind.Qsv) == 10);

        // 常量与协商结果自洽（防止常量与实现各写各的数字）
        Assert("位深: SDR 全局上限 = 12（avifenc --depth 只接受 8/10/12）",
            AvifEncoderCapabilities.SdrMaxBitDepth == 12);
        Assert("位深: HDR 常量 = 10", AvifEncoderCapabilities.HdrBitDepth == 10);
        Assert("位深: SdrBitDepthFor 硬件按能力表（NVENC 实测 10，QSV 未验证仍 8）",
            AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.Nvenc) == 10
            && AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.Qsv) == 8);
        Assert("位深: SdrBitDepthFor 软件按能力表（SVT 实测上限 10；avifenc 默认走 aom 故 12）",
            AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.SvtAv1) == 10
            && AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.Avifenc) == 12
            && AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.LibAom) == 12);
        Assert("位深: SDR 协商结果 == SdrBitDepthFor",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.LibAom, 16, hdr: false)
            == AvifEncoderCapabilities.SdrBitDepthFor(AvifEncoderKind.LibAom));
        Assert("位深: HDR 协商结果 == HdrBitDepth",
            AvifEncoderCapabilities.NegotiateBitDepth(AvifEncoderKind.Nvenc, 16, hdr: true)
            == AvifEncoderCapabilities.HdrBitDepth);
    }

    /// <summary>
    /// "色度按能力暴露"的能力表依据 —— UI 的隐藏规则就是读这三个 bool。
    /// <para>产品决策：只有编码器支持 422 / 444 时才把对应色度选项暴露给用户。</para>
    /// </summary>
    static void Test_AvifCaps_ChromaExposureMatrix()
    {
        Assert("暴露: NVENC 仅 420",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "420", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "422", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "444", 8, hdr: false));
        Assert("暴露: QSV 仅 420",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Qsv, "420", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Qsv, "422", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Qsv, "444", 8, hdr: false));
        foreach (var k in new[] { AvifEncoderKind.LibAom, AvifEncoderKind.Avifenc })
            Assert($"暴露: {k} 全色度 420/422/444",
                AvifEncoderCapabilities.Satisfies(k, "420", 8, hdr: false)
                && AvifEncoderCapabilities.Satisfies(k, "422", 8, hdr: false)
                && AvifEncoderCapabilities.Satisfies(k, "444", 8, hdr: false));
        // SVT-AV1 只有 4:2:0（2026-09-19 实测：codec=svt 的 422/444 一律编码失败）
        // ⇒ 它与两个"全色度"软件后端不同，不能一起断言，否则等于把已知产不出的组合暴露给用户。
        Assert("暴露: SvtAv1 仅 420",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.SvtAv1, "420", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.SvtAv1, "422", 8, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.SvtAv1, "444", 8, hdr: false));
        // NVENC SDR 可达 10-bit（2026-09-17 实测 outputBitDepth 生效）⇒ 不该再被判定为不满足；
        // QSV 未做同等实测，仍按 8 保守声明 ⇒ 10-bit 需求下不满足（Auto 选中 QSV 时要降级提示）。
        Assert("暴露: NVENC 满足 SDR 10-bit / QSV 不满足",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "420", 10, hdr: false)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Qsv, "420", 10, hdr: false));
        // 软件 SDR 可达 10-bit（avifenc --depth 会对 PNG 输入升采样）⇒ 不能被误判为不满足
        Assert("暴露: 软件满足 SDR 10-bit",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.LibAom, "420", 10, hdr: false)
            && AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Avifenc, "420", 10, hdr: false));
        // HDR：10 是下限、上限按编码器 ⇒ 8 一律不满足；12 只有能力到 12 的软件编码器满足。
        // 成对写是刻意的：只断"libaom 满足 12"会被"HDR 一律放开 12"这种错实现蒙过，
        // 配上"NVENC 不满足 12"才把边界钉住（硬件到 10 是 2026-09-17 实测的结论）。
        Assert("暴露: HDR 下 libaom 满足 10 与 12、不满足 8；NVENC 只满足 10",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.LibAom, "420", 10, hdr: true)
            && AvifEncoderCapabilities.Satisfies(AvifEncoderKind.LibAom, "420", 12, hdr: true)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.LibAom, "420", 8, hdr: true)
            && AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "420", 10, hdr: true)
            && !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "420", 12, hdr: true));
    }

    /// <summary>
    /// HDR 路径只有软件编码器可用（硬件的输入契约与容器层都不支持 PQ）。
    /// <para>这条是管线层"HDR 时把硬件 kind 改写为 Avifenc 并提示"的依据。</para>
    /// </summary>
    static void Test_AvifCaps_SupportsHdrPath()
    {
        foreach (var k in new[] { AvifEncoderKind.Auto, AvifEncoderKind.LibAom,
                                  AvifEncoderKind.SvtAv1, AvifEncoderKind.Avifenc,
                                  AvifEncoderKind.Nvenc })
            Assert($"HDR 路径: {k} 可用", AvifEncoderCapabilities.SupportsHdrPath(k));
        // ═══ 2026-09-18 决策更新：NVENC 已接通 HDR 10-bit 直通（PQ 输入 + colr PQ 声明）═══
        // 仅剩 QSV（NV12 8-bit 输入）不支持 HDR。
        foreach (var k in new[] { AvifEncoderKind.Qsv })
            Assert($"HDR 路径: {k} 不可用（NV12 8-bit 输入）", !AvifEncoderCapabilities.SupportsHdrPath(k));

        // 一致性护栏：声称支持 HDR 的具体编码器，位深上限必须达到 HDR 的 10-bit
        foreach (var k in new[] { AvifEncoderKind.LibAom, AvifEncoderKind.SvtAv1, AvifEncoderKind.Avifenc })
            Assert($"HDR 一致性: {k} 位深上限 ≥ 10",
                AvifEncoderCapabilities.SupportsHdrPath(k)
                && AvifEncoderCapabilities.Caps(k).MaxBitDepth >= AvifEncoderCapabilities.HdrBitDepth);
    }

    /// <summary>
    /// Auto 挑选：**严格按 NVENC → QSV → SVT-AV1 → libaom → avifenc 取第一个【可用】的**，
    /// 不按色度/位深需求过滤（2026-09-17 产品决策，实现见 <c>PickAuto</c>）。
    /// <para>这取代了早先"必须满足色度/位深需求"的版本：那一版在默认色度 444 时
    /// 会把硬件全部跳过、恒落到 libaom 软件编码（4K AVIF 约 22s vs 硬件约 0.7s），
    /// 使"Auto 硬件优先"形同虚设。能力不足改由 <c>NegotiateChroma</c> /
    /// <c>NegotiateBitDepth</c> 降级并<b>明确提示</b> —— 即"用最快的编码器 + 如实告知"。</para>
    /// <para>另一个要点是 Auto 必须在此【先解析成具体 kind】再交给下游协商（审查 P1-1）：
    /// 若拿着 Auto 去协商，会误用 libaom 的宽松能力表，导致实际降级却不提示。</para>
    /// </summary>
    static void Test_AvifCaps_PickAuto()
    {
        static bool All(AvifEncoderKind _) => true;
        static bool None(AvifEncoderKind _) => false;
        static bool OnlyQsv(AvifEncoderKind k) => k == AvifEncoderKind.Qsv;
        static bool OnlyLibAom(AvifEncoderKind k) => k == AvifEncoderKind.LibAom;
        static bool OnlySvt(AvifEncoderKind k) => k == AvifEncoderKind.SvtAv1;
        static bool SvtAndLibAom(AvifEncoderKind k) => k is AvifEncoderKind.SvtAv1 or AvifEncoderKind.LibAom;

        // ① 2026-09-19（用户决定）：Auto 按【色度】过滤 —— 选一个不必降级的编码器。
        //    420 ⇒ 硬件优先；422/444 ⇒ 让给 libaom（硬件给不出广色度，NVENC 恒 4:2:0）。
        //    ⚠ 这条规则与"默认色度是 420"是一体的：默认 444 + 过滤 = Auto 恒走软件（2026-09-17
        //    踩过的坑），两处必须一起看（见 AppSettingsData.ChromaAvif 的三段历史注释）。
        Assert("PickAuto: 420 → NVENC（硬件优先）",
            AvifEncoderCapabilities.PickAuto(All, "420", 8) == AvifEncoderKind.Nvenc);
        Assert("PickAuto: 420/10 → NVENC（默认设置的组合，硬件必须仍在）",
            AvifEncoderCapabilities.PickAuto(All, "420", 10) == AvifEncoderKind.Nvenc);
        Assert("PickAuto: 444 → libaom（不再选会静默降级的硬件）",
            AvifEncoderCapabilities.PickAuto(All, "444", 10) == AvifEncoderKind.LibAom);
        Assert("PickAuto: 422 → libaom（NVENC 无 4:2:2 AV1 管线，QSV 输入是 NV12）",
            AvifEncoderCapabilities.PickAuto(All, "422", 8) == AvifEncoderKind.LibAom);
        // 位深【不】参与过滤：场景（HDR/SDR）未知时按请求值过滤会拿错口径，仍由 NegotiateBitDepth 钳。
        Assert("PickAuto: 位深不过滤 —— 420/12 仍给 NVENC（随后由协商钳到其上限）",
            AvifEncoderCapabilities.PickAuto(All, "420", 12) == AvifEncoderKind.Nvenc);
        // SVT 实测只有 4:2:0/≤10-bit ⇒ 444 请求也不能给它（LibAom 排在它前面已足够，但两条都要钉住）。
        Assert("PickAuto: 444 不会落到 SVT（实测只有 4:2:0）",
            AvifEncoderCapabilities.PickAuto(SvtAndLibAom, "444", 10) == AvifEncoderKind.LibAom);

        // ② 硬件不可用时按序退到软件
        //    注：这条同时证明"第二轮兜底"—— QSV 并不支持 444，但除了它没有别的可用后端，
        //    于是仍交给它（随后由 NegotiateChroma 降到 420 并如实提示），Auto 不因参数高而失败。
        Assert("PickAuto: 仅 QSV 可用 → QSV",
            AvifEncoderCapabilities.PickAuto(OnlyQsv, "444", 12) == AvifEncoderKind.Qsv);
        Assert("PickAuto: 仅 libaom 可用 → libaom",
            AvifEncoderCapabilities.PickAuto(OnlyLibAom, "420", 8) == AvifEncoderKind.LibAom);
        // 软件兜底顺序固化（2026-09-19）：换上带 svt 的自编译 avifenc 后 IsKindAvailable(SvtAv1) 才真的为
        // true，这条顺序从此会决定"无硬件机器"的默认编码器。实测（svt_preset_sweep.py，420/10-bit）：
        // SVT 只在 -s 0~4 比 aom 快（-30%~-60%），管线固定的 -s 6 上两者持平而 svt 体积更大，
        // 且 SVT 只有 4:2:0/≤10-bit ⇒ libaom 必须排在前面，否则 Auto 会把 422/444/12-bit 无谓降级。
        // 若将来有人"按宣称速度"重排、或把 SpeedArg 挪到 -s ≤4，这两条会红 —— 届时按脚本重测再议。
        Assert("PickAuto: 无硬件且 SVT+libaom 都可用 → libaom（实测兜底选择）",
            AvifEncoderCapabilities.PickAuto(SvtAndLibAom, "444", 12) == AvifEncoderKind.LibAom);
        Assert("PickAuto: 仅 SVT 可用 → SVT（手动/唯一软件后端时仍要用上）",
            AvifEncoderCapabilities.PickAuto(OnlySvt, "420", 10) == AvifEncoderKind.SvtAv1);

        // ③ 一个都不可用 → 兜底 libaom（不得返回 Auto，否则协商会拿错能力表）
        Assert("PickAuto: 全部不可用 → 兜底 libaom",
            AvifEncoderCapabilities.PickAuto(None, "444", 12) == AvifEncoderKind.LibAom);

        // ④ Satisfies 与暴露规则自洽（Satisfies 必须显式带场景，否则位深语义会与协商分叉）
        Assert("Satisfies: NVENC 满足 420/8",
            AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "420", 8, hdr: false));
        Assert("Satisfies: NVENC 不满足 444",
            !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Nvenc, "444", 8, hdr: false));
        Assert("Satisfies: QSV 不满足 10-bit",
            !AvifEncoderCapabilities.Satisfies(AvifEncoderKind.Qsv, "420", 10, hdr: false));
    }

    /// <summary>设置字符串 → 枚举：大小写无关，未知值回落 Auto（与 SettingsService 白名单一致）。</summary>
    static void Test_AvifCaps_Parse()
    {
        Assert("Parse: Nvenc", AvifEncoderCapabilities.Parse("Nvenc") == AvifEncoderKind.Nvenc);
        Assert("Parse: 大小写无关", AvifEncoderCapabilities.Parse("svtav1") == AvifEncoderKind.SvtAv1);
        Assert("Parse: 空值回落 Auto", AvifEncoderCapabilities.Parse(null) == AvifEncoderKind.Auto);
        Assert("Parse: 未知值回落 Auto", AvifEncoderCapabilities.Parse("bogus") == AvifEncoderKind.Auto);
        Assert("Parse: 首尾空格容错", AvifEncoderCapabilities.Parse("  Qsv  ") == AvifEncoderKind.Qsv);
    }

    /// <summary>
    /// SDR 路径的 <c>--depth</c> 参数映射。
    /// <para>2026-09-17 修复：SDR 路径此前从不拼 <c>--depth</c>，<c>bitDepth</c> 是死参数 ——
    /// 无论传 8/10/12，输出恒为 8-bit，而设置默认与 UI 都写着 10-bit。现在位深真正透传给 avifenc，
    /// 而 avifenc 只接受 8/10/12（libavif apps/avifenc.c），故 8 不传参、&gt;12 一律钳到 12。</para>
    /// </summary>
    static void Test_AvifSdr_DepthArg()
    {
        Assert("--depth: 8-bit 不传参（沿用 avifenc 默认）", NativeAvifEncoder.GetDepthArg(8) == "");
        Assert("--depth: 10-bit → --depth 10", NativeAvifEncoder.GetDepthArg(10) == "--depth 10");
        Assert("--depth: 11 归到 10", NativeAvifEncoder.GetDepthArg(11) == "--depth 10");
        Assert("--depth: 12-bit → --depth 12", NativeAvifEncoder.GetDepthArg(12) == "--depth 12");
        Assert("--depth: 16-bit 钳到 12（avifenc 上限）", NativeAvifEncoder.GetDepthArg(16) == "--depth 12");
    }

    /// <summary>
    /// Auto 必须【先解析成具体编码器、再协商】（审查 P1-1）。
    /// <para>若顺序颠倒，<c>Caps(Auto)</c> 会回落到 libaom 的宽松能力表（全色度 / 12-bit），
    /// 于是协商永远判定"无需降级"，用户请求 444/10-bit 却拿到 420/8-bit 且无任何提示 ——
    /// 正是本次重构要消灭的静默改参数。这里用源码契约锁住调用顺序。</para>
    /// </summary>
    static void Test_Pipeline_AutoResolvedBeforeNegotiation()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (path is null) { Skip("Auto 先解析后协商", "未找到源文件"); return; }

        var text = File.ReadAllText(path);
        int iAuto = text.IndexOf("AvifEncoderSelector.PickAvailableKind", StringComparison.Ordinal);
        int iNeg = text.IndexOf("AvifEncoderCapabilities.NegotiateBitDepth", StringComparison.Ordinal);

        Assert("管线: 存在 Auto → 具体编码器的解析调用", iAuto >= 0);
        Assert("管线: 存在能力协商调用", iNeg >= 0);
        if (iAuto < 0 || iNeg < 0) return;
        Assert("管线: Auto 解析必须早于能力协商（否则降级提示失效）", iAuto < iNeg);

        // ═══ 协商不仅要晚于 Auto 解析，还必须晚于 HdrOutput 的【最终确定】═══
        // HDR 路径不走 SelectByKind（位深由 avifenc --depth 10 硬编码、色度未传 --yuv），
        // 协商结果对它完全不生效。若协商发生在"HDR 开启 + sRGB 目标 → 降级 SDR"
        // （改写 settings.HdrOutput）之前，就会拿入参 hdrOutput 判断场景
        // ⇒ HDR 用户收到根本不成立的"降级为 420 / 8-bit"提示。
        int iHdrFix = text.IndexOf("settings.HdrOutput = false;", StringComparison.Ordinal);
        Assert("管线: 存在 HdrOutput 最终确定点（HDR + sRGB 目标 → 降级 SDR）", iHdrFix >= 0);
        if (iHdrFix < 0) return;
        Assert("管线: 能力协商必须晚于 HdrOutput 最终确定（否则场景判断用错值）", iNeg > iHdrFix);
        string between = text[iHdrFix..iNeg];
        Assert("管线: 协商按 settings.HdrOutput（而非入参 hdrOutput）区分 SDR/HDR 场景",
            between.Contains("settings.HdrOutput"));
    }

    /// <summary>
    /// 位深规范化必须【按格式】钳制（审查 P2-7）。
    /// <para>统一放行 12-bit 时：TIFF 会写出 BitsPerSample=12 而数据是 16-bit 未打包的非法文件；
    /// JPEG LI / GainMap 的编码器根本没有位深参数，字段是死的却仍然可选可存。</para>
    /// </summary>
    static void Test_Settings_BitDepthClampedPerFormat()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        if (path is null) { Skip("位深按格式钳制", "未找到源文件"); return; }

        var text = File.ReadAllText(path);
        int iMethod = text.IndexOf("private static void NormalizeBitDepth", StringComparison.Ordinal);
        if (iMethod < 0) { Assert("找到 NormalizeBitDepth", false); return; }
        string body = text[iMethod..];

        Assert("位深: TIFF 走 8/16 钳制（其余值会产出非法 TIFF）",
            body.Contains("s.BitDepthTiff    = FixBitDepth8Or16"));
        Assert("位深: JPEG LI 恒 8（jpegli 无位深参数）",
            body.Contains("s.BitDepthJpegLi  = FixBitDepth8"));
        Assert("位深: GainMap 恒 8（Ultra HDR Base/增益图均 8-bit）",
            body.Contains("s.BitDepthGainMap = FixBitDepth8"));
        Assert("位深: WebP 恒 8", body.Contains("s.BitDepthWebP    = FixBitDepth8"));
        Assert("位深: 存在 8/16 钳制实现", body.Contains("FixBitDepth8Or16(int v) => v > 8 ? 16 : 8"));

        // JPEG XL：cjxl 无 -b 参数，位深由输入容器（PPM vs 16-bit PNG 中转 + sBIT）决定。
        // 2026-09-19 实测更正：8 / 10 / 12 / 16 的产物**并不相同** —— ffprobe 报
        // bits_per_raw_sample = 8 / 10 / 12 / 16，体积也各异（10/12/16 档 = 881/828/935 B）。
        // 旧断言"10 与 12 完全相同、UI 只给 8/10、12/16 归到 10"已被证伪，
        // 且它与 §12.2 新加的「16-bit」下拉项互相矛盾：正是那条矛盾让"选 16 重启变 10"顺行通过。
        Assert("位深: JPEG XL 走 FixBitDepthJxl", body.Contains("s.BitDepthJpegXl  = FixBitDepthJxl"));
        Assert("位深: JPEG XL 钳制须保留下拉提供的 8/10/16（越界值归 10）",
            body.Contains("FixBitDepthJxl(int v) => v switch { 8 => 8, 16 => 16, _ => 10 }"));
    }

    /// <summary>
    /// 每格式色度必须【单一事实源】：UI 写哪个字段，管线就得读哪个字段。
    /// <para>回归背景（2026-09-17）：`AvifChroma` 与 `ChromaAvif` 两个字段并存且默认值相同，
    /// 但 `CapturePipelineService` 读 `ChromaAvif`，而设置页的实时保存与界面回显却用
    /// `AvifChroma` —— 于是用户改了 AVIF 色度后**实时保存不生效**（要等全量保存），
    /// 界面却显示改成功了。这类"两个字段看起来一样、实际不同源"的缺陷不会报错，
    /// 只能靠契约断言钉住。现已统一到 `ChromaAvif`。</para>
    /// </summary>
    static void Test_Settings_ChromaSingleSource()
    {
        var uiPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var modelPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        var pipePath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (uiPath is null || modelPath is null || pipePath is null)
        { Skip("色度单一事实源", "未找到源文件"); return; }

        Assert("色度: 设置模型已移除重复字段 AvifChroma",
            !File.ReadAllText(modelPath).Contains("public string AvifChroma"));

        string ui = File.ReadAllText(uiPath);
        Assert("色度: 实时保存写 ChromaAvif（写 AvifChroma 则不生效）",
            ui.Contains("_settings.ChromaAvif = tag"));

        string pipe = File.ReadAllText(pipePath);
        int iAvif = pipe.IndexOf("OutputFormat.AVIF =>", StringComparison.Ordinal);
        Assert("色度: 管线 AVIF 分支读 ChromaAvif",
            iAvif >= 0 && pipe.Substring(iAvif, 80).Contains("s.ChromaAvif"));
    }

    /// <summary>
    /// AVIF 默认色度 = <b>4:2:0</b>，且两处事实源（字段默认值 / XAML IsSelected 项）必须同源。
    /// <para>只改一处会导致"设置里是 420、界面显示 444"或"首帧一个值、保存后变另一个"。</para>
    /// <para>这个默认值改过三次，别按"最新一条"随手对齐（完整理由见 AppSettingsData.ChromaAvif）：
    ///   2026-09-17 → 420（让 Auto 选到硬件）；2026-09-18 → 444（用户拍板默认视觉无损）；
    ///   2026-09-19 → 420（用户拍板，<b>并与"Auto 按色度过滤"同时生效</b>）。
    /// 第三次与前两次不同之处在 <see cref="AvifEncoderCapabilities.PickAuto"/>：
    /// 现在 420 才拿得到硬件，422/444 由 libaom 承担 —— 所以"默认 444 + Auto 硬件优先"
    /// 这对组合已经不存在了，若要恢复 444 默认，必须一并重议 Auto 的过滤规则，
    /// 否则默认设置又会在硬件上被静默降级（正是 2026-09-19 那次要消除的形态）。</para>
    /// </summary>
    static void Test_AvifChroma_DefaultIs420()
    {
        var modelPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (modelPath is null || xamlPath is null) { Skip("AVIF 默认色度 420", "未找到源文件"); return; }

        // 把源码里的双引号统一换成单引号再匹配：既避免字符串字面量内的转义，
        // 也能精确表达我们要找的 XAML 片段（Tag='420' / IsSelected='True'）。
        var model = File.ReadAllText(modelPath).Replace('"', '\'');
        Assert("默认色度: AppSettingsData.ChromaAvif 默认值为 420",
            model.Contains("public string ChromaAvif { get; set; } = '420'"),
            "仅改 XAML 不改字段默认值会让『界面 420、实际编码 444』脱节");

        var xaml = File.ReadAllText(xamlPath).Replace('"', '\'');
        int i = xaml.IndexOf("AvifChromaCbo", StringComparison.Ordinal);
        int end = i >= 0 ? xaml.IndexOf("</ComboBox>", i, StringComparison.Ordinal) : -1;
        var block = (i >= 0 && end > i) ? xaml[i..end] : "";
        Assert("默认色度: XAML 找到 AvifChromaCbo 块", block.Length > 0);
        if (block.Length == 0) return;

        int i420 = block.IndexOf("Tag='420'", StringComparison.Ordinal);
        Assert("默认色度: XAML 中存在 420 项", i420 >= 0);
        if (i420 < 0) return;
        int next420 = block.IndexOf("<ComboBoxItem", i420 + 1, StringComparison.Ordinal);
        var seg420 = block[i420..(next420 > 0 ? next420 : block.Length)];
        Assert("默认色度: XAML 中 IsSelected 落在 420 项", seg420.Contains("IsSelected='True'"));
        Assert("默认色度: XAML 中不再有第二个 IsSelected（否则两处默认值打架）",
            block.IndexOf("IsSelected='True'", StringComparison.Ordinal)
            == block.LastIndexOf("IsSelected='True'", StringComparison.Ordinal));

        // 第三处事实源：本地化文案会在运行时覆盖 XAML 的项文本 ⇒ 两处必须逐 Tag 完全一致。
        // 2026-09-25（用户口径：下拉项只留采样名，说明走灰字行）：原先这条钉的是
        // "420 项文字里要有『默认』二字"，那只是拿文案内容当"两处同步"的探针；
        // 现在项文字统一变短，探针失效 ⇒ 换成真正的漂移锁：逐 Tag 比对 XAML 与 LocaleManager，
        // 任何一处单独改（包括改回带括号的长文案）都当场红。
        var locPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (locPath is not null)
        {
            var locRaw = File.ReadAllText(locPath);
            foreach (var (tag, prop) in new[]
            {
                ("420", "AvifChroma420"), ("422", "AvifChroma422"), ("444", "AvifChroma444")
            })
            {
                var mx = System.Text.RegularExpressions.Regex.Match(
                    block, @"Tag='" + tag + @"'[^>]*>([^<]*)<");
                int k = locRaw.IndexOf(prop + " =>", StringComparison.Ordinal);
                string locLine = k < 0 ? "" : locRaw[k..(locRaw.IndexOf('\n', k) < 0
                    ? locRaw.Length : locRaw.IndexOf('\n', k))];
                var ml = System.Text.RegularExpressions.Regex.Match(locLine, "\"([^\"]*)\"");
                string xamlText = mx.Success ? mx.Groups[1].Value.Trim() : "";
                string locText = ml.Success ? ml.Groups[1].Value : "";
                Assert($"色度 {tag} 项：XAML 与 LocaleManager 文案逐字一致（运行时后者覆盖前者）",
                    xamlText.Length > 0 && locText.Length > 0 && xamlText == locText,
                    $"XAML='{xamlText}' 本地化='{locText}'");
            }
            // 项文字里不许再出现解释性括号 —— 说明归灰字行（AvifCodecHintTxt）。
            foreach (var prop in new[] { "AvifChroma420", "AvifChroma422", "AvifChroma444" })
            {
                int k = locRaw.IndexOf(prop + " =>", StringComparison.Ordinal);
                string locLine = k < 0 ? "" : locRaw[k..(locRaw.IndexOf('\n', k) < 0
                    ? locRaw.Length : locRaw.IndexOf('\n', k))];
                Assert($"色度项 {prop} 只留采样名（不带『（…）』解释）",
                    !locLine.Contains('（') && !locLine.Contains("default"), locLine.Trim());
            }
        }
        else
        {
            // 与同文件其它用例一致：源文件缺失要【留痕跳过】。原写法整段被 if 吞掉，
            // LocaleManager.cs 一旦改名 / 移动，这条契约既不高亮也不记 Skip —— 静默消失。
            Skip("默认色度: 本地化文案与 XAML 逐 Tag 一致", "未找到源文件 LocaleManager.cs");
        }
    }

    /// <summary>
    /// 产品决策：不支持的色度 / 位深必须【隐藏】而不是【置灰】。
    /// <para>置灰版本用户仍可能点到（或误以为只是暂时不可用），且 SelectedItem 是禁用项，
    /// 截图时才弹降级 Toast —— 事后告知；隐藏才是事前阻止。</para>
    /// </summary>
    static void Test_AvifOptions_ExposedNotDisabled()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (path is null) { Skip("AVIF 选项按能力暴露", "未找到源文件"); return; }

        var text = ReadSourceNormalized(path);
        int i = text.IndexOf("private void UpdateAvifOptionAvailability()", StringComparison.Ordinal);
        if (i < 0) { Assert("找到 UpdateAvifOptionAvailability", false); return; }

        // ⚠ 隐藏动作已被提取到共享辅助函数 SetItemsVisible 中（三个下拉共用），
        // 只切片 UpdateAvifOptionAvailability 会看不到 Visibility.Collapsed ——
        // 本断言曾因此恒不成立。故切片范围必须一路覆盖到 SetItemsVisible 结束。
        int j = text.IndexOf("private static (bool Changed, string Tag) SetItemsVisible(", StringComparison.Ordinal);
        Assert("找到 SetItemsVisible（隐藏逻辑的实际所在）", j > i);
        if (j <= i) return;

        // 收尾锚点找不到 ⇒ 判红并停手。旧写法在这里退化成"整份文件都算方法体"，
        // 于是"不许含 X"的否定断言恒假、"必须含 Y"的肯定断言恒真 —— 两个方向都不可信。
        int end = text.IndexOf("\n    }", j, StringComparison.Ordinal);
        Assert("必须切到 SetItemsVisible 的收尾锚点（行尾已由 ReadSourceNormalized 归一）", end > j, $"j={j} end={end}");
        if (end <= j) return;
        var body = text[i..end];

        Assert("AVIF 选项: 使用 Visibility.Collapsed 隐藏不支持项", body.Contains("Visibility.Collapsed"));
        Assert("AVIF 选项: 不再把三个下拉整体禁用",
            !body.Contains("AvifCodecCbo.IsEnabled = false")
            && !body.Contains("AvifChromaCbo.IsEnabled = false")
            && !body.Contains("BdAvifCbo.IsEnabled = false"));
    }

    /// <summary>
    /// 顺序契约：必须先改选（fallback.IsSelected）后隐藏（item.Visibility = ...）。
    /// <para>反序会先产生"选中项处于折叠态 / SelectedItem 为 null"的中间态，
    /// 此时 SaveSettings 会把 null 兜底成 "Auto" / 10 / 420 ⇒ 用户设置被静默改写。</para>
    /// </summary>
    static void Test_AvifOptions_ReselectBeforeHide()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (path is null) { Skip("改选早于隐藏", "未找到源文件"); return; }

        var text = ReadSourceNormalized(path);
        int i = text.IndexOf("SetItemsVisible(ComboBox cbo", StringComparison.Ordinal);
        if (i < 0) { Assert("找到 SetItemsVisible", false); return; }
        if (!TrySliceMethodBody(text, i, out var body))
        { Assert("必须切到 SetItemsVisible 的收尾锚点", false, "锚点丢失 ⇒ 不许拿整份文件判顺序"); return; }

        int iSel = body.IndexOf("fallback.IsSelected = true;", StringComparison.Ordinal);
        int iVis = body.IndexOf("item.Visibility = visible[i]", StringComparison.Ordinal);
        Assert("SetItemsVisible: 存在 fallback 改选", iSel >= 0);
        Assert("SetItemsVisible: 存在可见性应用", iVis >= 0);
        if (iSel < 0 || iVis < 0) return;
        Assert("SetItemsVisible: 改选必须早于隐藏（否则出现折叠态选中项）", iSel < iVis);
    }

    /// <summary>
    /// 位深下拉里出现的每一个档位，都必须能被 SettingsService 加载期的 FixBitDepth* 原样保留。
    /// <para>回归背景（2026-09-19 分析管线时发现）：§12.2 给 JPEG XL 下拉加了「16-bit（Float16 源真精度）」，
    /// 但 <c>FixBitDepthJxl</c> 仍是 §12 之前写的 <c>v &gt; 8 ? 10 : 8</c> ⇒ 用户选 16，本次会话生效、
    /// <b>重启后被静默改回 10</b>，表现为"设置没保存"。实测三者产物确实不同
    /// （ffprobe 报 bits_per_raw_sample 10 / 12 / 16，体积 881 / 828 / 935 B），不是等价档位。</para>
    /// <para>XAML 与 SettingsService 分处两处、无编译期关联，故沿用本仓库既有做法（源码文本交叉核对）：
    /// 判据是"修函数体里必须出现该档位数字"。它不能证明语义正确，但足以拦住"下拉给了、加载器不认"这一类漂移。</para>
    /// </summary>
    static void Test_Settings_BitDepthOptionsSurviveNormalize()
    {
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        if (xamlPath is null || svcPath is null)
        { Skip("位深选项与加载期规范化", "未找到源文件"); return; }

        var xaml = File.ReadAllText(xamlPath);
        var svc = File.ReadAllText(svcPath);

        // 每格式：ComboBox 名 ↔ Normalize 里被规范化的字段名 ↔ 专用 Fix 函数名。
        // 只有这 4 个格式在 UI 上提供位深下拉；WebP / JPEG LI / GainMap 恒 8-bit（无下拉，无需核对）。
        var formats = new (string Cbo, string Field, string Fixer)[]
        {
            ("BdPngCbo", "BitDepthPng", "FixBitDepth"),
            ("BdJpegXlCbo", "BitDepthJpegXl", "FixBitDepthJxl"),
            ("BdAvifCbo", "BitDepthAvif", "FixBitDepthAvif"),
            ("BdTiffCbo", "BitDepthTiff", "FixBitDepth8Or16"),
        };

        foreach (var (cbo, field, fixer) in formats)
        {
            // ① 该格式下拉提供的位深档位（只取 ComboBox 自身那段，避免吃进后一个控件的项）
            int at = xaml.IndexOf($"x:Name=\"{cbo}\"", StringComparison.Ordinal);
            if (at < 0) { Assert($"{cbo} 存在于 XAML", false, "找不到该 ComboBox"); continue; }
            int end = xaml.IndexOf("</ComboBox>", at, StringComparison.Ordinal);
            var tags = Regex.Matches(xaml[at..end], @"<ComboBoxItem\s+Tag=""(\d+)""")
                           .Select(m => m.Groups[1].Value).ToHashSet();
            if (tags.Count == 0) { Assert($"{cbo} 提供位深档位", false, "无 Tag=\"数字\" 项"); continue; }

            // ② Normalize 确实规范化了该字段
            bool normalized = Regex.IsMatch(svc, $@"s\.{field}\s*=\s*{fixer}\(s\.{field}\)");
            Assert($"{field} 由 {fixer} 规范化", normalized, "Normalize 里没有这一行");

            // ③ 修函数体必须"认得"每一个下拉档位
            int fs = svc.IndexOf($"static int {fixer}(int v)", StringComparison.Ordinal);
            if (fs < 0) { Assert($"{fixer} 函数体存在", false, "找不到函数定义"); continue; }
            string body = svc[fs..(svc.IndexOf("private ", fs + 10, StringComparison.Ordinal) > 0
                ? svc.IndexOf("private ", fs + 10, StringComparison.Ordinal) : svc.Length)];
            foreach (var t in tags)
                Assert($"{cbo} 档位 {t} 加载后不被改写", body.Contains(t),
                    $"{fixer} 的函数体里找不到 {t} ⇒ 选了它会在下次加载时被改成别的值（§12.2 的 JXL 16-bit 即此型回归）");
        }
    }

    /// <summary>
    /// 每格式质量默认值必须【三处同源】：
    /// ① <c>AppSettingsData</c> 的属性初始化器（新用户拿到的值）；
    /// ② <c>SettingsService.Normalize</c> 的 <c>ClampTo</c> 兜底值（越界旧值被"修正"成的值）；
    /// ③ 编码器的 <c>GetQualityRange().Default</c>（UI 滑块回退值）。
    /// <para>回归背景（2026-09-17）：② 长期停留在"视觉无损"对齐之前的旧值
    /// （JPEG XL 0.8 / AVIF 18 / WebP 92），而 ①③ 已是 1.0 / 15 / 90。
    /// 后果是越界或 NaN 的质量值会被"修正"成一个产品上已被废弃的旧默认 ——
    /// 用户调不动、界面也看不出异常。三者分处两个项目、无编译期关联，
    /// 只能靠这条断言防漂移。</para>
    /// </summary>
    static void Test_Settings_QualityDefaultsSingleSource()
    {
        var modelPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        if (modelPath is null || svcPath is null)
        { Skip("质量默认值三处同源", "未找到源文件"); return; }

        var model = File.ReadAllText(modelPath);
        var svc = File.ReadAllText(svcPath);

        // ① 属性初始化器：public double QualityAvif { get; set; } = 15;
        var modelDefaults = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(model,
            @"public double Quality(\w+)\s*\{\s*get;\s*set;\s*\}\s*=\s*([0-9]*\.?[0-9]+)\s*;"))
            modelDefaults[m.Groups[1].Value] = double.Parse(m.Groups[2].Value,
                System.Globalization.CultureInfo.InvariantCulture);

        // ② 规范化兜底：2026-09-24 起必须写成 dft.QualityXxx —— 值来自 ①，② 不再自带字面量。
        //    于是 ②≡① 由构造保证，本段改测【接线齐全且指对字段】：
        //    漏接某格式、或接错字段（QualityAvif 那行误写 dft.QualityWebp）仍会红；
        //    同时禁止任何人把数字默认值加回来（那会重新制造第二处事实源）。
        var clampWired = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(svc,
            @"ClampTo\(s\.Quality(\w+),\s*v\s*=>\s*s\.Quality\1\s*=\s*v,\s*[0-9]*\.?[0-9]+,\s*[0-9]*\.?[0-9]+,\s*dft\.Quality(\w+)\)"))
            if (m.Groups[1].Value == m.Groups[2].Value) clampWired.Add(m.Groups[1].Value);
        int literalDefaults = Regex.Matches(svc,
            @"ClampTo\(s\.Quality\w+,[^\n]*,\s*[0-9][0-9.]*\);").Count;

        // ③ 编码器声明的默认质量
        var expected = new (string Field, OutputFormat Format)[]
        {
            ("Png", OutputFormat.PNG), ("GainMap", OutputFormat.JPEG_GAINMAP),
            ("JpegLi", OutputFormat.JPEG_LI), ("JpegXl", OutputFormat.JPEG_XL),
            ("Avif", OutputFormat.AVIF), ("Webp", OutputFormat.WebP), ("Tiff", OutputFormat.TIFF),
        };

        Assert("质量默认: ① 模型与 ② 接线都覆盖到 7 个格式",
            modelDefaults.Count >= 7 && clampWired.Count >= 7,
            $"模型 {modelDefaults.Count} 条 / 接线 {clampWired.Count} 条"
            + $"（缺：{string.Join(",", expected.Select(e => e.Field).Where(f => !clampWired.Contains(f)))}）");
        Assert("质量默认: ② 不许重新出现数字兜底值（第二处事实源）",
            literalDefaults == 0, $"{literalDefaults} 处写回了字面量");

        foreach (var (field, format) in expected)
        {
            var (min, max, def, _) = EncoderFactory.Create(format).GetQualityRange();
            modelDefaults.TryGetValue(field, out double modelDef);

            Assert($"质量默认: {field} 编码器默认 {def} 落在区间 [{min}, {max}]",
                def >= min && def <= max);
            Assert($"质量默认: {field} 设置模型与编码器一致（{modelDef} vs {def}）",
                Math.Abs(modelDef - def) < 1e-6,
                "改了 GetQualityRange().Default 却没改 AppSettingsData，新用户会拿到越界值");
        }
    }

    /// <summary>
    /// NVENC 的 AV1 位深实测（2026-09-17）。
    /// <para>为什么需要这条断言：位深既看不出文件名也看不出体积，只能解析 AV1 序列头。
    /// 而"硬件 SDR 恒 8-bit"曾是被写进能力表与 UI 文案的长期结论 —— 实际上那只是
    /// <b>本代码库输入路径的限制</b>（<c>bufferFormat</c> 恒填 8-bit 的 ARGB，
    /// ARGB10 常量定义了却从未接线），NVIDIA / Intel 的 AV1 硬编本身支持 10-bit。</para>
    /// <para>本测试同时编码 8-bit 与 10-bit 两版并解析序列头，用于回答：
    /// "改 bufferFormat 是否足以让硬件产出 10-bit？" —— 实测答案是<b>否</b>。</para>
    /// </summary>
    static void Test_Nvenc_Av1TenBit()
    {
        if (!AvifEncoderSelector.IsKindAvailable(AvifEncoderKind.Nvenc))
        { Skip("NVENC AV1 10-bit", "本机 NVENC 不可用"); return; }

        // 渐变图（纯色可能被编码器走捷径，导致序列头解析不稳定）。
        // 尺寸必须 ≥ NvencAvifBackend.MinNvencDimension(128)，否则驱动报
        // "Frame dimensions are less than the minimum supported value" —— 与位深无关，
        // 曾被误读成"ARGB10 不被接受"。
        const int n = 256;
        var px = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            int x = i % n, y = i / n;
            px[i * 4 + 0] = (byte)(x * 4);
            px[i * 4 + 1] = (byte)(y * 4);
            px[i * 4 + 2] = (byte)((x + y) * 2);
            px[i * 4 + 3] = 255;
        }

        byte[]? bs8 = null, bs10 = null;
        try
        {
            using var dev = NvEncoderNative.CreateNvidiaDevice();
            if (dev is null) { Skip("NVENC AV1 10-bit", "无法创建 NVIDIA D3D11 设备"); return; }
            using var nv = new NvEncoderNative(dev);

            // ═══ 2026-09-17：直接问驱动"AV1 能吃哪些输入格式 / 能否 10-bit 编码" ═══
            // 这决定了"能否把真 10-bit 源送进硬件"，能力表不该靠文档推断。
            // 纯诊断输出，不作断言（驱动差异大，断言会把别的机器判红）。
            var av1 = Guid.Parse("0a352289-0aa7-4759-862d-5d15cd16d254");
            var fmts = nv.QueryInputFormats(av1);
            Console.WriteLine($"    [NVENC-CAPS] AV1 支持的输入格式 ({fmts.Length}):");
            foreach (var f in fmts)
                Console.WriteLine($"       0x{f:X8}  {DescribeBufferFormat(f)}");
            Console.WriteLine($"    [NVENC-CAPS] SUPPORT_10BIT_ENCODE = {nv.QueryEncodeCap(av1, 39)}");

            bs8 = nv.EncodeAv1(px, n, n, 30, 8);
            bs10 = nv.EncodeAv1(px, n, n, 30, 10);
        }
        catch (Exception ex)
        {
            // 能力探测已通过仍失败 ⇒ 属于驱动 / 本次输入特性问题，不该让门禁变红
            Skip("NVENC AV1 10-bit", $"编码抛异常: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Assert("NVENC: 8-bit 路径产出非空", bs8 is { Length: > 0 });
        Assert("NVENC: 10-bit 路径产出非空", bs10 is { Length: > 0 });
        if (bs8 is null || bs10 is null) return;

        int d8 = IvfWriter.ParseAv1SequenceHeader(bs8).bitDepth;
        int d10 = IvfWriter.ParseAv1SequenceHeader(bs10).bitDepth;
        Assert("NVENC: 请求 8-bit → 序列头 8-bit", d8 == 8, $"实际 {d8}");

        // ═══ 2026-09-17 接通：输出位深由 NV_ENC_CONFIG_AV1.outputBitDepth 决定 ═══
        // 该字段偏移由 MSVC offsetof 实测得到（NV_ENC_CONFIG + 280，inputBitDepth + 284），
        // 输入仍是 8-bit ARGB —— 头文件明确硬件会做 8→10 转换。
        // 若这里回落到 8，说明偏移失效或驱动改了默认，必须查，不能当噪声放过。
        Assert("NVENC: 请求 10-bit → 序列头 10-bit（硬件 8→10 转换）", d10 == 10, $"实际 {d10}");
        // ═══ 2026-09-19 收紧：原写作 `d8 != d10 || d8 == 0` ═══
        // 后半句是给"序列头压根没解析出来（bitDepth 返回 0）"开的后门 —— 解析器一旦失效，
        // 两次都得到 0，`d8 == 0` 直接成立，这条就永远是绿的。它想说的其实只有
        // "同一份输入、只换 outputBitDepth ⇒ 差异只能来自那个参数"，
        // 前提是两次都真的读出了位深。故要求两个值都有效且不相等（严格比原来更紧）。
        Assert("NVENC: 10-bit 与 8-bit 走的是同一份输入，位深差异只能来自 outputBitDepth",
            d8 > 0 && d10 > 0 && d8 != d10, $"d8={d8} d10={d10}");
    }

    /// <summary>
    /// 【决定性实验】真 10-bit 输入能否把精度送进硬件（2026-09-17）。
    /// <para>问题：8-bit 输入 + outputBitDepth=10 只是硬件升位深，源精度仍是 8-bit。
    /// 要判断 ARGB10 输入是否真的被当成 10-bit 处理，需要看<b>输出是否保留了 2 个低位</b>。</para>
    /// <para>做法（阶梯判据，对量化噪声不敏感）：256×256 分成 64 条水平色带，
    /// 第 b 条取值 <c>512+b</c> —— 相邻带只差 1，<b>8-bit 根本区分不开</b>。
    ///   · 若硬件按 10-bit 处理 ⇒ 解码亮度是 64 级平滑单调斜坡；
    ///   · 若被先截断成 8-bit 再升回 10-bit ⇒ 出现 16 级台阶（每 4 条带一个值）。
    /// 对照：把同一斜坡降到 8-bit 再走 8-bit 输入，必然是台阶，作为 baseline。</para>
    /// <para>⚠ QP 不能取 0：NVENC 会把 qp=0 映射成无损模式，本机驱动直接报
    /// "Lossless Coding mode not supported"。</para>
    /// </summary>
    static void Test_Nvenc_Argb10PrecisionProbe()
    {
        if (!AvifEncoderSelector.IsKindAvailable(AvifEncoderKind.Nvenc))
        { Skip("NVENC ARGB10 精度探针", "本机 NVENC 不可用"); return; }

        const int n = 256;
        const int bands = 64;
        const int rowsPerBand = n / bands;               // 4 行一带
        // A2R10G10B10: B 占最低 10 位，G 次之，R 再次，A 占最高 2 位
        var argb10 = new byte[n * n * 4];
        var bgra8 = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            int b = y / rowsPerBand;                     // 0..63
            int v = 512 + b;                             // 相邻带只差 1 ⇒ 8-bit 无法区分
            uint word = (uint)(v | (v << 10) | (v << 20));   // A=0, R=G=B=v
            byte v8 = (byte)(v >> 2);                    // 同一值降到 8-bit
            for (int x = 0; x < n; x++)
            {
                int i = (y * n + x) * 4;
                argb10[i + 0] = (byte)(word & 0xFF);
                argb10[i + 1] = (byte)((word >> 8) & 0xFF);
                argb10[i + 2] = (byte)((word >> 16) & 0xFF);
                argb10[i + 3] = (byte)((word >> 24) & 0xFF);
                bgra8[i + 0] = v8; bgra8[i + 1] = v8; bgra8[i + 2] = v8; bgra8[i + 3] = 255;
            }
        }

        string dir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "precision", "probe");
        byte[] bsA = [], bsB = [];   // 先给空数组：真没赋上值时下面的断言会红，而不是编译期定值分析说了算
        // ═══ 2026-09-19：catch 的范围收窄到【设备创建 + 编码调用】═══
        // 原实现把 ffmpeg 解码与阶梯判据也圈在这个 catch 里，于是"自产码流被解码器判死"
        // 乃至"读 ExitCode 抛 InvalidOperationException"全都变成 Skip（假绿）。
        // 这一段失败才算环境问题；解码与判据在 try 之外，失败一律判红。
        try
        {
            dir = Path.GetFullPath(dir);
            Directory.CreateDirectory(dir);
            using var dev = NvEncoderNative.CreateNvidiaDevice();
            if (dev is null) { Skip("NVENC ARGB10 精度探针", "无法创建 NVIDIA D3D11 设备"); return; }
            using var nv = new NvEncoderNative(dev);

            // QP 不能取 0（会触发不支持的无损模式），取 1 —— 平坦色带在此下几乎无损
            bsA = nv.EncodeAv1Argb10(argb10, n, n, 1);   // 真 10-bit 输入
            bsB = nv.EncodeAv1(bgra8, n, n, 1, 10);      // 8-bit 输入 + 10-bit 输出（升位深）
            File.WriteAllBytes(Path.Combine(dir, "argb10_in.av1"), bsA);
            File.WriteAllBytes(Path.Combine(dir, "argb8_in.av1"), bsB);
            File.WriteAllBytes(Path.Combine(dir, "src10.raw"), argb10);
        }
        catch (Exception ex)
        {
            // 能力探测已通过仍失败 ⇒ 属于驱动 / 本次输入特性问题，不该让门禁变红
            Skip("NVENC ARGB10 精度探针", $"编码抛异常: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        Console.WriteLine($"    [ARGB10-PROBE] dir={dir}");
        Console.WriteLine($"    [ARGB10-PROBE] 10-bit 输入产出 {bsA.Length} 字节，序列头位深={IvfWriter.ParseAv1SequenceHeader(bsA).bitDepth}");
        Console.WriteLine($"    [ARGB10-PROBE] 8-bit  输入产出 {bsB.Length} 字节，序列头位深={IvfWriter.ParseAv1SequenceHeader(bsB).bitDepth}");
        Assert("ARGB10 探针: 10-bit 输入产出非空", bsA.Length > 0);
        Assert("ARGB10 探针: 序列头声明 10-bit",
            IvfWriter.ParseAv1SequenceHeader(bsA).bitDepth == 10,
            $"实际 {IvfWriter.ParseAv1SequenceHeader(bsA).bitDepth}");

        // ═══ 2026-09-17 §5-3：探针升级为断言（交接 §3 的阶梯判据固化）═══
        // 8-bit 输入 + 10-bit 输出 = 完美 4 级台阶（同值平均连续带数 4.57）；
        // 真 10-bit 输入 = 平滑斜坡（54/64 级、同值平均连续带数 1.19）。
        // 判据对量化噪声不敏感（相邻带源值只差 1，8-bit 链路区分不开）。
        var ff = FindFfmpeg();
        if (ff is null)
        {
            // 全谱里唯一还配得上 Skip 的分支：本机没装 ffmpeg —— 那与产物对不对无关
            Skip("ARGB10 精度阶梯断言", "未找到 ffmpeg（需 C:\\Windows\\ffmpeg-8.0-full_build 或 PATH）");
            return;
        }

        var raw = Path.Combine(dir, "argb10_in_dec.raw");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                Arguments = $"-v error -i \"{Path.Combine(dir, "argb10_in.av1")}\" -f rawvideo -pix_fmt gray10le -y \"{raw}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var err = p.StandardError.ReadToEnd();
            // WaitForExit(int) 超时后读 ExitCode 会抛 InvalidOperationException —— 原实现里那个
            // 异常被外层 catch 吞成 Skip，于是"ffmpeg 卡死"永远显示为跳过。
            bool exited = p.WaitForExit(30000);
            if (!exited) { try { p.Kill(); } catch { } }
            int code = exited ? p.ExitCode : -1;

            // ═══ 2026-09-19 修假绿：被 ffmpeg 判死的是【我们自己产出的 .av1】═══
            // 这是产品缺陷，不是环境限制，原写作 Skip("…ffmpeg 解码失败") 等于把缺陷藏进跳过数。
            Assert("ARGB10 探针: 自产 .av1 能被 ffmpeg 解码（非零退出/超时一律判红）",
                exited && code == 0,
                exited ? $"ExitCode={code} {err.Trim()}" : "ffmpeg 30 秒未退出");
            if (!exited || code != 0) return;

            var decoded = File.ReadAllBytes(raw);
            // gray10le: 每样本 2 字节小端；256x256
            double distinct = 0, avgRun = 0;
            if (decoded.Length >= n * n * 2)
            {
                var bandVals = new double[bands];
                for (int b = 0; b < bands; b++)
                {
                    long sum = 0; int cnt = 0;
                    for (int y = b * rowsPerBand; y < (b + 1) * rowsPerBand; y++)
                        for (int x = 0; x < n; x++)
                        { sum += decoded[(y * n + x) * 2] | (decoded[(y * n + x) * 2 + 1] << 8); cnt++; }
                    bandVals[b] = (double)sum / cnt;
                }
                // 相邻带取值不同 = 阶梯分级的"级"数
                int levels = 1;
                for (int b = 1; b < bands; b++)
                    if (bandVals[b] != bandVals[b - 1]) levels++;
                double runs = 1;
                for (int b = 1; b < bands; b++)
                    if (bandVals[b] != bandVals[b - 1]) runs++;
                avgRun = bands / runs;
                distinct = levels;
            }
            Assert("ARGB10 精度: 64 带解码出 ≥48 个不同亮度级（真 10-bit 斜坡，8-bit 升位深只有 ~16）",
                distinct >= 48, $"实际 {distinct}/64");
            Assert("ARGB10 精度: 同值平均连续带数 < 2（台阶判据，8-bit 路径实测 4.57）",
                avgRun > 0 && avgRun < 2.0, $"实际 {avgRun:F2}");
            Console.WriteLine($"    [ARGB10-PROBE] 阶梯判据: 不同级={distinct}/64, 同值平均连续带={avgRun:F2}");
        }
        catch (Exception ex)
        {
            // 解码环节的异常同样不是"环境问题"（ffmpeg 已确认存在，输入是我们自己写的文件）
            Assert("ARGB10 探针: 自产 .av1 能被 ffmpeg 解码", false, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally { try { File.Delete(raw); } catch { } }
    }

    /// <summary>定位 ffmpeg.exe（阶梯判据解码用）：已知安装点 + PATH。</summary>
    static string? FindFfmpeg()
    {
        foreach (var p in new[]
        {
            @"C:\Windows\ffmpeg-8.0-full_build\bin\ffmpeg.exe",
            @"C:\ffmpeg\bin\ffmpeg.exe",
        })
            if (File.Exists(p)) return p;
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var d in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var f = Path.Combine(d, "ffmpeg.exe"); if (File.Exists(f)) return f; }
            catch { }
        }
        return null;
    }

    /// <summary>NV_ENC_BUFFER_FORMAT 的可读名称（诊断输出用）。</summary>
    static string DescribeBufferFormat(uint f) => f switch
    {
        0x00000000 => "UNDEFINED",
        0x00000001 => "NV12 (8-bit 4:2:0 semi-planar)",
        0x00000010 => "YV12 (8-bit 4:2:0 planar)",
        0x00000100 => "IYUV (8-bit 4:2:0 planar)",
        0x00001000 => "YUV444 (8-bit planar)",
        0x00010000 => "YUV420_10BIT ★ (10-bit 4:2:0 semi-planar, MSB 10b/2B)",
        0x00100000 => "YUV444_10BIT ★ (10-bit planar)",
        0x01000000 => "ARGB (8-bit packed A8R8G8B8)",
        0x02000000 => "ARGB10 ★ (10-bit packed A2R10G10B10)",
        0x04000000 => "AYUV (8-bit packed)",
        0x10000000 => "ABGR (8-bit packed A8B8G8R8)",
        0x20000000 => "ABGR10 ★ (10-bit packed A2B10G10R10)",
        0x40000000 => "U8 (1-D buffer)",
        0x40000001 => "NV16 (8-bit 4:2:2 semi-planar)",
        0x40000002 => "P210 ★ (10-bit 4:2:2 semi-planar)",
        _ => "?",
    };

    /// <summary>
    /// AV1 序列头解析回归 —— 用<b>本机 NVENC 实拍码流</b>作夹具，期望值由两个独立来源交叉确认：
    /// <para>① ffmpeg <c>-bsf:v trace_headers</c> 输出的逐字段位位置；
    /// ② libdav1d 实际解码得到的像素格式（<c>yuv420p</c> / <c>yuv420p10le</c>）。</para>
    /// <para>夹具 = OBU_TEMPORAL_DELIMITER + OBU_SEQUENCE_HEADER(11B)，
    /// 两片仅第 10 字节（payload byte 9）的 bit 2 不同 —— 那就是 <c>high_bitdepth</c>。</para>
    /// <para>历史缺陷：旧解析器漏读 frame_width_bits / max_frame_* 且 seq_choose_* 语义写反，
    /// 累积错位 19 位 ⇒ 10-bit 被判成 8-bit、4:2:0 被判成 4:4:4。</para>
    /// </summary>
    /// <summary>
    /// AV1 序列头解析器 —— 夹具一律用【真实产物的字节】。
    /// <para>为什么重做（2026-09-20，交接 §32.9-4）：旧夹具是手写的 11 字节 payload，其
    /// matrix_coefficients 恰好落在 <b>0(Identity)</b> 上 ⇒ 走的是解析器里"CICP 为 sRGB-identity
    /// 就硬写 1,1"那条分支，所以"4:2:0 判对"在坏代码上也成立、测试恒绿。而 NVENC 真实码流的
    /// matrix 是 1，走 else 分支：那里 <b>profile 0 的码流根本不写 subsampling 位</b>
    /// （规范规定恒 4:2:0），旧实现却读 2 位 ⇒ 读到的是 chroma_sample_position 的 00
    /// ⇒ 三份硬件 AVIF 的 av1C 全被标成 4:4:4（avifdec 读我们的容器报 YUV444，
    /// 而 ffmpeg 解同一条码流报 yuv420p）。夹具的形状正好绕开了缺陷。</para>
    /// <para>现在五份夹具都从真实产物里抓（注释标明来源），覆盖三种 profile × 两种 header 形态
    /// × 4:4:4 / 4:2:2 / 4:2:0：任何一位数错都会露出来。容器记录与码流真身的交叉核对
    /// 由 <c>scripts/precision/av1c_vs_bitstream.py</c> 常驻把关。</para>
    /// </summary>
    static void Test_Av1SequenceHeader_Parse()
    {
        // (来源样张, 序列头 OBU 完整字节, profile, 位深, chromaSubX, chromaSubY)
        // 期望值不是手算的：pix_fmt 由 ffprobe（AV1 解码器读码流）报出，profile 直接来自 OBU 头三个比特。
        var cases = new (string From, byte[] Obu, int Profile, int Depth, int X, int Y)[]
        {
            // NVENC SDR（sRGB 目标）：profile 0 + 非 reduced header —— 码流里【没有】subsampling 位
            ("nvenc/SDR",
             [0x0A, 0x0E, 0x00, 0x00, 0x00, 0x04, 0x43, 0xFD, 0xFE, 0x01, 0x0C, 0xC8, 0x08, 0x68, 0x0C, 0x20],
             0, 8, 1, 1),
            // NVENC 10-bit（样张 c_avifnv_444 / d_avifnv_10：请求 4:4:4，驱动恒按 4:2:0 编）
            ("nvenc/10bit",
             [0x0A, 0x0E, 0x00, 0x00, 0x00, 0x04, 0x43, 0xFD, 0xFE, 0x01, 0x0C, 0xE8, 0x08, 0x68, 0x0C, 0x20],
             0, 10, 1, 1),
            // avifenc 的 reduced_still_picture_header=1：9 个 feature flag 全是推断值，多读一位就错位
            ("avifenc/reduced",
             [0x0A, 0x0A, 0x18, 0x1D, 0xFF, 0xFF, 0xDA, 0x20, 0x21, 0xA0, 0x30, 0x80],
             0, 8, 1, 1),
            // profile 1（High）：mono_chrome 是推断值、不占比特，4:4:4
            ("avifenc/p1-444",
             [0x0A, 0x09, 0x38, 0x1D, 0xFF, 0xFF, 0xDA, 0xC0, 0x43, 0x40, 0x64],
             1, 10, 0, 0),
            // profile 2 + 12-bit：这一档的 subsampling 位【确实】写在码流里，值 0,0
            ("avifenc/p2-12bit",
             [0x0A, 0x0A, 0x58, 0x1D, 0xFF, 0xFF, 0xDA, 0xD0, 0x10, 0xD0, 0x18, 0x80],
             2, 12, 0, 0),
            // profile 2 + 非 12-bit：规范推断 4:2:2，码流不写
            ("avifenc/p2-422",
             [0x0A, 0x09, 0x58, 0x1D, 0xFF, 0xFF, 0xDA, 0x90, 0x10, 0xD0, 0x19],
             2, 10, 1, 0),
        };

        foreach (var c in cases)
        {
            var r = IvfWriter.ParseAv1SequenceHeader(c.Obu);
            Assert($"序列头[{c.From}]: profile={c.Profile} 位深={c.Depth}",
                r.profile == c.Profile && r.bitDepth == c.Depth,
                $"实得 profile={r.profile} bitDepth={r.bitDepth}");
            Assert($"序列头[{c.From}]: 色度=({c.X},{c.Y}) 且非单色",
                r.chromaSubX == c.X && r.chromaSubY == c.Y && !r.mono,
                $"实得 ({r.chromaSubX},{r.chromaSubY}) mono={r.mono}");
            Assert($"序列头[{c.From}]: 完整 OBU 原样捕获（av1C 的 configOBUs 用它）",
                r.seqHeaderObu is not null && r.seqHeaderObu.SequenceEqual(c.Obu),
                r.seqHeaderObu is null ? "null" : $"{r.seqHeaderObu.Length}B");
        }
    }

    /// <summary>
    /// HDR 场景必须区分「用户显式选了硬件编码器」与「Auto 自动解析到硬件」。
    /// <para>回归背景（2026-09-17）：旧实现不区分二者，一律弹 Warning Toast。
    /// 而 Auto 是产品默认值 —— 于是<b>用默认设置 + HDR 时第一次截图必然弹出
    /// "NVENC 不支持 HDR"</b>，可用户从未选过 NVENC，这条提示既不可操作也令人困惑。
    /// Auto 的自动回落属于正常协商结果，只应记 Info 日志。</para>
    /// </summary>
    static void Test_Pipeline_HdrAutoNoFalseAlarm()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (path is null) { Skip("HDR Auto 不弹假警报", "未找到源文件"); return; }

        var text = ReadSourceNormalized(path);
        int i = text.IndexOf("public EncodingSettings BuildEncodingSettings", StringComparison.Ordinal);
        if (i < 0) { Assert("找到 BuildEncodingSettings", false); return; }
        if (!TrySliceMethodBody(text, i, out var body))
        { Assert("必须切到 BuildEncodingSettings 的收尾锚点", false, "锚点丢失 ⇒ 不许拿整份文件判分支"); return; }

        Assert("HDR: 记录用户是否显式选择了编码器", body.Contains("avifAutoResolved"));

        int iAuto = body.IndexOf("if (avifAutoResolved)", StringComparison.Ordinal);
        Assert("HDR: 存在按 Auto / 显式选择分流的分支", iAuto >= 0);
        if (iAuto < 0) return;

        int iElse = body.IndexOf("\n                    else", iAuto, StringComparison.Ordinal);
        var autoBranch = iElse > iAuto ? body[iAuto..iElse] : "";
        Assert("HDR: Auto 分支不弹 Toast（否则默认设置 + HDR 会弹假警报）",
            autoBranch.Length > 0 && !autoBranch.Contains("ShowToast"));
        Assert("HDR: Auto 分支只记 Info 日志（回落仍可追溯）",
            autoBranch.Contains("LogService.Info"));
        Assert("HDR: 用户显式选择硬件时仍然提示",
            body.Contains("ToastService.ShowToast(\"AVIF HDR 编码器提示\""));
    }

    /// <summary>
    /// 编码设置必须来自 UI 线程取的快照：管线里不允许存在"没传快照就去读活设置"的路径。
    /// <para>回归背景（2026-09-19，交接 §26.4 P1）：<c>BuildEncodingSettings</c> 原先在
    /// <c>settingsSnapshot</c> 缺省时静默退回读 <c>_settings.Current</c>，而它要从设置对象里取
    /// 位深 / 色度 / AVIF 编码器选择 / 每格式质量等十余项。JXL、libaom AVIF 编码 0.5–2s，
    /// 足够用户在按下截图后改一次下拉 ⇒ 产物可能与"按下那一刻"的选择无关（正是 §11.3
    /// 建 <c>EncodingUiState</c> 快照要杜绝的那类跨线程读取）。
    /// 现在快照是必传参数，且唯一自建设置的 <c>EncodeAndSaveAsync</c> 重载（零调用者）已删除。</para>
    /// </summary>
    static void Test_Pipeline_EncodeSettingsRequireSnapshot()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (path is null) { Skip("编码设置走快照", "未找到源文件"); return; }
        var text = ReadSourceNormalized(path);

        int i = text.IndexOf("public EncodingSettings BuildEncodingSettings", StringComparison.Ordinal);
        Assert("找到 BuildEncodingSettings", i >= 0);
        if (i < 0) return;
        if (!TrySliceMethodBody(text, i, out var body))
        { Assert("必须切到 BuildEncodingSettings 的收尾锚点", false, "锚点丢失 ⇒ 整份文件都会被当成方法体"); return; }

        int sigEnd = body.IndexOf('{');
        var sig = sigEnd > 0 ? body[..sigEnd] : body;
        Assert("快照参数必传（可选/可空即等于留着静默旁路）",
            sig.Contains("AppSettingsData settingsSnapshot")
            && !sig.Contains("AppSettingsData? ") && !sig.Contains("settingsSnapshot ="),
            sig.Replace(Environment.NewLine, " "));
        Assert("方法体内不再读活设置 _settings.Current", !body.Contains("_settings.Current"));

        // 全文件只允许"定义"这一处 BuildEncodingSettings( —— 任何内部调用点都要走 MainWindow 的快照入口
        int calls = 0;
        for (int p = text.IndexOf("BuildEncodingSettings(", StringComparison.Ordinal);
             p >= 0; p = text.IndexOf("BuildEncodingSettings(", p + 1, StringComparison.Ordinal))
            calls++;
        Assert("管线内部不自建编码设置（否则又绕过快照）", calls == 1, $"实际 {calls} 处");
    }

    /// <summary>
    /// NVENC 的"色度请求未生效"必须留痕，但 <b>420 请求不能留痕</b>。
    /// <para>背景（2026-09-19，交接 §26.4 P2-3）：NVENC 从不写 chromaFormatIDC ⇒ 驱动恒按 4:2:0 编码，
    /// 请求 4:4:4 也<b>不报错</b>。产品路径的能力协商会先把 444 降到 420（那时后端看到的就是 420），
    /// 所以"420 ⇒ null"这条恰恰是防噪音的关键：若它也返回说明，默认设置在每次硬件截图后
    /// 都会弹一条"参数未生效"的假警告。反之，后端被直接调用（Tools / 未来调用点）时
    /// 444/422 必须留下痕迹，而不是静默产出 420。</para>
    /// </summary>
    static void Test_Nvenc_ChromaDowngradeNote()
    {
        Assert("NVENC 色度: 请求 420 不留痕（默认设置不得产生假警告）",
            NvencAvifBackend.ChromaDowngradeNote("420") is null);
        Assert("NVENC 色度: 未指定色度不留痕",
            NvencAvifBackend.ChromaDowngradeNote(null) is null);

        var note444 = NvencAvifBackend.ChromaDowngradeNote("444");
        Assert("NVENC 色度: 请求 444 必须写明「444 未生效、实际 4:2:0」",
            note444 is not null && note444.Contains("444") && note444.Contains("4:2:0"),
            note444 ?? "null");
        Assert("NVENC 色度: 请求 422 同样留痕",
            NvencAvifBackend.ChromaDowngradeNote("422") is not null);
    }

    /// <summary>
    /// 全编码器诚实性审计（2026-09-19）的六条契约 —— 每条都对应一个当时已被实测证实的缺陷形态。
    /// <para>审计方法：同一样张只改一个参数，比较产物**字节是否相同**（相同 ⇒ 该参数根本没进编码器），
    /// 再用 ffprobe 读回 pix_fmt 佐证；完整结论见交接 §27。</para>
    /// </summary>
    static void Test_EncoderHonesty_Audit20260919()
    {
        // ① JXL 色度必须映射到 cjxl 的真实命令行参数。修复前 444 与 420 产物逐字节相同（881 B vs 881 B）。
        Assert("JXL: 444 → --resampling 1",
            NativeJxlEncoder.BuildResamplingArg("444") == "--resampling 1",
            NativeJxlEncoder.BuildResamplingArg("444"));
        Assert("JXL: 420 → --resampling 2",
            NativeJxlEncoder.BuildResamplingArg("420") == "--resampling 2",
            NativeJxlEncoder.BuildResamplingArg("420"));
        // cjxl 只有 1/2/4/8 方形降采样，【没有 4:2:2 档】。宁可返回空串让编码器自选，
        // 也不许拿"看着最近"的 4x4 冒充 422 —— 那又是一次静默改参数。
        Assert("JXL: 422 / 未知 / 空 ⇒ 不传 --resampling",
            NativeJxlEncoder.BuildResamplingArg("422").Length == 0
            && NativeJxlEncoder.BuildResamplingArg("bogus").Length == 0
            && NativeJxlEncoder.BuildResamplingArg(null).Length == 0);

        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (xamlPath is not null)
        {
            var xaml = File.ReadAllText(xamlPath);
            // ② 界面上不许再留"调了没用"的控件。WebP 有损格式定义就是 4:2:0（cwebp 也确无色度参数）
            //    ⇒ 下拉撤掉、换成静态说明；本断言同时拦住"好心加回来"。
            Assert("WebP: 色度下拉已撤掉、改为静态说明文案",
                !xaml.Contains("ChromaWebPCbo") && xaml.Contains("WebPChromaNoteTxt"));
            var xlTags = ExtractComboItemTags(xaml, "ChromaJpegXlCbo");
            Assert("JXL 色度下拉只暴露 cjxl 真能表达的 444/420",
                xlTags.SequenceEqual(new[] { "444", "420" }, StringComparer.Ordinal),
                string.Join(",", xlTags));
        }

        // ③ 三处原先只有 Debug.WriteLine（Release 无声）的静默保底，必须写进通用降级通道
        var core = Path.Combine("src", "TrueToneCap.Core", "Encoding");
        string Read(string file)
        {
            var p = FindRepoFile(Path.Combine(core, file));
            return p is null ? "" : File.ReadAllText(p);
        }
        var fe = Read("FormatEncoders.cs");
        var na = Read("NativeAvifEncoder.cs");
        int NotifierWrites(string t)
        {
            int n = 0;
            for (int i = t.IndexOf("EncoderFallbackNotifier.LastReason", StringComparison.Ordinal);
                 i >= 0; i = t.IndexOf("EncoderFallbackNotifier.LastReason", i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }
        Assert("保底: cwebp 缺失→PNG 与 MFT→libaom 都留痕（≥2 处写入）",
            NotifierWrites(fe) >= 2, $"实际 {NotifierWrites(fe)} 处");
        Assert("保底: avifenc 拒位深→按 8-bit 重试要留痕",
            na.Contains("EncoderFallbackNotifier.LastReason"));

        // ④ 托管编码器不得直写最终路径：中途异常会在最终路径留下半成品坏文件
        //    匹配 "= File.Create(path)" 而不是裸 "File.Create(path)" —— 后者会命中
        //    "原为 File.Create(path) 直写"这类说明注释，把已修好的代码误判成未修（本条第一版就踩了）。
        var png = Read("ManagedPngEncoder.cs");
        var tiff = Read("ManagedTiffEncoder.cs");
        Assert("PNG/TIFF: 产物一律经 AtomicFileWriter 原子写（不留 = File.Create(path) 直写）",
            png.Length > 0 && tiff.Length > 0
            && !png.Contains("= File.Create(path)") && !tiff.Contains("= File.Create(path)")
            && png.Contains("AtomicFileWriter.Write(") && tiff.Contains("AtomicFileWriter.Write("));

        // ⑤ 只写不读的设置字段一律删除（WebP 的下拉已撤，TIFF 界面上本就没有）
        var modelPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        var pipePath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        if (modelPath is not null)
        {
            var model = File.ReadAllText(modelPath);
            Assert("设置模型: ChromaWebP / ChromaTiff 两个字段已删",
                !model.Contains("public string ChromaWebP")
                && !model.Contains("public string ChromaTiff"));
            // ⑥ 删字段的**兼容前提**：老用户的 settings.json 里还留着这些键。System.Text.Json 默认
            //    跳过未映射成员，所以能正常加载；一旦有人改成 Disallow，整份旧配置会在 Deserialize
            //    抛 JsonException —— 而 SettingsService 的既有注释写明那会导致"静默重置为默认值"。
            //    Test 工程不引用 App 工程，无法真反序列化，故钉住"该特性只许是 Skip"这条前提。
            // ═══ 2026-09-19 修假绿：原写法为 !model.Contains(Disallow) || model.Contains("Skip") ═══
            //    ① 后半句是全文件级兜底 —— 文件里任何一处出现 Skip（注释里出现也算）就能把 Disallow 救活，
            //      与本仓已修过的 `d8 == 0` 同形；② 文案声称查了 AppSettingsData / JsonSerializerContext
            //      两处，实际 model 只覆盖前者。现改为【就近判定】：正则抓特性实参，逐个断它等于 Skip，
            //      并把真正的 JsonSerializerContext 声明处（AppJsonContext.cs）纳入同一轮扫描。
            foreach (var (label, srcPath) in new[]
            {
                ("AppSettingsData", modelPath),
                ("AppJsonContext", FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "AppJsonContext.cs"))),
            })
            {
                if (srcPath is null)
                {
                    Skip($"设置模型: {label} 的 UnmappedMemberHandling 前提", "未找到源文件");
                    continue;
                }
                var hits = Regex.Matches(File.ReadAllText(srcPath),
                    @"\[\s*JsonUnmappedMemberHandling\s*\(\s*(?:JsonUnmappedMemberHandling\.)?(\w+)\s*\)\s*\]");
                string offending = "";
                foreach (Match m in hits)
                    if (m.Groups[1].Value != "Skip") { offending = m.Groups[1].Value; break; }
                Assert($"设置模型: {label} 上每个 [JsonUnmappedMemberHandling(X)] 的 X 都是 Skip（该文件共 {hits.Count} 处）",
                    offending.Length == 0,
                    hits.Count == 0 ? "未出现该特性 ⇒ 走 STJ 默认（跳过未映射成员），前提成立"
                                    : $"出现 X={offending} ⇒ 旧 settings.json 会在 Deserialize 抛 JsonException → 静默重置");
            }
        }

        // ⑦ 每个还活着的色度下拉，都必须有"被管线读取"的同源字段
        //    （历史上 AvifChroma/ChromaAvif 双字段就是这类问题：UI 写一个、管线读另一个，
        //     改了不生效，界面却显示改成功了 —— 见 Test_Settings_ChromaSingleSource）
        if (xamlPath is not null && pipePath is not null)
        {
            var xamlText = File.ReadAllText(xamlPath);
            var pipeText = File.ReadAllText(pipePath);
            foreach (var (combo, field) in new[]
            {
                ("AvifChromaCbo", "ChromaAvif"),
                ("ChromaJpegLiCbo", "ChromaJpegLi"),
                ("ChromaJpegXlCbo", "ChromaJpegXl"),
                // 2026-09-26：GainMapChromaCbo 随"增益图并入 JPEG LI"删除 —— 见下面两条专项断言。
            })
            {
                Assert($"色度下拉 {combo} 存在于 XAML", xamlText.Contains($"x:Name=\"{combo}\""));
                Assert($"管线映射表读 s.{field}（不读则该下拉是装饰）",
                    pipeText.Contains($"s.{field}"));
            }

            // 增益图态的色度必须与 JPEG LI 同源，且【不得】再长出第二个下拉
            Assert("管线对 JPEG_GAINMAP 读 JPEG LI 的位深与色度（合并后同源）",
                pipeText.Contains("OutputFormat.JPEG_GAINMAP => (s.BitDepthJpegLi, s.ChromaJpegLi)"),
                "增益图态若还读 ChromaGainMap，那个字段已经没有任何界面能改它 ⇒ 假配置复活");
            Assert("XAML 里不得再有第二个增益图色度下拉（两个控件写同一个字段就是刚合并掉的分裂）",
                !xamlText.Contains("GainMapChromaCbo"));
        }
    }

    /// <summary>
    /// 增益图的色度必须是"真设置"：默认 4:2:0，最高允许手动选 4:4:4（2026-09-19 用户决定）。
    /// <para>回归背景（交接 §27）：主路径把 base JPEG 硬编码成 "444"，只有两条降级旁路读设置
    /// ⇒ 同一格式因走哪条路而色度不同，字段本身形同虚设。现在主路径与旁路同源。</para>
    /// <para>2026-09-26（增益图并入 JPEG LI）：这条判据的<b>语义不变、载体变了</b> ——
    /// 生效的下拉与字段改成 JPEG LI 那一对（<c>ChromaJpegLiCbo</c> / <c>ChromaJpegLi</c>），
    /// 因为管线对 JPEG_GAINMAP 也读它。所以本用例同时钉三件事：
    /// 编码器不硬编码色度、生效字段默认 420、以及"不许再长出第二个增益图色度下拉"（见 ⑦ 那两条）。</para>
    /// </summary>
    static void Test_GainMap_ChromaIsRealSetting()
    {
        var gmPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "JpegGainMapEncoder.cs"));
        var modelPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (gmPath is null || modelPath is null || xamlPath is null)
        { Skip("GainMap 色度是真设置", "未找到源文件"); return; }

        var gm = File.ReadAllText(gmPath);
        int CountOf(string t, string needle)
        {
            int n = 0;
            for (int i = t.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = t.IndexOf(needle, i + 1, StringComparison.Ordinal))
                n++;
            return n;
        }

        Assert("GainMap: base 编码不再硬编码 \"444\"",
            !gm.Contains("distance, \"444\", icc"), "应改为传下的 chroma 形参");
        Assert("GainMap: 主路径与降级旁路都把设置里的色度传给 base 编码",
            CountOf(gm, "EncodeToJpegBytesSafe(sdrBgra, w, h, ") == 2
            && CountOf(gm, "settings.ChromaSubsampling") >= 2,
            $"调用点 {CountOf(gm, "EncodeToJpegBytesSafe(sdrBgra, w, h, ")} 处，传色度 {CountOf(gm, "settings.ChromaSubsampling")} 处");
        // 增益图平面走真灰度（YCbCr400），与 base 的色度开关无关 —— 别把它一并改掉
        Assert("GainMap: 增益图平面仍走灰度编码", gm.Contains("EncodeGray"));

        // 默认值与下拉档位同源（三处：字段默认 / XAML IsSelected / 兜底值）。
        // 2026-09-26：增益图并入 JPEG LI ⇒ 生效的下拉就是 ChromaJpegLiCbo、生效的字段是 ChromaJpegLi。
        // ChromaGainMap 降级为"只被一次性迁移读取的历史字段"，但它仍须默认 420 ——
        // 迁移靠"是否等于默认"判断用户有没有改过，默认漂了就会把没改过的老值当成用户选择搬过去。
        var model = File.ReadAllText(modelPath).Replace('"', '\'');
        Assert("增益图态实际生效的色度字段 ChromaJpegLi 默认 420",
            model.Contains("public string ChromaJpegLi { get; set; } = '420'"));
        Assert("遗留字段 ChromaGainMap 仍默认 420（迁移拿它判'用户是否改过'，默认漂=误搬）",
            model.Contains("public string ChromaGainMap { get; set; } = '420'"));
        var xaml = File.ReadAllText(xamlPath).Replace('"', '\'');
        int i = xaml.IndexOf("ChromaJpegLiCbo", StringComparison.Ordinal);
        int end = i >= 0 ? xaml.IndexOf("</ComboBox>", i, StringComparison.Ordinal) : -1;
        var block = (i >= 0 && end > i) ? xaml[i..end] : "";
        Assert("JPEG LI 色度下拉存在且默认落在 420 项（增益图态共用这一条）",
            block.Contains("Tag='420' IsSelected='True'"), block.Length > 0 ? "" : "未找到 ChromaJpegLiCbo 块");
        Assert("该下拉提供 422 与 444（用户可手动升到最高色度，增益图态同样有效）",
            block.Contains("Tag='422'") && block.Contains("Tag='444'"));
    }

    /// <summary>
    /// 源精度必须由"**Windows 给不给得出 &gt;8-bit**"决定，而不是由用户这次的输出意图决定
    /// （2026-09-19 用户对 §4.3 双路径策略的修正）。
    /// <para>旧逻辑：`PreferHdr = hdrOutput ‖ 目标是广色域` ⇒ 10-bit/ACM 显示器上只要没开 HDR 输出，
    /// 抓取就直接落到 8-bit BGRA，后面所有色域矩阵与色调映射都只能在 8-bit 数据上做。
    /// 新逻辑：调用方恒请求 Float16；给不给得出由 <c>CanDeliverExtendedRange</c>（DXGI 能力）决定，
    /// 并把结论写进能力负缓存，避免"每次截图都付一次首帧超时"。</para>
    /// </summary>
    /// <summary>
    /// WGC 的两个 GUID 各有其主，不许再互相顶替（2026-09-21 根因）。
    /// <para>代码曾把**类 IID**（= 默认接口 <c>IGraphicsCaptureItem</c> 的 IID）同时用作
    /// "QI 激活工厂的 interop 接口 IID"。前者作为 <c>CreateForMonitor</c> 的 riid 是对的，
    /// 后者错 ⇒ 症状不是崩溃而是**恒 E_NOINTERFACE**：方法0/方法2 在所有机器上静默失效，
    /// 捕获一直由 <c>TryCreateFromDisplayId</c> 兜着，只盯"能不能截图"完全看不出来。
    /// 权威值取自 <c>Windows Kits\10\Include\{10.0.26100.0,10.0.28000.0}\um\Windows.Graphics.Capture.Interop.h</c>
    /// （两版一致）；实机对撞见 <c>publish/probe-wgc-iid.ps1</c>。</para>
    /// <para>⚠ 这里只做源码文本判据（与 §27 同一手法）：真值验证交给方法0 的 hr —— 它一旦返回
    /// S_OK，就同时证明了 interop IID 与 riid 两处都取对了。</para>
    /// </summary>
    static void Test_WgcInteropIidsAreDistinct()
    {
        var wgcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "WgcCaptureService.cs"));
        if (wgcPath is null) { Skip("WGC interop IID", "未找到源文件"); return; }
        var wgc = File.ReadAllText(wgcPath);
        const string InteropIid = "3628E81B-3CAC-4C60-B7F4-23CE0E0C3356";
        const string ClassIid   = "79C3F95B-31F7-4EC2-A464-632EF5D30760";
        static int CountOf(string t, string n)
        {
            int c = 0;
            for (int i = t.IndexOf(n, StringComparison.Ordinal); i >= 0; i = t.IndexOf(n, i + 1, StringComparison.Ordinal)) c++;
            return c;
        }

        Assert("WGC interop 接口 IID = SDK 认定值", wgc.Contains(InteropIid));
        Assert("[ComImport] 的 Guid 引用常量而非字面量（防两处各写一份）",
            wgc.Contains("[ComImport, Guid(WgcInteropIid)]"));
        Assert("方法2 的 interopGuid 走同一常量", wgc.Contains("new Guid(WgcInteropIid)"));
        // ② 类 IID 只该出现在常量定义那一处；一旦又出现第二处，多半是拿它去顶替 interop IID 的老毛病复发
        Assert("类 IID 字面量全文件仅一处（不再顶替 interop IID）", CountOf(wgc, ClassIid) == 1,
            $"实际 {CountOf(wgc, ClassIid)} 处");
        // 只比代码行：下面这段注释里就原样写着 `typeof(GraphicsCaptureItem).GUID`，
        // 拿全文做否定断言会把已改好的代码判成未改（同 §3601 那条老教训，本机刚踩过一次）。
        static string CodeOnly(string t) => string.Join("\n", t.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        var wgcCode = CodeOnly(wgc);

        // CreateForMonitor 的 riid 必须是**显式类 IID**：投影类挂的不是类 IID（实测值见 ℹ️ 行），
        // 拿 typeof(...) 当 riid ⇒ CreateForMonitor 内部 QI 返回 E_NOINTERFACE，
        // 与"接口 IID 用错"同症状、不同位置 —— 第一次只修 IID 后本机仍失败就是卡在这。
        Assert("riid 走显式常量，不依赖投影类的 GUID",
            wgcCode.Contains("var itemGuid = new Guid(WgcItemClassIid);") && !wgcCode.Contains("typeof(GraphicsCaptureItem).GUID"),
            "仍应从 WgcItemClassIid 取");
        var projectedGuid = typeof(Windows.Graphics.Capture.GraphicsCaptureItem).GUID;
        // 把"投影类 GUID 不等于类 IID"记成断言而不是注释：哪天 CsWinRT 挂上了类 IID，
        // 这条会红，提醒可以改回 typeof(...) —— 判据要能被机器复查，不能只活在话里。
        Assert("投影类 GUID 实测值不等于类 IID（故必须用显式常量）", projectedGuid != new Guid(ClassIid),
            $"实测 {projectedGuid}");
        Console.WriteLine($"  ℹ️ 实测 typeof(GraphicsCaptureItem).GUID = {projectedGuid}（Interop.h 认定的类 IID 是 {ClassIid}）");

        // DisplayId 是"回退 1"，它静默返回 null 时用户看到的是"完全无法捕获"而日志一个字都没有
        Assert("DisplayId 反查失败分档留痕（GetMonitorInfo/无区域/异常 各一条）",
            CountOf(wgc, "DisplayId 反查失败") >= 2 && wgc.Contains("DisplayId 反查抛异常"),
            "三档失效必须可区分");
        Assert("CreateItem 全灭是 Info 级读数（默认档要能看见根因）",
            wgc.Contains("LogInfo(\"[WGC] ❌ 所有方法均无法创建 GraphicsCaptureItem\")"));
    }

    static void Test_Pipeline_SourcePrecisionIsCapabilityDriven()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var wgcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "WgcCaptureService.cs"));
        var crPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "CaptureResult.cs"));
        if (mwPath is null || wgcPath is null || crPath is null)
        { Skip("源精度由能力决定", "未找到源文件"); return; }

        // 只比代码行：本文件里"旧写法是 `PreferHdr = hdrOutput || …`"这类**说明注释**会被原文匹配命中，
        // 用整份文本做否定断言就会把已改好的代码判成未改（与 §27 记过的 File.Create 那次同型）。
        static string CodeOnly(string t) => string.Join("\n", t.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var mw = CodeOnly(File.ReadAllText(mwPath));
        int CountOf(string t, string n)
        {
            int c = 0;
            for (int i = t.IndexOf(n, StringComparison.Ordinal); i >= 0; i = t.IndexOf(n, i + 1, StringComparison.Ordinal)) c++;
            return c;
        }

        // ① 调用方不得再用输出意图去决定源精度
        Assert("捕获分派不再用 `PreferHdr = hdrOutput || …`（输出意图≠源精度）",
            !mw.Contains("PreferHdr = hdrOutput"), "应恒请求高精度，由服务侧按能力降级");
        Assert("三条捕获路径都显式 PreferHdr = true", CountOf(mw, "PreferHdr = true") >= 3,
            $"实际 {CountOf(mw, "PreferHdr = true")} 处");

        // ② 能力判定存在，且能力负缓存只由它写入（瞬时启动失败不得污染 —— 2026-09-15 的坑）
        var wgc = CodeOnly(File.ReadAllText(wgcPath));
        Assert("服务侧有 CanDeliverExtendedRange 能力判定",
            wgc.Contains("private static bool CanDeliverExtendedRange"));
        Assert("Float16 会话创建前先过能力判定", wgc.Contains("if (!CanDeliverExtendedRange(hmonitor))"));
        Assert("能力负缓存只在能力分支写入（全仓仅一处赋值）",
            CountOf(wgc, "s_hdrCapabilityCache[hmonitor] = false") == 1,
            $"实际 {CountOf(wgc, "s_hdrCapabilityCache[hmonitor] = false")} 处");

        // ③ 承重不变量：拿到 float 像素 ≡ 走高精度编码入口（MainWindow 的分派就靠这个字段）
        Assert("CaptureResult.IsHdr 的定义就是「有 Float16 像素」",
            File.ReadAllText(crPath).Contains("public bool IsHdr => HdrPixels is not null;"));
    }

    /// <summary>
    /// 逐屏高精度能力判据真值表（2026-09-19）。
    /// <para>为什么这条能存在：判据本体已从 App 层搬进 Core 的纯函数
    /// <c>DisplayEnumerator.CanDeliverExtendedRange</c>（单屏路径与多屏拼接路径【共用同一个】），
    /// 于是 Test 工程可以直接调它跑真值表 —— 比源码文本匹配硬得多：判据顺序写反、把兜底写成优先，
    /// 这条立刻红，不需要等到真机接一块 8-bit 副屏。</para>
    /// </summary>
    /// <para>2026-09-22 重写：判据从"能不能"（bool，含 advancedColorSupported(b0)）改成
    /// 三档分类（Unknown/NarrowSdr/WideGamutSdr/Hdr），逐屏证据换成 DWM 实际合成模式、
    /// 用户是否启用广色域、面板位深。b0 是<b>系统</b>能力，纯 sRGB 屏也报 1 —— 本机双屏实测
    /// 就是它让 800x600@100% 的 SDR 副屏白建了 Float16 会话。真值表跟着换成新的杠杆。</para>
    /// </summary>
    static void Test_Display_PerMonitorCapabilityTruthTable()
    {
        const int ModeUnknown = TrueToneCap.Core.Capture.DisplayInfo.ActiveModeUnknown;
        static TrueToneCap.Core.Capture.DisplayInfo Disp(
                bool isHdr = false, bool supportsHdr = false, bool acAnswered = false,
                int acMode = ModeUnknown, bool wideOn = false, int acBits = 0,
                int bitsPerColor = 8, bool primary = false,
                bool edidKnown = false, bool edidWide = false)
            => new TrueToneCap.Core.Capture.DisplayInfo
            {
                IsHdr = isHdr, SupportsHdr = supportsHdr, IsPrimary = primary,
                AdvancedColorInfoAvailable = acAnswered, AdvancedColorActiveMode = acMode,
                WideColorEnabled = wideOn, AdvancedColorBitsPerChannel = acBits,
                BitsPerColor = bitsPerColor,
                NativeGamutKnown = edidKnown, NativeGamutWide = edidWide,
            };
        bool Can(TrueToneCap.Core.Capture.DisplayInfo? d, bool sysAcm = false)
            => DEnum.CanDeliverExtendedRange(d, sysAcm);
        DEnum.DisplayColorClass Cls(TrueToneCap.Core.Capture.DisplayInfo? d, bool sysAcm = false)
            => DEnum.ClassifyDisplayColor(d, sysAcm);
        Assert("枚举不到显示器 ⇒ 宽松放行（'查不到'永远推不出'不支持'）", Can(null));
        Assert("枚举不到 ⇒ 分类 Unknown（放行 Float16，但不授权高位深声明）",
            Cls(null) == DEnum.DisplayColorClass.Unknown && !DEnum.CanClaimHdr(DEnum.DisplayColorClass.Unknown)
            && !DEnum.CanClaimWideGamut(DEnum.DisplayColorClass.Unknown));

        Assert("屏处于 HDR 模式 ⇒ 放行且分类 Hdr", Can(Disp(isHdr: true))
            && Cls(Disp(isHdr: true)) == DEnum.DisplayColorClass.Hdr);

        // ★ 顺序回归防线：DXGI 的面板证据必须先于 DISPLAYCONFIG 答复判定。
        // 写反过的后果：一块真 10-bit 高亮面板碰上"驱动没答复 + 全局 ACM 关" ⇒ 被降成窄色域
        // ⇒ 静默失去浮点源。这条是 2026-09-22 收紧过程中被本表抓到的，不是假想。
        Assert("≥10-bit 高亮面板在'无逐屏答复 + ACM 关'下仍放行（面板证据优先于查询答复）",
            Cls(Disp(supportsHdr: true)) == DEnum.DisplayColorClass.WideGamutSdr && Can(Disp(supportsHdr: true)));
        Assert("BitsPerColor=10（DXGI 报的位深）⇒ 放行", Can(Disp(bitsPerColor: 10)));

        // ★ 本次收紧的正主：系统说"支持高级颜色"但这块屏此刻就是 8-bit SDR 合成
        Assert("★ b0 支持高级颜色 + 此刻 SDR 合成(mode=0) + 未开广色域 + 8-bit ⇒ 判不支持",
            !Can(Disp(acAnswered: true, acMode: 0)),
            "旧判据在这一格放行 ⇒ 本机 800x600@100% SDR 副屏白建 Float16 会话");
        Assert("★ 上述那格分类必须是 NarrowSdr（不是 Unknown）",
            Cls(Disp(acAnswered: true, acMode: 0)) == DEnum.DisplayColorClass.NarrowSdr);

        Assert("逐屏答复=DWM 正在按 WCG 合成(mode=1) ⇒ 广色域 SDR",
            Cls(Disp(acAnswered: true, acMode: 1)) == DEnum.DisplayColorClass.WideGamutSdr && Can(Disp(acAnswered: true, acMode: 1)));
        Assert("逐屏答复=DWM 正在按 HDR 合成(mode=2) ⇒ Hdr",
            Cls(Disp(acAnswered: true, acMode: 2)) == DEnum.DisplayColorClass.Hdr);
        Assert("逐屏答复=每通道 ≥10bit ⇒ 放行", Can(Disp(acAnswered: true, acMode: 0, acBits: 10)));
        Assert("逐屏答复=用户启用了广色域 ⇒ 放行", Can(Disp(acAnswered: true, acMode: 0, wideOn: true)));

        // 全局 ACM 是注册表算出来的【单值】，一台 HDR 主屏就会把它点亮 ⇒ 逐屏有答复时它不许参与
        Assert("★ 逐屏明确 SDR + 全局 ACM 开着 ⇒ 判不支持（逐屏答复压过系统级单值）",
            !Can(Disp(acAnswered: true, acMode: 0), sysAcm: true));
        Assert("逐屏【无答复】+ 全局 ACM 开着 ⇒ 放行（退回旧行为，不误伤）",
            Can(Disp(), sysAcm: true) && Cls(Disp(), sysAcm: true) == DEnum.DisplayColorClass.Unknown);
        Assert("逐屏【无答复】+ 全局 ACM 关着 ⇒ 不支持", !Can(Disp(), sysAcm: false));

        // 两个方向的不对称是新判据的核心，得有一条断言钉住，别被"看起来矛盾"改回同向
        Assert("Unknown 只放宽源精度、不放宽声明（两者故意不同向）",
            Can(Disp(), sysAcm: true) && !DEnum.CanClaimWideGamut(Cls(Disp(), sysAcm: true)));
        Assert("NarrowSdr 两个声明判据都关", !DEnum.CanClaimWideGamut(DEnum.DisplayColorClass.NarrowSdr)
            && !DEnum.CanClaimHdr(DEnum.DisplayColorClass.NarrowSdr));
        Assert("广色域 SDR 可声明广色域、但不可声明 HDR",
            DEnum.CanClaimWideGamut(DEnum.DisplayColorClass.WideGamutSdr) && !DEnum.CanClaimHdr(DEnum.DisplayColorClass.WideGamutSdr));

        // ═══ 2026-09-22 第二轮收紧：ACM 标志 ≠ 面板广色域，必须有 EDID 硬件原色确证 ═══
        // 本机实测把这一格逼出来的：AUO75AC（ΔG=0.0246，普通 100% sRGB 笔电屏）开 ACM 后
        // mode=1(WCG) + 广色域已启用=是 全齐，上一版判据据此判 Wide 并允许声明 BT.2020。
        var acmOnNarrowPanel = Disp(acAnswered: true, acMode: 1, wideOn: true, edidKnown: true, edidWide: false);
        var acmOnWidePanel   = Disp(acAnswered: true, acMode: 1, wideOn: true, edidKnown: true, edidWide: true);
        var acmOnNoEdid      = Disp(acAnswered: true, acMode: 1, wideOn: true);
        Assert("★ ACM 全开（mode=1 + 广色域启用）但 EDID 确证不宽 ⇒ 判窄色域（源也不再取浮点）",
            Cls(acmOnNarrowPanel) == DEnum.DisplayColorClass.NarrowSdr && !Can(acmOnNarrowPanel));
        Assert("★ ACM 全开且 EDID 确证宽 ⇒ 广色域 SDR，放行浮点源",
            Cls(acmOnWidePanel) == DEnum.DisplayColorClass.WideGamutSdr && Can(acmOnWidePanel));
        Assert("EDID 读不到 + ACM 开 ⇒ 维持广色域档（拿不准不降级，与 Unknown 同一取向）",
            Cls(acmOnNoEdid) == DEnum.DisplayColorClass.WideGamutSdr && Can(acmOnNoEdid));
        Assert("10-bit 高亮面板不受 EDID 影响（面板证据排在 EDID 确证之前，不许反过来）",
            Cls(Disp(supportsHdr: true, edidKnown: true, edidWide: false)) == DEnum.DisplayColorClass.WideGamutSdr);
        Assert("确证不宽的屏不够格当拼接 HDR 源（本机 AUO 若被选中就是回归）",
            DEnum.SelectHdrSourceDisplay(new[] { acmOnNarrowPanel }, true) is null);

        // ★ 面板自证的位置：必须对【两个】分支都生效，不只对"有逐屏答复"那一支。
        // Win10 走的是 type9 答复 ⇒ ActiveMode 未知、看起来"没答复"，EDID 规则若写在
        // AdvancedColorInfoAvailable 分支里面就被绕开了，这块确证的 sRGB 屏照样判 Unknown，
        // 而 Unknown 在源精度侧是放行 ⇒ 换台机器就复发"白建 Float16 会话"。这条断言只对位置敏感。
        var noAnswerNarrowEdid = Disp(edidKnown: true, edidWide: false);
        Assert("★ 逐屏【无答复】+ 全局 ACM 开 + EDID 确证不宽 ⇒ NarrowSdr（不是 Unknown）",
            Cls(noAnswerNarrowEdid, true) == DEnum.DisplayColorClass.NarrowSdr && !Can(noAnswerNarrowEdid, true));
        var noAnswerWideEdid = Disp(edidKnown: true, edidWide: true);
        Assert("逐屏【无答复】+ ACM 开 + EDID 确证宽 ⇒ 仍是 Unknown（面板宽不等于此刻在用）",
            Cls(noAnswerWideEdid, true) == DEnum.DisplayColorClass.Unknown);
        Assert("逐屏【无答复】+ ACM 关 + EDID 确证宽 ⇒ 仍不支持（DWM 没在按扩展范围合成，宽面板也是 8-bit 内容）",
            !Can(noAnswerWideEdid, false));
        // 唯一让道的情况：DWM 此刻正在按 PQ 合成 —— 那一帧是 HDR 母版，降成窄色域会错在声明侧
        Assert("mode=2(HDR 合成) + EDID 确证不宽 ⇒ 仍判 Hdr（合成模式压过面板原色）",
            Cls(Disp(acAnswered: true, acMode: 2, edidKnown: true, edidWide: false)) == DEnum.DisplayColorClass.Hdr);

        // ═══ 拼接用的 HDR 源选择（2026-09-22 一并收紧，规则本体同样在 Core）═══
        var hdrP = Disp(isHdr: true, primary: true);
        var hdrS = Disp(isHdr: true);
        var wide = Disp(acAnswered: true, acMode: 1);              // 逐屏答复=DWM 正在按 WCG 合成
        var wideP = Disp(acAnswered: true, acMode: 1, primary: true);
        var narrow = Disp(acAnswered: true, acMode: 0);
        var unknownOnly = Disp();                                  // 无答复；配合 sysAcm=true 才是 Unknown

        Assert("只有 Unknown 档（无答复 + 全局 ACM 开）⇒ 选不出源",
            DEnum.SelectHdrSourceDisplay(new[] { unknownOnly }, true) is null,
            "旧写法用 CanDeliverExtendedRange，它对 Unknown 放行 ⇒ 一块未被证实的屏决定整图亮度基准");
        Assert("全是窄色域 ⇒ 选不出源", DEnum.SelectHdrSourceDisplay(new[] { narrow }, false) is null);
        // ★ 档优先于主次：这是"广色域 SDR 主屏 + 真 HDR 副屏"这种常见接法上唯一正确的取法
        Assert("广色域主屏 + HDR 副屏 ⇒ 选 HDR 那块（先比档，再讲主次）",
            ReferenceEquals(DEnum.SelectHdrSourceDisplay(new[] { wideP, hdrS }, false), hdrS));
        Assert("同档内主屏优先：HDR 主 + HDR 副 ⇒ 选主屏",
            ReferenceEquals(DEnum.SelectHdrSourceDisplay(new[] { hdrP, hdrS }, false), hdrP));
        Assert("同档内主屏优先：广色域主 + 广色域副 ⇒ 选主屏",
            ReferenceEquals(DEnum.SelectHdrSourceDisplay(new[] { wideP, wide }, false), wideP));
        Assert("没有主屏标记时退选第一个够档的",
            ReferenceEquals(DEnum.SelectHdrSourceDisplay(new[] { narrow, wide }, false), wide));
        // 不变式：源选择必须是单屏判据的【子集】，否则会出现"多屏选了单屏不肯建 float 的屏"
        var picked = DEnum.SelectHdrSourceDisplay(new[] { wideP, hdrS }, true);
        Assert("被选中的源必然满足单屏判据（两条路径同向，不许反向多出）",
            picked is not null && DEnum.CanDeliverExtendedRange(picked, true));

        // ═══ 可诊断性本身的哨兵：证据行一旦被简化回"一个词"，现场就又查不动了 ═══
        var evNull = DEnum.DescribeColorEvidence(null, false);
        var evNarrow = DEnum.DescribeColorEvidence(Disp(acAnswered: true, acMode: 0), false);
        Assert("证据行同时给出 DISPLAYCONFIG 与 DXGI 两路输入（不许只报分类词）",
            evNarrow.Contains("DWM合成模式=") && evNarrow.Contains("支持高级颜色=") &&
            evNarrow.Contains("IsHdr=") && evNarrow.Contains("每通道="));
        Assert("分类词也出现在证据行里（人读的是行，机器比的是词）",
            evNarrow.Contains("分类=NarrowSdr") && evNull.Contains("分类=Unknown"));
        var wgcSrc = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "WgcCaptureService.cs"))!);
        var pipeSrc = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"))!);
        var stSrc = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SelfTest.cs"));
        Assert("拒绝建 Float16 时必须把证据行打进日志（静默降级的反面）",
            wgcSrc.Contains("不建 Float16 会话") && wgcSrc.Contains("DescribeMonitorColor"));
        // ★ 2026-09-22 审查轮改指向（不是放宽）：钳制现在住在 Core 的 ColorDecision 里，
        //   管线只负责把"发生过收口"连同证据行一起打进日志。判据强度不减：
        //   两条收口都必须存在、都必须带证据行、且管线自己不许再拼一份口径。
        Assert("钳制的两条收口都真的打进日志，且各带一次证据行",
            pipeSrc.Contains("不给 HDR 声称") && pipeSrc.Contains("收口为 sRGB")
            && System.Text.RegularExpressions.Regex.Matches(pipeSrc, @"\{decision\.Evidence\}").Count == 2);
        Assert("管线不再自己渲染证据串（有第二份口径 = §41.6 那个形状复发）",
            !pipeSrc.Contains("\"分类="));
        Assert("渲染只有 Core 一处定义，三侧共用（经决定转手也算共用，但必须真转手）",
            wgcSrc.Contains("DescribeColorEvidence") && pipeSrc.Contains("decision.Evidence")
            && stSrc is not null && (File.ReadAllText(stSrc).Contains("DescribeColorEvidence")
                                     || File.ReadAllText(stSrc).Contains("ColorDecision.Build")));
        if (stSrc is not null)
        {
            var st = File.ReadAllText(stSrc);
            Assert("--display-audit 有入口且被接线（否则只是个死方法）",
                st.Contains("--display-audit") && st.Contains("RunDisplayAudit(auditPath, emitSamples:"));
            // 审查轮的教训：决定层对了推不出产物对了 ⇒ 末端落盘+读回这条腿不许掉
            Assert("--display-audit-emit 存在，且样本走产品那套三件套并从盘上读回",
                st.Contains("--display-audit-emit")
                && st.Contains("CapturePipelineService.PreparePixelsWithIcc")
                && st.Contains("ReadPngColorChunks(path)"));
        }
    }

    /// <summary>
    /// EDID 读取必须是【句柄驱动】的（2026-09-22 双屏实测抓到的第三个缺陷，与三档判据无关的前置缺陷）。
    /// <para>旧实现收了 <c>monitorHandle</c> 却全程没用它：遍历 <c>HKLM\...\Enum\DISPLAY</c> 返回
    /// <b>第一个</b>头部合法的 EDID。本机该树下有 3 个实例（AUO75AC 两个、PHLC38F 一个），
    /// 于是两块屏都读到 AUO75AC 的原色 —— 主屏（真 P3，ΔG=0.0963）被读成 ΔG=0.0246。
    /// 后果不止三档判据：<c>GetDisplayNativeGamutTag</c> 及一切依赖"原生色域"的功能，
    /// 在多显示器机器上一直吃的是别人家的 EDID。</para>
    /// <para>这一条与 <see cref="Test_Display_PerMonitorCapabilityTruthTable"/> 的分工：那张表测
    /// "拿到正确原色之后判得对不对"，这里测"原色是不是这块屏自己的"。后者单靠真值表永远测不出，
    /// 因为喂给它的 DisplayInfo 是我手写的。</para>
    /// </summary>
    static void Test_Edid_ReadIsHandleDriven_NotFirstMatch()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "ColorManagement", "ColorProfileProvider.cs"));
        if (p is null) { Skip("EDID 逐屏读取", "未找到 ColorProfileProvider.cs"); return; }
        var code = string.Join("\n", File.ReadAllLines(p).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        // 行为判据：一个不可能对应任何显示器的句柄，必须回答"不知道"，而不是"随便拿一块屏的数据"。
        var none = TrueToneCap.Core.ColorManagement.ColorProfileProvider.ProbeNativeWideGamut(nint.Zero);
        Assert("句柄无效 ⇒ Known=false（读不到 ≠ 确证不宽，更不许退化成别屏的 EDID）", !none.Known);
        Assert("句柄无效 ⇒ Wide=false 且给出可读原因", !none.Wide && none.Detail.Length > 0);
        Assert("句柄无效时的原因串是『无 EDID』而不是编造的坐标", none.Detail == "无 EDID");

        // 形态判据：钉住缺陷本体（忽略入参 + 全树扫描 + 取第一个合法值）。
        Assert("ReadEdidFromRegistry 把入参句柄解析成注册表实例路径",
            code.Contains("ReadEdidFromRegistry(nint monitorHandle)") &&
            code.Contains("GetMonitorRegistryInstancePath(monitorHandle)"));
        Assert("注册表键由解析出的实例路径拼出（不是硬编码遍历 DISPLAY 树）",
            code.Contains(@"Enum\{instancePath}\Device Parameters"));
        Assert("不再存在『遍历 DISPLAY 子键取第一个合法 EDID』的写法",
            !code.Contains("foreach (var subKeyName in displayKey.GetSubKeyNames())"));
        Assert("HMONITOR→设备接口路径这一步失败时返回 null（拿不到，而非拿错）",
            code.Contains("if (!EnumDisplayDevicesW(deviceName, 0u, ref dd, 1u)) return null;"));

        // ═══ 闸门与标签【同源】：直接拿真机量到的原色坐标喂判据本身 ═══
        // 为什么这一段必须存在：2026-09-22 审查轮做突变实验时，"把闸门整个去掉"跑出来是
        // 673/0 全绿 —— 因为闸门原先埋在 ProbeNativeWideGamut(nint) 里，需要真实 HMONITOR，
        // 单测一格都测不到，而真值表里那份 DisplayInfo 的原生标签是我手写的。
        // 判据不可测 = 判据不存在。现在闸门单独成函数，下面这些行才拿得动它。
        var gate = new System.Func<double, double, double, double, string>(
            TrueToneCap.Core.ColorManagement.ColorProfileProvider.NativeGamutTagFor);
        Assert("★ 本机副屏实测原色（AUO75AC，ΔG=0.0246）⇒ 标签只能是 sRGB",
            gate(0.6602, 0.3311, 0.2949, 0.6240) == "sRGB");
        Assert("★ 本机主屏实测原色（PHLC38F，ΔG=0.0963）⇒ 标签确证为宽",
            gate(0.6826, 0.3193, 0.2598, 0.6875) != "sRGB");
        // 这一格是旧宽松阈值（rx>0.65 且 gx≤0.30）的照妖镜：红点偏宽、绿点【一点没动】
        var onlyRedStretch = gate(0.680, 0.320, 0.3000, 0.6000);
        Assert("★ 只把红点做宽、绿点与 709 完全重合的面板 ⇒ 判窄（旧判据在这里误报 P3）",
            onlyRedStretch == "sRGB");
        Assert("标准 709/sRGB 原色的 ΔG 恒等于 0（门限的零点，不是约等于）",
            TrueToneCap.Core.ColorManagement.ColorProfileProvider
                .GreenStretchFromGreen(0.3000, 0.6000) < 1e-12);
        // 参考值复算：审查轮纠正过注释里手算错的 0.0992 / ≈0.099 / ≈0.129（交接 §40.5-2）
        bool Near(double a, double b) => System.Math.Abs(a - b) < 0.0005;
        Assert("DCI-P3 绿点的 ΔG 实测复算 = 0.0966（不是注释里曾写过的 0.099）",
            Near(TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.265, 0.690), 0.0966));
        Assert("AdobeRGB 绿点的 ΔG 实测复算 = 0.1421（不是注释里曾写过的 0.129）",
            Near(TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.210, 0.710), 0.1421));
        Assert("BT.2020 绿点的 ΔG 实测复算 = 0.2360",
            Near(TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.170, 0.797), 0.2360));
        Assert("门限两侧留量：确证窄(0.0246) 与确证宽(0.0963) 都在 0.05 的正确一侧",
            TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.2949, 0.6240) < ColorProfileProvider.GreenStretchForWide
            && TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.2598, 0.6875) > ColorProfileProvider.GreenStretchForWide);
    }

    /// <summary>
    /// <b>色彩决定的单一事实源真值表</b>（2026-09-22 审查轮，交接 §41）。
    /// <para>为什么这张表必须存在：在它之前，"这次输出用什么色域"被算两遍 —— 声明侧走三档判据
    /// （EDID ΔG 闸门），像素侧走 <c>GetDisplayNativeGamutTag</c> 的宽松阈值（<c>rx&gt;0.65 且 gx≤0.30</c>）
    /// 并把那个结果当**源矩阵**用；两遍各自解析用户下拉、各自问光标屏。本机那块 ΔG=0.0246 的普通
    /// sRGB 副屏同时命中"判窄色域"和"按 P3 搬色"，于是同一枚文件里像素与标签互相矛盾。
    /// 现在两遍合一成 Core 的 <c>ColorDecision</c>，所以这张表测的就是**产物本身**，不再是局部函数。</para>
    /// <para>它同时补上 §41.2 那个洞：旧钳制显式放过 "System" ⇒ 默认档"跟随显示器"完全不经钳制。</para>
    /// </summary>
    static void Test_ColorDecision_SingleSourceOfTruth()
    {
        const int ModeUnknown = TrueToneCap.Core.Capture.DisplayInfo.ActiveModeUnknown;
        // tag = EDID 确证、且【过了 ΔG 闸门之后】的原生标签：闸门不宽 ⇒ 这里只可能是 "sRGB"。
        static TrueToneCap.Core.Capture.DisplayInfo D(
            bool supportsHdr = false, bool acAnswered = false, int acMode = ModeUnknown,
            bool wideOn = false, int bits = 8, bool edidKnown = false, string? edidTag = null)
            => new TrueToneCap.Core.Capture.DisplayInfo
            {
                SupportsHdr = supportsHdr, BitsPerColor = bits,
                AdvancedColorInfoAvailable = acAnswered, AdvancedColorActiveMode = acMode,
                WideColorEnabled = wideOn,
                NativeGamutKnown = edidKnown,
                NativeGamutWide = edidTag is not null && edidTag != "sRGB",
                NativeGamutTag = edidTag ?? "sRGB",
            };
        // 本机副屏的真实形状：ACM 全开（mode=1 + 广色域已启用）+ EDID 确证不宽
        var narrow = D(acAnswered: true, acMode: 1, wideOn: true, edidKnown: true);
        // 本机主屏 P3 部分（非 HDR 态）：确证宽 + 正在按 WCG 合成
        var wideP3 = new TrueToneCap.Core.Capture.DisplayInfo
        {
            SupportsHdr = true, BitsPerColor = 10,
            AdvancedColorInfoAvailable = true, AdvancedColorActiveMode = 1, WideColorEnabled = true,
            NativeGamutKnown = true, NativeGamutWide = true, NativeGamutTag = "DisplayP3",
        };
        // HDR 态的确证宽屏
        var hdr = new TrueToneCap.Core.Capture.DisplayInfo
        {
            IsHdr = true, SupportsHdr = true, BitsPerColor = 10,
            AdvancedColorInfoAvailable = true, AdvancedColorActiveMode = 2, WideColorEnabled = true,
            NativeGamutKnown = true, NativeGamutWide = true, NativeGamutTag = "DisplayP3",
        };
        // 扩展合成开着、EDID 读不到
        var noEdid = D(acAnswered: true, acMode: 1, wideOn: true);

        bool Acm = true;

        // ═══ ★ 本表存在的理由：源与目标是【一次】决定的两个输出，永不分叉 ═══
        // 旧形状（§41.3 的错位 A）：声明被收口成 sRGB，像素却仍按 ui.ColorSpaceTag=BT2020 搬色。
        var n2020 = CDec.Build(narrow, "BT2020", false, Acm);
        Assert("★ 确证窄屏 + 显式 BT.2020 ⇒ 目标被收口为 sRGB（GamutClamped 留痕）",
            n2020.TargetTag == "sRGB" && n2020.GamutClamped);
        Assert("★ 同一份决定里源也是 sRGB ⇒ 像素路不会再按 P3 解矩阵（错位 A/B 的正面形状）",
            n2020.SourceTag == "sRGB");
        Assert("被收口时证据行必须可用（静默降级是另一种错法）",
            n2020.Evidence.Contains("分类=NarrowSdr") && n2020.Evidence.Contains("EDID原生"));

        // ═══ §41.2 那个洞：默认档"跟随显示器"过去【显式放过】钳制 ═══
        // ⚠ 这一格真正守住它的是"解析阶段就用过了闸门的原生标签"，不是下面那条钳制。
        //   两轮突变实验把分工量出来了（写在这儿免得下轮再猜）：
        //   · 把钳制条件改回"放过 System" ⇒ 681 全绿 ⇒ 这一格不归钳制管；
        //   · 把钳制整个去掉 ⇒ 3 红（本组那行 BT.2020 收口 + HDR 那格的双收口 + 承重不变式）；
        //   · 把闸门去掉（标签退回宽松阈值）⇒ 2 红（AUO 实测原色那行 + "只宽红点"的照妖镜行）。
        //   ⇒ 钳制管"用户显式要广色域"，闸门管"跟随显示器自动要广色域"，两条各有一格不可红。
        var nSys = CDec.Build(narrow, "System", false, Acm);
        Assert("★ 确证窄屏 + 跟随显示器 ⇒ 目标 sRGB（守它的是同源标签；旧实现这里交给宽松判据 ⇒ BT2020）",
            nSys.TargetTag == "sRGB");
        Assert("null / 空串与 System 同义（不许因为没传就落进未钳制分支）",
            CDec.Build(narrow, null, false, Acm).TargetTag == "sRGB" &&
            CDec.Build(narrow, "", false, Acm).TargetTag == "sRGB");

        // ═══ 确证宽屏要照常拿到广色域，并把源如实记成 P3（收窄不能把合法能力一起砍掉）═══
        var wSys = CDec.Build(wideP3, "System", false, Acm);
        Assert("确证 P3 屏 + 跟随显示器 ⇒ 目标 BT.2020、源 DisplayP3 ⇒ 真的搬（不是只改标签）",
            wSys.TargetTag == "BT2020" && wSys.SourceTag == "DisplayP3" && !wSys.GamutClamped);
        var wSrgb = CDec.Build(wideP3, "sRGB", false, Acm);
        Assert("确证 P3 屏 + 显式 sRGB ⇒ 源仍如实是 P3，目标 sRGB ⇒ 搬回来",
            wSrgb.SourceTag == "DisplayP3" && wSrgb.TargetTag == "sRGB");

        // ═══ EDID 读不到：不自动声称广色域，但用户显式要时仍搬（且搬的源如实是 sRGB）═══
        var eSys = CDec.Build(noEdid, "System", false, Acm);
        Assert("EDID 读不到 + 跟随显示器 ⇒ 不自动给广色域声明（源标签也未确证）",
            eSys.TargetTag == "sRGB" && eSys.SourceTag == "sRGB");
        var e2020 = CDec.Build(noEdid, "BT2020", false, Acm);
        Assert("EDID 读不到 + 显式 BT.2020 ⇒ 按 sRGB→BT.2020 真搬，声明与数学仍然一致",
            e2020.TargetTag == "BT2020" && e2020.SourceTag == "sRGB" && !e2020.GamutClamped);

        // ═══ HDR 档与"两个方向故意不同向"要活过这次合并 ═══
        var h = CDec.Build(hdr, "System", true, Acm);
        Assert("HDR 屏 + HDR 开关 ⇒ 目标 BT.2020 且允许写 PQ",
            h.TargetTag == "BT2020" && h.HdrOutput && h.ScreenClass == DEnum.DisplayColorClass.Hdr);
        Assert("HDR 屏的源如实是面板原生 P3（HDR 不改变【原生是什么】这个问题）",
            h.SourceTag == "DisplayP3");
        var hn = CDec.Build(narrow, "BT2020", true, Acm);
        Assert("窄屏 + HDR 开关 ⇒ 两项收口：不写 PQ、且广色域声明退回 sRGB",
            !hn.HdrOutput && hn.HdrClamped && hn.TargetTag == "sRGB" && hn.GamutClamped);
        var unknown2020 = CDec.Build(null, "BT2020", false, false);
        Assert("Unknown（无逐屏证据）⇒ 【不】钳制用户选择，维持 BT.2020（§39 的方向性活下来了）",
            unknown2020.ScreenClass == DEnum.DisplayColorClass.Unknown &&
            unknown2020.TargetTag == "BT2020" && !unknown2020.GamutClamped);
        Assert("Unknown 仍放行 Float16 源（源精度侧的宽松与声明侧的不授权是两件事）",
            unknown2020.ExtendedRangeAuthorized);

        // ═══ 承重不变式：对每一格都成立，防"以后再加一档时漏一格" ═══
        var allRows = new[] { n2020, nSys, wSys, wSrgb, eSys, e2020, h, hn, unknown2020 };
        // (a) 凡声明广色域 ⇒ 该档必须授权，或该档是 Unknown（Unknown 不施加钳制是既定取向）
        var badClaim = allRows.FirstOrDefault(r => r.TargetTag != "sRGB"
            && r.ScreenClass != DEnum.DisplayColorClass.Unknown
            && !DEnum.CanClaimWideGamut(r.ScreenClass));
        Assert("不变式：没有一行在【档位不授权】时声明了广色域", badClaim is null,
            badClaim is null ? "" : badClaim.Describe());
        // (b) 凡源标签非 sRGB ⇒ 必须出自 EDID 确证过的宽面板（不许凭开关猜源）
        var badSrc = allRows.FirstOrDefault(r => r.SourceTag != "sRGB"
            && !(r.Source?.NativeGamutKnown == true && r.Source.NativeGamutWide));
        Assert("不变式：没有一行在【EDID 未确证】时按非 sRGB 的源做矩阵", badSrc is null,
            badSrc is null ? "" : badSrc.Describe());
        // (c) 源与目标同源：同一实例里两者由一次决定给出（防"只改一侧"的未来回归）
        Assert("不变式：目标标签一定是收口后的可选面（sRGB / BT2020），不会漏出 P3 当输出",
            allRows.All(r => r.TargetTag is "sRGB" or "BT2020"));

        // ═══ NeedsMapping：开关删掉之后，"这次到底要不要跑矩阵"必须由决定自己回答 ═══
        // 它有两个消费方：CPU 矩阵（省一次白烧）与 8-bit GPU 纹理直通（正确性：直通会绕开矩阵）。
        Assert("sRGB 屏上目标被收口成 sRGB ⇒ 不需要搬色（直通仍可用）", !n2020.NeedsMapping);
        Assert("确证 P3 屏自动档 sRGB→BT.2020 不够，源如实是 P3 ⇒ 需要 P3→BT.2020 这一次搬色",
            wSys.NeedsMapping && wSys.SourceTag == "DisplayP3" && wSys.TargetTag == "BT2020");
        Assert("确证 P3 屏 + 显式 sRGB ⇒ 需要反向搬（P3→sRGB），不许因为『目标是个普通色域』就跳过",
            wSrgb.NeedsMapping);
        Assert("EDID 读不到 + 跟随显示器 ⇒ 源与目标都是 sRGB ⇒ 不搬色也不谎称搬了", !eSys.NeedsMapping);
        Assert("EDID 读不到 + 显式 BT.2020 ⇒ 按 sRGB 当真源去搬 ⇒ NeedsMapping 为真（声明与数学同源）",
            e2020.NeedsMapping);

        // ═══ 显式 vs 自动：GainMap 的两模式只认用户的手 ═══
        // 自动档在 P3 屏上也会把 TargetTag 解析成 BT2020 ⇒ "看标签"和"看用户"是两件事。
        Assert("显式 BT2020 ⇒ ExplicitWideTarget 真（GainMap 才允许整张增益图升级）",
            n2020.ExplicitWideTarget && n2020.RequestedTag == "BT2020");
        Assert("★ 跟随显示器 + 确证 P3 屏 ⇒ 目标虽是 BT2020，ExplicitWideTarget 必须为假",
            wSys.TargetTag == "BT2020" && !wSys.ExplicitWideTarget);
        Assert("null / 空 / System / sRGB 四种都算非显式",
            !CDec.Build(wideP3, null, false, Acm).ExplicitWideTarget
            && !CDec.Build(wideP3, "", false, Acm).ExplicitWideTarget
            && !CDec.Build(wideP3, "System", false, Acm).ExplicitWideTarget
            && !CDec.Build(wideP3, "sRGB", false, Acm).ExplicitWideTarget);
        Assert("旧配置里的显式广色域标签（DisplayP3/AdobeRGB，收口后同为 BT2020）算显式",
            CDec.Build(wideP3, "DisplayP3", false, Acm).ExplicitWideTarget
            && CDec.Build(wideP3, "AdobeRGB", false, Acm).ExplicitWideTarget);

        // ═══ 形态哨兵：两侧都不许再各自解析下拉（否则这张表护不住实际行为）═══
        var cps = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"))!);
        var mw = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"))!);
        Assert("管线不再自己解析用户下拉（解析只在 ColorDecision 里）",
            !cps.Contains("ResolveColorSpaceTag(") || cps.Contains("// 不许再调 ResolveColorSpaceTag"));
        Assert("像素路的入口签名收的是决定，不再带布尔开关（开关 = 又一次『像素与声明不同源』的入口）",
            cps.Contains("byte[] bgra, int w, int h, ColorDecision decision"));
        Assert("编码入口把【同一个】决定同时交给映射与声明两侧",
            cps.Contains("PreparePixelsWithIcc(bgra, w, h, decision)"));
        // ═══ 2026-09-22 用户拍板：色彩管理不可关，默认就映射进标准色域 ═══
        // 开关删掉的理由不是"少一个选项"，而是它同时是两类错的入口：
        //   ①关着时像素不动却按请求声明广色域（§41.4 的 M1）；②它顺带还是 8-bit GPU 纹理直通的闸门，
        //   一个 UI 布尔在决定"要不要绕开 CPU 矩阵"这种正确性问题。
        var xaml = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"))!);
        Assert("设置项与界面控件里都没有『烘焙开关』残留",
            !mw.Contains("IccBakeEnabled") && !xaml.Contains("IccBakeSwitch")
            && !File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"))!)
                .Contains("public bool IccBakeEnabled"));
        Assert("GPU 纹理直通的闸门改由决定判（要搬色就不许绕开 CPU 矩阵）",
            cps.Contains("if (gpuTexture is not null && decision.NeedsMapping)"));
        Assert("能力检测里那个只为开关服务的 IccBakeAvailable 没被补回来",
            !File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapabilityService.cs"))!)
                .Contains("public bool IccBakeAvailable"));
        Assert("有捕获结果的入口按被截屏决定，不再拿光标屏顶替",
            mw.Contains("ColorDecision.Build(captureResult.SourceDisplay"));
        // ═══ §48 #21：HDR 开着却出 8-bit PNG ⇒ 整次被退回 SDR，默认设置下"以为在出 HDR"═══
        // 协商必须①在 HdrOutput 定稿之后（早于 sRGB→SDR 那段就会给已降级的场景升位深），
        // ②真的写回 settings（只改局部 bitDepth 等于没改，编码器看到的还是 8）。
        Assert("HDR + PNG 的位深协商存在且写回 settings.OutputBitDepth",
            cps.Contains("format == OutputFormat.PNG && bitDepth < 10")
            && cps.Contains("settings.OutputBitDepth = 10"));
        Assert("位深协商排在 HdrOutput 定稿之后（否则会给已降级成 SDR 的场景白升位深）",
            cps.IndexOf("settings.HdrOutput = hdrOutput;") >= 0
            && cps.IndexOf("settings.HdrOutput = hdrOutput;")
               < cps.IndexOf("bitDepth < 10"));
        Assert("协商要像 AVIF 那样显式告知，不许静默改参数",
            cps.Contains("PNG HDR 位深协商") && cps.Contains("PNG HDR 位深提示"));
        // ★ 这条是本轮自己差点引进的行为变化：合并成一份决定时，把旧实现"ACM 开着 + EDID 探测失败
        //   + 目标本来就是 sRGB ⇒ 原样返回、不碰显示器 ICC"那个默认值顺手抹掉了，于是那种屏会掉进
        //   传统烘焙、拿用户校准 ICC 多做一次矩阵。判据是"源标签缺省时仍按 isSRgbTarget 兜一次"。
        Assert("EDID 读不到时源标签的缺省语义没被合并掉（否则 sRGB 目标会多跑一次显示器 ICC 烘焙）",
            System.Text.RegularExpressions.Regex.IsMatch(cps, @"isSRgbTarget\s*\?\s*""sRGB""\s*:\s*null"));
        Assert("光标屏顶替只允许出现在确实无源屏信息的入口，且集中成一个辅助函数",
            System.Text.RegularExpressions.Regex.Matches(mw, "GetMonitorUnderCursor\\(\\)").Count
            <= System.Text.RegularExpressions.Regex.Matches(mw, "DecisionForCursorScreen").Count + 3);
        // ═══ GainMap 两模式（§45）：模式判定只有一处，两条路共用 ═══
        var gmSrc = File.ReadAllText(FindRepoFile(Path.Combine(
            "src", "TrueToneCap.Core", "Encoding", "JpegGainMapEncoder.cs"))!);
        Assert("GainMap 的色彩模式判定收敛在一个函数，两条路都调它",
            System.Text.RegularExpressions.Regex.Matches(gmSrc, @"PrepareGainMapColorSpace\(frame\.Pixels").Count == 2);
        Assert("带增益图的主路要求显式；无增益图的 base-only 直接跟目标（requireExplicit:false）",
            gmSrc.Contains("requireExplicit: false")
            && gmSrc.Contains("bool wideTarget = settings.ColorSpaceTag == \"BT2020\";")
            && gmSrc.Contains("if (wideTarget && (!requireExplicit || settings.ExplicitWideTarget))"));
        Assert("拿不到可嵌的 2020 ICC 时整条退回默认模式，不许只搬像素不声明",
            gmSrc.Contains("IsEmbeddableIcc(icc2020)"));
        Assert("编码器侧不许自己解析下拉：显式与否只能来自 ColorDecision 转写的 settings 标记",
            !gmSrc.Contains("ResolveColorSpaceTag") && gmSrc.Contains("settings.ExplicitWideTarget"));
        // ═══ 2026-09-23：UI 侧不许再把色域下拉锁走 ═══
        // GainMap 分支曾在这里 ColorCbo.IsEnabled=false 并把格子强推到 BT.2020 —— 那是『GainMap
        // 无视标签』年代的遗留。标签一旦被真实读取（上面 requireExplicit 那组判据），这个锁定就等于
        // 替所有用户按下『整条增益图走 2020』，两档设计里的 sRGB 底图根本选不到。
        Assert("GainMap 分支不再禁用/强推色域下拉（锁住 = 用户拿不到 sRGB 底图那一档）",
            !mw.Contains("ColorCbo.IsEnabled = false") && !mw.Contains("bt2020Idx"));
        Assert("HDR 开关仍锁开启（Ultra HDR 没有『无增益图』这种产物），但只锁这一个",
            mw.Contains("HdrSwitch.IsEnabled = false;") && mw.Contains("HdrSwitch.IsOn = true;"));
        Assert("能力表里 GainMap 不再被标成不支持广色域（那条声明与两档实现相反）",
            !System.Text.RegularExpressions.Regex.IsMatch(
                mw, @"JPEG_GAINMAP\][\s\S]{0,120}SupportsWideGamut: false"));
    }


    /// <summary>
    /// <b>"HDR 开着"≠"HDR 直通"：五种交付形态 + 逐格式的声明通道</b>（2026-09-26 用户指令，交接 §53）。
    /// <para>为什么这张表必须存在：色域映射卡片原先只有一种画法 —— <c>HdrSwitch.IsOn</c> 为真就印
    /// "→ HDR 直通 (目标色域)"，而它的数据来源是自己上一行刚写下的那行中文文案（用 <c>Contains</c> 反推）。
    /// 产品里同一枚开关至少被三样东西收口：屏幕证据（<c>HdrClamped</c>）、目标是 sRGB（管线里的降级）、
    /// 格式没有 HDR 通道（<c>SupportsHdr</c> 为假）。三样都不在卡片上，于是"HDR 直通 (sRGB)"
    /// 这种自相矛盾的话能长期印在界面上。</para>
    /// <para>第三格（增益图 + sRGB 底图）测的是<b>产物缺陷</b>而不只是文案：那条降级原先无条件生效，
    /// <c>HdrOutput=false</c> 会让 <c>JpegGainMapEncoder.EncodeAsync</c> 命中"降级为普通 JPEG、无增益图"
    /// 那条路 —— 用户开着增益图开关、选 Ultra HDR 约定的 sRGB 底图，拿到的是一张普通 JPEG。</para>
    /// </summary>
    static void Test_HdrDelivery_FiveShapesAndPerFormatChannel()
    {
        const int ModeWcg = 1, ModeHdr = 2;
        static TrueToneCap.Core.Capture.DisplayInfo Scr(bool isHdr, int acMode, string edidTag)
            => new TrueToneCap.Core.Capture.DisplayInfo
            {
                IsHdr = isHdr, SupportsHdr = true, BitsPerColor = 10,
                AdvancedColorInfoAvailable = true, AdvancedColorActiveMode = acMode,
                WideColorEnabled = true, NativeGamutKnown = true,
                NativeGamutWide = edidTag != "sRGB", NativeGamutTag = edidTag,
            };
        var hdrScreen = Scr(true, ModeHdr, "DisplayP3");      // 三档里的 Hdr
        var sdrWideScreen = Scr(false, ModeWcg, "DisplayP3"); // WideGamutSdr：HDR 硬件在、系统没开
        var narrowScreen = Scr(false, ModeWcg, "sRGB");       // NarrowSdr：ACM 全开但面板自证不宽

        var allFormats = new[]
        {
            OutputFormat.PNG, OutputFormat.JPEG_LI, OutputFormat.JPEG_XL,
            OutputFormat.AVIF, OutputFormat.WebP, OutputFormat.TIFF, OutputFormat.JPEG_GAINMAP,
        };
        // 一律按"HDR 开关打开"问 —— 这条测试的主题就是"开关为真时到底交付什么"。
        HdrDelivery Del(TrueToneCap.Core.Capture.DisplayInfo? s, string tag, OutputFormat f)
            => FormatColorFacts.Delivery(CDec.Build(s, tag, true, true), f);

        // ═══ ① 同一个"HDR 开"，换格式就换形态 ═══
        foreach (var f in new[] { OutputFormat.PNG, OutputFormat.JPEG_XL, OutputFormat.AVIF, OutputFormat.TIFF })
            Assert($"{f} + HDR 屏 + BT.2020 ⇒ 容器写 PQ",
                Del(hdrScreen, "BT2020", f) == HdrDelivery.PqContainer);
        foreach (var f in new[] { OutputFormat.JPEG_LI, OutputFormat.WebP })
            Assert($"{f} + HDR 屏 + BT.2020 ⇒ 容器没有 HDR 通道（开关落空，界面不许再说直通）",
                Del(hdrScreen, "BT2020", f) == HdrDelivery.BlockedByFormat);
        Assert("默认档「跟随显示器」在 HDR 屏上解析成 BT.2020 ⇒ 也是写 PQ（不是又一格特例）",
            Del(hdrScreen, "System", OutputFormat.PNG) == HdrDelivery.PqContainer);

        // ═══ ② 目标是 sRGB ⇒ 普通格式退成 SDR，增益图不退 ═══
        Assert("★ PNG + HDR + 显式 sRGB ⇒ BlockedBySrgbTarget（旧卡片在这一格印「HDR 直通 (sRGB)」）",
            Del(hdrScreen, "sRGB", OutputFormat.PNG) == HdrDelivery.BlockedBySrgbTarget);
        Assert("★ 增益图 + HDR + sRGB 底图 ⇒ 仍是增益图：base 层恒 SDR 是 Ultra HDR 的约定，不是降级",
            Del(hdrScreen, "sRGB", OutputFormat.JPEG_GAINMAP) == HdrDelivery.GainMap);
        Assert("增益图 + Rec.2020 底图 ⇒ 同一个形态（两档只差底图用哪套原色，不改交付物是什么）",
            Del(hdrScreen, "BT2020", OutputFormat.JPEG_GAINMAP) == HdrDelivery.GainMap);

        // ═══ ③ 屏幕证据排在格式与目标前面 ═══
        Assert("窄色域屏 + HDR 开关 ⇒ 逐格式全是同一格 BlockedByDisplay（没有 HDR 源可交付）",
            allFormats.All(f => Del(narrowScreen, "BT2020", f) == HdrDelivery.BlockedByDisplay));
        Assert("★ 增益图 + 窄色域屏 ⇒ 屏不授权 ⇒ 产物退成普通 JPEG（这一格以前完全无声，现在卡片要说清）",
            Del(narrowScreen, "sRGB", OutputFormat.JPEG_GAINMAP) == HdrDelivery.BlockedByDisplay);
        var wcg = CDec.Build(sdrWideScreen, "BT2020", true, true);
        Assert("P3 SDR 屏（HDR 未开启）⇒ HDR 收口，但广色域声明照常可用（两个方向不是一回事）",
            Del(sdrWideScreen, "BT2020", OutputFormat.PNG) == HdrDelivery.BlockedByDisplay
            && wcg.TargetTag == "BT2020" && !wcg.GamutClamped);

        // ═══ ④ 承重不变式：全表扫（4 屏态 × 5 标签 × 7 格式 = 140 格），防"以后加一档漏一格" ═══
        int pqInSrgbContainer = 0, gainMapMismatch = 0, pqWithoutEncoderConsent = 0, cells = 0;
        foreach (var s in new[] { hdrScreen, sdrWideScreen, narrowScreen, null })
            foreach (var t in new[] { "System", "sRGB", "BT2020", "DisplayP3", "AdobeRGB" })
                foreach (var f in allFormats)
                {
                    cells++;
                    var d = CDec.Build(s, t, true, true);
                    var v = FormatColorFacts.Delivery(d, f);
                    // (a) sRGB 容器 + PQ 声明 = 声明与像素矛盾（§39 那类错），一格都不许有
                    if (v == HdrDelivery.PqContainer && d.TargetTag == "sRGB") pqInSrgbContainer++;
                    // (b) 增益图形态只属于增益图格式，且增益图格式在授权时只能是这个形态
                    if ((v == HdrDelivery.GainMap) != (f == OutputFormat.JPEG_GAINMAP
                            && d.HdrOutput && v != HdrDelivery.BlockedByDisplay)) gainMapMismatch++;
                    // (c) 界面说得出「写 PQ」的每一格，编码器自己也都认（不许承诺产物做不到的事）
                    if (v == HdrDelivery.PqContainer && !EncoderFactory.Create(f).SupportsHdr)
                        pqWithoutEncoderConsent++;
                }
        Assert("不变式：140 格里没有一格在 sRGB 目标下写 PQ", pqInSrgbContainer == 0,
            $"违例 {pqInSrgbContainer} 格");
        Assert("不变式：增益图形态与格式一一对应", gainMapMismatch == 0, $"违例 {gainMapMismatch} 格");
        Assert("不变式：说得出「写 PQ」的格子，编码器全部 SupportsHdr", pqWithoutEncoderConsent == 0,
            $"违例 {pqWithoutEncoderConsent} 格");
        Assert("扫描真的覆盖到 140 格（少一格就是这张表自己漏了组合）", cells == 140, $"cells={cells}");

        // ═══ ⑤ 逐格式的声明通道："同一枚目标色域，装进不同格式长得不一样" ═══
        string Decl(OutputFormat f, string tag, bool pq) => FormatColorFacts.Declaration(f, tag, pq);
        Assert("PNG 的 matrix 恒 identity(0)，AVIF 的 matrix 随三原色取 1/9 ⇒ 同一个 sRGB 目标两格数字不同",
            Decl(OutputFormat.PNG, "sRGB", false).Contains("1/13/0")
            && Decl(OutputFormat.AVIF, "sRGB", false).Contains("1/13/1")
            && Decl(OutputFormat.PNG, "BT2020", false).Contains("9/13/0")
            && Decl(OutputFormat.AVIF, "BT2020", false).Contains("9/13/9"));
        Assert("HDR 两格都是 16 号传输（PQ）：9/16/0 与 9/16/9",
            Decl(OutputFormat.PNG, "BT2020", true).Contains("9/16/0")
            && Decl(OutputFormat.AVIF, "BT2020", true).Contains("9/16/9"));
        Assert("★「有些格式是 BT.709」指的是 matrix 那一维；transfer 只有 13(sRGB)/16(PQ)，全仓没有 14 号",
            ColorSpaceConverter.GetCicpTransfer("sRGB", false) == 13
            && ColorSpaceConverter.GetCicpTransfer("BT2020", false) == 13
            && ColorSpaceConverter.GetCicpTransfer("BT2020", true) == 16
            && ColorSpaceConverter.GetCicpTransfer("DisplayP3", false) == 13);
        Assert("Rec.709 目标下 JPEG LI / WebP 什么都不写，TIFF 却连 Rec.709 也嵌一份 ICC ⇒ 同一目标两种通道",
            Decl(OutputFormat.JPEG_LI, "sRGB", false).Contains("什么都不写")
            && Decl(OutputFormat.WebP, "sRGB", false).Contains("什么都不写")
            && Decl(OutputFormat.TIFF, "sRGB", false).Contains("连 Rec.709 也")
            && !Decl(OutputFormat.TIFF, "sRGB", false).Contains("什么都不写"));

        // ═══ 2026-09-27 用户口径：面向用户的色域名字只有 Rec.709 / Rec.2020 两个 ═══
        // 原来同一档在界面上叫 sRGB、卡片里叫 BT.709/sRGB、声明文案里叫 BT.2020、灰字里还写过"非 sRGB"，
        // 而"BT.2020 (HDR)"那种写法把【原色】和【动态范围】两个维度混进一个词里。
        Assert("Core 的两个可选目标名 = Rec.709 / Rec.2020（Tag 仍是 sRGB/BT2020，那是盘上契约）",
            ColorProfileProvider.GetColorSpaceDisplayName("sRGB") == "Rec.709"
            && ColorProfileProvider.GetColorSpaceDisplayName("BT2020") == "Rec.2020",
            $"[{ColorProfileProvider.GetColorSpaceDisplayName("sRGB")}] [{ColorProfileProvider.GetColorSpaceDisplayName("BT2020")}]");
        Assert("七种格式的声明文案里不许再出现 BT.2020 / BT.709 这两个旧叫法（CICP 码点数字照旧印）",
            new[] { "sRGB", "BT2020" }.All(tag => new[] { false, true }.All(pq =>
                Enum.GetValues<OutputFormat>().All(f =>
                    !Decl(f, tag, pq).Contains("BT.2020") && !Decl(f, tag, pq).Contains("BT.709")))),
            string.Join(" | ", Enum.GetValues<OutputFormat>()
                .Select(f => Decl(f, "BT2020", false)).Where(s => s.Contains("BT.")).ToArray()));
        // 名字必须只有一份事实源：界面标签属性从 Core 要，XAML 里的字面量与 Core 的返回值必须相同
        var locPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        var locSrc = locPath is null ? "" : StripComments(File.ReadAllText(locPath));
        Assert("下拉标签不再自己写字面量，改成向 Core 的名字函数要（两处各写一份随时会漂）",
            locSrc.Contains("CsSRgb => TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetColorSpaceDisplayName(\"sRGB\")")
            && locSrc.Contains("CsBT2020 => TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetColorSpaceDisplayName(\"BT2020\")"),
            "LocaleManager 里这两行没按单源写法出现");
        var xamlNames = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"))!);
        Assert("XAML 里那两项的显示文字就是这个新名字（Tag 不动 ⇒ 盘上那个值与迁移表都不受影响）",
            xamlNames.Contains("<ComboBoxItem Tag=\"sRGB\">Rec.709</ComboBoxItem>")
            && xamlNames.Contains("<ComboBoxItem Tag=\"BT2020\">Rec.2020</ComboBoxItem>")
            && !xamlNames.Contains("<ComboBoxItem Tag=\"BT2020\">BT.2020</ComboBoxItem>"),
            "XAML 下拉项文字与 Core 的名字不同步");
        Assert("增益图那一格必须说出「容器不写 PQ」——它是 HDR 的第三种形态，不是 PQ 容器",
            Decl(OutputFormat.JPEG_GAINMAP, "sRGB", false).Contains("不写 PQ"));
        Assert("可选目标只剩 sRGB / BT.2020 ⇒ 通道文案不许再教用户去选 P3、Adobe RGB",
            allFormats.All(f => !Decl(f, "sRGB", false).Contains("P3")
                             && !Decl(f, "sRGB", false).Contains("Adobe")
                             && !Decl(f, "BT2020", false).Contains("P3")
                             && !Decl(f, "BT2020", true).Contains("Adobe")));
        Assert("7 个格式在 sRGB/2020 × SDR/PQ 的可达格上全部答得出话（不许留 _ => \"\" 的静默格）",
            allFormats.All(f => new[] { ("sRGB", false), ("BT2020", false), ("BT2020", true) }
                .All(k => Decl(f, k.Item1, k.Item2).Length > 24)));

        // ═══ ⑥ 接线哨兵：卡片与管线都必须问这一份，不许再各推一遍 ═══
        string Read(string rel) => File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", rel))!);
        // ⚠ 计数从"上一次命中的下一个字符"起找：写成 IndexOf(needle) 每次从头找同一处 ⇒ 死循环
        //   （这条判据第一次跑就把测试进程钉死在 90% CPU，13 分钟没吐一行日志）。
        static int Count(string hay, string needle)
        {
            int n = 0, i = 0;
            while ((i = hay.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
            return n;
        }
        var cm = StripLineComments(Read("MainWindow.ColorManagement.cs"));
        Assert("「显示器色域」「目标」两行各只有一个写入点，且没有任何读取点（旧代码把 .Text 当数据源）",
            Count(cm, "SourceGamutTxt.Text") == 1 && Count(cm, "TargetGamutTxt.Text") == 1
            && Count(cm, "MappingArrow.Text") == 1 && Count(cm, "GamutMapHintTxt.Text") == 1);
        Assert("卡片不再用 Contains 反推显示字符串", !cm.Contains(".Contains("));
        Assert("卡片三个字段全部来自同一份决定 + 同一份交付形态判定",
            cm.Contains("DecisionForCursorScreen(ui, ui.HdrEnabled)")
            && cm.Contains("FormatColorFacts.Delivery(decision, ui.Format)")
            && cm.Contains("FormatColorFacts.Declaration(format, d.TargetTag, writesPq)"));
        Assert("旧的显示文案前缀不许回来（它们曾经是判据的输入）",
            !cm.Contains("BT.2020/PQ") && !cm.Contains("HDR 未开启"));

        var pipe = StripLineComments(Read(Path.Combine("Services", "CapturePipelineService.cs")));
        Assert("管线里那条「HDR + sRGB ⇒ 降级」改问 FormatColorFacts，不再自己比标签",
            pipe.Contains("FormatColorFacts.Delivery(decision, format)")
            && pipe.Contains("delivery == HdrDelivery.BlockedBySrgbTarget")
            && !pipe.Contains("hdrOutput && resolvedTag == \"sRGB\""));

        // ═══ ⑦ 两份能力表不许分叉：界面表说的必须等于编码器自己说的 ═══
        var mwRaw = Read("MainWindow.xaml.cs");
        var drift = new System.Text.StringBuilder();
        foreach (var f in allFormats)
        {
            var m = Regex.Match(mwRaw, @"\[OutputFormat\." + f + @"\]\s*=\s*new\(\s*OutputFormat\."
                + f + @",\s*\n?\s*SupportsHdr:\s*(true|false)");
            if (!m.Success) { drift.Append($" {f}(读不到行)"); continue; }
            if ((m.Groups[1].Value == "true") != EncoderFactory.Create(f).SupportsHdr)
                drift.Append($" {f}(界面={m.Groups[1].Value} 编码器={EncoderFactory.Create(f).SupportsHdr})");
        }
        Assert("界面能力表与 EncoderFactory 的 SupportsHdr 逐格式一致（分叉了就会印出界面敢说、产物做不到的形态）",
            drift.Length == 0, drift.ToString());
    }

    /// <summary>
    /// 界面中文本地化覆盖（2026-09-26，交接 §55）。
    /// <para>要防的是同一类病：显示层另存一份真相。XAML 里写死中文，英文模式靠谁翻？
    /// 第一版答案是"代码里恰好也存在这条中文" —— 那个判据既证明不了机制（字符串可能只出现在注释里），
    /// 也证不出漏翻（控件类型不在扫描面、副窗口压根没挂扫描钩子时，判据照样绿）。
    /// 现在锁的是一条机制的四件事：</para>
    /// <list type="number">
    /// <item><description>每个窗口都挂可视树扫描钩子（漏挂 = 该窗口英文模式整窗中文）；</description></item>
    /// <item><description>XAML 每条中文静态文字都在 <c>LocaleManager.ZhToEn</c> 里（表是英文侧唯一真相）；</description></item>
    /// <item><description>承载中文的 (控件类型, 属性) 都在 App 声明的扫描面里 —— 判据读 App 自己
    /// 那份 <c>SweepSurface</c> 声明，不抄第二份清单；</description></item>
    /// <item><description>同一个中文只许有一种英文说法（表 vs 中英三元）。</description></item>
    /// </list>
    /// <para>判据自身两个坑都记在这儿，因为它们能把真假报反：
    /// ① 第一版取"代码侧已知中文"用 <c>"([^"]*中文[^"]*)"</c>，<c>[^"]</c> 吃换行 ⇒ 引号配对整体错位，
    /// 量到 221 条里全是几十行代码当一条的垃圾，"就绪""截图设置"这类表里明明有的条目反而报成孤儿；
    /// ② 临时探针忘了 XAML 实体解码，<c>gt;8-bit</c> 与表键对不上，又假报一条"代码里完全没有"。
    /// ⇒ 于是：字面量禁止跨行、实体解码、以及"抽掉一条必须恰好红一条"的变异自检都进了判据。</para>
    /// <para>覆盖层里 <c>RenderTextBitmap</c> 自绘的文字不经 XAML 属性 ⇒ 不在本判据范围内，另计。</para>
    /// </summary>
    /// <summary>
    /// 格式那一列对用户只报**界面名**，不许把编码器实现名或已合并的旧格式名卖出去（2026-09-27）。
    /// <para>起因（用户口径）："JPEG LI 和 JPEG GainMap 合并了啊，不再作为 2 个选项，只有一个下拉选项 JPEG 啊" ——
    /// 合并之后下拉里只剩一个 JPEG 档，而标签当时写的是 <c>"JPEG LI"</c>（那是编码器 jpegli 的实现名、
    /// 也是内部枚举名），卡片标题、灰字、声明文案、审计卡跟着一起说"JPEG LI"，
    /// 读起来就像界面上还有两种 JPEG。内部名保留在代码/日志/判据消息里，界面面一律 <c>JPEG</c>。</para>
    /// <para>判据形状与两条纪律：① 事实源是 <c>LocaleManager.FmtJpegLi</c>，直接钉它的右值；
    /// ② 扫"可见文字"而不是整个文件 —— XAML 先剥 XML 注释、C# 先剥整行注释（§56.5 那条教训：
    /// 解释这次改名的注释里必然写着被禁的词，不剥就是自我误伤）；
    /// ③ 枚举为空一律判红（扫不到东西就是判据自己瞎了，不是"很干净"）。</para>
    /// </summary>
    static void Test_FormatLabels_AreUserFacingNames()
    {
        var locPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (locPath is null || xamlPath is null)
        { Skip("格式标签不许用实现名", "找不到 LocaleManager.cs 或 MainWindow.xaml"); return; }

        var loc = StripComments(File.ReadAllText(locPath));
        Assert("JPEG_LI 这一档的界面标签 = 「JPEG」（增益图已并入，下拉里只有这一个 JPEG 档）",
            loc.Contains("FmtJpegLi => \"JPEG\""), "没找到 FmtJpegLi => \"JPEG\" 这一行");

        var xaml = System.Text.RegularExpressions.Regex.Replace(
            File.ReadAllText(xamlPath), @"<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var visible = System.Text.RegularExpressions.Regex
            .Matches(xaml, "(?:Text|Content)=\"([^\"\n]*)\"").Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value).ToList();
        Assert("XAML 可见文字要枚举得到（空或过少 = 判据自己瞎了）", visible.Count > 30, $"count={visible.Count}");
        var badXaml = visible.Where(s => s.Contains("JPEG LI") || s.Contains("JPEG Gain")).ToList();
        Assert("界面可见文字里不许出现「JPEG LI」「JPEG Gain Map」（实现名 / 已合并的旧格式名）",
            badXaml.Count == 0, string.Join(" | ", badXaml));

        // 翻译表两侧都算用户可见：键 = 界面中文，值 = 英文
        var pairs = System.Text.RegularExpressions.Regex
            .Matches(loc, @"\[""([^""\n]*)""\]\s*=\s*\r?\n?\s*""([^""\n]*)""")
            .Cast<System.Text.RegularExpressions.Match>().ToList();
        Assert("翻译表要枚举得到（本仓当前 180 条以上，少于 100 条就是解析掉了）",
            pairs.Count > 100, $"pairs={pairs.Count}");
        var badTable = pairs.Where(m => m.Groups[1].Value.Contains("JPEG LI")
                                     || m.Groups[2].Value.Contains("JPEG LI")
                                     || m.Groups[1].Value.Contains("JPEG Gain")
                                     || m.Groups[2].Value.Contains("JPEG Gain")).ToList();
        Assert("翻译表两侧都不许留着实现名（英文侧漏一条就是「中文改了英文没改」的另一种不一致）",
            badTable.Count == 0,
            string.Join(" | ", badTable.Select(m => m.Groups[1].Value + " ⇒ " + m.Groups[2].Value)));

        // 声明卡（七格 ×两档）是用户读"这个格式怎么写声明"的地方，同样不许冒出实现名
        var badDecl = (from f in Enum.GetValues<OutputFormat>()
                       from tag in new[] { "sRGB", "BT2020" }
                       from pq in new[] { false, true }
                       let s = FormatColorFacts.Declaration(f, tag, pq)
                       where s.Contains("JPEG LI") || s.Contains("JPEG Gain")
                       select $"{f}/{tag}/{pq}: {s}").ToList();
        Assert("逐格式声明文案用界面名（JPEG / JPEG（附带增益图）），不用 JPEG LI",
            badDecl.Count == 0, string.Join(" | ", badDecl));

        // 增益图仍然是"JPEG 的开关"而不是第二个格式（§52 那条锁的同族，这里再钉一次界面侧）
        Assert("界面可见文字里「增益图」必须挂在 JPEG 名下说，不再作为第二个格式选项",
            !visible.Any(s => s.Contains("JPEG Gain Map")),
            string.Join(" | ", visible.Where(s => s.Contains("JPEG Gain Map"))));
    }

    /// <summary>
    /// 两处"面向用户、但此前没有任何判据看着"的文本面：格式能力表的 <c>Hint:</c> 与
    /// <c>EncoderFallbackNotifier.LastReason</c>（降级 Toast 的正文）。
    /// <para>为什么要单独一条：§57–§59 连着做了三轮改名（Rec.709/Rec.2020、JPEG、Adobe 收口），
    /// 每轮都有判据护住 XAML 可见文字、翻译表两侧和逐格式声明卡 —— 而这两个面**一条锁都没有**，
    /// 于是同一句话里"Rec.2020 原色 + sRGB 传输（对）"和"退回 BT.2020"（错）能同时存在好几轮。
    /// 判据形态：扫字面量、命中旧叫法即红；并带 fail-closed 下限（扫不到东西就是自己瞎了）。</para>
    /// </summary>
    static void Test_VisibleToastAndHintStrings_UseCurrentNames()
    {
        // 旧叫法里，只有"说【用户可选项】"的写法算违规：sRGB 作为**传递函数/容器事实**必须允许出现
        //（"Rec.2020 原色 + sRGB 传输"是对的，"sRGB 目标不写声明"是过期的）。
        // ⚠ 故意不收 "BT2020" —— 那是盘上的 Tag 值与 ICC 生成的键，出现在代码字面量里是对的；
        //   带点的 "BT.2020" 才是给人看的旧叫法（ICC 描述文本除外：那是要写进文件的字节，
        //   所以只扫下面这几个文件，不扫 IccStore / ColorProfileProvider）。
        var stale = new[] { "BT.2020", "BT.709", "JPEG LI", "JPEG Gain Map", "目标 sRGB", "sRGB 目标" };
        var hits = new List<string>();

        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (mwPath is null) { Skip("Toast/Hint 用现行名", "找不到 MainWindow.xaml.cs"); return; }
        var mw = StripComments(ReadSourceNormalized(mwPath));
        // 按整条语句取，因为不少 Hint 是 `"a" + "b"` 拼接的 —— 只取第一段字面量会漏掉后半句。
        var hints = System.Text.RegularExpressions.Regex
            .Matches(mw, @"Hint:\s*([\s\S]{0,600}?),\s*\n")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .Where(s => s.Length > 0).ToList();
        Assert("格式能力表的 Hint 要枚举得到（本仓 7 个格式各一条，少于 5 条 = 判据自己瞎了）",
            hints.Count >= 5, $"count={hints.Count}");

        var notifySources = new[]
        {
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"),
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "JpegGainMapEncoder.cs"),
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "NativeJxlEncoder.cs"),
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "NativeAvifEncoder.cs"),
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "ImageEncoder.cs"),
        };
        var toasts = new List<string>();
        foreach (var rel in notifySources)
        {
            var p = FindRepoFile(rel);
            if (p is null) continue;
            var src = StripComments(ReadSourceNormalized(p));
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(
                         src, @"EncoderFallbackNotifier\.LastReason\s*=\s*([\s\S]{0,400}?);"))
                toasts.Add(m.Groups[1].Value);
        }
        Assert("降级 Toast 的赋值点要枚举得到（>0，否则这条锁什么都不判）",
            toasts.Count > 0, $"Hint {hints.Count} 条、Toast {toasts.Count} 条");

        foreach (var s in hints.Concat(toasts))
            foreach (var word in stale)
                if (s.Contains(word))
                    hits.Add($"「{word}」← {System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ")[..Math.Min(90, s.Length)]}");

        Assert("Hint / 降级 Toast 里不许用旧色域名或实现名说【用户可选项】（Rec.709/Rec.2020/JPEG）",
            hits.Count == 0, string.Join(" | ", hits.Distinct().Take(6)));
    }

    /// <summary>
    /// 锁：XAML 里【以 Collapsed 起步】的命名控件，代码里必须存在对它 <c>.Visibility</c> 的赋值。
    /// <para>为什么要有这条（2026-09-27，用户报「AVIF 缺少编码器选项」查出的一次发布期回归）：
    /// <c>AvifOptionsCard</c> 在 XAML 里是 <c>Visibility="Collapsed"</c>，而
    /// <c>AvifOptionsCard.Visibility = isAvif ? Visible : Collapsed</c> 那一行在
    /// <b>cde6168（release: v0.3.5-beta，我自己做的那个发布提交）里被删掉了</b> ⇒
    /// 「AVIF 编码器 / 色度采样 / .png 后缀」三行在任何状态下都不再出现，而 7 个套件全绿 ——
    /// 门禁对"控件存在但没人点亮"这一类完全失明：不崩、不报错、日志干净，只是界面上没有。</para>
    /// <para>判据形状：枚举 XAML 里同时带 <c>x:Name</c> 与 <c>Visibility="Collapsed"</c> 的开标签
    /// （两个属性的先后顺序不假设），再在 <c>MainWindow*.cs</c> 这几份 partial 里找
    /// <c>名字.Visibility</c> 或 <c>名字?.Visibility</c>。剥行注释 —— 注释里提到控件名不算点亮。</para>
    /// <para>⚠ 枚举为空必须判红：这条锁的全部意义是"一个都不能漏"，扫不到东西就是自己瞎了。</para>
    /// </summary>
    static void Test_Xaml_CollapsedCards_HaveVisibilityOwner()
    {
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (xamlPath is null) { Skip("Collapsed 卡片的点亮归属", "找不到 MainWindow.xaml"); return; }
        var appDir = Path.GetDirectoryName(xamlPath)!;
        var sources = Directory.GetFiles(appDir, "MainWindow*.cs")
            .Where(f => !f.Contains(".g.cs") && !f.Contains(".i.cs"))
            .ToList();
        if (sources.Count == 0) { Skip("Collapsed 卡片的点亮归属", "找不到 MainWindow*.cs partial"); return; }

        var xaml = File.ReadAllText(xamlPath);
        var owners = string.Join("\n", sources.Select(f => StripLineComments(File.ReadAllText(f))));

        var collapsed = new List<string>();
        foreach (System.Text.RegularExpressions.Match tag in
                 System.Text.RegularExpressions.Regex.Matches(xaml, @"<[A-Za-z][\w.]*\b[^>]*>"))
        {
            var name = System.Text.RegularExpressions.Regex.Match(tag.Value, @"x:Name=""(\w+)""");
            if (name.Success && tag.Value.Contains(@"Visibility=""Collapsed"""))
                collapsed.Add(name.Groups[1].Value);
        }
        collapsed = collapsed.Distinct().ToList();

        Assert($"XAML 里必须枚举得到 Collapsed 起步的命名控件（0 个 = 判据自己瞎了，当前 App 侧有 16 个这样的控件）",
            collapsed.Count > 0, $"partial 源 {sources.Count} 份，枚举到 {collapsed.Count} 个");

        var orphans = collapsed
            .Where(n => !owners.Contains(n + ".Visibility") && !owners.Contains(n + "?.Visibility"))
            .ToList();
        Assert("每个以 Collapsed 起步的控件都必须有代码点亮它（漏一个就是「界面里永远看不见」，而门禁不会响）",
            orphans.Count == 0,
            $"孤儿控件：{string.Join("、", orphans)}（共 {collapsed.Count} 个 Collapsed 控件，扫了 {sources.Count} 份 partial）");

        // 这条锁的【牙】不是自称的：把 AvifOptionsCard 那一行删掉就红 —— 实测过（见交接 §57）
        Assert("判据真的覆盖到 AvifOptionsCard 本身（否则这次修的缺陷不在锁的保护范围内）",
            collapsed.Contains("AvifOptionsCard"), $"枚举结果：{string.Join("、", collapsed)}");
    }

    static void Test_Xaml_HardcodedChineseIsLocalized()
    {
        var lmPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        var mainXaml = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (lmPath is null || mainXaml is null)
        { Skip("界面中文本地化覆盖", "未找到源文件"); return; }
        var appDir = Path.GetDirectoryName(mainXaml)!;
        var lcPath = Path.Combine(appDir, "MainWindow.Localization.cs");

        string Cut(string s) => s.Length > 24 ? s[..24] + "…" : s;
        bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── 1) 对照表：只取 ZhToEn 初始化块里的 ["中文"] = "English" ──
        var lmText = File.ReadAllText(lmPath);
        int at = lmText.IndexOf("ZhToEn =", StringComparison.Ordinal);
        if (at < 0) { Skip("界面中文本地化覆盖", "未找到 ZhToEn 表"); return; }
        var block = lmText.Substring(at, lmText.IndexOf("};", at, StringComparison.Ordinal) - at);
        var entries = Regex.Matches(block, @"\[""([^""\n]*)""]\s*=\s*""([^""\n]*)""")
            .Select(m => (Zh: m.Groups[1].Value, En: m.Groups[2].Value)).ToList();
        var table = new HashSet<string>(entries.Select(e => e.Zh), StringComparer.Ordinal);
        var tableEn = entries.ToDictionary(e => e.Zh, e => e.En, StringComparer.Ordinal);

        Assert("对照表解析出条目（解析失败 = 判据瞎了，不等于表是空的）", entries.Count > 100,
            $"只解析出 {entries.Count} 条");
        Assert("解析出的键值不跨行、不含反斜杠（不跨行=没吃到配对垃圾；不含反斜杠=本判据不必处理引号转义）",
            entries.All(e => e.Zh.IndexOf('\\') < 0 && e.En.IndexOf('\\') < 0),
            $"含反斜杠 {entries.Count(e => e.Zh.IndexOf('\\') >= 0 || e.En.IndexOf('\\') >= 0)} 条");
        Assert("对照表条目数 = 去重键数（重复键在静态构造期抛 ArgumentException，设置页直接起不来）",
            entries.Count == table.Count,
            $"{entries.Count} 条 / {table.Count} 键，重复：" +
            string.Join(",", entries.GroupBy(e => e.Zh).Where(g => g.Count() > 1).Select(g => Cut(g.Key)).Take(5)));
        Assert("每条都有英文（空英文 ⇒ 英文模式显示空白，比留中文更糟）",
            entries.All(e => e.En.Trim().Length > 0),
            $"空英文 {entries.Count(e => e.En.Trim().Length == 0)} 条");
        Assert("英文侧不残留中文（残留 = 这条根本没翻）",
            entries.All(e => !HasCjk(e.En)),
            $"残留：{string.Join(" | ", entries.Where(e => HasCjk(e.En)).Select(e => Cut(e.Zh)).Take(6))}");

        // ── 2) 扫描面：读 App 那份声明本身（判据自己再抄一份类型清单，就是第二份真相）──
        // 形状写死成"行首缩进 + (typeof(名字), 属性)"，解析前先剥 C# 注释：
        // 一条被注释掉的旧声明和真声明长得一模一样，留着它就是"扫描器不看这个控件、判据却认为看"。
        // ⚠ 本轮实测过方向本身：现在并不红（11 项，剥与不剥同一结果），这条是防将来的。
        var lcStripped = StripComments(File.ReadAllText(lcPath));
        var surface = ParseSweepSurface(lcStripped);
        Assert("扫描面从 App 声明里读到（少于 8 项 = 声明形状变了，判据得跟着改而不是静默放行）",
            surface.Count >= 8, $"只读到 {surface.Count} 项");
        // 对抗夹具：把一条声明整行放进块注释里。行注释（`// (typeof…`）本来就撞不上 `^\s*\(typeof\(`
        // 这个锚点，所以真正的风险形状是块注释里的"注释掉的旧声明"——它和真声明逐字符一样。
        // 不剥注释会数到 2 项（多出的那条能让"控件属性必须在扫描面里"从此对 Header 恒真）。
        const string Fixture = "        (typeof(TextBlock), \"Text\",\n"
                             + "/* 旧的写法留着当参考\n"
                             + "        (typeof(PhantomHeaderControl), \"Header\",\n"
                             + "*/\n";
        Assert("块注释里的声明不算扫描面（夹具：不剥=2 项 / 剥了=1 项，两个读数都得对上）",
            ParseSweepSurface(Fixture).Count == 1 && ParseSweepSurface(Fixture, strip: false).Count == 2,
            $"剥了 {ParseSweepSurface(Fixture).Count} 项 / 不剥 {ParseSweepSurface(Fixture, strip: false).Count} 项");
        // 判据自身的哨兵：按原文包含判断"挂没挂钩子"这种写法，只许待在 CallsLocaleSweep 的定义里
        // （那里是唯一的收口点），别处再冒出来一处，就等于"谁在注释里提一句 HookLocaleSweep，
        // 那个窗口算挂了钩子"。⚠ 上一版我写成"整文件不许出现该模式"，结果连定义自己都不许有 ⇒
        // 它从写下来那一刻就恒红（发布门禁替我抓到），这版改成计数。
        // needle 用拼接构造，否则这条断言自己就会命中它要找的模式。
        var self = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "TrueToneCap.Test", "CorePipelineTests.cs"))!);
        var needle = ".Contains(\"HookLocaleSweep" + "\")";
        int rawSites = CountOccurrences(self, needle);
        // 只数"按原文判断"的写法不许超过收口那一处。⚠ 上一版还挂着半句
        // `self.Contains("static bool CallsLocaleSweep(...)")`，看着像"检查收口存在"，
        // 其实把定义改名它照样绿（假保护）—— 已去掉，不留装饰性的右半句。
        Assert("原文包含判断只允许出现一处（= CallsLocaleSweep 的收口点），别处再冒出来一处即判红",
            rawSites == 1, $"数到 {rawSites} 处按原文判断的写法");

        // 翻译与"英文模式残留计数"必须共用同一个匹配函数。复核轮抓到的缺陷正是残留那边
        // 写成了精确类型相等（`slot.Control == …`）⇒ 声明在 FrameworkElement 上的 ToolTip 一格都不算，
        // 那条运行时告警报出的 N 是假的低，而静态判据全绿。这条是它的镜像断言。
        // 同样只看代码行：注释里出现 `slot.Control ==` 不算分叉（本轮那条说明注释就会误伤）。
        int body = lcStripped.IndexOf("ApplyLocaleCollect(DependencyObject? root", System.StringComparison.Ordinal);
        var residue = body < 0 ? "" : lcStripped.Substring(body, System.Math.Min(700, lcStripped.Length - body));
        Assert("残留计数与翻译共用 MatchesSurface（不许退回精确类型相等，那会漏掉 ToolTip 那一整类）",
            residue.Contains("MatchesSurface") && !residue.Contains("slot.Control =="),
            residue.Length == 0 ? "找不到 ApplyLocaleCollect" : "两处匹配规则又分叉了");

        // ── 3) XAML：App 下每一份都算，逐个元素带控件标签取出中文属性 ──
        var xamlFiles = Directory.GetFiles(appDir, "*.xaml")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        Assert("枚举到 App 的 XAML 文件（枚举为空必须拦，不许当成界面没有中文）",
            xamlFiles.Count >= 6, $"只枚举到 {xamlFiles.Count} 份");

        var attrs = new List<(string File, string Tag, string Prop, string Value)>();
        foreach (var f in xamlFiles)
        {
            var src = Regex.Replace(File.ReadAllText(f), @"<!--.*?-->", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (Match el in Regex.Matches(src, @"<([\w:]+)((?:[^<"">]|""[^""]*"")*?/?>)",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                foreach (Match a in Regex.Matches(el.Groups[2].Value, XamlTextAttrPattern))
                {
                    var val = System.Net.WebUtility.HtmlDecode(a.Groups[2].Value);
                    if (!HasCjk(val)) continue;
                    var prop = a.Groups[1].Value;
                    var tag2 = el.Groups[1].Value.Split(':').Last();
                    // 附加属性（ToolTipService.ToolTip）沿基类链注册，运行时任何 FrameworkElement
                    // 都扫得到 ⇒ 判据侧把两侧一起收口到上限名，否则"控件名对不上"会报假红。
                    if (prop.Contains('.')) tag2 = "FrameworkElement";
                    // <Window Title="…"> 不在可视树里（扫描器读不到），只能由构造器显式 Tr(Title)
                    // ⇒ 不进"必须对照表"的口子。窗口标题有没有英文，本判据管不到，另计。
                    if (tag2 == "Window" && prop == "Title") continue;
                    attrs.Add((Path.GetFileName(f), tag2, prop, val));
                }
            }
        }
        Assert("XAML 里量到一批中文静态文字（读不到就是判据瞎了，不是界面干净了）", attrs.Count > 150,
            $"只解析出 {attrs.Count} 处 / {attrs.Select(a => a.Value).Distinct().Count()} 条");

        var notInTable = attrs.Where(a => !table.Contains(a.Value))
            .Select(a => $"[{a.File}:{a.Tag}.{a.Prop}] {Cut(a.Value)}")
            .Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert("XAML 每条中文静态文字都在对照表里（对照表 + 可视树扫描是英文界面的唯一机制）",
            notInTable.Count == 0, $"待接 {notInTable.Count}：" + string.Join(" | ", notInTable.Take(6)));

        var outside = attrs.Where(a => !surface.Contains((a.Tag, a.Prop)))
            .Select(a => $"{a.Tag}.{a.Prop}").Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert("XAML 出现中文的每个控件属性都在扫描面里（表里有译文、扫描器却不看这类控件 = 英文界面留中文）",
            outside.Count == 0, "表外控件属性：" + string.Join(",", outside));

        // ── 4) 每个 Window 都必须挂扫描钩子：漏挂的窗口英文模式整窗中文，上面两条全看不出来 ──
        // 判据形状按"源文件是否声明 `class X : … Window`"判定：WinUI 窗口都是 Window 的子类，
        // 而 App 里唯一的 Application 派生类（App.xaml.cs）不是窗口 ⇒ 不需要"非窗口源"这条断言，
        // 加一条反而会在将来出现 helper 源文件时报假红（判据不许有恒真式）。
        var windowSources = Directory.GetFiles(appDir, "*.xaml.cs")
            .Where(f => Regex.IsMatch(StripComments(File.ReadAllText(f)),
                        @"\bclass\s+\w+\s*:\s*[\w.,\s<>]*\bWindow\b"))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        // 包含判断走 CallsLocaleSweep：注释里提一句 HookLocaleSweep 不算挂了钩子
        // （这条判据自己的注释早就这么声称，实际原先读的是原文 —— 声称的保护要回代码查）。
        var unhooked = windowSources.Where(f => !CallsLocaleSweep(File.ReadAllText(f)))
            .Select(f => Path.GetFileName(f)).ToList();
        Assert("每个 Window 都挂了本地化扫描钩子（XAML 中文只有可视树扫描这一条出路）",
            unhooked.Count == 0, "漏挂：" + string.Join(",", unhooked));

        // ── 5) 同一个中文不许有两种英文说法 ──
        // 扫【全部 App 源码】而不是只扫 LocaleManager.cs：本轮实测有 3 格（BrandTagline /
        // PageOutputTitle / AvifCodecLabelTxt）把中英对写在 MainWindow.Localization.cs 里，
        // 英文侧与表里不一致（"Output & Color" vs 表里 "Output & Colour"）—— 只扫一个文件的
        // 歧义锁看不见它，而那正是"后写入的一份说了算、表里那份成死文案"的现场。
        // 两种写法都认：`? "中" : "英"` 与 `En ? "英" : "中"`。
        var enOfPair = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int suppressed = 0, stateLabels = 0;
        foreach (var srcFile in Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            var code = Regex.Replace(File.ReadAllText(srcFile), @"(?://[^\n]*)|/\*.*?\*/", "",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(code, @"\?\s*""([^""\n]*)""\s*:\s*""([^""\n]*)"""))
            {
                var (lhs, rhs) = (m.Groups[1].Value, m.Groups[2].Value);
                string? zh, en;
                if (HasCjk(lhs) && !HasCjk(rhs)) { zh = lhs; en = rhs; }
                else if (!HasCjk(lhs) && HasCjk(rhs)) { zh = rhs; en = lhs; }
                else { if (HasCjk(lhs) || HasCjk(rhs)) stateLabels++; continue; }
                if (en.Trim().Length == 0) { suppressed++; continue; }   // 英文模式把这句抹掉
                if (!enOfPair.TryGetValue(zh, out var set))
                    enOfPair[zh] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(en);
            }
        }
        var divergent = new List<string>();
        foreach (var kv in enOfPair)
            if (tableEn.TryGetValue(kv.Key, out var tEn) && !kv.Value.Contains(tEn))
                divergent.Add($"[{Cut(kv.Key)}] 表=[{Cut(tEn)}] 代码={string.Join("/", kv.Value.Select(Cut))}");
        Assert("同一个中文只许有一种英文说法（否则切语言时后写入的那份说了算，前一份是死文案）",
            divergent.Count == 0, $"{divergent.Count} 处分歧：" + string.Join(" | ", divergent.Take(5)));
        Assert("歧义锁真的扫到了跨文件的中英对（少于 100 条 = 它退化成只看得见一个文件）",
            enOfPair.Count > 100,
            $"只数到 {enOfPair.Count} 条；另有 {suppressed} 条抹除式、{stateLabels} 条双中文状态标签");
        // ── 6) 判据自己的牙：各抽掉一条，对应判据必须恰好变红 ──
        var distinct = attrs.Select(a => a.Value).Distinct(StringComparer.Ordinal).ToList();
        var probe = distinct.OrderBy(k => k, StringComparer.Ordinal).First();
        var shrunken = new HashSet<string>(table, StringComparer.Ordinal);
        shrunken.Remove(probe);
        Assert("变异自检：抽掉对照表一条 ⇒ 待接必须恰好多出这一条",
            shrunken.Count == table.Count - 1 && distinct.Count(k => !shrunken.Contains(k)) == 1,
            $"抽掉 [{Cut(probe)}] 后待接数 = {distinct.Count(k => !shrunken.Contains(k))}");
        var noTb = surface.Where(s => s.Item1 != "TextBlock" || s.Item2 != "Text").ToHashSet();
        Assert("变异自检：抽掉扫描面一项 ⇒ 必须量到落在表外的中文控件属性",
            noTb.Count == surface.Count - 1 && attrs.Any(a => !noTb.Contains((a.Tag, a.Prop))),
            $"抽掉 TextBlock.Text 后表外处数 = {attrs.Count(a => !noTb.Contains((a.Tag, a.Prop)))}");
        var noHook = windowSources.Count(f => !CallsLocaleSweep(File.ReadAllText(f)));
        Assert("变异自检：钩子判据有牙 —— 窗口源必须 5 份以上且逐个含 HookLocaleSweep",
            windowSources.Count >= 5 && noHook == 0,
            $"窗口源 {windowSources.Count} 份，缺钩子 {noHook} 份");
    }

    /// <summary>
    /// 从 App 源码文本里解析扫描面声明（可选先剥注释）。
    /// 判据对真实文件和对抗夹具走同一个函数 ⇒ "剥注释"这道保护是被量过的，不是写在注释里的愿望。
    /// </summary>
    static HashSet<(string Type, string Prop)> ParseSweepSurface(string csharpText, bool strip = true)
        => Regex.Matches(strip ? StripComments(csharpText) : csharpText,
                @"(?m)^\s*\(typeof\(([\w.]+)\)\s*,\s*(?:nameof\((?:\w+)\.(\w+)\)|""([^""\n]+)"")")
            .Select(m => (m.Groups[1].Value.Split('.').Last(),
                          m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value))
            // 附加属性（带点名字）沿基类链注册，运行时任何 FrameworkElement 都扫得到
            // ⇒ XAML 侧与声明侧一起收口到上限名 FrameworkElement。
            .Select(s => s.Item2.Contains('.') ? ("FrameworkElement", s.Item2) : s)
            .ToHashSet();

    /// <summary>
    /// 一个 .xaml.cs 是否真的挂了扫描钩子：注释里提一句 <c>HookLocaleSweep</c> 不算。
    /// 判据里那条"每个 Window 都挂钩子"和变异自检都走这里，保证两者口径一致。
    /// </summary>
    static bool CallsLocaleSweep(string sourceText)
        => StripComments(sourceText).Contains("HookLocaleSweep");

    /// <summary>XAML 文字类属性（含 ToolTipService.ToolTip 这种带点前缀的附加属性）。</summary>
    const string XamlTextAttrPattern =
        @"\b(ToolTipService\.ToolTip|Text|Content|Header|PlaceholderText)=""([^""\n]*)""";

    /// <summary>剥掉行注释与块注释 —— 钩子判据不能被一句写在注释里的"HookLocaleSweep"喂绿。</summary>
    static string StripComments(string src)
        => Regex.Replace(src, @"(?://[^\n]*)|/\*.*?\*/", "",
             System.Text.RegularExpressions.RegexOptions.Singleline);



    /// <summary>
    /// JXL 的"目标标签 → cjxl <c>color_space</c>"映射表（2026-09-22，交接 §44）。
    /// <para>为什么要单独一张表测：产物字节是否真的按声明变，由
    /// <c>scripts/precision/jxl_color_space_discrimination.py</c> 在真机 cjxl 上证；
    /// 而**我们的标签该落到哪个枚举**是纯映射，cjxl 测不到它写错。两边各管一半，缺一个都有洞。</para>
    /// </summary>
    static void Test_Jxl_ColorSpaceMapping()
    {
        string F(string? t) => TrueToneCap.Core.Encoding.NativeJxlEncoder.JxlColorSpaceValue(t);
        Assert("sRGB ⇒ sRGB", F("sRGB") == "sRGB");
        Assert("System / null / 空串一律兜 sRGB（兜『不传参』会让 cjxl 去猜无元数据的中转文件）",
            F("System") == "sRGB" && F(null) == "sRGB" && F("") == "sRGB");
        Assert("★ BT2020 ⇒ RGB_D65_202_Rel_SRG（旧实现在这里回落成 sRGB = 主动写错，不是没写）",
            F("BT2020") == "RGB_D65_202_Rel_SRG");
        Assert("DisplayP3 / DCI_P3 ⇒ DisplayP3",
            F("DisplayP3") == "DisplayP3" && F("DCI_P3") == "DisplayP3");
        // 2026-09-27（用户口径）：AdobeRGB 一律收口进 Rec.2020。旧断言要求它兜成 sRGB，
        // 那是"同一枚用户选择在界面/HDR/JXL 三处得到三种产物"里 JXL 这一份反方向的口径
        //（理由"libjxl 归到最近枚举并丢弃 profile"已被 §56.4 实测推翻）。
        Assert("★ AdobeRGB ⇒ 与 BT2020 同值（收口进 Rec.2020，绝不许兜成 sRGB = 把广色域报成窄色域）",
            F("AdobeRGB") == F("BT2020") && F("AdobeRGB") == "RGB_D65_202_Rel_SRG",
            $"AdobeRGB→{F("AdobeRGB")}");
        Assert("没见过的标签兜 sRGB 而不是抛异常", F("SomethingElse") == "sRGB");

        // 交叉耦合：映射表产出的每个值，都必须在判别脚本真机验过的那批里。
        // 缺了这条，将来往表里加一个新枚举时"单测全绿 + 判别脚本照旧绿"= 假绿灯：
        // cjxl 对无元数据输入是 fail-closed（拼错的值直接不出图），线上才会炸。
        var judged = new[] { "sRGB", "DisplayP3", "RGB_D65_202_Rel_SRG" };
        var produced = new[] { null, "", "System", "sRGB", "BT2020", "DisplayP3", "DCI_P3", "AdobeRGB" }
            .Select(F).Distinct().ToArray();
        var unjudged = produced.Where(v => !judged.Contains(v)).ToArray();
        Assert("映射表产出的每个 color_space 值都被真机判别脚本覆盖",
            unjudged.Length == 0, unjudged.Length == 0 ? "" : $"未覆盖：{string.Join(",", unjudged)}");

        // ICC 优先这条不许被"两个都给"取代（libjxl 里 ICC 覆盖 color fields，两个同时给就是自相矛盾）
        var jxlSrc = File.ReadAllText(FindRepoFile(
            Path.Combine("src", "TrueToneCap.Core", "Encoding", "NativeJxlEncoder.cs"))!);
        var code = string.Join("\n", jxlSrc.Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert("有可嵌 ICC 时只给 icc_pathname，不再叠 color_space",
            System.Text.RegularExpressions.Regex.Matches(code, @"colorArg = \$?""-x icc_pathname").Count == 2
            && code.Contains("-x color_space={JxlColorSpaceValue(colorSpaceTag)}"));

        // 2026-09-26 实测（cjxl v0.11.2）：`-x icc_pathname=… -x color_space=Rec2100PQ` 无论先后，
        // 产物都只剩 ICC 的声明（PQ 被丢弃）⇒ "两个都给"不是"冗余"，而是【静默丢掉一半声明】。
        // HDR 分支原先就这么写着（那一格 color_space 是死参数），现已改成二选一。这条锁住回退。
        var bothArgs = code.Split('\n')
            .Where(l => l.Contains("Arguments =") && l.Contains("iccArg") && l.Contains("color_space"))
            .Select(l => { var s = l.Trim(); return s.Substring(0, Math.Min(70, s.Length)); }).ToList();
        Assert("任何一条 cjxl 命令行都不许同时出现 icc_pathname 与 color_space（同时给 = ICC 赢、PQ 消失）",
            bothArgs.Count == 0, string.Join(" | ", bothArgs));

        // 色度值域必须与界面下拉【逐项相等】：共享白名单放行 422、而 JXL 下拉只有 444/420，
        // 一旦盘上出现 422，按 Tag 选中就落空 ⇒ 下拉空白 + 下次保存被静默兜底（用户报的同族症状）。
        var jxlCbo = System.Text.RegularExpressions.Regex.Match(
            File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"))!),
            @"ChromaJpegXlCbo[\s\S]{0,400}?</ComboBox>");
        var uiChroma = System.Text.RegularExpressions.Regex.Matches(jxlCbo.Value, @"Tag=""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert("JXL 色度：Core 的值域 = 界面下拉的项（谁改了另一边就红，杜绝「值合法但界面没得选」）",
            uiChroma.Length == TrueToneCap.Core.Encoding.NativeJxlEncoder.ChromaOptions.Length
            && uiChroma.Zip(TrueToneCap.Core.Encoding.NativeJxlEncoder.ChromaOptions, (a, b) => a == b).All(x => x),
            $"界面 [{string.Join(",", uiChroma)}] vs Core [{string.Join(",", TrueToneCap.Core.Encoding.NativeJxlEncoder.ChromaOptions)}]");
        Assert("JXL 色度归一化：422 回默认 444（不冒充 4:2:0，也不留空让下拉落空）",
            TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma("422") == "444"
            && TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma("420") == "420"
            && TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma(null) == "444"
            && TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma("  444  ") == "444",
            $"422→{TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma("422")}");

        // 卡片文案：被实测推翻的机制解释不许回来，且必须说清"有损产物像素在 XYB"。
        var jxlDecl = FormatColorFacts.Declaration(OutputFormat.JPEG_XL, "BT2020", false);
        Assert("JXL 声明文案：写「XYB」、且不写已被推翻的「归到最近的枚举」（实测：Custom 原色精确保留 / 整份 ICC 内嵌）",
            jxlDecl.Contains("XYB") && !jxlDecl.Contains("归到最近的枚举"), jxlDecl);
    }


    /// <summary>
    /// 哨兵：App 层高精度源探针的【判据本身】不许再退化成恒真/恒跳。
    /// <para>2026-09-19 该探针抓到两个自身缺陷 —— 都不是管线缺陷，且都无法靠"真机跑一次看看"发现：
    /// · 中性像素计数从未 <c>++</c> ⇒ <c>NeutralN</c> 恒 0 ⇒ 第一道样本量闸门恒成立 ⇒ 探针在任何机器上最多只能 SKIP；
    /// · 整格容差 0.6 LSB8 &gt; 0.5 ⇒ <c>|q − round(q)| ≤ 0.5</c> 恒成立 ⇒ 判据 (b) 恒为 100%，
    ///   真机上一句"HDR float 帧整格 100.00%"看着像证据，其实是恒真式在自证。</para>
    /// <para>判据的合成帧自检（<c>RunHdrJudgeCheck</c>：256 级台阶帧必须 FAIL、1024 级必须 PASS）
    /// 已经在探针里测出这两条，但那条只在真机、且屏有高精度能力时才跑 ⇒ 这里用源码形态把同一件事
    /// 钉在每次门禁上（与"按字面规则改错"那几条同型的防退化）。</para>
    /// </summary>
    static void Test_HdrProbeJudge_NotDead()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SelfTest.cs"));
        if (p is null) { Skip("探针判据哨兵", "未找到 SelfTest.cs"); return; }
        // 只比代码行：本文件里"旧写法是 `const float GridTolLsb = 0.6f`"这类说明注释会被原文命中
        var code = string.Join("\n", File.ReadAllLines(p).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert("中性像素计数真的在累加（缺了它探针恒 SKIP）", code.Contains("neutral++;"));

        var m = Regex.Match(code, @"const float GridTolLsb\s*=\s*([0-9.]+)f?\s*;");
        Assert("整格容差仍是一个可解析的字面量", m.Success);
        if (m.Success)
        {
            float tol = float.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert($"整格容差 {tol} 必须落在 (0, 0.5) LSB8 内（≥0.5 判据恒真，≤0 判据恒假）",
                tol > 0f && tol < 0.5f);
        }

        Assert("合成帧自检被 Run 调用且计入报告（只定义不调用等于没有）",
            code.Contains("RunHdrJudgeCheck(outDir)") && code.Contains("\"HDR_JUDGE\""));
        Assert("自检与真机探针走同一条判定分支（不另写第二份判据）",
            CountOfStatic(code, "JudgeHdrFloat(") >= 3,
            "≥3 处：真机探针 1 次 + 自检 2 次");
    }

    /// <summary>
    /// 批次 8（2026-09-20）：NVENC 的色彩描述必须"下发给驱动的"与"写进容器的"同出一个值。
    /// <para>背景：RGB→YUV 是驱动自己做的，此前这个字段从不下发 ⇒ 驱动恒按默认 BT.601 转换，
    /// 而容器 colr 在另一处自行推断（先恒写 709，后改恒写 601）。同日把 matrixCoefficients
    /// 逐个码点扫过一遍（<c>--nvenc-color-probe sweep</c>）：<b>驱动按下发的 H.273 号换矩阵</b>
    /// （1→BT.709 误差≤1.2、9→BT.2020 NCL 误差≤1.0、0 被驱动直接拒绝），于是硬件路既能做
    /// 2020 转换又不用把软件路降级 —— 前提是"只有一个值"，本测试钉的就是不许出现第二个事实源。</para>
    /// <para>②③ 是文本断言（结构约束），①④ 是纯函数真值表。文本断言只匹配代码形态（先过 CodeOnly）。</para>
    /// </summary>
    static void Test_NvencColorIntent_SharedByEncoderAndContainer()
    {
        var fePath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"));
        var nvPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "NvEncoderNative.cs"));
        if (fePath is null || nvPath is null)
        {
            Skip("NVENC 色彩意图同源", "未找到 FormatEncoders.cs / NvEncoderNative.cs");
            return;
        }
        var fe = CodeOnly(fePath);
        var nv = CodeOnly(nvPath);

        // ── ① 纯函数真值表：三档产品配置各自该下发/声明什么 ──
        foreach (var (hdr, tag, p, t, m) in new[]
        {
            (false, "sRGB",   FormatHelper.CicpPrimariesBt709,  FormatHelper.CicpTransferSrgb, FormatHelper.CicpMatrixBt709),
            (false, "BT2020", FormatHelper.CicpPrimariesBt2020, FormatHelper.CicpTransferSrgb, FormatHelper.CicpMatrixBt2020Ncl),
            (true,  "BT2020", FormatHelper.CicpPrimariesBt2020, FormatHelper.CicpTransferPq,   FormatHelper.CicpMatrixBt2020Ncl),
            (false, "System", FormatHelper.CicpPrimariesBt709,  FormatHelper.CicpTransferSrgb, FormatHelper.CicpMatrixBt709),
        })
        {
            var c = FormatHelper.NvencAvifCicp(hdr, tag);
            Assert($"NvencAvifCicp(hdr={hdr}, {tag}) = {p}/{t}/{m}",
                c is not null && c.Value.Primaries == p && c.Value.Transfer == t && c.Value.Matrix == m,
                c is null ? "null" : $"{c.Value.Primaries}/{c.Value.Transfer}/{c.Value.Matrix}");
        }
        Assert("给不出自洽声明的色域 ⇒ null（AdobeRGB：既不下发码点也不写 nclx）",
            FormatHelper.NvencAvifCicp(false, "AdobeRGB") is null);
        Assert("摆幅与驱动侧同源（容器不许另取一个值）",
            FormatHelper.NvencAvifCicp(false, "sRGB")!.Value.FullRange == NvEncoderNative.NvEncWantsFullRange);
        // matrix 只由三原色决定：这条同时管住"HDR 一律 9"的旧写法回潮
        Assert("matrix 按三原色取，不看 SDR/HDR",
            FormatHelper.AvifMatrixCoefficients(FormatHelper.CicpPrimariesBt2020) == FormatHelper.CicpMatrixBt2020Ncl
            && FormatHelper.AvifMatrixCoefficients(FormatHelper.CicpPrimariesBt709) == FormatHelper.CicpMatrixBt709);

        // ── ② 每个 AV1 会话都收到色彩意图；每个硬件产物写入点都带上同一个值 ──
        Assert("AV1 会话不再以「无色彩意图」的旧形态创建",
            CountOfStatic(fe, "new NvEncoderNative(device!)") == 0
            && CountOfStatic(fe, "new NvEncoderNative(nvDevice)") == 0,
            "残留不带 hwCicp 的 AV1 会话 ⇒ 驱动会按下发前的默认矩阵转换，而容器声明的是另一张");
        Assert("会话与容器写入都成对带上 hwCicp",
            CountOfStatic(fe, "new NvEncoderNative(device!, hwCicp)") >= 3
            && CountOfStatic(fe, "new NvEncoderNative(nvDevice, hwCicp)") >= 2
            && CountOfStatic(fe, "hwCicp: hwCicp") >= 5);

        // ── ③ 容器不许在硬件分支上另推一张矩阵；MFT 那处例外（它没有下发环节）──
        Assert("WriteAvif 收到 hwCicp 时照抄、不自行推断", nv.Contains("if (hwCicp is { } hw)"));
        Assert("色彩字段只在 ApplyAv1ColorConfig 一处下发给驱动",
            CountOfStatic(nv, "Av1MatrixCoefficients") == 2,   // 常量声明 + 唯一写入
            $"实际 {CountOfStatic(nv, "Av1MatrixCoefficients")} 处");
        // MFT/QSV 那处产物写入必须保持"不带 hwCicp"：它的像素是我们自己转的，
        // 若图省事也传一个 hwCicp 进去，就等于宣称"驱动照我们下发的矩阵转过"——那是假话。
        Assert("全仓只剩 MFT/QSV 一处产物写入不带 hwCicp",
            CountOfStatic(fe, "IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag, iccProfile);") == 1,
            "唯一一处应属 MftAvifEncode ⇒ ≠1 说明要么硬件路漏了 hwCicp，要么 MFT 被误加上");
    }

    static int CountOfStatic(string text, string needle)
    {
        int c = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + 1, StringComparison.Ordinal)) c++;
        return c;
    }

    /// <summary>只取代码行（丢掉以 // 开头的行），防止注释里的旧写法被文本断言误命中。</summary>
    static string CodeOnly(string path) => string.Join("\n",
        File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    /// <summary>
    /// D-3（§30.9 的延伸）：HDR 声明路径必须先把"H.273 无法忠实表达"的色域归一掉。
    /// <para>背景：SDR 侧那道 <c>BuildSdrCicp("AdobeRGB") ⇒ null</c> 的守卫管住了 PNG cICP / AVIF colr /
    /// avifenc --cicp，但 HDR 分支（PQ）根本不问它 ⇒ 像素走完 Adobe 矩阵，声明却写成 primaries=1(BT.709)
    /// 或 BT.2020 —— 三处（PNG P1b / AVIF H1-Adobe / JXL J4）同根。
    /// ⚠ 这句里的"HDR 一律不嵌 ICC"到 2026-09-27 已不成立：JXL 的 P3/DCI_P3 目标改递【带 PQ 曲线的】
    /// ICC（交接 §62 —— 旧的那份 SDR TRC ICC 会把 PQ 挤掉，因为 ICC 压过 color fields）。
    /// 归一这一件事不受影响：Adobe 仍收口成 BT2020，下面两条判据照旧判它。</para>
    /// <para>这里钉两件事：①归一发生在<b>矩阵之前</b>且是就地改写（像素与声明必须同源，不能一个搬了一个没搬）；
    /// ②改写必须留痕（用户显式选的色域没生效，静默等于骗人）。TIFF 故意不在其列：它靠 ICC 声明，
    /// "Adobe 原色 + PQ TRC"的 profile 合法存在，归一反而把用户要的色域裁窄。</para>
    /// </summary>
    static void Test_HdrAdobeTag_NormalizedBeforeMatrix()
    {
        foreach (var (tag, expect) in new[]
        {
            ("AdobeRGB", "BT2020"), ("BT2020", "BT2020"), ("DisplayP3", "DisplayP3"),
            ("DCI_P3", "DCI_P3"), ("sRGB", "sRGB"),
        })
        {
            var s = new EncodingSettings { Format = OutputFormat.AVIF, HdrOutput = true, ColorSpaceTag = tag };
            EncoderFallbackNotifier.LastReason = null;
            var got = FormatHelper.HdrDeclarableTag(s);

            Assert($"HDR 场景 tag {tag} → {got}（期望 {expect}）", got == expect, $"实得 {got}");
            Assert($"{tag}: 就地改写 s.ColorSpaceTag（矩阵与声明同取一处）", s.ColorSpaceTag == got,
                $"s.ColorSpaceTag={s.ColorSpaceTag} 返回值={got}");
            bool mustNote = tag == "AdobeRGB";
            bool noted = !string.IsNullOrEmpty(EncoderFallbackNotifier.LastReason);
            Assert($"{tag}: {(mustNote ? "必须" : "不该")}留痕", noted == mustNote,
                noted ? EncoderFallbackNotifier.LastReason : "无");
        }

        // 归一只管 AdobeRGB：其余标签（含产品路径上游才会出现的 "System"）一律原样返回
        var sys = new EncodingSettings { ColorSpaceTag = "System" };
        Assert("System 原样返回（归一不扩大适用范围）",
            FormatHelper.HdrDeclarableTag(sys) == "System" && sys.ColorSpaceTag == "System");
    }

    /// <summary>
    /// D-2 哨兵：MainWindow 不许再把"源精度事实（有没有 Float16 帧）"当"编码场景（要不要 HDR 直通）"传。
    /// <para>§4.3 修正后 <c>PreferHdr</c> 恒为 true ⇒ 能力屏上 <c>actualHdr</c> 恒真，于是
    /// <c>BuildEncodingSettings(format, actualHdr, …)</c> 让 HDR 关掉的截图也走 HDR 协商：
    /// AVIF 容器位深被无条件钉成 10（用户的 8/12 不可达），QSV 还会为非 HDR 截图弹"不支持 HDR"提示。
    /// 测试工程只引用 Core（App 层拿不到），故沿用"探针判据哨兵"的源码形态断言：
    /// 只比代码行，注释里复述的旧写法不算命中。</para>
    /// </summary>
    static void Test_SourcePrecisionNotUsedAsScene()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (p is null) { Skip("编码场景哨兵", "未找到 MainWindow.xaml.cs"); return; }
        var code = CodeOnly(p);

        Assert("没有把 actualHdr 当编码场景传进 BuildEncodingSettings",
            !code.Contains("BuildEncodingSettings(format, actualHdr"));
        int sceneCalls = CountOfStatic(code, "BuildEncodingSettings(format, hdrScenario");
        Assert($"Float16 三处入口都改传 hdrScenario（实得 {sceneCalls}）", sceneCalls >= 3,
            "无感截图 / 选区 HDR / OnCaptureNow 各一处，少一处就是漏改");
        Assert("hdrScenario 仍保留 GainMap 例外（增益图要有原始 HDR 帧）",
            code.Contains("hdrOutput || format == OutputFormat.JPEG_GAINMAP"));
        Assert("S11 位深策略未回退成补偿式写法（不再手动置 HdrOutput=false）",
            !code.Contains("settings.HdrOutput = false;"));
    }

    /// <summary>
    /// D-5 + §31.3-2 哨兵：子进程的<b>超时必须可达</b> —— 全仓不许"先同步 ReadToEnd 再 WaitForExit"。
    /// <para>同一条链上有两个缺陷，第二个盖住了第一个：</para>
    /// <para>① 编码通路把 <c>proc.WaitForExit(ms)</c> 的返回值丢掉 ⇒ 超时后读 ExitCode 抛"进程状态非法"，
    /// 掩盖根因、子进程留在后台吃 CPU。查出的现场：cjxl ×3、cjpegli ×2、avifenc ×2。</para>
    /// <para>② 就算判了返回值也白判：<c>ReadToEnd()</c> 同步阻塞，子进程只要不退出就不关管道写端，
    /// 于是这句<b>永不返回</b>，后面那句 <c>WaitForExit</c> 根本执行不到 —— 5s/30s 全是摆设。
    /// 探测通路 6 处（<c>IsAvailable</c> 与 PATH 回退）+ 编码通路 8 处同一个写法。
    /// ⇒ 现统一走 <c>NativeProcess.DrainProbe</c> / <c>DrainAndFinish</c>：两条管道同时挂异步读，
    /// 排空与等待并行，既不因管道满而死锁，也不因进程不退出而丢超时。</para>
    /// <para>⚠ 本条只钉形态；"真的挂住时超时到底可不可达"由下面
    /// <see cref="Test_NativeProbeTimeoutIsReachable"/> 用会挂死的子进程实测，两者不可互替。</para>
    /// </summary>
    static void Test_NativeEncodeTimeoutIsJudged()
    {
        var files = new[] { "NativeAvifEncoder.cs", "NativeJxlEncoder.cs", "JpegLiNative.cs", "NativeWebPEncoder.cs" };
        int drain = 0, awaitedWait = 0;
        foreach (var f in files)
        {
            var p = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", f));
            if (p is null) { Skip($"子进程超时哨兵 {f}", "文件不存在"); continue; }
            var code = CodeOnly(p);

            Assert($"{f}: 没有同步 ReadToEnd() 挡在超时判据前面", !code.Contains(".ReadToEnd()"),
                "同步读满管道 ⇒ 子进程不退出时永不返回，超时形同虚设；须走 NativeProcess.Drain*");
            Assert($"{f}: 探测档字面量已收口（无 WaitForExit(5000)）", !code.Contains("WaitForExit(5000"));

            bool bare = code.Split('\n').Select(l => l.Trim()).Any(l =>
                l.StartsWith("proc.WaitForExit(", StringComparison.Ordinal));
            Assert($"{f}: 没有丢弃返回值的 WaitForExit", !bare,
                "存在裸 proc.WaitForExit(ms) ⇒ 超时后读 ExitCode 的错类型掩盖根因，且子进程留存");

            drain += CountOfStatic(code, "NativeProcess.DrainProbe(");
            drain += CountOfStatic(code, "NativeProcess.DrainAndFinish(");
            awaitedWait += CountOfStatic(code, "if (!proc.WaitForExit(");
        }
        Assert($"探测 6 处 + 编码 8 处都走 Drain*（实得 {drain}）", drain >= 14,
            "avifenc/jxl/jpegli/webp 的 IsAvailable 与 PATH 回退共 6 处，编码等待共 8 处");
        Assert($"保留手写等待的只剩「喂 stdin 那一处」（实得 {awaitedWait}）", awaitedWait == 1,
            "那一处必须先起异步读再往 stdin 写 PNG，Drain* 不适用；多出来说明有地方绕过了统一层");
    }

    /// <summary>
    /// §31.3-2 的<b>行为</b>判据：拿一个真会挂死的子进程量 <c>NativeProcess.DrainProbe</c> 的返回时间。
    /// <para>为什么必须有这条而不是只信上面的形态检查：形态钉子读的是源码文本，把
    /// <c>DrainProbe</c> 内部改回"先 <c>WaitForExit</c> 再 <c>ReadToEnd</c>"（一个看起来很合理的修法）
    /// 它照样全绿，而探测通路会重新变成"编码器一挂，整个截图链一起挂"。</para>
    /// <para>两个方向各造一个夹具，缺一侧就抓不到对应那类错：
    /// ① <b>挂死</b>：子进程继承两条管道、不写也不退出 ⇒ 必须【在超时档返回 false】，
    ///    旧写法和"wait 后 read"的新写法都会挂满整个 sleep；
    /// ② <b>管道满</b>：子进程一次性写 2 MB 再退出（> 命名管道缓冲区）⇒ 必须【全部读到且退出码为 0】，
    ///    这条专治"只把 WaitForExit 提前"的修法 —— 那样会死锁，且永远不会返回超时。</para>
    /// <para>不依赖环境：只用 cmd.exe 内建命令，不碰任何编码器。</para>
    /// </summary>
    static void Test_NativeProbeTimeoutIsReachable()
    {
        static System.Diagnostics.Process Start(string args)
            => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;

        // ① 挂死：ping 占住继承的管道句柄 30 秒，探测档只给 1.5 秒
        {
            using var proc = Start("/c ping -n 30 127.0.0.1 >nul");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool probed = TrueToneCap.Core.Encoding.NativeProcess.DrainProbe(
                proc, 1_500, out int ec, out string so, out string se);
            sw.Stop();
            Assert($"挂死的子进程被超时切断（{sw.ElapsedMilliseconds}ms，未挂满 30s）",
                !probed && sw.ElapsedMilliseconds < 8_000,
                "超时不可达 ⇒ 一个不退出也不关管道的编码器会把整条截图链拖死，5s 档形同虚设");
            Assert("超时时不读 ExitCode（返回 -1、文本为空，而不是抛\"进程状态非法\"）",
                ec == -1 && so.Length == 0 && se.Length == 0);
            Assert("超时后子进程被终止，不留后台", proc.HasExited);
        }

        // ② 管道满：2 MB stdout 远超命名管道缓冲区，必须先起异步读才不死锁
        {
            var big = Path.Combine(Path.GetTempPath(), $"ttc_pipe_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(big, new string('x', 2_000_000) + "\n");
                using var proc = Start($"/c type \"{big}\"");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool probed = TrueToneCap.Core.Encoding.NativeProcess.DrainProbe(
                    proc, 20_000, out int ec, out string so, out _);
                sw.Stop();
                Assert($"管道满不死锁：2 MB stdout 全读到（实得 {so.Length}B / {sw.ElapsedMilliseconds}ms）",
                    probed && ec == 0 && so.Length > 1_500_000,
                    "先 WaitForExit 再读管道 = 子进程写满缓冲区后阻塞、父进程等退出 ⇒ 死锁");
            }
            finally { try { File.Delete(big); } catch { } }
        }
    }

    /// <summary>
    /// §31.3-9：ICC 的"能不能声明"必须是<b>结构</b>判据，不是长度判据。
    /// <para>三台测量（<c>scripts/probes/icc_min_size_probe.py</c>，构造 / 现网语料 / LittleCMS 裁判）
    /// 给出的是<b>彼此矛盾</b>的两个方向，所以任何单一字节数都无解：</para>
    /// <para>① <b>396 B</b> 的 v2 matrix-RGB profile（必需标签齐、长度字段自洽、LittleCMS 认）
    /// 会被旧的 <c>Length &gt; 400</c> 判死 ⇒ 过严；
    /// ② <b>500 B</b> 的"壳"（长度自洽、LittleCMS 也认，但没有 rXYZ/gXYZ/bXYZ）反而能过 400 ⇒ 过松；
    /// ③ 本机现网 <c>RSWOP.icm</c> 是 218 103 B 的 <b>CMYK</b> 打印 profile，长度阈值再高也拦不住它
    ///    被嵌进一张 RGB 截图。⇒ 于是判据改问：长度字段自洽 + <c>acsp</c> + 色彩空间 <c>RGB </c>
    ///    + rXYZ/gXYZ/bXYZ/wtpt 都是界内的 <c>XYZ </c> 数据 + 三条 TRC 在。</para>
    /// <para>夹具由那条探针 <c>--emit-dir</c> 写出（同一段字节构造不在两种语言里各写一遍）。
    /// 第 6 项是这里最硬的一条：同一长度（396 B）、相反结论 —— 拿"真模板的前 396 字节"与
    /// "构造的最小合法 profile"对打，任何只看长度的实现都不可能同时给出这两个答案。</para>
    /// </summary>
    static void Test_IccEmbeddableIsStructuralNotLength()
    {
        byte[]? Load(string rel)
        {
            var p = FindRepoFile(Path.Combine("tests", "fixtures", rel));
            return p is null ? null : File.ReadAllBytes(p);
        }
        var legal = Load("icc_minimal_legal.icc");
        var shell = Load("icc_shell_no_primaries.icc");
        if (legal is null || shell is null)
        {
            // 夹具是**随仓库走**的二进制件 ⇒ 缺了就是检出/提交漏了东西，判红而不是 Skip。
            // （Skip 会藏在"跳过 1"里，而这套门禁已经有别的合法 Skip —— 那正好是让静默降级
            //   溜过去的形状。见 verify.ps1 的"零断言不许当通过"同一条纪律。）
            Assert("ICC 结构判据的夹具存在（tests/fixtures/icc_*.icc）", false,
                "缺 tests/fixtures/icc_minimal_legal.icc 或 icc_shell_no_primaries.icc ⇒ "
                + "补进版本控制，或跑 py scripts/probes/icc_min_size_probe.py --emit-dir tests/fixtures 重生成");
            return;
        }
        Assert($"夹具本身自洽（最小合法 {legal.Length} B / 壳 {shell.Length} B）",
            legal.Length < shell.Length, "夹具被改动过 ⇒ 下面两条反向断言失去意义");

        // ① 我们自己的标准模板必须全过（否则兜底路径整体失效）
        foreach (var (name, icc) in new (string, byte[]?)[]
        {
            ("GetDefaultSRgbIcc", ColorProfileProvider.GetDefaultSRgbIcc()),
            ("std sRGB", ColorProfileProvider.GetStandardIccProfile("sRGB")),
            ("std DisplayP3", ColorProfileProvider.GetStandardIccProfile("DisplayP3")),
            ("std BT2020", ColorProfileProvider.GetStandardIccProfile("BT2020")),
            ("std AdobeRGB", ColorProfileProvider.GetStandardIccProfile("AdobeRGB")),
            ("HDR std BT2020", ColorProfileProvider.GetHdrStandardIccProfile("BT2020")),
        })
            Assert($"标准模板 {name} 过结构判据",
                FormatHelper.IsEmbeddableIcc(icc),
                "自家兜底 profile 都不合格 ⇒ 所有'降级到标准 ICC'的分支都在往容器里塞 null");

        // ②③ 两个方向的反例：短的合法件要收，长的壳要拒
        Assert($"① {legal.Length} B 的最小合法 profile 被收（旧阈值 400 会判死它）",
            FormatHelper.IsEmbeddableIcc(legal));
        Assert($"② {shell.Length} B 的『无 primaries 壳』被拒（旧阈值 400 会放行它）",
            !FormatHelper.IsEmbeddableIcc(shell));

        // ④ 截断 / 尾部拼接：长度字段与自身不符一律拒，哪怕仍然很长
        var srgb = ColorProfileProvider.GetDefaultSRgbIcc();
        var truncated = new byte[srgb.Length - 1];
        Array.Copy(srgb, truncated, truncated.Length);
        Assert($"④ 真模板截掉 1 字节（{truncated.Length} B）⇒ 拒", !FormatHelper.IsEmbeddableIcc(truncated),
            "截断 profile 的标签表还在、字节数还大，只看长度就会放过去");
        var appended = new byte[srgb.Length + 4];
        Array.Copy(srgb, appended, srgb.Length);
        Assert($"④ 真模板尾部多 4 字节 ⇒ 拒（长度字段自洽性优先于大小）",
            !FormatHelper.IsEmbeddableIcc(appended));

        // ⑤ 色彩空间不是 RGB 就一律不算（③ 那台 CMYK 打印 profile 的形状）
        var cmyk = (byte[])srgb.Clone();
        cmyk[16] = (byte)'C'; cmyk[17] = (byte)'M'; cmyk[18] = (byte)'Y'; cmyk[19] = (byte)'K';
        Assert($"⑤ {cmyk.Length} B 的 CMYK profile ⇒ 拒（旧阈值只看长度，218 KB 也拦不住）",
            !FormatHelper.IsEmbeddableIcc(cmyk));

        // ⑥ 同长度、反结论：这是"判据不是长度"这条主张的直接证明
        var sameLen = new byte[legal.Length];
        Array.Copy(srgb, sameLen, legal.Length);
        Assert($"⑥ 同为 {legal.Length} B：构造的最小合法件收、真模板的前 {legal.Length} 字节拒",
            FormatHelper.IsEmbeddableIcc(legal) && !FormatHelper.IsEmbeddableIcc(sameLen),
            "两个字节数相同而结论相反 ⇒ 任何只看 Length 的实现都不可能同时对 ⇒ 判据必须是结构性的");

        // ⑦ 形态：三套字面量不许回来（Core/App 两侧都查）
        foreach (var rel in new[]
        {
            "src/TrueToneCap.Core/Encoding/FormatEncoders.cs",
            "src/TrueToneCap.Core/Encoding/NativeAvifEncoder.cs",
            "src/TrueToneCap.Core/Encoding/NativeJxlEncoder.cs",
            "src/TrueToneCap.Core/Encoding/ManagedPngEncoder.cs",
            "src/TrueToneCap.Core/Encoding/ManagedTiffEncoder.cs",
            "src/TrueToneCap.Core/Encoding/JpegLiNative.cs",
            "src/TrueToneCap.Core/Encoding/NativeWebPEncoder.cs",
            "src/TrueToneCap.Core/Encoding/NvEncoderNative.cs",
            "src/TrueToneCap.Core/ColorManagement/ColorProfileProvider.cs",
            "src/TrueToneCap.App/Services/CapturePipelineService.cs",
        })
        {
            var p = FindRepoFile(rel);
            if (p is null) { Skip($"ICC 阈值收口 {Path.GetFileName(rel)}", "文件不存在"); continue; }
            var code = CodeOnly(p);
            Assert($"{Path.GetFileName(rel)}: 不再有 ICC 长度字面量/旧常量",
                !code.Contains("MinUsableIccBytes") && !code.Contains("MinEmbeddableIccBytes")
                && !code.Contains("Length: > 128") && !code.Contains("Length > 128")
                && !code.Contains("Length: > 400") && !code.Contains("Length > 400")
                && !code.Contains("Length > 500") && !code.Contains("Length >= 500")
                && !code.Contains("iccProfile is { Length: > 0 }"),
                "§31.3-9 的结论是「长度不是那把尺子」⇒ 任何一处重新按字节数决定 ICC 能不能嵌，都要给出为什么"
                + "（变异实验 M3 就是把判据换回 icc.Length > 400 跑红的，形态钉子当时拦不住那种写法，故补上）");
        }

        // ⑧ 末端写入器也只问同一个判据 —— 这一条管的是"绕过了上层编排、直接调写入器"的形状：
        //    PNG 的 iCCP 是它唯一的色域声明通道，塞一份声明不了 RGB 的壳进去比不塞更坏。
        static bool PngHasIccp(byte[] png)
        {
            for (int pos = 8; pos + 8 <= png.Length;)
            {
                int len = (png[pos] << 24) | (png[pos + 1] << 16) | (png[pos + 2] << 8) | png[pos + 3];
                var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "iCCP") return true;
                if (type == "IEND" || len < 0) return false;
                pos += 12 + len;
            }
            return false;
        }
        var px = new byte[4 * 4 * 4];
        {
            using var msShell = new MemoryStream();
            ManagedPngEncoder.EncodeFast(px, 4, 4, msShell, shell);
            Assert("⑧ PNG 写入器拒收『无 primaries 的 ICC 壳』⇒ 容器里没有 iCCP",
                !PngHasIccp(msShell.ToArray()),
                "写入器若还留着 Length > 0 这种局部弱判据，上游收紧也挡不住直接调用");
            using var msLegal = new MemoryStream();
            ManagedPngEncoder.EncodeFast(px, 4, 4, msLegal, legal);
            Assert("⑧ PNG 写入器收下 396 B 的最小合法 profile ⇒ 有 iCCP",
                PngHasIccp(msLegal.ToArray()),
                "短小但结构完整的件被拒 = §31.3-9 那个 400 的老毛病换了个地方复发");
        }

        // ⑨ 手写兜底件：精简系统上它是唯一能用的 sRGB ICC，判据收紧后它若不合格，
        //    广色域声明就只在那类机器上静默消失 —— 本机永远跑不到这条分支，只能直接断言。
        var fallback = ColorProfileProvider.BuildValidSRgbIcc();
        Assert($"⑨ 手写兜底 sRGB ICC 过结构判据（{fallback.Length} B）",
            FormatHelper.IsEmbeddableIcc(fallback),
            "系统 sRGB 文件缺失/损坏时的最终回退件不合格 ⇒ 那些机器上所有 ICC 声明路整体失效");

        // ⑩ 烘焙产物（App 侧 settings.IccProfile 的真正来源）也必须过同一个判据
        var (bakedPx, bakedIcc) = ColorProfileProvider.BakeIccToTarget(px, 4, 4, fallback, "BT2020");
        Assert($"⑩ BakeIccToTarget 输出的目标 ICC 过结构判据（{(bakedIcc is null ? "null" : $"{bakedIcc.Length} B")}）",
            bakedIcc is null || FormatHelper.IsEmbeddableIcc(bakedIcc),
            "烘焙路给出的 ICC 是 App 唯一会往设置里塞的 ICC —— 它不合格就等于白烘");
    }

    /// <para>① 收口规则本身（Core 纯函数）：DisplayP3/DCI_P3/AdobeRGB/未知串 ⇒ BT2020，
    /// 且<b>幂等</b> —— 非幂等的迁移表在第二次加载时会把值搬去别处。</para>
    /// <para>② 产品路径解析出的标签<b>必须落在白名单里</b>：不假设本机面板是什么色域，
    /// 只断言"跑完 ACM 检测后不会漏出 P3/Adobe"，这条在任何机器上都成立。</para>
    /// <para>③ 持久化必须"旧表取标签 → 收口 → 新表找索引"，不能按新项数 clamp：
    /// <c>ColorSpaceIndex</c> 存的是索引，项数从 6 变 3 之后旧索引 2/3/4 会被读成
    /// sRGB/BT2020/sRGB —— 用户没碰过任何设置就换了输出色域，而保存时又把那个格子写回设置。
    /// 顺带钉掉 GainMap 的硬编码 <c>SelectedIndex = 5</c>。</para>
    /// </summary>
    static void Test_ColorSpaceOptions_NarrowedToTwo()
    {
        Assert("收口函数存在且公开",
            typeof(TrueToneCap.Core.ColorManagement.ColorProfileProvider)
                .GetMethod("CollapseSelectableTag") is not null);

        // ① 收口规则 + 幂等
        foreach (var (tag, want) in new[]
        {
            ("System", "System"), ("sRGB", "sRGB"), ("BT2020", "BT2020"),
            ("DisplayP3", "BT2020"), ("DCI_P3", "BT2020"), ("AdobeRGB", "BT2020"),
            ("", "System"), ("garbage", "BT2020"),
        })
        {
            var got = TrueToneCap.Core.ColorManagement.ColorProfileProvider.CollapseSelectableTag(tag);
            Assert($"收口 {tag} → {got}（期望 {want}）", got == want);
            Assert($"收口幂等：{got} 再过一次不变",
                TrueToneCap.Core.ColorManagement.ColorProfileProvider.CollapseSelectableTag(got) == got);
        }
        Assert("收口 null ⇒ System",
            TrueToneCap.Core.ColorManagement.ColorProfileProvider.CollapseSelectableTag(null) == "System");

        // ② 产品路径的解析结果必须在白名单内（不假设本机面板色域）
        var selectable = new[] { "System", "sRGB", "BT2020" };
        foreach (bool hdr in new[] { true, false })
            foreach (bool acm in new[] { true, false })
            {
                var hmon = TrueToneCap.Core.Capture.DisplayEnumerator.GetMonitorUnderCursor();
                var resolved = TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .ResolveColorSpaceTag("System", hdr, acm, hmon);
                Assert($"Resolve(\"System\", hdr={hdr}, acm={acm}) ⇒ {resolved} 在白名单内",
                    Array.IndexOf(selectable, resolved) >= 0,
                    "漏出 DisplayP3/AdobeRGB ⇒ 某条检测分支绕过了收口");
            }

        // ③a XAML 里色域项只剩三种子（这三个字符串是色彩空间专用，不会与格式下拉撞名）
        var xaml = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (xaml is null) { Skip("色域收口·界面与迁移", "未找到 MainWindow.xaml"); return; }
        var xamlText = File.ReadAllText(xaml);
        foreach (var gone in new[] { "DisplayP3", "DCI_P3", "AdobeRGB" })
            Assert($"界面下拉不再有 {gone} 项", !xamlText.Contains($"Tag=\"{gone}\""),
                "删项没删干净 ⇒ 用户仍能选到已废弃的目标");
        Assert("界面仍有 BT2020 项", xamlText.Contains("Tag=\"BT2020\""));

        // ③b 迁移按标签；③c 不留硬编码索引
        var settingsCs = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Settings.cs"));
        var mainCs = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (settingsCs is not null)
        {
            var code = CodeOnly(settingsCs);
            Assert("色域设置按【旧表取标签→收口→新表找索引】迁移",
                code.Contains("legacyTags") && code.Contains("CollapseSelectableTag"),
                "只按新项数 clamp 会让老配置的索引静默指向别的色域");
            Assert("已去掉裸 clamp 旧写法",
                !code.Contains("SelectedIndex = Math.Clamp(_settings.ColorSpaceIndex"));
        }
        if (mainCs is not null)
            Assert("GainMap 的色域锁定不再写死索引 5",
                !CodeOnly(mainCs).Contains("ColorCbo.SelectedIndex = 5"),
                "项序一改，硬编码索引就指到别的格子（还会被保存回设置）");
    }

    /// <summary>
    /// 实测哨兵：JXL 的色度档【必须真的改变产物】。
    /// <para>回归背景：审计发现同一样张在 4:4:4 / 4:2:0 下产出逐字节相同的 881 B 文件 ——
    /// 因为 <c>NativeJxlEncoder</c> 压根没有色度形参，设置里的下拉是纯装饰。现已接
    /// <c>--resampling</c>。这条用真编码器（cjxl）做端到端复验，与上一条的纯映射断言互补：
    /// 映射对了但没拼进命令行，只有这条会红。</para>
    /// </summary>
    static void Test_Jxl_ChromaActuallyChangesOutput()
    {
        if (!NativeJxlEncoder.IsAvailable) { Skip("JXL 色度实测", "cjxl.exe 不可用"); return; }

        const int W = 64, H = 64;
        var bgra = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            // 彩色斜纹：色度信息丰富 ⇒ 2x2 降采样一定会改变码流
            bgra[i * 4] = (byte)(i * 7);
            bgra[i * 4 + 1] = (byte)(255 - i * 3);
            bgra[i * 4 + 2] = (byte)(i * 11);
            bgra[i * 4 + 3] = 255;
        }

        var dir = Path.Combine(Path.GetTempPath(), "ttc_jxl_chroma_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var enc = EncoderFactory.Create(OutputFormat.JPEG_XL);
            var p444 = Path.Combine(dir, "a.jxl");
            var p420 = Path.Combine(dir, "b.jxl");
            Settings("444", p444);
            Settings("420", p420);

            if (!File.Exists(p444) || !File.Exists(p420))
            {
                Assert("JXL 色度实测: 两档都产出文件", false, "有档位没写出文件");
                return;
            }
            long s444 = new FileInfo(p444).Length, s420 = new FileInfo(p420).Length;
            Assert("JXL 色度实测: 两档产物非空", s444 > 0 && s420 > 0, $"{s444} B / {s420} B");
            Assert("JXL 色度实测: 444 与 420 产物必须不同（相同即说明开关又断了）",
                !File.ReadAllBytes(p444).AsSpan().SequenceEqual(File.ReadAllBytes(p420)),
                $"{s444} B vs {s420} B");
            Assert("JXL 色度实测: 4:2:0 比 4:4:4 小（降采样方向不能反）", s420 < s444,
                $"420={s420} B, 444={s444} B");

            void Settings(string chroma, string outPath) => enc.EncodeSdrAsync(
                bgra, W, H,
                new EncodingSettings
                {
                    Format = OutputFormat.JPEG_XL, Quality = 1.0f,
                    ChromaSubsampling = chroma, OutputBitDepth = 8, ColorSpaceTag = "sRGB",
                }, outPath).GetAwaiter().GetResult();
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录清理失败不影响结论 */ }
        }
    }

    /// <summary>
    /// 8-bit → 16-bit 升采样的**通道顺序**必须与 <c>ManagedPngEncoder.Encode16</c> 的
    /// 「B16 G16 R16 A16 大端」约定一致。
    /// <para>回归背景：JXL 旧的内联实现把 R 写到 offset 0、B 写到 offset 4，
    /// 导致 10/12-bit JXL 输出红蓝互换 —— 而 JXL 默认位深正是 10，即默认路径一直在踩。
    /// 这是"只断言文件非空"的测试抓不到的一类缺陷，故用逐字节断言钉死。</para>
    /// </summary>
    static void Test_FormatHelper_Bgra8ToBgra16BE()
    {
        // bgra 布局：B=0x11, G=0x22, R=0x33, A=0xFF
        var bgra = new byte[] { 0x11, 0x22, 0x33, 0xFF };
        var bgra16 = FormatHelper.Bgra8ToBgra16BE(bgra);

        Assert("Bgra8ToBgra16BE: 长度翻倍", bgra16.Length == bgra.Length * 2);
        // 期望 B16 G16 R16 A16 大端，且高低字节相同（v*257）
        var expected = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33, 0xFF, 0xFF };
        Assert("Bgra8ToBgra16BE: 通道顺序为 B/G/R/A 且 v*257 升深",
            bgra16.AsSpan().SequenceEqual(expected),
            $"实际 {Convert.ToHexString(bgra16)} 期望 {Convert.ToHexString(expected)}");

        // 交叉验证：同一像素走 HDR 路径的 Rgba16ToBgra16Bytes 应得到相同字节
        // （rgba16 顺序为 R,G,B,A ⇒ 这里传 R=0x33,G=0x22,B=0x11,A=0xFF）
        var viaHdr = FormatHelper.Rgba16ToBgra16Bytes(new ushort[] { 0x3333, 0x2222, 0x1111, 0xFFFF }, 1, 1);
        Assert("Bgra8ToBgra16BE: 与 Rgba16ToBgra16Bytes 字节一致（通道顺序同源）",
            viaHdr.AsSpan().SequenceEqual(expected),
            $"实际 {Convert.ToHexString(viaHdr)}");
    }

    /// <param name="detail">失败时补充的上下文（实测值 / 期望值）。
    /// 逐字节、逐参数一类的断言缺少它就无法定位差异，只能看到"失败了"。</param>
    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else
        {
            _failed++;
            Console.WriteLine(string.IsNullOrEmpty(detail)
                ? $"  ❌ {name}"
                : $"  ❌ {name} —— {detail}");
        }
    }
}
