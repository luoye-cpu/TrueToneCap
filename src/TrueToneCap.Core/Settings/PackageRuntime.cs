// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Settings/PackageRuntime.cs
// 「本进程是否以 MSIX 包身份运行」的判定 —— 决定设置该写哪里。
//
// ═══ 为什么需要它 ═══
// MSIX 包的安装目录（C:\Program Files\WindowsApps\<pkg>\）是【只读】的。
// 官方原文（desktop-to-uwp-behind-the-scenes）："Write inside the package — Not allowed.
// The package is read-only."
// 而本应用的设置一贯写在"程序同目录"（便携/绿色包的语义：设置跟着程序走）。
// 直接打成 MSIX 会得到：CanPersist 为 false ⇒ 全程按默认值表运行
// ⇒ **用户改的每一项设置重启即丢**，界面上只有日志里一行提示。
// 2026-10-07 核查商店上架时实测发现（全仓不存在任何"打包感知"的存储分支）。
//
// ═══ 为什么用 P/Invoke 而不是 Windows.Storage.ApplicationData ═══
// 本文件在 TrueToneCap.Core，Core 不引用 WinRT 投影（它只依赖 WinML/ORT 与 Vortice）。
// 为了一处判定而给 Core 引入 WinRT 依赖不划算，且 ApplicationData.Current 在
// 【非打包】进程里会抛异常，本来也得先判定再调用 —— 那判定正是这里要做的事。
// 故直接用 GetCurrentPackageFullName（appmodel.h）：这是"有无包身份"的最小官方判据。
//
// ⚠️ 判定必须【保守】：拿不准时一律当作"未打包"。
//    理由：未打包分支写的是程序同目录（原语义，绝不会更糟）；
//    误判成"已打包"才会把设置挪到 LocalAppData —— 那会改变绿色包用户的行为。
//    ⇒ 任何异常/未知返回码都返回 false。

using System.Runtime.InteropServices;

namespace TrueToneCap.Core.Settings;

/// <summary>运行形态判定：本进程是否以 MSIX 包身份运行。</summary>
public static class PackageRuntime
{
    // APPMODEL_ERROR_NO_PACKAGE = 15700：进程没有包身份（未打包）
    private const int APPMODEL_ERROR_NO_PACKAGE = 15700;

    // ERROR_INSUFFICIENT_BUFFER = 122：缓冲区太小（此处即"有名字可写"）
    private const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>
    /// 本进程是否具有 MSIX 包身份（即由包安装并按包身份启动）。
    /// <para>用途：MSIX 下包目录只读 ⇒ 设置必须改写到 <c>%LOCALAPPDATA%\TrueToneCap\</c>。</para>
    /// <para>判定在静态构造时做一次并缓存 —— 包身份在进程生命周期内不会改变，
    /// 而调用点（<c>SettingsService.SettingsPath</c>）是静态属性初始化，路径上不宜反复 P/Invoke。</para>
    /// </summary>
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            // 按长度 0 调用一次：只需要"有没有包身份"，不需要真的取到名字。
            // 实测语义（本机未打包进程，2026-10-07）：
            //   GetCurrentPackageFullName(null) -> rc=15700 (APPMODEL_ERROR_NO_PACKAGE), len=0
            // 有包身份时预期 rc=122 (ERROR_INSUFFICIENT_BUFFER) 且 len 被写成所需字符数。
            uint length = 0;
            int rc = GetCurrentPackageFullName(ref length, null);

            // ❌ 明确的"无包身份"
            if (rc == APPMODEL_ERROR_NO_PACKAGE) return false;

            // ✅ 缓冲区不足 = 有名字可写 = 有包身份
            if (rc == ERROR_INSUFFICIENT_BUFFER && length > 0) return true;

            // ⚠️ 其它返回码（含 0=成功、或某些环境下的意外值）：不猜。
            //    只在 length 真的被写成非零时认作"有包身份"——那是"有名字"的直接证据；
            //    其余一律按未打包处理（保守方向：维持程序同目录的原语义，绝不会更糟）。
            return length > 0;
        }
        catch
        {
            // 保守方向：拿不准 = 未打包
            return false;
        }
    }

    /// <summary>
    /// 查询当前进程的包全名。
    /// <para>返回 <c>APPMODEL_ERROR_NO_PACKAGE (15700)</c> 表示未打包；
    /// 返回 <c>ERROR_INSUFFICIENT_BUFFER (122)</c> 表示有包身份（缓冲区不够）。</para>
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
