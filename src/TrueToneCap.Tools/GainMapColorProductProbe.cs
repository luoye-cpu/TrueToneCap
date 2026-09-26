// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/GainMapColorProductProbe.cs
// GainMap 两模式的端到端判据：从【产品编码器写出的文件】里读回 base JPEG 的 ICC 原色 (--gainmap-color-product)
//
// 为什么必须读文件而不是读代码（2026-09-22，交接 §45）：
// "显式 BT.2020 ⇒ 整张增益图走 2020"这条改动的正确形状是**搬像素 + 换声明**。
// 只看代码很容易写出"只换了 ICC、像素没动"的版本，而产物在查看器里是偏色的、日志里却一切正常。
// 所以这里对每一枚产物做三件事：
//   ① 从 JPEG 段链里取出 APP2/ICC_PROFILE（jpegli 原样写，不分块 ⇒ 2KB 一次拿全）；
//   ② 解 rXYZ/gXYZ/bXYZ/wtpt 四个 XYZ 标签，按【该原色自己那一列】归一化成 (x,y)；
//   ③ 自检：ICC 头自报长度必须等于取出的字节数；白点必须落在 D65 附近。
//      任一自检不过 ⇒ 报"探针读错"，不把结论甩给产物。
//
// 三格对照（这是判据的全部力量所在）：
//   默认（tag=sRGB）           ⇒ base 原色 ≈ 709/sRGB
//   显式 BT.2020               ⇒ base 原色 ≈ BT.2020，且与默认那枚字节不同
//   tag=BT2020 但非显式(自动)   ⇒ 必须回到 sRGB base（Ultra HDR 默认形状不许被 P3 屏自动解锁）
//
// 退出码约定与 scripts/precision/precision_env.py 一致（门禁靠它区分三种结局，别改）：
//   0 = 判了且全部通过 / 1 = 判红 / 3 = 前置条件不满足 ⇒ SKIP，含义是"这一层本轮没判"，
//   【不是】通过 —— 第 4 层找不到（或不认新 CLI 的）外部解码器时只能退 3，绝不能退 0。

using System.Diagnostics;
using System.Security.Cryptography;
using TrueToneCap.Core;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Encoding;

public static class GainMapColorProductProbe
{
    const int W = 24, H = 24;

    /// <summary>三态结论，数值即退出码（0 通过 / 1 判红 / 3 SKIP），与 precision_env 同口径。</summary>
    enum Verdict { Pass = 0, Fail = 1, Skip = 3 }

    record struct IccInfo(bool Found, int Len, int Declared, double[] Xy, double Wx, double Wy, byte[] Raw);

    static byte[] BytesOf((string name, IccInfo icc, string hash, string jpg) r) => r.icc.Raw;

    public static int Run(string[] args)
    {
        if (!JpegLiNative.IsAvailable)
        {
            Console.WriteLine("❌ cjpegli 不可用 ⇒ 本轮没有判据（不是通过）");
            return 1;
        }

        var dir = Path.Combine(Path.GetTempPath(), "ttc_gainmap_color_probe");
        // 每次从空目录开始：临时目录跨轮存活，残留的 .jpg / .raw 会让"本轮什么都没产出"
        // 被读成"判据通过"（与 precision/jxl_color_space_discrimination.py 同一条教训）。
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // 一帧"像 HDR"的线性浮点：SDR 纸白 1.0 上下都有内容，高光到 6×
        var px = new float[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            float v = 0.25f + 5.5f * (i % 97) / 96f;
            px[i * 4] = v * 0.9f; px[i * 4 + 1] = v; px[i * 4 + 2] = v * 1.1f; px[i * 4 + 3] = 1f;
        }
        var frame = new HdrFrameData { Pixels = px, Width = W, Height = H };

        int fail = 0;
        var cases = new (string name, string tag, bool explicitWide)[]
        {
            ("默认 sRGB",        "sRGB",   false),
            ("显式 BT.2020",     "BT2020", true),
            ("自动 BT.2020(非显式)", "BT2020", false),
        };
        var results = new List<(string name, IccInfo icc, string hash, string jpg)>();

        foreach (var (name, tag, explicitWide) in cases)
        {
            var path = Path.Combine(dir, $"gm_{tag}_{explicitWide}.jpg");
            // 先删同名旧产物再编码：否则"编码器根本没写文件"会被上一轮留下的字节读成通过
            // （第 4 层的 TryDecode 同一条规矩）。
            if (File.Exists(path)) File.Delete(path);
            var settings = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP,
                HdrOutput = true,
                ColorSpaceTag = tag,
                ExplicitWideTarget = explicitWide,
                Quality = 1.0f,
                ChromaSubsampling = "420",
                OutputBitDepth = 8,
            };
            // ToneMappingParams 是 record struct ⇒ 只能整体赋值（`settings.ToneMappingParams.X = …`
            // 会撞 CS1612：属性返回值不是变量）
            settings.ToneMappingParams = settings.ToneMappingParams with
            {
                DisplayMaxNits = 1000,
                PaperWhiteNits = 200,
            };
            try
            {
                new JpegGainMapEncoder()
                    .EncodeAsync(frame, settings, path, CancellationToken.None)
                    .GetAwaiter().GetResult();
                var icc = ReadBaseIcc(path);
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..12];
                results.Add((name, icc, hash, path));
                Console.WriteLine($"  {name,-22} {new FileInfo(path).Length,7} B  md5={hash}  " +
                                  $"ICC={(icc.Found ? $"{icc.Len}B(自报 {icc.Declared})" : "无")}");
                if (icc.Found)
                    Console.WriteLine($"  {"",-22}   R=({icc.Xy[0]:F4},{icc.Xy[1]:F4}) G=({icc.Xy[2]:F4},{icc.Xy[3]:F4}) " +
                                      $"B=({icc.Xy[4]:F4},{icc.Xy[5]:F4}) 白=({icc.Wx:F4},{icc.Wy:F4})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {name,-22} ❌ 编码失败 {ex.GetType().Name}: {ex.Message}");
                fail++;
            }
        }

        Console.WriteLine("\n=== 自检（先怀疑探针，再怀疑产物）===");
        foreach (var (name, icc, _, _) in results)
        {
            if (!icc.Found) { Console.WriteLine($"  ❌ {name}: 没取到 ICC ⇒ 探针或封装读错"); fail++; continue; }
            if (icc.Len != icc.Declared)
            { Console.WriteLine($"  ❌ {name}: ICC 取到 {icc.Len} B 但自报 {icc.Declared} B ⇒ 探针读错，下面结论不作数"); fail++; }
            double dw = Math.Sqrt(Math.Pow(icc.Wx - 0.3127, 2) + Math.Pow(icc.Wy - 0.3290, 2));
            if (dw > 0.01)
            { Console.WriteLine($"  ❌ {name}: 白点 D65 误差 {dw:F4} > 0.01 ⇒ 取到的不是原色表或偏移读错"); fail++; }
        }
        if (results.Count == 3)
        {
            var (def, exp, auto) = (results[0], results[1], results[2]);
            // ⚠ 参照系陷阱（本轮自己踩的）：ICC 的 rXYZ/gXYZ/bXYZ 存的是 **D50 PCS、已做色度适应**
            //   的三刺激值，不是色度学课本上的 D65 原色坐标。拿 (0.30,0.60) 去比 sRGB 会误判成"探针错"：
            //   实测解出 G=(0.3212,0.5979) 恰好等于标准 sRGB ICC 的 gXYZ=(0.385,0.7169,0.0971) 列归一化值。
            //   ⇒ 判据只能在【同一适配空间】里比：方向 + 间距 + 与仓库自己那份标准 profile 逐字节相等。
            static double Dg(double[] xy) => Math.Sqrt(Math.Pow(xy[2] - 0.30, 2) + Math.Pow(xy[3] - 0.60, 2));
            if (Dg(def.icc.Xy) > 0.05)
            { Console.WriteLine($"  ❌ 默认模式绿点间距 {Dg(def.icc.Xy):F4} ≥ 0.05 ⇒ 默认 base 不是 sRGB/709 一档"); fail++; }
            else Console.WriteLine($"  ✅ 默认模式绿点间距 {Dg(def.icc.Xy):F4}（D50-PCS 空间，与标准 sRGB ICC 一致）");
            if (Dg(exp.icc.Xy) <= Dg(def.icc.Xy) + 0.03)
            { Console.WriteLine($"  ❌ 显式 BT.2020 的绿点没比默认更宽（{Dg(exp.icc.Xy):F4} vs {Dg(def.icc.Xy):F4}）"); fail++; }
            else Console.WriteLine($"  ✅ 显式 BT.2020 绿点确实更宽：ΔG {Dg(def.icc.Xy):F4} → {Dg(exp.icc.Xy):F4}");
            if (!(exp.icc.Xy[2] < def.icc.Xy[2] && exp.icc.Xy[3] > def.icc.Xy[3]))
            { Console.WriteLine("  ❌ 2020 的绿点方向不对（x 应更小、y 应更大）"); fail++; }

            // 与仓库自己生成的标准 profile 逐字节对得上 ⇒ 证明"模式挑中的就是那一份"
            if (!BytesOf(def).SequenceEqual(ColorProfileProvider.GetDefaultSRgbIcc()))
            { Console.WriteLine("  ❌ 默认 base 嵌的不是那份 sRGB 标准 ICC"); fail++; }
            var std2020 = ColorProfileProvider.GetStandardIccProfile("BT2020");
            if (std2020 is null || !BytesOf(exp).SequenceEqual(std2020))
            { Console.WriteLine("  ❌ 显式 2020 base 嵌的不是那份 BT.2020 标准 ICC"); fail++; }
            else Console.WriteLine("  ✅ 两枚 base 各自嵌的 ICC 与仓库标准 profile 逐字节相等");

            if (auto.hash != def.hash)
            { Console.WriteLine($"  ❌ 自动解析出的 BT.2020 也升级了整张增益图（{auto.hash} ≠ 默认 {def.hash}）"); fail++; }
            else Console.WriteLine("  ✅ 非显式的 BT.2020 目标 ⇒ 增益图保持 sRGB base（与默认逐字节相同）");
            if (exp.hash == def.hash)
            { Console.WriteLine("  ❌ 显式 BT.2020 与默认逐字节相同 ⇒ 什么都没改"); fail++; }
            else Console.WriteLine("  ✅ 显式 BT.2020 与默认产物字节不同（声明与像素一起换了）");
        }

        Console.WriteLine(fail == 0 ? "\n✅ 前三层（我们自己读回自己写的文件）判据通过" : $"\n❌ {fail} 条不成立");
        Console.WriteLine($"   产物留在 {dir}");

        // ═══ 第 4 层：外部参考解码器（libultrahdr ultrahdr_app）重建出的像素 ═══
        // 上面三层证的都是"我们自己写的文件里声明与像素是否一致"；这一层证的是
        // "**别人的实现**解出来是不是两张不同的图"。本轮补它是因为我先前错误地断言过
        // "本机没有 Ultra HDR 解码器"（见 §45.3 勘误），实际它一直在 tools 里。
        var l4 = ExternalDecodeCheck(results, dir, args);

        // 判红优先于 SKIP —— 前三层已经红了就没什么"没判"可言，别把 1 洗成 3。
        if (fail > 0 || l4 == Verdict.Fail) return (int)Verdict.Fail;
        // 前三层全绿、第 4 层没判 ⇒ 退 3：门禁显示成 SKIP，不是绿。
        return l4 == Verdict.Skip ? (int)Verdict.Skip : (int)Verdict.Pass;
    }

    /// <summary>
    /// 观感对比（<c>--gainmap-look &lt;rgba文件&gt; &lt;宽&gt; &lt;高&gt; [输出目录]</c>）。
    /// <para>判据版跑的是 24x24 合成色块 —— 那足够证"声明与像素同源"，但<strong>看不出观感</strong>：
    /// 纯色块上 sRGB base 与 BT.2020 base 的差别只是几个通道的均值偏移。这里改用真截图
    /// （真实内容、真实饱和色），把两种模式的渲染结果各落一张可打开的 PNG，再加一张 8× 放大差值图。</para>
    /// <para>⚠ 诚实边界：源是 8-bit sRGB 截图，HDR 余量是合成的（纸白抬到 2.5、亮部再拉到 ≈5 倍）
    /// ⇒ 这张对比看的是"整张增益图换原色之后看起来差多少"，不代表真 HDR 相机母版；
    /// 而"HDR 显示器上到底亮成什么样"仍然只能看实物，本机证不了。</para>
    /// </summary>
    public static int RunLook(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("用法: --gainmap-look <rgba文件> <宽> <高> [输出目录] [--sat <倍数>] [--chroma <420|444>]");
            Console.WriteLine("  造 rgba: ffmpeg -i 输入.png -vf scale=W:H -pix_fmt rgba -f rawvideo 输出.rgba");
            Console.WriteLine("  --sat 1.8 把线性饱和度外扩 ⇒ 像素落到 sRGB 之外，两模式的差别才有东西可承载");
            return 2;
        }
        var srcRgba = args[1];
        if (!int.TryParse(args[2], out int w) || !int.TryParse(args[3], out int h) || !File.Exists(srcRgba))
        { Console.WriteLine("❌ 参数不合法或源 rgba 不存在"); return 2; }
        var rgbaIn = File.ReadAllBytes(srcRgba);
        if (rgbaIn.Length != w * h * 4)
        { Console.WriteLine($"❌ rgba 长度 {rgbaIn.Length} ≠ {w}x{h}x4"); return 2; }
        var outDir = args.Length > 4 && !args[4].StartsWith('-') ? args[4] : Path.Combine(Path.GetTempPath(), "ttc_gainmap_look");
        Directory.CreateDirectory(outDir);

        // ★ 8-bit sRGB 截图本身【一个 out-of-sRGB 像素都没有】。拿它直接比两种模式，结论必然是
        //   "两张一样"——那只证明 2020 模式没把画面弄坏，不证明它有用。所以给一个饱和外扩开关：
        //   线性域里 c' = luma + sat·(c - luma)，sat>1 会把高饱和像素推到 sRGB 三角之外（出现负分量），
        //   sRGB 模式必须钳、BT.2020 模式留得住 ⇒ 这才是两种模式各自的工作面。
        double sat = 1.0;
        string chroma = "420";
        for (int a = 4; a < args.Length - 1; a++)
        {
            if (args[a] == "--sat" && double.TryParse(args[a + 1], out double sv)) sat = sv;
            // 与 v0.3.3 基准对撞时要能把这一维单独按住：0.3.3 的 GainMap 主路径把色度写死 444
            // （设置项是假配置，0.3.4 才改成真的读字段），所以两侧默认产物必然差在色度上。
            // 只有把 HEAD 也摆成 444 还残留差异，那才是【色彩管线】的回归。
            if (args[a] == "--chroma" && args[a + 1] is "420" or "422" or "444") chroma = args[a + 1];
        }

        // 8-bit sRGB 截图 → scRGB 线性浮点：纸白按 1.0=80nits / SDR 白 200nits 抬到 2.5，
        // 亮部再叠加到 ≈5 倍 ⇒ 增益图真的有东西可放大（否则整帧落在一档灰里，观感无从对比）
        var lin = new float[w * h * 4];
        int outside = 0;
        for (int i = 0; i < w * h; i++)
        {
            var e = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double s = rgbaIn[i * 4 + k] / 255.0;
                e[k] = s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
            }
            double luma = 0.2126 * e[0] + 0.7152 * e[1] + 0.0722 * e[2];
            bool neg = false;
            for (int k = 0; k < 3; k++)
            {
                double v = luma + sat * (e[k] - luma);
                if (v < -1e-4) neg = true;   // 线性 sRGB 坐标出现负分量 = 落在 sRGB 三角之外
                double boost = v > 0.35 ? 2.5 * (1 + 3.0 * (v - 0.35) / 0.65) : 2.5;
                lin[i * 4 + k] = (float)(v * boost);
            }
            if (neg) outside++;
            lin[i * 4 + 3] = 1f;
        }
        Console.WriteLine($"源：{w}x{h}，sat 外扩 ×{sat:0.##} ⇒ 线性域出现负分量（sRGB 三角之外）的像素 " +
                          $"{outside} 个，占 {100.0 * outside / (w * h):F2}%");
        var frame = new HdrFrameData { Pixels = lin, Width = w, Height = h };

        string Encode(string tag, bool explicit2020)
        {
            var path = Path.Combine(outDir, $"look_{tag}{(chroma == "420" ? "" : "_" + chroma)}.jpg");
            var s = new EncodingSettings
            {
                Format = OutputFormat.JPEG_GAINMAP, HdrOutput = true,
                ColorSpaceTag = tag, ExplicitWideTarget = explicit2020,
                Quality = 1.0f, ChromaSubsampling = chroma, OutputBitDepth = 8,
            };
            s.ToneMappingParams = s.ToneMappingParams with { DisplayMaxNits = 1000, PaperWhiteNits = 200 };
            new JpegGainMapEncoder().EncodeAsync(frame, s, path, CancellationToken.None)
                .GetAwaiter().GetResult();
            return path;
        }

        var jpgSrgb = Encode("sRGB", false);
        var jpg2020 = Encode("BT2020", true);
        Console.WriteLine($"编码完成：{Path.GetFileName(jpgSrgb)} / {Path.GetFileName(jpg2020)}");

        // 与第 4 层同一口径：【存在 ≠ 能用】。仓库内那份 ultrahdr_app 缺依赖 / 不认 -j/-z，
        // 直接取第一个候选会像刚才那样撞 exit=0xC0000135 ⇒ 逐候选做能力探测，取第一个探得过的。
        string? exe = null;
        string probed = "";
        foreach (var c in ExternalToolLocator.Candidates(
                     "TTC_ULTRAHDR_APP", System.Array.Empty<string>(), 1, "ultrahdr_app.exe", out probed))
        {
            if (!File.Exists(c)) continue;
            var p = TryDecode(c, jpgSrgb, Path.Combine(outDir, "__cap_probe.raw"), w, h, " -O 3 -o 3");
            if (p.Ok) { exe = c; break; }
            Console.WriteLine($"  候选探测失败：{c}\n    ⇒ {p.Why}");
        }
        if (exe is null)
        {
            Console.WriteLine($"⏭ 没有可用的 ultrahdr_app（找过：{probed}）⇒ 【渲染结果这一层没判】，只给你两枚 GainMap 本体");
            Console.WriteLine($"   默认模式: {jpgSrgb}");
            Console.WriteLine($"   全 2020 : {jpg2020}");
            Console.WriteLine("   补法：设 TTC_ULTRAHDR_APP 指向认 -j/-z 的那版 ultrahdr_app.exe");
            return 3;
        }
        Console.WriteLine($"外部解码：{exe}");
        // ★ 解码参数不是可选装饰：ultrahdr_app 的默认输出是 rgba1010102 + HLG 传递函数（-O 5 / -o 1）。
        //   拿那串字节当 8-bit sRGB 写 PNG，看到的是【读法造出来的】色带和偏色，不是观感。
        //   CLI 自己规定 sRGB 传递必须配 rgba8888 ⇒ 这里显式要 -O 3 -o 3。
        const string srgb8 = " -O 3 -o 3";
        var decSrgb = Path.Combine(outDir, "dec_srgb.raw");
        var dec2020 = Path.Combine(outDir, "dec_2020.raw");
        var (okA, whyA, _) = TryDecode(exe, jpgSrgb, decSrgb, w, h, srgb8);
        var (okB, whyB, _) = TryDecode(exe, jpg2020, dec2020, w, h, srgb8);
        if (!okA || !okB) { Console.WriteLine($"❌ 解码失败：sRGB={whyA} / 2020={whyB}"); return 1; }

        var rawA = File.ReadAllBytes(decSrgb);
        var rawB = File.ReadAllBytes(dec2020);
        var pngDefault = Path.Combine(outDir, "渲染_默认sRGB.png");
        var pngWideRaw = Path.Combine(outDir, "渲染_全BT2020_按存储.png");
        var pngWideMatch = Path.Combine(outDir, "渲染_全BT2020_折回sRGB.png");
        var pngDiff = Path.Combine(outDir, "差值_按最大幅度拉伸.png");
        var wideAsSrgb = Bt2020BytesToSrgb(rawB);
        ManagedPngEncoder.Encode(rawA, w, h, pngDefault, 8);
        ManagedPngEncoder.Encode(rawB, w, h, pngWideRaw, 8);
        ManagedPngEncoder.Encode(wideAsSrgb, w, h, pngWideMatch, 8);

        // 一票区分"解码器有没有顺手把宽色域折进 sRGB"：折过 ⇒ 我再折一次是重复换算，
        // 折回那张会【更差】；没折 ⇒ 它才是同图对比，差值明显小于按存储直接比。
        double dStore = MeanAbsDiff(rawA, rawB);
        double dMatch = MeanAbsDiff(rawA, wideAsSrgb);
        int over16 = 0, maxd = 0;
        var diff = new byte[rawA.Length];
        for (int i = 0; i < rawA.Length; i++)
            if (System.Math.Abs(rawA[i] - wideAsSrgb[i]) > maxd)
                maxd = System.Math.Abs(rawA[i] - wideAsSrgb[i]);
        for (int i = 0; i < rawA.Length; i += 4)
        {
            for (int k = 0; k < 3; k++)
            {
                int d = System.Math.Abs(rawA[i + k] - wideAsSrgb[i + k]);
                if (d > 16) over16++;
                diff[i + k] = (byte)System.Math.Min(255, d * 255 / System.Math.Max(1, maxd));
            }
            // 差值图的 alpha 必须钉成不透明：跟着差值走会得到一张【全白】的透明图，
            // 而差值的均值其实接近 0 —— 那是纯粹的呈现层假象（本轮实机踩到）。
            diff[i + 3] = 255;
        }
        ManagedPngEncoder.Encode(diff, w, h, pngDiff, 8);

        Console.WriteLine($"\n平均绝对差：按存储直接比 {dStore:F2}/255；把 2020 折回 sRGB 再比 {dMatch:F2}/255" +
                          $"（折回口径下超 16 级的子通道占 {100.0 * over16 / rawA.Length:F2}%，最大 {maxd}/255）");
        // 全帧均值会被 99% 的近中性像素摊平 —— 上面那对数字方向对、幅度小，不足以定论。
        // 高色步子集（max-min > 60）才是两种模式唯一会分叉的地方，差值必须在那里见分晓。
        var (hiStore, hiMatch, hiN) = HighChroma(rawA, rawB, wideAsSrgb);
        Console.WriteLine($"  只看高色调像素（{hiN} 个）：按存储 {hiStore:F2}/255，折回 sRGB 后 {hiMatch:F2}/255");
        if ((hiMatch < hiStore) == (dMatch < dStore))
            Console.WriteLine(hiMatch < hiStore
                ? "  ⇒ 两种口径同向：解码输出是【各自主色域里的字节】，不折回来就不是同一张图的对比"
                : "  ⇒ 两种口径同向：解码器已把输出折到 sRGB，按存储那两张才是同口径");
        else
            Console.WriteLine("  ⚠ 两种口径反向（全帧被近中性像素摊平）⇒ 以高色调口径为准，但这条判据本轮不算干净");
        Console.WriteLine("渲染结果（打开看）：");
        Console.WriteLine($"  {pngDefault}");
        Console.WriteLine($"  {pngWideMatch}   （与上一张同口径，可直接比观感）");
        Console.WriteLine($"  {pngWideRaw}   （把 2020 字节当 sRGB 看 = 不做管理的查看器会看到什么）");
        Console.WriteLine($"  {pngDiff}   （越亮 = 两模式折回同色域后仍差得越多）");
        Console.WriteLine("两枚 GainMap 本体（丢进 Windows 照片 / 手机相册看真实 HDR 效果）：");
        Console.WriteLine($"  {jpgSrgb}");
        Console.WriteLine($"  {jpg2020}");
        return 0;
    }

    static double MeanAbsDiff(byte[] a, byte[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++) sum += System.Math.Abs(a[i] - b[i]);
        return sum / a.Length;
    }

    /// <summary>只在参考图里"看得出颜色"的那些像素上比（max-min 通道跨度 &gt; 60），返回两种口径的均值。</summary>
    static (double Store, double Match, int N) HighChroma(byte[] a, byte[] b, byte[] bFolded)
    {
        double sSum = 0, mSum = 0; int n = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            int lo = System.Math.Min(a[i], System.Math.Min(a[i + 1], a[i + 2]));
            int hi = System.Math.Max(a[i], System.Math.Max(a[i + 1], a[i + 2]));
            if (hi - lo <= 60) continue;
            n++;
            for (int k = 0; k < 3; k++)
            {
                sSum += System.Math.Abs(a[i + k] - b[i + k]);
                mSum += System.Math.Abs(a[i + k] - bFolded[i + k]);
            }
        }
        return n == 0 ? (0, 0, 0) : (sSum / (n * 3), mSum / (n * 3), n);
    }

    /// <summary>
    /// 把"存成 BT.2020 原色、但字节走 sRGB 传递"的 RGBA 折进 sRGB 原色，好与 sRGB 那张同口径比。
    /// <para>矩阵不是抄来的：由 (0.708,0.292)(0.170,0.797)(0.131,0.046) 与 D65 白点现推，
    /// 对已发表的 BT.2020→BT.709 矩阵最大偏差 &lt; 0.002，且三行和恒为 1（中性灰不偏色）。</para>
    /// </summary>
    static byte[] Bt2020BytesToSrgb(byte[] src)
    {
        const double m00 = 1.660363, m01 = -0.587540, m02 = -0.072823;
        const double m10 = -0.124564, m11 = 1.132911, m12 = -0.008348;
        const double m20 = -0.018157, m21 = -0.100602, m22 = 1.118758;
        var dst = new byte[src.Length];
        for (int i = 0; i < src.Length; i += 4)
        {
            double r = SrgbToLinear(src[i]), g = SrgbToLinear(src[i + 1]), b = SrgbToLinear(src[i + 2]);
            dst[i] = LinearToSrgb(r * m00 + g * m01 + b * m02);
            dst[i + 1] = LinearToSrgb(r * m10 + g * m11 + b * m12);
            dst[i + 2] = LinearToSrgb(r * m20 + g * m21 + b * m22);
            dst[i + 3] = src[i + 3];
        }
        return dst;
    }

    static double SrgbToLinear(byte v)
    {
        double s = v / 255.0;
        return s <= 0.04045 ? s / 12.92 : System.Math.Pow((s + 0.055) / 1.055, 2.4);
    }

    static byte LinearToSrgb(double e)
    {
        if (e <= 0) return 0;
        if (e >= 1) return 255;
        double s = e <= 0.0031308 ? e * 12.92 : 1.055 * System.Math.Pow(e, 1.0 / 2.4) - 0.055;
        return (byte)System.Math.Round(System.Math.Clamp(s * 255.0, 0, 255));
    }

    /// <summary>
    /// 用 libultrahdr 的 <c>ultrahdr_app -m 1 -j &lt;in&gt; -z &lt;out&gt;</c> 把三枚产物解回 RGBA，
    /// 然后要求：非显式那枚的重建与默认<strong>逐字节相同</strong>、显式那枚<strong>必须不同</strong>。
    /// <para>
    /// 三态返回，调用方按退出码区分：<see cref="Verdict.Pass"/> 判了且通过 /
    /// <see cref="Verdict.Fail"/> 判红 / <see cref="Verdict.Skip"/> 这一层本轮<strong>没判</strong>。
    /// "没判"永远不许映射成 0 —— 那是本仓库零容忍的假绿灯形状。
    /// </para>
    /// 解码器路径来自 <see cref="ExternalToolLocator"/>（环境变量 &gt; 命令行 &gt; 一小段候选 + PATH），
    /// 且【候选存在不等于能用】：仓库内那份 ultrahdr_app 是不认 -j/-z 的旧 CLI，
    /// 所以先拿第一枚产物做一次能力探测，探不过就 SKIP（不猜版本号、也不写死哪条路径能用）。
    /// </summary>
    static Verdict ExternalDecodeCheck(
        List<(string name, IccInfo icc, string hash, string jpg)> results, string dir, string[] args)
    {
        Console.WriteLine("\n── 第 4 层：外部参考解码器 ultrahdr_app（libultrahdr）──");
        if (results.Count < 3)
        {
            Console.WriteLine("  ⏭ 跳过 (SKIP)：前三层没产出三枚可比对的产物 ⇒ 本层无从执行");
            return Verdict.Skip;
        }

        var cands = ExternalToolLocator.Candidates("TTC_ULTRAHDR_APP", args, 1, "ultrahdr_app.exe", out var probed);
        if (cands.Count == 0)
        {
            Console.WriteLine($"  ⏭ 跳过 (SKIP)：没找到 ultrahdr_app.exe（找过：{probed}）");
            Console.WriteLine("     ⇒ 这一层本轮【没判】，不是通过");
            Console.WriteLine("     补法：设环境变量 TTC_ULTRAHDR_APP=<exe>，或把路径作为 " +
                              "--gainmap-color-product 的参数递进来");
            Console.WriteLine("     （环境变量指错时【不】回退候选表 —— 显式指定就得显式生效，" +
                              "静默回退等于把 override 变成哑开关，见 ExternalToolLocator）");
            return Verdict.Skip;
        }

        // 能力探测：认不认 -j/-z 只有它自己知道。挑第一个探得过的当解码器。
        string? exe = null;
        int rejected = 0;
        foreach (var c in cands)
        {
            var probe = TryDecode(c, results[0].jpg, Path.Combine(dir, "__cap_probe.raw"));
            if (probe.Ok) { exe = c; break; }
            rejected++;
            Console.WriteLine($"      候选探测失败：{c}");
            Console.WriteLine($"        ⇒ {probe.Why}");
        }
        if (exe is not null)
        {
            Console.WriteLine($"  ℹ️  选中: {exe}" +
                              (rejected > 0 ? $"（前 {rejected} 个候选探不过能力探测，已跳过）" : ""));
        }
        else
        {
            Console.WriteLine($"  ⏭ 跳过 (SKIP)：{cands.Count} 个候选全都【探不过能力探测】" +
                              "（旧版 ultrahdr_app 不认 -j/-z 这套 CLI，或它依赖的 DLL 不在旁边）");
            Console.WriteLine("     ⇒ 这一层本轮【没判】，不是通过；确认过这个二进制确实认新 CLI 的话，" +
                              "【产物不可解】就是真回归，按交接 §45.3 查");
            return Verdict.Skip;
        }

        var raws = new Dictionary<string, byte[]>();
        foreach (var (name, _, _, jpg) in results)
        {
            var raw = Path.Combine(dir, Path.GetFileNameWithoutExtension(jpg) + ".raw");
            var (ok, why, data) = TryDecode(exe, jpg, raw);
            if (!ok || data is null)
            {
                // 探过的同一个二进制、同样的命令行解不出某一枚 ⇒ 这才是判红，不是没判
                Console.WriteLine($"  ❌ {name,-22} 解码失败：{why}");
                return Verdict.Fail;
            }
            raws[name] = data;
            Console.WriteLine($"  {name,-22} 重建 {data.Length} B  " +
                              $"md5={Convert.ToHexString(SHA256.HashData(data))[..12]}");
        }

        int fail = 0;
        if (raws.Count == 3)
        {
            var def = raws["默认 sRGB"]; var exp = raws["显式 BT.2020"]; var auto = raws["自动 BT.2020(非显式)"];
            if (!def.SequenceEqual(auto))
            { Console.WriteLine("  ❌ 非显式那枚经外部解码后与默认不同 ⇒ 界线没守住"); fail++; }
            else Console.WriteLine("  ✅ 外部解码器看：非显式 2020 与默认重建逐字节相同");
            if (def.SequenceEqual(exp))
            { Console.WriteLine("  ❌ 外部解码器看：显式 2020 与默认重建相同 ⇒ 它根本没读到差异"); fail++; }
            else Console.WriteLine("  ✅ 外部解码器看：显式 2020 重建出不同的像素（不只是我们自己的文件字节不同）");
            // 每像素 4 字节 RGBA，尺寸必须与输入一致
            if (def.Length != W * H * 4)
            { Console.WriteLine($"  ❌ 重建尺寸异常：{def.Length} B ≠ {W}x{H}x4"); fail++; }
        }
        return fail == 0 ? Verdict.Pass : Verdict.Fail;
    }

    /// <summary>
    /// 跑一次 ultrahdr_app 解码。能不能干活靠这里三件事判定：退出码 0、真的出了文件、
    /// 字节数等于 W*H*4 —— 三条缺一即"这个二进制在这一层上不可信"。
    /// 每次先删旧输出：否则上一轮留下的 .raw 会让一个根本不干活的旧版解码器"看起来成功"。
    /// </summary>
    static (bool Ok, string Why, byte[]? Data) TryDecode(
        string exe, string jpg, string raw, int cw = W, int ch = H, string extra = "")
    {
        try
        {
            if (File.Exists(raw)) File.Delete(raw);
            var psi = new ProcessStartInfo(exe, $"-m 1 -j \"{jpg}\" -z \"{raw}\"{extra}")
            { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "进程起不来", null);
            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(60_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已经退了 */ }
                return (false, "60s 超时", null);
            }
            if (proc.ExitCode != 0)
                return (false, $"exit={proc.ExitCode} {Tail(stderr.Length > 0 ? stderr : stdout)}", null);
            if (!File.Exists(raw))
                return (false, $"退出码 0 但没出文件 {Tail(stderr.Length > 0 ? stderr : stdout)}", null);
            var data = File.ReadAllBytes(raw);
            if (data.Length != cw * ch * 4)
                return (false, $"输出 {data.Length} B ≠ {cw}x{ch}x4（{cw}x{ch} RGBA）", null);
            return (true, $"{data.Length} B", data);
        }
        catch (Exception ex)
        {
            return (false, $"异常 {ex.GetType().Name}: {ex.Message}", null);
        }
    }

    static string Tail(string s)
    {
        s = s.Replace("\r", " ").Trim();
        return s.Length <= 120 ? s : "…" + s[^120..];
    }

    // ── JPEG APP2/ICC 提取 + XYZ 解原色（自成一体，不依赖本仓库其它解析器 ⇒ 独立参照）──

    static IccInfo ReadBaseIcc(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 4 || b[0] != 0xFF || b[1] != 0xD8) return new IccInfo(false, 0, 0, new double[6], 0, 0, System.Array.Empty<byte>());
        var icc = new List<byte>();
        int i = 2;
        while (i + 4 <= b.Length)
        {
            if (b[i] != 0xFF) break;
            byte marker = b[i + 1];
            if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
            int len = (b[i + 2] << 8) | b[i + 3];
            int segStart = i + 4, segEnd = i + 2 + len;      // len 含自身 2 字节
            if (marker == 0xE2 && segEnd <= b.Length && len > 14
                && System.Text.Encoding.ASCII.GetString(b, segStart, 11) == "ICC_PROFILE")
            {
                for (int k = segStart + 14; k < segEnd; k++) icc.Add(b[k]);  // 跳过签名+seq+count
            }
            if (marker == 0xDA) break;                        // SOS 之后是熵编码数据
            i = segEnd;
        }
        if (icc.Count < 132) return new IccInfo(false, 0, 0, new double[6], 0, 0, System.Array.Empty<byte>());
        var d = icc.ToArray();
        int declared = (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
        int n = (d[128] << 24) | (d[129] << 16) | (d[130] << 8) | d[131];
        var tags = new Dictionary<string, (int off, int size)>();
        for (int t = 0; t < n; t++)
        {
            int e = 132 + t * 12;
            if (e + 12 > d.Length) break;
            tags[System.Text.Encoding.ASCII.GetString(d, e, 4)] =
                ((d[e + 4] << 24) | (d[e + 5] << 16) | (d[e + 6] << 8) | d[e + 7],
                 (d[e + 8] << 24) | (d[e + 9] << 16) | (d[e + 10] << 8) | d[e + 11]);
        }
        double[] xy = new double[6];
        for (int c = 0; c < 3; c++)
        {
            string sig = new[] { "rXYZ", "gXYZ", "bXYZ" }[c];
            if (!tags.TryGetValue(sig, out var t) || t.off + 20 > d.Length)
                return new IccInfo(false, d.Length, declared, xy, 0, 0, d);
            if (System.Text.Encoding.ASCII.GetString(d, t.off, 4) != "XYZ ")
                return new IccInfo(false, d.Length, declared, xy, 0, 0, d);
            double X = SFixed(d, t.off + 8), Y = SFixed(d, t.off + 12), Z = SFixed(d, t.off + 16);
            double s = X + Y + Z;                              // ★ 分母是这一列自己的和
            xy[c * 2] = s == 0 ? 0 : X / s;
            xy[c * 2 + 1] = s == 0 ? 0 : Y / s;
        }
        double wx = 0, wy = 0;
        if (tags.TryGetValue("wtpt", out var wt) && wt.off + 20 <= d.Length)
        {
            double X = SFixed(d, wt.off + 8), Y = SFixed(d, wt.off + 12), Z = SFixed(d, wt.off + 16);
            double s = X + Y + Z; wx = X / s; wy = Y / s;
        }
        return new IccInfo(true, d.Length, declared, xy, wx, wy, d);
    }

    // ICC sfixed(2.30)：有符号 32 位定点，值 = raw / 65536
    static double SFixed(byte[] d, int o)
    {
        long raw = ((long)(int)((d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3]));
        return raw / 65536.0;
    }
}

/// <summary>
/// 外部判据工具（ultrahdr_app / cjxl 这类仓库外二进制）的定位 —— 优先级只在这一处定义，
/// 每个判据不再各写一遍各自的硬编码路径。
/// <para>
/// 优先级与 scripts/precision/precision_env.py 的 tool_candidates() 同一套约定：
///   ① 环境变量（TTC_ULTRAHDR_APP / TTC_CJXL…，';' 可分多条）—— 设了就【只认它】，
///      指错也不静默回退到候选表，否则"我明确指定了"这件事会变成没人知道的哑开关；
///   ② 命令行 / 门禁显式递进来的路径（verify.ps1 就是拿 precision_env 的候选表递进来的，
///      所以仓库之外那些绝对路径只在 python 一处维护，不在这里重复）；
///   ③ 一小段候选：仓库内 publish\PLAN\tools\ 那一份，再加 PATH 逐目录找。
/// </para>
/// <para>
/// ⚠ 这里返回的只是【存在】的候选，不等于【能用】：同名工具可能有两代互不兼容的 CLI
///   （ultrahdr_app 的旧版只认 -i/-o，解不出我们的产物）。"存在就当能用"正是假绿灯，
///   所以调用方必须再做一次能力探测，探不过就 SKIP（见 GainMapColorProductProbe）。
/// </para>
/// </summary>
static class ExternalToolLocator
{
    /// <summary>按上述优先级排出【存在】的候选，顺序即优先级；<paramref name="probed"/> 说清找过哪一档。</summary>
    public static List<string> Candidates(
        string envVar, string[] args, int argsFrom, string exeName, out string probed)
    {
        var fromEnv = Environment.GetEnvironmentVariable(envVar)?.Trim() ?? "";
        if (fromEnv.Length > 0)
        {
            probed = $"环境变量 {envVar}='{fromEnv}'";
            return KeepExisting(Split(fromEnv));
        }

        var passed = new List<string>();
        for (int i = argsFrom; i < args.Length; i++)
            if (!args[i].StartsWith('-')) passed.AddRange(Split(args[i]));
        if (passed.Count > 0)
        {
            probed = $"命令行参数 {passed.Count} 条（门禁侧由 precision_env.tool_candidates 递进来）";
            return KeepExisting(passed);
        }

        var repo = new List<string> { Path.Combine(RepoRoot(), "publish", "PLAN", "tools", exeName) };
        foreach (var d in Split(Environment.GetEnvironmentVariable("PATH") ?? ""))
        {
            try { repo.Add(Path.Combine(d, exeName)); }
            catch (Exception) { /* PATH 里的非法段（引号/超长）—— 少一个目录不影响判据，多一个也不会更假绿 */ }
        }
        probed = $@"仓库内 publish\PLAN\tools\{exeName} + PATH 各目录";
        return KeepExisting(repo);
    }

    static string[] Split(string s) =>
        s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static List<string> KeepExisting(IEnumerable<string> raw)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var p in raw)
        {
            string full;
            try { full = Path.GetFullPath(p); }
            catch (Exception) { continue; }
            if (seen.Add(full) && File.Exists(full)) list.Add(full);
        }
        return list;
    }

    static string RepoRoot()
    {
        // 往上找到解决方案文件所在处，而不是写死 ".." 的层数（FormatBench.cs 里那样）：
        // 层数猜错的失败方式是"候选表全空 ⇒ SKIP"，看着像环境问题，实际是接线断了。
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "TrueToneCap.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;
    }
}
