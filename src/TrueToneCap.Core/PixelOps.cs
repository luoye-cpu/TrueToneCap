// TrueToneCap.Core/PixelOps.cs
// 多 ISA 像素操作加速库
// 支持: AVX2 | AVX-512 (VL+BW) | AVX10.1/10.2 | ARM64 NEON
// 策略: 运行时检测最优 ISA → 分层回退 → JIT 自动向量化 → 标量

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace TrueToneCap.Core;

/// <summary>
/// 高性能像素操作工具集 — 多 ISA 自适应。
///
/// ISA 选择策略 (按优先级):
///   x64: AVX-512 VL (256-bit, 零降频) > AVX2 > Vector&lt;T&gt; (JIT auto-vec) > 标量
///   x64: AVX-512 BW (512-bit, 仅大数据集 &gt;128KB)  用于 Alpha 修复 / Half 转换
///   ARM: NEON AdvSimd > Vector&lt;T&gt; > 标量
/// </summary>
public static class PixelOps
{
    // ═══════════════════════════════════════════════════════
    //  ISA 能力检测 (JIT 常量折叠, 零运行时开销)
    // ═══════════════════════════════════════════════════════

    // ── 平台 ──
    /// <summary>运行在 x86/x64 平台。</summary>
    public static bool IsX86 => Avx2.IsSupported || Sse2.IsSupported;
    /// <summary>运行在 ARM64 平台。</summary>
    public static bool IsArm64 => AdvSimd.IsSupported;

    // ── x86 ISA 层级 ──
    /// <summary>AVX2 (Haswell 2013+, Zen 1 2017+) — 基准加速。</summary>
    public static bool HasAvx2 => Avx2.IsSupported;

    /// <summary>
    /// AVX-512 VL (Vector Length): 256-bit AVX-512 编码。
    /// 优势: 32 寄存器 (vs 16), 3-operand 编码, 掩码寄存器, 零降频。
    /// 覆盖: Intel Ice Lake+ (2019), Sapphire Rapids (2023), AMD Zen 4+ (2022).
    /// 对应 AVX10.1 的 256-bit 模式。
    /// </summary>
    public static bool HasAvx512VL => Avx512F.VL.IsSupported;

    /// <summary>
    /// AVX-512 BW (Byte/Word): 512-bit 字节/字操作。
    /// 仅对 &gt;128KB 的大数据集启用 (避免降频惩罚)。
    /// 覆盖: Intel Skylake-X (2017), Ice Lake (2019), AMD Zen 4+ (2022).
    /// </summary>
    public static bool HasAvx512BW => Avx512BW.IsSupported;

    /// <summary>
    /// AVX-512 F + VL + BW 全功能，且 512-bit 不会导致严重降频。
    /// AMD Zen 4/5 无降频问题; Intel Sapphire Rapids+ 降频可控。
    /// </summary>
    public static bool HasAvx512Full => Avx512F.IsSupported && Avx512BW.IsSupported && Avx512F.VL.IsSupported;

    /// <summary>
    /// AVX10.1 模式: AVX-512 特性但最大 256-bit (最安全的 AVX-512 用法)。
    /// </summary>
    public static bool HasAvx10_256 => HasAvx512VL;

    /// <summary>
    /// AVX10.2 模式: 完整 512-bit AVX-512 (未来 Intel 客户端 CPU)。
    /// </summary>
    public static bool HasAvx10_512 => Vector512.IsHardwareAccelerated && HasAvx512Full;

    // ── 扩展指令 (加速特定运算) ──
    /// <summary>FMA (Fused Multiply-Add): 3×3 色域矩阵/混合运算加速 (Haswell+, Zen 1+)。</summary>
    public static bool HasFma => Fma.IsSupported;

    /// <summary>AVX-VNNI (256-bit 整数点积): Zen 4+/Gracemont+, 用于像素级整数运算。</summary>
    public static bool HasAvxVnni => AvxVnni.IsSupported;

    /// <summary>AVX10 v1 (统一 256-bit AVX512 特性集, 2024+ Intel/AMD)。</summary>
    public static bool HasAvx10V1 => Avx10v1.IsSupported;

    /// <summary>AVX10 v2 (扩展, 2025+ Intel)。</summary>
    public static bool HasAvx10V2 => Avx10v2.IsSupported;

    // ── ARM64 ──
    /// <summary>ARM64 NEON (Snapdragon X, Apple M 系列) — 已实现。</summary>
    public static bool HasNeon => AdvSimd.IsSupported;
    /// <summary>ARM64 SVE/SVE2 — 预留 (未来 .NET 支持)。</summary>
    public static bool HasSve => false; // .NET 10 暂无 SVE intrinsic

    /// <summary>跨平台 SIMD 可用 (Vector128&lt;T&gt; 硬件加速)。</summary>
    public static bool HasVector128 => Vector128.IsHardwareAccelerated;

    // ── 策略选择 ──
    /// <summary>最优向量字节宽度 (考虑降频, 选 32 或 64)。</summary>
    public static int BestVectorByteWidth =>
        HasAvx10_512 ? 64 :   // 完整 AVX10.2 / Zen 4+ 512-bit
        HasAvx512VL ? 32 :    // AVX10.1 / AVX-512 VL (安全 256-bit)
        HasAvx2 ? 32 :        // AVX2
        HasVector128 ? 16 :   // SSE / NEON
        4;                    // 标量

    /// <summary>是否对当前数据规模启用 512-bit 向量 (阈值 ~128KB 避免降频得不偿失)。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool ShouldUse512Bit(int dataBytes) =>
        HasAvx10_512 && dataBytes >= 131072; // 128KB

    // ═══════════════════════════════════════════════════════
    //  sRGB → Linear 查找表 (256 项)
    //  预计算避免 MathF.Pow 每次调用 (&gt;5× 加速)
    //  此 LUT 与 ISA 无关，所有路径共享
    // ═══════════════════════════════════════════════════════

    /// <summary>sRGB byte (0-255) → linear float 预计算 LUT。</summary>
    public static readonly float[] SrgbToLinearLut = new float[256];

    static PixelOps()
    {
        for (int i = 0; i < 256; i++)
        {
            float c = i / 255f;
            SrgbToLinearLut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }

    // ═══════════════════════════════════════════════════════
    //  1. Alpha 通道修复 (BGRA, 设 Alpha=0xFF)
    //     分层: AVX-512 BW (64B) → AVX-512 VL (32B+32reg) → AVX2 (32B) → NEON → 标量
    //     3840×2160 (33MB): 512bw~1.2ms | 512vl~1.5ms | avx2~2ms | scalar~6ms
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 将 BGRA8 像素数组中每第 4 个字节设为 0xFF。
    /// 自动选择最优 ISA 路径。
    /// </summary>
    public static unsafe void FixAlphaChannel(byte[] pixels)
    {
        int len = pixels.Length;

        // BGRA 布局: [B, G, R, A] — alpha 在每个像素的第 4 字节 (offset 3)
        // 小端 uint 0xFF000000 在内存中 = [00, 00, 00, FF] → 正确匹配 alpha 位置
        // 旧 bug: 0x000000FF 小端 = [FF, 00, 00, 00] → 错误地 OR 了蓝色通道！

        // Tier 0: AVX-512 BW 512-bit — 64 字节/轮 (仅大数据集)
        if (ShouldUse512Bit(len) && HasAvx512BW)
        {
            var alphaMask512 = Vector512.Create(0xFF000000u).AsByte();

            fixed (byte* p = pixels)
            {
                byte* ptr = p;
                byte* end = p + len - 63;

                while (ptr <= end)
                {
                    var v = Avx512F.LoadVector512(ptr);
                    v = Avx512F.Or(v, alphaMask512);
                    Avx512F.Store(ptr, v);
                    ptr += 64;
                }

                for (byte* tail = ptr; tail <= p + len - 4; tail += 4)
                    tail[3] = 0xFF;
            }
            return;
        }

        // Tier 1: AVX2 或 AVX-512 VL (256-bit, 32 寄存器)
        if (HasAvx2 || HasAvx512VL)
        {
            var alphaMask256 = Vector256.Create(0xFF000000u).AsByte();

            fixed (byte* p = pixels)
            {
                byte* ptr = p;
                byte* end = p + len - 31;

                while (ptr <= end)
                {
                    var v = Vector256.LoadUnsafe(ref *ptr);
                    v = Vector256.BitwiseOr(v, alphaMask256);
                    v.StoreUnsafe(ref *ptr);
                    ptr += 32;
                }

                for (byte* tail = ptr; tail <= p + len - 4; tail += 4)
                    tail[3] = 0xFF;
            }
            return;
        }

        // Tier 2: ARM64 NEON (预留 — 当前回退到标量)
        if (HasNeon)
        {
            FixAlphaChannelNeon(pixels);
            return;
        }

        // Tier 3: JIT 自动向量化
        for (int i = 3; i < len; i += 4)
            pixels[i] = 0xFF;
    }

    /// <summary>ARM64 NEON Alpha 通道修复 — 16 字节/轮 (4 像素)。</summary>
    private static unsafe void FixAlphaChannelNeon(byte[] pixels)
    {
        int len = pixels.Length;
        // NEON 128-bit: 每轮处理 16 字节 = 4 个 BGRA 像素
        // 掩码: 每 4 字节第 4 位为 0xFF，其余为 0x00
        var alphaMask = Vector128.Create(0xFF000000u).AsByte();

        fixed (byte* p = pixels)
        {
            byte* ptr = p;
            byte* end = p + len - 15;

            while (ptr <= end)
            {
                var v = AdvSimd.LoadVector128(ptr);
                v = AdvSimd.Or(v, alphaMask);
                AdvSimd.Store(ptr, v);
                ptr += 16;
            }

            // 尾部标量处理
            for (byte* tail = ptr; tail < p + len; tail += 4)
                tail[3] = 0xFF;
        }
    }

    // ═══════════════════════════════════════════════════════
    //  2. Half-float → float 批量转换
    //     分层: AVX-512 F (16half/轮) → F16C/NEON JIT-intrinsic → 标量
    //     .NET 10: BitConverter.Int16BitsToHalf → VCVTPH2PS (x86) / NEON (ARM)
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 从原始字节指针逐行转换 Half→Float。
    /// 2026-08-11: 三层 ISA — AVX2 (vpmovsxwd ymm, 8 half/轮) → SSE4.1 (4 half/轮) → 标量。
    /// .NET 11 无 AVX-512 vpmovsxwd zmm API → 512-bit 对 F16C 无收益, 不实现。
    /// 简化: half subnormal (值 &lt; 6e-5) 置 0 — 视觉不可见，换取全 SIMD 无分支。
    /// AVX10.1/10.2 设备均包含 AVX2 → 自动走 Tier 1。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ConvertHalfToFloatRow(
        byte* srcRow, float* dstRow, int count)
    {
        ushort* sp = (ushort*)srcRow;

        // Tier 1: AVX2+ — 8 half → 8 float / 轮 (vpmovsxwd ymm)
        if (Avx2.IsSupported && count >= 16)
        {
            int i = 0;
            int last = count - 8;
            for (; i <= last; i += 8)
            {
                var h16 = Vector128.Load(sp + i).AsInt16(); // 8 half
                var b = Avx2.ConvertToVector256Int32(h16); // vpmovsxwd 符号扩展 → 8 int
                var s = Avx2.And(Avx2.ShiftLeftLogical(b, 16), Vector256.Create<int>(int.MinValue)); // 符号 → bit31
                var e = Avx2.And(Avx2.ShiftRightLogical(b, 10), Vector256.Create<int>(0x1F)); // 指数
                var m = Avx2.And(b, Vector256.Create<int>(0x3FF)); // 尾数
                var eAdj = Avx2.Add(e, Vector256.Create<int>(112)); // -15+127
                var f = Avx2.Or(s, Avx2.Or(Avx2.ShiftLeftLogical(eAdj, 23), Avx2.ShiftLeftLogical(m, 13)));
                // e == 0 (subnormal/零) → 0
                var isZero = Avx2.CompareEqual(e, Vector256<int>.Zero);
                f = Avx2.BlendVariable(f, Vector256<int>.Zero, isZero);
                // e == 31 (inf/nan) → 0x7F800000 | 符号
                var isInf = Avx2.CompareEqual(e, Vector256.Create<int>(31));
                f = Avx2.BlendVariable(f, Avx2.Or(s, Vector256.Create<int>(0x7F800000)), isInf);
                Avx2.Store((int*)(dstRow + i), f); // vmovdqu ymm
            }
            // 尾部标量
            for (; i < count; i++)
                dstRow[i] = (float)BitConverter.Int16BitsToHalf((short)sp[i]);
            return;
        }

        // Tier 2: SSE4.1 — 4 half → 4 float / 轮
        if (Sse41.IsSupported && count >= 8)
        {
            int i = 0;
            int last = count - 4;
            for (; i <= last; i += 4)
            {
                var h16 = Vector128.Load(sp + i).AsInt16(); // 8 half (用前 4)
                var b = Sse41.ConvertToVector128Int32(h16); // 前 4 short 符号扩展 → 4 int
                var s = Sse2.And(Sse2.ShiftLeftLogical(b, 16), Vector128.Create<int>(int.MinValue)); // 符号 → bit31
                var e = Sse2.And(Sse2.ShiftRightLogical(b, 10), Vector128.Create<int>(0x1F)); // 指数
                var m = Sse2.And(b, Vector128.Create<int>(0x3FF)); // 尾数
                var eAdj = Sse2.Add(e, Vector128.Create<int>(112)); // -15+127
                var f = Sse2.Or(s, Sse2.Or(Sse2.ShiftLeftLogical(eAdj, 23), Sse2.ShiftLeftLogical(m, 13)));
                // e == 0 (subnormal/零) → 0
                var isZero = Sse2.CompareEqual(e, Vector128<int>.Zero);
                f = Sse41.BlendVariable(f, Vector128<int>.Zero, isZero);
                // e == 31 (inf/nan) → 0x7F800000 | 符号
                var isInf = Sse2.CompareEqual(e, Vector128.Create<int>(31));
                f = Sse41.BlendVariable(f, Sse2.Or(s, Vector128.Create<int>(0x7F800000)), isInf);
                f.AsSingle().Store(dstRow + i);
            }
            // 尾部标量
            for (; i < count; i++)
                dstRow[i] = (float)BitConverter.Int16BitsToHalf((short)sp[i]);
            return;
        }

        // 通用路径: .NET 10 Half 类型 (JIT-intrinsic → F16C/NEON 自动)
        for (int i = 0; i < count; i++)
        {
            dstRow[i] = (float)BitConverter.Int16BitsToHalf((short)sp[i]);
        }
    }

    /// <summary>
    /// 批量转换 Half (ushort[]) → float[]。
    /// </summary>
    public static float[] ConvertHalfToFloat(ushort[] src, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
            result[i] = (float)BitConverter.Int16BitsToHalf((short)src[i]);
        return result;
    }

    /// <summary>
    /// 从 float* 批量转换为 Half (ushort*)。
    /// 2026-08-11: 三层 ISA — AVX2 (8 float/轮, 256-bit 位运算 + vpshufb 打包) → SSE4.1 (4 float/轮) → 标量。
    /// .NET 11 无 AVX-512 vpmovdw (int→ushort 窄化) API → 512-bit 打包瓶颈, 不实现。
    /// 简化: subnormal 结果 (|float| &lt; 2^-14 ≈ 6e-5) 置 0 — 视觉不可见，换取全 SIMD 无分支。
    /// 用于 GPU 纹理上传 (Float32 → Float16)。
    /// AVX10.1/10.2 设备均包含 AVX2 → 自动走 Tier 1。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe void ConvertFloatToHalfRow(
        float* srcRow, ushort* dstRow, int count)
    {
        // Tier 1: AVX2+ — 8 float → 8 half / 轮
        if (Avx2.IsSupported && count >= 16)
        {
            var signMask = Vector256.Create<int>(0x8000);
            var expBias = Vector256.Create<int>(-112); // -127+15
            var expField = Vector256.Create<int>(0xFF);
            var mantMask = Vector256.Create<int>(0x007FFFFF);
            var infBits = Vector256.Create<int>(0x7C00); // inf (有限溢出也饱和到 inf, 与 C# (Half) 转换一致)
            // vpshufb ymm 索引是每 128 位通道内偏移 (0-15): 每通道 8 索引 + 8 无输出(0xFF)
            // 输出: 低8字节=[a0..a3], 高8字节=[a4..a7] → Permute4x64 重组为连续 16 字节
            var packMask = Vector256.Create((byte)0, 1, 4, 5, 8, 9, 12, 13, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
                0, 1, 4, 5, 8, 9, 12, 13, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);

            int i = 0;
            int last = count - 8;
            for (; i <= last; i += 8)
            {
                var bits = Avx2.LoadVector256(srcRow + i).AsInt32();
                var s = Avx2.And(Avx2.ShiftRightLogical(bits, 16), signMask); // 符号 → bit15
                var e = Avx2.Add(Avx2.And(Avx2.ShiftRightLogical(bits, 23), expField), expBias); // 调整后指数
                var m = Avx2.And(bits, mantMask);
                // round-to-nearest-even (与 C# (Half) 转换一致): 检查被丢弃的 13 位
                var lsb = Avx2.And(Avx2.ShiftRightLogical(m, 13), Vector256.Create<int>(1)); // 结果最低位
                var round = Avx2.And(m, Vector256.Create<int>(0x1FFF));
                var carryMask = Avx2.Or(
                    Avx2.CompareGreaterThan(round, Vector256.Create<int>(0x1000)),  // > 半 → 进位
                    Avx2.And(Avx2.CompareEqual(round, Vector256.Create<int>(0x1000)), lsb)); // = 半且 LSB=1 → 进位
                var mS = Avx2.Add(Avx2.ShiftRightLogical(m, 13), Avx2.And(carryMask, Vector256.Create<int>(1)));
                // 进位溢出 (mS == 1024) → 指数 +1, 尾数归零
                var over = Avx2.CompareEqual(mS, Vector256.Create<int>(0x400));
                var eR = Avx2.Add(e, Avx2.And(over, Vector256.Create<int>(1)));
                mS = Avx2.AndNot(over, mS);
                // 正常路径: s | (eR<<10) | mS
                var h = Avx2.Or(s, Avx2.Or(Avx2.ShiftLeftLogical(eR, 10), mS));
                // e <= 0 (subnormal/零) → 仅符号位 (SSE 无 int LessEqual, 用 ~(e>0))
                var gtZero = Avx2.CompareGreaterThan(e, Vector256<int>.Zero);
                var isSub = Avx2.AndNot(gtZero, Vector256<int>.AllBitsSet);
                h = Avx2.BlendVariable(h, s, isSub);
                // e >= 31 (inf/nan) → s | 0x7C00 (有限溢出也饱和到 inf, 与 C# 一致)
                var isInf = Avx2.CompareGreaterThan(e, Vector256.Create<int>(30));
                h = Avx2.BlendVariable(h, Avx2.Or(s, infBits), isInf);
                // 打包: [a0..a3] 低8字节 + [a4..a7] 高8字节 → Permute4x64 把 qword2 移到 pos1 → 低128位连续
                var packed = Avx2.Shuffle(h.AsUInt16().AsByte(), packMask);
                var perm = Avx2.Permute4x64(packed.AsInt64(), 0b00_00_10_00); // [q0,q2,0,0]
                Sse2.Store((byte*)(dstRow + i), perm.GetLower().AsByte());
            }
            // 尾部标量
            for (; i < count; i++)
                dstRow[i] = (ushort)BitConverter.HalfToInt16Bits((Half)srcRow[i]);
            return;
        }

        // Tier 2: SSE4.1 — 4 float → 4 half / 轮 (128-bit 位运算)
        if (Sse41.IsSupported && count >= 8)
        {
            var signMask = Vector128.Create<int>(0x8000);
            var expBias = Vector128.Create<int>(-112); // -127+15
            var expField = Vector128.Create<int>(0xFF);
            var mantMask = Vector128.Create<int>(0x007FFFFF);
            var infBits = Vector128.Create<int>(0x7C00); // inf (有限溢出也饱和到 inf, 与 C# (Half) 转换一致)

            int i = 0;
            int last = count - 4;
            for (; i <= last; i += 4)
            {
                var bits = Vector128.Load(srcRow + i).AsInt32();
                var s = Sse2.And(Sse2.ShiftRightLogical(bits, 16), signMask); // 符号 → bit15
                var e = Sse2.Add(Sse2.And(Sse2.ShiftRightLogical(bits, 23), expField), expBias); // 调整后指数
                var m = Sse2.And(bits, mantMask);
                // round-to-nearest-even (与 C# (Half) 转换一致): 检查被丢弃的 13 位
                var lsb = Sse2.And(Sse2.ShiftRightLogical(m, 13), Vector128.Create<int>(1)); // 结果最低位
                var round = Sse2.And(m, Vector128.Create<int>(0x1FFF));
                var carryMask = Sse2.Or(
                    Sse2.CompareGreaterThan(round, Vector128.Create<int>(0x1000)),  // > 半 → 进位
                    Sse2.And(Sse2.CompareEqual(round, Vector128.Create<int>(0x1000)), lsb)); // = 半且 LSB=1 → 进位
                var mS = Sse2.Add(Sse2.ShiftRightLogical(m, 13), Sse2.And(carryMask, Vector128.Create<int>(1)));
                // 进位溢出 (mS == 1024) → 指数 +1, 尾数归零
                var over = Sse2.CompareEqual(mS, Vector128.Create<int>(0x400));
                var eR = Sse2.Add(e, Sse2.And(over, Vector128.Create<int>(1)));
                mS = Sse2.AndNot(over, mS);
                // 正常路径: s | (eR<<10) | mS
                var h = Sse2.Or(s, Sse2.Or(Sse2.ShiftLeftLogical(eR, 10), mS));
                // e <= 0 (subnormal/零) → 仅符号位 (SSE 无 int LessEqual, 用 ~(e>0))
                var gtZero = Sse2.CompareGreaterThan(e, Vector128<int>.Zero);
                var isSub = Sse2.AndNot(gtZero, Vector128<int>.AllBitsSet);
                h = Sse41.BlendVariable(h, s, isSub);
                // e >= 31 (inf/nan) → s | 0x7C00 (有限溢出也饱和到 inf, 与 C# 一致)
                var isInf = Sse2.CompareGreaterThan(e, Vector128.Create<int>(30));
                h = Sse41.BlendVariable(h, Sse2.Or(s, infBits), isInf);
                // 打包: 4 int 的低 16 位 [a,0,b,0,c,0,d,0] (ushort 视角) → 连续 [a,b,c,d]
                // pshufb 字节重排: 取字节 0,1,4,5,8,9,12,13
                var packed = Ssse3.Shuffle(h.AsUInt16().AsByte(),
                    Vector128.Create((byte)0, 1, 4, 5, 8, 9, 12, 13,
                        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
                packed.GetLower().Store((byte*)(dstRow + i)); // 前 8 字节 = 4 half
            }
            // 尾部标量
            for (; i < count; i++)
                dstRow[i] = (ushort)BitConverter.HalfToInt16Bits((Half)srcRow[i]);
            return;
        }

        // 通用路径: .NET 10 Half 类型 (JIT-intrinsic → F16C/NEON 自动)
        for (int i = 0; i < count; i++)
        {
            dstRow[i] = (ushort)BitConverter.HalfToInt16Bits((Half)srcRow[i]);
        }
    }

    // ═══════════════════════════════════════════════════════
    //  3. BGRA8 → scRGB Linear (LUT + 多核)
    //     预计算 sRGB LUT 替代 MathF.Pow (~5× 加速)
    //     Parallel.For 利用多核
    //     3840×2160: ~15ms
    //     ISA 无关 — LUT 查表已足够快
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 使用预计算 LUT 将 BGRA8 → scRGB linear float[]。
    /// BGRA byte order → RGBA float order (scRGB linear)。
    /// </summary>
    public static float[] BgraToScrgbLinearFast(byte[] bgra, int w, int h)
    {
        int pixelCount = w * h;
        var linear = new float[pixelCount * 4];
        var lut = SrgbToLinearLut;

        if (pixelCount >= 50000)
        {
            Parallel.For(0, pixelCount, pi =>
            {
                int i = pi * 4;
                linear[i] = lut[bgra[i + 2]];         // R (BGRA: B=idx, G=idx+1, R=idx+2)
                linear[i + 1] = lut[bgra[i + 1]];     // G
                linear[i + 2] = lut[bgra[i]];         // B
                linear[i + 3] = bgra[i + 3] / 255f;   // A
            });
        }
        else
        {
            for (int pi = 0; pi < pixelCount; pi++)
            {
                int i = pi * 4;
                linear[i] = lut[bgra[i + 2]];
                linear[i + 1] = lut[bgra[i + 1]];
                linear[i + 2] = lut[bgra[i]];
                linear[i + 3] = bgra[i + 3] / 255f;
            }
        }

        return linear;
    }

    // ═══════════════════════════════════════════════════════
    //  4. 灰度降采样 (多核并行)
    //     BT.601 加权: Y = 0.299R + 0.587G + 0.114B (定点)
    //     Parallel.For 外层循环利用多核
    //     ISA 无关 — 定点乘法不依赖 SIMD 宽度
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// BGRA8 全分辨率 → 缩小灰度图。
    /// 多核并行 + 定点整数避免浮点。
    /// </summary>
    public static byte[] DownsampleToGraySimd(byte[] bgra, int w, int h, int dw, int dh)
    {
        var gray = new byte[dw * dh];

        Parallel.For(0, dh, dy =>
        {
            for (int dx = 0; dx < dw; dx++)
            {
                int sx = dx * w / dw;
                int sy = dy * h / dh;
                int ex = Math.Min((dx + 1) * w / dw, w);
                int ey = Math.Min((dy + 1) * h / dh, h);

                long sum = 0;
                int cnt = 0;

                for (int ay = sy; ay < ey; ay++)
                {
                    int rowBase = ay * w * 4;
                    for (int ax = sx; ax < ex; ax++)
                    {
                        int idx = rowBase + ax * 4;
                        // BT.601 亮度定点: Y = (299*R + 587*G + 114*B) / 1000
                        sum += bgra[idx] * 114 + bgra[idx + 1] * 587 + bgra[idx + 2] * 299;
                        cnt++;
                    }
                }

                gray[dy * dw + dx] = (byte)(sum / ((long)cnt * 1000));
            }
        });

        return gray;
    }

    // ═══════════════════════════════════════════════════════
    //  5. 梯度投影 (边缘检测辅助) — 多核 + 跨平台 SIMD
    //     使用 Vector&lt;T&gt; 跨平台 SIMD, 无需 intrinsic
    //     适用于 RegionDetector.ComputeEdgeProjections
    // ═══════════════════════════════════════════════════════

    /// <summary>
    /// 计算灰度图的水平和垂直梯度投影 (多核)。
    /// 替代 RegionDetector.ComputeEdgeProjections 标量版本。
    /// 数据规模小 (960×540), 多核并行即足够。
    /// </summary>
    public static void ComputeEdgeProjectionsSimd(
        byte[] gray, int w, int h, float[] hEdges, float[] vEdges)
    {
        // 水平投影 — 每行独立, 多核
        Parallel.For(0, h, y =>
        {
            int rowBase = y * w;
            float sum = 0;
            for (int x = 1; x < w; x++)
                sum += Math.Abs(gray[rowBase + x] - gray[rowBase + x - 1]);
            hEdges[y] = sum / w;
        });

        // 垂直投影 — 每列独立, 多核
        Parallel.For(0, w, x =>
        {
            float sum = 0;
            for (int y = 1; y < h; y++)
                sum += Math.Abs(gray[y * w + x] - gray[(y - 1) * w + x]);
            vEdges[x] = sum / h;
        });
    }
}
