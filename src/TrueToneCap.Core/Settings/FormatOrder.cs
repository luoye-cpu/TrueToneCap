// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Settings/FormatOrder.cs
// 输出格式下拉的【唯一顺序事实源】+ 旧表→新表的一次性重映射。
//
// 为什么这件事必须待在 Core 而不是 App：
// 2026-09-26 把「JPEG Gain Map (HDR)」从格式列表里取消、并入 JPEG LI 之后，
// `AppSettingsData.FormatIndex` 这个【裸索引】的含义变了 —— 旧表里 1 是 GainMap、2 是 JPEG LI，
// 新表里 1 是 JPEG LI。而全仓对 FormatIndex 唯一的防线是"越界钳制"（`is < 0 or > 6`），
// 删掉一项之后旧值 1 与默认值 2 都仍然落在合法区间内 ⇒ 钳制【永不触发】，
// 表现是"GainMap 用户静默变成 JPEG LI、JPEG LI 用户静默变成 JPEG XL"，且没有任何提示。
// 同型缺陷在 ColorSpaceIndex 上发生过一次（那次靠 legacyTags 表修，见 MainWindow.Settings.cs）。
//
// 重映射表是"用户设置会不会被静默改掉"的关键判据，必须能被门禁【执行】而不是被"读到"：
// 放 App 里就只能写源码哨兵（钉得住写法，钉不住语义），放 Core 才能直接对撞真值。

using TrueToneCap.Core.Encoding;

namespace TrueToneCap.Core.Settings;

/// <summary>格式下拉的顺序与历史迁移。</summary>
public static class FormatOrder
{
    /// <summary>
    /// 当前界面格式下拉的顺序（索引就是 <c>AppSettingsData.FormatIndex</c> 持久化的值）。
    /// <para>⚠ <see cref="OutputFormat.JPEG_GAINMAP"/> 刻意【不在】这张表里：
    /// 它不再是用户可选项，而是"JPEG LI + 增益图开关"的内部形态。
    /// 因此本表长度（<see cref="Count"/>）就是 FormatIndex 的合法上界，
    /// 钳制必须按它来、不能写死数字。</para>
    /// </summary>
    public static readonly OutputFormat[] UiFormats =
    [
        OutputFormat.PNG,
        OutputFormat.JPEG_LI,
        OutputFormat.JPEG_XL,
        OutputFormat.AVIF,
        OutputFormat.WebP,
        OutputFormat.TIFF,
    ];

    /// <summary>合法索引上界（= 表长 - 1）。</summary>
    public static int MaxIndex => UiFormats.Length - 1;

    /// <summary>默认格式在表里的位置：JPEG LI。</summary>
    public static int DefaultIndex => System.Array.IndexOf(UiFormats, OutputFormat.JPEG_LI);

    /// <summary>索引 → 格式；越界回落默认（JPEG LI），不抛。</summary>
    public static OutputFormat FormatAt(int index) =>
        index >= 0 && index <= MaxIndex ? UiFormats[index] : UiFormats[DefaultIndex];

    /// <summary>格式 → 索引；不在表里（如 GainMap 的内部形态）返回 -1。</summary>
    public static int IndexOf(OutputFormat f) => System.Array.IndexOf(UiFormats, f);

    /// <summary>
    /// 旧表（GainMap 仍是独立格式时）的索引含义。顺序必须与当时
    /// <c>MainWindow._formats</c> 完全一致 —— 这张表就是"用户盘上那个数字原本指谁"的证词。
    /// </summary>
    public static readonly OutputFormat[] LegacyUiFormats =
    [
        OutputFormat.PNG,
        OutputFormat.JPEG_GAINMAP,
        OutputFormat.JPEG_LI,
        OutputFormat.JPEG_XL,
        OutputFormat.AVIF,
        OutputFormat.WebP,
        OutputFormat.TIFF,
    ];

    /// <summary>
    /// 把旧表的 <paramref name="legacyIndex"/> 翻译成【新表索引 + 增益图开关】。
    /// <para>刻意返回成对结果而不是只返回索引：旧 1 号（GainMap）迁移后必须是
    /// "JPEG LI 且增益图开"，只搬索引会把它降成普通 JPEG LI —— 那是静默丢功能。</para>
    /// <para>越界（含负数）回落到"默认格式 + 开关关"，由调用方决定是否记日志。</para>
    /// </summary>
    public static (int Index, bool GainMapEnabled) RemapLegacy(int legacyIndex)
    {
        var f = legacyIndex >= 0 && legacyIndex < LegacyUiFormats.Length
            ? LegacyUiFormats[legacyIndex]
            : OutputFormat.JPEG_LI;
        bool gm = f == OutputFormat.JPEG_GAINMAP;
        int neu = IndexOf(f == OutputFormat.JPEG_GAINMAP ? OutputFormat.JPEG_LI : f);
        return (neu < 0 ? DefaultIndex : neu, gm);
    }
}
