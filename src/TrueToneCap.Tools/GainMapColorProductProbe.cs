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
    static (bool Ok, string Why, byte[]? Data) TryDecode(string exe, string jpg, string raw)
    {
        try
        {
            if (File.Exists(raw)) File.Delete(raw);
            var psi = new ProcessStartInfo(exe, $"-m 1 -j \"{jpg}\" -z \"{raw}\"")
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
            if (data.Length != W * H * 4)
                return (false, $"输出 {data.Length} B ≠ {W}x{H}x4（24x24 RGBA）", null);
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
