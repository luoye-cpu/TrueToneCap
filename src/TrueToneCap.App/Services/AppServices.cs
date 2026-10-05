// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
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

    // 2026-10-01：这里原有的 `public static GpuToneMapper? GpuToneMapper` 已随该类一起删除——
    // 它在 09-16 就停止构造（下面的注释当时写明"死代码"），属性本身也全仓无人读取。

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
        // ⚠ 异常路径必须释放已创建的 WGC/GPU 对象：
        // 之前 wgc 在 GetOrCreateDevice 抛异常时既未注册进容器（容器不会负责释放），
        // 也没有被 Dispose → 其内部的 D3D11 设备与非托管资源泄漏。
        WgcCaptureService? wgc = null;
        try
        {
            wgc = new WgcCaptureService();
            var primaryMonitor = TrueToneCap.Core.Capture.DisplayEnumerator.EnumerateDisplays()
                .FirstOrDefault(d => d.IsPrimary)?.MonitorHandle
                ?? TrueToneCap.Core.Capture.DisplayEnumerator.GetMonitorUnderCursor();
            var d3dDevice = wgc.GetOrCreateDevice(primaryMonitor);

            // ═══ GpuToneMapper：2026-09-16 停止构造，2026-10-01 连类一起删除 ═══
            // 当时的判断是"死代码"：属性全仓没有调用者，而构造它要按磁盘上的 .cso 走一遍 IO，
            // 每次启动都在为一条不会执行的路径付费；实际的 HDR→SDR 走 Core 的 ToneMapper（CPU）。
            // 09-16 只停了构造、留下了类和属性，于是"清单写着在用"这句假话又活了两周
            // —— 这次连类、ShaderLoader 和那份 ToneMapping.hlsl 资产一并摘掉，并在门禁里
            // 补了 Test_ShaderList_HasNoOrphanEntry（清单每条都要有剥注释后的 .cso 消费点）。
            // 若将来真要接 GPU 色调映射，从 HdrCaptureWindow 的 FindCso + 自己的交换链那条活路加，
            // 别复活这份"启动时构造、运行中没人读"的形状。

            // 共享 D3D11 设备给 NVENC 后端
            TrueToneCap.Core.Encoding.NvencAvifBackend.SetSharedD3DDevice(d3dDevice);

            services.AddSingleton(wgc);

            // 已移交容器托管，避免 finally 中重复释放
            wgc = null;

            LogService.Info("AppServices", "WGC/GPU 管线就绪");
        }
        catch (Exception ex)
        {
            LogService.Error("AppServices", "WGC/GPU 初始化失败（将以 CPU 回退模式运行）", ex);
        }
        finally
        {
            wgc?.Dispose();   // 移交容器后 wgc 已被置 null ⇒ 这里只会释放在移交前就失败的那一份
        }

        // ── Phase 1 (2026-08-11): 启动后台预热预览 UI 缓存（Win2D 设备 + 工具栏文字位图）──
        try
        {
            _ = Task.Run(HdrCaptureWindow.PreloadUiCache);
        }
        catch { }

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

        // 最后停止日志后台清理任务 —— 必须放在最后，
        // 以便上述各步的释放过程仍能被记录到日志文件。
        try { LogService.Shutdown(); } catch { }
    }
}
