// TrueToneCap.Core/Capture/WinRtScreenCapture.cs
// Windows.Graphics.Capture 原生 HDR 捕获（替代 DXGI Desktop Duplication）

using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace TrueToneCap.Core.Capture;

/// <summary>使用 Windows.Graphics.Capture API 的屏幕捕获器，原生支持 HDR (Float16)。</summary>
public sealed class WinRtScreenCapture : IDisposable
{
    private ID3D11Device _d3dDevice;
    private IDirect3DDevice? _winrtDevice;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;
    private readonly nint _targetMonitor;
    private bool _disposed;
    private CapturedFrame? _latestFrame;
    private readonly object _lock = new();
    private TaskCompletionSource<CapturedFrame?>? _frameTcs;

    public bool IsHdr { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    // IGraphicsCaptureItemInterop GUID: 79C3F95B-31F7-4EC2-A464-632EF5D30760
    [ComImport, Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        int CreateForWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr result);
        int CreateForMonitor(IntPtr hmonitor, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IntPtr result);
    }

    public WinRtScreenCapture(ID3D11Device d3dDevice, nint targetMonitor)
    {
        _d3dDevice = d3dDevice;
        _targetMonitor = targetMonitor;
    }

    /// <summary>初始化捕获。返回 true 表示成功。</summary>
    public bool Initialize()
    {
        try
        {
            // 1. 创建 WinRT IDirect3DDevice（从 D3D11 设备）
            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            _winrtDevice = Direct3D11Helpers.CreateDirect3DDeviceFromDXGIDevice(dxgiDevice);

            // 2. 通过 IGraphicsCaptureItemInterop 从 HMONITOR 创建 GraphicsCaptureItem
            var item = CreateItemForMonitor(_targetMonitor);
            if (item is null) return false;
            _item = item;
            Width = item.Size.Width;
            Height = item.Size.Height;

            // 3. 创建帧池（HDR 支持：R16G16B16A16Float）
            var pixelFormat = DirectXPixelFormat.R16G16B16A16Float;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, pixelFormat, (int)2, item.Size);
            _framePool.FrameArrived += OnFrameArrived;

            // 4. 创建会话并开始
            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();

            IsHdr = true;
            System.Diagnostics.Debug.WriteLine($"[WinRtCapture] 初始化成功: {Width}x{Height} HDR=True");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinRtCapture] 初始化失败: {ex.Message}");
            // 回退 SDR
            return TryInitializeSdr();
        }
    }

    private bool TryInitializeSdr()
    {
        try
        {
            DisposeInternal();
            using var dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
            _winrtDevice = Direct3D11Helpers.CreateDirect3DDeviceFromDXGIDevice(dxgiDevice);

            var item = CreateItemForMonitor(_targetMonitor);
            if (item is null) return false;
            _item = item;
            Width = item.Size.Width;
            Height = item.Size.Height;

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, (int)2, item.Size);
            _framePool.FrameArrived += OnFrameArrived;

            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            _session.StartCapture();

            IsHdr = false;
            System.Diagnostics.Debug.WriteLine($"[WinRtCapture] SDR 回退成功: {Width}x{Height}");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinRtCapture] SDR 回退也失败: {ex.Message}");
            return false;
        }
    }

    private static GraphicsCaptureItem? CreateItemForMonitor(nint hmonitor)
    {
        try
        {
            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            var guid = typeof(GraphicsCaptureItem).GUID;
            int hr = interop.CreateForMonitor(hmonitor, guid, out var ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
            {
                System.Diagnostics.Debug.WriteLine($"[WinRtCapture] CreateForMonitor HR=0x{hr:X8}");
                return null;
            }
            return MarshalInterface<GraphicsCaptureItem>.FromAbi(ptr);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WinRtCapture] CreateForMonitor 异常: {ex.Message}");
            return null;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_lock)
        {
            if (_disposed) return;
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null) return;

                // 提取 D3D11 纹理
                using var dxgiSurface = Direct3D11Helpers.GetDXGIInterfaceFromObject<IDXGISurface>(frame.Surface);
                var texture = dxgiSurface.QueryInterface<ID3D11Texture2D>();
                if (texture is null) return;

                var desc = texture.Description;
                _latestFrame?.Dispose();
                _latestFrame = new CapturedFrame(texture, 0, (int)desc.Width, (int)desc.Height);

                // 完成等待的 Task
                _frameTcs?.TrySetResult(_latestFrame);
                _frameTcs = null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WinRtCapture] FrameArrived 错误: {ex.Message}");
                _frameTcs?.TrySetResult(null);
                _frameTcs = null;
            }
        }
    }

    /// <summary>异步获取下一帧</summary>
    public Task<CapturedFrame?> TryAcquireNextFrameAsync(int timeoutMs = 1000)
    {
        lock (_lock)
        {
            _frameTcs?.TrySetCanceled();
            _frameTcs = new TaskCompletionSource<CapturedFrame?>();
        }

        // 超时处理
        var timeoutTask = Task.Delay(timeoutMs);
        var frameTask = _frameTcs.Task;

        return Task.WhenAny(frameTask, timeoutTask).ContinueWith(t =>
        {
            lock (_lock)
            {
                if (t.Result == timeoutTask)
                {
                    _frameTcs?.TrySetResult(null);
                    _frameTcs = null;
                    return null;
                }
                return frameTask.Result;
            }
        });
    }

    private void DisposeInternal()
    {
        lock (_lock)
        {
            _frameTcs?.TrySetCanceled();
            _frameTcs = null;
        }
        try { _session?.Dispose(); } catch { }
        try { _framePool?.Dispose(); } catch { }
        try { _item = null; } catch { }
        try { _winrtDevice?.Dispose(); } catch { }
        _session = null;
        _framePool = null;
        _item = null;
        _winrtDevice = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _latestFrame?.Dispose();
        DisposeInternal();
    }
}

/// <summary>WinRT Direct3D 互操作辅助</summary>
internal static class Direct3D11Helpers
{
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    public static IDirect3DDevice CreateDirect3DDeviceFromDXGIDevice(IDXGIDevice dxgiDevice)
    {
        IntPtr dxgiPtr = Marshal.GetIUnknownForObject(dxgiDevice);
        try
        {
            int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiPtr, out var ptr);
            if (hr < 0 || ptr == IntPtr.Zero)
                throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice HR=0x{hr:X8}");
            return MarshalInterface<IDirect3DDevice>.FromAbi(ptr);
        }
        finally { Marshal.Release(dxgiPtr); }
    }

    public static T GetDXGIInterfaceFromObject<T>(object obj) where T : class
    {
        var access = obj.As<IDirect3DDxgiInterfaceAccess>();
        int hr = access.GetInterface(typeof(T).GUID, out var ptr);
        if (hr < 0 || ptr == IntPtr.Zero) throw new InvalidOperationException($"GetDXGIInterface failed: 0x{hr:X8}");
        return MarshalInterface<T>.FromAbi(ptr);
    }

    [ComImport, Guid("A9B3D012-3DF2-4EE3-B8D1-8695F4EED3E1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDirect3DDxgiInterfaceAccess
    {
        int GetInterface([MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IntPtr p);
    }
}
