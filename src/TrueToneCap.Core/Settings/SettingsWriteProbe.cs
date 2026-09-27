// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Settings/SettingsWriteProbe.cs
// 「设置到底存不存得下来」的判定 —— 用一次真实的建文件/删文件，而不是看属性位。
//
// 为什么不能只看 FileAttributes.ReadOnly：
// 原实现的探测是「文件不存在 ⇒ 认为同目录可用；存在且带只读位 ⇒ 才降级」。
// 但真实世界里"存不下去"的主因【根本没有文件可看】：装到 Program Files（ACL 拒绝普通用户写）、
// 放在只读介质、被策略软件拦写 —— 这些情况下文件还不存在，属性位探测一律放行，
// 于是每次 Save 都抛异常，而调用方清一色走 SaveQuiet()（原先是空的 catch{ }，连日志都没有）
// ⇒ 症状恰好就是这段防护本来要防的那件事："用户改了设置 → 重启回到默认值，且毫无线索"。
// 目录的只读属性位在 Windows 上也不代表不可写（系统忽略它），所以只能真写一次。

namespace TrueToneCap.Core.Settings;

/// <summary>设置持久化位置的可用性判定。</summary>
public static class SettingsWriteProbe
{
    /// <summary>
    /// <paramref name="dir"/> 能否新建并删除一个文件。
    /// <para>刻意用真实写入而不是 <c>Directory.GetAccessControl</c>：ACL 查询要拿
    /// 当前用户与令牌的 effective permission（还要算继承与 deny 顺序），自己算一遍就是
    /// 第二套事实源且必然与系统判定不一致；写一次则与稍后真正要做的写完全同路。</para>
    /// </summary>
    public static bool CanWriteNewFile(string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        // 随机名：避免与并发实例/别的程序抢同一个探针文件
        var probe = Path.Combine(dir, $".truetonecap-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(probe, "");
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            // 失败也必须清：探针留在用户目录里就是垃圾（成功路径同理走这里）
            try { if (File.Exists(probe)) File.Delete(probe); } catch { }
        }
    }

    /// <summary>
    /// 设置文件能否被本程序持续读写 —— 目录真写得进去，且【已存在的】文件不是只读。
    /// <para>返回 false 时程序的口径是：<b>既不读也不写这个文件，全程按默认值表运行，并把问题记进日志</b>。
    /// 不做"悄悄换个位置存"的降级 —— 那会让"设置到底存在哪"出现第二个事实源，
    /// 而用户界面上完全看不出来（这正是 2026-09-25 之前那段注释承诺过、实现却没做到的事）。</para>
    /// </summary>
    public static bool CanPersistSettings(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(filePath) && File.GetAttributes(filePath).HasFlag(FileAttributes.ReadOnly))
                return false;
            return CanWriteNewFile(dir);
        }
        catch
        {
            return false;   // 连"能不能写"都问不出来的环境，一律按不可持久化处理
        }
    }
}
