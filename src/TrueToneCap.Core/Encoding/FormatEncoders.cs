// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/FormatEncoders.cs
// 全部编码器 — 零 Magick.NET 依赖
// PNG: 托管编码器 | JPEG LI: jpegli P/Invoke | JXL: cjxl | AVIF: libavif/硬件 | WebP: libwebp | TIFF: 托管
// （2026-09-26 删除 BMP 托管编码器：`OutputFormat` 里没有 BMP、工厂与界面下拉都没有它，
//   唯一调用点是门禁自己的两个专项测试 —— 一个没有产品入口的格式不构成"支持的格式"。）
using System.Threading.Tasks;
using System.IO;
using TrueToneCap.Core.Logging;
using Vortice.Direct3D11;
using Vortice.Direct3D;

namespace TrueToneCap.Core.Encoding;

// ────── PNG ──────
public sealed class PngEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.PNG;
    public override bool SupportsHdr => true;
    public override (float, float, float, QualityScale) GetQualityRange() => (100f, 100f, 100f, QualityScale.LosslessFixed100);
    public override QualityDesc GetQualityDescription(float _) => new(QualityDescKind.Lossless, 100f);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            if (s.OutputBitDepth <= 8)
            {
                // 用户选择 8-bit → 色调映射到 SDR，走 SDR 编码路径
                // ═══ 2026-09-19 可追溯性：HDR 意图在这里被改掉，此前完全无声 ═══
                // 8-bit 容器装不下 PQ（1023 级要压成 255 级，高光阶梯全毁），所以只能退回 SDR ——
                // 决策是对的，但"用户要 HDR、拿到 SDR"必须让他知道（同 WebP→PNG / avifenc 拒位深
                // 那条通道；ToSdrAndDegrade 只翻标志，不产生任何对外痕迹）。
                EncoderFallbackNotifier.LastReason =
                    "PNG 的 HDR 输出需要 ≥10-bit 容器（PQ 压进 8-bit 会毁掉高光阶梯）"
                    + "⇒ 本次按请求的 8-bit 走 SDR 色调映射，未输出 HDR";
                var d = FormatHelper.ToSdrAndDegrade(f, s);
                await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
            }
            else
            {
                // HDR: scRGB → 目标色域线性 → PQ → PNG (10/12/16-bit) + cICP
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var csTag = FormatHelper.HdrDeclarableTag(s);
                    int hdrBitDepth = s.OutputBitDepth switch { 10 => 10, 12 => 12, _ => 16 };
                    // HdrToPq16 直接量化到目标位深，再左对齐到 16-bit 容器
                    var p16 = FormatHelper.HdrToPq16(f, csTag, hdrBitDepth);
                    var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                    byte[] cicp = FormatHelper.BuildHdrCicp(csTag); // PQ transfer=16, matrix=identity（PNG 存 RGB 采样）
                    // ═══ 2026-08-09 修复: HDR 不嵌 ICC ═══
                    // PNG 3.0 规范: cICP 优先于 iCCP。嵌入的 iCCP (标准 ICC, sRGB TRC)
                    // 与 PQ 像素矛盾 → 按 iCCP 解码的查看器图像错误。CICP 声明足够。
                    byte[]? icc = null;
                    // IHDR 始终为 16-bit，sBIT 标记实际位深 (PNG 3.0 Table 12: color type 6 仅允许 8/16)
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, path, cicp: cicp, iccProfile: icc, bitDepth: hdrBitDepth);
                }, ct);
            }
        }
        else
        {
            // ═══ 2026-09-19 解耦：精度链与容器位深是两件独立的事（策略：源精度由 Windows 给不给得出 >8-bit 决定）═══
            // ① 中转/输入精度 = FormatHelper.IsHighPrecisionSource：本入口恒为 Float16 scRGB 源
            //    ⇒ 恒走"色域矩阵 → 色调映射 → gamma"的浮点链，最后只量化一次；
            // ② 最终容器 = 用户请求的 OutputBitDepth（PNG IHDR 8/16 + sBIT）。
            // 旧写法用同一个 `OutputBitDepth >= 10` 同时决定两件事 ⇒ 用户选 8-bit 就顺带跳过了色域矩阵。
            if (FormatHelper.IsHighPrecisionSource(f) && s.OutputBitDepth >= 10)
            {
                // ≥10 的请求 ⇒ 16-bit IHDR + sBIT=实际位深（PNG 没有 10/12 的 color type 6 容器）。
                // 容器是终点、不会再被按位深读一次 ⇒ quantizeBits 用默认 16（左对齐会白丢 6 位精度）。
                var px16 = FormatHelper.PrepareSdr16Pixels(f, s);
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var (icc, cicp) = FormatHelper.GetColorMetadata(s);
                    ManagedPngEncoder.Encode16(px16, f.Width, f.Height, path, cicp: cicp, iccProfile: icc, bitDepth: s.OutputBitDepth);
                }, ct);
                return;
            }
            // 8-bit 请求：同一条浮点链，只是最后量化到 8-bit（矩阵不跳过 ⇒ 广色域声明与像素仍一致）
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }

    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // ═══ 2026-09-17 双路径策略：byte[] 入口恒为 8-bit 源 ⇒ 不升位深 ═══
        // ⚠ 2026-09-18 注释勘误：旧注释描述的"10/12 落 16-bit 容器 + sBIT=8"机制已不存在——
        //     下方钳制先于 switch 执行，byte[] 入口恒输出 8-bit PNG（无 sBIT）。
        //     2026-09-19 更新：Float16 源无论请求 8 还是 ≥10 都不再经过本方法 ——
        //     ≥10 走 EncodeAsync 的 16-bit 分支（PrepareSdr16Pixels），
        //     8 走 EncodeAsync 的浮点链量化到 8-bit 后再调本方法（只做容器写入，不做色彩换算）。
        if (s.OutputBitDepth > 8) s.OutputBitDepth = 8;
        int bitDepth = s.OutputBitDepth switch
        {
            10 => 10,
            12 => 12,
            >= 16 => 16,
            _ => 8,
        };
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var (icc, cicp) = FormatHelper.GetColorMetadata(s);
            ManagedPngEncoder.Encode(px, w, h, path, bitDepth, icc, cicp);
        }, ct);
    }
}

// ────── JPEG LI (jpegli — 唯一 JPEG 编码器) ──────
public sealed class JpegLiEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_LI;
    public override bool SupportsHdr => false;
    public override (float, float, float, QualityScale) GetQualityRange() => (0.5f, 3.0f, 1.0f, QualityScale.ButteraugliRange);
    public override QualityDesc GetQualityDescription(float q) => new(QualityDescKind.DistanceWithNote, q);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // JPEG LI 不支持 HDR，始终降级到 SDR（ToSdrAndDegrade = 同一条浮点链，只是量化到 8-bit）
        // ═══ 2026-09-17 双路径策略：JPEG 容器恒 8-bit ⇒ 诚实降 8（防下游回退 PNG 时升采样）═══
        // 2026-09-19：容器恒 8 不再意味着精度链被跳过 —— 色域矩阵/色调映射/gamma 照做，
        // 广色域 tag 与 ICC 一并保留（旧实现把 tag 覆写成 sRGB，声明与实际像素两处都错）。
        s.OutputBitDepth = 8;
        var d = FormatHelper.ToSdrAndDegrade(f, s);
        await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        try
        {
            await EncodeSdrCoreAsync(px, w, h, s, path, ct);
        }
        catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException or AggregateException))
        {
            // ═══ 2026-09-29 用户口径：接上 PNG 保底，且触发必须告诉用户 ═══
            // 此前这条腿【完全没有】保底（JpegGainMapEncoder.cs 那句"无回退，必须可用"就是这个口径），
            // 于是 cjpegli 在位但这次编不出来 / 写盘撞共享冲突 ⇒ 图直接丢。
            // 与 WebP / AVIF 走同一份 FormatHelper.PngLastResort，不在这里再写一遍取声明/登记路径/留痕。
            FormatHelper.PngLastResort(px, w, h, path, s.ColorSpaceTag, s.IccProfile, ex,
                who: "JPEG LI",
                why: $"cjpegli 本次编码或写盘失败（{ex.GetType().Name}）⇒ 回退 PNG",
                notice: "本机 cjpegli 本次编码失败 ⇒ 本次已改存为 PNG 文件（不是 JPEG LI，像素无损、体积可能更大），色域声明按本次设置一并写入");
        }
    }

    private static async Task EncodeSdrCoreAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;

            if (!JpegLiNative.IsAvailable)
                throw new InvalidOperationException("JPEG LI 编码需要 cjpegli.exe (Google jpegli)，请将 cjpegli.exe 放入 native/ 目录、PLAN/tools/ 目录或系统 PATH。");

            var jpegBytes = JpegLiNative.Encode(px, w, h, s.Quality, s.ChromaSubsampling, icc, effort: s.Effort);
            File.WriteAllBytes(path, jpegBytes);
        }, ct);
    }
}

// ────── JPEG XL ──────
public sealed class JpegXlEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.JPEG_XL;
    public override bool SupportsHdr => true;
    public override (float, float, float, QualityScale) GetQualityRange() => (0.1f, 4.0f, 1.0f, QualityScale.ButteraugliDistanceRange);
    public override QualityDesc GetQualityDescription(float q) => q <= 0.1f ? new(QualityDescKind.NearLossless, q) : new(QualityDescKind.Distance, q);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        try
        {
            await EncodeCoreAsync(f, s, path, ct);
        }
        catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException or AggregateException))
        {
            // ═══ 2026-09-29 用户口径：接上 PNG 保底（HDR 与高精度 SDR 两条支都盖）═══
            // 这条腿原先同样【没有】保底：cjxl 非零退出 / 超时 / 写盘撞共享冲突 ⇒ 直接冒出去。
            // 保底交回【产品自己的 PNG 编码器】处理（理由见 FormatHelper.PngLastResortHdrFrameAsync）——
            // 这里的源是 scRGB float 帧，硬压成 8-bit 等于在保底里再偷偷做一次动态范围降级。
            await FormatHelper.PngLastResortHdrFrameAsync(f, s, path, ex,
                who: "JPEG XL",
                why: $"cjxl 本次编码或写盘失败（{ex.GetType().Name}）⇒ 回退 PNG",
                notice: "本机 cjxl 本次编码失败 ⇒ 本次已改存为 PNG 文件（不是 JPEG XL，HDR 与否与位深按本次设置由 PNG 编码器决定），色域声明一并由它写入");
        }
    }

    private async Task EncodeCoreAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct)
    {
        if (s.HdrOutput)
        {
            // HDR JXL: scRGB → 目标色域线性 → PQ 16-bit → NativeJxlEncoder.EncodeHdr
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = FormatHelper.HdrDeclarableTag(s);
                // JXL HDR 固定 16-bit 全精度
                var pq16 = FormatHelper.HdrToPq16(f, csTag, 16);
                // ═══ 2026-08-10 修复(2): intensity_target = 显示器 HDR 峰值 ═══
                // 截图内容的主控目标 = 源显示器 (截的就是显示器显示的)。
                // PQ 是绝对亮度编码 (码值→绝对 nits), intensity_target 语义 =
                // "内容亮度上限" = 显示器峰值。查看器据此做 tone-map:
                // - 显示器能力 ≥ intensity_target → 绝对亮度直显 (SDR 白点 200nits 正确)
                // - 显示器能力 <  → 按比例压缩
                // 错误历史:
                //   恒 10000 → 查看器把 200nits SDR 内容在 10000nits 容器 tone-map → 极暗
                //   内容实际峰值(822) → 部分查看器 re-normalize 内容峰值→显示器峰值
                //     → SDR 白点被抬亮 → 发灰 (用户指出的问题)
                // 正确: 显示器峰值 (DisplayMaxNits 系统检测优先) + 内容峰值兜底防 clip
                float intensityTarget = s.ToneMappingParams.DisplayMaxNits;
                if (intensityTarget <= 0f) intensityTarget = 1000f; // 兜底: HDR 显示器常见峰值
                float contentPeak = FormatHelper.ComputePeakNits(pq16);
                if (intensityTarget < contentPeak) intensityTarget = contentPeak;
                // ═══ 2026-08-09 修复: JXL HDR 不嵌 ICC ═══
                // `-x color_space=Rec2100PQ` 已声明 BT.2020 + PQ + intensity_target。
                // 嵌入 ICC (标准 ICC, sRGB TRC) 会覆盖 color fields → PQ 传输丢失 →
                // 解码器按 sRGB gamma 解码 PQ 值 → 图像错误。
                // ═══ 2026-09-27 实测收口（交接 §62，闭 §31.3-5）：P3 那一档违反的正是上面这条 ═══
                // 旧写法给 P3 递的是 `GetStandardIccProfile(csTag)`、或用户那份可嵌 ICC ——
                // 两者按定义都是 **SDR 曲线**的 profile，而 ICC 在 cjxl 里压过 color fields
                //（`icc_pathname` 与 `-x color_space` 两种先后顺序产物逐字节相同，实测）⇒ P3 目标下
                // jxlinfo 读回 Primaries=[P3] / Transfer=[sRGB]，而同一条声明里 intensity_target 仍写
                // 1000 nits：一张图同时自称 SDR 曲线与 HDR 峰值。码值差的倍数是量的，不是推的
                //（同一批 PQ 像素按 sRGB 解 ⇒ 100 nit 档 22.1×、203 nit 档 1.46×）。
                // 改法 = 递【带 PQ 曲线】的那份 ICC。`GetHdrStandardIccProfile` 在 HDR TIFF 一路早就
                // 这么用了（见同文件 TIFF 分支同段注释：非用 PQ TRC ICC 不可），JXL 这边只是漏了同步。
                // 实测随包 cjxl v0.11.2 把这份 ICC 归约成 color fields（Custom P3 原色 + Transfer=PQ、
                // 209 B、与改前像素 max|Δ|=1）⇒ 原色与传递函数两个维度不再互相牺牲。
                // ⚠ 用户那份 s.IccProfile 在 HDR 路不再透传：它描述的是面板、且是 SDR 曲线，
                //   而像素已被 HdrToPq16 搬到 csTag + PQ ⇒ 声明要说【编码后】的空间，不是说面板。
                // ⚠ 可达性如实记账：界面侧 <c>CollapseSelectableTag</c> 把 DisplayP3 / DCI_P3 一律并成
                //   BT2020 ⇒ 【今天】从界面走不进这一支，这不是正在出货的错。留着的理由是分支本来就在，
                //   而加一档或改解析顺序随时会把它接通（接通的瞬间旧写法就交出"PQ 被按 sRGB 曲线解释"的产物）。
                //   它【已有判据盯着】：--jxl-color-product 的 HDR 层直接驱动编码器、不经过界面，
                //   把下面这个函数换回 GetStandardIccProfile 就当场红（变异实测见交接 §62.4）。
                byte[]? jxlIcc = csTag is "DisplayP3" or "DCI_P3"
                    ? ColorManagement.ColorProfileProvider.GetHdrStandardIccProfile(csTag)
                    : null;
                NativeJxlEncoder.EncodeHdr(pq16, f.Width, f.Height, path, s.Quality, jxlIcc, intensityTarget, csTag, s.ChromaSubsampling, s.Effort);
            }, ct);
        }
        else
        {
            // ═══ 2026-09-27（用户口径）：AdobeRGB 收口进 Rec.2020，不再退回 sRGB ═══
            // 旧写法（2026-09-19 缺陷 B）在这里把 tag 改成 sRGB 并清掉 ICC，理由是
            // "libjxl 会把承载不了的 ICC 归到最近枚举并丢弃 profile"。那条理由【已被实测推翻】
            //（交接 §56.4）：用我们自己产物里那份 3144 字节 Adobe ICC 走 icc_pathname 通路喂
            // 随包 cjxl v0.11.2，jxlinfo 读回 Custom(0.639998,0.329997) —— Adobe 原色逐字还在；
            // 旧结论里的"primaries=bt2020"是 ffprobe 在【读取方】把自定义原色归到最近 H.273 码点。
            // 更要紧的是口径不一致：界面侧 CollapseSelectableTag 与 HDR 侧 HdrDeclarableTag
            // 早就把 Adobe 收口成 BT2020，只有这条 JXL SDR 路往反方向（sRGB）收 ——
            // 同一枚用户选择在三个地方得到三种产物。现在三处同向。
            // BT.2020 比 AdobeRGB 宽 ⇒ 归一不裁色域；代价是"用户显式选的 Adobe 未被单独搬运"，
            // 所以仍走 EncoderFallbackNotifier 留痕，不静默。
            if (s.ColorSpaceTag == "AdobeRGB")
            {
                s.ColorSpaceTag = ColorManagement.ColorProfileProvider.MapAdobeTargetToWide(s.ColorSpaceTag);
                s.IccProfile = null;   // 必须一起清：实测 ICC 在 cjxl 里压过 color fields
                                       //（两种参数顺序都给 ICC 那份声明），留着 Adobe 的 ICC
                                       // 就会"像素按 2020 搬、声明写 Adobe 原色" —— 比原先更难查。
                EncoderFallbackNotifier.LastReason =
                    "目标色域 AdobeRGB 已按产品口径收口为 Rec.2020（界面只留 Rec.709 / Rec.2020 两档，"
                    + "HDR 路同样收口）⇒ 本张像素与声明同为 Rec.2020，未单独做 Adobe 原色搬运";
            }
            // ═══ 2026-09-19 解耦：两件事各自的判据（详见 PNG 分支同段说明）═══
            // ① 输入精度：本入口源恒为 Float16 scRGB ⇒ 恒走浮点链（矩阵 + 色调映射 + gamma）
            // ② 最终容器位深：用户请求的 OutputBitDepth → cjxl 的 16-bit PNG 输入容器 + sBIT
            if (FormatHelper.IsHighPrecisionSource(f) && s.OutputBitDepth >= 10)
            {
                // ═══ 2026-09-19 缺陷 D-6：中转像素必须与中转 PNG 的 sBIT 同源 ═══
                // 旧写法用 PrepareSdr16Pixels 的默认 quantizeBits=16（满量程铺到 0-65535），而下面
                // Encode16Bgra16 拿【用户请求位深】去写中转 PNG 的 sBIT=10/12 ⇒ "像素按 16-bit 铺满、
                // 标签却说只有 10/12 位有效"。cjxl 读不读 sBIT 决定这些低位的命运：不读 ⇒ 与 AVIF 那条
                // 路等价；读并按左对齐解释 ⇒ 直接误缩放。按请求位深量化再左对齐后，两种读法拿到的都是
                // 同一组 10/12-bit 值 ⇒ 语义不再取决于外部二进制的内部实现。
                // 映射与 AVIF 组 B 同源（≤8→8、≥12→12、其余→10）；JXL 容器本身支持 16-bit
                // （avifenc 只到 12），故 ≥16 保留 16。TIFF/PNG 不在此列：前者 BPS 恒 16、
                // 后者的 sBIT 台阶取整发生在同一个容器写入里，容器与像素本来就一致。
                int jxlDepth = s.OutputBitDepth >= 16 ? 16 : FormatHelper.AvifContainerDepth(s.OutputBitDepth);
                var px16 = FormatHelper.PrepareSdr16Pixels(f, s, jxlDepth);
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
                    // ═══ 2026-09-19：色度开关此前从未抵达 cjxl（本仓库默认位深 10 ⇒ 走的就是这条），
                    // 界面上的 4:4:4 / 4:2:0 改了产物字节完全相同。映射见 NativeJxlEncoder.BuildResamplingArg。
                    NativeJxlEncoder.Encode16Bgra16(px16, f.Width, f.Height, path, s.Quality, s.OutputBitDepth, icc, s.ChromaSubsampling, s.ColorSpaceTag, s.Effort);
                }, ct);
                return;
            }
            // 8-bit 请求：同一条浮点链，只是最后量化到 8-bit（矩阵不跳过）
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        try
        {
            await EncodeSdrCoreAsync(px, w, h, s, path, ct);
        }
        catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException or AggregateException))
        {
            // 与 JPEG LI / WebP 同一条保底（byte[] 入口恒 8-bit ⇒ 用非 HDR 那份就够，不引入第二次降级）。
            FormatHelper.PngLastResort(px, w, h, path, s.ColorSpaceTag, s.IccProfile, ex,
                who: "JPEG XL",
                why: $"cjxl（8-bit SDR 入口）本次编码或写盘失败（{ex.GetType().Name}）⇒ 回退 PNG",
                notice: "本机 cjxl 本次编码失败 ⇒ 本次已改存为 PNG 文件（不是 JPEG XL，像素无损、体积可能更大），色域声明按本次设置一并写入");
        }
    }

    private static async Task EncodeSdrCoreAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct)
    {
        // ═══ 2026-09-17 双路径策略：byte[] 入口恒为 8-bit 源 ⇒ 不升位深 ═══
        if (s.OutputBitDepth > 8) s.OutputBitDepth = 8;
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
            // 有可嵌 ICC 就走 ICC；没有时由 NativeJxlEncoder 按【目标标签】选 cjxl 的 color_space
            // IDE 名（过去这里等价于"没 ICC 就一律声明 sRGB"，会把 BT.2020 的产物主动写错）。
            NativeJxlEncoder.Encode(px, w, h, path, s.Quality, s.OutputBitDepth, icc, s.ChromaSubsampling, s.ColorSpaceTag, s.Effort);
        }, ct);
    }
}

// ────── AVIF (动态后端) ──────
public sealed class AvifEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.AVIF;
    public override bool SupportsHdr => true;
    public override (float, float, float, QualityScale) GetQualityRange() => (0f, 63f, 15f, QualityScale.Crf);
    public override QualityDesc GetQualityDescription(float q) => q <= 0 ? new(QualityDescKind.Lossless, q) : new(QualityDescKind.Crf, q);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        try
        {
            await EncodeCoreAsync(f, s, path, ct);
        }
        catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException or AggregateException))
        {
            // HDR 那条腿此前【没有任何】保底 —— 硬件链上移到编码器这一层之后必须一起补上，
            // 否则"关掉开关只影响 SDR 路"就是一种口径分裂。
            // 走 PNG 编码器本体：请求 ≥10-bit 时产出的是带 PQ 声明的 HDR PNG（动态范围保住），
            // 请求 8-bit 时 PngEncoder 自己会退回 SDR 并留下第二条留痕（在下面并成一句）。
            await FormatHelper.PngLastResortHdrFrameAsync(f, s, path, ex,
                who: "AVIF",
                why: $"AVIF HDR 编码失败（{ex.GetType().Name}）⇒ 按用户设置回退 PNG",
                notice: "本机 AVIF HDR 编码本次失败 ⇒ 本次已改存为 PNG 文件（不是 AVIF；HDR 与位深按本次设置由 PNG 编码器决定），色域声明一并由它写入");
        }
    }

    private async Task EncodeCoreAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct)
    {
        if (s.HdrOutput)
        {
            // HDR AVIF: scRGB → 目标色域线性 → PQ 16-bit → 16-bit PNG 中转 + avifenc
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var csTag = FormatHelper.HdrDeclarableTag(s);
                // ═══ 2026-09-18（用户指令）：HDR 输出优先走 NVENC 硬件 10-bit ═══
                // NVENC 已支持真 10-bit 输入（ARGB10）+ outputBitDepth=10；容器元数据由
                // WriteAvif 的 colr(nclx) 按 PQ(transfer=16) 声明（码流级 CDI 驱动不写，实测）。
                // 失败回退 avifenc 软件路径（16-bit PNG 中转），下方既有逻辑不变。
                if (s.AvifEncoderKind is AvifEncoderKind.Nvenc or AvifEncoderKind.Auto)
                {
                    try
                    {
                        var pq10 = Processing.ToneMapper.HdrToArgb10Pq(f.Pixels, f.Width, f.Height, csTag);
                        // clli 的内容峰值必须来自【这张图真正编进去的那份码值】：硬件路是 ARGB10，
                        // 软件路是 16-bit PQ，两路都问 FormatHelper.AvifHdrPeakNits 拿同一个口径。
                        float hwPeakNits = FormatHelper.AvifHdrPeakNits(
                            s, FormatHelper.ComputePeakNitsFromArgb10(pq10));
                        // 已在外层 Task.Run 的线程池线程上，直接同步调用（内部为阻塞实现）
                        bool hwOk = NvencAvifBackend.TryEncodeNvencHdrAvif(
                            pq10, f.Width, f.Height, (int)s.Quality, path, ct, csTag, hwPeakNits);
                        if (hwOk)
                        {
                            CoreLog.Debug("AVIF HDR", "NVENC 硬件 10-bit 直通成功", LogCategory.Encoding);
                            return;
                        }
                        CoreLog.Warn("AVIF HDR", "NVENC 不可用/失败，回退 avifenc 软件编码", LogCategory.Encoding);
                        // ═══ 2026-09-19 可追溯性：HDR 分支原先只留 Debug 输出，Release 下完全无声 ═══
                        // 硬件与软件的产物差别不小（码流 chroma 恒 420；clli 自 2026-09-20 两路都写、
                        // 取值同一个函数），用户有权知道这一张是哪条路编的。
                        EncoderFallbackNotifier.LastReason =
                            "NVENC 的 HDR 10-bit 直通本次不可用 ⇒ 已改由 avifenc 软件编码"
                            + "（PQ/10-bit；两条路的 clli 取值同一个函数，亮度声明不变）";
                    }
                    catch (Exception ex)
                    {
                        CoreLog.Warn("AVIF HDR", $"NVENC 异常（{ex.GetType().Name}），回退 avifenc 软件编码",
                            ex, LogCategory.Encoding);
                        EncoderFallbackNotifier.LastReason =
                            $"NVENC 的 HDR 直通抛出异常（{ex.GetType().Name}）⇒ 已改由 avifenc 软件编码";
                    }
                }
                // 量化位深 = 上游按编码器能力协商出的结果（HDR 下限 10，libaom 可到 12）。
                // 2026-09-25 前这里是字面量 10，而能力表给 libaom 声明的是 MaxBitDepth:12 ⇒
                // "选了 12 也只给 10"，且这个 10 在管线 / 量化 / avifenc 命令行各写了一遍。
                int hdrDepth = s.OutputBitDepth;
                var p16 = FormatHelper.HdrToPq16(f, csTag, hdrDepth);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(p16, f.Width, f.Height);
                byte[] cicpHdr = FormatHelper.BuildHdrCicp(csTag); // PQ transfer=16, matrix=identity（AVIF 侧换成 9，见 FormatHelper.AvifMatrixCoefficients）
                // ═══ 2026-08-09 修复: HDR 不嵌 ICC (同 PNG, CICP 优先声明) ═══
                byte[]? iccHdr = null;
                var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_avif_hdr_{Guid.NewGuid():N}.png");
                try
                {
                    // fastest: true —— 这个 PNG 只是交给 avifenc 的中转文件，用完即删。
                    // 压缩率不影响最终 AVIF 质量，用快速压缩可省下数秒（实测 4K HDR AVIF
                    // 的前处理开销主要就耗在这里）。
                    ManagedPngEncoder.Encode16(bgra16, f.Width, f.Height, tmpPng, cicp: cicpHdr, iccProfile: iccHdr, fastest: true);
                    // ═══ 2026-08-10 修复: AVIF HDR clli = 显示器 HDR 峰值 (同 JXL) ═══
                    // MaxCLL 语义 = 内容亮度上限 = 显示器峰值。恒 10000 → SDR 内容极暗。
                    // 2026-09-20：这条口径搬进 FormatHelper.AvifHdrPeakNits，硬件路用同一个函数。
                    float peakNits = FormatHelper.AvifHdrPeakNits(
                        s, FormatHelper.ComputePeakNits(p16));
                    // ═══ 2026-09-17→18：HDR 的色度与编码器选择真实生效 ═══
                    // 色度：直通用户设置，EncodeFile 会拼成 --yuv。
                    // 编码器：上方已优先尝试 NVENC 硬件直通（PQ 10-bit + colr PQ 声明）；
                    //   走到这里说明硬件不可用或用户明确选了软件 —— avifenc 16-bit PNG
                    //   中转是兜底路径（cICP 已带 PQ 声明，无"HDR 被当 SDR"问题）。
                    NativeAvifEncoder.EncodeFile(tmpPng, path, (int)s.Quality, cicpHdr, csTag, peakNits,
                        chroma: s.ChromaSubsampling,
                        codec: AvifEncoderSelector.ResolveAvifencCodec(s.AvifEncoderKind),
                        bitDepth: hdrDepth, effort: s.Effort);
                }
                finally { try { File.Delete(tmpPng); } catch { } }
            }, ct);
        }
        else
        {
            var be = AvifEncoderSelector.SelectByKind(s.AvifEncoderKind, s.ChromaSubsampling, s.OutputBitDepth);

            // ═══ 2026-09-19 解耦：AVIF 的三件判据各管一件事（旧写法用同一个 OutputBitDepth>=10 混在一起）═══
            // ① 源能给多高：IsHighPrecisionSource —— 本入口 = WGC Float16 scRGB（源精度）
            // ② 编码器吃得下多高：be.SupportsTrue10BitInput（当前仅 NVENC 有 ARGB10 输入通路）
            // ③ 最终容器要多高：用户请求的 s.OutputBitDepth（avifenc --depth / NVENC outputBitDepth）
            // ⚠ ②与③必须同时成立才用 ARGB10 通路：只看③ 会给只吃 8-bit 输入的后端送 10-bit 像素；
            //   只看② 则把用户明确要的 8-bit 容器偷偷升成 10-bit（两个条件各看一半都会错）。
            if (FormatHelper.IsHighPrecisionSource(f) && be.SupportsTrue10BitInput && s.OutputBitDepth >= 10)
            {
                // 浮点链（矩阵 → 色调映射 → gamma）后量化一次到 A2R10G10B10；tag/ICC 记账与容器声明同源
                var px10 = FormatHelper.PrepareSdrArgb10Pixels(f, s);
                await be.EncodeArgb10Async(px10, f.Width, f.Height, (int)s.Quality, path, ct,
                    s.ChromaSubsampling, s.OutputBitDepth, s.ColorSpaceTag, s.IccProfile);
                return;
            }
            // ═══ 软件后端（avifenc）只吃 8-bit BGRA 输入 ⇒ ≥10-bit 请求改走 16-bit PNG 中转，
            //     把 Float16 源的精度真正送进编码器；容器位深仍由用户请求决定（GetDepthArg 钳 10/12）═══
            if (FormatHelper.IsHighPrecisionSource(f) && !be.SupportsTrue10BitInput && s.OutputBitDepth >= 10)
            {
                // 中转按【实际容器位深】量化并左对齐到 16-bit 高位：avifenc 取高 N 位时原样拿回那 N 位，
                // 不再发生"16-bit 量化 → 又被 --depth 截到 10-bit"的二次量化。
                int avifDepth = FormatHelper.AvifContainerDepth(s.OutputBitDepth);
                // ═══ 2026-09-19 可追溯性：本分支【不调用 be】，硬件后端在这里被无声换成 avifenc ═══
                // 组 B 的前提是 !be.SupportsTrue10BitInput，而 QSV 的 MFT 输入是 NV12 8-bit ⇒ 10/12-bit
                // 请求既给不进硬件、这里也不会走硬件；旧实现一声不吭地用软件编完（4K 约 22s vs 硬件 0.12s），
                // 用户只看到"变慢了"。补进通用降级通道，与 MFT 失败回退 libaom 那条同源。
                if (s.AvifEncoderKind == AvifEncoderKind.Qsv)
                    EncoderFallbackNotifier.LastReason =
                        $"QSV(MFT) 无法接收 {avifDepth}-bit 输入 ⇒ 本张改由 avifenc 软件编码"
                        + "（容器位深仍按请求，但硬件未被使用，速度明显变慢）";
                var px16 = FormatHelper.PrepareSdr16Pixels(f, s, avifDepth);
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    // ═══ 广色域 tag 已由 PrepareSdr16Pixels 保留 ⇒ 容器标记必须与像素一致
                    //     （同函数已做 ICC 兜底；AdobeRGB 返回 null = 只靠 ICC 声明，见 BuildSdrCicp）
                    byte[]? icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
                    byte[]? cicpSdr = FormatHelper.BuildSdrCicp(s.ColorSpaceTag);
                    var tmpPng = Path.Combine(Path.GetTempPath(), $"ttc_avif_sdr16_{Guid.NewGuid():N}.png");
                    try
                    {
                        ManagedPngEncoder.Encode16(px16, f.Width, f.Height, tmpPng, cicp: cicpSdr, iccProfile: icc, fastest: true);
                        // ═══ 2026-10-08 修复(P1①): 位深真实生效 ═══
                        // 此前 EncodeFile 未传位深 → avifenc 默认 8-bit 处理高位深输入。
                        NativeAvifEncoder.EncodeFile(tmpPng, path, (int)s.Quality, cicp: cicpSdr,
                            colorSpaceTag: s.ColorSpaceTag ?? "sRGB", chroma: s.ChromaSubsampling,
                            codec: AvifEncoderSelector.ResolveAvifencCodec(s.AvifEncoderKind),
                            bitDepth: avifDepth,
                            effort: s.Effort,
                            // ⚠ 必须显式传：avifenc【不会】从中转 PNG 的 iCCP 继承 ICC（2026-09-20 实测），
                            //   漏传就等于软件路的广色域 AVIF 只剩 nclx，与硬件路的 prof+nclx 不一致。
                            iccProfile: icc);
                    }
                    finally { try { File.Delete(tmpPng); } catch { } }
                }, ct);
                return;
            }
            // ═══ 8-bit 容器请求（或源没有浮点数据）：同一条浮点链，只是最后量化到 8-bit ═══
            // 色域矩阵/色调映射/gamma 一律不跳过 —— 用户要 8-bit 只约束容器，不约束精度链。
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // ═══ 2026-09-16 重构：单一「AVIF 编码器」模型 ═══
        // 审查 P1-3 修复：Auto 也统一走 SelectByKind（内部 PickAuto，硬件优先 + SupportsAv1 过滤）。
        // 旧 AvifBackend（AvifBackendIndex）路径已废弃 —— 该字段在新 UI 中不再写入，
        // 读它只会拿到 settings.json 里的冻结旧值，导致 Auto 行为不确定。
        var be = AvifEncoderSelector.SelectByKind(s.AvifEncoderKind, s.ChromaSubsampling, s.OutputBitDepth);
        try
        {
            await be.EncodeAsync(px, w, h, (int)s.Quality, path, ct, s.ChromaSubsampling, s.OutputBitDepth, s.ColorSpaceTag, s.IccProfile, s.GpuTexture);
        }
        catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException or AggregateException))
        {
            // ═══ 2026-09-29 审查轮 §13.9 + 用户口径"开关" ═══
            // 旧写法把"libaom 也失败 ⇒ 退 PNG"挂在【硬件分支的 catch 里】。而 Auto 在没有 AV1 编码
            // 硬件的机器上（本机之外多数 Intel/AMD 机器）选出来就是 avifenc ⇒ avifenc 一失败图就没了,
            // 尽管这条函数自己的注释写着"确保截图不丢失"。
            // 现在硬件链（FallbackToLibAom）只【抛出】，换格式一律落在这一层 ⇒ 用户那个开关在这里生效，
            // 且这条 catch 同时覆盖"软件后端本身失败"和"硬件→libaom 也失败"两种来路。
            // ⚠ 取消 / ArgumentException（文件名本身非法）/ AggregateException（保底链自己已经试过 PNG
            //   且连 PNG 都没写出去，见 PngLastResort 末尾）都从 when 里排除，不再单列两条 `throw;` 分支——
            //   那两条在本轮把换格式上移之后已经与这里的过滤条件完全重复，留着只会让人以为还有别的链。
            FormatHelper.PngLastResort(px, w, h, path, s.ColorSpaceTag, s.IccProfile, ex,
                who: "AVIF",
                why: $"AVIF 编码失败（{ex.GetType().Name}；硬件链的 PNG 保底已上移到这一层）⇒ 最终回退 PNG",
                notice: "本机 AVIF 编码后端（avifenc / libaom / 硬件）本次失败 ⇒ 本次已改存为 PNG 文件（不是 AVIF，像素无损、体积更大），色域声明按本次设置一并写入");
        }
    }
}

// ────── WebP ──────
public sealed class WebPEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.WebP;
    public override bool SupportsHdr => false;
    public override (float, float, float, QualityScale) GetQualityRange() => (50f, 100f, 90f, QualityScale.QualityPercent);
    public override QualityDesc GetQualityDescription(float q) => q >= 100 ? new(QualityDescKind.Lossless, q) : new(QualityDescKind.Percent, q);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // WebP 不支持 HDR，始终降级到 SDR（ToSdrAndDegrade = 同一条浮点链，只是量化到 8-bit）
        // ═══ 2026-09-17 双路径策略：WebP 容器无 >8-bit 档 ⇒ 诚实降 8 ═══
        // 防止下游回退 PNG 时按 10-bit 升采样（那是容器级升采样，不产生信息）。
        // 2026-09-19：位深降 8 只约束容器；色域矩阵照做，tag/ICC 与像素保持一致（同 JPEG LI）。
        s.OutputBitDepth = 8;
        var d = FormatHelper.ToSdrAndDegrade(f, s);
        await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        // ═══ 2026-09-17 双路径策略：WebP 容器无 >8-bit 档；byte[] 源也不升位深 ═══
        s.OutputBitDepth = 8;
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var icc = (s.ColorSpaceTag is not (null or "System" or "sRGB")) ? s.IccProfile : null;
                NativeWebPEncoder.Encode(px, w, h, path, s.Quality, s.Quality >= 100, icc, s.Effort);
            }
            catch (Exception ex) when (s.PngFallbackEnabled && ex is not (OperationCanceledException or ArgumentException))
            {
                // ═══ 2026-09-29 审查轮 F 批：这里此前只 catch (DllNotFoundException) ═══
                // 而"回退 PNG"这句 promise 覆盖的是【这次拿不到 WebP 产物】整件事：cwebp 在位但编码失败
                // （非零退出 / 30 s 超时 / 产物没写出来，见 NativeWebPEncoder.cs:88-92 与
                // NativeEncoderGuard 那条 result.Success == false）抛的是 InvalidOperationException，
                // 旧写法让它直接冒出去 ⇒ 图丢、LastReason 仍是 null，而注释与文案都写着"回退 PNG"。
                // 行为判据：ColorPipelineTests.WebPFallback_WhenBackendFails_StillDeliversPngAndNotice
                //（独占目标文件真跑一次，修前 4 条红）。
                // ⚠ ArgumentException 不接：那是 FormatHelper.ValidateNativePath 报"这个文件名本身非法"，
                //   换个后缀仍然写不出去，兜下去只会把诊断从"路径非法"糊成"保底也失败"。
                bool missing = ex is DllNotFoundException;
                FormatHelper.PngLastResort(px, w, h, path, s.ColorSpaceTag, s.IccProfile, ex,
                    who: "WebP",
                    why: missing ? "libwebp 不可用，回退 PNG"
                                 : $"cwebp 本次编码失败（{ex.GetType().Name}），回退 PNG",
                    notice: missing
                        ? "本机没有可用的 cwebp（libwebp）⇒ 本次已改存为 PNG 文件（不是 WebP，像素无损、体积更大），色域声明按本次设置一并写入"
                        : "本机 cwebp 本次编码失败 ⇒ 本次已改存为 PNG 文件（不是 WebP，像素无损、体积更大），色域声明按本次设置一并写入");
            }
        }, ct);
    }
}

// ────── TIFF ──────
public sealed class TiffEncoder : ImageEncoder
{
    public override OutputFormat Format => OutputFormat.TIFF;

    /// <summary>
    /// TIFF 的 SDR 色彩声明（2026-09-19 缺陷 B1）。
    /// <para>TIFF 没有 cICP / color fields 这一类"码点"通道，<b>ICC 是唯一能把色域说清楚的标签</b>
    /// （Tag 34675 InterColorProfile）。所以原先那行 <c>(tag 是广色域) ? s.IccProfile : null</c>
    /// 在 sRGB 目标下产出的是【零声明】文件：16-bit/8-bit RGBA 像素上不带任何色彩标记，
    /// 读取端只能各自猜（TIFF 规范也没规定默认色域，Photoshop 与 libtiff 的默认并不一致）。</para>
    /// <para>因此：sRGB 也要显式嵌标准 profile；广色域若拿不到<b>结构上能声明色域</b>的 ICC
    /// （判据 <see cref="FormatHelper.IsEmbeddableIcc"/>）就换成该色域的标准 profile，
    /// 宁可声明得保守也不能不声明。</para>
    /// </summary>
    private static byte[]? SdrIccForTiff(EncodingSettings s)
    {
        if (s.ColorSpaceTag is not (null or "System" or "sRGB"))
            return FormatHelper.IsEmbeddableIcc(s.IccProfile)
                ? s.IccProfile
                : ColorManagement.ColorProfileProvider.GetStandardIccProfile(s.ColorSpaceTag);
        return ColorManagement.ColorProfileProvider.GetDefaultSRgbIcc();
    }
    public override bool SupportsHdr => true;
    public override (float, float, float, QualityScale) GetQualityRange() => (100f, 100f, 100f, QualityScale.LosslessFixed);
    public override QualityDesc GetQualityDescription(float _) => new(QualityDescKind.Lossless, 100f);
    public override async Task EncodeAsync(HdrFrameData f, EncodingSettings s, string path, CancellationToken ct = default)
    {
        if (s.HdrOutput)
        {
            // HDR TIFF: 16-bit PQ (目标色域线性 → PQ)
            await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                // 这里【故意不】套 FormatHelper.HdrDeclarableTag（与 PNG/AVIF/JXL 的 HDR 分支不同）：
                // TIFF 没有 CICP 机制，色彩全靠嵌 ICC，而 GetHdrStandardIccProfile("AdobeRGB") 给出的正是
                // "Adobe 原色 + PQ TRC"的 profile ⇒ HDR+Adobe 在 TIFF 上可以被忠实声明，
                // 归一到 BT.2020 反而把用户要的色域裁窄了。ICC 不受 H.273 码点表限制。
                var csTag = s.ColorSpaceTag ?? "sRGB";
                // TIFF HDR 固定 16-bit 全精度
                var pq16 = FormatHelper.HdrToPq16(f, csTag, 16);
                var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(pq16, f.Width, f.Height);
                // ═══ 2026-08-09 修复: TIFF HDR 嵌 PQ ICC ═══
                // TIFF 无 CICP 机制, 必须嵌 ICC。但标准 ICC 是 sRGB TRC, 与 PQ 像素矛盾。
                // 用 PQ TRC ICC (GetHdrStandardIccProfile), 否则解码器按 sRGB gamma 解码 PQ 值。
                byte[]? iccHdr = ColorManagement.ColorProfileProvider.GetHdrStandardIccProfile(csTag);
                // ═══ 2026-09-24（#22）：这里改掉用户选的位深，就必须留下痕迹 ═══
                // TIFF 的 BitsPerSample 只有 8/16 两档合法（写 10/12 会产出打不开的文件，见 XAML 那条
                // 注释），而 8-bit 装不下 PQ ⇒ HDR 只能落 16-bit。方向与 PNG 相反：PNG 遇 8-bit 是
                // 【降成 SDR】（丢 HDR 意图，#21 已给它加了协商 + 提示），TIFF 是【升到 16-bit】
                // （保住 HDR，代价是体积翻倍）。两者都属"界面设置与产物不一致"，都不许无声 ——
                // 不告知的话用户只会发现"我选了 8-bit，文件怎么是 16-bit 的、还大一倍"，无从归因。
                if (s.OutputBitDepth <= 8)
                    EncoderFallbackNotifier.LastReason =
                        "HDR TIFF 的容器位深恒为 16-bit（TIFF 只承认 8/16 两档 BitsPerSample，"
                        + $"而 8-bit 装不下 PQ）⇒ 本张未按所选的 {s.OutputBitDepth}-bit 输出，而是 16-bit";
                // 显式声明输入为 16-bit，避免依赖数组长度推断
                ManagedTiffEncoder.Encode(bgra16, f.Width, f.Height, path, 16, iccHdr, inputBitDepth: 16);
            }, ct);
        }
        else
        {
            // ═══ 2026-09-19 解耦：两件事各自的判据（详见 PNG 分支同段说明）═══
            // ① 输入精度：本入口源恒为 Float16 scRGB ⇒ 恒走浮点链（矩阵 + 色调映射 + gamma）
            // ② 最终容器：TIFF 的 BitsPerSample 只有 8/16 两档 ⇒ ≥10 的请求统一落到 16-bit BPS
            //    （保精度优先；10/12 与 16 在 TIFF 里没法分别声明，只能同档对待）
            if (FormatHelper.IsHighPrecisionSource(f) && s.OutputBitDepth >= 10)
            {
                var px16 = FormatHelper.PrepareSdr16Pixels(f, s);
                await Task.Run(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    var icc = SdrIccForTiff(s);
                    ManagedTiffEncoder.Encode(px16, f.Width, f.Height, path, 16, icc, inputBitDepth: 16);
                }, ct);
                return;
            }
            // 8-bit 请求：同一条浮点链，只是最后量化到 8-bit（矩阵不跳过）
            var d = FormatHelper.ToSdrAndDegrade(f, s);
            await EncodeSdrAsync(d, f.Width, f.Height, s, path, ct);
        }
    }
    public override async Task EncodeSdrAsync(byte[] px, int w, int h, EncodingSettings s, string path, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = SdrIccForTiff(s);
            // ═══ 2026-09-17 双路径策略：byte[] 入口恒为 8-bit 源 ⇒ 不升位深 ═══
            if (s.OutputBitDepth > 8) s.OutputBitDepth = 8;
            // SDR 输入恒为 8-bit BGRA（每像素 4 字节），显式声明避免按长度推断
            ManagedTiffEncoder.Encode(px, w, h, path, s.OutputBitDepth, icc, inputBitDepth: 8);
        }, ct);
    }
}

// ────── AVIF 后端 ──────
public static class AvifEncoderSelector
{
    private static readonly Dictionary<AvifEncoderBackend, IAvifEncoder> _be = new()
    {
        [AvifEncoderBackend.LibAom] = new LibAomAvifBackend(),
        [AvifEncoderBackend.Qsv] = new QsvAvifBackend(),
        [AvifEncoderBackend.Nvenc] = new NvencAvifBackend(),
    };
    private static readonly MftAvifBackend s_mftBackend = new();

    public static IAvifEncoder Select(AvifEncoderBackend pref)
    {
        if (pref == AvifEncoderBackend.Auto)
        {
            var encoders = GpuCapability.DetectEncoders();

            // ⚠ 必须同时校验 SupportsAv1：GpuCapability 已通过 DeviceId 范围与适配器名称
            // 精确判定 AV1 编码能力，而 Available 仅表示"NVENC 会话可创建"
            // （RTX 30 的 HEVC 可用即返回 true），并不代表支持 AV1 编码。
            // 缺少该过滤会导致 RTX 30 及更早显卡被选中 → AV1 编码失败 → 回退软件编码，
            // 白白经历一次硬件初始化开销。
            var nv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.NVENC && e.Available && e.SupportsAv1);
            var qsv = encoders.FirstOrDefault(e => e.Type == GpuEncoderType.QSV && e.Available && e.SupportsAv1);

            // ═══ 2026-08-30 修复: Auto 模式下硬件编码从未被选中 ═══
            // 原实现把 libaom 置于最高优先级，而 avifenc.exe 是**随程序内嵌发布**的，
            // 因此 LibAomAvailable 恒为 true —— NVENC/QSV 分支成为永远走不到的死代码。
            // 后果：即使配备 RTX 4090，4K AVIF 也要跑约 22 秒软件编码。
            //
            // 现调整为「有明确 AV1 能力的硬件优先」：
            //   · 硬件后端内部均有完善回退（NvencAvifBackend 失败 → AvifFallbackHelper → libaom），
            //     故优先尝试硬件不会降低可靠性，最坏情况等价于原行为。
            //   · SupportsAv1 为 false 的设备（如 RTX 30）不会被选中，避免无谓的失败回退。
            //   · DetectEncoders() 结果有缓存，探测开销只发生一次。
            if (nv is not null) return _be[AvifEncoderBackend.Nvenc];
            if (qsv is not null) return _be[AvifEncoderBackend.Qsv];

            // 无可用硬件 AV1 编码器 → 软件路径
            // libaom 优先于 MFT：MFT 可能检测为可用但实际编码失败
            if (LibAomAvailable) return _be[AvifEncoderBackend.LibAom];
            // ⚠ 别把这一行当死代码删掉：LibAomAvailable 依赖随包发布的 avifenc.exe，
            // 发布态恒为 true，但**开发态 native/ 目录为空时它为 false**，
            // 此处是那时唯一还能工作的软件回退（MFT 走系统 AV1 编码器）。
            if (s_mftBackend.IsAvailable) return s_mftBackend;
            return _be[AvifEncoderBackend.LibAom];
        }
        var b = _be.GetValueOrDefault(pref) ?? _be[AvifEncoderBackend.LibAom];
        return b.IsAvailable ? b : _be[AvifEncoderBackend.LibAom];
    }

    private static bool LibAomAvailable => _be[AvifEncoderBackend.LibAom].IsAvailable;

    /// <summary>
    /// ═══ 2026-09-16 新增：按"AVIF 编码器种类"（单一下拉的新模型）选择编码器 ═══
    /// <para>取代原「编码后端 + AVIF 编码器」两层结构。Auto 交由能力表
    /// <see cref="AvifEncoderCapabilities.PickAuto"/> 挑选（硬件优先，且需满足色度/位深需求）；
    /// 手动选择时尊重用户——若所选编码器不可用才回退 libaom。</para>
    /// <para>P2-5 修复：不再写全局静态的 avifenc codec 字段（原 <c>NativeAvifEncoder.Codec</c>
    /// 有并发竞态，该字段现已被移除）。软件路径（LibAom/SvtAv1）通过
    /// <see cref="LibAomAvifBackend"/> 实例的 codec 字段携带 avifenc 内部编码器选择，
    /// 实例不可变即无竞态。</para>
    /// </summary>
    public static IAvifEncoder SelectByKind(AvifEncoderKind kind, string chroma = "420", int bitDepth = 8)
    {
        if (kind == AvifEncoderKind.Auto)
            kind = AvifEncoderCapabilities.PickAuto(IsKindAvailable, chroma, bitDepth);

        switch (kind)
        {
            case AvifEncoderKind.LibAom:
                return new LibAomAvifBackend("LibAom");
            case AvifEncoderKind.SvtAv1:
                return new LibAomAvifBackend("SvtAv1"); // 同为 avifenc 软件路径，仅 --codec 不同
            case AvifEncoderKind.Nvenc:
                return _be[AvifEncoderBackend.Nvenc];
            case AvifEncoderKind.Qsv:
                return _be[AvifEncoderBackend.Qsv];
            default: // Avifenc：由 avifenc 自选内部编码器
                return _be[AvifEncoderBackend.LibAom];
        }
    }

    /// <summary>
    /// ═══ 2026-09-17 新增：HDR 路径的 avifenc <c>--codec</c> 取值 ═══
    /// <para>HDR 分支不构造后端实例（硬件后端在 HDR 下不可用），但仍要尊重用户在
    /// 「AVIF 编码器」下拉里做的<b>软件</b>编码器选择，故这里只做纯映射，不触发任何探测。</para>
    /// <para>为什么不复用 <see cref="SelectByKind"/>：
    /// ① 它会在 Auto 下调用 <c>GpuCapability.DetectEncoders()</c> 与 NVENC/MFT 的原生可用性探测
    ///    （P/Invoke 崩溃在 .NET 上不可捕获，见 NativeEncoderGuard），而 HDR 根本用不上硬件；
    /// ② 它的"放宽"分支可能返回 NVENC / Qsv 后端实例，本路径拿了也无法使用；
    /// ③ <see cref="LibAomAvifBackend"/> 的 codec 是私有字段，靠类型判断去取既绕又脆。</para>
    /// <para>返回值交给 <c>NativeAvifEncoder.GetCodecArg</c>：SvtAv1 在本机 avifenc 未编译 SVT 时
    /// 会被自动降级为空串（回落到默认编码器），不会导致编码失败。</para>
    /// </summary>
    public static string ResolveAvifencCodec(AvifEncoderKind kind) => kind switch
    {
        AvifEncoderKind.LibAom => "LibAom",
        AvifEncoderKind.SvtAv1 => "SvtAv1",
        // Auto / Avifenc 由 avifenc 自选；Nvenc / Qsv 在 HDR 下不支持，回落软件
        _ => "Auto",
    };

    /// <summary>该种类编码器在本机是否可用（供 Auto 挑选与 UI 可用性判断）。</summary>
    public static bool IsKindAvailable(AvifEncoderKind kind) => kind switch
    {
        // ═══ 2026-09-16 修复（审查 P2-4）：Nvenc 必须同时校验 SupportsAv1 ═══
        // IsAvailable 仅表示"会话可创建"（RTX 30 的 HEVC 可用即 true），不代表支持 AV1 编码。
        // 缺该过滤会让 RTX 30 及更早显卡在 Auto 下被选中 → AV1 编码失败 → 白走一次回退。
        // DetectEncoders() 结果有缓存，探测开销只发生一次。
        AvifEncoderKind.Nvenc => _be[AvifEncoderBackend.Nvenc].IsAvailable
            && GpuCapability.DetectEncoders().Any(e => e.Type == GpuEncoderType.NVENC && e.Available && e.SupportsAv1),
        AvifEncoderKind.Qsv => _be[AvifEncoderBackend.Qsv].IsAvailable,
        AvifEncoderKind.SvtAv1 => NativeAvifEncoder.IsSvtAvailable,
        // LibAom 与 Avifenc 都走 avifenc 软件路径
        _ => LibAomAvailable,
    };

    /// <summary>
    /// Auto 模式挑选实际编码器（硬件优先，只看可用性；能力不足由下游协商降级并提示）。
    /// <para>App 管线在能力协商**之前**调用它把 Auto 解析成具体 kind，
    /// 使协商与降级提示针对真实选中的编码器（审查 P1-1）。</para>
    /// </summary>
    public static AvifEncoderKind PickAvailableKind(string chroma, int bitDepth) =>
        AvifEncoderCapabilities.PickAuto(IsKindAvailable, chroma, bitDepth);
}

public sealed class LibAomAvifBackend : IAvifEncoder
{
    // ═══ P2-5 修复：软件编码器（libaom/svt）通过实例字段传递，不再写全局静态 Codec ═══
    // 原实现用 NativeAvifEncoder.Codec 全局可变字段，并发编码任务会互相覆盖
    // （SvtAv1 请求可能被并发 LibAom 请求改写）。现在由构造时解析的类型决定，
    // 且 SelectByKind 每次为软件路径新建实例（无共享可变状态）。
    private readonly string _codec;

    public LibAomAvifBackend(string codec = "Auto") => _codec = codec;
    public AvifEncoderBackend Backend => AvifEncoderBackend.LibAom;
    public bool IsAvailable => NativeAvifEncoder.IsAvailable;
    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null, CompressionEffort effort = CompressionEffort.Balanced)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var icc = (colorSpaceTag is not (null or "System" or "sRGB")) ? iccProfile : null;
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, isHdr: false, chroma: chroma, bitDepth: displayBitDepth, iccProfile: icc, colorSpaceTag: colorSpaceTag, codec: _codec, effort: effort);
        }, ct);
    }
}

/// <summary>
/// MFT AV1 编码的共享实现（审查 P3：消除 QSV 与 MFT 两份逐字重复的代码）。
/// <para>2026-09-16 起这两个后端走的是【同一条】MFT 路径 —— QSV 在 Intel 显卡上由 MFT
/// 调度到 Quick Sync，在 NVIDIA / AMD 上则调度到各自硬件编码器。原先两份近乎逐字的
/// 复制实现，任何一处修改（崩溃隔离、回退策略、临时文件处理）都极易漏改另一处，
/// 故合并为单一实现，差异仅保留"崩溃隔离标签名"一个参数。</para>
/// </summary>
file static class MftAvifEncode
{
    // ═══ 2026-09-18 P1-QSV 探测假绿：仅当真正走 MFT 硬件路径产出码流时为 true，
    //     回退 libaom 前置 false。AvifHardwareProbe 经反射读取（QSV/MFT 共用本类）。═══
    internal static volatile bool LastEncodeUsedHardware;

    /// <param name="tag">崩溃隔离与日志用的标签（"QSV_MFT" / "MFT"），便于区分两条调用来源。</param>
    public static async Task EncodeAsync(string tag, byte[] bgra, int w, int h, int crf, string path,
        CancellationToken ct, string chroma, int displayBitDepth, string? colorSpaceTag, byte[]? iccProfile,
        CompressionEffort effort)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            LastEncodeUsedHardware = false;

            // ═══ 2026-09-20：MFT/QSV 也纳入崩溃哨兵（交接 §31.3-7）═══
            // 此前哨兵只认 "NVENC" 一个 key，而 MFT 同样是**进程内** COM/P-Invoke 调用：
            // AccessViolation 一类 CSE 在 .NET 上 catch 不住（进程直接消失，NativeEncoderGuard
            // 那层对这类无效，见其文件头）⇒ 没有哨兵就等于"每次开机都在同一条路上再崩一次"。
            // key 统一用 "MFT"：QSV_MFT / MFT 两个 tag 走的是同一条原生实现，
            // 拆成两个 key 会各崩各禁，看不出是同一处崩的。
            Exception? mftFailure = EncoderCrashSentinel.IsBlocked("MFT")
                ? new InvalidOperationException(
                    "上次进程在 MFT 原生编码中崩溃 ⇒ 本次已自动改用软件编码（下次启动会再试一次）")
                : null;
            if (mftFailure is null)
            {
                EncoderCrashSentinel.Arm("MFT");
                try
                {
                    // ═══ P/Invoke / COM 崩溃隔离：CSE (AccessViolation/SEH) 安全 ═══
                    var result = NativeEncoderGuard.TryEncode(tag, () =>
                    {
                        using var mft = new MftEncoderNative(w, h, useAv1: true);
                        return mft.Encode(bgra);
                    });

                    if (result.Success)
                    {
                        LastEncodeUsedHardware = true;
                        IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag, iccProfile);
                        return;
                    }
                    mftFailure = result.Error;
                }
                finally
                {
                    // 能执行到这里 = 原生调用已经返回（进程还活着）→ 解除哨兵。
                    // 若途中 0xC0000005，本行不执行，哨兵残留 ⇒ 下次启动禁用 MFT。
                    EncoderCrashSentinel.Disarm("MFT");
                }
            }

            CoreLog.Warn($"AVIF {tag}",
                $"MFT 硬件编码失败（{mftFailure?.GetType().Name}: {mftFailure?.Message}），回退 libaom",
                LogCategory.Encoding);
            // ═══ 2026-09-19 留痕（只记录，不改行为）═══
            // 这条回退原先只有 Debug.WriteLine，Release 下完全无声：4K 上软件约 22s vs 硬件约 0.7s
            //（见 AvifEncoderCapabilities 同一组实测数字），用户只会觉得"变慢了"，既不知原因也不易发现。
            // 不复用 NvencAvifBackend.LastFallbackReason —— 那是 NVENC 专用语义（见 EncoderFallbackNotifier）。
            EncoderFallbackNotifier.LastReason =
                $"系统 MFT 硬件编码（{tag}）本次失败：{mftFailure?.Message} —— 已回退 libaom 软件编码，"
                + "文件正常但会明显变慢";
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
        }, ct);
    }
}

public sealed class QsvAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Qsv;

    /// <summary>
    /// ═══ 2026-09-16 实现：改走 Windows Media Foundation (MFT) ═══
    /// <para>原实现依赖自造的 oneVPL P/Invoke 结构体（QsvEncoderNative），
    /// 与 Intel 官方头文件定义不符（忽略 reserved[] 与 union 布局），
    /// 参数会写进保留区，因此被刻意停用、恒返回 false —— 用户选了 QSV 也只会静默落到 libaom。</para>
    /// 现改为 MFT：微软维护的标准硬件编码 COM 接口。
    ///  · 在 Intel 显卡上，MFT 会调度到 Quick Sync —— 即名副其实的 QSV；
    ///  · 在 NVIDIA / AMD 上则调度到各自的硬件编码器，一条路径覆盖所有厂商；
    ///  · 不需要自造结构体，不受 oneVPL 头文件版本差异影响。
    /// 系统没有 AV1 MFT 时 IsAvailable 为 false，仍会正常回退 libaom。
    /// <para>2026-09-19：<c>QsvEncoderNative.cs</c> 经确认已零调用方（IsAvailable 恒 false 且无人引用），
    /// 判为死代码待删；上面那段"自造结构体布局 = 参数写进 reserved 区"的教训同时抄进了
    /// <c>MftEncoderNative.cs</c> 顶部 —— 删文件不会丢知识，别把它当成"还可以再抄一条 QSV 直通"的模板。</para>
    /// </summary>
    public bool IsAvailable => MftEncoderNative.IsAv1MftAvailable;

    public Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null, CompressionEffort effort = CompressionEffort.Balanced)
        => MftAvifEncode.EncodeAsync("QSV_MFT", bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile, effort);
}

public sealed class NvencAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Nvenc;

    /// <summary>
    /// NVENC 接受 <c>NV_ENC_BUFFER_FORMAT_ARGB10</c> 输入 ⇒ 源精度可保到 10-bit。
    /// <para>2026-09-17 运行时实测：驱动对 AV1 报告的输入格式含 ARGB10 / ABGR10 /
    /// YUV420_10BIT，且 <c>NV_ENC_CAPS_SUPPORT_10BIT_ENCODE=1</c>；阶梯判据进一步确认
    /// 解码亮度是真 10-bit 斜坡（8-bit 输入只有 4 级台阶）。</para>
    /// <para>⚠ 这是"典型能力"而非运行时查询：某些驱动/显卡组合可能不接受 ARGB10。
    /// 那种情况由 <see cref="TryEncodeNvencArgb10"/> 失败后回退 8-bit 通路兜住 ——
    /// 退化的是<b>源精度</b>（回到与现状一致），不是容器位深，也不会退回软件编码。</para>
    /// </summary>
    public bool SupportsTrue10BitInput => true;

    // ═══ 2026-08-25 P2 性能优化: NVENC 失败缓存 ═══
    // 纹理路径失败后缓存结果, 避免后续每次调用都重试纹理直通路径.
    // 场景: 用户第一次截图纹理路径失败 (设备/驱动状态), 后续每次仍重试 → 浪费.
    private static volatile bool s_texturePathFailed;
    private static volatile bool s_nvencFailed;

    public bool IsAvailable
    {
        get
        {
            try
            {
                if (s_nvencFailed) return false; // 快速短路: 上次已失败
                var avail = NvEncoderNative.IsAvailable;
                CoreLog.Trace("AVIF", () => $"NVENC IsAvailable: {avail}", LogCategory.Encoding);
                return avail;
            }
            catch (Exception ex)
            {
                CoreLog.Warn("AVIF", "NVENC 检测异常，本次判定为不可用并缓存失败", ex, LogCategory.Encoding);
                s_nvencFailed = true;
                return false;
            }
        }
    }

    /// <summary>重置失败缓存 (驱动恢复/显示器切换时调用)。</summary>
    public static void ResetFailureCache()
    {
        s_texturePathFailed = false;
        s_nvencFailed = false;
    }

    // 缓存的 D3D11 设备 (避免每次编码创建新设备)
    private static ID3D11Device? s_cachedD3DDevice;
    private static readonly object s_deviceLock = new();

    // ⚠️ 用 volatile 字段承载，而不是自动属性：
    //    本开关由 UI 线程（设置页）写入、由编码线程池读取。bool 读写虽是原子的，
    //    但缺少易失性语义时读取方可能长期看到缓存中的旧值 ——
    //    表现为"用户改了设置却不生效"，且极难复现。
    private static volatile bool s_hardwareEncodingEnabled = true;

    /// <summary>
    /// 是否允许使用 NVENC 硬件编码。由 App 从用户设置注入（Core 不反向依赖 App 设置）。
    ///
    /// 2026-09-15 新增：NVENC 的原生崩溃无法被 catch（见 EncoderCrashSentinel 说明），
    /// 除了崩溃后自动降级，还需要给用户一个【主动开关】——
    /// 用于主动回避反复崩溃，也用于重新启用（勾选时清除哨兵）。
    /// </summary>
    public static bool HardwareEncodingEnabled
    {
        get => s_hardwareEncodingEnabled;
        set => s_hardwareEncodingEnabled = value;
    }

    /// <summary>设置共享 D3D11 设备 (由 WgcCaptureService 或 MainWindow 调用)。</summary>
    public static void SetSharedD3DDevice(ID3D11Device device)
    {
        lock (s_deviceLock)
        {
            s_cachedD3DDevice?.Dispose();
            s_cachedD3DDevice = device;
            ResetFailureCache(); // 新设备 = 重置缓存
        }
    }

    /// <summary>
    /// 通知：外部持有的共享 D3D11 设备【已被释放】，请丢弃缓存引用。
    ///
    /// 2026-09-15 修复（P1-8 悬垂设备）：WgcCaptureService 在换显示器时会 Dispose 旧设备，
    /// 但此前并不通知本类 —— 于是 s_cachedD3DDevice 仍指向已释放的 COM 对象，
    /// 下次 AVIF 编码用它做 NVENC 纹理路径 → 原生访问违规。
    /// 而 P/Invoke 层的崩溃在 .NET 上【不可捕获】（见 NativeEncoderGuard 注释），
    /// 结果是整个进程被杀死，而不是回退到 libaom。
    ///
    /// ⚠️ 这里只清引用，【不】调用 Dispose —— 设备所有权在调用方，且此刻通常已释放。
    /// </summary>
    public static void NotifySharedDeviceDisposed()
    {
        lock (s_deviceLock)
        {
            if (s_cachedD3DDevice is null) return;
            CoreLog.Info("AVIF", "共享 D3D11 设备已失效，丢弃缓存引用并重置失败缓存", LogCategory.Encoding);
            s_cachedD3DDevice = null;
            ResetFailureCache();
        }
    }

    /// <summary>NVENC 编码的最小边长（像素）。</summary>
    /// <remarks>
    /// 实测 96×96 触发 hr=0x00000008
    /// "Frame dimensions are less than the minimum supported value"。
    /// 取 128 留有余量：低于该值直接走软件路径（小图用 libaom 编码很快，代价可忽略）。
    /// 精确下限随驱动/显卡代次而异，故用保守值而非临界值。
    /// </remarks>
    internal const int MinNvencDimension = 128;   // internal: 供 AvifHardwareProbe 校验样张尺寸

    /// <summary>
    /// 上一次编码是否**真的**走了 NVENC（而不是中途回退到 libaom）。
    /// <para>为什么需要它：探测（AvifHardwareProbe）原先只检查"是否产出了有效 AVIF 文件"，
    /// 但本后端任何一步失败都会回退 libaom 并照样写出有效文件 —— 于是探测**恒为绿色**，
    /// 对外报告"NVENC 可用"，实际可能一次都没用过硬件。</para>
    /// 置位：仅在真正由 NVENC 产出码流后。
    /// </summary>
    internal static volatile bool LastEncodeUsedHardware;

    /// <summary>
    /// 上一次编码"本可用硬件、却退到软件"的原因（正常用上硬件时为 null）。
    /// <para>为什么要暴露它：硬件回退软件会让 4K AVIF 从约 0.12s 变成约 22s（约 180 倍），
    /// 而原实现全程只有 Debug.WriteLine —— Release 下完全无声，
    /// 用户只会觉得"软件变慢了"，既不知道原因，也不知道能否恢复。</para>
    /// 上层应在保存完成后读取本属性并给出一次性提示；读取后请置 null，避免重复打扰。
    /// 注：仅对"用户预期硬件可用"的场景置位；用户主动关闭硬件开关、或图像过小
    ///（软件编码本来就没损失）的场景不置位，以免制造噪音。
    /// </summary>
    // public：App 层（CapturePipelineService）需要读取它来提示用户。
    // 注意 Core 的 InternalsVisibleTo 只授予了 Test 项目，App 看不到 internal 成员。
    public static volatile string? LastFallbackReason;   // volatile：跨线程读写（编码线程写，UI 读）

    /// <summary>
    /// NVENC 请求非 4:2:0 时的降级说明；请求本身就是 420（或没给）时返回 null。
    /// <para>背景（2026-09-19，交接 §26.4 P2-3）：<c>NvEncoderNative</c> 里的
    /// <c>Av1BitfieldWord</c>（chromaFormatIDC 所在字）只声明了偏移、从不写入，驱动于是恒按 4:2:0 编码 ——
    /// 实测请求 444 时产出的码流序列头仍是 4:2:0，<b>而且不报错</b>（交接 §26.2 的色度扫描）。
    /// 产品路径通常到不了这里：<c>BuildEncodingSettings</c> 已按能力表把 444 协商成 420 并提示过；
    /// 本函数服务于"后端被直接调用"的那一类（Tools、未来调用点），让静默改参数留下痕迹。
    /// 只记录、不改行为。</para>
    /// </summary>
    internal static string? ChromaDowngradeNote(string? chroma) =>
        chroma is "420" or null or "" ? null
            : $"NVENC 恒按 4:2:0 编码（驱动侧不写 chromaFormatIDC）⇒ 请求的 {chroma} 未生效";

    /// <summary>
    /// 判断 NVENC 失败是否属于"本次输入不适用"（区别于"硬件不可用"）。
    /// </summary>
    /// <remarks>
    /// 驱动对"这张图编不了"会给出明确的尺寸/分辨率类错误；这类失败不应导致永久禁用硬件编码
    /// （否则后续大图也会退到软件路径，实测慢约 180 倍）。
    /// 未匹配到已知特征时按"硬件不可用"处理 —— 保守方向是禁用，避免每张图都白试一次。
    /// </remarks>
    internal static bool IsInputSpecificNvencFailure(Exception? ex)   // internal: 供测试做分类回归
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message;
            if (string.IsNullOrEmpty(msg)) continue;
            if (msg.Contains("dimensions are less than the minimum", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("minimum supported value", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("unsupported resolution", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("resolution is not supported", StringComparison.OrdinalIgnoreCase)
             // ═══ 2026-09-17：buffer 格式/色度类失败（如非 Blackwell 卡请求 NV16 4:2:2）
             // 也属"本次输入不适用"——只该跳过 422 路径，不应永久禁用整个 NVENC（否则
             // 后续所有 420 截图都退回 22s 级软件编码，且用户无从察觉）。
             || msg.Contains("unsupported param", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("no encode capability", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("buffer format", StringComparison.OrdinalIgnoreCase)
             || msg.Contains("input format", StringComparison.OrdinalIgnoreCase)
             // ═══ 2026-09-24：CRF 0（AV1 的 QP0 = 请求 lossless）实测签名 ═══
             // 本机 RTX 4060：InitEncoder hr=0x00000008 lastErr="Lossless Coding mode not
             // supported." ⇒ 这是"这个参数组合硬件给不出"，不是"这张卡的编码器坏了"。
             // 漏掉它的代价被实测放大过一整轮：一次 CRF 0 失败会把 s_nvencFailed 置位，
             // 此后【同进程所有】AVIF 都退回软件（顺序实验 --crf 2,0,5,8,15：只有 2 走了硬件），
             // 只能靠重启恢复 —— 与上面那段"一张小图废掉全部大图"是同一个病。
             || msg.Contains("Lossless Coding mode not supported", StringComparison.OrdinalIgnoreCase)
             // NV_ENC_ERR_UNSUPPORTED_PARAM (0xC)：实测 RTX 5080 上 AV1+NV16 4:2:2 在
             // EncodePicture 阶段被驱动以此码拒绝（NVENC 的 4:2:2 编码仅 HEVC 支持，
             // AV1 只支持 420）。属"该输入组合不适用"，不应永久禁用 NVENC。
             || msg.Contains("EncodePicture: 0x0000000C", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public async Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null, CompressionEffort effort = CompressionEffort.Balanced)
    {
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            // 先归零：只有真正由 NVENC 产出码流时才会被置位（供 AvifHardwareProbe 判定，见字段说明）
            LastEncodeUsedHardware = false;
            LastFallbackReason = null;

            // ═══ 用户开关 (2026-09-15) ═══
            // 用户在设置里关闭硬件加速 → 直接走软件编码，连 NVENC 会话都不打开。
            if (!HardwareEncodingEnabled)
            {
                CoreLog.Debug("AVIF", "硬件编码已在设置中禁用 → 走软件编码", LogCategory.Encoding);
                AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
                return;
            }

            // ═══ 2026-09-15 优化：最小尺寸前置判断 ═══
            // NVENC 对编码尺寸有下限。实测 96×96 返回
            //   hr=0x00000008 "Frame dimensions are less than the minimum supported value"
            // 低于下限时直接走软件路径：既省掉一次注定失败的会话创建（创建 + 初始化并不便宜），
            // 更重要的是**不污染失败缓存** —— 详见本方法末尾对 s_nvencFailed 的分类说明。
            if (w < MinNvencDimension || h < MinNvencDimension)
            {
                CoreLog.Debug("AVIF",
                    $"尺寸 {w}x{h} 低于 NVENC 最小支持边长({MinNvencDimension}) → 直接走软件编码",
                    LogCategory.Encoding);
                AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
                return;
            }

            // ═══ 崩溃哨兵 (2026-09-15) ═══
            // P/Invoke 层的访问违规在 .NET 上【不可捕获】，进程会直接终止：既没有回退机会，
            // 也来不及写任何日志（详见 NativeEncoderGuard 注释与 tests/manual/cse-probe 实测）。
            // 哨兵把判断推迟到"下次启动"：若上次是在 NVENC 崩的，本次直接禁用它走软件编码 ——
            // 否则每次启动都会走进同一条路径再崩一次，用户只能反复看到闪退而无法自救。
            if (EncoderCrashSentinel.IsBlocked("NVENC"))
            {
                CoreLog.Warn("AVIF", "NVENC 上次曾导致进程崩溃（哨兵残留）→ 本次禁用，回退 libaom", LogCategory.Encoding);
                s_nvencFailed = true;
                LastFallbackReason = "上次 NVENC 导致进程崩溃，本次已自动改用软件编码（可在设置中重新启用硬件编码）";
                AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
                return;
            }

            // 快速短路: 若 NVENC 完全不可用, 直接回退 libaom
            if (s_nvencFailed)
            {
                CoreLog.Warn("AVIF", "NVENC 已缓存失败, 直接回退 libaom", LogCategory.Encoding);
                LastFallbackReason = "硬件编码当前不可用（此前失败已被记住），已改用软件编码 —— 重启程序或重新启用硬件编码可恢复";
                AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
                return;
            }

            // 获取共享 D3D11 设备 (纹理路径 + CPU 路径共用)
            ID3D11Device? device = null;
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            bool ownDevice = false;
            if (device is null)
            {
                CoreLog.Trace("AVIF", () => "NVENC: 无共享设备，创建新 D3D11 设备", LogCategory.Encoding);
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                ownDevice = true;
            }

            try
            {
            // 进入任何 NVENC 原生调用之前武装哨兵；只要进程活着走到 finally 就解除。
            EncoderCrashSentinel.Arm("NVENC");

            // ═══ 2026-09-17 实测：硬件 10-bit 已接通（推翻早前"暂不可用"的结论）═══
            // 早前的观察是对的，但推论错了：把输入改成 ARGB10、bufferFormat 填
            // NV_ENC_BUFFER_FORMAT_ARGB10 后，驱动确实仍产出 8-bit 码流。
            // 真因是——输出位深由 NV_ENC_CONFIG_AV1.outputBitDepth 决定，与输入格式无关：
            //   · outputBitDepth 位于 NV_ENC_CONFIG + 280，inputBitDepth 在 +284；
            //   · 官方 nvEncodeAPI.h 原文："HW will do the bitdepth conversion internally
            //     from inputBitDepth -> outputBitDepth if bit depths differ. Support for
            //     8 bit input to 10 bit encode conversion only"
            // ⇒ 输入保持 8-bit ARGB 即可产出 10-bit 码流（但源精度仍只有 8-bit）。
            // ⚠ 这只是"够用"的通路。真要保住 10-bit 源精度，必须同时
            //   ① 用 ARGB10 输入（inputBitDepth 会由 ApplyAv1ColorConfig 自动设成 10）
            //   ② outputBitDepth=10
            //   二者缺一都会退化：只改输入 → 被降到 8-bit；只改输出 → 升位深。
            //   （2026-09-17 曾误记成"ARGB10 输入无效"，实测推翻，详见
            //    docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md §3）
            // 偏移量来源：本机 MSVC 对官方 nvEncodeAPI.h（SDK 13.1）逐字段 offsetof 实测，
            // 探针源码见 scripts/probes/nvoff.c（此前凭注释猜测的偏移错得离谱，详见
            // NvEncoderNative 顶部常量表）。独立验证：ffprobe 报 pix_fmt=yuv420p10le。
            //
            // 位深取值：displayBitDepth 已由上游 CapturePipelineService 用
            // NegotiateBitDepth 按能力表钳过（硬件上限 10），故此处原样传递、不再二次钳制
            // —— 多一处钳制就多一处"声称 X 实际给 Y"的静默改参数。
            // 注意默认位深是 8（ImageEncoderSettings.OutputBitDepth 默认值），
            // 只有用户显式选择 10-bit 时才会真正走到 10-bit 编码。
            int nvencBitDepth = displayBitDepth;

            // ═══ 2026-09-20：色彩意图只算一次，同时喂给"下发给驱动"与"写进容器"两处 ═══
            // 之前是两处各推各的（驱动侧压根不下发 ⇒ 恒按默认 BT.601 转，容器侧却写 BT.709），
            // 采信 nclx 的解码器会拿错的逆矩阵还原像素。同源取值才可能一致。
            // null = 给不出自洽声明（AdobeRGB）：那时两侧都退回旧行为（不下发 + 只靠 ICC）。
            var hwCicp = FormatHelper.NvencAvifCicp(hdrOutput: false, colorSpaceTag);

            // ═══ GPU 纹理直通路径 (仅当纹理可用且未缓存失败) ═══
            // ═══ 2026-09-17：chroma != "422" 现为【防御性】条件（恒真）═══
            // AvifEncoderCapabilities 已把 NVENC 的 Supports422 声明为 false，
            // 4:2:2 请求会在 CapturePipelineService 的协商里先被降级成 420，故到不了本方法
            //（下方 CPU 像素路径的 422 分派已一并移除）。
            // 仍保留该条件：纹理是 BGRA8/ARGB 打包格式，本身无法携带 4:2:2，
            // 一旦将来能力表放开 422 而此处忘了同步，422 直传纹理会被【静默产出 420】
            //（与用户请求不符且无提示）——留一道闸比删掉更安全。
            if (texture is not null && !s_texturePathFailed && chroma != "422")
            {
                var result = NativeEncoderGuard.TryEncode("NVENC_Texture", () =>
                {
                    using var nv = new NvEncoderNative(device!, hwCicp);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var bs = nv.EncodeAv1FromTexture(texture, w, h, crf, nvencBitDepth, effort);
                    CoreLog.Debug("AVIF", $"✅ NVENC 纹理直通: {w}x{h} CRF={crf} {nvencBitDepth}-bit {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB", LogCategory.Encoding);
                    return bs;
                });
                if (result.Success)
                {
                    LastEncodeUsedHardware = true;   // 纹理直通确实由 NVENC 产出
                    IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag, iccProfile,
                        hwCicp: hwCicp);
                    return;
                }
                CoreLog.Warn("AVIF", $"NVENC 纹理路径失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，回退 CPU 像素路径", LogCategory.Encoding);

                // ═══ 2026-09-15：与 s_nvencFailed 同样的分类处理 ═══
                // 原实现无条件缓存失败，此后**所有**图都不再尝试纹理直通
                // （GPU 纹理直送 NVENC，可省掉一次 CPU 回读）。
                // 但纹理路径失败也可能是"这张图的纹理特性不适用"（如尺寸低于硬件下限），
                // 与"显卡/驱动不支持纹理输入"是两回事，混为一谈同样会造成
                // "一次偶发失败 → 全局永久退化"。分类依据与 s_nvencFailed 保持一致。
                if (IsInputSpecificNvencFailure(result.Error))
                {
                    CoreLog.Debug("AVIF", "属【本次输入不适用】，不禁用纹理路径（后续仍尝试纹理直通）", LogCategory.Encoding);
                }
                else
                {
                    s_texturePathFailed = true; // 硬件/驱动层面不支持纹理输入，缓存失败
                }
            }

            // ═══ CPU 像素路径 (纹理不可用/纹理路径失败时使用) ═══
            var result2 = NativeEncoderGuard.TryEncode("NVENC", () =>
            {
                using var nv = new NvEncoderNative(device!, hwCicp);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // ═══ 2026-09-17：移除恒不可达的 4:2:2 分派 ═══
                // 原写法 `chroma == "422" ? EncodeAv1_422(...) : EncodeAv1(...)` 的 422 分支恒不可达：
                //   ① AvifEncoderCapabilities 已把 NVENC 的 Supports422 声明为 false；
                //   ② 4:2:2 请求会在 CapturePipelineService 的协商里被降级成 420，
                //      PickAuto 也不会为满足 422 而选中 NVENC ⇒ 调用方拿到的 chroma 恒非 422。
                // 该分支既是死代码，又制造了"实现支持 422 / 能力表声明不支持"的自相矛盾。
                // ⚠ 若将来 NVENC 的 AV1 真支持 4:2:2，必须【同时】改两处，只改一处会重新打架：
                //   ① AvifEncoderCapabilities 中 NVENC 条目的 Supports422；
                //   ② 本处、下方 N 卡设备重试路径，以及上方纹理路径的 chroma != "422" 守卫。
                // 位深：由 outputBitDepth 决定（见上方 2026-09-17 注释），输入恒为 8-bit ARGB，
                // 硬件在内部完成 8→10 转换，不需要把像素先扩成 16-bit。
                var bs = nv.EncodeAv1(bgra, w, h, crf, nvencBitDepth, effort);
                CoreLog.Debug("AVIF", $"✅ NVENC AV1: {w}x{h} 色度={chroma} CRF={crf} {nvencBitDepth}-bit {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB", LogCategory.Encoding);
                return bs;
            });

            // ═══ 2026-08-31: 共享设备失败时改用 N 卡专用设备重试 ═══
            // 混合显卡系统（Intel 核显 + NVIDIA 独显）上，WGC 注入的共享设备可能位于核显，
            // 无法打开 NVENC 会话 → 此前直接回退软件编码，4K AVIF 需 20+ 秒。
            // CPU 像素路径不依赖纹理所属设备，故可改用一块真正的 N 卡设备重试。
            if (!result2.Success && !ownDevice)
            {
                CoreLog.Warn("AVIF",
                    $"NVENC 共享设备失败 ({result2.Error?.Message})，尝试 N 卡专用设备",
                    LogCategory.Encoding);
                using var nvDevice = NvEncoderNative.CreateNvidiaDevice();
                if (nvDevice is not null)
                {
                    var result3 = NativeEncoderGuard.TryEncode("NVENC_Nvidia", () =>
                    {
                        using var nv = new NvEncoderNative(nvDevice, hwCicp);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        // 同上方共享设备路径（2026-09-17）：422 分派已移除，统一 EncodeAv1。
                        // 若日后恢复 422，此处必须与能力表的 Supports422 同步放开（不可只改一处）。
                        var bs = nv.EncodeAv1(bgra, w, h, crf, nvencBitDepth, effort);
                        CoreLog.Debug("AVIF",
                            $"✅ NVENC AV1 (N 卡设备): {w}x{h} 色度={chroma} CRF={crf} {nvencBitDepth}-bit {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB",
                            LogCategory.Encoding);
                        return bs;
                    });
                    if (result3.Success)
                    {
                        LastEncodeUsedHardware = true;   // N 卡设备路径确实由 NVENC 产出
                        LastFallbackReason = ChromaDowngradeNote(chroma);
                        IvfWriter.WriteAvif(result3.Value!, w, h, path, colorSpaceTag, iccProfile,
                            hwCicp: hwCicp);
                        return;
                    }
                    CoreLog.Warn("AVIF",
                        $"NVENC N 卡设备亦失败: {result3.Error?.Message}", LogCategory.Encoding);
                }
            }

            if (result2.Success)
            {
                LastEncodeUsedHardware = true;   // CPU 像素路径也确实由 NVENC 编码（只是输入来自 CPU）
                LastFallbackReason = ChromaDowngradeNote(chroma);
                IvfWriter.WriteAvif(result2.Value!, w, h, path, colorSpaceTag, iccProfile,
                    hwCicp: hwCicp);
                return;
            }

            CoreLog.Warn("AVIF", $"NVENC 失败 ({result2.Error?.GetType().Name}: {result2.Error?.Message})，回退 libaom", LogCategory.Encoding);

            // ═══ 2026-09-15 优化：失败必须分类，不能一律永久禁用 ═══
            // 原实现无条件 s_nvencFailed = true。但失败原因其实是两类：
            //   ① 硬件/驱动不可用（初始化失败、无编码能力、设备丢失）
            //      → 应当缓存并永久禁用，否则每张图都要白试一次；
            //   ② 本次输入不适用（尺寸低于最小支持、该分辨率不支持等）
            //      → 只说明【这一张】不该走 NVENC，显卡本身是好的。
            // 若把 ② 也永久禁用，一张小图失败后，后续所有 4K 大图都会退回 libaom：
            // 实测 4K AVIF 硬件 ~0.12s vs 软件 ~22s（约 180 倍差距），
            // 而用户只会觉得"软件变慢了"，完全无从察觉原因 —— 只能靠重启恢复。
            var inputSpecific = IsInputSpecificNvencFailure(result2.Error);
            if (inputSpecific)
            {
                CoreLog.Debug("AVIF", "属【本次输入不适用】，不禁用 NVENC（后续大图仍走硬件）", LogCategory.Encoding);
            }
            else
            {
                s_nvencFailed = true; // 硬件/驱动层面不可用，缓存失败，后续不再尝试
            }
            // 失败原因必须带原始异常信息，否则日志只有固定文案，无法远程诊断 hr 代码
            LastFallbackReason = (inputSpecific
                ? "当前图像不适用于硬件编码，已改用软件编码（不影响后续截图）"
                : "硬件编码初始化/编码失败，已改用软件编码 —— 更新显卡驱动后通常可恢复")
                + $" 原因: {result2.Error?.GetType().Name}: {result2.Error?.Message}";
            AvifFallbackHelper.FallbackToLibAom(bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile);
            }
            finally
            {
                // 能走到这里 = 所有 NVENC 原生调用已正常返回（进程没崩） → 解除哨兵。
                // 若进程在编码途中被 0xC0000005 杀死，本行不会执行，哨兵残留，
                // 下次启动即由上面的 IsBlocked 判定并降级。
                EncoderCrashSentinel.Disarm();

                // 自己创建的设备用完释放 (共享设备由调用方管理)
                if (ownDevice && device is not null)
                {
                    lock (s_deviceLock) { if (s_cachedD3DDevice == null) s_cachedD3DDevice = device; else device.Dispose(); }
                }
            }
        }, ct);
    }

    /// <summary>
    /// 以**真 10-bit 输入**（A2R10G10B10）编码 AV1。
    /// <para>与 <see cref="EncodeAsync"/> 的区别：后者输入恒为 8-bit，10-bit 输出是硬件
    /// 升位深（源精度仍 8-bit，实测解码亮度为 4 级台阶）；本方法把 10-bit 像素原样送入
    /// NVENC，实测产出真 10-bit 斜坡。详见
    /// <c>docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md</c> §3。</para>
    /// </summary>
    /// <remarks>
    /// <b>刻意不复用 EncodeAsync 的重试矩阵</b>：那条路径的哨兵 / 失败缓存 / libaom 回退
    /// 已经过大量实测打磨，在这里复制一份只会制造第二处事实源。
    /// 本方法只负责"能不能用真 10-bit 编出来"；<b>任一环节失败就整体交回
    /// <see cref="EncodeAsync"/></b>（按 8-bit 输入重来），所有兜底逻辑仍只有一份。
    /// </remarks>
    public async Task EncodeArgb10Async(byte[] argb10, int w, int h, int crf, string path, CancellationToken ct,
        string chroma = "420", int displayBitDepth = 10, string? colorSpaceTag = null, byte[]? iccProfile = null)
    {
        bool ok = await Task.Run(() =>
            TryEncodeNvencArgb10(argb10, w, h, crf, path, ct, colorSpaceTag, iccProfile), ct);
        if (ok)
        {
            // 真 10-bit 通路成功也要记色度降级：TryEncodeNvencArgb10 不接 chroma 形参
            //（驱动恒 420），而调用方请求的 444 同样不会生效。??= 是为了不盖掉更具体的原因。
            LastFallbackReason ??= ChromaDowngradeNote(chroma);
            return;
        }

        // 真 10-bit 通路没走成 ⇒ 退回成熟的 8-bit 通路（哨兵 / 缓存 / libaom 回退都在那边）。
        // 这里把精度降到 8-bit 是**诚实的降级**：不假装保留了 10-bit 精度。
        await EncodeAsync(AvifPixelFormat.Argb10ToBgra8(argb10), w, h, crf, path, ct,
            chroma, displayBitDepth, colorSpaceTag, iccProfile);

        // ═══ 2026-09-18 P2：真 10-bit → 8-bit 输入的降级必须可感知 ═══
        // 内层 EncodeAsync 回退 libaom 等场景会写自己的 LastFallbackReason；
        // 只有它未设置原因（8-bit 通路本身成功）时才补写本通路的降级说明，
        // 否则会覆盖更具体的失败原因。
        if (LastFallbackReason is null)
            LastFallbackReason = "NVENC 真 10-bit 输入通路失败，已回退 8-bit 输入编码（容器位深仍按请求声明）";
    }

    /// <summary>尝试用 NVENC 编码 HDR（PQ 10-bit 输入）AVIF；成功写文件返回 true（2026-09-18，HDR 硬件直通）。</summary>
    /// <remarks>复用 <see cref="TryEncodeNvencArgb10"/> 的设备/哨兵/回退约定；
    /// 容器 colr 按 HDR 声明 PQ（transfer=16）。码流级 CDI 驱动不写（实测），
    /// 色彩声明完全依赖容器 —— 与 avifenc 路径同构。</remarks>
    internal static bool TryEncodeNvencHdrAvif(byte[] pq10, int w, int h, int crf, string path,
        CancellationToken ct, string? colorSpaceTag, float peakNits)
    {
        LastEncodeUsedHardware = false;

        if (!HardwareEncodingEnabled || s_nvencFailed
            || EncoderCrashSentinel.IsBlocked("NVENC")
            || w < MinNvencDimension || h < MinNvencDimension)
            return false;

        ID3D11Device? device = null;
        bool ownDevice = false;
        lock (s_deviceLock) { device = s_cachedD3DDevice; }
        if (device is null)
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            ownDevice = true;
            if (device is null) return false;
        }

        EncoderCrashSentinel.Arm("NVENC");
        // HDR(PQ) 一路的三原色/传输/矩阵是固定的一组（9/16/9，与软件路 avifenc 的 --cicp 同值）；
        // 下发给驱动 + 写进容器同一个对象，见 FormatHelper.NvencAvifCicp。
        var hwCicp = FormatHelper.NvencAvifCicp(hdrOutput: true, colorSpaceTag);
        try
        {
            ct.ThrowIfCancellationRequested();
            var result = NativeEncoderGuard.TryEncode("NVENC_HDR_PQ10", () =>
            {
                using var nv = new NvEncoderNative(device!, hwCicp);
                return nv.EncodeAv1Argb10(pq10, w, h, crf);
            });

            if (!result.Success)
            {
                CoreLog.Warn("AVIF HDR",
                    $"NVENC PQ10 失败 ({result.Error?.GetType().Name}: {result.Error?.Message})",
                    LogCategory.Encoding);
                return false;   // 不置 s_nvencFailed：失败可能仅限 HDR 通路
            }

            LastEncodeUsedHardware = true;
            IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag, iccProfile: null,
                hdrOutput: true, hwCicp: hwCicp,
                clli: FormatHelper.AvifContentLightLevel(peakNits));
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("AVIF HDR", $"NVENC 异常（{ex.GetType().Name}）", ex, LogCategory.Encoding);
            return false;
        }
        finally
        {
            EncoderCrashSentinel.Disarm();
            if (ownDevice && device is not null)
            {
                lock (s_deviceLock)
                {
                    if (s_cachedD3DDevice is null) { s_cachedD3DDevice = device; ownDevice = false; }
                }
                if (ownDevice) device.Dispose();
            }
        }
    }

    /// <summary>尝试用 NVENC 的 ARGB10 通路编码；成功写文件并返回 true，任何失败返回 false。</summary>
    private bool TryEncodeNvencArgb10(byte[] argb10, int w, int h, int crf, string path,
        CancellationToken ct, string? colorSpaceTag, byte[]? iccProfile)
    {
        LastEncodeUsedHardware = false;
        LastFallbackReason = null;

        // 前置条件不满足时直接返回 false，让调用方走 8-bit 通路 ——
        // 不在这里重复实现"禁用/回退"的分支。
        if (!HardwareEncodingEnabled || s_nvencFailed
            || EncoderCrashSentinel.IsBlocked("NVENC")
            || w < MinNvencDimension || h < MinNvencDimension)
            return false;

        ID3D11Device? device = null;
        bool ownDevice = false;
        lock (s_deviceLock) { device = s_cachedD3DDevice; }
        if (device is null)
        {
            device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            ownDevice = true;
        }

        // 真 10-bit 的 SDR 通路：色彩意图与 8-bit 路同一份算法，见 FormatHelper.NvencAvifCicp
        var hwCicp = FormatHelper.NvencAvifCicp(hdrOutput: false, colorSpaceTag);

        try
        {
            EncoderCrashSentinel.Arm("NVENC");
            ct.ThrowIfCancellationRequested();

            var result = NativeEncoderGuard.TryEncode("NVENC_ARGB10", () =>
            {
                using var nv = new NvEncoderNative(device!, hwCicp);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var bs = nv.EncodeAv1Argb10(argb10, w, h, crf);
                CoreLog.Debug("AVIF",
                    $"✅ NVENC AV1 (ARGB10 真 10-bit): {w}x{h} CRF={crf} {sw.ElapsedMilliseconds}ms {bs.Length / 1024}KB",
                    LogCategory.Encoding);
                return bs;
            });

            // 共享设备失败时，用一块真正的 N 卡设备重试（混合显卡场景）
            if (!result.Success && !ownDevice)
            {
                using var nvDevice = NvEncoderNative.CreateNvidiaDevice();
                if (nvDevice is not null)
                {
                    result = NativeEncoderGuard.TryEncode("NVENC_ARGB10_Nvidia", () =>
                    {
                        using var nv = new NvEncoderNative(nvDevice, hwCicp);
                        return nv.EncodeAv1Argb10(argb10, w, h, crf);
                    });
                }
            }

            if (!result.Success)
            {
                CoreLog.Warn("AVIF",
                    $"NVENC ARGB10 失败 ({result.Error?.GetType().Name}: {result.Error?.Message})，退回 8-bit 通路",
                    LogCategory.Encoding);
                // ⚠ 此处【不】置 s_nvencFailed：失败可能只是"10-bit 输入这一条路不通"
                //   （驱动/分辨率组合），而 8-bit 通路大概率仍可用。
                //   若在这里禁用硬件，一次 10-bit 失败就会让后续所有截图退回 22s 级软件编码 ——
                //   正是 2026-09-15 做过分类处理要避免的那种"全局永久退化"。
                return false;
            }

            LastEncodeUsedHardware = true;
            IvfWriter.WriteAvif(result.Value!, w, h, path, colorSpaceTag, iccProfile,
                hwCicp: hwCicp);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("AVIF", $"NVENC ARGB10 异常（{ex.GetType().Name}）", ex, LogCategory.Encoding);
            return false;
        }
        finally
        {
            EncoderCrashSentinel.Disarm();
            if (ownDevice && device is not null)
            {
                lock (s_deviceLock)
                {
                    if (s_cachedD3DDevice == null) s_cachedD3DDevice = device;
                    else device.Dispose();
                }
            }
        }
    }

    /// <summary>从纹理直接编码 HEVC (预留, 未来 HEIF 支持)。</summary>
    public static byte[] EncodeHevcFromTexture(ID3D11Texture2D texture, int w, int h, int qp, ID3D11Device? device = null)
    {
        if (device is null)
        {
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            if (device is null)
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        using var nv = new NvEncoderNative(device);
        return nv.EncodeHevcFromTexture(texture, w, h, qp);
    }

    /// <summary>从像素缓冲编码 HEVC (预留, 未来 HEIF 支持)。</summary>
    public static byte[] EncodeHevc(byte[] bgra, int w, int h, int qp, ID3D11Device? device = null)
    {
        if (device is null)
        {
            lock (s_deviceLock) { device = s_cachedD3DDevice; }
            if (device is null)
                device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        }
        using var nv = new NvEncoderNative(device);
        return nv.EncodeHevc(bgra, w, h, qp);
    }
}

/// <summary>MFT AV1 编码后端 — 使用 Windows 系统内置硬件编码器 (NVIDIA/Intel/AMD 自适应)。</summary>
public sealed class MftAvifBackend : IAvifEncoder
{
    public AvifEncoderBackend Backend => AvifEncoderBackend.Mft;
    public bool IsAvailable => MftEncoderNative.IsAv1MftAvailable;

    // 与 QsvAvifBackend 共用 MftAvifEncode（审查 P3 去重）：两者同为 MFT 路径，
    // 仅崩溃隔离标签不同。旧的重复实现里那份多出的 Debug.WriteLine 计时无实际价值，
    // 与 QSV 保持一致后不再保留。
    public Task EncodeAsync(byte[] bgra, int w, int h, int crf, string path, CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null, ID3D11Texture2D? texture = null, CompressionEffort effort = CompressionEffort.Balanced)
        => MftAvifEncode.EncodeAsync("MFT", bgra, w, h, crf, path, ct, chroma, displayBitDepth, colorSpaceTag, iccProfile, effort);
}

// ────── AVIF 回退辅助 ──────
file static class AvifFallbackHelper
{
    /// <summary>安全回退到 libaom 软件编码。libaom 本身也做异常隔离，防止回退链断裂。</summary>
    public static void FallbackToLibAom(byte[] bgra, int w, int h, int crf, string path,
        CancellationToken ct, string chroma = "420", int displayBitDepth = 8, string? colorSpaceTag = null, byte[]? iccProfile = null,
        CompressionEffort effort = CompressionEffort.Balanced)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var icc = (colorSpaceTag is not (null or "System" or "sRGB")) ? iccProfile : null;
            NativeAvifEncoder.Encode(bgra, w, h, path, crf, isHdr: false, chroma: chroma, bitDepth: displayBitDepth, iccProfile: icc, colorSpaceTag: colorSpaceTag, effort: effort);
        }
        catch (Exception ex)
        {
            // ═══ 2026-09-29 用户口径"保底要有开关"⇒ 换格式这一步【不在这里】做 ═══
            // 旧写法在这里直接 PNG 保底。问题是：后端层拿不到 <c>EncodingSettings</c>（接口传的是扁平参数），
            // 于是"用户是否允许换格式"这个决定在硬件链里【无法表达】——总开关做不了，只能一路兜到底。
            // 现在这里只把 libaom 的失败原样抛出去，换格式的决定统一交给
            // <c>AvifEncoder.EncodeSdrAsync</c> / <c>EncodeAsync</c> 那两处带 <c>s.PngFallbackEnabled</c> 的 catch
            // ⇒ 格式切换这件事从此只发生在"看得见用户设置"的那一层，且全仓只有一处实现。
            CoreLog.Warn("AVIF", "libaom 回退也失败 ⇒ 交给上层按用户设置决定是否改存 PNG", ex, LogCategory.Encoding);
            throw;
        }
    }
}

// ────── 辅助 ──────
public static class FormatHelper
{
    // ═══ 编码回退路径校正 (2026-09-15) ═══
    // 某些格式的末端保底会改写文件后缀（如 AVIF 全后端失败 → 保底写 .png），
    // 但编码器接口签名不返回路径，调用方仍持有原路径 ⇒ 指向一个并不存在的文件，
    // 而上层照常 Toast「已保存」、照常塞进剪贴板，用户找不到图。
    // 这里登记「请求路径 → 实际写入路径」，供管线层在编码完成后校正。
    private static readonly Dictionary<string, string> s_fallbackPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object s_fallbackLock = new();

    /// <summary>登记一次末端回退的实际落盘路径。</summary>
    internal static void RecordFallbackPath(string requestedPath, string actualPath)
    {
        lock (s_fallbackLock) s_fallbackPaths[requestedPath] = actualPath;
    }

    /// <summary>把请求路径校正为实际落盘路径；未发生回退时原样返回。</summary>
    /// <remarks>命中后即移除（一次性消费），避免长驻会话下字典无界增长。</remarks>
    public static string ResolveActualPath(string requestedPath)
    {
        lock (s_fallbackLock)
        {
            if (s_fallbackPaths.TryGetValue(requestedPath, out var actual))
            {
                s_fallbackPaths.Remove(requestedPath);
                if (File.Exists(actual)) return actual;
            }
        }
        return requestedPath;
    }

    /// <summary>
    /// 末端保底（8-bit BGRA 那一腿）：把这张图以【带色域声明】的 PNG 落盘 + 登记实际路径 + 写留痕。
    /// <para>全仓【换格式】的实现只有两份：这一个（源是量化后的 BGRA）与 <see cref="PngLastResortHdrFrameAsync"/>
    /// （源是 scRGB float 帧）。CorePipelineTests 钉的是"换后缀只许出现两次"，出现第三处就意味着有人又抄了一份
    /// 并行保底 —— 2026-09-29 F 批的教训就长这样：WebP 原先自己内联写了整套（取声明 → Encode → 写留痕 → 登记路径），
    /// 于是"promise 写在文案里、实现却只接住 <c>DllNotFoundException</c>"这种错法没有任何一处会暴露。</para>
    /// <para>调用点（2026-09-29 实测：SDR 腿 5 处、HDR 腿 3 处）＝ JPEG LI / JPEG XL / AVIF / WebP / 增益图。
    /// 2026-09-29 之前只有 AVIF 与 WebP 两格，另外三族"后端失败"的结局是图直接丢掉。
    /// AVIF 那条的【硬件链】不在这儿：它失败后只把后端改成 libaom 并抛出（<c>AvifFallbackHelper.FallbackToLibAom</c>），
    /// 换不换格式由上面那一层的 catch 决定 —— 后端那一层看不到 <see cref="EncodingSettings.PngFallbackEnabled"/>，
    /// 在它里面换格式就等于用户那个开关管不住这条路径。</para>
    /// <para>参数里没有"格式名"这种东西：日志类目 <c>who</c>、日志文案 <c>why</c>、用户可见的 <c>notice</c>
    /// 都是调用方的事实，只有【怎么落盘】这一段是各条腿共享的机制，所以只有一份。</para>
    /// </summary>
    /// <param name="who">日志类目（调用方那一族，如 "AVIF" / "WebP" / "JPEG XL"），保底失败时用它归属到正确的编码族。</param>
    /// <param name="why">写进 Warn 的原因（后端为什么失败）。</param>
    /// <param name="notice">写进 <see cref="EncoderFallbackNotifier"/> 的用户可见文案，必须说清换的是【格式】。</param>
    internal static void PngLastResort(byte[] bgra, int w, int h, string path,
        string? colorSpaceTag, byte[]? iccProfile, Exception cause, string who, string why, string notice)
    {
        CoreLog.Warn(who, why, cause, LogCategory.Encoding);
        try
        {
            var pngPath = Path.ChangeExtension(path, ".png");
            // 保底 PNG 也必须带色域声明：少了它，产物是"广色域像素 + 零色彩声明"的 PNG，
            // 查看器一律按 sRGB 解 ⇒ 系统性偏色。取法三处保底同一条，映射逻辑不在这里再写一份。
            var (pngIcc, pngCicp) = GetColorMetadata(colorSpaceTag, iccProfile);
            ManagedPngEncoder.Encode(bgra, w, h, pngPath, 8, pngIcc, pngCicp);
            CoreLog.Info(who, $"PNG 保底成功，实际落盘: {pngPath}", LogCategory.Encoding);

            // 换的是【格式】而不只是参数：只从后缀发现，用户查不到原因（体积/兼容性/下游按原后缀处理都变了）。
            // 单槽通道不许把【更早那条】吃掉：同一次编码里可能已经写过一条格式内降级（例如"MFT 失败已回退
            // libaom，会明显变慢"），直接赋值 = 用户只看见换了格式、永远不知道它还绕开过硬件后端。
            // 与 HDR 那份同样并起来，并另留一行 Warn，保证日志里两条都查得到。
            // （槽位由上层读完即清，见 CapturePipelineService.NotifyEncoderFallbackIfAny，所以这里读到的是本次的。）
            var earlier = EncoderFallbackNotifier.LastReason;
            if (!string.IsNullOrEmpty(earlier))
                CoreLog.Warn(who, $"保底换格式时覆盖了一条更早的降级留痕，已并入而非丢弃：{earlier}",
                    LogCategory.Encoding);
            EncoderFallbackNotifier.LastReason =
                string.IsNullOrEmpty(earlier) ? notice : $"{notice}｜另外：{earlier}";

            // ═══ 2026-09-15 修复：登记"实际写入路径" ═══
            // 保底写的是 .png，而调用方持有的仍是原路径 —— 该路径指向的文件
            // 根本不存在，但上层照常返回它，于是 Toast / 剪贴板 / 归档都指向空文件，
            // 用户看到"✅ 已保存"却找不到图。登记后由 ResolveActualPath 校正。
            RecordFallbackPath(path, pngPath);
        }
        catch (Exception pngEx)
        {
            CoreLog.Error(who, $"{who} 的 PNG 保底也失败 ⇒ 这张图确实丢了", pngEx, LogCategory.Encoding);
            throw new AggregateException($"{who} 编码与 PNG 保底均失败", cause, pngEx);
        }
    }

    /// <summary>
    /// <see cref="PngLastResort"/> 的 HDR 版本：源是 scRGB float 帧，保底必须走【产品自己的 PNG 编码器】，
    /// 而不是在这里再算一遍"PQ 该怎么写、cICP 该是哪几个字节"。
    /// <para>为什么必须另开一条而不是复用 8-bit 那份：JXL / 增益图的 HDR 路给的是 <see cref="HdrFrameData"/>
    /// 而不是量化后的 BGRA，硬压成 8-bit PNG 等于在保底里再做一次【动态范围】降级，而那句降级
    /// 早就由 <c>PngEncoder.EncodeAsync</c> 自己管（<c>:30-37</c>：请求 8-bit 时它自己退回 SDR 并留痕）。
    /// 所以这里只把帧原样交给 PNG 编码器，声明/位深/传递函数一律由它决定 ⇒ 全仓仍只有一处懂"PNG 怎么写"。</para>
    /// <para>换后缀、登记实际落盘路径、写用户可见留痕这三件事与非 HDR 版同一条纪律。</para>
    /// </summary>
    internal static async Task PngLastResortHdrFrameAsync(HdrFrameData f, EncodingSettings s, string path,
        Exception cause, string who, string why, string notice, CancellationToken ct = default)
    {
        CoreLog.Warn(who, why, cause, LogCategory.Encoding);
        var pngPath = Path.ChangeExtension(path, ".png");
        // 进保底先把这一槽清掉：下面 PNG 编码器自己写的留痕（例如"请求 8-bit ⇒ HDR 退回 SDR"）
        // 必须能被认出来是【本次】写的，而不是上一次编码留下的残值 —— 单槽通道只能靠约定清。
        EncoderFallbackNotifier.LastReason = null;
        try
        {
            await EncoderFactory.Create(OutputFormat.PNG).EncodeAsync(f, s, pngPath, ct);
            CoreLog.Info(who, $"PNG 保底（HDR 帧）成功，实际落盘: {pngPath}", LogCategory.Encoding);
            // 两条留痕都要说：换格式是用户【一定】要知道的，而 PNG 编码器内部又改了动态范围
            // （请求 8-bit ⇒ PQ 装不下 ⇒ 退回 SDR）同样不能吞 —— 单槽通道就把它接在后面并起来。
            var inner = EncoderFallbackNotifier.LastReason;
            EncoderFallbackNotifier.LastReason = string.IsNullOrEmpty(inner) ? notice : $"{notice}｜另外：{inner}";
            RecordFallbackPath(path, pngPath);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception pngEx)
        {
            CoreLog.Error(who, $"{who} 的 PNG 保底（HDR 帧）也失败 ⇒ 这张图确实丢了", pngEx, LogCategory.Encoding);
            throw new AggregateException($"{who} 编码与 PNG 保底均失败", cause, pngEx);
        }
    }

    /// <summary>路径中禁止出现的字符（会破坏命令行引号配对）。</summary>
    private const string InvalidPathChars = "\"\n\r\0";

    /// <summary>校验传给原生命令行编码器的路径参数，防止命令行引号被破坏。
    /// <para>
    /// 原生编码器（avifenc / cwebp）通过 Process.Start 拼接命令行，路径若含
    /// 引号/换行会破坏参数引号配对，导致参数注入或编码到错误路径。
    /// Windows 文件名本身不允许这些字符，正常路径不会被误伤。
    /// </para>
    /// </summary>
    public static void ValidateNativePath(string path, string paramName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException($"路径参数为空: {paramName}");
        // 用 span 重载（IndexOfAny 的定参重载最多支持 3 个）
        if (path.AsSpan().IndexOfAny(InvalidPathChars) >= 0)
            throw new ArgumentException($"路径参数含非法字符（引号/换行/空字符）: {paramName}");
    }

    /// <summary>
    /// SDR 浮点链的 8-bit 量化出口（2026-09-19）：与 <see cref="PrepareSdr16Pixels"/> /
    /// <see cref="PrepareSdrArgb10Pixels"/> 共用 <see cref="PrepareSdrLinearFloat"/> 的
    /// 色域矩阵与 tag/ICC 记账，差别只有最后的量化位数。
    /// <para>⚠ 必须先取准备段的结果、再读 <c>s.ColorSpaceTag</c>：准备段会就地记账标签
    /// （sRGB/System → "sRGB"，广色域保留），色调映射的亮度权重要按<b>目标</b>色域取。</para>
    /// </summary>
    public static byte[] ToSdr(HdrFrameData f, EncodingSettings s)
    {
        var linear = PrepareSdrLinearFloat(f, s);
        return Processing.ToneMapper.FloatToSRgbBytes(
            linear, f.Width, f.Height, s.ToneMappingParams, s.ColorSpaceTag);
    }

    /// <summary>
    /// HDR→SDR 降级辅助 (2026-08-09): 将 HDR 帧色调映射为 SDR BGRA8 并改写 settings 为 SDR 路径。
    /// 统一 PNG/JXL/AVIF/TIFF/JPEG_LI/WebP 编码器中的重复降级模式。
    /// <para>2026-09-19 解耦：本函数与 <see cref="PrepareSdr16Pixels"/> /
    /// <see cref="PrepareSdrArgb10Pixels"/> 是同一条浮点链的<b>三个量化出口</b>，
    /// 差别只有最后的量化位数 —— 色域矩阵、色调映射、gamma 一律照做。
    /// 因此<b>不再</b>无条件把 ColorSpaceTag 覆写成 "sRGB"（那等于声明"像素在 sRGB"，
    /// 而像素其实已被矩阵搬到目标色域）：标签与 ICC 兜底由
    /// <see cref="PrepareSdrLinearFloat"/> 统一记账（sRGB/System 仍覆写成 sRGB，既有行为不变）。</para></summary>
    public static byte[] ToSdrAndDegrade(HdrFrameData f, EncodingSettings s)
    {
        var d = ToSdr(f, s);
        s.HdrOutput = false;
        return d;
    }

    /// <summary>
    /// SDR 浮点链的<b>唯一准备段</b>（2026-09-19 解耦）：色域矩阵（按 tag）→ 目标色域线性 float[]。
    /// <para>为什么要有它：此前"做不做色域矩阵"是由<b>输出位深</b>（<c>OutputBitDepth ≥ 10</c>）
    /// 顺带决定的 —— 16-bit 走 PrepareSdr16Pixels（有矩阵），8-bit 走 ToSdr（无矩阵）。
    /// 于是用户选 8-bit 就同时丢掉了矩阵、色调映射后的一致 gamma 与广色域声明，
    /// "输出容器位深"和"内部精度链"两件事被一个条件绑死。现在二者彻底分开：
    /// 精度链恒为 <c>矩阵 → 色调映射 → gamma → 量化一次</c>，<c>OutputBitDepth</c> 只管最终容器。</para>
    /// <para>副作用（tag / ICC 记账，所有量化出口共用这一份事实源）：
    /// 广色域 tag（BT2020/DisplayP3/DCI_P3/AdobeRGB）→ <b>保留</b> tag（像素已在该色域，
    /// 容器必须同色域声明），并在捕获 profile 不可信时内嵌标准 ICC 兜底；
    /// sRGB/System → 覆写为 "sRGB"（既有行为）。<see cref="ColorManagement.ColorSpaceConverter.GetMatrix"/>
    /// 对 sRGB 返回 null（单位阵），跳过矩阵与"乘单位阵"结果等价。</para>
    /// </summary>
    /// <returns>目标色域的线性 float[]（可能就是 <c>f.Pixels</c> 本身，调用方只读不改）。</returns>
    /// <remarks>internal 而非 private：GainMap 的 <b>HDR 关闭</b>那条降级路（普通 JPEG，
    /// 没有 Ultra HDR 契约）必须走同一次矩阵，否则"同一个目标色域，浮点源不搬、8-bit 源搬"。
    /// ⚠ GainMap 的 HDR 主路径【故意不调它】—— base layer 恒 sRGB 是 Ultra HDR 的格式约定。</remarks>
    internal static float[] PrepareSdrLinearFloat(HdrFrameData f, EncodingSettings s)
    {
        if (s.ColorSpaceTag is "BT2020" or "DisplayP3" or "DCI_P3" or "AdobeRGB")
        {
            var matrix = ColorManagement.ColorSpaceConverter.GetMatrix(s.ColorSpaceTag);
            var converted = ColorManagement.ColorSpaceConverter.ConvertScrgbToTarget(
                f.Pixels, f.Width, f.Height, matrix);
            // ICC 兜底：拿不到结构完整的 ICC 就换成目标色域的标准 profile（切片 3）
            // ⚠ 这里判的是"能不能声明"，不是"声明得对不对色域"——后者由上面的 GetMatrix 负责。
            if (!IsEmbeddableIcc(s.IccProfile))
            {
                var std = ColorManagement.ColorProfileProvider.GetStandardIccProfile(s.ColorSpaceTag);
                if (IsEmbeddableIcc(std)) s.IccProfile = std;
            }
            return converted;
        }
        // sRGB/System：单位阵 ⇒ 无需拷贝，直接进色调映射
        s.ColorSpaceTag = "sRGB";
        return f.Pixels;
    }

    /// <summary>
    /// 16-bit SDR 通路的像素准备：浮点链（矩阵 + 色调映射 + gamma）→ 16-bit 大端 BGRA。
    /// <para>2026-09-19：矩阵段已收敛到 <see cref="PrepareSdrLinearFloat"/>，与 8-bit /
    /// ARGB10 两条路同源，这里只保留"量化到 16-bit 容器"这一步。</para>
    /// 副作用（同 ToSdrAndDegrade）：s.HdrOutput=false 由调用方或本方法置位。
    /// </summary>
    /// <param name="quantizeBits">透传给 <see cref="Processing.ToneMapper.FloatToBgra16Bytes"/>：
    /// 中转给会再按位深读一次的编码器（avifenc）时传请求位深，避免二次量化；
    /// 直接落盘（PNG sBIT / TIFF BPS16 / JXL 16-bit 输入）时用默认 16。</param>
    public static byte[] PrepareSdr16Pixels(HdrFrameData f, EncodingSettings s, int quantizeBits = 16)
    {
        var linear = PrepareSdrLinearFloat(f, s);
        s.HdrOutput = false;
        return Processing.ToneMapper.FloatToBgra16Bytes(
            linear, f.Width, f.Height, s.ToneMappingParams, s.ColorSpaceTag, quantizeBits);
    }

    /// <summary>
    /// ARGB10（A2R10G10B10，NVENC 真 10-bit 输入）通路的像素准备：同一条浮点链，只是量化到 10-bit。
    /// <para>2026-09-19 收敛：此前 AVIF 的 NVENC 分支自己内联了一份"矩阵 + ICC 兜底 + tag 覆写"，
    /// 与 PrepareSdr16Pixels 是两份事实源（改一处漏一处，两条路迟早产出不同颜色）。</para>
    /// </summary>
    public static byte[] PrepareSdrArgb10Pixels(HdrFrameData f, EncodingSettings s)
    {
        var linear = PrepareSdrLinearFloat(f, s);
        s.HdrOutput = false;
        return Processing.ToneMapper.FloatToArgb10Bytes(
            linear, f.Width, f.Height, s.ToneMappingParams, s.ColorSpaceTag);
    }

    // ═══ ICC 与 CICP 判据的单一事实源（2026-09-19 收敛散落各处的魔数字；2026-09-20 把长度阈值换成结构判据）═══

    /// <summary>
    /// "这份 ICC 到底能不能承担色彩声明" —— <b>结构</b>判据，不是长度判据。
    /// <para>为什么要换掉长度阈值（§31.3-9）：原先三处并存 <c>&gt;400 / &gt;128 / &gt;0</c>，
    /// 而 <c>400</c> 那处的注释写着它回答"这份 ICC 足以代表目标色域吗"。
    /// <c>scripts/probes/icc_min_size_probe.py</c> 三面取证（构造 / 现网语料 / LittleCMS 裁判）
    /// 量出这两个方向<b>互相矛盾，任何单一数字都无解</b>：
    /// ① 一份 <b>396 B</b> 的 v2 matrix-RGB profile（必需标签齐、长度字段自洽、
    ///    rXYZ+gXYZ+bXYZ 之和与 D50 白差 3e-4、<b>LittleCMS 认</b>）会被 <c>&gt;400</c> 判死；
    /// ② 一份 <b>500 B</b> 的"壳"（长度字段自洽、LittleCMS 也认，但<b>没有 rXYZ/gXYZ/bXYZ</b>）
    ///    反而能过 <c>&gt;400</c>；
    /// ③ 本机现网的 <c>RSWOP.icm</c> 是 <b>218103 B 的 CMYK 打印 profile</b>，任何长度阈值都拦不住它
    ///    被嵌进一张 RGB 截图。
    /// ⇒ 要拒的①②③长度彼此重叠，所以这里改成逐条问"能不能声明"：长度字段自洽 + <c>acsp</c>
    ///    + 色彩空间是 <c>RGB </c> + rXYZ/gXYZ/bXYZ/wtpt 四份 XYZ 数据在界内 + 三条 TRC 在（没有 TRC
    ///    就不知道 RGB→线性 的映射，那正是声明的本体）。</para>
    /// <para>⚠ 刻意<b>不</b>断言的：`rXYZ+gXYZ+bXYZ == wtpt`。实测微软自家
    /// <c>sRGB Color Space Profile.icm</c> 的 wtpt 记的是<b>原始 D65</b>(0.9505,1,1.0891) 而列值和是
    /// D50（差 0.264），Windows HDR 自动校准 profile（816 B, v4）两者都差得更远 ——
    /// 拿这条去拒会误杀真实在用的 profile。列和只用于诊断，不当门禁。</para>
    /// </summary>
    /// <param name="icc">判为 <c>true</c> 时保证非空（<see cref="NotNullWhenAttribute"/>），
    /// 调用点因此不需要再写 <c>!</c>。</param>
    public static bool IsEmbeddableIcc(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] byte[]? icc)
    {
        if (icc is null || icc.Length < 136) return false;                 // 132 头 + 4 标签数
        if (Be32(icc, 0) != (uint)icc.Length) return false;                // 长度字段与自身不符 ⇒ 截断/拼接
        if (Be32(icc, 36) != 0x61637370) return false;                     // 'acsp'
        if (Be32(icc, 16) != 0x52474220) return false;                     // 'RGB ' —— CMYK/'K Lab' 一律不算
        uint tagCount = Be32(icc, 128);
        if (tagCount > 400 || 132 + 12L * tagCount > icc.Length) return false;
        foreach (var sig in IccRequiredXyzTags)                            // rXYZ gXYZ bXYZ wtpt
            if (!FindTag(icc, tagCount, sig, out int off) || off + 20 > icc.Length
                || Be32(icc, off) != 0x58595A20)                          // 数据类型必须是 'XYZ '
                return false;
        foreach (var sig in IccRequiredTrcTags)                            // rTRC gTRC bTRC
            if (!FindTag(icc, tagCount, sig, out int off) || off + 12 > icc.Length)
                return false;                                              // curv / para 都由 12 字节头起
        return true;
    }

    private static readonly uint[] IccRequiredXyzTags = { 0x7258595A, 0x6758595A, 0x6258595A, 0x77747074 };
    private static readonly uint[] IccRequiredTrcTags = { 0x72545243, 0x67545243, 0x62545243 };

    private static uint Be32(byte[] d, int off) =>
        ((uint)d[off] << 24) | ((uint)d[off + 1] << 16) | ((uint)d[off + 2] << 8) | d[off + 3];

    /// <summary>在标签表里找 4 字节签名；命中返回其数据偏移。偏移越界视为未命中。</summary>
    private static bool FindTag(byte[] d, uint tagCount, uint sig, out int dataOffset)
    {
        dataOffset = 0;
        for (uint i = 0; i < tagCount; i++)
        {
            int entry = 132 + (int)i * 12;
            if (entry + 12 > d.Length) return false;
            if (Be32(d, entry) != sig) continue;
            long off = Be32(d, entry + 4), size = Be32(d, entry + 8);
            if (off <= 0 || size < 8 || off + size > d.Length) return false;
            dataOffset = (int)off;
            return true;
        }
        return false;
    }

    // ── CICP（ITU-T H.273）槽位：PNG cICP / avifenc --cicp / NVENC colr(nclx) 共用 ──
    public const byte CicpMatrixIdentity = 0;      // RGB 采样，无 YUV 往返
    public const byte CicpMatrixBt709 = 1;         // SDR YUV 矩阵
    /// <summary>
    /// BT.601-7 525 行（= SMPTE 170M）的 H.273 matrix 码点。
    /// <para><b>2026-09-20 现状：硬件 AVIF 路已不再需要它</b>——同日把
    /// <c>NV_ENC_CONFIG_AV1.matrixCoefficients</c> 真正下发给驱动后，NVENC 会按我们要求的
    /// 矩阵做 RGB→YUV（见 <see cref="NvencAvifCicp"/>）。本常量现在只服务一条路径：
    /// <b>MFT/QSV</b>——那条路的 RGB→NV12 是【我们自己】用 601 整数系数算的
    /// （<c>MftEncoderNative.BgraToNv12</c>，U=(-38r-74g+112b)>>8+128 那一组）。</para>
    /// <para>实测依据（<c>--nvenc-color-probe</c> 与其 <c>sweep</c> 子模式 +
    /// <c>scripts/probes/nvenc_matrix_probe.py</c>，本机 RTX + 当前驱动）：
    /// 不写这个字段时驱动默认按 <b>BT.601/full</b> 转（纯色带拟合误差 3.4/1023，
    /// 次优候选 80.0 ⇒ 判据成立）；逐个下发 H.273 码点后驱动【照做】——
    /// 1→BT.709(误差≤1.2)、2/5/6→BT.601、9→BT.2020 NCL(误差≤1.0)、0(identity) 直接被驱动拒绝
    /// （<c>InitEncoder 0x8 "invalid color configuration"</c>）。SDR/HDR(PQ) 两条路行为一致。
    /// 也就是说驱动吃 H.273 号域，不需要换成 AV1 序列头那套编号。</para>
    /// <para>⚠ 码点 5 与 6 都是"BT.601-7"（525/625 之分），Kr/Kb 完全相同 ⇒ 解码结果无差别，
    ///   取 5 只是命名习惯（SMPTE 170M）。
    /// ⚠ 本常量的适用域是 <b>ISOBMFF colr(nclx) 与 NVENC 配置字段</b>（两者都是 H.273 号域）；
    ///   若哪天要手工改<b>已编好的 AV1 码流</b>序列头，那里是另一套编号，不能照抄。</para>
    /// </summary>
    public const byte CicpMatrixBt601 = 5;
    public const byte CicpMatrixBt2020Ncl = 9;     // HDR(PQ) YUV 矩阵
    public const byte CicpTransferSrgb = 13;       // sRGB transfer（图片/截图标准，非 BT.709 视频 OETF=1）
    public const byte CicpTransferPq = 16;         // ST.2084 PQ
    public const byte CicpFullRangeFlag = 1;
    public const byte CicpPrimariesBt709 = 1;      // H.273 colour_primaries：BT.709 = sRGB 三原色
    public const byte CicpPrimariesBt2020 = 9;    // H.273 colour_primaries：BT.2020 三原色

    /// <summary>
    /// AVIF 实际生效的容器位深：AV1 只有 8/10/12 三档（<c>avifenc --depth</c> 与
    /// <c>NV_ENC_CONFIG_AV1.outputBitDepth</c> 的取值集合相同），其余请求一律钳到最近的一档。
    /// <para>为什么要有这一个函数：位深的"钳制"原本散在 <c>NativeAvifEncoder.GetDepthArg</c> 里，
    /// 而 2026-09-19 起 PNG 中转还要按<b>同一档</b>量化（避免二次量化）—— 两处若各算一次，
    /// 中转就可能按 16-bit 喂、容器却按 12-bit 声明。钳制口径必须只有一个事实源。</para>
    /// </summary>
    public static int AvifContainerDepth(int requestedBitDepth) =>
        requestedBitDepth <= 8 ? 8 : (requestedBitDepth >= 12 ? 12 : 10);

    /// <summary>
    /// 容器/码流侧（AV1 恒为 YUV）该声明哪个 matrix 系数 —— <b>只看三原色，不看 SDR/HDR</b>。
    /// <para>为什么与 PNG 的 cICP 不同：<b>PNG 存的是 RGB 采样</b>，matrix 槽写 identity(0) 就是事实，
    /// 没有 YUV 往返可声明；<b>AV1/AVIF 是 YUV 码流</b>，必须声明真正把 RGB↔YUV 对上的矩阵
    /// —— 声明 0 时 Windows 照片等解码器不支持 identity、自行套错矩阵 → YUV 解码偏色
    /// （绿偏黄/红偏紫，2026-08-10 实测），故两处不能共用同一个值，但必须共用这一个函数。</para>
    /// <para>为什么按三原色而不是按"是否 HDR"（2026-09-20 收口）：Y 的加权系数本就是 primaries
    /// 的属性（BT.2020 用 0.2627/0.6780/0.0593，BT.709 用 0.2126/0.7152/0.0722）。
    /// 旧写法 <c>hdrOutput ? 9 : 1</c> 让广色域 SDR 拿 709 权重去装 2020 原色的像素，
    /// 而 HDR 又用 2020 权重 —— 同一条"matrix 跟 primaries 走"的规则被拆成两套，
    /// 还顺带造出"primaries=1 + matrix=9"这种自相矛盾的组合格（HDR + sRGB 标签时）。
    /// 现在软件（avifenc <c>--cicp</c>）与硬件（NVENC 配置字段）都从这里取值，
    /// 三档组合各自实测过：纯色带探针 <c>scripts/probes/nvenc_matrix_probe.py</c>。</para>
    /// </summary>
    public static byte AvifMatrixCoefficients(byte primaries) =>
        primaries == CicpPrimariesBt2020 ? CicpMatrixBt2020Ncl : CicpMatrixBt709;

    /// <summary>一份自洽的 AVIF 色彩意图（H.273 码点 + 摆幅）。</summary>
    /// <remarks>存在的意义是<b>把"下发给驱动的"和"写进容器的"绑成同一个值</b>：
    /// NVENC 自己决定 RGB→YUV（矩阵不由我们挑），一旦配置字段与 colr 各算各的，
    /// 就会出现"硬件用 A 转、容器声明 B"——那比两处都不声明更坏，因为解码器会
    /// 照着错的说明书去逆矩阵。2026-09-20 的码点扫描证明驱动按 H.273 号域执行本字段，
    /// 于是这里可以给出一份真话（详见 <see cref="CicpMatrixBt601"/> 的实测记录）。</remarks>
    public readonly record struct AvifCicp(byte Primaries, byte Transfer, byte Matrix, bool FullRange);

    /// <summary>
    /// 硬件（NVENC）AVIF 的 CICP 意图；<b>null = 给不出自洽声明</b> —— 目前只有 AdobeRGB
    /// （H.273 无该 primaries 码点，见 <see cref="BuildSdrCicp"/>）：此时既不下发码点也不写 nclx，
    /// 保持"驱动默认 + ICC 说明"的旧行为，不猜一个值冒充。
    /// <para><paramref name="colorSpaceTag"/> 必须是<b>调用方已归一</b>的标签：HDR 路先过
    /// <see cref="HdrDeclarableTag"/>，SDR 路用设置里的原标签。</para>
    /// </summary>
    public static AvifCicp? NvencAvifCicp(bool hdrOutput, string? colorSpaceTag)
    {
        if (!hdrOutput && BuildSdrCicp(colorSpaceTag) is null) return null;
        // "System"/null 一律按 sRGB 解释（与容器 nclx 一直的口径相同，不另起一套）
        byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(
            colorSpaceTag is not (null or "System" or "sRGB") ? colorSpaceTag : "sRGB");
        // matrix 只问 AvifMatrixCoefficients(primaries) —— 软件路（avifenc --cicp）问的是同一个函数、
        // 同一个入参，两路因此不可能给出不同的 AVIF；驱动按下发的号转换（2026-09-20 码点扫描），
        // 所以下发什么就是什么，无需把任何一路降级去迁就另一路。
        return new AvifCicp(primaries,
            hdrOutput ? CicpTransferPq : CicpTransferSrgb,
            AvifMatrixCoefficients(primaries),
            NvEncoderNative.NvEncWantsFullRange);
    }

    /// <summary>SDR 的 CICP 四元组 <c>[primaries, transfer, matrix, fullRange]</c>（PNG cICP 形态，matrix=identity）。
    /// <para>AdobeRGB → 返回 null：H.273 Table 2 里它没有标准 primaries 码，
    /// 而 cICP 优先于 iCCP ⇒ 写下去会把 AdobeRGB 误标成 BT.709/sRGB、色彩丢失，只能靠 ICC 声明。</para></summary>
    public static byte[]? BuildSdrCicp(string? colorSpaceTag)
    {
        if (colorSpaceTag == "AdobeRGB") return null;
        byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(colorSpaceTag ?? "sRGB");
        byte transfer = ColorManagement.ColorSpaceConverter.GetCicpTransfer(colorSpaceTag ?? "sRGB", hdrOutput: false);
        return [primaries, transfer, CicpMatrixIdentity, CicpFullRangeFlag];
    }

    /// <summary>HDR 的 CICP 四元组（PQ transfer；PNG cICP 形态 matrix=identity，AVIF 侧由
    /// <see cref="AvifMatrixCoefficients"/> 换成 YUV 矩阵）。HDR 一律不嵌 ICC（见各格式 2026-08-09 备注）。</summary>
    public static byte[] BuildHdrCicp(string? colorSpaceTag)
    {
        byte primaries = ColorManagement.ColorSpaceConverter.GetCicpPrimaries(colorSpaceTag ?? "BT2020");
        return [primaries, CicpTransferPq, CicpMatrixIdentity, CicpFullRangeFlag];
    }

    /// <summary>
    /// HDR 场景下"容器能忠实声明"的目标色域归一（2026-09-19 缺陷 D-3：PNG/AVIF/JXL 三处同根）。
    /// <para><b>为什么 HDR 要单独问一遍</b>：SDR 侧"AdobeRGB 在 H.273 Table 2 没有 primaries 码点
    /// ⇒ 不写码点、只靠 ICC 声明"的守卫挂在 <see cref="BuildSdrCicp"/> 上，而 HDR 声明路径
    /// （PQ + cICP / colr(nclx) / cjxl 的 color_space）<b>根本不经过它</b>；而这些通道都只认 H.273 的
    /// 原色码点，里面没有 Adobe 那一格。于是"HDR + AdobeRGB"会走完 Adobe 矩阵、再把 Adobe 原色的像素报成
    /// primaries=1(BT.709)（PNG/AVIF 硬件）或 BT.2020（JXL 的 Rec2100PQ）—— 声明与像素矛盾且无兜底。</para>
    /// <para><b>口径</b>：HDR 容器家族本来就是 BT.2020（<c>ResolveColorSpaceTag</c> 把 "System"+HDR
    /// 也解析成它），故把不可声明的 AdobeRGB 归一到 BT2020，并<b>就地改写 s.ColorSpaceTag</b> ——
    /// 矩阵（<see cref="HdrToPq16"/> / <c>ToneMapper.HdrToArgb10Pq</c>）与声明（cICP/colr/color_space/
    /// PQ-ICC）都从同一个 <c>s</c> 取值 ⇒ 两者同源，不会出现"像素搬了、声明没搬"。BT.2020 比 AdobeRGB
    /// 更宽，归一不裁色域；代价是用户显式选的 Adobe 未生效 ⇒ 必须留痕（下方 Toast 文案）。</para>
    /// <para><b>只在 HDR 分支调用</b>：SDR 侧 Adobe 在 PNG / AVIF / TIFF / JPEG LI / WebP 上还有
    /// "不写码点、只带 Adobe 的 ICC"这条忠实出路；而 JXL 的 SDR 路自 2026-09-27 起与这里同向
    /// （收口成 BT2020），不再单独退回 sRGB —— 见 <c>ImageEncoder</c> 的 JXL 分支说明。</para>
    /// </summary>
    public static string HdrDeclarableTag(EncodingSettings s)
    {
        var tag = s.ColorSpaceTag ?? "sRGB";
        if (tag != "AdobeRGB") return tag;

        s.ColorSpaceTag = ColorManagement.ColorProfileProvider.MapAdobeTargetToWide(tag);
        EncoderFallbackNotifier.LastReason =
            "HDR(PQ) 容器无法忠实声明 AdobeRGB（H.273 没有 Adobe 这一格原色码点，而这条路的声明走 "
            + "cICP / colr(nclx) / cjxl 的 color fields）⇒ 本张已按 HDR 家族的 Rec.2020 输出，像素与声明同色域";
        return "BT2020";
    }

    /// <summary>
    /// 源精度判据（2026-09-19 解耦的核心）。
    /// <para>本仓库里"源精度"不是配置项，而是<b>入口</b>决定的事实：
    /// <see cref="ImageEncoder.EncodeAsync"/> 拿到 <see cref="HdrFrameData.Pixels"/>
    /// （WGC Float16 会话的 scRGB 线性浮点，Windows 能给 &gt;8-bit 才建得起这个会话）⇒ 高精度源；
    /// <see cref="ImageEncoder.EncodeSdrAsync"/> 拿到 byte[] BGRA8 ⇒ 8-bit 源。
    /// 于是"中转容器/喂给编码器的输入精度"由本判据决定，"最终容器位深"由
    /// <c>EncodingSettings.OutputBitDepth</c>（用户请求）决定 —— 两者不再共用一个位深条件。</para>
    /// <para>判据写成"有没有浮点像素"而不是恒真，是为了让"空数组"显式落到 8-bit 通路。
    /// ⚠ 但落到那条路【不等于能跑通】：只要 <c>Width*Height &gt; 0</c> 而 <c>Pixels</c> 为空，
    /// 下游 <see cref="ToSdr"/> / <see cref="HdrToPq16"/> 仍会按下标读 <c>f.Pixels</c> ⇒
    /// <c>IndexOutOfRangeException</c>（2026-09-19 两条独立审查同指此处；旧注释声称"退到 8-bit 通路、
    /// 行为与改造前一致"，那说的是分派结果，不是可运行结果）。产品路径不可达：唯一的构造门是
    /// <c>captureResult.HdrPixels is not null</c>，故不给内核加防御性守卫 —— 真要支持空帧输入，
    /// 得先在两个内核里补长度判据，而不是依赖本判据兜底。</para>
    /// </summary>
    public static bool IsHighPrecisionSource(HdrFrameData f) => f.Pixels is { Length: > 0 };

    /// <summary>根据 EncodingSettings 计算 ICC 和 CICP 元数据。</summary>
    public static (byte[]? icc, byte[]? cicp) GetColorMetadata(EncodingSettings s)
        => GetColorMetadata(s.ColorSpaceTag, s.IccProfile);

    /// <summary>
    /// 同一份声明的裸值入口：只吃 <c>colorSpaceTag</c> + <c>iccProfile</c>，
    /// 给手上没有完整 <see cref="EncodingSettings"/> 的末端保底路径（AVIF 全后端失败 → PNG）复用，
    /// 免得那条路自己再拼一遍"要不要嵌 ICC / 下发哪组码点"—— 那份逻辑只该有这一处。
    /// </summary>
    internal static (byte[]? icc, byte[]? cicp) GetColorMetadata(string? colorSpaceTag, byte[]? iccProfile)
    {
        bool isSRgb = colorSpaceTag is null or "System" or "sRGB";
        byte[]? icc = (!isSRgb && IsEmbeddableIcc(iccProfile)) ? iccProfile : null;
        // ═══ AdobeRGB 无标准 CICP primaries code (ITU-T H.273 Table 2) ═══
        // 详见 BuildSdrCicp：误标的 cICP 会覆盖 AdobeRGB iCCP → 色彩丢失，故只靠 ICC 声明。
        var cicp = BuildSdrCicp(colorSpaceTag);
        return (icc, cicp);
    }


    /// <summary>HDR scRGB → PQ 16-bit RGBA 数组。
    /// 根据目标位深直接量化到正确精度，然后左对齐到 16-bit 容器。
    /// 确保 16-bit 容器中真正存放的是 10/12-bit 数据（左对齐），而非完整 16-bit 数据被截断。</summary>
    /// <param name="f">HDR 帧数据（scRGB 线性浮点像素）。</param>
    /// <param name="colorSpaceTag">目标色域标签，null/sRGB 时不转换。</param>
    /// <param name="bitDepth">目标位深: 10/12/16。10-bit 量化到 0-1023 后左移 6 位。</param>
    public static ushort[] HdrToPq16(HdrFrameData f, string? colorSpaceTag = null, int bitDepth = 16)
    {
        int pixelCount = f.Width * f.Height;

        // ── 根据目标位深确定量化范围 ──
        // 10-bit: 0-1023, 左移到 16-bit 容器高位 (<<6)
        // 12-bit: 0-4095, 左移到 16-bit 容器高位 (<<4)
        // 16-bit: 0-65535, 全精度
        int maxValue = bitDepth switch
        {
            10 => 1023,
            12 => 4095,
            _ => 65535,
        };
        int shift = bitDepth switch
        {
            10 => 6,
            12 => 4,
            _ => 0,
        };

        // ═══ 色域转换矩阵（scRGB BT.709 → 目标色域线性）═══
        var matrix = ColorManagement.ColorSpaceConverter.GetMatrix(colorSpaceTag ?? "sRGB");
        float m00 = 0, m01 = 0, m02 = 0;
        float m10 = 0, m11 = 0, m12 = 0;
        float m20 = 0, m21 = 0, m22 = 0;
        bool hasMatrix = false;
        if (matrix is not null)
        {
            hasMatrix = true;
            m00 = matrix[0, 0]; m01 = matrix[0, 1]; m02 = matrix[0, 2];
            m10 = matrix[1, 0]; m11 = matrix[1, 1]; m12 = matrix[1, 2];
            m20 = matrix[2, 0]; m21 = matrix[2, 1]; m22 = matrix[2, 2];
        }

        var src = f.Pixels;
        var p16 = new ushort[pixelCount * 4];
        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            float r = src[i];
            float g = src[i + 1];
            float b = src[i + 2];
            float a = src[i + 3];

            // 融合色域转换（无矩阵时直通，无中间 float[] 分配）
            if (hasMatrix)
            {
                float rr = r * m00 + g * m01 + b * m02;
                float gg = r * m10 + g * m11 + b * m12;
                float bb = r * m20 + g * m21 + b * m22;
                r = rr; g = gg; b = bb;
            }

            // PQ 编码 → 直接量化到目标位深 → 左对齐到 16-bit 容器
            float pqR = LinearToPQ(r);
            float pqG = LinearToPQ(g);
            float pqB = LinearToPQ(b);
            a = Math.Clamp(a, 0f, 1f);

            // 量化到 maxValue 范围，然后左移到 16-bit 容器高位
            // 10-bit: 0-1023 → <<6 → 0-65520 (高位 10-bit 有效)
            // 12-bit: 0-4095 → <<4 → 0-65520 (高位 12-bit 有效)
            // 16-bit: 0-65535 → 直通
            int qiR = (int)Math.Round(pqR * maxValue);
            int qiG = (int)Math.Round(pqG * maxValue);
            int qiB = (int)Math.Round(pqB * maxValue);
            int qiA = (int)Math.Round(Math.Clamp(a, 0f, 1f) * maxValue);

            p16[i]     = (ushort)Math.Clamp(qiR << shift, 0, 65535);
            p16[i + 1] = (ushort)Math.Clamp(qiG << shift, 0, 65535);
            p16[i + 2] = (ushort)Math.Clamp(qiB << shift, 0, 65535);
            p16[i + 3] = (ushort)Math.Clamp(qiA << shift, 0, 65535);
        });
        return p16;
    }

    /// <summary>RGBA 16-bit 数组 → BGRA 16-bit 字节数组（大端，供 PNG 16-bit 编码使用）。</summary>
    /// <remarks>C1 fix: 保留完整 16-bit 精度，不再截断为 8-bit。</remarks>
    public static byte[] Rgba16ToBgra16Bytes(ushort[] rgba16, int w, int h)
    {
        int pixelCount = w * h;
        var bgra16 = new byte[pixelCount * 8]; // 4 channels × 2 bytes
        for (int i = 0; i < pixelCount; i++)
        {
            int si = i * 4;
            int di = i * 8;
            // BGRA 16-bit big-endian (PNG 网络字节序)
            WriteU16BE(bgra16, di, rgba16[si + 2]);     // B
            WriteU16BE(bgra16, di + 2, rgba16[si + 1]); // G
            WriteU16BE(bgra16, di + 4, rgba16[si]);     // R
            WriteU16BE(bgra16, di + 6, rgba16[si + 3]); // A
        }
        return bgra16;
    }

    private static void WriteU16BE(byte[] buf, int off, ushort val)
    {
        buf[off] = (byte)(val >> 8);
        buf[off + 1] = (byte)val;
    }

    /// <summary>BGRA8（每像素 4 字节）→ BGRA16 大端（每像素 8 字节）。
    /// 8→16 位"容器升深"采用字节复制 v*257 = (v&lt;&lt;8)|v：高字节与低字节同为 v。
    /// <para>
    /// ⚠ 通道顺序必须与 <see cref="Rgba16ToBgra16Bytes"/> 一致 —— **B16 G16 R16 A16 大端**，
    /// 这是 <see cref="ManagedPngEncoder.Encode16"/> 的输入约定。
    /// 把 R 写到 offset 0 会导致 PNG 红蓝互换：<c>NativeJxlEncoder</c> 旧的内联实现正是这个缺陷，
    /// 而 JXL 默认位深就是 10，等于默认路径一直在输出红蓝颠倒的文件。
    /// </para>
    /// <para>⚠ 这只是"容器位深提升"，8-bit 源不会因此获得任何新信息（不会消除色带）。</para>
    /// </summary>
    /// <param name="bgra">BGRA8 像素（B,G,R,A 每通道 1 字节）。</param>
    /// <returns>长度为 <c>bgra.Length * 2</c> 的 BGRA16 大端数组。</returns>
    public static byte[] Bgra8ToBgra16BE(byte[] bgra)
    {
        var bgra16 = new byte[bgra.Length * 2];
        for (int i = 0, di = 0; i < bgra.Length; i += 4, di += 8)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2], a = bgra[i + 3];
            bgra16[di + 0] = b; bgra16[di + 1] = b;   // B
            bgra16[di + 2] = g; bgra16[di + 3] = g;   // G
            bgra16[di + 4] = r; bgra16[di + 5] = r;   // R
            bgra16[di + 6] = a; bgra16[di + 7] = a;   // A（保留原始 alpha）
        }
        return bgra16;
    }

    // ST.2084 PQ 常数 (SMPTE ST.2084)
    private const float PQ_m1 = 2610f / 16384f;
    private const float PQ_m2 = 2523f / 32f;
    private const float PQ_c1 = 3424f / 4096f;
    private const float PQ_c2 = 2413f / 128f;
    private const float PQ_c3 = 2392f / 128f;

    /// <summary>从 PQ16 数组计算内容实际峰值亮度 (nits)。
    /// ═══ 2026-08-10: 用于 JXL `--intensity_target` ═══
    /// 截图内容通常是 SDR 桌面 (峰值 200-800 nits), 恒传 10000 会让 tone-map
    /// 查看器把 SDR 内容显示得极暗。传实际峰值 → 查看器正确映射。
    /// clamp 到 [203, 10000] 保证合法 (libjxl 要求 ≥ 255, 保守用 203)。
    /// 含 Alpha 通道 (65535) 不计入峰值。</summary>
    public static float ComputePeakNits(ushort[] pq16)
    {
        int maxCode = 0;
        // 步进采样加速: 4K 图像 3300 万码值, 全扫约 10ms
        for (int i = 0; i < pq16.Length; i += 4)
        {
            int c = pq16[i];
            if (c > maxCode) maxCode = c;
            c = pq16[i + 1];
            if (c > maxCode) maxCode = c;
            c = pq16[i + 2];
            if (c > maxCode) maxCode = c;
        }
        return PeakNitsFromMaxCode(maxCode, 65535);
    }

    /// <summary>
    /// 从 NVENC HDR 直通的 <b>ARGB10 打包码值</b>算内容峰值 —— 与
    /// <see cref="ComputePeakNits(ushort[])"/> 同一条 PQ EOTF，只是位深/打包不同。
    /// <para>为什么要有这一份：硬件通路不产生 16-bit PQ 数组（它把 scRGB 直接量化进
    /// A2R10G10B10），而 clli 必须描述<b>这张图真正编进去的码值</b>。若图省事拿显示器峰值充当
    /// 内容峰值，就等于给一张最高只有 300 nits 的桌面截图声明 1000 nits 上限 ——
    /// tone-map 查看器会按声明把画面压暗，正是本项目一直修的"声明与像素不一致"。</para>
    /// <para>打包顺序取自 <c>NvEncoderNative.EncodeAv1Argb10</c> 的注释：
    /// B 占最低 10 位、G 次之、R 再次、A 占最高 2 位；Alpha 不计入峰值。</para>
    /// </summary>
    public static float ComputePeakNitsFromArgb10(byte[] argb10Pq)
    {
        int maxCode = 0;
        for (int i = 0; i + 3 < argb10Pq.Length; i += 4)
        {
            uint px = (uint)(argb10Pq[i] | (argb10Pq[i + 1] << 8)
                             | (argb10Pq[i + 2] << 16) | (argb10Pq[i + 3] << 24));
            int c = (int)(px & 0x3FFu);                       // B
            if (c > maxCode) maxCode = c;
            c = (int)((px >> 10) & 0x3FFu);                   // G
            if (c > maxCode) maxCode = c;
            c = (int)((px >> 20) & 0x3FFu);                   // R
            if (c > maxCode) maxCode = c;
        }
        return PeakNitsFromMaxCode(maxCode, 1023);
    }

    /// <summary>PQ 码值 → nits 的唯一一份 EOTF 反解（16-bit 与 10-bit 两路共用）。</summary>
    private static float PeakNitsFromMaxCode(int maxCode, double fullScale)
    {
        if (maxCode <= 0) return 203f;
        double vv = maxCode / fullScale;
        double vm = Math.Pow(vv, 1.0 / PQ_m2);
        double num = Math.Max(vm - PQ_c1, 0.0);
        double den = PQ_c2 - PQ_c3 * vm;
        float nits = (float)(Math.Pow(num / den, 1.0 / PQ_m1) * 10000.0);
        return Math.Clamp(nits, 203f, 10000f);
    }

    /// <summary>
    /// AVIF 的 <c>clli</c>（Content Light Level）取值 —— 软件路（avifenc <c>--clli</c>）与
    /// 硬件路（容器属性盒）<b>共用这一个规则</b>，两路编同一张图必须给出同一份亮度声明。
    /// <para>MaxFALL 用峰值的一半是既有近似（"仅元数据提示"，本次不改数值以免改变产物），
    /// 但它必须是<b>同一个近似</b>：两路各写一个公式，就成了又一次"容器说两套话"。</para>
    /// </summary>
    /// <param name="peakNits">内容实际峰值（nits）；≤0 = 未知 ⇒ 返回 null（两侧都不写）。</param>
    public static (ushort MaxCLL, ushort MaxFALL)? AvifContentLightLevel(float peakNits)
    {
        if (peakNits <= 0f) return null;
        int maxCll = (int)Math.Clamp(peakNits, 203f, 10000f);
        return ((ushort)maxCll, (ushort)Math.Max(1, maxCll / 2));
    }

    /// <summary>
    /// AVIF HDR 的 clli 峰值口径：<b>显示器 HDR 峰值</b>与<b>本张内容的实际峰值</b>取较大者
    /// （内容峰值只允许往上抬，防止 clip）。显示器峰值未上报时按 1000 nits（HDR 屏常见值）。
    /// <para>与 JXL 的 <c>--intensity_target</c> 同源同策（见 2026-08-10 那两条修复备注）。
    /// 两条编码路（软件 avifenc / 硬件 NVENC）都问这一个函数，只是"内容实际峰值"各自从
    /// 自己真正编码的那份码值算（<see cref="ComputePeakNits(ushort[])"/> /
    /// <see cref="ComputePeakNitsFromArgb10"/>）。</para>
    /// </summary>
    public static float AvifHdrPeakNits(EncodingSettings s, float contentPeakNits)
    {
        float peak = s.ToneMappingParams.DisplayMaxNits;
        if (peak <= 0f) peak = 1000f;
        return peak < contentPeakNits ? contentPeakNits : peak;
    }

    /// <summary>scRGB 线性光 → PQ (ST.2084) 感知量化编码。</summary>
    internal static float LinearToPQ(float scRgbLinear)
    {
        float nits = Math.Max(scRgbLinear * 80f, 0f);
        float L = Math.Clamp(nits / 10000f, 0f, 1f);
        float Lp = MathF.Pow(L, PQ_m1);
        float numerator = PQ_c1 + PQ_c2 * Lp;
        float denominator = 1f + PQ_c3 * Lp;
        return MathF.Pow(numerator / denominator, PQ_m2);
    }
}

// ────── 工厂（无状态编码器单例缓存，减少 GC 压力）──────
public static class EncoderFactory
{
    private static readonly PngEncoder s_png = new();
    private static readonly JpegLiEncoder s_jpegLi = new();
    private static readonly JpegXlEncoder s_jpegXl = new();
    private static readonly AvifEncoder s_avif = new();
    private static readonly WebPEncoder s_webp = new();
    private static readonly TiffEncoder s_tiff = new();
    private static readonly JpegGainMapEncoder s_gainMap = new();

    public static ImageEncoder Create(OutputFormat f) => f switch
    { OutputFormat.PNG => s_png, OutputFormat.JPEG_LI => s_jpegLi, OutputFormat.JPEG_XL => s_jpegXl, OutputFormat.AVIF => s_avif, OutputFormat.WebP => s_webp, OutputFormat.TIFF => s_tiff, OutputFormat.JPEG_GAINMAP => s_gainMap, _ => s_png };
    public static OutputFormat Parse(string n) => n?.ToUpperInvariant() switch
    { "PNG" => OutputFormat.PNG, "JPEG LI" or "JPEGLI" => OutputFormat.JPEG_LI, "JPEG XL" or "JXL" => OutputFormat.JPEG_XL, "AVIF" => OutputFormat.AVIF, "WEBP" => OutputFormat.WebP, "TIFF" or "TIF" => OutputFormat.TIFF, "JPEG GAINMAP" or "JPEGGAINMAP" or "GAINMAP" or "ULTRAHDR" => OutputFormat.JPEG_GAINMAP, _ => OutputFormat.PNG };
}
