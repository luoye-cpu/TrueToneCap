// TrueToneCap.Core/Encoding/ImageEncoder.cs
// 多格式图像编码器抽象基类

using Vortice.Direct3D11;
using Vortice.DXGI;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.Core.Encoding;

public sealed class EncodingSettings
{
    public OutputFormat Format { get; set; } = OutputFormat.PNG;
    public float Quality { get; set; } = 90f;
    public bool HdrOutput { get; set; }
    public byte[]? IccProfile { get; set; }
    public ImageMetadata? Metadata { get; set; }
    public Processing.ToneMappingParams ToneMappingParams { get; set; } = new(ToneMapMode.Hable);
    public bool PreferGpuEncode { get; set; } = true;
    /// <summary>AVIF 编码后端偏好。</summary>
    public AvifEncoderBackend AvifBackend { get; set; } = AvifEncoderBackend.Auto;
    /// <summary>是否为 AVIF 文件添加 .png 后缀（兼容不支持 .avif 的软件）。</summary>
    public bool AvifPngSuffix { get; set; }
    /// <summary>AVIF 色度采样: 420 / 422 / 444。默认 444。</summary>
    public string AvifChroma { get; set; } = "444";
    /// <summary>色度采样: 420 / 422 / 444。适用于 AVIF/WebP/JPEG XL。默认 444。</summary>
    public string ChromaSubsampling { get; set; } = "444";
    /// <summary>输出位深: 8 / 10 / 12。适用于 PNG/AVIF/JPEG XL/WebP。默认 8。</summary>
    public int OutputBitDepth { get; set; } = 8;
    /// <summary>显示器位深 (8/10 bit)，用于匹配输出精度。</summary>
    public int DisplayBitDepth { get; set; } = 8;
    /// <summary>色彩空间标签（用于 CICP 元数据嵌入）。默认 "System"。</summary>
    public string ColorSpaceTag { get; set; } = "System";
    /// <summary>JPEG Gain Map 增益图模式: Rgb 彩色增益 / Gray 灰度增益。</summary>
    public GainMapMode GainMapMode { get; set; } = GainMapMode.Gray;

    /// <summary>GPU 纹理（D3D11 纹理引用），用于 GPU 直通编码路径。
    /// 支持 GPU 直通的编码器（目前 NVENC AVIF）使用此纹理直接编码，
    /// 跳过 GPU→CPU 回读再上传的往返开销。</summary>
    public ID3D11Texture2D? GpuTexture { get; set; }
}

public enum OutputFormat { PNG, JPEG_LI, JPEG_XL, AVIF, WebP, TIFF, JPEG_GAINMAP }

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

    /// <summary>GPU 纹理（D3D11 纹理引用），用于 GPU 直通编码路径。
    /// 当此字段不为 null 时，支持 GPU 直通的编码器应优先使用纹理而非 CPU 像素数组。</summary>
    public ID3D11Texture2D? GpuTexture { get; init; }
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
    Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null);
}
