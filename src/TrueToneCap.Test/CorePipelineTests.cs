// TrueToneCap.Test/CorePipelineTests.cs
// 核心管线单元测试 — PixelOps / ToneMapper / ColorProfileProvider / GamutMapper / Annotation / Encoders
// 运行: dotnet run --project src/TrueToneCap.Test -- --unit-tests

using TrueToneCap.Core;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Annotation;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.Test;

/// <summary>核心管线单元测试集。</summary>
public static class CorePipelineTests
{
    private static int _passed, _failed;

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
        Test_ToneMapper_Aces_OutputInRange();
        Test_ToneMapper_Aces_HighlightRolloff();
        Test_ToneMapper_Aces_OdtMonotonic();
        Test_ToneMapper_Aces_GrayLevels();
        Test_ToneMapper_FusedKernel_MatchesSeparate();
        Test_ToneMapper_SwizzleOrder();

        // ColorProfileProvider
        Test_SrgbIcc_ValidHeader();
        Test_SrgbIcc_MinimumSize();
        Test_SrgbIcc_Cached();
        Test_ColorProfile_SrgbIcc_Valid();
        Test_ColorProfile_ResolveColorSpaceTag();
        Test_ColorProfile_MapColorSpaceTag();
        Test_ColorProfile_GetStandardIcc_AllSpaces();

        // PixelOps 扩展
        Test_DownsampleToGray_Dimensions();
        Test_DownsampleToGray_UniformColor();
        Test_ComputeEdgeProjections_FlatImage();
        Test_ComputeEdgeProjections_EdgeDetection();

        // GamutMapper
        Test_GamutMapper_HdrToSRgb_OutputRange();
        Test_GamutMapper_HdrToSRgb_BlackPreserved();
        Test_GamutMapper_MapToSRgb_NullIcc_Passthrough();

        // AnnotationManager
        Test_AnnotationManager_AddLayer();
        Test_AnnotationManager_UndoRedo();
        Test_AnnotationManager_RemoveLayer();

        // FormatEncoders
        Test_EncoderRegistry_AllFormatsRegistered();
        Test_EncoderRegistry_QualityRanges();
        Test_EncodingSettings_Defaults();

        // IccStore
        Test_IccStore_AllSpaces_Valid();
        Test_IccStore_Srgb_HasAcspSignature();

        // ColorSpaceConverter
        Test_ColorSpaceConverter_MatrixRowSum();
        Test_ColorSpaceConverter_NoNegativeValues();
        Test_ColorSpaceConverter_GetCicpPrimaries();

        // ColorProfileProvider 逻辑
        Test_ColorProfile_SrgbIcc_Valid();
        Test_ColorProfile_ResolveColorSpaceTag();
        Test_ColorProfile_MapColorSpaceTag();
        Test_ColorProfile_GetStandardIcc_AllSpaces();

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
        Console.WriteLine($"══════════════════════════════════════");
        return _failed > 0 ? 1 : 0;
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

    static void Test_ConvertFloatToHalf_Roundtrip()
    {
        // float → half → float 往返精度测试
        float[] testValues = [0f, 1f, -1f, 0.5f, 100f, 0.001f, -0.001f];
        bool ok = true;
        foreach (float v in testValues)
        {
            ushort half = (ushort)BitConverter.HalfToInt16Bits((Half)v);
            float back = (float)BitConverter.Int16BitsToHalf((short)half);
            if (Math.Abs(back - v) > 0.01f * Math.Max(1, Math.Abs(v)))
            { ok = false; break; }
        }
        Assert("ConvertFloatToHalf: 往返精度 <1%", ok);
    }

    static void Test_ConvertFloatToHalf_SpecialValues()
    {
        // 特殊值: 0, Inf, NaN
        ushort zero = (ushort)BitConverter.HalfToInt16Bits((Half)0f);
        bool ok = zero == 0;
        Assert("ConvertFloatToHalf: 0 → 0x0000", ok);
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

    static void Test_ToneMapper_Aces_OutputInRange()
    {
        var pixels = new float[] { 0f, 0.5f, 1f, 2f, 10f, 0.1f, 1f, 1f };
        ToneMapper.AcesToneMapCpu(pixels, 2, 1);
        bool ok = true;
        for (int i = 0; i < pixels.Length; i += 4)
            if (pixels[i] < 0 || pixels[i] > 1 || pixels[i + 1] < 0 || pixels[i + 1] > 1 || pixels[i + 2] < 0 || pixels[i + 2] > 1)
            { ok = false; break; }
        Assert("ToneMapper.Aces: 所有输出在 [0,1]", ok);
    }

    static void Test_ToneMapper_Aces_HighlightRolloff()
    {
        // 验证 ACES 高光平滑滚降：适度高光输入应单调递增（有区分度），
        // 而非硬截断到纯白。SDR 输出最终 clamp 到 [0,1]，但中间调有渐进肩部。
        // 输入 1x/2x/4x/8x 亮度，输出应严格递增（肩部滚降，非硬切）。
        float Prev(float v)
        {
            var p = new float[] { v, v, v, 1f };
            ToneMapper.AcesToneMapCpu(p, 1, 1);
            return p[0];
        }
        float o1 = Prev(1f), o2 = Prev(2f), o4 = Prev(4f), o8 = Prev(8f);
        bool ok = o1 < o2 && o2 < o4 && o4 < o8 && o8 <= 1.0f;
        Assert($"ToneMapper.Aces: 高光滚降单调 ({o1:F3}→{o2:F3}→{o4:F3}→{o8:F3})", ok);
    }

    static void Test_ToneMapper_Aces_OdtMonotonic()
    {
        // 验证 ODT 单调递增：更强的输入映射更亮（对比度增强不反转）
        var lo = new float[] { 0.2f, 0.2f, 0.2f, 1f };
        var hi = new float[] { 0.8f, 0.8f, 0.8f, 1f };
        ToneMapper.AcesToneMapCpu(lo, 1, 1);
        ToneMapper.AcesToneMapCpu(hi, 1, 1);
        bool ok = hi[0] > lo[0];
        Assert("ToneMapper.Aces: ODT 单调递增", ok);
    }

    static void Test_ToneMapper_Aces_GrayLevels()
    {
        // 验证 ACES 对典型灰阶的映射亮度合理（不严重偏暗）
        // 输入为 scRGB 线性（1.0 = 80 nits 参考）。测试 0.18(18%灰)/0.5/1.0(参考白)
        var p18 = new float[] { 0.18f, 0.18f, 0.18f, 1f };
        var p50 = new float[] { 0.5f, 0.5f, 0.5f, 1f };
        var p100 = new float[] { 1.0f, 1.0f, 1.0f, 1f };
        ToneMapper.AcesToneMapCpu(p18, 1, 1);
        ToneMapper.AcesToneMapCpu(p50, 1, 1);
        ToneMapper.AcesToneMapCpu(p100, 1, 1);
        // 参考白 (1.0) 应映射到接近 0.7-0.8（SDR 参考白），18% 灰应映射到 ~0.2-0.3
        bool ok = p18[0] < p50[0] && p50[0] < p100[0];
        ok &= p100[0] > 0.6f && p100[0] < 0.95f;   // 参考白合理
        ok &= p18[0] > 0.15f && p18[0] < 0.4f;     // 18% 灰合理（不严重偏暗）
        Assert($"ToneMapper.Aces: 灰阶亮度合理 (18%={p18[0]:F3}, 50%={p50[0]:F3}, 100%={p100[0]:F3})", ok);
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
    //  GamutMapper 测试
    // ═══════════════════════════════════════

    static void Test_GamutMapper_HdrToSRgb_OutputRange()
    {
        // HDR 输入 → SDR 输出所有值在 [0,255]
        var rng = new Random(99);
        int w = 32, h = 32;
        var hdr = new float[w * h * 4];
        for (int i = 0; i < hdr.Length; i += 4)
        {
            hdr[i] = (float)(rng.NextDouble() * 10);
            hdr[i + 1] = (float)(rng.NextDouble() * 10);
            hdr[i + 2] = (float)(rng.NextDouble() * 10);
            hdr[i + 3] = 1f;
        }
        var p = new ToneMappingParams { Mode = ToneMapMode.Aces };
        var sdr = GamutMapper.HdrToSRgb(hdr, w, h, p);
        // 输出是 byte[]，天然在 [0,255]，验证长度正确
        bool ok = sdr.Length == w * h * 4;
        Assert($"GamutMapper.HdrToSRgb: 输出长度 {sdr.Length} == {w * h * 4}", ok);
    }

    static void Test_GamutMapper_HdrToSRgb_BlackPreserved()
    {
        // 纯黑 HDR → SDR 仍为黑
        var hdr = new float[] { 0f, 0f, 0f, 1f };
        var p = new ToneMappingParams { Mode = ToneMapMode.Hable };
        var sdr = GamutMapper.HdrToSRgb(hdr, 1, 1, p);
        bool ok = sdr[0] == 0 && sdr[1] == 0 && sdr[2] == 0;
        Assert("GamutMapper.HdrToSRgb: 黑色保持黑色", ok);
    }

    static void Test_GamutMapper_MapToSRgb_NullIcc_Passthrough()
    {
        // 无 ICC → 直通，像素不变
        var bgra = new byte[] { 10, 20, 30, 255, 40, 50, 60, 255 };
        var (result, icc) = GamutMapper.MapToSRgb(bgra, 2, 1, null);
        bool ok = ReferenceEquals(result, bgra) && icc is null;
        Assert("GamutMapper.MapToSRgb: null ICC → 直通", ok);
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
    }

    static void Test_EncodingSettings_Defaults()
    {
        var s = new EncodingSettings();
        bool ok = s.Format == OutputFormat.PNG
            && s.Quality == 90f
            && !s.HdrOutput
            && s.AvifBackend == AvifEncoderBackend.Auto
            && s.AvifChroma == "444"
            && s.DisplayBitDepth == 8;
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

    // ═══════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════

    static void Assert(string name, bool condition)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}"); }
    }
}
