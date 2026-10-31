// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/CompressionEffort.cs
// 「压缩强度」三档 —— AVIF / JPEG XL / WebP 的 effort/speed 旋钮（2026-10-07 用户口径）
//
// ═══════════════════════════════════════════════════════════════════════════════
//  为什么这一档起初只有三个格式
// ═══════════════════════════════════════════════════════════════════════════════
// 只有 AVIF / JPEG XL / WebP 的编码器有【独立于质量】的 effort/speed 旋钮：
//   avifenc -s 0..10（0 最慢）、cjxl -e 1..10、cwebp -m 0..6、NVENC preset P1..P7。
// 而 JPEG LI 与 PNG 的"压缩强度"与【质量】是同一个旋钮（butteraugli 距离 / 无损压缩级别），
// 再开一档等于给同一个东西开第二个入口，会与质量滑块打架 ⇒ 它们不加这一档。
//
// ═══ 2026-10-29 更正：上面那句对 PNG / TIFF 不成立，它们已纳入（见 PngTiffDeflateLevel）═══
// 排除理由的前提是"无损压缩级别与质量滑块会打架"，但 PNG 的 GetQualityRange 恒为
// (100,100,100, LosslessFixed100) —— 质量滑块对 PNG【本来就锁死在一个台阶上】，打架的对象不存在。
// 而 PNG/TIFF 恰是全仓最慢的两条（4K HDR PNG 的 deflate 一步实测 28.1s，同内容 level 6 只要 1.85s），
// 用户据此要求给它们三档。JPEG LI 仍只有一个旋钮（质量），不动它。
//
// ═══════════════════════════════════════════════════════════════════════════════
//  取值依据：4K 真实截图实测（2026-10-07，样本 TrueToneCap_20260930_025449195.png 4.66 MB）
// ═══════════════════════════════════════════════════════════════════════════════
//   cjxl -d 1.0:   e3 0.64s/490KB   e5 0.82s/491KB   e7 1.09s/490.8KB
//                  e9 25.0s/541KB   e10 24.0s/541KB
//   avifenc -q60:  s0 8.46s/127KB    s2 5.71s/128.5KB  s4 2.79s/134.4KB
//                  s6 0.94s/144.3KB  s8 0.61s/157.3KB  s10 0.59s/180.1KB
//   cwebp -q90:    m0 0.39s/306KB    m2 0.42s/268KB    m4 0.74s/258KB   m6 0.95s/248KB
//
// ⚠ **cjxl 的 effort 9/10 不能当"最高档"**：实测在该样张上 e9/e10 比 e3【慢 39× 且体积大 10%】
//   （另一张样张上 e9 比 e7 小 5.4% ⇒ 不是普遍更差，但**不稳定**）。
//   把一个"更慢且可能更大"的值当最高档提供给用户，是在骗人 ⇒ 三档取 e3/e5/e7。
//
// ⚠ **两把尺不可比**：NVENC 只有 P1..P7，与 avifenc 的 0..10 不是同一个刻度，
//   下面的映射是【各自档位的对应关系】，不是"同一档应该一样快"。别照数字横向比。

namespace TrueToneCap.Core.Encoding;

/// <summary>压缩强度三档。作用于 AVIF / JPEG XL / WebP / JPEG LI 的 effort 旋钮，
/// 以及 PNG / TIFF 的无损 deflate 级别（2026-10-29 起；对这两者它【就是】唯一的旋钮，质量滑块锁死）。
/// <para>档位名按<b>用户视角的结果</b>命名（体积优先 / 均衡 / 速度优先），而不是按底层参数名 ——
/// 底层各家的参数名、方向、量纲都不同（avifenc 越小越慢、cjxl 越大越慢、cwebp 越大越慢、
/// NVENC P 越大越快、cjpegli 是布尔开关），照参数名命名必然误导。</para>
/// <para>⚠ <b>JPEG LI 是后来加进来的（2026-10-07，用户指出"--xyb 是正收益"）</b>：
/// 它此前被排除在"压缩强度"之外（理由：它的压缩强度与质量是同一个旋钮）。
/// 但 <c>--xyb</c> 并非质量旋钮 —— 它换的是色彩空间，实测在当年那批内容与质量上全都更小
/// （2026-10-29 换三张新截图复测出现一个"反而大 3.0%"的反例，见
///  <see cref="CompressionEffortMap.JpegLiUseXyb"/>），故按用户口径纳入。</para>
/// <para>⚠ <b>PNG / TIFF 也是后来加进来的（2026-10-29，用户点名"编码速度相对过慢的几个要给档位"）</b>：
/// 起初排除它们的理由是"会与质量滑块打架"，而 PNG 的质量滑块恒锁在 100（<c>LosslessFixed100</c>），
/// 打架的前提不成立 ⇒ 纳入，见 <see cref="CompressionEffortMap.PngTiffDeflateLevel"/>。
/// JPEG LI 仍只有一个旋钮（质量），不给它第二个入口。</para></summary>
public enum CompressionEffort
{
    /// <summary>体积优先：把压缩参数拉满，编码最慢。</summary>
    Smallest = 0,

    /// <summary>均衡（默认）：速度与体积的折中。</summary>
    Balanced = 1,

    /// <summary>速度优先：编码最快，体积最大。</summary>
    Fastest = 2,

    /// <summary>Tag <c>Quick</c>，界面显示「快速」（2026-10-29 用户口径新增）：<b>只比拉满档稍微放宽一点参数</b>的那一档。
    /// <para>为什么要有它：用户给 PNG / TIFF / WebP / JPEG LI 定的菜单是<b>两档</b> ——
    /// 「均衡」作默认（= Tag <c>Smallest</c> = 参数拉满，<b>名字里的"均衡"指"默认那档"，不指参数在中点</b>）
    /// + 一格「快速」（= 本成员）。三档体系里的 <see cref="Balanced"/>
    /// 对无损格式来说放宽的幅度太大（zlib level 6 比 9 快 10 倍但体积涨到 +3.8%、
    /// 平直内容上能涨 163%），不是"稍微"。</para>
    /// <para>各家的实际落点（端到端复测见 <c>artifacts/tier_bench_2026-10-29.log</c>）：
    /// <b>PNG/TIFF = zlib level 8</b> —— 三张真截图快 1.2~2.4×（PNG）/ 1.2~1.8×（TIFF）、
    /// 体积 +0.3%~+1.4% / +0.5%~+5.2%；4K HDR 合成帧 PNG 快 8.1×、体积<b>反而小 1.1%</b>，
    /// TIFF 快 4.8×、体积 +1.4%。
    /// <b>WebP = -m 5</b> —— 端到端快 1.2~1.5×、体积 +0.1%~+1.7%（该耗时含启动 cwebp 与写临时
    /// PNG 的固定开销，两档同量 ⇒ 比值是被压低的下界；2026-10-07 的 CLI 裸测是 179ms/29166B →
    /// 158ms/29650B）。
    /// <b>JPEG LI = 不加 --xyb</b> —— 快 1.2~1.3×，体积两张 +5.9%/+11.6%、一张 −2.9%
    /// （它只有布尔开关，两档就是开/关）。
    /// AVIF / JPEG XL / NVENC 的界面仍是三档，这一格在它们那里<b>不可达但必须有定义</b>
    /// （否则 switch 的默认分支会让"选了 Quick"变成静默等效于均衡）。</para>
    /// <para>⚠ 枚举数值只追加不重排：持久化写的是字符串，但任何把枚举当整数落盘/传参的地方
    /// 都经不起静默改序。</para></summary>
    Quick = 3,
}

/// <summary>档位 → 各编码器参数的<b>唯一</b>映射处。
/// <para>为什么集中在这里：四个消费方（avifenc 软件路 / NVENC 硬件路 / cjxl / cwebp）分处四个文件，
/// 若各自 switch 一遍，"改档位只改了一处"就是迟早的事，而这种漏改**不会报错**、
/// 只表现为"某个档位对某个格式没生效"。集中成一处的代价是四个调用点都要改传参，
/// 收益是映射规则可以被单测逐格钉住（见 CorePipelineTests 的档位判据）。</para></summary>
public static class CompressionEffortMap
{
    /// <summary>把持久化/界面的字符串（"Smallest"/"Quick"/"Balanced"/"Fastest"）解析成档位。
    /// <para>空/未识别一律回落 <see cref="CompressionEffort.Balanced"/>（默认档），
    /// 与 <c>SettingsService.Normalize</c> 对其它枚举的处理口径一致：坏值不许抛，
    /// 但也不许静默变成"最慢档"（那会让一次设置损坏变成每张图多等 25 秒）。</para></summary>
    public static CompressionEffort Parse(string? s) => s switch
    {
        "Smallest" => CompressionEffort.Smallest,
        "Quick" => CompressionEffort.Quick,
        "Fastest" => CompressionEffort.Fastest,
        "Balanced" => CompressionEffort.Balanced,
        _ => CompressionEffort.Balanced,
    };

    /// <summary>档位 → 持久化字面量（设置文件里那个值，也是界面 Tag）。
    /// <para>⚠ 每个档位必须显式列出一格：<c>_ =&gt; "Balanced"</c> 曾把 <see cref="CompressionEffort.Quick"/>
    /// 折成 "Balanced" 写回盘 —— 用户选「快速」（Tag Quick），下次启动读到的却是 Balanced，
    /// 且界面回填到另一项，属于最难发现的一类静默改义。判据
    /// <c>CorePipelineTests.Test_PerfWiring_PngTiffEffortTiers</c> 逐档做 round-trip 对撞。</para></summary>
    public static string ToTag(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => "Smallest",
        CompressionEffort.Quick => "Quick",
        CompressionEffort.Fastest => "Fastest",
        CompressionEffort.Balanced => "Balanced",
        _ => throw new System.ArgumentOutOfRangeException(nameof(e), e, "未知档位 —— 新增枚举成员必须在这里补一格"),
    };

    /// <summary>AVIF（avifenc 软件路）的 <c>-s</c> 值。avifenc 语义：<b>0 最慢、10 最快</b>。
    /// <para>⚠ AVIF 的界面是<b>三档</b>（用户 2026-10-29 口径），<see cref="CompressionEffort.Quick"/>
    /// 在这里不可达，但必须显式给值 —— 落到 <c>_</c> 分支就等于"选了 Quick 实际跑均衡"，
    /// 而这种事不会报错、只会表现成"某个档位对某个格式没生效"。</para></summary>
    public static int AvifencSpeed(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 0,   // 7.2 s / 110.2 KB（再配 tune=ssim ⇒ 93.7 KB）
        CompressionEffort.Quick => 3,      // 不可达，取 0 与 6 之间的中点
        CompressionEffort.Fastest => 10,   // 0.59 s / 180.1 KB
        _ => 6,                            // 0.94 s / 144.3 KB —— 与改动前的硬编码值一致
    };

    /// <summary>「最小体积」档额外下发给 libaom 的高级选项（<c>-a key=value</c>）。
    /// <para><b>为什么是 <c>tune=ssim</c></b>：4K 实测它是唯一真正减小体积的小参 ——
    ///   110.2 → <b>93.7 KB（−15%）</b>，而且<b>不额外变慢</b>（7.2 → 6.5 s）。
    ///   逐项排除过：<c>aq-mode=2</c> / <c>sharpness=7</c> / <c>enable-chroma-deltaq=1</c>
    ///   在本内容上<b>零效果</b>（体积一位不变），全开还会互相干扰（102.5 KB，比只开 ssim 更差）。
    ///   机理：libaom 对"非 RGB 静止图像"默认选 <c>tune=iq</c>（感知指标），
    ///   而截图这类人工合成内容用 <c>ssim</c> 的率失真取舍更划算。</para>
    /// <para>⚠ <b>只在「最小体积」档下发</b>：它是"极限档"的一部分。若写死则连默认档一起改，
    ///   就违背了"均衡档 == 改动前行为"（AVIF 用户产物会静默变小、体积/耗时都变）。</para>
    /// <para>⚠ 调用方必须确认当前 codec 是 libaom：SVT-AV1 / NVENC 不认 aom 的 key，
    ///   avifenc 会对未使用的 key 报警告。见 <c>AvifencAdvancedArgs</c> 的用法。</para></summary>
    public static IReadOnlyList<string> AvifencAdvancedArgs(CompressionEffort e) =>
        e == CompressionEffort.Smallest ? ["-a", "tune=ssim"] : [];

    /// <summary>JPEG XL 的 <c>-e</c> 值。cjxl 语义：<b>越大越慢</b>，范围 1..10。
    /// <para>⚠ <b>三档只能到 7，不能"拉满"</b>。实测两张真实 4K 截图（2026-10-07）：</para>
    /// <list type="bullet">
    /// <item>样张 A：e7 255.2 KB → e8 <b>241.3 KB</b>（省 5.4%）</item>
    /// <item>样张 B：e7 490.8 KB → e8 <b>534.7 KB</b>（<b>涨 8.9%</b>）</item>
    /// </list>
    /// <para>同一个参数、不同内容、方向相反 ⇒ <b>无法承诺"拉满一定更小"</b>。
    /// 用户在按下快门时无法预知自己属于哪一类，所以给一个"保证最小"的高 effort 是假承诺。
    /// 另：<c>--brotli_effort=11</c> / <c>--buffering=0</c> / <c>-P 15 -E 11</c> 实测对体积
    /// 零效果（241.5 → 241.2 KB），却把时间从 6.3 s 抬到 13.1 s，故不下发。</para>
    /// <para>该档的真实价值在<b>时间</b>：默认档 e5 把 25 秒降到 1 秒内而体积不变。</para></summary>
    public static int JxlEffort(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 7,   // 0.36–0.88 s / 255.2–490.8 KB（见上：不能再高）
        CompressionEffort.Quick => 6,      // 不可达（JXL 界面保持三档），取 7 与 5 的中点
        CompressionEffort.Fastest => 3,    // 0.26–0.64 s / 258.8–490 KB
        _ => 5,                            // 0.31–0.82 s / 253.7–491 KB
    };

    /// <summary>WebP 的 <c>-m</c> 值。cwebp 语义：<b>0 最快、6 最慢</b>。
    /// <para>⚠ <b>不要再加其它小参</b>。实测（4K）：<c>-pass 10</c> 让时间从 0.47 s 涨到 3.53 s
    /// 而体积<b>反而变大</b>（205.0 → 205.7 KB）；<c>-af -sharp_yuv</c> 208.2 KB；
    /// <c>-sns 100</c> 213.4 KB —— 全是负收益。cwebp 的 pass 会重写 <c>-sns</c>/<c>-f</c> 等
    /// 感知参数，把 <c>-q</c> 已调好的取舍改坏。</para>
    /// <para>唯一略有正面的是 <c>-preset photo</c>（200.8 KB，−2%，同速），但它会覆盖其它参数、
    /// 且语义是"内容类型提示"而非压缩强度（<c>-preset drawing</c> 反而是 210.2 KB 更大）
    /// ⇒ 不作为档位的一部分下发。</para></summary>
    public static int WebPMethod(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 6,   // 0.47–0.95 s / 248.3–205.0 KB
        CompressionEffort.Quick => 5,      // 2026-10-29 实测 cwebp 1.5.0 真截图：158ms / 29650B
                                           //（对 m6 的 179ms / 29166B：省 12% 时间、多 1.7% 体积）
        CompressionEffort.Fastest => 2,    // 0.42 s / 267.8 KB
        _ => 4,                            // 0.74 s / 258.2 KB —— 与 cwebp 默认值一致（改动前就是它）
    };

    /// <summary>NVENC 的 preset 序号（1..7）。NVENC 语义：<b>P1 最快、P7 最慢（同质量下更小）</b>。
    /// <para>⚠ 与 avifenc 的 0..10 <b>不是同一把尺</b>，此处只是"各档的对应关系"。
    /// 硬件路只有 7 档且 GPU 上极快，所以三档之间的时间差远小于软件路。</para></summary>
    public static int NvencPresetNumber(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 7,
        CompressionEffort.Quick => 5,      // 不可达（AVIF 界面保持三档）；硬件路三档差本来就小
        CompressionEffort.Fastest => 1,
        _ => 4,
    };

    /// <summary>「最小体积」档是否给 cjpegli 加 <c>--xyb</c>（JPEG LI）。
    /// <para><b>为什么加</b>：2026-10-07 实测（两张真实截图 × 整个质量区间 + GainMap 的 Baseline 路）
    /// 在那批内容上<b>全部更小</b>，无一例外：</para>
    /// <list type="bullet">
    /// <item>样张 A：-d 0.5 <b>−4.7%</b>、-d 1.0 −2.9%、-d 2.0 −3.8%、-d 3.0 <b>−4.6%</b></item>
    /// <item>样张 B：-d 0.5 −4.0%、-d 1.0 −2.4%、-d 3.0 −3.5%</item>
    /// <item>GainMap 的 Baseline 路（<c>-p 0</c>）：−1.5%（同样有效）</item>
    /// </list>
    /// <para>⚠ <b>2026-10-29 换一批新样张复测，"无一例外"这个全称断言被推翻了</b>
    /// （三张新的真实截图、-d 1.0，原始数据 <c>artifacts/tier_bench_2026-10-29.log</c>）：
    /// 开 <c>--xyb</c> 的两张分别小 <b>5.5% / 10.4%</b>，第三张【反而大 <b>3.0%</b>】。
    /// ⇒ 正确的说法是"在 5 个测过的样本里 4 个更小，收益 −1.5%~−10.4%，代价约 1.2~1.3 倍时间"，
    /// 而不是"一律更小"。当年那两个数（1.5%~4.7%、1.5~1.8 倍）仍然成立，只是各自只描述当年那批内容。
    /// 机理不变：XYB 是感知均匀的色彩空间，量化误差按人眼敏感度分布 ⇒ 同等感知质量下通常更省码率，
    /// 但"通常"不等于"总是"，这里就是一个反例。</para>
    /// <para>⚠ <b>只在「最小体积」档加</b>：它是极限档的一部分。若写死则连别的档一起变。</para>
    /// <para>⚠ 与 AVIF 的 <c>tune=ssim</c> 不同，<c>--xyb</c> 是 cjpegli 的**顶层开关**，
    /// 不需要"仅在某 codec 下"的前置判断。</para></summary>
    public static bool JpegLiUseXyb(CompressionEffort e) => e == CompressionEffort.Smallest;

    /// <summary>JPEG LI 的 <c>--xyb</c> 参数串（空串 = 不加）。</summary>
    public static string JpegLiXybArg(CompressionEffort e) => JpegLiUseXyb(e) ? " --xyb" : "";

    /// <summary>PNG / TIFF 的无损 deflate 档位（2026-10-29 用户口径新增，同日定为两档）。
    /// <para><b>为什么这两个格式要这一档</b>：它们是全仓最慢的两条，而慢就慢在 deflate 一步。
    /// 本机实测（同一份 raw 只换 level 重压，两轮复现，体积按 KB）：</para>
    /// <list type="bullet">
    /// <item>4K HDR 10-bit 噪声（raw 63.3 MB）：<b>level 9 = 20.60s / 39046KB</b> → 8 = 1.99s / 38317KB
    /// （<b>快 10.4 倍还小 1.9%</b>）→ 6 = 1.53s / 39409KB（+0.9%）→ 1 = 0.62s / 53300KB（+36.5%）</item>
    /// <item>真截图（raw 2.4 MB）：9 = 0.41s / 334KB → 8 = 0.21s / 335KB（+0.3%）→ 6 = 0.04s / 346KB → 1 = 0.01s</item>
    /// <item>真截图（raw 11.3 MB）：9 = 0.25s / 254KB → 8 = 0.07s / <b>249KB（−1.8%，比拉满还小）</b> → 6 = 0.02s / 251KB</item>
    /// </list>
    /// <para>⚠ <b>"拉满不保证更小"在这里直接成立</b>：三个样本里 level 8 两次比 9 更小、一次只大 0.3%，
    /// 却快 2~10 倍 ⇒ 档位的名字只描述"我们最慢/最拼的那一档"，不承诺严格最小。</para>
    /// <para><b>返回 int 而不是 <c>CompressionLevel</c> 枚举</b>：反射实测 .NET 那个枚举只有 4 个成员
    /// （NoCompression / Fastest / Optimal / SmallestSize，<b>序号是 0/1/2/3，不是 zlib level</b>，
    /// 运行时把它们分别翻译成 level 0/1/6/9），因此<b>表达不了 level 8</b> —— 而 8 恰是用户要的
    /// "稍微放宽一点参数"那一档。
    /// 故走 <c>ZLibStream(Stream, ZLibCompressionOptions)</c>：它的 <c>CompressionLevel</c> 是可写 int，
    /// −1 即 zlib 默认（= 6）。⚠ 本运行时会把 level 分桶：<b>7 与 8 产物逐字节相同、5 与 6 相同</b>
    /// ⇒ 真正可用的点只有 9 / 8 / 6 / 1，档位表只在这四个里取，不要以为能调到 7。
    /// ⚠ 也别把"序号 3 == level 9"当成可以换算的关系：枚举↔level 的对应只能由产物对撞来证明，
    /// 见 <c>CorePipelineTests.Test_PerfWiring_DeflateOptionsEqualsEnum</c>。</para>
    /// <para><b>档位只改 deflate 的搜索强度</b>（<c>max_chain</c> 128→4096、<c>max_lazy</c> 16→258 等），
    /// 码流格式不变 ⇒ 解压器看不出区别，任何一档的产物都同样合法、同样无损；
    /// 判据把"四个档位解出的扫描线逐字节相同"钉住了这一点。</para>
    /// <para><b>界面按用户口径只给两格</b>：显示「均衡」= Tag <c>Smallest</c>（level 9，默认，其实就是拉满）
    /// 与显示「快速」= Tag <c>Quick</c>（level 8）。
    /// ⚠ 两格的显示名不描述参数深度（本格式的"均衡"就是最慢那一档），界面文案因此一律不写实测数字——
    ///   快慢与体积代价随机器变，数字只留在源码与本注释里。</para>
    /// Smallest == 这两个格式纳入档位之前的硬编码 level 9 ⇒ 没动过设置的人**解压后**逐字节不变。
    /// ⚠ 2026-10-31 起压缩字节可能来自可选的 libdeflate（`DeflateZlib`）：高熵内容才外包，
    ///   可压缩内容仍走内置 zlib-ng 9（外包会让 UI 型产物大 7.7%）。
    /// Balanced / Fastest 两格不可达但必须显式有值（落到默认分支= "选了却不生效"）；
    /// 编码器侧的默认参数同样是 Smallest，所以不传档位的调用点（保底 PNG、诊断件、测试夹具）也解压后不变。
    /// 界面 IsSelected 与 <c>AppSettingsData</c> 的字段默认必须同源，判据逐下拉对撞。</para></summary>
    public static int PngTiffDeflateLevel(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 9,  // = 2026-10-29 之前硬编码的 CompressionLevel.SmallestSize
        CompressionEffort.Quick => 8,     // 用户要的"稍微放宽"：快 2~10 倍，体积 −1.9% ~ +0.3%
        CompressionEffort.Balanced => 6,  // 不可达（PNG/TIFF 界面两格），保留定义
        CompressionEffort.Fastest => 1,   // 不可达，保留定义：+36.5% 体积（平直内容能翻倍）
        _ => throw new System.ArgumentOutOfRangeException(nameof(e), e, "未知档位 —— 新增枚举成员必须在这里补一格"),
    };
}
