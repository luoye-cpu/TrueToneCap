// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Models/CaptureResult.cs
// 统一捕获结果 DTO — 封装 WGC 捕获的 HDR/SDR 数据

using TrueToneCap.Core.Capture;
using TrueToneCap.Core.Processing;
using Vortice.Direct3D11;

namespace TrueToneCap.App.Models;

/// <summary>捕获结果：包含像素数据和元数据。</summary>
public sealed class CaptureResult : IDisposable
{
    private bool _disposed;

    /// <summary>HDR 浮点像素 (R16G16B16A16_Float → float[])，无 HDR 时为 null。</summary>
    public float[]? HdrPixels { get; init; }

    /// <summary>SDR 字节像素 (BGRA8)，用于预览。</summary>
    public byte[]? SdrPixels { get; init; }

    /// <summary>GPU 纹理（D3D11 纹理引用，用于 GPU 直通编码路径）。
    /// 当此字段不为 null 时，编码器应优先使用纹理而非 CPU 像素数组，
    /// 避免 GPU→CPU→GPU 往返。
    /// 为 null = 调用方没要这张快照（选区预览，WgcCaptureConfig.WantGpuTexture=false）
    /// 或这一拍取不到；两种情况都属正常，不是错误信号。</summary>
    public ID3D11Texture2D? GpuTexture { get; init; }

    /// <summary>是否有 HDR 浮点数据。</summary>
    public bool IsHdr => HdrPixels is not null;

    /// <summary>这一拍是否拿到了 HDR 数据 —— 路线 B 的像素可能还在共享槽里，
    /// 等选区定下来才按矩形回读（此时 <see cref="HdrPixels"/> 为 null，但帧是真的有）。</summary>
    public bool HasHdr => HdrPixels is not null || SharedHandle != 0;

    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>显示器 ICC Profile（从 WCS 获取）。</summary>
    public byte[]? IccProfile { get; set; }

    /// <summary>捕获来源显示器信息。</summary>
    public DisplayInfo? SourceDisplay { get; set; }

    /// <summary>路线 B：这一帧的跨设备共享纹理句柄（0 = 没取到槽）。
    /// 取走即冻结，直到 <see cref="TakeShared"/> 的调用方或 <see cref="Dispose"/> 交还为止。</summary>
    public nint SharedHandle { get; init; }
    public int SharedWidth { get; init; }
    public int SharedHeight { get; init; }

    /// <summary>路线 B（SDR 半）：这一帧的 8-bit 共享槽句柄（0 = 没取到槽）。
    /// <para>与 <see cref="SharedHandle"/> <b>成对</b>取出、成对交还，但**不是同一帧**：两块槽来自两个
    /// 独立的 WGC 会话（SDR 与 Float16），它们各自到货、各自冻结 —— 今天的 CPU 路径本来就是两张帧，
    /// 这里不新增承诺。消费侧只许把它当"这次捕获本会拿到的那张 SDR"，不许当"预览正采样着的那张"。</para></summary>
    public nint SdrSharedHandle { get; init; }
    public int SdrSharedWidth { get; init; }
    public int SdrSharedHeight { get; init; }

    private Action? _sharedRelease;
    private Action? _sdrSharedRelease;

    /// <summary>认领共享槽（所有权移交调用方），返回句柄；没取到槽返回 0。</summary>
    public nint TakeShared(out int width, out int height)
    {
        width = SharedWidth; height = SharedHeight;
        _sharedRelease = null;   // 交还责任转给调用方（预览窗的生命周期上）
        return SharedHandle;
    }

    /// <summary>认领 SDR 共享槽（与 <see cref="TakeShared"/> 同一套所有权纪律）。</summary>
    public nint TakeSharedSdr(out int width, out int height)
    {
        width = SdrSharedWidth; height = SdrSharedHeight;
        _sdrSharedRelease = null;
        return SdrSharedHandle;
    }

    /// <summary>会话侧登记交还动作。没被 <see cref="TakeShared"/> 认领时，Dispose 负责交还 ——
    /// 扫描路径拿不动这台预览，槽不释放就会一直冻着。</summary>
    internal void SetSharedRelease(Action? release) => _sharedRelease = release;

    /// <summary>同上，SDR 那块槽的交还动作。</summary>
    internal void SetSdrSharedRelease(Action? release) => _sdrSharedRelease = release;

    /// <summary>捕获耗时（毫秒）。</summary>
    public long CaptureTimeMs { get; set; }

    /// <summary>捕获质量降级警告 (非空 = 可能有功能降级，UI 应向用户提示)。
    /// 如: WGC CreateForMonitor 失败回退到此仅捕获壁纸 (不含应用窗口)。</summary>
    public string? DegradationWarning { get; set; }

    /// <summary>获取可用于显示/编码的 BGRA8 像素。HDR 帧会自动执行色调映射。</summary>
    /// <param name="toneParams">色调映射参数，默认分段 Reinhard (与主管线一致)。
    /// 注意: 必须用构造函数 (保留默认 PaperWhite=200/MaxNits=1000), 对象初始化器会置 0。</param>
    public byte[]? GetDisplayPixels(TrueToneCap.Core.Processing.ToneMappingParams? toneParams = null)
    {
        if (SdrPixels is not null) return SdrPixels;
        if (HdrPixels is not null)
            return TrueToneCap.Core.Processing.ToneMapper.FloatToSRgbBytes(HdrPixels, Width, Height,
                toneParams ?? new TrueToneCap.Core.Processing.ToneMappingParams(TrueToneCap.Core.Processing.ToneMapMode.SegmentedReinhard));
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { (GpuTexture as IDisposable)?.Dispose(); } catch { }
        // 没被认领的共享槽在这里交还（被认领时 _sharedRelease 已被置空）
        var rel = _sharedRelease; _sharedRelease = null;
        try { rel?.Invoke(); } catch { }
        // SDR 那块槽同一套纪律：没认领就由这里交还，否则它会一直冻着、下一帧起再也不更新。
        var relSdr = _sdrSharedRelease; _sdrSharedRelease = null;
        try { relSdr?.Invoke(); } catch { }
    }
}
