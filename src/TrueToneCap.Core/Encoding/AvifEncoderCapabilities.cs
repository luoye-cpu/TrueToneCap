// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/AvifEncoderCapabilities.cs
// AVIF 编码器能力表 —— 集中声明各编码器"能做什么"。
//
// 为什么需要它：
//   重构前，各编码器的能力边界**散落在注释与实现细节里**（NVENC 走 NV12/ARGB 输入、MFT 转 NV12、
//   avifenc 的 --yuv 参数、HDR 路径硬编码 --depth 10），没有任何一处能回答"这个编码器支持 444 吗？支持 10-bit 吗？"。
//   结果是：用户选了硬件加速 + 444，代码默默忽略请求，出来的东西与预期不符且无提示。
//
//   现在统一到这张表：UI 依据它决定哪些选项可用、选择器依据它协商实际使用的色度/位深、
//   Auto 模式依据它挑选"既可用、又满足当前需求"的编码器（硬件优先）。

namespace TrueToneCap.Core.Encoding;

/// <summary>AVIF 编码器种类（对应设置里的单一"AVIF 编码器"下拉）。</summary>
public enum AvifEncoderKind
{
    /// <summary>自动：优先硬件，其次软件；并兼顾色度/位深需求。</summary>
    Auto,
    /// <summary>avifenc 软件路径（其内部编码器交给 avifenc 自己挑，通常即 libaom）。</summary>
    Avifenc,
    /// <summary>Intel QSV / Windows MFT 硬件路径（经 NV12，仅 4:2:0）。</summary>
    Qsv,
    /// <summary>libaom：参考实现，兼容性最好，支持全色度。</summary>
    LibAom,
    /// <summary>SVT-AV1：面向多核优化，同画质通常快于 libaom（需 avifenc 编译时启用）。</summary>
    SvtAv1,
    /// <summary>NVIDIA NVENC 硬件路径。</summary>
    Nvenc,
}

/// <summary>一个 AVIF 编码器的能力。</summary>
/// <param name="Kind">编码器种类。</param>
/// <param name="DisplayName">UI 显示名。</param>
/// <param name="IsHardware">是否硬件加速。</param>
/// <param name="Supports420">是否支持 4:2:0。</param>
/// <param name="Supports422">是否支持 4:2:2。</param>
/// <param name="Supports444">是否支持 4:4:4。</param>
/// <param name="MaxBitDepth">该编码器可达的位深上限。SDR 路径再取 min(MaxBitDepth, 12)，
/// HDR 路径恒 10（avifenc <c>--depth 10</c> 硬编码）。见
/// <see cref="AvifEncoderCapabilities.SdrBitDepthFor"/> 与
/// <see cref="AvifEncoderCapabilities.HdrBitDepth"/>。</param>
public sealed record AvifEncoderCaps(
    AvifEncoderKind Kind,
    string DisplayName,
    bool IsHardware,
    bool Supports420,
    bool Supports422,
    bool Supports444,
    int MaxBitDepth);

public static class AvifEncoderCapabilities
{
    public static readonly IReadOnlyDictionary<AvifEncoderKind, AvifEncoderCaps> Table =
        new Dictionary<AvifEncoderKind, AvifEncoderCaps>
        {
            // 软件：色度与位深最灵活，代价是速度
            // 注：MaxBitDepth 是"该编码器可达的位深上限"，两条路径的实际取值不同：
            //   · SDR：min(MaxBitDepth, 12)。软件靠 avifenc 的 --depth 对 PNG 输入升采样；
            //     NVENC 靠 outputBitDepth（2026-09-17 接通，8-bit 输入由硬件升到 10-bit）；
            //     QSV/MFT 仍受 NV12 输入限制 → 8。
            //   · HDR：恒 10（avifenc --depth 10 硬编码），与编码器种类无关。
            // 分场景协商见 SdrBitDepthFor / HdrBitDepth。
            [AvifEncoderKind.LibAom] = new(
                AvifEncoderKind.LibAom, "libaom（软件 · 兼容优先）", IsHardware: false,
                Supports420: true, Supports422: true, Supports444: true, MaxBitDepth: 12),

            // ═══ SVT-AV1：本机【实测】能力（2026-09-19，内嵌 avifenc 换成自编译的 aom+svt 构建）═══
            // 判据（两条互相独立，都要求"读回 == 请求"）：
            //   ① 本仓库 AvifEncoderProbe：真编码 → 解析产物 av1C；
            //   ② scripts/precision/svt_caps_matrix.py：色度 × 位深九宫格 → 外部 ffprobe 读回 pix_fmt。
            // 实测矩阵（codec=svt，输入为管线真实使用的 8-bit / 16-bit PNG）：
            //            8-bit        10-bit       12-bit
            //   4:2:0    yuv420p ✅   yuv420p10le ✅   ❌
            //   4:2:2    ❌           ❌                ❌     全部 "Failed to encode image:
            //   4:4:4    ❌           ❌                ❌     Encoding of color planes failed"
            //   同一输入的 codec=aom 九宫格全绿 ⇒ 不是输入或容器的问题，是 SVT 这条路本身只到 4:2:0/10-bit。
            // ⇒ 声明必须是 420-only / ≤10-bit：若继续声明 422/444，协商会把广色度请求派给 SvtAv1，
            //   而 `--codec svt` 现在【真的】会下发（此前内嵌二进制没有 svt，GetCodecArg 静默回落 aom，
            //   所以声明错了也看不出来）。失败的编码会走 AvifFallbackHelper 回退 libaom，但那只是兜底，
            //   不是"选项可用"。
            // 与官方来源的口径差异（记录以免有人又把它改回全色度）：
            //   · libavif src/codec_svt.c 确实为 444/422 与 >10-bit 写了映射（444→HIGH_PROFILE，
            //     422 或 depth>10→PROFESSIONAL_PROFILE），SVT-AV1 User Guide 只声明独立 app 的 y4m 输入；
            //   · 本构建以 AVIF_LIBYUV=OFF 编译，libavif 交给 SVT 的输入格式因此受限。
            //   哪个是主因未定论 —— 但"本机产出什么"以实测为准（交接 §3 纪律：以独立实现读回的事实为准）。
            [AvifEncoderKind.SvtAv1] = new(
                AvifEncoderKind.SvtAv1, "SVT-AV1（软件 · 速度优先）", IsHardware: false,
                Supports420: true, Supports422: false, Supports444: false, MaxBitDepth: 10),

            // avifenc 通用软件入口：不传 --codec，由 avifenc 自选（本机解析到 aom）
            // ⇒ 实测与 LibAom 同档：420/422/444 全色度、≤12-bit（2026-09-19 实测确认）
            [AvifEncoderKind.Avifenc] = new(
                AvifEncoderKind.Avifenc, "avifenc（软件 · 由 avifenc 自选编码器）", IsHardware: false,
                Supports420: true, Supports422: true, Supports444: true, MaxBitDepth: 12),

            // 硬件：快，但受输入格式限制
            // NVENC：AV1 硬件编码器仅支持 4:2:0（NVENC Application Note 官方能力表：
            // 4:2:2 编码仅 H.264/HEVC 在 Blackwell 支持，AV1 任何代际都无 422 管线；
            // 实测 RTX 5080 EncodePicture 返回 0x0C UNSUPPORTED_PARAM）。4:2:2 请求
            // 由协商直接落到 libaom（软件真 422），不再白走 NVENC 失败回退。
            // ═══ 位深：10-bit 已于 2026-09-17 接通 ═══
            // 输出位深由 NV_ENC_CONFIG_AV1.outputBitDepth（NV_ENC_CONFIG + 280，
            // 由 MSVC offsetof 实测）决定，与输入 bufferFormat 无关。
            // nvEncodeAPI.h 原文："HW will do the bitdepth conversion internally from
            // inputBitDepth -> outputBitDepth if bit depths differ. Support for 8 bit
            // input to 10 bit encode conversion only"
            // ⇒ 输入仍是 8-bit ARGB，硬件内部升到 10-bit，不必改用 ARGB10/P010 输入。
            // 🔬 实测（RTX 5080 / 驱动 616.56，Test_Nvenc_Av1TenBit）：
            //   8-bit 输入 + outputBitDepth=10 → AV1 序列头 bitDepth=10，
            //   经 ffprobe 独立确认 pix_fmt=yuv420p10le。
            // 🔬 实测（RTX 5080 / SDK 13.1，交接文档 §3 阶梯判据）：
            //   · 8-bit 输入 + outputBitDepth=10 → 序列头确实写 10-bit，但解码亮度是
            //     **完美的 4 级台阶** ⇒ 只是容器级升位深，源精度仍 8-bit。
            //   · ARGB10 输入 + inputBitDepth = outputBitDepth = 10 → 解码出**真 10-bit 斜坡**
            //     （64 带源还原 60 级、同值平均连续带 1.07）⇒ 硬件吃得了真 10-bit 输入。
            // ⚠ 勘误：本文件曾据"只换 bufferFormat 为 ARGB10 → 序列头仍 8-bit"判定
            //   "ARGB10 无效"。该推论**错误**（当时 outputBitDepth 还是 preset 默认的 8，
            //   inputBitDepth / outputBitDepth 必须配套），已由交接文档 §3 推翻。
            //   本仓库任何位置再出现"ARGB10 无效"一律以 §3 为准。
            // 真 10-bit 输入通路见 NvencAvifBackend.EncodeArgb10Async（SupportsTrue10BitInput）。
            [AvifEncoderKind.Nvenc] = new(
                AvifEncoderKind.Nvenc, "NVIDIA NVENC（硬件加速）", IsHardware: true,
                Supports420: true, Supports422: false, Supports444: false, MaxBitDepth: 10),

            // QSV / MFT：输入被转成 NV12（8-bit 4:2:0）。改用 P010 即具备 10-bit 能力，
            // 同样属于"实现未接线"而非硬件限制。
            // ═══ 2026-09-19：显示名必须写清"质量滑块在这条路上不起作用"═══
            // 依据（MftEncoderNative.ConfigureMft）：输出媒体类型恒写
            // MF_MT_AVG_BITRATE = 10_000_000，且从不设置码率控制模式 / 质量类属性，
            // 而调用方传下来的 crf 只用于回退 libaom 那一次（MftAvifEncode 里 crf 不进 MFT）。
            // ⇒ 能力表/UI 若不声明，用户拖质量滑块以为在调硬件编码，实际产物与滑块无关 ——
            //   正是本仓库反复出现的"参数写了没人读"。先由显示名如实声明（行为未变），
            //   真要接通见 MftEncoderNative 那条"何时该按 crf 推导码率"的注释。
            [AvifEncoderKind.Qsv] = new(
                AvifEncoderKind.Qsv, "Intel QSV / 系统 MFT（硬件加速 · 固定 10 Mbps 码率，质量滑块不适用）", IsHardware: true,
                Supports420: true, Supports422: false, Supports444: false, MaxBitDepth: 8),
        };

    public static AvifEncoderCaps Caps(AvifEncoderKind kind) =>
        Table.TryGetValue(kind, out var c) ? c : Table[AvifEncoderKind.LibAom];

    /// <summary>把设置字符串解析为编码器种类；未知/空值回落 Auto（与 SettingsService 白名单一致）。</summary>
    public static AvifEncoderKind Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "avifenc" => AvifEncoderKind.Avifenc,
        "qsv" => AvifEncoderKind.Qsv,
        "libaom" => AvifEncoderKind.LibAom,
        "svtav1" => AvifEncoderKind.SvtAv1,
        "nvenc" => AvifEncoderKind.Nvenc,
        _ => AvifEncoderKind.Auto,
    };

    /// <summary>
    /// 把任意色度字符串收敛到 420/422/444；未知值按 420 处理。
    /// <para>为什么必须收敛：若把 "400" 之类的非法值原样透传，它会被一路带到
    /// avifenc 的 <c>--yuv</c> 参数上（原生侧行为未定义）。收敛后，
    /// 协商的返回值【恒为合法值】，调用方不必再自行校验。</para>
    /// </summary>
    private static string NormalizeChroma(string? chroma) =>
        chroma is "420" or "422" or "444" ? chroma : "420";

    private static bool SupportsChroma(AvifEncoderCaps c, string chroma) => chroma switch
    {
        "420" => c.Supports420,
        "422" => c.Supports422,
        "444" => c.Supports444,
        _ => false,   // 未知色度一律视为"不支持"，交由协商降级到 420
    };

    /// <summary>把期望色度协商到该编码器实际支持的范围内（444 → 422 → 420 依次降级）。</summary>
    public static string NegotiateChroma(AvifEncoderKind kind, string want)
    {
        var c = Caps(kind);
        want = NormalizeChroma(want);
        if (SupportsChroma(c, want)) return want;
        foreach (var fallback in new[] { "422", "420" })
            if (SupportsChroma(c, fallback)) return fallback;
        return "420";
    }

    // ═══ 2026-09-17 修复：位深协商必须区分 SDR / HDR，不能按编码器种类一刀切 ═══
    // 原实现是 Math.Min(want, MaxBitDepth)，与两条真实路径都不符：
    //   · SDR：软件输入虽是 8-bit PNG，但 avifenc 的 --depth 支持对 PNG 输入升采样 ⇒ 可达 10 / 12；
    //     硬件 NVENC 自 2026-09-17 起也支持 10-bit（outputBitDepth，输入仍是 8-bit ARGB），
    //     QSV / MFT 仍受 NV12 输入限制 ⇒ 8。
    //     旧代码对软件也按"恒 8"处理，于是请求 10-bit 时协商前后相等、连降级提示都不触发，
    //     日志却打"位深=10" —— 真正的静默改参数。
    //   · HDR：位深由 avifenc 的 `--depth 10` 硬编码，与编码器种类、用户设置都无关 ⇒ 恒 10。
    // 故改为按场景协商：SDR 取 SdrBitDepthFor（NVENC 10 / QSV 8 / 软件 10·12），HDR 恒 10。
    //
    // ⚠ 历史坑：本段注释曾长期写成"SDR 恒 8-bit"，而实现早已放开软件的 10/12-bit
    // （NativeAvifEncoder.GetDepthArg + SDR 路径拼 --depth）。注释与实现相反比没注释更危险 ——
    // 后来者会按注释去"修正"正确代码。改位深相关实现时必须同步本段。

    /// <summary>SDR 路径位深的**全局上限** —— avifenc 的 <c>--depth</c> 只接受 8 / 10 / 12。</summary>
    public const int SdrMaxBitDepth = 12;

    /// <summary>HDR 路径的位深 —— 由 avifenc 的 <c>--depth 10</c> 硬编码（见 NativeAvifEncoder.EncodeFile）。</summary>
    public const int HdrBitDepth = 10;

    /// <summary>
    /// SDR 路径下该编码器**真实可达**的位深上限。
    /// <para>硬件 NVENC：10-bit 已接通（见能力表 Nvenc 条目）⇒ 取能力表的 10。
    /// QSV / MFT 仍走 NV12（8-bit）输入 ⇒ 能力表仍为 8，见下方注释。</para>
    /// <para>软件（avifenc / libaom / svt）：输入是 PNG，而 avifenc 的 <c>--depth</c>
    /// 支持对 PNG/JPEG 输入做位深转换（8-bit 源会被升采样）⇒ 取能力表与 12 的较小值
    ///（libaom 12、svt/avifenc 10）。</para>
    /// </summary>
    public static int SdrBitDepthFor(AvifEncoderKind kind)
        => Math.Min(Caps(kind).MaxBitDepth, SdrMaxBitDepth);

    /// <summary>
    /// 按场景把期望位深协商到**实际会产出**的值。
    /// <para>HDR：恒 <see cref="HdrBitDepth"/>（10），与编码器种类、请求值都无关；
    /// SDR：不超过 <see cref="SdrBitDepthFor"/>（软件 10/12，硬件 8）。</para>
    /// </summary>
    public static int NegotiateBitDepth(AvifEncoderKind kind, int want, bool hdr) =>
        hdr ? HdrBitDepth                        // HDR：恒 10，与编码器、设置都无关
            : Math.Min(want, SdrBitDepthFor(kind));

    /// <summary>
    /// Auto 模式挑编码器：按 <b>NVENC → QSV → libaom → SVT-AV1 → avifenc</b> 的顺序，
    /// 取第一个【可用且能把所请求的色度原样编出来】的；无人能满足时退回"第一个可用的"。
    /// <para>═══ 2026-09-19 产品决定（用户）：默认色度回到 4:2:0，Auto 从此按色度过滤 ═══
    /// 420 ⇒ 硬件（NVENC / QSV）；422、444 ⇒ libaom。也就是"选一个不必降级的编码器"，
    /// 不再"用最快的编码器然后告知降级"。</para>
    /// <para><b>为什么这与 2026-09-17 那条相反的决定不矛盾</b>：那次的失败模式是
    /// <b>默认 444</b> 时硬件被过滤掉、Auto 恒落到 libaom（4K 约 22s vs 硬件约 0.7s），
    /// "硬件优先"形同虚设。默认改回 420 之后默认路径照样命中硬件，被过滤掉的只剩
    /// "用户主动要 422/444"这一种情形 —— 而那种情形下硬件本来也给不出 422/444
    /// （NVENC 恒 4:2:0，见交接 §26.7；QSV 输入是 NV12）。届时慢是软件编码器的固有代价，
    /// 快与广色度不可兼得，交给用户在下拉里选，而不是 Auto 替他悄悄砍掉一半色度。</para>
    /// <para><b>位深不参与过滤</b>（与色度的差别是真实的）：位深的合法集合取决于场景
    /// （HDR 恒 10；SDR 软件 10/12、硬件 8/10），而本函数没有 hdr 上下文，
    /// <paramref name="wantBitDepth"/> 只是用户请求值 ⇒ 过滤会拿错口径，
    /// 仍由 <see cref="NegotiateBitDepth"/> 在场景确定后钳制。
    /// 色度则请求值即目标值，协商只是改像素 ⇒ 才值得在分派阶段就让开。</para>
    /// </summary>
    /// <param name="isAvailable">外部提供的可用性探测（通常是各后端的 IsAvailable）。</param>
    /// <param name="wantChroma">期望色度；用于过滤 —— 只保留 <see cref="NegotiateChroma"/> 不会改动它的那些种类。</param>
    /// <param name="wantBitDepth">保留形参以表达调用意图；不参与过滤（理由见 remarks）。</param>
    public static AvifEncoderKind PickAuto(
        Func<AvifEncoderKind, bool> isAvailable, string wantChroma, int wantBitDepth)
    {
        static IEnumerable<AvifEncoderKind> Order() =>
        [
            AvifEncoderKind.Nvenc,          // 硬件优先
            AvifEncoderKind.Qsv,
            // ═══ 软件兜底：libaom 在前、SVT-AV1 在后（2026-09-19 依实测调整）═══
            // 换上带 svt 的自编译 avifenc 之后，IsKindAvailable(SvtAv1) 第一次真的为 true，
            // 于是这条顺序会在【无硬件编码器】的机器上决定默认走谁 —— 不再是纸面问题。
            // 实测（1280x720 16-bit PNG 输入、-q 80、420/10-bit、各点 3 次取中位，
            //   scripts/precision/svt_preset_sweep.py）：
            //   -s 0  aom 1.78s / svt 1.23s   -30%
            //   -s 2  aom 0.74s / svt 0.29s   -60%     ⇒ "SVT 更快"在【慢速档】确实成立
            //   -s 4  aom 0.34s / svt 0.21s   -39%
            //   -s 6  aom 0.13s / svt 0.13s    ≈ 持平，且 svt 体积更大   ← 管线唯一在用的档位
            //   -s 8  aom 0.10s / svt 0.11s   +7%，svt 体积大 28%
            // SpeedArg 是常量 `-s 6`（见 NativeAvifEncoder 的实测甜点注记），SVT 的优势档本产品
            // 永远走不到；而 SVT 实测只有 4:2:0 与 ≤10-bit，Auto 选它等于把 422/444/12-bit 无谓降级。
            // ⇒ 兜底仍是 libaom；用户在下拉里【手动】选 SVT-AV1 不受影响。
            //    若将来把 SpeedArg 挪到 -s ≤4，这条顺序值得用同一脚本重测后再议。
            AvifEncoderKind.LibAom,
            AvifEncoderKind.SvtAv1,
            AvifEncoderKind.Avifenc,
        ];

        // 与协商同源：未知/缺失色度按 420 处理（否则"未知值"会把所有编码器都过滤光）。
        var want = NormalizeChroma(wantChroma);

        // 第一轮：可用 且 色度能原样编出来 ⇒ 才是"不需要降级"的选择。
        foreach (var kind in Order())
            if (isAvailable(kind) && NegotiateChroma(kind, want) == want)
                return kind;

        // 第二轮：没人满足该色度（例如只有硬件却请求 444）⇒ 仍取第一个可用的，
        // 由 NegotiateChroma / NegotiateBitDepth 降级、上层如实提示。
        // Auto 从不因"参数太高"而失败，只是不再"无条件优先硬件"。
        foreach (var kind in Order())
            if (isAvailable(kind)) return kind;

        return AvifEncoderKind.LibAom;   // 一个都不可用时的兜底
    }

    /// <summary>
    /// ═══ 该编码器种类能否用于 **HDR 路径** ═══
    /// <para>2026-09-18 更新：**只剩 QSV·MFT 不可用**。NVENC 的 HDR 10-bit 直通已接通
    /// （<c>ToneMapper.HdrToArgb10Pq</c> 产出 PQ 10-bit 输入 → NVENC → 容器 colr 按
    /// transfer=16(PQ) 声明；码流级 <c>color_description_present_flag</c> 驱动不写，已实测）。
    /// 失败时回退 avifenc 软件路径，对外行为不变。</para>
    /// <para>QSV·MFT 不可用是三条代码层面的硬约束，不是策略选择：
    /// <b>① 输入契约</b>：<c>IAvifEncoder.EncodeAsync</c> 只接受 8-bit BGRA（每像素 4 字节）。
    ///   NVENC 走 <c>NV_ENC_BUFFER_FORMAT_ARGB</c>（8-bit 打包），MFT 走 <c>BgraToNv12</c>（8-bit 4:2:0）；
    ///   10-bit 输出是靠 <c>NV_ENC_CONFIG_AV1.outputBitDepth</c> 让硬件把 8-bit 输入升到
    ///   10-bit，输入契约本身<b>仍然只有 8-bit BGRA</b>。
    ///   HDR 分支的数据是 16-bit 容器的 PQ 像素（每像素 8 字节），根本喂不进去。
    /// <b>② 容器层</b>：硬件路径用 <c>IvfWriter.WriteAvif</c> 自写 ISOBMFF，其 colr(nclx)
    ///   <b>已按场景分派</b>（2026-09-19 更正：旧述"硬编码 transfer=13/matrix=1"已失效 ——
    ///   现在 SDR 写 13/1、HDR 写 16/9，且三字段按 16-bit 大端写，见 <c>NvEncoderNative</c> 的
    ///   colr 段；仍缺 <b>clli/mdcv</b>，见交接 §26.4 第 4 项）
    ///   ⇒ 本条不再构成 HDR 障碍，QSV·MFT 被排除的真实原因是 ① 与 ③。
    /// <b>③ 色彩描述</b>：硬件序列头里 <c>color_description_present_flag=0</c>（即 BT.709+sRGB+
    ///   Identity 默认），无法表达 PQ/BT.2020 —— 这是比位深更硬的约束
    ///   （位深 10 自 2026-09-17 起对 SDR 已不是障碍）。</para>
    /// <para>Auto 视为可用（它会由管线解析后再判定）。</para>
    /// </summary>
    public static bool SupportsHdrPath(AvifEncoderKind kind) =>
        kind is not AvifEncoderKind.Qsv;

    /// <summary>该编码器在给定场景下是否满足需求（供 UI 判断选项是否可用）。</summary>
    /// <param name="hdr">是否为 HDR 路径。位深的合法集合在两个场景下不同，必须显式指定。</param>
    /// <remarks>
    /// ⚠ 旧签名没有 <c>hdr</c>，恒按 <c>MaxBitDepth</c>（HDR 语义）判断位深，
    /// 与 <see cref="NegotiateBitDepth"/> 的分场景语义不一致 —— 两者一旦分歧，
    /// UI 就会暴露出管线实际产不出的选项。现改为直接复用协商结果，
    /// 从根本上保证"UI 判断"与"实际产出"同源。
    /// </remarks>
    public static bool Satisfies(AvifEncoderKind kind, string chroma, int bitDepth, bool hdr)
    {
        var c = Caps(kind);
        return SupportsChroma(c, NormalizeChroma(chroma))
            && bitDepth == NegotiateBitDepth(kind, bitDepth, hdr);
    }
}
