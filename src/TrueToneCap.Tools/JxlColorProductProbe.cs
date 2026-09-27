// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/JxlColorProductProbe.cs
// 产品代码路径上的 JXL 色彩声明生效性探针 (--jxl-color-product)
//
// 为什么需要它（2026-09-22，交接 §44）：`scripts/precision/jxl_color_space_discrimination.py`
// 证的是"cjxl 认这些枚举值"，用的输入是我手搓的 P6 PPM。而产品路径的中转文件是
// **我们自己写的**（8-bit 走 WriteBgraToPpm，≥10-bit 走 ManagedPngEncoder.Encode16 的 16-bit PNG）。
// 只要那个中转文件带上任何色彩元数据（iCCP / sRGB / gAMA / cHRM），`-x color_space=` 就会被
// cjxl 当作"输入已有信息"而**完全忽略** —— 实测那种情况下 8 种取值产出 md5 全同。
// 所以"改了映射表"不等于"产物变了"，必须拿产品入口自己证一次。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -- --jxl-color-product [jxlinfo.exe;djxl.exe]
//   HDR 那一层要第三方解码器读回容器声明（交接 §31.3-5 当年就卡在"本机无 JXL 解码器"），
//   定位顺序与 GainMap 第 4 层同一套：环境变量 TTC_JXLINFO / TTC_DJXL > 命令行 > 仓库内 + PATH。
// 退出码: 0 = 每条位深路上、不同目标标签都产出不同字节，且 HDR 产物的容器声明与 PQ 像素一致；
//   1 = 有相同（= 提示没抵达 cjxl）或 HDR 声明与像素矛盾；
//   3 = SDR 层判了且没红，但 HDR 层【本轮没判】（第三方解码器没探到 / 探到了却干不了活）。
//   cjxl 本身没有 SKIP 分支：它是仓库自带的（产品运行时用的就是那一份），
//   探不到就是检出缺件 ⇒ 判红而不是"没判"，这一点与 GainMap 探针第 4 层相反（那里是仓库外工具）。

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TrueToneCap.Core.Encoding;

public static class JxlColorProductProbe
{
    static string H(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..16];

    public static int Run(string[] args)
    {
        if (!NativeJxlEncoder.IsAvailable)
        {
            Console.WriteLine("❌ cjxl.exe 不可用 ⇒ 本轮没有判据（不是通过）");
            return 1;
        }

        const int W = 64, H2 = 64;
        var bgra = new byte[W * H2 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 200; bgra[i + 1] = 90; bgra[i + 2] = 40; bgra[i + 3] = 255; }

        var dir = Path.Combine(Path.GetTempPath(), "ttc_jxl_product_probe");
        // 每次从空目录开始：残留的 .jxl 会让"本轮没编码成功"被读成通过（判据比的是本轮 md5）
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // 表里的三个不同落点 + 一个"未知标签"（应当兜到 sRGB，与 sRGB 同字节 ⇒ 证明永不吐出非法值）
        // + 一个"别名标签" AdobeRGB：2026-09-27 用户口径要求它收口进 Rec.2020，
        //   所以它的产物字节必须与 BT2020 【完全相同】、与 sRGB 不同（判据 3）。
        //   这条探的是编码器的直接入口（绕过 FormatEncoders 的收口），因此它量的就是映射表本身；
        //   上游收口是否也在做，由 CorePipelineTests 里那条"不许兜成 sRGB"的映射断言管。
        var tags = new (string? tag, string label)[]
        {
            ("sRGB",     "sRGB"),
            ("BT2020",   "BT2020 → RGB_D65_202_Rel_SRG"),
            ("DisplayP3","DisplayP3"),
            ("XYZZY",    "未知标签（应兜 sRGB）"),
            ("AdobeRGB", "别名 AdobeRGB（应=BT2020）"),
        };

        int fail = 0;
        foreach (var (depth, name) in new (int, string)[] { (8, "8-bit / PPM 中转"), (10, "10-bit / 16-bit PNG 中转") })
        {
            Console.WriteLine($"\n── {name}（产品入口 NativeJxlEncoder.{(depth == 8 ? "Encode" : "Encode16Bgra16")}）──");
            var hashes = new List<(string label, string hash, string file)>();
            foreach (var (tag, label) in tags)
            {
                var f = Path.Combine(dir, $"d{depth}_{label.Replace(" ", "_").Replace("→", "-")}.jxl");
                // 先删同名旧产物：判据比的是【本轮字节的 md5】，上一轮留下的文件会直接顶替"没编码成功"
                if (File.Exists(f)) File.Delete(f);
                try
                {
                    if (depth == 8)
                        NativeJxlEncoder.Encode(bgra, W, H2, f, 1.0f, 8, null, null, tag);
                    else
                        NativeJxlEncoder.Encode16Bgra16(FormatHelper.Bgra8ToBgra16BE(bgra), W, H2, f,
                            1.0f, 10, null, null, tag);
                    hashes.Add((label, H(f), Path.GetFileName(f)));
                    Console.WriteLine($"  {label,-34} md5={H(f)}  {new FileInfo(f).Length} B");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {label,-34} ❌ 抛异常 {ex.GetType().Name}: {ex.Message}");
                    fail++;
                }
            }

            // 判据 1：sRGB / BT2020 / DisplayP3 三者必须两两不同（"未知"与"别名"不参与，它们要的是相等）
            var core = hashes.Where(x => !x.label.StartsWith("未知") && !x.label.StartsWith("别名")).ToList();
            foreach (var a in core)
                foreach (var b in core)
                    if (string.CompareOrdinal(a.label, b.label) < 0 && a.hash == b.hash)
                    {
                        Console.WriteLine($"  ❌ {a.label} 与 {b.label} 字节相同 ⇒ color_space 没抵达 cjxl（中转文件带了元数据？）");
                        fail++;
                    }
            // 判据 2：未知标签必须与 sRGB 同值（兜底合法，不吐非法枚举）
            var s = core.FirstOrDefault(x => x.label == "sRGB").hash;
            var u = hashes.FirstOrDefault(x => x.label.StartsWith("未知")).hash;
            if (u != s)
            {
                Console.WriteLine($"  ❌ 未知标签没兜到 sRGB（{u} vs {s}）");
                fail++;
            }
            else Console.WriteLine("  ✅ 未知标签兜到 sRGB（不会吐出 cjxl 不认的值）");

            // 判据 3（2026-09-27 用户口径）：AdobeRGB 收口成 Rec.2020 ⇒ 产物必须与 BT2020 逐字节相同，
            // 且必须与 sRGB 不同（"退回 sRGB"那份旧口径若被改回去，这条当场红）。
            var bt = core.FirstOrDefault(x => x.label.StartsWith("BT2020")).hash;
            var ad = hashes.FirstOrDefault(x => x.label.StartsWith("别名")).hash;
            if (ad is null || bt is null)
            {
                Console.WriteLine("  ❌ 别名/BT2020 那一格没产出产物 ⇒ 这一条没判（不是通过）");
                fail++;
            }
            else if (ad != bt)
            {
                Console.WriteLine($"  ❌ AdobeRGB 的产物与 BT2020 不同（{ad} vs {bt}）"
                    + (ad == s ? " 且它等于 sRGB ⇒ 旧口径「Adobe 退回 sRGB」被改回来了" : ""));
                fail++;
            }
            else if (ad == s)
            {
                Console.WriteLine("  ❌ AdobeRGB 与 BT2020 同字节、却又与 sRGB 同字节 ⇒ 三格塌成一格，判据失效");
                fail++;
            }
            else Console.WriteLine("  ✅ AdobeRGB 的产物 = BT2020（收口进 Rec.2020），且确实不同于 sRGB");
        }

        Console.WriteLine(fail == 0
            ? "\n✅ 产品路径上：不同目标标签 ⇒ 不同字节（两条位深路各 3 种），中转文件没有吞掉色彩提示"
            : $"\n❌ {fail} 条不成立 ⇒ §44 的改动在这条路上是空转，别当成已修");
        Console.WriteLine($"   产物留在 {dir}（可直接喂判别脚本或以后接 djxl 复核语义）");

        // ═══ HDR 层（交接 §31.3-5：HDR + DisplayP3 那一路的容器声明到底剩下什么）═══
        // 判红优先于 SKIP：SDR 层已经红了就没有"这轮没判"可谈。
        int hdr = fail > 0 ? 0 : HdrLayer(args);
        if (hdr == 1) return 1;
        if (hdr == 3)
        {
            Console.WriteLine("   ⇒ SDR 那两层判了且通过；HDR 这一层【本轮没判】—— 整体按 SKIP 记账，不算绿");
            return 3;
        }
        return fail == 0 ? 0 : 1;
    }

    // ════════════════════════════════════════════════════════════════════
    //  HDR 层：产品自己的 HDR 入口编码 ⇒ 第三方 jxlinfo 读回容器声明
    // ════════════════════════════════════════════════════════════════════
    /// <remarks>
    /// 要回答的问题（交接 §31.3-5 / §32.9-6 第 5 行，当年"卡在没有 JXL 解码器"）：
    /// HDR(PQ) 像素 + DisplayP3 目标这一路，产品会走到 <c>iccArg</c> 非空的分支
    /// （<c>FormatEncoders.cs</c> 的 <c>jxlIcc = csTag is "DisplayP3" or "DCI_P3" ? …</c>），
    /// 而 <c>NativeJxlEncoder.EncodeHdr</c> 现在是"有 ICC 就【只】交 ICC、不再交 color_space=Rec2100PQ"。
    /// 于是"ICC 与 color fields 并存"这个字面前提在本轮已不成立，但真正要问的是：
    /// <b>容器里最后剩下的那份声明，传递函数还是不是 PQ？</b>
    /// 交进去的 P3 profile 是 <c>PatchSrgbPrimaries(…)</c> —— 只换 rXYZ/gXYZ/bXYZ/desc，
    /// <b>TRC 沿用 sRGB 模板那条 256 点 curv</b> ⇒ 若它成为唯一声明，就是"用 SDR 曲线装 PQ 值"。
    /// 这一层不猜：把产品写出的文件交给 libjxl 自己的 <c>jxlinfo</c> 读回来判。
    /// </remarks>
    static int HdrLayer(string[] args)
    {
        // ── 前置：第三方读回工具（仓库外二进制 ⇒ 探不到就 SKIP，不自家解析器自证）──
        var infoCands = KeepExeNamed(ExternalToolLocator.Candidates(
            "TTC_JXLINFO", args, 1, "jxlinfo.exe", out string probedInfo), "jxlinfo.exe");
        var djlCands = KeepExeNamed(ExternalToolLocator.Candidates(
            "TTC_DJXL", args, 1, "djxl.exe", out string probedDjxl), "djxl.exe");
        Console.WriteLine("\n── HDR 层（产品入口 JpegXlEncoder.EncodeAsync / HdrOutput=true）──");
        if (infoCands.Count == 0 || djlCands.Count == 0)
        {
            Console.WriteLine($"  ⏭ 跳过 (SKIP)：jxlinfo 找过 {probedInfo}"
                + $" / djxl 找过 {probedDjxl}");
            Console.WriteLine("     补法：设 TTC_JXLINFO / TTC_DJXL 指向 libjxl 构建出的 exe"
                + "（要能加载同目录的 jxl.dll），或把路径作为 --jxl-color-product 的后置参数递进来");
            return 3;
        }

        const int W = 32, H2 = 32;
        // 两档中性灰：203 nits（HDR 容器里的 SDR 参考白）与 100 nits。
        // 取中性是故意的 —— 色域矩阵不会把中性搬走，于是"读回值 vs 声明"只差在传递函数这一维。
        // scRGB 线性 → nits 的 ×80 换算出自 FormatHelper.LinearToPQ，不在这里另立一份口径。
        var pixels = new float[W * H2 * 4];
        for (int y = 0; y < H2; y++)
        {
            float v = (y < H2 / 2 ? 203f : 100f) / 80f;
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                pixels[i] = v; pixels[i + 1] = v; pixels[i + 2] = v; pixels[i + 3] = 1f;
            }
        }
        var frame = new HdrFrameData { Pixels = pixels, Width = W, Height = H2 };

        // 产物目录：门禁样张目录 artifacts/precision/ 是另一条 lane 与门禁共用的，不往里写。
        var outDir = Environment.GetEnvironmentVariable("TTC_JXL_HDR_OUT");
        if (string.IsNullOrWhiteSpace(outDir))
            outDir = Path.Combine(RepoRoot(), "tests", "output", "jxl-hdr-p3-2026-09-27");
        Directory.CreateDirectory(outDir);

        // 产品交给 cjxl 的那份 P3 ICC，以及 HDR 中转用的 16-bit PNG —— 原样落盘，
        // 让外部脚本判"并存时谁赢"用的就是产品自己那份字节，而不是脚本另搓的近似物。
        var p3Icc = TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetStandardIccProfile("DisplayP3");
        var p3IccPath = Path.Combine(outDir, "product_p3_icc.icc");
        if (p3Icc is not null) File.WriteAllBytes(p3IccPath, p3Icc);
        // 顺手把"另一条可选写法"也落盘：HDR 家族里唯一带 PQ 曲线的自产 profile（TIFF 那一路用的就是它）。
        // 本探针【不】把它接进产品路径 —— 接不接是维护者的决定，这里只让取证脚本能拿同一份字节试一次。
        var pqIcc = TrueToneCap.Core.ColorManagement.ColorProfileProvider.GetHdrStandardIccProfile("DisplayP3");
        if (pqIcc is not null)
            File.WriteAllBytes(Path.Combine(outDir, "candidate_p3_pq_icc.icc"), pqIcc);
        var pq16ForDump = FormatHelper.HdrToPq16(frame, "DisplayP3", 16);
        var tmpPngPath = Path.Combine(outDir, "product_input.png");
        ManagedPngEncoder.Encode16(
            FormatHelper.Rgba16ToBgra16Bytes(pq16ForDump, W, H2), W, H2, tmpPngPath);

        // (目标标签, 是否带用户/屏幕 ICC) × 判据。DisplayP3/DCI_P3 是缺陷点名的那一格；
        // AdobeRGB 探 HdrDeclarableTag 的收口；sRGB / 未知标签探"其余一律 Rec2100PQ"那条兜底；
        // 最后两格探"ICC 在不在手上"这个分支的两种走向。
        var cases = new (string tag, byte[]? icc, string name, string note)[]
        {
            ("BT2020",    null,    "hdr_BT2020",        "Rec.2020 目标（应走 color fields）"),
            ("DisplayP3", null,    "hdr_DisplayP3",     "§31.3-5 点名的那一格：P3 ICC 兜底声明"),
            ("DCI_P3",    null,    "hdr_DCI_P3",        "同族标签，产物应与 DisplayP3 逐字节相同"),
            ("AdobeRGB",  null,    "hdr_AdobeRGB",      "HDR 侧收口成 BT2020（HdrDeclarableTag）"),
            ("sRGB",      null,    "hdr_sRGB",          "非广色域目标：仍交 color_space=Rec2100PQ"),
            ("XYZZY",     null,    "hdr_XYZZY",         "未知标签：HDR 路不走 SDR 那张映射表"),
            ("DisplayP3", p3Icc,   "hdr_DisplayP3_userIcc", "手上已有可嵌 ICC 时同一分支"),
            ("BT2020",    p3Icc,   "hdr_BT2020_withP3Icc",  "⚠ 探的是「广色域 tag 忽略手上的 ICC」这一格"),
        };

        var rows = new List<(string name, string file, string hash, string bytes,
            string transfer, string primaries, string iccLine, int? intensity)>();
        int fail = 0;

        foreach (var c in cases)
        {
            var path = Path.Combine(outDir, c.name + ".jxl");
            if (File.Exists(path)) File.Delete(path);   // 残留文件会把"没编成功"读成判过
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_XL,
                Quality = 1.0f,
                HdrOutput = true,
                ColorSpaceTag = c.tag,
                OutputBitDepth = 16,
                IccProfile = c.icc,
                ChromaSubsampling = "444",
            };
            try
            {
                new JpegXlEncoder().EncodeAsync(frame, settings, path)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {c.name,-24} ❌ 产品入口抛异常 {ex.GetType().Name}: {ex.Message}");
                fail++;
                continue;
            }

            // 第三方读回：jxlinfo 区分"内嵌 N-byte ICC"与"color fields 三件套"，
            // 这是"ICC 与 fields 二选一里剩下哪一个"的唯一有效判据
            //（JXL 的 ICC 走 delta 压缩 ⇒ grep acsp 这种负断言无效，见交接 §56.4）。
            // ⚠ 两遍都要跑：本机 libjxl v0.12 的 jxlinfo 只在【不带 -v】时打
            //   "Intensity target / Min nits"那一段（实测 v0.12.0 a7a9c78，带 -v 反而少了这四行），
            //   而 -v 多出来的是通道数与 alpha —— 拼起来才是完整读数，别假定 verbose 是超集。
            string? outText = null;
            foreach (var cand in infoCands)
            {
                var r = RunProc(cand, $"\"{path}\"");
                var rv = RunProc(cand, $"-v \"{path}\"");
                if (r.Exit == 0 && rv.Exit == 0 && r.Out.Contains("JPEG XL"))
                {
                    outText = r.Out + "\n" + rv.Out;
                    break;
                }
                Console.WriteLine($"      候选不可用：{cand} (exit=0x{r.Exit:X8}/0x{rv.Exit:X8}) {r.Err.Trim()}");
            }
            if (outText is null)
            {
                Console.WriteLine($"  ⏭ 跳过 (SKIP)：{infoCands.Count} 个 jxlinfo 候选都没能读出这一枚"
                    + "（多半是同目录缺 jxl.dll）⇒ HDR 层本轮没判");
                return 3;
            }
            string transfer = Grab(outText, @"Transfer function:\s*(.+)");
            string primaries = Grab(outText, @"Primaries:\s*(.+)");
            string iccLine = Grab(outText, @"^(\d+-byte ICC profile.*)$", multiline: true);
            // jxlinfo 打的是 "Intensity target: 1000.000000 nits" ⇒ 是小数，别按 int 解
            //（int.TryParse 在这条上静默失败过一轮，读数全成 "-"）。
            int? intensity = double.TryParse(Grab(outText, @"Intensity target:\s*([\d.]+)"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double it) ? (int)it : (int?)null;
            long bytes = new FileInfo(path).Length;
            rows.Add((c.name, Path.GetFileName(path), H(path), $"{bytes} B",
                transfer, primaries, iccLine, intensity));
            Console.WriteLine($"  {c.name,-24} {bytes,6} B  sha={H(path)[..8]}  "
                + $"Transfer=[{transfer}]  Primaries=[{TrimCustom(primaries)}]  "
                + $"ICC=[{(iccLine.Length > 0 ? iccLine : "无内嵌，读回的是 color fields")}]  "
                + $"intensity={intensity?.ToString() ?? "-"} nits   ← {c.note}");
        }

        if (rows.Count == 0) return fail > 0 ? 1 : 3;

        // ── 判据 1：像素是 PQ ⇒ 容器声明的传递函数必须是 PQ ──
        // 这条就是 §31.3-5 的实质："声明 HDR 的图里嵌了一份 SDR 曲线的 profile"。
        // 红 = 成立（PQ 值被按 sRGB 曲线解释）；不是把期望调成现状来换绿灯。
        foreach (var r in rows)
        {
            if (!r.transfer.Contains("PQ", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  ❌ {r.name}：像素是 PQ（产品走 FormatHelper.HdrToPq16），"
                    + $"容器却读回 Transfer=[{r.transfer}]"
                    + (r.iccLine.Length > 0 ? $" + 内嵌 {r.iccLine}" : " + 无内嵌 ICC（fields 那份声明）")
                    + (r.intensity is int it2 && it2 > 300
                        ? $"，而同一条声明里 intensity_target 仍写着 {it2} nits ⇒ 一张图同时自称 SDR 曲线与 HDR 峰值"
                        : "")
                    + $"\n      ⇒ PQ 码值会被按 {r.transfer} 曲线解释 ⇒ §31.3-5 的自相矛盾成立（产物级）");
                fail++;
            }
        }

        // ── 判据 2：P3 那一格的 primaries 不许塌成 sRGB（ICC 兜底声明的目的就是别报窄色域）──
        foreach (var r in rows.Where(x => x.name.StartsWith("hdr_DisplayP3") || x.name.StartsWith("hdr_DCI_P3")))
        {
            if (r.primaries.Trim().Equals("sRGB", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  ❌ {r.name}：目标 P3，读回 Primaries=[{r.primaries}]"
                    + " ⇒ 那份 ICC 没进容器，色域被报窄");
                fail++;
            }
        }

        // ── 判据 3：DisplayP3 与 DCI_P3 必须同字节；且必须不同于 BT2020（否则 ICC 那份声明根本没落地）──
        var p3 = rows.FirstOrDefault(x => x.name == "hdr_DisplayP3");
        var p3b = rows.FirstOrDefault(x => x.name == "hdr_DCI_P3");
        var bt = rows.FirstOrDefault(x => x.name == "hdr_BT2020");
        if (p3.name is null || p3b.name is null || bt.name is null)
        {
            Console.WriteLine("  ❌ 三格里有没产出产物的 ⇒ 这一层没判全（不是通过）");
            fail++;
        }
        else
        {
            if (p3.hash != p3b.hash)
            {
                Console.WriteLine($"  ❌ DisplayP3({p3.hash}) 与 DCI_P3({p3b.hash}) 产物不同字节"
                    + " ⇒ 两个同族标签走到了不同分支");
                fail++;
            }
            if (p3.hash == bt.hash)
            {
                Console.WriteLine($"  ❌ DisplayP3 与 BT2020 产物逐字节相同（{p3.hash}）"
                    + " ⇒ 交给 cjxl 的那份 P3 ICC 没有改变容器声明，兜底声明是空转");
                fail++;
            }
        }

        // 逐格声明汇总 + 落盘一份原始读数，供 scripts/precision/jxl_hdr_p3_icc_readback.py 复核
        File.WriteAllLines(Path.Combine(outDir, "jxlinfo_readback.txt"),
            rows.Select(r => $"{r.name}\t{r.bytes}\tsha={r.hash}\tfile={r.file}\ttransfer={r.transfer}"
                + $"\tprimaries={r.primaries}\tembedded_icc={(r.iccLine.Length > 0 ? r.iccLine : "none")}"
                + $"\tintensity_nits={r.intensity?.ToString() ?? "-"}"));
        Console.WriteLine($"   产物与原始读数留在 {outDir}");

        return fail > 0 ? 1 : 0;
    }

    /// <summary>jxlinfo 的 "Primaries: Custom - red(x=…)" 太长，表格里只留前 60 字符。</summary>
    static string TrimCustom(string s) => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>ExternalToolLocator 的候选表按文件名找，命令行/环境变量却允许一次塞多条 ——
    /// 按【可执行文件名】过滤掉递错的候选（不猜版本、也不拿错工具凑数）。</summary>
    static List<string> KeepExeNamed(List<string> cands, string exeName) =>
        cands.Where(c => string.Equals(Path.GetFileName(c), exeName, StringComparison.OrdinalIgnoreCase))
             .ToList();

    static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TrueToneCap.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }

    static string Grab(string text, string pattern, bool multiline = false)
    {
        var m = multiline
            ? Regex.Match(text, pattern, RegexOptions.Multiline)
            : Regex.Match(text, pattern);
        return m.Success ? m.Groups[1].Value.Trim() : "";
    }

    static (int Exit, string Out, string Err) RunProc(string exe, string argLine)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = argLine,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? ".",
        };
        // ═══ 第三方 JXL 读回工具是 libjxl 的【构建产物】，不是自包含 exe ═══
        // CMake 把 jxlinfo.exe/djxl.exe 放 build/tools/<Config>/、把 jxl.dll 等运行时放
        // build/lib/<Config>/ 与 build/third_party/**/<Config>/ ⇒ 直接跑必退 0xC0000135
        // (STATUS_DLL_NOT_FOUND)。⚠ WorkingDirectory 对此【无效】（实测）：Loader 找隐式依赖
        // 先看 exe 自己所在目录，那里恰恰没有 DLL。做法是把"那棵构建树里放 dll 的目录"
        // 追加进子进程 PATH —— 换台机器找不到就是"这层探不到 ⇒ SKIP"，
        // 不会把加载失败当成判定结论（那正是 §31.3-5 当年被误判成"本机无解"的原因）。
        var extraDirs = DllDirsFor(exe);
        if (extraDirs.Length > 0)
            psi.Environment["PATH"] = string.Join(';',
                extraDirs.Append(Environment.GetEnvironmentVariable("PATH") ?? ""));
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEnd();
        var se = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } return (-1, so, se + "\n超时"); }
        return (p.ExitCode, so, se);
    }

    static readonly System.Collections.Generic.Dictionary<string, string[]> s_dllDirs = new();
    static readonly object s_dllDirsLock = new();

    /// <summary>exe 往上 2 级（tools/&lt;Config&gt; → tools → build）那棵树里所有放着 *.dll 的目录。</summary>
    static string[] DllDirsFor(string exe)
    {
        var key = Path.GetFullPath(exe);
        lock (s_dllDirsLock) if (s_dllDirs.TryGetValue(key, out var hit)) return hit;

        var dirs = new List<string> { Path.GetDirectoryName(key) ?? "." };
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(key)));
        if (root is not null)
            try
            {
                var eo = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    MaxRecursionDepth = 4,      // 只在构建根下面找，不去扫整块盘
                    IgnoreInaccessible = true,
                };
                foreach (var f in Directory.EnumerateFiles(root, "*.dll", eo))
                    if (Path.GetDirectoryName(f) is { } d) dirs.Add(d);
            }
            catch { }   // 没权限/路径过长 ⇒ 少几个目录，最坏退化成 SKIP，不能抛
        var arr = dirs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (s_dllDirsLock) s_dllDirs[key] = arr;
        return arr;
    }
}
