// TrueToneCap.Core/Platform/PlatformFactory.cs
// 平台工厂 — 根据运行时 OS 选择对应的平台实现
// 当前: Windows (WGC + D3D11 + Win32)
// 未来: Linux (KMS/PipeWire + Vulkan/OpenGL + X11/Wayland)

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.Platform;

/// <summary>当前运行平台。</summary>
public enum RuntimePlatform
{
    Windows,
    LinuxX64,
    LinuxArm64,
    Unknown
}

/// <summary>
/// 平台工厂 — 提供当前平台的抽象实现。
/// 迁移策略: Windows 实现保持现有代码路径，Linux 实现逐步填充。
/// </summary>
public static class PlatformFactory
{
    /// <summary>检测当前运行平台。</summary>
    public static RuntimePlatform CurrentPlatform
    {
        get
        {
            if (OperatingSystem.IsWindows()) return RuntimePlatform.Windows;
            if (OperatingSystem.IsLinux())
                return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? RuntimePlatform.LinuxArm64
                    : RuntimePlatform.LinuxX64;
            return RuntimePlatform.Unknown;
        }
    }

    /// <summary>是否为 Windows 平台。</summary>
    public static bool IsWindows => OperatingSystem.IsWindows();

    /// <summary>是否为 Linux 平台。</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>
    /// 创建捕获后端。
    /// Windows: 由 App 层 WgcCaptureService 提供（通过 DI 注入）。
    /// Linux: 未来实现 KmsCaptureBackend / PipeWireCaptureBackend。
    /// </summary>
    public static ICaptureBackend? CreateCaptureBackend()
    {
        // Windows 捕获后端在 App 层创建（依赖 WinUI 3 / WGC）
        // Linux 捕获后端未来在此处创建
        return CurrentPlatform switch
        {
            RuntimePlatform.Windows => null, // App 层通过 DI 提供 WgcCaptureService
            RuntimePlatform.LinuxX64 or RuntimePlatform.LinuxArm64 => null, // TODO: KmsCaptureBackend
            _ => null
        };
    }

    /// <summary>
    /// 创建 GPU 渲染器。
    /// Windows: GpuToneMapper (D3D11 + HLSL)
    /// Linux 未来: VulkanToneMapper / OpenGLToneMapper
    /// </summary>
    public static IGpuRenderer? CreateGpuRenderer(object? nativeDevice = null)
    {
        return CurrentPlatform switch
        {
            RuntimePlatform.Windows => null, // App 层通过 DI 提供 GpuToneMapper
            RuntimePlatform.LinuxX64 or RuntimePlatform.LinuxArm64 => null, // TODO: VulkanToneMapper
            _ => null
        };
    }

    /// <summary>
    /// 创建窗口检测器。
    /// Windows: Win32WindowDetector (user32.dll)
    /// Linux 未来: X11WindowDetector / WaylandWindowDetector
    /// </summary>
    public static IWindowDetector? CreateWindowDetector()
    {
        return CurrentPlatform switch
        {
            RuntimePlatform.Windows => null, // 现有 RegionDetector 保持兼容
            RuntimePlatform.LinuxX64 or RuntimePlatform.LinuxArm64 => null, // TODO
            _ => null
        };
    }

    /// <summary>
    /// 创建色彩配置提供者。
    /// Windows: ColorProfileProvider (WCS / mscms.dll)
    /// Linux 未来: LcmsColorProfileProvider (lcms2 + colord)
    /// </summary>
    public static IColorProfileProvider? CreateColorProfileProvider()
    {
        return CurrentPlatform switch
        {
            RuntimePlatform.Windows => null, // 现有 ColorProfileProvider 保持兼容
            RuntimePlatform.LinuxX64 or RuntimePlatform.LinuxArm64 => null, // TODO
            _ => null
        };
    }

    /// <summary>
    /// 创建能力检测器。
    /// Windows: CapabilityService (Registry + DXGI)
    /// Linux 未来: LinuxCapabilityDetector (/sys/class/drm + vulkaninfo)
    /// </summary>
    public static ICapabilityDetector? CreateCapabilityDetector()
    {
        return CurrentPlatform switch
        {
            RuntimePlatform.Windows => null, // 现有 CapabilityService 保持兼容
            RuntimePlatform.LinuxX64 or RuntimePlatform.LinuxArm64 => null, // TODO
            _ => null
        };
    }
}
