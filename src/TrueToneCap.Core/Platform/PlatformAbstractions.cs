// TrueToneCap.Core/Platform/PlatformAbstractions.cs
// 跨平台抽象层 — 轻量接口定义，为 Linux 迁移做准备
// 不同于已删除的旧 Platform 层（纯存根），此文件定义实际被 Core 引用的抽象

namespace TrueToneCap.Core.Platform;

/// <summary>运行时操作系统。</summary>
public enum RuntimePlatform
{
    Windows,
    Linux,
    LinuxX64,
    LinuxArm64,
    macOS,
    Unknown
}

/// <summary>平台检测工具。</summary>
public static class PlatformDetection
{
    public static RuntimePlatform Current =>
        OperatingSystem.IsWindows() ? RuntimePlatform.Windows :
        OperatingSystem.IsLinux() ? RuntimePlatform.Linux :
        OperatingSystem.IsMacOS() ? RuntimePlatform.macOS :
        RuntimePlatform.Unknown;

    public static bool IsWindows => Current == RuntimePlatform.Windows;
    public static bool IsLinux => Current == RuntimePlatform.Linux;
    public static bool IsMacOS => Current == RuntimePlatform.macOS;
    public static bool IsWindowsOrLinux => IsWindows || IsLinux;
}