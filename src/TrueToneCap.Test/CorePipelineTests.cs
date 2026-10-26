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
        Test_BgraToScrgbForDisplay_PresentationContract();
        Test_SdrPreview_RoutesThroughTheHdrWindow();
        Test_FormatCombo_RebuildKeepsThePersistedFormat();
        Test_XamlControlsResources_MergedExactlyOnce();
        Test_ComboByTag_NeverLeavesBlank();
        Test_XamlIcons_DoNotDependOnSymbolFonts();
        Test_LogViewer_Toolbar_HasDistinctColumns();
        Test_WgcCapture_DegradationIsVisibleAtDefaultVerbosity();
        Test_QualityTexts_CoreStringsAreAllCovered();
        Test_UiFontSweep_DoesNotTouchIconSubtrees();
        Test_Autostart_OnlyWrittenOnUserSave();
        Test_ShowPreview_HasAConsumer();
        Test_FrameworkOwnedResidue_IsReallyFrameworks();

        Test_OutputDir_OnlyFallsBackWhenEmpty_FailureIsVisible();

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
        Test_ShaderList_HasNoOrphanEntry();
        Test_FormatHelper_ResolveActualPath();

        // 跨项目源码契约（App 层的"两处常量必须同步"类约束）
        Test_SettingsWhitelist_MatchesXamlTags();
        Test_AppSelfChecks_AreCalled();
        Test_PngFallbackSwitch_Wired();
        Test_SettingsDiffPruner_Semantics();
        Test_SettingsPersistence_Sentinels();
        Test_CaptureRoots_FailureNeverSilent();
        Test_AnnotationComposedForAllDestinations();
        Test_ReviewRoundFixes_HaveTeeth();
        Test_LlmConfig_InjectsYoudaoCredentials();
        Test_StartupManager_ReadHandlesQuotedCommand();
        Test_StartupManager_PackagedUsesStartupTask();
        Test_OcrPreviewTranslation_CancellationAndSnapshot();
        Test_LogDirectory_PackagedBranch();
        Test_SpdxHeaders_OwnSourceFilesAreStamped();

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
        Test_PackageRuntime_UnpackagedDefault();
        Test_SettingsPersistence_BlockedPath_Sentinels();
        Test_GainMapMerged_FormatIndexMigration();
        Test_HdrPngDepthNegotiation_SourceSentinels();
        Test_AvifCrfFloorTable();
        Test_DegradationToast_SingleDedupRule();
        Test_StatusBarLocale_CsMustNotReWriteChinese();
        Test_HotkeyConflictLog_CarriesRealWin32Error();
        Test_LogViewer_WindowAndInstanceAttribution();
        Test_OverlayReentryLatch_SitsBeforeWindowCreation();
        Test_Overlay_HasNoGpuBackgroundPath();
        Test_BusyRejection_DoesNotUseFailureToast();
        // HDR 抓帧窗的字是画进像素的 ⇒ 本地化闸门/UIA/降级告警三条检测路径全瞎，这里把它钉成可数的数
        StringLiteralScannerTests.RunAll(Assert);   // 尺子自己的对抗用例（先于用它的锁）
        Test_HdrBakedText_BlindSpotIsCounted();
        Test_ToolbarContract_SingleSource();


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
        Test_Edid_ChromaticityBitsMatchSpec();
        Test_OcrPreviewWindow_ConstructionGuarded();
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
        Test_UnofficialTranslateEndpointsAreOptIn();  // B-8：端点开关【关掉】时必须一条请求都不发
        Test_TranslationBackendAvailabilityIsHonest();
        Test_FreeTranslateEndpointsAreOnByDefault();   // 2026-10-23：免费档开箱可用，不许逼用户填 key
        Test_EdgeFreeEndpointShape();
        Test_MyMemoryCharLimitAndSilentRejection();
        Test_FreeEndpointHintNamesARealControl();      // 提示里引号内的控件名必须在界面上逐字存在
        Test_FreeEndpointsDefaultMigrationIsWired();
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
    /// 说明：这两条用例会读写 %LOCALAPPDATA%\TrueToneCap\encoder-crash.&lt;backend&gt;.sentinel，
    /// 结束时均主动 ClearAll 复原（ClearAll 也会忘掉本会话的"已消费"记 ⇒ 用例之间不互相污染）。
    /// 若用户此前确有崩溃残留，会被用例清除 —— 影响仅为"重新启用一次硬件编码"，属无害副作用。
    /// <para>═══ 2026-09-29 审查轮 §10.3 V5：这里原先拿【本进程 Arm】当"崩溃残留"的替身 ═══
    /// 哨兵加上 PID 归属后那个替身不再成立：同进程写的在途文件按定义不是残留，<c>IsBlocked</c>
    /// 必须返回 false（返回 true 才是本轮修的那个 bug）。要模拟残留就伪造一份【别的进程】写的文件，
    /// 路径用 <c>SentinelPathFor</c> 现取 —— 在测试里重抄一遍拼法，它就会悄悄跟实现分叉。</para>
    /// </summary>
    static void Test_EncoderCrashSentinel_ArmDisarm()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            bool before = EncoderCrashSentinel.IsBlocked("NVENC");
            EncoderCrashSentinel.Arm("NVENC");
            bool inFlight = EncoderCrashSentinel.IsBlocked("NVENC");
            bool keptWhileInFlight = File.Exists(EncoderCrashSentinel.SentinelPathFor("NVENC"));
            EncoderCrashSentinel.Disarm();
            bool after = EncoderCrashSentinel.IsBlocked("NVENC");

            Assert("EncoderCrashSentinel: 初始未阻塞", !before);
            Assert("EncoderCrashSentinel: 本进程在途的 Arm 不算崩溃残留（否则第二个并发编码会替第一个拆保护并误报崩溃）",
                !inFlight);
            Assert("EncoderCrashSentinel: 在途期间文件仍在 ⇒ 真崩了仍有据可读", keptWhileInFlight);
            Assert("EncoderCrashSentinel: Disarm 后解除（模拟正常返回）",
                !after && !File.Exists(EncoderCrashSentinel.SentinelPathFor("NVENC")));

            WriteForeignSentinel("NVENC");
            Assert("EncoderCrashSentinel: 别的进程留下的残留仍判崩溃（上面三个 false 不是恒 false）",
                EncoderCrashSentinel.IsBlocked("NVENC"));
        }
        finally
        {
            EncoderCrashSentinel.ClearAll();
        }
    }

    /// <summary>伪造一份"另一个进程留下的"哨兵（三行：后端名 / PID / 时间戳）。</summary>
    static void WriteForeignSentinel(string backend)
    {
        var p = EncoderCrashSentinel.SentinelPathFor(backend);
        var dir = Path.GetDirectoryName(p)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(p, backend + Environment.NewLine + (Environment.ProcessId + 1) +
            Environment.NewLine + DateTime.UtcNow.ToString("O"));
    }

    static void Test_EncoderCrashSentinel_BackendScoped()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            WriteForeignSentinel("NVENC");          // 崩溃发生在 NVENC 上（别的进程留下的证据）
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
    /// <para>═══ 2026-09-29 审查轮 §10.3 V5：这个方法的第二条期望原先把【错行为】钉成了契约 ═══
    /// 旧写法要求"两次 Arm + 一次 Disarm 之后 IsBlocked == true"，可那个文件就是本进程自己刚写的
    /// 在途标记。按旧期望实现的结果是：第二个并发编码把第一个仍在原生调用中的保护读成
    /// "上次进程崩了" ⇒ 删掉它、整进程禁用硬件、还弹一条假的崩溃理由。
    /// 现在的契约是【在途 ≠ 残留】：同进程（PID 相同或本进程仍有在途计数）判 false 且不许删文件；
    /// 只有别的进程留下的、或旧两行格式（无 PID 行）的残留才判 true。两臂各咬一种错。</para>
    /// </summary>
    static void Test_EncoderCrashSentinel_ConcurrentArm()
    {
        try
        {
            EncoderCrashSentinel.ClearAll();
            EncoderCrashSentinel.Arm("NVENC");   // 线程 A 进入原生编码
            EncoderCrashSentinel.Arm("NVENC");   // 线程 B 进入原生编码
            EncoderCrashSentinel.Disarm();       // A 正常返回 —— B 仍在编码中

            var path = EncoderCrashSentinel.SentinelPathFor("NVENC");
            Assert("哨兵: 同进程在途的 Arm 不得被读成「上次进程崩溃」（不许据此禁用硬件）",
                !EncoderCrashSentinel.IsBlocked("NVENC"),
                "在途与残留同形 ⇒ 第二个并发编码会替第一个拆保护并弹假原因");
            Assert("哨兵: 先返回者不许替后返回者删文件（保护仍在，直到最后一个 Disarm）",
                File.Exists(path));

            EncoderCrashSentinel.Disarm();       // B 也返回 —— 此时才应清除
            Assert("哨兵: 最后一个返回者才清除哨兵", !File.Exists(path));

            // ── 对照臂：别的进程留下的残留【必须】判成崩溃（上面那条 false 不能是"永远 false"） ──
            var dir = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, "NVENC" + Environment.NewLine +
                (Environment.ProcessId + 1) + Environment.NewLine + DateTime.UtcNow.ToString("O"));
            Assert("哨兵: 别的进程 PID 留下的文件必须判为崩溃残留并禁用该后端一次",
                EncoderCrashSentinel.IsBlocked("NVENC"),
                "这一臂不成立 ⇒ 上一条的 false 是恒 false，整条判据空转");
            Assert("哨兵: 残留被读后即消费（禁用只持续下一次启动那一个会话，不跨会话永久）",
                !File.Exists(path) && EncoderCrashSentinel.IsBlocked("NVENC"));

            // ── 旧两行格式（第二行是时间戳而不是 PID）：宁可少一次在途保护，也要能恢复 ──
            EncoderCrashSentinel.ClearAll();
            File.WriteAllText(path, "NVENC" + Environment.NewLine + DateTime.UtcNow.ToString("O"));
            Assert("哨兵: 旧两行格式（无 PID 行）的残留仍判为崩溃 ⇒ 不会因为换了格式就永久卡住",
                EncoderCrashSentinel.IsBlocked("NVENC") && !File.Exists(path));

            // ── 归属校验：文件名与首行不一致时，既不替别人禁用、也不替别人消费 ──
            //   ⚠ 这一臂必须写到【被请求后端自己的】路径上，否则它是"文件不存在"判绿的空转臂。
            EncoderCrashSentinel.ClearAll();
            var otherPath = EncoderCrashSentinel.SentinelPathFor("QSV-MFT");
            File.WriteAllText(otherPath, "NVENC" + Environment.NewLine +
                (Environment.ProcessId + 1) + Environment.NewLine + DateTime.UtcNow.ToString("O"));
            Assert("哨兵: 首行后端名与所请求不符时不许禁用（NVENC 的崩溃不能算到 QSV 头上）",
                !EncoderCrashSentinel.IsBlocked("QSV-MFT") && File.Exists(otherPath),
                "凭文件存在就禁用 ⇒ 归属又错回去；顺手删掉 ⇒ 替别人消费了哨兵");
            Assert("哨兵: 读别的后端时不许顺手删掉这份不匹配的残留（NVENC 自己没残留 ⇒ false）",
                !EncoderCrashSentinel.IsBlocked("NVENC") && File.Exists(otherPath));
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

    /// <summary>
    /// 呈现帧函数 <c>BgraToScrgbForDisplay</c> 的契约（2026-09-30，SDR 改道进 HDR 界面的前置条件）。
    /// <para>三条各对应一种真机上【看得见】的错法，缺一条就会复现维护者肉眼抓到的症状：
    /// ① 参考白没乘 ⇒ SDR 内容比周围桌面暗一档（HDR 输出下 1.0 名义 = 80 nits，而系统 SDR 白点由用户定）；
    /// ② 在非 HDR 输出上也乘 ⇒ 白点被推到 1.5，而 DWM 对普通屏的 FP16 scRGB 内容做数值钳位 ⇒ 削成一团白；
    /// ③ 沿用源 alpha ⇒ Ignore + flip + WS_EX_NOREDIRECTIONBITMAP 下 A=0 表现成"透出实时桌面"
    ///    （<c>HdrCaptureWindow.cs:332-338</c> 那条注释自己描述过的现象）。</para>
    /// <para>尺子取【独立实现】而不是抄本文件的 LUT：期望值按 IEC 61966-2-1 的 sRGB EOTF 现算，
    /// 名义参考白从 <see cref="PixelOps.NominalSdrReferenceWhiteNits"/> 取，不在测试里重抄常数
    /// （§30.3 那条"抄常数要配行为锚"的直接落实）。</para>
    /// </summary>
    static void Test_BgraToScrgbForDisplay_PresentationContract()
    {
        // ── 夹具：一整条 256 级灰阶，A 通道故意一半 0、一半 255（看呈现帧还敢不敢信源 alpha）──
        const int n = 256;
        var bgra = new byte[n * 4];
        for (int k = 0; k < n; k++)
        {
            int i = k * 4;
            bgra[i] = (byte)k; bgra[i + 1] = (byte)k; bgra[i + 2] = (byte)k;
            bgra[i + 3] = (byte)((k % 2 == 0) ? 0 : 255);
        }
        static float SrgbEotf(int code)   // 独立参照：IEC 61966-2-1
        {
            float c = code / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
        float wantHdrWhite = 120f / PixelOps.NominalSdrReferenceWhiteNits;
        int wi = 255 * 4;   // 码点 255 那一格的起点（夹具里 pixel 索引 == 码点）

        var hdr = PixelOps.BgraToScrgbForDisplay(bgra, n, 1, hdrOutputActive: true, sdrWhiteNits: 120);
        var sdr = PixelOps.BgraToScrgbForDisplay(bgra, n, 1, hdrOutputActive: false, sdrWhiteNits: 120);

        Assert("呈现帧两档长度都 == w*h*4（呈现侧按这个索引，短一寸就越界读写）",
            hdr.Length == n * 4 && sdr.Length == n * 4, $"实得 {hdr.Length}/{sdr.Length}");

        // ① 参考白：两档的白点值必须分别是 1.5 与 1.0
        Assert($"HDR 输出档：码点 255 → {wantHdrWhite:0.###}（= 系统 SDR 白点 120 / 名义 80），三通道",
            Math.Abs(hdr[wi] - wantHdrWhite) < 1e-5f && Math.Abs(hdr[wi + 1] - wantHdrWhite) < 1e-5f
            && Math.Abs(hdr[wi + 2] - wantHdrWhite) < 1e-5f,
            $"实得 {hdr[wi]},{hdr[wi + 1]},{hdr[wi + 2]}");
        Assert("非 HDR 输出档：码点 255 → 恰好 1.0（再高一点就要被 DWM 钳位）",
            sdr[wi] == 1f && sdr[wi + 1] == 1f && sdr[wi + 2] == 1f,
            $"实得 {sdr[wi]},{sdr[wi + 1]},{sdr[wi + 2]}");

        // ② 值域：非 HDR 档整帧不得 >1；HDR 档白必须真的 >1（等于旧函数就是这刀没生效）
        float maxSdr = 0f;
        for (int k = 0; k < n; k++) { if (sdr[k * 4] > maxSdr) maxSdr = sdr[k * 4]; }
        Assert($"非 HDR 档整帧 max ≤ 1.0（实得 {maxSdr:0.####}）", maxSdr <= 1.000001f,
            "白点被推到 1 以上 = 普通屏上被数值钳位 = 白字糊成一团白");
        Assert("HDR 档确实抬到 1.0 以上（与旧函数逐位相同 = 参考白根本没乘）", hdr[wi] > 1f,
            $"实得 {hdr[wi]}");

        // ③ alpha：夹具里一半像素 A=0，两档输出都必须是一
        int aBad = 0;
        for (int k = 0; k < n; k++) { if (hdr[k * 4 + 3] != 1f || sdr[k * 4 + 3] != 1f) aBad++; }
        Assert("A 通道一律 1.0，不信源帧 alpha（夹具含 128 格 A=0）", aBad == 0, $"实得 {aBad} 格不是一");

        // ④ 与独立 EOTF 参照逐点对齐（含低端非线性段）
        foreach (int code in new[] { 0, 10, 187, 255 })
        {
            float e = SrgbEotf(code);
            int i = code * 4;
            Assert($"非 HDR 档 码点 {code} → 独立 EOTF 参照 {e:0.######}（三通道逐位）",
                Math.Abs(sdr[i] - e) < 1e-6f && Math.Abs(sdr[i + 1] - e) < 1e-6f && Math.Abs(sdr[i + 2] - e) < 1e-6f,
                $"实得 {sdr[i]},{sdr[i + 1]},{sdr[i + 2]}");
            Assert($"HDR 档 码点 {code} → {e:0.######}×{wantHdrWhite:0.###}",
                Math.Abs(hdr[i] - e * wantHdrWhite) < 1e-6f, $"实得 {hdr[i]}");
        }

        // ⑤ 读不到白点不许猜
        var noNits = PixelOps.BgraToScrgbForDisplay(bgra, n, 1, hdrOutputActive: true, sdrWhiteNits: 0);
        Assert("sdrWhiteNits<=0 ⇒ 不缩放（回落 1.0），不猜一个白点，但 A 仍写 1",
            noNits[wi] == 1f && noNits[wi + 3] == 1f, $"实得 白={noNits[wi]} A={noNits[wi + 3]}");

        // ⑥ 守卫必须响亮：短一寸都不许静默失步
        bool threw = false;
        try { PixelOps.BgraToScrgbForDisplay(new byte[4 * 10], 32, 1, hdrOutputActive: true, sdrWhiteNits: 120); }
        catch (ArgumentException) { threw = true; }
        Assert("源帧长度不足 ⇒ 抛 ArgumentException（少读字段还照样出图是本仓反复拦的那类）", threw);

        // ⑦ 单调性（被钳回/回绕都会破坏它）
        bool mono = true;
        for (int k = 1; k < n; k++) { if (sdr[k * 4] < sdr[(k - 1) * 4]) { mono = false; break; } }
        Assert("非 HDR 档灰阶输出单调（不单调=某处被钳回或回绕）", mono);

        // ⑧ 负样本：这一档【不许】等于旧函数的输出，否则说明缩放根本没进呈现路径
        var legacy = PixelOps.BgraToScrgbLinearFast(bgra, n, 1);
        bool sameAsLegacy = true;
        for (int k = 0; k < n; k++) { if (hdr[k * 4] != legacy[k * 4]) { sameAsLegacy = false; break; } }
        Assert("HDR 档与旧函数逐位不同（旧函数就是「白点没乘」那一版，相同即改道无效）", !sameAsLegacy);
    }

    /// <summary>
    /// 选区预览改道的形状锁（2026-09-30，用户口径"以 HDR 下的界面为准，SDR 与 HDR 体验统一"）。
    /// <para>钉四件事，每件对应一种会把"两套界面 / 白屏"请回来的改法：
    /// ① <c>if (hasHdr)</c> 那道分叉不许回来；
    /// ② 呈现帧必须由带参考白与 alpha 契约的 <c>BgraToScrgbForDisplay</c> 生成，MainWindow 里
    ///    不许再直接吃 <c>BgraToScrgbLinearFast</c>（那是"白点没乘 + 信驱动给的 alpha"那一版）；
    /// ③ <c>LoadFrame</c> 必须吃那份统一帧 <c>presentFrame</c>，不许退回只有 HDR 才有值的
    ///    <c>hdrDesktopPixels</c>（连带老那句 <c>if (hdrDesktopPixels is null) return;</c> 也不许回来——
    ///    它会把 SDR 档吞成"什么都没发生"）；
    /// ④ <c>new SelectionOverlay(</c> 全文件只许 1 处，且必须排在 <c>if (initOk)</c>【之后】——
    ///    它是"预览窗建不起来"的最后兜底，不是 SDR 的正路。</para>
    /// <para>取下标前一律剥整行注释：我这段说明里就带着这些锚文本，不剥注释这条锁就此恒真
    /// （§17.2 记过同一类"哨兵读到注释"）。</para>
    /// </summary>
    static void Test_SdrPreview_RoutesThroughTheHdrWindow()
    {
        var mp = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs（缺文件=这条锁失明，不许 Skip 过去）", mp is not null);
        if (mp is null) return;
        var code = StripLineComments(ReadSourceNormalized(mp));

        Assert("SDR/HDR 那道 if (hasHdr) 分叉已撤掉（回来就等于两套界面）",
            !code.Contains("if (hasHdr)"), "hasHdr 重新当地面门槛 ⇒ SDR 又走回旧覆盖层");
        Assert("预览呈现帧走带参考白/alpha 契约的那支，MainWindow 里不再直接吃 LinearFast",
            code.Contains("BgraToScrgbForDisplay(") && !code.Contains("BgraToScrgbLinearFast("),
            "直接吃 LinearFast = 白点没乘 + 相信驱动给的 alpha，正是维护者肉眼抓到的那一族");
        Assert("LoadFrame 吃的是统一帧 presentFrame；老的『没 HDR 帧就静默 return』不许回来",
            code.Contains("hdrWnd.LoadFrame(presentFrame,") && !code.Contains("LoadFrame(hdrDesktopPixels")
            && !code.Contains("if (hdrDesktopPixels is null) return;"),
            "退回 hdrDesktopPixels ⇒ SDR 档要么没帧，要么被那句 return 吞成无声取消");

        int overlays = 0;
        for (int i = code.IndexOf("new SelectionOverlay(", StringComparison.Ordinal); i >= 0;
             i = code.IndexOf("new SelectionOverlay(", i + 1, StringComparison.Ordinal)) overlays++;
        int initOk = code.IndexOf("if (initOk)", StringComparison.Ordinal);
        int firstOverlay = code.IndexOf("new SelectionOverlay(", StringComparison.Ordinal);
        Assert($"旧覆盖层只留 1 处创建点（实得 {overlays}），且排在预览窗初始化判定【之后】（覆盖层={firstOverlay} 判定={initOk}）",
            overlays == 1 && initOk >= 0 && firstOverlay > initOk,
            "覆盖层回到 if (initOk) 之前 = 它又成了 SDR 的正路，而不是兜底");

        Assert("预览窗失败的回退会发一条可见提示（不再只写 log；报告 §33.3 那条静默）",
            code.Contains("PreviewFallbackTitle") && code.Contains("ToastService.ShowToast("),
            "只记 log ⇒ 用户按了键、看见一张不一样的界面，没有任何一句话解释");

        // 兜底那条路的【可达入口】（报告 §35.5：改道之后这 1662+613 行在正常构建里到不了，
        // 于是"兜底能不能用"只能引改道前的历史二进制）。判据要钉住三件事：
        // 存在、默认不改行为、进兜底前先把压在上面的预览窗收掉。
        // 数的是【读取点】而不是名字出现次数：日志文案里本来就该带上开关名（与 TTC_FORCE_SDR 同形），
        // 按名字数会把那条消息数成第二个读取点——第一版就是这么红的。
        int switchSites = System.Text.RegularExpressions.Regex.Matches(
            code, @"GetEnvironmentVariable\(""TTC_FORCE_LEGACY_OVERLAY""\)").Count;
        Assert($"诊断开关 TTC_FORCE_LEGACY_OVERLAY 只有一个读取点（实得 {switchSites}）",
            switchSites == 1, "两处读取 ⇒ 一处改了另一处不变，兜底入口就此半开");
        Assert("该开关必须与 initOk 取【与】：不置它时默认行为逐字不变",
            Regex.IsMatch(code, @"if\s*\(initOk\s*&&\s*Environment\.GetEnvironmentVariable\(""TTC_FORCE_LEGACY_OVERLAY""\)\s*==\s*""1""\)"),
            "不跟 initOk 取与 ⇒ 正常路径也会被它改道，这个开关就不再是「只让兜底可达」");
        Assert("主动进兜底前先 Close 预览窗并清注册（否则量到的是压在覆盖层上面的那扇窗）",
            code.Contains("hdrWnd.Close()") && code.Contains("_activeHdrWindow = null"),
            "不 Close ⇒ 自绘窗还挂在 topmost 上，harness 拍到的是它而不是覆盖层，读数全假");

        // 参考白这条规则现在只有一处实现；拼接那侧若把 80 重新抄回字面量，这条会红
        var wgc = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "WgcCaptureService.cs"));
        Assert("SDR→scRGB 的名义参考白只有一处定义（拼接侧引用常数，不再各抄一个 80）",
            wgc is not null && StripLineComments(ReadSourceNormalized(wgc))
                .Contains("PixelOps.NominalSdrReferenceWhiteNits"),
            "两处各自抄 80 ⇒ 一处改了另一处不变，亮度基准就此分叉");

        // 兜底那侧的钳制常数：§33.1 用真实拖拽量到"写死 460×50 vs 实测约 932×72" ⇒ 贴右边缘必然把
        // 最后两枚按钮推出屏幕。改道后主路不再走它，但兜底还在，就把这条也钉住（它此前没有判据覆盖）。
        var ovp = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml.cs"));
        var ovc = ovp is null ? "" : StripLineComments(ReadSourceNormalized(ovp));
        int bare460 = System.Text.RegularExpressions.Regex.Matches(ovc, @"-\s*460\b").Count;
        Assert($"兜底覆盖层按实测尺寸钳工具条（实得裸 460 = {bare460} 处，要求 0）",
            ovc.Contains("ToolbarWidthDip()") && ovc.Contains("ToolbarHeightDip()") && bare460 == 0,
            "460 回来 = 贴右边缘那一臂又是『6 枚按钮只看得见 4 枚』");
    }

    /// <summary>
    /// 格式下拉重建时【不许】把用户存好的格式抹成默认（2026-10-01 实机量到的 P1）。
    /// <para>链条（三段，少一段都不成立）：构造期先跑 <c>RebuildFormatCombo()</c>，那时控件尚未实化、
    /// <c>SelectedIndex</c> 读回来是 -1 ⇒ 旧写法按 <c>FormatOrder.DefaultIndex</c> 回落成 PNG；
    /// 而这一次<b>程序性</b>赋值会触发 <c>OnFormatChanged</c>（此刻 <c>_uiReady</c> 已经是 true），
    /// 于是 <c>_settings.FormatIndex = 0</c> 外加 <c>SaveQuiet()</c> 落盘。
    /// 用户看到的和量到的都一样：选的格式下次开机失效，而且<b>盘上那个键会被删掉</b>（差量写盘 ⇒ 0 就是默认 ⇒ 键消失）。</para>
    /// <para>前后读数都是实机取的，不靠读码：<c>tests/output/realmachine-2026-10-01/format-persist.log</c>
    /// （改前：盘上 3 ⇒ 界面 PNG ⇒ 会话结束键没了）、
    /// <c>…/format-persist-postfix2.log</c>（改后：盘上 5 ⇒ 界面 TIFF ⇒ 键还在）。
    /// 本卡是 T2 文本锁——它钉得住回落取哪个值，钉不住"赋值会不会又触发写盘"；
    /// 后者住在实机格里，改这段代码必须重跑那个格子，别只跑门禁。</para>
    /// </summary>
    static void Test_FormatCombo_RebuildKeepsThePersistedFormat()
    {
        var mp = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs（缺文件=本卡失明，不许 Skip 过去）", mp is not null);
        if (mp is null) return;
        var code = StripLineComments(ReadSourceNormalized(mp));

        int rb = code.IndexOf("private void RebuildFormatCombo()", StringComparison.Ordinal);
        int rbAgain = rb < 0 ? -1 : code.IndexOf("private void RebuildFormatCombo()", rb + 1, StringComparison.Ordinal);
        Assert($"RebuildFormatCombo 的定义唯一（实得 {rb} / 第二处 {rbAgain}）", rb >= 0 && rbAgain < 0,
            "长出第二个重建口 ⇒ 这条锁和本卡要判的那一段就不是用户实际跑的那一段");
        if (rb < 0 || rbAgain >= 0) return;

        int bodyEnd = code.IndexOf("\n    }", rb, StringComparison.Ordinal);
        Assert("取到 RebuildFormatCombo 的方法体", bodyEnd > rb);
        if (bodyEnd <= rb) return;
        var body = code.Substring(rb, bodyEnd - rb);

        // ① 回落的值必须是持久化那一份
        Assert("控件未实化（keep<0）时回落到 _settings.FormatIndex，不是表里的默认项",
            body.Contains("keep < 0 ? _settings.FormatIndex : keep"),
            "取 DefaultIndex ⇒ 启动那一次程序性赋值把用户的格式抹成 PNG，还连带写回盘上");
        Assert("旧的 DefaultIndex 回落写法不许回来",
            !body.Contains("keep < 0 ? FormatOrder.DefaultIndex : keep"),
            "同上一句，只是防「改一半」：两处并存时读哪一处取决于谁在后面");

        // ② 这个回落只有【一处】——第二处就是一条没人看守的默认路径
        int clampSites = System.Text.RegularExpressions.Regex.Matches(
            body, @"FormatCbo\.SelectedIndex\s*=").Count;
        Assert($"方法体内对 SelectedIndex 的赋值只有一处（实得 {clampSites}）", clampSites == 1,
            "两处赋值 ⇒ 其中一处可以绕过上面那条回落，本卡就此只判住一半");

        // ③ keep 必须在换 ItemsSource【之前】读（换了就被 WinUI 打回 -1，读晚了等于永远走回落）
        int keepAt = body.IndexOf("int keep = FormatCbo.SelectedIndex;", StringComparison.Ordinal);
        int srcAt = body.IndexOf("FormatCbo.ItemsSource =", StringComparison.Ordinal);
        Assert($"keep 的读取排在 ItemsSource 赋值之前（keep={keepAt} ItemsSource={srcAt}）",
            keepAt >= 0 && srcAt > keepAt,
            "读晚了 ⇒ keep 恒 -1，回落分支取代用户选择");
    }

    /// <summary>
    /// 主题资源那一段：第二次合入 <b>不能删</b>，而它的失败<b>不许静默</b>（2026-10-01 实测过的方向）。
    /// <para>我本轮先按"纯噪声"把它删过一次 ⇒ 产品直接起不来：日志停在 OCR 初始化那一行，
    /// 托管 UnhandledException 没跑（<c>%LOCALAPPDATA%\TrueToneCap\crash.log</c> 时间戳没动），
    /// WER 归档给出 <c>故障模块 combase.dll / 异常代码 8000ffff</c> —— 同一个 COM 失败换了个地方冒出来，
    /// 而那里没人接。所以那条 Warn 的含义不是"故障已被降级"，而是"故障还在、这一层挡住了它"，
    /// 且这次构造顺带完成的框架初始化后面要用。</para>
    /// <para>本卡因此钉三件事：XAML 那份在、代码侧那一次 Add 也在、它的 catch 必须往日志说话。
    /// 真正的修法（为什么构造它会失败）还没找到，找到之前别拿"少一条警告"当收益去删它。</para>
    /// </summary>
    static void Test_XamlControlsResources_MergedExactlyOnce()
    {
        var ax = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "App.xaml"));
        var ac = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "App.xaml.cs"));
        Assert("找到 App.xaml 与 App.xaml.cs（缺文件=本卡失明）", ax is not null && ac is not null);
        if (ax is null || ac is null) return;

        var xaml = File.ReadAllText(ax);
        Assert("App.xaml 里那一份 XamlControlsResources 必须在",
            xaml.Contains("<XamlControlsResources"),
            "删掉它 ⇒ 主题资源全没，界面退化成裸控件");

        var code = StripLineComments(ReadSourceNormalized(ac));
        int addSites = System.Text.RegularExpressions.Regex.Matches(
            code, @"MergedDictionaries\.Add\s*\(\s*new\s+[\w.]*XamlControlsResources").Count;
        Assert($"代码侧那一次 Add 必须还在（实得 {addSites} 处，要求 1）", addSites == 1,
            "删掉它产品会【起不来】：0x8000FFFF 换个地方抛出且无人 catch（WER: combase.dll），实测于 2026-10-01");
        Assert("它的 catch 必须往日志说话，不许静默吞掉",
            code.Contains("XamlControlsResources 加载失败"),
            "静默吞掉 ⇒ 这条每次都发生的 COM 失败从账上消失，而下一个人只会看到图标不对");
        // 这一条是本轮我自己踩出来的：在 try/catch 之外读 Resources.MergedDictionaries 会把产品打死。
        // 这一段排在 UnhandledException 注册【之前】，那里抛出的东西没人接 ⇒ 日志停在 NativeResolver、
        // crash.log 时间戳不动、WER 给出 combase.dll / 8000ffff。所以"多加一条守卫"不是更稳，是更脆。
        int allRefs = System.Text.RegularExpressions.Regex.Matches(code, @"MergedDictionaries").Count;
        int tryAt = code.IndexOf("Resources.MergedDictionaries.Add", StringComparison.Ordinal);
        int guardAt = code.IndexOf("MergedDictionaries.Count", StringComparison.Ordinal);
        Assert($"MergedDictionaries 只许出现在那对 try/catch 里（全文件 {allRefs} 处，越界引用={guardAt}）",
            allRefs == 1 && tryAt >= 0 && guardAt < 0,
            "在 try/catch 之外读它 ⇒ 同一个 0x8000FFFF 换个地方抛出且无人 catch，产品直接起不来（2026-10-01 实测）");
    }

    /// <summary>
    /// 下拉回填不许留空白，也不许在没选中项时把盘上的值改成写死的兜底（2026-10-01，用户："修"）。
    /// <para>本机盘上 <c>OverlayColor="#99001833"</c> 不在那 7 个选项里 ⇒ 下拉显示空白，而保存侧
    /// 那句 <c>?? "#99001833"</c> 又把这个<b>既不在选项里、也与模型默认 #80000000 不同向</b>的值写回去
    /// ——兜底字面量自己就是造出这条脏数据的凶手（每次空白保存都把它再生产一遍）。
    /// <c>OcrLanguage</c> 同形：ONNX 引擎下那个下拉是空的，旧写法每次保存把用户存过的语言抹成 ""。</para>
    /// <para>处方与 §3 那条 P1 同一条：<b>回落取持久化真值，不取写死的默认</b>。
    /// 这里的做法是"未匹配就把原值补成一项并选中"⇒ 界面显示的就是实际生效的，18 处 <c>?? 字面量</c>
    /// 那条兜底路径整体不再被触发（所以只改这两处被点名的，其余留作现状）。</para>
    /// </summary>
    static void Test_ComboByTag_NeverLeavesBlank()
    {
        var mp = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Settings.cs"));
        Assert("找到 MainWindow.Settings.cs（缺文件=本卡失明）", mp is not null);
        if (mp is null) return;
        var code = StripLineComments(ReadSourceNormalized(mp));

        int at = code.IndexOf("private static void SetComboByTag(", StringComparison.Ordinal);
        int again = at < 0 ? -1 : code.IndexOf("private static void SetComboByTag(", at + 1, StringComparison.Ordinal);
        Assert($"SetComboByTag 定义唯一（实得 {at} / 第二处 {again}）", at >= 0 && again < 0,
            "长出第二个回填口 ⇒ 有一处会悄悄回到「匹配不到就空白」");
        if (at < 0 || again >= 0) return;
        int bodyEnd = code.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert("取到 SetComboByTag 的方法体", bodyEnd > at);
        if (bodyEnd <= at) return;
        var body = code.Substring(at, bodyEnd - at);

        Assert("0 个选项的下拉直接早退（ONNX 下 OcrLangCbo 本来就没有可选项，那不是值域漂移）",
            body.Contains("cbo.Items.Count == 0"),
            "把「控件本来就没项」和「值域漂了」混成一条 Warn ⇒ 真漂移被固定噪声埋掉");
        Assert("回填比较与校验侧同一把尺（OrdinalIgnoreCase）",
            body.Contains("StringComparison.OrdinalIgnoreCase"),
            "IsOneOf 忽略大小写、这里区分 ⇒ 白名单「RGB」对 XAML「Rgb」会一路活到界面再变空白");
        Assert("未匹配时把原值补成一项并选中，不许留空白",
            body.Contains("cbo.Items.Add") && body.Contains("cbo.SelectedIndex"),
            "空白那一瞬就是保存侧兜底字面量改掉用户值的时机");
        // 选中必须走【控件级 SelectedIndex】。11:18 实机量到：对未实化页面上的容器写
        // item.IsSelected = true 是空操作 ⇒ 项在列表里（8 项、含原值）却"选中=无"，界面仍旧空白。
        // 这与本轮那条格式 P1 同族：控件级状态在实化前后语义不同。
        Assert("选中用控件级 SelectedIndex，不许退回容器级 item.IsSelected",
            !body.Contains(".IsSelected = true"),
            "退回 item.IsSelected ⇒ 页面没实化时静默失效，判据看着绿、界面还是空白");
        // 上面那条只证"补了一项"，证不到补的那一项【带不带原值】。
        // 变异实测：把 `Tag = tag` 改成 `Tag = null` 时上面那条照样绿（M-C 无牙），
        // 而 null Tag 会让保存侧 (string)item.Tag 落回空 ⇒ 与"留空白"是同一个坏结果。
        Assert("补的那一项必须把盘上原值挂在 Tag 上（否则保存侧读回的是 null/空）",
            body.Contains("Tag = tag"),
            "Tag 丢了 ⇒ 界面看着有值、保存却把它抹成空，比留空白更坏（它看起来是好的）");

        // 两处被点名的保存侧：只许"有选中项才回写"，不许留写死的兜底。
        // ⚠ 这一条原先只看 MainWindow.Settings.cs 一份文件，而 _settings.OverlayColor 其实有
        //   【两个写者】——另一处是 MainWindow.xaml.cs 的 OnOverlayColorChanged。10-01 改了前一份、
        //   漏了后一份，判据照样绿，本机盘上至今仍是那个脏值（11:44 的 UIA 读数里它作为
        //   SetComboByTag 补出来的第 8 项冒出来，标签就是一串裸 #99001833）。
        //   尺子的覆盖面要跟着"这个字段有几个写者"走，不是跟着"我当时打开了哪个文件"走。
        string Cut2(string s) => s.Length > 96 ? s[..96] + "…" : s;
        var overlayWriters = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(mp)!, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
            foreach (Match m in Regex.Matches(StripLineComments(ReadSourceNormalized(f)),
                         @"_settings\.OverlayColor\s*=[^\r\n]*"))
                overlayWriters.Add($"{Path.GetFileName(f)}: {m.Value.Trim()}");
        Assert("量到了 OverlayColor 的全部写者（少于 2 个 = 尺子又只看见一个文件，正是原来那条的毛病）",
            overlayWriters.Count >= 2,
            $"实得 {overlayWriters.Count} 个写者：" + string.Join(" || ", overlayWriters.Select(Cut2)));
        var stillFallback = overlayWriters
            .Where(w => w.Contains(@"?? ""#") || w.Contains("#99001833")).ToList();
        Assert("OverlayColor 的【每一个】写者都不许再留写死的兜底颜色",
            stillFallback.Count == 0,
            "兜底字面量自己不在 7 个选项里 ⇒ 每次空白保存都把脏值再生产一遍，还和模型默认 #80000000 不同向；"
            + $"仍带兜底的写者 {stillFallback.Count} 个：" + string.Join(" || ", stillFallback.Select(Cut2)));
        Assert("OcrLanguage 的保存侧只在有选中项时回写",
            code.Contains("ocrLangItem"),
            "ONNX 下下拉为空 ⇒ 旧写法每次保存把用户存过的语言抹成空串，切回 Windows OCR 只能落回第一项");
    }

    /// <summary>
    /// 界面图标不许再依赖符号字体（2026-10-01 实机定因后的形状锁）。
    /// <para>起因与证据链在报告 §11-②：这个进程按【字体友好名】解析符号字体失败——
    /// 裸 TextBlock 写 `Segoe MDL2 Assets` / `Segoe Fluent Icons` / `Segoe UI Symbol` 三族并排画同一枚
    /// U+E713 全是豆腐块，而同一段里的中文与拉丁正常；GDI+ 用同一族同一码点能画出完好齿轮
    /// （`gdiplus-glyph.png`），挪走 `DWriteCore.dll` 也不改变结果。
    /// 修法走的是同图里已被证明可用的一条渲染路径：CheckBox 的勾是 `Path` 几何、一直显示正常 ⇒
    /// 把图标取成字形轮廓（`tests/manual/safety/w7-glyph-to-pathdata.ps1` 用 GDI+ 的 GraphicsPath
    /// 抽轮廓，回放自检 `w7-pathdata-roundtrip-check.ps1` 证明数据忠实）后改用 `PathIcon`。</para>
    /// <para>本卡钉三件事：换过的文件里不许再出现 `<FontIcon`；不许靠"把图标删了"来通过（每文件要求
    /// 至少一枚 PathIcon）；以及 <c>SilentCaptureToast.xaml</c> 那 5 枚是<b>登记在案的例外</b>
    /// （它们带 `Style="{StaticResource ToastActionIcon}"` 与 `x:Name` 代码回填，要连样式和代码一起改）——
    /// 例外要写死数量，多出来就红。</para>
    /// </summary>
    static void Test_XamlIcons_DoNotDependOnSymbolFonts()
    {
        var converted = new[]
        {
            "MainWindow.xaml", "SelectionOverlay.xaml", "OcrPreviewWindow.xaml",
            "AnnotationWindow.xaml", "ThirdPartyNoticesWindow.xaml",
        };
        int totalIcons = 0;
        // 2026-10-26 契约单源化：主工具条 7 枚按钮的轮廓搬进 Models/ToolbarContract.cs
        //（仍是内联 Data 字符串，运行时生成 PathIcon），计入总数防"图标凭空消失"。
        var contractText = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "ToolbarContract.cs"))
            ?? throw new InvalidOperationException("ToolbarContract.cs 必须存在（图标形态判据的计数面）"));
        int contractIcons = System.Text.RegularExpressions.Regex.Matches(contractText, "\"F0 ").Count;
        foreach (var name in converted)
        {
            var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", name));
            Assert($"找到 {name}（缺文件=本卡失明）", path is not null);
            if (path is null) continue;
            var text = File.ReadAllText(path);
            int fontIcons = System.Text.RegularExpressions.Regex.Matches(text, @"<FontIcon\b").Count;
            int pathIcons = System.Text.RegularExpressions.Regex.Matches(text, @"<PathIcon\b").Count;
            totalIcons += pathIcons;
            Assert($"{name} 里不许再有 FontIcon（实得 {fontIcons}）且图标不许凭空消失（PathIcon {pathIcons} 枚）",
                fontIcons == 0 && pathIcons >= 1,
                "符号字体在这个进程解析不出来 ⇒ 回到 FontIcon 就等于回到豆腐块");
        }
        Assert($"已换成 PathIcon 的图标总数不少于 30（实得 {totalIcons} + 契约 {contractIcons}）",
            totalIcons + contractIcons >= 30,
            "数量掉下来说明有人整批改回 FontIcon 或删掉了图标。2026-10-26 起 SDR 主工具条 7 枚按钮由契约生成，"
            + "它们的轮廓以同样形态（内联 Data mini-language）住在 Models/ToolbarContract.cs，这里一并计数 —— "
            + "形态判据不变：绝不回 FontIcon。");

        var toast = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SilentCaptureToast.xaml"));
        Assert("找到 SilentCaptureToast.xaml（登记例外要用）", toast is not null);
        if (toast is not null)
        {
            var tt = File.ReadAllText(toast);
            int n = System.Text.RegularExpressions.Regex.Matches(tt, @"<FontIcon\b").Count;
            Assert($"SilentCaptureToast 的 FontIcon 必须为 0（实得 {n}）", n == 0,
                "回到 FontIcon ⇒ 通知的语义图标又变豆腐块；这 5 枚原来是按 tone 回填 Glyph，改 PathIcon 后回填的是 Data");
            Assert("按 tone 切换的四枚预置图标都必须在（切可见性，不是共享 Geometry）",
                tt.Contains("IconSuccess") && tt.Contains("IconWarning")
                && tt.Contains("IconError") && tt.Contains("IconInfo"),
                "少一枚 = 有一档 tone 没有对应图标");
        }

        // 例外清零后，App.xaml 那份隐式样式已删（23:11 A/B：主窗默认页含一枚可见下拉箭头，
        // 删前删后逐像素零差异）。它原本的注释写着"否则图标字形无法渲染"——那是错的：
        // 本进程按字体友好名解析符号字体本来就失败（报告 §11-②），这份样式救不了任何图标。
        var appx = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "App.xaml"));
        var appxText = appx is null ? "" : File.ReadAllText(appx);
        Assert("App.xaml 不许再出现隐式 FontIcon 样式，也不许再按名要符号字体",
            appx is not null && !appxText.Contains("TargetType=\"FontIcon\"") && !appxText.Contains("Segoe MDL2"),
            "全仓 XAML 已无 FontIcon 元素 ⇒ 再放一份隐式样式只会是带假注释的死码");

        // LogViewer 是另一个 exe（TTCLogViewer）：主程序修好了不代表它也修好了
        var lvPath = FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "MainWindow.xaml"));
        Assert("找到 LogViewer/MainWindow.xaml（缺文件=这个独立进程从判据上消失）", lvPath is not null);
        if (lvPath is not null)
        {
            var lv = File.ReadAllText(lvPath);
            int lvFont = System.Text.RegularExpressions.Regex.Matches(lv, @"<FontIcon\b").Count;
            int lvPath2 = System.Text.RegularExpressions.Regex.Matches(lv, @"<PathIcon\b").Count;
            Assert($"LogViewer 里不许再有 FontIcon（实得 {lvFont}）且图标不许消失（PathIcon {lvPath2} 枚）",
                lvFont == 0 && lvPath2 >= 5,
                "它的 5 枚此前不在任何清单里 = 纯盲区；本轮一并换成 PathIcon 并纳进本卡");
        }

        // B0 的否决锁（2026-10-02）。曾经试过把 46 枚轮廓收进两张 Icons.xaml、XAML 侧回到
        // <PathIcon Style="{StaticResource TtcIcon.XXXX}"/> 一行——权威源写明 PathIcon.Data 是 Geometry、
        // mini-language 只在【属性字符串】上转换，但 Setter.Value 是 object，对照臂实测能生效，
        // 于是照着做了。结果：默认页（导航栏 5 枚）改前改后【逐像素零差异】，
        // 可 AI 翻译页两枚卡头图标【恒定少一枚】；而少哪一枚会随文件里无关行的增删换位
        // （同一次构建连拍 4 次读数完全一致 ⇒ 不是运行时抖动，是构建期就定了），
        // 给那枚加显式 Width/Height 也救不回来。
        // 那一轮 --unit-tests 1121 条全绿、门禁构建 0 错误 ⇒ 这个红在屏幕外没有任何东西看得见。
        // 判语：Style 下发 Data 这条路在本进程不可用，且失效面无法从屏幕外估界，内联 Data 是唯一形态。
        int styleCarriedIcons = 0, inlineDataIcons = 0;
        foreach (var name in converted.Append("SilentCaptureToast.xaml"))
        {
            var path = FindRepoFile(Path.Combine("src", "TrueToneCap.App", name));
            if (path is null) continue;
            var text = File.ReadAllText(path);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(text, @"<PathIcon\b[^>]*?/>",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                if (m.Value.Contains("Style=\"{StaticResource TtcIcon.")) styleCarriedIcons++;
                if (m.Value.Contains("Data=\"F0 ")) inlineDataIcons++;
            }
        }
        var lvIconPath = FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "MainWindow.xaml"));
        if (lvIconPath is not null)
        {
            var lvText = File.ReadAllText(lvIconPath);
            styleCarriedIcons += System.Text.RegularExpressions.Regex.Matches(
                lvText, @"Style=""\{StaticResource TtcIcon\.").Count;
            inlineDataIcons += System.Text.RegularExpressions.Regex.Matches(lvText, @"Data=""F0 ").Count;
        }
        Assert($"不许有图标把轮廓交给 Style 下发（实得 {styleCarriedIcons} 枚），且每一枚都自带内联 Data（实得 {inlineDataIcons} + 契约 {contractIcons} 枚）",
            styleCarriedIcons == 0 && inlineDataIcons + contractIcons >= 46,
            "Style 下发 Data 会构建期随机丢图标，而单套件与门禁全绿——这条就是那次踩坑留下的路障。"
            + "契约里的 7 条轮廓同为内联 Data 形态，一并计入");
        Assert("两个工程都不许出现 Icons.xaml（B0 的生成物）",
            FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Icons.xaml")) is null
            && FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "Icons.xaml")) is null,
            "这张表存在的唯一理由（集中放轮廓）已经被上一条判据否掉，留着就是带死生成器的半成品");
    }

    /// <summary>
    /// LogViewer 工具条的六个元素必须各占一列（2026-10-02 实机发现并修复）。
    /// <para>起因：本轮为了目视那 5 枚换过 PathIcon 的图标，第一次把 TTCLogViewer 拍下来
    /// （报告 §13.6 此前记它"未目视"）。图上工具条的文字重叠成一团。回源一看：
    /// 表头那个 Grid 声明了 9 个 ColumnDefinition，而 <c>刷新 / 选择目录 / 级别筛选 / 搜索 / 清理过期 / 清空</c>
    /// 六个直接子元素<b>一个都没写 <c>Grid.Column</c></b> ⇒ 全部默认落在第 0 列，彼此叠在同一位置。
    /// 这不是本轮换图标引入的（图标都带 Width/Height 14），是 LogViewer 一直就有的布局缺陷，
    /// 只是过去没人截过它的图。</para>
    /// <para>UIA 几何对照（<c>w9-logviewer-uia-geometry.ps1</c>，取图进程未声明 DPI 感知、
    /// PrintWindow 那条读数会被虚拟化，所以判重叠改用 UIA 的 BoundingRectangle）：
    /// 修复前六枚 X 全等于 105、<b>重叠对 15 个</b>、X 不递增；修复后 X 依次
    /// 447/568/725/839/1011/1174、<b>重叠对 0 个</b>、右缘全部在窗口内。
    /// 本卡把"这一形状不许悄悄退回去"钉成源码判据。</para>
    /// </summary>
    /// <summary>
    /// Core 的编码器把用户可见文案做成了中文成品串（<c>GetQualityRange().Label</c> 与
    /// <c>GetQualityDescription</c>），而本地化表只存在于 App 这一层 ⇒ 只能在显示点往回翻，
    /// 能翻的集合就是 <c>MainWindow.xaml.cs</c> 里那份 <c>CoreQualityTexts</c>。
    /// 这条判据枚举<b>全部</b>格式 × {区间下界, 默认, 上界} 实跑 Core，问一句话：把清单里的
    /// 串从产出文案里<b>扣掉</b>后，还剩不剩中文？剩 = Core 新增或改写了文案而没人补翻译 ⇒
    /// 红并点名（英文界面那几处残留中文就是从这里来的）。
    /// <para>不在这里复刻 App 的替换算法：那是第二份真相。这里只做减法，方向与
    /// <c>QualityText</c> 一致而实现独立。清单 ↔ 对照表的对应关系也在这里查，
    /// 两边任一边走样都会红。</para>
    /// </summary>
    static void Test_QualityTexts_CoreStringsAreAllCovered()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var lmPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (mwPath is null || lmPath is null) { Skip("质量文案本地化覆盖", "未找到源文件"); return; }

        string Cut(string s) => s.Length > 30 ? s[..30] + "…" : s;
        bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── 1) 反向锁：Core 的那两个方法【不许再产出用户可见文案】 ──
        // 这才是这次改动的落点。过去这里返回中文成品串，App 只能往回翻；现在 Core 只给枚举 + 数值。
        // 谁再往 Core 里塞一句中文质量文案，这里红并点名。
        var coreLeak = new List<string>();
        int coreLinesSeen = 0;
        foreach (var name in new[] { "ImageEncoder.cs", "FormatEncoders.cs", "JpegGainMapEncoder.cs" })
        {
            var cp = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", name));
            if (cp is null) { coreLeak.Add(name + " 找不到文件（本卡这一半失明）"); continue; }
            foreach (var line in StripLineComments(ReadSourceNormalized(cp)).Split('\n'))
            {
                if (!line.Contains("GetQualityRange", StringComparison.Ordinal)
                    && !line.Contains("GetQualityDescription", StringComparison.Ordinal)) continue;
                coreLinesSeen++;
                if (HasCjk(line)) coreLeak.Add($"{name}: {Cut(line.Trim())}");
            }
        }
        Assert("扫到了 Core 里那两个方法的全部声明/实现（少于 12 行 = 扫描口径不对，不能记绿）",
            coreLinesSeen >= 12, $"实得 {coreLinesSeen} 行");
        Assert("Core 的质量接口不许再返回中文字面量（措辞归 App 的 ScaleLabels / QualityText）",
            coreLeak.Count == 0,
            $"泄漏 {coreLeak.Count} 处：" + string.Join(" | ", coreLeak.Take(6)));

        var mw = StripLineComments(ReadSourceNormalized(mwPath));

        // ── 2) App 的措辞表要【逐个枚举成员】都有模板，且每条在 ZhToEn 有译文 ──
        int at = mw.IndexOf("ScaleLabels", StringComparison.Ordinal);
        int dictEnd = at < 0 ? -1 : mw.IndexOf("};", at, StringComparison.Ordinal);
        var scaleTemplates = (at < 0 || dictEnd < 0) ? new List<(string Key, string Zh)>()
            : Regex.Matches(mw.Substring(at, dictEnd - at),
                    @"\[\w*\.(\w+)\]\s*=\s*""([^""\n]*)""")
                .Select(m => (Key: m.Groups[1].Value, Zh: m.Groups[2].Value)).ToList();
        Assert("从 MainWindow.xaml.cs 切出质量轴措辞表（切不出来 = 这一半没有尺子）",
            at >= 0 && dictEnd > at && scaleTemplates.Count >= 1,
            $"ScaleLabels 位置 {at} / 收尾 }}; 位置 {dictEnd}，解析 {scaleTemplates.Count} 条");
        var scaleMembers = Enum.GetNames<QualityScale>();
        var noTemplate = scaleMembers.Where(m => !scaleTemplates.Any(kv => kv.Key == m)).ToList();
        Assert("每一个 QualityScale 成员都有措辞模板（Core 加一档而 App 没跟 ⇒ 界面会露出枚举名）",
            noTemplate.Count == 0, $"缺模板 {noTemplate.Count} 个：" + string.Join(" | ", noTemplate));
        var dupTemplate = scaleTemplates.GroupBy(kv => kv.Key)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert("措辞表里没有重复成员", dupTemplate.Count == 0, "重复：" + string.Join(" | ", dupTemplate));

        // 描述行那 7 个 Kind 走 switch、不在这张表里 ⇒ 单独点名扫一遍
        var descBranches = Regex.Matches(mw, @"QualityDescKind\.(\w+)\s*=>")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var noDesc = Enum.GetNames<QualityDescKind>().Where(n => !descBranches.Contains(n)).ToList();
        Assert("每一个 QualityDescKind 成员都有渲染分支",
            noDesc.Count == 0, $"缺分支 {noDesc.Count} 个：" + string.Join(" | ", noDesc));

        // ── 3) 措辞 ↔ 对照表：有名无实 = 英文模式那半句还是中文 ──
        var lmText = File.ReadAllText(lmPath);
        int tabAt = lmText.IndexOf("ZhToEn =", StringComparison.Ordinal);
        int tabEnd = tabAt < 0 ? -1 : lmText.IndexOf("};", tabAt, StringComparison.Ordinal);
        var table = (tabAt < 0 || tabEnd < 0) ? new HashSet<string>(StringComparer.Ordinal)
            : Regex.Matches(lmText.Substring(tabAt, tabEnd - tabAt),
                    @"\[""([^""\n]*)""]\s*=\s*""([^""\n]*)""")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert("对照表解析成功（解析不出条目 = 这一半判据没有牙）", table.Count > 100,
            $"只解析出 {table.Count} 条 / ZhToEn = 位置 {tabAt} / 收尾 }}; 位置 {tabEnd}"
            + $" / 块长 {(tabAt >= 0 && tabEnd > tabAt ? tabEnd - tabAt : -1)}"
            + $" / 实读文件 {Path.GetFileName(lmPath)} 共 {lmText.Length} 字符");
        var noTranslation = scaleTemplates.Select(kv => kv.Zh).Where(z => !table.Contains(z)).ToList();
        Assert("质量轴措辞每一条都在 ZhToEn 有译文", noTranslation.Count == 0,
            $"无译文 {noTranslation.Count} 条：" + string.Join(" | ", noTranslation.Select(Cut)));
        // 描述行用的是片段（"距离: " / " (越小越清晰)"），它们也必须各自有译文
        var trFrags = Regex.Matches(mw, @"LocaleManager\.Tr\(""([^""\n]*)""\)")
            .Select(m => m.Groups[1].Value).Where(HasCjk).Distinct().ToList();
        var fragBad = trFrags.Where(z => !table.Contains(z)).ToList();
        Assert("App 里每一处 LocaleManager.Tr(中文) 的中文都在表里（片段也算）",
            fragBad.Count == 0,
            $"无译文 {fragBad.Count} 条：" + string.Join(" | ", fragBad.Select(Cut)));

        // ── 3a) 夹具：走样要能被同一把尺子抓到
        var fakeMissing = scaleMembers.Append("BrandNewScale")
            .Where(m => !scaleTemplates.Any(kv => kv.Key == m)).ToList();
        Assert("变异自检·夹具①：多出一个没模板的 QualityScale 成员要被抓到",
            fakeMissing.SequenceEqual(new[] { "BrandNewScale" }),
            "实得：" + string.Join(" | ", fakeMissing));
        Assert("变异自检·夹具②：Core 那两行里塞一句中文要被第 1 步抓到",
            HasCjk("GetQualityRange() => (1f, 2f, 2f, \"新中文档\");")
            && !HasCjk("GetQualityRange() => (1f, 2f, 2f, QualityScale.Crf);"),
            "HasCjk 对中文不敏感 ⇒ 第 1 步是恒绿的装饰");

        // ── 3b) 第二个产出者：AVIF 后端名（Core 能力表的 DisplayName）+ App 自己拼的状态后缀 ──
        // 11:57 的 UIA 展开读数暴露出来的：AvifCodecCbo 的项文字【不是】XAML 字面量，
        // 而是 UpdateAvifEncoderLabels() 按能力表逐重写进去的 DisplayName ⇒
        // "XAML 项有没有被点名"那把静态尺子看不见它，第三批补的 SetComboItemText 写在这
        // 也会被盖掉（已撤，改在重写那一处过 CoreText）。这条判据钉的就是这第二个产出者。
        int bat = mw.IndexOf("string[] CoreAvifBackendTexts", StringComparison.Ordinal);
        int batEnd = bat < 0 ? -1 : mw.IndexOf("};", bat, StringComparison.Ordinal);
        var backendKeys = (bat < 0 || batEnd < 0) ? new List<string>()
            : Regex.Matches(mw.Substring(bat, batEnd - bat), @"""([^""\n]*)""")
                .Select(m => m.Groups[1].Value).ToList();
        Assert("从 MainWindow.xaml.cs 切出 App 会翻的 AVIF 后端串清单（切不出来=这一半没有尺子）",
            bat >= 0 && batEnd > bat && backendKeys.Count >= 1,
            $"string[] CoreAvifBackendTexts 位置 {bat} / 收尾 }}; 位置 {batEnd}，解析 {backendKeys.Count} 条");
        var noBackendEn = backendKeys.Where(k => !table.Contains(k)).ToList();
        Assert("后端清单里每一条都在 ZhToEn 有译文", noBackendEn.Count == 0,
            $"无译文 {noBackendEn.Count} 条：" + string.Join(" | ", noBackendEn.Select(Cut)));
        var caps = new List<(string Where, string Text)>();
        foreach (var k in Enum.GetValues<AvifEncoderKind>())
            caps.Add(($"{k}.DisplayName", AvifEncoderCapabilities.Caps(k).DisplayName));
        caps.Add(("App 拼的状态后缀", "（本机不可用）"));   // 这一条不在能力表里，是 App 自己拼的
        var capsCjk = caps.Where(c => HasCjk(c.Text)).ToList();
        Assert("量到了含中文的后端名/后缀（一条都没有 = 这一半判据是装饰）", capsCjk.Count >= 2,
            $"能力表+后缀共 {caps.Count} 条，含中文 {capsCjk.Count} 条");
        Assert("后端清单里没有空串（同上：抛出 + 减法恒真）",
            !backendKeys.Any(string.IsNullOrEmpty), $"空串 {backendKeys.Count(string.IsNullOrEmpty)} 条");
        var bKeys = backendKeys.Where(k => !string.IsNullOrEmpty(k)).ToList();
        string UncoveredBackend(string s) => bKeys.OrderByDescending(k => k.Length)
            .Aggregate(s, (acc, k) => acc.Replace(k, "", StringComparison.Ordinal));
        var backendBad = capsCjk.Where(c => HasCjk(UncoveredBackend(c.Text))).ToList();
        Assert("Core 能力表产出的中文后端名全被 App 清单覆盖（未覆盖 = 英文界面半中半英）",
            backendBad.Count == 0,
            $"未覆盖 {backendBad.Count} 条："
            + string.Join(" | ", backendBad.Select(c => $"{c.Where}={Cut(c.Text)}")));
        Assert("变异自检·夹具③：后端清单外的新中文要被同一把尺子抓到",
            HasCjk(UncoveredBackend("某个新后端（本机从没测过）")),
            "夹具扣完清单后没有中文残留 ⇒ 减法做多了，这一半判据没牙");

        // ── 3c) 第三个产出者：Core 的 FormatColorFacts.Declaration（色域映射卡那一句）──
        // 与第 1 步【反向】配对：第 1 步锁"Core 的质量接口不许吐中文"；这里承认
        // FormatColorFacts 就是要吐中文（它同时喂界面与日志，只能由调用方决定翻不翻），
        // 于是要求它吐出的每一条中文都被 App 清单点名、且表里有译文。
        // 谁在 Declaration 里新加一句中文而 App 没跟 ⇒ 英文界面整列漏翻，这里红并点名。
        var cfPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatColorFacts.cs"));
        Assert("找到 Core 的 FormatColorFacts.cs（缺文件=这一半失明）", cfPath is not null);
        if (cfPath is not null)
        {
            const string StrLit = @"\u0022([^\u0022\n]*)\u0022";      // 一条 C# 字符串字面量
            var cfSrc = StripLineComments(ReadSourceNormalized(cfPath));
            var cfLits = Regex.Matches(cfSrc, StrLit).Select(m => m.Groups[1].Value)
                .Where(HasCjk).Distinct().ToList();
            Assert("量到了 Declaration 的中文模板（少于 20 条 = 这一半没有尺子）", cfLits.Count >= 20,
                $"实得 {cfLits.Count} 条");
            int cfAt = mw.IndexOf("CoreColorFactsTexts", StringComparison.Ordinal);
            int cfEnd = cfAt < 0 ? -1 : mw.IndexOf("};", cfAt, StringComparison.Ordinal);
            var cfKnown = (cfAt < 0 || cfEnd < 0) ? new List<string>()
                : Regex.Matches(mw.Substring(cfAt, cfEnd - cfAt), StrLit)
                    .Select(m => m.Groups[1].Value).ToList();
            Assert("App 侧点名了这份声明通道清单（切不出来=没有对照面）", cfKnown.Count >= 1,
                $"CoreColorFactsTexts 位置 {cfAt} / 收尾 }}; 位置 {cfEnd}，解析 {cfKnown.Count} 条");
            var cfUnlisted = cfLits.Where(s => !cfKnown.Contains(s, StringComparer.Ordinal)).ToList();
            Assert("Core 每一条声明通道文案都被 App 清单点名（未点名 = 英文界面漏翻）",
                cfUnlisted.Count == 0,
                $"未点名 {cfUnlisted.Count} 条：" + string.Join(" | ", cfUnlisted.Select(Cut)));
            var cfNoEn = cfKnown.Where(k => !table.Contains(k)).ToList();
            Assert("声明通道清单每一条都在 ZhToEn 有译文", cfNoEn.Count == 0,
                $"无译文 {cfNoEn.Count} 条：" + string.Join(" | ", cfNoEn.Select(Cut)));

            // 模板与译文的占位符数量必须一致：Core 那侧是 string.Format(T(tpl), args)，
            // 译文少一个 {1} 就漏代值、多一个 {2} 就当场抛 FormatException。
            var pairsEn = Regex.Matches(lmText.Substring(tabAt, tabEnd - tabAt),
                    @"\[(\u0022[^\u0022\n]*)\u0022\]\s*=\s*\u0022([^\u0022\n]*)\u0022")
                .Select(m => (Zh: m.Groups[1].Value, En: m.Groups[2].Value))
                .ToDictionary(x => x.Zh, x => x.En, StringComparer.Ordinal);
            var phBad = cfKnown
                .Where(k => pairsEn.TryGetValue(k, out var en)
                            && CountPlaceholders(k) != CountPlaceholders(en))
                .ToList();
            Assert("模板与译文的占位符数量一致（不一致会漏代值或直接抛 FormatException）",
                phBad.Count == 0,
                $"对不上 {phBad.Count} 条：" + string.Join(" | ", phBad.Select(Cut)));
            Assert("变异自检·夹具④：Core 新加一句未点名的中文声明要被抓到",
                !cfKnown.Contains("某新格式：没有通道可声明。", StringComparer.Ordinal)
                && new[] { "某新格式：没有通道可声明。" }
                    .Any(s => !cfKnown.Contains(s, StringComparer.Ordinal)),
                "夹具没被抓到 ⇒ 上面那条'未点名'是恒绿的装饰");
        }


    }

    /// <summary>
    /// 界面字体 sweep 不许进图标子树（报告 §11-② 的根因锁，2026-10-03 三臂实测定死）。
    /// <para>FontIcon / SymbolIcon 派生自 Control，字形由它<b>模板里的内部 TextBlock</b> 画；
    /// sweep 写的是局部值（优先级高于样式）⇒ 一路递归下去就把那个 TextBlock 换成界面字体，
    /// 而默认回退链里没有 PUA 码位，整窗图标成豆腐块。三臂读数：无守卫=方框、
    /// <b>只跳过 IconElement 本身而仍往下递归=还是方框</b>、整棵子树 return=真齿轮
    /// （tests/output/recovery-2026-10-03/font-probe-{before,after,subtree}.png）。
    /// 第二臂是关键：它说明"判据只要写成一个类型检查就算修好"是错的，
    /// 所以这里锁的是<b>结构顺序</b>——return 必须排在递归那行之前，而不是"提到了 IconElement"。</para>
    /// </summary>
    static void Test_UiFontSweep_DoesNotTouchIconSubtrees()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs（缺文件=本卡失明）", mwPath is not null);
        if (mwPath is null) return;
        var src = StripLineComments(ReadSourceNormalized(mwPath));

        int at = src.IndexOf("public static void ApplyFontToVisualTree", StringComparison.Ordinal);
        int again = at < 0 ? -1 : src.IndexOf("public static void ApplyFontToVisualTree", at + 1, StringComparison.Ordinal);
        Assert($"sweep 实现唯一（实得 {at} / 第二处 {again}）", at >= 0 && again < 0,
            "长出第二个 sweep ⇒ 有一处会继续往图标里灌界面字体");
        if (at < 0 || again >= 0) return;
        int bodyEnd = src.IndexOf("\n    }", at, StringComparison.Ordinal);
        Assert("取到 sweep 的方法体", bodyEnd > at);
        if (bodyEnd <= at) return;
        var body = src.Substring(at, bodyEnd - at);

        int iconAt = body.IndexOf("IconElement", StringComparison.Ordinal);
        int recurseAt = body.IndexOf("VisualTreeHelper.GetChildrenCount", StringComparison.Ordinal);
        Assert("方法体里既认得 IconElement、也认得递归那一行（两个锚点都要在，否则下面比较无意义）",
            iconAt >= 0 && recurseAt >= 0, $"IconElement={iconAt} 递归={recurseAt}");
        // 这才是有牙的那一条：跳过必须发生在【往下递归之前】
        Assert("图标分支的 return 排在递归之前（只跳过 IconElement 本身等于没修）",
            iconAt >= 0 && recurseAt >= 0 && iconAt < recurseAt
            && body.Substring(iconAt, recurseAt - iconAt).Contains("return", StringComparison.Ordinal),
            "IconElement 之后、递归之前没有 return ⇒ sweep 仍会进模板里的内部 TextBlock");
        // 曾在这里加过一条"IconElement 之后不许出现 FontFamily ="的反向哨兵，实测【误报】：
        // 图标分支之后紧跟的就是 else if (parent is Control ctrl) ctrl.FontFamily = ...，
        // 任何定长窗口都必然命中这段合法代码。它既误报又与上一条结构判据重复 ⇒ 撤。
        // 上一条到底有没有牙，由变异证明（把 return 挪到递归之后，必须红）。
    }

    /// <summary>
    /// 开机自启动【只许在用户真的按下「保存设置」时】写注册表（P2 发现 #35 的锁，2026-10-03）。
    /// <para>旧代码在 MainWindow 构造路径里也写一次 <c>StartupManager.IsEnabled = _settings.AutoStart</c>，
    /// 而 <c>GetAppPath()</c> 取 <c>Environment.ProcessPath</c> ⇒ "最后启动过的那个 exe"就成了自启动项：
    /// AutoStart=false 的任何一个 dev 构建起来一次，就把用户真正注册过的那一项【删掉】；
    /// AutoStart=true 时则把它改指到那个构建的输出目录。10-02 实测被这样改过一次。</para>
    /// <para>所以这条锁的不是"有没有那句代码"，而是"赋值点有几处、在不该在的地方"。
    /// T3 那一格（w9-autostart-guard-cell.ps1）用哨兵值撞过：修之前必被删、修之后逐字还在。</para>
    /// </summary>
    static void Test_Autostart_OnlyWrittenOnUserSave()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs（缺文件=本卡失明）", mwPath is not null);
        if (mwPath is null) return;
        var appDir = Path.GetDirectoryName(mwPath)!;
        var sites = new List<string>();
        foreach (var f in Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            var src = StripLineComments(ReadSourceNormalized(f));
            foreach (Match m in Regex.Matches(src, @"StartupManager\.IsEnabled\s*=[^;\r\n]*"))
            {
                int owner = src.LastIndexOf("void ", m.Index, StringComparison.Ordinal);
                var head = owner < 0 ? "" : src.Substring(owner, Math.Min(60, m.Index - owner));
                sites.Add($"{Path.GetFileName(f)} :: {m.Value.Trim()}");
                _ = head;
            }
        }
        Assert("量到了自启动的赋值点（0 处 = 尺子没看见东西，不能记绿）",
            sites.Count >= 1, $"实得 {sites.Count} 处");
        // 只许一个写者，且必须是用户显式保存那条路径（SyncStartupAndHotkey）
        Assert("自启动赋值点全仓唯一（多一处就有一条【用户没要求改】的路径会去改注册表）",
            sites.Count == 1, $"实得 {sites.Count} 处：" + string.Join(" || ", sites));
        Assert("那唯一的写者是 SyncStartupAndHotkey 里的 _settings.AutoStart（由「保存设置」触发）",
            sites.Count == 1 && sites[0].Contains("MainWindow.Settings.cs", StringComparison.Ordinal)
            && sites[0].Contains("_settings.AutoStart", StringComparison.Ordinal),
            "实得：" + string.Join(" || ", sites));
        // 构造路径必须改成"只读 + 不一致就报警"，不许静默改写
        var ctor = StripLineComments(ReadSourceNormalized(mwPath));
        Assert("启动路径改成了只读一致性检查，并且不一致会报警（不许悄悄吞掉漂移）",
            ctor.Contains("StartupManager.IsEnabled", StringComparison.Ordinal)
            && Regex.IsMatch(ctor, @"registered\s*!=\s*_settings\.AutoStart"),
            "构造路径里既读又比、还要 Warn —— 少了报警就等于漂移无人知");
    }

    /// <summary>
    /// 「截图后右下角缩略图预览」这个复选框必须有真实消费者（可用性审计 F4，2026-10-03）。
    /// <para>它过去被三处赋值进 <c>_settings.ShowPreview</c>、又被一处读回去设 <c>IsChecked</c>，
    /// 但【没有任何行为读它】——取消勾选后那块带「打开文件夹」的自定义浮层照样弹。
    /// 界面写着它管这件事，实际没人读：这是"设置项与行为脱钩"那一类，
    /// 用户按它改了没反应，比没有这个开关更糟。</para>
    /// <para>这条锁不查"有没有出现次数"（那会被三个写入点自己喂饱），
    /// 查的是【读的一侧要出现在决定要不要创建浮层的那个判断里】。</para>
    /// </summary>
    static void Test_ShowPreview_HasAConsumer()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs（缺文件=本卡失明）", mwPath is not null);
        if (mwPath is null) return;
        var src = StripLineComments(ReadSourceNormalized(mwPath));

        var writes = Regex.Matches(src, @"_settings\.ShowPreview\s*=[^;\r\n]*").Count;
        Assert("写侧仍在（复选框还连着设置，实得 " + writes + " 处）", writes >= 1,
            "写侧没了 ⇒ 这个开关整条链都断了，该连带撤控件而不是留个空壳");
        Assert("读侧要真的参与决定浮层：出现 if (!_settings.ShowPreview …) 这种判断",
            Regex.IsMatch(src, @"if\s*\(\s*!\s*_settings\.ShowPreview"),
            "只有赋值没有判断 ⇒ 用户取消勾选后右下角照样弹，这就是 F4 原本的形状");
        // 关掉预览不等于什么都不提示：必须退回系统通知，否则会把 ToastOnCapture 的语义一起吃掉
        int at = src.IndexOf("if (!_settings.ShowPreview", StringComparison.Ordinal);
        Assert("退回分支取的是系统通知那条路（不是静默吞掉提示）",
            at >= 0 && src.Substring(at, Math.Min(260, src.Length - at))
                .Contains("ToastService.ShowCaptureSuccess", StringComparison.Ordinal),
            "关掉浮层却什么都不发 ⇒ 顺手改坏了另一个开关的语义");
    }

    /// <summary>数模板里 {0}/{1}… 这类占位符的个数（<c>{</c> 紧跟数字才算）。</summary>
    static int CountPlaceholders(string s) => Regex.Matches(s, @"\{\d+\}").Count;

    /// <summary>
    /// 英文残留计数里那条"框架自带"白名单不许被拿来自证清白（2026-10-03）。
    /// <para>背景：修完 R2 之后残留从 5 降到 1，剩下 3 处（CommandBar 溢出按钮「更多」、
    /// ToggleSwitch 默认「开 / 关」）在<b>全仓 XAML/.cs 里搜不到</b> —— 它们是 WinUI 自己资源表里
    /// 随 <b>OS 显示语言</b> 取词的控件文字，与本应用的 AppLanguage 无关，永远清不掉。
    /// 混进"我们的漏翻"计数就等于养出一条读不到底的告警，所以单独归类。</para>
    /// <para>但这条白名单本身是个逃生口：把自家没翻的串塞进去，Warn 就永远 0。
    /// 所以这里逐条验"它确实不在我们的任何一份 XAML/.cs 里" —— 在，就说明它是我们的漏翻，红。</para>
    /// </summary>
    static void Test_FrameworkOwnedResidue_IsReallyFrameworks()
    {
        var locPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Localization.cs"));
        Assert("找到 MainWindow.Localization.cs（缺文件=本卡失明）", locPath is not null);
        if (locPath is null) return;
        var src = StripLineComments(ReadSourceNormalized(locPath));
        int at = src.IndexOf("s_frameworkOwnedText", StringComparison.Ordinal);
        int end = at < 0 ? -1 : src.IndexOf("};", at, StringComparison.Ordinal);
        var allowed = (at < 0 || end < 0) ? new List<string>()
            : Regex.Matches(src.Substring(at, end - at), @"\u0022([^\u0022\n]*)\u0022")
                .Select(m => m.Groups[1].Value).ToList();
        Assert("切出框架自带白名单（切不出来=这条判据没有尺子）", allowed.Count >= 1,
            $"s_frameworkOwnedText 位置 {at} / 收尾 }}; 位置 {end}，解析 {allowed.Count} 条");
        Assert("白名单规模封顶 6 条（它每长一条，就是有人把自家漏翻判成了框架的词）",
            allowed.Count <= 6, $"实得 {allowed.Count} 条：" + string.Join(" | ", allowed));

        // 使用面：App 工程里所有 .xaml 与 .cs（剥注释），并【先去掉白名单自己那一段】
        // —— 否则白名单里的词永远会命中它自己的声明，那条断言恒红。
        var appDir = Path.GetDirectoryName(locPath)!;
        var faceSb = new System.Text.StringBuilder();
        foreach (var f in Directory.EnumerateFiles(appDir, "*.xaml", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(appDir, "*.cs", SearchOption.AllDirectories))
                     .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")))
        {
            var one = StripLineComments(ReadSourceNormalized(f));
            if (Path.GetFileName(f) == "MainWindow.Localization.cs")
                one = one.Substring(0, one.IndexOf("s_frameworkOwnedText", StringComparison.Ordinal));
            faceSb.Append(one).Append('\n');
        }
        var face = faceSb.ToString();
        // ⚠ 必须是【完整字面量】匹配，不能用 Contains(子串)：白名单里有 "开"/"关" 这种单字，
        //   而"未开启""开关"这类词遍布全仓 —— 子串匹配会让这条判据恒红，
        //   10-03 第一次写就是这么错的（据此差点把"框架自带"的结论推翻，其实错的是尺子）。
        string Lit(string w)
        {
            var e = System.Text.RegularExpressions.Regex.Escape(w);
            // \u0022 是正则里的双引号 —— 借它躲开 C# 字符串与外层脚本两层转义。
            return "\\u0022" + e + "\\u0022|>" + e + "<|=\\u0022" + e + "\\u0022";
        }
        var ours = allowed.Where(w => System.Text.RegularExpressions.Regex.IsMatch(face, Lit(w))).ToList();
        Assert("白名单每一条都不是我们自己写死的字面量（是 = 那是我们的漏翻，不是框架的词）",
            ours.Count == 0, "伪装成框架词：" + string.Join(" | ", ours));
        // 反向：这条判据自己要有牙 —— 我们确实写过的中文，必须被 face 认出来
        Assert("变异自检·夹具：我们真写死的字面量要被同一把尺子认出来",
            System.Text.RegularExpressions.Regex.IsMatch(face, Lit("能力检测中..."))
            && System.Text.RegularExpressions.Regex.IsMatch(face, Lit("截图后右下角缩略图预览")),
            "使用面认不出已知字面量 ⇒ 上面那条'不是我们写的'是恒真的装饰");
    }

    static void Test_LogViewer_Toolbar_HasDistinctColumns()
    {
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "MainWindow.xaml"));
        Assert("找到 LogViewer/MainWindow.xaml（缺文件=本卡失明）", path is not null);
        if (path is null) return;
        var lines = File.ReadAllText(path).Split('\n');

        string[] toolbar = ["RefreshBtn", "OpenDirBtn", "LevelFilterCbo", "SearchTxt", "ClearOldBtn", "ClearAllBtn"];
        var cols = new List<string>();
        int missing = 0;
        foreach (var nm in toolbar)
        {
            var hit = lines.FirstOrDefault(l => l.Contains($"x:Name=\"{nm}\""));
            if (hit is null) { Assert($"{nm} 必须存在于 LogViewer 主窗（它被删了要显式改本卡）", false, "元素消失 = 判据覆盖面缩水"); missing++; continue; }
            var g = System.Text.RegularExpressions.Regex.Match(hit, @"Grid.Column=""(\d+)""");
            if (!g.Success) { Assert($"{nm} 所在行缺 Grid.Column ⇒ 它会退回第 0 列与别人叠在一起", false,
                    "六个元素同列就是本轮实机看到的『文字重叠成一团』"); missing++; continue; }
            cols.Add(g.Groups[1].Value);
        }
        Assert($"工具条六个元素都要有 Grid.Column（实得 {cols.Count}/{toolbar.Length}）",
            cols.Count == toolbar.Length, "少一个就是少一列，那一个会和别人叠在同一位置");
        Assert($"六个列号要互不相同（实得 {cols.Distinct().Count()} 个不同值：{string.Join(",", cols)}）",
            cols.Distinct().Count() == toolbar.Length,
            "重复列号 = 两枚元素落在同一格；UIA 实测这种形状的重叠对数是 15");
        var hdr = lines.FirstOrDefault(l => l.Contains("<ColumnDefinition"));
        Assert($"表头声明的列数（实得 {(hdr is null ? -1 : hdr.Split("<ColumnDefinition").Length - 1)}）要不少于元素数 6",
            hdr is not null && hdr.Split("<ColumnDefinition").Length - 1 >= toolbar.Length,
            "列不够，多出来的元素同样会挤回已有列");
    }

    /// <summary>
    /// 降级事实不许只落 Debug 档（2026-10-02 可用性审计 §2.1）。
    /// <para>契约在 <c>CoreLog</c> 自己身上：<c>LogDetail.Brief</c> 的注释写着"简（默认）: 用户操作、
    /// 启动时的各种能力识别、<b>一切降级事实</b>。Info 及以上"，而 <c>AppSettingsData.LogVerbosity</c>
    /// 默认值就是 <c>"Brief"</c>。所以一句降级一旦用 Debug 记录，默认配置下用户永远看不到它。</para>
    /// <para>起因：<c>WgcCaptureService</c> 里 <c>private static void Log(string) =&gt; LogService.Debug(...)</c>
    /// 与 <c>LogInfo(...)</c> 并存（38 处 vs 24 处），而"放弃整图 HDR，输出 SDR"这类句子走的是前者
    /// ⇒ 一个主打真色/HDR 截图的工具，最要紧的那条降级被自己的日志档过滤掉了。本轮已把三处提到 Info，
    /// 本卡防止它再被改回去、也防止同类新句子直接写成 Debug。</para>
    /// <para>⚠ 本卡必须带反向锚：只判"没有违规"会因为【降级句子被整句删掉】而假绿，
    /// 所以第二条断言要求确有若干条降级事实以 Info 及以上落盘。</para>
    /// </summary>
    static void Test_WgcCapture_DegradationIsVisibleAtDefaultVerbosity()
    {
        var src = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "WgcCaptureService.cs"));
        Assert("找到 WgcCaptureService.cs（缺文件=本卡失明）", src is not null);
        if (src is null) return;
        var lines = File.ReadAllLines(src);
        string[] degradeWords = ["放弃", "丢弃", "捕获失败", "回退", "不参与"];

        // ⚠ 光靠词表会被"换个说法"绕过 —— 本轮就现场踩过一次：我把那句
        //   "D3D11 设备创建于默认适配器 (回退)" 改成 "（未按显示器匹配到适配器）"，
        //   词表里的"回退"随之消失，变异（LogInfo→Log）当场不再被抓 = 假绿。
        //   所以已知降级必须【按当前措辞逐条钉死】，词表只是加宽的新句捕获网。
        //   改措辞是允许的，但要同一次改动里更新这张表，否则本卡红。
        string[] knownDegradations =
        [
            "放弃整图 HDR，输出 SDR",
            "HDR 源区域填充失败，放弃 HDR 输出",
            "Float16 捕获失败",
            "D3D11 设备创建于默认适配器",
            "适配器匹配异常，改走默认适配器",
            "已回退 SDR",
        ];

        bool IsDegradeLine(string t)
        {
            if (t.StartsWith("//") || t.StartsWith("*")) return false;   // 整行注释 / 块注释续行
            int c = t.IndexOf("//", StringComparison.Ordinal);
            if (c >= 0) t = t[..c];                                       // 行尾注释也要剥：
            return degradeWords.Any(t.Contains);                          // 本仓注释里大量出现"回退/放弃"
        }

        var offenders = new List<string>();
        for (int i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("//") || t.StartsWith("*")) continue;
            int cc = t.IndexOf("//", StringComparison.Ordinal);
            var code = cc >= 0 ? t[..cc] : t;
            bool isDegrade = IsDegradeLine(t) || knownDegradations.Any(code.Contains);
            if (!isDegrade) continue;
            if (code.Contains("Log($") || code.Contains("Log(\"") || code.Contains("LogService.Debug"))
                offenders.Add($"{i + 1}: {code[..Math.Min(72, code.Length)]}");
        }
        Assert($"降级语句不许只写进 Debug 档（实得 {offenders.Count} 处）", offenders.Count == 0,
            "默认详细度是 Brief=Info 及以上，走 Debug 等于用户永远看不见这条降级 ｜ " +
            string.Join("  ", offenders.Take(4)));

        // 逐条点名：已知降级里任何一条不再以 Info 及以上出现 ⇒ 红（这条不受换词影响）
        var missing = knownDegradations.Where(k => !lines.Any(l =>
            (l.Contains("LogInfo(") || l.Contains("LogService.Warn") || l.Contains("LogService.Info"))
            && l.Contains(k))).ToList();
        Assert($"已知降级逐条都要以 Info 及以上落盘（缺失 {missing.Count} 条）", missing.Count == 0,
            "换了措辞就要同一次改动里更新这张表；一条都不许悄悄退回 Debug ｜ 缺失：" +
            string.Join(" | ", missing.Take(4)));

        int infoLevel = lines.Count(l =>
            (l.Contains("LogInfo(") || l.Contains("LogService.Warn") || l.Contains("LogService.Info"))
            && degradeWords.Any(l.Contains));
        Assert($"至少要有 3 条降级事实以 Info 及以上落盘（实得 {infoLevel}）", infoLevel >= 3,
            "一条都没有 = 降级提示被整句删掉了；本卡不能靠『没有违规』判绿");
    }

    /// <summary>
    /// 输出目录这条链的现状锁（2026-10-01，报告 §41）。
    /// <para>起因：本机两处设置都写着 <c>OutputPath="D:\TTC"</c> 而<b>这台机器没有 D 盘</b>
    /// （实测 <c>Test-Path D:\</c> = False，盘符只有 C,E,G,Q,W）。全仓原本<b>一条判据都没点过 OutputPath</b>
    /// ⇒ 这条路是纯盲区。本卡只钉现状，不引入任何降级：将来谁要加"建不出来就回落到 图片"这种兜底，
    /// 必须显式改这条并说明理由（静默把用户的落点改掉，正是本仓反复定义的"降级不是修复"）。</para>
    /// <para>另半边钉可见性：目录建不出来时异常会一路冒到保存链的 catch，那里必须既落日志又弹提示——
    /// "日志里一个字都没有、屏幕上只有一张不一样的界面"这种形状本轮刚在覆盖层里修过一次。</para>
    /// </summary>
    static void Test_OutputDir_OnlyFallsBackWhenEmpty_FailureIsVisible()
    {
        var cps = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"));
        Assert("找到 CapturePipelineService.cs（缺文件=本卡失明，不许静默过去）", cps is not null);
        if (cps is null) return;
        var code = StripLineComments(ReadSourceNormalized(cps));

        // ① 只有【空/空白】才回落，非空路径一律按用户写的用
        Assert("空 OutputPath 才回落到 图片\\TrueToneCap（回落条件读得到）",
            code.Contains("string.IsNullOrWhiteSpace(outDir)")
            && code.Contains("SpecialFolder.MyPictures"),
            "这两句是回落的唯一入口；读不到说明回落形状变了");

        // ② 非空但建不出来 ⇒ 当场抛，没有第二道回落。
        //    ⚠ 第一版这里写的是"那一句原样存在"，被自己的变异当场判成假绿：
        //      把整句包进 `try { … } catch { 换目录 }` 之后，那句子串**仍然完整存在** ⇒ 判据照绿
        //      （tests/output/tierA-2026-10-01/mutation-outputdir.log）。所以改成【结构判据】：
        //      这个解析方法里一条 try/catch 都不许有——要加降级，就必须先过本卡。
        int gmStart = code.IndexOf("public string GetEffectiveOutputDir()", StringComparison.Ordinal);
        Assert("读得到 GetEffectiveOutputDir 的方法体（锚点漂了就不判，不蒙绿）", gmStart >= 0,
            $"IndexOf 实得 {gmStart}");
        int gmEnd = gmStart >= 0 ? code.IndexOf("\n    }", gmStart, StringComparison.Ordinal) : -1;
        var gmBody = gmStart >= 0 && gmEnd > gmStart ? code.Substring(gmStart, gmEnd - gmStart) : "";
        Assert("输出目录解析里不许有任何 try/catch（非空路径不可创建就当场抛，不许静默换个目录）",
            gmBody.Contains("Directory.CreateDirectory(outDir)")
            && !gmBody.Contains("try") && !gmBody.Contains("catch"),
            $"方法体里 try={gmBody.Contains("try")} catch={gmBody.Contains("catch")}");

        // ③ 失败必须同时留日志与弹提示（不静默）
        var mw = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        Assert("找到 MainWindow.xaml.cs", mw is not null);
        if (mw is null) return;
        var mwCode = StripLineComments(ReadSourceNormalized(mw));
        Assert("保存失败会落一条 Error 日志（含异常），不是只往状态栏塞一句",
            System.Text.RegularExpressions.Regex.IsMatch(mwCode,
                @"LogService\.Error\(""MainWindow"",\s*\$""截图保存失败"),
            "只剩 SetStatus/Toast 时，事后翻日志会什么都找不到（本轮 §39.4 就是被这种形状骗过一次）");
        Assert("保存失败会弹用户可见的失败提示",
            mwCode.Contains("ToastService.ShowCaptureFailed(ex.Message)"),
            "抛异常但界面不吭声 = 用户以为截到了，其实一张都没有");
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
    /// B-8（2026-09-20 许可合规审计 → 2026-10-23 默认值改判）：Google 的两个<b>非官方</b>端点
    /// （网页版 <c>client=gtx</c>、Chrome 划词 <c>client=dict-chrome-ex</c>）在开关关闭时必须
    /// <b>一个请求都不发</b>，而不是"照样发出去但忽略结果"。
    /// <para>⚠ 这条卡的<b>牙没变，变的是默认值</b>：2026-10-23 用户口径"不应该让用户手动填 KEY，
    /// 免费端点应该考虑各种免费开放端点"⇒ 开关默认开（见
    /// <see cref="Test_FreeTranslateEndpointsAreOnByDefault"/>）。这里继续钉的是"关掉就真不发"，
    /// 那一半从来不是产品取向问题，而是"关着但偷偷发"这个形状本身不可接受。</para>
    /// <para>① ② ③ ④ 是<b>行为</b>判据（直接调用取端点的函数拿真值表）；
    /// ⑤ 是源码级判据，防止"函数改好了但没人调用它"—— 这类漂移本仓真实踩过
    /// （有道凭据从未注入，见 MainWindow.xaml.cs 的 2026-09-15 注释）。</para>
    /// </summary>
    static void Test_UnofficialTranslateEndpointsAreOptIn()
    {
        // ① 开关关掉 ⇒ 连 URL 都不构造（默认值那一半由 Test_FreeTranslateEndpointsAreOnByDefault 钉）
        var cfg = new LlmConfig { AllowUnofficialTranslateEndpoints = false };
        Assert("B-8/LlmConfig: 显式关掉后 Google 两口一条都不返回",
            TranslationService.UnofficialGoogleEndpoints(cfg.AllowUnofficialTranslateEndpoints, "你好", "en", null).Length == 0);

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
    /// 2026-10-23 用户口径："不应该让用户手动填 KEY，免费端点应该考虑各种免费开放端点"。
    /// 这条卡的不是某个端点的形状，而是<b>默认配置下用户开箱就能翻译</b>这件事 ——
    /// 在那之前，免费档 + 不填凭据 = <c>AnyBackendAvailable</c> 为 false，点翻译只会换来一句
    /// "还没有任何可用的翻译后端"，于是"免费"实际上等于"先去注册一个 AppID"。
    /// <para>① ② 是 Core 侧行为真值（直接构造默认 <see cref="LlmConfig"/> 问它可不可用）；
    /// ③ 是 App 侧持久化默认值的源码哨兵 —— Test 工程只引用 Core，够不到
    /// <c>AppSettingsData</c>，所以只能钉源码形态（与 <c>Test_GainMapMerged_FormatIndexMigration</c>
    /// 同一族手法）。少了 ③ 就是"Core 默认开了但 App 传下来的是 false"这种半截改动的盲区。</para>
    /// </summary>
    static void Test_FreeTranslateEndpointsAreOnByDefault()
    {
        var cfg = new LlmConfig();
        Assert("默认配置: 免费公共端点是开着的（不需要用户勾选才用）",
            cfg.AllowUnofficialTranslateEndpoints);
        Assert("默认配置: 不填任何凭据也算『有后端可用』（否则点翻译只会得到去注册）",
            TranslationService.AnyBackendAvailable(cfg));

        // 默认配置下不该再走"三条路任选一条"那段劝退文案
        string hint = TranslationService.DescribeBackends(cfg);
        Assert("默认配置: 说明里不再宣称『还没有任何可用的翻译后端』",
            !hint.Contains("还没有任何可用的翻译后端"), hint);
        Assert("默认配置: 说明改为列出真正启用的那一条免费端点",
            hint.Contains("免费公共端点"), hint);

        var appSettings = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        if (appSettings is null)
        {
            // 文件读不到 ⇒ 这一格的 ③ 判红而不是 Skip：App 侧默认值漂回 false 时，
            // "Core 默认开、盘上默认关"这种半截改动会恰好藏在 Skip 里（本仓 53 条哨兵的通病）。
            Assert("AppSettingsData.cs 可读（否则 App 侧默认值无人判）", false, "FindRepoFile 没找到该文件");
        }
        else
        {
            string src = File.ReadAllText(appSettings);
            Assert("App 侧持久化默认值同为 true（钉代码形态，不钉关键词）",
                src.Contains("public bool AllowUnofficialTranslateEndpoints { get; set; } = true;"),
                "AppSettingsData 里这条属性没有 = true 的初值");
        }
    }

    /// <summary>
    /// Edge 免费端点（<c>edge.microsoft.com/translate/translatetext</c>）的线格式。
    /// <para>为什么值得单独一条：它是 2026-10-23 本机实测里最好的一条免费路
    /// （免凭据、468–577 ms、单条 4 万字符仍可用、14 个目标语言码原样通），
    /// 而它有两个"写错就是 400、且 400 的响应体是空的"形状约束 ——
    /// 空体意味着服务端不给原因，靠线上试错永远看不出坏在哪。两条都必须在离线判据里钉死：</para>
    /// <list type="number">
    /// <item><description>body 是<b>裸字符串数组</b>，不是对象数组也不是对象；</description></item>
    /// <item><description>自动检测 = <b>不发 <c>from</c> 参数</b>；发 <c>from=auto</c> 会 400
    /// （Google 那条 <c>sl = sourceLang ?? "auto"</c> 的写法照搬过来就是这条缺陷）。</description></item>
    /// </list>
    /// </summary>
    static void Test_EdgeFreeEndpointShape()
    {
        const string t = "The output folder does not exist.";

        Assert("Edge/开关关掉: 一个请求都不构造",
            TranslationService.EdgeFreeEndpointRequest(false, t, "zh-Hans", null) is null);

        var auto = TranslationService.EdgeFreeEndpointRequest(true, t, "zh-Hans", null);
        Assert("Edge/自动检测靠【省略 from 参数】实现",
            auto is not null && !auto.Value.url.Contains("from="), auto?.url ?? "<null>");
        Assert("Edge/绝不发 from=auto（实测 400 空体，与 Google 的 auto 写法相反）",
            auto is not null && !auto.Value.url.Contains("from=auto") && !auto.Value.url.Contains("from=AUTO"),
            auto?.url ?? "<null>");

        var explicitSrc = TranslationService.EdgeFreeEndpointRequest(true, t, "en", "zh-CN");
        Assert("Edge/给了源语言就带上，且用 App 的原码不映射",
            explicitSrc is not null && explicitSrc.Value.url.Contains("from=zh-CN")
            && explicitSrc.Value.url.Contains("to=en"), explicitSrc?.url ?? "<null>");

        Assert("Edge/body 必须是裸字符串数组（不能是对象、也不能是对象数组）",
            auto is not null && auto.Value.body.StartsWith("[\"") && auto.Value.body.EndsWith("\"]")
            && !auto.Value.body.Contains("{"), auto?.body ?? "<null>");

        // ⚠ 这一条是本轮真发请求时逮到的缺陷的固化：.NET 的 HttpClient 默认一个 UA 都不发，
        //    而该端点缺 UA 直接 400 "Client Browser Version not supported" ⇒ 第一档静默失效、
        //    全部降到必应，而离线判据（URL 形状、body 形状）当时全绿。
        //    用 urllib 取证时它替自己注入了 User-Agent: Python-urllib/3.x，所以"我没设 UA"
        //    被误读成"这个端点不需要 UA" —— 仪器偷偷加的头，必须当成被测对象的一部分来量。
        Assert("Edge/必须带一个【非空】User-Agent（缺了就是 400，实测 curl/Python-urllib 这类也算数）",
            auto is not null && !string.IsNullOrWhiteSpace(auto.Value.ua), auto?.ua ?? "<null>");
        Assert("Edge/UA 用产品自己的标识而不是冒充 Chrome（它不解析版本，冒充只会把依赖藏起来）",
            auto is not null && auto.Value.ua.StartsWith("TrueToneCap/"), auto?.ua ?? "<null>");

        // 原文里带引号/反斜杠/换行时，body 必须是**合法且往返一致**的 JSON —— 这一族的对位判据在
        // Google 那条是"URL 编码不破坏查询串"，而 Edge 的原文进的是 body 不是 URL：照抄那条会写出
        // 一个永不成立的断言（本轮第一版就是这样，红在"url 里没有原文"——红的是判据，不是产品）。
        var nasty = TranslationService.EdgeFreeEndpointRequest(true, "a\"b\\c\nd#é", "zh-Hans", null);
        bool roundTrips = false;
        string parsed = "";
        if (nasty is not null)
        {
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(nasty.Value.body);
                var arr = d.RootElement;
                if (arr.ValueKind == System.Text.Json.JsonValueKind.Array && arr.GetArrayLength() == 1)
                {
                    parsed = arr[0].GetString() ?? "";
                    roundTrips = parsed == "a\"b\\c\nd#é";
                }
            }
            catch (System.Text.Json.JsonException) { /* 留 parsed/roundTrips 给 detail 打印 */ }
        }
        Assert("Edge/原文含引号反斜杠换行时 body 仍是合法 JSON 且逐字往返",
            roundTrips, $"实得 {parsed?.Replace("\n", "\\n") ?? "<解析失败>"}");
        Assert("Edge/原文【不进 URL】（进 body；哪天有人把它拼进查询串，这条与上面那条会分开指向两种错法）",
            nasty is not null && !nasty.Value.url.Contains("a%22b") && !nasty.Value.url.Contains("a\"b"),
            nasty?.url ?? "<null>");

        // ── 响应解析：成功形状与三种失败形状都要给出可行动的话 ──
        var (good, goodNote) = TranslationService.ParseEdgeResponse(200,
            """[{"translations":[{"text":"输出文件夹不存在。","backend":"MTRANS_MTIR"}]}]""");
        Assert("Edge/正常形状取到译文", good == "输出文件夹不存在。", goodNote);

        var (e400, note400) = TranslationService.ParseEdgeResponse(400, "");
        Assert("Edge/400 空体要说『形状不对』并把两条已知错形写进话里（否则只剩『没有译文』）",
            e400 is null && note400.Contains("体为空")
            && note400.Contains("裸字符串数组") && note400.Contains("from=auto"), note400);

        var (html, noteHtml) = TranslationService.ParseEdgeResponse(200, "<html><body>登录</body></html>");
        Assert("Edge/200 但回的是网页 ⇒ 判失败而不是判成功",
            html is null && noteHtml.Contains("不是 JSON"), noteHtml);

        var (empty, noteEmpty) = TranslationService.ParseEdgeResponse(200, "[]");
        Assert("Edge/空数组 ⇒ 判失败", empty is null && noteEmpty.Contains("空数组"), noteEmpty);

        var (big, noteBig) = TranslationService.ParseEdgeResponse(400,
            "Request exceeds the maximum allowed translation size.");
        Assert("Edge/超长时把服务端那句原话带给用户（实测 4 万字符过、20 万被拒）",
            big is null && noteBig.Contains("exceeds the maximum"), noteBig);

        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "TranslationService.cs"));
        if (svcPath is null)
            Assert("TranslationService.cs 可读（Edge 字面量的唯一性无人判）", false, "FindRepoFile 没找到该文件");
        else
        {
            string src = File.ReadAllText(svcPath);
            Assert("Edge 端点字面量全文件只出现一次（别处再拼一份就绕过了开关与形状约束）",
                src.Split("edge.microsoft.com").Length - 1 == 1,
                $"实得 {src.Split("edge.microsoft.com").Length - 1} 处");
        }
    }

    /// <summary>
    /// MyMemory 匿名档的两条实测硬事实：单条上限在<b>字符</b>上（不是官方文档写的 bytes），
    /// 以及<b>被拒时回的是 HTTP 200 + 体内 responseStatus≠200，拒因塞在译文栏里</b>。
    /// <para>后一条与必应的"HTTP 200 + 体内 statusCode=205"同族：任何"按状态码下结论"的实现
    /// 都会把拒绝当成功、把拒因当译文显示给用户。前一条决定切分策略按什么单位算——
    /// 按字节会把能过的文本白切一半（中文 300 字符＝900 字节实测照过）。</para>
    /// </summary>
    static void Test_MyMemoryCharLimitAndSilentRejection()
    {
        Assert("MyMemory/上限常量是 500 个字符", TranslationService.MyMemoryMaxChars == 500,
            TranslationService.MyMemoryMaxChars.ToString());

        var at = TranslationService.MyMemoryEndpointRequest(true, new string('a', TranslationService.MyMemoryMaxChars), "zh-CN", null);
        Assert("MyMemory/恰好 500 字符 ⇒ 构造请求", at is not null, "<null>");

        var over = TranslationService.MyMemoryEndpointRequest(true, new string('a', TranslationService.MyMemoryMaxChars + 1), "zh-CN", null);
        Assert("MyMemory/501 字符 ⇒ 不发请求（发出去只会换来一句写在译文栏里的拒因）", over is null,
            over?.url ?? "<null>");

        // 中文 300 字符 = 900 UTF-8 字节：若哪天有人按字节判，这一条会当场红
        var zh = TranslationService.MyMemoryEndpointRequest(true, new string('汉', 300), "en", null);
        Assert("MyMemory/按【字符】不按【字节】判（中文 300 字符＝900 字节仍应放行）",
            zh is not null && System.Text.Encoding.UTF8.GetByteCount(new string('汉', 300)) > TranslationService.MyMemoryMaxChars,
            zh?.url ?? "<null>");

        Assert("MyMemory/关掉开关 ⇒ 不构造",
            TranslationService.MyMemoryEndpointRequest(false, "hello", "zh-CN", null) is null);

        var auto = TranslationService.MyMemoryEndpointRequest(true, "hello", "zh-CN", null);
        Assert("MyMemory/源语言缺省写 autodetect（不是 auto）",
            auto is not null && auto.Value.url.Contains("langpair=autodetect"), auto?.url ?? "<null>");

        // ⚠ 两个形状都要判：真件在【拒绝】时把 responseStatus 写成字符串 "403"，
        //    成功时才写数字 200（2026-10-23 由 scripts/probes/edge_mymemory_free_endpoint_probe.py
        //    第 3 节抓到）。本卡第一版只喂数字 403 —— 产品代码恰好两种都解析所以照样绿，
        //    而真件形状一条都没测到（"夹具与真件不同形"这一族）。
        var (strForm, strNote) = TranslationService.ParseMyMemoryResponse(
            """{"responseData":{"translatedText":"QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500 CHARS"},"quotaFinished":null,"mtLangSupported":null,"responseDetails":"QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500 CHARS","responseStatus":"403","responderId":null,"exception_code":null,"matches":[]}""");
        Assert("MyMemory/真件形状（体内 403 是字符串）⇒ 判失败，且不许把拒因当译文返回",
            strForm is null && strNote.Contains("responseStatus=403"), strNote);

        var (lenNote, lenBody) = TranslationService.ParseMyMemoryResponse(
            """{"responseData":{"translatedText":"QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500","match":1},"responseDetails":"","responseStatus":403}""");
        Assert("MyMemory/同一条拒绝写成数字 403 时也必须判失败（两种形状都收，不许只认一种）",
            lenNote is null && lenBody.Contains("responseStatus=403"), lenBody);

        var (quota, quotaNote) = TranslationService.ParseMyMemoryResponse(
            """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY","match":1},"responseStatus":200}""");
        Assert("MyMemory/额度用尽那句被写在译文栏里 ⇒ 不许当译文",
            quota is null && quotaNote.Contains("额度"), quotaNote);

        var (ok, okNote) = TranslationService.ParseMyMemoryResponse(
            """{"responseData":{"translatedText":"输出文件夹不存在。","match":0.85},"responseStatus":200}""");
        Assert("MyMemory/正常形状取到译文", ok == "输出文件夹不存在。", okNote);

        var (junk, junkNote) = TranslationService.ParseMyMemoryResponse("<html>cf challenge</html>");
        Assert("MyMemory/回的是网页 ⇒ 判失败", junk is null && junkNote.Contains("不是 JSON"), junkNote);

        // 这一条钉的是解析器自己的崩口：对 JsonElement 的 Null 值调 GetString() 抛
        // InvalidOperationException，而外层只 catch JsonException ⇒ 会变成"翻译服务炸一下"
        // 而不是"这个后端失败并降级到下一条"。形状不确定时按失败处理，绝不许抛。
        string? threw = null;
        try
        {
            var (n, note) = TranslationService.ParseMyMemoryResponse(
                """{"responseData":{"translatedText":"x"},"responseStatus":null}""");
            threw = n is null && note.Contains("responseStatus=-1") ? "" : "判成了成功/原因不含状态码：" + note;
        }
        catch (Exception ex) { threw = "THREW:" + ex.GetType().Name; }
        Assert("MyMemory/responseStatus 是 null 或缺字段 ⇒ 判失败而不是抛异常（外层只兜得住 JsonException）",
            threw == "", threw ?? "<null>");
    }

    /// <summary>
    /// 提示里引号内的那个界面控件名，必须真的存在于界面上。
    /// <para>起因是一条真实缺陷：<c>DescribeBackends</c> 让用户去勾「允许回退到非官方翻译端点
    /// （必应网页版 / Google）」，而界面上那条叫「允许回退到 Google 非官方翻译端点」——
    /// 那串带必应的字面量<b>全仓只出现在提示里</b>，界面上一处都没有。后果是闭环的：
    /// 免密钥那条路的标签指向 Google，而紧邻的说明恰好写着"Google 在国内实测超时"⇒
    /// 用户照着提示找不到勾，唯一看得见的出路只剩去填 AppID。
    /// 当时的判据只对 Core 那句话做 <c>Contains("非官方")</c> 关键词比对，两边各写一半照样全绿。</para>
    /// <para>所以这条钉的是<b>跨文件的一致性</b>：从 Core 的返回值里把【引号内的标签】抽出来，
    /// 逐个要求它在 <c>MainWindow.xaml</c> 里逐字存在。变异形态 = 把界面文案改回只提 Google，
    /// 必须当场红。<b>读不到 XAML 判红而不是 Skip</b>（理由见 <c>Test_FreeTranslateEndpointsAreOnByDefault</c>
    /// 里同一族的说明）。</para>
    /// </summary>
    static void Test_FreeEndpointHintNamesARealControl()
    {
        // 关掉开关才会走"三条路"那段提示 ⇒ 用显式关掉的配置来取这句话
        string hint = TranslationService.DescribeBackends(new LlmConfig { AllowUnofficialTranslateEndpoints = false });
        Assert("取到的是『一条都没配』那段提示（否则本卡没有输入可判）",
            hint.Contains("还没有任何可用的翻译后端"), hint);

        var labels = System.Text.RegularExpressions.Regex.Matches(hint, "「([^」]+)」")
            .Select(m => m.Groups[1].Value).ToList();
        Assert("提示里至少有一个【引号内的控件名】可判（一个都没有说明文案被改成了泛指，本卡就空转了）",
            labels.Count >= 1, $"实得 {labels.Count} 个：{string.Join(" / ", labels)}");

        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml"));
        if (xamlPath is null)
        {
            Assert("MainWindow.xaml 可读（否则『提示引用的控件名』这一格完全没判）", false,
                "FindRepoFile 没找到 MainWindow.xaml");
            return;
        }
        string xaml = File.ReadAllText(xamlPath);

        foreach (string label in labels)
            Assert($"提示里引用的控件名逐字存在于界面：{label}", xaml.Contains(label));

        // 反向半边：留痕句里引号内的名字同样必须是界面上真实的那一条
        var disabled = System.Text.RegularExpressions.Regex.Match(
            TranslationService.FreeEndpointsDisabledNote, "「([^」]+)」");
        Assert("关掉后端时的留痕句也引用真实控件名",
            disabled.Success && xaml.Contains(disabled.Groups[1].Value),
            TranslationService.FreeEndpointsDisabledNote);
    }

    /// <summary>
    /// 老文件里被写死的 <c>false</c> 怎么翻、以及<b>翻完之后门必须永久关上</b>。
    /// <para>需要迁移的只有一批人：v0.3.4 及更早是<b>整份写回</b>的，那些文件里钉着
    /// <c>AllowUnofficialTranslateEndpoints: false</c>；而 2026-09-24 起 <c>Save()</c> 只写差集，
    /// 没被碰过的键根本不落盘 ⇒ 改默认值对他们自动生效。源码哨兵钉住"迁移挂了、章盖在唯一写盘出口、
    /// 门用的是自己的标记而不是 <c>SettingsRevision</c>"这三件事——
    /// 借默认值表那个版本门会把全体用户的质量档再清一遍（<see cref="Test_GainMapMerged_FormatIndexMigration"/>
    /// 同一族教训）。</para>
    /// </summary>
    static void Test_FreeEndpointsDefaultMigrationIsWired()
    {
        var svcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SettingsService.cs"));
        var dataPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "AppSettingsData.cs"));
        if (svcPath is null || dataPath is null)
        {
            Assert("SettingsService.cs / AppSettingsData.cs 可读（迁移接线无人判）", false,
                $"svc={svcPath is not null} data={dataPath is not null}");
            return;
        }
        string svc = File.ReadAllText(svcPath);
        string data = File.ReadAllText(dataPath);

        Assert("Load 里挂了这次翻面迁移（不是只写了函数没人调）",
            svc.Contains("migrated |= MigrateFreeEndpointsDefaultOn(Current);"));
        Assert("章盖在唯一的写盘出口 Save()（与 GainMapMerged 同一处，保证写过的文件不再被翻）",
            svc.Contains("Current.FreeEndpointsMigrated = true;"));
        Assert("门是 FreeEndpointsMigrated，函数体里不许借 SettingsRevision 触发",
            svc.Contains("if (s.FreeEndpointsMigrated) return false;"));
        Assert("AppSettingsData 里有这一格标记（缺键的老文件读作 false ⇒ 迁移才会触发一次）",
            data.Contains("public bool FreeEndpointsMigrated { get; set; } = false;"));

        // 翻面之后不许有第二次：方法体内不能再有"每次都写 true"的无条件赋值
        int start = svc.IndexOf("internal static bool MigrateFreeEndpointsDefaultOn", StringComparison.Ordinal);
        Assert("迁移函数本体取得到（取不到就没法判它的形态）", start >= 0, start.ToString());
        if (start >= 0)
        {
            string body = svc[start..Math.Min(svc.Length, start + 2000)];
            Assert("迁移函数不盖章（章归 Save 盖）：体内不许出现 FreeEndpointsMigrated = true",
                !body.Contains("FreeEndpointsMigrated = true"), body[..Math.Min(body.Length, 160)]);
        }
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

        // ⑩ 接线：生产路径真的调它，且排在 Google 前面（本机实测必应 1.1–2.4 s、Google 各挂 5 s 超时，
        //     反序会让国内用户每行白等 10 秒才轮得到能用的那条）
        var path = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "TranslationService.cs"));
        if (path is null) { Skip("必应/调用侧接线", "未找到 TranslationService.cs"); return; }
        string code = CodeOnly(path);
        int bingAt = code.IndexOf("await TryBingWebEndpointAsync(", StringComparison.Ordinal);
        int googleAt = code.IndexOf("await TryGoogleMultiEndpointAsync(", StringComparison.Ordinal);
        Assert("必应/TryBingWebEndpointAsync 被降级链调用且排在 Google 前",
            bingAt >= 0 && googleAt >= 0 && bingAt < googleAt, $"bing@{bingAt} google@{googleAt}");

        // ⑩.1 2026-10-23 的完整顺序：Edge → 必应 → MyMemory → Google（全部本机实测延迟排出来的，
        //       不是按"哪家更有名"。反序的代价按行数乘：多行并发时每一行都要白等前面那几条的超时。）
        int edgeAt = code.IndexOf("await TryEdgeFreeEndpointAsync(", StringComparison.Ordinal);
        int mmAt = code.IndexOf("await TryMyMemoryEndpointAsync(", StringComparison.Ordinal);
        Assert("免费端点四条都在降级链里，且顺序是 Edge→必应→MyMemory→Google",
            edgeAt >= 0 && bingAt >= 0 && mmAt >= 0 && googleAt >= 0
            && edgeAt < bingAt && bingAt < mmAt && mmAt < googleAt,
            $"edge@{edgeAt} bing@{bingAt} mymemory@{mmAt} google@{googleAt}");

        // ⑪ 免费端点关掉 + 什么都没配 ⇒ 抛的是"没配后端"，一条请求都发不出去。
        //     这条是整轮整改的落点：以前这种情况会真的往外发请求，然后回一句"翻译失败"。
        //     ⚠ 2026-10-23 之后必须【显式关掉】才能构造这个状态 ——
        //     写 `new LlmConfig()` 现在得到的是"免费端点可用"，那条用例会当场变成
        //     "门禁联网发了一次真请求"（红不红全看网络，而绿灯时没人知道它发过）。
        var offline = new TranslationService(new LlmConfig { AllowUnofficialTranslateEndpoints = false });
        string? thrown = null;
        try { offline.TranslateAsync("Hello", "zh-CN").GetAwaiter().GetResult(); }
        catch (TranslationNotConfiguredException ex) { thrown = ex.Message; }
        catch (Exception ex) { thrown = "WrongType:" + ex.GetType().Name; }
        Assert("免费端点关掉且无凭据 ⇒ 抛 TranslationNotConfiguredException（不是网络文案、也不发请求）",
            thrown is not null && !thrown.StartsWith("WrongType:") && thrown.Contains("免费"),
            thrown ?? "<没抛>");
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
    /// 预览窗翻译扇出的两处加固（2026-10-07 全仓审查发现并修复）。
    /// <para><b>① 取消</b>：<c>TranslateAsync</c> 的签名接受 <c>CancellationToken</c>，但调用点
    /// 从来没传过，窗口内也没有任何 CTS，<c>OnClosed</c> 还是空的 ⇒ 关掉预览窗后，
    /// 最多 N 行请求继续跑到各自超时（Google/Bing 每端点 5 s），并一直占着
    /// <c>SemaphoreSlim(4)</c> 的槽位。实测影响：不崩、不损坏数据（UI 更新走
    /// <c>TryEnqueue</c> 安全 no-op），纯粹浪费网络与并发槽位。</para>
    /// <para><b>② 快照</b>：原先 <c>_translatedLines</c> 与 <c>_showTranslation</c> 分两处写，
    /// 渲染端可能读到"有译文但标志未置位"或反之的半成品。改为一次性发布不可变对象。</para>
    /// <para>各条各咬一种退化；这些都是**运行期行为**，文本级断言只能钉住接线形态，
    /// 因此这里刻意把"接线是否存在"写清楚（而不是断言行为本身）。</para>
    /// </summary>
    static void Test_OcrPreviewTranslation_CancellationAndSnapshot()
    {
        var win = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "OcrPreviewWindow.xaml.cs"));
        if (win is null) { Skip("预览窗翻译扇出加固", "未找到 OcrPreviewWindow.xaml.cs"); return; }

        string CodeOf(string path) => string.Join("\n",
            File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("*")));
        var code = CodeOf(win);

        // ── ① 取消 ──
        Assert("预览窗必须持有 CancellationTokenSource",
            code.Contains("CancellationTokenSource"), "没有 CTS ⇒ 关窗后无法取消在途翻译");
        Assert("关窗（OnClosed）必须调用 Cancel",
            Regex.IsMatch(code, @"OnClosed[\s\S]{0,400}?\.Cancel\(\)"),
            "OnClosed 为空 ⇒ 关窗后在途请求继续跑到各自超时");
        Assert("行翻译必须把 token 传进 TranslateAsync（第 4 个实参）",
            Regex.IsMatch(code, @"TranslateAsync\([^)]*,\s*null\s*,\s*ct\s*\)"),
            "不传 ct ⇒ 请求不会随窗口关闭而中止");
        Assert("信号量等待必须带 token（否则关窗后仍在排队等槽位）",
            code.Contains("WaitAsync(ct)"), "漏 token ⇒ 扇出要等前一批跑完才退出");
        Assert("视觉路也要传 token",
            Regex.IsMatch(code, @"TranslateImageWithVisionAsync\([^)]*ct\s*\)"),
            "视觉路不传 ct ⇒ 上传大图时关窗无法中止");

        // ⚠️ 关键：行级 catch 若先吞掉 OperationCanceledException，
        //    取消会被当成"这一行翻译失败"，firstError 写成"操作已取消"，
        //    关窗于是在状态栏显示成"某行失败"。这条正是我修第一版时漏掉的。
        Assert("行级 catch 必须让 OperationCanceledException 先抛出去（不许被当成行失败）",
            Regex.IsMatch(code, @"catch\s*\(\s*OperationCanceledException\s*\)\s*\{\s*throw;\s*\}"),
            "取消被通用 catch 吞掉 ⇒ 关窗被报成'某行翻译失败'");
        Assert("扇出外层要有 OperationCanceledException 分支（取消不是错误，别报成翻译失败）",
            Regex.IsMatch(code, @"catch\s*\(\s*OperationCanceledException\s*\)"));

        // ── ② 快照 ──
        // ⚠️ 2026-10-07 变异测试发现的弱点：原先这条写成
        //      code.Contains("record TranslationSnapshot") && code.Contains("_translation = new TranslationSnapshot(")
        //    把类型名改掉（record TranslationSnapshotRenamed）时，第一子句失败但第二子句
        //    因为 "new TranslationSnapshot(" 是 "new TranslationSnapshotRenamed(" 的前缀**仍然为真**
        //    ⇒ 整体判据保持绿色。两子句不是独立证据，&& 被前缀关系击穿。
        //    改为对**类型名本身**做词边界匹配，让"改名字"这类变异必红。
        Assert("译文与显示标志必须合成一个不可变快照（不再分两处写）",
            Regex.IsMatch(code, @"\brecord\s+TranslationSnapshot\b")
            && Regex.IsMatch(code, @"new\s+TranslationSnapshot\s*\("),
            "退回两个独立字段 ⇒ 渲染端可能读到半成品");
        Assert("不得再直接读写 _translatedLines（已全部迁移到快照）",
            !code.Contains("_translatedLines"),
            "残留 _translatedLines ⇒ 两条数据源并存");
        Assert("取消后不得再发布结果（窗口已关，发布了也只是改内存）",
            code.Contains("ThrowIfCancellationRequested"));
    }

    /// <summary>
    /// "每个自有源码文件都带 SPDX 声明"这句对外口径的判据（2026-10-22 补）。
    /// <para><b>为什么补这条</b>：索引里长期写着"464 个跟踪文件都已带头"，本轮实测
    /// 自有文件 261 个里<strong>有 87 个根本没带头</strong> —— 一句没有判据背书的对外声明必然腐烂，
    /// 而它腐烂的方式是"文档说合规、仓库不合规"，读文档的人无法察觉。</para>
    /// <para>口径与 <c>scripts/add-spdx-headers.py</c> 同源：扩展名 <c>.cs/.ps1/.py/.c/.h/.hlsl</c>，
    /// 判据是"文件前 4000 字节内出现 <c>SPDX-License-Identifier:</c>"。
    /// 上游逐字副本（VSCap 的 <c>upstream/</c> 子树）由上游自己声明许可，不在口径内 ——
    /// 那条豁免本身也要被盯住：本轮就有一次误盖，把 Apache 章插进了上游的 <c>DesktopCapture.h</c>，
    /// 既误署版权方又破坏 INDEX.json 里"逐字副本"的声明。</para>
    /// </summary>
    static void Test_SpdxHeaders_OwnSourceFilesAreStamped()
    {
        const string card = "SPDX 头覆盖";
        var tool = FindRepoFile(Path.Combine("scripts", "add-spdx-headers.py"));
        if (tool is null) { Skip(card, "定位不到 add-spdx-headers.py ⇒ 口径锚点没了"); return; }
        var repoRoot = Directory.GetParent(tool.Replace('/', Path.DirectorySeparatorChar))!.Parent!.FullName;

        // 谓词单独抽出来，好让"它自己有没有牙"成为一条可判的断言（否则整卡可能空转）
        static bool HasHeader(string text) =>
            text.Contains("SPDX-License-Identifier:", StringComparison.Ordinal);

        Assert("谓词两臂必须分叉：有头的认得出、没头的不许认成有头",
            HasHeader("// SPDX-License-Identifier: Apache-2.0") && !HasHeader("#pragma once\r\n#include <QImage>"),
            "两臂同真或同假 ⇒ 这把尺子没有牙");

        string[] exts = [".cs", ".ps1", ".py", ".c", ".h", ".hlsl"];
        string vendored = "src/TrueToneCap.VSCap/upstream/";

        // 口径 = 跟踪清单，不是磁盘遍历。实测差异不是装饰性的：磁盘上另有 85 个
        // 被 .gitignore 挡住的旧 harness 没有头（它们根本不属于这个仓库），按磁盘遍历会把
        // 判据变成"永远红"，进而被人当噪声删掉。
        var tracked = new List<string>();
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "ls-files")
            {
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var gp = System.Diagnostics.Process.Start(psi);
            if (gp is null) throw new InvalidOperationException("Process.Start 返回 null");
            while (gp.StandardOutput.ReadLine() is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line)) tracked.Add(line.Trim().Replace('\\', '/'));
            }
            gp.WaitForExit(15000);
        }
        catch (Exception ex)
        {
            // fail-closed：读不到跟踪清单就是"判据失明"，绝不允许落成绿
            Assert("git ls-files 可用（清单读不到时本卡没有牙齿）", false, ex.Message);
            return;
        }

        var scanned = new List<string>();
        var offenders = new List<string>();
        foreach (var rel in tracked)
        {
            if (!exts.Contains(Path.GetExtension(rel).ToLowerInvariant())) continue;
            if (rel.StartsWith(vendored, StringComparison.Ordinal)) continue;   // 上游逐字副本豁免
            var abs = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(abs))
            {
                offenders.Add(rel + "（在跟踪清单里但磁盘上没有）");
                continue;
            }
            byte[] head;
            try
            {
                using var fs = File.OpenRead(abs);
                head = new byte[Math.Min(4000, fs.Length)];
                int got = 0;
                while (got < head.Length)
                {
                    int n = fs.Read(head, got, head.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
            }
            catch (Exception ex) { offenders.Add(rel + "（读失败：" + ex.GetType().Name + "）"); continue; }
            scanned.Add(rel);
            if (!HasHeader(System.Text.Encoding.UTF8.GetString(head))) offenders.Add(rel);
        }

        // 枚举塌空 ⇒ 下面那条"零违规"会恒真，这是本仓踩过的最大假绿源
        Assert("扫描到至少 200 个自有源码文件（枚举塌空等于判据失明）",
            scanned.Count >= 200, $"实得 {scanned.Count} 个");
        Assert("每个自有源码文件前 4000 字节内都有 SPDX 声明",
            offenders.Count == 0,
            $"缺头 {offenders.Count} 个：{string.Join(", ", offenders.Take(8))}"
            + "（补法：py scripts/add-spdx-headers.py）");

        // 反向哨兵：豁免必须是真的豁免（那个文件确实存在且确实没被我们的章污染），
        // 否则"upstream/ 不盖章"只是一句可以随时被扩大成"什么都不查"的空话。
        var upstreamFile = Path.Combine(repoRoot, "src", "TrueToneCap.VSCap", "upstream",
            "src", "cap", "DesktopCapture.h");
        if (File.Exists(upstreamFile))
            Assert("上游逐字副本保持自己的许可（我们的章不许出现在 upstream/ 里）",
                !HasHeader(File.ReadAllText(upstreamFile)),
                "DesktopCapture.h 里出现了 SPDX 声明 ⇒ 已误署版权方");
        else
            Assert("上游逐字副本仍在仓库里（豁免对象消失要点名，不能静默少一块）",
                false, $"未找到 {upstreamFile}");
    }

    /// <summary>
    /// 日志目录必须按运行形态分支（2026-10-07 补）。
    /// <para><b>为什么补这条</b>：我改了 <c>LogService</c> 让它按 <c>PackageRuntime.IsPackaged</c>
    /// 选目录，却**没有为它加任何判据** —— 这是变异测试抓出来的：把
    /// <c>PackageRuntime.IsPackaged</c> 替换成常量 <c>false</c>（等于废掉整个修复）后，
    /// 单元测试**仍然全绿**。一条没有判据保护的修复，下次重构就会被无声删掉。</para>
    /// <para>背景：MSIX 包目录只读，原来日志写 <c>&lt;程序目录&gt;\log</c> 会失败并落到
    /// <c>%TEMP%</c> 兜底 —— 不丢日志但位置反直觉（用户在程序目录找不到 log/）。
    /// 修复后打包形态写 <c>%LOCALAPPDATA%\TrueToneCap\log</c>。</para>
    /// </summary>
    static void Test_LogDirectory_PackagedBranch()
    {
        var f = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LogService.cs"));
        if (f is null) { Skip("日志目录按运行形态分支", "未找到 LogService.cs"); return; }

        // 滤掉注释：否则判据会被说明文字里的同名字样满足（这是本仓多次踩过的坑）
        var code = string.Join("\n",
            File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//") && !l.TrimStart().StartsWith("*")));

        Assert("日志目录必须按 PackageRuntime.IsPackaged 分支",
            code.Contains("PackageRuntime.IsPackaged"),
            "不分支 ⇒ MSIX 下仍写包目录（只读）⇒ 退化成 %TEMP% 兜底，位置反直觉");
        Assert("打包形态的日志目录落在 %LOCALAPPDATA%\\TrueToneCap\\log（与设置/密钥同处）",
            code.Contains("LocalApplicationData") && code.Contains("\"log\""),
            "日志若写别处，用户按文档找不到");
        Assert("未打包形态仍写程序目录下的 log（绿色包语义不变）",
            code.Contains("BaseDirectory") && code.Contains("\"log\""),
            "改掉这一支会破坏绿色包的'日志跟着程序走'语义");
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
        // ⚠ 2026-10-23 起"什么都没填"不再等于"没有可用后端"：免费公共端点默认开着。
        //    所以这一族判据里的"空配置"一律显式写成 `免费端点关掉` ——
        //    留着原来的 `new LlmConfig()` 会得到两种错：可用/不可用判反，
        //    以及最后那条"必须在发请求之前就抛"变成"门禁真的联网发了一次请求"。
        static LlmConfig NoneConfigured() => new() { AllowUnofficialTranslateEndpoints = false };

        Assert("免费端点关掉且无凭据 ⇒ 判定为无可用后端", !TranslationService.AnyBackendAvailable(NoneConfigured()));
        Assert("只有 AppKey 没有 AppSecret ⇒ 仍算没有（有道会被跳过，不许声称可用）",
            !TranslationService.AnyBackendAvailable(new LlmConfig { YoudaoAppKey = "k", AllowUnofficialTranslateEndpoints = false }));
        Assert("AppKey + AppSecret ⇒ 可用",
            TranslationService.AnyBackendAvailable(new LlmConfig { YoudaoAppKey = "k", YoudaoAppSecret = "s" }));
        Assert("开了 LLM 但没填端点 ⇒ 不算可用",
            !TranslationService.AnyBackendAvailable(new LlmConfig { UseCustomLlm = true, AllowUnofficialTranslateEndpoints = false }));
        Assert("LLM + 端点 ⇒ 可用",
            TranslationService.AnyBackendAvailable(new LlmConfig { UseCustomLlm = true, ApiEndpoint = "https://api.deepseek.com/v1" }));
        Assert("免费公共端点开着 ⇒ 可用（这就是 2026-10-23 之后的默认状态）",
            TranslationService.AnyBackendAvailable(new LlmConfig { AllowUnofficialTranslateEndpoints = true }));

        string empty = TranslationService.DescribeBackends(NoneConfigured());
        Assert("没配时的说明给出三条出路（免费端点 / 有道 / LLM）",
            empty.Contains("免费公共") && empty.Contains("有道") && empty.Contains("LLM"), empty);
        Assert("没配时的说明不甩锅网络", !empty.Contains("网络问题"), empty);
        Assert("免费端点那条写清代价（文字发给第三方），且引用的控件名界面上真有（见 Test_FreeEndpointHintNamesARealControl）",
            empty.Contains("第三方"), empty);
        string configured = TranslationService.DescribeBackends(
            new LlmConfig { YoudaoAppKey = "k", YoudaoAppSecret = "s" });
        Assert("配了之后的说明切成「已启用哪些 + 那是真失败」口径",
            configured.Contains("有道官方 API") && configured.Contains("没返回结果"), configured);

        // 空配置必须在**发请求之前**就抛，而不是悄悄联网试一次
        string outcome;
        try
        {
            new TranslationService(NoneConfigured()).TranslateAsync("hello", "zh-CN")
                .GetAwaiter().GetResult();
            outcome = "NO-THROW";
        }
        catch (TranslationNotConfiguredException ex) { outcome = ex.Message; }
        catch (Exception ex) { outcome = "WRONG-TYPE:" + ex.GetType().Name; }
        Assert("无任何可用后端时直接抛 TranslationNotConfiguredException（带三条出路）",
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
    /// 着色器清单不许有孤儿条目（A 档回归，2026-10-01）。
    /// <para>为什么只能钉源码：.cso 是构建产物、不入库（见 App.csproj 的 CollectCompiledShaders 说明），
    /// "运行时到底加载不加载"要开预览才有读数；而这条锁要防的恰恰是<b>声明与实现分家</b>——
    /// <c>ShaderCompiler.cs</c> 的注释曾长期写着 <c>ToneMapping.hlsl ✅ 存在，运行时消费</c>，
    /// 但它唯一的加载方 <c>GpuToneMapper</c> 自 2026-09-16 起不再构造
    /// （<c>AppServices.cs</c> 自己写着"【死代码】…属性全仓没有任何调用者"）。</para>
    /// <para>三张名单各自独立地取：清单来自 <c>ShaderCompiler.cs</c> 的 <c>new("X.hlsl", …)</c>，
    /// 磁盘来自 <c>Shaders/*.hlsl</c> 目录列举，构建期来自 <c>App.csproj</c> 的 CompileShaders Inputs——
    /// 两两比对是单向锁，所以三张都要对。消费点判据只认<b>剥掉整行注释之后</b>的代码，
    /// 否则一句"我们曾查找 X.cso"的注释就能把自己证成活的。</para>
    /// </summary>
    static void Test_ShaderList_HasNoOrphanEntry()
    {
        const string card = "着色器清单无孤儿条目";
        var scPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "ShaderCompiler.cs"));
        if (scPath is null) { Skip(card, "未找到 ShaderCompiler.cs"); return; }

        var sc = StripLineComments(ReadSourceNormalized(scPath));
        var listed = Regex.Matches(sc, @"new\(""([^""]+\.hlsl)""").Select(m => m.Groups[1].Value)
                         .OrderBy(x => x, StringComparer.Ordinal).ToList();
        // 守卫必须 fail-closed：清单若为空（锚点写错/清单被挪走），下面每条"不许出现 X"的缺席断言都恒真。
        Assert("着色器清单至少要有 2 条（空清单会让本卡全部判据失去牙齿）",
            listed.Count >= 2, $"实得 {listed.Count} 条：{string.Join(",", listed)}");

        var appDir = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(scPath)!, ".."));
        var coreDir = Path.GetFullPath(Path.Combine(appDir, "..", "TrueToneCap.Core"));
        var scanDirs = new[] { appDir, coreDir };

        // ① 清单 ↔ 磁盘
        var shaderDir = Path.Combine(appDir, "Shaders");
        var onDisk = Directory.Exists(shaderDir)
            ? Directory.GetFiles(shaderDir, "*.hlsl").Select(p => Path.GetFileName(p)!)
                       .OrderBy(x => x, StringComparer.Ordinal).ToList()
            : new List<string>();
        Assert("清单与 Shaders/*.hlsl 逐字一致（谁多谁少都算漂移）",
            string.Join(",", listed) == string.Join(",", onDisk),
            $"清单={string.Join(",", listed)} 磁盘={string.Join(",", onDisk)}");

        // ② 清单 ↔ 构建期 Inputs（增量构建时 CompileShaders.ps1 那份四方自检可能根本不跑）
        var csprojPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "TrueToneCap.App.csproj"));
        var csprojInputs = new List<string>();
        if (csprojPath is not null)
        {
            var m = Regex.Match(ReadSourceNormalized(csprojPath),
                "Name=\"CompileShaders\"[^>]*Inputs=\"([^\"]+)\"");
            if (m.Success)
                csprojInputs = m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => Path.GetFileName(p.Replace('\\', '/'))).Distinct()
                    .OrderBy(x => x, StringComparer.Ordinal).ToList();
        }
        Assert("App.csproj 的 CompileShaders Inputs 与清单一致",
            csprojInputs.Count == listed.Count && !listed.Except(csprojInputs).Any()
            && !csprojInputs.Except(listed).Any(),
            $"csproj={string.Join(",", csprojInputs)} 清单={string.Join(",", listed)}");

        // ③ 每条清单都要有"按名字找字节码"的消费点；清单自己那份不算消费方
        foreach (var entry in listed)
        {
            var hits = ScanCode(scanDirs, $"\"{entry}.cso\"", exceptFiles: new[] { scPath });
            Assert($"清单条目 {entry} 有运行时消费点", hits.Count >= 1,
                hits.Count == 0 ? "剥注释后 App/Core 无人按名字找它的 .cso" : $"命中：{string.Join(",", hits)}");
        }

        // ④ 反向哨兵：只删资产、留下指向它的代码，比原地留着更坏
        Assert("ToneMapping.hlsl 不在清单里", !listed.Contains("ToneMapping.hlsl"));
        Assert("Shaders/ 下没有 ToneMapping.hlsl 源文件",
            !File.Exists(Path.Combine(shaderDir, "ToneMapping.hlsl")));
        var orphan = ScanCode(scanDirs, "ToneMapping.hlsl", exceptFiles: Array.Empty<string>());
        Assert("无人加载的 ToneMapping.hlsl 不许被任何代码引用", orphan.Count == 0,
            $"命中：{string.Join(",", orphan)}");
    }

    /// <summary>在给定工程目录里找"剥掉整行注释后仍含某字面量"的文件（跳过 obj/bin，返回文件名）。</summary>
    static List<string> ScanCode(string[] dirs, string literal, string[] exceptFiles)
    {
        var skip = new HashSet<string>(exceptFiles.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var hits = new List<string>();
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var cs in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(cs);
                if (full.Contains("\\obj\\") || full.Contains("\\bin\\")
                    || full.Contains("/obj/") || full.Contains("/bin/")) continue;
                if (skip.Contains(full)) continue;
                if (StripLineComments(ReadSourceNormalized(cs)).Contains(literal)) hits.Add(Path.GetFileName(cs));
            }
        }
        return hits;
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
    /// 「换格式保底开关」必须【从界面一路接到编码器】——本条把这条链的每一段都钉住。
    /// <para>来历（2026-09-29 用户口径）：接上 PNG 保底 ⇒ "触发一定要告诉用户" ⇒ "给保底做一个开关在用户设置里面"。
    /// 三件事分别落在四个文件加 Core，任何一段接不上，用户看到的都是一个假旋钮：
    /// 界面有勾但产品照旧换格式 / 关掉之后仍然偷偷落 PNG / 英文界面剩中文。</para>
    /// <para>为什么这一条只能是源码锁：Core 侧的行为矩阵（<c>ColorPipelineTests.JpegLiAndJxl_BackendFailure_NeverClaimsSuccess</c>，
    /// 5 格式 × 开关两态 = 10 格 / 45 条断言）判的是【编码器认不认这个布尔】；
    /// 而"设置页那个勾到布尔之间"这一截全在 App 工程里，<c>Test</c> 只引用 Core，运行时够不到 ⇒
    /// 这里钉接线，行为那半边由矩阵钉。<b>两段合起来才叫这条链被判过。</b></para>
    /// </summary>
    static void Test_PngFallbackSwitch_Wired()
    {
        string Read(params string[] rel) => relaggregate(rel);
        static string relaggregate(string[] rel)
        {
            var p = FindRepoFile(Path.Combine(rel));
            return p is null ? "" : File.ReadAllText(p);
        }

        // ═══ 定位失败必须先红，不能让下面的负断言空转 ═══
        // Read 找不到文件返回 ""：正向锁（Contains）会红，但【负向】锁
        // （!bench.Contains / badWriters.Count == 0 / 闸的处数下限之外的"不许出现"那一类）
        // 在空串上恒成立 ⇒ 文件一丢就集体假绿。这一轮 §11.6 数过 53 条"缺文件即 Skip"的失明，
        // 同一个道理不能在自己新写的锁里再犯一遍（本文件 2186 那段注释立的规矩：找不到就是 ❌）。
        var needed = new (string Label, string[] Rel)[]
        {
            ("Core/FormatEncoders.cs", ["src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"]),
            ("Core/JpegGainMapEncoder.cs", ["src", "TrueToneCap.Core", "Encoding", "JpegGainMapEncoder.cs"]),
            ("App/Models/AppSettingsData.cs", ["src", "TrueToneCap.App", "Models", "AppSettingsData.cs"]),
            ("App/Services/CapturePipelineService.cs", ["src", "TrueToneCap.App", "Services", "CapturePipelineService.cs"]),
            ("App/MainWindow.xaml.cs", ["src", "TrueToneCap.App", "MainWindow.xaml.cs"]),
            ("App/MainWindow.Settings.cs", ["src", "TrueToneCap.App", "MainWindow.Settings.cs"]),
            ("App/MainWindow.xaml", ["src", "TrueToneCap.App", "MainWindow.xaml"]),
            ("App/Services/LocaleManager.cs", ["src", "TrueToneCap.App", "Services", "LocaleManager.cs"]),
            ("Tools/FormatBench.cs", ["src", "TrueToneCap.Tools", "FormatBench.cs"]),
            ("App/Services/SelfTest.cs", ["src", "TrueToneCap.App", "Services", "SelfTest.cs"]),
        };
        var misses = needed.Where(n => FindRepoFile(Path.Combine(n.Rel)) is null).Select(n => n.Label).ToList();
        Assert($"保底开关这条链的 {needed.Length} 个源文件都要定位到（实得缺 {misses.Count} 个）", misses.Count == 0,
            "缺的那几个文件上的【负向】锁在空串里恒成立 ⇒ 先拦，不许拿假绿当读数：" + string.Join("；", misses));
        if (misses.Count > 0) return;

        var core = Read("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs");
        var gm = Read("src", "TrueToneCap.Core", "Encoding", "JpegGainMapEncoder.cs");
        var model = Read("src", "TrueToneCap.App", "Models", "AppSettingsData.cs");
        var pipe = Read("src", "TrueToneCap.App", "Services", "CapturePipelineService.cs");
        var main = Read("src", "TrueToneCap.App", "MainWindow.xaml.cs");
        var sett = Read("src", "TrueToneCap.App", "MainWindow.Settings.cs");
        var xaml = Read("src", "TrueToneCap.App", "MainWindow.xaml");
        var loc = Read("src", "TrueToneCap.App", "Services", "LocaleManager.cs");

        Assert("App 的设置项存在且【默认开】（默认翻成关就是拿一次审查改变所有老用户的行为）",
            model.Contains("public bool PngFallbackEnabled { get; set; } = true;"), "");

        // ── 开关只有一个写者 ──
        // AvifPngSuffix 那种"快照字段 + 控件位"双写是前车之鉴：同一参数两个写者时，
        // 只测其中一半会留下另一半的漂移（本轮 §14 刚为此撤回过一把尺子）。
        Assert("管线侧把这个字段从设置快照接进 EncodingSettings",
            pipe.Contains("PngFallbackEnabled = s.PngFallbackEnabled"), "");
        // ═══ 测量宿主【不许】碰这个开关（默认必须是关）═══
        // 保底会把"编码器失败"洗成"产物存在"，而外部裁判普遍按"那个文件在不在"读结果。
        // 实测过一次：把 EncodingSettings 的初始值定成 true，param_sweep_check 的
        // c_avifsvt_444 就从"按预期失败"翻成"竟然成功了"（真因只是多了一枚 .png）。
        // ⇒ 这条把"测量路径永远关"钉住：Tools 与 SelfTest 里出现这个字段名就是有人在
        //   给测量宿主打开保底（或至少在读它），那必须先说清楚为什么。
        var bench = Read("src", "TrueToneCap.Tools", "FormatBench.cs");
        var st = Read("src", "TrueToneCap.App", "Services", "SelfTest.cs");
        Assert("测量宿主（Tools / 审计卡）不提这个开关 ⇒ 它们永远拿 EncodingSettings 的关默认值",
            !bench.Contains("PngFallbackEnabled") && !st.Contains("PngFallbackEnabled = true"),
            "宿主一旦自己打开保底，'编码器坏了'就会以'产物存在'的形式被裁判读成成功");
        // 但审计卡【必须】把这个布尔判到两个方向。原先它只拿 snap 自己的值和落点比 ⇒ 单向锁：
        // EncodingSettings 的默认是【关】，用户也关着时，把管线里那行接线删掉两侧也都是关 ⇒ 卡片照样
        // "一致 ✓"，最该判的那一格没有牙（2026-09-29 审查轮从 App lane 抓回来，增益档那一格同病）。
        // ⇒ 现在两趟探针（正向 snap + 反向取反快照）的形状钉在这里，取值对不对仍由 --display-audit 的退出码判。
        Assert("审计卡的保底开关探针两个方向都跑（正向 snap + 反向取反快照）",
            st.Contains("flipped.PngFallbackEnabled = !snap.PngFallbackEnabled")
            && st.Contains("probeEsFlipped.PngFallbackEnabled"),
            "退回单向比较 = 关态那一格零判据；删掉取反那趟 = 只有盘上恰好开着时才判得到");
        // ═══ 台账 §17.6 第 1 条（2026-09-30 接手轮）：落点·全局这一段与"哪块屏"无关 ⇒ 必须排在
        // 逐屏循环【之外】。钉的是【顺序】而不是"有没有这句话"（同 Test_OverlayReentryLatch 的写法），
        // 挪回循环里 = 下面三刀各红一次。为什么值得钉：
        //   ① 卡片末尾"管线落点判红 N 条"的那个 N，在多屏那天会把同一句分叉数成 N 条 ⇒ N 不是缺陷数；
        //   ② 这一段里的 Load() 不是纯读 —— 迁移门触发时它末尾会 Save()、还会写 SecretStore，
        //      而审计跑在单实例锁之前 ⇒ 每块屏重复一遍"可能改用户盘"的动作。
        // ⚠ 取下标前必须剥整行注释：不剥的话，注释散文里出现的锚文本会把下标提前，
        //   这条锁就此恒真（§17.2 记过同一类"哨兵读到注释"的错）。
        var stPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SelfTest.cs"));
        var stCode = stPath is null ? "" : StripLineComments(ReadSourceNormalized(stPath));
        int idxLoad = stCode.IndexOf("settingsSource.Load()", StringComparison.Ordinal);
        int idxLoop = stCode.IndexOf("foreach (var d in displays)", StringComparison.Ordinal);
        Assert("落点·全局的两处锚点都还在（Load 与逐屏循环头）", idxLoad >= 0 && idxLoop >= 0,
            $"实得 Load={idxLoad} 循环头={idxLoop}（少一个说明锚点漂了，不是代码坏了）");
        Assert($"Load() 必须【先于】逐屏循环（实得 {idxLoad} < {idxLoop}）= 整轮只读一次盘",
            idxLoad >= 0 && idxLoop >= 0 && idxLoad < idxLoop,
            "挪回循环里 = 多屏那天把 Load 的写盘副作用重复 N 次（它迁移门触发时会 Save 并写 SecretStore）");
        Assert("SelfTest.cs 全文件只许一处 Load()（不许按屏各读一份）",
            System.Text.RegularExpressions.Regex.Matches(stCode, @"\.Load\(\s*\)").Count == 1,
            "第二处 = 有人把落点段复制回逐屏循环 ⇒ 判红条数会按屏数吹大");
        Assert("落点判红的标签：全局那两条不许带屏名",
            stCode.Contains("全局·保底开关") && stCode.Contains("全局·增益档反向")
            && !stCode.Contains("{d.Name}·保底开关") && !stCode.Contains("{d.Name}·增益档反向"),
            "带 d.Name = 同一句分叉按屏数各记一条 ⇒ '判红 N 条'不再是缺陷数");
        // 这句"你的设置与默认值在这几根轴上相同 ⇒ 差分读数不判别"讲的是【设置侧】，与屏无关。
        // 第一版我只挪了全局那一份，循环里那份漏了 ⇒ 卡片上同句出现两次（本机 1 屏；两屏就是三次）。
        // 是补出来的切片读数（audit-case 的逐屏段）抓到的，不是推出来的 ⇒ 钉成"全文件恰好一处"。
        int sameAxis = System.Text.RegularExpressions.Regex.Matches(
            stCode, System.Text.RegularExpressions.Regex.Escape("你这份设置与默认值在【这几根轴】")).Count;
        Assert($"同轴警告那句在 SelfTest.cs 里恰好一处（不许按屏各印一遍，实得 {sameAxis} 处）",
            sameAxis == 1, "两处 = 循环里还留着一份 ⇒ 同一句警告按屏数重复");
        // MainWindow 里允许出现 <c>_settings.PngFallbackEnabled = …</c>（那是"保存用户选择"，
        // 是这条链的上游），不许出现的是把值盖进【EncodingSettings】的那一句。
        // 第一版这里写成 !main.Contains("settings.PngFallbackEnabled =")，被自己的
        // <c>_settings.PngFallbackEnabled =</c> 当子串命中 ⇒ 假红了一条根本不存在的写者。
        var badWriters = System.Text.RegularExpressions.Regex
            .Matches(main, @"(\w+)\.PngFallbackEnabled\s*=")
            .Where(m => m.Groups[1].Value != "_settings")
            .Select(m => m.Value).ToList();
        Assert("MainWindow 那份 BuildEncodingSettings【不】把这个字段盖进编码设置（写者只许管线那一处）",
            badWriters.Count == 0, string.Join("；", badWriters));

        // ── Core 侧每条换格式腿都要认这个闸 ──
        // 模式必须锚在 `when (` 上：原先写的是宽泛的 `(s|settings)\.PngFallbackEnabled`，
        // 于是文档注释里的 `<see cref="EncodingSettings.PngFallbackEnabled"/>` 也被算成一处读闸
        //（本轮我自己新写的那段注释当场把数字吹大了一格）。注释不是判据，读闸的是那条 catch 的守卫。
        int gates = System.Text.RegularExpressions.Regex.Matches(core, @"when \(\s*s\.PngFallbackEnabled").Count
                  + System.Text.RegularExpressions.Regex.Matches(gm, @"when \(\s*settings\.PngFallbackEnabled").Count;
        Assert($"换格式保底的每一腿都读这个闸（实得 {gates} 处 catch 守卫，五格式 × SDR/HDR 入口 ≥ 7）", gates >= 7,
            "少一处 = 那条腿的开关是假的（关掉仍会偷偷改存 PNG），行为矩阵也只覆盖了一部分");

        // ── 界面侧：控件、回填、保存、事件分支，四段缺一不可 ──
        Assert("XAML 里有这个开关控件", xaml.Contains("x:Name=\"PngFallbackChk\""), "");
        Assert("ApplySettingsToUI 回填这个勾（否则界面显示与盘上值不一致，用户看不出自己关没关）",
            sett.Contains("PngFallbackChk.IsChecked = _settings.PngFallbackEnabled"), "");
        Assert("SaveSettings 读回这个勾", sett.Contains("_settings.PngFallbackEnabled = PngFallbackChk.IsChecked == true"), "");
        Assert("OnPerFormatChanged 里有这个控件的分支（漏了就是：勾能点、值不落盘）",
            main.Contains("cb.Name == nameof(PngFallbackChk)"), "");

        // ── 英文界面：XAML 那两串中文必须在 ZhToEn 表里有一对一的译文 ──
        // 逐条按【XAML 实际字面量】去表里查，而不是数条数 —— 改了文案忘了改表，
        // 英文模式下就是残留中文（本仓 §55 那条扫描面判据的同族）。
        var zhLiterals = System.Text.RegularExpressions.Regex.Matches(xaml,
                "x:Name=\"PngFallback(?:Chk|Hint)\"[\\s\\S]{0,600}?(?:Content|Text)=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToList();
        Assert($"从 XAML 取到保底开关的中文文案（实得 {zhLiterals.Count} 条，应为 2）", zhLiterals.Count == 2,
            "取不到 = 控件结构被改，下面的漏翻锁就此失去对象 ⇒ 这条必须先红");
        // 查表要先切出【表窗口】：整份 LocaleManager.cs 里 `["…"]` 这种形状不止表里有（别的字典/数组
        // 同样长这样），拿全文件当表 ⇒ 一条根本不在表里的键也能"有对照"。更强的整表尺子在
        // Test_UiChineseLocalizationCoverage（解析全表 + 扫 XAML 全部中文），这一处只钉这两串，
        // 所以窗口必须自己切对，并自证切到了。
        int tabAt = loc.IndexOf("ZhToEn =", StringComparison.Ordinal);
        int tabEnd = tabAt < 0 ? -1 : loc.IndexOf("};", tabAt, StringComparison.Ordinal);
        var tableWindow = tabAt >= 0 && tabEnd > tabAt ? loc.Substring(tabAt, tabEnd - tabAt) : "";
        Assert("从 LocaleManager.cs 切到 ZhToEn 表窗口（切不到就等于没有查表的尺子）", tableWindow.Length > 0,
            $"ZhToEn = 命中位置 {tabAt}，收尾那一处 }}; 位置 {tabEnd}");
        foreach (var zh in zhLiterals)
            Assert($"保底开关文案在 ZhToEn 表里有英文对照：{zh[..Math.Min(18, zh.Length)]}…",
                tableWindow.Contains($"[\"{zh}\"]"), "");
    }

    /// <summary>
    /// App 侧自检「被写了」不等于「被跑了」——本条把 <c>RunNormalizeGateCheck</c> 钉进两个入口。
    /// <para>起因（本轮批次 E）：为了让 <c>Normalize</c> 的越界回落不再是"读码台账"，
    /// 在 <c>SelfTest.cs</c> 里新写了一条直调生产分支的自检。而这类"自检函数"最省事的失效方式
    /// 不是写错，是<b>没人调用</b>：函数留着、编译通过、门禁全绿、判据一次没跑。
    /// 本仓 §11.6 数过 53 条源码哨兵"缺文件即 Skip"＝集体失明，同一个道理：
    /// 接线必须自己也是一条会红的判据。</para>
    /// <para>刻意用断言而不是 Skip：文件找不到就是 <c>❌</c>。Skip 只在"这轮没判"时用，
    /// 而这两个文件在仓库里，找不到只可能是路径变了或文件被删 —— 那正是必须拦住的情形。</para>
    /// </summary>
    static void Test_AppSelfChecks_AreCalled()
    {
        var stPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "SelfTest.cs"));
        Assert("能在仓库里定位到 SelfTest.cs（定位不到就是接线哨兵失明）", stPath is not null,
            stPath is null ? "FindRepoFile 未命中 src/TrueToneCap.App/Services/SelfTest.cs" : stPath);
        if (stPath is null) return;
        var st = File.ReadAllText(stPath);

        // ① 两个入口都得调它：`--settings-migration`（毫秒级随手复验）与 `--selftest`（CI 与出包）。
        // 数的是【调用点】而不是某一种写法：第一版这里钉的是 `RunNormalizeGateCheck(outDir).Status == "fail"`
        // 这一串，而 --selftest 那侧是 `var ng = RunNormalizeGateCheck(outDir);` 先接住再判 ——
        // 于是它把自己【没匹配到】读成"没接线"，当场红了一次。红得对（那时确实只接了一个入口），
        // 但尺子必须认得出两种等价写法，否则下次换个写法它就哑了。
        int called = Regex.Matches(st, @"RunNormalizeGateCheck\(\s*outDir\s*\)").Count;
        Assert($"RunNormalizeGateCheck 被两个入口点名（实得 {called} 处）", called >= 2,
            called == 1 ? "只有 --settings-migration 或只有 --selftest 会跑它 ⇒ 另一个入口零判据"
                        : "没有任何入口调用 ⇒ 这条自检是死代码，门禁永远全绿而它一次没跑");

        // ② 读数必须进 selftest.json：只在控制台打印的话，CI 失败时没人能对着机器读差异
        Assert("NORMALIZE 这条进了 selftest.json（JsonEntry 注册）",
            Regex.IsMatch(st, @"JsonEntry\(\s*""NORMALIZE"""), st.Length.ToString());

        // ③ 它必须【直调生产分支】，而不是在自检里"算一遍应该得什么"——后者是恒等式，改坏产品不会红
        // 地板贴着【当前夹具枚数】（2026-09-29 审查轮数过：oob/keep/cased/upper/legacy/legacyLower/csIdx = 7 枚，
        // 原先写 >= 4 时实际已是 6 枚 ⇒ 悄悄删两枚夹具这把尺子也不响；标签还写着"四枚"，与实况差两枚）。
        int prodCalls = Regex.Matches(st, @"SettingsService\.Normalize\(").Count;
        Assert($"自检真调 SettingsService.Normalize（实得 {prodCalls} 处，七枚夹具各一次）", prodCalls >= 7,
            "少一枚 = 那一格的 Normalize 分支没被真跑过，读数就只是我算的期望值");

        // ④ 夹具不许退化成"只喂默认值"：默认值全过 = 恒真式。数一下夹具里的越界字面量
        Assert("越界夹具带着真越界值（999 与 99 都在）",
            st.Contains("\"QualityAvif\", \"999\"") && st.Contains("\"QualityJpegXl\", \"9\""), "");
    }

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

        // ═══ 2026-09-29（审查批次 E）把同一条尺子从 2 个下拉扩到 7 个 ═══
        // 这条判据防的是"配置里的值域 > 界面项"，而它有两种犯法：多出一个值，【以及大小写不同】。
        // 校验用的 IsOneOf 是 OrdinalIgnoreCase，回填界面用的 SetComboByTag 却是
        // `(string)item.Tag == tag`（区分大小写）⇒ 白名单写 "RGB"、XAML 挂 "Rgb" 时，
        // 值一路活到界面，下拉却匹配不到任何项 ⇒ 空白 + 下次保存被兜底值覆盖。
        // 原先只钉 OcrEngineMode / AvifEncoder 两项，其余五项从来没有这一层对照。
        CheckWhitelistEqualsTags(xaml, srv, "ThemeCbo", "s.ThemeMode", expectTags: 4);
        CheckWhitelistEqualsTags(xaml, srv, "LanguageCbo", "s.Language", expectTags: 2);
        CheckWhitelistEqualsTags(xaml, srv, "GainMapModeCbo", "s.GainMapMode", expectTags: 2);
        CheckWhitelistEqualsTags(xaml, srv, "ArchiveModeCbo", "s.ArchiveMode", expectTags: 3);
        CheckWhitelistEqualsTags(xaml, srv, "ToastPositionCbo", "s.ToastPosition", expectTags: 5);

        // ═══ 2026-10-07：压缩强度三档（AVIF / JPEG XL / WebP）═══
        // 三个下拉共用同一组 Tag（Smallest/Balanced/Fastest），但各写各的字段 ⇒ 逐个对撞。
        // 这条判据在这里格外值钱：三个下拉长得一样，若某个分支漏写或写错字段名，
        // 界面看起来"改成功了"而管线读的是另一个字段（本仓 ChromaAvif 那次就是这形状）。
        CheckWhitelistEqualsTags(xaml, srv, "EffortAvifCbo", "s.EffortAvif", expectTags: 3);
        CheckWhitelistEqualsTags(xaml, srv, "EffortJpegXlCbo", "s.EffortJpegXl", expectTags: 3);
        CheckWhitelistEqualsTags(xaml, srv, "EffortWebpCbo", "s.EffortWebp", expectTags: 3);
        // JPEG LI 是后来纳入的（用户据 --xyb 实测要求）——增益图与它共用同一栏。
        CheckWhitelistEqualsTags(xaml, srv, "EffortJpegLiCbo", "s.EffortJpegLi", expectTags: 3);

        // ── 档位 → 底层参数的映射（2026-10-07，用户口径「最小体积」= 极限参数拉满）──
        // 值本身由真机实测定（见 docs/reviews/compression/encoder-params-and-smallest-tier-2026-10-07.md），
        // 这里钉的是"落地的值不许被随手改动"，因为每个数字背后都有一次实测。
        Assert("压缩强度/JXL 三档 = 7/5/3",
            CompressionEffortMap.JxlEffort(CompressionEffort.Smallest) == 7
            && CompressionEffortMap.JxlEffort(CompressionEffort.Balanced) == 5
            && CompressionEffortMap.JxlEffort(CompressionEffort.Fastest) == 3);
        Assert("压缩强度/AVIF 三档 = 0/6/10（最小体积已改为 0 = 极限）",
            CompressionEffortMap.AvifencSpeed(CompressionEffort.Smallest) == 0
            && CompressionEffortMap.AvifencSpeed(CompressionEffort.Balanced) == 6
            && CompressionEffortMap.AvifencSpeed(CompressionEffort.Fastest) == 10);
        Assert("压缩强度/WebP 三档 = 6/4/2",
            CompressionEffortMap.WebPMethod(CompressionEffort.Smallest) == 6
            && CompressionEffortMap.WebPMethod(CompressionEffort.Balanced) == 4
            && CompressionEffortMap.WebPMethod(CompressionEffort.Fastest) == 2);
        Assert("压缩强度/NVENC 三档 = P7/P4/P1",
            CompressionEffortMap.NvencPresetNumber(CompressionEffort.Smallest) == 7
            && CompressionEffortMap.NvencPresetNumber(CompressionEffort.Balanced) == 4
            && CompressionEffortMap.NvencPresetNumber(CompressionEffort.Fastest) == 1);

        // ── 「均衡档必须等于改动前的行为」──
        // 这是本功能最重要的一条承诺：默认档在 AVIF/WebP 上恰好是它们原来的硬编码值
        // ⇒ 不选档位的用户产物逐字节不变。改这两个数字就等于静默改变所有老用户的输出。
        Assert("压缩强度/AVIF 均衡档 == 改动前的硬编码 '-s 6'（老用户产物不变）",
            CompressionEffortMap.AvifencSpeed(CompressionEffort.Balanced) == 6);
        Assert("压缩强度/WebP 均衡档 == cwebp 默认 '-m 4'（老用户产物不变）",
            CompressionEffortMap.WebPMethod(CompressionEffort.Balanced) == 4);

        // ── libaom 高级选项只在「最小体积」档下发 ──
        // 实测量化（真机 4K）：tune=ssim 把 110.2 KB 降到 93.7 KB（−15%）且不额外变慢；
        // 其余小参（aq-mode / sharpness / enable-chroma-deltaq）在该内容上零效果。
        // ⚠ 若把它下发放到所有档位，就会连"均衡档 == 改动前"一起破坏 ⇒ 必须只在 Smallest 有。
        var advSmallest = CompressionEffortMap.AvifencAdvancedArgs(CompressionEffort.Smallest);
        Assert("压缩强度/最小体积档带 libaom tune=ssim",
            advSmallest.Count == 2 && advSmallest[0] == "-a" && advSmallest[1] == "tune=ssim",
            string.Join(",", advSmallest));
        Assert("压缩强度/均衡档不带任何 libaom 高级选项（否则会改掉老用户产物）",
            CompressionEffortMap.AvifencAdvancedArgs(CompressionEffort.Balanced).Count == 0);
        Assert("压缩强度/最快档不带任何 libaom 高级选项",
            CompressionEffortMap.AvifencAdvancedArgs(CompressionEffort.Fastest).Count == 0);

        // ── 坏值回落「均衡」而不是「最小体积」──
        // 回落成最慢档会让一次配置损坏变成"每张图都跑极限参数"（AVIF 上从 0.9 s 变 7.2 s）。
        Assert("压缩强度/未识别值回落均衡档（不是最小体积档）",
            CompressionEffortMap.Parse(null) == CompressionEffort.Balanced
            && CompressionEffortMap.Parse("") == CompressionEffort.Balanced
            && CompressionEffortMap.Parse("wat") == CompressionEffort.Balanced);

        // ── JPEG LI 的 --xyb（2026-10-07 用户据实测要求纳入）──
        // 实测（两张真实截图 × -d 0.5/1.0/2.0/3.0 + GainMap 的 -p 0 路）**一律更小 1.5%~4.7%**，
        // 是本仓所有编码器小参里唯一"在所有测过的内容与质量上都为正"的开关。
        Assert("压缩强度/JPEG LI 最小体积档带 --xyb",
            CompressionEffortMap.JpegLiUseXyb(CompressionEffort.Smallest)
            && CompressionEffortMap.JpegLiXybArg(CompressionEffort.Smallest) == " --xyb");
        Assert("压缩强度/JPEG LI 均衡档不带 --xyb（否则老用户产物会变）",
            !CompressionEffortMap.JpegLiUseXyb(CompressionEffort.Balanced)
            && CompressionEffortMap.JpegLiXybArg(CompressionEffort.Balanced) == "");
        Assert("压缩强度/JPEG LI 最快档不带 --xyb",
            !CompressionEffortMap.JpegLiUseXyb(CompressionEffort.Fastest));
        Assert("压缩强度/Tag 往返一致",
            CompressionEffortMap.Parse(CompressionEffortMap.ToTag(CompressionEffort.Smallest)) == CompressionEffort.Smallest
            && CompressionEffortMap.Parse(CompressionEffortMap.ToTag(CompressionEffort.Balanced)) == CompressionEffort.Balanced
            && CompressionEffortMap.Parse(CompressionEffortMap.ToTag(CompressionEffort.Fastest)) == CompressionEffort.Fastest);

        // ═══ 色域那一轴不比对白名单字符串，它存的是【裸索引】⇒ 要钉的是两张表的长度等式 ═══
        // Normalize 里的越界上界写的是字面量 5，而它的含义是"旧 6 项表的最后一项"（MainWindow.Settings.cs
        // 的 legacyTags）。今天两者一致，但没有任何东西保证下次改 legacyTags 时那个 5 跟着改 ——
        // 差一格的后果与 2026-09-26 摘掉 GainMap 那一项同型：合法区间放过一个指向不存在的旧索引。
        var uiSetPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.Settings.cs"));
        if (uiSetPath is null) { Skip("色域索引上界 vs legacyTags 表长", "未找到 MainWindow.Settings.cs"); }
        else
        {
            var uiSet = File.ReadAllText(uiSetPath);
            var legacy = ExtractQuotedValuesOfArrays(uiSet, "var legacyTags = new[]");
            var current = ExtractQuotedValuesOfArrays(uiSet, "var currentTags = new[]");
            var bound = System.Text.RegularExpressions.Regex.Match(srv,
                @"ColorSpaceIndex is < 0 or > (\d+)");
            Assert($"Normalize 的色域上界写死了字面量（实得 {(bound.Success ? bound.Groups[1].Value : "<没匹配到>")}）",
                bound.Success);
            Assert($"legacyTags 解析到 6 项", legacy.Count == 6, $"实际 {legacy.Count}");
            if (bound.Success && legacy.Count > 0)
                Assert($"色域索引上界 {bound.Groups[1].Value} == legacyTags 表长-1 {legacy.Count - 1}",
                    int.Parse(bound.Groups[1].Value) == legacy.Count - 1,
                    "两者不等 ⇒ 旧文件里的某个合法索引会被 Normalize 判成越界（静默改成 System），"
                    + "或反过来放过一个 legacyTags 里没有的索引（SetComboByTag 落空）");
            // currentTags 必须逐项等于界面那三项：Array.IndexOf(currentTags, wantTag) 落空时
            // 代码走 `newIdx < 0 → 0`，用户看到"跟随显示器"而想要的是 Rec.2020。
            var colorTags = ExtractComboItemTags(xaml, "ColorCbo");
            // 两边都是 0 时"数量相等"照样成立 ⇒ 先各自证明"解析器真的解析到了东西"，
            // 否则这条锁在两张表都读瞎的那一格是恒真的（本仓 §11.6 那条"空枚举必须拦"的同型）。
            Assert($"界面 ColorCbo 解析到 Tag（实得 {colorTags.Count}）", colorTags.Count > 0,
                "一个都没解析到 ⇒ 下面两条等式在空集合上恒成立，这一格等于没判");
            Assert($"currentTags 解析到项（实得 {current.Count}）", current.Count > 0,
                "抽不到 currentTags ⇒ 与 Tag 的数量等式就变成 0==0 的假绿");
            Assert($"界面 ColorCbo 解析到 {(current.Count > 0 ? current.Count : 3)} 个 Tag",
                colorTags.Count == current.Count, $"currentTags {current.Count} 项 vs Tag {colorTags.Count} 项");
            if (current.Count > 0 && colorTags.Count > 0)
                Assert($"currentTags[{string.Join(",", current)}] == ColorCbo Tag[{string.Join(",", colorTags)}]",
                    current.SequenceEqual(colorTags, StringComparer.Ordinal));
        }

        // ═══ "Avifenc" 档 2026-09-24 从界面移除，但旧值必须迁到 LibAom 而不是 Auto ═══
        // Auto 优先走硬件 ⇒ 拿它当迁移目标会翻转用户"选了软件编码器"的语义。
        // 这条判据防的是将来有人"顺手简化"成一句白名单回落。
        // ⚠ 锚点不能用 IndexOf("s.AvifEncoder = \"LibAom\"")：那句在文件里【出现两次】——
        //   MigrateLegacyAvifEncoder（旧两层设置迁移）先写了一遍，第一处 IndexOf 命中的是它，
        //   于是"其之前最近的 Avifenc"永远是 -1，这条锁红得莫名其妙（本轮实测 950/1）。
        //   改成一整段形状匹配：Avifenc 那枚比较与 LibAom 那句赋值必须在同一小块里。
        Assert("旧值 Avifenc 显式迁到 LibAom（不是悄悄落到 Auto）",
            System.Text.RegularExpressions.Regex.IsMatch(srv,
                "AvifEncoder[\\s\\S]{0,60}\"Avifenc\"[\\s\\S]{0,200}s\\.AvifEncoder = \"LibAom\""),
            "找不到『Avifenc 比较 → LibAom 赋值』这一段 ⇒ 迁移被改成了白名单回落（Auto 优先走硬件）");
        // 2026-09-29 审查轮加的第二层：这道门必须与紧随其后的 FitTag【同向】认大小写。
        // 原先写的是精确 ==，而 FitTag 按 IgnoreCase 认得 "avifenc" ⇒ 手写成小写的那份逃过迁移、
        // 被白名单回落成 Auto（优先硬件），恰好是本条禁止的那个翻转。真行为由 NORMALIZE 新增的
        // "AvifEncoder(小写逃过迁移门)" 那一臂钉住，这里钉的是写法不许退回精确比较。
        Assert("Avifenc 那道迁移门按 IgnoreCase 比（与 FitTag 同口径）",
            System.Text.RegularExpressions.Regex.IsMatch(srv,
                "Avifenc\"[\\s\\S]{0,60}StringComparison\\.OrdinalIgnoreCase"),
            "改成精确 == 就等于允许手写的 avifenc 绕过迁移、被白名单回落成 Auto");
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

        // ═══ 2026-09-29 审查轮 §10.3 V6/V7：上面那两条只钉"存不存在"，对这次的病是盲的 ═══
        // ① 迁移重写必须排在 Normalize 之后。写在前面 ⇒ 把【未归一的越界值】钉回盘上，
        //    而 Save() 顺手盖的 SettingsRevision/GainMapMerged 章让迁移门永不再开 ⇒ 盘上坏值就此固化。
        Assert("迁移重写排在 Normalize 之后（越界值不许被钉回盘上）",
            srvCode.IndexOf("Normalize(Current)", System.StringComparison.Ordinal) >= 0
            && srvCode.IndexOf("if (migrated) Save()", System.StringComparison.Ordinal) >
               srvCode.IndexOf("Normalize(Current)", System.StringComparison.Ordinal),
            "Save 早于 Normalize ⇒ 手改盘上的 FormatIndex=99 这类会被原样写回并盖章固化");

        // ② 空凭据不许无条件当成"用户要删"：读盘失败时 Current 的凭据字段同样是空的。
        Assert("清除凭据必须挂在「本次读取可信」上（坏文件不许毁掉 secrets.bin）",
            srvCode.Contains("else if (allowDelete)")
            && !System.Text.RegularExpressions.Regex.IsMatch(srvCode, @"else\s*\{\s*SecretStore\.Delete\("),
            "无条件走删除分支 ⇒ 一份写坏或丢失的 settings.json 就能无备份地删掉 API Key");

        // ③ 2026-09-29 审查轮：那道门只有"读进来"一个来源时，新装机那一格是坏的——
        //   首启根本没有 settings.json ⇒ Load() 走 else ⇒ 标志整会话为假 ⇒ 用户先填 API Key、再清空，
        //   删除被一直挡成一条 Warn，下次启动 Load() 又从 SecretStore 回填出"我已经删掉了"的那条凭据。
        //   ⇒ "本会话写成功过"必须也算可信；而写成功必须排在 File.Move 之后（没落盘就不算），
        //     Load() 开头两个标志要一起复位（读失败会把 Current 换成默认值表，之前那次写的可信度随之作废）。
        int saveCallsGated = System.Text.RegularExpressions.Regex
            .Matches(srvCode, @"SaveSecret\([^;]*SecretClearanceIsUserIntent\)").Count;
        Assert($"两个凭据写点都走同一道门（实得 {saveCallsGated}，应为 2）", saveCallsGated == 2,
            "漏一个 = 那条凭据的空值仍然无条件当成用户意图");
        Assert("写成功也算「本次可信」，且必须排在真正落盘之后",
            srvCode.Contains("_savedToDisk = true;")
            && srvCode.IndexOf("File.Move(tmp, SettingsPath", System.StringComparison.Ordinal) >= 0
            && srvCode.IndexOf("File.Move(tmp, SettingsPath", System.StringComparison.Ordinal)
               < srvCode.IndexOf("_savedToDisk = true;", System.StringComparison.Ordinal),
            "写盘失败却把标志抬起来 ⇒ 又回到\"空值即用户要删\"的假前提");
        Assert("Load() 开头把读、写两个可信来源一起复位",
            srvCode.Contains("_loadedFromDisk = false;") && srvCode.Contains("_savedToDisk = false;"),
            "只复位读的那半 ⇒ 上一次写留下的可信度会穿过一次失败的 Load，坏文件又能删凭据了");
    }

    /// <summary>
    /// 三根截图入口的失败静默度必须一致（2026-09-29 审查轮 §10.3 V1）。
    /// <para>形状：写了 Error/Warn 就 return，而那一格里既没有状态栏也没有通知 ⇒ 用户按下热键后
    /// 什么都没发生也没被告知。选区那根一直是 ❌ + 通知，无感那根只写日志 —— 同一种错在两根入口
    /// 表现不同，报障时按「有没有弹提示」根本分不出是哪条路。</para>
    /// <para>Test 引用不到 App ⇒ 这条只能钉「日志语句与可见提示在同一段里」。</para>
    /// </summary>
    static void Test_CaptureRoots_FailureNeverSilent()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (mwPath is null)
        {
            Skip("截图根失败可见性", "未找到 MainWindow.xaml.cs");
            return;
        }
        var mw = File.ReadAllText(mwPath);
        // 三条无感路径上的失败点：拿日志原文当锚点。锚不在 ⇒ 先判红，不能让这条悄悄变成空判。
        var anchors = new[]
        {
            "WGC 捕获服务未初始化",       // 捕获服务没起来
            "SDR 像素数据为空，跳过保存", // 拿到空帧
            "无感截图编码失败",           // 后台编码抛
        };
        foreach (var anchor in anchors)
        {
            int at = mw.IndexOf(anchor, System.StringComparison.Ordinal);
            Assert($"无感截图的失败点【{anchor}】仍在源码里", at >= 0,
                "锚点不见了 ⇒ 这条判据失效（改日志文案要同步这里，别让它空转）");
            if (at < 0) continue;
            int ret = mw.IndexOf("return;", at, System.Math.Min(400, mw.Length - at));
            int end = ret >= 0 ? ret + 7 : System.Math.Min(at + 400, mw.Length);
            var win = mw.Substring(at, end - at);
            Assert($"【{anchor}】必须同时给用户状态栏与失败提示（不许只写日志）",
                win.Contains("SetStatus(") && win.Contains("ShowCaptureFailed("),
                "只写日志 = 用户按了热键却零反馈；对照选区那根是同形两句");
        }
    }

    /// <summary>
    /// 四个目的地都必须吃【合成标注后】的像素（2026-09-29 审查轮 §10.3 V10）。
    /// <para>缺陷形状：只有「保存/复制」做标注合成，「识字/翻译」落回原始桌面像素 ⇒
    /// 用户用马赛克盖住的原文会被 OCR 原样读出来，且没有任何提示。</para>
    /// </summary>
    static void Test_AnnotationComposedForAllDestinations()
    {
        var ovPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml.cs"));
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (ovPath is null || mwPath is null)
        {
            Skip("标注合成覆盖四个目的地", "未找到 SelectionOverlay.xaml.cs / MainWindow.xaml.cs");
            return;
        }
        var ov = StripLineComments(File.ReadAllText(ovPath));
        var mw = StripLineComments(File.ReadAllText(mwPath));

        int g = ov.IndexOf("if (result is MainToolbarAction.", System.StringComparison.Ordinal);
        Assert("SDR 覆盖层：合成守卫必须同时含 Save/Copy/Ocr/Translate（契约枚举 2026-10-26 起，原 ActionResult.Confirm 改叫 Save）",
            g >= 0 && ov.Substring(g, System.Math.Min(140, ov.Length - g)) is var guard
            && guard.Contains("MainToolbarAction.Save") && guard.Contains("MainToolbarAction.Copy")
            && guard.Contains("MainToolbarAction.Ocr") && guard.Contains("MainToolbarAction.Translate"),
            "少一个目的地 = 那一路读的是未合成的原始桌面像素（马赛克白画）");

        foreach (var act in new[] { "Ocr", "Translate" })
        {
            int c = mw.IndexOf($"case MainToolbarAction.{act}:", System.StringComparison.Ordinal);
            Assert($"HDR 覆盖层：case {act} 必须走 ComposeAnnotatedSdr（与 Save/Copy 同源）",
                c >= 0 && mw.Substring(c, System.Math.Min(300, mw.Length - c)).Contains("ComposeAnnotatedSdr(")
                && !mw.Substring(c, System.Math.Min(300, mw.Length - c))
                       .Contains($"{act}FromPixelsAsync(sdrRegion", System.StringComparison.Ordinal),
                "把未合成的 sdrRegion 直接交给识字/翻译 ⇒ 标注与马赛克在这两路上失效");
        }
    }

    /// <summary>
    /// 2026-09-29 审查轮 §10.3 那五处修复各自的判据（能真跑的都提级到 T1，够不到的老实标 T2）。
    /// <para>每条都配一条【会翻转的对照臂】：只测"修好之后是对的"不算数，要能证明旧写法在这里必然红。</para>
    /// </summary>
    static void Test_ReviewRoundFixes_HaveTeeth()
    {
        // ── ① BT.601 灰度权重：R/B 系数曾装反（BGRA 布局下把 77 给了 B、29 给了 R）──
        // 夹具挑的是"正确权重"与"装反权重"给出【相反结论】的一对颜色：
        //   (R=255,G=120,B=0) → 正确 147 / 装反 99 ；(R=0,G=120,B=255) → 正确 99 / 装反 147
        // 阈值 110 正好卡在中间 ⇒ 旧写法两臂全反，这条一定红。
        static byte[] Solid(int r, int g, int b)
        {
            var px = new byte[64 * 64 * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)b; px[i + 1] = (byte)g; px[i + 2] = (byte)r; px[i + 3] = 255; }
            return px;
        }
        var brightWarm = Solid(255, 120, 0);    // 真亮度高（红权重大）
        var dimCool = Solid(0, 120, 255);       // 真亮度低（蓝权重小）
        Assert("灰度权重按 BT.601 落在正确通道上：暖色不算深底",
            !BitmapPreprocessor.IsDarkBackground(brightWarm, 64, 64),
            "R/B 系数装反会把暖色读成深底 ⇒ 深色主题判定与反色一起走反");
        Assert("灰度权重按 BT.601 落在正确通道上：冷色算深底",
            BitmapPreprocessor.IsDarkBackground(dimCool, 64, 64),
            "两臂必须相反，同判说明这条判据是恒真的");

        // ── ② LLM 回 200 而 content 为空：原写法把源文本当译文返回（静默假成功）──
        bool threwNull = false, threwBlank = false;
        try { TranslationService.RequireTranslatedContent(null); } catch { threwNull = true; }
        try { TranslationService.RequireTranslatedContent("   "); } catch { threwBlank = true; }
        Assert("端点回空 content 必须判失败（不许把用户自己的源文本当译文返回）",
            threwNull && threwBlank,
            "content?.Trim() ?? text 这种回落长得像成功，比报错更坏");
        Assert("有内容时原样去空白返回（别把这条改成恒抛）",
            TranslationService.RequireTranslatedContent("  hello  ") == "hello");

        // ⚠ 上面两条臂只证明 helper 本身正确 —— 变异实测 M2 抓到：把调用点退回
        //    `content?.Trim() ?? text` 时它们【照样全绿】。缺陷长在调用点上，所以必须再钉调用点。
        //    切片只取到本方法结束（`\n    }`），并先剥掉整行注释 —— helper 的 XML 文档里就写着
        //    旧写法当反例，不剥注释这条锁会永久假红。
        var tsPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Services", "TranslationService.cs"));
        string tsRaw = tsPath is null ? "" : File.ReadAllText(tsPath);
        int la = tsRaw.IndexOf(".GetProperty(\"content\")", System.StringComparison.Ordinal);
        int lb = la < 0 ? -1 : tsRaw.IndexOf("\n    }", la, System.StringComparison.Ordinal);
        var llmWin = (la >= 0 && lb > la) ? StripLineComments(tsRaw.Substring(la, lb - la)) : "";
        Assert("LLM 解析的调用点必须真的走那道守卫（helper 正确 ≠ 有人调它）",
            llmWin.Contains("RequireTranslatedContent(content)"),
            "调用点退回 content?.Trim() ?? text ⇒ 端点回空 content 时又把源文本当译文交出去");
        Assert("LLM 解析路径上不许再出现『回空就吐源文本』这种回落",
            llmWin.Length > 0 && !llmWin.Contains("?? text") && !llmWin.Contains("?? sourceText"),
            "这正是本轮 §10.3 V2 修掉的那一行的写法");

        // ── ③ AVIF 全后端失败的 PNG 保底必须仍带色域声明 ──
        // 行为级：裸值入口与 EncodingSettings 入口必须逐字节同结果（映射逻辑只该有一处）。
        foreach (var (tag, icc) in new (string, byte[]?)[3]
        {
            ("BT2020", new byte[128]), ("sRGB", null), ("System", new byte[64]),
        })
        {
            var st = new EncodingSettings { ColorSpaceTag = tag, IccProfile = icc };
            var viaSettings = FormatHelper.GetColorMetadata(st);
            var viaRaw = FormatHelper.GetColorMetadata(tag, icc);
            Assert($"两个入口同输入必须同输出（tag={tag}）",
                SameBytes(viaSettings.icc, viaRaw.icc) && SameBytes(viaSettings.cicp, viaRaw.cicp),
                "保底若自己拼一份映射，主路改了它不会跟着改 ⇒ 又长出一个事实源");
        }
        Assert("sRGB / System 目标不嵌 ICC（保底与主路共用的这条口径不许漂）",
            FormatHelper.GetColorMetadata("sRGB", new byte[128]).icc is null
            && FormatHelper.GetColorMetadata("System", new byte[128]).icc is null);
        var fePath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatEncoders.cs"));
        // ⚠ §13.9 之后这条必须钉在【函数本体】上。原先的窗口是 "最终回退 PNG" → 最近的 "RecordFallbackPath"，
        //   而 WebP 保底里也有一句 RecordFallbackPath ⇒ 我在它前面新加一处调用，窗口就被提前截断、
        //   锁自己错位红了（本轮实测：--unit-tests 899/1）。锚点式文本锁就这么脆。
        //   真正的行为判据是 ColorPipelineTests.AvifPngFallback_KeepsDeclarationAndNotice
        //   （独占目标文件、真跑一次保底、读回 PNG 的 iCCP/cICP）；下面这几条只钉形状。
        string feSrc = fePath is null ? "" : File.ReadAllText(fePath);
        var fbWin = SliceAround(feSrc, "internal static void PngLastResort", "catch (Exception pngEx");
        Assert("PNG 保底那一路真的把色域声明传进编码器（就近钉）",
            fbWin.Contains("ManagedPngEncoder.Encode(bgra, w, h, pngPath, 8, "),
            "少了这两个实参 ⇒ 产物是「广色域像素 + 零声明」的 PNG，查看器一律按 sRGB 解");
        Assert("PNG 保底必须写降级留痕（换的是格式，不只是参数）",
            fbWin.Contains("EncoderFallbackNotifier.LastReason"),
            "只从后缀发现格式换了 ⇒ 用户查不到原因（WebP 保底早就写了这条）");
        // 2026-09-29 审查轮：那条留痕【不许覆盖更早的那条】。原先这里是裸赋值 ⇒ 同一次编码里已经写过
        // 的格式内降级（例如"MFT 失败已回退 libaom，会明显变慢"）被"改存 PNG"挤出唯一槽位，
        // 用户永远不知道自己还绕开过硬件后端。HDR 那份从一开始就是并起来的，byte[] 这份必须同形。
        Assert("PNG 保底不许吃掉更早的降级留痕（与 HDR 那份同样并起来 + 另留一行 Warn）",
            fbWin.Contains("｜另外：") && fbWin.Contains("var earlier = EncoderFallbackNotifier.LastReason;")
            && fbWin.Contains("已并入而非丢弃"),
            "裸赋值 = 换格式那句把本次编码里更早的格式内降级挤出唯一槽位，两份事实只剩一份可见");
        // 调用点按"哪条腿"分别数：byte[] 那份（AVIF SDR / WebP / JPEG LI / JXL SDR）与
        // HDR 帧那份（AVIF HDR / JXL HDR）。增益图的两处在 JpegGainMapEncoder.cs，由
        // Test_PngFallbackSwitch_Wired 那条"每个写者都读这个闸"的总数覆盖。
        int sdrLegs = CountOccurrences(feSrc, "FormatHelper.PngLastResort(");
        int hdrLegs = CountOccurrences(feSrc, "FormatHelper.PngLastResortHdrFrameAsync(");
        Assert($"换格式保底的 byte[] 腿调用点 ≥4（实得 {sdrLegs}）", fbWin.Length > 0 && sdrLegs >= 4,
            "少一条腿就退回「只覆盖某一条路」的形状；行为矩阵那时会红，但归属不到是哪条路");
        Assert($"换格式保底的 HDR 腿调用点 ≥2（实得 {hdrLegs}）", hdrLegs >= 2,
            "HDR 那条腿没有保底 = 用户在 HDR 屏上按一次热键什么都不落，而开关也无从关掉它");
        // ═══ 2026-09-29 F 批：保底实现【只能有一份】═══
        // WebP 原先自己内联写了整套（取声明 → Encode → 写留痕 → 登记路径），与 AVIF 那份并行存在，
        // 于是"promise 写在文案里、实现却只接住 DllNotFoundException"这种错法没有任何一处会暴露。
        // 换后缀是"换格式"这件事实最硬的签名 ⇒ 它只许出现在保底的两个函数里
        // （byte[] 那份 + HDR 帧那份），出现第三处就意味着有人又抄了一份保底。
        var suffixSwitches = System.Text.RegularExpressions.Regex
            .Matches(feSrc, @"Path\.ChangeExtension\(\s*path\s*,\s*""\.png""").Count;
        Assert($"换格式保底的「换后缀」实现只许两份（byte[] 与 HDR 帧各一，实得 {suffixSwitches}）",
            suffixSwitches == 2,
            "第三处 = 又长出一份并行保底；一份都没有 = HDR 那条腿的保底被删了");
        // 硬件链自己【不再】换格式：2026-09-29 把决定上移到带 EncodingSettings 的那一层，
        // 否则用户那个开关在硬件路径上根本无从生效（后端层拿不到设置）。
        var hwChain = SliceAround(feSrc, "public static void FallbackToLibAom", "throw;");
        Assert("AVIF 硬件链失败后只抛出，不在后端层换格式（开关才管得住它）",
            hwChain.Length > 0 && !hwChain.Contains("PngLastResort"),
            "后端层里换格式 = 那一层看不到 PngFallbackEnabled，用户关掉开关也照换");
    }

    /// <summary>取 [锚点, 结束锚点] 之间的那段源码（锚点找不到就返回空串 ⇒ 判红，不空转）。</summary>
    static string SliceAround(string src, string startAnchor, string endAnchor)
    {
        int a = src.IndexOf(startAnchor, System.StringComparison.Ordinal);
        if (a < 0) return "";
        int b = src.IndexOf(endAnchor, a, System.StringComparison.Ordinal);
        return b > a ? src.Substring(a, b - a + endAnchor.Length) : "";
    }

    /// <summary>字节序列同值（不是同引用）。&lt;c&gt;BuildSdrCicp&lt;/c&gt; 每次新分配数组，
    /// 拿 <c>==</c> 比会恒定判红——本轮第一版就是这么错的。</summary>
    static bool SameBytes(byte[]? a, byte[]? b)
        => ReferenceEquals(a, b) || (a is not null && b is not null
            && a.AsSpan().SequenceEqual(b.AsSpan()));
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
            // 2026-10-01 这一格的口径改过：未选中时的回落取【持久化那一份】(_settings.FormatIndex)，
            // 不再取表里的默认项 —— 默认项那条会把用户选的格式抹成 PNG 并连带写回盘上
            // （实机前后读数见 Test_FormatCombo_RebuildKeepsThePersistedFormat 的注释）。
            // 本条只判"先读后写 + 回落这条路接的是持久化真值"，值的正确性由那张卡判。
            && rbBody.Contains("_settings.FormatIndex"),      // 未选中时的落点 = 盘上那一份
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

    /// <summary>
    /// 打包形态（MSIX）的开机自启必须走 StartupTask，而不是注册表。
    /// <para>为什么必须有这条：MSIX 下对 HKCU 的写入被重定向到包专属 hive（官方："All writes under
    /// HKCU are copied on write to a private per-user, per-app location."）⇒ 那条 <c>Run</c> 项
    /// <b>对系统启动无效</b>。而症状最具欺骗性：界面开关能勾、注册表里也读得到值，
    /// <b>只有重启后才发现不会自动启动</b>。2026-10-07 复核时确认清单里早就声明了
    /// <c>startupTask</c>，但代码一直没接这个 API —— 即"声明了但没实现"。</para>
    /// <para>四条各咬一种退化：① 有没有按形态分支；② 打包分支是否真的用 StartupTask；
    /// ③ TaskId 与清单是否一致（写错就静默不生效）；④ 读端是否也走同一分支
    /// （只改写端会让界面显示与实际相反）。</para>
    /// </summary>
    static void Test_StartupManager_PackagedUsesStartupTask()
    {
        var src = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "StartupManager.cs"));
        var manifest = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Package.appxmanifest"));
        if (src is null || manifest is null)
        { Skip("打包形态的开机自启", "未找到 StartupManager.cs / Package.appxmanifest"); return; }

        var text = StripLineComments(File.ReadAllText(src));

        Assert("StartupManager: 按运行形态分支（打包用 StartupTask、未打包用注册表）",
            text.Contains("PackageRuntime.IsPackaged"));

        Assert("StartupManager: 打包分支调用 StartupTask.GetAsync",
            text.Contains("StartupTask") && text.Contains("GetAsync"));

        Assert("StartupManager: 启用走 RequestEnableAsync（而不是直接写注册表）",
            text.Contains("RequestEnableAsync"));
        Assert("StartupManager: 禁用走强类型 Disable() 调用",
            text.Contains(".Disable()"));

        // ═══ 2026-10-07 跟进：禁止退回反射 ═══
        // 背景（自查发现的真缺陷）：我最初用反射调 StartupTask（理由是"不想让未打包形态沾 WinRT"），
        // 那版有 3 个 bug，且**全部指向同一后果 —— 打包形态自启永远不生效、只留一行 Warn**：
        //   ① MakeGenericMethod(typeof(int)) 对两个不同返回类型是错的
        //      （GetAsync→IAsyncOperation<StartupTask>，RequestEnableAsync→IAsyncOperation<StartupTaskState>）；
        //   ② 对**开放泛型**参数调 IsAssignableFrom 恒为 false ⇒ 每个 AsTask 重载都被跳过；
        //   ③ 于是 AsTask 永远返回 null，调用方全部提前 return。
        // 实测证明反射本就不必要：本工程 TFM 是 net11.0-windows10.0.26100.0，
        // `using System;` 之后 `await StartupTask.GetAsync(...)` **直接编译通过**。
        // ⇒ 这条断言把"用编译期类型、别用反射"钉住：编译期能查出的错，不该推迟到装机后才暴露。
        Assert("StartupManager: 打包分支不得再用反射（那版反射恒失败 ⇒ 自启静默不生效）",
            !text.Contains("MakeGenericMethod") && !text.Contains("GetAssemblies")
            && !text.Contains("ResolveStartupTaskType"));

        // TaskId 必须与清单逐字一致 —— 不一致时 API 返回 null，且没有任何报错
        var manifestText = File.ReadAllText(manifest);
        var m = Regex.Match(manifestText, @"StartupTask\s+TaskId=""([^""]+)""");
        Assert("清单里声明了 startupTask 且能解析出 TaskId", m.Success);
        if (m.Success)
        {
            var taskId = m.Groups[1].Value;
            Assert($"StartupManager 的 StartupTaskId 与清单一致（{taskId}）",
                text.Contains($"\"{taskId}\""),
                "两处不一致时 StartupTask.GetAsync 返回 null，自启静默失效且无报错");
        }

        // 读端也必须分支：只改写端会导致"界面显示已禁用、实际已启用"（或反之）这种谎报
        var getter = text.Contains("Packaged") && text.Contains("IsEnabledPackaged");
        Assert("StartupManager: 读端也走打包分支（否则界面与实际状态相反）", getter);
    }

    /// <summary>
    /// 提取 XAML 中指定 ComboBox 块内的 ComboBoxItem Tag。
    /// <para>锚点用 <c>x:Name="X"</c> 而不是裸名字：XAML 里 <c>ColorCbo</c> 是
    /// <c>OverlayColorCbo</c> / <c>BorderColorCbo</c> 的子串，按裸名"取第一次命中"一旦别人把
    /// 那两张卡片挪到前面，这里就会静默换块 ⇒ 解析到别人的 Tag 还自认为对了（本轮实测过这个形状）。
    /// 现有 8 个调用点都是 <c>SelectionChanged</c> 的界面下拉，XAML 里必然写成 <c>x:Name="…"</c>。</para>
    /// </summary>
    static List<string> ExtractComboItemTags(string xaml, string comboBoxName)
    {
        int idx = xaml.IndexOf("x:Name=\"" + comboBoxName + "\"", StringComparison.Ordinal);
        if (idx < 0) return new List<string>();
        int end = xaml.IndexOf("</ComboBox>", idx, StringComparison.Ordinal);
        var block = end > idx ? xaml[idx..end] : xaml[idx..];
        return ExtractQuotedValues(block, "Tag=\"");
    }

    /// <summary>取 <c>var X = new[] { "a", "b", … };</c> 这一行的字面量（到分号为止）。</summary>
    static List<string> ExtractQuotedValuesOfArrays(string cs, string decl)
    {
        int idx = cs.IndexOf(decl, StringComparison.Ordinal);
        if (idx < 0) return new List<string>();
        int end = cs.IndexOf(';', idx, StringComparison.Ordinal);
        var seg = end > idx ? cs[idx..end] : cs[idx..];
        return ExtractQuotedValues(seg, "\"");
    }

    /// <summary>
    /// 提取 <c>FitTag(s.Xxx, v =&gt; …, dft.Xxx, "a", "b", …)</c> 里的合法值。
    /// <para>只取调用里的字符串字面量：第三参是 <c>dft.Xxx</c>（无引号），lambda 体也无引号
    /// ⇒ 段内所有引号串恰好等于那张 Tag 表，不会混进默认值。</para>
    /// <para>2026-09-29 前这里找的是 <c>IsOneOf(s.Xxx…</c>；换成 <c>FitTag(</c> 之后，若有人把某一轴
    /// 退回成"只校验不改写拼法"的写法，这里就解析不到 ⇒ <c>白名单: 解析到 …</c> 那条断言直接红，
    /// 不会静默放过。</para>
    /// </summary>
    static List<string> ExtractIsOneOfArgs(string cs, string propertyRef)
    {
        int idx = cs.IndexOf("FitTag(" + propertyRef, StringComparison.Ordinal);
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
    /// MSIX 包身份判定（2026-10-07，商店上架核查时补）。
    /// <para>为什么必须有：MSIX 包目录只读（官方原文："Write inside the package — Not allowed.
    /// The package is read-only."）。设置若仍写程序同目录，<c>CanPersist</c> 为 false
    /// ⇒ 全程按默认值表运行 ⇒ <b>用户改的每一项设置重启即丢</b>，界面上只有日志一行提示。
    /// <c>SettingsService.SettingsPath</c> 因此按本判据分支 —— 判据错了，整个修复就是空的。</para>
    /// <para>⚠️ 本测试【只能验未打包这一侧】：打包侧需要真的装一次 MSIX 才有包身份，
    /// 在这台开发机上造不出来。故这里锁住"未打包必须判 false"（保守方向）——
    /// 若哪天它翻成 true，绿色包用户的设置会被悄悄挪去 LocalAppData，
    /// 而那正是本判据最该拦住的方向。</para>
    /// </summary>
    static void Test_PackageRuntime_UnpackagedDefault()
    {
        var isPackaged = TrueToneCap.Core.Settings.PackageRuntime.IsPackaged;
        Assert("未打包进程必须判为无包身份（否则绿色包的设置会被挪去 LocalAppData）",
            !isPackaged,
            $"IsPackaged={isPackaged}；若本测试确实跑在已安装的 MSIX 里，请改为断言 true 并说明环境");

        // 判据由静态初始化缓存，但必须可重复读且稳定（不能"第一次之外都乱答"）
        Assert("包身份判定稳定（两次读取一致）",
            TrueToneCap.Core.Settings.PackageRuntime.IsPackaged == isPackaged);
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

        // ⚠️ 2026-10-07 改判据形态：SettingsPath 现在按运行形态三目选目录，
        //    不再是单一的 `Path.Combine(AppContext.BaseDirectory, SettingsFileName)`。
        //    这里钉的【意图】不变 —— "路径只有一个表达式、没有第二答案、没有 ResolveSettingsPath"：
        //      · 未打包分支仍必须落到 AppContext.BaseDirectory；
        //      · 目录由三目表达式一处决定（不散落、不依赖运行时探测结果）。
        Assert("路径唯一事实源 = 一个表达式按运行形态确定性选目录（不再是「探测失败才换」的第二答案）",
            svc.Contains("public static string SettingsPath { get; } =")
            && svc.Contains("? Path.Combine(")
            && svc.Contains(": AppContext.BaseDirectory")
            && !svc.Contains("ResolveSettingsPath"));
        // ═══ 2026-10-07 新增：打包形态必须改写到可写位置 ═══
        // 背景（商店上架核查时实测发现）：MSIX 包目录只读（官方："Write inside the package —
        // Not allowed. The package is read-only."）。若仍写程序同目录，CanPersist 为 false
        // ⇒ 全程按默认值表运行 ⇒ 用户改的每一项设置重启即丢，界面上只有日志一行提示。
        // 这三条各咬一种退化：① 有没有按包身份分支；② 打包时去不去 LocalAppData；
        // ③ 判定是否走那个 P/Invoke 帮助类（而不是散落的内联调用）。
        Assert("设置路径必须按 PackageRuntime.IsPackaged 分支（否则 MSIX 下设置无处可写）",
            svc.Contains("PackageRuntime.IsPackaged"));
        Assert("打包形态下设置落到 %LOCALAPPDATA%\\TrueToneCap（包目录只读，写不进去）",
            svc.Contains("SpecialFolder.LocalApplicationData") && svc.Contains("\"TrueToneCap\""));
        Assert("包身份判定集中在 PackageRuntime（不在 SettingsService 里内联 P/Invoke）",
            svc.Contains("TrueToneCap.Core.Settings.PackageRuntime"));
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

        // ═══ 2026-09-30 任务 #10：消费降级留痕必须排在【两条编码腿】的 finally 里 ═══
        // 原先这一句只写在成功路径末尾 ⇒ 编码整条失败时那次原因留在单槽里，下一次【成功】的截图
        // 会把它当成自己的降级弹出来（本文件那句"不清空会把上一次的原因报到下一次截图上"警告过，
        // 但清槽只做了一半路径）。按 finally 里有没有它来钉，而不是钉"调用总数"——
        // 调用总数对"写在成功路径"这种错法完全不敏感（一样是 2 次）。
        int consumesInFinally = System.Text.RegularExpressions.Regex
            .Matches(cps, @"AllowSleep\(\);[\s\S]{0,200}?NotifyEncoderFallbackIfAny\(").Count;
        Assert($"两条编码腿都在 finally 里消费降级留痕（实得 {consumesInFinally}，应为 2）",
            consumesInFinally == 2,
            "少于 2 = 有一条腿的失败路径不清槽，原因会泄漏到下一张；0 = 整个机制退回成功路径");
        int totalConsumes = System.Text.RegularExpressions.Regex
            .Matches(cps, @"NotifyEncoderFallbackIfAny\(").Count;
        Assert("消费点只有『一处定义 + 两条腿各一次调用』（实得 3）", totalConsumes == 3,
            $"实得 {totalConsumes}：多出来的那一处若落在 finally 之外，成功路径会把原因提前吃掉，"
            + "失败路径又回到不消费的老形状");
    }

    /// <summary>
    /// 英文界面的状态栏不许被 .cs 里的字面量盖回中文（2026-09-30 批次 T3 实测抓出来的）。
    /// <para>起因：进程外 UIA 扫真身（`tests/manual/safety/en-ui-count.ps1`，读数
    /// `tests/output/en-ui-count-en2.log`）——英文模式下 41 个带名控件里 9 条仍是中文，
    /// 其中「就绪」<b>已经在 ZhToEn 表里</b>（<c>LocaleManager.cs:316</c>），XAML 初值也确实被翻了，
    /// 但 <c>MainWindow.xaml.cs</c> 有三处把字面量 <c>"就绪"</c> 直接写回 <c>StatusTxt.Text</c>
    /// ⇒ 英文界面每次操作后状态栏自己变回中文。</para>
    /// <para>为什么必须新加这一条：现有的"界面中文本地化覆盖"那把尺子查的是<b>XAML 字面量有没有进表</b>，
    /// 它管不到"运行时用 .cs 盖回中文"这一类——而这类恰恰是 UIA 看得见、静态扫描看不见的
    /// （同族缺口见交接报告 §55"漏翻在 .cs 拼串"）。所以这里钉的是【赋值右侧】的形态，
    /// 而不是数表里的条目。</para>
    /// </summary>
    static void Test_StatusBarLocale_CsMustNotReWriteChinese()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var lp = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (p is null || lp is null)
        { Skip("状态栏英文模式回中文", "未找到 MainWindow.xaml.cs / LocaleManager.cs"); return; }
        var main = StripLineComments(File.ReadAllText(p));
        var loc = File.ReadAllText(lp);

        Assert("对照表里确实有『就绪 → Ready』这一条（否则下面两条是空谈）",
            loc.Contains("[\"就绪\"] = \"Ready\"") && loc.Contains("public static string Ready"),
            "表里缺这条 ⇒ 界面扫描也翻不出来，属另一种错法");
        int raw = System.Text.RegularExpressions.Regex
            .Matches(main, @"StatusTxt\.Text\s*=\s*""就绪""").Count;
        Assert($"状态栏不许再直写中文『就绪』（实得 {raw} 处，应为 0）", raw == 0,
            "直写字面量会盖掉可视树扫描的结果 ⇒ 英文模式下每次操作后状态栏变回中文（UIA 实测抓到 3 处）");
        int via = System.Text.RegularExpressions.Regex
            .Matches(main, @"StatusTxt\.Text\s*=\s*LocaleManager\.Ready").Count;
        Assert($"三处复位点都走 LocaleManager.Ready（实得 {via}，应为 3）", via == 3,
            "少一处 = 那条路径仍然直写或写了别的串，英文界面按路径随机回退成中文");
    }

    /// <summary>
    /// 热键冲突日志里的 <c>err=</c> 必须是真的错误码（2026-09-30 批次 T3 实测抓到它恒打 0）。
    /// <para>来历：本进程抢下 <c>Ctrl+Shift+S</c> / <c>Ctrl+Shift+Q</c> 再起真身，产品日志如实写了
    /// <c>冲突: Ctrl+Shift+S (err=0)</c> 并回退到 <c>Ctrl+Alt+S</c> / <c>Ctrl+Alt+Q</c>（见
    /// <c>tests/output/hotkey-clipboard-cell2.log</c> 与其后的产品日志）。回退链本身是对的，
    /// 但那个 <c>err=0</c> 是假的：<c>RegisterHotKey</c> 的 <c>LibraryImport</c> 没开
    /// <c>SetLastError</c>，<c>Marshal.GetLastWin32Error()</c> 取不到本次调用的错误码。</para>
    /// <para>为什么值得单独钉：热键问题只有两个成因——被别的程序占用（1410）与参数不合法（87），
    /// 而这两者的处置完全相反。日志把关键分叉印成 0，等于把这一格退回"靠猜"。</para>
    /// </summary>
    static void Test_HotkeyConflictLog_CarriesRealWin32Error()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "HotkeyManager.cs"));
        if (p is null) { Skip("热键冲突日志的 err", "未找到 HotkeyManager.cs"); return; }
        var src = File.ReadAllText(p);

        Assert("冲突日志确实读了 GetLastWin32Error（否则下面那条没意义）",
            src.Contains("Marshal.GetLastWin32Error()"),
            "日志里不印错误码也行，但那就该把这句从话里删掉，别留一个恒为 0 的数");
        Assert("RegisterHotKey 的 LibraryImport 必须开 SetLastError = true",
            System.Text.RegularExpressions.Regex.IsMatch(src,
                @"LibraryImport\(""user32\.dll""[^)]*SetLastError\s*=\s*true\s*\)\][\s\S]{0,120}?RegisterHotKey"),
            "不开这个标志，err 恒打 0 ⇒ 排查的人分不出『被占用(1410)』与『参数非法(87)』");
    }

    /// <summary>
    /// LogViewer 的"调用者 → 哪个窗口"与"这行属于哪个实例"判据（镜像正则哨兵，2026-09-30）。
    /// <para>为什么只能钉源码：Test 工程只引用 Core，运行时够不到 LogViewer 的 LogParser，
    /// 所以把生产侧的四条正则原文抄一份当镜像，再拿【逐字相等】把两侧锁住。抄两份是刻意的：
    /// 不锁字面，改生产判据时这条用例就永远绿在错的地方；锁了字面，任一侧漂移立刻红 ——
    /// 那时该做的决定是"两边一起改"，而不是"把这条删掉"。</para>
    /// <para>钉住的缺陷本体：旧 caller 判据 <c>[A-Za-z0-9_]+\.cs</c> 只吃"一个点的名字"，
    /// 而窗口源文件的形状恰恰是代码后置 <c>SelectionOverlay.xaml.cs</c> / <c>MainWindow.xaml.cs</c>
    /// 与分部切片 <c>MainWindow.Settings.cs</c> ⇒ 真实日志（当日 2120 行）里 739 行 Caller 恒空、
    /// "文件.cs:行号"整段粘在 Message 上当文案，日志查看器对"这行出自哪个窗口"整体失明。
    /// 探针实测读数：修前 Caller 非空 1358 / 修后 2097，窗口归属 0 → 791，实例身份 0 → 69。</para>
    /// </summary>
    static void Test_LogViewer_WindowAndInstanceAttribution()
    {
        // ── 镜像常量：与 LogParser.cs 的字面量逐字一致（漂移即红，见上面的设计说明）──
        const string CallerMirror = @"\s+([A-Za-z0-9_.]+\.cs):(\d+)\s*$";
        const string WindowMirror =
            @"^(?!App\.xaml\.cs$)(?<name>[A-Za-z0-9_]*Window|[A-Za-z0-9_]+(?=\.xaml\.cs$))(?:\.[A-Za-z0-9_]+)?\.cs$";
        const string InstanceMirror = @"实例#(\d+)";
        const string ActivationMirror = @"(?:Activated\s*#(\d+)|第(\d+)次)";

        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "Services", "LogParser.cs"));
        // fail-closed：源文件都没定位到 ⇒ 下面一条判据都没跑，必须报红。
        // 这里不许 Skip：Skip 会把"测试自己瞎了"记成"这块没测"，而这条正是唯一能看见那处失明的地方。
        if (p is null)
        {
            Assert("LogViewer 窗口/实例哨兵：LogParser.cs 已定位", false,
                "未找到 src/TrueToneCap.LogViewer/Services/LogParser.cs ⇒ 镜像比对一条都没执行");
            return;
        }
        var src = StripLineComments(ReadSourceNormalized(p));

        // 取某条 Regex 字段声明后面的 @"" 字面量（锚点找不到返回 null，由调用方判红）
        string? LiteralOf(string field)
        {
            int i = src.IndexOf(field, StringComparison.Ordinal);
            if (i < 0) return null;
            int at = src.IndexOf('@', i, StringComparison.Ordinal);
            if (at < 0) return null;
            int open = at + 2;                                   // 跳过 @"
            int close = src.IndexOf('"', open, StringComparison.Ordinal);
            return close < 0 ? null : src[open..close];
        }
        static string Yes(string? v) => v is null ? "无" : "有";

        var prodCaller = LiteralOf("s_callerRegex");
        var prodWindow = LiteralOf("s_windowRegex");
        var prodInstance = LiteralOf("s_instanceRegex");
        var prodActivation = LiteralOf("s_activationRegex");
        Assert("生产侧四条正则都按字段名找得到（找不到 = 锚点漂了，不是代码坏了）",
            prodCaller is not null && prodWindow is not null &&
            prodInstance is not null && prodActivation is not null,
            $"实得 caller={Yes(prodCaller)} window={Yes(prodWindow)} instance={Yes(prodInstance)} activation={Yes(prodActivation)}");

        Assert("caller 正则与镜像逐字一致", prodCaller == CallerMirror, $"生产侧实得「{prodCaller}」");
        Assert("窗口归属正则与镜像逐字一致", prodWindow == WindowMirror, $"生产侧实得「{prodWindow}」");
        Assert("实例号正则与镜像逐字一致", prodInstance == InstanceMirror, $"生产侧实得「{prodInstance}」");
        Assert("激活次数正则与镜像逐字一致", prodActivation == ActivationMirror, $"生产侧实得「{prodActivation}」");

        // 旧判据不许回来（它才是这次失明的成因，且写起来只差一个点，最容易顺手改回去）
        Assert("旧的单点式 caller 判据已下线",
            !src.Contains(@"\s+([A-Za-z0-9_]+\.cs):(\d+)\s*$"),
            "[A-Za-z0-9_]+\\.cs 匹配不到 MainWindow.xaml.cs 这种两个点的名字 ⇒ 窗口行的调用者又会掉回 Message");

        // 行为面：拿镜像复现生产的归属结论（含两类被漏掉的窗口与必须排除的非窗口文件）
        var cases = new (string File, string Want)[]
        {
            ("SelectionOverlay.xaml.cs", "SelectionOverlay"),   // WinUI 窗口，代码后置，不以 Window 结尾
            ("MainWindow.xaml.cs", "MainWindow"),               // 同上（本仓主窗口）
            ("MainWindow.Settings.cs", "MainWindow"),           // 同一窗口的分部切片
            ("AnnotationWindow.xaml.cs", "AnnotationWindow"),
            ("HdrCaptureWindow.cs", "HdrCaptureWindow"),        // 自绘窗口（自己 CreateWindowExW 一个 HWND）
            ("App.xaml.cs", ""),                                // Application，不是窗口
            ("LogService.cs", ""),                              // 服务类，不该被误认成窗口
            ("ToastService.cs", ""),
        };
        foreach (var (file, want) in cases)
        {
            var m = Regex.Match(file, WindowMirror);
            var got = m.Success ? m.Groups["name"].Value : "";
            Assert($"窗口归属：{file} → 「{want}」", got == want, $"镜像实得「{got}」");
        }

        // 真实日志行的形状核对（这两条正是报告里"看不见归属"的那两行）
        const string overlayLine = "04:42:47.536 [Info   ] #qqwrtg [界面/Overlay] " +
            "Activated #2（实例#1）进入背景创建 SelectionOverlay.xaml.cs:189";
        var cm = Regex.Match(overlayLine, CallerMirror);
        Assert("真实覆盖层行能切出调用者文件", cm is { Success: true } && cm.Groups[1].Value == "SelectionOverlay.xaml.cs",
            $"实得「{(cm.Success ? cm.Groups[1].Value : "<未匹配>")}」");
        Assert("该行能归到 SelectionOverlay 窗口",
            cm.Success && Regex.Match(cm.Groups[1].Value, WindowMirror) is { Success: true } wm2 &&
            wm2.Groups["name"].Value == "SelectionOverlay");
        var im = Regex.Match(overlayLine, InstanceMirror);
        Assert("该行读得出实例号 1", im is { Success: true } && im.Groups[1].Value == "1", $"实得「{im.Groups[1].Value}」");
        var am = Regex.Match(overlayLine, ActivationMirror);
        Assert("该行读得出激活次数 2（Activated #N 这一支）",
            am is { Success: true } && am.Groups[1].Value == "2", $"实得「{am.Groups[1].Value}」");
        const string readyLine = "覆盖层就绪 实例#1 第30次进入 DPI=144 Scale=1.50 虚拟桌面=3840x2160@(0,0)";
        var am2 = Regex.Match(readyLine, ActivationMirror);
        Assert("'第N次进入'那一支也读得出同一个数（30）",
            am2 is { Success: true } && am2.Groups[2].Value == "30", $"实得「{am2.Groups[2].Value}」");

        // 解析出来的东西必须真在界面上露出来，否则这条归因就只是字段名
        var xaml = FindRepoFile(Path.Combine("src", "TrueToneCap.LogViewer", "MainWindow.xaml"));
        if (xaml is null)
        {
            Assert("LogViewer 列表模板已定位（MainWindow.xaml）", false, "未找到 src/TrueToneCap.LogViewer/MainWindow.xaml");
            return;
        }
        var template = ReadSourceNormalized(xaml);
        Assert("列表模板绑定窗口徽标（WindowDisplay + WindowVisibility）",
            template.Contains("{Binding WindowDisplay}") && template.Contains("{Binding WindowVisibility}"));
        Assert("列表模板绑定实例徽标（InstanceDisplay + InstanceVisibility）",
            template.Contains("{Binding InstanceDisplay}") && template.Contains("{Binding InstanceVisibility}"));
    }

    /// <summary>
    /// 覆盖层里不许再出现 GPU 背景那一路（B 档的反向哨兵，2026-10-01 报告 §39）。
    /// <para>被删的那条路不是"性能选型没选中"，是【实测从来没上屏】：标记帧实验里下层窗整帧品红 +
    /// 跳过覆盖层 CPU 合成，屏幕读到 0/6400 品红、三通道均值中性灰（判据与亮度无关，报告 §37.7）。
    /// 它当时会报"呈现成功"，而成功恰好把唯一画得出来的那段关掉 —— 这就是"预览全白"的成因。
    /// 所以本卡锁的不是形状好看，是【别再把它请回来】。</para>
    /// </summary>
    static void Test_Overlay_HasNoGpuBackgroundPath()
    {
        var ov = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml.cs"));
        if (ov is null) { Assert("覆盖层源文件在（缺文件不许静默过去）", false, "未找到 SelectionOverlay.xaml.cs"); return; }
        var src = StripLineComments(ReadSourceNormalized(ov));

        Assert("下层供帧窗的类不许被覆盖层再引用（删掉的是一条被证明不上屏的供帧路）",
            !src.Contains("HdrPreviewWindow"), "覆盖层里又出现了 HdrPreviewWindow");
        Assert("覆盖层里不许再有按名字找字节码去呈现的那一步",
            !src.Contains("PresentFrame("), "覆盖层里又出现了 PresentFrame");
        Assert("CPU 背景合成必须仍是无条件（当年那个「GPU 成功就跳过」的开关就是全白的成因）",
            !src.Contains("gpuBgOk") && src.Contains("RenderDesktopBackground(desktopPixels, vw, vh)"),
            "出现 gpuBgOk 这类「成功就跳过」的条件 = 恢复「预览全白」");
        Assert("就绪那行不许再承诺一个不存在的 GPU 字段（读数不能指向没有的东西）",
            !src.Contains("GPU背景="), "覆盖层就绪那行又写回了 GPU背景=");
        Assert("DWM 那套四边 -1 的透明尝试不许回来（实测在 WinUI 3 岛窗上没兑现）",
            !src.Contains("DwmExtendFrameIntoClientArea"),
            "又想靠透下去让另一扇窗供帧 ⇒ 先重做 §37.7 那个标记帧实验再说");
    }

    /// <summary>
    /// 选区覆盖层的防再入闩锁必须钉在 Activated 回调【入口】（2026-09-30 报告 §23.3 / §24.4 的实机风暴）。
    /// <para>起因：<c>tests/manual/safety/overlay-visual-cell.ps1</c> 在真身上量到——单实例（打点只有
    /// 实例#1）却打了 30 条「进入背景创建」、只有 1 条「背景渲染」，同一时刻 EnumWindows 数到
    /// 36 个可见的 <c>HDR Preview</c> 全屏窗。旧写法把 <c>_bgRendered = true</c> 放在回调【底部】，
    /// 而 CreateWindowEx / GPU 呈现这一段会跑嵌套消息泵 ⇒ Activated 在同一次调用里被再次派发，
    /// 每一层进来都看见 false ⇒ 每层建一个背景窗；最深那层置位后往外退，其余各层在底部看见 true，
    /// 又把 CPU 兜底跳过了 ⇒ 所以是"几十个窗 + 只渲染一次"这种看着矛盾的形状。</para>
    /// <para>为什么只能钉源码：Test 工程不引用 App，运行时够不到这个回调（同族的 App 侧盲区见报告 §18）。
    /// 而且这条不变量是【顺序】不变量——不是"有没有这句话"，是"这句话在那次分配之前"，
    /// 所以判据拿两个下标比大小；复位与包裹形态各钉一刀。</para>
    /// </summary>
    static void Test_OverlayReentryLatch_SitsBeforeWindowCreation()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml.cs"));
        if (p is null) { Skip("覆盖层防再入闩锁", "未找到 SelectionOverlay.xaml.cs"); return; }
        var src = StripLineComments(ReadSourceNormalized(p));

        int latch = src.IndexOf("_bgRendered = true", StringComparison.Ordinal);
        // 2026-10-01 B 档：原来的右锚点是 `new HdrPreviewWindow(`，那扇窗整个删了（报告 §39）。
        // 顺序不变量本身没变——闩锁必须先于【回调里那段会跑消息泵的重活】，所以右锚点换成仍然存在的
        // 背景渲染 + 区域检测那一步，语义不变：闩锁在后面 = 每次激活都再跑一遍。
        int alloc = src.IndexOf("RenderDesktopBackground(desktopPixels, vw, vh)", StringComparison.Ordinal);
        Assert("闩锁写入点与背景渲染段都还在（少一个说明锚点漂了，不是代码坏了）",
            latch >= 0 && alloc >= 0, $"实得 latch={latch} alloc={alloc}");
        Assert($"闩锁必须【先于】背景渲染段（实得 latch={latch} < alloc={alloc}）",
            latch >= 0 && alloc >= 0 && latch < alloc,
            "把 `_bgRendered = true` 挪回回调底部 = 恢复『一次捕获叠 30 个全屏窗』的那个形状");
        Assert("闩锁全文件只许写一次 true、不许有复位",
            System.Text.RegularExpressions.Regex.Matches(src, @"_bgRendered\s*=\s*true").Count == 1
            && !src.Contains("_bgRendered = false"),
            "GPU 成功路径上那句重复置位是当年风暴的伴生物；多一个复位入口，入口闩锁就退化成『每次激活都重建』");
        Assert("CPU 兜底 + 窗口检测不许再被闩锁包着",
            !src.Contains("if (!_bgRendered)"),
            "闩锁移到入口后若仍留着这个判断，兜底和窗口检测会被静默关死——比风暴更糟");
        Assert("入口早退守卫只许有一处（防再入的唯一出口）",
            System.Text.RegularExpressions.Regex.Matches(src, @"if\s*\(_bgRendered\)").Count == 1,
            "多于一个说明有人把守卫复制进了分支里，两处读的是同一个闩锁却是两套行为");
    }


    /// <summary>
    /// 被防重入闸拦下的那一次按键【不是失败】，不许再借"截图失败"的通知通道。
    /// <para>起因（2026-09-30，造 GPU 失败腿时撞出来的）：同一毫秒两行日志
    /// <c>截图已在进行中，忽略重复触发</c> 紧跟 <c>已提交系统通知: ❌ 截图失败</c>
    /// ——红色失败通知的正文写的却是"请稍候"，标题与正文互相打脸。
    /// 用户视角更糟：他按了个键，屏幕上什么都没发生，却收到一条"截图失败"。</para>
    /// <para>为什么钉形状而不是钉文案：文案会改，通道不会随便换。
    /// <c>ShowCaptureFailed</c> 走的是老的 <c>Show(dict)</c> 直发系统通知，
    /// 既忽略用户设的"通知位置"，也把"被拦下"和"真失败"混成同一个出口。</para>
    /// </summary>
    static void Test_BusyRejection_DoesNotUseFailureToast()
    {
        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        if (p is null) { Skip("防重入通知语义", "未找到 MainWindow.xaml.cs"); return; }
        var src = StripLineComments(ReadSourceNormalized(p));
        int at = src.IndexOf("忽略重复触发", StringComparison.Ordinal);
        Assert("防重入闸那一支还在（锚点漂了就不判，不蒙绿）", at >= 0, "找不到『忽略重复触发』这行 ⇒ 后两条没有意义");
        var win = at >= 0 ? src[at..Math.Min(src.Length, at + 400)] : "";
        Assert("拦下之后 400 字符内不许再出现 ShowCaptureFailed",
            at >= 0 && !win.Contains("ShowCaptureFailed"),
            "红色『❌ 截图失败』与『请稍候』正文打脸；被拦下的那一发并没有失败");
        Assert("要改走带 tone 的统一入口 ShowToast + ToastTone.Info",
            at >= 0 && win.Contains("ShowToast(") && win.Contains("ToastTone.Info"),
            "统一入口才尊重用户设的『通知位置』（旧路径直发系统通知，忽略该设置）");
    }

    /// <summary>
    /// HDR 抓帧窗（<c>Services/HdrCaptureWindow.cs</c>）的界面文字是【直接画进像素缓冲】的
    /// （<c>DrawTextGdi(...)</c> / <c>ds.DrawText(...)</c>），不是 XAML 控件 ⇒ 三条检测路径全瞎：
    /// 本地化闸门只扫 XAML 字面量、UIA 实测那块全屏窗暴露 0 个控件（2026-09-30）、
    /// 降级与翻译告警也不看这里。本条把这块盲区钉成一个【可数的数】：
    /// 谁再往那扇窗里烘焙一条中文，闸门就红。
    /// <para>计数单位 = 手写扫描器切出的完整字面量（<c>StringLiteralScanner</c>；
    /// 它自己的对抗用例 <c>StringLiteralScannerTests</c> 就在同一次运行里跑，尺子坏了先响）。
    /// 上一轮拿正则数：实测 98 而手点 41 —— 正则把 <c>$"…{expr}…"</c> 从插值洞处切成
    /// <c>)} 全屏={(_mpoFullsc</c> 这种碎片，单位一旦没有意义，基线就是一把随机抖动的尺子 ⇒ 禁用正则计数。</para>
    /// <para>缺文件判红而不是 Skip：本仓 memory 记过"缺文件即 Skip = 集体失明"这一类缺陷，
    /// 这条锁判的正是"没人看得见的那扇窗"，再留一个静默跳过就等于把锁拆开。</para>
    /// </summary>
    static void Test_HdrBakedText_BlindSpotIsCounted()
    {
        // ═══ 基线：2026-09-30 实测（HdrCaptureWindow.cs 里含 U+4E00–U+9FFF 的字面量条数）═══
        // 用另一个独立实现（Python 版同一规格扫描器）对过同一份文件：同样 98 条，计数单位是整条字面量。
        // 改动这个数只能是【刻意的】：中文真的增减、或把某条接进了本地化 ⇒ 在同一次改动里同步调它，
        // 并在提交说明里说清楚。不许为了让闸门变绿而随手改这里。
        // 2026-09-30：98 → 99。抬升的原因是新增了一条【日志】串（HdrCaptureWindow.cs 的
        // `选区完成 WxH …`，给"选区后按键有没有落地"这类判据当前提见证），不是画进位图的字变多了。
        // 为什么算在基线里而不是给它开个例外：这把尺的单位本来就是"该文件里的中文字面量条数"——
        // 头六条里 `"CreateWindowExW 失败: {…}"`、`"MPO 检测失败"`、`"VRR 支持: ✓ 启用"` 都是日志串。
        // 单位变了才要改口径；只是数量 +1 就走它自己写的解释①（同一次改动里改基线并说明）。
        // 2026-10-26：99 → 106（+7，HDR 工具条补"二维码"按钮）→ 45（−61，契约单源化）。
        // 当天第二次改动的缘故：按钮文字/提示/字形字面量整体搬进 Models/ToolbarContract.cs
        //（图集 8 + GDI 回退 14 + 预热 14 + Down() 命中 14 + 标注 11 = 61 条离开本文件），
        // 留下的是日志/诊断串。烘焙文案的可见性由此换了一个更强的把守者：
        // Test_ToolbarContract_SingleSource 盯着契约形状、两渲染器取数与对照表键位。
        const int BakedCnBaseline = 45;

        var p = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "HdrCaptureWindow.cs"));
        if (p is null)
        {
            Assert("HDR 抓帧窗盲区计数：那份源码必须定位得到", false,
                "FindRepoFile 未命中 src/TrueToneCap.App/Services/HdrCaptureWindow.cs ⇒ " +
                "这里不许 Skip：判不到的文件上，下面的否定断言会在空串上恒成立（假绿）");
            return;
        }

        var src = ReadSourceNormalized(p);
        var codeOnly = StripLineComments(src);          // 锚点判据只看代码，注释里提一句 LocaleManager 不该把闸门挤红
        var scan = StringLiteralScanner.Scan(src);
        var cjk = StringLiteralScanner.CjkLiterals(scan);
        static string Trunc(string t, int max = 30) => t.Length <= max ? t : t[..max] + "…";

        // 扫描器自己没跑偏才谈得上计数（未闭合 ⇒ 残破那条已经被丢弃，但单位就不再是"整条字面量"了）
        Assert("HDR 抓帧窗：扫描全程没有未闭合字面量（有的话下面那个数不是【字面量】计数）",
            scan.UnterminatedAt < 0 && scan.Literals.Count > 0,
            scan.UnterminatedAt < 0 ? $"一条字面量都没扫到（实得 {scan.Literals.Count}）⇒ 文件读空了？" : scan.UnterminatedNote);

        Assert($"HDR 抓帧窗烘焙中文条数 == 基线 {BakedCnBaseline} 条（实得 {cjk.Count} 条）",
            cjk.Count == BakedCnBaseline,
            $"基线 {BakedCnBaseline} vs 实得 {cjk.Count}；前 6 条长这样（让你看清计数单位是什么）：" +
            string.Join("｜", cjk.Take(6).Select(l => Trunc(l.Text))) + "。" +
            "只有两种正当解释：①那扇窗的中文确实增减 / 接进了本地化 ⇒ 同一次改动里改基线并说明；" +
            "②扫描器坏了 ⇒ 先看 StringLiteralScannerTests 是否也红");

        // ── 锚点①：盲区这个前提还在（文字仍然是画进像素的）──
        int gdi = Regex.Matches(codeOnly, @"DrawTextGdi\(").Count;
        int w2d = Regex.Matches(codeOnly, @"\.DrawText\(").Count;
        Assert($"两条绘制腿都还在（DrawTextGdi( 实得 {gdi} 处 / .DrawText( 实得 {w2d} 处）",
            gdi >= 1 && w2d >= 1,
            "一条都没了 ⇒ 那扇窗不再自己画字，本条的基线与前提一起过期，应删锁并改走真正的本地化闸门");

        // ── 锚点②：不许有绘制腿【直接】吃 LocaleManager 的文本 ──
        // 有人真去本地化它时，这条会红 ⇒ 逼他在同一次改动里把中文条数（上面那个基线）降下来，
        // 而不是"接了本地化、基线照旧"—— 那等于盲区计数停在旧世界。
        var fedFromLocale = Regex.Matches(codeOnly, @"(DrawTextGdi|\.DrawText)\([^;]*LocaleManager")
            .Select(m => Trunc(m.Value)).ToList();
        Assert("绘制腿没有一处把 LocaleManager 的文本直接喂进去（实得 " + fedFromLocale.Count + " 处）",
            fedFromLocale.Count == 0,
            "要么已经本地化 ⇒ 基线必须同批缩小；要么是没接线（拿 LocaleManager 当装饰），两种都要先说清楚：" +
            string.Join("｜", fedFromLocale));
    }

    /// <summary>
    /// 工具条契约单源化的判据（2026-10-26）。
    /// <para>背景：二维码按钮 2026-10-26 只落 SDR 覆盖层、漏了 D3D 预览窗（用户实机发现）——
    /// 根因是同一份按钮清单存在 5 份副本（HdrCaptureWindow 图集/GDI 回退/启动预热/Down() 命中
    /// + SelectionOverlay.xaml），改一漏四没有任何闸门会响。收敛后 <c>ToolbarContract</c> 是唯一声明处；
    /// 本判据盯三件事：① 契约自身形状（主 7 钮/顺序/文案、标注 10 工具）不许被悄悄改动；
    /// ② 两个渲染器必须真的从契约取数 —— HdrCaptureWindow 里不许再长出按钮提示/字形字面量，
    /// SelectionOverlay.xaml 里不许再有主工具条按钮；③ 契约每条文案必须是 LocaleManager
    /// 对照表的键 —— D3D 烘字那扇"三路全盲"的窗（明细报告 §55 / §34.6-3）从声明层接进
    /// 本地化账本，这是给盲区补的第一路检测。</para>
    /// <para>为什么是源码哨兵：Test 只引用 Core，进不了 App 程序集（与位深协商那组同理由）。</para>
    /// </summary>
    static void Test_ToolbarContract_SingleSource()
    {
        var ctPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Models", "ToolbarContract.cs"));
        var hdrPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "HdrCaptureWindow.cs"));
        var xamlPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml"));
        var ovPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "SelectionOverlay.xaml.cs"));
        var lcPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (ctPath is null || hdrPath is null || xamlPath is null || ovPath is null || lcPath is null)
        {
            Assert("工具条契约单源：五份源文件必须都定位得到", false,
                "缺文件会让下面的否定断言在空串上恒成立（假绿），不许静默跳过：" +
                string.Join(",", new[] { ctPath, hdrPath, xamlPath, ovPath, lcPath }.Select(p => p is null ? "空" : "有")));
            return;
        }

        var ct = ReadSourceNormalized(ctPath);

        // ── ① 契约形状：主工具条 7 钮，顺序/动作/文字不许漂 ──
        int m0 = ct.IndexOf("ToolbarButtonDef[] Main", StringComparison.Ordinal);
        int mEnd = m0 >= 0 ? ct.IndexOf("\n    };", m0, StringComparison.Ordinal) : -1;
        Assert("契约：Main 声明块找得到（形状变了判据得跟着改，不许静默放行）", m0 >= 0 && mEnd > m0);
        if (m0 < 0 || mEnd <= m0) return;
        var mainBlock = ct[m0..mEnd];
        var entries = Regex.Matches(mainBlock, @"new\(MainToolbarAction\.(\w+), ""([^""]*)"", ""([^""]*)"", ""([^""]*)""")
            .Select(m => (Action: m.Groups[1].Value, Glyph: m.Groups[2].Value, Text: m.Groups[3].Value, Hint: m.Groups[4].Value))
            .ToList();
        Assert($"契约：主工具条必须声明 7 枚按钮（实得 {entries.Count}）", entries.Count == 7,
            "多一枚少一枚都意味着两个渲染器的按钮数一起变 —— 这种改动必须连 GetToolbarLayout/命中/图集一起核");
        Assert("契约：主按钮动作序 == 保存/标注/复制/识字/翻译/二维码/取消（SDR 与 HDR 的命中序都由它决定）",
            string.Join(",", entries.Select(e => e.Action))
                == "Save,Annotate,Copy,Ocr,Translate,Qr,Cancel",
            "实得：" + string.Join(",", entries.Select(e => e.Action)));
        Assert("契约：按钮文字 == 保存/标注/复制/识字/翻译/二维码/取消",
            string.Join(",", entries.Select(e => e.Text)) == "保存,标注,复制,识字,翻译,二维码,取消",
            "实得：" + string.Join(",", entries.Select(e => e.Text)));
        Assert("契约：7 条提示互不相同且都非空（提示槽按句宽取最大值，重复/空串都是声明事故）",
            entries.All(e => e.Hint.Length > 0) && entries.Select(e => e.Hint).Distinct().Count() == 7);

        var anno0 = ct.IndexOf("ToolbarAnnoTool[] Anno", StringComparison.Ordinal);
        var annoEnd = anno0 >= 0 ? ct.IndexOf("\n    };", anno0, StringComparison.Ordinal) : -1;
        Assert("契约：Anno 声明块找得到", anno0 >= 0 && annoEnd > anno0);
        if (anno0 < 0 || annoEnd <= anno0) return;
        var annoTexts = Regex.Matches(ct[anno0..annoEnd], @"new\(""([^""]*)"", ""([^""]*)""\)")
            .Select(m => m.Groups[2].Value).ToList();
        Assert("契约：标注工具 10 枚，顺序即 OnAnnoToolbarClick 的分支序（矩形…取消）",
            string.Join(",", annoTexts) == "矩形,椭圆,箭头,画笔,文字,马赛克,撤销,重做,完成,取消",
            "实得：" + string.Join(",", annoTexts));
        Assert("契约：AnnoHint 静态提示存在（图集槽 18 的内容）",
            ct.Contains("AnnoHint = \"拖拽绘制标注\"", StringComparison.Ordinal));

        // ── ③ 文案必须都在本地化对照表（给烘字盲区补的路） ──
        var lc = File.ReadAllText(lcPath);
        var missingKeys = entries.SelectMany(e => new[] { e.Text, e.Hint })
            .Where(s => !lc.Contains("[\"" + s + "\"]", StringComparison.Ordinal)).ToList();
        Assert("契约：每条按钮文字/提示都是 LocaleManager 对照表的键（缺 " + missingKeys.Count + " 条）",
            missingKeys.Count == 0,
            "SDR 的运行时扫描靠这张表翻英文，D3D 烘字靠它留声明账 ⇒ 缺键：" + string.Join("｜", missingKeys));

        // ── ②-1 D3D 预览窗必须从契约取数，不许再长出按钮文案 ──
        var hdr = StripLineComments(ReadSourceNormalized(hdrPath));
        int refs = Regex.Matches(hdr, "ToolbarContract\\.").Count;
        Assert("D3D 预览窗：ToolbarContract 引用 ≥ 4 处（图集/CompositeUI 提示宽/GDI 回退/预热/命中）",
            refs >= 4, $"实得 {refs} 处 —— 少到 4 以下说明又有人把清单抄回了文件里");
        var hintsInHdr = entries.Select(e => e.Hint).Where(h => hdr.Contains(h, StringComparison.Ordinal)).ToList();
        Assert("D3D 预览窗：不再出现任何按钮提示字面量（实得 " + hintsInHdr.Count + " 条）",
            hintsInHdr.Count == 0,
            "提示只有契约一处可改 —— 这里再出现就是改一漏四的老路：" + string.Join("｜", hintsInHdr));
        var glyphsInHdr = entries.Select(e => e.Glyph).Where(g => hdr.Contains(g, StringComparison.Ordinal)).ToList();
        Assert("D3D 预览窗：不再出现任何按钮字形转义（\\u2611 这批只属于契约；实得 " + glyphsInHdr.Count + " 个）",
            glyphsInHdr.Count == 0,
            "字形+文字的合成串在运行时拼 ⇒ 文件里出现字形转义就是手抄清单回归：" + string.Join("｜", glyphsInHdr));

        // ── ②-2 SDR 覆盖层：主工具条按钮必须由契约生成 ──
        var xaml = ReadSourceNormalized(xamlPath);
        Assert("SDR 覆盖层：XAML 里不再有主工具条按钮（x:Name=\"Btn…\" 实得 "
            + Regex.Matches(xaml, "x:Name=\"Btn").Count + " 个）",
            !xaml.Contains("x:Name=\"Btn", StringComparison.Ordinal),
            "按钮回到 XAML = 契约外多出一份清单（二维码按钮事故的形状）");
        var goneHandlers = new[] { "OnConfirm", "OnCopy", "OnOcr", "OnTranslate", "OnQr", "OnCancel" }
            .Where(h => xaml.Contains("Click=\"" + h + "\"", StringComparison.Ordinal)).ToList();
        Assert("SDR 覆盖层：被契约生成取代的旧 Click 处理器不许残留（实得 " + goneHandlers.Count + " 个）",
            goneHandlers.Count == 0, "残留：" + string.Join(",", goneHandlers));
        var ov = StripLineComments(ReadSourceNormalized(ovPath));
        Assert("SDR 覆盖层：BuildMainToolbar 从 ToolbarContract.Main 生成按钮（两处锚点都要在）",
            ov.Contains("ToolbarContract.Main", StringComparison.Ordinal)
            && ov.Contains("BuildMainToolbar", StringComparison.Ordinal),
            "事件处理器在而生成逻辑没了 ⇒ 按钮哪来的？");
        Assert("SDR 覆盖层：旧 ActionResult 枚举不许回来（动作词表已并入 MainToolbarAction）",
            !ov.Contains("enum ActionResult", StringComparison.Ordinal));
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
            code.Contains("ReadEdidFromRegistry(nint monitorHandle, out string source)") &&
            code.Contains("GetMonitorRegistryInstancePath(monitorHandle)"));
        Assert("注册表键由解析出的实例路径拼出（不是硬编码遍历 DISPLAY 树）",
            code.Contains(@"Enum\{instancePath}\Device Parameters"));
        Assert("不再存在『遍历 DISPLAY 子键取第一个合法 EDID』的写法",
            !code.Contains("foreach (var subKeyName in displayKey.GetSubKeyNames())"));
        // ═══ 2026-10-21：光"按句柄查"还不够 —— 查下去之后恒取 index 0 仍然会拿错屏 ═══
        // 本机实测：\\.\DISPLAY1 名下 Monitor0=AUO75AC(DISPLAY_DEVICE_ACTIVE 为假)、
        // Monitor1=SAC2765(活动)。旧写法取 index 0 ⇒ 外接 P3 屏吃的是没在用的内屏 EDID，
        // ΔG 从 0.0867 被读成 0.0234 ⇒ "宽"翻成"不宽"，源矩阵跟着取错。
        Assert("设备接口路径那一跳必须走『挑活动那一台』的helper（不再直接按 index 0 调）",
            code.Contains("TryGetActiveMonitor(deviceName, 1u, out var dd)") &&
            !code.Contains("EnumDisplayDevicesW(deviceName, 0u"));
        Assert("挑活动那一台时看的是 DISPLAY_DEVICE_ACTIVE，且没有活动项就返回 false（= 不知道）",
            code.Contains("if ((dd.StateFlags & DisplayDeviceActive) == 0) continue;") &&
            code.Contains("active = default;") && code.Contains("return false;"));
        Assert("显示器指纹（缓存键）同一条纪律：也走 TryGetActiveMonitor，不再各写一遍 index 0",
            code.Contains("TryGetActiveMonitor((monitorInfo.szDevice ?? \"\").Trim(), 0u, out var dd)"));
        Assert("HMONITOR→设备接口路径这一步失败时返回 null（拿不到，而非拿错）",
            code.Contains("if (!TryGetActiveMonitor(deviceName, 1u, out var dd)) return null;"));
        Assert("逐屏证据行带 EDID 来源实例路径 ⇒ 读错屏在卡片上直接看得见",
            code.Contains("EDID来源={edidSource}"));

        // ═══ 闸门与标签【同源】：直接拿真机量到的原色坐标喂判据本身 ═══
        // 为什么这一段必须存在：2026-09-22 审查轮做突变实验时，"把闸门整个去掉"跑出来是
        // 673/0 全绿 —— 因为闸门原先埋在 ProbeNativeWideGamut(nint) 里，需要真实 HMONITOR，
        // 单测一格都测不到，而真值表里那份 DisplayInfo 的原生标签是我手写的。
        // 判据不可测 = 判据不存在。现在闸门单独成函数，下面这些行才拿得动它。
        var gate = new System.Func<double, double, double, double, string>(
            TrueToneCap.Core.ColorManagement.ColorProfileProvider.NativeGamutTagFor);
        // ═══ 本机三块屏的实测原色（2026-10-21 用修正后的取位从注册表 EDID 复算）═══
        // 旧版这里记的 0.0246 / 0.0963 是【错位取位】读出来的（见 Test_Edid_ChromaticityBitsMatchSpec），
        // 结论方向没变，但坐标不是屏的真值 ⇒ 换成规范取位的读数，并把"现在正在用的那块"补进来。
        Assert("★ 本机内屏实测原色（AUO75AC / B160QAN02.7，ΔG=0.0234）⇒ 标签只能是 sRGB",
            gate(0.6621, 0.3311, 0.2959, 0.6230) == "sRGB");
        Assert("★ 本机外接屏实测原色（SAC2765 / DM72u，ΔG=0.0867）⇒ 标签确证为宽",
            gate(0.6826, 0.3086, 0.2598, 0.6768) != "sRGB");
        Assert("★ 本机外接屏（SAC2765）细标签就是 DisplayP3 ⇒ 源矩阵取的是 P3，不是 sRGB",
            gate(0.6826, 0.3086, 0.2598, 0.6768) == "DisplayP3");
        Assert("★ 上一代外接屏实测原色（PHLC38F / 27M2N5800P，ΔG=0.0989）⇒ 标签确证为宽",
            gate(0.6807, 0.3174, 0.2578, 0.6895) != "sRGB");
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
        Assert("门限两侧留量：确证窄(0.0234) 与确证宽(0.0867/0.0989) 都在 0.05 的正确一侧",
            TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.2959, 0.6230) < ColorProfileProvider.GreenStretchForWide
            && TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.2598, 0.6768) > ColorProfileProvider.GreenStretchForWide
            && TrueToneCap.Core.ColorManagement.ColorProfileProvider
                    .GreenStretchFromGreen(0.2578, 0.6895) > ColorProfileProvider.GreenStretchForWide);
    }

    /// <summary>
    /// EDID 色度那 10 个字节的<b>取位</b>判据（2026-10-21）。
    /// <para>为什么单独一条：这一版之前 <c>ParseEdidChromaticity</c> 把四个坐标的低 2 位全从 0x19 里取
    /// （Rx 取到 By、Ry 取到 Bx、Gx 取到 Gy、Gy 取到 Gx），注释还把规范抄反了，而<b>没有任何断言
    /// 观测这个函数</b> —— 它错了 3 年没人知道。误差 ≤3/1024≈0.0029/坐标，本机结论方向没翻，
    /// 但闸门是 0.05，贴阈值的屏会被这点错位翻面。</para>
    /// <para>夹具是两块屏的<b>真字节</b>（本机注册表 EDID 的 0x19-0x22）加一条构造的极端样本：
    /// 构造样本故意让每个坐标的低 2 位互不相同，旧取位与规范取位因此给出不同的数 ——
    /// 这就是这条判据的牙（做完变异实测：把取位改回旧写法，本条立刻红）。</para>
    /// </summary>
    static void Test_Edid_ChromaticityBitsMatchSpec()
    {
        var parse = new System.Func<byte[], (float rx, float ry, float gx, float gy)>(
            TrueToneCap.Core.ColorManagement.ColorProfileProvider.ParseEdidChromaticity);
        var dg = new System.Func<double, double, double>(
            TrueToneCap.Core.ColorManagement.ColorProfileProvider.GreenStretchFromGreen);

        // ── 真机字节（本机三块屏，从 HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\<id>\Device Parameters\EDID 取）──
        static byte[] WithChroma(string hex10)
        {
            var e = new byte[128];
            for (int i = 0; i < 7; i++) e[i] = 0xFF;   // 合法头
            e[0] = 0x00; e[7] = 0x00;
            var compact = hex10.Replace(" ", "");
            for (int i = 0; i < 10; i++)
                e[0x19 + i] = System.Convert.ToByte(compact.Substring(i * 2, 2), 16);
            return e;
        }
        (float, float, float, float) Expect(string hex10)
        {
            var e = WithChroma(hex10);
            float V(int hi, int lo, int shift) => (e[hi] * 4 + ((e[lo] >> shift) & 3)) / 1024f;
            return (V(0x1B, 0x1A, 6), V(0x1C, 0x1A, 4), V(0x1D, 0x19, 6), V(0x1E, 0x19, 4));
        }
        bool Same((float rx, float ry, float gx, float gy) a, (float rx, float ry, float gx, float gy) b)
            => System.Math.Abs(a.rx - b.rx) < 1e-6 && System.Math.Abs(a.ry - b.ry) < 1e-6
            && System.Math.Abs(a.gx - b.gx) < 1e-6 && System.Math.Abs(a.gy - b.gy) < 1e-6;

        foreach (var (name, hex, wantDg, wide) in new (string, string, double, bool)[]
        {
            ("AUO75AC 内屏", "EC B5 A9 54 4B 9F 24 0E 50 54", 0.0234, false),
            ("SAC2765 外接", "9D C5 AE 4F 42 AD 23 0D 50 54", 0.0867, true),
            ("PHLC38F 外接", "2F 55 AE 51 42 B0 24 0E 50 54", 0.0989, true),
        })
        {
            var got = parse(WithChroma(hex));
            Assert($"★ {name}：按规范取位解出的坐标与独立复算逐位相同", Same(got, Expect(hex)));
            Assert($"★ {name}：ΔG 复算 = {wantDg:F4}（{(wide ? "过" : "不过")} 0.05 闸门）",
                System.Math.Abs(dg(got.gx, got.gy) - wantDg) < 0.0005
                && (dg(got.gx, got.gy) >= 0.05) == wide);
            // 白点是这条判据的裁决者：三块屏都该落在 D65 附近（背光白点由厂家定，与色域无关）。
            var e = WithChroma(hex);
            float wX = (e[0x21] * 4 + ((e[0x1A] >> 2) & 3)) / 1024f;
            float wY = (e[0x22] * 4 + (e[0x1A] & 3)) / 1024f;
            Assert($"★ {name}：白点解出来落在 D65 附近（取位方向对了才会有这个数）",
                System.Math.Abs(wX - 0.3127f) < 0.005f && System.Math.Abs(wY - 0.3290f) < 0.005f);
        }

        // ── 构造样本：四个坐标的低 2 位互不相同 ⇒ 旧取法（全从 0x19 取）必然算错 ──
        // Rx=0.6406(低2位=3) Ry=0.3311(2) Gx=0.3008(1) Gy=0.6006(0) Bx=0.1504(1) By=0.0605(3)
        var synth = WithChroma("FF 00 00 00 00 00 00 00 00 00");
        synth[0x19] = 0b01_00_01_11;   // Gx=01 Gy=00 Bx=01 By=11
        synth[0x1A] = 0b11_10_00_00;   // Rx=11 Ry=10 Wx=00 Wy=00
        synth[0x1B] = 164; synth[0x1C] = 84; synth[0x1D] = 76; synth[0x1E] = 150;
        var s = parse(synth);
        Assert("构造样本：Rx 的低 2 位取自 0x1A 的 7-6（旧写法会取到 By 的位）",
            System.Math.Abs(s.rx - (164 * 4 + 3) / 1024f) < 1e-6);
        Assert("构造样本：Ry 的低 2 位取自 0x1A 的 5-4",
            System.Math.Abs(s.ry - (84 * 4 + 2) / 1024f) < 1e-6);
        Assert("构造样本：Gx 的低 2 位取自 0x19 的 7-6（旧写法会取到 Gy 的位）",
            System.Math.Abs(s.gx - (76 * 4 + 1) / 1024f) < 1e-6);
        Assert("构造样本：Gy 的低 2 位取自 0x19 的 5-4",
            System.Math.Abs(s.gy - (150 * 4 + 0) / 1024f) < 1e-6);
        // 变异对照：旧写法把 Gy 的低位(0x19 的 7-6 = 1)安到 Gy 上 ⇒ 差 1/1024，本条能分辨
        Assert("构造样本与旧取位不同 ⇒ 这条判据真的有牙（旧写法会给出 gy=+1 LSB）",
            System.Math.Abs(s.gy - (150 * 4 + 1) / 1024f) > 1e-6);
    }


    /// <summary>
    /// 识字/翻译预览窗那一次必崩的三把锁（2026-10-21，台账 §70）。
    /// <para>缺陷形状：<c>OcrPreviewWindow.xaml</c> 的标记里写了 <c>IsChecked="True"</c>，
    /// 加载期抛 <c>XamlParseException / 0x802B000A</c>，内层原文
    /// "Failed to assign to property 'Microsoft.UI.Xaml.Controls.Primitives.ToggleButton.IsChecked'"；
    /// 而开窗那句在 <c>DispatcherQueue.TryEnqueue</c> 的<b>回调体</b>里 —— 外层那个 try/catch
    /// 只包住"入队"这个动作，接不到回调里抛的东西 ⇒ 异常逃进分发器，升级成原生 fail-fast
    /// （<c>0xc000027b</c> @ CoreMessagingXP+0x97976），进程直接消失、托管侧零日志。</para>
    /// <para>三把锁各钉一层，少一层都会回到"崩了没人知道"：①标记里不许再有那个赋值（改到代码里赋）；
    /// ②回调体自己必须 try/catch；③门禁必须真把这两扇窗各建一次（<c>--window-probe</c>，判据=退出码）。</para>
    /// </summary>
    static void Test_OcrPreviewWindow_ConstructionGuarded()
    {
        var xamlP = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "OcrPreviewWindow.xaml"));
        var ctorP = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "OcrPreviewWindow.xaml.cs"));
        var mwP = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var gateP = FindRepoFile(Path.Combine("scripts", "verify.ps1"));
        Assert("找到预览窗两份源 + 主窗 + 门禁脚本（缺件=本卡失明）",
            xamlP is not null && ctorP is not null && mwP is not null && gateP is not null);
        if (xamlP is null || ctorP is null || mwP is null || gateP is null) return;

        var xaml = File.ReadAllText(xamlP);
        Assert("★ 标记里【不许】给 ToggleButton 赋 IsChecked（2026-10-21 实测：加载期 0x802B000A）",
            !System.Text.RegularExpressions.Regex.IsMatch(xaml, @"<ToggleButton[^>]*IsChecked",
                System.Text.RegularExpressions.RegexOptions.Singleline));

        var ctor = ReadSourceNormalized(ctorP);
        int initAt = ctor.IndexOf("this.InitializeComponent();", StringComparison.Ordinal);
        int codeAt = ctor.IndexOf("OverlayToggle.IsChecked = true;", StringComparison.Ordinal);
        Assert("默认档改到代码里赋，且排在 InitializeComponent 之后", initAt >= 0 && codeAt > initAt);

        var mw = ReadSourceNormalized(mwP);
        int guarded = 0, opened = 0, at = 0;
        while (true)
        {
            at = mw.IndexOf("DispatcherQueue.TryEnqueue(() =>", at, StringComparison.Ordinal);
            if (at < 0) break;
            int end = mw.IndexOf("});", at, StringComparison.Ordinal);
            if (end < 0) break;
            var body = mw.Substring(at, end - at);
            if (body.Contains("OpenOcrPreviewWindow("))
            {
                opened++;
                if (body.Contains("try") && body.Contains("catch (Exception")) guarded++;
            }
            at = end + 3;
        }
        Assert($"两处「开窗」回调各自带 try/catch（实得 {guarded}/{opened}）",
            opened == 2 && guarded == 2,
            "回调体不受外层 try/catch 保护 ⇒ 异常逃进分发器就变成原生 fail-fast，托管侧一行日志都没有");

        var gate = File.ReadAllText(gateP);
        Assert("门禁把这两扇窗真建一次：--window-probe 已接线，且拿不到退出码记【未判】而不是绿",
            gate.Contains("'--window-probe'") && gate.Contains("--window-probe 退出码为空 ⇒ 未判")
            && gate.Contains("运行期 XAML 窗构造探针: 退出码 0"));
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

        // ══════════════════════════════════════════════════════════════════════════════
        //  2026-10-06：JPEG 家族 / WebP 的「默认档就是 Rec.709」（格式口径收口）
        // ══════════════════════════════════════════════════════════════════════════════
        // 为什么要有这一整块：上面那张 4 参表守的是【屏幕证据】这条口径，而本轮新增的是【格式口径】——
        // 同一块【完全授权的 P3 屏】上，光是用户选了 JPEG LI/WebP，产物的目标色域就从 Rec.2020 变成
        // Rec.709。这是产品口径变更（用户没选过广色域 ⇒ 不替他升级），与"这块屏给不给"毫无关系。
        // ⚠ 上面 4 参的断言【一条都不用改】：format 是可选参数，不传 = 维持今天的行为。这正是本轮
        //   把它做成可选参数而不是改默认行为的目的；下面每一格都要拿 5 参形状去问。
        // ⚠ 这一块必须自己扛住"两种收口不许串味"：屏幕钳制先跑（置 GamutClamped），格式钳制后跑
        //   （置 Rec709Defaulted），而"显式 BT.2020 + 窄屏 + JPEG LI"那格两条都真 —— 标记必须各归各。

        // ── (1) 白名单本身：哪些格式算"默认档就是 Rec.709" ──
        // 为什么单钉这一条：设计上用【白名单】而不是黑名单（见 DefaultsToRec709 的注释）——
        // 以后往 OutputFormat 里加新格式时，它默认落进"不改变今天行为"那一侧，必须有人主动登记。
        // 这条断言就是那次"主动登记"的账本：漏登记 = 新格式静默跟随显示器，而界面上看不出来。
        Assert("★ 默认档 = Rec.709 的格式【正好】是 JPEG LI / GainMap / WebP 三个（白名单，新格式默认不登记）",
            CDec.DefaultsToRec709(OutputFormat.JPEG_LI)
            && CDec.DefaultsToRec709(OutputFormat.JPEG_GAINMAP)
            && CDec.DefaultsToRec709(OutputFormat.WebP)
            && !CDec.DefaultsToRec709(OutputFormat.PNG)
            && !CDec.DefaultsToRec709(OutputFormat.JPEG_XL)
            && !CDec.DefaultsToRec709(OutputFormat.AVIF)
            && !CDec.DefaultsToRec709(OutputFormat.TIFF),
            string.Join(" ", Enum.GetValues<OutputFormat>()
                .Select(f => $"{f}={CDec.DefaultsToRec709(f)}")));
        // null 是"这次调用没有格式上下文"（动画录制 / 自检 / 静默采样）⇒ 必须 false，
        // 否则那族入口会跟着一起改行为，而它们根本没有"用户选的那个格式"这回事。
        Assert("★ 没有格式上下文的调用（format=null）不在白名单里 ⇒ 那族入口行为逐字不变",
            !CDec.DefaultsToRec709(null));

        // ── (2) 核心新行为：完全授权的 P3 屏 + 跟随显示器 + 三个格式 ⇒ 目标被收成 sRGB ──
        // 这一格是本轮的产品主张：屏【完全授权】广色域（不是被屏幕证据压掉的），收口纯粹来自格式口径。
        // 故 GamutClamped 必须为假（它说的是"屏不给"，这里屏明明给了），Rec709Defaulted 才是那个原因。
        foreach (var (f, why) in new[]
                 {
                     (OutputFormat.JPEG_LI, "JPEG LI 的消费端固定假设 Rec.709"),
                     (OutputFormat.WebP, "WebP 同上（浏览器/聊天软件按 Rec.709 解）"),
                     (OutputFormat.JPEG_GAINMAP, "Ultra HDR 的底图按约定也是 Rec.709"),
                 })
        {
            var d = CDec.Build(wideP3, "System", false, Acm, f);
            Assert($"★ {f} + 确证 P3 屏 + 跟随显示器 ⇒ 目标收成 sRGB 且记 Rec709Defaulted（{why}）",
                d.TargetTag == "sRGB" && d.Rec709Defaulted,
                $"Target={d.TargetTag} Rec709Defaulted={d.Rec709Defaulted} | {d.Describe()}");
            Assert($"{f}：这一收口是【格式口径】不是【屏幕证据】⇒ GamutClamped 必须为假（屏是给了的）",
                !d.GamutClamped && !d.HdrClamped, d.Describe());
            // 源侧一个字都不许动：像素此刻确实在 P3 里，只是要搬去 sRGB。
            // 这也是"声明与像素同源"那条承重不变式在格式收口下的正面形状。
            Assert($"{f}：源色域仍如实是 DisplayP3 ⇒ 真的搬（P3→sRGB），不是只把标签改成 sRGB",
                d.SourceTag == "DisplayP3" && d.NeedsMapping, d.Describe());
            Assert($"{f}：跟随显示器【不算】用户的手 ⇒ ExplicitWideTarget 为假",
                !d.ExplicitWideTarget && d.RequestedTag == "System");
        }

        // ── (3) 用户显式要 Rec.2020 ⇒ 格式默认档必须让路（"要 2020 请明说"就是这条） ──
        // 为什么必须有：这是新口径的另一半。只测"会收口"而不测"显式时不收口"，等于把功能改成
        // "JPEG/WebP 永远出不了 Rec.2020"—— 那是另一个（更坏的）行为，与本轮需求相反。
        var expJpeg = CDec.Build(wideP3, "BT2020", false, Acm, OutputFormat.JPEG_LI);
        Assert("★ 显式 BT.2020 + JPEG LI ⇒ 不收口（目标仍是 BT2020，Rec709Defaulted 为假）",
            expJpeg.TargetTag == "BT2020" && !expJpeg.Rec709Defaulted && expJpeg.ExplicitWideTarget,
            expJpeg.Describe());
        Assert("显式 BT.2020 + WebP ⇒ 同样让路（两个格式同一条规则，不是各写各的）",
            CDec.Build(wideP3, "BT2020", false, Acm, OutputFormat.WebP) is { TargetTag: "BT2020", Rec709Defaulted: false });
        Assert("显式 BT.2020 + 增益图 ⇒ 不收口（用户要整张增益图走 Rec.2020，与底图档位无关）",
            CDec.Build(wideP3, "BT2020", false, Acm, OutputFormat.JPEG_GAINMAP) is { TargetTag: "BT2020", Rec709Defaulted: false });
        // 旧配置里的广色域别名（DisplayP3/AdobeRGB）经 CollapseSelectableTag 收成 BT2020 ⇒ 也算"明说"。
        // 这条防的是"按原样字符串判显式"那种写法：用户存的是 P3，实现若只认字面 BT2020 就会把它当自动档收掉。
        Assert("★ 旧别名 DisplayP3 / AdobeRGB 也算显式（按收口后的值判，不按原样字符串判）",
            CDec.Build(wideP3, "DisplayP3", false, Acm, OutputFormat.JPEG_LI) is { TargetTag: "BT2020", ExplicitWideTarget: true, Rec709Defaulted: false }
            && CDec.Build(wideP3, "AdobeRGB", false, Acm, OutputFormat.JPEG_LI) is { TargetTag: "BT2020", ExplicitWideTarget: true, Rec709Defaulted: false });
        // 显式 sRGB 是"用户要的就是 709"，目标本来就是 sRGB ⇒ 没什么可收的，标记也不许亮。
        // （标记的语义是"我们替用户把广色域压回去了"，不是"目标是 sRGB"。）
        Assert("显式 sRGB + JPEG LI ⇒ 目标是 sRGB 但 Rec709Defaulted 为假（没东西被收口）",
            CDec.Build(wideP3, "sRGB", false, Acm, OutputFormat.JPEG_LI) is { TargetTag: "sRGB", Rec709Defaulted: false });

        // ── (4) 其余格式与非格式入口一律不受影响（收窄的范围必须精确，不能让整仓一起变） ──
        foreach (var f in new[] { OutputFormat.PNG, OutputFormat.JPEG_XL, OutputFormat.AVIF, OutputFormat.TIFF })
            Assert($"{f} 有完整色彩通道 ⇒ 跟随显示器照旧解析成 BT2020（新口径不许外溢到它们）",
                CDec.Build(wideP3, "System", false, Acm, f) is { TargetTag: "BT2020", Rec709Defaulted: false });
        Assert("★ format=null（4 参形状）⇒ 与上面那句 4 参断言逐字同值：不收口、标记为假",
            CDec.Build(wideP3, "System", false, Acm, (OutputFormat?)null) is { TargetTag: "BT2020", Rec709Defaulted: false }
            && wSys.TargetTag == "BT2020" && !wSys.Rec709Defaulted);
        // format 与 requestedTag 是【两个正交的输入】：格式白名单不该让"显式"这条一起失效。
        Assert("format 与 requestedTag 正交：白名单格式 + 显式广色域 ⇒ 两条判据各答各的",
            CDec.DefaultsToRec709(OutputFormat.JPEG_LI)
            && CDec.Build(wideP3, "BT2020", false, Acm, OutputFormat.JPEG_LI).ExplicitWideTarget);

        // ── (5) HDR 档：格式收口只动【目标色域】，不许把 HDR 开关一起收掉 ──
        // 为什么单独钉：收口的形状与"目标是 sRGB ⇒ 降级成 SDR"那条老逻辑长得像，极易被顺手写进去。
        // 但 HDR 许不许写 PQ 由【屏幕】决定（HdrClamped 那条路），格式只决定通道与色域。
        // 增益图尤其不能丢 HdrOutput —— 丢了它 Delivery 会在第①步就返回 BlockedByDisplay，
        // 用户开着增益图开关却拿到一张普通 JPEG（那正是本文件顶部注释记着的老缺陷）。
        var hdrGm = CDec.Build(hdr, "System", true, Acm, OutputFormat.JPEG_GAINMAP);
        Assert("★ 增益图 + HDR 屏 + 跟随显示器 ⇒ HdrOutput 仍为真，只是目标收成 sRGB（HDR 由增益图承载）",
            hdrGm.HdrOutput && !hdrGm.HdrClamped && hdrGm.TargetTag == "sRGB" && hdrGm.Rec709Defaulted,
            hdrGm.Describe());
        Assert("增益图在 HDR 屏上源仍如实是 P3（收目标不等于改源）",
            hdrGm.SourceTag == "DisplayP3" && hdrGm.NeedsMapping);
        var hdrJpeg = CDec.Build(hdr, "System", true, Acm, OutputFormat.JPEG_LI);
        Assert("JPEG LI + HDR 屏 + 跟随显示器 ⇒ HdrOutput 为真而目标 sRGB（两件事都记着自己的原因）",
            hdrJpeg.HdrOutput && hdrJpeg.TargetTag == "sRGB" && hdrJpeg.Rec709Defaulted && !hdrJpeg.HdrClamped,
            hdrJpeg.Describe());

        // ── (6) 两处收口不许串味：窄屏 + 显式 BT.2020 + JPEG LI ⇒ 两个标记各归各 ──
        // 这是本轮最容易写错的一格，也是钳制顺序（屏幕先、格式后）唯一的可见后果：
        //   · 屏幕钳制先把目标收成 sRGB 并置 GamutClamped（它看到的目标确实是 BT2020）；
        //   · 格式钳制随后看到 target 已是 sRGB ⇒ 条件 target != "sRGB" 不成立 ⇒ 不置 Rec709Defaulted。
        // 若哪天把两处顺序对调，格式钳制会先把目标清成 sRGB，屏幕钳制就再也不置位 ⇒ GamutClamped 静默丢失，
        // 而这枚标记有它自己的 UI 文案（"你这块屏不授权"）。这条断言就是那次对调的报警器。
        var narrowExplicit = CDec.Build(narrow, "BT2020", false, Acm, OutputFormat.JPEG_LI);
        Assert("★ 确证窄屏 + 显式 BT.2020 + JPEG LI ⇒ GamutClamped 真【且】Rec709Defaulted 假（屏先收，格式不再记一笔）",
            narrowExplicit.TargetTag == "sRGB" && narrowExplicit.GamutClamped && !narrowExplicit.Rec709Defaulted,
            narrowExplicit.Describe());
        Assert("同格不许因为「格式也默认 709」就把屏证据那条收口算成格式收的（日志里两种原因必须分开读）",
            narrowExplicit.Describe().Contains("广色域已收口")
            && !narrowExplicit.Describe().Contains("格式默认档收为Rec.709"),
            narrowExplicit.Describe());
        // 反过来：窄屏 + 跟随显示器（目标本来就是 sRGB）⇒ 没有任何东西被收口，两枚标记都为假。
        // 这条防的是"无条件置位"那种写法 —— 标记一旦变成"目标是 sRGB 就亮"，它就不再是一条证据。
        Assert("确证窄屏 + 跟随显示器 + JPEG LI ⇒ 目标 sRGB 但两枚收口标记都为假（默认 709 + 窄屏恰好同向）",
            CDec.Build(narrow, "System", false, Acm, OutputFormat.JPEG_LI) is
                { TargetTag: "sRGB", Rec709Defaulted: false, GamutClamped: false });
        // Unknown 屏（逐屏证据缺失）+ 显式 BT.2020 + JPEG LI ⇒ 格式默认档【不许】盖掉用户显式选择。
        // ⚠ 这一格守的是格式钳制的【边界】，不是它的触发 —— 两者必须都有断言，缺哪边都危险：
        //   · 「会收口」那一边由上面的 (2)/(3) 守着（wideP3 + 跟随显示器 ⇒ 收成 sRGB）；
        //   · 这一格守的是反面：用户【手动】拨到 Rec.2020 时，格式默认档一个字都不许动。
        // 为什么反面这么要紧（这是本轮的承重语义，不是补充说明）：需求原话是
        //   「JPEG LI 下默认色彩是 Rec.709，勾了 ultra HDR 后也是 Rec.709（选择跟随显示器），
        //     只有用户手动指定 Rec.2020 时候才是 Rec.2020」。
        //   ⇒ 钳制条件里的 `!explicitWideRequested` 就是"手动指定"这个守卫。若它被删掉或写错，
        //     用户明确要的 Rec.2020 会被静默降成 Rec.709 —— 那是与需求【完全相反】的行为，
        //     而且产物看着"更正常"，极难被发现。这条断言就是那个守卫的报警器。
        // 选 Unknown 屏不是因为它能让钳制触发（恰恰相反），而是因为它是唯一能把两处收口【各自单独关掉】的屏态：
        //   屏幕钳制在 classKnown=false 时整体跳过 ⇒ GamutClamped 必为假；
        //   于是这一格只剩"格式钳制会不会误伤显式选择"这一个变量，归因不含糊。
        Assert("★ Unknown 屏 + 显式 BT.2020 + JPEG LI ⇒ 格式默认档【不许】盖掉用户显式选择（默认档只管没选过的人）",
            CDec.Build(null, "BT2020", false, Acm, OutputFormat.JPEG_LI) is
                { TargetTag: "BT2020", Rec709Defaulted: false, GamutClamped: false });
        // 对照：同一个用户显式选择，换成不默认 709 的 PNG ⇒ 结果逐项相同。
        // 两者相同正是这条要证明的事：显式选择高于格式默认档，与"这个格式有没有默认档"无关。
        // ⚠ 这不是"重复断言"——它把"JPEG LI 那一格保持 BT2020"的原因钉在【显式】上：
        //   若哪天有人把守卫写错，PNG 那格仍会绿、而 JPEG LI 那格会红，两格一比就知道是格式档闯的祸。
        Assert("对照：同一显式选择换成 PNG ⇒ 结果逐项相同（显式选择高于格式默认档，与格式是否白名单无关）",
            CDec.Build(null, "BT2020", false, Acm, OutputFormat.PNG) is
                { TargetTag: "BT2020", Rec709Defaulted: false, GamutClamped: false }
            && CDec.Build(null, "BT2020", false, Acm, OutputFormat.JPEG_LI) is
                { TargetTag: "BT2020", Rec709Defaulted: false, GamutClamped: false });

        // ── (7) 审计行必须能读出"是谁收的" ──
        // Describe() 是现场日志与 --display-audit 的唯一出处；两种收口共用一种形状（目标=sRGB），
        // 全靠这两句尾巴区分。少了它，读日志的人只能看到"目标 sRGB"，分不出屏不给还是格式不给。
        Assert("★ 审计行把格式收口与屏幕收口分开写（两种原因共用『目标=sRGB』这一种形状，只能靠尾巴分辨）",
            hdrGm.Describe().Contains("[格式默认档收为Rec.709]")
            && !hdrGm.Describe().Contains("[广色域已收口]")
            && narrowExplicit.Describe().Contains("[广色域已收口]"));
        Assert("凡 Rec709Defaulted 为真 ⇒ 审计行必须带那句尾巴（标记与文案不许分叉）",
            CDec.Build(wideP3, "System", false, Acm, OutputFormat.WebP).Describe().Contains("[格式默认档收为Rec.709]"));

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
        // ═══ 上游 PR #1（VS-Cap 侧车）合入时必须带的两条牙 ═══
        // 侧车是"像素出进程、编辑完再回来"的入口：TTC1 头里只有 x/y，且 C++ 回写时恒填 0，
        // 所以源屏身份只能由宿主自己带 —— 让它退回"保存那一刻的光标屏"就是 §41.4 那个缺口的翻版。
        var vsCap = File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.VSCap.cs"))!);
        Assert("VS-Cap 导出沿用被截那块屏；只有剪贴板贴图路才退回光标屏",
            vsCap.Contains("ExportVSCapAsync(f, avif, dest, display)")
            && vsCap.Contains("ExportVSCapAsync(f, avif, dest, null)")
            && vsCap.Contains("DecisionForDisplay(display, ui, hdrScenario, format)")
            && !vsCap.Contains("DecisionForCursorScreen"));
        // PR 原文在构造函数里无条件 RegisterNamed("vscap-pin") ⇒ 功能默认关着也抢注一个系统级热键，
        // 与它自述的 "off by default; the original TTC workflow is preserved" 相反。
        Assert("VS-Cap 关着就不注册 Ctrl+Alt+T，且关掉时真的注销",
            System.Text.RegularExpressions.Regex.IsMatch(vsCap, @"bool on = .*_settings\.UseVSCap")
            && vsCap.Contains("HotkeyManager.UnregisterNamed(\"vscap-pin\")")
            && !mw.Contains("if (!HotkeyManager.RegisterNamed(this, \"vscap-pin\""));
        // 侧车走的是 csproj 一条 glob：构建机上没有 Qt ⇒ 0 个条目 ⇒ 构建打包全绿而功能根本不存在。
        // 那时还把开关摆着，等于给用户一个点了只会收到 FileNotFoundException 的按钮。
        Assert("侧车不在包里时整个入口收起（开关+按钮+热键），并留一条可读日志",
            vsCap.Contains("VSCapEditor.IsAvailable")
            && vsCap.Contains("VSCapModeChk.Visibility = available ? Visibility.Visible : Visibility.Collapsed")
            && File.ReadAllText(FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "VSCapEditor.cs"))!)
                   .Contains("public static bool IsAvailable"));
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

        // ═══ ③′ 2026-10-06：默认目标改成 Rec.709 之后，交付形态【一格都没变】 ═══
        // 这一小节存在的唯一理由是"证明那次产品口径变更没有波及交付形态"，而不是"再测一遍 Delivery"。
        // 上面所有的 Del(...) 走的都是 4 参 Build（无格式上下文），因此它们守的仍是老行为；
        // 而真实管线【一定会传格式】⇒ 上面那张表与线上跑的根本不是同一个形状。这里有责任把
        // "带上格式之后结论是否仍然一致"单独钉一遍，否则 140 格全绿也说明不了线上的事。
        //
        // 逐格推一遍（HDR 屏 + HDR 开关 + 跟随显示器）：
        //   · JPEG LI / WebP：新的格式收口把目标从 BT2020 变成 sRGB，HdrOutput 仍为真；
        //     Delivery 的第②步（SupportsHdr=false）在③之前命中 ⇒ BlockedByFormat。
        //     ⚠ 关键：它【不能】变成 BlockedBySrgbTarget —— 两者的用户动作完全不同
        //     （前者"换格式"，后者"改目标"），而对 JPEG/WebP 而言把目标改回 2020 也写不出 PQ。
        //     这条断言就是那个"顺序不许对调"的报警器。
        HdrDelivery Fmt(TrueToneCap.Core.Capture.DisplayInfo? s, string tag, OutputFormat f)
            => FormatColorFacts.Delivery(CDec.Build(s, tag, true, true, f), f);
        foreach (var f in new[] { OutputFormat.JPEG_LI, OutputFormat.WebP })
            Assert($"★ {f} + HDR 屏 + 跟随显示器（带格式的 5 参形状）⇒ BlockedByFormat，不是 BlockedBySrgbTarget",
                Fmt(hdrScreen, "System", f) == HdrDelivery.BlockedByFormat);
        // 增益图两档都必须是 GainMap：新收口只换底图原色，不改"交付物是什么"。
        // 若哪天有人把"目标=sRGB ⇒ 降级"顺手写进增益图这条路，这里立刻红。
        Assert("★ 增益图 + HDR 屏 + 跟随显示器（带格式）⇒ 仍是 GainMap：收口只换原色，不换交付形态",
            Fmt(hdrScreen, "System", OutputFormat.JPEG_GAINMAP) == HdrDelivery.GainMap);
        Assert("增益图 + HDR 屏 + 显式 BT.2020（带格式）⇒ 也是 GainMap（两档只差原色）",
            Fmt(hdrScreen, "BT2020", OutputFormat.JPEG_GAINMAP) == HdrDelivery.GainMap);
        // 对照组：不默认 709 的格式在【同一个 5 参形状】下不受影响 ⇒ 收口确实只针对白名单那一族。
        // 这条同时把上面三格的"变化"归因锁死：变的只有白名单格式，不是 Delivery 或屏判据变了。
        Assert("对照：PNG 在同一个 5 参形状下仍写 PQ（说明上面几格的变化来自格式口径，不是 Delivery 变了）",
            Fmt(hdrScreen, "System", OutputFormat.PNG) == HdrDelivery.PqContainer);
        // 4 参 vs 5 参必须能同时成立：同一个 PNG + System 两种形状同值 ⇒ "不传格式 = 不改行为"。
        Assert("同一个 PNG + 跟随显示器：4 参与 5 参形状同值 ⇒ 『不传格式 = 不改行为』这句话可执行地成立",
            Del(hdrScreen, "System", OutputFormat.PNG) == Fmt(hdrScreen, "System", OutputFormat.PNG)
            && Del(hdrScreen, "System", OutputFormat.PNG) == HdrDelivery.PqContainer);

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

        // ═══ 2026-10-06：声明文案必须自己交代「默认档是 Rec.709」这条规则 ═══
        // 为什么这不能只靠上面那条 4 参行为断言：行为收口发生在一个用户【看不见】的地方
        // （Build 里 target 被改成 sRGB），而用户看到的解释只有色域映射卡片上这句文案。
        // 卡片同时是"过去跟随显示器会解析出 Rec.2020、现在不会"这件事的唯一交代处 ——
        // 少了它，用户遇到的最坏情况是"产物色域悄悄和我以前拿到的不同，界面一个字没说"。
        // ⚠ 断言取【稳定子串】而不是整句：文案的措辞会随语言/润色改，但这两件事必须一直在。
        //   (a) `Rec.709` — 说的是哪一档；(b) `【显式】`/`显式` — 说清升级的开关在用户手里。
        //   按整句匹配的话，任何一次标点或语序调整都会红，而那并不代表规则丢了。
        foreach (var f in new[] { OutputFormat.JPEG_LI, OutputFormat.WebP })
        {
            var txt = Decl(f, "sRGB", false);
            Assert($"★ {f} 的声明文案说出「默认档就是 Rec.709」（用户看得见的唯一交代处）",
                txt.Contains("Rec.709"),
                txt);
            Assert($"★ {f} 的声明文案说清 Rec.2020 只在【显式】选择时才会写出（自动档不升级）",
                txt.Contains("默认目标") && txt.Contains("显式"),
                txt);
        }
        // 两族格式必须说成【同一件事】：JPEG 与 WebP 分句写（各自的"什么都不写"细节不同），
        // 但规则本身只有一条。这条防的是"改了一处的文案、另一处留着旧说法"。
        Assert("JPEG LI 与 WebP 的默认档说法是同一件事（两处分开写句子，但规则不许分叉）",
            new[] { OutputFormat.JPEG_LI, OutputFormat.WebP }
                .All(f => Decl(f, "sRGB", false).Contains("默认目标") && Decl(f, "sRGB", false).Contains("Rec.709")));
        // 反向：不默认 709 的格式不许长出这句话（否则用户会以为 PNG/AVIF 也被收口了）。
        Assert("反向：不默认 Rec.709 的格式不许出现「默认目标」这套说法（免得用户以为 PNG/AVIF 也被收口）",
            new[] { OutputFormat.PNG, OutputFormat.JPEG_XL, OutputFormat.AVIF, OutputFormat.TIFF }
                .All(f => !Decl(f, "sRGB", false).Contains("默认目标")));
        // ⚠ 这几条必须与全仓「旧叫法封杀」共存：文案只能说 `Rec.709`，不许写 `BT.709`/`BT.2020`
        //   （上面 7191 那条已在全域扫过，这里对新加的两句再点名一次，免得日后补文案时用错叫法）。
        Assert("新加的默认档文案用新叫法（Rec.709），不许带 BT.709 / BT.2020 这两个被全域封杀的旧写法",
            new[] { OutputFormat.JPEG_LI, OutputFormat.WebP }
                .All(f => !Decl(f, "sRGB", false).Contains("BT.709") && !Decl(f, "sRGB", false).Contains("BT.2020")));
        // 措辞纪律的直接后果：这句话不许写成 `sRGB 目标` —— ColorDecision 内部那枚标记是 sRGB，
        // 但面向用户的档位名只有 Rec.709。写成 sRGB 会撞上界面用词的旧叫法清单。
        Assert("声明文案说档位时用 Rec.709 而不是 sRGB（内部标记是 sRGB，那是盘上契约，不是用户用词）",
            new[] { OutputFormat.JPEG_LI, OutputFormat.WebP }
                .All(f => !Decl(f, "sRGB", false).Contains("sRGB 目标")
                       && !Decl(f, "sRGB", false).Contains("目标 sRGB")));

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
            // 第 4 个实参是本地化钩子：Core 的 Declaration 同时喂界面与日志，
            // 只有这一路该传 Tr，日志与 SelfTest 那张表刻意不传（保持中文）。
            // 摘掉它 = 界面退回中文，所以这里钉的是【带钩子那一整句】，不是旧的那三参形式。
            && cm.Contains("FormatColorFacts.Declaration(format, d.TargetTag, writesPq, LocaleManager.Tr)"),
            "Declaration 的调用要带 LocaleManager.Tr 这一路 —— 少它英文界面就整列中文");
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
    /// <b>v0.3.5-beta（release: v0.3.5-beta，我自己做的那个发布提交）里被删掉了</b> ⇒
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

        // ── 7) 反向孤儿：表里有、而界面上没有任何一处用它 ──
        // 上面几条管的都是正方向（"界面有中文 ⇒ 表里必须有英文"）。反方向过去没人管 ⇒
        // 删掉一处界面而不清表，表里就留下一条死键，而且【不会红】——这正是 §35.3 点名的闸门缺口，
        // 也是 B/C 档删除动作的前置读数（夹具④就是那个数）。
        // 使用面 = XAML 静态中文（上面 attrs）+ App 全部 .cs 剥注释后的源码文本 + 窗口标题。
        // 为什么用"包含"而不是再解析一遍字面量：再写一个解析器就是第二份真相（本仓反复踩过）。
        // 误差方向是刻意的——宁可漏报也不误报；而"新孤儿"只在引用它的文件被删掉时出现，
        // 那时包含匹配必然失败 ⇒ 该响的一定响。
        var csFiles = Directory.GetFiles(appDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("\\obj\\") && !f.Contains("\\bin\\")
                        && Path.GetFileName(f) != "LocaleManager.cs").ToList();
        // 使用面要能按需"摘掉某一份界面"再算——这样才敢用它报 B/C 档的翻译代价（夹具④）。
        string FaceOf(string[] skipBasenamePrefixes)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var f in csFiles)
            {
                var name = Path.GetFileName(f);
                if (skipBasenamePrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;
                sb.Append(StripComments(File.ReadAllText(f))).Append('\n');
            }
            return sb.ToString();
        }
        var csFace = FaceOf(Array.Empty<string>());
        // ⚠ 使用面必须自己补上 `<Window Title="…">`：上面 attrs 那一循环【刻意】不收窗口标题
        //   （窗口不在可视树里，正向判据只能放行、由构造器 `Title = Tr(Title)` 兜）。
        //   第一版直接拿 attrs 当使用面 ⇒ 误报两条真在用的键（`TrueToneCap 标注` / `TrueToneCap 文字识别`），
        //   这条自纠本身就是要留的证据：借用别人的扫描面，就要连它故意留的洞一起补。
        var winTitles = new List<string>();
        foreach (var f in xamlFiles)
            foreach (Match m in Regex.Matches(File.ReadAllText(f),
                         @"<Window\b[^>]*?\bTitle=""([^""]*)""", RegexOptions.Singleline))
            {
                var t = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
                if (HasCjk(t)) winTitles.Add(t);
            }
        // 守卫不写"至少 N 条"这种我猜的数：直接钉住"第一版栽的那两条必须在提取结果里"。
        // （实测本仓中文窗口标题共 3 条，写 >=5 会在今天就把这条判据自己判红。）
        Assert("窗口标题单独进了使用面（读不到那两条已知标题 = 提取式失效，会把真键误报成孤儿）",
            winTitles.Contains("TrueToneCap 标注", StringComparer.Ordinal)
            && winTitles.Contains("TrueToneCap 文字识别", StringComparer.Ordinal),
            $"读到 {winTitles.Count} 条：" + string.Join(" | ", winTitles.Select(Cut)));

        static List<string> OrphanKeys(IEnumerable<string> keys,
            IEnumerable<(string File, string Tag, string Prop, string Value)> xaml,
            IReadOnlyCollection<string> titles, string codeText) =>
            keys.Where(k => !xaml.Any(a => a.Value == k) && !titles.Contains(k, StringComparer.Ordinal)
                            && !codeText.Contains(k, StringComparison.Ordinal))
                .OrderBy(s => s, StringComparer.Ordinal).ToList();
        List<string> OrphansOf(IEnumerable<string> keys) => OrphanKeys(keys, attrs, winTitles, csFace);

        // fail-closed 守卫：使用面读空了，"零孤儿"就是假绿（这把尺子自己先要能看见东西）
        Assert("反向判据的使用面不是空的（XAML 中文与 .cs 源码文本都要读到）",
            attrs.Count > 150 && csFace.Length > 100_000,
            $"attrs={attrs.Count} 处 / .cs 面 {csFace.Length} 字符");
        // 夹具①：一条谁都不用的键必须被抓出来（不然"零孤儿"可能只是判据失明）
        Assert("反向判据有牙·夹具①：凭空造一条键必须被判孤儿",
            OrphansOf(new[] { "绝不可能出现在界面上的键文甲" }).Count == 1,
            "夹具没被抓出来 ⇒ 这条锁是恒绿的装饰");
        // 夹具②：真在 XAML 里出现的中文一条都不许报成孤儿（误报会淹没真信号）
        Assert("反向判据不误报·夹具②：XAML 实际用到的中文，孤儿数必须为 0",
            OrphansOf(distinct).Count == 0,
            $"误报 {OrphansOf(distinct).Count} 条：" + string.Join(" | ", OrphansOf(distinct).Select(Cut).Take(6)));
        // 夹具③：这两个正是第一版误报出来的键（只做窗口标题用）——必须被认成"在用"。
        Assert("反向判据不误报·夹具③：只做窗口标题的中文键不许被判孤儿（第一版就是栽在这里）",
            OrphansOf(new[] { "TrueToneCap 标注", "TrueToneCap 文字识别" }).Count == 0,
            "窗口标题又从使用面里漏掉了（若是这两扇窗被删了，本夹具要与基线一起撤，不许只放开这一条）");

        // 基线 0 = 2026-10-01 由这条判据自己报出的实得数（第一版报 2，那两条是窗口标题、属误报，
        // 已把标题补进使用面）。留 0 的意义：以后删一处界面忘了清表，这里就红并点名。
        const int OrphanBaseline = 0;
        var orphanKeys = OrphansOf(table);
        // 夹具④ = 这一节真正要为 B/C 档报的数：把覆盖层那两份文件（.xaml 与 .xaml.cs）整个从使用面里摘掉，
        // 表里立刻死掉几条？这就是"删界面却忘了清表"的翻译代价，实测数直接打进日志。
        var goneXaml = attrs.Where(a => !a.File.StartsWith("SelectionOverlay", StringComparison.Ordinal)).ToList();
        var goneCs = FaceOf(new[] { "SelectionOverlay" });
        var ifOverlayGone = OrphanKeys(table, goneXaml, winTitles, goneCs);
        Console.WriteLine($"  ⓘ 反向孤儿·夹具④：摘掉 SelectionOverlay 那两份文件 ⇒ 表里立刻死 {ifOverlayGone.Count} 条："
                          + string.Join(" | ", ifOverlayGone.Select(Cut).Take(15)));
        Assert("反向判据能报出删除代价（摘掉覆盖层 ⇒ 表里必须立刻出现死键；当前树自己是零死键）",
            ifOverlayGone.Count >= 1 && orphanKeys.Count == OrphanBaseline,
            $"摘掉后死 {ifOverlayGone.Count} 条 / 当前树死 {orphanKeys.Count} 条");
        Assert($"表里的死键数量 == 基线 {OrphanBaseline}（实得 {orphanKeys.Count}）",
            orphanKeys.Count == OrphanBaseline,
            $"基线 {OrphanBaseline} vs 实得 {orphanKeys.Count}：" +
            string.Join(" | ", orphanKeys.Select(Cut).Take(8)));
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
