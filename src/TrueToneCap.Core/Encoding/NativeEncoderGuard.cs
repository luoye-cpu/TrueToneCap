// TrueToneCap.Core/Encoding/NativeEncoderGuard.cs
// P/Invoke 崩溃隔离层 — 捕获 CSE (AccessViolationException/SEHException)
// 防止原生编码器 (MFT/NVENC/QSV) 崩溃导致整个应用进程终止
// 注意: .NET Core 中 CSE 捕获通过 csproj <LegacyCorruptedStateExceptionsPolicy>true</...> 启用

namespace TrueToneCap.Core.Encoding;

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
    /// 安全执行原生编码操作，捕获所有异常（含 CSE）。
    /// 需要 csproj 中设置 &lt;LegacyCorruptedStateExceptionsPolicy&gt;true&lt;/LegacyCorruptedStateExceptionsPolicy&gt;
    /// 才能捕获 AccessViolationException。
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
        System.Diagnostics.Debug.WriteLine(msg);
        Console.Error.WriteLine(msg);
    }
}
