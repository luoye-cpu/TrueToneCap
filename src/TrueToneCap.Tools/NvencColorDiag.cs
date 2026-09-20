// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/NvencColorDiag.cs
// NVENC 的 RGB→YUV 到底用哪张矩阵、什么摆幅 —— 纯色带实测诊断。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -c Release -- --nvenc-color-probe
//       python scripts/probes/nvenc_matrix_probe.py
//
//       dotnet run --project src/TrueToneCap.Tools -c Release -- --nvenc-color-probe sweep
//       python scripts/probes/nvenc_matrix_probe.py --sweep
//
// 为什么要这条：批次 8 要给 NVENC 填 NV_ENC_CONFIG_AV1 的
// colorPrimaries / transferCharacteristics / matrixCoefficients / colorRange 四个字段，
// 而 RGB→YUV 是【硬件自己做的】—— 矩阵不由我们选。声明一张它没用的矩阵，比不声明更坏：
// 不声明时解码器按容器 colr 走，声明错了则是"照着错说明书翻译像素"。
// 所以先测后写：同一张纯色带分别喂 NVENC 与 avifenc（后者矩阵由 --cicp 决定、已知），
// 解回 YUV 后比对候选 (601 / 709 / 2020) × (limited / full)。
//
// sweep 模式再往前一步：把 matrixCoefficients 逐个码点下发给驱动，看它到底听不听。
// 不听 ⇒ 只能改声明（或改成软件做 RGB→YUV 后喂 YUV）；听 ⇒ 硬件也能做 2020，
// 无需把精度和色度学降级成 BT.601。两种答案都 actionable，猜则是自欺。
//
// 尺寸 256×256 是硬要求：MinNvencDimension=128，小于它会被前置短路回退 avifenc
// —— 那正是 §30.9 里"扫参样张测不到 NVENC"的坑（交接 §31.2 B2 行）。

using TrueToneCap.Core.Encoding;

// ⚠ 放在全局命名空间（FormatBench 是 top-level statements，分发处引用全局类型）
public static class NvencColorDiag
{
    // 8 条纯色带：BGRA8，每条 32 行高。选色覆盖三原色 + 三个二次色 + 黑/白/中灰，
    // 因为矩阵系数在纯色上的响应是线性的、彼此独立的 —— 一张表就能区分 601/709/2020。
    private static readonly (byte R, byte G, byte B)[] Bands =
    [
        (0, 0, 0), (255, 255, 255), (255, 0, 0), (0, 255, 0),
        (0, 0, 255), (255, 255, 0), (0, 255, 255), (128, 128, 128),
    ];

    private const int W = 256, H = 256, BandH = 32;

    /// <summary>扫描哪些 H.273 矩阵码点：0=identity(RGB) 1=BT.709 2=unspecified
    /// 5=BT.470BG(601-625) 6=SMPTE170M(601-525) 9=BT.2020 NCL。
    /// 号域取自 nvEncodeAPI.h 的 NV_ENC_VUI_MATRIX_COEFFS —— 头文件明说该字段按 H.273 取值，
    /// 与 AV1 序列头自己的 matrix_coefficients 枚举不是一套（那个 2020 NCL 不是 9）。</summary>
    private static readonly uint[] SweepCodes = [0, 1, 2, 5, 6, 9];

    public static void Run(string? mode = null)
    {
        string dir = Path.Combine("artifacts", "precision", "probe", "nvenc_color");
        Directory.CreateDirectory(dir);
        if (mode == "sweep") { RunSweep(dir); return; }

        var bgra = BuildSdrBands();

        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("NVENC RGB→YUV 矩阵 / 摆幅实测（纯色带 256x256）");
        Console.WriteLine($"色带顺序: {string.Join(" ", Bands.Select(c => $"({c.R},{c.G},{c.B})"))}");
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine();

        var enc = EncoderFactory.Create(OutputFormat.AVIF);

        // 三种硬件/软件路径各出一份：NVENC 与 QSV(MFT) 的矩阵都由硬件自己定（待测），
        // libaom 那份的矩阵是我们自己下发的 --cicp（作量尺参照，必须先判成 709/full）。
        foreach (var (kind, name) in new[]
        {
            (AvifEncoderKind.Nvenc, "nvenc"), (AvifEncoderKind.LibAom, "libaom"),
            (AvifEncoderKind.Qsv, "qsv"),
        })
        {
            var be = AvifEncoderSelector.SelectByKind(kind);
            if (!be.IsAvailable)
            {
                Console.WriteLine($"⏭ {name}: 后端不可用 ⇒ 跳过（不能拿回退产物冒充硬件）");
                continue;
            }
            var s = SdrSettings(kind);
            string path = Path.Combine(dir, $"bands_{name}.avif");
            try
            {
                enc.EncodeSdrAsync((byte[])bgra.Clone(), W, H, s, path).GetAwaiter().GetResult();
                Report(name, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("下一步: py scripts/probes/nvenc_matrix_probe.py");
        Console.WriteLine("  它会解回 YUV 并对 (601/709/2020) × (limited/full) 六个候选做拟合，");
        Console.WriteLine("  用 avifenc/libaom 那份当参照（那份的矩阵是我们自己声明的）。");

        // ═══ 广色域 SDR（BT.2020 三原色 + HDR 关）—— 用户收窄色域后 SDR 只剩这一档广色域 ═══
        // 这一档要单独测：它下发给驱动的是 primaries=9 而 matrix=1（与软件路 avifenc 的
        // `--cicp 9/13/1` 同值，理由见 FormatHelper.NvencAvifCicp）。
        // "三原色 9 + 矩阵 1"这个组合是否真被两侧照做，此前从未实测过。
        foreach (var (kind, name) in new[]
        {
            (AvifEncoderKind.Nvenc, "nvenc_wg"), (AvifEncoderKind.LibAom, "libaom_wg"),
        })
        {
            var s = SdrSettings(kind);
            s.ColorSpaceTag = "BT2020";
            Try(name, p => enc.EncodeSdrAsync((byte[])bgra.Clone(), W, H, s, p));
        }

        RunHdr(enc, dir);
    }

    /// <summary>
    /// 码点扫描：每个候选矩阵码点各出一份 SDR + 一份 HDR 样张，由分析脚本反推
    /// "驱动实际用了哪张矩阵"，从而判出①这个字段驱动听不听、②哪个码点对应我们要的变换。
    /// </summary>
    private static void RunSweep(string dir)
    {
        var enc = EncoderFactory.Create(OutputFormat.AVIF);
        var bgra = BuildSdrBands();
        var px = BuildHdrBands();

        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("NVENC matrixCoefficients 码点扫描（H.273 号域）");
        Console.WriteLine($"候选码点: {string.Join(" ", SweepCodes)}");
        Console.WriteLine("══════════════════════════════════════");

        var be = AvifEncoderSelector.SelectByKind(AvifEncoderKind.Nvenc);
        if (!be.IsAvailable)
        {
            Console.WriteLine("❌ NVENC 后端不可用 ⇒ 扫不了，不要用别的后端产物冒充（会得出假结论）");
            return;
        }

        foreach (uint code in SweepCodes)
        {
            NvEncoderNative.ProbeMatrixCoefficientsOverride = code;
            // ⚠ 容器 colr 仍按产品逻辑写（SDR=601 / HDR=2020），与这里下发的码点无关：
            //   本模式判的是【像素实际被哪张矩阵变换了】，不是【我们声明了什么】。
            //   分析脚本在 sweep 模式下也不核对声明，只看像素。
            Try($"nvenc_mk{code}", path =>
                enc.EncodeSdrAsync((byte[])bgra.Clone(), W, H, SdrSettings(AvifEncoderKind.Nvenc), path));
            Try($"nvenc_hdr_mk{code}", path =>
                enc.EncodeAsync(new HdrFrameData
                {
                    Pixels = (float[])px.Clone(), Width = W, Height = H,
                }, HdrSettings(AvifEncoderKind.Nvenc), path));
        }
        NvEncoderNative.ProbeMatrixCoefficientsOverride = null;

        Console.WriteLine();
        Console.WriteLine("下一步: py scripts/probes/nvenc_matrix_probe.py --sweep");
        Console.WriteLine("  它对每个码点反推出实测矩阵，给出\"驱动是否听这个字段\"的结论。");
    }

    private static void Try(string name, Func<string, Task> encode)
    {
        string path = Path.Combine("artifacts", "precision", "probe", "nvenc_color", $"bands_{name}.avif");
        // 先删旧样张：本次失败/作废时不能留下上一轮的文件冒充"已测到"。
        try { File.Delete(path); } catch { /* 不存在或被占用 */ }
        // 每个码点都是独立一次判定：上一次 InitEncoder 失败会被记进 NVENC 失败缓存，
        // 后续码点便全部静默回退 avifenc —— 实测就是这样产出了六个字节数完全相同的"硬件样张"。
        NvencAvifBackend.ResetFailureCache();
        NvencAvifBackend.LastFallbackReason = null;
        EncoderFallbackNotifier.LastReason = null;
        try
        {
            encode(path).GetAwaiter().GetResult();
            string? degraded = NvencAvifBackend.LastFallbackReason ?? EncoderFallbackNotifier.LastReason;
            if (degraded is { Length: > 0 })
            {
                // 这条守卫是探针的命门：回退产物照样是合法 AVIF，光看文件存在与否查不出来。
                Console.WriteLine($"❌ {name,-16} 未真正走 NVENC ⇒ 样张作废（测的是 avifenc，不是硬件）\n"
                    + $"   {degraded}");
                try { File.Delete(path); } catch { }
                return;
            }
            Report(name, path);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ {name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
        }
    }

    private static void Report(string name, string path)
    {
        string actual = FormatHelper.ResolveActualPath(path);
        Console.WriteLine($"✅ {name,-16} → {actual} ({new FileInfo(actual).Length} B)"
            + (actual.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "  ⚠ 换格式回退了！" : ""));
    }

    private static EncodingSettings SdrSettings(AvifEncoderKind kind) => new()
    {
        Format = OutputFormat.AVIF,
        HdrOutput = false,
        AvifEncoderKind = kind,
        Quality = 15f,
        OutputBitDepth = 8,
        ChromaSubsampling = "420",
        ColorSpaceTag = "sRGB",
    };

    private static EncodingSettings HdrSettings(AvifEncoderKind kind) => new()
    {
        Format = OutputFormat.AVIF,
        HdrOutput = true,
        AvifEncoderKind = kind,
        Quality = 15f,
        OutputBitDepth = 10,
        ChromaSubsampling = "420",
        ColorSpaceTag = "BT2020",
    };

    private static byte[] BuildSdrBands()
    {
        var bgra = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            var (r, g, b) = Bands[y / BandH];
            for (int x = 0; x < W; x++)
            {
                int k = (y * W + x) * 4;
                bgra[k] = b; bgra[k + 1] = g; bgra[k + 2] = r; bgra[k + 3] = 255;
            }
        }
        return bgra;
    }

    /// <summary>
    /// HDR(PQ) 版纯色带 —— 因为 NvEncWantsFullRange 会同时作用到 HDR 直通路，
    /// 而那条路的矩阵【从未实测】：容器现在替它声明 matrix=9(BT.2020 NCL)，
    /// 万一硬件照旧用 601 转，那就是我们刚修掉的那个错在 HDR 上重犯。
    /// 源为 Float16 scRGB（1.0 = 80 nits，与管线一致），带值取真实 HDR 亮度档。
    /// </summary>
    private static float[] BuildHdrBands()
    {
        // (scRGB 线性值)：0=黑，12.5=1000nits，2.5=200nits，1.5=120nits
        float V200 = 200f / 80f, V1000 = 1000f / 80f, V120 = 120f / 80f;
        var hdrBands = new (float R, float G, float B)[]
        {
            (0, 0, 0), (V1000, V1000, V1000), (V200, 0, 0), (0, V200, 0),
            (0, 0, V200), (V200, V200, 0), (0, V200, V200), (V120, V120, V120),
        };
        var px = new float[W * H * 4];
        for (int y = 0; y < H; y++)
        {
            var (r, g, b) = hdrBands[y / BandH];
            for (int x = 0; x < W; x++)
            {
                int k = (y * W + x) * 4;
                px[k] = r; px[k + 1] = g; px[k + 2] = b; px[k + 3] = 1f;
            }
        }
        // 色带顺序要交给分析脚本，它按同一组 nits 值预测 PQ 码值
        Console.WriteLine();
        Console.WriteLine($"HDR 色带(scRGB线性): {string.Join(" ", hdrBands.Select(c => $"({c.R:F3},{c.G:F3},{c.B:F3})"))}");
        return px;
    }

    private static void RunHdr(ImageEncoder enc, string dir)
    {
        var px = BuildHdrBands();

        foreach (var (kind, name) in new[]
        {
            (AvifEncoderKind.Nvenc, "nvenc_hdr"), (AvifEncoderKind.LibAom, "libaom_hdr"),
        })
        {
            var be = AvifEncoderSelector.SelectByKind(kind);
            if (!be.IsAvailable)
            {
                Console.WriteLine($"⏭ {name}: 后端不可用 ⇒ 跳过");
                continue;
            }
            var s = HdrSettings(kind);
            string path = Path.Combine(dir, $"bands_{name}.avif");
            try
            {
                enc.EncodeAsync(new HdrFrameData
                {
                    Pixels = (float[])px.Clone(), Width = W, Height = H,
                }, s, path).GetAwaiter().GetResult();
                Report(name, path);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ {name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}");
            }
        }
    }
}
