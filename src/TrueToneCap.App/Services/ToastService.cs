// TrueToneCap.App/Services/ToastService.cs
// Windows 11 Toast 通知服务 — 截图成功/失败的用户反馈

using Microsoft.Windows.AppNotifications;

namespace TrueToneCap.App.Services;

/// <summary>Toast 通知类型。</summary>
public enum ToastType
{
    CaptureSuccess,
    CaptureFailed,
    RecordingStarted,
    RecordingCompleted,
    OcrCompleted,
    TranslateCompleted
}

/// <summary>Toast 通知管理器（Windows 11 AppNotificationManager）。</summary>
public static class ToastService
{
    private static bool s_registered;

    /// <summary>注册 AppNotificationManager（必须在应用启动时调用一次）。</summary>
    public static void Register()
    {
        if (s_registered) return;
        try
        {
            // 对于 WinUI 3 打包应用，需要注册通知
            // 非打包应用使用快捷方式注册（必须手动创建开始菜单快捷方式）
            AppNotificationManager.Default.Register();
            s_registered = true;
            LogService.Info("ToastService", "通知注册成功");
        }
        catch (Exception ex)
        {
            LogService.Error("ToastService", "通知注册失败", ex);
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
        }
        catch (Exception ex)
        {
            LogService.Warn("ToastService", $"发送通知失败: {ex.Message}");
        }
    }

    private static string EscapeXml(string text)
        => System.Net.WebUtility.HtmlEncode(text);
}
