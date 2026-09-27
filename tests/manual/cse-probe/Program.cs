// CSE (Corrupted State Exception) 可捕获性探针
//
// 目的：回答 P0-4 的关键未知 —— 在 .NET 11 (net11.0-windows) 上，
//       原生层触发的 AccessViolationException 到底能不能被 try/catch 拦住？
//
// 背景：TrueToneCap 的 NativeEncoderGuard / OnnxOcrEngine 里有多处
//       catch (AccessViolationException) / catch (SEHException)，
//       意图是在 NVENC / MFT / QSV 原生崩溃时走软件回退链。
//       若 CSE 不可捕获，这些 catch 就是死代码，硬件编码器崩溃会直接杀死进程。
//
// 两种触发方式：
//   nre     读空指针 —— 会被 CLR 转成 NullReferenceException（不是真 AV，仅作对照）
//   pinvoke 经 P/Invoke 让原生代码写无效地址 —— 这才是 NVENC/MFT 崩溃的真实场景
//
// 判据：
//   退出码 0              → 被托管 catch 捕获（回退链可行）
//   退出码 -1073741819    → 0xC0000005，进程被 AV 杀死（catch 是死代码）

using System;
using System.Runtime.InteropServices;

namespace CseProbe;

internal static unsafe class Program
{
    // ⚠️ 原本在此标注 [HandleProcessCorruptedStateExceptions]（.NET Framework 传统做法），
    //    但构建被 SYSLIB0032 拦下（TreatWarningsAsErrors=true），官方原文：
    //      "Recovery from corrupted process state exceptions is not supported;
    //       HandleProcessCorruptedStateExceptionsAttribute is ignored."
    //    —— 编译器层面已确认：该特性在 .NET (Core/5+/11) 上被忽略。故此处不标注。

    [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
    private static extern void RtlMoveMemory(IntPtr dest, IntPtr src, UIntPtr length);

    private static int TriggerViaPinvoke()
    {
        try
        {
            byte[] src = new byte[64];
            // 目标地址远离 null page → 原生层真实访问违规（0xC0000005）
            IntPtr badDest = new IntPtr(0x00000000DEADBEEF);
            var h = GCHandle.Alloc(src, GCHandleType.Pinned);
            try
            {
                Console.WriteLine("PINVOKE: 即将向无效地址写入...");
                RtlMoveMemory(badDest, h.AddrOfPinnedObject(), (UIntPtr)src.Length);
            }
            finally { h.Free(); }

            Console.WriteLine("PINVOKE_NO_AV: 未触发访问违规（意外）");
            return 0;
        }
        catch (AccessViolationException ex)
        {
            Console.WriteLine("CAUGHT_AV: " + ex.GetType().FullName);
            return 0;
        }
        catch (SEHException ex)
        {
            Console.WriteLine("CAUGHT_SEH: " + ex.GetType().FullName);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("CAUGHT_OTHER: " + ex.GetType().FullName);
            return 0;
        }
    }

    private static int TriggerViaNullDeref()
    {
        try
        {
            int* p = (int*)0;
            int v = *p;
            Console.WriteLine($"READ_OK {v}");
            return 0;
        }
        catch (AccessViolationException ex)
        {
            Console.WriteLine("CAUGHT_AV: " + ex.GetType().FullName);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("CAUGHT_OTHER: " + ex.GetType().FullName);
            return 0;
        }
    }

    private static int Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0] : "pinvoke";
        Console.WriteLine($"CSE_PROBE_START mode={mode}");
        try
        {
            return mode switch
            {
                "nre" => TriggerViaNullDeref(),
                _ => TriggerViaPinvoke(),
            };
        }
        finally
        {
            // 若 AV 未被捕获，进程会在此之前直接终止，本行不会出现
            Console.WriteLine("FINALLY_REACHED");
        }
    }
}
