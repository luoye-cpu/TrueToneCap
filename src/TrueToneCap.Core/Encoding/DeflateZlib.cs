// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/DeflateZlib.cs
// 无损 deflate 后端：优先可选 libdeflate.dll，缺失/装载失败/自检不过 ⇒ 自动回退内置 zlib-ng。

using System.IO.Compression;
using System.Runtime.InteropServices;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// PNG/TIFF 共用的 zlib 封装压缩入口。
/// <para><b>为什么要这一层</b>：level 9 的 deflate 在 16-bit 扫描线上是保存链最长的一段
/// （deflate 占 84–95%，见 `docs/reviews/performance/pipeline-measurements-2026-10-30.md`）。
/// 真实 HDR 截图（2250×1350 PQ）实测：zlib-ng 9 要 10.55s，libdeflate 10 只要 6.05s 且体积 −6.0%；
/// 合成噪声上 5.92s → 1.29s（−2.2%）；纯 UI 内容则由 zlib-ng 9 继续胜出（更快且小 7.7%）
/// ⇒ 按内容分流，不搞全量替换。两条臂解压后逐字节相等。</para>
/// <para><b>为什么默认回退</b>：deflate.dll 是可选的进程外资产，不是运行时自带；找不到它、
/// 架构不对、导出缺失、自检压缩回读不等、或运行中抛错，都必须退回内置 zlib-ng，
/// 绝不能让"省时间"变成"PNG/TIFF 存不出来"。探测结果在进程内只生成一次。</para>
/// <para><b>产物口径</b>：两条路都输出 zlib 封装（头 + Adler32），PNG IDAT 与 TIFF tag 8 的容器
/// 含义不变；但<b>压缩字节不同</b> —— 承诺是"解压后逐字节相同"，不是"产物逐字节相同"。</para>
/// </summary>
public static class DeflateZlib
{
    /// <summary>诊断开关：默认允许用 libdeflate；`TTC_DEFLATE=0` 强制内置 zlib-ng。</summary>
    private static readonly bool s_allowLibDeflate =
        Environment.GetEnvironmentVariable("TTC_DEFLATE") != "0";

    /// <summary>诊断开关：显式指定 deflate.dll 路径（测试/排查用；不存在时继续找默认位置）。</summary>
    private static readonly string? s_overridePath =
        Environment.GetEnvironmentVariable("TTC_DEFLATE_DLL");

    /// <summary>
    /// libdeflate 跑 level 10（不是 9）。
    /// <para>真实 HDR 截图（2250×1350 PQ raw，2026-10-31 实测）：zlib-ng 9 要 10,551ms / 5,499,379 B；
    /// libdeflate 9 是 2,816ms / +1.2%；libdeflate 10 是 6,050ms / <b>−6.0%</b>
    /// ⇒ 10 在这类"中等可压缩"真实内容上同时赢时间与体积。噪声臂同理（1,293ms / −2.2%）。
    /// libdeflate 11/12 体积再小 0.4% 但耗时接近甚至超过 zlib-ng，故不取。</para>
    /// <para>Quick(8) 仍走内置：libdeflate 的 8 与 9 在判据夹具上产物完全相同，
    /// 两档都外包会让 PNG/TIFF 的「极高/快速」变成产出同一份文件的菜单。</para>
    /// </summary>
    private const int LibDeflateLevel = 10;

    /// <summary>
    /// 可压缩性探针阈值：内置 level 1 在 1 MiB 采样上的压缩比 ≥ 此值 ⇒ 交给 libdeflate。
    /// <para>实测（2026-10-31，2250×1350 10-bit raw）：
    /// UI 内容 0.0123、真实 HDR 截图 0.3208、合成噪声 0.8369。
    /// 低于阈值（=几乎纯色/文字的 UI 截图）仍用 zlib-ng 9：那种数据它更快找到长匹配、产物也更小
    /// （UI raw：186,716 B vs libdeflate 9 的 201,046 B，+7.7%）；0.05 与 UI 的 0.012 留有 4× 余量。</para>
    /// </summary>
    private const double LibDeflateMinRatio = 0.05;

    private static readonly object s_lock = new();
    private static int s_state;            // 0 = 未探测，1 = 可用，2 = 不可用
    private static string s_detail = "";   // 可用时是路径，不可用是原因

    private static nint s_dll;
    private static AllocCompressorFn? s_alloc;
    private static FreeCompressorFn? s_free;
    private static ZlibCompressBoundFn? s_bound;
    private static ZlibCompressFn? s_compress;

    /// <summary>libdeflate 是否可用（会触发一次探测；启动期可先调 <see cref="Initialize"/>）。</summary>
    public static bool IsAvailable
    {
        get
        {
            Probe();
            return s_state == 1;
        }
    }

    /// <summary>当前后端名（用于启动日志/诊断）：`libdeflate` 或 `zlib-ng`。</summary>
    public static string BackendName => IsAvailable ? "libdeflate" : "zlib-ng";

    /// <summary>启动期探测一次并记日志。重复调用只探测一次，不产生重复日志。</summary>
    public static void Initialize() => Probe();

    /// <summary>用当前可用后端把 <paramref name="data"/> 压成 zlib 封装。失败一律回退内置实现。</summary>
    public static byte[] Compress(byte[] data, int level)
    {
        if (data.Length == 0) return CompressBuiltin(data, level);
        // 只有 Smallest(9) 且内容不是"几乎纯色/文字"时才外包：那种 UI 内容交给 libdeflate 会变大
        // （UI raw 实测 +7.7%），违背「极高 = 体积最小」这格菜单的承诺。
        if (s_allowLibDeflate && level == 9 && IsAvailable && IsLibDeflateCandidate(data)
            && TryCompressNative(data, LibDeflateLevel, out var compressed))
            return compressed;
        return CompressBuiltin(data, level);
    }

    /// <summary>
    /// 廉价可压缩性探针：从整帧均匀取最多 256 个 4 KiB 块（合计 1 MiB），用内置 level 1 压一次。
    /// <para>均匀取样而不是只取开头：UI 截图常见"大片纯色 + 一块照片/噪声"，只看开头会把
    /// 整帧误判成可压缩。1 MiB 的 level 1 在 4K 帧上约几毫秒，相对要省的秒级开销可忽略。</para>
    /// </summary>
    internal static bool IsLibDeflateCandidate(byte[] data)
    {
        const int chunk = 4096, maxChunks = 256;
        int total = chunk * maxChunks;
        byte[] sample;
        if (data.Length <= total)
        {
            sample = data;
        }
        else
        {
            sample = new byte[total];
            int stride = data.Length / maxChunks;
            for (int i = 0; i < maxChunks; i++)
                Buffer.BlockCopy(data, i * stride, sample, i * chunk, chunk);
        }

        var packed = CompressBuiltin(sample, 1);
        return (double)packed.Length / sample.Length >= LibDeflateMinRatio;
    }

    /// <summary>内置 zlib-ng 的实现（也是回退臂；测试直接用它做等价对撞）。</summary>
    public static byte[] CompressBuiltin(byte[] data, int level)
    {
        var options = new ZLibCompressionOptions { CompressionLevel = level };
        using var ms = new MemoryStream();
        using (var zlib = new ZLibStream(ms, options, leaveOpen: true))
            zlib.Write(data);
        return ms.ToArray();
    }

    private static bool TryCompressNative(byte[] data, int level, out byte[] compressed)
    {
        compressed = [];
        try
        {
            compressed = NativeCompress(data, level);
            return true;
        }
        catch (Exception ex)
        {
            // 运行中才坏掉也要退回内置：一次失败就永久停用，避免每次保存都付一次异常与日志。
            Disable($"压缩失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Probe()
    {
        if (Volatile.Read(ref s_state) != 0) return;
        lock (s_lock)
        {
            if (s_state != 0) return;

            if (!s_allowLibDeflate)
            {
                s_state = 2;
                s_detail = "诊断开关 TTC_DEFLATE=0";
                CoreLog.Info("Deflate", "无损压缩后端: 内置 zlib-ng（诊断开关 TTC_DEFLATE=0）", LogCategory.Encoding);
                return;
            }

            string? path = ResolveLibraryPath(out bool explicitPathMissing);
            if (path is null)
            {
                s_state = 2;
                s_detail = explicitPathMissing ? "TTC_DEFLATE_DLL 指向的文件不存在" : "未找到 deflate.dll";
                if (explicitPathMissing)
                {
                    CoreLog.Warn("Deflate",
                        $"TTC_DEFLATE_DLL 指向的文件不存在 ⇒ 回退内置 zlib-ng", LogCategory.Encoding);
                }
                else
                {
                    CoreLog.Info("Deflate",
                        "无损压缩后端: 内置 zlib-ng（未找到 deflate.dll；放到 exe 旁或 native/ 即启用 libdeflate）",
                        LogCategory.Encoding);
                }
                return;
            }

            try
            {
                BindExports(path);
                SelfTest();
                s_state = 1;
                s_detail = path;
                CoreLog.Info("Deflate", $"无损压缩后端: libdeflate（{path}）", LogCategory.Encoding);
            }
            catch (Exception ex)
            {
                s_state = 2;
                s_detail = $"{path}: {ex.GetType().Name}: {ex.Message}";
                CoreLog.Warn("Deflate",
                    $"libdeflate 不可用 ⇒ 回退内置 zlib-ng（{ex.GetType().Name}: {ex.Message}）",
                    LogCategory.Encoding);
                FreeIfLoaded();
            }
        }
    }

    private static string? ResolveLibraryPath(out bool explicitPathMissing)
    {
        explicitPathMissing = false;
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(s_overridePath)) candidates.Add(s_overridePath!);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "deflate.dll"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "native", "deflate.dll"));

        for (int i = 0; i < candidates.Count; i++)
        {
            if (File.Exists(candidates[i])) return Path.GetFullPath(candidates[i]);
        }
        explicitPathMissing = !string.IsNullOrWhiteSpace(s_overridePath);
        return null;
    }

    private static void BindExports(string path)
    {
        s_dll = NativeLibrary.Load(path);
        s_alloc = Marshal.GetDelegateForFunctionPointer<AllocCompressorFn>(
            NativeLibrary.GetExport(s_dll, "libdeflate_alloc_compressor"));
        s_free = Marshal.GetDelegateForFunctionPointer<FreeCompressorFn>(
            NativeLibrary.GetExport(s_dll, "libdeflate_free_compressor"));
        s_bound = Marshal.GetDelegateForFunctionPointer<ZlibCompressBoundFn>(
            NativeLibrary.GetExport(s_dll, "libdeflate_zlib_compress_bound"));
        s_compress = Marshal.GetDelegateForFunctionPointer<ZlibCompressFn>(
            NativeLibrary.GetExport(s_dll, "libdeflate_zlib_compress"));
    }

    /// <summary>自检：压一段确定性样本再用 .NET 的 ZLibStream 解回来，必须逐字节相等。</summary>
    private static void SelfTest()
    {
        var sample = new byte[64 * 1024];
        for (int i = 0; i < sample.Length; i++)
            sample[i] = (byte)((i * 31) ^ (i >> 7));
        var compressed = NativeCompress(sample, 9);
        using var ms = new MemoryStream(compressed);
        using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        zlib.CopyTo(decoded);
        if (!decoded.ToArray().AsSpan().SequenceEqual(sample))
            throw new InvalidDataException("自检压缩回读不一致");
    }

    private static unsafe byte[] NativeCompress(byte[] data, int level)
    {
        if (s_alloc is null || s_free is null || s_bound is null || s_compress is null)
            throw new InvalidOperationException("libdeflate 尚未绑定");

        nint compressor = s_alloc(level);
        if (compressor == 0)
            throw new InvalidOperationException($"libdeflate_alloc_compressor({level}) 返回 NULL");

        nint outPtr = 0;
        try
        {
            nuint bound = s_bound(compressor, (nuint)data.Length);
            if (bound == 0 || bound > int.MaxValue)
                throw new InvalidOperationException($"压缩输出界异常: {bound}");
            outPtr = Marshal.AllocHGlobal((int)bound);
            fixed (byte* p = data)
            {
                nuint written = s_compress(compressor, p, (nuint)data.Length, (byte*)outPtr, bound);
                if (written == 0 || written > bound)
                    throw new InvalidOperationException($"libdeflate_zlib_compress 返回 {written}");
                var result = new byte[(int)written];
                Marshal.Copy(outPtr, result, 0, (int)written);
                return result;
            }
        }
        finally
        {
            if (outPtr != 0) Marshal.FreeHGlobal(outPtr);
            s_free(compressor);
        }
    }

    private static void Disable(string reason)
    {
        lock (s_lock)
        {
            if (s_state == 2) return;
            s_state = 2;
            s_detail = reason;
            CoreLog.Warn("Deflate", $"libdeflate 运行中失败 ⇒ 本进程改用内置 zlib-ng（{reason}）",
                LogCategory.Encoding);
        }
    }

    private static void FreeIfLoaded()
    {
        if (s_dll != 0)
        {
            try { NativeLibrary.Free(s_dll); } catch { }
            s_dll = 0;
        }
        s_alloc = null; s_free = null; s_bound = null; s_compress = null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint AllocCompressorFn(int level);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeCompressorFn(nint compressor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nuint ZlibCompressBoundFn(nint compressor, nuint inN);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate nuint ZlibCompressFn(nint compressor, byte* inPtr, nuint inN, byte* outPtr, nuint outN);
}
