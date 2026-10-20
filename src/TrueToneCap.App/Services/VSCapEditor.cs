// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
using System.Diagnostics;
using System.Text.Json;

namespace TrueToneCap.App.Services;

internal static class VSCapEditor
{
    public static string ExecutablePath => Path.Combine(AppContext.BaseDirectory, "data", "vscap", "ttc-vscap.exe");

    /// <summary>侧车是否随本包分发。Qt 不在构建机上时 `build/vscap-runtime` 是空的，
    /// csproj 那条 glob 展开成 0 个条目 ⇒ 构建与打包全绿，但功能不存在。
    /// <para>所以入口必须问这句，而不是等用户点开才收到 FileNotFoundException。</para></summary>
    public static bool IsAvailable { get; } = File.Exists(ExecutablePath);

    public static Task OpenAsync(VSCapFrame? frame, int x, int y, string outputDirectory,
        Func<VSCapFrame, bool, string, Task> export, Action<string> reportError)
    {
        string executable = ExecutablePath;
        if (!File.Exists(executable)) throw new FileNotFoundException("VS-Cap editor is missing. Run scripts/BuildVSCap.ps1 before publishing.", executable);
        string session = Path.Combine(Path.GetTempPath(), "TrueToneCap-VSCap", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(session);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--session"); start.ArgumentList.Add(session);
        start.ArgumentList.Add("--output-dir"); start.ArgumentList.Add(outputDirectory);
        start.ArgumentList.Add("--parent-pid"); start.ArgumentList.Add(Environment.ProcessId.ToString());
        try
        {
            if (frame is null) start.ArgumentList.Add("--paste");
            else
            {
                string input = Path.Combine(session, "capture.frame");
                frame.Write(input, x, y);
                start.ArgumentList.Add("--capture"); start.ArgumentList.Add(input);
            }
            var process = Process.Start(start) ?? throw new IOException("Cannot launch VS-Cap editor");
            _ = ObserveAsync(process, session, ready, export, reportError);
        }
        catch
        {
            Directory.Delete(session, true);
            throw;
        }
        return ready.Task;
    }

    // Called on the WinUI thread; continuations retain its synchronization context.
    private static async Task ObserveAsync(Process process, string session, TaskCompletionSource ready,
        Func<VSCapFrame, bool, string, Task> export, Action<string> reportError)
    {
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var message = json.RootElement;
                switch (message.GetProperty("event").GetString())
                {
                    case "selection-closed": ready.TrySetResult(); break;
                    case "error": reportError(message.GetProperty("message").GetString() ?? "VS-Cap failed"); break;
                    case "export":
                        string path = message.GetProperty("frame").GetString()!;
                        try { await export(VSCapFrame.Read(path), message.GetProperty("avif").GetBoolean(), message.GetProperty("destination").GetString()!); }
                        catch (Exception ex) { reportError(ex.Message); }
                        finally { File.Delete(path); }
                        break;
                }
            }
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) reportError($"VS-Cap exited ({process.ExitCode}): {await errors}");
        }
        catch (Exception ex) { reportError(ex.Message); }
        finally
        {
            ready.TrySetResult();
            process.Dispose();
            try { Directory.Delete(session, true); } catch (IOException) { }
        }
    }
}
