// TrueToneCap.App/Services/SecretStore.cs
// 敏感凭据加密存储 — Windows DPAPI (CryptProtectData, 当前用户 + 当前机器绑定)
// 用途: LLM API Key 等凭据，避免明文落入 settings.json

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TrueToneCap.App.Services;

/// <summary>敏感凭据加密存储（Windows DPAPI，绑定当前用户 + 当前机器）。
/// <para>
/// 之前 LLM API Key 以明文写入 <c>%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json</c>，
/// 任何能读取该目录的进程、或用户外发的配置备份/截图，都会泄露密钥。
/// 现改为经 DPAPI 加密后单独存放于 secrets.bin。
/// </para>
/// </summary>
/// <remarks>
/// 直接 P/Invoke <c>CryptProtectData</c> 而非引用 System.Security.Cryptography.ProtectedData
/// NuGet 包：保持零额外依赖，且 LibraryImport 为源生成，完全 AOT 兼容。
/// </remarks>
[SupportedOSPlatform("windows")]
public static partial class SecretStore
{
    /// <summary>LLM API Key 在凭据库中的键名。</summary>
    public const string LlmApiKeyName = "LlmApiKey";

    /// <summary>有道智云开放平台 应用密钥在凭据库中的键名（应用ID 非敏感，存 settings.json）。</summary>
    public const string YoudaoAppSecretName = "YoudaoAppSecret";

    // ── DPAPI 常量 ──
    // CRYPTPROTECT_UI_FORBIDDEN: 禁止弹出 UI 提示（服务/无交互场景必需）
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    private static readonly string SecretsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrueToneCap", "secrets.bin");

    private static readonly object s_lock = new();
    private static Dictionary<string, string>? s_cache;

    /// <summary>读取凭据；不存在或解密失败返回 null。</summary>
    public static string? Get(string key)
    {
        lock (s_lock) return Load().GetValueOrDefault(key);
    }

    /// <summary>写入（或覆盖）凭据。</summary>
    public static void Set(string key, string value)
    {
        lock (s_lock)
        {
            var dict = Load();
            dict[key] = value;
            Save(dict);
        }
    }

    /// <summary>删除凭据。</summary>
    public static void Delete(string key)
    {
        lock (s_lock)
        {
            var dict = Load();
            if (dict.Remove(key)) Save(dict);
        }
    }

    // ═══════════════════════════════════════
    //  持久化
    //  明文格式: 逐行 "key\tbase64(value)\n"，整体经 DPAPI 加密
    // ═══════════════════════════════════════

    private static Dictionary<string, string> Load()
    {
        if (s_cache is not null) return s_cache;

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(SecretsPath))
            {
                var plain = Unprotect(File.ReadAllBytes(SecretsPath));
                if (plain is not null)
                {
                    foreach (var line in Encoding.UTF8.GetString(plain)
                                 .Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        var k = line[..tab];
                        var v = line[(tab + 1)..].TrimEnd('\r');
                        try { dict[k] = Encoding.UTF8.GetString(Convert.FromBase64String(v)); }
                        catch (FormatException) { LogService.Warn("SecretStore", $"凭据条目损坏，已跳过: {k}"); }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("SecretStore", $"凭据读取失败: {ex.Message}");
        }

        s_cache = dict;
        return dict;
    }

    private static void Save(Dictionary<string, string> dict)
    {
        var dir = Path.GetDirectoryName(SecretsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var sb = new StringBuilder();
        foreach (var (k, v) in dict)
            sb.Append(k).Append('\t').Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(v))).Append('\n');

        var cipher = Protect(Encoding.UTF8.GetBytes(sb.ToString()));

        // 原子写入：先写临时文件再 Move 覆盖。
        // File.WriteAllBytes 在写入过程中若进程崩溃/断电，会留下一个截断的 secrets.bin，
        // 解密失败 → 用户已保存的 API Key 永久丢失且无备份可恢复。
        // ✚ 临时文件用随机名：避免可预测路径的符号链接/TOCTOU 风险。
        var tmp = Path.Combine(
            Path.GetDirectoryName(SecretsPath) ?? Path.GetTempPath(),
            $"secrets_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tmp, cipher);
            File.Move(tmp, SecretsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw; // 让调用方（SettingsService.SaveQuiet）决定如何处理
        }

        s_cache = dict;
    }

    // ═══════════════════════════════════════
    //  DPAPI P/Invoke
    // ═══════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DATA_BLOB pDataIn, nint szDataDescr, nint pOptionalEntropy,
        nint pvReserved, nint pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, nint szDataDescr, nint pOptionalEntropy,
        nint pvReserved, nint pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint hMem);

    private static byte[] Protect(byte[] data)
    {
        nint inPtr = Marshal.AllocHGlobal(data.Length);
        var inBlob = new DATA_BLOB { cbData = data.Length, pbData = inPtr };
        var outBlob = default(DATA_BLOB);
        try
        {
            Marshal.Copy(data, 0, inPtr, data.Length);
            if (!CryptProtectData(ref inBlob, 0, 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                throw new CryptographicException(
                    $"CryptProtectData 失败 (Win32={Marshal.GetLastWin32Error()})");
            return CopyAndFreeBlob(outBlob);
        }
        finally { Marshal.FreeHGlobal(inPtr); }
    }

    /// <summary>解密；失败（非本机/非本用户/数据被篡改）返回 null 而非抛出。</summary>
    private static byte[]? Unprotect(byte[] cipher)
    {
        nint inPtr = Marshal.AllocHGlobal(cipher.Length);
        var inBlob = new DATA_BLOB { cbData = cipher.Length, pbData = inPtr };
        var outBlob = default(DATA_BLOB);
        try
        {
            Marshal.Copy(cipher, 0, inPtr, cipher.Length);
            if (!CryptUnprotectData(ref inBlob, 0, 0, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
            {
                LogService.Warn("SecretStore",
                    $"凭据解密失败 (Win32={Marshal.GetLastWin32Error()}) — 可能来自其他用户账户或机器");
                return null;
            }
            return CopyAndFreeBlob(outBlob);
        }
        finally { Marshal.FreeHGlobal(inPtr); }
    }

    /// <summary>拷贝 DPAPI 输出缓冲并释放。DPAPI 用 LocalAlloc 分配，须用 LocalFree 释放。</summary>
    private static byte[] CopyAndFreeBlob(DATA_BLOB blob)
    {
        if (blob.pbData == 0 || blob.cbData <= 0) return [];
        try
        {
            var result = new byte[blob.cbData];
            Marshal.Copy(blob.pbData, result, 0, blob.cbData);
            return result;
        }
        finally { LocalFree(blob.pbData); }
    }
}
