// TrueToneCap.Core/Services/OcrEngineType.cs
// OCR 引擎类型枚举 — 手动选择，无自动降级

namespace TrueToneCap.Core.Services;

/// <summary>OCR 引擎类型。</summary>
public enum OcrEngineType
{
    /// <summary>ONNX PP-OCRv6 DirectML (GPU, FP16)。</summary>
    OnnxGpu,
    /// <summary>ONNX PP-OCRv6 CPU (FP16 模型, FP32 计算)。</summary>
    OnnxCpu,
    /// <summary>Windows 系统 OCR (需安装语言包)。</summary>
    WindowsOcr,
}