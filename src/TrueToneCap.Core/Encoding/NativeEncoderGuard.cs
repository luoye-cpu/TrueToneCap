// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/NativeEncoderGuard.cs
// P/Invoke 崩溃隔离层 — 捕获 CSE (AccessViolationException/SEHException)
// 防止原生编码器 (MFT/NVENC/QSV) 崩溃导致整个应用进程终止
// 注意: .NET Core 中 CSE 捕获通过 csproj <LegacyCorruptedStateExceptionsPolicy>true</...> 启用

using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// 原生编码器子进程的超时处置 —— 唯一入口是下面两个 Drain 方法。
/// <para>两件必须同时成立的事，做错一件等于没做：</para>
/// <para>① <b>超时可达</b>：<c>StandardOutput.ReadToEnd()</c> 是同步阻塞的，子进程只要不退出就不会
/// 关闭管道写端 ⇒ 这句<b>永不返回</b>，后面那句 <c>WaitForExit(ms)</c> 压根执行不到，5s/30s 全是摆设。
/// （2026-09-19 先修了"丢掉返回值"那一半，2026-09-20 才发现真正挡在前面的是这句。）
/// 修法：两条管道<b>同时</b>挂异步读，再等进程退出 —— 排空与等待并行，于是既不因管道满而死锁，
/// 也不因进程不退出而丢超时。</para>
/// <para>② <b>超时后不读 ExitCode</b>：进程未退出时读它会抛 InvalidOperationException，冒出来的错名
/// 是"编码失败 (exit=…)"，既看不出根因是超时，又把<b>仍在运行的子进程</b>留在后台（avifenc/cjxl
/// 编 4K 能吃满 CPU，下一次截图又起一个，累积成资源泄漏）。⇒ 超时一律 Kill 整棵进程树，
/// 探测档返回 false、编码档抛 <see cref="TimeoutException"/>。</para>
/// <para>判据两条，不可互替：<c>Test_NativeEncodeTimeoutIsJudged</c>（钉形态，全仓不许再出现
/// 同步 <c>ReadToEnd()</c> 或裸 <c>WaitForExit</c>）+ <c>Test_NativeProbeTimeoutIsReachable</c>
/// （真造"挂死"与"管道满"两种子进程量返回时间，专治把 <c>WaitForExit</c> 提到读管道之前的错修法）。</para>
/// </summary>
public static class NativeProcess
{
    /// <summary>可用性探测的超时档位（原先散在 6 处字面量 <c>WaitForExit(5000)</c>）。</summary>
    public const int ProbeTimeoutMs = 5_000;

    /// <summary>进程已退出后，留给两条管道把剩余字节排空完的时间窗。</summary>
    private const int DrainGraceMs = 2_000;

    /// <summary>
    /// 排空两条管道 + 拿到退出码；<b>超时可达</b>版，供可用性探测用（超时不抛，返回 false）。
    /// <para>为什么必须有这个函数：<c>proc.StandardOutput.ReadToEnd()</c> 是同步阻塞的，
    /// 而子进程只要还活着就不会关闭写端 —— 于是这句在<b>永不退出</b>的子进程上永远不返回，
    /// 后面那句 <c>WaitForExit(5000)</c> 根本执行不到，5 秒超时形同虚设。
    /// 实测过的现场：<c>cmd /c "ping -n 30 127.0.0.1"</c>（继承了 stderr 却不写），
    /// 旧写法要挂 30 秒才返回，本函数按超时在毫秒级返回 false。</para>
    /// <para>修法是把两条管道同时挂成异步读，再 <c>WaitForExit(timeoutMs)</c>；
    /// 排空动作与等待并行进行 ⇒ 既不因管道满而死锁，也不因进程不退出而丢超时。</para>
    /// </summary>
    /// <returns>true = 子进程在 timeoutMs 内退出，<paramref name="exitCode"/> 有效；
    /// false = 超时（子进程已被终止），此时不读 ExitCode（读它会抛"进程状态非法"）。</returns>
    public static bool DrainProbe(System.Diagnostics.Process proc, int timeoutMs,
        out int exitCode, out string stdout, out string stderr)
    {
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            try { proc.WaitForExit(1_000); } catch { }
            exitCode = -1; stdout = string.Empty; stderr = string.Empty;
            return false;
        }

        (stdout, stderr) = AwaitDrain(outTask, errTask);
        exitCode = proc.ExitCode;
        return true;
    }

    /// <summary>排空两条管道 + 拿到退出码，超时按 <see cref="TimeoutException"/> 抛出（编码通路口径）。</summary>
    /// <exception cref="TimeoutException">子进程超时未退出（已终止整棵进程树）。</exception>
    public static (int exitCode, string stdout, string stderr) DrainAndFinish(
        System.Diagnostics.Process proc, int timeoutMs, string what)
    {
        if (!DrainProbe(proc, timeoutMs, out int ec, out string so, out string se))
            throw new TimeoutException($"[{what}] 子进程超过 {timeoutMs / 1000}s 未退出，已终止");
        return (ec, so, se);
    }

    /// <summary>进程退出后排空两条管道；用尽窗口就按"没读到"返回，不把它变成第二个挂死点。</summary>
    private static (string stdout, string stderr) AwaitDrain(
        System.Threading.Tasks.Task<string> outTask, System.Threading.Tasks.Task<string> errTask)
    {
        try { System.Threading.Tasks.Task.WaitAll(new System.Threading.Tasks.Task[] { outTask, errTask }, DrainGraceMs); }
        catch { /* 超时窗口用尽，或某条管道被继承者占着 —— 下面按 IsCompleted 取值 */ }
        return (Safe(outTask), Safe(errTask));

        static string Safe(System.Threading.Tasks.Task<string> t) =>
            t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : string.Empty;
    }
}

/// <summary>
/// 原生编码器安全执行包装器。
/// 捕获所有异常（包括 CSE），提供结构化回退。
/// </summary>
public static class NativeEncoderGuard
{
    /// <summary>执行结果。</summary>
    public readonly struct GuardResult<T>
    {
        public bool Success { get; init; }
        public T? Value { get; init; }
        public Exception? Error { get; init; }
        public string EncoderName { get; init; }

        public static GuardResult<T> Ok(T value, string name) => new() { Success = true, Value = value, EncoderName = name };
        public static GuardResult<T> Fail(Exception ex, string name) => new() { Success = false, Error = ex, EncoderName = name };
    }

    /// <summary>
    /// 安全执行原生编码操作。
    ///
    /// ⛔ 2026-09-15 重要更正：下面两个 CSE 分支（AccessViolationException / SEHException）
    ///    在 .NET (Core/5+/11) 上【永远不会命中】—— CSE 属损坏进程状态异常，进程内不可捕获。
    ///    实测：P/Invoke 触发 0xC0000005 后进程直接终止（退出码 -1073741819），
    ///    本方法的 catch 与调用方 finally 均未执行；且 LegacyCorruptedStateExceptionsPolicy
    ///    属性已不再写入 runtimeconfig（加与不加 runtimeconfig 逐字节一致）。
    ///    详见 docs/pipeline-review-2026-09-15.md 的 P0-4。
    ///
    ///    保留这两个分支仅作文档/未来兼容之用，【不要】依赖它们做"崩溃已防护"的假设。
    ///    真正可靠的隔离是【子进程】：avifenc / cjxl / cwebp 已是子进程，崩溃只影响子进程；
    ///    而 MFT / NVENC / QSV 是进程内 P/Invoke，其崩溃目前无法拦截。
    /// </summary>
    public static GuardResult<byte[]> TryEncode(string encoderName, Func<byte[]> encodeAction)
    {
        try
        {
            var result = encodeAction();
            if (result is null || result.Length == 0)
                return GuardResult<byte[]>.Fail(
                    new InvalidOperationException($"{encoderName}: 编码输出为空"), encoderName);
            return GuardResult<byte[]>.Ok(result, encoderName);
        }
        catch (AccessViolationException ex)
        {
            LogCrash(encoderName, "AccessViolation (CSE)", ex);
            return GuardResult<byte[]>.Fail(ex, encoderName);
        }
        catch (System.Runtime.InteropServices.SEHException ex)
        {
            LogCrash(encoderName, "SEH (CSE)", ex);
            return GuardResult<byte[]>.Fail(ex, encoderName);
        }
        catch (BadImageFormatException ex)
        {
            LogCrash(encoderName, "BadImage (DLL 损坏)", ex);
            return GuardResult<byte[]>.Fail(ex, encoderName);
        }
        catch (DllNotFoundException ex)
        {
            LogCrash(encoderName, "DllNotFound", ex);
            return GuardResult<byte[]>.Fail(ex, encoderName);
        }
        catch (Exception ex)
        {
            LogCrash(encoderName, ex.GetType().Name, ex);
            return GuardResult<byte[]>.Fail(ex, encoderName);
        }
    }

    /// <summary>
    /// 安全执行原生编码操作（无返回值版本）。
    /// </summary>
    public static GuardResult<bool> TryExecute(string encoderName, Action action)
    {
        try
        {
            action();
            return GuardResult<bool>.Ok(true, encoderName);
        }
        catch (AccessViolationException ex)
        {
            LogCrash(encoderName, "AccessViolation (CSE)", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (System.Runtime.InteropServices.SEHException ex)
        {
            LogCrash(encoderName, "SEH (CSE)", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (BadImageFormatException ex)
        {
            LogCrash(encoderName, "BadImage (DLL 损坏)", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (DllNotFoundException ex)
        {
            LogCrash(encoderName, "DllNotFound", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (Exception ex)
        {
            LogCrash(encoderName, ex.GetType().Name, ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
    }

    /// <summary>
    /// 安全执行异步编码操作，含 CSE 隔离 + 超时保护。
    /// </summary>
    public static async Task<GuardResult<bool>> TryEncodeAsync(
        string encoderName, Func<Task> encodeAction,
        int timeoutMs = 30_000, CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);

            var task = encodeAction();
            var completed = await Task.WhenAny(task, Task.Delay(Timeout.Infinite, cts.Token));

            if (completed != task)
            {
                var timeoutEx = new TimeoutException($"{encoderName}: 编码超时 ({timeoutMs}ms)");
                LogCrash(encoderName, "Timeout", timeoutEx);
                return GuardResult<bool>.Fail(timeoutEx, encoderName);
            }

            await task; // 传播异常
            return GuardResult<bool>.Ok(true, encoderName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 用户取消，正常传播
        }
        catch (AccessViolationException ex)
        {
            LogCrash(encoderName, "AccessViolation (CSE)", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (System.Runtime.InteropServices.SEHException ex)
        {
            LogCrash(encoderName, "SEH (CSE)", ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
        catch (Exception ex)
        {
            LogCrash(encoderName, ex.GetType().Name, ex);
            return GuardResult<bool>.Fail(ex, encoderName);
        }
    }

    private static void LogCrash(string encoder, string category, Exception ex)
    {
        var msg = $"[NativeEncoderGuard] ⚠️ {encoder} 崩溃隔离 [{category}]: {ex.Message}";
        CoreLog.Warn("NativeEncoderGuard", $"{encoder} 崩溃隔离 [{category}]", ex, LogCategory.Encoding);
        Console.Error.WriteLine(msg);
    }
}
