// TrueToneCap.Core/Processing/ShaderLoader.cs
// 着色器字节码加载器 — 文件系统优先，嵌入资源回退
// 消除 GpuToneMapper 和 GpuEffectProcessor 中的重复加载代码

using System.Reflection;

namespace TrueToneCap.Core.Processing;

/// <summary>HLSL 着色器字节码加载器。文件系统优先，嵌入资源回退。</summary>
public static class ShaderLoader
{
    private static readonly object s_cacheLock = new();
    private static readonly Dictionary<string, byte[]> s_cache = new();

    /// <summary>加载着色器字节码。先查找文件系统 data/Shaders/，再回退到嵌入资源。</summary>
    public static byte[]? Load(string shaderName)
    {
        // 缓存检查
        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(shaderName, out var cached))
                return cached;
        }

        try
        {
            byte[]? bytes = null;

            // 策略1: 从文件系统加载
            var filePath = Path.Combine(AppContext.BaseDirectory, "data", "Shaders", shaderName);
            if (File.Exists(filePath))
                bytes = File.ReadAllBytes(filePath);

            // 策略2: 嵌入资源回退
            if (bytes is null)
            {
                var asm = Assembly.GetExecutingAssembly();
                var resName = asm.GetManifestResourceNames()
                    .FirstOrDefault(n => n.EndsWith(shaderName, StringComparison.OrdinalIgnoreCase));
                if (resName is not null)
                {
                    using var stream = asm.GetManifestResourceStream(resName);
                    if (stream is not null)
                    {
                        bytes = new byte[stream.Length];
                        stream.ReadExactly(bytes);
                    }
                }
            }

            // 写入缓存
            if (bytes is not null)
            {
                lock (s_cacheLock)
                    s_cache[shaderName] = bytes;
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清除着色器缓存（极少使用，仅当热重载时）。</summary>
    public static void ClearCache()
    {
        lock (s_cacheLock)
            s_cache.Clear();
    }
}