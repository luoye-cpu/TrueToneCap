// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Processing/D3DDeviceLost.cs
// D3D11「设备丢失」（Device Removed / Device Reset）的判定辅助。
//
// 为什么需要它:
//   GPU 驱动重置（TDR）、显卡切换（独显/核显）、远程桌面会话变更、驱动升级后，
//   D3D11 设备会被标记为"已移除"。此后所有 Present / Map / CopyResource 都会失败，
//   而**窗口不会报错、只是永久黑屏** —— 用户只能杀掉进程重开。
//
//   本项目此前【全仓没有任何 DeviceRemoved / DXGI_ERROR_DEVICE 的判定】
//   （2026-09-15 审查确认）：HdrCaptureWindow.RenderCore 的 catch 只写一行日志，
//   于是每帧都在一个已死的设备上重复失败，界面静默变黑且无法自愈。
//
// 设计要点:
//   ① 判定是**纯函数**（IsDeviceLostHResult），不依赖任何 COM 调用，可单测、零副作用；
//   ② 查询设备状态（IsDeviceRemoved）自身也可能抛异常（设备已彻底失效），故内部兜底；
//   ③ 不负责重建 —— 重建涉及交换链/纹理/着色器的整体重建，由各窗口自行编排，
//      这里只提供"该不该重建"的可靠判据。

using System;
using Vortice.Direct3D11;

namespace TrueToneCap.Core.Processing;

/// <summary>D3D11 设备丢失判定与诊断。</summary>
public static class D3DDeviceLost
{
    // ── HRESULT 常量（十进制）──
    // 取自 Windows SDK winerror.h / dxgitype.h。硬编码而不引用 Vortice 枚举，
    // 是为了让判定逻辑与具体的封装版本解耦，便于单测。
    public const int S_OK = 0;
    /// <summary>DXGI_ERROR_DEVICE_REMOVED：设备已被移除（驱动重置/显卡拔出）。</summary>
    public const int DXGI_ERROR_DEVICE_REMOVED = unchecked((int)0x887A0005);
    /// <summary>DXGI_ERROR_DEVICE_RESET：设备重置失败，需重建。</summary>
    public const int DXGI_ERROR_DEVICE_RESET = unchecked((int)0x887A0007);
    /// <summary>DXGI_ERROR_DRIVER_INTERNAL_ERROR：驱动内部错误。</summary>
    public const int DXGI_ERROR_DRIVER_INTERNAL_ERROR = unchecked((int)0x887A0020);
    /// <summary>D3DDDIERR_DEVICEREMOVED：D3D 运行时报告设备已移除。</summary>
    public const int D3DDDIERR_DEVICEREMOVED = unchecked((int)0x88760870);
    /// <summary>E_OUTOFMEMORY：显存不足，某些驱动会以此报告不可恢复状态。</summary>
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);

    /// <summary>
    /// 判定 HRESULT 是否属于"设备丢失/重置"类错误 —— 即必须重建设备才能恢复。
    /// </summary>
    /// <remarks>
    /// 刻意不把 <see cref="E_OUTOFMEMORY"/> 纳入：显存不足往往是可以通过缩小资源
    /// 恢复的，与"设备物理消失"性质不同，混为一谈会导致过度重建。
    /// </remarks>
    public static bool IsDeviceLostHResult(int hr) => hr switch
    {
        DXGI_ERROR_DEVICE_REMOVED => true,
        DXGI_ERROR_DEVICE_RESET => true,
        DXGI_ERROR_DRIVER_INTERNAL_ERROR => true,
        D3DDDIERR_DEVICEREMOVED => true,
        _ => false
    };

    /// <summary>把 HRESULT 转成便于日志排查的名称（未知码返回十六进制）。</summary>
    public static string GetHResultName(int hr) => hr switch
    {
        S_OK => "S_OK",
        DXGI_ERROR_DEVICE_REMOVED => "DXGI_ERROR_DEVICE_REMOVED",
        DXGI_ERROR_DEVICE_RESET => "DXGI_ERROR_DEVICE_RESET",
        DXGI_ERROR_DRIVER_INTERNAL_ERROR => "DXGI_ERROR_DRIVER_INTERNAL_ERROR",
        D3DDDIERR_DEVICEREMOVED => "D3DDDIERR_DEVICEREMOVED",
        E_OUTOFMEMORY => "E_OUTOFMEMORY",
        _ => $"0x{hr:X8}"
    };

    /// <summary>
    /// 从异常中提取 HRESULT。支持 COMException / ExternalException，
    /// 以及任何设置了 HResult 的异常。
    /// </summary>
    /// <returns>取到非默认 HResult 时返回 true。</returns>
    public static bool TryGetHResult(Exception? ex, out int hr)
    {
        hr = 0;
        if (ex is null) return false;
        // 优先用运行时提供的 HResult（.NET 已把 COM 失败码映射到这里）
        hr = ex.HResult;
        return hr != 0 && hr != 1;   // 1 = COR_E_APPLICATION 之类的默认值，无诊断价值
    }

    /// <summary>
    /// 判定异常（含内部异常链）是否由设备丢失引起。
    /// </summary>
    public static bool IsDeviceLostException(Exception? ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (TryGetHResult(e, out int hr) && IsDeviceLostHResult(hr)) return true;
        }
        return false;
    }

    /// <summary>
    /// 主动查询设备是否已被移除（DeviceRemovedReason != S_OK）。
    /// </summary>
    /// <remarks>
    /// 相比"等异常"，主动查询更可靠：某些驱动下 Present 会**静默失败**
    /// （不抛异常、画面不更新），只有 DeviceRemovedReason 能暴露问题。
    /// 该属性在设备彻底失效时自身也可能抛异常 —— 这里一律视为"已移除"。
    /// </remarks>
    public static bool IsDeviceRemoved(ID3D11Device? device)
    {
        if (device is null) return true;   // 没有设备 == 不可用
        try
        {
            var r = device.DeviceRemovedReason;
            // Result/ResultCode 都实现了 Success 语义；这里取 Code 统一判定
            return !r.Success;
        }
        catch
        {
            // 连查询都失败 → 设备必然已不可用
            return true;
        }
    }

    /// <summary>
    /// 综合判定：先查设备的移除原因（最可靠），再看异常是否为设备丢失类。
    /// 任一命中即认为需要重建。
    /// </summary>
    public static bool ShouldRebuild(ID3D11Device? device, Exception? ex)
        => IsDeviceRemoved(device) || IsDeviceLostException(ex);

    /// <summary>
    /// 生成便于排查的诊断文本（含 HRESULT 名称），用于日志。
    /// </summary>
    public static string Describe(ID3D11Device? device, Exception? ex)
    {
        string dev = "device=<null>";
        if (device is not null)
        {
            try
            {
                int code = device.DeviceRemovedReason.Code;
                dev = $"DeviceRemovedReason={GetHResultName(code)}";
            }
            catch (Exception q) { dev = $"DeviceRemovedReason=<查询失败: {q.Message}>"; }
        }
        string exPart = "none";
        if (ex is not null && TryGetHResult(ex, out int hr))
            exPart = $"{ex.GetType().Name}({GetHResultName(hr)})";
        else if (ex is not null)
            exPart = ex.GetType().Name;
        return $"{dev}, exception={exPart}";
    }
}
