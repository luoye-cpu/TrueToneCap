// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/UsabilityTests.cs
// 综合可用性测试 — 完整覆盖所有核心组件 + 组合测试
// 运行: dotnet run --project src/TrueToneCap.Test -- --usability-tests

using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using TrueToneCap.Core;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Detection;

namespace TrueToneCap.Test;

/// <summary>综合可用性测试：验证所有内置组件在全部可用组合下能正常工作。</summary>
public static class UsabilityTests
{
    private static int _passed, _failed;
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_UsabilityTest");
    private static readonly Random s_rng = new(42);
    private static int _warnings;
    /// <summary>因环境不满足而【显式跳过】的用例数 —— 既不算通过也不算失败。</summary>
    private static int _skipped;

    /// <summary>显示器数量。-1 = 枚举失败（当作无显示器）。headless 环境下为 0。</summary>
    private static int s_displayCount = -2;

    /// <summary>是否有可用显示器。用来决定显示器相关用例是"跑"还是"显式跳过"。
    /// <para>
    /// 2026-09-14：本套件此前被整体标记为"需要桌面会话"，因而【不在发布门禁内】，
    /// 门禁覆盖率只有 199/373。实际上 18 个小节里只有 DisplayInfo / RegionDetector
    /// 两处真正依赖显示器，其余（PixelOps、ToneMapper、色彩、标注、编码器、并发）
    /// 全是纯 CPU 逻辑。把这两处改为"无显示器则显式跳过"后，本套件即可进 headless 门禁。
    /// </para></summary>
    private static bool HasDisplays()
    {
        if (s_displayCount == -2)
        {
            try { s_displayCount = DisplayEnumerator.EnumerateDisplays().Count; }
            catch { s_displayCount = -1; }
        }
        return s_displayCount > 0;
    }

    /// <summary>显式跳过一条用例：不计入通过/失败，但必须留下原因（禁止静默通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    public static int RunAll()
    {
        _passed = _failed = _warnings = _skipped = 0;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 综合可用性测试");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("═══════════════════════════════════════════════════════════════\n");

        var sw = Stopwatch.StartNew();

        // ─── 1. PixelOps 完整测试 ───
        Console.WriteLine("── 1. PixelOps ──");
        PixelOps_FixAlphaChannel();
        PixelOps_FixAlphaChannel_AlreadySet();
        PixelOps_FixAlphaChannel_OddSizes();
        PixelOps_BgraToScrgbLinear_KnownValue();
        PixelOps_BgraToScrgbLinear_AllWhite();
        PixelOps_DownsampleToGray_Identity();
        PixelOps_DownsampleToGray_NonDivisible();
        PixelOps_ComputeEdgeProjections_Empty();
        PixelOps_BestVectorWidth();
        PixelOps_DetectISA();

        // ─── 2. ToneMapper 影像质量测试 ───
        Console.WriteLine("\n── 2. ToneMapper ──");
        ToneMapper_AllModes_Quality();
        ToneMapper_Exposure_Parameter();
        ToneMapper_ZeroSized_NoCrash();
        ToneMapper_ExtremeValues_NoCrash();
        ToneMapper_AllModes_Deterministic();

        // ─── 3. ColorProfileProvider 完整测试 ───
        Console.WriteLine("\n── 3. ColorProfileProvider ──");
        ColorProfile_AllStandardIcc_Valid();
        ColorProfile_MapColorSpace_All();
        ColorProfile_GetStandardIcc_Consistent();
        ColorProfile_DisplayIcc_NullSafe();

        // ─── 4. ColorSpaceConverter / ColorProfileProvider 测试 ───
        Console.WriteLine("\n── 4. ColorSpaceConverter / ColorProfileProvider ──");
        ColorSpaceConverter_HdrToSRgb_AllModes();
        ColorSpaceConverter_MapToSRgb_WithIcc();
        ColorSpaceConverter_MapToSRgb_BoundaryPixels();
        ColorProfileProvider_BakeIccToTarget_AllSpaces();

        // ─── 5. AnnotationManager 测试 ───
        Console.WriteLine("\n── 5. AnnotationManager ──");
        Annotation_AllLayerTypes();
        Annotation_UndoRedo_Multiple();
        Annotation_UndoRedo_Boundary();
        Annotation_ClearAll();

        // ─── 6. Encoding 编码器可用性测试 ───
        Console.WriteLine("\n── 6. 编码器可用性 ──");
        Encoder_AllFormats_Create();
        Encoder_AllFormats_QualityRange();
        Encoder_AllFormats_QualityDescription();

        // ─── 7. SDR 编码组合测试 ───
        Console.WriteLine("\n── 7. SDR 编码组合 ──");
        var testPattern = GenerateTestPattern(1920, 1080);
        var smallPattern = GenerateTestPattern(127, 63); // 非对齐尺寸

        // 7a. 每种格式 + 基础编码测试
        SdrEncode_AllFormats(testPattern, 1920, 1080);
        SdrEncode_AllFormats(smallPattern, 127, 63);

        // 7b. 色度采样组合测试 (JPEG)
        SdrEncode_ChromaCombinations(testPattern, 1920, 1080);

        // 7c. 位深组合测试 (PNG/AVIF/JXL)
        SdrEncode_BitDepthCombinations(testPattern, 1920, 1080);

        // 7d. ICC 嵌入测试
        SdrEncode_IccEmbedding(testPattern, 1920, 1080);

        // 7e. CICP 嵌入测试 (PNG)
        SdrEncode_CicpEmbedding(testPattern, 1920, 1080);

        // 7f. 极简像素测试 (1x1, 2x2, 3x3)
        SdrEncode_MinimalPixels();

        // ─── 8. HDR 编码测试 ───
        Console.WriteLine("\n── 8. HDR 编码 ──");
        var hdrFrame = CreateHdrFrame(testPattern, 1920, 1080);
        HdrEncode_AllSupported(hdrFrame);
        HdrEncode_Pq16_Precision();

        // ─── 9. ManagedJpegEncoder 专项测试 ───
        Console.WriteLine("\n── 9. JPEG 编码器专项 ──");
        Jpeg_AllQualityLevels();
        Jpeg_Chroma_420_422_444();
        Jpeg_IccLargeProfile();
        Jpeg_EncodeToBytes_ValidJpeg();
        Jpeg_Streaming_Valid();

        // ─── 10. ManagedPngEncoder 专项测试 ───
        Console.WriteLine("\n── 10. PNG 编码器专项 ──");
        Png_AllBitDepths();
        Png_16bit_Roundtrip();
        // 2026-09-19：Png_IccAndCicp_MutualExclusion → Png_IccAndCicp_Coexist（源码从不互斥，见该方法注释）
        Png_IccAndCicp_Coexist();
        Png_StreamOutput();

        // ─── 11. ManagedBmpEncoder 专项测试 ───
        Console.WriteLine("\n── 11. BMP 编码器专项 ──");
        Bmp_AllSizes();
        Bmp_StreamOutput();

        // ─── 12. JpegGainMapEncoder 专项测试 ───
        Console.WriteLine("\n── 12. JPEG Gain Map ──");
        GainMap_GrayMode();
        GainMap_RgbMode();
        GainMap_QualitySettings();

        // ─── 13. FormatHelper 辅助测试 ───
        Console.WriteLine("\n── 13. FormatHelper ──");
        FormatHelper_HdrToPq16_Valid();
        FormatHelper_Rgba16ToBgra16_Valid();
        FormatHelper_GetColorMetadata_AllTags();
        FormatHelper_ToSdr_AllModes();

        // ─── 14. 边界和压力测试 ───
        Console.WriteLine("\n── 14. 边界条件 ──");
        Boundary_ZeroWidth_NoCrash();
        Boundary_EmptyPixelData();
        Boundary_ExtremeQuality();

        // ─── 15. DisplayInfo 检测 ───
        Console.WriteLine("\n── 15. DisplayInfo ──");
        DisplayInfo_Enumerate_NoCrash();
        DisplayInfo_CursorMonitor_NoCrash();

        // ─── 16. RegionDetector 检测 ───
        Console.WriteLine("\n── 16. RegionDetector ──");
        RegionDetector_NoCrash();

        // ─── 17. 综合管线测试 ───
        Console.WriteLine("\n── 17. 综合管线 ──");
        Pipeline_ColorSpace_ToSdr();
        Pipeline_ColorSpace_NoIcc_SrgbTarget();
        Pipeline_ColorSpace_WithIcc_NonSrgbTarget();

        // ─── 18. 编码器线程安全测试 ───
        Console.WriteLine("\n── 18. 并发安全 ──");
        Concurrent_EncoderFactory();

        // ─── 汇总 ───
        sw.Stop();
        Console.WriteLine($"\n═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  综合可用性测试完成");
        Console.WriteLine($"  耗时: {sw.ElapsedMilliseconds}ms");
        TestSummary.Report("--usability-tests", _passed, _failed, _warnings, sw.ElapsedMilliseconds, _skipped);
        Console.WriteLine($"═══════════════════════════════════════════════════════════════\n");

        // 输出测试输出目录
        var files = Directory.GetFiles(OutDir, "*.*");
        if (files.Length > 0)
        {
            Console.WriteLine($"  编码输出文件 ({files.Length} 个):");
            foreach (var f in files.OrderBy(f => f))
            {
                var fi = new FileInfo(f);
                Console.WriteLine($"    {fi.Name,-40} {fi.Length,8} bytes");
            }
        }

        Console.WriteLine();
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  1. PixelOps
    // ═══════════════════════════════════════════════════════════════

    static void PixelOps_FixAlphaChannel()
    {
        var pixels = new byte[] { 10, 20, 30, 0, 40, 50, 60, 0, 70, 80, 90, 128 };
        PixelOps.FixAlphaChannel(pixels);
        // 当前实现语义: 无条件将所有 alpha 字节强制为 0xFF
        bool ok = pixels[3] == 0xFF && pixels[7] == 0xFF && pixels[11] == 0xFF;
        Assert("FixAlphaChannel: 全 alpha 强制→255", ok);
    }

    static void PixelOps_FixAlphaChannel_AlreadySet()
    {
        var pixels = new byte[] { 1, 2, 3, 255, 4, 5, 6, 200 };
        PixelOps.FixAlphaChannel(pixels);
        // 所有 alpha 均被强制为 0xFF
        bool ok = pixels[3] == 255 && pixels[7] == 255;
        Assert("FixAlphaChannel: alpha 统一设为 255", ok);
    }

    static void PixelOps_FixAlphaChannel_OddSizes()
    {
        // 1 pixel
        var p1 = new byte[] { 0, 0, 0, 0 };
        PixelOps.FixAlphaChannel(p1);
        Assert("FixAlphaChannel: 1px 全零→255", p1[3] == 0xFF);

        // 3 pixels (非SIMD对齐)
        var p3 = new byte[12];
        PixelOps.FixAlphaChannel(p3);
        Assert("FixAlphaChannel: 3px 全零→255", p3[3] == 0xFF && p3[7] == 0xFF && p3[11] == 0xFF);
    }

    static void PixelOps_BgraToScrgbLinear_KnownValue()
    {
        // ═══ 2026-09-19：原名 ..._Roundtrip，但 Core 侧没有 scRGB 线性→BGRA 的反向公开 API
        // （唯一的 LinearToBgra8 是 JpegGainMapEncoder 的 private 方法），本用例实际只做单向已知值比对
        // ⇒ 按名实相符改名，不为了配上一个"Roundtrip"名字去新造反向实现。
        // 已知值: sRGB(128,128,128) → linear ≈ 0.2158
        var bgra = new byte[] { 128, 128, 128, 255 };
        var linear = PixelOps.BgraToScrgbLinearFast(bgra, 1, 1);
        bool ok = Math.Abs(linear[0] - 0.2158f) < 0.01f
               && Math.Abs(linear[1] - 0.2158f) < 0.01f
               && Math.Abs(linear[2] - 0.2158f) < 0.01f
               && Math.Abs(linear[3] - 1f) < 0.01f;
        Assert($"BgraToScrgbLinear: 128→linear≈0.216 (got {linear[0]:F4})", ok);
    }

    static void PixelOps_BgraToScrgbLinear_AllWhite()
    {
        var bgra = new byte[] { 255, 255, 255, 255 };
        var linear = PixelOps.BgraToScrgbLinearFast(bgra, 1, 1);
        bool ok = linear[0] > 0.9f && linear[1] > 0.9f && linear[2] > 0.9f;
        Assert("BgraToScrgbLinear: 纯白→linear≈1.0", ok);
    }

    static void PixelOps_DownsampleToGray_Identity()
    {
        // 1:1 降采样应输出与输入相同尺寸
        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = bgra[i + 1] = bgra[i + 2] = 128; bgra[i + 3] = 255; }
        var gray = PixelOps.DownsampleToGraySimd(bgra, 16, 16, 16, 16);
        bool ok = gray.Length == 16 * 16;
        Assert("DownsampleToGray: 1:1 降采样尺寸不变", ok);

        // 所有值应为 128 (128*0.299 + 128*0.587 + 128*0.114 = 128)
        bool allCorrect = gray.All(v => v == 128);
        Assert("DownsampleToGray: 1:1 中性灰准确", allCorrect);
    }

    static void PixelOps_DownsampleToGray_NonDivisible()
    {
        // 非整除尺寸不应崩溃
        var bgra = new byte[100 * 100 * 4];
        var gray = PixelOps.DownsampleToGraySimd(bgra, 100, 100, 17, 13);
        Assert("DownsampleToGray: 非整除尺寸不崩溃", gray.Length == 17 * 13);
    }

    static void PixelOps_ComputeEdgeProjections_Empty()
    {
        // 纯色图像 → 零梯度
        var gray = new byte[64 * 48];
        Array.Fill(gray, (byte)128);
        var hEdges = new float[48];
        var vEdges = new float[64];
        PixelOps.ComputeEdgeProjectionsSimd(gray, 64, 48, hEdges, vEdges);
        bool ok = hEdges.All(v => Math.Abs(v) < 1f) && vEdges.All(v => Math.Abs(v) < 1f);
        Assert("ComputeEdgeProjections: 纯色→零梯度", ok);
    }

    static void PixelOps_BestVectorWidth()
    {
        // ═══ 2026-09-19：原来只断 bw > 0 ⇒ 检测器返回 8/16/32/64 里任何一个都算绿。
        // 宽度必须由检测器自己报出的能力位推导出来（档位与位不一致 = 选择逻辑错位），
        // 并且不得跌回标量档 —— x64/Arm64 都有 128-bit 向量基线。
        int bw = PixelOps.BestVectorByteWidth;
        int expected = PixelOps.HasAvx10_512 ? 64
                     : PixelOps.HasAvx512VL ? 32
                     : PixelOps.HasAvx2 ? 32
                     : PixelOps.HasVector128 ? 16
                     : 4;
        Assert($"BestVectorByteWidth: {bw} == 由能力位推导的 {expected}", bw == expected,
            $"AVX10.512={PixelOps.HasAvx10_512} AVX512VL={PixelOps.HasAvx512VL} AVX2={PixelOps.HasAvx2} V128={PixelOps.HasVector128}");

        var archForWidth = RuntimeInformation.ProcessArchitecture;
        if (archForWidth is Architecture.X64 or Architecture.Arm64)
            Assert($"BestVectorByteWidth({archForWidth}): 架构基线 ≥16", bw >= 16, $"实际 {bw}");
        else
            Skip($"BestVectorByteWidth({archForWidth})", "非 x64/Arm64，推不出 128-bit 向量基线");
    }

    static void PixelOps_DetectISA()
    {
        // ═══ 2026-09-19：原来写成 hasAny || hasVector，而 hasVector 就是 Vector128.IsHardwareAccelerated
        // —— 受支持平台上恒真 ⇒ 整条断言结构上不可能为假，一个全 false 的坏检测器照样绿。
        // 现在按架构推导："必然为真的那一位必须为真"+"跨架构的位必须互斥"。
        var arch = RuntimeInformation.ProcessArchitecture;
        if (arch is Architecture.X64 or Architecture.X86)
        {
            // SSE2 是 x86/x64 上 .NET 的运行基线 ⇒ IsX86 与 Vector128 加速都必须报真
            Assert($"ISA 检测({arch}): x86 家族基线位为真 (IsX86={PixelOps.IsX86}, Vector128={PixelOps.HasVector128})",
                   PixelOps.IsX86 && PixelOps.HasVector128);
            Assert($"ISA 检测({arch}): 互斥——不应报出 ARM 位 (IsArm64={PixelOps.IsArm64}, NEON={PixelOps.HasNeon})",
                   !PixelOps.IsArm64 && !PixelOps.HasNeon);
        }
        else if (arch is Architecture.Arm64 or Architecture.Arm)
        {
            // NEON/AdvSimd 是 ARM64 的运行基线
            Assert($"ISA 检测({arch}): ARM 家族基线位为真 (IsArm64={PixelOps.IsArm64}, NEON={PixelOps.HasNeon}, Vector128={PixelOps.HasVector128})",
                   PixelOps.IsArm64 && PixelOps.HasNeon && PixelOps.HasVector128);
            Assert($"ISA 检测({arch}): 互斥——不应报出 x86 位 (IsX86={PixelOps.IsX86}, AVX2={PixelOps.HasAvx2})",
                   !PixelOps.IsX86 && !PixelOps.HasAvx2);
        }
        else
        {
            Skip($"ISA 检测({arch})", "未知架构，推不出必然为真的基线位");
        }

        // 层级自洽：高层为真 ⇒ 低层必为真（检测器各档互相矛盾时这里变红）
        Assert($"ISA 检测: 层级自洽 (AVX512Full={PixelOps.HasAvx512Full} ⇒ AVX2={PixelOps.HasAvx2})",
               !PixelOps.HasAvx512Full || PixelOps.HasAvx2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  2. ToneMapper
    // ═══════════════════════════════════════════════════════════════

    static void ToneMapper_AllModes_Quality()
    {
        // 验证所有模式输出图像质量: 非全黑、非全白、有梯度差异
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        var hdr = new float[64 * 4];
        for (int i = 0; i < hdr.Length; i += 4)
        {
            float t = i / (float)hdr.Length;
            hdr[i] = t * 10f;     // R: 0→10
            hdr[i + 1] = (1 - t) * 5f; // G: 5→0
            hdr[i + 2] = 2f;      // B: 恒定
            hdr[i + 3] = 1f;
        }

        foreach (var mode in modes)
        {
            var p = new ToneMappingParams { Mode = mode };
            var bytes = ToneMapper.FloatToSRgbBytes(hdr, 8, 2, p);

            bool hasRange = false;
            int minVal = 255, maxVal = 0;
            for (int i = 0; i < bytes.Length; i += 4)
            {
                int lum = (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3;
                minVal = Math.Min(minVal, lum);
                maxVal = Math.Max(maxVal, lum);
            }
            hasRange = maxVal - minVal > 20;

            // ⚠ 原来是 `bytes.All(b => b >= 0)` 配着"所有字节在[0,255]"的文案 —— 对 `byte` 而言
            // 上下界都由类型保证，这是**编译级恒真**，等于什么都没判。换成真正可能失败的那一半：
            // 输出不许退化成一整片同值（全 0 / 全 255 = 该映射模式把画面压平），
            // 这才是"动态范围"这个名字本来要说的话。
            bool allFlat = bytes.All(x => x == bytes[0]);
            Assert($"ToneMapper.{mode}: 动态范围={minVal}-{maxVal}, 未退化成一整片同值={allFlat == false}",
                hasRange && !allFlat, allFlat ? "整幅输出同一个字节值 ⇒ 该映射模式把画面压平" : "");
        }
    }

    static void ToneMapper_Exposure_Parameter()
    {
        // ═══ 2026-09-19 收紧后重做夹具 ═══
        // 原式 `b2[2] >= b1[2] || b2[2] >= b1[2] - 5` 的第二个析取式吸收了第一个 ⇒ 参数完全无效也绿。
        // 换成严格式之后它【立刻红了】，但红的原因不是产品坏了：夹具喂的是 0.5 线性中性灰，
        // -2EV 就已经 252、+2EV 被钳到 255 —— 两点都落在曲线肩部，饱和处当然量不出差异。
        // ⇒ 判据要按 Exposure 的真实语义写，而不是挑一个能过的输入值：
        //   ① 任何源电平上，输出随 EV **单调不降**（这条在饱和段也必须成立，是最硬的形状约束）；
        //   ② 在曲线未饱和的区段（线性 ≤0.1，留出头寸），-2EV→+2EV 必须**可测量地变亮**（>16）；
        //   ③ 全幅输出不得逐字节相同（否则"参数根本没被读"）。
        // ②用"未饱和区段"这个条件替代"挑中的某个输入"，换机器/改曲线参数都不会退化成恒真。
        float[] levels = [0.02f, 0.05f, 0.10f, 0.50f];
        float[] evs = [-2f, -1f, 0f, 1f, 2f];
        var hdr = new float[] { 0.5f, 0.5f, 0.5f, 1f, 0.5f, 0.5f, 0.5f, 1f };

        foreach (float level in levels)
        {
            for (int i = 0; i < hdr.Length; i += 4)
            {
                hdr[i] = hdr[i + 1] = hdr[i + 2] = level;
                hdr[i + 3] = 1f;
            }
            var lum = new List<int>();
            var outs = new List<byte[]>();
            foreach (float ev in evs)
            {
                var p = new ToneMappingParams { Mode = ToneMapMode.Hable, Exposure = ev };
                var b = ToneMapper.FloatToSRgbBytes(hdr, 2, 1, p);
                lum.Add((b[0] + b[1] + b[2]) / 3);     // BGRA 的 B,G,R 三通道（中性灰时同源同值）
                outs.Add(b);
            }
            bool monotone = true;
            for (int k = 1; k < lum.Count; k++)
                if (lum[k] < lum[k - 1]) monotone = false;
            bool differsFromDark = !outs[^1].AsSpan().SequenceEqual(outs[0]);
            int span = lum[^1] - lum[0];
            bool measurable = level <= 0.1f ? span > 16 : true;   // 饱和段只要求形状正确，不要求还能拉开

            bool ok = monotone && differsFromDark && measurable;
            Assert($"ToneMapper.Exposure: 源={level:F2} 输出随 EV 单调不降且参数生效" +
                   $" (EV {evs[0]:+0;-0}→{evs[^1]:+0;-0} 亮度 {string.Join("→", lum)}，跨度 {span})",
                   ok,
                   !monotone ? "非单调：加大曝光反而变暗" :
                   !differsFromDark ? "±2EV 输出逐字节相同 ⇒ Exposure 没被读" :
                   !measurable ? $"未饱和段跨度仅 {span}（要求 >16）" : "");
        }
    }

    static void ToneMapper_ZeroSized_NoCrash()
    {
        // 0x0 不崩溃
        try
        {
            var p = new ToneMappingParams { Mode = ToneMapMode.Hable };
            var bytes = ToneMapper.FloatToSRgbBytes([], 0, 0, p);
            Assert("ToneMapper: 0x0 不崩溃", bytes.Length == 0);
        }
        catch { Assert("ToneMapper: 0x0 不崩溃", false, "抛出异常"); }
    }

    static void ToneMapper_ExtremeValues_NoCrash()
    {
        var hdr = new float[] { float.MaxValue, float.MaxValue, float.MaxValue, 1f,
                                 float.MinValue, float.MinValue, float.MinValue, 1f,
                                 float.NaN, float.NaN, float.NaN, 1f,
                                 -1f, -1f, -1f, 1f };
        try
        {
            var p = new ToneMappingParams { Mode = ToneMapMode.Hable };
            var bytes = ToneMapper.FloatToSRgbBytes(hdr, 2, 2, p);
            // 至少不崩溃，输出有效
            Assert("ToneMapper: 极端值不崩溃", bytes.Length == 16);
        }
        catch
        {
            // NaN 可能引发异常，这是可接受的
            _warnings++;
            Console.WriteLine($"  ⚠ ToneMapper: 极端值问题 (NaN)，可接受");
        }
    }

    static void ToneMapper_AllModes_Deterministic()
    {
        var hdr = new float[] { 0.1f, 0.2f, 0.3f, 1f, 0.4f, 0.5f, 0.6f, 1f };
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        foreach (var mode in modes)
        {
            var p = new ToneMappingParams { Mode = mode };
            var b1 = ToneMapper.FloatToSRgbBytes(hdr, 2, 1, p);
            var b2 = ToneMapper.FloatToSRgbBytes(hdr, 2, 1, p);
            Assert($"ToneMapper.{mode}: 确定性输出", b1.SequenceEqual(b2));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  3. ColorProfileProvider
    // ═══════════════════════════════════════════════════════════════

    static void ColorProfile_AllStandardIcc_Valid()
    {
        var spaces = new[] { "sRGB", "DisplayP3", "DCI_P3", "AdobeRGB", "BT2020" };
        foreach (var space in spaces)
        {
            var cs = ColorProfileProvider.MapColorSpaceTag(space);
            var icc = ColorProfileProvider.GetStandardIccProfile(cs);
            if (icc is { Length: > 128 } customIcc)
            {
                bool valid = customIcc[36] == (byte)'a' && customIcc[37] == (byte)'c' && customIcc[38] == (byte)'s' && customIcc[39] == (byte)'p';
                Assert($"ColorProfile: {space} ICC有效 ({customIcc.Length}B)", valid);
            }
            else
            {
                Assert($"ColorProfile: {space} ICC有效", false);
            }
        }
    }

    static void ColorProfile_MapColorSpace_All()
    {
        // ═══ 2026-09-19：原来只判 !string.IsNullOrEmpty(cs) ⇒ MapColorSpaceTag 把任何标签
        // 都映射成同一个字符串也照样绿。现按 ColorProfileProvider.MapColorSpaceTag 的源码逐 tag 比对
        // （同仓范式：CorePipelineTests 的 Test_ColorProfile_MapColorSpaceTag 就是 == "sRGB"）。
        var expected = new (string Tag, string Want)[]
        {
            ("System",   "sRGB"),       // 非 ACM 下 System 落到 sRGB
            ("sRGB",      "sRGB"),
            ("DisplayP3", "DisplayP3"),
            ("DCI_P3",    "DisplayP3"),  // DCI-P3 与 Display-P3 同原色，刻意并入
            ("AdobeRGB",  "AdobeRGB"),
            ("BT2020",    "BT2020"),
        };
        foreach (var (tag, want) in expected)
        {
            var cs = ColorProfileProvider.MapColorSpaceTag(tag);
            Assert($"MapColorSpaceTag: {tag} → {want}", cs == want, $"实际 → {cs}");
        }
        // 未知标签必须落到 sRGB（default 分支），否则"新增色域被静默吞掉"无从发现
        Assert("MapColorSpaceTag: 未知标签 → sRGB(default)", ColorProfileProvider.MapColorSpaceTag("Nonsense") == "sRGB",
            $"实际 → {ColorProfileProvider.MapColorSpaceTag("Nonsense")}");
    }

    static void ColorProfile_GetStandardIcc_Consistent()
    {
        var cs1 = ColorProfileProvider.MapColorSpaceTag("sRGB");
        var icc1 = ColorProfileProvider.GetStandardIccProfile(cs1);
        var icc2 = ColorProfileProvider.GetStandardIccProfile(cs1);
        Assert("ColorProfile: sRGB ICC 缓存一致", ReferenceEquals(icc1, icc2));
    }

    static void ColorProfile_DisplayIcc_NullSafe()
    {
        // 空句柄不崩溃
        try
        {
            var icc = ColorProfileProvider.GetDisplayIccProfile(0);
            // 契约: 空句柄下要么返回 null（无显示器），要么返回完整 ICC profile。
            // 不应返回「非空但长度可疑」的残缺数据。
            if (icc is null)
                Skip("ColorProfile: GetDisplayIccProfile(0)", "无显示器（环境限制，返回 null 属预期）");
            else
                Assert($"ColorProfile: GetDisplayIccProfile(0) 返回完整 ICC ({icc.Length}B)",
                       icc.Length > 128);
        }
        catch { Assert("ColorProfile: GetDisplayIccProfile(0) 不崩溃", false, "抛出异常"); }
    }

    // ═══════════════════════════════════════════════════════════════
    //  4. GamutMapper
    // ═══════════════════════════════════════════════════════════════

    static void ColorSpaceConverter_HdrToSRgb_AllModes()
    {
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        var hdr = new float[] { 0.5f, 0.3f, 0.8f, 1f, 1.5f, 2.0f, 0.5f, 1f };
        foreach (var mode in modes)
        {
            var p = new ToneMappingParams { Mode = mode };
            var converted = ColorSpaceConverter.ConvertScrgbToTarget(hdr, 2, 1, ColorSpaceConverter.GetMatrix("BT2020"));
            var bytes = TrueToneCap.Core.Processing.ToneMapper.FloatToSRgbBytes(converted, 2, 1, p);
            bool ok = bytes.Length == 8 && bytes.All(b => b >= 0);
            Assert($"ColorSpaceConverter: {mode} 输出有效", ok);
        }
    }

    static void ColorSpaceConverter_MapToSRgb_WithIcc()
    {
        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 64; bgra[i + 2] = 192; bgra[i + 3] = 255; }
        // 使用 sRGB ICC 做映射
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        var (pixels, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, 16, 16, srgbIcc, "DisplayP3");
        if (pixels is null)
        {
            // ═══ 2026-09-19：原来记 Skip("无 D3D 设备，返回 null 属预期路径") —— 但
            // ColorProfileProvider.BakeIccToTarget 通体是 CPU 矩阵运算，函数体内 0 处 GPU 调用，
            // 返回 null 的唯一路径是连内置 sRGB ICC 都取不到（或内部异常被它自己的 catch 吞掉）。
            // 那是产品故障，不是环境限制 ⇒ 改记红。
            Assert("BakeIccToTarget: MapToSRgb 有产出", false,
                "内置 ICC 表取不到（BakeIccToTarget 为纯 CPU 路径，与 D3D 无关）");
            return;
        }
        // 有产出时必须满足尺寸与 ICC 完整性契约（不可写成 targetIcc is null || 恒真式）
        Assert($"BakeIccToTarget: 像素尺寸正确 ({pixels.Length}B)", pixels.Length == bgra.Length);
        Assert($"BakeIccToTarget: ICC 完整 ({targetIcc?.Length ?? 0}B)",
               targetIcc is not null && targetIcc.Length > 0);
    }

    static void ColorSpaceConverter_MapToSRgb_BoundaryPixels()
    {
        // 边界像素值: 0, 255, 随机
        var bgra = new byte[4 * 4 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(i / 4 * 16);     // B
            bgra[i + 1] = (byte)(255 - i / 4 * 16); // G
            bgra[i + 2] = (byte)(i * 7 % 256); // R
            bgra[i + 3] = 255;
        }
        // ═══ 2026-09-19：原来一次都没调用 BakeIccToTarget，只对着自己造的输入判
        // bgra.All(b => b >= 0 && b <= 255) —— 对 byte[] 这是编译级恒真，名字承诺的"输出在界内"
        // 什么都没证。现真调用，并断产出非空/等长/alpha 直通/且没有走"像素直通"回退。
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        var (px, icc) = ColorProfileProvider.BakeIccToTarget(bgra, 4, 4, srgbIcc, "DisplayP3");
        Assert("BakeIccToTarget: 边界像素值有产出", px is not null && px.Length == bgra.Length,
            px is null ? "返回 null（内置 ICC 表取不到）" : $"{px!.Length}B");
        if (px is null) return;

        // alpha 直通：8-bit 边界值用例里"输出在界内"对 byte[] 恒真，可观测的界内证据是
        // 逐像素 alpha 保持 255（通道错位/被清零时这里变红）。
        bool alphaKept = true;
        for (int i = 3; i < px.Length; i += 4) if (px[i] != 255) { alphaKept = false; break; }
        Assert("BakeIccToTarget: 边界像素 alpha 逐像素直通=255", alphaKept);

        // 名字承诺"映射到目标色域" ⇒ 产出不得与输入逐字节相同（相同 = 矩阵解析失败走了直通回退）
        Assert("BakeIccToTarget: 边界像素真的换到 DisplayP3（非直通回退）",
               !px.AsSpan().SequenceEqual(bgra) && icc is { Length: >= 128 },
               $"像素{(px.AsSpan().SequenceEqual(bgra) ? "与输入全等(直通)" : "已改变")}, ICC {icc?.Length ?? 0}B");
    }

    static void ColorProfileProvider_BakeIccToTarget_AllSpaces()
    {
        var bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();

        var targets = new[] { "sRGB", "DisplayP3", "BT2020", "AdobeRGB" };
        foreach (var target in targets)
        {
            var (pixels, icc) = ColorProfileProvider.BakeIccToTarget(bgra, 64, 64, srgbIcc, target);
            if (pixels is null)
            {
                // ═══ 2026-09-19：同上——BakeIccToTarget 是纯 CPU 路径，null 只可能是
                // 内置 sRGB ICC 取不到 / 内部异常，属产品故障，不再伪装成"无 D3D 设备"的环境跳过。
                Assert($"BakeIccToTarget: {target} 有产出", false,
                    "内置 ICC 表取不到（BakeIccToTarget 为纯 CPU 路径，与 D3D 无关）");
                continue;
            }
            bool sizeOk = pixels.Length == bgra.Length;
            Assert($"BakeIccToTarget: {target} 像素尺寸正确 ({pixels.Length}B)", sizeOk);

            // ICC 契约（见 ColorProfileProvider.BakeIccToTarget）:
            //   sRGB 是隐含默认色域，目标为 sRGB 时刻意不嵌入 ICC（"sRGB 目标不嵌入 ICC"）；
            //   其余目标必须嵌入完整 ICC —— 至少 128B 才能容纳 ICC 头。
            if (target.Equals("sRGB", StringComparison.OrdinalIgnoreCase))
            {
                Assert($"BakeIccToTarget: {target} 不嵌入 ICC(契约) ({icc?.Length ?? 0}B)",
                       icc is null || icc.Length == 0);
            }
            else
            {
                Assert($"BakeIccToTarget: {target} 嵌入完整 ICC ({icc?.Length ?? 0}B)",
                       icc is not null && icc.Length >= 128);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  5. AnnotationManager
    // ═══════════════════════════════════════════════════════════════

    static void Annotation_AllLayerTypes()
    {
        var mgr = new AnnotationManager();
        mgr.AddLayer(AnnotationLayer.CreateRectangle(0, 0, 100, 100, "#FF0000", 2));
        mgr.AddLayer(AnnotationLayer.CreateEllipse(10, 10, 50, 50, "#00FF00", 3));
        mgr.AddLayer(AnnotationLayer.CreateArrow(0, 0, 100, 100, "#0000FF", 2));
        mgr.AddLayer(AnnotationLayer.CreateText(20, 20, "测试", "#FFFFFF", 16));
        mgr.AddLayer(AnnotationLayer.CreateNumber(30, 30, 1, "#FF0000", 20));
        mgr.AddLayer(AnnotationLayer.CreateBlur(40, 40, 60, 60, 10));
        mgr.AddLayer(AnnotationLayer.CreateMosaic(50, 50, 70, 70, 8));
        mgr.AddLayer(AnnotationLayer.CreateFreehand([(0, 0), (10, 10), (20, 20)], "#FF8800", 3));
        Assert("Annotation: 8种图层类型创建", mgr.LayerCount == 8);
    }

    static void Annotation_UndoRedo_Multiple()
    {
        var mgr = new AnnotationManager();
        for (int i = 0; i < 5; i++)
            mgr.AddLayer(AnnotationLayer.CreateRectangle(0, 0, 10, 10, "#000", 1));

        mgr.Undo();
        mgr.Undo();
        Assert("Annotation: Undo 2次后 LayerCount=3", mgr.LayerCount == 3);
        Assert("Annotation: Undo 后 CanRedo=true", mgr.CanRedo);

        mgr.Redo();
        Assert("Annotation: Redo 1次后 LayerCount=4", mgr.LayerCount == 4);
    }

    static void Annotation_UndoRedo_Boundary()
    {
        var mgr = new AnnotationManager();
        // 空状态不崩溃
        mgr.Undo();
        mgr.Redo();
        Assert("Annotation: 空状态 Undo/Redo 不崩溃", mgr.LayerCount == 0 && !mgr.CanUndo && !mgr.CanRedo);

        // 添加后撤销到空，再撤销不崩溃
        mgr.AddLayer(AnnotationLayer.CreateRectangle(0, 0, 10, 10, "#000", 1));
        mgr.Undo();
        mgr.Undo(); // 第二次撤销
        Assert("Annotation: 边界 Undo 不崩溃", mgr.LayerCount == 0);
    }

    static void Annotation_ClearAll()
    {
        var mgr = new AnnotationManager();
        var added = new List<AnnotationLayer>();
        for (int i = 0; i < 10; i++)
        {
            var layer = AnnotationLayer.CreateRectangle(0, 0, 10, 10, "#000", 1);
            mgr.AddLayer(layer);
            added.Add(layer);
        }
        // 逐个移除 (按 Guid)
        foreach (var layer in added)
            mgr.RemoveLayer(layer.Id);
        Assert("Annotation: 全部移除后 LayerCount=0", mgr.LayerCount == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  6. 编码器可用性
    // ═══════════════════════════════════════════════════════════════

    static void Encoder_AllFormats_Create()
    {
        var formats = Enum.GetValues<OutputFormat>();
        bool allOk = true;
        foreach (var fmt in formats)
        {
            try
            {
                var encoder = EncoderFactory.Create(fmt);
                allOk &= encoder != null && encoder.Format == fmt;
            }
            catch { allOk = false; }
        }
        Assert($"EncoderFactory: 全部 {formats.Length} 种格式可创建", allOk);
    }

    static void Encoder_AllFormats_QualityRange()
    {
        var formats = Enum.GetValues<OutputFormat>();
        bool allOk = true;
        foreach (var fmt in formats)
        {
            var encoder = EncoderFactory.Create(fmt);
            var (min, max, def, label) = encoder.GetQualityRange();
            allOk &= min <= def && def <= max && !string.IsNullOrEmpty(label);
        }
        Assert($"EncoderFactory: 全部 {formats.Length} 种格式质量范围合法", allOk);
    }

    static void Encoder_AllFormats_QualityDescription()
    {
        var formats = Enum.GetValues<OutputFormat>();
        bool allOk = true;
        foreach (var fmt in formats)
        {
            var encoder = EncoderFactory.Create(fmt);
            var desc = encoder.GetQualityDescription(encoder.GetQualityRange().Default);
            allOk &= !string.IsNullOrEmpty(desc);
        }
        Assert($"EncoderFactory: 全部 {formats.Length} 种格式质量描述非空", allOk);
    }

    // ═══════════════════════════════════════════════════════════════
    //  7. SDR 编码组合测试
    // ═══════════════════════════════════════════════════════════════

    static void SdrEncode_AllFormats(byte[] bgra, int w, int h)
    {
        var formats = new[] { OutputFormat.PNG, OutputFormat.JPEG_LI, OutputFormat.JPEG_XL,
                              OutputFormat.AVIF, OutputFormat.WebP, OutputFormat.TIFF };
        foreach (var fmt in formats)
        {
            // ═══ 2026-09-19：原来 AVIF / JPEG_XL 的异常被 catch 降级成 _warnings++，
            // 而 RunAll 的退出码只看 _failed ⇒ 编码器全线抛异常本套件照样 0 失败。
            // 现按能力闸门：缺 native 工具 = 环境限制 → 显式 Skip；闸门之后的异常一律记红。
            if (fmt == OutputFormat.AVIF && !NativeAvifEncoder.IsAvailable)
            {
                Skip($"SDR AVIF {w}x{h}", "avifenc.exe 不可用（本机缺 native 工具）");
                continue;
            }
            if (fmt == OutputFormat.JPEG_XL && !NativeJxlEncoder.IsAvailable)
            {
                Skip($"SDR JXL {w}x{h}", "cjxl.exe 不可用（本机缺 native 工具）");
                continue;
            }
            try
            {
                var encoder = EncoderFactory.Create(fmt);
                var settings = new EncodingSettings
                {
                    Format = fmt, Quality = 90f, HdrOutput = false,
                    ChromaSubsampling = "444", OutputBitDepth = 8,
                };
                string ext = fmt switch
                {
                    OutputFormat.PNG => ".png", OutputFormat.JPEG_LI => ".jpg",
                    OutputFormat.JPEG_XL => ".jxl", OutputFormat.AVIF => ".avif",
                    OutputFormat.WebP => ".webp", OutputFormat.TIFF => ".bmp",
                    _ => ".bin"
                };
                string path = Path.Combine(OutDir, $"sdr_{fmt}_{w}x{h}{ext}");
                if (File.Exists(path)) File.Delete(path);

                encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();

                var fi = new FileInfo(path);
                Assert($"SDR {fmt} {w}x{h}: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
            }
            catch (Exception ex)
            {
                Assert($"SDR {fmt} {w}x{h}", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    static void SdrEncode_ChromaCombinations(byte[] bgra, int w, int h)
    {
        var chromas = new[] { "444", "422", "420" };
        foreach (var chroma in chromas)
        {
            try
            {
                var encoder = EncoderFactory.Create(OutputFormat.JPEG_LI);
                var settings = new EncodingSettings
                {
                    Format = OutputFormat.JPEG_LI, Quality = 90f, HdrOutput = false,
                    ChromaSubsampling = chroma, OutputBitDepth = 8,
                };
                string path = Path.Combine(OutDir, $"jpeg_chroma_{chroma}_{w}x{h}.jpg");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
                var fi = new FileInfo(path);
                Assert($"JPEG Chroma {chroma}: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
            }
            catch (Exception ex)
            {
                Assert($"JPEG Chroma {chroma}", false, ex.GetType().Name);
            }
        }
    }

    static void SdrEncode_BitDepthCombinations(byte[] bgra, int w, int h)
    {
        // PNG 位深组合
        // ═══ 2026-09-19：原来三档只断"文件字节数 > 0"，位深参数写了没人读也照样绿。
        // 现按 PngEncoder.EncodeSdrAsync 的契约回读容器：byte[] 入口恒为 8-bit 源 ⇒
        // "OutputBitDepth > 8 先被钳回 8"，于是三档产物都必须是 8-bit 容器且【不写 sBIT】。
        // 哪天有人去掉钳制、写出 PNG 3.0 里根本不存在的 colorType 6 / 10-bit 容器，
        // 这份独立解析器会当场报错（"组合非法"），而不是继续报绿。
        foreach (var bd in new[] { 8, 10, 12 })
        {
            try
            {
                var encoder = EncoderFactory.Create(OutputFormat.PNG);
                var settings = new EncodingSettings
                {
                    Format = OutputFormat.PNG, Quality = 100f, HdrOutput = false,
                    ChromaSubsampling = "444", OutputBitDepth = bd,
                };
                string path = Path.Combine(OutDir, $"png_bitdepth_{bd}bit_{w}x{h}.png");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
                var fi = new FileInfo(path);
                Assert($"PNG {bd}-bit: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
                if (!fi.Exists || fi.Length == 0) continue;

                var png = TryReadPng(path, $"PNG {bd}-bit(SDR/8-bit 源)");
                if (png is null) continue;
                byte[]? sbit = png.Find("sBIT");
                Assert($"PNG {bd}-bit(SDR/8-bit 源): 容器恒 8-bit RGBA 且不写 sBIT",
                    png.BitDepth == 8 && png.ColorType == 6 && sbit is null,
                    $"请求 {bd}-bit ⇒ 实际 {png.BitDepth}-bit/colorType {png.ColorType}, sBIT={(sbit is null ? "无" : string.Join(",", sbit))}");
            }
            catch (Exception ex)
            {
                Assert($"PNG {bd}-bit", false, ex.GetType().Name + ": " + ex.Message);
            }
        }

        // AVIF 位深组合（能力闸门在前：avifenc 缺失属环境限制；闸门之后的异常一律记红）
        if (!NativeAvifEncoder.IsAvailable)
        {
            Skip("AVIF 位深测试", "avifenc.exe 不可用（本机缺 native 工具）");
            return;
        }
        foreach (var bd in new[] { 8, 10 })
        {
            try
            {
                var encoder = EncoderFactory.Create(OutputFormat.AVIF);
                var settings = new EncodingSettings
                {
                    Format = OutputFormat.AVIF, Quality = 18f, HdrOutput = false,
                    ChromaSubsampling = "420", OutputBitDepth = bd,
                };
                string path = Path.Combine(OutDir, $"avif_bitdepth_{bd}bit_{w}x{h}.avif");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
                var fi = new FileInfo(path);
                Assert($"AVIF {bd}-bit: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
                if (!fi.Exists || fi.Length == 0) continue;

                // 位深请求必须真的进到码流声明里：读产物 av1C 的 bitDepth
                // （判据口径与 Core 侧 AvifEncoderProbe 一致 —— 它就是为了拦"avifenc 静默回退 8-bit"而存在）。
                bool parsed = AvifEncoderProbe.TryParseAv1C(File.ReadAllBytes(path), out var av1c);
                Assert($"AVIF {bd}-bit: 产物 av1C 声明的位深 == 请求的 {bd}",
                    parsed && av1c.BitDepth == bd,
                    parsed ? $"av1C 读回 {av1c.BitDepth}-bit / {av1c.Chroma}" : "产物里解析不到 av1C");
            }
            catch (Exception ex)
            {
                Assert($"AVIF {bd}-bit", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    static void SdrEncode_IccEmbedding(byte[] bgra, int w, int h)
    {
        // 非 sRGB 色域 → 嵌入 ICC
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        var formats = new[] { OutputFormat.PNG, OutputFormat.JPEG_LI, OutputFormat.JPEG_XL, OutputFormat.WebP };
        foreach (var fmt in formats)
        {
            // ═══ 2026-09-19：原来 JXL/WebP 的异常只 _warnings++，而退出码只看 _failed ⇒
            // 这两档全线抛异常本套件仍然 0 失败。改为能力闸门在前 + 闸门之后异常记红。
            if (fmt == OutputFormat.JPEG_XL && !NativeJxlEncoder.IsAvailable)
            {
                Skip($"ICC {fmt}", "cjxl.exe 不可用（本机缺 native 工具）");
                continue;
            }
            if (fmt == OutputFormat.WebP && !NativeWebPEncoder.IsAvailable)
            {
                Skip($"ICC {fmt}", "cwebp.exe 不可用（本机缺 native 工具）");
                continue;
            }
            try
            {
                var encoder = EncoderFactory.Create(fmt);
                var settings = new EncodingSettings
                {
                    Format = fmt, Quality = 90f, HdrOutput = false,
                    ChromaSubsampling = "444", OutputBitDepth = 8,
                    ColorSpaceTag = "DisplayP3",
                    IccProfile = srgbIcc,
                };
                string ext = fmt switch
                {
                    OutputFormat.PNG => ".png", OutputFormat.JPEG_LI => ".jpg",
                    OutputFormat.JPEG_XL => ".jxl", OutputFormat.WebP => ".webp",
                    _ => ".bin"
                };
                string path = Path.Combine(OutDir, $"icc_{fmt}_{w}x{h}{ext}");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
                var fi = new FileInfo(path);
                Assert($"ICC {fmt}: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
                if (fmt != OutputFormat.PNG || !fi.Exists || fi.Length == 0) continue;

                // ═══ 2026-09-19：原来这条只有 fi.Exists && fi.Length > 0 ——
                // "ICC 嵌入"这个名字与嵌入与否无关。PNG 用本文件自带的独立解析器逐字节回读。
                var png = TryReadPng(path, $"ICC {fmt}");
                if (png is null) continue;
                Assert("ICC PNG: iCCP 解出的 profile 与传入的逐字节相同",
                    IccpRoundtrips(png.Find("iCCP"), srgbIcc),
                    png.Find("iCCP") is null ? "缺 iCCP" : $"iCCP {png.Find("iCCP")!.Length}B");
                // DisplayP3 → CICP primaries=12（ColorProfileProvider.GetCicpPrimaries），
                // SDR transfer=13 / matrix=identity=0 / fullRange=1。
                byte[] wantP3Cicp = [12, 13, 0, 1];
                Assert("ICC PNG: cICP == [12,13,0,1]（DisplayP3 声明与 iCCP 并存）",
                    CicpMatches(png.Find("cICP"), wantP3Cicp), CicpText(png.Find("cICP")));
            }
            catch (Exception ex)
            {
                Assert($"ICC {fmt}", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    static void SdrEncode_CicpEmbedding(byte[] bgra, int w, int h)
    {
        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.PNG);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.PNG, Quality = 100f, HdrOutput = false,
                ChromaSubsampling = "444", OutputBitDepth = 8,
                ColorSpaceTag = "BT2020", // sRGB → CICP, 非 sRGB → ICC
            };
            string path = Path.Combine(OutDir, $"cicp_bt2020_{w}x{h}.png");
            if (File.Exists(path)) File.Delete(path);
            encoder.EncodeSdrAsync(bgra, w, h, settings, path).GetAwaiter().GetResult();
            var fi = new FileInfo(path);
            Assert($"CICP BT.2020: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
            if (!fi.Exists || fi.Length == 0) return;

            // ═══ 2026-09-19：同上，"CICP 嵌入"必须回读 cICP 本体而不是文件大小。
            var png = TryReadPng(path, "CICP BT.2020");
            if (png is null) return;
            byte[]? cicp = png.Find("cICP");
            byte[] wantBt2020Cicp = [9, 13, 0, 1];
            Assert("CICP BT.2020: cICP 逐字节 == [9,13,0,1]（primaries=9 BT.2100）",
                CicpMatches(cicp, wantBt2020Cicp), CicpText(cicp));
            // byte[] 源入口不做 ICC 兜底（兜底只在浮点链 PrepareSdrLinearFloat 里）⇒ 只写 cICP
            Assert("CICP BT.2020: 未传 ICC ⇒ 不写 iCCP", png.Find("iCCP") is null,
                png.Find("iCCP") is null ? "" : $"多出 iCCP {png.Find("iCCP")!.Length}B");
        }
        catch (Exception ex)
        {
            Assert("CICP BT.2020", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void SdrEncode_MinimalPixels()
    {
        // 1x1, 2x2, 3x3 像素
        foreach (var size in new[] { 1, 2, 3 })
        {
            var px = new byte[size * size * 4];
            for (int i = 0; i < px.Length; i += 4) { px[i] = 128; px[i + 1] = 64; px[i + 2] = 255; px[i + 3] = 255; }

            try
            {
                var encoder = EncoderFactory.Create(OutputFormat.PNG);
                string path = Path.Combine(OutDir, $"minimal_{size}x{size}.png");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeSdrAsync(px, size, size,
                    new EncodingSettings { Format = OutputFormat.PNG, Quality = 100f, HdrOutput = false, ChromaSubsampling = "444", OutputBitDepth = 8 },
                    path).GetAwaiter().GetResult();
                Assert($"PNG {size}x{size}: {new FileInfo(path).Length}B", new FileInfo(path).Exists && new FileInfo(path).Length > 0);
            }
            catch (Exception ex)
            {
                Assert($"PNG {size}x{size}", false, ex.GetType().Name);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  8. HDR 编码测试
    // ═══════════════════════════════════════════════════════════════

    static HdrFrameData CreateHdrFrame(byte[] bgra, int w, int h)
    {
        var hdr = PixelOps.BgraToScrgbLinearFast(bgra, w, h);
        // 放大亮度模拟 HDR
        for (int i = 0; i < hdr.Length; i += 4)
        {
            hdr[i] *= 3f;
            hdr[i + 1] *= 3f;
            hdr[i + 2] *= 3f;
        }
        return new HdrFrameData { Pixels = hdr, Width = w, Height = h };
    }

    static void HdrEncode_AllSupported(HdrFrameData frame)
    {
        var hdrFormats = new[] { OutputFormat.PNG, OutputFormat.JPEG_XL, OutputFormat.AVIF, OutputFormat.JPEG_GAINMAP };
        foreach (var fmt in hdrFormats)
        {
            // ═══ 2026-09-19：原来这三档的异常只 _warnings++（退出码只看 _failed）⇒
            // HDR 编码器全部抛异常，本套件仍然"0 失败"。先按能力闸门，闸门后一律记红。
            if (fmt == OutputFormat.AVIF && !NativeAvifEncoder.IsAvailable)
            {
                Skip($"HDR {fmt}", "avifenc.exe 不可用（本机缺 native 工具）");
                continue;
            }
            if (fmt == OutputFormat.JPEG_XL && !NativeJxlEncoder.IsAvailable)
            {
                Skip($"HDR {fmt}", "cjxl.exe 不可用（本机缺 native 工具）");
                continue;
            }
            if (fmt == OutputFormat.JPEG_GAINMAP && !JpegLiNative.IsAvailable)
            {
                Skip($"HDR {fmt}", "cjpegli.exe 不可用（GainMap 的 base/gain JPEG 都靠它）");
                continue;
            }
            try
            {
                var encoder = EncoderFactory.Create(fmt);
                if (!encoder.SupportsHdr)
                {
                    Skip($"HDR {fmt}", "编码器不支持 HDR（能力限制）");
                    continue;
                }
                var settings = new EncodingSettings
                {
                    Format = fmt, Quality = 90f, HdrOutput = true,
                    ChromaSubsampling = "444", OutputBitDepth = 10,
                };
                string ext = fmt switch
                {
                    OutputFormat.PNG => ".png", OutputFormat.JPEG_XL => ".jxl",
                    OutputFormat.AVIF => ".avif", OutputFormat.JPEG_GAINMAP => ".jpg",
                    _ => ".bin"
                };
                string path = Path.Combine(OutDir, $"hdr_{fmt}{ext}");
                if (File.Exists(path)) File.Delete(path);
                encoder.EncodeAsync(frame, settings, path).GetAwaiter().GetResult();
                var fi = new FileInfo(path);
                Assert($"HDR {fmt}: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
            }
            catch (Exception ex)
            {
                Assert($"HDR {fmt}", false, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    static void HdrEncode_Pq16_Precision()
    {
        // 验证 PQ 16-bit 转换精度
        var hdr = new HdrFrameData
        {
            Pixels = [0.5f, 0.5f, 0.5f, 1f, 0.1f, 0.2f, 0.3f, 1f],
            Width = 2, Height = 1
        };
        var p16 = FormatHelper.HdrToPq16(hdr);
        // ═══ 2026-09-19：原来只断 p16.Length == 8 && p16.All(v => v <= 65535)
        // —— ushort 恒 ≤65535，等于只断长度。现按 ST.2084 的绝对码点 + 单调性来判。
        // LinearToPQ 的口径：scRGB 线性 1.0 = 80 nits ⇒ 0.5 = 40 nits、0.1/0.2/0.3 = 8/16/24 nits。
        Assert("HdrToPq16: 输出长度 = 像素数×4", p16.Length == 8, $"{p16.Length}");

        const int Code40Nits = 27478;   // round(0.419284 × 65535)
        int gray40 = p16[0];
        Assert($"HdrToPq16: 0.5 线性(=40nits) 的 PQ 码点 ≈ {Code40Nits}±320 (实际 {gray40})",
            Math.Abs(gray40 - Code40Nits) <= 320
            && p16[0] == p16[1] && p16[1] == p16[2],
            $"R/G/B={p16[0]},{p16[1]},{p16[2]}");
        // 同一像素内三通道必须随线性值单调（曲线写反/通道错位都会在这里变红）
        Assert("HdrToPq16: 像素内 0.1<0.2<0.3 线性 → 码点单调递增",
            p16[4] < p16[5] && p16[5] < p16[6], $"{p16[4]},{p16[5]},{p16[6]}");
        // 跨像素：0.5 灰必须亮过 0.3 蓝（两像素来自同一帧，亮度顺序不能翻）
        Assert("HdrToPq16: 跨像素单调 0.3 线性 < 0.5 线性", p16[6] < gray40, $"{p16[6]} < {gray40}");
        Assert("HdrToPq16: alpha 1.0 → 16-bit 容器满值 65535（两像素）",
            p16[3] == 65535 && p16[7] == 65535, $"{p16[3]},{p16[7]}");

        // 验证黑色
        var blackHdr = new HdrFrameData { Pixels = [0f, 0f, 0f, 1f], Width = 1, Height = 1 };
        var blackP16 = FormatHelper.HdrToPq16(blackHdr);
        Assert("HdrToPq16: 黑色→0", blackP16[0] == 0 && blackP16[1] == 0 && blackP16[2] == 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  9. JPEG 专项
    // ═══════════════════════════════════════════════════════════════

    static void Jpeg_AllQualityLevels()
    {
        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 64; bgra[i + 2] = 255; bgra[i + 3] = 255; }

        foreach (float q in new float[] { 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f })
        {
            var data = JpegLiNative.Encode(bgra, 16, 16, q);
            bool valid = data.Length > 100 && data[0] == 0xFF && data[1] == 0xD8;
            Assert($"JPEG Quality={q}: {data.Length}B, SOI={valid}", valid);
        }
    }

    static void Jpeg_Chroma_420_422_444()
    {
        var bgra = new byte[32 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 100; bgra[i + 1] = 150; bgra[i + 2] = 200; bgra[i + 3] = 255; }

        foreach (var chroma in new[] { "444", "422", "420" })
        {
            var data = JpegLiNative.Encode(bgra, 32, 16, 1.0f, chroma);
            bool valid = data.Length > 50 && data[0] == 0xFF && data[1] == 0xD8;
            Assert($"JPEG Chroma={chroma}: {data.Length}B, 有效={valid}", valid);
        }
    }

    /// <summary>
    /// JPEG 的 ICC 段结构（§31.3-8）。本用例原名"LargeProfile"却用 3.1KB 的 sRGB profile，
    /// 并且把"ICC 存在"判成"文件里出现过 0xFFE1" —— 那是**按当时的缺陷形态写死的断言**：
    /// 旧实现把 ICC 写进 APP1 且缺分块字段，于是一家能读到的外部实现都没有，测试却恒绿。
    /// <para>现在按权威口径判（libjpeg-turbo jcicc.c/jdicc.c：ICC 是 APP2，标识符后跟
    /// seq_no(1 基) + chunk_cnt）：① 单块必须 <c>FF E2 … 01 01</c>；② 全文件不许出现 APP1 形态的
    /// ICC；③ 段长字段自洽；④ 位置在 APP0(JFIF) 之后。另有真正的大 profile（&gt;65519B 载荷上限）
    /// 走分块：段数、seq 递增、每段 cnt 相同、拼回后与输入逐字节相同。</para>
    /// </summary>
    static void Jpeg_IccLargeProfile()
    {
        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }

        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        var data = JpegLiNative.Encode(bgra, 16, 16, 1.0f, "444", icc);
        bool valid = data.Length > 200 && data[0] == 0xFF && data[1] == 0xD8;
        Assert($"JPEG ICC: 编码产出可用（{data.Length}B）", valid);
        if (!valid) return;

        var segs = JpegAppSegments(data);
        Assert("JPEG ICC: 全文件不存在 APP1 形态的 ICC（旧缺陷形态）",
            !segs.Any(s => s.Marker == 0xE1 && s.Payload.StartsWith(IccProfileId)),
            $"发现 {segs.Count(s => s.Marker == 0xE1 && s.Payload.StartsWith(IccProfileId))} 段");
        var iccSegs = segs.Where(s => s.Marker == 0xE2 && s.Payload.StartsWith(IccProfileId)).ToList();
        Assert("JPEG ICC: 存在 APP2 的 ICC_PROFILE 段", iccSegs.Count >= 1, $"实得 {iccSegs.Count} 段");
        if (iccSegs.Count == 0) return;
        Assert("JPEG ICC: 单块 profile 的 seq/cnt 必须是 01 01",
            iccSegs.Count == 1 && iccSegs[0].Payload[12] == 1 && iccSegs[0].Payload[13] == 1,
            $"段数 {iccSegs.Count}, seq={iccSegs[0].Payload[12]}, cnt={iccSegs[0].Payload[13]}");
        Assert("JPEG ICC: 段内 profile 与嵌入前逐字节相同",
            iccSegs[0].Payload.AsSpan(14).SequenceEqual(icc));
        // 位置：JFIF 要求 APP0 紧跟 SOI，ICC 排它后面
        int iccPos = -1, app0Pos = -2;
        for (int i = 2; i + 4 <= data.Length && iccPos < 0; i++)
        {
            if (data[i] != 0xFF) continue;
            if (data[i + 1] == 0xE2 && data.AsSpan(i + 4, 12).SequenceEqual(IccProfileId)) iccPos = i;
            else if (data[i + 1] == 0xE0) app0Pos = i;
        }
        Assert("JPEG ICC: 若产物带 APP0/JFIF，ICC 段必须排在它之后",
            app0Pos == -2 || iccPos > app0Pos, $"app0={app0Pos} icc={iccPos}");

        // ── 真·大 profile：段长字段 16-bit ⇒ 超过 65519B 必须分块 ──
        // 起点必须是【没嵌过 ICC】的 JPEG：上面那张 data 已经带着一段 sRGB 的 APP2，
        // 拿它当底会数出 3 段 ICC（实测就是这样把本用例判红的），分块算术无从谈起。
        var clean = JpegLiNative.Encode(bgra, 16, 16, 1.0f, "444", iccProfile: null);
        Assert("JPEG ICC: 对照组——不传 profile 时产物里不该有 ICC 段",
            !JpegAppSegments(clean).Any(s => s.Marker == 0xE2 && s.Payload.StartsWith(IccProfileId)));
        var big = new byte[70_000];
        BitConverter.GetBytes((uint)big.Length).CopyTo(big, 0);      // 头部声明长度（BE）
        big[36] = (byte)'a'; big[37] = (byte)'c'; big[38] = (byte)'s'; big[39] = (byte)'p';
        var injected = JpegLiNative.InjectIccIntoJpeg(clean, big);
        var bigSegs = JpegAppSegments(injected)
            .Where(s => s.Marker == 0xE2 && s.Payload.StartsWith(IccProfileId)).ToList();
        Assert("JPEG ICC: 70000B profile 必须切成多段（单段上限 65519B）",
            bigSegs.Count == 2, $"实得 {bigSegs.Count} 段");
        Assert("JPEG ICC: 每段 seq 从 1 递增、chunk_cnt 恒等于总段数",
            bigSegs.Count == 2 && bigSegs[0].Payload[12] == 1 && bigSegs[1].Payload[12] == 2
            && bigSegs.All(s => s.Payload[13] == bigSegs.Count),
            bigSegs.Count == 2 ? $"{bigSegs[0].Payload[12]}/{bigSegs[0].Payload[13]} "
                              + $"{bigSegs[1].Payload[12]}/{bigSegs[1].Payload[13]}" : "段数不对");
        var rebuilt = bigSegs.OrderBy(s => s.Payload[12])
                            .SelectMany(s => s.Payload.Skip(14)).ToArray();
        Assert("JPEG ICC: 分块拼回后与原 profile 逐字节相同", rebuilt.SequenceEqual(big),
            $"{rebuilt.Length} vs {big.Length}");
    }

    private static readonly byte[] IccProfileId = System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0");

    /// <summary>扫 JPEG 的段（marker + payload），遇 SOS 停止 —— 只看结构，不碰熵编码数据。</summary>
    private static List<(byte Marker, byte[] Payload)> JpegAppSegments(byte[] jpeg)
    {
        var outList = new List<(byte, byte[])>();
        int pos = 2;
        while (pos + 4 <= jpeg.Length)
        {
            if (jpeg[pos] != 0xFF) { pos++; continue; }
            byte marker = jpeg[pos + 1];
            if (marker == 0xDA) break;
            if (marker is 0x01 or 0xD8 or 0xD9 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }
            int seg = (jpeg[pos + 2] << 8) | jpeg[pos + 3];
            if (seg < 2 || pos + 2 + seg > jpeg.Length) break;
            var payload = new byte[seg - 2];
            Array.Copy(jpeg, pos + 4, payload, 0, payload.Length);
            outList.Add((marker, payload));
            pos += 2 + seg;
        }
        return outList;
    }

    static void Jpeg_EncodeToBytes_ValidJpeg()
    {
        var bgra = new byte[8 * 8 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 255; bgra[i + 1] = 0; bgra[i + 2] = 0; bgra[i + 3] = 255; } // 纯蓝
        var data = JpegLiNative.Encode(bgra, 8, 8, 1.0f);
        bool valid = data.Length > 50
            && data[0] == 0xFF && data[1] == 0xD8  // SOI
            && data[^2] == 0xFF && data[^1] == 0xD9; // EOI
        Assert("JPEG: SOI + EOI 标记正确", valid);
    }

    static void Jpeg_Streaming_Valid()
    {
        var bgra = new byte[32 * 32 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = (byte)(i % 256); bgra[i + 1] = (byte)(i * 2 % 256); bgra[i + 2] = (byte)(i * 3 % 256); bgra[i + 3] = 255; }
        string path = Path.Combine(OutDir, "jpeg_stream_test.jpg");
        var bytes = JpegLiNative.Encode(bgra, 32, 32, 1.0f);
        File.WriteAllBytes(path, bytes);
        var fi = new FileInfo(path);
        Assert($"JPEG 文件写入: {fi.Length}B", fi.Exists && fi.Length > 0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  10. PNG 专项
    // ═══════════════════════════════════════════════════════════════

    static void Png_AllBitDepths()
    {
        var bgra = new byte[16 * 16 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 64; bgra[i + 2] = 255; bgra[i + 3] = 255; }

        // ═══ 2026-09-19：原来三档只断"文件字节数 > 0"⇒ bitDepth 参数没被读也照样绿。
        // 现按 ManagedPngEncoder.Encode(byte[] BGRA8 源) 的契约回读容器：
        //   · PNG 3.0 colorType 6 只有 8/16 两种容器 ⇒ 请求 10/12 必须是 16-bit 容器；
        //   · sBIT 声明的是【源精度】不是请求位深 —— 8-bit 源升到 16-bit 容器仍是 8 位信息
        //     （见 ManagedPngEncoder 内 "byte[] 输入只有 8-bit 精度" 的注释），所以期望 [8,8,8,8]；
        //   · 请求 8-bit 时容器就是 8-bit 且【不写 sBIT】。
        foreach (int bd in new[] { 8, 10, 12 })
        {
            string path = Path.Combine(OutDir, $"png_bitdepth_{bd}_{16}x{16}.png");
            if (File.Exists(path)) File.Delete(path);
            ManagedPngEncoder.Encode(bgra, 16, 16, path, bd);
            var fi = new FileInfo(path);
            Assert($"PNG {bd}-bit: {fi.Length}B", fi.Exists && fi.Length > 0);
            var png = TryReadPng(path, $"PNG {bd}-bit(直调编码器)");
            if (png is null) continue;
            byte[]? sbit = png.Find("sBIT");

            int wantContainer = bd <= 8 ? 8 : 16;
            Assert($"PNG {bd}-bit: IHDR 容器位深 == {wantContainer}（colorType 6 只有 8/16）",
                png.Width == 16 && png.Height == 16 && png.ColorType == 6 && png.BitDepth == wantContainer,
                $"实际 {png.Width}x{png.Height}/{png.BitDepth}/ct{png.ColorType}");

            if (bd <= 8)
            {
                Assert($"PNG {bd}-bit: 容器即源精度 ⇒ 不写 sBIT", sbit is null,
                    sbit is null ? "" : $"多出 sBIT [{string.Join(",", sbit)}]");
            }
            else
            {
                Assert($"PNG {bd}-bit: sBIT == [8,8,8,8]（声明 8-bit 源精度，四通道一致且 ≤ 容器位深）",
                    sbit is { Length: 4 } && sbit[0] == 8 && sbit[1] == 8 && sbit[2] == 8 && sbit[3] == 8
                    && 8 <= png.BitDepth,
                    sbit is null ? "缺 sBIT" : $"[{string.Join(",", sbit)}]");
                // 16-bit 容器里的台阶必须是 v*257（8→16 的无损扩展），否则"升位深"只是补零
                bool upscaled = true;
                for (int i = 0; i < bgra.Length / 4 && upscaled; i++)
                {
                    ushort[] want = [(ushort)(bgra[i * 4 + 2] * 257), (ushort)(bgra[i * 4 + 1] * 257),
                                     (ushort)(bgra[i * 4] * 257), (ushort)(bgra[i * 4 + 3] * 257)];
                    for (int c = 0; c < 4; c++)
                        if (png.Samples[i * 4 + c] != want[c]) { upscaled = false; break; }
                }
                Assert($"PNG {bd}-bit: 16-bit 容器按 RGBA 逐采样 == 源值×257（无通道错位/无补零）", upscaled);
            }
        }
    }

    static void Png_16bit_Roundtrip()
    {
        // ═══ 2026-09-19：原来只断"文件字节数 > 0" ═══
        // 那种断言下，编码器把 RGBA 写成 BGRA、16-bit 写成小端、滤波算错、CRC 打错、
        // IDAT 少一行……全都照样绿。现在做真往返：写 → 用独立实现解析（含逐块 CRC）→
        // 反向滤波 → 逐采样比对。任何一步不合法都会在解析阶段抛异常并被记为失败。

        // ── ① 16-bit 容器：逐采样精确相等 ──
        // 每像素 R/G/B/A 数量级都不同 ⇒ 通道错位、字节序错位无法互相抵消。
        const int w = 5, h = 3;
        var rgba16 = new ushort[w * h * 4];
        for (int i = 0; i < w * h; i++)
        {
            rgba16[i * 4 + 0] = (ushort)(i * 1229 + 17);          // R
            rgba16[i * 4 + 1] = (ushort)(65535 - i * 997);        // G
            rgba16[i * 4 + 2] = (ushort)((i * 7919) & 0xFFFF);    // B
            rgba16[i * 4 + 3] = (ushort)(i % 2 == 0 ? 65535 : 0); // A 交替 0/满 ⇒ alpha 不是常量
        }
        string path = Path.Combine(OutDir, "png_16bit_roundtrip.png");
        if (File.Exists(path)) File.Delete(path);
        ManagedPngEncoder.Encode16(FormatHelper.Rgba16ToBgra16Bytes(rgba16, w, h), w, h, path);

        PngFile png;
        try { png = PngFile.Read(path); }
        catch (Exception ex)
        {
            Assert("PNG 16-bit: 写出的是可独立解析的 PNG", false, ex.GetType().Name + ": " + ex.Message);
            return;
        }
        Assert("PNG 16-bit: 写出的是可独立解析的 PNG（逐块 CRC 已校验）", true);
        Assert($"PNG 16-bit: IHDR = {w}x{h} / 位深 16 / colorType 6(RGBA)",
            png.Width == w && png.Height == h && png.BitDepth == 16 && png.ColorType == 6,
            $"实际 {png.Width}x{png.Height} / {png.BitDepth} / {png.ColorType}");
        Assert("PNG 16-bit: 容器位深=存储位深 ⇒ 不应写 sBIT", png.Find("sBIT") is null);
        Assert("PNG 16-bit: 恰有一块 IDAT 且以 IEND 收尾",
            png.Count("IDAT") == 1 && png.Has("IEND") && png.Chunks[^1].Type == "IEND",
            string.Join(",", png.Chunks.Select(c => c.Type)));

        int expected = w * h * 4;
        bool same = png.Samples.Length == expected;
        int firstBad = -1;
        for (int i = 0; same && i < expected; i++)
            if (png.Samples[i] != rgba16[i]) { firstBad = i; break; }
        Assert("PNG 16-bit: 每个采样逐值相等（RGBA 顺序 + 大端 + 滤波还原正确）",
            same && firstBad < 0,
            same ? (firstBad < 0 ? $"{expected} 个采样全等"
                 : $"首个不符 #{firstBad}(像素{firstBad / 4} 通道{firstBad % 4}) 期望 {rgba16[firstBad]} 实际 {png.Samples[firstBad]}")
                 : $"解出 {png.Samples.Length} 个采样，期望 {expected}");

        // ── ② 10-bit 容器：sBIT 声明与实际台阶必须自洽 ──
        // 输入刻意让每个通道的低 6 位都非零 ⇒ 取整方向一旦退回"向下截断"，
        // 所有采样会集体低半个台阶：单点误差越过 ±32、总体误差变成大的负数，两处都变红。
        const int n2 = 64;                       // 8×8 ⇒ 0..63 每一种低 6 位余数各出现一次
        var src16 = new ushort[n2 * 4];
        for (int k = 0; k < n2; k++)
        {
            src16[k * 4 + 0] = (ushort)(k * 64 + k % 64);                       // 低位段
            src16[k * 4 + 1] = (ushort)(32768 + k * 64 + (k + 31) % 64);        // 高位段
            src16[k * 4 + 2] = (ushort)(65472 - (k * 64 + (k + 7) % 64));       // 贴近容器上限
            src16[k * 4 + 3] = 65535;                                           // 单独验钳位（见下）
        }
        string path10 = Path.Combine(OutDir, "png_10bit_sbit.png");
        if (File.Exists(path10)) File.Delete(path10);
        ManagedPngEncoder.Encode16(FormatHelper.Rgba16ToBgra16Bytes(src16, 8, 8), 8, 8, path10, bitDepth: 10);

        PngFile p10;
        try { p10 = PngFile.Read(path10); }
        catch (Exception ex)
        {
            Assert("PNG 10-bit: 写出的是可独立解析的 PNG", false, ex.GetType().Name + ": " + ex.Message);
            return;
        }
        var sb = p10.Find("sBIT");
        Assert("PNG 10-bit: IHDR 仍是 16-bit 容器（PNG colorType 6 无 10-bit）",
            p10.BitDepth == 16 && p10.ColorType == 6, $"{p10.BitDepth}/{p10.ColorType}");
        Assert("PNG 10-bit: sBIT 存在、四通道一致",
            sb is { Length: 4 } && sb[0] == sb[1] && sb[1] == sb[2] && sb[2] == sb[3],
            sb is null ? "缺 sBIT" : string.Join(",", sb));
        if (sb is not { Length: 4 }) return;

        int sig = sb[0];
        Assert("PNG 10-bit: sBIT 声明 10 且不超过容器位深（语义自洽）",
            sig == 10 && sig <= p10.BitDepth, $"sBIT={sig} 容器={p10.BitDepth}");

        int dropBits = p10.BitDepth - sig;
        int mask = (1 << dropBits) - 1;          // sBIT 声明"低 dropBits 位不携带信息"⇒ 必须为 0
        int halfStep = 1 << (dropBits - 1);
        long errSum = 0;
        bool aligned = true, withinHalf = true;
        int worst = 0;
        for (int i = 0; i < n2 * 4; i++)
        {
            int stored = p10.Samples[i];
            if ((stored & mask) != 0) aligned = false;
            if (i % 4 == 3) continue;            // alpha 恒 65535，单独判，不参与"就近"统计
            int err = stored - src16[i];
            errSum += err;
            if (Math.Abs(err) > halfStep) { withinHalf = false; if (Math.Abs(err) > worst) worst = Math.Abs(err); }
        }
        Assert($"PNG 10-bit: 全部采样低位清零（与 sBIT={sig} 一致）", aligned,
            $"低 {dropBits} 位应为 0");
        Assert($"PNG 10-bit: 单点误差不超过半个台阶 (≤{halfStep})，实际最大 {worst}",
            withinHalf, "向下截断会给出最多 -63 的误差");
        // 64 个像素正好走完 0..63 全部余数一轮 ⇒ 就近取整的平均误差是 +0.5 LSB，截断是 -31.5 LSB
        double meanErr = errSum / (n2 * 3.0);
        Assert($"PNG 10-bit: 总体无单向偏暗（平均误差 {meanErr:F2} LSB，应落在 [-0.5,+1.0]）",
            meanErr >= -0.5 && meanErr <= 1.0, "截断式取整的期望误差是 -32 LSB，即整图系统性偏暗半档");

        // 65535 的最近台阶是 65536 —— 容器放不下，必须钳到最高台阶而不是回绕成 0（纯白变纯黑）
        bool alphaClamped = true;
        for (int i = 3; i < n2 * 4; i += 4)
            if (p10.Samples[i] != 65472) { alphaClamped = false; break; }
        Assert("PNG 10-bit: 65535 钳到容器内最高台阶 65472（不回绕成 0）", alphaClamped,
            $"实际 {p10.Samples[3]}");
    }

    static void Png_IccAndCicp_Coexist()
    {
        // ═══ 2026-09-19：原名 Png_IccAndCicp_MutualExclusion —— 名字承诺"互斥"，源码却不是这么做的 ═══
        // ManagedPngEncoder.Encode / Encode16 的实际规则是【两者都写】（cICP 在前、iCCP 在后，
        // 源码注释明写"PNG 3.0 / RFC 9327 推荐 cICP 给新解码器、iCCP 给老解码器兜底"），
        // 只有"调用方传了哪个"决定写哪个，编码器自己不做任何取舍。
        // 原用例因此只敢断两条"文件字节数 > 0"，把这条假绿灯留着。现在按源码的实际行为钉死：
        // 三种输入组合各自的 chunk 存在性 + 数量 + 内容逐字节回读。
        var bgra = new byte[8 * 8 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }

        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        byte[] cicp = [1, 13, 0, 1];

        // ── ① 只给 ICC ──
        string pathIcc = Path.Combine(OutDir, "png_icc_only.png");
        ManagedPngEncoder.Encode(bgra, 8, 8, pathIcc, 8, icc, null);
        var a = TryReadPng(pathIcc, "PNG 声明/只给 ICC");
        if (a is null) return;
        Assert("PNG 声明/只给 ICC ⇒ 写 iCCP、不写 cICP",
            a.Has("iCCP") && !a.Has("cICP"), string.Join(",", a.Chunks.Select(x => x.Type)));
        Assert("PNG 声明/iCCP 解出来的 profile 与传入的逐字节相同", IccpRoundtrips(a.Find("iCCP"), icc));

        // ── ② 只给 CICP ──
        string pathCicp = Path.Combine(OutDir, "png_cicp_only.png");
        ManagedPngEncoder.Encode(bgra, 8, 8, pathCicp, 8, null, cicp);
        var b = TryReadPng(pathCicp, "PNG 声明/只给 CICP");
        if (b is null) return;
        Assert("PNG 声明/只给 CICP ⇒ 写 cICP、不写 iCCP",
            b.Has("cICP") && !b.Has("iCCP"), string.Join(",", b.Chunks.Select(x => x.Type)));
        byte[]? ccBytes = b.Find("cICP");
        Assert("PNG 声明/cICP 内容与传入的 4 字节逐项相同",
            ccBytes is { Length: 4 } && ccBytes[0] == 1 && ccBytes[1] == 13 && ccBytes[2] == 0 && ccBytes[3] == 1,
            ccBytes is null ? "缺 cICP" : string.Join(",", ccBytes));

        // ── ③ 两个都给：按源码规则共存，且 cICP 排在 iCCP 之前 ──
        string pathBoth = Path.Combine(OutDir, "png_icc_and_cicp.png");
        ManagedPngEncoder.Encode(bgra, 8, 8, pathBoth, 8, icc, cicp);
        var c = TryReadPng(pathBoth, "PNG 声明/两者都给");
        if (c is null) return;
        Assert("PNG 声明/两者都给 ⇒ 共存而非互斥（源码实际行为：两个都写）",
            c.Count("iCCP") == 1 && c.Count("cICP") == 1,
            $"iCCP×{c.Count("iCCP")} cICP×{c.Count("cICP")}");
        // 上面已要求恰好各 1；这条独立存在是因为"按名字取第一条命中"的读法会把重复写入伪装成正常，
        // 所以数量本身也要作为判据，而不是只判存在性。
        Assert("PNG 声明/色彩元数据 chunk 无重复写入",
            c.Count("iCCP") <= 1 && c.Count("cICP") <= 1 && c.Count("IHDR") == 1,
            string.Join(",", c.Chunks.Select(x => x.Type)));
        Assert("PNG 声明/cICP 排在 iCCP 之前（新解码器先看到紧凑声明）",
            c.IndexOf("cICP") >= 0 && c.IndexOf("iCCP") >= 0 && c.IndexOf("cICP") < c.IndexOf("iCCP"),
            $"cICP@{c.IndexOf("cICP")} iCCP@{c.IndexOf("iCCP")}");
        Assert("PNG 声明/共存时两个 chunk 的内容都保持原样",
            c.Find("cICP") is { Length: 4 } && IccpRoundtrips(c.Find("iCCP"), icc));
    }

    /// <summary>2026-09-19：读 PNG，失败就记一条红并返回 null（避免异常把整套用例带崩）。</summary>
    static PngFile? TryReadPng(string path, string label)
    {
        try { return PngFile.Read(path); }
        catch (Exception ex)
        {
            Assert($"{label}: 写出的是可独立解析的 PNG", false, ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    /// <summary>iCCP 载荷按规范是 <c>名称\0 + 压缩方法(0) + deflate(profile)</c>；解回来必须等于原 profile。</summary>
    static bool IccpRoundtrips(byte[]? iccp, byte[] expectedIcc)
    {
        if (iccp is not { Length: > 5 }) return false;
        int nul = Array.IndexOf(iccp, (byte)0);
        if (nul <= 0 || nul + 1 >= iccp.Length) return false;         // 名称非空且后面还有内容
        if (iccp[nul + 1] != 0) return false;                          // 压缩方法只允许 0 = deflate
        try
        {
            using var ms = new MemoryStream(iccp, nul + 2, iccp.Length - nul - 2, false);
            using var zs = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            zs.CopyTo(outMs);
            return outMs.ToArray().AsSpan().SequenceEqual(expectedIcc);
        }
        catch { return false; }
    }

    /// <summary>2026-09-19：cICP 逐字节比对。<paramref name="want"/> 为 null 时要求"确实没有 cICP"
    /// （AdobeRGB 走的就是这条路：H.273 无对应 primaries 码，误标会盖掉 iCCP）。</summary>
    static bool CicpMatches(byte[]? got, byte[]? want)
        => want is null ? got is null : got is { Length: 4 } && got.AsSpan().SequenceEqual(want);

    /// <summary>把 4 字节 cICP 打印成 [p,t,m,r]，便于失败时一眼看出错在哪一位。</summary>
    static string CicpText(byte[]? cicp)
        => cicp is null ? "无" : cicp.Length == 4 ? $"[{cicp[0]},{cicp[1]},{cicp[2]},{cicp[3]}]" : $"({cicp.Length}B)";

    static void Png_StreamOutput()
    {
        var bgra = new byte[8 * 8 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 200; bgra[i + 1] = 100; bgra[i + 2] = 50; bgra[i + 3] = 255; }
        using var ms = new MemoryStream();
        ManagedPngEncoder.Encode(bgra, 8, 8, ms, 8);
        Assert($"PNG 流输出: {ms.Length}B", ms.Length > 20);
    }

    // ═══════════════════════════════════════════════════════════════
    //  11. BMP 专项
    // ═══════════════════════════════════════════════════════════════

    static void Bmp_AllSizes()
    {
        foreach (var size in new[] { 1, 2, 3, 16, 127, 256 })
        {
            var bgra = new byte[size * size * 4];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = (byte)(i % 256); bgra[i + 1] = (byte)(i * 2 % 256); bgra[i + 2] = (byte)(i * 3 % 256); bgra[i + 3] = 255; }
            string path = Path.Combine(OutDir, $"bmp_{size}x{size}.bmp");
            if (File.Exists(path)) File.Delete(path);
            ManagedBmpEncoder.Encode(bgra, size, size, path);
            var fi = new FileInfo(path);
            bool valid = fi.Exists && fi.Length > 50;
            // BMP 签名: 'BM'
            if (valid) { var header = File.ReadAllBytes(path); valid = header[0] == (byte)'B' && header[1] == (byte)'M'; }
            Assert($"BMP {size}x{size}: {fi.Length}B, BM={valid}", valid);
        }
    }

    static void Bmp_StreamOutput()
    {
        var bgra = new byte[8 * 8 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 100; bgra[i + 1] = 150; bgra[i + 2] = 200; bgra[i + 3] = 255; }
        using var ms = new MemoryStream();
        ManagedBmpEncoder.Encode(bgra, 8, 8, ms);
        Assert($"BMP 流输出: {ms.Length}B", ms.Length > 50);
    }

    // ═══════════════════════════════════════════════════════════════
    //  12. JPEG Gain Map
    // ═══════════════════════════════════════════════════════════════

    static void GainMap_GrayMode()
    {
        var bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 128; bgra[i + 2] = 128; bgra[i + 3] = 255; }
        var hdr = CreateHdrFrame(bgra, 64, 64);

        // ═══ 2026-09-19：原来异常只 _warnings++，而退出码只看 _failed ⇒ GainMap 编码器
        // 稳定抛异常时这条仍然报绿。JpegGainMapEncoder 的硬依赖是 cjpegli.exe（base 与 gain
        // 两张 JPEG 都由它出）⇒ 缺工具属环境限制先 Skip，闸门之后的异常一律记红。
        if (!JpegLiNative.IsAvailable)
        {
            Skip("GainMap Gray", "cjpegli.exe 不可用（本机缺 native 工具）");
            return;
        }
        try
        {
            var encoder = new JpegGainMapEncoder();
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP, Quality = 1.0f, HdrOutput = true,
                GainMapMode = GainMapMode.Gray,
            };
            string path = Path.Combine(OutDir, "gainmap_gray.jpg");
            if (File.Exists(path)) File.Delete(path);
            encoder.EncodeAsync(hdr, settings, path).GetAwaiter().GetResult();
            var fi = new FileInfo(path);
            Assert($"GainMap Gray: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
        }
        catch (Exception ex)
        {
            Assert("GainMap Gray", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void GainMap_RgbMode()
    {
        var bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 128; bgra[i + 1] = 64; bgra[i + 2] = 192; bgra[i + 3] = 255; }
        var hdr = CreateHdrFrame(bgra, 64, 64);

        // 同 GainMap_GrayMode：能力闸门在前，闸门之后的异常一律记红。
        if (!JpegLiNative.IsAvailable)
        {
            Skip("GainMap RGB", "cjpegli.exe 不可用（本机缺 native 工具）");
            return;
        }
        try
        {
            var encoder = new JpegGainMapEncoder();
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP, Quality = 1.5f, HdrOutput = true,
                GainMapMode = GainMapMode.Rgb,
            };
            string path = Path.Combine(OutDir, "gainmap_rgb.jpg");
            if (File.Exists(path)) File.Delete(path);
            encoder.EncodeAsync(hdr, settings, path).GetAwaiter().GetResult();
            var fi = new FileInfo(path);
            Assert($"GainMap RGB: {fi.Length / 1024:N0}KB", fi.Exists && fi.Length > 0);
        }
        catch (Exception ex)
        {
            Assert("GainMap RGB", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void GainMap_QualitySettings()
    {
        // GainMap 模式通过 EncodingSettings.GainMapMode 配置（JpegGainMapEncoder 不再有实例属性）
        var settings = new EncodingSettings { Format = OutputFormat.JPEG_GAINMAP };
        Assert("GainMap: 默认 GainMapMode=Gray", settings.GainMapMode == GainMapMode.Gray);

        // ═══ 2026-09-19：原名"设置生效"名不符实 —— GainMapMode 是自动属性，
        // "刚赋值就读回来"在结构上不可能为假，它证的只是"这个字段可写"。
        // 产物级的证据目前只到 ColorPipelineTests 的 ISO 21496-1 元数据用例
        // （Gray 与 Rgb 两档分别断过长度 + 多通道标志位），本文件不重复构造那条判据。
        settings.GainMapMode = GainMapMode.Rgb;
        Assert("GainMap: GainMapMode 字段可写（仅配置对象，产物级差异见 ColorPipelineTests 的 ISO 元数据用例）",
            settings.GainMapMode == GainMapMode.Rgb, $"读回 {settings.GainMapMode}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  13. FormatHelper
    // ═══════════════════════════════════════════════════════════════

    static void FormatHelper_HdrToPq16_Valid()
    {
        // ═══ 2026-09-19：原来三条判据里，p16.All(v => v <= 65535) 对 ushort 是编译级恒真、
        // p16[4] > 0 也什么都不排除（PQ 曲线写反、nits 基准写错都 >0）。
        // 像素刻意按线性升序排：0 → 0.5 → 1.0，于是"码点随线性单调"与"绝对码点"两条都能判。
        // LinearToPQ 的口径：scRGB 线性 1.0 = 80 nits、0.5 = 40 nits（见 FormatHelper.LinearToPQ）。
        var frame = new HdrFrameData
        {
            Pixels = new float[] { 0f, 0f, 0f, 1f, 0.5f, 0.5f, 0.5f, 1f, 1f, 1f, 1f, 1f },
            Width = 3, Height = 1
        };
        var p16 = FormatHelper.HdrToPq16(frame);
        Assert("HdrToPq16: 长度=12 (3px*4ch)", p16.Length == 12, $"{p16.Length}");
        Assert("HdrToPq16: 黑色=0", p16[0] == 0 && p16[1] == 0 && p16[2] == 0,
            $"{p16[0]},{p16[1]},{p16[2]}");
        Assert("HdrToPq16: 码点随线性单调 0 < 0.5 < 1.0",
            p16[0] == 0 && p16[4] > p16[0] && p16[8] > p16[4], $"{p16[0]} < {p16[4]} < {p16[8]}");

        // 绝对码点（ST.2084，16-bit 容器）：40nits → 0.419284×65535 = 27478、
        // 80nits → 0.485857×65535 = 31841。容差 320 ≈ 1% —— 足以区分"白点写成 100nits"(33297)。
        const int Code40 = 27478, Code80 = 31841, PqTol = 320;
        Assert($"HdrToPq16: 0.5 线性(40nits) 的 PQ 码点 == {Code40}±{PqTol} (实际 {p16[4]})",
            Math.Abs(p16[4] - Code40) <= PqTol && p16[4] == p16[5] && p16[5] == p16[6]);
        Assert($"HdrToPq16: 1.0 线性(80nits) 的 PQ 码点 == {Code80}±{PqTol} (实际 {p16[8]})",
            Math.Abs(p16[8] - Code80) <= PqTol && p16[8] == p16[9] && p16[9] == p16[10]);
        Assert("HdrToPq16: alpha=1 逐像素映射到 16-bit 满值 65535",
            p16[3] == 65535 && p16[7] == 65535 && p16[11] == 65535, $"{p16[3]},{p16[7]},{p16[11]}");
    }

    static void FormatHelper_Rgba16ToBgra16_Valid()
    {
        ushort[] rgba16 = [255, 0, 0, 65535, 0, 65535, 0, 65535]; // 红 + 绿
        var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(rgba16, 1, 2);
        Assert("Rgba16ToBgra16: 长度=16 (2px*8B)", bgra16.Length == 16);
        // 第1像素: BGRA → B=0, G=0, R=255, A=65535
        Assert("Rgba16ToBgra16: 红→BGRA[0]=0(B)", bgra16[0] == 0 && bgra16[1] == 0);
        // 第1像素 R=255 → 16位大端 [0x00,0xFF] 位于 offset 4,5
        Assert("Rgba16ToBgra16: 红→BGRA[4..5]=R(大端)", bgra16[4] == 0 && bgra16[5] == 255);
    }

    static void FormatHelper_GetColorMetadata_AllTags()
    {
        // ═══ 2026-09-19：原来非 sRGB 三档只判"cicp 长度为 4"（primaries 写错也绿），
        // 而 icc is null 是用 IccProfile = null 自己构造出来的永真式。
        // 期望值出处：ColorProfileProvider.GetCicpPrimaries（sRGB/BT.709=1、P3=12、BT.2020=9）
        // + BuildSdrCicp 的 [primaries, transfer=13, matrix=identity=0, fullRange=1]
        //（同仓 CorePipelineTests 已逐值断过 GetCicpPrimaries，这里保持一致）。
        var expect = new (string Tag, byte[]? Cicp)[]
        {
            ("System", new byte[] { 1, 13, 0, 1 }),
            ("sRGB", new byte[] { 1, 13, 0, 1 }),
            ("DisplayP3", new byte[] { 12, 13, 0, 1 }),
            ("DCI_P3", new byte[] { 12, 13, 0, 1 }),
            ("AdobeRGB", null),      // H.273 Table 2 无 AdobeRGB primaries ⇒ 只靠 iCCP 声明
            ("BT2020", new byte[] { 9, 13, 0, 1 }),
        };
        foreach (var (tag, want) in expect)
        {
            var settings = new EncodingSettings { ColorSpaceTag = tag, IccProfile = null };
            var (icc, cicp) = FormatHelper.GetColorMetadata(settings);
            Assert($"GetColorMetadata({tag}): icc=null, cicp=={CicpText(want)}",
                icc is null && CicpMatches(cicp, want), $"实际 icc={(icc is null ? "null" : $"{icc!.Length}B")}, cicp={CicpText(cicp)}");
        }

        // ── 另开一组：喂真 ICC（必须过结构判据），让 icc 分支不再是"构造出来的永真" ──
        var realIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        Assert($"GetColorMetadata: 用例自备的真 ICC 过结构判据 ({realIcc.Length}B)",
            FormatHelper.IsEmbeddableIcc(realIcc),
            "自备夹具本身不合格 ⇒ 下面那组'icc 被保留'的断言就全是空转");
        var withIcc = new (string Tag, byte[]? Cicp, bool KeepIcc)[]
        {
            ("sRGB", new byte[] { 1, 13, 0, 1 }, false),      // sRGB 是隐含色域 ⇒ 即便给了 ICC 也不嵌
            ("DisplayP3", new byte[] { 12, 13, 0, 1 }, true),
            ("AdobeRGB", null, true),
        };
        foreach (var (tag, want, keepIcc) in withIcc)
        {
            var settings = new EncodingSettings { ColorSpaceTag = tag, IccProfile = realIcc };
            var (icc, cicp) = FormatHelper.GetColorMetadata(settings);
            bool iccOk = keepIcc ? ReferenceEquals(icc, realIcc) : icc is null;
            Assert($"GetColorMetadata({tag}+真ICC): icc {(keepIcc ? "原样带回" : "被丢弃(sRGB 隐含)")}, cicp=={CicpText(want)}",
                iccOk && CicpMatches(cicp, want),
                $"实际 icc={(icc is null ? "null" : $"{icc!.Length}B")}, cicp={CicpText(cicp)}");
        }
    }

    static void FormatHelper_ToSdr_AllModes()
    {
        var frame = new HdrFrameData
        {
            Pixels = new float[] { 0.5f, 0.3f, 0.8f, 1f, 1.5f, 2.0f, 0.5f, 1f },
            Width = 2, Height = 1
        };
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        foreach (var mode in modes)
        {
            var settings = new EncodingSettings { ToneMappingParams = new ToneMappingParams { Mode = mode } };
            var bytes = FormatHelper.ToSdr(frame, settings);
            Assert($"ToSdr({mode}): 长度={bytes.Length}, 所有字节在[0,255]", bytes.Length == 8 && bytes.All(b => b >= 0 && b <= 255));
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  14. 边界条件
    // ═══════════════════════════════════════════════════════════════

    static void Boundary_ZeroWidth_NoCrash()
    {
        // ═══ 2026-09-19：原来写成 try { Assert("…不崩溃", true); } catch { _warnings++; } ——
        // Assert(true) 恒真 + 异常不记失败 ⇒ 这条在结构上不可能为假，编码器崩了也报绿。
        // 现在：异常一律记红，并给出可观测结果（0x0 要么不出文件，出了就必须是非空且可独立解析的 PNG，
        // 不能在最终路径留下"写了签名没写 IEND"的半成品 —— 那是 AtomicFileWriter 的契约）。
        const string name = "ZeroWidth: 0x0 PNG 不崩溃";
        string path = Path.Combine(OutDir, "zero_width.png");
        if (File.Exists(path)) File.Delete(path);
        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.PNG);
            var settings = new EncodingSettings { Format = OutputFormat.PNG, Quality = 100f, HdrOutput = false, ChromaSubsampling = "444", OutputBitDepth = 8 };
            encoder.EncodeSdrAsync([], 0, 0, settings, path).GetAwaiter().GetResult();
            bool wrote = File.Exists(path);
            long len = wrote ? new FileInfo(path).Length : 0;
            Assert($"{name}（不留 0 字节半成品）", !wrote || len > 0, wrote ? $"产出 {len}B" : "未产出文件");
            if (wrote && len > 0) _ = TryReadPng(path, "ZeroWidth: 0x0 产物");   // 解析失败自己记红
        }
        catch (Exception ex)
        {
            Assert(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void Boundary_EmptyPixelData()
    {
        // 空像素数组 + 非零尺寸 → 应处理
        // 同 Boundary_ZeroWidth_NoCrash：原实现把异常降级成警告 ⇒ 改记红 + 可观测产物。
        const string name = "EmptyPixels: 空数据不崩溃";
        string path = Path.Combine(OutDir, "empty_pixels.png");
        if (File.Exists(path)) File.Delete(path);
        try
        {
            ManagedPngEncoder.Encode([], 0, 0, path, 8);
            bool wrote = File.Exists(path);
            long len = wrote ? new FileInfo(path).Length : 0;
            Assert($"{name}（不留 0 字节半成品）", !wrote || len > 0, wrote ? $"产出 {len}B" : "未产出文件");
            if (wrote && len > 0) _ = TryReadPng(path, "EmptyPixels: 空数据产物");
        }
        catch (Exception ex)
        {
            Assert(name, false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    static void Boundary_ExtremeQuality()
    {
        // 所有格式的极端质量值
        var formats = Enum.GetValues<OutputFormat>();
        foreach (var fmt in formats)
        {
            var encoder = EncoderFactory.Create(fmt);
            var (min, max, def, _) = encoder.GetQualityRange();

            // 2026-09-15 修正：原为 Assert(..., true) 恒真断言 —— 7 个格式一次性产出 7 条空通过，
            // 且注释声称的「编码器内部应 clamp」从未被真正调用验证。
            // 改为可观测契约：默认质量必须落在 [min, max] 区间内（真实判据，且无需实际编码）。
            // 注：越界输入（min-1 / max+1）的 clamp 行为属于编码器内部实现，此处不构造假断言，
            //     需要覆盖请新增真正调用编码器的用例。
            Assert($"ExtremeQuality({fmt}): 默认质量 {def} ∈ [{min}, {max}]", def >= min && def <= max);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  15. DisplayInfo
    // ═══════════════════════════════════════════════════════════════

    static void DisplayInfo_Enumerate_NoCrash()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            // 2026-09-15 修正：`displays.Count >= 0` 对 int 恒真，等于没断言。
            if (displays.Count == 0)
            {
                Skip("DisplayEnumerator", "headless 环境未枚举到显示器");
                return;
            }
            Assert($"DisplayEnumerator: {displays.Count} 显示器", displays.Count >= 1);
        }
        catch (Exception ex)
        {
            Assert("DisplayEnumerator", false, ex.GetType().Name);
        }
    }

    static void DisplayInfo_CursorMonitor_NoCrash()
    {
        // 无显示器（headless / 会话 0）时必然取不到光标所在显示器 —— 这是环境限制，
        // 不是产品缺陷。显式跳过，而不是把它算成通过（恒真）或失败（永远进不了门禁）。
        if (!HasDisplays())
        {
            Skip("GetMonitorUnderCursor", "无可用显示器（headless 环境）");
            return;
        }
        try
        {
            _ = DisplayEnumerator.GetMonitorUnderCursor();
            Assert("GetMonitorUnderCursor: 不崩溃", true);
        }
        catch (Exception ex)
        {
            Assert("GetMonitorUnderCursor", false, ex.GetType().Name);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  16. RegionDetector
    // ═══════════════════════════════════════════════════════════════

    static void RegionDetector_NoCrash()
    {
        // 窗口检测依赖真实窗口树；无显示器时同样属于环境限制，显式跳过。
        if (!HasDisplays())
        {
            Skip("RegionDetector.DetectWindows", "无可用显示器（headless 环境）");
            return;
        }
        try
        {
            var results = RegionDetector.DetectWindows(0, 0, 1920, 1080, new HashSet<nint>());
            // ═══ 2026-09-19：原来 catch 里只 _warnings++ ⇒ "不崩溃"这条结构上不可能为假。
            // 闸门（有显示器）之后的异常一律记红，并补上 DetectWindows 自己声明的过滤契约作为可观测结果：
            // 返回的每个区域都必须 ≥30×30 且与请求的虚拟桌面矩形有交集。
            int nRegions = results?.Count ?? -1;
            Assert("RegionDetector.DetectWindows: 不崩溃", nRegions >= 0, $"返回 {nRegions} 个区域");
            if (results is null) return;
            bool sized = true, intersecting = true;
            foreach (var r in results)
            {
                if (r.Width < 30 || r.Height < 30) sized = false;
                if (r.X >= 1920 || r.X + r.Width <= 0 || r.Y >= 1080 || r.Y + r.Height <= 0) intersecting = false;
            }
            Assert("RegionDetector.DetectWindows: 每个区域都过掉尺寸下限(≥30×30)", sized,
                string.Join(" | ", results.Take(5).Select(r => $"{r.Width}x{r.Height}")));
            Assert("RegionDetector.DetectWindows: 每个区域都与请求的捕获区有交集", intersecting,
                string.Join(" | ", results.Take(5).Select(r => $"{r.X},{r.Y} {r.Width}x{r.Height}")));
        }
        catch (Exception ex)
        {
            Assert("RegionDetector.DetectWindows: 不崩溃", false, ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  17. 综合管线测试
    // ═══════════════════════════════════════════════════════════════

    static void Pipeline_ColorSpace_ToSdr()
    {
        // 验证色彩空间设置 → 色调映射正确
        var hdr = new float[] { 0.5f, 0.3f, 0.8f, 1f, 1.5f, 2.0f, 0.5f, 1f };
        var frame = new HdrFrameData { Pixels = hdr, Width = 2, Height = 1 };

        var settings = new EncodingSettings
        {
            ToneMappingParams = new ToneMappingParams { Mode = ToneMapMode.Hable },
            ColorSpaceTag = "sRGB",
        };
        var bytes = FormatHelper.ToSdr(frame, settings);
        bool ok = bytes.Length == 8 && bytes.All(b => b >= 0 && b <= 255);
        Assert("Pipeline: HDR→SDR+sRGB 输出有效", ok);
    }

    static void Pipeline_ColorSpace_NoIcc_SrgbTarget()
    {
        // sRGB 目标 → GetColorMetadata 应返回 CICP, 无 ICC
        var settings = new EncodingSettings { ColorSpaceTag = "sRGB", IccProfile = null };
        var (icc, cicp) = FormatHelper.GetColorMetadata(settings);
        bool ok = icc is null && cicp is { Length: 4 } && cicp[0] == 1 && cicp[1] == 13;
        Assert("Pipeline: sRGB → CICP, 无ICC", ok);
    }

    static void Pipeline_ColorSpace_WithIcc_NonSrgbTarget()
    {
        // 非 sRGB 目标 + IccProfile 设置 → 应返回 ICC 和 CICP（PNG 3.0 推荐两者共存）
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        var settings = new EncodingSettings { ColorSpaceTag = "DisplayP3", IccProfile = srgbIcc };
        var (icc, cicp) = FormatHelper.GetColorMetadata(settings);
        bool ok = icc is { Length: > 128 } && cicp is { Length: 4 } && cicp[0] == 12 && cicp[1] == 13;
        Assert("Pipeline: DisplayP3+ICC→返回ICC和CICP", ok);
    }

    // ═══════════════════════════════════════════════════════════════
    //  18. 并发安全
    // ═══════════════════════════════════════════════════════════════

    static void Concurrent_EncoderFactory()
    {
        // EncoderFactory 是线程安全的，并行创建不应崩溃
        try
        {
            var results = new OutputFormat[10];
            Parallel.For(0, 10, i =>
            {
                var fmt = (OutputFormat)(i % 6);
                results[i] = EncoderFactory.Create(fmt).Format;
            });
            Assert("Concurrent: EncoderFactory 并行创建不崩溃", results.All(r => r >= 0));
        }
        catch (Exception ex)
        {
            Assert("Concurrent: EncoderFactory", false, ex.GetType().Name);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 独立 PNG 读取器 —— 刻意不复用 <see cref="ManagedPngEncoder"/>（被测对象）。
    /// <para>2026-09-19：PNG 用例此前只断"文件字节数 &gt; 0"，等于用"能不能写出文件"代替
    /// "写出的文件对不对"。判据必须来自规范本身而不是我们的第二份实现，所以这里按
    /// PNG 3.0 逐块解析 + 校验 CRC + 解压 IDAT + 反向滤波，任何一步不合法就抛异常。</para>
    /// <para>只支持非隔行的 8/16-bit（本项目的产物范围）。</para>
    /// </summary>
    sealed class PngFile
    {
        public int Width, Height, BitDepth, ColorType, Compression, FilterMethod, Interlace;
        public readonly List<(string Type, byte[] Data)> Chunks = [];
        /// <summary>反向滤波后的采样值，行主序，每像素 <see cref="Channels"/> 个（16-bit 容器按大端读）。</summary>
        public ushort[] Samples = [];

        public int Channels => ColorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };

        public bool Has(string name) => IndexOf(name) >= 0;
        public int IndexOf(string name)
        {
            for (int i = 0; i < Chunks.Count; i++) if (Chunks[i].Type == name) return i;
            return -1;
        }
        public int Count(string name)
        {
            int n = 0;
            foreach (var c in Chunks) if (c.Type == name) n++;
            return n;
        }
        public byte[]? Find(string name)
        {
            foreach (var c in Chunks) if (c.Type == name) return c.Data;
            return null;
        }

        static int BE32(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];

        /// <summary>CRC-32（多项式 0xEDB88320，逐位实现）：与编码器那份查表实现刻意不同形，
        /// 避免"两份实现共享同一个 bug"。</summary>
        static uint Crc32(byte[] d, int off, int len)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < len; i++)
            {
                crc ^= d[off + i];
                for (int k = 0; k < 8; k++)
                    crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
            return crc ^ 0xFFFFFFFF;
        }

        static int Paeth(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            return pb <= pc ? b : c;
        }

        public static PngFile Read(string path)
        {
            var d = File.ReadAllBytes(path);
            byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
            if (d.Length < 8) throw new InvalidDataException("文件比 PNG 签名还短");
            for (int i = 0; i < 8; i++)
                if (d[i] != sig[i]) throw new InvalidDataException("PNG 签名不符");

            var img = new PngFile();
            var idat = new MemoryStream();
            int pos = 8;
            while (pos + 12 <= d.Length)
            {
                int len = BE32(d, pos);
                string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
                if (len < 0 || (long)pos + 12 + len > d.Length)
                    throw new InvalidDataException($"{type} 块长 {len} 越界（文件 {d.Length}B）");
                var data = new byte[len];
                Buffer.BlockCopy(d, pos + 8, data, 0, len);
                uint stored = (uint)BE32(d, pos + 8 + len);
                uint calc = Crc32(d, pos + 4, len + 4);       // CRC 覆盖 类型 + 数据
                if (stored != calc)
                    throw new InvalidDataException($"{type} CRC 不符：文件写 {stored:X8}，按规范算得 {calc:X8}");
                img.Chunks.Add((type, data));
                if (type == "IDAT") idat.Write(data, 0, len);
                pos += 12 + len;
                if (type == "IEND") break;
            }

            var ihdr = img.Find("IHDR") ?? throw new InvalidDataException("缺 IHDR");
            if (ihdr.Length != 13) throw new InvalidDataException($"IHDR 长度={ihdr.Length}，应为 13");
            img.Width = BE32(ihdr, 0);
            img.Height = BE32(ihdr, 4);
            img.BitDepth = ihdr[8];
            img.ColorType = ihdr[9];
            img.Compression = ihdr[10];
            img.FilterMethod = ihdr[11];
            img.Interlace = ihdr[12];
            if (img.Compression != 0 || img.FilterMethod != 0)
                throw new InvalidDataException($"compression={img.Compression} filter={img.FilterMethod}（只允许 0/0）");
            if (img.Interlace != 0) throw new InvalidDataException("本读取器不支持 Adam7 隔行");
            if (img.Channels == 0) throw new InvalidDataException($"未知 colorType={img.ColorType}");
            if ((img.BitDepth is not (1 or 2 or 4 or 8 or 16)) ||
                (img.ColorType == 6 && img.BitDepth is not (8 or 16)))   // PNG 3.0 Table 11
                throw new InvalidDataException($"colorType={img.ColorType} 与 bitDepth={img.BitDepth} 组合非法");
            if (!img.Has("IEND")) throw new InvalidDataException("缺 IEND（文件被截断？）");

            byte[] raw;
            using (var src = new MemoryStream(idat.ToArray()))
            using (var zs = new ZLibStream(src, CompressionMode.Decompress))
            using (var sink = new MemoryStream())
            {
                zs.CopyTo(sink);
                raw = sink.ToArray();
            }

            int bytesPerChannel = img.BitDepth <= 8 ? 1 : 2;
            int rowBytes = img.Width * img.Channels * bytesPerChannel;
            if (raw.Length != (long)img.Height * (1 + rowBytes))
                throw new InvalidDataException(
                    $"IDAT 解压得 {raw.Length}B，按 {img.Width}x{img.Height}/{img.BitDepth}/ct{img.ColorType} 应为 {img.Height * (1 + rowBytes)}B");

            // 反向滤波（PNG 3.0 6.3）：注意 bpp 是"每像素字节数"，回看距离按像素对齐
            int bpp = img.Channels * bytesPerChannel;
            var recon = new byte[img.Height * rowBytes];
            for (int y = 0; y < img.Height; y++)
            {
                int lineStart = y * (1 + rowBytes);
                byte f = raw[lineStart];
                if (f > 4) throw new InvalidDataException($"第 {y} 行滤波类型 {f} 非法");
                int dst = y * rowBytes, prev = dst - rowBytes;
                for (int i = 0; i < rowBytes; i++)
                {
                    int x = raw[lineStart + 1 + i];
                    int a = i >= bpp ? recon[dst + i - bpp] : 0;
                    int b = y > 0 ? recon[prev + i] : 0;
                    int c = y > 0 && i >= bpp ? recon[prev + i - bpp] : 0;
                    int pred = f switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) >> 1, _ => Paeth(a, b, c) };
                    recon[dst + i] = (byte)((x + pred) & 0xFF);
                }
            }

            if (img.BitDepth == 16)
            {
                img.Samples = new ushort[img.Height * img.Width * img.Channels];
                for (int i = 0; i < img.Samples.Length; i++)
                    img.Samples[i] = (ushort)((recon[i * 2] << 8) | recon[i * 2 + 1]);   // 大端 = PNG 规范字节序
            }
            else if (img.BitDepth == 8)
            {
                img.Samples = new ushort[recon.Length];
                for (int i = 0; i < recon.Length; i++) img.Samples[i] = recon[i];
            }
            else throw new InvalidDataException($"本读取器只解 8/16-bit，得到 {img.BitDepth}");

            return img;
        }
    }

    static byte[] GenerateTestPattern(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            bgra[i]     = (byte)(y * 255 / Math.Max(h - 1, 1));     // B: 垂直渐变
            bgra[i + 1] = (byte)(x * 255 / Math.Max(w - 1, 1));    // G: 水平渐变
            bgra[i + 2] = (byte)((x + y) % 256);                    // R: 对角条纹
            bgra[i + 3] = 255;

            // 模拟文字区域 (高对比度)
            if (x > w / 4 && x < w * 3 / 4 && y > h / 3 && y < h / 3 + 40)
            {
                bool isText = ((x - w / 4) / 3 + (y - h / 3) / 3) % 2 == 0;
                bgra[i] = bgra[i + 1] = bgra[i + 2] = (byte)(isText ? 0 : 255);
            }
        }
        return bgra;
    }

    static void Assert(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name} — {detail}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name} — {detail}"); }
    }
}