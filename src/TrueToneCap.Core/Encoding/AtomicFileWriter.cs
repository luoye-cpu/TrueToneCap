// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/AtomicFileWriter.cs
// 托管编码器的"原子写文件"助手 —— 同目录临时文件 + 成功后替换，失败退回直写。

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// 把最终产物写到同目录的临时文件、写成功后再替换目标路径。
/// <para><b>为什么需要它</b>：PNG / TIFF 的托管编码器原先直接 <c>File.Create(path)</c> 后逐 chunk 写，
/// 中途任何异常（参数校验失败、磁盘写满、取消）都会在最终路径留下一份坏文件：
/// 最轻的是 0 字节但 <c>File.Exists</c> 为真的空文件，重的是写了一半的 PNG / TIFF。
/// 上层 <c>FormatHelper.ResolveActualPath</c> 在没有登记回退时原样返回请求路径，
/// 于是"产物已就位"这个假象一路留到用户面前 —— 与"静默降级"同一类看不出来的失败。</para>
/// <para><b>做法与本仓库同源</b>：<see cref="IvfWriter.WriteAvif"/> 的硬件 AVIF 路径已在用这套
/// （同目录 <c>.文件名.GUID.tmp</c> → 写完后 <c>File.Move(overwrite: true)</c>；临时文件建不出来时
/// 退回直写目标路径）。此处只把同一条逻辑集中一次实现，供 PNG（3 处）与 TIFF（1 处）复用，
/// 避免四份副本各自漂移。</para>
/// </summary>
internal static class AtomicFileWriter
{
    /// <summary>
    /// 落盘流缓冲（2026-10-31）：托管编码器的写体是"小头 + 大 chunk"混合模式
    /// （PNG 逐 chunk、TIFF 头/值区/多条 strip 交替回填），默认 4 KiB 会把小块写打成一串小系统调用。
    /// 1 MiB 时小块在缓冲里合并；≥缓冲的大块写直接透传，不多拷一遍 ⇒ 两头都不吃亏。
    /// 产物字节与缓冲大小无关（只影响系统调用形状）。
    /// </summary>
    private const int WriteBufferBytes = 1 << 20;

    /// <summary>
    /// 以流的形式原子写入 <paramref name="path"/>。
    /// <para>写体（<paramref name="writeBody"/>）抛异常时：临时文件被删除、最终路径保持原样
    /// （不会被半成品覆盖），异常原样上抛 —— 不回退、不吞。</para>
    /// </summary>
    internal static void Write(string path, Action<FileStream> writeBody)
    {
        var fullPath = Path.GetFullPath(path);
        var tempPath = Path.Combine(Path.GetDirectoryName(fullPath) ?? ".",
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        FileStream fs;
        try
        {
            fs = File.Create(tempPath, WriteBufferBytes);
        }
        catch
        {
            // 同目录建不出临时文件（目录只读、权限、策略拦截）⇒ 退回直写目标路径：
            // 与 WriteAvif 同策略，不因"修原子性"而新增一条完全写不出文件的失败路径。
            tempPath = fullPath;
            fs = File.Create(fullPath, WriteBufferBytes);
        }

        bool completed = false;
        try
        {
            writeBody(fs);
            completed = true;
        }
        finally
        {
            // 必须先关闭句柄：Windows 上替换目标文件要求源与目标都未被占用
            fs.Dispose();
            if (!completed && tempPath != fullPath) TryDelete(tempPath);
        }

        if (tempPath == fullPath) return;   // 退回直写的场景：无中间文件可提交

        try
        {
            File.Move(tempPath, fullPath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* 清理失败不改变编码结果，忽略 */ }
    }
}
