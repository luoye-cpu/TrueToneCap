// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/ImageEncoder.cs
// 多格式图像编码器抽象基类

using Vortice.Direct3D11;
using Vortice.DXGI;
using TrueToneCap.Core.Processing;

namespace TrueToneCap.Core.Encoding;

public sealed class EncodingSettings
{
    // ⚠️ 下面这两个默认值只是「类型兜底」，【不代表产品默认值】：
    //   · Format: 产品默认是 JPEG LI（AppSettingsData.FormatIndex = 2），此处为 PNG；
    //   · Quality: 90 仅对 WebP(50-100) 之类的"百分比式"区间有意义，
    //     对 JPEG LI(0.5-3.0 butteraugli) / AVIF(0-63 CRF) / JPEG XL(0.1-4.0) 都是【越界值】。
    // 真正的产品默认值在 AppSettingsData 的 Quality* 字段，与各编码器的
    // GetQualityRange() 逐一对齐。构造本类时【必须】显式指定 Format 与 Quality，
    // 不要依赖这里的初始值 —— 否则有损格式会拿到一个荒谬的质量参数。
    public OutputFormat Format { get; set; } = OutputFormat.PNG;
    public float Quality { get; set; } = 90f;
    public bool HdrOutput { get; set; }
    public byte[]? IccProfile { get; set; }
    public ImageMetadata? Metadata { get; set; }
    public Processing.ToneMappingParams ToneMappingParams { get; set; } = new(ToneMapMode.SegmentedReinhard);
    // ═══ 2026-09-19 清理：删除 PreferGpuEncode / AvifChroma / DisplayBitDepth 三个属性 ═══
    // 三者都是"每次编码都被赋值、Core 侧却零读取"的只写通道：色度的事实源是
    // ChromaSubsampling，位深的事实源是 OutputBitDepth，GPU 直通与否由 GpuTexture 是否非空决定。
    // 留着会让人以为"还有一条 GPU 开关 / 一条 AVIF 专用色度 / 一条按显示位深匹配精度的通道"，
    // 与上面 AvifBackend 的删除理由同类（交接 §26.4 P2）。
    // ═══ 2026-09-17 清理：原 AvifBackend 属性已删除 ═══
    // 它是重构遗留的僵尸字段：CapturePipelineService 恒写 Auto，而 Core 侧
    // （AvifEncoder.EncodeSdrAsync）自 P1-3 修复起只读 AvifEncoderKind，从未读过它。
    // 留着只会让新代码误以为"还有一条后端选择通道"。后端枚举 AvifEncoderBackend 保留
    // （AvifEncoderSelector.Select(backend) 与 IAvifEncoder.Backend 仍在使用）。
    /// <summary>AVIF 编码器种类（单一「AVIF 编码器」下拉，2026-09-16 重构）。</summary>
    public AvifEncoderKind AvifEncoderKind { get; set; } = AvifEncoderKind.Auto;
    /// <summary>是否为 AVIF 文件添加 .png 后缀（兼容不支持 .avif 的软件）。</summary>
    public bool AvifPngSuffix { get; set; }
    /// <summary>是否为 JXL 文件添加 .png 后缀（兼容不支持 .jxl 的软件）。</summary>
    public bool JxlPngSuffix { get; set; }
    /// <summary>色度采样: 420 / 422 / 444。适用于 AVIF/WebP/JPEG XL。默认 444。</summary>
    public string ChromaSubsampling { get; set; } = "444";
    /// <summary>
    /// 输出位深。默认 8。
    /// <para>⚠️ 各格式【实际生效的位深集合不同】，传值前请确认，否则会被静默忽略或产出非法文件：
    /// PNG 8/16 容器（10/12 一并落到 16-bit 容器，并写 sBIT=8 标注真实精度）、
    /// TIFF 8/16（其余值由编码器钳到 16）、
    /// AVIF 由 <see cref="AvifEncoderCapabilities"/> 按编码器再协商、
    /// JPEG XL 8/16、JPEG LI / GainMap / WebP 恒 8（编码器无位深概念）。</para>
    /// </summary>
    public int OutputBitDepth { get; set; } = 8;
    /// <summary>色彩空间标签（用于 CICP 元数据嵌入）。默认 "System"。</summary>
    public string ColorSpaceTag { get; set; } = "System";
    /// <summary>JPEG Gain Map 增益图模式: Rgb 彩色增益 / Gray 灰度增益。</summary>
    public GainMapMode GainMapMode { get; set; } = GainMapMode.Gray;

    /// <summary>目标色域是用户<b>显式</b>选的，而不是"跟随显示器"自动解析出来的。
    /// <para>只有 JPEG GainMap 读它：Ultra HDR 的 base layer 默认走 sRGB/BT.709，
    /// 而用户手动选了 BT.2020 时他要的是"整张增益图都在 2020 里"（2026-09-22 口径）。
    /// 自动解析出 BT2020（一块 P3 屏）不算显式 ⇒ 仍走默认 sRGB base。
    /// 事实源是 <c>ColorDecision.ExplicitWideTarget</c>，编码器不许自己猜。</para></summary>
    public bool ExplicitWideTarget { get; set; }

    /// <summary>GPU 纹理（D3D11 纹理引用），用于 GPU 直通编码路径。
    /// 支持 GPU 直通的编码器（目前 NVENC AVIF）使用此纹理直接编码，
    /// 跳过 GPU→CPU 回读再上传的往返开销。</summary>
    public ID3D11Texture2D? GpuTexture { get; set; }
}

public enum OutputFormat { PNG, JPEG_LI, JPEG_XL, AVIF, WebP, TIFF, JPEG_GAINMAP }

/// <summary>AVIF 编码后端。</summary>
public enum AvifEncoderBackend { Auto, LibAom, Qsv, Nvenc, Mft }

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

    /// <summary>
    /// 该后端能否接受**真 10-bit 输入**（A2R10G10B10）并保留源精度。
    /// <para>与"能否产出 10-bit 容器"是两件事：8-bit 输入 + 10-bit 输出也能产出 10-bit 文件，
    /// 但源精度仍只有 8-bit（实测解码亮度是 4 级台阶）。本属性回答的是"源精度能不能保住"。</para>
    /// <para>默认 false ⇒ 未覆写的后端走 <see cref="EncodeArgb10Async"/> 的默认实现
    /// （降到 8-bit 再编码），不会假装保留了精度。</para>
    /// </summary>
    bool SupportsTrue10BitInput => false;

    /// <param name="displayBitDepth">
    /// ⚠ 名字是历史遗留 —— 它承载的是**协商后的输出位深**，不是显示器位深。
    /// <c>AvifEncoder.EncodeSdrAsync</c> 传的是 <c>settings.OutputBitDepth</c>，
    /// 软件后端据此拼 avifenc 的 <c>--depth</c>；NVENC 后端据此设置
    /// <c>NV_ENC_CONFIG_AV1.outputBitDepth</c>（输入恒为 8-bit ARGB，硬件内部做 8→10 转换，
    /// 2026-09-17 实测确认可产真 10-bit 码流）。
    /// 名字与语义不符，但改动涉及 4 个后端与全部调用方，故先以注释固化真实语义。
    /// </param>
    /// <param name="texture">GPU 纹理（可选）。纹理是 BGRA8 打包格式，但 10-bit 请求不受影响：
    /// 位深由 outputBitDepth 控制而非输入格式，故纹理路径同样可产出 10-bit。</param>
    Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null);

    /// <summary>
    /// 以 **真 10-bit 输入**（A2R10G10B10 打包，每像素 4 字节）编码。
    /// <para>与 <see cref="EncodeAsync"/> 的差别只在**源精度**：后者输入恒为 8-bit，
    /// 10-bit 输出是硬件升位深（源精度仍 8-bit）；本方法把 10-bit 像素原样送入，
    /// 精度真正保留。2026-09-17 阶梯判据实测：ARGB10 输入产出真 10-bit 斜坡，
    /// 8-bit 输入只能产出 4 级台阶。</para>
    /// <para>默认实现：把 10-bit 降到 8-bit 后转交 <see cref="EncodeAsync"/> ——
    /// 不支持真 10-bit 输入的后端因此无需改动，且不会假装自己保留了精度
    /// （它们产出的仍是正确的 10-bit 容器，只是源精度被降到 8-bit，与现状一致）。</para>
    /// </summary>
    Task EncodeArgb10Async(byte[] argb10, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 10, string? colorSpaceTag = null, byte[]? iccProfile = null)
        => EncodeAsync(AvifPixelFormat.Argb10ToBgra8(argb10), w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
}

/// <summary>AVIF 后端共用的像素格式辅助。
/// <para>⚠ 不要命名为 <c>AvifFallbackHelper</c>：FormatEncoders.cs 里已有一个
/// file-scoped 的同名类型，重名会造成编译冲突。</para></summary>
internal static class AvifPixelFormat
{
    /// <summary>A2R10G10B10 → BGRA8（丢弃低 2 位）。供不支持真 10-bit 输入的后端兜底。</summary>
    /// <remarks>布局：每像素 4 字节小端，B 占最低 10 位、G 次之、R 再次、A 占最高 2 位。</remarks>
    public static byte[] Argb10ToBgra8(byte[] argb10)
    {
        var bgra = new byte[argb10.Length];
        for (int i = 0; i < argb10.Length; i += 4)
        {
            uint word = (uint)(argb10[i] | (argb10[i + 1] << 8) | (argb10[i + 2] << 16) | (argb10[i + 3] << 24));
            bgra[i + 0] = (byte)((word & 0x3FF) >> 2);
            bgra[i + 1] = (byte)(((word >> 10) & 0x3FF) >> 2);
            bgra[i + 2] = (byte)(((word >> 20) & 0x3FF) >> 2);
            bgra[i + 3] = 255;
        }
        return bgra;
    }
}
