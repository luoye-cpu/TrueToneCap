// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/ToastService.cs
// Windows 11 Toast 通知服务 — 截图成功/失败的用户反馈

using Microsoft.Windows.AppNotifications;
using System.Runtime.InteropServices;

namespace TrueToneCap.App.Services;

// ToastType 枚举曾在这里（CaptureSuccess / CaptureFailed / RecordingStarted / RecordingCompleted /
// OcrCompleted / TranslateCompleted）。2026-09-21 整枚删除：全仓 0 处引用 —— ToastService 对外
// 是一组具名方法（ShowCaptureSuccess / ShowCaptureFailed / ShowCaptureDegraded …），
// 没有任何入口按类型枚举分派，也不参与序列化。留着的代价不是 6 个名字，而是让人以为
// "通知是按类型组织的"，进而往一个没人读的表上加成员。

/// <summary>
/// 通知通道：决定一条通知走哪种呈现方式。
/// <para>两者不是一回事，也不可互相替代：</para>
/// <list type="bullet">
/// <item><see cref="System"/> —— Windows 系统通知：由系统渲染、进通知中心、事后可查，
/// 位置固定在屏幕右下角（无法自定义），样式不能改。</item>
/// <item><see cref="Overlay"/> —— 应用内浮层：自绘窗口、支持四角定位、样式可控，
/// 但不进通知中心，几秒后自动消失。</item>
/// </list>
/// </summary>
public enum ToastChannel
{
    /// <summary>即时反馈：应用内浮层。用于需要立刻看到、但不必留存的提示（降级、OCR、翻译）。</summary>
    Overlay,

    /// <summary>可追溯：Windows 系统通知。用于事后可能要回看的事件（无感截图完成、录制完成、失败）。</summary>
    System
}

/// <summary>Toast 通知管理器（Windows 11 AppNotificationManager）。</summary>
public static class ToastService
{
    private static bool s_registered;

    /// <summary>UI 线程调度器（Register 在启动时由 UI 线程调用，此处捕获以创建应用内浮层）。</summary>
    private static Microsoft.UI.Dispatching.DispatcherQueue? s_uiQueue;

    /// <summary>系统通知是否可用（注册成功 且 未以管理员权限运行）。</summary>
    private static bool s_systemNotifyAvailable;
    private static bool s_elevated;

    /// <summary>是否以提升（管理员）权限运行 —— 该状态下系统通知不受支持。</summary>
    private static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    // ═══ AppUserModelID：非打包应用发送 Windows 系统通知的**必要前提** ═══
    // 本项目以非打包（unpackaged）方式运行。未显式设置 AUMID 时，
    // AppNotificationManager.Show() **不会抛异常、也不写日志**，
    // 但通知根本不会显示 —— 静默失败，很难定位。
    // 这正是"切到 Windows 自带通知后收不到通知"的原因。
    private const string AppUserModelId = "TrueToneCap.Desktop";

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appID);

    /// <summary>
    /// 通知位置提供器。由应用启动时注入（SettingsService.Current 是实例属性，
    /// 静态类拿不到，故用委托解耦）。未注入时回退右下角。
    /// </summary>
    public static Func<string>? PositionProvider { get; set; }

    /// <summary>注册 AppNotificationManager（必须在应用启动时调用一次）。</summary>
    public static void Register()
    {
        if (s_registered) return;
        try
        {
            // ① 先确保开始菜单有带 AUMID 的快捷方式（Windows 据此登记应用）
            EnsureAppShortcut();

            // ② 必须在 Register() **之前**设置 AUMID，否则系统通知会被静默丢弃
            int hr = SetCurrentProcessExplicitAppUserModelID(AppUserModelId);
            LogService.Info("ToastService", $"AppUserModelID 设置: {AppUserModelId} hr=0x{hr:X8}");

            // ③ 关键限制（官方文档）：以提升（管理员）权限运行时，系统通知不受支持，
            //    Show() 会**静默失败**且无任何提示。提前检测，以便后面回退到应用内浮层。
            s_elevated = IsElevated();
            if (s_elevated)
                LogService.Warn("ToastService",
                    "检测到以管理员权限运行 —— Windows 系统通知不受支持（Show 会静默失败），将回退应用内浮层");

            // ④ 用**带资产**的重载注册。依据 WindowsAppSDK 规范：非打包应用可传
            //    DisplayName + Icon，Icon 必须是本地 png/bmp/jpg/ico。
            //    无参 Register() 时系统要"从 Shell 获取"这些资产，本项目无内置图标，
            //    获取失败会导致注册不完整 —— 通知提交成功却不显示。
            var icon = EnsureAppIcon();
            if (icon is not null)
            {
                AppNotificationManager.Default.Register("TrueToneCap", new Uri(icon));
                LogService.Info("ToastService", $"通知注册成功（带资产, 图标={icon}）");
            }
            else
            {
                AppNotificationManager.Default.Register();
                LogService.Info("ToastService", "通知注册成功（无资产）");
            }
            s_systemNotifyAvailable = !s_elevated;
            // 捕获 UI 线程调度器：应用内浮层（非 WindowsNotify 位置）需要它来创建窗口
            s_uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            s_registered = true;
            LogService.Info("ToastService", "通知注册成功");
        }
        catch (Exception ex)
        {
            LogService.Error("ToastService", "通知注册失败", ex);
            s_systemNotifyAvailable = false;   // 后续 ShowToast 会回退到应用内浮层
            // Toast 不可用不影响核心功能
        }
    }

    /// <summary>显示截图成功通知。</summary>
    public static void ShowCaptureSuccess(string filePath, long elapsedMs)
    {
        var fileName = Path.GetFileName(filePath);
        Show(new Dictionary<string, string>
        {
            ["title"] = "✅ 截图已保存",
            ["body"] = $"{fileName}\n耗时 {elapsedMs}ms • 已复制到剪贴板"
        });
    }

    /// <summary>显示截图失败通知。</summary>
    public static void ShowCaptureFailed(string reason)
    {
        Show(new Dictionary<string, string>
        {
            ["title"] = "❌ 截图失败",
            ["body"] = reason
        });
    }

    /// <summary>显示截图降级警告通知（截图成功但功能有损）。</summary>
    /// <remarks>
    /// 2026-09-16：改为走统一入口 —— 尊重用户的"通知位置"设置。
    /// 原实现只发 Windows 系统通知，用户若在设置里选了四角位置，降级提示仍会忽略它。
    /// </remarks>
    public static void ShowCaptureDegraded(string warning)
        => ShowToast("编码已降级为软件处理", warning, ToastTone.Warning, ToastChannel.Overlay);

    /// <summary>
    /// 统一通知入口：按用户设置的"通知位置"决定走应用内浮层还是 Windows 系统通知。
    /// </summary>
    /// <param name="tone">语义（决定图标与配色；Warning 用于降级，不是绿色对勾）。</param>
    public static void ShowToast(string title, string body, ToastTone tone = ToastTone.Success,
        ToastChannel channel = ToastChannel.Overlay)
    {
        string position = "BottomRight";
        try { position = PositionProvider?.Invoke() ?? "BottomRight"; } catch { }

        // 走系统通知的两种情形：
        //   ① 用户在设置里显式选择「Windows 系统通知」—— 尊重用户选择，全部走系统；
        //   ② 本条通知属于「可追溯」类别（无感截图完成 / 录制完成 / 失败）——
        //      即便用户设了四角位置也应进通知中心，便于事后回看。
        if (position == "WindowsNotify" || channel == ToastChannel.System)
        {
            if (s_systemNotifyAvailable)
            {
                Show(new Dictionary<string, string> { ["title"] = title, ["body"] = body });
                return;
            }
            // 系统通知不可用（注册失败，或以管理员权限运行）→ **不 return**，
            // 改为回退应用内浮层：用户至少还能看到通知，而不是什么都没有。
            LogService.Warn("ToastService", "系统通知不可用，回退应用内浮层");
        }

        // 应用内浮层：必须在 UI 线程创建窗口
        var queue = s_uiQueue;
        if (queue is null)
        {
            Show(new Dictionary<string, string> { ["title"] = title, ["body"] = body });
            return;
        }

        try
        {
            queue.TryEnqueue(() =>
            {
                try
                {
                    var toast = new SilentCaptureToast(position);
                    toast.SetContent(title, body, tone);
                    toast.Activate();
                }
                catch (Exception ex)
                {
                    LogService.Warn("Toast", $"提示窗口创建失败，回退系统通知: {ex.Message}");
                    Show(new Dictionary<string, string> { ["title"] = title, ["body"] = body });
                }
            });
        }
        catch (Exception ex)
        {
            LogService.Warn("Toast", $"通知调度失败: {ex.Message}");
        }
    }

    /// <summary>显示录制完成通知。</summary>
    public static void ShowRecordingCompleted(string filePath, int frameCount)
    {
        var fileName = Path.GetFileName(filePath);
        Show(new Dictionary<string, string>
        {
            ["title"] = "🎬 录制完成",
            ["body"] = $"{fileName}\n共 {frameCount} 帧"
        });
    }

    /// <summary>显示 OCR 结果通知。</summary>
    public static void ShowOcrResult(string text)
    {
        var truncated = text.Length > 100 ? text[..97] + "..." : text;
        Show(new Dictionary<string, string>
        {
            ["title"] = "📝 文字提取结果",
            ["body"] = truncated
        });
    }

    /// <summary>显示翻译结果通知。</summary>
    public static void ShowTranslationResult(string translated)
    {
        var truncated = translated.Length > 100 ? translated[..97] + "..." : translated;
        Show(new Dictionary<string, string>
        {
            ["title"] = "🌐 翻译完成",
            ["body"] = truncated
        });
    }

    private static void Show(Dictionary<string, string> args)
    {
        if (!s_registered) return;

        try
        {
            // 构建简单的 XML Toast
            var title = args.GetValueOrDefault("title", "TrueToneCap");
            var body = args.GetValueOrDefault("body", "");

            var xml = $"""
                <toast>
                    <visual>
                        <binding template="ToastGeneric">
                            <text>{EscapeXml(title)}</text>
                            <text>{EscapeXml(body)}</text>
                        </binding>
                    </visual>
                </toast>
                """;

            var notification = new AppNotification(xml);
            AppNotificationManager.Default.Show(notification);
            // 记一条日志：系统通知是否提交成功无法从返回值判断，
            // 有了这行才能区分"没走到这里"和"提交了但系统没显示"
            LogService.Info("ToastService", $"已提交系统通知: {title}");
        }
        catch (Exception ex)
        {
            LogService.Warn("ToastService", $"发送通知失败: {ex.Message}");
        }
    }

    // ═══ 开始菜单快捷方式：非打包应用发系统通知的登记手段 ═══
    // 仅有进程级 AUMID 还不够。Windows 需要一个**带 AUMID 的开始菜单快捷方式**，
    // 才会把应用登记进"设置 → 系统 → 通知"的应用列表；否则 Show() 一切正常
    //（日志显示已提交），通知却被系统直接丢弃。

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PropertyKey pkey);
        void GetValue(ref PropertyKey key, out PropVariant pv);
        void SetValue(ref PropertyKey key, ref PropVariant pv);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey { public Guid fmtid; public uint pid; }

    /// <summary>PROPVARIANT 的极简表示 —— 这里只需承载一个 LPWSTR。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public nint pwszVal;   // 联合体首个成员：LPWSTR
    }

    private const ushort VT_LPWSTR = 31;
    // PKEY_AppUserModel_ID = {9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}, 5
    private static PropertyKey PKEY_AppUserModel_ID => new()
    {
        fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
        pid = 5
    };

    /// <summary>确保开始菜单存在带 AUMID 的快捷方式，让 Windows 登记本应用。</summary>
    private static void EnsureAppShortcut()
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            string startMenu = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs");
            string lnk = Path.Combine(startMenu, "TrueToneCap.lnk");
            if (File.Exists(lnk)) return;   // 已存在不重复创建

            Directory.CreateDirectory(startMenu);

            var link = (IShellLinkW)new ShellLink();
            link.SetPath(exePath);
            link.SetWorkingDirectory(Path.GetDirectoryName(exePath) ?? "");
            link.SetDescription("TrueToneCap 截图工具");

            // 关键：把 AUMID 写入快捷方式属性，否则系统仍不认识这个应用
            var store = (IPropertyStore)link;
            var key = PKEY_AppUserModel_ID;
            nint ptr = Marshal.StringToCoTaskMemUni(AppUserModelId);
            try
            {
                var pv = new PropVariant { vt = VT_LPWSTR, pwszVal = ptr };
                store.SetValue(ref key, ref pv);
                store.Commit();
            }
            finally { Marshal.FreeCoTaskMem(ptr); }

            ((IPersistFile)link).Save(lnk, false);
            LogService.Info("ToastService", $"已创建开始菜单快捷方式(带 AUMID): {lnk}");
        }
        catch (Exception ex)
        {
            LogService.Warn("ToastService", $"创建开始菜单快捷方式失败（系统通知可能不显示）: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保存在本地图标文件，供 <c>Register(displayName, iconUri)</c> 使用。
    /// <para>依据 WindowsAppSDK 规范：非打包应用可用带资产的 Register 重载，
    /// 但 Icon 必须是**本地** png/bmp/jpg/ico，否则 API 直接抛异常。
    /// 本项目没有内置图标资源，这里程序化生成一个最小纯色 PNG（#0078D4）。</para>
    /// </summary>
    private static string? EnsureAppIcon()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueToneCap");
            string file = Path.Combine(dir, "app.png");
            if (File.Exists(file)) return file;
            Directory.CreateDirectory(dir);

            const int w = 256, h = 256;
            var raw = new byte[h * (1 + w * 4)];
            int p = 0;
            for (int y = 0; y < h; y++)
            {
                raw[p++] = 0;                        // 行过滤器: None
                for (int x = 0; x < w; x++)
                {
                    raw[p++] = 0xD4;                 // R
                    raw[p++] = 0x78;                 // G
                    raw[p++] = 0x00;                 // B
                    raw[p++] = 0xFF;                 // A  → #0078D4
                }
            }

            byte[] zdata;
            using (var ms = new MemoryStream())
            {
                using (var z = new System.IO.Compression.ZLibStream(ms,
                    System.IO.Compression.CompressionLevel.Optimal, true))
                    z.Write(raw, 0, raw.Length);
                zdata = ms.ToArray();
            }

            using var fs = File.Create(file);
            fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });   // PNG 签名
            WritePngChunk(fs, "IHDR", BuildPngIhdr(w, h));
            WritePngChunk(fs, "IDAT", zdata);
            WritePngChunk(fs, "IEND", Array.Empty<byte>());
            return file;
        }
        catch (Exception ex)
        {
            LogService.Warn("ToastService", $"创建图标失败: {ex.Message}");
            return null;
        }
    }

    private static byte[] BuildPngIhdr(int w, int h)
    {
        var b = new List<byte>();
        b.AddRange(BitConverter.GetBytes(w).Reverse());
        b.AddRange(BitConverter.GetBytes(h).Reverse());
        b.Add(8);                       // 位深
        b.Add(6);                       // 颜色类型: RGBA
        b.Add(0); b.Add(0); b.Add(0);   // 压缩 / 过滤 / 隔行，均为 0
        return b.ToArray();
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(BitConverter.GetBytes(data.Length).Reverse().ToArray());
        s.Write(typeBytes);
        s.Write(data);
        s.Write(BitConverter.GetBytes(Crc32(typeBytes.Concat(data).ToArray())).Reverse().ToArray());
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static string EscapeXml(string text)
        => System.Net.WebUtility.HtmlEncode(text);
}
