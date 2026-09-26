// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/MainWindow.ColorManagement.cs
// MainWindow 的「色彩管理 UI」职责（partial class 拆分）
//
// 拆分说明（2026-08-29）：MainWindow.xaml.cs 过大，按职责拆分为多个 partial class 文件。
// 本文件承载色彩管理相关逻辑：源色域检测、色域映射 UI 提示、色彩空间标签读取。
//
// ⚠ partial class 拆分不改变运行时行为：所有成员仍属同一个 MainWindow 类型，
//   私有字段（_settings / ColorCbo / HdrSwitch 等）跨文件直接可见。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 「色彩与 HDR」+「色域映射」两张卡的<b>唯一</b>刷新点：一次 <see cref="ColorDecision"/>，
    /// 源行 / 目标行 / 映射说明三处都从这一份渲染。
    /// <para><b>原先的形状</b>（2026-09-26 修）：源那一行问的是【主屏】（ACM 那一支却又单独去问光标屏
    /// 的 EDID），映射行再把自己刚写下的那行中文文案用 <c>Contains</c> 反推回来当输入。于是
    /// ①一张卡上两行来自两块屏；②换一套文案，判定就跟着变；③Core 里那三项收口
    /// （屏幕不授权 / 目标是 sRGB / 格式没有 HDR 通道）在这里<b>一项都看不见</b>，HDR 一开就写"直通"。</para>
    /// <para>现在这里不做任何色彩判定：判定在 <see cref="ColorDecision.Build"/>，
    /// 交付形态在 <see cref="FormatColorFacts.Delivery"/>，声明通道在
    /// <see cref="FormatColorFacts.Declaration"/>，本函数只负责把三者印出来。</para>
    /// </summary>
    private void RefreshColorMappingUi()
    {
        var ui = CaptureEncodingUiState();
        // 与编码入口同一条构造路径（光标屏；粘贴/预览这些入口本来就没有"被截那块屏"的概念）。
        var decision = DecisionForCursorScreen(ui, ui.HdrEnabled);
        var delivery = FormatColorFacts.Delivery(decision, ui.Format);
        bool writesPq = delivery == HdrDelivery.PqContainer;

        SourceGamutTxt.Text = DescribeSourceDisplay(decision);
        TargetGamutTxt.Text = DescribeTargetGamut(decision, ui.Format);
        MappingArrow.Text = !ui.HdrEnabled
            ? (decision.NeedsMapping ? "→ 色域映射到" : "→ 直通（同色域）")
            : delivery switch
            {
                HdrDelivery.PqContainer => "→ HDR 直通（容器写 PQ）",
                HdrDelivery.GainMap => "→ 增益图（SDR 底图 + gain map）",
                HdrDelivery.BlockedByDisplay => "→ 色调映射为 SDR（屏幕证据不授权 HDR 声称）",
                HdrDelivery.BlockedBySrgbTarget => "→ 色调映射为 SDR（sRGB 容器装不下 PQ）",
                _ => "→ 色调映射为 SDR（此格式没有 HDR 通道）",
            };
        GamutMapHintTxt.Text = BuildGamutHint(ui.Format, decision, delivery, writesPq);
    }

    /// <summary>源那一行：按<b>三档分类</b>印，而不是按"开关组合"拼字符串。</summary>
    private static string DescribeSourceDisplay(ColorDecision d)
    {
        var src = d.Source;
        if (src is null)
            return "读不到被截屏的逐屏证据 ⇒ 不授权广色域/HDR 声称（仍放行 Float16 源）";

        string bits = $"{src.BitsPerColor}-bit";
        string native = src.NativeGamutKnown
            ? ColorProfileProvider.GetColorSpaceDisplayName(src.NativeGamutTag)
            : "EDID 未读到原色（不猜）";
        return d.ScreenClass switch
        {
            DisplayEnumerator.DisplayColorClass.Hdr => $"HDR（{bits}，PQ，面板原生 {native}）",
            DisplayEnumerator.DisplayColorClass.WideGamutSdr =>
                $"SDR 广色域（{native}，{bits}，ACM）"
                + (src.SupportsHdr && !src.IsHdr ? " —— 面板支持 HDR，系统未开启" : ""),
            DisplayEnumerator.DisplayColorClass.NarrowSdr => $"SDR sRGB（{bits}）",
            _ => $"逐屏证据不足（{bits}）⇒ 不授权广色域/HDR 声称",
        };
    }

    /// <summary>目标那一行。增益图形态下这个值管的是<b>底图</b>色域（用户 2026-09-26 口径）。</summary>
    private static string DescribeTargetGamut(ColorDecision d, OutputFormat format)
    {
        string name = d.TargetTag == "BT2020" ? "BT.2020" : "sRGB";
        return format == OutputFormat.JPEG_GAINMAP ? $"{name}（底图色域）" : name;
    }

    /// <summary>灰字那一行：管线在干什么 + 这个格式能把声明写成什么 + 有没有被收口。</summary>
    private static string BuildGamutHint(OutputFormat format, ColorDecision d,
        HdrDelivery delivery, bool writesPq)
    {
        var parts = new System.Collections.Generic.List<string>();
        parts.Add(delivery switch
        {
            HdrDelivery.PqContainer => "WGC Float16 → 色域矩阵 → PQ ST.2084。",
            HdrDelivery.GainMap =>
                "WGC Float16 → 分段 Reinhard 出 SDR 底图，另算一张增益图；HDR 在增益图里，不在容器声明里。",
            HdrDelivery.BlockedByDisplay =>
                "本次按 SDR 色调映射：HDR 开关仍按你的设置保留，只是这块屏的逐屏证据不足以声明 PQ。",
            HdrDelivery.BlockedBySrgbTarget =>
                "本次按 SDR 色调映射：目标被解析/收口成 sRGB，而 PQ 只能与广色域原色一起声明。",
            HdrDelivery.BlockedByFormat =>
                "本次按 SDR 色调映射：这个格式的容器没有 HDR 通道（HDR 开关会落空，产物是 8-bit SDR）。",
            _ => d.NeedsMapping
                ? (d.ExtendedRangeAuthorized
                    ? "WGC Float16 捕获完整广色域 → 3×3 矩阵搬到目标 → 分段 Reinhard → SDR 输出。"
                    : "8-bit 源 → ICC/矩阵搬到目标 → SDR 输出。")
                : "源与目标同一套原色 ⇒ 矩阵是恒等变换，本次不跑搬色。",
        });
        parts.Add(FormatColorFacts.Declaration(format, d.TargetTag, writesPq));
        if (d.GamutClamped)
            parts.Add($"⚠ 广色域声明已被逐屏证据收口为 sRGB ⇒ {d.Evidence}");
        if (d.HdrClamped)
            parts.Add($"⚠ HDR 声称已被收口 ⇒ {d.Evidence}");
        return string.Join(" ", parts);
    }


    /// <summary>从 ColorCbo 获取当前选择的色彩空间标签。
    /// <para>
    /// ⚠ 仅供 UI 线程调用。后台编码线程请改用 <see cref="CaptureEncodingUiState"/> 采集的
    /// <c>EncodingUiState.ColorSpaceTag</c> —— 跨线程访问 WinUI 控件会抛异常，
    /// 而此处的空 catch 会把异常吞掉并静默返回 "System"，
    /// 导致用户选了 Display P3 / BT.2020 却实际输出 System 色域且无任何提示。
    /// </para>
    /// </summary>
    private string GetSelectedColorSpaceTag()
    {
        try
        {
            if (ColorCbo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
        }
        catch { }
        return "System";
    }
}
