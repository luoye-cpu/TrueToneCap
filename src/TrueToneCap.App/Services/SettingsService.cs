// TrueToneCap.App/Services/SettingsService.cs
// 设置持久化服务 — 从 MainWindow 提取，负责 JSON 配置的加载/保存
// 使用 System.Text.Json 源生成器实现 AOT 兼容

using System.Text.Json;
using TrueToneCap.App.Models;

namespace TrueToneCap.App.Services;

/// <summary>应用设置持久化服务。</summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueToneCap", "TrueToneCap.settings.json");

    // ═══ AOT 兼容：使用源生成器上下文，避免运行时反射 ═══
    private static readonly AppJsonContext s_jsonContext = AppJsonContext.Default;

    /// <summary>当前设置实例（内存中）。</summary>
    public AppSettingsData Current { get; private set; } = new();

    /// <summary>从磁盘加载设置。文件不存在或损坏时返回默认值。</summary>
    public AppSettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                // 使用源生成器反序列化（AOT 兼容）
                Current = JsonSerializer.Deserialize(json, s_jsonContext.AppSettingsData) ?? new();
                LogService.Info("SettingsService", $"设置已从 {SettingsPath} 加载");
            }
            else
            {
                Current = new();
                LogService.Info("SettingsService", "设置文件不存在，使用默认值");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"设置加载失败，使用默认值: {ex.Message}");
            Current = new();
        }
        return Current;
    }

    /// <summary>保存当前设置到磁盘。</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        // 使用源生成器序列化（AOT 兼容）
        // 注意: 源生成器 API 不支持传入 JsonSerializerOptions，输出为紧凑格式
        var json = JsonSerializer.Serialize(Current, s_jsonContext.AppSettingsData);
        File.WriteAllText(SettingsPath, json);
        LogService.Info("SettingsService", $"设置已保存到 {SettingsPath}");
    }

    /// <summary>静默保存（不抛异常）。</summary>
    public void SaveQuiet()
    {
        try { Save(); } catch { }
    }
}
