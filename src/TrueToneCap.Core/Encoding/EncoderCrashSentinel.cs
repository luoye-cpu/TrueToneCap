// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/EncoderCrashSentinel.cs
//
// 编码器崩溃哨兵 —— 在「CSE 不可捕获」这一前提下，让崩溃变成可恢复的状态。
//
// ── 为什么需要它 ──
// P/Invoke 触发的 AccessViolationException 在 .NET (Core/5+/11) 上【无法被 catch】：
// 进程直接终止，既没有回退机会，也来不及写任何日志。详见 NativeEncoderGuard 的注释
// 与 tests/manual/cse-probe 的实测（退出码 -1073741819，catch 与 finally 均未执行）。
// 于是 NVENC / MFT / QSV 这类进程内原生编码器一旦崩溃，用户看到的是"程序闪退"，
// 而下次启动又会走进同一条路径、再崩一次 —— 没有任何自愈能力。
//
// ── 思路 ──
// 既然无法在崩溃【当时】做点什么，就把判断推迟到崩溃【之后】：
//   1. 进入原生编码前写哨兵（记录后端名 + 写入进程的 PID + 时间）
//   2. 正常返回后清除哨兵
//   3. 若进程在编码途中死亡 → 哨兵必然残留
//   4. 下次启动读到残留哨兵 → 判定"上次是在这个后端崩的" → 自动禁用该后端，
//      回退到软件编码（avifenc / libaom），从而不再重复崩溃
//
// 第 1 步里的 PID 不是可选项：同一进程内两次并发 Arm 时，后来者读到的那个在途文件
// 与"上次进程崩了留下的文件"长得一模一样，只能靠 PID（以及本进程自己的在途计数）分开。
//
// 这不是"修复崩溃"，而是把不可恢复的硬崩溃降级为可自愈的软件路径。
// 用户在设置里应能看到"硬件编码已因上次崩溃而停用"，并可手动重置（ClearAll）。

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;

namespace TrueToneCap.Core.Encoding;

/// <summary>记录"进程是否在某个原生编码后端中崩溃"，用于下次启动时自动降级。</summary>
public static class EncoderCrashSentinel
{
    private static readonly object s_lock = new();

    /// <summary>当前进行中的原生编码调用数量（按后端分别计数）。
    ///
    /// ═══ 2026-09-18 修复（P1-哨兵并发归属错误）═══
    /// 原实现是【单计数 + 单哨兵文件】：NVENC 与 QSV/MFT 并发编码时，
    //  先返回的那个把计数减到 0 就把哨兵删了，另一个仍在原生调用中的线程失去保护；
    //  更糟的是两个后端互相覆盖同一个哨兵文件，崩溃后读出的"归属后端"可能张冠李戴 ——
    //  明明是 NVENC 崩了，却因为 QSV 晚一步写文件而把 QSV 禁用。
    //  现改为【按后端一计数、一文件】：文件名含后端名（encoder-crash.&lt;backend&gt;.sentinel），
    //  Arm/Disarm 各自只动自己后端的文件，互不干扰；IsBlocked 除文件名+首行外，还核对 PID 与本进程在途计数
    //  （2026-09-27：同进程里后 Arm 的那个，不再把前一个仍在原生调用中的在途文件读成"上次进程崩了"）。
    //  各计数只存在于进程内，进程崩溃时自然丢失（哨兵随之残留），这正是期望行为。
    /// </summary>
    private static readonly ConcurrentDictionary<string, int> s_armCounts = new(StringComparer.OrdinalIgnoreCase);

    private static string SentinelDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueToneCap");

    /// <summary>指定后端的哨兵文件路径（2026-09-18：文件名含后端名，各后端互不覆盖）。
    /// <para>internal 而不是 private：用例要能就地伪造一份"别的进程留下的"哨兵文件，
    /// 而不必把这条路径的拼法在测试里再抄一遍（抄一遍就意味着它可以悄悄跟实现分叉）。</para></summary>
    internal static string SentinelPathFor(string backend)
    {
        // 文件名安全化：后端名只含字母数字，但防御性替换一遍
        var safe = string.Join("_", backend.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(SentinelDir, $"encoder-crash.{safe}.sentinel");
    }

    /// <summary>
    /// 上次进程是否在使用指定后端时崩溃（哨兵残留）。
    /// ⚠️ 读取失败一律返回 false —— 诊断功能绝不能反过来挡住正常编码。
    /// <para>═══ 2026-09-20：禁用只持续【下一次启动的那一个会话】═══
    /// 本函数是"读后即消费"：判定为 blocked 的这一次就把哨兵删掉，同时在进程内记住结论，
    /// 于是本会话所有编码都避开该后端，而下一次启动会重新试一次。
    /// 原实现只读不删：因为被禁用的后端再也不会 <see cref="Arm"/>，也就永远不会
    /// <see cref="Disarm"/> 去清文件 —— 一次偶发崩溃把硬件编码<b>永久</b>关掉，重启也不恢复。
    /// 这与 <c>NvencAvifBackend</c> 里"一次偶发失败 ⇒ 全局永久退化"（2026-09-15 已就内存缓存
    /// 做过分类修复）是同一个病，只是这个版本跨进程存活，更严重。
    /// 若真的每次都崩，行为就是"每次启动崩一次→当次禁用"，仍然不会再连续闪退。
    /// 手动复位入口 <see cref="ClearAll"/> 保留，但 2026-09-18 起界面上已无"硬件编码"复选框
    /// （用户指令移除），所以不能把恢复寄托在它身上。</para>
    /// <para>═══ 2026-09-27：只认【别的进程】留下的哨兵 ═══
    /// 文件是【本进程】写的（PID 相同）、或本进程仍有该后端的调用在途（Arm 计数 &gt; 0）时，
    /// 它的存在只说明"有人正在原生编码"，不说明"上次崩了"。这两种情况返回 false 并且
    /// <b>不删文件</b> —— 旧实现没有这层区分，于是第二个并发编码会把第一个仍在原生调用中的
    /// 保护文件删掉，还顺手弹一条假的"上次 NVENC 导致进程崩溃"。</para>
    /// </summary>
    public static bool IsBlocked(string backend)
    {
        lock (s_lock)
        {
            try
            {
                if (s_consumedThisSession.Contains(backend)) return true;
                var p = SentinelPathFor(backend);
                if (!File.Exists(p)) return false;
                // 首行是后端名，次行是写入进程的 PID，第三行是时间戳（供人工排查）。
                // 首行与请求的后端名双重校验：旧版单文件残留时首行可能是别的后端，
                // 此时不能凭文件存在就误判，也不能替别人消费掉哨兵。
                // ⚠ 必须先用完再关掉 reader 才去删：Windows 上 File.Delete 打不开着的文件，
                //   旧写法把 `using var reader` 和删除放在同一作用域 ⇒ 删除恒定抛、又被下面的
                //   catch{} 吞掉 ⇒ "读后即消费"其实一次也没消费掉文件，下次启动照样看到残留，
                //   正是 2026-09-20 注释声称修掉的"一次偶发崩溃把硬件永久关掉"。
                //   这条是 2026-09-29 新增的"消费后文件必须消失"断言抓出来的，不是读代码读出来的。
                string? first, second;
                using (var reader = new StreamReader(p))
                {
                    first = reader.ReadLine();
                    second = reader.ReadLine();
                }
                if (string.IsNullOrWhiteSpace(first)
                    || !string.Equals(first.Trim(), backend, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (InFlightByThisProcess(backend, second))
                    return false;   // 在途而非残留：这个文件归那个还没返回的调用负责清，这里不许动

                s_consumedThisSession.Add(backend);
                try { File.Delete(p); } catch { /* 删不掉只影响下次的重试机会，不影响本会话判定 */ }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>哨兵是否出自【本进程】、且那次调用还没返回 —— 两种信号任一成立即算在途。
    /// <para>两条都要：PID 相同只说明"文件是本进程写的"，而本进程也可能早已 Disarm 完；
    /// 计数 &gt; 0 则直接说明该后端还有一次原生调用没回来。同进程内并发 Arm 共用同一个文件路径，
    /// 后 Arm 者只是把内容重写一遍（PID 不变），所以两者合起来才足以断定"在途"。</para>
    /// <para>PID 行缺失或不是整数（2026-09-27 之前的两行旧格式）⇒ 判为"不是本进程"，仍走读后即消费：
    /// 宁可少一次在途保护，也不让旧格式残留卡在下次启动。</para>
    /// <para>没覆盖的：两路 TrueToneCap 实例并发编码时彼此看到的都是"别的 PID"，仍会被当成崩过残留 ——
    /// 那是跨进程归属，需要文件锁或实例标识，不在这次范围内。</para></summary>
    private static bool InFlightByThisProcess(string backend, string? pidLine)
    {
        if (s_armCounts.TryGetValue(backend, out int inflight) && inflight > 0)
            return true;

        return int.TryParse(pidLine?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int writerPid)
            && writerPid == Environment.ProcessId;
    }

    /// <summary>进入原生编码【之前】调用：标记"正在用该后端编码"。</summary>
    public static void Arm(string backend)
    {
        lock (s_lock)
        {
            s_armCounts.AddOrUpdate(backend, 1, (_, c) => c + 1);
            try
            {
                var p = SentinelPathFor(backend);
                var dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(p,
                    backend + Environment.NewLine +
                    // PID 行：让 IsBlocked 能把"本进程还在途"与"上次进程崩溃残留"分开
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
                    DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture));
            }
            catch
            {
                // 写不了就算了：最坏情况是失去这次保护，但绝不能影响编码本身
            }
        }
    }

    /// <summary>原生编码【正常返回后】调用：递减【本后端】计数，归零时才清除该后端的标记。
    /// <para>backend 有默认值：现有调用方（NVENC 路径）均为无参调用，保持兼容。</para></summary>
    public static void Disarm(string backend = "NVENC")
    {
        lock (s_lock)
        {
            // 还有其它线程仍在用该后端编码 —— 保留哨兵，等最后一个返回者再删。
            // ═══ 2026-09-18：只删/只递减自己后端的，绝不再波及其他后端的哨兵 ═══
            int left = s_armCounts.AddOrUpdate(backend, -1, (_, c) => c - 1);
            if (left > 0) return;
            // 未配对的 Disarm（异常路径/重复调用）不应让计数长期为负，否则后续
            // 永远删不掉哨兵；兜底归零后照常清除。
            if (left < 0)
            {
                s_armCounts[backend] = 0;
            }
            // 走到这里 = 该后端刚从一次原生调用中【正常返回】 ⇒ 既删文件，也忘掉"本会话已消费"。
            // 少了后半句，被消费过哨兵的后端即使后来真的成功编完一次，仍会被一直判为禁用。
            // （s_consumedThisSession 只在 s_lock 下读写，与 IsBlocked/Disarm 同锁，不需第二把锁）
            DeleteSentinel(backend);
            s_consumedThisSession.Remove(backend);
        }
    }

    /// <summary>本会话内已经消费过哨兵的后端（保持整会话一致的禁用结论，不在两次编码之间摇摆）。
    /// 与该后端一次【成功返回】的 Disarm 一起清除——那就是"它其实没崩"的直接证据。</summary>
    private static readonly HashSet<string> s_consumedThisSession = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>手动重置（供设置界面"重新启用硬件编码"使用）。无条件清除所有后端哨兵并归零计数。</summary>
    public static void ClearAll()
    {
        lock (s_lock)
        {
            s_armCounts.Clear();
            s_consumedThisSession.Clear();   // 否则"手动重置"在本进程内不生效（会话级禁用记还没忘）
            DeleteAllSentinels();
        }
    }

    private static void DeleteSentinel(string backend)
    {
        try
        {
            var p = SentinelPathFor(backend);
            if (File.Exists(p)) File.Delete(p);
        }
        catch
        {
            // 忽略：诊断功能不应影响编码本身
        }
    }

    /// <summary>删除目录下全部后端的哨兵文件（2026-09-18：ClearAll 需覆盖旧版单文件与各后端文件）。</summary>
    private static void DeleteAllSentinels()
    {
        try
        {
            var dir = SentinelDir;
            if (!Directory.Exists(dir)) return;
            foreach (var p in Directory.EnumerateFiles(dir, "encoder-crash*.sentinel"))
            {
                try { File.Delete(p); } catch { /* 单个删不掉不阻塞其余 */ }
            }
        }
        catch
        {
            // 忽略：诊断功能不应影响编码本身
        }
    }

    /// <summary>是否存在任一后端哨兵（诊断用）。</summary>
    public static bool IsArmed()
    {
        lock (s_lock)
        {
            try
            {
                var dir = SentinelDir;
                return Directory.Exists(dir)
                    && Directory.EnumerateFiles(dir, "encoder-crash*.sentinel").Any();
            }
            catch { return false; }
        }
    }
}
