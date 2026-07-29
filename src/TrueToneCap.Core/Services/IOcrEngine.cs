// TrueToneCap.Core/Services/IOcrEngine.cs
// OCR 引擎抽象接口 — 手动选择，无自动降级

namespace TrueToneCap.Core.Services;

/// <summary>OCR 引擎模式。</summary>
public enum OcrEngineMode { Cpu, Gpu }

/// <summary>OCR 引擎元信息。</summary>
public record OcrEngineInfo(
    string Name,
    OcrEngineMode Mode,
    bool IsAvailable,
    OcrEngineType EngineType,
    string? Version = null);

/// <summary>OCR 引擎抽象接口。</summary>
public interface IOcrEngine
{
    OcrEngineInfo Info { get; }
    Task<OcrResult> RecognizeAsync(byte[] bgra, int w, int h, string? lang = null, CancellationToken ct = default);
}
