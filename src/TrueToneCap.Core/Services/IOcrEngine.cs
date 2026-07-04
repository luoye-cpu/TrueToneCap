// TrueToneCap.Core/Services/IOcrEngine.cs
// OCR 引擎抽象接口 — 支持多引擎可插拔

namespace TrueToneCap.Core.Services;

/// <summary>OCR 引擎模式。</summary>
public enum OcrEngineMode { Cpu, Gpu }

/// <summary>OCR 引擎元信息。</summary>
public record OcrEngineInfo(string Name, OcrEngineMode Mode, bool IsAvailable, string? Version = null);

/// <summary>OCR 引擎抽象接口。</summary>
public interface IOcrEngine
{
    OcrEngineInfo Info { get; }
    Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h, string? lang = null, CancellationToken ct = default);
}

/// <summary>OCR 结果（扩展：含置信度与引擎信息）。</summary>
public sealed class OcrResultEx : OcrResult
{
    public string? EngineName { get; set; }
    public float AverageConfidence { get; set; }
}
