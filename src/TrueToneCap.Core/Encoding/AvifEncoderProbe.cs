// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/AvifEncoderProbe.cs
// AVIF 编码器【实测能力】探测 —— 回答"这个编码器到底能产出什么"，而不是"文档说它能什么"。
//
// 为什么需要它：
//   AvifEncoderCapabilities 是一张**手写**的能力表（NVENC 10-bit / libaom 12-bit / QSV 8-bit、
//   硬件只有 4:2:0 …）。它有两个先天缺陷：
//     ① 写表依据是机型实测与文档，换一张卡 / 换一个 avifenc 版本就可能不成立；
//     ② "参数被接受"不等于"参数真的生效" —— avifenc 请求 --depth 12 失败会静默回退 8-bit，
//        硬件后端任一步失败会回退 libaom 并照样写出一个有效 AVIF。
//   于是 UI 可能把"请求 12-bit"当成"产出 12-bit"，用户拿到的是 8-bit 文件却毫无察觉
//   （这正是 2026-08-10 / 2026-09-17 反复出现的"静默改参数"同类缺陷）。
//
//   本探针的做法与 AvifHardwareProbe 同源（真编码 + 独立判据），但判据更硬：
//     编码一张小样张 → **解析产物 AVIF 的 av1C 配置盒**，直接读回
//     high_bitdepth / twelve_bit / chroma_subsampling_x|y —— 即"实际产出了什么"。
//   av1C 是 ISOBMFF 里由编码器/封装器写入的声明，与像素实际编码格式一致（AV1 规范要求），
//   比"看命令行参数"或"看文件能不能打开"都更接近事实。
//
// ⚠ 判据纪律（交接 §3 教训）：绝不用"自己的第二份实现"做交叉验证。
//   本文件解析 av1C 只用于**读取编码器声明的产物格式**，不用于验证编码器正确性；
//   独立交叉验证请用外部工具（ffprobe / ffmpeg），见 Tools `--avif-caps` 的 ffprobe 对照输出。

using System.Diagnostics;
using System.Linq;
using System.Text;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>从产物 AVIF 的 av1C 配置盒读回的实际格式。</summary>
/// <param name="BitDepth">实际位深（8 / 10 / 12）。</param>
/// <param name="Chroma">实际色度（"420" / "422" / "444"）。</param>
public readonly record struct Av1CInfo(int BitDepth, string Chroma);

/// <summary>
/// 一个 AVIF 编码器的**实测**能力。
/// <para><see cref="Measured"/> 为 false 时表示尚未探测（或后端不可用），
/// 调用方必须回落到静态能力表 <see cref="AvifEncoderCapabilities"/>，
/// 不得把未探测值当成"不支持" —— 那会把可用选项误隐藏。</para>
/// </summary>
public sealed record AvifMeasuredCaps(
    AvifEncoderKind Kind,
    /// <summary>Auto 解析到的实际编码器（非 Auto 为 null）。</summary>
    AvifEncoderKind? ResolvedKind,
    /// <summary>后端是否可用（IsAvailable）。</summary>
    bool Available,
    /// <summary>是否完成实测。false ⇒ 用静态能力表。</summary>
    bool Measured,
    bool Chroma420,
    bool Chroma422,
    bool Chroma444,
    /// <summary>实测可达的 SDR 位深上限（8 / 10 / 12）。</summary>
    int MaxBitDepth,
    /// <summary>是否支持真 10-bit 输入（后端自报，非实测）。</summary>
    bool True10BitInput,
    /// <summary>HDR 路径是否可用（来自静态 SupportsHdrPath，非实测）。</summary>
    bool HdrPath,
    /// <summary>探测过程中的错误摘要（null = 无）。</summary>
    string? Error)
{
    /// <summary>该色度是否【实测】可用（未知值一律 false）。</summary>
    public bool SupportsChroma(string chroma) => chroma switch
    {
        "420" => Chroma420,
        "422" => Chroma422,
        "444" => Chroma444,
        _ => false,
    };

    /// <summary>人类可读摘要（日志 / 诊断用）。</summary>
    public override string ToString()
    {
        if (!Measured)
            return Available ? $"{Kind}: 未探测（回落静态表）" : $"{Kind}: 不可用";
        var chromas = new[] { ("420", Chroma420), ("422", Chroma422), ("444", Chroma444) }
            .Where(x => x.Item2).Select(x => x.Item1);
        string resolved = ResolvedKind is { } r ? $"（Auto→{r}）" : "";
        return $"{Kind}{resolved}: 实测 色度 {string.Join("/", chromas)} · 位深 ≤{MaxBitDepth}-bit"
             + (True10BitInput ? " · 真 10-bit 输入" : "")
             + (HdrPath ? " · HDR" : "")
             + (Error is null ? "" : $" · {Error}");
    }
}

/// <summary>
/// AVIF 编码器实测能力探测（按 kind 缓存；探测是重操作，只能在后台线程调用
/// <see cref="EnsureMeasured"/>，UI 线程只调用 <see cref="Get"/> 读缓存）。
/// </summary>
public static class AvifEncoderProbe
{
    /// <summary>探测样张边长。必须 ≥ <see cref="NvencAvifBackend.MinNvencDimension"/>，
    /// 否则 NVENC 会被前置判断跳过、转而走软件回退 —— 与 AvifHardwareProbe 的"假绿"同因。</summary>
    private const int ProbeSize = 256;

    /// <summary>单次编码的等待上限（软件 avifenc 在首次调用时要起进程，给足余量）。</summary>
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromSeconds(20);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<AvifEncoderKind, AvifMeasuredCaps> _cache = new();
    private static readonly object _measureLock = new();

    /// <summary>
    /// 读取缓存的实测结果。**永不触发 I/O**（后台未测完时返回 <c>Measured=false</c> 的记录）。
    /// </summary>
    public static AvifMeasuredCaps Get(AvifEncoderKind kind)
        => _cache.TryGetValue(kind, out var c)
            ? c
            : new AvifMeasuredCaps(kind, null, Available: true, Measured: false,
                Chroma420: true, Chroma422: true, Chroma444: true,
                MaxBitDepth: AvifEncoderCapabilities.SdrMaxBitDepth,
                True10BitInput: false,
                HdrPath: AvifEncoderCapabilities.SupportsHdrPath(kind),
                Error: null);

    /// <summary>
    /// 实测（或取缓存）指定编码器。⚠ 会真实编码若干张小样张并起 avifenc 子进程，
    /// **必须在后台线程调用**；UI 线程请只用 <see cref="Get"/>。
    /// </summary>
    public static AvifMeasuredCaps EnsureMeasured(AvifEncoderKind kind)
    {
        if (_cache.TryGetValue(kind, out var cached)) return cached;
        lock (_measureLock)
        {
            if (_cache.TryGetValue(kind, out var again)) return again;
            var caps = Measure(kind);
            _cache[kind] = caps;
            CoreLog.Info("AvifCapsProbe", $"实测能力: {caps}", LogCategory.Encoding);
            return caps;
        }
    }

    /// <summary>清空缓存（切换显卡 / 更新驱动 / 诊断重测时用）。</summary>
    public static void Invalidate() => _cache.Clear();

    /// <summary>是否已经完成过实测。</summary>
    public static bool IsMeasured(AvifEncoderKind kind)
        => _cache.TryGetValue(kind, out var c) && c.Measured;

    // ────────────────────────────── 探测主体 ──────────────────────────────

    private static AvifMeasuredCaps Measure(AvifEncoderKind kind)
    {
        // Auto 不是一种编码器：先解析成本机实际会挑中的那个，测它才有意义。
        AvifEncoderKind? resolved = null;
        var target = kind;
        if (kind == AvifEncoderKind.Auto)
        {
            target = AvifEncoderSelector.PickAvailableKind("420", 8);
            resolved = target;
        }

        var be = AvifEncoderSelector.SelectByKind(target, "420", 8);
        bool hdrPath = AvifEncoderCapabilities.SupportsHdrPath(target);
        bool true10 = be.SupportsTrue10BitInput;

        // ═══ 可用性必须按【kind】判定，不能只看后端实例的 IsAvailable ═══
        // SvtAv1 与 LibAom 共用同一个 LibAomAvifBackend 实例（IsAvailable 只表示"avifenc 在"），
        // 而 GetCodecArg 在 svt 不可用时返回**空串**（静默回落 aom）。若只看后端可用性，
        // 就会把 aom 的实测结果标成 SvtAv1 —— 正是"静默改参数"的同型问题。
        // 故这里先过一遍 IsKindAvailable（它按 kind 判定：SvtAv1 要求 --version 含 svt）。
        bool kindAvailable = kind == AvifEncoderKind.Auto
            ? be.IsAvailable
            : AvifEncoderSelector.IsKindAvailable(kind) && be.IsAvailable;

        if (!kindAvailable)
        {
            // ═══ 不可用必须给出**可行动**的原因 ═══
            // 只写"不可用"，用户既不知道是"这个 avifenc 没编进来"还是"这台机器没有这块硬件"，
            // 也就无从决定要不要换构建 / 换显卡。SvtAv1 尤其关键：本机内嵌 avifenc 1.4.2
            // 只编译了 aom（`--version` 无 `svt [enc]`），若不查版本就去测，
            // 会把 aom 的实测结果当成 SVT-AV1 的能力（2026-09-18 已修的误归因）。
            string reason = kind switch
            {
                AvifEncoderKind.SvtAv1 => NativeAvifEncoder.SvtUnavailableReason ?? "后端不可用",
                AvifEncoderKind.LibAom or AvifEncoderKind.Avifenc
                    => NativeAvifEncoder.IsAvailable ? "后端不可用" : "avifenc 不可用",
                AvifEncoderKind.Nvenc => "NVENC 不可用（无 N 卡 / 不支持 AV1 / 会话创建失败）",
                AvifEncoderKind.Qsv => "QSV/MFT 不可用（系统无 AV1 MFT 编码器）",
                _ => "后端不可用",
            };
            return new AvifMeasuredCaps(kind, resolved, Available: false, Measured: false,
                Chroma420: true, Chroma422: true, Chroma444: true,
                MaxBitDepth: AvifEncoderCapabilities.SdrMaxBitDepth,
                True10BitInput: true10, HdrPath: hdrPath, Error: reason);
        }

        var px = MakeProbeImage(ProbeSize);
        string? error = null;

        // ── ① 色度：请求 420/422/444，看产物 av1C 真正写的是什么 ──
        // 关键：硬件后端会**忽略**不支持的色度请求（仍产出 420），
        // 所以判据必须是"产物里读回的色度 == 请求的色度"，而不是"编码没报错"。
        bool c420 = DeliversChroma(be, target, px, "420");
        bool c422 = DeliversChroma(be, target, px, "422");
        bool c444 = DeliversChroma(be, target, px, "444");
        if (!c420) error = "连 4:2:0 都没产出（后端不可用或全部失败）";

        // ── ② 位深：请求 10/12，看产物 av1C 的 high_bitdepth / twelve_bit **是否等于请求值** ──
        // ⚠ 判据必须是"读回的位深 == 请求的位深"：avifenc 请求 12 失败会静默回退 8-bit，
        //   若只判"文件存在"就会得出"支持 12-bit"的假绿（与静态表同型错误）。
        int maxDepth = 8;
        if (DeliversDepth(be, target, px, 10)) maxDepth = 10;
        if (DeliversDepth(be, target, px, 12)) maxDepth = 12;

        return new AvifMeasuredCaps(kind, resolved, Available: true, Measured: c420,
            Chroma420: c420, Chroma422: c422, Chroma444: c444,
            MaxBitDepth: maxDepth, True10BitInput: true10, HdrPath: hdrPath, Error: error);
    }

    /// <summary>实测：请求某色度时，产物 av1C 读回的色度是否【真的是它】。</summary>
    private static bool DeliversChroma(IAvifEncoder be, AvifEncoderKind kind, byte[] px, string chroma)
        => Delivers(be, kind, px, chroma, 8, expectChroma: chroma, expectDepth: null, out _);

    /// <summary>实测：请求某位深时，产物 av1C 读回的位深是否【真的是它】。</summary>
    private static bool DeliversDepth(IAvifEncoder be, AvifEncoderKind kind, byte[] px, int depth)
        => Delivers(be, kind, px, "420", depth, expectChroma: null, expectDepth: depth, out _);

    /// <summary>编码一张小样张并判定"产物是否【真的】是请求的色度/位深"。
    /// <para>三重判据（缺一不可）：① 文件存在且非空；② 硬件后端必须自报"确实走了硬件"
    /// （否则是回退 libaom 的产物 —— AvifHardwareProbe 的假绿教训）；③ av1C 读回的格式匹配请求。</para>
    /// </summary>
    private static bool Delivers(IAvifEncoder be, AvifEncoderKind kind, byte[] px,
        string chroma, int depth, string? expectChroma, int? expectDepth, out int actualDepth)
    {
        actualDepth = 0;
        var path = Path.Combine(Path.GetTempPath(), $"ttc_caps_{Guid.NewGuid():N}.avif");
        try
        {
            var task = Task.Run(() => be.EncodeAsync(px, ProbeSize, ProbeSize, 30, path,
                CancellationToken.None, chroma, depth));
            if (!task.Wait(EncodeTimeout))
            {
                // 同 AvifHardwareProbe：超时后不能立刻删文件（编码器仍持有它）
                try { task.Wait(TimeSpan.FromSeconds(15)); } catch { }
                return false;
            }
            if (!File.Exists(path) || new FileInfo(path).Length <= 100) return false;

            // 硬件后端：文件有效 ≠ 硬件产出（回退 libaom 也会写出有效文件）
            if (IsHardwareKind(kind) && !HardwareDelivered(kind)) return false;

            var bytes = File.ReadAllBytes(path);
            if (!TryParseAv1C(bytes, out var info)) return false;

            actualDepth = info.BitDepth;
            if (expectChroma is not null && info.Chroma != expectChroma) return false;
            if (expectDepth is { } ed && info.BitDepth != ed) return false;
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("AvifCapsProbe", $"{kind} 探测样张编码失败 chroma={chroma} depth={depth} ⇒ 该项能力记为不支持",
                ex, LogCategory.Encoding);
            return false;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static bool IsHardwareKind(AvifEncoderKind kind)
        => kind is AvifEncoderKind.Nvenc or AvifEncoderKind.Qsv;

    /// <summary>该硬件后端上一次编码是否真的走了硬件（NVENC 直读，QSV/MFT 经反射 —— 同 AvifHardwareProbe）。</summary>
    private static bool HardwareDelivered(AvifEncoderKind kind)
    {
        if (kind == AvifEncoderKind.Nvenc) return NvencAvifBackend.LastEncodeUsedHardware;
        if (kind == AvifEncoderKind.Qsv) return MftLastEncodeUsedHardware();
        return true;
    }

    private static bool MftLastEncodeUsedHardware()
    {
        try
        {
            var asm = typeof(AvifEncoderProbe).Assembly;
            var t = asm.GetType("TrueToneCap.Core.Encoding.MftAvifEncode", throwOnError: false)
                     ?? asm.GetTypes().FirstOrDefault(x => x.Name == "MftAvifEncode");
            var f = t?.GetField("LastEncodeUsedHardware",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return f?.GetValue(null) is bool b && b;
        }
        catch { return false; }
    }

    /// <summary>探测样张：256×256 水平渐变 + 竖条纹理（避免纯色图被编码器走捷径）。</summary>
    private static byte[] MakeProbeImage(int size)
    {
        var px = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                byte v = (byte)(x * 255 / (size - 1));
                byte w = (byte)(y * 255 / (size - 1));
                byte tex = (byte)((x / 8) % 2 == 0 ? 255 : 200);
                px[i] = (byte)((v + tex) / 2);       // B
                px[i + 1] = w;                        // G
                px[i + 2] = (byte)(255 - v);          // R
                px[i + 3] = 255;                      // A
            }
        }
        return px;
    }

    // ────────────────────────────── av1C 解析 ──────────────────────────────

    /// <summary>
    /// 从 AVIF 文件字节中解析 <c>av1C</c> 配置盒，读回**实际**位深与色度。
    /// <para>AV1CodecConfigurationRecord（ISO/IEC 14496-15）byte2 位布局：
    /// seq_tier_0(1) | high_bitdepth(1) | twelve_bit(1) | monochrome(1) |
    /// chroma_subsampling_x(1) | chroma_subsampling_y(1) | chroma_sample_position(2)。</para>
    /// </summary>
    public static bool TryParseAv1C(byte[] data, out Av1CInfo info)
    {
        info = default;
        if (data is not { Length: > 16 }) return false;
        if (!ScanBoxes(data, 0, data.Length, out byte flags)) return false;

        bool high = (flags & 0x40) != 0;
        bool twelve = (flags & 0x20) != 0;
        bool subX = (flags & 0x08) != 0;
        bool subY = (flags & 0x04) != 0;

        int depth = twelve ? 12 : high ? 10 : 8;
        // subsampling_x/y: 00=4:4:4、10=4:2:2、11=4:2:0（01 保留）
        string chroma = (!subX && !subY) ? "444" : (subX && !subY) ? "422" : (subX && subY) ? "420" : "?";
        info = new Av1CInfo(depth, chroma);
        return chroma != "?";
    }

    /// <summary>递归遍历 ISOBMFF 盒，找到 av1C 并返回其配置字节 2。</summary>
    private static bool ScanBoxes(byte[] d, int start, int end, out byte flags)
    {
        flags = 0;
        int p = start;
        while (p + 8 <= end)
        {
            uint size = ReadU32BE(d, p);
            // ⚠ 必须全限定：本文件所在命名空间是 TrueToneCap.Core.Encoding，
            // 直接写 Encoding.ASCII 会被解析成命名空间而不是 System.Text.Encoding。
            string type = System.Text.Encoding.ASCII.GetString(d, p + 4, 4);
            int header = 8;
            if (size == 1)
            {
                if (p + 16 > end) return false;
                ulong big = ReadU64BE(d, p + 8);
                if (big > int.MaxValue) return false;
                size = (uint)big;
                header = 16;
            }
            else if (size == 0)
            {
                size = (uint)(end - p);
            }

            if (size < (uint)header || p + size > end) return false;

            if (type == "av1C")
            {
                // av1C 是普通 Box（无 version/flags），负载第 3 字节即格式字
                if (size < (uint)header + 3) return false;
                flags = d[p + header + 2];
                return true;
            }

            // 只递归可能包住 av1C 的容器（避免把 mdat 当盒子链去扫）
            if (type is "moov" or "trak" or "mdia" or "minf" or "stbl" or "stsd" or "av01"
                     or "iprp" or "ipco" or "meta" or "dinf" or "edts" or "moof" or "traf")
            {
                int inner = p + header;
                // FullBox（version/flags 4 字节）：meta / stsd；stsd 后还需跳过 entry_count(4)
                if (type is "meta") inner += 4;
                else if (type is "stsd") inner += 8;
                if (inner < (int)(p + size) && ScanBoxes(d, inner, (int)(p + size), out flags))
                    return true;
            }

            p += (int)size;
        }
        return false;
    }

    private static uint ReadU32BE(byte[] d, int p)
        => ((uint)d[p] << 24) | ((uint)d[p + 1] << 16) | ((uint)d[p + 2] << 8) | d[p + 3];

    private static ulong ReadU64BE(byte[] d, int p)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | d[p + i];
        return v;
    }

    /// <summary>诊断用：把所有 kind 的实测结果打成可读表格（含 Auto 解析结果）。</summary>
    public static string GetSummary()
    {
        var sb = new StringBuilder();
        foreach (var kind in new[] { AvifEncoderKind.Auto, AvifEncoderKind.Nvenc, AvifEncoderKind.Qsv,
                                     AvifEncoderKind.SvtAv1, AvifEncoderKind.LibAom, AvifEncoderKind.Avifenc })
        {
            sb.Append("  ").AppendLine(EnsureMeasured(kind).ToString());
        }
        return sb.ToString();
    }
}
