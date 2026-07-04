// TrueToneCap.Core/Encoding/AvifHardwareProbe.cs
// AVIF 硬件编码器能力探测 — 用小样张实测编码，非驱动存在性检测

using System.Diagnostics;

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

/// <summary>AVIF 硬件编码器实地探测工具。通过 64×64 样张实际编码验证后端可用性。</summary>
public static class AvifHardwareProbe
{
    private static AvifProbeResult? _cached;

    /// <summary>探测结果缓存。首次调用自动执行探测，后续返回缓存。</summary>
    public static AvifProbeResult Result
    {
        get
        {
            if (_cached != null) return _cached;
            _cached = RunProbe();
            return _cached;
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

        // 64×64 灰色测试样张
        var testPixels = new byte[64 * 64 * 4];
        for (int i = 0; i < testPixels.Length; i += 4)
        { testPixels[i] = 128; testPixels[i + 1] = 128; testPixels[i + 2] = 128; testPixels[i + 3] = 255; }

        try
        {
            nvencOk = ProbeSingle(AvifEncoderBackend.Nvenc, testPixels, 64, 64);
        }
        catch (Exception ex) { error = $"NVENC probe: {ex.Message}"; }

        try
        {
            qsvOk = ProbeSingle(AvifEncoderBackend.Qsv, testPixels, 64, 64);
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
                Debug.WriteLine($"[AvifProbe] {backend}: IsAvailable=false, skip");
                return false;
            }

            var tmpPath = Path.Combine(Path.GetTempPath(), $"ttc_avif_probe_{backend}.avif");
            // 使用 Task.Run 确保编码在线程池上运行，可直接 .GetAwaiter().GetResult()
            var task = Task.Run(() => encoder.EncodeAsync(bgra, w, h, 30, tmpPath, CancellationToken.None));
            if (!task.Wait(TimeSpan.FromSeconds(5)))
            {
                Debug.WriteLine($"[AvifProbe] {backend}: timeout after 5s");
                return false;
            }

            bool valid = File.Exists(tmpPath) && new FileInfo(tmpPath).Length > 100;
            try { File.Delete(tmpPath); } catch { }
            Debug.WriteLine($"[AvifProbe] {backend}: {(valid ? "OK" : "FAIL (file too small or missing)")}");
            return valid;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AvifProbe] {backend}: exception — {ex.Message}");
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
