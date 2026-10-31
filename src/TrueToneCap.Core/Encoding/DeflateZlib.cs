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

    /// <summary>后端名（留痕与日志共用一份字面量，别让两处各拼一遍）。</summary>
    internal const string BackendLibDeflate = "libdeflate";

    /// <summary>内置后端名。</summary>
    internal const string BackendZlibNg = "zlib-ng";

    /// <summary>当前后端名（用于启动日志/诊断）：`libdeflate` 或 `zlib-ng`。</summary>
    public static string BackendName => IsAvailable ? BackendLibDeflate : BackendZlibNg;

    /// <summary>启动期探测一次并记日志。重复调用只探测一次，不产生重复日志。</summary>
    public static void Initialize() => Probe();

    /// <summary>用当前可用后端把 <paramref name="data"/> 压成 zlib 封装。失败一律回退内置实现。</summary>
    public static byte[] Compress(byte[] data, int level)
    {
        if (data.Length == 0) return CompressBuiltin(data, level);
        // 只有 Smallest(9) 且内容不是"几乎纯色/文字"时才外包：那种 UI 内容交给 libdeflate 会变大
        // （UI raw 实测 +7.7%），违背「极高 = 体积最小」这格菜单的承诺。
        return CompressWithDecision(data, level, SelectNativeBackend(data, level));
    }

    /// <summary>
    /// 流式压缩入口：把 zlib 封装的字节【增量】推给 <paramref name="destination"/>，调用方不需要
    /// 一整份压缩结果（PNG 的 IDAT 分块写出就是它的消费者）。
    /// <para><b>码流与 <see cref="Compress"/> 逐字节相同</b>：同 level、同一次 <c>Write(data)</c>、
    /// 同一后端判定 ⇒ 分段只发生在【搬运】那一层，不进 deflate。</para>
    /// <para>⚠ 只有内置臂是真流式（destination 侧那块 64 KiB 缓冲就是全部中间量）；
    /// libdeflate 侧本文件只绑定了【一次性】API（<c>libdeflate_zlib_compress</c>，见 BindExports），
    /// 没有流式入口，所以那条仍会先产出尺寸精确的数组再分块喂出去 —— 省掉的是 MemoryStream 的
    /// 扩容链与裁剪拷贝，不是那个数组本身。</para>
    /// </summary>
    /// <returns>实际使用的后端名（native 运行中失败会如实返回内置）。</returns>
    internal static string CompressTo(Stream destination, byte[] data, int level, bool useNative)
    {
        if (useNative && TryCompressNative(data, LibDeflateLevel, out var packed))
        {
            destination.Write(packed, 0, packed.Length);
            return BackendLibDeflate;
        }
        WriteBuiltinZlib(destination, data, level);
        return BackendZlibNg;
    }

    /// <summary>内置实现的流式写法：与 <see cref="CompressBuiltin"/> 同一 options、同一写入形状，
    /// 只差在目的地是调用方的流（因此没有缓冲初值可估，也没有末尾裁剪拷贝）。</summary>
    private static void WriteBuiltinZlib(Stream destination, byte[] data, int level)
    {
        var options = new ZLibCompressionOptions { CompressionLevel = level };
        using var zlib = new ZLibStream(destination, options, leaveOpen: true);
        zlib.Write(data);
    }

    /// <summary>
    /// 廉价可压缩性探针：从整帧均匀取最多 256 个 4 KiB 块（合计 1 MiB），用内置 level 1 压一次。
    /// <para>均匀取样而不是只取开头：UI 截图常见"大片纯色 + 一块照片/噪声"，只看开头会把
    /// 整帧误判成可压缩。1 MiB 的 level 1 在 4K 帧上约几毫秒，相对要省的秒级开销可忽略。</para>
    /// <para>一帧只判一次：分条并行的调用方走 <see cref="IsLibDeflateCandidate(byte[][])"/>
    /// 那条重载，判完把结论交给各条（见 <see cref="CompressWithDecision"/>）。</para>
    /// </summary>
    internal static bool IsLibDeflateCandidate(byte[] data)
    {
        const int chunk = 4096, maxChunks = 256;
        int total = chunk * maxChunks;
        if (data.Length <= total) return ProbeSample(data);

        var sample = new byte[total];
        int stride = data.Length / maxChunks;
        for (int i = 0; i < maxChunks; i++)
            Buffer.BlockCopy(data, i * stride, sample, i * chunk, chunk);
        return ProbeSample(sample);
    }

    /// <summary>整帧判定一次，结论交给各条复用（<see cref="CompressWithDecision"/>）。
    /// <para>存在的全部理由：分条并行后若每条各调一次 <see cref="Compress"/>，探针会从"一次几毫秒"
    /// 变成 4K 16-bit ≈ 64 次；而"按素材自适应"判的是整帧可压缩性，不是某一条的形状
    /// —— 逐条判定还会让同一张图的不同条落到不同后端，把 10-31 那批定下的单帧口径打散。</para>
    /// <para>采样仍是同一条尺子（256 块 × 4 KiB、内置 level 1、阈值 <see cref="LibDeflateMinRatio"/>），
    /// 只是把块 round-robin 摊到各条上，等价于跨整帧均匀取样。</para></summary>
    internal static bool IsLibDeflateCandidate(byte[][] strips)
    {
        const int chunk = 4096, maxChunks = 256;
        var sample = new byte[chunk * maxChunks];
        long totalBytes = 0;
        foreach (var s in strips) totalBytes += s.Length;
        if (totalBytes <= sample.Length)
        {
            // 帧比采样窗口还小（小图 / 极少条）：整帧首尾相接喂进去，口径与单块版一致。
            int at = 0;
            foreach (var s in strips) { Buffer.BlockCopy(s, 0, sample, at, s.Length); at += s.Length; }
            return ProbeSample(sample.AsSpan(0, at).ToArray());
        }

        long stride = totalBytes / maxChunks;
        int written = 0;
        for (int i = 0; i < maxChunks; i++)
        {
            long offset = i * stride;
            int s = 0;
            while (offset >= strips[s].Length) { offset -= strips[s].Length; s++; }
            // 靠近条尾时取不满 4 KiB ⇒ 从条首回绕，保证样本总长确定（比值口径不许随条数漂移）
            int take = (int)Math.Min(chunk, strips[s].Length - offset);
            if (take < chunk)
            {
                Buffer.BlockCopy(strips[s], (int)offset, sample, written, take);
                written += take;
                Buffer.BlockCopy(strips[s], 0, sample, written, chunk - take);
                written += chunk - take;
            }
            else
            {
                Buffer.BlockCopy(strips[s], (int)offset, sample, written, chunk);
                written += chunk;
            }
        }
        return ProbeSample(sample);
    }

    /// <summary>唯一的判据式子：两版采样共用，免得"同一张图两条路判得不一样"。</summary>
    private static bool ProbeSample(byte[] sample)
    {
        var packed = CompressBuiltin(sample, 1);
        return (double)packed.Length / sample.Length >= LibDeflateMinRatio;
    }

    /// <summary>整帧的后端判定（单块形态）。</summary>
    internal static bool SelectNativeBackend(byte[] frame, int level)
    {
        bool useNative = s_allowLibDeflate && level == 9 && IsAvailable && IsLibDeflateCandidate(frame);
        LogRouting(level, useNative, frame.LongLength);
        return useNative;
    }

    /// <summary>整帧的后端判定（分条形态）。</summary>
    internal static bool SelectNativeBackend(byte[][] strips, int level)
    {
        bool useNative = s_allowLibDeflate && level == 9 && IsAvailable && IsLibDeflateCandidate(strips);
        long raw = 0;
        foreach (var s in strips) raw += s.LongLength;
        LogRouting(level, useNative, raw);
        return useNative;
    }

    /// <summary>
    /// 把【这次保存走了哪个后端】写成一条可外查的留痕（2026-10-31 交付要求：分流必须是能被查到的
    /// 产品决定，而不是只有代码里的一次 if）。
    /// <para>档位：Debug = 诊断档。默认「简」档一行都不写（Brief 的运行照旧安静），用户把详细度调到
    /// 「诊断」起就能在日志文件里逐次查到；写的是"这次选了什么、raw 多大"，不含任何图像内容字节
    /// （<see cref="CoreLog"/> 的隐私地板只承诺刮参数值，不替调用方兜裸数据 —— 这里本来就没有）。</para>
    /// <para>量：一次保存【一行】。这两个重载是每帧唯一的判定入口（PNG 每保存一次调一次；TIFF
    /// 单条调一次、多条由整帧判一次再交给各条 —— 逐条各判是上一条交付已经判死过的形状），
    /// 所以不必再加节流，加了反而多一处可变状态。</para>
    /// <para>⚠ 只走 <see cref="CoreLog"/>：App 侧 <c>LogService</c> 的 StreamWriter 是
    /// <c>AutoFlush = true</c>，那是"崩溃前的行已经到了 OS"的唯一保证；本方法不许另开任何带缓冲的落盘。</para>
    /// </summary>
    private static void LogRouting(int level, bool useNative, long rawBytes)
    {
        CoreLog.Debug("Deflate",
            $"无损压缩后端选择: {(useNative ? BackendLibDeflate : BackendZlibNg)}" +
            $"（请求 level={level}，raw={rawBytes} B；libdeflate 实跑档={LibDeflateLevel}）",
            LogCategory.Encoding);
    }

    /// <summary>按已定好的结论压单块；native 侧失败仍回退内置（与 <see cref="Compress"/> 同一兜底）。</summary>
    internal static byte[] CompressWithDecision(byte[] data, int level, bool useNative)
    {
        if (useNative && TryCompressNative(data, LibDeflateLevel, out var compressed)) return compressed;
        return CompressBuiltin(data, level);
    }

    /// <summary>内置 zlib-ng 的实现（也是回退臂；测试直接用它做等价对撞）。</summary>
    public static byte[] CompressBuiltin(byte[] data, int level)
    {
        var options = new ZLibCompressionOptions { CompressionLevel = level };
        // 无初值时 MemoryStream 从 256 B 倍增扩容，4K 规模 ≈ 18 次 realloc + ~2× 产物量的拷贝流量；
        // 初值按【这一次调用的输入】外推（见 EstimatedCapacity），扩容链基本不再触发。
        // 容量只是缓冲尺寸，不进 deflate 码流 ⇒ 产物字节与容量估计无关，估错最多退化成旧行为。
        using var ms = new MemoryStream(EstimatedCapacity(data.Length));
        using (var zlib = new ZLibStream(ms, options, leaveOpen: true))
            zlib.Write(data);
        // zlib.Dispose() 写完最终块 + Adler32 后 ms 不再有任何写入 —— 这之后就是一次性读取位置，
        // GetBuffer() 暴露不缩容的原始缓冲；有效区仅 [0, Length)。返回类型是 byte[]，
        // 尾部容量过剩必须裁一次（这趟拷贝在改成流式 API 前消不掉，见 IDAT/回填对长度的依赖）。
        // 2026-10-31：PNG 的 IDAT 已经改走 CompressTo 流式路径，这里剩下的 byte[] 形态消费者
        // 只有 TIFF 的分条（每条 ≈1 MiB，必须拿到确切长度才能回填 273/279）与探针样本。
        int len = (int)ms.Length;
        var buf = ms.GetBuffer();
        if (buf.Length == len) return buf;    // 容量恰好命中：零拷贝直出
        var result = new byte[len];
        Buffer.BlockCopy(buf, 0, result, 0, len);
        return result;
    }

    /// <summary>
    /// 缓冲初值：只由【这次调用的输入长度】决定，不含任何跨调用状态。
    /// <para><b>2026-10-31 去掉的是什么</b>：原先这里有一份 <c>static long s_ratioPermille</c> 的 EMA
    /// （每次 level ≥ 6 的压缩观测一次、后续所有压缩读它）。三条理由判它出局：
    /// ① 它是"随调用历史变化的值" ⇒ 同一份输入保存两次可以分配得不同，而本仓对无损产物的口径是
    ///   确定性优先（产物字节不许依赖历史，分配尺寸同样不该依赖）；
    /// ② 它是【跨格式共用】的一份值：AVIF/JXL/WebP 保底到 PNG 的那条腿（FormatEncoders.PngLastResort
    ///   → ManagedPngEncoder.Encode，默认 Smallest）与用户真正要的 PNG 保存写的是同一个 static，
    ///   于是一次保底帧的比例会决定后面正常保存的初值 —— 谁先跑、跑了几次，答案就变了；
    /// ③ 它按"最近一帧"外推，而 UI（实测比例 0.0123）与照片（实测 0.226）差一个数量级，
    ///   EMA 的 3/4 慢跟意味着交替保存时两边都长期拿到错的初值。</para>
    /// <para><b>为什么 1/4 这个数</b>：上面 ③ 的两个实测读数（0.0123 / 0.226）与噪声上界 0.8369 一起
    /// 把内容的产物比例摊在 1%~84% 这一整段，任何单一常数都会在一端错。取 1/4 是拿两端的
    /// 【代价有界】换确定性：高估（UI 那端）多付的是 ≤ 输入 1/4 的一次清零，低估（噪声那端）
    /// 只是退回旧行为 —— MemoryStream 倍增链的拷贝总量 ≈ 2× 产物，本来就是旧版的成本。
    /// 2026-10-31 之后这条只剩 ≈1 MiB 级的缓冲（TIFF 分条 + 1 MiB 探针样本）要估，两端都在
    /// 几百 KB 量级。<b>读数（2026-10-31 实测，<c>--bench-deflate-cap</c>：1 MiB/次、level 9、
    /// 三档可压缩性夹具、读数按形状分列）</b>：① 初值<b>不进码流</b> —— 五种显式初值（含故意给的 8 B
    /// 与硬膨胀上界）与三形状×六臂的产物全部逐字节相同；② 分配上"只看本次输入的 1/4"是<b>有端错的</b>
    /// 一式：低熵内容（实测比例 0.012）比改动前的无初值形状<b>多</b>分配 238 KB/次，而 0.36 与 1.00
    /// 两档分别<b>少</b>分配 251 KB / 2,344 KB/次，整段交替 12 次少 7.07 MB；③ 耗时<b>判不出</b>
    /// （同一条代码路径两条臂的差就有 1.4 ms，小于它的一律读成打平）。⇒ 这一格是"高熵内容省掉倍增链、
    /// 低熵内容多付一次一次性缓冲"的交换，<b>不是纯收益</b>。</para>
    /// <para><b>EMA 那条由读数判死，不是由理由判死</b>：同台上加了 ema 臂（仪表自己持状态，产品没有），
    /// 交替序列整段它比本式<b>多</b>分配 2.65 MB = 自身噪声下限的 328 倍；ui 那一档它的中位初值被
    /// 后一帧的 noise 拖到 521 KB（=上面 ② 说的"串值"，现在有了数）。只在连续同内容的 ui 上它才赢
    /// （165 KB vs 276 KB/次）。⇒ 本文件不恢复历史型初值；要恢复得先有新读数。</para>
    /// <para>钳位两端保持原样：下限 64 KiB（免得小图也走一遍倍增链），上限是 deflate 的硬膨胀界
    /// （输入 + 输入/8 + 512，含块头与 Adler 的宽裕），估错永不越界、也不会溢出成负数。</para>
    /// </summary>
    internal static int EstimatedCapacity(int inputLength)
    {
        if (inputLength <= 0) return 256;
        long est = (long)inputLength / 4 + 1024;
        long bound = inputLength + (inputLength >> 3) + 512;   // deflate 最坏膨胀 + 块头/Adler 的宽裕上界
        long cap = Math.Min(Math.Max(est, 64 * 1024), bound);
        return (int)Math.Min(cap, int.MaxValue);
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
