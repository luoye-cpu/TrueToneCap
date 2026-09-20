// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/EncoderFallbackNotifier.cs
// 编码器"静默降级"的统一留痕通道 —— 只记录人话说明，不改变任何编码行为。

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// 编码器静默降级的原因（编码线程写、UI 线程读）。
/// <para><b>为什么要它</b>：本仓库反复出现同一类缺陷 —— 编码器在后台换了参数或换了格式，
/// 却无人告知。三处已确认的实例（2026-09-19）：MFT/QSV 失败回退 libaom（Release 下只有
/// <c>Debug.WriteLine</c>，等于完全无声，而 4K 上软件约 22s vs 硬件约 0.7s）、cwebp 缺失时<b>悄悄改写成
/// .png</b>（除文件名外没有任何原因可查）、所请求位深被 avifenc 拒绝时<b>悄悄按 8-bit 重试</b>。
/// 用户看到的只是"文件出来了"，差别无从得知。</para>
/// <para><b>为什么不复用 <see cref="NvencAvifBackend.LastFallbackReason"/></b>：那个字段是 NVENC 专用语义
/// （"本可用硬件却退到软件"），上层据此给出的建议（重新启用硬件编码等）只对 NVENC 成立；
/// 把"改存成 PNG""降成 8-bit"这类事情塞进去会污染它的含义。故另开一个只承载
/// "换了格式 / 换了参数 / 换了路径"的通用通道，两边互不覆盖。</para>
/// <para><b>写入方只写不改行为</b>：本类的赋值一律发生在既有回退分支内，回退本身与修复前完全一致；
/// 读取与展示由上层负责接线。</para>
/// </summary>
public static class EncoderFallbackNotifier
{
    /// <summary>
    /// 最近一次静默降级的说明；null = 没有待读的降级。
    /// <para>volatile：编码线程写、UI 线程读 —— 与 <c>NvencAvifBackend.LastFallbackReason</c> 同型。
    /// 上层读完请置 null，避免陈旧原因被下一次编码误读。</para>
    /// </summary>
    public static volatile string? LastReason;
}
