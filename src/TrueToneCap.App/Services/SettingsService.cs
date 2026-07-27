// TrueToneCap.App/Services/SettingsService.cs
// 设置持久化服务 — 从 MainWindow 提取，负责 JSON 配置的加载/保存

using System.Text.Json;

namespace TrueToneCap.App.Services;

/// <summary>应用设置持久化服务。</summary>
public sealed class SettingsService
{
    private static readonly string SettingsPath =
        Path.Combine(AppContext.BaseDirectory, "TrueToneCap.settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

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
                Current = JsonSerializer.Deserialize<AppSettingsData>(json) ?? new();
            }
            else
            {
                Current = new();
            }
        }
        catch
        {
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
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(Current, SerializerOptions));
    }

    /// <summary>静默保存（不抛异常）。</summary>
    public void SaveQuiet()
    {
        try { Save(); } catch { }
    }
}
