// TrueToneCap.App/Services/AppServices.cs
// 应用服务容器 — 基于 Microsoft.Extensions.DependencyInjection
// 所有服务通过 IServiceProvider 管理生命周期，AppServices 提供静态访问门面

using Microsoft.Extensions.DependencyInjection;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.App.Services;

/// <summary>应用级服务容器（DI 管理，静态门面访问）。</summary>
public static class AppServices
{
    private static IServiceProvider? _provider;
    private static ServiceProvider? _serviceProvider;

    /// <summary>底层服务提供者（高级场景使用）。</summary>
    public static IServiceProvider Provider =>
        _provider ?? throw new InvalidOperationException("AppServices 未初始化");

    /// <summary>设置持久化服务。</summary>
    public static SettingsService Settings => GetRequired<SettingsService>();

    /// <summary>系统能力检测服务。</summary>
    public static CapabilityService Capability => GetRequired<CapabilityService>();

    /// <summary>截图编码管线服务。</summary>
    public static CapturePipelineService Pipeline => GetRequired<CapturePipelineService>();

    /// <summary>WGC 统一捕获服务（可能为 null，初始化失败时）。</summary>
    public static WgcCaptureService? Wgc => GetOptional<WgcCaptureService>();

    /// <summary>GPU 色调映射器（可能为 null，着色器缺失时回退 CPU）。</summary>
    public static GpuToneMapper? GpuToneMapper => GetOptional<GpuToneMapper>();

    private static T GetRequired<T>() where T : class =>
        Provider.GetRequiredService<T>();

    private static T? GetOptional<T>() where T : class =>
        Provider.GetService<T>();

    /// <summary>初始化所有服务。在 App 构造函数中调用一次。</summary>
    public static void Initialize()
    {
        // 日志服务最先初始化
        LogService.InitializeFileLog();
        LogService.Info("AppServices", "开始初始化应用服务 (DI 容器)");

        var services = new ServiceCollection();

        // ── 注册核心服务（单例）──
        services.AddSingleton<SettingsService>(_ =>
        {
            var s = new SettingsService();
            s.Load();
            return s;
        });
        services.AddSingleton<CapabilityService>();
        services.AddSingleton<CapturePipelineService>(sp =>
            new CapturePipelineService(sp.GetRequiredService<SettingsService>()));

        // ── WGC + GPU 管线（可能失败，不阻塞启动）──
        try
        {
            var wgc = new WgcCaptureService();
            var primaryMonitor = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays()
                .FirstOrDefault(d => d.IsPrimary)?.MonitorHandle
                ?? TrueToneCap.Core.Capture.DisplayEnumerator.GetMonitorUnderCursor();
            var d3dDevice = wgc.GetOrCreateDevice(primaryMonitor);
            var gpuToneMapper = new GpuToneMapper(d3dDevice);

            // 共享 D3D11 设备给 NVENC 后端
            TrueToneCap.Core.Encoding.NvencAvifBackend.SetSharedD3DDevice(d3dDevice);

            services.AddSingleton(wgc);
            services.AddSingleton(gpuToneMapper);

            LogService.Info("AppServices", $"WGC/GPU 管线就绪, GPU色调映射={gpuToneMapper.IsAvailable}");
        }
        catch (Exception ex)
        {
            LogService.Error("AppServices", "WGC/GPU 初始化失败（将以 CPU 回退模式运行）", ex);
        }

        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false
        });
        _provider = _serviceProvider;

        LogService.Info("AppServices", "DI 容器构建完成");
    }

    /// <summary>释放所有服务。</summary>
    public static void Shutdown()
    {
        // 保存设置
        try { GetOptional<SettingsService>()?.SaveQuiet(); } catch { }

        // 释放 DI 容器（自动 Dispose 所有 IDisposable 单例）
        _serviceProvider?.Dispose();
        _serviceProvider = null;
        _provider = null;
    }
}
