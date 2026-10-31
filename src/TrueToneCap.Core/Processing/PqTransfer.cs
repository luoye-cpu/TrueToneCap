// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
using System;

namespace TrueToneCap.Core.Processing;

/// <summary>
/// ST.2084 (PQ) 编码的查表实现。存在的全部理由：HDR 每条产物腿都要对整帧做 PQ，
/// 而原式是每通道两个 <see cref="MathF.Pow"/>——4K 一帧 2,488 万次 PQ。
/// </summary>
/// <remarks>
/// <para><b>形状</b>：PQ 在 L→0 处斜率发散，均匀分段的表在这个域上不成立（实测 10-bit
/// 最大偏差几百码）。改按 s = L^(1/4) 均匀建表 + 线性插值，两次 sqrt 换掉两次 pow。</para>
/// <para><b>精度口径</b>（2026-10-31 本机实测，见 docs/reviews/performance/full-pipeline-review-2026-10-31.md §2.4）：
/// 4K 帧 134.6ms → 58.4ms（2.30×），同形状纯内存下限 40.2ms；10-bit 码字最大偏差 1，
/// 全域 2,000 万次密扫只有 0.062% 的取值与逐像素 pow 不同码。</para>
/// <para><b>位深上限</b>：偏差随目标码数线性放大，16-bit 容器（65535 级）会远超 1 码 ⇒
/// 只到 12-bit 用表，更深的位深走原式（<see cref="EncodeExact"/>）。</para>
/// <para><b>被否证的那条路</b>，别再走：把判界表（每个输出码对应一条 L 门限）+ 二分做出来
/// 是 0.88× —— 比逐像素 pow 还慢，而且 2,000 万密扫仍有 8,865 处不同码（float 边界）。</para>
/// </remarks>
internal static class PqTransfer
{
    // 与 FormatHelper 逐字同一组 ST.2084 常数。
    private const double M1 = 2610.0 / 16384.0, M2 = 2523.0 / 32.0;
    private const double C1 = 3424.0 / 4096.0, C2 = 2413.0 / 128.0, C3 = 2392.0 / 128.0;

    /// <summary>表分段数（s = L^(1/4) 上的均匀格数）。4097 个 float ≈ 16KB，一次建表 4,097 次 double pow。</summary>
    private const int Segments = 4096;

    /// <summary>查表适用的最大量化码数：12-bit 容器是 4095。超过它（16-bit）必须走原式。</summary>
    private const int TableMaxQuantum = 4095;

    private static readonly float[] s_table = BuildTable();

    /// <summary>TTC_PQ_TABLE=0 回到逐像素 pow（诊断/A-B 用）。</summary>
    private static readonly bool s_tableEnabled =
        Environment.GetEnvironmentVariable("TTC_PQ_TABLE") != "0";

    private static float[] BuildTable()
    {
        var t = new float[Segments + 1];
        for (int i = 0; i <= Segments; i++)
        {
            double s = (double)i / Segments;
            t[i] = (float)PqDouble(s * s * s * s);
        }
        return t;
    }

    private static double PqDouble(double L)
    {
        double Lp = Math.Pow(L, M1);
        return Math.Pow((C1 + C2 * Lp) / (1.0 + C3 * Lp), M2);
    }

    /// <summary>scRGB 线性光 → PQ 感知量化（0..1）。按位深选择查表或原式。</summary>
    public static float Encode(float scRgbLinear, int maxValue)
    {
        if (!s_tableEnabled || maxValue > TableMaxQuantum) return EncodeExact(scRgbLinear);
        return EncodeTable(scRgbLinear);
    }

    /// <summary>原式：两个 MathF.Pow。与改动前的产品行为逐字节同源，也是查表的对照臂。
    /// 常数取 float 形态，与 FormatHelper 那份 (2610f/16384f 等) 逐字相同——分母全是 2 的幂，
    /// float 与 double 求值同值，但形状一致才让"对照臂就是旧实现"这句话成立。</summary>
    public static float EncodeExact(float scRgbLinear)
    {
        float m1 = 2610f / 16384f, m2 = 2523f / 32f;
        float c1 = 3424f / 4096f, c2 = 2413f / 128f, c3 = 2392f / 128f;
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, m1);
        float numerator = c1 + c2 * Lp;
        float denominator = 1f + c3 * Lp;
        return MathF.Pow(numerator / denominator, m2);
    }

    /// <summary>查表臂：s = L^(1/4)（两次 sqrt，硬件指令）落在哪一格，格内线性插值。</summary>
    public static float EncodeTable(float scRgbLinear)
    {
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = nits / 10000f;
        if (L <= 0f) return 0f;
        if (L >= 1f) return 1f;

        float s = MathF.Sqrt(MathF.Sqrt(L));
        float pos = s * Segments;
        int i = (int)pos;
        // s < 1 ⇒ i < Segments；只有 L 恰为 1 时才会碰到 Segments，上面已经截掉。
        float f = pos - i;
        float lo = s_table[i];
        return lo + (s_table[i + 1] - lo) * f;
    }

    /// <summary>开关当前状态，供判据与日志印出两侧输入（不许"看起来用了表其实没用"）。</summary>
    public static bool UsingTable => s_tableEnabled;

    public static int SegmentsCount => Segments;
}
