// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Settings/HdrOutputDefault.cs
//
// "HDR 下默认应该开启 HDR 输出"（2026-10-27 用户口径）的判定本体。
//
// 为什么单独成函数：
//   · 首运行那条（FirstRun ⇒ HdrEnabled = 显示器支持 HDR）只跑一次。首运行落在 SDR 屏、
//     或 EDID 没读出来（SupportsHdr=false）的用户，后来换了 HDR 屏，盘上的 false 会永久保留 ——
//     本机实机日志就是这形态：显示器枚举 "HDR当前启用=True, HDR硬件支持=True"，
//     而 settings.json 是 "HdrEnabled":false。
//   · 反过来，"用户亲手关过"必须永久尊重（状态粘住会永久翻转偏好，这个仓在这上面栽过）。
//   · 两条方向要能各自单独验：放在 MainWindow 的 else-if 里只能靠源码字符串猜，
//     抽到这里就能用真值表钉住（CorePipelineTests 的 HdrOutputDefault 那一组）。
namespace TrueToneCap.Core.Settings;

public static class HdrOutputDefault
{
    /// <summary>本次启动是否应按默认把 HDR 输出打开。</summary>
    /// <param name="supportsHdr">显示器（硬件/EDID）支持 HDR。与"Windows HDR 当前是否启用"无关：
    /// 硬件支持就允许输出 HDR —— 这条与首运行分支同一口径。</param>
    /// <param name="hdrEnabled">盘上当前值。</param>
    /// <param name="userToggled">用户是否亲手拨过 HDR 开关（<c>AppSettingsData.HdrUserToggled</c>）。
    /// 拨过一次就永不再自动翻转，含"关"。</param>
    public static bool ShouldAutoEnable(bool supportsHdr, bool hdrEnabled, bool userToggled)
        => supportsHdr && !hdrEnabled && !userToggled;
}
