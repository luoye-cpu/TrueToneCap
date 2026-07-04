// TrueToneCap.Core/Encoding/ImageEncoder.cs
// 多格式图像编码器抽象基类

namespace TrueToneCap.Core.Encoding;

public sealed class EncodingSettings
{
    public OutputFormat Format { get; set; } = OutputFormat.PNG;
    public float Quality { get; set; } = 90f;
    public bool HdrOutput { get; set; }
    public byte[]? IccProfile { get; set; }
    public ImageMetadata? Metadata { get; set; }
    public Processing.ToneMappingParams ToneMappingParams { get; set; } = new();
    public bool PreferGpuEncode { get; set; } = true;
    /// <summary>AVIF 编码后端偏好。</summary>
    public AvifEncoderBackend AvifBackend { get; set; } = AvifEncoderBackend.Auto;
    /// <summary>是否为 AVIF 文件添加 .png 后缀（兼容不支持 .avif 的软件）。</summary>
    public bool AvifPngSuffix { get; set; }
}

public enum OutputFormat { PNG, JPEG_LI, JPEG_XL, AVIF, WebP, BMP, JPEG_GAINMAP }

/// <summary>AVIF 编码后端。</summary>
public enum AvifEncoderBackend { Auto, LibAom, Qsv, Nvenc }

public sealed class ImageMetadata
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public int ScreenX, ScreenY, ScreenWidth, ScreenHeight;
    public string? ForegroundWindowTitle, ForegroundProcessName, DisplayName, ColorSpace, CursorType;
    public bool IsHdr;
    public int CursorX, CursorY;
}

public sealed class HdrFrameData
{
    public float[] Pixels { get; init; } = [];
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[]? IccProfile { get; init; }
    public ImageMetadata? Metadata { get; init; }
}

public abstract class ImageEncoder
{
    public abstract OutputFormat Format { get; }
    public abstract bool SupportsHdr { get; }
    public abstract Task EncodeAsync(HdrFrameData frame, EncodingSettings settings,
        string outputPath, CancellationToken ct = default);
    public abstract Task EncodeSdrAsync(byte[] sdrPixels, int width, int height,
        EncodingSettings settings, string outputPath, CancellationToken ct = default);
    public abstract (float Min, float Max, float Default, string Label) GetQualityRange();
    public virtual string GetQualityDescription(float quality) => quality.ToString("F0");
}

/// <summary>AVIF 编码器抽象接口。</summary>
public interface IAvifEncoder
{
    AvifEncoderBackend Backend { get; }
    bool IsAvailable { get; }
    Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct);
}
