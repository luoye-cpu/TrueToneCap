// ═══ Security Fix: Command Injection Prevention for External Encoders ═══
// 2026-11-08: Critical CVE fix - Sanitize file paths before passing to native encoders
// Reference: CWE-78 OS Command Injection in NativeAvifEncoder.cs:485, etc.

using System;
using System.IO;
using System.Security;

namespace TrueToneCap.Core.Security;

/// <summary>
/// Provides input sanitization for file paths passed to external process encoders
/// Prevents OS command injection via malicious filename/path characters
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] s_invalidChars = Path.GetInvalidPathChars();
    private static readonly string[] s_shellMetacharacters = new[] { ";", "|", "&", "`", "$(", "${", ")", "'", "\"", "<", ">", "\n", "\r" };

    /// <summary>
    /// Validates and sanitizes a file path for safe use in external encoder commands
    /// </summary>
    public static string SanitizeForEncoding(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("Path cannot be empty", nameof(rawPath));

        var fullPath = Path.GetFullPath(rawPath);
        
        // Check for invalid path characters (Windows-specific)
        foreach (var c in s_invalidChars)
        {
            if (fullPath.Contains(c))
                throw new ArgumentException($"Invalid character '{c}' in path", nameof(rawPath));
        }

        // Check for shell metacharacters that could execute arbitrary commands
        foreach (var metaChar in s_shellMetacharacters)
        {
            if (fullPath.Contains(metaChar))
                throw new SecurityException(
                    $"Path contains potentially dangerous shell metacharacter '{metaChar}'");
        }

        // Additional validation: Ensure path exists or is creatable
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"Parent directory does not exist: {dir}");
        }

        return fullPath;
    }

    /// <summary>
    /// Validates multiple paths at once (batch mode for encoding pipelines)
    /// </summary>
    public static (string input, string output) SanitizePair(string inputPath, string outputPath)
    {
        return (SanitizeForEncoding(inputPath), SanitizeForEncoding(outputPath));
    }
}
