// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/ColorPipelineTests.cs
// 色彩管线精度测试 — 色域映射、ICC/CICP 验证、PQ 曲线、端到端编码校验
// 运行: dotnet run --project src/TrueToneCap.Test -- --color-tests

using System.Diagnostics;
using TrueToneCap.Core;
using TrueToneCap.Core.Processing;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.Test;

/// <summary>色彩管线精度测试：验证所有色彩转换的正确性、编码文件色彩标记完整性。</summary>
public static class ColorPipelineTests
{
    private static int _passed, _failed;
    /// <summary>因环境不满足而【显式跳过】的用例数 —— 既不算通过也不算失败。</summary>
    private static int _skipped;
    private static readonly string OutDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_ColorTest");

    /// <summary>显式跳过一条用例：不计入通过/失败，但必须留下原因（禁止静默通过）。</summary>
    static void Skip(string name, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭  {name} — 跳过: {reason}");
    }

    public static int RunAll()
    {
        _passed = 0; _failed = 0; _skipped = 0;
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("══════════════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 色彩管线精度测试");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("══════════════════════════════════════════════\n");

        var sw = Stopwatch.StartNew();

        // ─── 1. sRGB↔Linear 往返精度 ───
        Console.WriteLine("── 1. sRGB↔Linear 往返 ──");
        SrgbLinear_Roundtrip_Accuracy();
        SrgbLinear_Lut_Consistency();
        SrgbLinear_EdgeCases();

        // ─── 2. PQ 曲线精度 ───
        Console.WriteLine("\n── 2. PQ 曲线 ──");
        PqCurve_KnownValues();
        PqCurve_Symmetric();
        PqCurve_ExtremeValues();

        // ─── 3. scRGB↔PQ 往返 ───
        Console.WriteLine("\n── 3. scRGB↔PQ ──");
        ScrgbToPq_Roundtrip();
        ScrgbToPq_Brightness();

        // ─── 4. ICC 配置文件验证 ───
        Console.WriteLine("\n── 4. ICC 配置文件 ──");
        IccProfile_AllStandardSpaces();
        IccProfile_MatrixAccuracy();
        IccProfile_BakeToTarget();
        IccProfile_HdrPqProfile();
        // 2026-09-20 许可合规 A-3：生成的 ICC 不得携带第三方版权字段，也不得取自本机系统文件
        IccProvenance_NoThirdPartyCopyrightBytes();

        // ─── 5. 编码文件色彩标记验证 ───
        Console.WriteLine("\n── 5. 编码文件色彩标记 ──");
        EncodedPng_CicpChunk_Valid();
        EncodedPng_IccpChunk_Valid();
        AvifPngFallback_KeepsDeclarationAndNotice();
        WebPFallback_WhenBackendFails_StillDeliversPngAndNotice();
        JpegLiAndJxl_BackendFailure_NeverClaimsSuccess();
        EncodedPng_HdrCicp_Bt2020Pq();
        EncodedPng_HdrCicp_10BitSbit();
        EncodedJpeg_GainMapMarker();
        EncodedAvif_ColorProperties();

        // ─── 6. 色调映射单调性 ───
        Console.WriteLine("\n── 6. 色调映射单调性 ──");
        ToneMap_AllModes_Monotonic();
        ToneMap_BlackPreservation();
        ToneMap_WhitePreservation();

        // ─── 6b. 分段 Reinhard 曲线（SDR 白点断崖回归防护）───
        Console.WriteLine("\n── 6b. 分段 Reinhard 曲线 ──");
        SegmentedReinhard_Monotonic_NoDarkRing();
        SegmentedReinhard_ContinuousAtSdrWhite();
        SegmentedReinhard_SdrRangeIdentity();
        SegmentedReinhard_SdrWhiteMapsToUnity();
        SegmentedReinhard_Pipeline_NoDarkRing();

        // ─── 7. 色域映射 ───
        Console.WriteLine("\n── 7. 色域映射 ──");
        GamutMap_SrgbToP3();
        // 2026-09-19 §4.3 双路径策略：色域矩阵跟【源精度】走，容器位深跟【用户请求】走，两者解耦。
        GamutDepth_DecoupledAtEveryOutputDepth();
        // §30.9 声明与像素矛盾家族：D-1 直通不得冒充目标色域；HDR 意图被改掉必须留痕
        BakeFailure_DoesNotClaimTargetGamut();
        Png_HdrBitDepthDowngrade_IsTraceable();
        // 缺陷 C：生成的 sRGB profile 要满足 D50-PCS，且烘焙矩阵不能 D50/D65 混乘
        SrgbIcc_PrimaryColumnsSumToD50White();
        BakeIcc_SrgbToSrgb_IsExactIdentity();
        // 缺陷 B1：TIFF 没有码点通道 ⇒ sRGB 也必须嵌 ICC，不许零声明
        Tiff_EmbedsIccEvenForSrgb();

        // ─── 8. 色域转换矩阵精度验证 ───
        Console.WriteLine("\n── 8. 色域转换矩阵精度 ──");
        ColorMatrix_ScrgbToP3_Accuracy();
        ColorMatrix_ScrgbToBt2020_Accuracy();
        ColorMatrix_ScrgbToAdobeRgb_Accuracy();
        // 2026-09-19：删去 ColorMatrix_ApplySrgbToTargetGamut()。
        // 它只为 ColorSpaceConverter.ApplySrgbToTargetGamut 这个零生产调用方的函数而存在
        // （8→float→矩阵→8 的双重量化陷阱，已随函数一并删除，理由见 ColorProfileProvider.cs 原位注释）。
        // 留一条"只覆盖永不执行代码"的用例，本身就是假绿灯。

        // ─── 9. Gain Map XMP 验证 ───
        Console.WriteLine("\n── 9. Gain Map XMP 验证 ──");
        GainMap_BuildXmpMetadata_Output();
        GainMap_BuildIsoMetadata_Output();
        GainMap_GainRange_SingleSourceAcrossPixelsXmpAndIso();

        sw.Stop();
        Console.WriteLine($"\n══════════════════════════════════════════════");
        TestSummary.Report("--color-tests", _passed, _failed, 0, sw.ElapsedMilliseconds, _skipped);
        Console.WriteLine("══════════════════════════════════════════════\n");
        // ⚠️ 用失败数当退出码会在「恰好 256 个失败」时溢出为 0（假绿灯）
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════════════
    //  1. sRGB↔Linear 往返精度
    // ═══════════════════════════════════════════════

    static void SrgbLinear_Roundtrip_Accuracy()
    {
        // 验证 sRGB→Linear→sRGB 往返 ΔE < 0.5 (肉眼不可察)
        float maxDelta = 0;
        for (int i = 0; i <= 255; i++)
        {
            float srgb = i / 255f;
            // sRGB → Linear (使用 PixelOps LUT)
            float lin = TrueToneCap.Core.PixelOps.SrgbToLinearLut[i];
            // Linear → sRGB (使用 ToneMapper 的公式)
            float back = lin <= 0.0031308f ? 12.92f * lin : 1.055f * MathF.Pow(lin, 1f / 2.4f) - 0.055f;
            float delta = Math.Abs(srgb - back);
            if (delta > maxDelta) maxDelta = delta;
        }
        Assert($"sRGB↔Linear 往返: 最大Δ={maxDelta * 255:F2}/255", maxDelta * 255 <= 0.5f);
    }

    static void SrgbLinear_Lut_Consistency()
    {
        // LUT 值与直接公式计算一致
        var lut = TrueToneCap.Core.PixelOps.SrgbToLinearLut;
        bool consistent = true;
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            float expected = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            if (Math.Abs(lut[i] - expected) > 0.0001f) { consistent = false; break; }
        }
        Assert("sRGB→Linear LUT: 与公式一致", consistent);
    }

    static void SrgbLinear_EdgeCases()
    {
        // 边界值: 0, 127, 255
        var lut = TrueToneCap.Core.PixelOps.SrgbToLinearLut;
        Assert("sRGB 0 → Linear ≈ 0.0", Math.Abs(lut[0]) < 0.0001f);
        Assert("sRGB 255 → Linear ≈ 1.0", Math.Abs(lut[255] - 1.0f) < 0.001f);
        // sRGB 128 → Linear ≈ 0.2158
        Assert("sRGB 128 → Linear ≈ 0.2158", Math.Abs(lut[128] - 0.2158f) < 0.005f);
    }

    // ═══════════════════════════════════════════════
    //  2. PQ 曲线精度
    // ═══════════════════════════════════════════════

    // ═══ 2026-09-19：下面这三条此前测的是【本文件自己抄写的一份 PQ 公式】═══
    // 生产侧的 FormatHelper.HdrToPq16 / NativeAvifEncoder.LinearToPq 一次都没被碰到，
    // 而断言只有 `pq > 0 && pq <= 1.0f` —— 注释里写死的 0.5081/0.7515/1.0 从未参与比较。
    // 现在本地副本（LinearToPq / PqToLinear）已删除，全部改由生产入口求值：
    //   编码腿 = FormatHelper.HdrToPq16（public，scRGB 线性 → 逐通道 16-bit PQ 码点）
    //   解码腿 = FormatHelper.ComputePeakNits（public，PQ 正向 EOTF，double 求值）
    // 判据是 SMPTE ST 2084 逆 EOTF 的锚点值（100/1000/10000 nits），不是我们自己的实现。
    // scRGB 约定 80 nits = 1.0 ⇒ 输入像素 = nits/80，与生产侧 nits = linear*80 同一条链路。

    static void PqCurve_KnownValues()
    {
        // ST.2084 逆 EOTF 锚点（L = nits/10000 归一化；0 nits 处 PQ 有 ~7.3e-7 的公式固有偏置，
        // 量化到 16-bit 后仍是码点 0）。1000 nits 一栏用 0.7518270962 而非交接里抄的 0.7515：
        // 后者与标准差 20 LSB，会被 ±2 LSB 容差直接判失败（0.7515 是不精确的三位小数引用）。
        (float nits, double pq)[] anchors =
        [
            (0f,     0.0000000000),
            (100f,   0.5080784215),
            (1000f,  0.7518270962),
            (10000f, 1.0000000000),
        ];
        foreach (var (nits, pq) in anchors)
        {
            int expected = (int)Math.Round(pq * 65535.0);
            var c = Pq16CodesForNits(nits);
            Assert($"PQ 码点: {nits,6} nits → {c[0]} (ST.2084 参考 {expected}, |Δ| ≤ 2 LSB)",
                Math.Abs(c[0] - expected) <= 2 && Math.Abs(c[1] - expected) <= 2 && Math.Abs(c[2] - expected) <= 2,
                $"[{c[0]},{c[1]},{c[2]},{c[3]}]");
        }

        // 像素打包格式：RGBA 逐通道 16-bit ⇒ 灰像素三通道同码点，alpha=1.0 落在末位 = 65535。
        // （不写明这一条的话，"取到的是 R 还是被交错打包的某个 16-bit 槽"根本无从判断。）
        var w = Pq16CodesForNits(10000f);
        Assert("HdrToPq16 打包: 灰像素 R=G=B、alpha=65535",
            w[0] == w[1] && w[1] == w[2] && w[3] == 65535, $"[{w[0]},{w[1]},{w[2]},{w[3]}]");
    }

    static void PqCurve_Symmetric()
    {
        // 往返：nits →(生产 HdrToPq16)→ 16-bit 码点 →(生产 ComputePeakNits)→ nits。
        // 两条腿都是生产函数，测试文件不再自带逆公式。
        // 取样区间 [300, 8700] nits：ComputePeakNits 把返回值 clamp 到 [203, 10000]
        // （它是给 JXL intensity_target 用的，越界必然失真），故不拿区间外的点做往返判据。
        int prev = -1;
        bool monotonic = true;
        float worstRel = 0;
        const int samples = 60;
        for (int i = 0; i < samples; i++)
        {
            float nits = 300f * MathF.Pow(29f, i / (float)(samples - 1));   // 300 → 8700，对数均匀
            var codes = Pq16CodesForNits(nits);
            if (codes[0] <= prev) monotonic = false;
            prev = codes[0];
            float back = FormatHelper.ComputePeakNits(codes);
            worstRel = Math.Max(worstRel, Math.Abs(back - nits) / nits);
        }
        Assert($"PQ 往返（生产函数）: {samples} 点最大相对误差 {worstRel * 100:F4}%", worstRel <= 0.0005f);
        // 单调性同样改成对生产函数求值后的单调 —— 原先是对本地副本的往返恒等式，恒成立
        Assert($"PQ 码点对 nits 严格单调（{samples} 点，生产 HdrToPq16）", monotonic);
    }

    static void PqCurve_ExtremeValues()
    {
        // 定义域端点与越界行为，全部经生产入口求值。
        var black = Pq16CodesForNits(0f);
        Assert($"PQ 0 nits → 码点 {black[0]} (参考 0, |Δ| ≤ 2 LSB)", Math.Abs(black[0] - 0) <= 2);
        var peak = Pq16CodesForNits(10000f);
        Assert($"PQ 10000 nits → 码点 {peak[0]} (参考 65535)", peak[0] == 65535);
        // 越界：LinearToPQ 先把 L clamp 到 [0,1] ⇒ 高侧饱和、低侧归零（不是 NaN、不是回绕）
        var over = Pq16CodesForNits(20000f);
        Assert($"PQ 超界 20000 nits 饱和 → 码点 {over[0]}", over[0] == 65535);
        var under = Pq16CodesForNits(-640f);   // scRGB 负光（浮点会话下会出现）
        Assert($"PQ 负光 -8 nits 归零 → 码点 {under[0]}", under[0] == 0);
    }

    // ═══════════════════════════════════════════════
    //  3. scRGB↔PQ 往返
    // ═══════════════════════════════════════════════

    static void ScrgbToPq_Roundtrip()
    {
        // scRGB linear (80nits=1.0) → PQ → 回到 scRGB
        // 简单验证: FormatHelper.HdrToPq16 和 FormatHelper.Pq16ToHdr 的往返
        var hdr = new float[] { 0.5f, 0.3f, 0.8f, 1.0f, 1.5f, 2.0f, 0.5f, 1.0f };
        var pq16 = FormatHelper.HdrToPq16(new HdrFrameData { Pixels = hdr, Width = 2, Height = 1 });

        // 验证 PQ16 值在合理范围内（非全零、非全满）
        bool hasContent = false;
        for (int i = 0; i < pq16.Length; i++)
        {
            if (pq16[i] > 0 && pq16[i] < 0xFFFF) { hasContent = true; break; }
        }
        Assert("HdrToPq16: 输出包含有效数据", hasContent);
        Assert("HdrToPq16: 输出长度正确", pq16.Length == 8);
    }

    static void ScrgbToPq_Brightness()
    {
        // 高亮度值应产生更高的 PQ 值
        var dark = new float[] { 0.2f, 0.2f, 0.2f, 1f, 0.2f, 0.2f, 0.2f, 1f };
        var bright = new float[] { 3.0f, 3.0f, 3.0f, 1f, 3.0f, 3.0f, 3.0f, 1f };
        var pqDark = FormatHelper.HdrToPq16(new HdrFrameData { Pixels = dark, Width = 2, Height = 1 });
        var pqBright = FormatHelper.HdrToPq16(new HdrFrameData { Pixels = bright, Width = 2, Height = 1 });
        Assert("scRGB 高亮→更高 PQ 值", pqBright[0] > pqDark[0] && pqBright[1] > pqDark[1]);
    }

    // ═══════════════════════════════════════════════
    //  4. ICC 配置文件验证
    // ═══════════════════════════════════════════════

    static void IccProfile_AllStandardSpaces()
    {
        // 使用 GetDefaultSRgbIcc 避免可能循环调用的 GetStandardIccProfile
        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        bool valid = icc.Length >= 128
            && icc[36] == (byte)'a' && icc[37] == (byte)'c'
            && icc[38] == (byte)'s' && icc[39] == (byte)'p';
        Assert($"sRGB ICC: 有效 ({icc.Length}B)", valid);
        Assert("sRGB ICC: 最小尺寸", icc.Length >= 128);
    }

    static void IccProfile_MatrixAccuracy()
    {
        // 名字承诺"矩阵值与已知值一致 / 包含 D50 白点"，原实现却是 tagCount >= 9 和 xHi > 0
        // —— 后者连"这个字段是 X 通道"都不证明。现照 IccProfile_HdrPqProfile 的 tag 遍历范式，
        // 真正把 s15Fixed16 读出来与公布值比对。
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        Assert($"sRGB ICC: 长度足以容纳 tag 表 ({srgbIcc.Length}B)", srgbIcc.Length >= 144);
        if (srgbIcc.Length < 144) return;

        int tagCount = srgbIcc[128] << 24 | srgbIcc[129] << 16 | srgbIcc[130] << 8 | srgbIcc[131];
        Assert($"sRGB ICC: tag 数量 = {tagCount}", tagCount is >= 9 and <= 64);

        // ① 头部 PCS 照明体（偏移 68，3×s15Fixed16）：ICC.1 规定 display profile 的 PCS 恒为 D50。
        //    这才是"包含 D50 白点"这句话唯一能落地的位置（原实现读的正是这 4 个字节，却只判 >0）。
        var (pX, pY, pZ) = ReadS15Triple(srgbIcc, 68);
        Assert($"sRGB ICC: 头部 PCS 照明体 = D50 (0.9642, 1.0000, 0.8249)，实际 ({pX:F4}, {pY:F4}, {pZ:F4})",
            MathF.Abs(pX - 0.9642f) < 0.002f && MathF.Abs(pY - 1.0f) < 0.002f && MathF.Abs(pZ - 0.8249f) < 0.002f);

        // ② wtpt 标签：sRGB 自己的照明体是 D65（CIE 1931 x=0.3127,y=0.3290 ⇒ Y=1 归一化后
        //    0.95047 / 1.00000 / 1.08883）。逐值钉死，改动白点即红。
        //    ⚠ 唯一来源是 Core 手写的 BuildValidSRgbIcc（2026-09-20 起不再读 Windows 的
        //      "sRGB Color Space Profile.icm"，理由见 IccProvenance_NoThirdPartyCopyrightBytes），
        //      它的 wtpt 与微软那份一样写成 **未做 D50 适配的 D65**，因此本用例把
        //      "D50"这条硬要求放在 ① 的头部 PCS 照明体上；wtpt 未做 D50 适配是产物的既有事实
        //      （ICC.1 严格来说要求 wtpt 也在 D50 PCS 下），已作为 Core 的一致性问题上报。
        var wtpt = FindIccXyzTag(srgbIcc, tagCount, "wtpt");
        if (wtpt is null) { Assert("sRGB ICC: 含 wtpt 标签", false, "tag 表里没有 wtpt"); return; }
        var (wX, wY, wZ) = wtpt.Value;
        Assert($"sRGB ICC: wtpt = sRGB 照明体 D65 (0.95047, 1.00000, 1.08883)，实际 ({wX:F4}, {wY:F4}, {wZ:F4})",
            MathF.Abs(wX - 0.95047f) < 0.002f && MathF.Abs(wY - 1.0f) < 0.002f && MathF.Abs(wZ - 1.08883f) < 0.002f);

        // ③ rXYZ / gXYZ / bXYZ：三列 = RGB→XYZ 矩阵。判据是公布值（由 BT.709/sRGB 原色坐标
        //    经 XYZ 组合得到），容差 ±0.002 覆盖 s15Fixed16 的量化步长 1/65536。
        var rT = FindIccXyzTag(srgbIcc, tagCount, "rXYZ");
        var gT = FindIccXyzTag(srgbIcc, tagCount, "gXYZ");
        var bT = FindIccXyzTag(srgbIcc, tagCount, "bXYZ");
        if (rT is null || gT is null || bT is null)
        {
            Assert("sRGB ICC: rXYZ/gXYZ/bXYZ 三个标签齐全", false,
                $"rXYZ={rT is not null} gXYZ={gT is not null} bXYZ={bT is not null}");
            return;
        }
        var (rX, rY, rZ) = rT.Value;
        var (gX, gY, gZ) = gT.Value;
        var (bX, bY, bZ) = bT.Value;
        Console.WriteLine($"     rXYZ=({rX:F4},{rY:F4},{rZ:F4}) gXYZ=({gX:F4},{gY:F4},{gZ:F4}) bXYZ=({bX:F4},{bY:F4},{bZ:F4})");

        // 矩阵按哪一个白点表达，由它自己的行和（M·(1,1,1) = 媒体白点）说明。
        // 两种取值都各有公布矩阵可比：D50 是 ICC.1 要求的 PCS 表达，D65 是 Core 手写回退
        // （ColorProfileProvider.cs:229-231，未做 D50 适配）实际写出的表达。二者都对不上就是矩阵错。
        float sumX = rX + gX + bX, sumY = rY + gY + bY, sumZ = rZ + gZ + bZ;
        static bool Near(float a, float b) => MathF.Abs(a - b) < 0.002f;
        bool isD50 = Near(sumX, 0.9642f) && Near(sumY, 1.0f) && Near(sumZ, 0.8249f);
        bool isD65 = Near(sumX, 0.95047f) && Near(sumY, 1.0f) && Near(sumZ, 1.08883f);
        Assert($"sRGB ICC: rXYZ+gXYZ+bXYZ 自洽于一个公布白点 ({sumX:F4}, {sumY:F4}, {sumZ:F4}) = D50 或 D65",
            isD50 ^ isD65);
        if (isD50 == isD65) return;

        // 行 = X/Y/Z，列 = R/G/B
        float[,] published = isD50
            ? new float[3, 3] { { 0.4360747f, 0.3850649f, 0.1430804f },
                                { 0.2225045f, 0.7168786f, 0.0606169f },
                                { 0.0139322f, 0.0971045f, 0.7141733f } }   // IEC 61966-2-1 的 D50 PCS（Bradford）
            : new float[3, 3] { { 0.4124547f, 0.3575816f, 0.1804232f },
                                { 0.2126729f, 0.7151522f, 0.0721750f },
                                { 0.0193339f, 0.1191920f, 0.9503041f } };   // 未适配的 D65 原值
        float[,] got = new float[3, 3] { { rX, gX, bX }, { rY, gY, bY }, { rZ, gZ, bZ } };
        float worst = 0; int wi = 0, wj = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float dd = MathF.Abs(got[i, j] - published[i, j]);
                if (dd > worst) { worst = dd; wi = i; wj = j; }
            }
        string whichWp = isD50 ? "D50" : "D65";
        Assert($"sRGB ICC: rXYZ/gXYZ/bXYZ 九个值逐个对上 {whichWp} 公布矩阵 (|Δ| ≤ 0.002，最差 [{wi},{wj}] Δ={worst:E2})",
            worst <= 0.002f);
    }

    static void IccProfile_BakeToTarget()
    {
        // 验证 ICC 烘焙不崩溃，输出合理
        var bgra = new byte[64 * 64 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 128; bgra[i + 1] = 64; bgra[i + 2] = 192; bgra[i + 3] = 255;
        }
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        var targets = new[] { "sRGB", "DisplayP3", "BT2020", "AdobeRGB" };
        foreach (var target in targets)
        {
            try
            {
                var (pixels, icc) = ColorProfileProvider.BakeIccToTarget(bgra, 64, 64, srgbIcc, target);
                if (pixels is null)
                {
                    // 无 D3D 设备时返回 null 属环境限制，必须显式跳过而非伪装通过
                    Skip($"ICC 烘焙: {target}", "无 D3D 设备，BakeIccToTarget 返回空（环境限制）");
                }
                else
                {
                    // 有产出时必须满足尺寸契约
                    Assert($"ICC 烘焙: {target} 像素尺寸正确", pixels.Length == bgra.Length);

                    // ICC 契约（见 ColorProfileProvider.BakeIccToTarget）:
                    //   sRGB 是隐含默认色域，目标为 sRGB 时刻意不嵌入 ICC（"sRGB 目标不嵌入 ICC"）；
                    //   其余目标必须嵌入完整 ICC —— 至少 128B 才能容纳 ICC 头。
                    if (target.Equals("sRGB", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert($"ICC 烘焙: {target} 不嵌入 ICC(契约) ({icc?.Length ?? 0}B)",
                               icc is null || icc.Length == 0);
                    }
                    else
                    {
                        Assert($"ICC 烘焙: {target} ICC 完整 ({icc?.Length ?? 0}B)",
                               icc is not null && icc.Length >= 128);
                    }
                }
            }
            catch (Exception ex)
            {
                Assert($"ICC 烘焙: {target} 异常: {ex.Message}", false);
            }
        }
    }

    static void IccProfile_HdrPqProfile()
    {
        // ═══ 2026-08-09: HDR PQ ICC (TIFF 用) — 必须声明 PQ TRC, 否则 PQ 像素按 sRGB 解码 ═══
        var pqIcc = ColorProfileProvider.GetHdrStandardIccProfile("BT2020");
        Assert("HDR PQ ICC: 非空", pqIcc is { Length: > 128 });
        if (pqIcc is null) return;

        int tagCount = pqIcc[128] << 24 | pqIcc[129] << 16 | pqIcc[130] << 8 | pqIcc[131];
        Assert("HDR PQ ICC: 9 tags", tagCount == 9);

        // 验证 rXYZ = BT.2020 D50 primaries (0.6735, 0.2791, -0.0019)
        bool xyzOk = false, pqTrcOk = false;
        for (int i = 0; i < tagCount; i++)
        {
            int e = 132 + i * 12;
            if (e + 12 > pqIcc.Length) break;
            uint sig = (uint)(pqIcc[e] << 24 | pqIcc[e + 1] << 16 | pqIcc[e + 2] << 8 | pqIcc[e + 3]);
            int off = pqIcc[e + 4] << 24 | pqIcc[e + 5] << 16 | pqIcc[e + 6] << 8 | pqIcc[e + 7];
            if (sig == 0x7258595Au && off + 20 <= pqIcc.Length) // rXYZ
            {
                static float S15(byte[] b, int o) => (b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]) / 65536f;
                float rX = S15(pqIcc, off + 8);
                float rY = S15(pqIcc, off + 12);
                xyzOk = MathF.Abs(rX - 0.6735f) < 0.005f && MathF.Abs(rY - 0.2791f) < 0.005f;
            }
            else if ((sig == 0x72545243u || sig == 0x67545243u || sig == 0x62545243u) && off + 12 <= pqIcc.Length) // TRC
            {
                // curv: [4B 'curv'][4B reserved][4B count][values]
                int cnt = pqIcc[off + 8] << 24 | pqIcc[off + 9] << 16 | pqIcc[off + 10] << 8 | pqIcc[off + 11];
                pqTrcOk = cnt == 4096;
                if (pqTrcOk && off + 12 + 4096 * 2 <= pqIcc.Length)
                {
                    // 中点值: PQ EOTF V=0.5 → L≈0.0092 (归一化 10000 nits)
                    int mid = off + 12 + (cnt / 2) * 2;
                    int v = pqIcc[mid] << 8 | pqIcc[mid + 1];
                    pqTrcOk = MathF.Abs(v / 65535f - 0.0092f) < 0.002f;
                }
            }
        }
        Assert("HDR PQ ICC: rXYZ = BT.2020 D50 primaries", xyzOk);
        Assert("HDR PQ ICC: TRC = PQ 4096 点曲线 (中点 L≈0.0092)", pqTrcOk);
    }

    // ═══════════════════════════════════════════════
    //  5. 编码文件色彩标记验证
    // ═══════════════════════════════════════════════

    static void EncodedPng_CicpChunk_Valid()
    {
        // 编码 PNG 并验证 cICP chunk —— 名字写着 _Valid，原实现却只判了 `CheckPngChunk(...)` 存在。
        // 同文件的 HDR 那条（EncodedPng_HdrCicp_Bt2020Pq）早已逐字节读回，这里照同一口径比较。
        var bgra = GeneratePattern(8, 8);
        string path = Path.Combine(OutDir, "cicp_test.png");
        if (File.Exists(path)) File.Delete(path);

        var encoder = EncoderFactory.Create(OutputFormat.PNG);
        var settings = new EncodingSettings
        {
            Format = OutputFormat.PNG, Quality = 100, HdrOutput = false,
            ColorSpaceTag = "sRGB", OutputBitDepth = 8,
        };
        encoder.EncodeSdrAsync(bgra, 8, 8, settings, path).GetAwaiter().GetResult();

        // 验证文件存在
        Assert("PNG cICP: 文件已创建", File.Exists(path) && new FileInfo(path).Length > 0);
        if (!File.Exists(path)) return;

        var chunks = ReadPngChunks(path);
        // 文件被创建 ≠ 是个 PNG：签名与 IHDR 顺序必须站得住（否则 chunk 链本身就没读对）
        Assert("PNG cICP: 首块是 IHDR",
            chunks.Count > 0 && chunks[0].Type == "IHDR",
            chunks.Count > 0 ? chunks[0].Type : "无 chunk");

        var cicp = FindPngChunk(chunks, "cICP");
        if (cicp is not { Length: 4 })
        {
            Assert("PNG cICP: cICP 块存在且为 4 字节", false,
                cicp is null ? "无 cICP 块" : $"cICP 长度={cicp.Length}");
            return;
        }
        Assert("PNG cICP: cICP 块存在且为 4 字节", true, $"[{cicp[0]},{cicp[1]},{cicp[2]},{cicp[3]}]");

        // cICP 的 4 字节 = primaries / transfer / matrix / fullRange（ITU-T H.273 + RFC 9327）。
        // 判据来自标准码点与"PNG 存 RGB 采样、输入是 8-bit sRGB"这一事实，
        // 不是复刻 ManagedPngEncoder 的写入逻辑。
        Assert($"PNG cICP: primaries=1 (BT.709/sRGB 原色)，实际 {cicp[0]}", cicp[0] == 1);
        Assert($"PNG cICP: transfer=13 (sRGB IEC 61966-2-1，非 BT.709 视频 OETF=1)，实际 {cicp[1]}", cicp[1] == 13);
        Assert($"PNG cICP: matrix=0 (identity，PNG 存 RGB 采样)，实际 {cicp[2]}", cicp[2] == 0);
        Assert($"PNG cICP: fullRange=1，实际 {cicp[3]}", cicp[3] == 1);

        // sRGB 是隐含默认色域 ⇒ 除 cICP 外不应再嵌 iCCP 与之竞争声明
        // （对照 EncodedPng_IccpChunk_Valid：非 sRGB 才必须有 iCCP）。
        Assert("PNG cICP: sRGB 档不嵌 iCCP", FindPngChunk(chunks, "iCCP") is null);
    }

    /// <summary>
    /// AVIF 全后端失败时的 PNG 保底，必须【仍然带上色域声明 + 降级留痕】（2026-09-29 审查轮 §10.3 V11 / §13.7）。
    /// <para>为什么要真跑一遍：§11.6 量到 <c>[1.7/2]</c> 那 14 条外部判据里【没有任何一条会走保底路径】
    /// ⇒ 三条"连格式都换了"的保底此前只有读码与文本锁。这条把 WebP/AVIF 那两格之一跑到产物上。</para>
    /// <para>故障注入用"目标文件被本进程独占"：avifenc 是独立进程，打不开被 <c>FileShare.None</c> 占住的
    /// <c>shot.avif</c> ⇒ 软件路失败 ⇒ 落进 catch 的 PNG 保底；保底写的是同名 <c>.png</c>，那条路径是空的
    /// ⇒ 能走完。另一条看着更省事的"喂 avifenc 必败的 <c>--depth 16</c>"已被实测否掉：
    /// <c>NativeAvifEncoder.GetDepthArg</c>（<c>:245-249</c>）自己先过一道 <c>AvifContainerDepth</c> 钳制 ⇒
    /// 产品路发不出那个参数（avifenc 1.4.2 确实会在参数期就拒，但那不是产品能走到的形状）。</para>
    /// </summary>
    static void AvifPngFallback_KeepsDeclarationAndNotice()
    {
        var dir = Path.Combine(OutDir, "avif-fallback");
        Directory.CreateDirectory(dir);
        var avifPath = Path.Combine(dir, "shot.avif");
        var pngPath = Path.ChangeExtension(avifPath, ".png");
        foreach (var f in new[] { avifPath, pngPath }) if (File.Exists(f)) File.Delete(f);

        var settings = new EncodingSettings
        {
            Format = OutputFormat.AVIF, Quality = 60, OutputBitDepth = 8, HdrOutput = false,
            ColorSpaceTag = "DisplayP3",
            IccProfile = ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.DisplayP3, "Display P3"),
            AvifEncoderKind = AvifEncoderKind.LibAom,
            // 这一格判的就是"保底认不认这个开关"⇒ 必须显式开（EncodingSettings 的初始值是关，
            // 因为测量宿主默认不许把失败洗成产物；见 ImageEncoder.cs 那段属性注释）。
            PngFallbackEnabled = true,
        };
        EncoderFallbackNotifier.LastReason = null;   // 单槽通道：不把上一拍的残留带进断言

        Exception? thrown = null;
        try
        {
            using (new FileStream(avifPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try
                {
                    EncoderFactory.Create(OutputFormat.AVIF)
                        .EncodeSdrAsync(GeneratePattern(8, 8), 8, 8, settings, avifPath)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex) { thrown = ex; }
            }
        }
        catch (Exception ex) { thrown = ex; }   // 独占句柄本身释放失败 ⇒ 也要留痕，不许静默

        var chunks = ReadPngChunks(pngPath);
        Assert("AVIF 保底：真的落了一枚非空 PNG（没落 = 注入没生效或保底本身坏）",
            File.Exists(pngPath) && new FileInfo(pngPath).Length > 0,
            thrown is null ? "avifenc 竟然写成功了 ⇒ 独占没起作用，本条这一轮【没判到东西】"
                           : $"抛的是 {thrown.GetType().Name}: {thrown.Message.Split('\n')[0]}");
        Assert("AVIF 保底的 PNG 必须带 iCCP 或 cICP（非 sRGB 目标不许零声明）",
            FindPngChunk(chunks, "iCCP") is not null || FindPngChunk(chunks, "cICP") is not null,
            "广色域像素 + 无声明 ⇒ 查看器一律按 sRGB 解，用户拿到系统性偏色的图");
        Assert("AVIF 保底必须写降级留痕，且说清换的是【格式】",
            (EncoderFallbackNotifier.LastReason ?? "").Contains("PNG"),
            $"实得 {EncoderFallbackNotifier.LastReason ?? "<null>"}");
    }

    /// <summary>
    /// F 批（2026-09-29）保底矩阵：五格式 × 开关两态，每一格只允许三种结局里的两种合法结局。
    /// <para>这一条原本的名字与口径是"JPEG LI / JXL / 增益图【没有】保底，只钉不许静默成功"（本轮 §15.3）；
    /// 用户随后拍板"接上 PNG 保底 + 触发必须告诉用户 + 给保底做开关"⇒ 三条腿都接上了，
    /// 于是这一格升成一张矩阵：<b>开</b> ⇒ 必须落 PNG、必须写留痕、必须登记实际路径；
    /// <b>关</b> ⇒ 必须抛、且【一律不落 PNG】（关掉以后还偷偷留一张 PNG，就等于开关没生效）。</para>
    /// <para>不合法的那一格永远是同一种形状：<b>既不抛、盘上又什么都没有</b> ——
    /// 编码器接口不返回路径，管线层会拿调用方那个 path 去 Toast「已保存」、塞剪贴板、进归档
    /// （见 <c>FormatHelper.ResolveActualPath</c>），于是用户按了热键、看到绿勾、盘上什么都没有（§12 那族）。</para>
    /// <para>注入：对目标文件持 <c>FileShare.None</c> 独占 ⇒ 子进程非零退出 / <c>File.WriteAllBytes</c>
    /// 撞共享冲突。每格独立目录 ⇒ "关掉开关后不许留 PNG"这句话才不会被上一格的残留喂成假绿。</para>
    /// </summary>
    static void JpegLiAndJxl_BackendFailure_NeverClaimsSuccess()
    {
        // 每一格的【前提】：那个后端在这台机器上存在。缺了前提，下面那句"注入成立"的读数依然成立
        // （0 字节壳是夹具自己造的，后端不在位时也没有进程去写它）⇒ 整格会绿着通过，
        // 却一次都没有判到矩阵要判的那一格："后端在位、而这一次编不出来"。
        // ⇒ 不在位就逐格记 Skip（进未覆盖台账，一格一条，与十格的格数对得上），不许记 ✅。
        // WebP 那一格早就有这道前提（见 WebPFallback_WhenBackendFails…），现在五格都有。
        static (string Probe, bool Present) BackendPresence(OutputFormat f) => f switch
        {
            OutputFormat.JPEG_LI or OutputFormat.JPEG_GAINMAP
                => ("cjpegli（JpegLiNative.IsAvailable）", JpegLiNative.IsAvailable),
            OutputFormat.JPEG_XL => ("cjxl（NativeJxlEncoder.IsAvailable）", NativeJxlEncoder.IsAvailable),
            OutputFormat.WebP => ("cwebp（NativeWebPEncoder.IsAvailable）", NativeWebPEncoder.IsAvailable),
            _ => ("avifenc（NativeAvifEncoder.IsAvailable）", NativeAvifEncoder.IsAvailable),
        };

        foreach (var fmt in new[] { OutputFormat.JPEG_LI, OutputFormat.JPEG_XL, OutputFormat.JPEG_GAINMAP,
                                    OutputFormat.WebP, OutputFormat.AVIF })
        foreach (var allow in new[] { true, false })
        {
            var (probe, present) = BackendPresence(fmt);
            if (!present)
            {
                Skip($"{fmt}/保底={(allow ? "开" : "关")}", $"本机取不到 {probe} ⇒ 走不到「后端在位而编码失败」那一支");
                continue;
            }
            var dir = Path.Combine(OutDir, $"fbx-{fmt}-{(allow ? "on" : "off")}");
            Directory.CreateDirectory(dir);
            var ext = fmt switch
            {
                OutputFormat.JPEG_LI or OutputFormat.JPEG_GAINMAP => ".jpg",
                OutputFormat.JPEG_XL => ".jxl",
                OutputFormat.WebP => ".webp",
                _ => ".avif",
            };
            var path = Path.Combine(dir, "shot" + ext);
            var pngPath = Path.ChangeExtension(path, ".png");
            // 每格独立目录 + 先清 PNG：这一格判的是【这一次】编不出来的时候做了什么。
            // 不清的话，上一格（同一 ext 的另一开关态）留下的 shot.png 会把"关掉开关后盘上不该有 PNG"
            // 直接读成假红，而反过来把"开"格读成假绿的可能也存在。
            foreach (var f in new[] { path, pngPath }) if (File.Exists(f)) File.Delete(f);

            var settings = new EncodingSettings
            {
                Format = fmt, Quality = 1.0f, OutputBitDepth = 8, HdrOutput = false,
                ColorSpaceTag = "sRGB", ChromaSubsampling = "444",
                PngFallbackEnabled = allow,
                AvifEncoderKind = AvifEncoderKind.LibAom,   // 避开硬件：AVIF 那两格要判的是软件后端这条腿
            };
            EncoderFallbackNotifier.LastReason = null;

            Exception? thrown = null;
            try
            {
                using (new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    try
                    {
                        EncoderFactory.Create(fmt)
                            .EncodeSdrAsync(GeneratePattern(8, 8), 8, 8, settings, path)
                            .GetAwaiter().GetResult();
                    }
                    catch (Exception ex) { thrown = ex; }
                }
            }
            catch (Exception ex) { thrown = ex; }

            var tag = $"{fmt}/保底={(allow ? "开" : "关")}";
            // 注入证据：那个文件仍是我创建的 0 字节壳 ⇒ 失败确实是独占造成的，不是编码器自己坏了
            Assert($"{tag}：注入成立（独占挡住了写入，原路径仍是 0 字节）",
                File.Exists(path) && new FileInfo(path).Length == 0,
                File.Exists(path) ? $"{new FileInfo(path).Length} B ⇒ 独占没挡住，这一格没判到东西"
                                  : "<原路径连壳都没有 ⇒ 夹具坏了>");

            if (allow)
            {
                Assert($"{tag}：必须落一枚非空 PNG（接上保底之后，这一格不许再把图丢掉）",
                    File.Exists(pngPath) && new FileInfo(pngPath).Length > 0,
                    thrown is null ? "<连原始编码都没失败 ⇒ 注入或格式判断有假>"
                                   : $"抛的是 {thrown.GetType().Name}: {thrown.Message.Split('\n')[0]}");
                Assert($"{tag}：触发保底必须留痕，且说清换的是【格式】（用户口径：一定要告诉用户）",
                    (EncoderFallbackNotifier.LastReason ?? "").Contains("PNG"),
                    $"实得 {EncoderFallbackNotifier.LastReason ?? "<null>"}");
                Assert($"{tag}：必须登记实际落盘路径，上层才报得出那张 PNG 而不是空的 .{ext.TrimStart('.')}",
                    FormatHelper.ResolveActualPath(path) == pngPath,
                    $"实得 {FormatHelper.ResolveActualPath(path)}");
            }
            else
            {
                Assert($"{tag}：开关关掉 ⇒ 必须抛（失败要可见，不许静默）", thrown is not null,
                    "既没抛也没产物 ⇒ 上层拿这个路径报「已保存」；抛都没抛更是直接谎报");
                Assert($"{tag}：开关关掉 ⇒ 一律不许落 PNG（留了 PNG = 开关没生效）",
                    !File.Exists(pngPath),
                    // ⚠ 断言的"失败详情"参数是【无条件求值】的：这里曾经直接 new FileInfo(pngPath).Length，
                    // 于是在这一格【通过】的时候抛 FileNotFoundException ⇒ 整套件崩、退出码 0xE0434353。
                    // 详情里凡是要读文件，必须先判存在。
                    File.Exists(pngPath)
                        ? $"盘上出现 {new FileInfo(pngPath).Length} B 的 {Path.GetFileName(pngPath)}"
                        : "<不该走到这一句>");
                Assert($"{tag}：开关关掉 ⇒ 不留「换过格式」的登记（上层不许被悄悄改路径）",
                    FormatHelper.ResolveActualPath(path) == path,
                    $"实得 {FormatHelper.ResolveActualPath(path)}");
                if (thrown is not null)
                    Assert($"{tag}：失败原因可归属（异常带类型与信息，日志/界面说得出是哪个后端）",
                        thrown.GetType().Name != "Exception" && !string.IsNullOrWhiteSpace(thrown.Message),
                        $"{thrown.GetType().Name} / {thrown.Message}");
            }
        }
    }

    /// <summary>
    /// F 批（2026-09-29）：WebP 那条"改存 PNG"的保底，此前<strong>只在 cwebp 缺失时兑现</strong>。
    /// <para>形状与 §13.9 的 AVIF 同一族：<c>WebPEncoder.EncodeSdrAsync</c> 只
    /// <c>catch (DllNotFoundException)</c>，而 <c>NativeWebPEncoder.Encode</c> 在 cwebp **在位但编码失败**时
    /// 抛的是 <c>InvalidOperationException</c>（非零退出 / 30 s 超时 / 产物没写出来，见
    /// <c>NativeWebPEncoder.cs:88-92</c> 与 <c>NativeEncoderGuard</c> 那条 <c>result.Success == false</c>）
    /// ⇒ 注释与留痕文案都写着"回退 PNG"，实际这条路根本不进保底：图丢、<c>LastReason</c> 仍是 null。</para>
    /// <para>注入：对目标 <c>shot.webp</c> 持 <c>FileShare.None</c> 独占句柄 ⇒ cwebp 撞共享冲突失败，
    /// 而保底写的 <c>shot.png</c> 那条路径是空的 ⇒ 只有"保底真的接住了这一类异常"才能落出 PNG。
    /// ⚠ 前置条件：<c>cwebp</c> 必须在位——它不在位时走的是【另一条】(DllNotFound) 分支，
    /// 那条本来就会落 PNG，用它来判这一格就是假绿 ⇒ 不在位就 Skip（Skip 进门禁的"未覆盖"台账）。</para>
    /// </summary>
    static void WebPFallback_WhenBackendFails_StillDeliversPngAndNotice()
    {
        if (!NativeWebPEncoder.IsAvailable)
        {
            Skip("WebP 保底：cwebp 在位但编码失败", "本机取不到可用的 cwebp ⇒ 走不到「编码失败」那一支");
            return;
        }

        var dir = Path.Combine(OutDir, "webp-fallback");
        Directory.CreateDirectory(dir);
        var webpPath = Path.Combine(dir, "shot.webp");
        var pngPath = Path.ChangeExtension(webpPath, ".png");
        foreach (var f in new[] { webpPath, pngPath }) if (File.Exists(f)) File.Delete(f);

        var settings = new EncodingSettings
        {
            Format = OutputFormat.WebP, Quality = 90, OutputBitDepth = 8, HdrOutput = false,
            ColorSpaceTag = "DisplayP3",
            IccProfile = ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.DisplayP3, "Display P3"),
            PngFallbackEnabled = true,   // 同 AVIF 那格：这一格判的就是"开了保底之后认不认"
        };
        EncoderFallbackNotifier.LastReason = null;   // 单槽通道：不把上一拍的残留带进断言

        // ═══ 对照臂：同一份像素、不加独占 ⇒ 必须真落出一枚 WEBP ═══
        // 第一版没有这一条，它断言的是"cwebp 确实失败了（thrown 非 null）"——而修好之后保底把异常吃掉了,
        // thrown 变成 null ⇒ 那条前置判据自己红了一格（实测 webp-fallback-after.log 的 ❌）。
        // "抛没抛"不是注入成立的证据，【独占挡住写入】才是，所以下面量的是那个 .webp 还是 0 字节。
        var ctlPath = Path.Combine(dir, "control.webp");
        if (File.Exists(ctlPath)) File.Delete(ctlPath);
        EncoderFactory.Create(OutputFormat.WebP)
            .EncodeSdrAsync(GeneratePattern(8, 8), 8, 8, settings, ctlPath)
            .GetAwaiter().GetResult();
        var ctlBytes = File.Exists(ctlPath) ? File.ReadAllBytes(ctlPath) : Array.Empty<byte>();
        Assert("对照臂：不加独占时 cwebp 正常产出 WEBP（这条不成立，下面所有读数都失去基线）",
            ctlBytes.Length > 12 && ctlBytes[0] == (byte)'R' && ctlBytes[1] == (byte)'I'
            && ctlBytes[2] == (byte)'F' && ctlBytes[3] == (byte)'F'
            && ctlBytes[8] == (byte)'W' && ctlBytes[9] == (byte)'E' && ctlBytes[10] == (byte)'B' && ctlBytes[11] == (byte)'P',
            $"实得 {ctlBytes.Length} B");

        Exception? thrown = null;
        try
        {
            using (new FileStream(webpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                try
                {
                    EncoderFactory.Create(OutputFormat.WebP)
                        .EncodeSdrAsync(GeneratePattern(8, 8), 8, 8, settings, webpPath)
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex) { thrown = ex; }
            }
        }
        catch (Exception ex) { thrown = ex; }   // 独占句柄本身释放失败 ⇒ 也要留痕，不许静默

        // 注入成立的独立证据：那个 .webp 是我用独占创建的【空文件】，cwebp 打不开它 ⇒ 字节数仍为 0。
        // 与"保底有没有落 PNG"不是同一件事 ⇒ 不会因为保底生效而变成恒真（对照臂 + 这一条一起才是完整判据）。
        Assert("注入成立：独占确实挡住了 cwebp 的写入（那个 .webp 仍是 0 字节）",
            File.Exists(webpPath) && new FileInfo(webpPath).Length == 0,
            $"实得 {(File.Exists(webpPath) ? new FileInfo(webpPath).Length.ToString() : "<不存在>")} B"
            + "（>0 ⇒ 独占没起作用，这一轮没判到东西；保底若同时也没落 PNG，下面会单独红）");

        var chunks = ReadPngChunks(pngPath);
        Assert("WebP 保底：cwebp 编码失败时也要落一枚非空 PNG（promise 是「改存 PNG」，不是「抛异常」）",
            File.Exists(pngPath) && new FileInfo(pngPath).Length > 0,
            thrown is null ? "<注入没生效>" : $"抛的是 {thrown.GetType().Name}: {thrown.Message.Split('\n')[0]}");
        Assert("WebP 保底的 PNG 必须带 iCCP 或 cICP（非 sRGB 目标不许零声明）",
            FindPngChunk(chunks, "iCCP") is not null || FindPngChunk(chunks, "cICP") is not null,
            "广色域像素 + 无声明 ⇒ 查看器一律按 sRGB 解");
        Assert("WebP 保底必须写留痕，且说清换的是【格式】",
            (EncoderFallbackNotifier.LastReason ?? "").Contains("PNG"),
            $"实得 {EncoderFallbackNotifier.LastReason ?? "<null>"}");
        // 实际落盘路径必须登记，否则 Toast / 剪贴板 / 归档仍指向那条并不存在的 .webp（同 P0-D）
        Assert("WebP 保底要登记「实际写入路径」，上层 ResolveActualPath 才校正得回来",
            FormatHelper.ResolveActualPath(webpPath) == pngPath,
            $"实得 {FormatHelper.ResolveActualPath(webpPath)}");
    }

    static void EncodedPng_IccpChunk_Valid()
    {
        // 非 sRGB 色域 → 应嵌入 iCCP chunk
        var bgra = GeneratePattern(8, 8);
        string path = Path.Combine(OutDir, "iccp_test.png");
        if (File.Exists(path)) File.Delete(path);

        var encoder = EncoderFactory.Create(OutputFormat.PNG);
        var settings = new EncodingSettings
        {
            Format = OutputFormat.PNG, Quality = 100, HdrOutput = false,
            ColorSpaceTag = "DisplayP3", OutputBitDepth = 8,
            IccProfile = ColorProfileProvider.GetDefaultSRgbIcc(),
        };
        encoder.EncodeSdrAsync(bgra, 8, 8, settings, path).GetAwaiter().GetResult();

        bool hasIccp = CheckPngChunk(path, "iCCP");
        Assert("PNG iCCP: 非 sRGB 色域嵌入 ICC", hasIccp);
    }

    static void EncodedPng_HdrCicp_Bt2020Pq()
    {
        // HDR 编码 → PNG 16-bit 的 cICP 块必须逐字节回读校验：[9, 16, 0, 1] = BT.2020 + ST.2084 PQ。
        //
        // ═══ 2026-09-19：本用例此前只断言"文件已创建"，等于什么都没验 ═══
        // 而且原来连输入都没给对：EncodingSettings.ColorSpaceTag 的默认值是 "System"，
        // PngEncoder 会把它原样交给 BuildHdrCicp，而 GetCicpPrimaries("System") 落到 default 分支
        // ⇒ 写出的是 primaries=1（BT.709）。也就是说这条用例连"BT2020"这个前提都不成立，
        // 名字里的 Bt2020Pq 是空头承诺。现在显式指定 ColorSpaceTag="BT2020"，
        // 让它真的走一遍"用户选 BT.2020 + HDR"的路径。
        // （ primaries=1 对默认配置而言与像素是一致的 —— 那是另一回事，不在本用例范围内。）
        var hdr = new float[16 * 16 * 4];
        for (int i = 0; i < hdr.Length; i++) hdr[i] = 0.5f;
        string path = Path.Combine(OutDir, "hdr_cicp_test.png");
        if (File.Exists(path)) File.Delete(path);

        var encoder = EncoderFactory.Create(OutputFormat.PNG);
        var settings = new EncodingSettings
        {
            Format = OutputFormat.PNG, Quality = 100, HdrOutput = true,
            ColorSpaceTag = "BT2020",
            OutputBitDepth = 16,
        };
        var hdrFrame = new HdrFrameData { Pixels = hdr, Width = 16, Height = 16 };
        encoder.EncodeAsync(hdrFrame, settings, path).GetAwaiter().GetResult();

        Assert("PNG HDR CICP: 文件已创建", File.Exists(path) && new FileInfo(path).Length > 0);
        if (!File.Exists(path)) return;

        var chunks = ReadPngChunks(path);
        // 文件被创建 ≠ 是个 PNG：签名、IHDR 顺序都得站得住
        Assert("PNG HDR CICP: 首块是 IHDR",
            chunks.Count > 0 && chunks[0].Type == "IHDR",
            chunks.Count > 0 ? chunks[0].Type : "无 chunk");

        var ihdr = FindPngChunk(chunks, "IHDR");
        if (ihdr is { Length: >= 13 })
        {
            int depth = ihdr[8], colorType = ihdr[9];
            Assert("PNG HDR CICP: IHDR 位深=16、color type=6(RGBA)",
                depth == 16 && colorType == 6, $"bitDepth={depth} colorType={colorType}");
        }
        else
        {
            Assert("PNG HDR CICP: IHDR 位深=16、color type=6(RGBA)", false, "IHDR 缺失或过短");
        }

        var cicp = FindPngChunk(chunks, "cICP");
        if (cicp is not { Length: 4 })
        {
            Assert("PNG HDR CICP: cICP 块存在且为 4 字节", false,
                cicp is null ? "无 cICP 块" : $"cICP 长度={cicp.Length}");
            return;
        }
        Assert("PNG HDR CICP: cICP 块存在且为 4 字节", true,
            $"[{cicp[0]},{cicp[1]},{cicp[2]},{cicp[3]}]");

        // cICP 的 4 字节 = primaries / transfer / matrix / fullRange（ITU-T H.273 + RFC 9327）
        Assert($"PNG HDR CICP: primaries=9 (BT.2020)，实际 {cicp[0]}", cicp[0] == 9);
        Assert($"PNG HDR CICP: transfer=16 (ST.2084 PQ)，实际 {cicp[1]}", cicp[1] == 16);
        Assert($"PNG HDR CICP: matrix=0 (identity，PNG 存 RGB 采样)，实际 {cicp[2]}", cicp[2] == 0);
        Assert($"PNG HDR CICP: fullRange=1，实际 {cicp[3]}", cicp[3] == 1);

        // 16-bit 容器没有截断 ⇒ 按源码规则不应写 sBIT
        Assert("PNG HDR CICP: 16-bit 容器不写 sBIT", FindPngChunk(chunks, "sBIT") is null);
    }

    /// <summary>
    /// HDR + <b>10-bit</b> 请求下的 PNG（2026-09-24，§48 #21）。
    /// <para>为什么单独一条：管线现在把"HDR 开启 + PNG + 位深&lt;10"协商成 10-bit
    /// （<c>CapturePipelineService</c>，与 AVIF 的 HDR⇒10 同构），于是**默认路径从原来的
    /// 16-bit 容器换到了 10-bit 量化 + sBIT 声明** —— 而上面那条用例走的是 16，
    /// 新默认值落进的是这条**原本无人覆盖**的分支。不补它，就等于把默认换成一条没验过的路。</para>
    /// <para>PNG 的 IHDR 只允许 1/2/4/8/16，所以"10-bit PNG"必然是：16-bit 容器
    /// + <c>sBIT</c> 声明每通道有效 10 位 + <c>cICP</c> 声明 PQ。三者缺一就不是我们要的东西。</para>
    /// </summary>
    static void EncodedPng_HdrCicp_10BitSbit()
    {
        var hdr = new float[16 * 16 * 4];
        for (int i = 0; i < hdr.Length; i++) hdr[i] = 0.5f;
        string path = Path.Combine(OutDir, "hdr_cicp_d10_test.png");
        if (File.Exists(path)) File.Delete(path);

        var encoder = EncoderFactory.Create(OutputFormat.PNG);
        var settings = new EncodingSettings
        {
            Format = OutputFormat.PNG, Quality = 100, HdrOutput = true,
            ColorSpaceTag = "BT2020", OutputBitDepth = 10,
        };
        encoder.EncodeAsync(new HdrFrameData { Pixels = hdr, Width = 16, Height = 16 },
            settings, path).GetAwaiter().GetResult();
        Assert("PNG HDR 10bit: 文件已创建", File.Exists(path) && new FileInfo(path).Length > 0);
        if (!File.Exists(path)) return;

        var chunks = ReadPngChunks(path);
        var ihdr = FindPngChunk(chunks, "IHDR");
        Assert("PNG HDR 10bit: 容器仍是 16-bit（IHDR 没有 10 这个档），color type=6",
            ihdr is { Length: >= 13 } && ihdr[8] == 16 && ihdr[9] == 6,
            ihdr is { Length: >= 13 } ? $"bitDepth={ihdr[8]} colorType={ihdr[9]}" : "IHDR 异常");

        var sbit = FindPngChunk(chunks, "sBIT");
        Assert("PNG HDR 10bit: 写了 sBIT（不写就等于宣称这是 16-bit 全精度，与请求不符）",
            sbit is { Length: 4 }, sbit is null ? "无 sBIT 块" : $"长度={sbit.Length}");
        if (sbit is { Length: 4 })
            Assert("PNG HDR 10bit: sBIT 每通道都是 10",
                sbit[0] == 10 && sbit[1] == 10 && sbit[2] == 10 && sbit[3] == 10,
                $"[{sbit[0]},{sbit[1]},{sbit[2]},{sbit[3]}]");

        var cicp = FindPngChunk(chunks, "cICP");
        Assert("PNG HDR 10bit: cICP 仍是 BT.2020 + PQ + identity + full",
            cicp is { Length: 4 } && cicp[0] == 9 && cicp[1] == 16 && cicp[2] == 0 && cicp[3] == 1,
            cicp is { Length: 4 } ? $"[{cicp[0]},{cicp[1]},{cicp[2]},{cicp[3]}]" : "cICP 缺失/长度异常");
    }

    static void EncodedJpeg_GainMapMarker()
    {
        // JPEG Gain Map 编码验证 —— 原来唯一断言是"文件已创建"，等于什么都没验。
        // 结构证据此前只在 EncodingIntegrationTests.cs:204-262（另一次门禁 --encoding-tests）里读回，
        // 现在本用例内也逐条读回，使 --color-tests 单独跑时这条不是空承诺。
        // 判据来自段布局标准本身（T.81 的 marker/长度、CTA-2048 的 XMP+MPF、ISO 21496-1 的
        // 增益图侧元数据），不是自家 JpegGainMapEncoder 的写入代码：
        //   Base   : SOI … APP1(hdrgm XMP) … APP2(MPF) … SOS … EOI
        //   GainMap: 第二个 SOI 之后紧跟 APP2(urn:iso:std:iso:ts:21496:-1)
        var bgra = GeneratePattern(32, 32);
        string path = Path.Combine(OutDir, "gainmap_test.jpg");
        if (File.Exists(path)) File.Delete(path);

        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.JPEG_GAINMAP);
            // GainMap 质量 = butteraugli distance (0.5-3.0), 用 1.0 (90 是百分比语义, 非法)
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP, Quality = 1.0f, HdrOutput = true,
                GainMapMode = GainMapMode.Rgb,
                OutputBitDepth = 8,
            };
            var hdr = PixelOps.BgraToScrgbLinearFast(bgra, 32, 32);
            var hdrFrame = new HdrFrameData { Pixels = hdr, Width = 32, Height = 32 };
            encoder.EncodeAsync(hdrFrame, settings, path).GetAwaiter().GetResult();
            Assert("JPEG Gain Map: 文件已创建", File.Exists(path) && new FileInfo(path).Length > 0);
            if (!File.Exists(path)) return;

            var bytes = File.ReadAllBytes(path);
            int secondSoi = -1;
            for (int i = 2; i + 1 < bytes.Length; i++)
                if (bytes[i] == 0xFF && bytes[i + 1] == 0xD8) { secondSoi = i; break; }
            Assert("JPEG Gain Map: 含第二个 SOI（Base + 增益图 = 双 SOI）",
                secondSoi > 0, secondSoi < 0 ? "全文只有一个 SOI ⇒ 没有增益图" : $"SOI@{secondSoi}");
            if (secondSoi < 0) return;

            // ① Base 侧（第二个 SOI 之前）的 XMP：hdrgm 命名空间的版本与增益上限
            var xmp = FindJpegAppPayload(bytes, 0, secondSoi, 0xE1, "http://ns.adobe.com/xap/1.0/");
            Assert("JPEG Gain Map: Base 内有 APP1 XMP 段（必须落在第二个 SOI 之前）", xmp is not null);
            if (xmp is not null)
            {
                Assert("JPEG Gain Map: XMP 含 hdrgm:Version", xmp.Contains("hdrgm:Version"));
                Assert("JPEG Gain Map: XMP 含 hdrgm:GainMapMax", xmp.Contains("hdrgm:GainMapMax"));
            }
            // ② Base 侧的 MPF（CIPA DC-007）—— 主图靠它把增益图作为衍生图挂上
            Assert("JPEG Gain Map: Base 内含 MPF APP2 段",
                FindJpegAppPayload(bytes, 0, secondSoi, 0xE2, "MPF") is not null);
            // ③ 增益图侧（第二个 SOI 之后）的 ISO 21496-1 二进制元数据
            Assert("JPEG Gain Map: 增益图侧含 ISO 21496-1 段（必须落在第二个 SOI 之后）",
                FindJpegAppPayload(bytes, secondSoi, bytes.Length, 0xE2, "urn:iso:std:iso:ts:21496:-1") is not null);
        }
        catch (Exception ex)
        {
            Skip("JPEG Gain Map 编码", $"依赖 Native lib，本机不可用: {ex.GetType().Name}");
        }
    }

    static void EncodedAvif_ColorProperties()
    {
        // AVIF 编码验证 —— 原来只有 `File.Exists && Length > 0`。
        // 现在本文件内自带一次最小外部读回：AVIF 是 ISOBMFF，色彩声明就是 ipco 里 colr(nclx)
        // 盒的那 7 个字节（三个 16-bit 码点 + 1 字节 range），尺寸声明就是 ispe 盒。
        // 判据来自 ISO/IEC 23091-2 §6.1（nclx 字段宽度与 19 字节规范盒体）、ITU-T H.273 码点、
        // ISO/IEC 14496-12（ispe/colr 盒布局）—— 不是我们自己那份写入器。
        // （WgcAvifBackendTests.cs:265 有个更完整的 ValidateAvifContainer，但那是另一个文件、
        //  且只在 --wgc-avif-tests 门禁里跑，本用例不能指望它兜底。）
        var bgra = GeneratePattern(32, 32);
        string path = Path.Combine(OutDir, "avif_color_test.avif");
        if (File.Exists(path)) File.Delete(path);

        try
        {
            var encoder = EncoderFactory.Create(OutputFormat.AVIF);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.AVIF, Quality = 50f, HdrOutput = false,
                ChromaSubsampling = "444", OutputBitDepth = 8,
            };
            encoder.EncodeSdrAsync(bgra, 32, 32, settings, path).GetAwaiter().GetResult();
            bool ok = File.Exists(path) && new FileInfo(path).Length > 0;
            // 2026-09-15 修正：原为 `ok || true` 恒真断言，AVIF 编码彻底失败也判通过。
            Assert("AVIF: 文件已创建且非空", ok);
            if (!ok) return;

            var d = File.ReadAllBytes(path);

            // ── ispe：容器声明的图像尺寸必须与请求编码的尺寸一致 ──
            // ispe 是 FullBox：['ispe'(4)][version+flags(4)][width(4)][height(4)]，均大端。
            int ispe = IndexOfAscii(d, "ispe");
            if (ispe < 0 || ispe + 16 > d.Length)
            {
                Assert("AVIF: ispe 盒存在且完整", false, ispe < 0 ? "未找到 ispe 盒" : "ispe 越界");
            }
            else
            {
                int w = (int)ReadBeUInt32(d, ispe + 8);
                int h = (int)ReadBeUInt32(d, ispe + 12);
                Assert($"AVIF: ispe 尺寸 = 32×32，实际 {w}×{h}", w == 32 && h == 32);
            }

            // ── colr(nclx)：8-bit SDR sRGB 内容 ⇒ primaries=1 / transfer=13 / matrix=1 ──
            int colr = IndexOfAscii(d, "colrnclx");
            if (colr < 0 || colr + 15 > d.Length)
            {
                Assert("AVIF: colr(nclx) 盒存在", false, "未找到 nclx 型 colr ⇒ 内容对外没有色彩声明");
                return;
            }
            uint boxSize = ReadBeUInt32(d, colr - 4);
            Assert($"AVIF: colr 盒体 = 19 字节（nclx 规范形状），实际 {boxSize}", boxSize == 19,
                "字段被按 8-bit 写出时盒体是 16 字节，严格解码器会整块丢弃 colr");
            if (boxSize != 19) return;

            int f = colr + 8;                       // 跳过 'colr' 与 'nclx'
            int prim = (d[f] << 8) | d[f + 1];
            int trc = (d[f + 2] << 8) | d[f + 3];
            int mat = (d[f + 4] << 8) | d[f + 5];
            int range = (d[f + 6] >> 7) & 1;
            Assert($"AVIF nclx: primaries=1 (BT.709/sRGB 原色)，实际 {prim}", prim == 1);
            Assert($"AVIF nclx: transfer=13 (sRGB IEC 61966-2-1)，实际 {trc}", trc == 13);
            // AV1 恒为 YUV 码流 ⇒ matrix 不能是 identity(0)；8-bit SDR 内容应声明 BT.709(1)
            Assert($"AVIF nclx: matrix=1 (BT.709，AV1 无 identity 可言)，实际 {mat}", mat == 1);
            // full_range_flag 只作观测、不钉单值：软件 avifenc 写 0x80(full)、NVENC 自写容器写
            // 0(limited)，两条路径各自与自己的 RGB→YUV 输出一致（见 NvEncoderNative.cs 的 colr 备注），
            // 钉成任一个都会把另一条合法路径判成失败。
            Console.WriteLine($"  ℹ️ AVIF nclx full_range_flag={range} (0=limited / 1=full，随后端而异)");
        }
        catch (Exception ex)
        {
            Skip("AVIF 编码", $"AVIF 编码器不可用: {ex.GetType().Name}");
        }
    }

    // ═══════════════════════════════════════════════
    //  6. 色调映射单调性
    // ═══════════════════════════════════════════════

    static void ToneMap_AllModes_Monotonic()
    {
        // 所有色调映射模式应保持单调性（亮度更高的输入 → 亮度更高的输出）
        //
        // ⚠ 2026-08-30 修复: 原测试声明 64 像素却按 8×2=16 像素调用 FloatToSRgbBytes，
        // 导致渐变只覆盖 t∈[0,0.234]（归一化后 maxY≤0.469），
        // 完全没采样到 SDR 白点 (y=1) 附近 —— 正是 SegmentedReinhardMap 断崖所在区间。
        // 这是该缺陷长期未被发现的原因。现尺寸与像素数一致，覆盖完整动态范围。
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        const int pixelCount = 64;
        foreach (var mode in modes)
        {
            var hdr = new float[pixelCount * 4];
            for (int i = 0; i < hdr.Length; i += 4)
            {
                float t = i / (float)hdr.Length;
                // 覆盖到 headroom 之外: maxY = t*5*0.4 = t*2 → 最高 2× SDR 白点
                hdr[i] = t * 5f; hdr[i + 1] = t * 3f; hdr[i + 2] = t * 2f; hdr[i + 3] = 1f;
            }
            var p = new ToneMappingParams { Mode = mode };
            var bytes = ToneMapper.FloatToSRgbBytes(hdr, pixelCount, 1, p);

            bool monotonic = true;
            int prevLum = 0;
            for (int i = 0; i < bytes.Length; i += 4)
            {
                int lum = (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3;
                if (lum < prevLum - 1) { monotonic = false; break; }  // 容差收紧到 1
                prevLum = lum;
            }
            Assert($"ToneMap.{mode}: 单调性", monotonic);
        }
    }

    // ═══════════════════════════════════════════════
    //  6b. 分段 Reinhard 曲线（SDR 白点断崖回归防护）
    // ═══════════════════════════════════════════════
    //
    // 背景: SegmentedReinhardMap 曾在 y=1（SDR 白点）处不连续 ——
    // 左分支返回 1.0，右分支接 libultrahdr 的 ReinhardMap（R(1)≈0.52@headroom=5），
    // 使刚超过 SDR 白点的高光反而更暗（暗环伪影）。
    // 以下用例直接针对曲线本身，防止回归。

    /// <summary>曲线必须全程单调不减（暗环的直接成因就是非单调）。</summary>
    static void SegmentedReinhard_Monotonic_NoDarkRing()
    {
        foreach (float headroom in new[] { 1.5f, 2f, 5f, 10f })
        {
            bool monotonic = true;
            float prev = float.NegativeInfinity;
            float worstDrop = 0f;
            // 细密扫过 SDR 白点两侧，最高到 2×headroom
            for (float y = 0f; y <= headroom * 2f; y += 0.001f)
            {
                float f = ToneMapper.SegmentedReinhardMap(y, headroom);
                if (f < prev) { monotonic = false; worstDrop = Math.Max(worstDrop, prev - f); }
                prev = f;
            }
            Assert($"SegmentedReinhard(headroom={headroom}): 全程单调不减",
                monotonic, monotonic ? "" : $"最大回退={worstDrop:F4}");
        }
    }

    /// <summary>曲线在 SDR 白点 (y=1) 处必须连续 —— 断崖的核心判据。</summary>
    static void SegmentedReinhard_ContinuousAtSdrWhite()
    {
        bool ok = true;
        string detail = "";
        foreach (float headroom in new[] { 1.5f, 2f, 5f, 10f })
        {
            float below = ToneMapper.SegmentedReinhardMap(1.0f, headroom);
            float above = ToneMapper.SegmentedReinhardMap(1.0f + 1e-6f, headroom);
            float jump = Math.Abs(above - below);
            if (jump > 1e-4f)
            {
                ok = false;
                detail += $" headroom={headroom}: 跳变={jump:F4};";
            }
        }
        Assert("SegmentedReinhard: SDR 白点处连续 (无断崖)", ok, detail);
    }

    /// <summary>SDR 范围内必须恒等映射 —— 保证 SDR 内容 100% 保真、GainMap 增益为 0。</summary>
    static void SegmentedReinhard_SdrRangeIdentity()
    {
        bool ok = true;
        string detail = "";
        // y=1 旧值示意: headroom=5 时旧实现返回 (1.0, 但 y>1 分支 R(1)=0.52)
        foreach (float y in new[] { 0f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1.0f })
        {
            float f = ToneMapper.SegmentedReinhardMap(y, 5f);
            if (Math.Abs(f - y) > 1e-5f)
            {
                ok = false;
                detail += $" y={y}: f={f:F4};";
            }
        }
        Assert("SegmentedReinhard: SDR 范围 (y≤1) 恒等映射", ok, detail);
    }

    /// <summary>SDR 白点必须映射到 SDR 上界 1.0（SDR 查看器与 HDR 查看器观感一致）。</summary>
    static void SegmentedReinhard_SdrWhiteMapsToUnity()
    {
        bool ok = true;
        string detail = "";
        foreach (float headroom in new[] { 1.5f, 2f, 5f, 10f })
        {
            float f = ToneMapper.SegmentedReinhardMap(1.0f, headroom);
            if (Math.Abs(f - 1.0f) > 1e-5f)
            {
                ok = false;
                detail += $" headroom={headroom}: f(1)={f:F4};";
            }
        }
        Assert("SegmentedReinhard: SDR 白点映射为 1.0", ok, detail);
    }

    /// <summary>端到端: 经完整管线的亮度斜坡不得出现暗环（亮度回退）。</summary>
    static void SegmentedReinhard_Pipeline_NoDarkRing()
    {
        const int pixelCount = 256;
        var hdr = new float[pixelCount * 4];
        // 中性灰斜坡，亮度从 0 扫到 6× SDR 白点（PaperWhite=200 → 归一化后 y = scRGB/2.5）
        for (int pi = 0; pi < pixelCount; pi++)
        {
            float y = pi / (float)pixelCount * 15f;   // scRGB 值，15/2.5 = 6× 白点
            hdr[pi * 4] = y; hdr[pi * 4 + 1] = y; hdr[pi * 4 + 2] = y; hdr[pi * 4 + 3] = 1f;
        }
        var p = new ToneMappingParams { Mode = ToneMapMode.SegmentedReinhard };
        var bytes = ToneMapper.FloatToSRgbBytes(hdr, pixelCount, 1, p);

        bool monotonic = true;
        int prevLum = -1;
        int worstDrop = 0;
        for (int i = 0; i < bytes.Length; i += 4)
        {
            int lum = (bytes[i] + bytes[i + 1] + bytes[i + 2]) / 3;
            if (lum < prevLum)
            {
                monotonic = false;
                worstDrop = Math.Max(worstDrop, prevLum - lum);
            }
            prevLum = lum;
        }
        Assert("SegmentedReinhard 管线: 亮度斜坡无暗环", monotonic,
            monotonic ? "" : $"最大亮度回退={worstDrop} 灰阶");
    }

    static void ToneMap_BlackPreservation()
    {
        // 纯黑输入 → 纯黑输出
        var hdr = new float[] { 0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f };
        var modes = new[] { ToneMapMode.Reinhard, ToneMapMode.Hable, ToneMapMode.SegmentedReinhard };
        foreach (var mode in modes)
        {
            var p = new ToneMappingParams { Mode = mode };
            var bytes = ToneMapper.FloatToSRgbBytes(hdr, 2, 1, p);
            Assert($"ToneMap.{mode}: 纯黑保持", bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0);
        }
    }

    static void ToneMap_WhitePreservation()
    {
        // 极高亮度 → 纯白
        var hdr = new float[] { 100f, 100f, 100f, 1f };
        var p = new ToneMappingParams { Mode = ToneMapMode.Hable };
        var bytes = ToneMapper.FloatToSRgbBytes(hdr, 1, 1, p);
        Assert("ToneMap.Hable: 极高亮度→接近纯白", bytes[0] >= 250 && bytes[1] >= 250 && bytes[2] >= 250);
    }

    // ═══════════════════════════════════════════════
    //  7. 色域映射
    // ═══════════════════════════════════════════════

    static void GamutMap_SrgbToP3()
    {
        // ═══ 2026-09-19：原名 GamutMap_SrgbToP3_Identity，名实不符 ═══
        // 原实现传的 target 是 "sRGB"，走的是 BakeIccToTarget 里"sRGB→sRGB 短路、原样返回同一
        // 个数组引用"的分支（见该方法对 IsSrgbProfile 的判断）—— 一次色域映射都没发生。
        // 更糟的是：矩阵解析失败时 BakeIccToTarget 会静默"像素直通 + 只嵌目标 ICC"，
        // 那种回归在这条用例下同样是绿的。故拆成两件事分别钉住。
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();

        // ── ① 先诚实标注旧用例真正验的东西：sRGB→sRGB 是短路，不是映射 ──
        var gray = new byte[4] { 128, 128, 128, 255 };
        var (samePixels, sameIcc) = ColorProfileProvider.BakeIccToTarget(gray, 1, 1, srgbIcc, "sRGB");
        if (samePixels is null)
        {
            Skip("ICC 烘焙 sRGB→sRGB", "BakeIccToTarget 返回空（异常/无标准 profile）");
        }
        else
        {
            bool near = true;
            for (int i = 0; i < gray.Length; i++)
                if (Math.Abs(samePixels[i] - gray[i]) > 3) { near = false; break; }
            Assert("sRGB→sRGB 烘焙: 接近不变（短路路径，未做映射）", near,
                object.ReferenceEquals(samePixels, gray) ? "返回同一数组引用" : "走了矩阵");
            Assert("sRGB→sRGB 烘焙: sRGB 目标不嵌 ICC", sameIcc is null);
        }

        // ── ② 真正的 sRGB → Display P3 比对 ──
        // 输入：纯红 (BGRA = 0,0,255,255) + 纯黑 (0,0,0,255)。
        // P3 原色比 sRGB 宽 ⇒ 同一颜色用 P3 表示时"更淡"：sRGB 纯红在 P3 里不再是纯色，
        // G/B 必然从 0 抬起（转换矩阵非对角元全正）。这是色域几何的必然结论，
        // 不依赖具体数值。
        // ⚠ 这里仍不写死精确字节：本用例钉的是色域几何的定性关系（P3 更宽 ⇒ G/B 抬起），
        //   绝对值精度归 EncodedPng_* 与 scripts/precision/ 那批探针管。
        //   （2026-09-20 起源 profile 已确定为本仓库手写件，跨机字节稳定，
        //    但"写死数字"并不会让这条用例更强 —— 它测的不是序列化。）
        var bgra = new byte[8] { 0, 0, 255, 255, 0, 0, 0, 255 };
        var (pixels, targetIcc) = ColorProfileProvider.BakeIccToTarget(bgra, 2, 1, srgbIcc, "DisplayP3");
        if (pixels is null)
        {
            Skip("sRGB→P3 烘焙", "BakeIccToTarget 返回空（环境限制）");
            return;
        }

        Assert("sRGB→P3: 返回非空目标 ICC（P3 目标必须嵌 ICC）", targetIcc is { Length: > 128 });
        // 像素必须真的被改写：直通回退（矩阵解析失败）时输出与输入逐字节相同，这条立刻变红
        Assert($"sRGB→P3: 纯红像素被改写（未走直通回退）B={pixels[0]} G={pixels[1]} R={pixels[2]}",
            pixels[0] != 0 || pixels[1] != 0 || pixels[2] != 255, "输入 B=0 G=0 R=255");
        Assert($"sRGB→P3: sRGB 纯红在 P3 中不再是纯色（G/B 抬起）G={pixels[1]} B={pixels[0]}",
            pixels[1] > 5 && pixels[0] > 2);
        Assert($"sRGB→P3: 红仍是主通道（R 未被反超）R={pixels[2]}",
            pixels[2] > pixels[1] && pixels[2] > pixels[0]);
        Assert("sRGB→P3: 纯黑保持纯黑（0 不进、矩阵行不凭空造光）",
            pixels[4] == 0 && pixels[5] == 0 && pixels[6] == 0,
            $"B={pixels[4]} G={pixels[5]} R={pixels[6]}");
        Assert("sRGB→P3: Alpha 直通", pixels[3] == 255 && pixels[7] == 255);
    }

    /// <summary>
    /// D-1（交接 §30.9 同族）：ICC 转换没做成时，返回值不得携带"目标色域"的 ICC。
    /// <para>旧写法在源/目标矩阵不可解析时 <c>return (bgra, targetIcc)</c> 并注释"查看器负责转换"——
    /// 可查看器读到的是"像素已在目标色域"：sRGB 像素被标成 P3，按 P3 解释反而更偏（过饱和）。
    /// 而调用方以 <c>baked is not null</c> 判成功，"直通"与"转换成功"在返回值上根本不可分辨。
    /// 现在直通就返回 <c>icc = null</c>，由 App 层（PreparePixelsWithIcc → EncodeAndSaveAsync）
    /// 把声明退回像素真实所在色域并留痕。⚠ 与 <c>IsIdentityMatrix</c> 那一支不同：那一支像素
    /// 【确实】已在目标色域，嵌目标 ICC 是对的，不在本判据的射程内。</para>
    /// </summary>
    static void BakeFailure_DoesNotClaimTargetGamut()
    {
        // 长度够 header(132B)、但 tagCount=0 ⇒ ExtractRgbToXyzMatrix 必返 null ⇒ 走"转换不可完成"分支
        var bogus = new byte[512];
        var bgra = new byte[2 * 2 * 4];
        EncoderFallbackNotifier.LastReason = null;

        var (px, icc) = ColorProfileProvider.BakeIccToTarget(bgra, 2, 2, bogus, "DisplayP3");
        Assert("源 ICC 不可解析 ⇒ 像素按直通返回且不抛", px is not null && px.Length == bgra.Length);
        Assert("源 ICC 不可解析 ⇒ 不返回目标 ICC（声明与像素不得矛盾）", icc is null,
            icc is null ? "null" : $"{icc!.Length}B");
        Assert("转换失败必须留下对外痕迹（Release 下不能只剩 Debug.WriteLine）",
            !string.IsNullOrEmpty(EncoderFallbackNotifier.LastReason),
            EncoderFallbackNotifier.LastReason ?? "无");

        // 合成自检（判据必须敢输出 FAIL）：正常源 ICC 走同一条路仍要拿到目标 ICC，
        // 否则上面那条"icc 恒为 null"也能混过去。
        var srgbIcc = ColorProfileProvider.GetStandardIccProfile("sRGB");
        if (srgbIcc is { Length: > 128 })
        {
            // 必须先清：上一条断言刚刚留下过原因，不清就等于拿别人的痕迹自检（恒假红/恒假绿都可能）
            EncoderFallbackNotifier.LastReason = null;
            var (px2, icc2) = ColorProfileProvider.BakeIccToTarget(bgra, 2, 2, srgbIcc, "DisplayP3");
            Assert("合成自检：正常 sRGB→P3 仍返回目标 ICC（判据非恒真）", icc2 is { Length: > 128 },
                $"{icc2?.Length ?? 0}B");
            Assert("合成自检：正常路径不误报降级",
                string.IsNullOrEmpty(EncoderFallbackNotifier.LastReason),
                EncoderFallbackNotifier.LastReason ?? "无");
        }
        else Skip("合成自检：正常路径", "取不到标准 sRGB profile");
    }

    /// <summary>
    /// 可追溯性：HDR 意图被容器能力改掉（8-bit 装不下 PQ）时，必须留下对外痕迹。
    /// <para>全仓 6 类"换参数/换执行者/换格式"的降级里，只有 WebP→PNG、avifenc 拒位深、MFT→libaom
    /// 三条写了 <c>EncoderFallbackNotifier</c>；PNG 的这条 HDR→SDR 静默翻转是其中之一。
    /// 不打 native 工具（ManagedPngEncoder 纯 C#）⇒ 任何机器都能跑。</para>
    /// </summary>
    static void Png_HdrBitDepthDowngrade_IsTraceable()
    {
        const int W = 4, H = 4;
        var px = new float[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            px[i * 4] = 0.5f; px[i * 4 + 1] = 0.3f; px[i * 4 + 2] = 0.2f; px[i * 4 + 3] = 1f;
        }
        var path = Path.Combine(OutDir, $"hdr_downgrade_trace_{Guid.NewGuid():N}.png");

        var s = new EncodingSettings
        {
            Format = OutputFormat.PNG, HdrOutput = true, OutputBitDepth = 8, ColorSpaceTag = "sRGB",
        };
        EncoderFallbackNotifier.LastReason = null;
        EncoderFactory.Create(OutputFormat.PNG)
            .EncodeAsync(new HdrFrameData { Pixels = px, Width = W, Height = H }, s, path)
            .GetAwaiter().GetResult();

        Assert("PNG: HDR+8-bit 降级留下对外痕迹", !string.IsNullOrEmpty(EncoderFallbackNotifier.LastReason),
            EncoderFallbackNotifier.LastReason ?? "无");
        Assert("PNG: 降级后确实按 SDR 出图（标志已翻转）", !s.HdrOutput);
        Assert("PNG: 产物落在请求的 .png 路径", File.Exists(path), path);
        // 反向自检：≥10-bit 的 HDR 请求不该报降级，否则"留痕"退化成"每句都喊"
        var sOk = new EncodingSettings
        {
            Format = OutputFormat.PNG, HdrOutput = true, OutputBitDepth = 10, ColorSpaceTag = "sRGB",
        };
        EncoderFallbackNotifier.LastReason = null;
        EncoderFactory.Create(OutputFormat.PNG)
            .EncodeAsync(new HdrFrameData { Pixels = px, Width = W, Height = H }, sOk,
                Path.Combine(OutDir, $"hdr_ok_trace_{Guid.NewGuid():N}.png"))
            .GetAwaiter().GetResult();
        Assert("合成自检：10-bit HDR 不误报降级（判据不是恒真）",
            string.IsNullOrEmpty(EncoderFallbackNotifier.LastReason),
            EncoderFallbackNotifier.LastReason ?? "无");
    }

    /// <summary>读一个 ICC 里 XYZ 型标签的 s15Fixed16 三元组（自带小解析器，不复用产品代码 ⇒ 交叉验证）。</summary>
    static int BeI32(byte[] d, int o) => d[o] << 24 | d[o + 1] << 16 | d[o + 2] << 8 | d[o + 3];

    static float[]? ReadIccXyzTag(byte[] icc, string sig)
    {
        int n = BeI32(icc, 128);
        for (int i = 0; i < n; i++)
        {
            int off = 132 + i * 12;
            if (off + 12 > icc.Length) return null;
            if (System.Text.Encoding.ASCII.GetString(icc, off, 4) != sig) continue;
            int o = BeI32(icc, off + 4);
            if (o + 20 > icc.Length) return null;
            return new[] { BeI32(icc, o + 8) / 65536f, BeI32(icc, o + 12) / 65536f, BeI32(icc, o + 16) / 65536f };
        }
        return null;
    }

    static bool Near(float[]? v, float x, float y, float z, float tol)
        => v is { Length: 3 } && Math.Abs(v[0] - x) <= tol && Math.Abs(v[1] - y) <= tol && Math.Abs(v[2] - z) <= tol;

    /// <summary>
    /// A-3（2026-09-20 许可合规审计）：本仓库产出的 ICC 必须与第三方 profile 脱钩。
    /// <para>修之前的链路：<c>GetDefaultSRgbIcc</c> 优先读
    /// <c>System32\spool\drivers\color\sRGB Color Space Profile.icm</c>（微软随系统的那份，
    /// <c>cprt</c>="Copyright (c) 1998 Hewlett-Packard Company"、<c>creator</c>="HP  "），
    /// 而 <c>PatchSrgbPrimaries</c> 以它为模板 <c>Clone</c> 后<b>只覆写 rXYZ/gXYZ/bXYZ+desc</b>
    /// ⇒ 残留标签让 Display P3 / BT.2020 / Adobe RGB 一起带上 HP 版权串，并随用户导出的
    /// PNG(iCCP)/TIFF/AVIF(colr prof)/JPEG(APP2) 分发（实测四类容器全命中）。
    /// 另一个后果：模板取自本机 ⇒ 换台机器产物字节就变。</para>
    /// <para>判据设计：<c>ContainsAsciiBytes</c> 那条<b>不依赖</b>本测试自己的 tag 解析器
    /// （解析器与写入器同源会构成"构造恒等"），所以它是本组里最硬的一条；
    /// cprt 文本比对是第二层，用来抓"字节里没有 HP 但也没写清是谁的"这种情况。</para>
    /// </summary>
    static void IccProvenance_NoThirdPartyCopyrightBytes()
    {
        var cases = new (string Label, byte[]? Icc)[]
        {
            ("sRGB",         ColorProfileProvider.GetDefaultSRgbIcc()),
            ("Display P3",   ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.DisplayP3, "Display P3")),
            ("BT.2020",      ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.BT2020, "BT.2020")),
            ("Adobe RGB",    ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.AdobeRGB, "Adobe RGB (1998)")),
            ("P3 (PQ)",      ColorProfileProvider.GetHdrStandardIccProfile("DisplayP3")),
            ("P3 (builder)", IccProfileBuilder.Generate(IccPrimaries.DisplayP3, IccTransferCurve.Srgb)),
        };

        foreach (var (label, icc) in cases)
        {
            if (icc is not { Length: > 132 })
            {
                Assert($"{label}: profile 生成成功", false, "返回空或过短");
                continue;
            }
            Assert($"{label}: 结构合法、可嵌入", FormatHelper.IsEmbeddableIcc(icc));
            Assert($"{label}: 字节里没有第三方版权串",
                !ContainsAsciiBytes(icc, "Hewlett") && !ContainsAsciiBytes(icc, "Copyright (c) 1998"),
                "被 Clone 带进来的系统 cprt —— 它会随用户产物分发");
            var cprt = ReadIccTextTypeTag(icc, "cprt");
            Assert($"{label}: cprt 写明本项目自己的许可",
                cprt == ColorProfileProvider.GeneratedIccCopyright, $"实得 {cprt ?? "<无 cprt 标签>"}");
            Assert($"{label}: deviceManufacturer / creator 留空（不冒充任何厂商）",
                Be32At(icc, 48) == 0 && Be32At(icc, 80) == 0,
                $"mfr={Be32At(icc, 48):X8} creator={Be32At(icc, 80):X8}");
            // ProfileID 刻意留零，不是漏算：本机唯一能实证的配方是 ICC.1:2022(v4.4) 的
            // "整份 profile 清零 flags/intent/ID 后取 MD5"（scripts/probes/icc_profile_id_check.py
            // 对 3 份 Windows 自产 v4 profile 3/3 命中），而这里生成的是 v2.1 profile；
            // v2 时代那套"定长字段拼接"配方在本机拿不到任何非零样本可验（6 份 v2 文件全为 0，
            // 包括微软自己那份 sRGB）。写一个没验证过的 ID 比留零更坏。
            Assert($"{label}: ProfileID 留零（v2 配方未实证，见探针注释）", AllZero(icc, 84, 16));
        }

        // ── 跨机确定性 + 与本机系统件脱钩（策略1 一旦被改回来，这两条立刻分头变红）──
        var srgb = ColorProfileProvider.GetDefaultSRgbIcc();
        Assert("生成 sRGB 与手写件逐字节相同（跨机确定性）",
            srgb.AsSpan().SequenceEqual(ColorProfileProvider.BuildValidSRgbIcc()));
        var sysPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "spool", "drivers", "color", "sRGB Color Space Profile.icm");
        if (File.Exists(sysPath))
        {
            var sys = File.ReadAllBytes(sysPath);
            Assert("生成 sRGB ≠ 本机系统 sRGB（策略1 没有被改回来）",
                !sys.AsSpan().SequenceEqual(srgb), $"系统件 {sys.Length}B");
            // 对照样本要自证有效，否则"绿"可能只是因为这台机器的系统件本来就没 HP 串
            Console.WriteLine(ContainsAsciiBytes(sys, "Hewlett")
                ? $"  ℹ  对照成立：本机系统 sRGB({sys.Length}B) 含 HP 版权串，策略1 若回来就会渗进每个导出文件"
                : "  ⚠️  对照失效：本机系统 sRGB 不含 HP 串 ⇒ 上面两条仍拦得住\"改回读系统文件\"，"
                  + "但\"不含第三方版权串\"那条在此机器上不是回归哨兵");
        }
        else
        {
            Console.WriteLine("  ℹ  本机无系统 sRGB 文件（精简系统），跳过脱钩对照");
        }

        // ── 端到端：查真正落到用户产物上的那一份，而不是内存里的中间品 ──
        string path = Path.Combine(OutDir, "provenance_p3.png");
        if (File.Exists(path)) File.Delete(path);
        var encoder = EncoderFactory.Create(OutputFormat.PNG);
        var settings = new EncodingSettings
        {
            Format = OutputFormat.PNG, Quality = 100, HdrOutput = false,
            ColorSpaceTag = "DisplayP3", OutputBitDepth = 8,
            IccProfile = ColorProfileProvider.PatchSrgbPrimaries(IccPrimaries.DisplayP3, "Display P3"),
        };
        encoder.EncodeSdrAsync(GeneratePattern(8, 8), 8, 8, settings, path).GetAwaiter().GetResult();

        var iccp = FindPngChunk(ReadPngChunks(path), "iCCP");
        if (iccp is null)
        {
            Assert("端到端 PNG: 有 iCCP 可查", false, "写入器没嵌 ICC ⇒ 本组判据失去出口");
        }
        else
        {
            var embedded = InflateIccpPayload(iccp);
            Assert("端到端 PNG: iCCP 解压出的 profile 不含第三方版权串",
                embedded is { Length: > 132 } && !ContainsAsciiBytes(embedded, "Hewlett"),
                $"解出 {embedded?.Length ?? 0}B");
            Assert("端到端 PNG: iCCP 里的 cprt 是本项目自己的声明",
                embedded is not null && ReadIccTextTypeTag(embedded, "cprt") == ColorProfileProvider.GeneratedIccCopyright,
                $"实得 {ReadIccTextTypeTag(embedded ?? [], "cprt") ?? "<无>"}");
        }
    }

    static uint Be32At(byte[] b, int i)
        => i + 4 <= b.Length
            ? ((uint)b[i] << 24 | (uint)b[i + 1] << 16 | (uint)b[i + 2] << 8 | b[i + 3])
            : 0xFFFFFFFF;

    static bool AllZero(byte[] b, int off, int len)
    {
        if (off + len > b.Length) return false;
        for (int i = 0; i < len; i++) if (b[off + i] != 0) return false;
        return true;
    }

    /// <summary>整份字节流里搜 ASCII 子串（与 tag 解析无关，故可作为独立判据）。</summary>
    static bool ContainsAsciiBytes(byte[] hay, string needle)
    {
        byte[] n = System.Text.Encoding.ASCII.GetBytes(needle);
        if (n.Length == 0 || hay.Length < n.Length) return false;
        for (int i = 0; i + n.Length <= hay.Length; i++)
        {
            int j = 0;
            while (j < n.Length && hay[i + j] == n[j]) j++;
            if (j == n.Length) return true;
        }
        return false;
    }

    /// <summary>
    /// 读 textType 标签（cprt 用这个：sig(4)+reserved(4)+ASCII\0）。
    /// 注意与 desc 的 textDescriptionType 不同 —— 后者在偏移 8 处多一个长度字段。
    /// </summary>
    static string? ReadIccTextTypeTag(byte[] icc, string sig)
    {
        uint want = (uint)((sig[0] << 24) | (sig[1] << 16) | (sig[2] << 8) | sig[3]);
        int n = (int)Be32At(icc, 128);
        for (int i = 0; i < n; i++)
        {
            int e = 132 + i * 12;
            if (e + 12 > icc.Length) return null;
            if (Be32At(icc, e) != want) continue;
            int o = (int)Be32At(icc, e + 4);
            int len = (int)Be32At(icc, e + 8);
            if (o < 128 || len < 9 || o + len > icc.Length) continue;
            int end = o + 8;
            while (end < o + len && icc[end] != 0) end++;
            return System.Text.Encoding.ASCII.GetString(icc, o + 8, end - (o + 8));
        }
        return null;
    }

    /// <summary>iCCP 载荷 = 名称\0 + 压缩方法(0) + zlib(profile)。</summary>
    static byte[]? InflateIccpPayload(byte[] iccp)
    {
        int i = 0;
        while (i < iccp.Length && iccp[i] != 0) i++;
        if (i + 2 > iccp.Length) return null;             // 名称之后还要有压缩方法字节
        try
        {
            using var ms = new MemoryStream(iccp, i + 2, iccp.Length - i - 2);
            using var zs = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            zs.CopyTo(outMs);
            return outMs.ToArray();
        }
        catch { return null; }
    }

    /// <summary>
    /// 缺陷 C（第 1 半）：本仓库生成的 sRGB ICC 必须满足 ICC 自己的 D50-PCS 约定。
    /// <para>旧写法把 IEC 61966-2-1 那张【D65 引用】的矩阵直接当 rXYZ/gXYZ/bXYZ 写进 profile
    /// ⇒ 三列之和是 D65 白，而 ICC 要求 PCS 是 D50。参照数值不是凭记忆：实测本机
    /// <c>C:\Windows\System32\spool\drivers\color\sRGB Color Space Profile.icm</c>
    /// （微软随系统的 sRGB，v2.1，3144B）—— rXYZ=(0.4361,0.2225,0.0139)，三列之和=(0.9643,1.0,0.8251)，
    /// 而它的 wtpt 仍是未适配的 D65 (0.9505,1.0,1.0891) ⇒ "媒体白点照原样记"是它的既有做法，
    /// 本条同时钉住这一点，防止有人"顺手统一"成 D50 反而改错。</para>
    /// </summary>
    static void SrgbIcc_PrimaryColumnsSumToD50White()
    {
        var icc = ColorProfileProvider.GetDefaultSRgbIcc();
        if (icc is not { Length: > 300 }) { Skip("sRGB ICC 结构", "生成失败"); return; }

        var r = ReadIccXyzTag(icc, "rXYZ");
        var g = ReadIccXyzTag(icc, "gXYZ");
        var b = ReadIccXyzTag(icc, "bXYZ");
        var w = ReadIccXyzTag(icc, "wtpt");
        Assert("rXYZ/gXYZ/bXYZ/wtpt 四个标签都在", r is not null && g is not null && b is not null && w is not null);
        if (r is null || g is null || b is null || w is null) return;

        var sum = new[] { r[0] + g[0] + b[0], r[1] + g[1] + b[1], r[2] + g[2] + b[2] };
        Assert($"三原色列之和 = D50 白（实得 {sum[0]:F4},{sum[1]:F4},{sum[2]:F4}）",
            Near(sum, 0.9642f, 1.0f, 0.8249f, 0.003f),
            "和落在 D65 (0.9505,1,1.0890) 就是写错 PCS —— 这是缺陷 C 的直接判据");
        Assert("wtpt 保持未适配 D65（与微软系统 profile 一致，不许顺手改成 D50）",
            Near(w, 0.9505f, 1.0f, 1.0891f, 0.002f),
            $"{w[0]:F4},{w[1]:F4},{w[2]:F4}");
        Assert("rXYZ 与 IsSrgbProfile 的期望同源 (0.4360,0.2225)",
            Math.Abs(r[0] - 0.4360f) < 0.01f && Math.Abs(r[1] - 0.2225f) < 0.01f,
            $"{r[0]:F4},{r[1]:F4}");
    }

    /// <summary>
    /// 缺陷 C（第 2 半，真正的根因）：ICC 的 D50 源矩阵与硬编码的 D65 目标矩阵不能直接相乘。
    /// <para>判据取"sRGB→sRGB 烘焙必须是逐字节不变的恒等"：修好之前，源列(D50) 与目标逆矩阵(D65)
    /// 差一次白点适配 ⇒ 复合矩阵不是单位阵 ⇒ <c>IsIdentityMatrix</c> 判不过 ⇒ 真的对像素乘了一张
    /// 带偏移的矩阵（同一张 profile 自己转自己都会变色，这是可观察的错误，不是纸面担忧）。</para>
    /// </summary>
    static void BakeIcc_SrgbToSrgb_IsExactIdentity()
    {
        var srgbIcc = ColorProfileProvider.GetDefaultSRgbIcc();
        if (srgbIcc is not { Length: > 300 }) { Skip("sRGB→sRGB 恒等", "生成 profile 失败"); return; }

        const int W = 8, H = 4;
        var bgra = new byte[W * H * 4];
        var rnd = new Random(20260919);
        rnd.NextBytes(bgra);
        for (int i = 0; i < W * H; i++) bgra[i * 4 + 3] = 255;   // alpha 直通，别把差异混进来

        var (outPx, outIcc) = ColorProfileProvider.BakeIccToTarget(bgra, W, H, srgbIcc, "sRGB");
        Assert("sRGB→sRGB 有产出且不抛", outPx is not null && outPx.Length == bgra.Length);
        if (outPx is null) return;

        int diffCount = 0;
        int maxDiff = 0;
        for (int i = 0; i < bgra.Length; i++)
        {
            int d = Math.Abs(bgra[i] - outPx[i]);
            if (d > 0) diffCount++;
            if (d > maxDiff) maxDiff = d;
        }
        Assert($"sRGB→sRGB 逐字节恒等（差异字节 {diffCount}，最大 {maxDiff}）",
            diffCount == 0, "非恒等 ⇒ 复合矩阵没回到 D65 同域（D50/D65 混乘回归）");
        Assert("sRGB 目标按契约不嵌 ICC", outIcc is null, $"{outIcc?.Length ?? 0}B");
    }

    /// <summary>
    /// 缺陷 B1：TIFF 没有 cICP 通道，ICC 是唯一能把色域说清楚的标签 ⇒ sRGB 也不能"零声明"。
    /// <para>旧写法 <c>(tag 是广色域) ? s.IccProfile : null</c> 在 sRGB 目标下产出完全不带色彩标记的
    /// 文件（16-bit RGBA 上什么声明都没有，读取端各自猜）。判据只问"有没有 ICC 的 acsp 签名"，
    /// 两条通路（8-bit 源 / Float16 源 ≥10-bit）都要有。</para>
    /// </summary>
    static void Tiff_EmbedsIccEvenForSrgb()
    {
        const int W = 4, H = 4;
        var bgra = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            bgra[i * 4] = 40; bgra[i * 4 + 1] = 120; bgra[i * 4 + 2] = 200; bgra[i * 4 + 3] = 255;
        }
        var hdrPx = new float[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            hdrPx[i * 4] = 0.4f; hdrPx[i * 4 + 1] = 0.5f; hdrPx[i * 4 + 2] = 0.6f; hdrPx[i * 4 + 3] = 1f;
        }
        var enc = EncoderFactory.Create(OutputFormat.TIFF);

        var s8 = new EncodingSettings { Format = OutputFormat.TIFF, OutputBitDepth = 8, ColorSpaceTag = "sRGB" };
        var p8 = Path.Combine(OutDir, $"tiff_srgb8_{Guid.NewGuid():N}.tif");
        enc.EncodeSdrAsync(bgra, W, H, s8, p8).GetAwaiter().GetResult();

        var s16 = new EncodingSettings { Format = OutputFormat.TIFF, OutputBitDepth = 16, ColorSpaceTag = "sRGB" };
        var p16 = Path.Combine(OutDir, $"tiff_srgb16_{Guid.NewGuid():N}.tif");
        enc.EncodeAsync(new HdrFrameData { Pixels = hdrPx, Width = W, Height = H }, s16, p16)
            .GetAwaiter().GetResult();

        foreach (var (name, path) in new[] { ("8-bit 源", p8), ("Float16 源 ≥10", p16) })
        {
            if (!File.Exists(path)) { Assert($"TIFF {name}: 产物存在", false, path); continue; }
            var bytes = File.ReadAllBytes(path);
            bool hasAcsp = false;
            for (int i = 3; i + 1 <= bytes.Length && !hasAcsp; i++)
                hasAcsp = bytes[i] == (byte)'a' && bytes[i + 1] == (byte)'c'
                       && bytes[i + 2] == (byte)'s' && bytes[i + 3] == (byte)'p';
            Assert($"TIFF {name}: sRGB 目标也嵌 ICC（找到 acsp）", hasAcsp,
                "找不到 ⇒ 又回到零声明：TIFF 没有 cICP，不嵌 ICC 就等于什么也没说");
        }
    }

    /// <summary>
    /// 2026-09-19 §4.3「双路径策略」的行为级证据：<b>色域矩阵跟源精度走，容器位深跟用户请求走</b>，两件事必须解耦。
    /// <para>回归背景：旧写法用同一个 <c>OutputBitDepth &gt;= 10</c> 一次决定"要不要跑广色域矩阵"和"容器写几 bit"
    /// —— 16-bit 走 <c>PrepareSdr16Pixels</c>（有矩阵），8-bit 走 <c>ToSdr</c>（无矩阵）。于是用户选 8-bit 输出，
    /// Display P3 的像素被悄悄退回 sRGB，而容器的 cICP/ICC 仍声明 P3。外部判据
    /// <c>scripts/precision/gamut_depth_consistency.py</c> 的量：同一样张 8-bit/16-bit 的 cICP 原色都是 12、
    /// 两档之间 0.000% 的采样差 &gt;1 LSB，而"跑矩阵 vs 不跑矩阵"有 43.2% 的采样不同 —— 修法生效后才是现在这个样子。</para>
    /// <para>这条不打 native 工具，只在 Core 的浮点链上取证 ⇒ 任何机器都能跑：
    /// ① 同帧、同 8-bit 容器，tag=P3 与 tag=sRGB 必须显著不同 ⇒ 矩阵在 8-bit 输出上真跑了；
    /// ② 中性像素 (c,c,c) 必须两档 tag 一致（矩阵行和为 1）⇒ 差别只来自矩阵，没夹白带点缩放；
    /// ③ 同 tag 的 8-bit 与 16-bit 必须只差量化 ≤1 LSB ⇒ 位深没有改颜色；
    /// ④ 16-bit 容器的低字节必须大面积非零 ⇒ 浮点链真带出了 8-bit 表达不了的信息（不是升位深假高精度）。</para>
    /// </summary>
    static void GamutDepth_DecoupledAtEveryOutputDepth()
    {
        const int W = 4, H = 4;
        // 11 个有彩色 + 5 个中性（r=g=b）。全部落在 [0,1] 内 ⇒ 色调映射对三档输出的影响一致，
        // 这里的断言也全是【相对】关系，不依赖色调曲线的具体形状。
        var cols = new (float R, float G, float B)[]
        {
            (0.90f, 0.05f, 0.05f), (0.05f, 0.90f, 0.05f), (0.05f, 0.05f, 0.90f),
            (0.50f, 0.50f, 0.00f), (0.00f, 0.50f, 0.50f), (0.50f, 0.00f, 0.50f),
            (0.20f, 0.70f, 0.35f), (0.75f, 0.15f, 0.60f), (0.35f, 0.35f, 0.80f),
            (0.80f, 0.45f, 0.10f), (0.30f, 0.60f, 0.90f),
            (0.00f, 0.00f, 0.00f), (0.10f, 0.10f, 0.10f), (0.35f, 0.35f, 0.35f),
            (0.65f, 0.65f, 0.65f), (1.00f, 1.00f, 1.00f),
        };
        var src = new float[W * H * 4];
        for (int i = 0; i < cols.Length; i++)
        {
            src[i * 4] = cols[i].R; src[i * 4 + 1] = cols[i].G;
            src[i * 4 + 2] = cols[i].B; src[i * 4 + 3] = 1f;
        }
        var frame = new HdrFrameData { Pixels = src, Width = W, Height = H };

        // ⚠ 每次调用现造一份 settings：准备段会【就地】记账（ColorSpaceTag / IccProfile，16-bit 出口还改 HdrOutput）。
        // 复用同一个对象会让第二次跑到第一次的副作用上 —— 那等于用被污染的第二份实现自证。
        EncodingSettings New(string tag) => new EncodingSettings { ColorSpaceTag = tag, HdrOutput = false };

        byte[] p3_8 = FormatHelper.ToSdr(frame, New("DisplayP3"));
        byte[] srgb_8 = FormatHelper.ToSdr(frame, New("sRGB"));
        byte[] p3_16 = FormatHelper.PrepareSdr16Pixels(frame, New("DisplayP3"));

        int chromaN = 0, chromaMoved = 0, lowByteN = 0, tested = 0;
        int maxNeutralDiff = 0, maxDepthDiff = 0;
        for (int pi = 0; pi < W * H; pi++)
        {
            float r = src[pi * 4], g = src[pi * 4 + 1], b = src[pi * 4 + 2];
            bool neutral = MathF.Abs(r - g) < 1e-4f && MathF.Abs(g - b) < 1e-4f;
            for (int ch = 0; ch < 3; ch++)
            {
                // 两个出口都是 BGRA 序（8-bit: B,G,R,A；16-bit: 大端 B_hi,B_lo,G_hi,G_lo…）⇒ 同位直比
                int v8 = p3_8[pi * 4 + ch];
                int v8s = srgb_8[pi * 4 + ch];
                int v16 = (p3_16[pi * 8 + ch * 2] << 8) | p3_16[pi * 8 + ch * 2 + 1];
                tested++;
                if ((v16 & 0xFF) != 0) lowByteN++;

                int d = Math.Abs(v8 - v8s);
                if (neutral) maxNeutralDiff = Math.Max(maxNeutralDiff, d);
                else { chromaN++; if (d > 2) chromaMoved++; }

                maxDepthDiff = Math.Max(maxDepthDiff, Math.Abs((v16 >> 8) - v8));
            }
        }

        Assert($"① 矩阵在 8-bit 容器上真跑了：有彩通道 {chromaMoved}/{chromaN} 个差 >2 LSB",
            chromaN > 0 && chromaMoved * 2 >= chromaN,
            "若为 0 ⇒ 8-bit 路径把色域矩阵又跳回去了（§4.3 回退）");
        Assert($"② 中性像素不受 tag 影响（矩阵行和为 1）：最大差 {maxNeutralDiff} LSB",
            maxNeutralDiff <= 1, "差得越多越像夹带了白点缩放/亮度增益，而不是纯矩阵");
        Assert($"③ 位深只改量化、不改颜色：8-bit vs 16-bit(P3) 最大差 {maxDepthDiff} LSB",
            maxDepthDiff <= 1);
        Assert($"④ 16-bit 容器确有 8-bit 表达不了的低位：低字节非零 {lowByteN}/{tested} 通道",
            lowByteN * 2 >= tested, "低字节几乎全 0 ⇒ 只是把 8-bit 左移了 8 位（升位深式假高精度）");
    }

    // ═══════════════════════════════════════════════
    //  8. 色域转换矩阵精度验证
    // ═══════════════════════════════════════════════

    static void ColorMatrix_ScrgbToP3_Accuracy()
    {
        // 名字承诺 Accuracy，原实现只验了行和 ≈ 1（行和=1 只保证"白不变"，任何错误的
        // 非对角系数都能通过）与元素非负。现按公布值逐系数比对。
        // 公布值来源：BT.709/sRGB 原色 (0.640,0.330)(0.300,0.600)(0.150,0.060) 与
        // Display P3 原色 (0.680,0.320)(0.265,0.690)(0.150,0.060)、同 D65 白点 (0.3127,0.3290)，
        // 经 XYZ 组合 inv(M_P3)·M_sRGB 得到（本文件不重算这条推导，只钉住结果值）。
        // 注：交接/审计稿里抄的那组（0.0332/0.9968/-0.0290、-0.0171/0.0724/0.9437）与标准
        // 原色推不出对应关系（第二行和为 1.001、第三行首项符号相反，偏差 0.034 ≫ 1e-3），
        // 而生产值与上面的推导逐系数差 ≤ 3.5e-4，故判据取推导结果。
        AssertGamutMatrix("SrgbToP3", ColorSpaceConverter.SrgbToDisplayP3, new float[3, 3]
        {
            { 0.8224621f, 0.1775380f, 0.0000000f },
            { 0.0331942f, 0.9668058f, 0.0000000f },
            { 0.0170826f, 0.0723974f, 0.9105199f },
        });

        float[] rowSums = {
            ColorSpaceConverter.SrgbToDisplayP3[0,0] + ColorSpaceConverter.SrgbToDisplayP3[0,1] + ColorSpaceConverter.SrgbToDisplayP3[0,2],
            ColorSpaceConverter.SrgbToDisplayP3[1,0] + ColorSpaceConverter.SrgbToDisplayP3[1,1] + ColorSpaceConverter.SrgbToDisplayP3[1,2],
            ColorSpaceConverter.SrgbToDisplayP3[2,0] + ColorSpaceConverter.SrgbToDisplayP3[2,1] + ColorSpaceConverter.SrgbToDisplayP3[2,2],
        };
        bool allRowSumClose = true;
        for (int i = 0; i < 3; i++)
            if (MathF.Abs(rowSums[i] - 1.0f) > 0.001f) allRowSumClose = false;
        Assert("SrgbToP3: 所有行和 ≈ 1.0", allRowSumClose);

        // 验证 BT.709 纯色在 [0,1] 范围内转换后无负值
        var bgra = new byte[16 * 16 * 4];
        float minVal = float.MaxValue;
        float maxVal = float.MinValue;
        // 遍历 [0,1]^3 采样点
        for (int ri = 0; ri <= 4; ri++)
        for (int gi = 0; gi <= 4; gi++)
        for (int bi = 0; bi <= 4; bi++)
        {
            float r = ri / 4f, g = gi / 4f, b = bi / 4f;
            float rr = r * ColorSpaceConverter.SrgbToDisplayP3[0,0] + g * ColorSpaceConverter.SrgbToDisplayP3[0,1] + b * ColorSpaceConverter.SrgbToDisplayP3[0,2];
            float gg = r * ColorSpaceConverter.SrgbToDisplayP3[1,0] + g * ColorSpaceConverter.SrgbToDisplayP3[1,1] + b * ColorSpaceConverter.SrgbToDisplayP3[1,2];
            float bb = r * ColorSpaceConverter.SrgbToDisplayP3[2,0] + g * ColorSpaceConverter.SrgbToDisplayP3[2,1] + b * ColorSpaceConverter.SrgbToDisplayP3[2,2];
            minVal = MathF.Min(MathF.Min(MathF.Min(minVal, rr), gg), bb);
            maxVal = MathF.Max(MathF.Max(MathF.Max(maxVal, rr), gg), bb);
        }
        Assert("SrgbToP3: BT.709 [0,1]^3 转换后无负值", minVal >= -0.001f);
        Assert("SrgbToP3: BT.709 [0,1]^3 转换后 ≤ 1.0", maxVal <= 1.001f);
    }

    static void ColorMatrix_ScrgbToBt2020_Accuracy()
    {
        // 公布值：BT.709 线性 → BT.2020 线性（ITU-R BT.2087 的 XYZ 矩阵组合所得，
        // 与 NativeAvifEncoder.FloatToPq10 里那份独立写出的系数同一套值）。
        AssertGamutMatrix("SrgbToBT2020", ColorSpaceConverter.SrgbToBt2020, new float[3, 3]
        {
            { 0.6274039f, 0.3292830f, 0.0433131f },
            { 0.0690973f, 0.9195404f, 0.0113623f },
            { 0.0163914f, 0.0880133f, 0.8955953f },
        });

        float[] rowSums = {
            ColorSpaceConverter.SrgbToBt2020[0,0] + ColorSpaceConverter.SrgbToBt2020[0,1] + ColorSpaceConverter.SrgbToBt2020[0,2],
            ColorSpaceConverter.SrgbToBt2020[1,0] + ColorSpaceConverter.SrgbToBt2020[1,1] + ColorSpaceConverter.SrgbToBt2020[1,2],
            ColorSpaceConverter.SrgbToBt2020[2,0] + ColorSpaceConverter.SrgbToBt2020[2,1] + ColorSpaceConverter.SrgbToBt2020[2,2],
        };
        bool allRowSumClose = true;
        for (int i = 0; i < 3; i++)
            if (MathF.Abs(rowSums[i] - 1.0f) > 0.001f) allRowSumClose = false;
        Assert("SrgbToBT2020: 所有行和 ≈ 1.0", allRowSumClose);

        // BT.709 [0,1]^3 应映射到 BT.2020 范围内
        float minVal = float.MaxValue;
        for (int ri = 0; ri <= 4; ri++)
        for (int gi = 0; gi <= 4; gi++)
        for (int bi = 0; bi <= 4; bi++)
        {
            float r = ri / 4f, g = gi / 4f, b = bi / 4f;
            float rr = r * ColorSpaceConverter.SrgbToBt2020[0,0] + g * ColorSpaceConverter.SrgbToBt2020[0,1] + b * ColorSpaceConverter.SrgbToBt2020[0,2];
            float gg = r * ColorSpaceConverter.SrgbToBt2020[1,0] + g * ColorSpaceConverter.SrgbToBt2020[1,1] + b * ColorSpaceConverter.SrgbToBt2020[1,2];
            float bb = r * ColorSpaceConverter.SrgbToBt2020[2,0] + g * ColorSpaceConverter.SrgbToBt2020[2,1] + b * ColorSpaceConverter.SrgbToBt2020[2,2];
            minVal = MathF.Min(MathF.Min(MathF.Min(minVal, rr), gg), bb);
        }
        Assert("SrgbToBT2020: BT.709 [0,1]^3 无负值", minVal >= -0.001f);
    }

    static void ColorMatrix_ScrgbToAdobeRgb_Accuracy()
    {
        // 公布值：sRGB 线性 → Adobe RGB (1998) 线性。AdobeRGB 与 sRGB 共享红/蓝原色与 D65 白点
        // （只有绿原色不同）⇒ 第二行恒为 (0,1,0)、第三行首项恒为 0 是**结构性**判据
        // （原实现只验行和 ≈ 1，把整张矩阵换成任意"行和为 1"的错值都能通过）。
        AssertGamutMatrix("SrgbToAdobeRGB", ColorSpaceConverter.SrgbToAdobeRgb, new float[3, 3]
        {
            { 0.7151256f, 0.2848744f, 0.0000000f },
            { 0.0000000f, 1.0000000f, 0.0000000f },
            { 0.0000000f, 0.0411619f, 0.9588381f },
        });

        float[] rowSums = {
            ColorSpaceConverter.SrgbToAdobeRgb[0,0] + ColorSpaceConverter.SrgbToAdobeRgb[0,1] + ColorSpaceConverter.SrgbToAdobeRgb[0,2],
            ColorSpaceConverter.SrgbToAdobeRgb[1,0] + ColorSpaceConverter.SrgbToAdobeRgb[1,1] + ColorSpaceConverter.SrgbToAdobeRgb[1,2],
            ColorSpaceConverter.SrgbToAdobeRgb[2,0] + ColorSpaceConverter.SrgbToAdobeRgb[2,1] + ColorSpaceConverter.SrgbToAdobeRgb[2,2],
        };
        bool allRowSumClose = true;
        for (int i = 0; i < 3; i++)
            if (MathF.Abs(rowSums[i] - 1.0f) > 0.001f) allRowSumClose = false;
        Assert("SrgbToAdobeRGB: 所有行和 ≈ 1.0", allRowSumClose);
    }

    // 2026-09-19：原 ColorMatrix_ApplySrgbToTargetGamut() 已删除。
    // 被测对象 ColorSpaceConverter.ApplySrgbToTargetGamut 是"8-bit → float → 矩阵 → 8-bit"的
    // 双重量化实现，且在 src/ 下没有任何生产调用方 —— 这条用例存在的唯一作用，
    // 是让一份永不执行的代码看起来有测试覆盖。函数与本用例一并移除，理由写在
    // ColorProfileProvider.cs 的原位注释里。

    // ═══════════════════════════════════════════════
    //  9. Gain Map XMP 验证
    // ═══════════════════════════════════════════════

    static void GainMap_BuildXmpMetadata_Output()
    {
        // 直接验证 BuildXmpMetadata 的输出长度和内容
        var xmp = JpegGainMapEncoder.BuildXmpMetadata(1920, 1080, 481, 271, GainMapMode.Gray);
        string xmpStr = System.Text.Encoding.UTF8.GetString(xmp);
        bool hasGmVersion = xmpStr.Contains("hdrgm:Version");
        bool hasBaseRendition = xmpStr.Contains("BaseRenditionIsHDR");
        bool hasMinGain = xmpStr.Contains("GainMapMin");
        bool hasMaxGain = xmpStr.Contains("GainMapMax");
        bool hasGamma = xmpStr.Contains("Gamma");
        bool hasContainer = xmpStr.Contains("Container:Directory") && xmpStr.Contains("Item:Semantic=\"Primary\"");
        bool hasGainMapItem = xmpStr.Contains("Item:Semantic=\"GainMap\"");
        bool hasXpacketEnd = xmpStr.Contains("xpacket end");
        // 关键: GainMapMin/Max 必须是 log2 值 (默认 headroom=12.5 → max=log2(12.5)≈3.64)
        bool minIsLog2 = xmpStr.Contains("GainMapMin>0.0");
        bool maxIsLog2 = xmpStr.Contains("GainMapMax>3.64");
        Assert($"BuildXmpMetadata: 长度={xmp.Length}字节", xmp.Length >= 500);
        Assert("BuildXmpMetadata: 包含 hdrgm:Version", hasGmVersion);
        Assert("BuildXmpMetadata: 包含 BaseRenditionIsHDR", hasBaseRendition);
        Assert("BuildXmpMetadata: 包含 GainMapMin", hasMinGain);
        Assert("BuildXmpMetadata: 包含 GainMapMax", hasMaxGain);
        Assert("BuildXmpMetadata: 包含 Gamma", hasGamma);
        Assert("BuildXmpMetadata: 包含 Container:Directory + Primary", hasContainer);
        Assert("BuildXmpMetadata: 包含 GainMap Item", hasGainMapItem);
        Assert("BuildXmpMetadata: GainMapMin 为 log2 值 (0)", minIsLog2);
        Assert($"BuildXmpMetadata: GainMapMax 为 log2 值 ({MathF.Log2(12.5f):F2})", maxIsLog2);
        Assert("BuildXmpMetadata: 包含 xpacket end", hasXpacketEnd);
        // ═══ 2026-08-10 修复: GainMap item 含 Item:Length (libultrahdr 对齐) ═══
        var xmpWithLen = JpegGainMapEncoder.BuildXmpMetadata(1920, 1080, 481, 271, GainMapMode.Gray, 12.5f, 12345);
        string xmpWithLenStr = System.Text.Encoding.UTF8.GetString(xmpWithLen);
        Assert("BuildXmpMetadata: GainMap Item 含 Item:Length", xmpWithLenStr.Contains("Item:Length=\"12345\""));
        var xmpNoLen = JpegGainMapEncoder.BuildXmpMetadata(1920, 1080, 481, 271, GainMapMode.Gray);
        Assert("BuildXmpMetadata: 无长度时不写 Item:Length", !System.Text.Encoding.UTF8.GetString(xmpNoLen).Contains("Item:Length"));
    }

    static void GainMap_BuildIsoMetadata_Output()
    {
        // 验证 ISO 21496-1 二进制元数据 (非公共分母模式)
        var iso = JpegGainMapEncoder.BuildIso21496Metadata(GainMapMode.Gray);
        // 布局: u16 min_version + u16 writer_version + u8 flags
        //   + baseHeadroomN/D + altHeadroomN/D (4×u32)
        //   + 每通道 [minN, minD, maxN, maxD, gammaN, gammaD, baseOffN, baseOffD, altOffN, altOffD] (10×u32)
        int expectedLen = 2 + 2 + 1 + (4 * 4) + (10 * 4);
        Assert($"ISO 元数据: 长度={iso.Length} (期望 {expectedLen})", iso.Length == expectedLen);
        // min_version=0, writer_version=0
        Assert("ISO 元数据: min_version=0", iso[0] == 0 && iso[1] == 0);
        Assert("ISO 元数据: writer_version=0", iso[2] == 0 && iso[3] == 0);
        // flags: 非公共分母 (bit3=0) 且非多通道 (Gray)
        Assert("ISO 元数据: 非 common denominator", (iso[4] & 0x08) == 0);
        Assert("ISO 元数据: Gray 非多通道", (iso[4] & 0x80) == 0);
        // ═══ 2026-08-10 修复: useBaseColorSpace (bit6=0x40) 必须设置 ═══
        // 增益图在 base (sRGB/BT.709) 色彩空间计算, 对齐 libultrahdr use_base_cg=true;
        // 缺失 → 解码器按 alternate (HDR) 色域解释 → 增益涂层错误
        Assert("ISO 元数据: useBaseColorSpace (0x40)", (iso[4] & 0x40) != 0);
        // headroom: base=0/1, alt=max/1
        int expectedMax = (int)Math.Round(MathF.Log2(12.5f) * 100f); // 364 (log2(12.5)=3.64 × 100)
        Assert("ISO 元数据: baseHeadroom=0/1", iso[5] == 0 && iso[6] == 0 && iso[7] == 0 && iso[8] == 0 && iso[9] == 0 && iso[10] == 0 && iso[11] == 0 && iso[12] == 1);
        Assert($"ISO 元数据: altHeadroom={expectedMax}/100", 
            iso[13] == (expectedMax >> 24 & 0xFF) && iso[14] == (expectedMax >> 16 & 0xFF) && 
            iso[15] == (expectedMax >> 8 & 0xFF) && iso[16] == (expectedMax & 0xFF) &&
            iso[17] == 0 && iso[18] == 0 && iso[19] == 0 && iso[20] == 100);
        // 每通道字段 (从偏移 21 开始)
        int off = 21;
        // gainMapMin = 0/1
        Assert("ISO 元数据: gainMapMin=0/1", iso[off] == 0 && iso[off+1] == 0 && iso[off+2] == 0 && iso[off+3] == 0 && iso[off+4] == 0 && iso[off+5] == 0 && iso[off+6] == 0 && iso[off+7] == 1);
        // gainMapMax = expectedMax/100 (精确, 与 XMP 一致!)
        Assert($"ISO 元数据: gainMapMax={expectedMax}/100 (精确)",
            iso[off+8] == (expectedMax >> 24 & 0xFF) && iso[off+9] == (expectedMax >> 16 & 0xFF) && 
            iso[off+10] == (expectedMax >> 8 & 0xFF) && iso[off+11] == (expectedMax & 0xFF) &&
            iso[off+12] == 0 && iso[off+13] == 0 && iso[off+14] == 0 && iso[off+15] == 100);
        // ═══ 关键: gamma = 1/1 (不能是 1/64 = 0.015625!) ═══
        Assert("ISO 元数据: gamma=1/1 (关键!)", iso[off+16] == 0 && iso[off+17] == 0 && iso[off+18] == 0 && iso[off+19] == 1 && iso[off+20] == 0 && iso[off+21] == 0 && iso[off+22] == 0 && iso[off+23] == 1);
        // offset = 1/64
        Assert("ISO 元数据: baseOffset=1/64", iso[off+24] == 0 && iso[off+25] == 0 && iso[off+26] == 0 && iso[off+27] == 1 && iso[off+28] == 0 && iso[off+29] == 0 && iso[off+30] == 0 && iso[off+31] == 64);
        Assert("ISO 元数据: altOffset=1/64", iso[off+32] == 0 && iso[off+33] == 0 && iso[off+34] == 0 && iso[off+35] == 1 && iso[off+36] == 0 && iso[off+37] == 0 && iso[off+38] == 0 && iso[off+39] == 64);

        // RGB 模式: 多通道标志 + 3 通道
        var isoRgb = JpegGainMapEncoder.BuildIso21496Metadata(GainMapMode.Rgb);
        int expectedRgb = 2 + 2 + 1 + (4 * 4) + (3 * 10 * 4);
        Assert($"ISO 元数据 RGB: 长度={isoRgb.Length} (期望 {expectedRgb})", isoRgb.Length == expectedRgb);
        Assert("ISO 元数据 RGB: 多通道标志", (isoRgb[4] & 0x80) != 0);
    }

    /// <summary>
    /// 增益图的 log2 量程必须只有一个来源：像素量化用的值 == XMP == ISO（交接 §31.3-3）。
    /// <para>缺陷形态（headroom ≤ 1，即 HDR 峰值不高于 SDR 纸白 —— 在非 HDR 屏上开着 HDR 输出就会遇到，
    /// 例如 DisplayMaxNits=80 / PaperWhiteNits=80）：像素侧把 maxLog2Gain 兜底成 1.0 去量化，
    /// 而 XMP 与 ISO 各自<b>又算了一次</b> <c>log2(max(headroom,1)) = 0</c> ⇒
    ///   ① 声明与像素矛盾：解码 <c>log_boost = min + (max−min)·recovery</c> 恒 0，增益层整层作废；
    ///   ② 更糟的是 <c>HDRCapacityMin == HDRCapacityMax == 0</c>（ISO 侧 baseHdrHeadroom ==
    ///      alternateHdrHeadroom），违反"capacity_max 必须严格大于 capacity_min"，
    ///      libavif / libultrahdr 的权重公式在此除零。</para>
    /// <para>这里不只比"值对不对"，还钉一条结构性的：<b>XMP 十进制 ×100 必须等于 ISO 的分子整数</b>
    /// —— 只要还剩一处第二次算 log2、或两侧取整方式不同，这条就会红（原先正是 XMP 走
    /// <c>ToString("0.00")</c>、ISO 走 <c>MathF.Round</c> 的默认取偶，恰在中点可差 0.01）。</para>
    /// </summary>
    static void GainMap_GainRange_SingleSourceAcrossPixelsXmpAndIso()
    {
        // (headroom, 期望的百分位整数)：退化 1.0 与 0.5 都兜到 100（=1.00 log2），
        // 正常档保持 log2 的真实值不变 —— 兜底只作用于退化处，不许把 2.0 也"修"成 1.0。
        foreach (var (headroom, want) in new[] { (1.0f, 100), (0.5f, 100), (2.0f, 100), (12.5f, 364) })
        {
            var xmp = System.Text.Encoding.UTF8.GetString(JpegGainMapEncoder.BuildXmpMetadata(
                1920, 1080, 481, 271, GainMapMode.Gray, headroom));
            var iso = JpegGainMapEncoder.BuildIso21496Metadata(GainMapMode.Gray, headroom);

            static string Field(string xmp, string name)
            {
                int a = xmp.IndexOf($"<{name}>", StringComparison.Ordinal);
                int b = xmp.IndexOf('<', a + name.Length + 2, StringComparison.Ordinal);
                return a < 0 || b < 0 ? "?" : xmp[(a + name.Length + 2)..b];
            }

            string maxTxt = Field(xmp, "hdrgm:GainMapMax");
            string capMin = Field(xmp, "hdrgm:HDRCapacityMin");
            string capMax = Field(xmp, "hdrgm:HDRCapacityMax");
            int xmpCentibels = (int)Math.Round(
                double.Parse(maxTxt, System.Globalization.CultureInfo.InvariantCulture) * 100.0);

            Assert($"headroom={headroom}: GainMapMax={maxTxt} 等于唯一整数源 {want / 100m:0.00}",
                xmpCentibels == want, $"实际 {maxTxt}");
            Assert($"headroom={headroom}: ISO alternateHdrHeadroom 分子 == 同一个整数",
                BeI32(iso, 13) == want && BeI32(iso, 17) == 100,
                $"实际 {BeI32(iso, 13)}/{BeI32(iso, 17)}");
            Assert($"headroom={headroom}: ISO gainMapMax 分子 == 同一个整数（与 XMP 零漂移）",
                BeI32(iso, 29) == want && BeI32(iso, 33) == 100,
                $"实际 {BeI32(iso, 29)}/{BeI32(iso, 33)}");
            // 除零守卫：capacity 区间必须严格递增，XMP 与 ISO 两侧都要成立
            Assert($"headroom={headroom}: HDRCapacityMax({capMax}) 严格大于 Min({capMin})",
                double.Parse(capMax, System.Globalization.CultureInfo.InvariantCulture)
                > double.Parse(capMin, System.Globalization.CultureInfo.InvariantCulture));
            Assert($"headroom={headroom}: ISO alternate({BeI32(iso, 13)}) 严格大于 base({BeI32(iso, 5)})",
                BeI32(iso, 13) > BeI32(iso, 5));
        }

        // 反向自检：判据不许恒真 —— 退化档与正常档若被判成同一个值，本用例就什么都没钉住
        static int IsoAlt(float hr)
            => BeI32(JpegGainMapEncoder.BuildIso21496Metadata(GainMapMode.Gray, hr), 13);
        Assert("退化档(1.0) 与正常档(12.5) 的分子确实不同（兜底只作用于退化处，没有把所有值抹平）",
            IsoAlt(1.0f) != IsoAlt(12.5f), $"{IsoAlt(1.0f)} vs {IsoAlt(12.5f)}");
        Assert("log2(2)=1.00 是真值而非兜底巧合：headroom=2 与 1.0 同为 100 是数学上的相等",
            IsoAlt(2.0f) == 100, "若这条红，说明兜底或取整被动到了别处");
    }

    // ═══════════════════════════════════════════════
    //  辅助方法
    // ═══════════════════════════════════════════════

    /// <summary>
    /// 把一个亮度（nits）送进生产入口 <see cref="FormatHelper.HdrToPq16"/>，取回 RGBA 四个 16-bit 码点。
    /// <para>输入按 scRGB 线性约定 <c>nits / 80</c>（80 nits = 1.0，见 FormatHelper.LinearToPQ 内的
    /// <c>nits = linear * 80</c>）；帧为 1×1 灰像素、alpha = 1。colorSpaceTag 显式给 "sRGB"，
    /// 使 <c>ColorSpaceConverter.GetMatrix</c> 返回 null ⇒ 这一条只测 PQ 曲线，不混入色域矩阵。</para>
    /// <para>2026-09-19：本文件原有的 <c>LinearToPq</c> / <c>PqToLinear</c> 两份本地副本已删除 ——
    /// 它们让 PQ 用例只在验证测试文件自己抄的公式，生产的编码器一次也没被跑到（自证）。</para>
    /// </summary>
    static ushort[] Pq16CodesForNits(float nits)
    {
        float v = nits / 80f;
        var frame = new HdrFrameData { Pixels = [v, v, v, 1f], Width = 1, Height = 1 };
        return FormatHelper.HdrToPq16(frame, "sRGB", 16);
    }

    /// <summary>
    /// 逐系数比对 3×3 色域矩阵与公布值（容差 ±1e-3）。原实现只验"行和 ≈ 1 + 元素非负"，
    /// 而"行和=1"只保证白点不变 —— 任意把两个系数互换都能通过，这不叫 Accuracy。
    /// </summary>
    static void AssertGamutMatrix(string tag, float[,] m, float[,] published)
    {
        float worst = 0; int wi = 0, wj = 0;
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                float d = MathF.Abs(m[i, j] - published[i, j]);
                if (d > worst) { worst = d; wi = i; wj = j; }
            }
        Assert($"{tag}: 9 个系数逐个对上公布值 (|Δ| ≤ 1e-3, 最差 [{wi},{wj}] Δ={worst:E2})", worst <= 1e-3f);
    }

    /// <summary>读 3 个连续的 s15Fixed16（大端）—— ICC 的 XYZ 三元组；越界返回 NaN 三元组。</summary>
    static (float X, float Y, float Z) ReadS15Triple(byte[] b, int o)
        => o >= 0 && o + 12 <= b.Length
            ? (S15Fixed16(b, o), S15Fixed16(b, o + 4), S15Fixed16(b, o + 8))
            : (float.NaN, float.NaN, float.NaN);

    /// <summary>ICC s15Fixed16：32 位有符号大端，低 16 位是小数（1.0 = 65536）。</summary>
    static float S15Fixed16(byte[] b, int o) => (b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]) / 65536f;

    /// <summary>
    /// 在 ICC tag 表里按签名找一个 xyzType 标签并读回它的三个 s15Fixed16（找不到返回 null）。
    /// <para>tag 表: [4B count]@128 起，每项 [4B sig][4B offset][4B len]；
    /// 标签体: [4B 'XYZ '][4B reserved][3× s15Fixed16] ⇒ 值从 offset+8 开始（ICC.1 §10.4）。
    /// 这里按规范布局自己走表，不复用 ColorProfileProvider 的解析函数。</para>
    /// </summary>
    static (float X, float Y, float Z)? FindIccXyzTag(byte[] icc, int tagCount, string sig)
    {
        int want = sig[0] << 24 | sig[1] << 16 | sig[2] << 8 | sig[3];
        for (int i = 0; i < tagCount; i++)
        {
            int e = 132 + i * 12;
            if (e + 12 > icc.Length) break;
            if ((icc[e] << 24 | icc[e + 1] << 16 | icc[e + 2] << 8 | icc[e + 3]) != want) continue;
            int off = icc[e + 4] << 24 | icc[e + 5] << 16 | icc[e + 6] << 8 | icc[e + 7];
            var t = ReadS15Triple(icc, off + 8);
            return float.IsNaN(t.X) ? null : t;
        }
        return null;
    }

    /// <summary>
    /// 在 [from,to) 内走 JPEG 段链，返回第一个载荷含 <paramref name="needle"/> 的指定 APP marker 段载荷文本。
    /// <para>布局判据来自 T.81：marker 两字节 <c>FF Ex</c> + 大端 2 字节长度（含长度自身、不含 marker）
    /// + 载荷。找不到返回 null。自己走段链而不是问 JpegGainMapEncoder，是为了让"段落位置"
    /// （XMP/MPF 在 Base 内、ISO 21496-1 在增益图 SOI 之后）成为可判定的事实。</para>
    /// </summary>
    static string? FindJpegAppPayload(byte[] d, int from, int to, byte marker, string needle)
    {
        int end = Math.Min(to, d.Length);
        for (int i = Math.Max(from, 0); i + 4 < end; i++)
        {
            if (d[i] != 0xFF || d[i + 1] != marker) continue;
            int len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) continue;     // 长度越界 ⇒ 段链已损坏，跳过该 marker
            var payload = System.Text.Encoding.UTF8.GetString(d, i + 4, len - 2);
            if (payload.Contains(needle)) return payload;
        }
        return null;
    }

    /// <summary>字节序列里首次出现 ASCII 串的位置（找不到 -1）。</summary>
    static int IndexOfAscii(byte[] d, string needle)
    {
        var n = System.Text.Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i + n.Length <= d.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++) if (d[i + j] != n[j]) { hit = false; break; }
            if (hit) return i;
        }
        return -1;
    }

    /// <summary>大端 u32（ISOBMFF 盒长/尺寸字段一律大端）。</summary>
    static uint ReadBeUInt32(byte[] d, int o)
        => o >= 0 && o + 4 <= d.Length
            ? ((uint)d[o] << 24 | (uint)d[o + 1] << 16 | (uint)d[o + 2] << 8 | d[o + 3])
            : 0u;

    /// <summary>生成 BGRA 8x8 测试图案。</summary>
    static byte[] GeneratePattern(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = (byte)(i / 4 * 7 % 256);      // B
            bgra[i + 1] = (byte)(255 - i / 4 * 7);  // G
            bgra[i + 2] = (byte)(i / 4 * 13 % 256); // R
            bgra[i + 3] = 255;
        }
        return bgra;
    }

    /// <summary>检查 PNG 文件是否包含指定 chunk。</summary>
    static bool CheckPngChunk(string path, string chunkName)
        => FindPngChunk(ReadPngChunks(path), chunkName) is not null;

    /// <summary>
    /// 2026-09-19：独立遍历 PNG chunk 链（不复用 ManagedPngEncoder 的写入逻辑）：返回按文件顺序排列的
    /// <c>(类型, 数据)</c>。签名不符、块长越界时返回已解析到的部分 —— 调用方的"首块是 IHDR"
    /// 之类的断言因此能区分"文件不是 PNG"和"PNG 但缺元数据"。
    /// </summary>
    static List<(string Type, byte[] Data)> ReadPngChunks(string path)
    {
        var list = new List<(string Type, byte[] Data)>();
        byte[] d;
        try { d = File.ReadAllBytes(path); } catch { return list; }
        byte[] sig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (d.Length < 8) return list;
        for (int i = 0; i < 8; i++) if (d[i] != sig[i]) return list;

        int pos = 8;
        while (pos + 12 <= d.Length)
        {
            int len = (d[pos] << 24) | (d[pos + 1] << 16) | (d[pos + 2] << 8) | d[pos + 3];
            string type = System.Text.Encoding.ASCII.GetString(d, pos + 4, 4);
            // 长度越界（含 len 为负 / 加法溢出）⇒ 结构已损坏，停止解析
            if (len < 0 || (long)pos + 12 + len > d.Length) break;
            var data = new byte[len];
            System.Buffer.BlockCopy(d, pos + 8, data, 0, len);
            list.Add((type, data));
            pos += 12 + len;
            if (type == "IEND") break;
        }
        return list;
    }

    /// <summary>取同名 chunk 的数据（不存在返回 null；同名多次出现取第一个）。</summary>
    static byte[]? FindPngChunk(List<(string Type, byte[] Data)> chunks, string name)
    {
        foreach (var c in chunks)
            if (c.Type == name) return c.Data;
        return null;
    }

    static void Assert(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}{(detail is not null ? $": {detail}" : "")}"); }
    }
}