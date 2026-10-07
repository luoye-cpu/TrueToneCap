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
    //   · Format: 产品默认是 PNG（`AppSettingsData.FormatIndex` 缺键 ⇒ `FormatOrder.DefaultIndex`
    //     = 表里 PNG 那一格，即 0），此处也写 PNG；
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
    /// <summary>色彩空间标签（用于 CICP 元数据嵌入）。默认 "System"。
    /// <para><b>这里故意不做任何映射</b>（2026-09-27 试过后退回）：把 "AdobeRGB ⇒ BT2020" 塞进本属性的
    /// setter 确实能一处收口所有容器，但它同时吃掉了 <c>HdrDeclarableTag</c> 的【留痕】设计 ——
    /// 那条分支存在的意义是"用户显式选的色域没生效，必须让他看见"（判据 "AdobeRGB: 必须留痕" 就是它）。
    /// 属性静默改写 ⇒ 编码器再也看不到"用户原本要的是 Adobe"，代价从可见变成不可见。
    /// 收口因此放在<strong>调用方</strong>：界面侧 <c>CollapseSelectableTag</c>、
    /// 编码侧与样张宿主共用 <c>ColorProfileProvider.MapAdobeTargetToWide</c>（JXL 的 SDR 分支、
    /// HDR 的 <c>HdrDeclarableTag</c>、<c>FormatBench</c> 的两处）。</para></summary>
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

    /// <summary>压缩强度（三档）。只影响 AVIF / JPEG XL / WebP 的 effort/speed 旋钮，
    /// <b>不动质量参数</b> —— 两者是正交的两根轴（质量决定"丢多少信息"，强度决定
    /// "花多少时间去找更好的编码方式"）。JPEG LI / PNG 不读它（它们没有独立旋钮，见
    /// <see cref="CompressionEffort"/> 的文件头说明）。
    /// <para>默认 <see cref="CompressionEffort.Balanced"/>：这是 2026-10-07 用户口径
    /// （"一档中间"为默认），并且对 AVIF/WebP 而言**恰好等于改动前的硬编码值**
    /// （avifenc `-s 6`、cwebp 默认 `-m 4`）⇒ 这两个格式的产物不变；
    /// 只有 JPEG XL 从 `-e 9`（实测 25 s）变成 `-e 5`（实测 0.82 s）—— 那正是用户要的"顺带提速"。</para></summary>
    public CompressionEffort Effort { get; set; } = CompressionEffort.Balanced;

    /// <summary>
    /// 【换格式保底】的总闸：本次编码后端失败时，允不允许把这张图改存成 PNG（而不是什么都不落）。
    /// <para>产品默认是【开】（截图工具的失败模式应该是"无损但格式不对"，不是"按下热键什么都没发生"），
    /// 但那个"开"是由 <c>CapturePipelineService.BuildEncodingSettings</c> 从设置快照带进来的 ——
    /// <b>本属性的初始值刻意是 <c>false</c></b>：任何直接 <c>new EncodingSettings</c> 的调用方
    /// （<c>--param-sweep</c> / <c>--selftest</c> / <c>--container-audit</c> / 各外部裁判脚本 / 单测夹具）
    /// 要的是"我让它编什么格式、失败就必须是失败"。保底一旦在测量路径上生效，就会把"编码器不行"
    /// 洗成"产物存在"，而这类判据的读法普遍是"有没有那个文件"。
    /// 实测证据（2026-09-29）：把初始值定成 <c>true</c> 之后，<c>param_sweep_check.py</c> 的
    /// <c>c_avifsvt_444</c> 那一格从"按预期失败（SVT 仅 4:2:0，未静默降级）"立刻翻成
    /// "竟然成功了 —— SVT 能力表需要重测"，而真因只是保底在扫参目录里多写了一枚
    /// <c>c_avifsvt_444.png</c>（裁判按名字前缀找文件，命中了它）。</para>
    /// <para>关掉之后失败照原样抛出去（产品界面走 <c>ShowCaptureFailed</c> 那条），产物**仍然是用户选的
    /// 格式**或者根本没有产物；不会再看一眼后缀却发现"我要 JPEG XL、盘上躺着 PNG"。</para>
    /// <para>它只管【换格式】这一件事。格式内部的后端阶梯（AVIF 硬件 → libaom、HDR → 同格式 SDR 色调映射、
    /// 位深/色度协商）不归它管，那些是同一个容器内的参数降级，另有各自的留痕通道
    /// （<see cref="EncoderFallbackNotifier"/>）。</para>
    /// <para>用户侧的事实源是 <c>AppSettingsData.PngFallbackEnabled</c>（默认 true，设置页那个勾）；
    /// 把它写进本属性的只有管线那一处，见 CorePipelineTests 的"开关写者只许一处"。</para>
    /// </summary>
    public bool PngFallbackEnabled { get; set; }
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

/// <summary>
/// 质量轴是哪一种刻度。<b>刻意是枚举而不是字符串</b>。
/// <para>过去这里返回的是中文成品串（<c>"质量 (50-100; 85+ 视觉无损)"</c> 之类），
/// 而本地化表（<c>LocaleManager.ZhToEn</c>）只存在于 App 那一层 ⇒ App 只能在显示点
/// 往回翻一个【下层生成的用户可见文案】；<c>$"距离: {q:F1}"</c> 那种带数值的整串
/// 更是永远精确匹配不上，只能按片段换。Core 只说"是哪一种刻度"，
/// 措辞与数值格式化归 App（<c>MainWindow.QualityLabels</c>）。
/// 反向哨兵：<c>Test_QualityTexts_CoreStringsAreAllCovered</c> 会扫这两个方法的函数体，
/// 再出现中文字面量就红。</para>
/// </summary>
public enum QualityScale
{
    LosslessFixed100,
    LosslessFixed,
    QualityPercent,
    Crf,
    ButteraugliRange,
    ButteraugliDistanceRange,
}

/// <summary>当前质量值怎么读：<b>类型 + 数值</b>，同样不含任何用户可见措辞。</summary>
public enum QualityDescKind
{
    Lossless,
    NearLossless,
    Distance,
    DistanceWithNote,
    Crf,
    Percent,
    PlainNumber,
}

/// <summary>一次质量读数的中性结果。<see cref="QualityDescKind"/> 说明怎么读，
/// <see cref="Value"/> 原样带过去，格式化与措辞由 App 决定（这样中文模式可以逐字复刻旧输出）。</summary>
public readonly record struct QualityDesc(QualityDescKind Kind, float Value);

public abstract class ImageEncoder
{
    public abstract OutputFormat Format { get; }
    public abstract bool SupportsHdr { get; }
    public abstract Task EncodeAsync(HdrFrameData frame, EncodingSettings settings,
        string outputPath, CancellationToken ct = default);
    public abstract Task EncodeSdrAsync(byte[] sdrPixels, int width, int height,
        EncodingSettings settings, string outputPath, CancellationToken ct = default);
    public abstract (float Min, float Max, float Default, QualityScale Scale) GetQualityRange();

    /// <summary>该格式<b>有没有可调的质量参数</b>（区间退化成一个点 ⇒ 没有）。</summary>
    /// <remarks>
    /// PNG / TIFF 是无损格式，<see cref="GetQualityRange"/> 恒返回 <c>(100,100,100)</c>，
    /// 所以界面那根滑块划不动、那个输入框只能填 100 —— 不是坏了，是【本来就没有这个参数】。
    /// 判据写成"区间退化"而不是"点名格式"：App 侧旧写法是
    /// <c>QualitySld.IsEnabled = format != OutputFormat.PNG</c>，只挡住了 PNG，
    /// 同样是 (100,100) 的 TIFF 仍渲染出一根 enabled 却划不动的滑块 —— 同一条事实被两处
    /// 各写一遍、其中一处漏了一个格式。UI 与测试现在都问这一个属性。
    /// </remarks>
    public bool QualityIsFixed => GetQualityRange() is (var min, var max, _, _) && max <= min;

    public virtual QualityDesc GetQualityDescription(float quality) => new(QualityDescKind.PlainNumber, quality);
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
    /// <param name="effort">压缩强度三档（2026-10-07 新增）。只映射到各后端的 effort/speed 旋钮，
    /// <b>不改变质量参数</b>。默认 <see cref="CompressionEffort.Balanced"/> 与改动前行为一致
    /// （avifenc 软件路仍是 <c>-s 6</c>、NVENC 路仍是 P1 档位附近的均衡值）。
    /// 加在接口上而不是塞进 <c>EncodingSettings</c> 传参：四个后端（libaom/qsv/nvenc/mft）
    /// 都要各自映射到不同刻度，集中在一个映射表里（<see cref="CompressionEffortMap"/>）。</param>
    Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null, CompressionEffort effort = CompressionEffort.Balanced);

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
