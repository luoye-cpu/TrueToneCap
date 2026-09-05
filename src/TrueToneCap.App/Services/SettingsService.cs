// TrueToneCap.App/Services/SettingsService.cs
// 设置持久化服务 — 从 MainWindow 提取，负责 JSON 配置的加载/保存
// 使用 System.Text.Json 源生成器实现 AOT 兼容
// ⚠ 敏感字段（LLM API Key）不进 JSON，改由 SecretStore 加密存储

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
                // 注: LlmApiKey 已标记 [JsonIgnore]，不会从 JSON 读取
                Current = JsonSerializer.Deserialize(json, s_jsonContext.AppSettingsData) ?? new();

                // v0.3.3 迁移: 旧版本把 API Key 明文写在 settings.json
                var migrated = MigratePlaintextApiKey(json);

                // 从加密凭据库读取敏感凭据
                Current.LlmApiKey = SecretStore.Get(SecretStore.LlmApiKeyName) ?? "";
                Current.YoudaoAppSecret = SecretStore.Get(SecretStore.YoudaoAppSecretName) ?? "";

                // 若发生迁移，立即重写 settings.json 以清除其中的明文
                if (migrated) Save();

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

        // 敏感字段不写入 JSON（[JsonIgnore]），改存加密凭据库
        SaveSecret(SecretStore.LlmApiKeyName, Current.LlmApiKey);
        SaveSecret(SecretStore.YoudaoAppSecretName, Current.YoudaoAppSecret);

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

    /// <summary>把单个敏感字段写入加密凭据库；值为空时删除对应条目。
    /// <para>
    /// ⚠ 失败必须与"设置保存"隔离：加密存储可能因 DPAPI 不可用、磁盘满、
    /// 权限不足等原因失败。若让它抛出，会连带导致全部非敏感设置（格式/质量/热键…）
    /// 都保存不了。故此处只记录日志，保证用户其余设置不受影响。
    /// </para>
    /// </summary>
    private static void SaveSecret(string name, string value)
    {
        try
        {
            if (!string.IsNullOrEmpty(value))
                SecretStore.Set(name, value);
            else
                SecretStore.Delete(name);
        }
        catch (Exception ex)
        {
            LogService.Error("SettingsService", $"凭据保存失败（{name}），其余设置仍将保存", ex);
        }
    }

    /// <summary>检测并把旧版本明文存储的 API Key 迁移到加密凭据库。</summary>
    /// <returns>确实发生了迁移时返回 true（调用方需重写 settings.json 清除明文）。</returns>
    private static bool MigratePlaintextApiKey(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(nameof(AppSettingsData.LlmApiKey), out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var plain = prop.GetString();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    SecretStore.Set(SecretStore.LlmApiKeyName, plain);
                    LogService.Info("SettingsService", "已将明文 API Key 迁移到加密存储");
                    return true;
                }
            }
        }
        catch (JsonException) { /* 非法 JSON：无需迁移 */ }
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"API Key 迁移失败: {ex.Message}");
        }
        return false;
    }
}
