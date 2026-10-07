// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/CompressionEffort.cs
// 「压缩强度」三档 —— AVIF / JPEG XL / WebP 的 effort/speed 旋钮（2026-10-07 用户口径）
//
// ═══════════════════════════════════════════════════════════════════════════════
//  为什么只有这三个格式有这一档
// ═══════════════════════════════════════════════════════════════════════════════
// 只有 AVIF / JPEG XL / WebP 的编码器有【独立于质量】的 effort/speed 旋钮：
//   avifenc -s 0..10（0 最慢）、cjxl -e 1..10、cwebp -m 0..6、NVENC preset P1..P7。
// 而 JPEG LI 与 PNG 的"压缩强度"与【质量】是同一个旋钮（butteraugli 距离 / 无损压缩级别），
// 再开一档等于给同一个东西开第二个入口，会与质量滑块打架 ⇒ 它们不加这一档。
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

/// <summary>压缩强度三档。作用于 AVIF / JPEG XL / WebP / JPEG LI 的 effort 旋钮（不动质量参数）。
/// <para>档位名按<b>用户视角的结果</b>命名（体积优先 / 均衡 / 速度优先），而不是按底层参数名 ——
/// 底层各家的参数名、方向、量纲都不同（avifenc 越小越慢、cjxl 越大越慢、cwebp 越大越慢、
/// NVENC P 越大越快、cjpegli 是布尔开关），照参数名命名必然误导。</para>
/// <para>⚠ <b>JPEG LI 是后来加进来的（2026-10-07，用户指出"--xyb 是正收益"）</b>：
/// 它此前被排除在"压缩强度"之外（理由：它的压缩强度与质量是同一个旋钮）。
/// 但 <c>--xyb</c> 并非质量旋钮 —— 它换的是色彩空间，实测在**所有内容与质量上都更小**，
/// 故按用户口径纳入。PNG / TIFF 仍不纳入（它们确实只有质量/无损级别这一个旋钮）。</para></summary>
public enum CompressionEffort
{
    /// <summary>体积优先：编码最慢，产物最小。</summary>
    Smallest = 0,

    /// <summary>均衡（默认）：速度与体积的折中。</summary>
    Balanced = 1,

    /// <summary>速度优先：编码最快，体积最大。</summary>
    Fastest = 2,
}

/// <summary>档位 → 各编码器参数的<b>唯一</b>映射处。
/// <para>为什么集中在这里：四个消费方（avifenc 软件路 / NVENC 硬件路 / cjxl / cwebp）分处四个文件，
/// 若各自 switch 一遍，"改档位只改了一处"就是迟早的事，而这种漏改**不会报错**、
/// 只表现为"某个档位对某个格式没生效"。集中成一处的代价是四个调用点都要改传参，
/// 收益是映射规则可以被单测逐格钉住（见 CorePipelineTests 的档位判据）。</para></summary>
public static class CompressionEffortMap
{
    /// <summary>把持久化/界面的字符串（"Smallest"/"Balanced"/"Fastest"）解析成档位。
    /// <para>空/未识别一律回落 <see cref="CompressionEffort.Balanced"/>（默认档），
    /// 与 <c>SettingsService.Normalize</c> 对其它枚举的处理口径一致：坏值不许抛，
    /// 但也不许静默变成"最慢档"（那会让一次设置损坏变成每张图多等 25 秒）。</para></summary>
    public static CompressionEffort Parse(string? s) => s switch
    {
        "Smallest" => CompressionEffort.Smallest,
        "Fastest" => CompressionEffort.Fastest,
        "Balanced" => CompressionEffort.Balanced,
        _ => CompressionEffort.Balanced,
    };

    /// <summary>档位 → 持久化字面量（设置文件里那个值，也是界面 Tag）。</summary>
    public static string ToTag(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => "Smallest",
        CompressionEffort.Fastest => "Fastest",
        _ => "Balanced",
    };

    /// <summary>AVIF（avifenc 软件路）的 <c>-s</c> 值。avifenc 语义：<b>0 最慢、10 最快</b>。</summary>
    public static int AvifencSpeed(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 0,   // 7.2 s / 110.2 KB（再配 tune=ssim ⇒ 93.7 KB）
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
        CompressionEffort.Fastest => 2,    // 0.42 s / 267.8 KB
        _ => 4,                            // 0.74 s / 258.2 KB —— 与 cwebp 默认值一致（改动前就是它）
    };

    /// <summary>NVENC 的 preset 序号（1..7）。NVENC 语义：<b>P1 最快、P7 最慢（同质量下更小）</b>。
    /// <para>⚠ 与 avifenc 的 0..10 <b>不是同一把尺</b>，此处只是"各档的对应关系"。
    /// 硬件路只有 7 档且 GPU 上极快，所以三档之间的时间差远小于软件路。</para></summary>
    public static int NvencPresetNumber(CompressionEffort e) => e switch
    {
        CompressionEffort.Smallest => 7,
        CompressionEffort.Fastest => 1,
        _ => 4,
    };

    /// <summary>「最小体积」档是否给 cjpegli 加 <c>--xyb</c>（JPEG LI）。
    /// <para><b>为什么加</b>：实测（2026-10-07，两张真实截图 × 整个质量区间）
    /// <c>--xyb</c> <b>一律更小</b>，无一例外：</para>
    /// <list type="bullet">
    /// <item>样张 A：-d 0.5 <b>−4.7%</b>、-d 1.0 −2.9%、-d 2.0 −3.8%、-d 3.0 <b>−4.6%</b></item>
    /// <item>样张 B：-d 0.5 −4.0%、-d 1.0 −2.4%、-d 3.0 −3.5%</item>
    /// <item>GainMap 的 Baseline 路（<c>-p 0</c>）：−1.5%（同样有效）</item>
    /// </list>
    /// <para>这是本仓全部编码器小参里**唯一一个在所有测过的内容与质量上都为正收益**的开关
    /// （对照：cjxl 的高 effort 有正有负、cwebp 的小参基本为负）。</para>
    /// <para>机理：XYB 是感知均匀的色彩空间，量化误差按人眼敏感度分布，
    /// 因此在同等感知质量下码率更低。</para>
    /// <para>⚠ <b>只在「最小体积」档加</b>：代价是编码时间约 1.5–1.8×（例：-d 1.0 由 0.11 s → 0.16 s）。
    /// 若写死则连默认档一起变（默认档的承诺是"与改动前逐字节一致"）。</para>
    /// <para>⚠ 与 AVIF 的 <c>tune=ssim</c> 不同，<c>--xyb</c> 是 cjpegli 的**顶层开关**，
    /// 不需要"仅在某 codec 下"的前置判断。</para></summary>
    public static bool JpegLiUseXyb(CompressionEffort e) => e == CompressionEffort.Smallest;

    /// <summary>JPEG LI 的 <c>--xyb</c> 参数串（空串 = 不加）。</summary>
    public static string JpegLiXybArg(CompressionEffort e) => JpegLiUseXyb(e) ? " --xyb" : "";
}
