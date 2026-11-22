// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Security/PathSanitizer.cs
// 外部进程文件路径校验 —— 与 ProcessStartInfo.ArgumentList (UseShellExecute = false) 配合使用

using System;
using System.IO;

namespace TrueToneCap.Core.Security;

/// <summary>
/// 校验传给外部编码器 / 工具进程的文件路径。
/// <para>所有外部进程均通过 <c>ProcessStartInfo.ArgumentList</c>（<c>UseShellExecute = false</c>）
/// 直接调用 <c>CreateProcessW</c>，不经过 Shell 解释；因此本类只拒绝 Windows 路径非法字符与控制字符，
/// 不再误杀 <c>)</c>、<c>&amp;</c>、<c>'</c>、<c>;</c> 等合法 Windows 目录/用户名字符。</para>
/// </summary>
public static class PathSanitizer
{
    private static readonly char[] s_invalidPathChars = Path.GetInvalidPathChars();

    // Windows NTFS/FAT 路径段中永远非法的字符（与 Path.GetInvalidFileNameChars 中非分隔符子集对齐）。
    // 合法的 Windows 路径字符如 ) ( & ' ; $ ` % 等绝不拦截。
    private static readonly char[] s_disallowedSegmentChars = ['"', '<', '>', '|', '*', '?'];

    /// <summary>
    /// 校验并规范化用于外部编码器进程的文件路径。
    /// </summary>
    public static string SanitizeForEncoding(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new ArgumentException("Path cannot be empty", nameof(rawPath));

        foreach (var c in rawPath)
        {
            if (char.IsControl(c) || Array.IndexOf(s_invalidPathChars, c) >= 0)
                throw new ArgumentException($"Invalid control or path character (U+{(int)c:X4}) in path", nameof(rawPath));
        }

        var fullPath = Path.GetFullPath(rawPath);

        // 跳过 Windows 扩展长度路径前缀 (\\?\ 或 \\.\) 后再检查非法字符，避免误伤 \\?\ 中的 '?'
        ReadOnlySpan<char> body = fullPath.AsSpan();
        if (body.StartsWith(@"\\?\", StringComparison.Ordinal) || body.StartsWith(@"\\.\", StringComparison.Ordinal))
            body = body[4..];

        int badIdx = body.IndexOfAny(s_disallowedSegmentChars);
        if (badIdx >= 0)
            throw new ArgumentException($"Invalid character '{body[badIdx]}' in path", nameof(rawPath));

        // 绝对路径正常以盘符 (X:\) 或 UNC (\\) 开头；防御性拦截以 '-' 开头避免被 CLI 当成选项
        if (fullPath.StartsWith('-'))
            throw new ArgumentException("Normalized path must not start with '-'", nameof(rawPath));

        // 确保父目录已存在
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Parent directory does not exist: {dir}");

        return fullPath;
    }

    /// <summary>
    /// 批量校验输入与输出文件路径。
    /// </summary>
    public static (string input, string output) SanitizePair(string inputPath, string outputPath)
    {
        return (SanitizeForEncoding(inputPath), SanitizeForEncoding(outputPath));
    }
}
