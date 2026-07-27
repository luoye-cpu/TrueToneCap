// TrueToneCap.App/Services/AppServices.cs
// 轻量级服务定位器 — WinUI 3 无内置 DI，使用静态单例注册
// 所有服务在 App 启动时注册，全局可访问

using TrueToneCap.Core.Processing;

namespace TrueToneCap.App.Services;

/// <summary>应用级服务定位器（单例注册，全局访问）。</summary>
public static class AppServices
{
    private static SettingsService? _settings;
    private static CapabilityService? _capability;
    private static CapturePipelineService? _pipeline;
    private static WgcCaptureService? _wgc;
    private static GpuToneMapper? _gpuToneMapper;

    /// <summary>设置持久化服务。</summary>
    public static SettingsService Settings =>
        _settings ?? throw new InvalidOperationException("AppServices 未初始化");

    /// <summary>系统能力检测服务。</summary>
    public static CapabilityService Capability =>
        _capability ?? throw new InvalidOperationException("AppServices 未初始化");

    /// <summary>截图编码管线服务。</summary>
    public static CapturePipelineService Pipeline =>
        _pipeline ?? throw new InvalidOperationException("AppServices 未初始化");

    /// <summary>WGC 统一捕获服务（可能为 null，初始化失败时）。</summary>
    public static WgcCaptureService? Wgc => _wgc;

    /// <summary>GPU 色调映射器（可能为 null，着色器缺失时回退 CPU）。</summary>
    public static GpuToneMapper? GpuToneMapper => _gpuToneMapper;

    /// <summary>初始化所有服务。在 App 构造函数中调用一次。</summary>
    public static void Initialize()
    {
        // 日志服务最先初始化
        LogService.InitializeFileLog();
        LogService.Info("AppServices", "开始初始化应用服务");

        _settings = new SettingsService();
        _settings.Load();

        _capability = new CapabilityService();
        _pipeline = new CapturePipelineService(_settings);

        // WGC + GPU 管线（可能失败，不阻塞启动）
        try
        {
            _wgc = new WgcCaptureService();
            var primaryMonitor = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays()
                .FirstOrDefault(d => d.IsPrimary)?.MonitorHandle
                ?? TrueToneCap.Core.Capture.DisplayEnumerator.GetMonitorUnderCursor();
            var d3dDevice = _wgc.GetOrCreateDevice(primaryMonitor);
            _gpuToneMapper = new GpuToneMapper(d3dDevice);

            // 共享 D3D11 设备给 NVENC 后端
            TrueToneCap.Core.Encoding.NvencAvifBackend.SetSharedD3DDevice(d3dDevice);
            LogService.Info("AppServices", $"WGC/GPU 管线就绪, GPU色调映射={_gpuToneMapper.IsAvailable}");
        }
        catch (Exception ex)
        {
            LogService.Error("AppServices", "WGC/GPU 初始化失败", ex);
        }
    }

    /// <summary>释放所有服务。</summary>
    public static void Shutdown()
    {
        _gpuToneMapper?.Dispose();
        _wgc?.Dispose();
        _settings?.SaveQuiet();
    }
}
