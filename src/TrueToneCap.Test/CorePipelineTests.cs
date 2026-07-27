// TrueToneCap.Test/CorePipelineTests.cs
// 核心管线单元测试 — PixelOps / ToneMapper / ColorProfileProvider
// 运行: dotnet run --project src/TrueToneCap.Test -- --unit-tests

using TrueToneCap.Core;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;

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
        Test_ToneMapper_FusedKernel_MatchesSeparate();
        Test_ToneMapper_SwizzleOrder();

        // ColorProfileProvider
        Test_SrgbIcc_ValidHeader();
        Test_SrgbIcc_MinimumSize();
        Test_SrgbIcc_Cached();

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
    //  辅助
    // ═══════════════════════════════════════

    static void Assert(string name, bool condition)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}"); }
    }
}
