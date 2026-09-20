// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/AvifHardwareProbe.cs
// AVIF 硬件编码器能力探测 — 用小样张实测编码，非驱动存在性检测

using System.Diagnostics;
using System.Linq;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>NVENC/QSV 实测编码结果。</summary>
public sealed class AvifProbeResult
{
    /// <summary>NVENC 实测可用。</summary>
    public bool NvencAvailable { get; init; }
    public bool QsvAvailable { get; init; }
    /// <summary>检测耗时。</summary>
    public TimeSpan Elapsed { get; init; }
    /// <summary>检测过程中发生的错误（null = 正常）。</summary>
    public string? Error { get; init; }

    public bool AnyHardwareAvailable => NvencAvailable || QsvAvailable;
}

/// <summary>AVIF 硬件编码器实地探测工具。通过实际编码一张样张来验证后端可用性。</summary>
public static class AvifHardwareProbe
{
    /// <summary>
    /// 探测样张边长（正方形）。
    /// <para>必须 ≥ <see cref="NvencAvifBackend.MinNvencDimension"/>。详见 RunProbe 中的说明。</para>
    /// </summary>
    private const int ProbeSize = 256;

    private static AvifProbeResult? _cached;
    private static readonly object _cacheLock = new();

    /// <summary>探测结果缓存。首次调用自动执行探测，后续返回缓存。
    /// ═══ 2026-09-18 P2：双检锁 —— 原实现 check-then-set 非原子，
    /// 首次并发访问会各跑一次探测（各开 NVENC/QSV 会话 + 起子进程）。═══</summary>
    public static AvifProbeResult Result
    {
        get
        {
            if (_cached != null) return _cached;
            lock (_cacheLock)
            {
                if (_cached != null) return _cached;
                _cached = RunProbe();
                return _cached;
            }
        }
    }

    /// <summary>强制重新探测（忽略缓存）。</summary>
    public static AvifProbeResult Refresh()
    {
        _cached = RunProbe();
        return _cached;
    }

    private static AvifProbeResult RunProbe()
    {
        var sw = Stopwatch.StartNew();
        bool nvencOk = false, qsvOk = false;
        string? error = null;

        // ═══ 2026-09-15 修复：样张尺寸必须大于 NVENC 最小支持边长 ═══
        // 原实现用 64×64。而 NVENC 有最小尺寸要求（实测 96×96 即报
        // "Frame dimensions are less than the minimum supported value"），
        // 更小的样张会被直接判定为不适用、转而走软件回退 —— 但输出文件依然有效，
        // 于是探针得出 **"NVENC 可用"的假绿结论**：对外报告硬件可用，实际从不走硬件路径。
        // 256×256 既满足下限，编码开销也可忽略。
        System.Diagnostics.Debug.Assert(
            ProbeSize >= NvencAvifBackend.MinNvencDimension,
            "探测样张过小 —— NVENC 会被前置判断跳过，导致探测结果假绿");

        var testPixels = new byte[ProbeSize * ProbeSize * 4];
        for (int i = 0; i < testPixels.Length; i += 4)
        { testPixels[i] = 128; testPixels[i + 1] = 128; testPixels[i + 2] = 128; testPixels[i + 3] = 255; }

        try
        {
            nvencOk = ProbeSingle(AvifEncoderBackend.Nvenc, testPixels, ProbeSize, ProbeSize);
        }
        catch (Exception ex) { error = $"NVENC probe: {ex.Message}"; }

        try
        {
            qsvOk = ProbeSingle(AvifEncoderBackend.Qsv, testPixels, ProbeSize, ProbeSize);
        }
        catch (Exception ex) { error = (error is null ? "" : error + "; ") + $"QSV probe: {ex.Message}"; }

        sw.Stop();
        return new AvifProbeResult
        {
            NvencAvailable = nvencOk,
            QsvAvailable = qsvOk,
            Elapsed = sw.Elapsed,
            Error = error
        };
    }

    /// <summary>对单个后端执行一次 64×64 实测编码，验证输出 AVIF 文件有效。</summary>
    private static bool ProbeSingle(AvifEncoderBackend backend, byte[] bgra, int w, int h)
    {
        try
        {
            var encoder = AvifEncoderSelector.Select(backend);
            if (!encoder.IsAvailable)
            {
                CoreLog.Warn("AvifProbe", $"{backend}: IsAvailable=false，跳过实测 → 判为不可用", LogCategory.Encoding);
                return false;
            }

            // 随机文件名：固定名可被同主机低权限用户预置硬链接（TOCTOU）
            var tmpPath = Path.Combine(Path.GetTempPath(), $"ttc_avif_probe_{backend}_{Guid.NewGuid():N}.avif");
            // 使用 Task.Run 确保编码在线程池上运行，可直接 .GetAwaiter().GetResult()
            var task = Task.Run(() => encoder.EncodeAsync(bgra, w, h, 30, tmpPath, CancellationToken.None));
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                // ═══ 2026-09-18 P2：超时后不能遗弃任务 —— 编码器仍持有 tmpPath，
                // 可能在下方 File.Delete 之后才写完，留下孤儿文件（且句柄/进程继续占用）。
                // 再等一次（有界），确保任务结束后才清理。═══
                CoreLog.Warn("AvifProbe", $"{backend}: timeout after 5s，等待任务收尾后判为不可用", LogCategory.Encoding);
                try { task.Wait(TimeSpan.FromSeconds(15)); }
                catch { /* 任务自身异常已在委托内消化 */ }
                try { File.Delete(tmpPath); } catch { }
                return false;
            }

            bool valid = File.Exists(tmpPath) && new FileInfo(tmpPath).Length > 100;
            try { File.Delete(tmpPath); } catch { }

            // ═══ 2026-09-15 修复：文件有效 ≠ 真的用了硬件 ═══
            // 后端在任一步失败时都会回退 libaom，并**照样写出一个有效 AVIF 文件**，
            // 于是"文件存在且够大"这个判据恒成立 ⇒ 探测永远返回"可用"（假绿）：
            // UI 显示"NVENC ✓"，实际可能一次都没走过硬件路径，用户完全无法察觉。
            // 现在以后端自报的 LastEncodeUsedHardware 为准
            //（该标志仅在真正由硬件产出码流时置位，EncodeAsync 入口会先归零）。
            // ═══ 2026-09-18 修复（P1-QSV 探测假绿）：QSV 与 NVENC 同样校验 ═══
            // 原实现只对 NVENC 校验 LastEncodeUsedHardware，QSV 路径（MFT）即使中途
            // 失败回退 libaom，探测仍因文件有效而报"QSV ✓"—— 同样的假绿。
            // QSV/MFT 侧的标志由后端（MftAvifEncode）以静态字段承载；
            // 反射读取以容忍字段尚未编译进本次构建：字段缺失时保持旧的文件有效性判据
            //（宁可不拒绝，也不因探测基础设施问题误杀可用后端），字段存在即生效。
            if (backend == AvifEncoderBackend.Nvenc && !NvencAvifBackend.LastEncodeUsedHardware)
            {
                CoreLog.Warn("AvifProbe", "NVENC: 文件有效但并非硬件产出（已回退软件）→ 判为不可用", LogCategory.Encoding);
                return false;
            }
            if (backend == AvifEncoderBackend.Qsv && !QsvLastEncodeUsedHardware())
            {
                CoreLog.Warn("AvifProbe", "QSV: 文件有效但并非硬件产出（已回退软件或标志缺失）→ 判为不可用", LogCategory.Encoding);
                return false;
            }

            if (valid) CoreLog.Info("AvifProbe", $"{backend}: OK", LogCategory.Encoding);
            else CoreLog.Warn("AvifProbe", $"{backend}: FAIL (file too small or missing) → 判为不可用", LogCategory.Encoding);
            return valid;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("AvifProbe", $"{backend}: 探测异常 → 判为不可用", ex, LogCategory.Encoding);
            return false;
        }
    }

    /// <summary>
    /// 读取 QSV/MFT 侧的 LastEncodeUsedHardware 标志（2026-09-18，P1-QSV 探测假绿）。
    /// <para>该标志由 MftAvifEncode 静态承载（QsvAvifBackend 与 MftAvifBackend 共用 MFT 路径），
    /// 仅当真正走 QSV/MFT 硬件路径产出码流时为 true，失败回退 libaom 时为 false。
    /// 经反射读取：类型/字段不存在（旧构建）或读取异常时返回 false —— 保守方向是"不判可用"，
    /// 因为探测假绿的代价（用户被 UI 误导）大于漏判的代价（探测少报一次可用，会重试）。</para>
    /// </summary>
    private static bool QsvLastEncodeUsedHardware()
    {
        try
        {
            var asm = typeof(AvifHardwareProbe).Assembly;
            var t = asm.GetType("TrueToneCap.Core.Encoding.MftAvifEncode", throwOnError: false)
                     ?? asm.GetTypes().FirstOrDefault(x => x.Name == "MftAvifEncode");
            var f = t?.GetField("LastEncodeUsedHardware",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return f?.GetValue(null) is bool b && b;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>生成人类可读的探测摘要。</summary>
    public static string GetSummary()
    {
        var r = Result;
        return $"NVENC={(r.NvencAvailable ? "✓" : "✗")} QSV={(r.QsvAvailable ? "✓" : "✗")} ({r.Elapsed.TotalSeconds:F1}s)";
    }
}
