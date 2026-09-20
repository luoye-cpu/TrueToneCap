// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/AvifHdrDiag.cs
// AVIF HDR 路径 PQ 实测诊断 —— 用 Float16 scRGB 源走完整 HDR 编码路径（
// AVIF Auto→NVENC 直通或 avifenc 软件），再用外部 ffprobe 交叉验证
// 码流色彩声明（PQ/BT.2020/10-bit）与 clli。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release -- --avif-hdr-test [--kind nvenc|avifenc|libaom|auto]
// 说明: 源为合成的 Float16 scRGB（含超 SDR 白点高光 + 纹理，模拟真实 HDR 截图内容），
//       与 --bitdepth-bench / --sdr16-bench 同型；不依赖 WGC 真实捕获（Tools 不引用 App）。

using System.Diagnostics;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;
using TrueToneCap.Core.Processing;

// ⚠ 放在全局命名空间（FormatBench 是 top-level statements，分发处引用全局类型）
public static class AvifHdrDiag
{
    public static void Run(string[] args)
    {
        string kindStr = "auto";
        if (args.Length >= 3 && args[1] == "--kind")
        {
            kindStr = args[2].ToLowerInvariant();
        }
        var kind = kindStr switch
        {
            "nvenc" => AvifEncoderKind.Nvenc,
            "avifenc" => AvifEncoderKind.Avifenc,
            "libaom" => AvifEncoderKind.LibAom,
            _ => AvifEncoderKind.Auto,
        };

        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("AVIF HDR 实测（PQ 10-bit + BT.2020）");
        Console.WriteLine($"指定编码器: {kind}");
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine();

        // ── 前置检查 ──
        if (!NativeAvifEncoder.IsAvailable)
        {
            Console.WriteLine("⚠ avifenc 不可用，软件 HDR 回退路径无法测试");
        }
        // NvencAvifBackend.IsAvailable 是实例属性 —— 经 SelectByKind 取缓存单例查询
        if (!AvifEncoderSelector.SelectByKind(AvifEncoderKind.Nvenc).IsAvailable)
        {
            Console.WriteLine("⚠ NVENC 不可用，若指定 --kind nvenc 将不可用");
        }
        if (!NvencAvifBackend.HardwareEncodingEnabled)
        {
            Console.WriteLine("⚠ NVENC 硬件编码已在设置中禁用（需在 App 设置里开启）");
        }

        // 1. 构造合成 Float16 scRGB 源（模拟真实 HDR 截图：含超 SDR 白点高光 + 竖条纹理）
        // 等同于 --bitdepth-bench / --sdr16-bench 的源构造方式（交接 §5-5/§8 实测采信）
        Console.WriteLine("\n[1/4] 构造合成 Float16 scRGB 源（640×480，含 >200nits 高光 + 纹理）...");
        const int BW = 640, BH = 480;
        var hdr = new float[BW * BH * 4];
        for (int y = 0; y < BH; y++)
            for (int x = 0; x < BW; x++)
            {
                int i = (y * BW + x) * 4;
                // 亮度：左 0.05→右 0.95（相对 scRGB 白点 1.0=80nits）→ 对应约 4→76 nits 基础
                // 加一条 3 倍亮度的高光条（相当于 600+ nits，模拟 HDR 高光）
                float baseV = 0.05f + 0.9f * x / (BW - 1);
                float tex = ((x >> 3) & 1) == 1 ? 3.5f : 1.0f;   // 竖条纹理，产生高光
                hdr[i] = baseV * tex;     // R
                hdr[i + 1] = baseV * tex; // G
                hdr[i + 2] = baseV * tex; // B
                hdr[i + 3] = 1f;          // A
            }
        Console.WriteLine($"  ✅ 合成 scRGB 源: {BW}x{BH}, 像素数={BW * BH}, 含 >1.0 范围高光");
        // 打印显示器峰值参考
        Console.WriteLine($"  参考：PaperWhiteNits=200 (sdrWhite=2.5), DisplayMaxNits=1000 (headroom=5.0)");

        // 2. 构造编码设置（HDR 输出，目标色域跟随 System 解析为 BT.2020）
        // 生产环境中 CapturePipelineService.BuildEncodingSettings 会把 "System"+HDR=true 解析为 BT2020
        var colorSpaceTagResolved = "BT2020"; // 生产环境下的真实解析结果
        var settings = new EncodingSettings
        {
            Format = OutputFormat.AVIF,
            Quality = 15f, // CRF
            HdrOutput = true,
            AvifEncoderKind = kind,
            ColorSpaceTag = colorSpaceTagResolved, // HDR 开启时实际解析为 BT2020
            ToneMappingParams = new ToneMappingParams
            {
                Mode = ToneMapMode.SegmentedReinhard,
                PaperWhiteNits = 200f,
                DisplayMaxNits = 1000f,
            },
        };

        // 3. 编码（走 EncodeHdrFrameAsync → NVENC HDR 直通优先）
        Console.WriteLine($"\n[2/4] 编码 HDR AVIF (编码器={kind})...");
        var encoder = EncoderFactory.Create(settings.Format);

        // 无 GPU 纹理：NVENC HDR 直通会走 CPU 像素路径（EncodeAv1Argb10），同样验证码流
        // 色域已由上方 settings.ColorSpaceTag 解析为 BT2020（与生产环境 BuildEncodingSettings
        // —— HDR 开启 + "System"→BT2020 —— 一致），决定 colr/–cicp 的 primaries 声明
        var hdrFrame = new HdrFrameData
        {
            Pixels = hdr,
            Width = BW,
            Height = BH,
            IccProfile = null,
            Metadata = null,
            GpuTexture = null, // 合成源无 GPU 纹理，走 CPU 像素路径（EncodeAv1Argb10）
        };

        string outDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_HdrTest");
        Directory.CreateDirectory(outDir);
        string path = Path.Combine(outDir, $"hdr_test_{kind}.avif");

        var sw = Stopwatch.StartNew();
        try
        {
            encoder.EncodeAsync(hdrFrame, settings, path).GetAwaiter().GetResult();
            sw.Stop();
            var size = new FileInfo(path).Length;
            Console.WriteLine($"  ✅ 编码成功: {size / 1024.0:F1} KB, 耗时 {sw.ElapsedMilliseconds} ms");
            Console.WriteLine($"  产物路径: {path}");
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ❌ 编码失败: {ex.Message}");
            if (ex.InnerException != null) Console.WriteLine($"  Inner: {ex.InnerException.Message}");
            return;
        }

        // 4. 用 ffprobe 交叉验证
        Console.WriteLine("\n[3/4] ffprobe 交叉验证...");
        string ffprobe = @"C:\Windows\ffmpeg-8.0-full_build\bin\ffprobe.exe";
        if (!File.Exists(ffprobe))
        {
            Console.WriteLine("  ⚠ 未找到 ffprobe，跳过交叉验证");
        }
        else
        {
            VerifyWithFfprobe(ffprobe, path);
        }

        // 5. 用本仓库 av1C 解析二次确认
        Console.WriteLine("\n[4/4] 本仓库 av1C 解析...");
        VerifyWithAv1C(path);

        Console.WriteLine("\n══════════════════════════════════════");
        Console.WriteLine("✅ HDR 实测完成");
        Console.WriteLine("判读要点：");
        Console.WriteLine("  - ffprobe pix_fmt = yuv420p10le（10-bit 4:2:0）");
        Console.WriteLine("  - color_transfer=16 (ST.2084 PQ), color_primaries=9 (BT.2020)");
        Console.WriteLine("  - clli MaxCLL/MaxPALL = 非默认值（内容实际峰值）");
        Console.WriteLine("  - 本仓库 av1C 解析 bitDepth=10, chroma=420");
        Console.WriteLine("═注══ 若 NVENC HDR 直通失败会自动回退 avifenc 软件路径。");
        Console.WriteLine("══════════════════════════════════════");
    }

    private static void VerifyWithFfprobe(string ffprobe, string path)
    {
        var probeFields = new[]
        {
            "pix_fmt",
            "color_space",
            "color_transfer",
            "color_primaries",
            "bits_per_raw_sample",
            "side_data_list", // clli
        };
        var argsStr = $"-v error -select_streams v:0 -show_entries stream={string.Join(",", probeFields)} -of csv=p=0:s=x \"{path}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffprobe,
            Arguments = argsStr,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is null) { Console.WriteLine("  ❌ ffprobe 启动失败"); return; }
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(15000);

        Console.WriteLine($"  产物: {path}");
        Console.WriteLine($"  ffprobe 原始: {output}");
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine($"    {line}");
        }

        // ffprobe csv 输出按字段顺序: pix_fmt,color_space,color_transfer,color_primaries,bits,side_data
        var fields = output.Split(new[] { "x" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Trim()).ToArray();
        string pixFmt = fields.Length > 0 ? fields[0] : "";
        string colorSpace = fields.Length > 1 ? fields[1] : "";
        string colorTransfer = fields.Length > 2 ? fields[2] : "";
        string colorPrimaries = fields.Length > 3 ? fields[3] : "";

        // ── 位深/色度（pix_fmt yuv420p10le / yuv444p10le 都是 10-bit，色度不同）──
        bool is10Bit = pixFmt.Contains("p10", StringComparison.Ordinal) || pixFmt.Contains("p12", StringComparison.Ordinal);
        if (pixFmt.Contains("p10", StringComparison.Ordinal) || pixFmt.Contains("p12", StringComparison.Ordinal))
            Console.WriteLine($"  ✅ 码流位深: {pixFmt}（≥10-bit）");
        else if (pixFmt.Contains("p8", StringComparison.Ordinal) || pixFmt.Contains("420"))
            Console.WriteLine($"  ⚠ 码流位深偏低于 10-bit: {pixFmt}");
        else
            Console.WriteLine($"  ℹ 码流格式: {pixFmt}");

        // ── 传递函数（PQ = ST.2084）──
        if (colorTransfer.Contains("smpte2084", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  ✅ 传递函数: ST.2084 PQ (smpte2084)");
        else if (!string.IsNullOrEmpty(colorTransfer) && colorTransfer != "unknown")
            Console.WriteLine($"  ℹ 传递函数: {colorTransfer}");
        else
            Console.WriteLine("  ⚠ 传递函数未声明为 PQ (unknown)");

        // ── 原色/色度空间（BT.2020）──
        if (colorSpace.Contains("bt2020", StringComparison.OrdinalIgnoreCase) && colorPrimaries.Contains("bt2020", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine("  ✅ 原色/色度空间: BT.2020");
        else if (colorPrimaries.Contains("bt2020", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"  ✅ 原色: BT.2020 (色度空间={colorSpace})");
        else if (!string.IsNullOrEmpty(colorPrimaries) && colorPrimaries != "unknown")
            Console.WriteLine($"  ⚠ 原色: {colorPrimaries}（非 BT.2020）");
        else
            Console.WriteLine("  ⚠ 原色/色度空间未声明为 BT.2020 (unknown)");

        // ── NVENC 码流级色彩声明留空提示 ──
        // （交接 §9/§17.6：NVENC 驱动不写 color_description_present_flag，未知是预期）
        if (colorTransfer == "unknown" && colorSpace == "unknown")
            Console.WriteLine("  ℹ 码流级色彩声明未知 —— 若为 NVENC 直通产物，这是驱动已知边界，" +
                "色彩靠容器 colr(nclx) 声明（生产路径已按 PQ/BT.2020 写）。");
    }

    private static void VerifyWithAv1C(string path)
    {
        if (!AvifEncoderProbe.TryParseAv1C(File.ReadAllBytes(path), out var info))
        {
            Console.WriteLine("  ❌ av1C 解析失败");
            return;
        }
        Console.WriteLine($"  本仓库 av1C 解析: bitDepth={info.BitDepth}, chroma={info.Chroma}");
        if (info.BitDepth == 10 && info.Chroma == "420")
            Console.WriteLine("  ✅ 实测符合 10-bit 4:2:0 预期");
        else
            Console.WriteLine("  ⚠ 实测不符合预期");
    }
}