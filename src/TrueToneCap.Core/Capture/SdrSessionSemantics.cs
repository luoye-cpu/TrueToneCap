// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Capture/SdrSessionSemantics.cs
//
// 「WGC 的 8-bit 桌面会话在 HDR 模式下交回的字节是什么语义」的判定本体。
//
// 为什么要单独成一个纯函数，而不是把结论写在探针里：
//   2026-10-26 兜底覆盖层被肉眼看到"背景比实时桌面亮"，归因有两个互斥假设 ——
//     (a) 字节已按系统纸白抬升过 ⇒ 8-bit 窗口再被 DWM 抬一次 = 双重提亮；
//     (b) 字节是 authored sRGB ⇒ 亮只抬了一次，过曝另有来源。
//   两者指向完全不同的修法，而"真机跑一次看读数"证明不了【尺子本身会红】。
//   所以读数与结论分家：探针只负责交回 PatchReading，判定走这里，
//   合成对照臂（authored 一张表 / 抬升一张表）由单测喂进【同一个】Judge —— 先例见
//   App/Services/SelfTest.cs 的 JudgeHdrFloat（那边正是靠合成帧抓到判据自身的两个死缺陷）。
//
// 绝对锚点从哪来：补丁的 authored 值是探针自己用 GDI 画上去的已知字节，
//   不依赖纸白读数，也不依赖任何一侧会话的语义假设。

namespace TrueToneCap.Core.Capture;

/// <summary>一枚纯色补丁在两个会话里的实测读数 + 同区"补丁未显示时"的对照读数。</summary>
/// <param name="AuthoredByte">探针画上去的 sRGB 字节（中性灰，R=G=B）。</param>
/// <param name="Mean8">8-bit 会话该区的三通道均值。</param>
/// <param name="StdDev8">8-bit 会话该区灰度的标准差 —— 大 = 采样区根本不是那块补丁。</param>
/// <param name="MeanFloatLinear">Float16 会话该区灰度的线性均值；拿不到 float 时传 0。</param>
/// <param name="ControlMean8">同一区域在补丁【隐藏】时的 8-bit 均值 —— 用来证明读数真的来自补丁。</param>
public readonly record struct PatchReading(
    int AuthoredByte, double Mean8, double StdDev8, double MeanFloatLinear, double ControlMean8);

public enum EightBitSemantics { Unknown, AuthoredSrgb, BoostedToSdrWhite }

public enum Float16Semantics { Unknown, NotMeasured, SceneReferred, BoostedToSdrWhite }

/// <param name="SelfCheckPassed">假 ⇒ 这组读数【不足以判】，任何结论都按"没判"处理，不许当通过读。</param>
public readonly record struct SdrSemanticsVerdict(
    EightBitSemantics EightBit,
    Float16Semantics Float16,
    double EightBitRatio,
    double FloatRatio,
    double SdrWhiteScale,
    bool SelfCheckPassed,
    string Why);

public static class SdrSessionSemantics
{
    /// <summary>名义 SDR 参考白 (nits)。与 <see cref="PixelOps.NominalSdrReferenceWhiteNits"/> 同值，
    /// 这里用 double 重述一次是为了让本判定不依赖 App 侧的 float 常量。</summary>
    public const double NominalReferenceWhiteNits = 80.0;

    public const double MaxStdDevUniform = 4.0;      // 采样区必须基本均匀
    public const double MinLandedDelta = 6.0;        // 补丁均值与"补丁隐藏时"的差下限 ⇒ 证明采到了补丁本身
    public const double AuthoredTolerance = 0.04;    // mean8/authored 落在 1±此值 ⇒ authored
    public const double BoostedMinRatio = 1.08;      // 全部补丁都 ≥ 此倍率 ⇒ 字节里已含一次抬升
    public const double FloatTolerance = 0.10;       // float 倍率与假设值的相对容差
    public const int MinUsablePatches = 2;           // 不足 ⇒ 不判
    public const int MidToneLow = 24;                // 两端(近黑/近白)在钳位下退化，只当中段
    public const int MidToneHigh = 220;

    /// <summary>纸白倍率 k = SDR 白点 / 80；纸白读不到时返回 0（= 不参与判定，不猜一个值）。</summary>
    public static double SdrWhiteScale(int sdrWhiteNits) =>
        sdrWhiteNits > 0 ? sdrWhiteNits / NominalReferenceWhiteNits : 0.0;

    /// <summary>单枚补丁是否够格进判定。</summary>
    public static bool IsUsable(in PatchReading p) =>
        p.AuthoredByte >= MidToneLow && p.AuthoredByte <= MidToneHigh &&
        p.StdDev8 <= MaxStdDevUniform &&
        Math.Abs(p.Mean8 - p.ControlMean8) >= MinLandedDelta;

    /// <summary>
    /// 读表判定。<paramref name="patches"/> 由探针提供（可含不可用条目，这里自己筛）。
    /// <para>判据是【单调且互斥】的：8-bit 一侧要么整体贴着 authored（±4%），要么整体被抬到
    /// ≥1.08 倍；落在两者之间的灰带 → Unknown，不硬掰成任何一个结论。</para>
    /// </summary>
    public static SdrSemanticsVerdict Judge(IReadOnlyList<PatchReading> patches, int sdrWhiteNits)
    {
        ArgumentNullException.ThrowIfNull(patches);
        double k = SdrWhiteScale(sdrWhiteNits);

        var usable = new List<PatchReading>();
        foreach (var p in patches)
            if (IsUsable(p)) usable.Add(p);

        if (usable.Count < MinUsablePatches)
            return new SdrSemanticsVerdict(EightBitSemantics.Unknown, Float16Semantics.Unknown,
                double.NaN, double.NaN, k, false,
                $"可用补丁 {usable.Count}/{patches.Count} < {MinUsablePatches}" +
                $"（判据：{MidToneLow}≤authored≤{MidToneHigh}、灰度标准差≤{MaxStdDevUniform:0.#}、" +
                $"与隐藏态对照差≥{MinLandedDelta:0.#}）⇒ 不判");

        var byteRatios = new double[usable.Count];
        var floatRatios = new double[usable.Count];
        bool anyFloat = false;
        for (int i = 0; i < usable.Count; i++)
        {
            var p = usable[i];
            byteRatios[i] = p.Mean8 / p.AuthoredByte;
            double authoredLinear = PixelOps.SrgbToLinearLut[p.AuthoredByte];
            floatRatios[i] = authoredLinear > 0 ? p.MeanFloatLinear / authoredLinear : double.NaN;
            if (p.MeanFloatLinear > 0) anyFloat = true;
        }
        double rMin = double.MaxValue, rMax = double.MinValue;
        foreach (var r in byteRatios) { if (r < rMin) rMin = r; if (r > rMax) rMax = r; }
        double rMed = Median(byteRatios);
        double fMed = anyFloat ? Median(floatRatios) : double.NaN;

        // ── 8-bit 一侧 ──
        EightBitSemantics e8 =
            rMin >= 1.0 - AuthoredTolerance && rMax <= 1.0 + AuthoredTolerance ? EightBitSemantics.AuthoredSrgb :
            rMin >= BoostedMinRatio ? EightBitSemantics.BoostedToSdrWhite :
            EightBitSemantics.Unknown;

        // ── Float16 一侧：锚在同一枚补丁的 authored 线性值上，与 8-bit 的语义无关 ──
        Float16Semantics f16;
        if (!anyFloat) f16 = Float16Semantics.NotMeasured;
        else if (Math.Abs(fMed - 1.0) <= FloatTolerance) f16 = Float16Semantics.SceneReferred;
        else if (k > 0 && Math.Abs(fMed - k) <= FloatTolerance * k) f16 = Float16Semantics.BoostedToSdrWhite;
        else f16 = Float16Semantics.Unknown;

        string why =
            $"8-bit mean8/authored = {rMed:0.000}（逐枚 {rMin:0.000}~{rMax:0.000}，{usable.Count} 枚）" +
            $" | float/ authored线性 = {(anyFloat ? fMed.ToString("0.000") : "n/a")}" +
            $" | 纸白倍率 k = {(k > 0 ? k.ToString("0.000") : "读不到")}（纸白 {sdrWhiteNits}nits）" +
            $" | 阈值 authored±{AuthoredTolerance:0.##} / 抬升≥{BoostedMinRatio:0.##} / float±{FloatTolerance:0.##}";

        return new SdrSemanticsVerdict(e8, f16, rMed, fMed, k, true, why);
    }

    /// <summary>
    /// 结论对"改哪儿"的含义，写给读报告的人。只依据 8-bit 那一侧 —— 兜底层过曝的必要条件就是
    /// 字节里已含一次抬升；b 成立时这条一定不成立。
    /// </summary>
    public static string Implication(in SdrSemanticsVerdict v) => v.EightBit switch
    {
        EightBitSemantics.AuthoredSrgb =>
            "8-bit 会话交回的是 authored sRGB ⇒ 兜底 8-bit 窗只会被 DWM 抬一次，D3D 档的 ×k 也正好是补那一次。" +
            "⇒ 【双重提亮】这一支被否证：过曝另有来源，去查兜底窗自己的色彩空间/呈现路径，别改捕获层。",
        EightBitSemantics.BoostedToSdrWhite =>
            "8-bit 会话交回的字节里已经含一次纸白抬升 ⇒ 兜底 8-bit 窗会被 DWM 再抬一次（双重提亮成立），" +
            "而 D3D 档把同一份字节再 ×k 也是第二抬。⇒ 修在字节进入显示/编码之前，把这一抬除掉（方案 A/B 二选一，" +
            "但两处消费方必须同改，不许只改兜底）。",
        _ => v.SelfCheckPassed
            ? "读数落在两条假设之间的灰带（既不是 1±4% 也不是整体 ≥1.08）⇒ 本轮不判，补测或换灰阶再跑。"
            : "自检未过 ⇒ 本轮【没判】，不是通过。",
    };

    private static double Median(double[] xs)
    {
        var sorted = (double[])xs.Clone();
        Array.Sort(sorted);
        int n = sorted.Length;
        return n == 0 ? double.NaN : (n & 1) == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }
}
