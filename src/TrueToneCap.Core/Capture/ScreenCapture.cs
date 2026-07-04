using System.Runtime.InteropServices;
using System.Linq;
using Vortice.DXGI;
using Vortice.Direct3D11;
using Vortice.Direct3D;
using Vortice.Mathematics;

namespace TrueToneCap.Core.Capture;

/// <summary>诊断日志条目。</summary>
public sealed record CaptureLogEntry(
    DateTime Timestamp, string Level, string Message, string? ErrorCode = null, int Retry = 0);

/// <summary>捕获事件类型。</summary>
public enum CaptureEvent { Started, Succeeded, Timeout, AccessLost, HdrFallback, DeviceRecreated, Error }

/// <summary>捕获事件参数。</summary>
public sealed class CaptureEventArgs : EventArgs
{
    public CaptureEvent Event { get; init; }
    public string? Message { get; init; }
    public int RetryCount { get; init; }
}

/// <summary>使用 DXGI Desktop Duplication 的健壮屏幕捕获器。
/// 带诊断日志、HDR 状态预检、设备丢失重建、锁屏/UAC 优雅降级。
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private const int MaxRetries = 3;
    private const int MaxConsecutiveTimeouts = 60;

    private ID3D11Device _device;
    private IDXGIOutput6? _output;
    private IDXGIOutputDuplication? _duplication;
    private readonly object _lock = new();
    private readonly nint _targetMonitor; // HMONITOR of the target display
    private readonly bool _forceSdr;
    private DisplayInfo? _displayInfo;
    private int _consecutiveTimeouts;
    private int _retryCount;
    private volatile bool _disposed;
    private readonly List<CaptureLogEntry> _log = [];

    public event EventHandler<CaptureEventArgs>? CaptureEventHappened;

    /// <summary>创建捕获器。targetMonitor=0 则使用鼠标所在显示器。forceSdr=true 强制 SDR 捕获。</summary>
    public ScreenCapture(ID3D11Device device, nint targetMonitor = 0, bool forceSdr = false)
    {
        _device = device;
        _targetMonitor = targetMonitor != 0 ? targetMonitor : DisplayEnumerator.GetMonitorUnderCursor();
        _forceSdr = forceSdr;
        WriteLog("INFO", $"ScreenCapture 初始化: Monitor=0x{_targetMonitor:X8} ForceSdr={forceSdr}");
        InitializeDuplication();
    }

    /// <summary>工厂方法。诊断适配器匹配状态并使用默认设备创建。</summary>
    public static ScreenCapture CreateForMonitor(nint targetMonitor, bool forceSdr)
    {
        nint mon = targetMonitor != 0 ? targetMonitor : DisplayEnumerator.GetMonitorUnderCursor();
        var displays = DisplayEnumerator.EnumerateDisplays();
        var di = displays.FirstOrDefault(d => d.MonitorHandle == mon);
        if (di is null)
        {
            if (GetCursorPos(out int cx, out int cy))
                di = displays.FirstOrDefault(d => cx >= d.X && cx <= d.X + d.Width && cy >= d.Y && cy <= d.Y + d.Height);
        }
        if (di is null) throw new InvalidOperationException("找不到目标显示器");

        // 诊断日志
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
        for (uint ai = 0; ; ai++)
        {
            if (factory.EnumAdapters1(ai, out var a).Failure || a is null) break;
            try
            {
                for (uint oi = 0; ; oi++)
                {
                    if (a.EnumOutputs(oi, out var o).Failure || o is null) break;
                    try
                    {
                        var od = o.Description;
                        if (od.AttachedToDesktop && (od.Monitor == di.MonitorHandle ||
                            (Math.Abs(od.DesktopCoordinates.Left - di.X) <= 1 &&
                             Math.Abs(od.DesktopCoordinates.Top - di.Y) <= 1)))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[Capture] {di.Name} → Adapter[{ai}] {a.Description1.Description}");
                            o.Dispose(); a.Dispose();
                            goto done;
                        }
                    }
                    finally { o.Dispose(); }
                }
            }
            finally { a.Dispose(); }
        }
    done:
        // Vortice 3.8.3 不暴露适配器感知的 D3D11CreateDevice 重载
        var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        return new ScreenCapture(device, mon, forceSdr);
    }

    public RectI OutputRect => _displayInfo is null
        ? new RectI(0, 0, 1920, 1080)
        : new RectI(_displayInfo.X, _displayInfo.Y,
            _displayInfo.X + _displayInfo.Width, _displayInfo.Y + _displayInfo.Height);

    public nint MonitorHandle => _displayInfo?.MonitorHandle ?? 0;
    public Format CaptureFormat { get; private set; } = Format.B8G8R8A8_UNorm;
    public bool IsHdrCapture { get; private set; }
    public IReadOnlyList<CaptureLogEntry> Log => _log;

    // ═══════════════════════════════════════
    // 初始化
    // ═══════════════════════════════════════

    private void InitializeDuplication()
    {
        var displays = DisplayEnumerator.EnumerateDisplays();
        WriteLog("DEBUG", $"枚举到 {displays.Count} 个显示器, 目标 HMONITOR=0x{_targetMonitor:X8}");

        // 验证：目标 HMONITOR 是否有效（排除虚拟/远程桌面显示器）
        var targetInfo = GetMonitorInfo(_targetMonitor);
        if (targetInfo is null)
        {
            WriteLog("ERROR", $"HMONITOR 0x{_targetMonitor:X8} 无效或无法获取信息，使用坐标匹配");
            _displayInfo = FindDisplayByCoordinates(displays);
        }
        else
        {
            WriteLog("DEBUG", $"HMONITOR 信息: Flags=0x{targetInfo.Value.Flags:X8} " +
                $"Rect=({targetInfo.Value.RcMonitor.Left},{targetInfo.Value.RcMonitor.Top})-({targetInfo.Value.RcMonitor.Right},{targetInfo.Value.RcMonitor.Bottom})");

            // 排除伪显示器 (Flags=0 通常为虚拟/远程桌面驱动)
            if (targetInfo.Value.Flags == 0 || targetInfo.Value.RcMonitor.Width == 0)
            {
                WriteLog("WARN", $"HMONITOR 0x{_targetMonitor:X8} 可能是虚拟/远程显示器 (Flags=0x{targetInfo.Value.Flags:X8})，使用坐标匹配");
                _displayInfo = FindDisplayByCoordinates(displays);
            }
            else
            {
                // 策略1: HMONITOR 精确匹配（Win32 HMONITOR 可能与 DXGI Monitor 相同）
                _displayInfo = displays.FirstOrDefault(d => d.MonitorHandle == _targetMonitor);
                if (_displayInfo is null)
                {
                    WriteLog("WARN", "HMONITOR 精确匹配失败 (Win32≠DXGI handle)，尝试坐标匹配...");
                    _displayInfo = FindDisplayByCoordinates(displays);
                }
            }
        }

        // 策略2: 坐标匹配
        if (_displayInfo is null)
        {
            WriteLog("WARN", "坐标匹配失败，回退到主显示器");
            _displayInfo = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
        }

        if (_displayInfo is null)
            throw new InvalidOperationException(
                "未找到任何物理显示器。\n" +
                $"目标 HMONITOR: 0x{_targetMonitor:X8}\n" +
                $"枚举到 {displays.Count} 个 DXGI 输出:\n" +
                string.Join("\n", displays.Select(d =>
                    $"  [{d.Index}] {d.Name} {d.Width}x{d.Height} " +
                    $"Monitor=0x{d.MonitorHandle:X8} HDR={d.IsHdr} " +
                    $"Rect=({d.X},{d.Y})-({d.X+d.Width},{d.Y+d.Height}) " +
                    $"Adapter={d.AdapterName}")));

        WriteLog("INFO", $"匹配成功: [{_displayInfo.Index}] {_displayInfo.Name} " +
            $"{_displayInfo.Width}x{_displayInfo.Height} HDR={_displayInfo.IsHdr} " +
            $"Monitor=0x{_displayInfo.MonitorHandle:X8} Adapter={_displayInfo.AdapterName}");

        bool tryHdr = _displayInfo.IsHdr && !_forceSdr;
        WriteLog("INFO", tryHdr ? "尝试 HDR 捕获 (Float16)" : "使用 SDR 捕获 (BGRA8)");

        if (_device is null)
        {
            _device = D3D11.D3D11CreateDevice(
                DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            WriteLog("DEBUG", "创建新 D3D11 设备");
        }

        // 通过 Monitor 句柄精确查找 DXGI 输出
        _output = FindDxgiOutput(displays);
        if (_output is null)
            throw new InvalidOperationException(
                $"无法为显示器 [{_displayInfo.Index}] {_displayInfo.Name} " +
                $"找到 DXGI 输出。Monitor=0x{_displayInfo.MonitorHandle:X8}");

        CreateDuplication(tryHdr);
        _consecutiveTimeouts = 0;
        _retryCount = 0;
        WriteLog("INFO", $"初始化完成: Format={CaptureFormat} HDR={IsHdrCapture}");
        RaiseEvent(CaptureEvent.Started, $"捕获就绪: {_displayInfo.Name}");
    }

    /// <summary>通过鼠标坐标查找显示器（最可靠的方式）。</summary>
    private static DisplayInfo? FindDisplayByCoordinates(IReadOnlyList<DisplayInfo> displays)
    {
        if (displays.Count == 0) return null;
        if (!GetCursorPos(out int x, out int y))
        {
            System.Diagnostics.Debug.WriteLine("[Capture] GetCursorPos 失败");
            return null;
        }
        // 使用 inclusive 边界
        var match = displays.FirstOrDefault(d =>
            x >= d.X && x <= d.X + d.Width && y >= d.Y && y <= d.Y + d.Height);
        if (match is not null)
            System.Diagnostics.Debug.WriteLine($"[Capture] 坐标匹配: ({x},{y}) → [{match.Index}] {match.Name} {match.Width}x{match.Height}");
        else
            System.Diagnostics.Debug.WriteLine($"[Capture] 坐标匹配失败: ({x},{y}), 可用显示器: {string.Join("; ", displays.Select(d => $"[{d.Index}]({d.X},{d.Y})-({d.X+d.Width},{d.Y+d.Height})"))}");
        return match;
    }

    /// <summary>遍历所有适配器/输出查找匹配的 DXGI 输出。</summary>
    private IDXGIOutput6? FindDxgiOutput(IReadOnlyList<DisplayInfo> displays)
    {
        if (_displayInfo is null) return null;
        WriteLog("DEBUG", $"查找 DXGI 输出: 目标={_displayInfo.Name} Monitor=0x{_displayInfo.MonitorHandle:X8} Coords=({_displayInfo.X},{_displayInfo.Y})-({_displayInfo.X+_displayInfo.Width},{_displayInfo.Y+_displayInfo.Height})");

        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
        for (uint ai = 0; ; ai++)
        {
            if (factory.EnumAdapters1(ai, out var a).Failure || a is null) break;
            bool adapterMatched = false;
            try
            {
                for (uint oi = 0; ; oi++)
                {
                    if (a.EnumOutputs(oi, out var o).Failure || o is null) break;
                    try
                    {
                        var oDesc = o.Description;
                        var oMonitor = oDesc.Monitor;
                        var oRect = oDesc.DesktopCoordinates;
                        bool attached = oDesc.AttachedToDesktop;

                        WriteLog("DEBUG", $"  Adapter[{ai}].Output[{oi}]: Monitor=0x{oMonitor:X8} Attached={attached} " +
                            $"Rect=({oRect.Left},{oRect.Top})-({oRect.Right},{oRect.Bottom})");

                        if (!attached)
                        {
                            WriteLog("DEBUG", $"  跳过: 输出未附加到桌面");
                            o.Dispose();
                            continue;
                        }

                        // 匹配策略 (按优先级):
                        // 1. Monitor 句柄精确匹配
                        bool monitorMatch = oMonitor == _displayInfo.MonitorHandle;
                        // 2. 坐标包容匹配 (含边界容差)
                        bool coordMatch =
                            Math.Abs(oRect.Left - _displayInfo.X) <= 1 &&
                            Math.Abs(oRect.Top - _displayInfo.Y) <= 1 &&
                            Math.Abs(oRect.Right - (_displayInfo.X + _displayInfo.Width)) <= 1 &&
                            Math.Abs(oRect.Bottom - (_displayInfo.Y + _displayInfo.Height)) <= 1;

                        if (monitorMatch || coordMatch)
                        {
                            WriteLog("INFO", $"✅ 匹配 DXGI 输出: Adapter[{ai}].Output[{oi}] " +
                                $"(MonitorMatch={monitorMatch}, CoordMatch={coordMatch})");
                            var output6 = o.QueryInterface<IDXGIOutput6>();
                            o.Dispose();
                            adapterMatched = true;
                            return output6;
                        }
                    }
                    catch (Exception ex)
                    {
                        WriteLog("WARN", $"  Adapter[{ai}].Output[{oi}] 查询异常: {ex.Message}");
                    }
                    o.Dispose();
                }
            }
            catch (Exception ex)
            {
                WriteLog("WARN", $"  Adapter[{ai}] 枚举异常: {ex.Message}");
            }
            a.Dispose();
            if (adapterMatched) break;
        }

        WriteLog("ERROR", $"DXGI 输出未找到。已搜索所有适配器/输出。目标: {_displayInfo.Name}");
        return null;
    }

    // ────── Win32 Monitor 信息 ──────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint CbSize;
        public RECT RcMonitor;
        public RECT RcWork;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(nint hMonitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out int x, out int y);

    private static MONITORINFOEX? GetMonitorInfo(nint hMonitor)
    {
        var info = new MONITORINFOEX { CbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
        return GetMonitorInfoW(hMonitor, ref info) ? info : null;
    }

    private void CreateDuplication(bool tryHdr)
    {
        SafeDispose(ref _duplication);

        // ── SDR 策略 ──
        if (!tryHdr)
        {
            // 关键测试: DuplicateOutput1([BGRA8, Float16]) — 双格式让驱动选择
            try
            {
                var formats = new Format[] { Format.B8G8R8A8_UNorm, Format.R16G16B16A16_Float };
                _duplication = _output!.DuplicateOutput1(_device, formats);
                CaptureFormat = Format.Unknown; // 由首帧检测实际返回格式
                IsHdrCapture = false;
                WriteLog("INFO", $"SDR: DuplicateOutput1([BGRA8,Float16]) 成功 fmtVals={(int)Format.B8G8R8A8_UNorm},{(int)Format.R16G16B16A16_Float}");
                return;
            }
            catch (Exception ex)
            {
                WriteLog("WARN", $"SDR: DuplicateOutput1 双格式失败: {ex.Message}");
            }

            // 回退: DuplicateOutput 无格式
            try
            {
                _duplication = _output!.DuplicateOutput(_device);
                CaptureFormat = Format.Unknown;
                IsHdrCapture = false;
                WriteLog("INFO", "SDR: DuplicateOutput 回退成功");
                return;
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException($"无法创建 SDR 桌面复制: {ex2.Message}", ex2);
            }
        }

        // ── HDR 策略: BGRA8 → Float16 升级 ──
        try
        {
            _duplication = _output!.DuplicateOutput1(_device,
                [(Format)Format.B8G8R8A8_UNorm]);
            CaptureFormat = Format.B8G8R8A8_UNorm;
            IsHdrCapture = false;
            WriteLog("INFO", "HDR: SDR 基础创建成功");

            try
            {
                SafeDispose(ref _duplication);
                _duplication = _output!.DuplicateOutput1(_device,
                    [(Format)Format.R16G16B16A16_Float]);
                CaptureFormat = Format.R16G16B16A16_Float;
                IsHdrCapture = true;
                WriteLog("INFO", "✅ HDR: Float16 升级成功");
            }
            catch (Exception ex)
            {
                WriteLog("WARN", $"HDR: Float16 不支持，保持 SDR: {ex.Message}");
                RaiseEvent(CaptureEvent.HdrFallback, "Float16 不支持，使用 SDR");
                _duplication = _output!.DuplicateOutput1(_device,
                    [(Format)Format.B8G8R8A8_UNorm]);
                CaptureFormat = Format.B8G8R8A8_UNorm;
                IsHdrCapture = false;
            }
            return;
        }
        catch (Exception ex)
        {
            WriteLog("ERROR", $"HDR: 创建失败: {ex.Message}，尝试 DuplicateOutput");
            try
            {
                _duplication = _output!.DuplicateOutput(_device);
                CaptureFormat = Format.Unknown;
                IsHdrCapture = false;
                WriteLog("INFO", "HDR: DuplicateOutput 回退成功");
                return;
            }
            catch (Exception ex2)
            {
                throw new InvalidOperationException(
                    $"无法创建桌面复制: {ex2.Message}", ex2);
            }
        }
    }

    // ═══════════════════════════════════════
    // 重建
    // ═══════════════════════════════════════

    private bool Reinitialize()
    {
        _retryCount++;
        WriteLog("WARN", $"开始重建 (第 {_retryCount}/{MaxRetries} 次)", null, _retryCount);
        RaiseEvent(CaptureEvent.DeviceRecreated, $"重建捕获 ({_retryCount}/{MaxRetries})", _retryCount);

        SafeDispose(ref _duplication);
        SafeDispose(ref _output);

        for (int a = 0; a < MaxRetries; a++)
        {
            try
            {
                InitializeDuplication();
                WriteLog("INFO", $"✅ 重建成功 (尝试 {a + 1})");
                _retryCount = 0;
                return true;
            }
            catch when (a < MaxRetries - 1)
            {
                int delay = 200 * (a + 1);
                WriteLog("WARN", $"重建失败，{delay}ms 后重试...", null, a + 1);
                Thread.Sleep(delay);
            }
            catch (Exception ex)
            {
                WriteLog("ERROR", $"重建最终失败: {ex.Message}", ex.HResult.ToString("X8"));
                return false;
            }
        }
        WriteLog("ERROR", "重建用尽所有重试次数");
        return false;
    }

    // ═══════════════════════════════════════
    // 帧捕获
    // ═══════════════════════════════════════

    public CapturedFrame? TryAcquireNextFrame(int timeoutMs = 16)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            if (_duplication is null)
            {
                WriteLog("WARN", "Duplication 为空，跳过捕获");
                return null;
            }

            try
            {
                var result = _duplication.AcquireNextFrame(
                    (uint)timeoutMs, out var frameInfo, out var resource);

                // ── 超时 ──
                if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                {
                    _consecutiveTimeouts++;
                    if (_consecutiveTimeouts == 1)
                        WriteLog("DEBUG", "首次超时（可能桌面空闲）");
                    if (_consecutiveTimeouts >= MaxConsecutiveTimeouts)
                    {
                        WriteLog("WARN", $"连续超时 {_consecutiveTimeouts} 次 → 桌面可能锁定/UAC");
                        RaiseEvent(CaptureEvent.Timeout, $"连续超时 {_consecutiveTimeouts} 次");
                    }
                    return null;
                }

                // ── 设备丢失 ──
                if (result == Vortice.DXGI.ResultCode.AccessLost)
                {
                    WriteLog("WARN", "DXGI_ERROR_ACCESS_LOST → 重建 Duplication");
                    RaiseEvent(CaptureEvent.AccessLost, "设备丢失");
                    Reinitialize();
                    return null;
                }

                // ── 其他错误 ──
                if (result.Failure || resource is null)
                {
                    WriteLog("WARN", $"AcquireNextFrame 失败: {result.Code:X8}", result.Code.ToString("X8"));
                    return null;
                }

                // ── 成功 ──
                _consecutiveTimeouts = 0;

                using var acquired = resource.QueryInterface<ID3D11Texture2D>();
                var desc = acquired.Description;

                // 自动检测格式（仅当 DuplicateOutput 回退导致格式未知时）
                if (CaptureFormat == Format.Unknown)
                {
                    CaptureFormat = desc.Format;
                    IsHdrCapture = desc.Format == Format.R16G16B16A16_Float;
                    WriteLog("INFO", $"自动检测格式: {desc.Format} (raw={((int)desc.Format)}) HDR={IsHdrCapture}");
                }
                else if (desc.Format != CaptureFormat)
                {
                    WriteLog("WARN", $"⚠ 格式不匹配! 期望={CaptureFormat}({(int)CaptureFormat}) 实际={desc.Format}({(int)desc.Format})");
                }

                var copyDesc = new Texture2DDescription
                {
                    Width = desc.Width, Height = desc.Height,
                    MipLevels = 1, ArraySize = 1, Format = desc.Format,
                    SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget
                };
                var owned = _device.CreateTexture2D(copyDesc);
                _device.ImmediateContext.CopyResource(owned, acquired);
                _duplication.ReleaseFrame();

                if (_consecutiveTimeouts > 0)
                    WriteLog("INFO", "从超时恢复，捕获正常");
                RaiseEvent(CaptureEvent.Succeeded, $"{desc.Width}x{desc.Height}");

                return new CapturedFrame(owned, frameInfo.LastPresentTime,
                    (int)desc.Width, (int)desc.Height);
            }
            catch (Exception ex) when (ex is not ObjectDisposedException)
            {
                WriteLog("ERROR", $"捕获异常: {ex.GetType().Name}: {ex.Message}", ex.HResult.ToString("X8"));
                RaiseEvent(CaptureEvent.Error, ex.Message);

                if (_retryCount < MaxRetries)
                {
                    if (Reinitialize()) _retryCount = 0;
                }
                return null;
            }
        }
    }

    /// <summary>刷新 HDR 状态，返回是否发生了变化。</summary>
    public bool RefreshHdrStatus()
    {
        try
        {
            if (_displayInfo is null) return false;
            var displays = DisplayEnumerator.EnumerateDisplays();
            var current = displays.FirstOrDefault(d => d.MonitorHandle == _displayInfo.MonitorHandle);
            if (current is not null && current.IsHdr != IsHdrCapture)
            {
                WriteLog("INFO", $"HDR 状态变化: {IsHdrCapture}→{current.IsHdr}，重建捕获");
                CreateDuplication(current.IsHdr);
                return true;
            }
        }
        catch (Exception ex)
        {
            WriteLog("ERROR", $"HDR 状态检测失败: {ex.Message}");
        }
        return false;
    }

    // ═══════════════════════════════════════
    // 实用方法
    // ═══════════════════════════════════════

    private void WriteLog(string lvl, string msg, string? code = null, int retry = 0)
    {
        var entry = new CaptureLogEntry(DateTime.UtcNow, lvl, msg, code, retry);
        lock (_log) { _log.Add(entry); if (_log.Count > 200) _log.RemoveAt(0); }
        System.Diagnostics.Debug.WriteLine($"[Capture:{lvl}] {msg}");
    }

    private void RaiseEvent(CaptureEvent ev, string msg, int retry = 0) =>
        CaptureEventHappened?.Invoke(this, new CaptureEventArgs { Event = ev, Message = msg, RetryCount = retry });

    private static void SafeDispose<T>(ref T? obj) where T : class, IDisposable
    {
        try { obj?.Dispose(); } catch { }
        obj = null;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        lock (_lock) { SafeDispose(ref _duplication); SafeDispose(ref _output); }
        WriteLog("INFO", "ScreenCapture 已释放");
    }
}
