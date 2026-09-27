// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/NvEncoderNative.cs
// NVENC 原生 SDK — P/Invoke nvEncodeAPI64.dll (NVIDIA 驱动自带)
// 支持: AV1 (RTX 40+), HEVC (GTX 10+), D3D11 纹理直通

using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Encoding;

/// <summary>NVENC 原生编码器 — 直接调用 NVIDIA 驱动 DLL。</summary>
public sealed unsafe class NvEncoderNative : IDisposable
{
    private const int NVENC_SUCCESS = 0;
    // ═══ NV_ENC_DEVICE_TYPE ═══
    // ⚠ 该枚举在两个版本的 nvEncodeAPI.h 中含义不同：
    //   旧版: DIRECTX=0, CUDA=1
    //   新版: CUDA=0, DX9=1, DX11=2, OPENGL=3
    // 实测（驱动 616.56 / RTX 5080）：传 D3D11 设备时 deviceType=2 返回
    // NV_ENC_ERR_UNSUPPORTED_DEVICE(2)，而 deviceType=0 **成功**打开会话 ——
    // 即该驱动按旧版语义解释。为兼容两种定义，运行时依次尝试（见构造函数）。
    private const uint NV_ENC_DEVICE_TYPE_DIRECTX = 0;  // 旧版语义（实测有效）
    private const uint NV_ENC_DEVICE_TYPE_DX11 = 2;     // 新版语义
    // ═══ 2026-09-13 修正: 枚举值与官方 nvEncodeAPI.h 一致 ═══
    // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0x0 (不是 2, 2=CUDAARRAY)
    private const uint NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0;
    // NV_ENC_BUFFER_FORMAT_ARGB10 = 0x02000000 (A2R10G10B10 packed 10-bit)
    // NV_ENC_BUFFER_FORMAT_ARGB   = 0x01000000 (A8R8G8B8 packed 8-bit, 也就是 BGRA8 内存布局)
    // ═══ 2026-09-13 修正: BGRA8 输入用 ARGB, 不是 ARGB10 (后者为 10-bit) ═══
    private const uint NV_ENC_BUFFER_FORMAT_ARGB10 = 0x02000000;
    private const uint NV_ENC_BUFFER_FORMAT_ABGR10 = 0x20000000;   // A2B10G10R10 packed 10-bit
    private const uint NV_ENC_BUFFER_FORMAT_ARGB = 0x01000000;
    // ═══ 2026-09-16: NV16 = 8-bit 半平面 YUV 4:2:2（422 编码输入）═══
    // 仅 Blackwell 及之后 GPU 支持 4:2:2 编码（官方 Application Note 能力表），
    // 不支持的卡上 InitEncoder/CreateInputBuffer 会失败 → 由调用方回退软件编码。
    private const uint NV_ENC_BUFFER_FORMAT_NV16 = 0x40000001;
    // NV_ENC_INPUT_IMAGE = 0x0, NV_ENC_OUTPUT_BITSTREAM = 0x2
    private const uint NV_ENC_INPUT_IMAGE = 0;
    private const uint NV_ENC_PIC_FLAG_FORCEINTRA = 0x01;
    private const uint NV_ENC_PIC_FLAG_EOS = 0x08;

    // ═══ NVENC 结构体尺寸（bytes）——SDK 13.1 / x64，MSVC sizeof() 实测 ═══
    // 来源：nvEncodeAPI.h（NVENCAPI_MAJOR_VERSION 13 / MINOR_VERSION 1），
    // 由 cl.exe + sizeof()/offsetof() 导出，探针源码见 scripts/probes/nvoff.c。
    // ⚠ 这些不是估算值。此前按注释推定的尺寸【普遍偏小】，驱动写入时会越界写堆，
    //   症状是随机的、不可捕获的访问违规（正是 EncoderCrashSentinel 要兜的那类崩溃）。
    private const int InitParamsSize = 1800;        // NV_ENC_INITIALIZE_PARAMS
    private const int EncConfigSize = 3584;         // NV_ENC_CONFIG
    private const int PresetConfigSize = 5128;      // NV_ENC_PRESET_CONFIG      （旧 4104 → 越界 1024B）
    private const int FuncListSize = 2552;          // NV_ENCODE_API_FUNCTION_LIST（旧 512  → 越界 2040B）
    private const int OpenSessionExSize = 1552;     // NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS（旧 1088）
    private const int PicParamsSize = 3360;         // NV_ENC_PIC_PARAMS          （旧 2400 → 越界 960B）
    private const int LockBitstreamSize = 1544;     // NV_ENC_LOCK_BITSTREAM      （旧 2400，偏大无害）
    private const int LockInputBufferSize = 1544;   // NV_ENC_LOCK_INPUT_BUFFER   （旧 1600）
    private const int CreateInputBufferSize = 776;  // NV_ENC_CREATE_INPUT_BUFFER （旧 800）
    private const int CreateBitstreamSize = 776;    // NV_ENC_CREATE_BITSTREAM_BUFFER（旧 800）
    private const int RegisterResourceSize = 1536;  // NV_ENC_REGISTER_RESOURCE   （旧 264  → 越界 1272B）
    private const int MapInputResourceSize = 1544;  // NV_ENC_MAP_INPUT_RESOURCE  （旧 120  → 越界 1424B）

    // ── NV_ENC_CONFIG 字段偏移（绝对）──
    private const int CfgGopLength = 20;            // （旧注释写 24，实为 20）
    private const int CfgFrameIntervalP = 24;       // （旧注释写 28，实为 24）
    private const int CfgRcParams = 40;             // NV_ENC_RC_PARAMS 基址（旧注释写 44）
    private const int CfgAv1 = 168;                 // encodeCodecConfig.av1Config 基址
    // NV_ENC_RC_PARAMS: rateControlMode@4 → 绝对 44；constQP@8 → 绝对 48（qpInterP/B/Intra +0/+4/+8）
    private const int RcRateControlMode = CfgRcParams + 4;   // 44
    private const int RcConstQp = CfgRcParams + 8;           // 48
    // NV_ENC_CONFIG_AV1 相对偏移（基址 168）：
    //   level0 tier4 minPartSize8 maxPartSize12 位域组16 idrPeriod20 ... numFwdRefs104
    //   numBwdRefs108 outputBitDepth112 inputBitDepth116
    // ⚠ Av1BitfieldWord 是【实测出来的偏移，但从未被写入】—— 不是漏写一处赋值那么简单：
    //   不写 chromaFormatIDC ⇒ 驱动恒按 4:2:0 编码，请求 4:4:4 也【不报错】（交接 §26.2 色度扫描、
    //   §26.4 P2-3）。产品路径由 BuildEncodingSettings 的能力协商先把 444 降级并提示；
    //   后端被直接调用时的留痕在 FormatEncoders.ChromaDowngradeNote。
    //   留着本常量是给"真要按 NV16/4:2:2 打这条偏移"的人省一次 offsetof 实测（Blackwell 起才有 AV1 4:2:2）。
    private const int Av1BitfieldWord = CfgAv1 + 16;         // 184（chromaFormatIDC 在 bit3..4）
    private const int Av1OutputBitDepth = CfgAv1 + 112;      // 280
    private const int Av1InputBitDepth = CfgAv1 + 116;       // 284
    // 色彩描述四字段（相对 CfgAv1，NV_ENC_CONFIG_AV1 尾部）：
    //   colorPrimaries@68 transferCharacteristics@72 matrixCoefficients@76 colorRange@80
    //   chromaSamplePosition@84
    // 偏移来源是权威的：按 nvEncodeAPI.h 的字段顺序逐位核算（x64）——
    //   @0 level/@4 tier/@8 minPartSize/@12 maxPartSize，
    //   @16 起 20 个位域成员合计正好 1+1+1+1+1+1+1+2+1+1+1+1+1+1+1+2+14 = 32 bit ⇒ 独占一个 word，
    //   @20 idrPeriod … @44 reserved2 之后指针按 8 对齐落 @48/@56，@64 maxTemporalLayersMinus1，
    //   于是四个色彩字段落在 68/72/76/80、chromaSamplePosition@84、filmGrainParams@96。
    // 该推导与三个【实测锚点】互相印证：filmGrainParams 指针占 @96..103 ⇒ numFwdRefs@104 ✓、
    //   numBwdRefs@108 ✓、outputBitDepth@112 ✓（后两个正是本文件早已实测过的偏移）。
    // ⚠ 这里的取值域是 H.273(ISO/IEC 23091-2) 码点，头文件原文："as defined in section of
    //   ISO/IEC 23091-4/ITU-T H.273" ⇒ BT.2020 NCL 是 9；而 AV1 序列头自己的
    //   matrix_coefficients 枚举与 H.273 并不等价，不能把容器的号直接抄进码流。
    private const int Av1ColorPrimaries = CfgAv1 + 68;        // H.273 colour_primaries
    private const int Av1TransferCharacteristics = CfgAv1 + 72; // H.273 transfer_characteristics
    private const int Av1MatrixCoefficients = CfgAv1 + 76;    // H.273 matrix_coefficients
    private const int Av1ColorRange = CfgAv1 + 80;           // 0=studio(limited) 1=full

    /// <summary>
    /// 诊断钩子：非 null 时把该 H.273 码点写进 <c>av1Config.matrixCoefficients</c>。
    /// 唯一使用者是 <c>--nvenc-color-probe sweep</c>；产品路径恒为 null ⇒ 行为不变。
    /// <para>为什么需要它：RGB→YUV 是驱动做的，而"驱动是否真按这个字段选矩阵"
    /// 在文档里查不到确定答案（头文件只说取值域）。不扫描就只能二选一：
    /// 要么照抄一张硬件可能没用过的矩阵（=谎报），要么把已经能做的 2020 转换降级成 601。
    /// 两个都不要，所以逐个码点编纯色带、解回 YUV 反推实际矩阵（交接 §32.7）。</para>
    /// </summary>
    public static uint? ProbeMatrixCoefficientsOverride;

    /// <summary>
    /// 硬件 AVIF 要的采样范围。<b>true = full swing</b>：图片语义，且 10-bit 下
    /// full 给 1024 级而 limited 只给 876 级 ⇒ 降摆幅就是丢精度，本项目的取向是"要准确与高精度"，
    /// 所以这里把硬件提上来，而不是把软件路（avifenc 实测就是 full）降下去对齐。
    /// ⚠ 这个开关是否真被驱动执行，由纯色带探针判：
    ///   <c>--nvenc-color-probe</c> + <c>scripts/probes/nvenc_matrix_probe.py</c>
    ///   —— 它会比对"容器声明的 full_range"与"实测像素摆幅"，不一致就直接判失败。
    /// 容器 colr 的 full_range 字节与本字段必须同源取值，否则声明又和像素打架。
    /// </summary>
    public const bool NvEncWantsFullRange = true;

    // ── NV_ENC_REGISTER_RESOURCE 偏移 ──
    private const int RrWidth = 8;
    private const int RrHeight = 12;
    private const int RrPitch = 16;
    private const int RrSubResourceIndex = 20;
    private const int RrResourceToRegister = 24;             // （旧代码写 32）
    private const int RrRegisteredResource = 32;             // out（旧代码读 48）
    private const int RrBufferFormat = 40;                   // （旧代码写 20）
    private const int RrBufferUsage = 44;                    // （旧代码写 24）

    // ── NV_ENC_MAP_INPUT_RESOURCE 偏移 ──
    private const int MirInputResource = 8;
    private const int MirRegisteredResource = 16;            // （旧代码写 8）
    private const int MirMappedResource = 24;                // out
    private const int MirMappedBufferFmt = 32;               // out

    // ═══ NV_ENC_BIT_DEPTH（实测：不是 1/2）═══
    // NV_ENC_BIT_DEPTH_INVALID = 0, NV_ENC_BIT_DEPTH_8 = 8, NV_ENC_BIT_DEPTH_10 = 10
    private const uint NvEncBitDepth8 = 8;
    private const uint NvEncBitDepth10 = 10;

    // ═══ NV_ENC_PARAMS_RC_MODE（实测）═══
    private const uint NV_ENC_PARAMS_RC_CONSTQP = 0;   // VBR = 1, CBR = 2

    // ═══ 运行时缓冲区大小校验（防止驱动升级导致静默越界）═══
    // 依据实际手写偏移校验，不依赖 C# 结构体布局（后者与手写偏移不一致，校验无意义）。
    // 关键偏移说明（64-bit，来自 nvEncodeAPI.h 与实测）：
    //   NV_ENC_INITIALIZE_PARAMS: version(4) + pad(4) + encodeGUID(16)@8 + presetGUID(16)@24
    //       + encodeWidth(4)@40 + encodeHeight(4)@44 + darWidth(4)@48 + darHeight(4)@52
    //       + frameRateNum(4)@56 + frameRateDen(4)@60 + enableEncodeAsync(4)@64 + enablePTD(4)@68
    //       + ... + encodeConfig(nint)@88 (8字节) → 需 >= 96 bytes
    //   NV_ENC_CONFIG: version(4) + pad(4) + profileGUID(16)@8 + ... + rateControl字段到约 200+ bytes
    //   NV_ENC_PRESET_CONFIG: version(4)+reserved(4)+NV_ENC_CONFIG@8 → 需 >= 8+EncConfigSize
    private static void ValidateBufferSizes()
    {
    // 校验：本文件手写访问的「最大偏移 + 字段宽度」必须 ≤ 该结构的真实 sizeof。
    // 真实 sizeof 由 MSVC offsetof/sizeof 实测得到（scripts/probes/nvoff.c），
    // 不依赖 C# 结构布局（后者与手写偏移不一致，校验无意义）。
        Check(140 + 4, InitParamsSize, nameof(InitParamsSize));                 // bufferFormat@140
        Check(Av1InputBitDepth + 4, EncConfigSize, nameof(EncConfigSize));      // av1Config.inputBitDepth@284
        Check(8 + EncConfigSize, PresetConfigSize, nameof(PresetConfigSize));   // version+reserved + NV_ENC_CONFIG@8
        Check(68 + 4, PicParamsSize, nameof(PicParamsSize));                    // pictureStruct@68
        Check(RrBufferUsage + 4, RegisterResourceSize, nameof(RegisterResourceSize));
        Check(MirMappedBufferFmt + 4, MapInputResourceSize, nameof(MapInputResourceSize));
        Check(24 + 4, LockBitstreamSize, nameof(LockBitstreamSize));            // pictureType 之后仍在结构内
        Check(96 + 8, FuncListSize, nameof(FuncListSize));                      // nvEncInitializeEncoder@96
        Check(24 + 4, OpenSessionExSize, nameof(OpenSessionExSize));            // apiVersion@24
    }

    private static void Check(int need, int have, string name)
    {
        if (need > have)
            throw new InvalidOperationException(
                $"[NVENC] 手写偏移上限 {need} 超出 {name}={have}（结构真实 sizeof 偏小或偏移写错）。");
    }

    // ── 支持的 SDK 版本 (NVENCAPI_VERSION = MAJOR | (MINOR << 24)) ──
    // SDK 13.1: 13 | (1 << 24) = 0x0100000D  (Blackwell, driver 570+, latest)
    // SDK 13.0: 13 | (0 << 24) = 0x0000000D  (Blackwell)
    // SDK 12.2: 12 | (2 << 24) = 0x0200000C  (Ada Lovelace)
    // SDK 12.1: 12 | (1 << 24) = 0x0100000C
    // SDK 12.0: 12 | (0 << 24) = 0x0000000C
    // SDK 11.1: 11 | (1 << 24) = 0x0100000B
    // SDK 11.0: 11 | (0 << 24) = 0x0000000B
    // 参考: FFmpeg nv-codec-headers/include/ffnvcodec/nvEncodeAPI.h
    // ═══ 2026-09-01 更正: 恢复「高版本优先」═══
    // 此前曾把 SDK 12.2 提到首位，理由是"13.x 的 InitializeEncoder 返回
    // NV_ENC_ERR_INVALID_VERSION(15)"。但那个错误的真实原因是结构体 version 字段
    // 写成了裸版本号（缺 (n<<16)|(0x7<<28)），与 SDK 版本本身无关。
    // subversion 修正后（INITIALIZE_PARAMS=5、CONFIG=6），13.x 已可正常初始化。
    //
    // 高版本优先对 AV1 至关重要：RTX 5080 (Blackwell) 的 AV1 编码需要较新 SDK，
    // 旧 SDK 下 InitializeEncoder 会返回 NV_ENC_ERR_NO_ENCODE_CAPABILITY (0x8)。
    // ═══ 2026-09-05 修复: SDK 版本探测顺序 ═══
    // ✓ 版本号格式: NVENCAPI_VERSION = MAJOR | (MINOR << 24)
    //   SDK 13.1 = 13 | (1 << 24) = 0x0100000D
    // ✓ 实测（驱动 616.56 / RTX 5080）:
    //   · CreateInstance 宽松接受 10.0~13.2 各版本
    //   · 但 OpenEncodeSessionEx 严格校验 apiVersion —— 13.2 (0x0200000D) 返回
    //     NV_ENC_ERR_INVALID_VERSION (0xF)，session 无法打开
    //   · 13.1 可正常打开 session 且支持 AV1 → 放在首位
    private static readonly uint[] s_sdkVersions = [
        0x0100000D,  // SDK 13.1 (Blackwell, driver 570+; 实测 RTX 5080 616.56 可用)
        0x0000000D,  // SDK 13.0 (Blackwell)
        0x0200000C,  // SDK 12.2 (Ada)
        0x0100000C,  // SDK 12.1
        0x0000000C,  // SDK 12.0
        0x0100000B,  // SDK 11.1
        0x0000000B,  // SDK 11.0
    ];

    // ── GUIDs ──
    // ═══ 2026-09-01 修正: AV1 codec GUID ═══
    // 原实现写为 0x0A78D0B8-1D63-4B45-8E6B-C5F41C7C8C2C，与真实值不符，
    // 导致 GetEncodePresetConfig / InitializeEncoder 返回 UNSUPPORTED_PARAM (0xC)。
    // 正确值由驱动自身通过 NvEncGetEncodeGUIDs 报告（实测 RTX 5080 / 4060）:
    //   H264 = 6bc82762-4e63-4ca4-aa85-1e50f321f6bf
    //   HEVC = 790cdc88-4522-4d7b-9425-bda9975f7603
    //   AV1  = 0a352289-0aa7-4759-862d-5d15cd16d254
    // 即 nvEncodeAPI.h 中的 NV_ENC_CODEC_AV1_GUID。
    internal static readonly Guid CodecAv1 =
        new(0x0A352289, 0x0AA7, 0x4759, 0x86, 0x2D, 0x5D, 0x15, 0xCD, 0x16, 0xD2, 0x54);
    // 以下三者均已与驱动报告的 GUID 列表逐一核对
    private static readonly Guid CodecHevc =
        new(0x790CDC88, 0x4522, 0x4D7B, 0x94, 0x25, 0xBD, 0xA9, 0x97, 0x5F, 0x76, 0x03);
    private static readonly Guid CodecH264 =
        new(0x6BC82762, 0x4E63, 0x4CA4, 0xAA, 0x85, 0x1E, 0x50, 0xF3, 0x21, 0xF6, 0xBF);
    // ═══ 2026-09-13 修正: NV_ENC_PRESET_P1_GUID 官方值 ═══
    // 来源: nvEncodeAPI.h (SDK 13.1, FFmpeg nv-codec-headers)
    //   {FC0A8D3E-45F8-4CF8-80C7-298871590EBF}
    // 旧值 49DF21C5-6DFA-4FEB-9781-51BEE52B390C 是错误来源的魔数, 驱动不接受。
    private static readonly Guid PresetP1 =
        new(0xFC0A8D3E, 0x45F8, 0x4CF8, 0x80, 0xC7, 0x29, 0x88, 0x71, 0x59, 0x0E, 0xBF);

    // NV_ENC_CODEC_PROFILE_AUTOSELECT_GUID = {BFD6F8E7-233C-4341-8B3E-4818523803F4}
    // 让驱动为该 codec 自动选择合适 profile（官方推荐, 用于 profileGUID 兜底）
    private static readonly Guid CodecProfileAutoSelect =
        new(0xBFD6F8E7, 0x233C, 0x4341, 0x8B, 0x3E, 0x48, 0x18, 0x52, 0x38, 0x03, 0xF4);

    // NV_ENC_AV1_PROFILE_MAIN_GUID = {5F2A39F5-F14E-4F95-9A9E-B76D568FCF97}
    // AV1 Main profile (SDR/8-bit 场景所需; 10-bit 用 Main10 同理)
    private static readonly Guid CodecAv1ProfileMain =
        new(0x5F2A39F5, 0xF14E, 0x4F95, 0x9A, 0x9E, 0xB7, 0x6D, 0x56, 0x8F, 0xCF, 0x97);

    // NV_ENC_TUNING_INFO_HIGH_QUALITY = 1 (FFmpeg 默认; tuningInfo=UNDEFINED(0) 会被驱动拒绝)
    private const uint NV_ENC_TUNING_INFO_HIGH_QUALITY = 1;

    /// <summary>枚举驱动报告支持的编码器 codec GUID。</summary>
    /// <returns>GUID 列表；调用失败或不支持时返回空列表。</returns>
    public unsafe List<Guid> EnumerateCodecGuids()
    {
        var list = new List<Guid>();
        if (_encoder == 0) return list;
        try
        {
            var countFn = GetFuncDelegate<GetEncodeGuidCountFn>(GetFuncPtr(FnGetEncodeGuidCount));
            int hr = countFn(_encoder, out uint count);
            if (hr != NVENC_SUCCESS || count == 0 || count > 64) return list;

            var guidsFn = GetFuncDelegate<GetEncodeGuidsFn>(GetFuncPtr(FnGetEncodeGuids));
            int bytes = (int)count * 16;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hr = guidsFn(_encoder, buf, count, out uint actual);
                if (hr != NVENC_SUCCESS) return list;
                for (uint i = 0; i < actual && i < count; i++)
                    list.Add(new Guid(new ReadOnlySpan<byte>((void*)(buf + (int)i * 16), 16)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { /* 枚举失败不影响主流程 */ }
        return list;
    }

    // ═══ 2026-09-17 P1 修复：非 readonly —— 构造失败时需在 catch 中置 0 后释放 ═══
    private nint _dll;
    private nint _funcTable;
    private nint _encoder;
    // ═══ 2026-09-17 P1 修复：非 readonly（在 Initialize 中赋值）+ 可空（构造未完成时为 null）═══
    private ID3D11Device? _d3dDevice;
    private bool _disposed;
    // ═══ 2026-09-17 P1 修复：Dispose 与编码互斥 ═══
    // _disposed 非 volatile 且 DestroyEncoder 可在编码中途发生（原生 use-after-free）。
    // 编码方法经 EncodeRaw/EncodeRawFromTexture 包装持锁；Dispose 亦持锁 ⇒ 串行化。
    private readonly object _gate = new();
    private uint _activeVersion;

    /// <summary>仅检查 DLL 存在性 (轻量)。</summary>
    public static bool IsDllPresent
    {
        get
        {
            try { var h = NativeLibrary.Load("nvEncodeAPI64.dll"); NativeLibrary.Free(h); return true; }
            catch { return false; }
        }
    }

    /// <summary>完整可用性检查：DLL 存在 + 能在**任一 NVIDIA 适配器**上打开编码会话。</summary>
    /// <remarks>
    /// ═══ 2026-08-31 修复: 混合显卡系统上 NVENC 被误判为不可用 ═══
    /// <para>
    /// 原实现用 <c>D3D11CreateDevice(DriverType.Hardware)</c> 且**不指定适配器**，
    /// 此时 D3D11 使用"默认适配器"。在 Intel 核显 + NVIDIA 独显的混合配置下，
    /// 默认适配器常常是核显（主显示器接在核显上时尤其如此），
    /// 于是在核显上打开 NVENC 会话必然失败 → IsAvailable 返回 false
    /// → 所有 NVIDIA GPU 被 GpuCapability 标记为不可用 → Auto 永远选软件编码。
    /// </para>
    /// <para>
    /// 实测案例（RTX 5080 + RTX 4060 Laptop + Intel UHD）：
    /// 两块 N 卡均 <c>SupportsAv1=True</c> 但 <c>Available=False</c>，
    /// 强制选 NVENC 后输出与 libaom **字节数完全相同**（说明已回退）。
    /// </para>
    /// <para>
    /// 修复：默认适配器失败后，显式枚举所有 VendorId=0x10DE 的适配器逐一重试。
    /// </para>
    /// </remarks>
    public static bool IsAvailable
    {
        get
        {
            if (!IsDllPresent) return false;

            // 快速路径：默认适配器（单 GPU 或默认适配器即 N 卡时直接命中）
            try
            {
                // ⚠ 探测用的 D3D11 设备必须释放：原实现创建后既未 Dispose 也未复用，
                // 而 IsAvailable 在每次后端选择时都可能被调用（Select → GpuCapability），
                // 每次泄漏一个 ID3D11Device 及其关联的非托管资源。
                using var device = D3D11.D3D11CreateDevice(
                    Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                using var nv = new NvEncoderNative(device);
                return true;
            }
            catch (Exception ex)
            {
                CoreLog.Debug("NVENC", $"默认适配器探测失败，将枚举 N 卡: {ex.Message}", LogCategory.Encoding);
            }

            // 回退路径：显式枚举所有 NVIDIA 适配器
            return TryOpenOnNvidiaAdapter();
        }
    }

    /// <summary>显式枚举所有 NVIDIA 适配器，尝试在其上打开 NVENC 会话。任一成功即返回 true。</summary>
    private static bool TryOpenOnNvidiaAdapter()
    {
        using var device = CreateNvidiaDevice();
        if (device is null) return false;
        try
        {
            using var nv = new NvEncoderNative(device);
            CoreLog.Debug("NVENC", "✅ NVIDIA 适配器会话打开成功", LogCategory.Encoding);
            return true;
        }
        catch (Exception ex)
        {
            CoreLog.Warn("NVENC", "NVIDIA 适配器会话打开失败", ex, LogCategory.Encoding);
            return false;
        }
    }

    /// <summary>D3D11_SDK_VERSION。</summary>
    private const uint D3D11_SDK_VERSION = 7;

    /// <summary>NVENC 结构体 version 字段的附加位。
    /// <para>
    /// 即头文件宏 <c>NVENCAPI_STRUCT_VERSION(1) = NVENCAPI_VERSION | (1 &lt;&lt; 16) | (0x7 &lt;&lt; 28)</c>。
    /// NV_ENCODE_API_FUNCTION_LIST.version 与 NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS.version
    /// **都必须**带上这些位，否则驱动返回 NV_ENC_ERR_INVALID_VERSION (15)。
    /// </para>
    /// <para>
    /// 实测（RTX 5080，驱动 570+）：仅 <c>ver</c> 或 <c>ver | (1&lt;&lt;16)</c> 均失败；
    /// <c>ver | (1&lt;&lt;16) | (0x7&lt;&lt;28)</c> 成功（SDK 10.0 ~ 13.2 全部接受）。
    /// </para>
    /// </summary>
    private const uint NvencStructVersionBits = (1u << 16) | (0x7u << 28);

    /// <summary>构造 NVENC 结构体的 version 字段。
    /// <para>
    /// 即 <c>NVENCAPI_STRUCT_VERSION(n) = NVENCAPI_VERSION | (n &lt;&lt; 16) | (0x7 &lt;&lt; 28)</c>，
    /// 部分结构体还需额外 <c>| (1 &lt;&lt; 31)</c>。
    /// </para>
    /// <para>
    /// ⚠ **每个结构体的 n 各不相同**（以 nvEncodeAPI.h 为准，并经 nvoff.c 探针/实机验证）：
    /// <list type="bullet">
    /// <item>NV_ENC_INITIALIZE_PARAMS_VER = STRUCT_VERSION(5) | (1&lt;&lt;31)</item>
    /// <item>NV_ENC_CONFIG_VER            = STRUCT_VERSION(9) | (1&lt;&lt;31)（2026-09-18 勘误：旧注释写 6 与代码不符，代码值经实机工作）</item>
    /// <item>NV_ENC_PIC_PARAMS_VER        = STRUCT_VERSION(7) | (1&lt;&lt;31)</item>
    /// <item>NV_ENC_REGISTER_RESOURCE_VER = STRUCT_VERSION(5)</item>
    /// <item>NV_ENC_MAP_INPUT_RESOURCE_VER= STRUCT_VERSION(4)</item>
    /// <item>NV_ENC_CREATE_INPUT_BUFFER_VER / NV_ENC_LOCK_BITSTREAM_VER = STRUCT_VERSION(2)（2026-09-18 勘误：旧注释写 1）；
    ///       CREATE_BITSTREAM_BUFFER / LOCK_INPUT_BUFFER / OPEN_SESSION_EX_PARAMS = STRUCT_VERSION(1)</item>
    /// </list>
    /// 原实现对所有结构体统一使用 (1&lt;&lt;16)，导致 InitializeEncoder 返回
    /// NV_ENC_ERR_INVALID_VERSION (15)。
    /// </para>
    /// </summary>
    private static uint StructVer(uint apiVersion, int n, bool highBit = false)
        => apiVersion | ((uint)n << 16) | (0x7u << 28) | (highBit ? (1u << 31) : 0u);

    /// <summary>原生 D3D11CreateDevice。
    /// ⚠ Vortice 3.8.3 公开的简化重载不接受 IDXGIAdapter，其 10 参数原生版不可访问，
    /// 故此处直接 P/Invoke。指定适配器时 driverType 必须为 0 (D3D_DRIVER_TYPE_UNKNOWN)。</summary>
    // ⚠ 必须显式 EntryPoint="D3D11CreateDevice"：d3d11.dll 导出的是该名称，
    // 若按 C# 方法名 D3D11CreateDeviceNative 查找会抛 EntryPointNotFoundException。
    [System.Runtime.InteropServices.DllImport("d3d11.dll",
        EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDeviceNative(
        nint pAdapter,
        int driverType,
        nint software,
        uint flags,
        nint pFeatureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out nint ppDevice,
        out int pFeatureLevel,
        out nint ppImmediateContext);

    /// <summary>在**第一个可用的 NVIDIA 适配器**上创建 D3D11 设备。
    /// <para>
    /// 供 NVENC 编码使用：当共享设备（来自 WGC，混合显卡下可能是核显）无法打开
    /// NVENC 会话时，用它创建一块真正 N 卡上的设备。
    /// </para>
    /// <para>
    /// ⚠ Vortice 3.8.3 的 <c>D3D11.D3D11CreateDevice</c> 只有 (DriverType, Flags, ...)
    /// 简化重载和 10 参数原生重载，**没有**接受 IDXGIAdapter 的简化重载，
    /// 因此此处调用原生版本并传入适配器的 NativePointer。
    /// </para>
    /// </summary>
    /// <returns>设备；无 N 卡或创建失败时返回 null。调用方负责 Dispose。</returns>
    public static ID3D11Device? CreateNvidiaDevice()
    {
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter is null) break;
                try
                {
                    if (adapter.Description.VendorId != 0x10DE) continue; // 仅 NVIDIA

                    int hr = D3D11CreateDeviceNative(
                        adapter.NativePointer,
                        0,                                           // D3D_DRIVER_TYPE_UNKNOWN
                        nint.Zero,                                   // Software (HMODULE)
                        (uint)DeviceCreationFlags.BgraSupport,
                        nint.Zero,                                   // pFeatureLevels (null = 默认列表)
                        0,                                           // FeatureLevels = 0 → 用默认
                        D3D11_SDK_VERSION,
                        out var devPtr, out _, out var ctxPtr);

                    if (hr < 0 || devPtr == 0)
                    {
                        CoreLog.Warn("NVENC",
                            $"适配器 {adapter.Description.Description.Trim()} 创建设备失败 hr=0x{hr:X8}", LogCategory.Encoding);
                        continue;
                    }

                    // 立即上下文由 ID3D11Device.ImmediateContext 再次获取，此处释放原始引用
                    if (ctxPtr != 0) Marshal.Release(ctxPtr);

                    CoreLog.Debug("NVENC",
                        $"✅ 在 {adapter.Description.Description.Trim()} 上创建设备", LogCategory.Encoding);
                    return new ID3D11Device(devPtr);
                }
                catch (Exception ex)
                {
                    CoreLog.Warn("NVENC",
                        $"适配器 {adapter.Description.Description.Trim()} 创建设备失败", ex, LogCategory.Encoding);
                }
                finally { adapter.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("NVENC", "适配器枚举失败", ex, LogCategory.Encoding);
        }
        return null;
    }

    /// <summary>探测驱动支持的最高 API 版本。</summary>
    public static uint ProbeApiVersion()
    {
        nint dll = 0;
        try
        {
            dll = NativeLibrary.Load("nvEncodeAPI64.dll");
            var createFn = NativeLibrary.GetExport(dll, "NvEncodeAPICreateInstance");
            var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(createFn);
            var table = Marshal.AllocHGlobal(FuncListSize);
            try
            {
                foreach (uint ver in s_sdkVersions)
                {
                    new Span<byte>((void*)table, FuncListSize).Clear();
                    // 同上：version 字段需完整的 NVENCAPI_STRUCT_VERSION 位
                    // NV_ENCODE_API_FUNCTION_LIST_VER = STRUCT_VERSION(2) (SDK 13.1)
                    *(uint*)table = StructVer(ver, 2);
                    int hr = create(table);
                    if (hr == NVENC_SUCCESS)
                    {
                        CoreLog.Info("NVENC", $"ProbeApiVersion: {(ver & 0xFF)}.{(ver >> 24) & 0xFF}", LogCategory.Encoding);
                        return ver;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch (Exception ex) { CoreLog.Warn("NVENC", "ProbeApiVersion 失败", ex, LogCategory.Encoding); }
        finally { if (dll != 0) NativeLibrary.Free(dll); }
        return 0;
    }

    /// <param name="avifCicp">这份码流要下发/声明的色彩意图（H.273 码点）。
    /// 传 null = 不下发色彩描述（驱动默认 BT.601/full 转换），只适用于给不出自洽声明的场景
    ///（AdobeRGB）。<b>调用方必须把同一个值再传给 <see cref="IvfWriter.WriteAvif"/></b>，
    /// 否则就是"硬件按 A 转、容器声明 B"。判据：纯色带探针会实测两者是否一致。</param>
    public NvEncoderNative(ID3D11Device d3dDevice, FormatHelper.AvifCicp? avifCicp = null)
    {
        _avifCicp = avifCicp;
        // ═══ 2026-09-17 P1 修复：构造失败时释放 _funcTable/_dll ═══
        // 旧代码在 SDK 探测全失败 / OpenEncodeSessionEx 失败时直接抛异常，
        // _funcTable（HGlobal）与 _dll（模块句柄）永久泄漏 —— IsAvailable 每次失败
        // 探测都走一遍这条路径，失败反复发生就反复泄漏。
        try
        {
            Initialize(d3dDevice);
        }
        catch
        {
            if (_funcTable != 0) { Marshal.FreeHGlobal(_funcTable); _funcTable = 0; }
            if (_dll != 0) { NativeLibrary.Free(_dll); _dll = 0; }
            throw;
        }
    }

    /// <summary>本会话下发给驱动的色彩意图；null = 不碰色彩描述字段（见构造函数注释）。</summary>
    private readonly FormatHelper.AvifCicp? _avifCicp;

    private void Initialize(ID3D11Device d3dDevice)
    {
        // 启动时校验缓冲区大小（驱动/平台差异导致结构体大小变化时提前失败，而非静默越界）
        ValidateBufferSizes();

        _d3dDevice = d3dDevice;
        _dll = NativeLibrary.Load("nvEncodeAPI64.dll");
        var diag = (string msg) => { CoreLog.Debug("NVENC", msg, LogCategory.Encoding); Console.WriteLine(msg); };
        diag("DLL 已加载");

        var createFn = NativeLibrary.GetExport(_dll, "NvEncodeAPICreateInstance");
        var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(createFn);

        // ⚠ 旧值 512：驱动会写满整个 NV_ENCODE_API_FUNCTION_LIST（2552 字节）→ 越界 2 KB
        _funcTable = Marshal.AllocHGlobal(FuncListSize);
        bool created = false;
        int lastHr = -1;

        foreach (uint ver in s_sdkVersions)
        {
            new Span<byte>((void*)_funcTable, FuncListSize).Clear();
            // ═══ 2026-08-31 修复: NV_ENCODE_API_FUNCTION_LIST 的 version 字段 ═══
            // 必须填 NV_ENCODE_API_FUNCTION_LIST_VER = NVENCAPI_VERSION | (1 << 16)。
            // 原实现只填了 NVENCAPI_VERSION（缺 1<<16），导致 NvEncodeAPICreateInstance
            // 恒返回 NV_ENC_ERR_INVALID_VERSION (15) —— **在任何系统上都无法初始化**，
            // 与显卡型号、驱动、适配器选择统统无关。
            // 实测：RTX 5080 + RTX 4060 均报 "所有 SDK 版本探测失败 hr=0x0000000F"。
            // NV_ENCODE_API_FUNCTION_LIST_VER = STRUCT_VERSION(2) (SDK 13.1)
            *(uint*)_funcTable = StructVer(ver, 2);
            int hr = create(_funcTable);
            CoreLog.Trace("NVENC", () => $"   SDK {(ver & 0xFF)}.{(ver >> 24) & 0xFF:00}: hr=0x{hr:X8} {(hr == NVENC_SUCCESS ? "✓" : "✗")}", LogCategory.Encoding);
            if (hr == NVENC_SUCCESS)
            {
                _activeVersion = ver;
                created = true;
                diag($"✅ CreateInstance OK (SDK {(ver & 0xFF)}.{(ver >> 24) & 0xFF})");
                break;
            }
            lastHr = hr;
        }

        if (!created)
            throw new InvalidOperationException(
                $"[NVENC] 所有 SDK 版本探测失败 (最后 hr=0x{lastHr:X8})。驱动可能不支持 NVENC。");

        var openFn = Marshal.GetDelegateForFunctionPointer<OpenSessionFn>(GetFuncPtr(FnOpenEncodeSessionEx));
        nint enc = 0;

        // NvEncOpenEncodeSessionEx 需要 NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS 结构体
        // ⚠ 真实 sizeof = 1552 字节（含 reserved1[253] + reserved2[62]），
        //   旧实现 1024 → 1088 仍不足 → 驱动读写越界
        byte* osp = stackalloc byte[OpenSessionExSize];
        new Span<byte>(osp, OpenSessionExSize).Clear();
        // NVENCAPI_STRUCT_VERSION(1) = NVENCAPI_VERSION | (1 << 16) | (0x7 << 28)
        *(uint*)osp = _activeVersion | NvencStructVersionBits;
        *(nint*)(osp + 8) = _d3dDevice.NativePointer;
        *(uint*)(osp + 24) = _activeVersion;

        // ═══ 2026-08-31 修复: deviceType 两种枚举语义并存，运行时依次尝试 ═══
        // 先按实测有效的旧版 DIRECTX(0)，再试新版 DX11(2)。
        int hr2 = -1;
        foreach (uint devType in stackalloc uint[] { NV_ENC_DEVICE_TYPE_DIRECTX, NV_ENC_DEVICE_TYPE_DX11 })
        {
            *(uint*)(osp + 4) = devType;
            enc = 0;
            hr2 = openFn((nint)osp, &enc);
            if (hr2 == NVENC_SUCCESS)
            {
                diag($"✅ 编码会话 OK (AV1, deviceType={devType})");
                break;
            }
        }
        if (hr2 != NVENC_SUCCESS)
            throw new InvalidOperationException($"[NVENC] OpenEncodeSessionEx 失败: 0x{hr2:X8}");

        _encoder = enc;
    }

    // ═══════════ 编码 ═══════════

    public byte[] EncodeAv1(byte[] bgra, int w, int h, int qp)
        => EncodeAv1(bgra, w, h, qp, 8);

    /// <summary>AV1 编码，可指定输出位深（8 或 10）。</summary>
    /// <para><b>8-bit 从来不是硬件上限</b>：AV1 Main profile 本就覆盖 8 与 10-bit，
    /// NVIDIA / Intel 支持 AV1 硬编的卡都支持 10-bit。</para>
    /// <para>🔬 <b>2026-09-17 实测</b>：输出位深由 <c>NV_ENC_CONFIG_AV1.outputBitDepth</c>
    /// 决定（MSVC offsetof 实测：NV_ENC_CONFIG + 280；inputBitDepth = +284）。
    /// 仅改 <c>bufferFormat</c> 为 ARGB10 无效（序列头 bitDepth 仍为 8），必须写本字段。</para>
    /// <para>输入<b>始终</b>是 8-bit BGRA：头文件明确 8-bit→10-bit 由硬件内部转换，
    /// 故无需 A2R10G10B10 输入。若请求 10-bit 但拿不到 preset config，本方法抛异常
    /// （不静默降回 8-bit）。</para>
    public byte[] EncodeAv1(byte[] bgra, int w, int h, int qp, int bitDepth)
        => EncodeRaw(bgra, w, h, qp, CodecAv1, NV_ENC_BUFFER_FORMAT_ARGB, bitDepth);

    public byte[] EncodeHevc(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecHevc, NV_ENC_BUFFER_FORMAT_ARGB);

    /// <summary>
    /// ═══ 2026-09-16: AV1 4:2:2 编码（NV16 半平面 YUV 4:2:2 输入）═══
    /// 仅 Blackwell 及之后 GPU 支持 4:2:2 编码；不支持的卡上 InitializeEncoder /
    /// CreateInputBuffer 会失败并抛出异常 → 调用方（NvencAvifBackend）回退软件编码。
    /// 色度转换在 CPU 侧完成（BGRA → YUV NV16, BT.601, 与 NV12 路径同系数）。
    /// </summary>
    /// <remarks>
    /// ═══ 2026-09-17：⚠ 当前【无调用方 · 不可达 · 保留待启用】═══
    /// 保留它是为了将来 NVENC 真支持 AV1 4:2:2 时可原地启用，但现阶段它是死代码：
    ///   ① 事实层面：AV1 硬件编码器【任何代际】都没有 4:2:2 管线（4:2:2 编码仅
    ///      H.264/HEVC 在 Blackwell 支持）；实测 RTX 5080 以 NV16 送 AV1，
    ///      EncodePicture 返回 0x0C UNSUPPORTED_PARAM ——
    ///      即上方摘要里"仅 Blackwell 及之后支持"这个前提本身已被证伪。
    ///   ② 声明层面：AvifEncoderCapabilities 已把 NVENC 的 Supports422 声明为 false，
    ///      4:2:2 请求会在上游（CapturePipelineService 的能力协商）被降级成 420，
    ///      PickAuto 也不会为满足 422 而选中 NVENC ⇒ 调用方拿到的 chroma 恒非 422。
    /// ⇒ 故 NvencAvifBackend.EncodeAsync 里的 422 分派（共享设备 + N 卡设备两条 CPU 像素路径）
    ///   已于 2026-09-17 移除，本方法暂时无人调用。
    ///
    /// 【启用前必须同时改两处】，只改一处会再次造成"声明与实现打架"：
    ///   ① AvifEncoderCapabilities 中 NVENC 条目的 Supports422 改为 true；
    ///   ② FormatEncoders.NvencAvifBackend.EncodeAsync 的分派
    ///      （含纹理直通路径的 chroma != "422" 守卫）。
    /// </remarks>
    public byte[] EncodeAv1_422(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(BgraToNv16(bgra, w, h), w, h, qp, CodecAv1, NV_ENC_BUFFER_FORMAT_NV16);

    /// <summary>把位深与色彩描述写进 <c>encodeCodecConfig.av1Config</c>（仅 AV1 有效）。</summary>
    /// <para>
    /// nvEncodeAPI.h 原文（outputBitDepth）：
    /// 「HW will do the bitdepth conversion internally from inputBitDepth -&gt; outputBitDepth
    /// if bit depths differ. Support for 8 bit input to 10 bit encode conversion only」
    /// </para>
    /// <para>
    /// 这两句话必须一起读，<b>单读后半句会得出错误结论</b>：
    ///   · <b>输出</b>位深由本字段决定，与 bufferFormat 无关 —— 2026-09-16 只改
    ///     bufferFormat 为 ARGB10 却仍拿到 8-bit，正是因为它没动 outputBitDepth
    ///     （当时被误记成"ARGB10 无效"并写进了文档）。
    ///   · "Support for 8 bit input to 10 bit encode conversion only" 限制的是
    ///     <b>转换</b>方向（只支持 8→10，不支持 10→8），<b>不是</b>禁止 10-bit 输入。
    ///     2026-09-17 运行时实测：本机驱动对 AV1 报告的输入格式含
    ///     <c>ARGB10 / ABGR10 / YUV420_10BIT</c>，且 <c>NV_ENC_CAPS_SUPPORT_10BIT_ENCODE=1</c>
    ///     ⇒ input=10 / output=10 的原生 10-bit 通路是存在的（无需转换）。
    /// </para>
    /// <para>
    /// <paramref name="cicp"/> 非 null 时四个色彩字段一起下发：驱动会<b>照着做</b> RGB→YUV
    /// （2026-09-20 码点扫描实测，见 <see cref="FormatHelper.AvifCicp"/>）。
    /// 为 null 时只写摆幅、不碰矩阵 —— 即"给不出自洽声明就不猜"的 AdobeRGB 分支。
    /// </para>
    private static unsafe void ApplyAv1ColorConfig(byte* cfg, Guid codec, int outputBitDepth,
                                                   uint bufferFormat, FormatHelper.AvifCicp? cicp)
    {
        if (codec != CodecAv1) return;
        bool in10 = bufferFormat == NV_ENC_BUFFER_FORMAT_ARGB10
                 || bufferFormat == NV_ENC_BUFFER_FORMAT_ABGR10;
        *(uint*)(cfg + Av1OutputBitDepth) = outputBitDepth >= 10 ? NvEncBitDepth10 : NvEncBitDepth8;
        *(uint*)(cfg + Av1InputBitDepth) = in10 ? NvEncBitDepth10 : NvEncBitDepth8;
        // 摆幅：见 NvEncWantsFullRange（要 full，不靠把软件路降级来"统一"）。
        // 只写这一个字段就能验证驱动是否真按 full swing 输出——纯色带拟合会直接判出来。
        // 容器 colr 的 full_range 字节同样取自 NvEncWantsFullRange（经 AvifCicp.FullRange），
        // 两处同源 ⇒ 不可能"容器说一套、硬件做一套"。
        *(uint*)(cfg + Av1ColorRange) = NvEncWantsFullRange ? 1u : 0u;
        if (cicp is { } c)
        {
            *(uint*)(cfg + Av1ColorPrimaries) = c.Primaries;
            *(uint*)(cfg + Av1TransferCharacteristics) = c.Transfer;
            *(uint*)(cfg + Av1MatrixCoefficients) = ProbeMatrixCoefficientsOverride ?? c.Matrix;
        }
    }

    /// <summary>以 <b>真 10-bit</b> 输入（A2R10G10B10 打包）编码 AV1。</summary>
    /// <remarks>
    /// 与 <see cref="EncodeAv1(byte[], int, int, int, int)"/> 的区别：后者输入恒为 8-bit，
    /// 10-bit 输出是硬件升位深（源精度只有 8-bit）；本方法把 10-bit 像素原样送入，
    /// 源精度真正保留到 10-bit。输入布局：每像素 4 字节，B 占最低 10 位、G 次之、R 再次、A 占最高 2 位。
    /// </remarks>
    public byte[] EncodeAv1Argb10(byte[] argb10, int w, int h, int qp)
        => EncodeRaw(argb10, w, h, qp, CodecAv1, NV_ENC_BUFFER_FORMAT_ARGB10, 10);

    private byte[] EncodeRaw(byte[] pixels, int w, int h, int qp, Guid codec, uint bufferFormat,
                            int outputBitDepth = 8)
    {
        // ═══ 2026-09-17 P1 修复：与 Dispose 互斥（见 _gate 注释）═══
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EncodeRawCore(pixels, w, h, qp, codec, bufferFormat, outputBitDepth);
        }
    }

    private byte[] EncodeRawCore(byte[] pixels, int w, int h, int qp, Guid codec, uint bufferFormat,
                            int outputBitDepth = 8)
    {
        // CRF 0 = AV1 的 QP0 = 向硬件请求 lossless，而 NVENC 没有这个模式
        // （本机实测 InitEncoder hr=0x00000008 "Lossless Coding mode not supported"）。
        // UI 已按 AvifEncoderCapabilities.MinCrf 把滑块下限抬到 1；这里兜的是【绕过 UI 的那条路】：
        // settings.json 里遗留的 0、或 Auto 在设值之后才解析成硬件 —— 不兜就白吃一次
        // 驱动失败再退软件，且那次失败还曾被缓存成"整卡不可用"（见同表的 remarks）。
        qp = Math.Max(qp, AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Nvenc));

        var initFn = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(GetFuncPtr(FnInitializeEncoder));
        var createBufFn = Marshal.GetDelegateForFunctionPointer<CreateBitstreamFn>(GetFuncPtr(FnCreateBitstreamBuffer));
        var encFn = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(GetFuncPtr(FnEncodePicture));
        var lockFn = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(GetFuncPtr(FnLockBitstream));
        var unlockFn = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(GetFuncPtr(FnUnlockBitstream));

        nint initBuf = Marshal.AllocHGlobal(InitParamsSize);
        nint cfgBuf = Marshal.AllocHGlobal(EncConfigSize);
        int hr = -1;   // 初始化结果；后续步骤复用该变量
        try
        {
            unsafe
            {
                new Span<byte>((void*)initBuf, InitParamsSize).Clear();
                new Span<byte>((void*)cfgBuf, EncConfigSize).Clear();

                // ═══ 2026-09-13 重写: 移除 GetEncodePresetConfig 依赖 ═══
                // 官方 nvEncodeAPI.h (SDK 13.1) 明确:
                //   1) NvEncGetEncodePresetConfig "is not supported starting from
                //      driver version 590 (will return NV_ENC_ERR_UNSUPPORTED_PARAM)"
                //   2) "NvEncGetEncodePresetConfig() API is not applicable to AV1"
                // → 该 API 在目标环境 (RTX 5080 / 驱动 616.56) 必然失败, 旧代码在这里
                //   反复尝试后以"空壳 config"初始化 → InitializeEncoder 必败。
                // 正确路径: 见下方主/备两路径。

                // ── 1. preset: 驱动枚举, 官方 P1 优先 ──
                var preset = PickPresetGuid(codec);

                // ── 2. 构造 NV_ENC_INITIALIZE_PARAMS (13.1: VER(7)|1<<31) ──
                // ═══ Windows GUID 是 4 字节对齐 (Data1=uint32_t), 非 8 字节 ═══
                //    +0 version | +4 encodeGUID | +20 presetGUID | +36 encodeWidth
                //    +40 encodeHeight | +44 darWidth | +48 darHeight | +52 frameRateNum
                //    +56 frameRateDen | +60 enableEncodeAsync | +64 enablePTD
                //    +88 encodeConfig (nint) | +136 tuningInfo | +140 bufferFormat
                byte* p = (byte*)initBuf;
                new Span<byte>((void*)initBuf, InitParamsSize).Clear();
                *(uint*)(p + 0) = StructVer(_activeVersion, 7, highBit: true);
                Buffer.MemoryCopy(&codec, p + 4, 16, 16);      // encodeGUID  @ 4
                Buffer.MemoryCopy(&preset, p + 20, 16, 16);    // presetGUID  @ 20
                *(uint*)(p + 36) = (uint)w;                    // encodeWidth @ 36
                *(uint*)(p + 40) = (uint)h;                    // encodeHeight@ 40
                *(uint*)(p + 44) = (uint)w;                    // darWidth    @ 44
                *(uint*)(p + 48) = (uint)h;                    // darHeight   @ 48
                *(uint*)(p + 52) = 30;                         // frameRateNum@ 52 (30fps)
                *(uint*)(p + 56) = 1;                          // frameRateDen@ 56
                *(uint*)(p + 64) = 1;                          // enablePTD   @ 64
                *(uint*)(p + 136) = NV_ENC_TUNING_INFO_HIGH_QUALITY;  // tuningInfo @136 (必须非 0, 与 GetEncodePresetConfigEx 一致)
                *(uint*)(p + 140) = bufferFormat;                     // bufferFormat @140 (匹配输入像素格式)

                // ═══ 2026-09-13 主路径: GetEncodePresetConfigEx 获取 AV1 preset 配置 ═══
                // FFmpeg nvenc.c 做法: 用 Ex API (带 tuningInfo=HIGH_QUALITY) 取完整
                // NV_ENC_CONFIG (含 av1Config 联合体), memcpy 后传 encodeConfig。
                // 注意: AV1 不能只用纯 preset (encodeConfig=NULL), 驱动要求显式 config。
                int hrPreset = TryGetPresetConfig(codec, preset, cfgBuf);
                Console.WriteLine($"[NVENC] GetEncodePresetConfigEx hr=0x{hrPreset:X8} (preset={preset})");
                if (hrPreset == NVENC_SUCCESS)
                {
                    // ═══ 直接使用驱动返回的完整 preset 配置, 仅修正 GOP 与 QP ═══
                    // NV_ENC_CONFIG (Windows GUID 4 字节对齐):
                    //   version@0 | profileGUID@4(16B) | gopLength@24 | frameIntervalP@28 | rcParams@44
                    // (偏移与纹理路径兜底分支 1110-1115 一致; 旧 20/24 会踩进 profileGUID)
                    byte* cfg = (byte*)cfgBuf;
                    // ═══ 2026-09-17 修正：旧偏移整体后移 4 字节 ═══
                    // 实测（MSVC offsetof，SDK 13.1）：gopLength@20、frameIntervalP@24、
                    // rcParams@40 → rateControlMode@44、constQP@48（qpInterP/B/Intra +0/+4/+8）。
                    // 旧代码写 24/28/48 → 实际写进了 frameIntervalP/monoChrome/qpInterP，
                    // 且把 qp 写进了 averageBitRate(@60) ⇒ QP 从未生效。
                    *(uint*)(cfg + CfgGopLength) = 1;        // gopLength = 1 (单帧全 I)
                    *(int*)(cfg + CfgFrameIntervalP) = 0;    // frameIntervalP = 0 (I only)
                    *(uint*)(cfg + RcRateControlMode) = NV_ENC_PARAMS_RC_CONSTQP;
                    *(uint*)(cfg + RcConstQp + 0) = (uint)qp;   // constQP.qpInterP
                    *(uint*)(cfg + RcConstQp + 4) = (uint)qp;   // constQP.qpInterB
                    *(uint*)(cfg + RcConstQp + 8) = (uint)qp;   // constQP.qpIntra
                    ApplyAv1ColorConfig(cfg, codec, outputBitDepth, bufferFormat, _avifCicp);
                    *(nint*)(p + 88) = cfgBuf;       // encodeConfig @88
                    CoreLog.Trace("NVENC", () => $"使用 preset 配置 (修正 GOP + CONSTQP qp={qp})", LogCategory.Encoding);
                }
                else
                {
                    // ═══ 10-bit 不能靠 preset 兜底 ═══
                    // encodeConfig=NULL 时驱动用 preset 默认配置，outputBitDepth 恒为 8。
                    // 此处若静默继续，就是"用户要 10-bit、实际给 8-bit"——正是本次重构要消除的
                    // 静默参数变更。故显式失败，由调用方回退软件编码器。
                    if (codec == CodecAv1 && outputBitDepth >= 10)
                        throw new InvalidOperationException(
                            "[NVENC] 请求 10-bit 输出，但 GetEncodePresetConfigEx 失败，无法设置 " +
                            "av1Config.outputBitDepth；不回退 8-bit，交由调用方降级到软件编码。");
                    *(nint*)(p + 88) = 0;   // encodeConfig = NULL (兜底纯 preset)
                    CoreLog.Warn("NVENC", "GetEncodePresetConfigEx 失败, 回退纯 preset", LogCategory.Encoding);
                }

                hr = initFn(_encoder, initBuf);
                // lastErr 只在【失败】时才读：驱动成功返回后 GetLastError() 的内容不受保证，
                // 无条件取字符串会把缓冲区里的残留字节当文本读出来（实测成功分支日志里
                // 出现 lastErr=OT<乱码>）。失败路径仍必须带上它 —— 那是远程诊断 hr 码的唯一线索。
                string? errDesc = hr == NVENC_SUCCESS ? null : GetLastErrorString();
                string errSuffix = errDesc is null ? "" : $" lastErr={errDesc}";
                AppendDiag($"[EncodeRaw] InitEncoder hr=0x{hr:X8} (presetCfg=0x{hrPreset:X8}){errSuffix}");
                Console.WriteLine($"[NVENC] InitEncoder hr=0x{hr:X8}{errSuffix}");
                CoreLog.Trace("NVENC", () => $"InitEncoder hr=0x{hr:X8}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS)
                    throw new InvalidOperationException(
                        $"[NVENC] InitEncoder: 0x{hr:X8} (presetCfg=0x{hrPreset:X8}; {w}x{h}; sdk=0x{_activeVersion:X8}; lastErr={errDesc})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initBuf);
            Marshal.FreeHGlobal(cfgBuf);
        }

        int bufSize = w * h * 4;
        // ═══ 结构体缓冲区：一律用 MSVC 实测的 sizeof（见文件顶部常量）═══
        // 旧值为估算值，其中 NV_ENC_PIC_PARAMS 2400 < 真实 3360 → 驱动写入越界 960 字节。
        nint ib = Marshal.AllocHGlobal(CreateInputBufferSize);
        nint lb = Marshal.AllocHGlobal(LockInputBufferSize);
        nint bb = Marshal.AllocHGlobal(CreateBitstreamSize);
        nint pic = Marshal.AllocHGlobal(PicParamsSize);
        nint lk = Marshal.AllocHGlobal(LockBitstreamSize);
        // ═══ 2026-09-17 P0 修复：驱动侧资源句柄提出来，finally 里销毁 ═══
        // 旧代码 CreateInputBuffer/CreateBitstreamBuffer 后从不销毁（函数表索引 13/15
        // 甚至未定义）⇒ 每帧泄漏 VRAM/驱动句柄直至创建失败。
        nint inputBuf = 0;
        nint bsBuf = 0;
        bool inputLocked = false;
        try
        {
            unsafe
            {
                new Span<byte>((void*)ib, CreateInputBufferSize).Clear();
                *(uint*)ib = StructVer(_activeVersion, 2);              // NV_ENC_CREATE_INPUT_BUFFER_VER = STRUCT_VERSION(2)
                *(uint*)(ib + 4) = (uint)w;                             // width
                *(uint*)(ib + 8) = (uint)h;                             // height
                *(uint*)(ib + 16) = bufferFormat;                      // bufferFmt (匹配输入像素格式)
                hr = GetFuncDelegate<CreateInputBufferFn>(GetFuncPtr(FnCreateInputBuffer))(_encoder, ib);
                inputBuf = *(nint*)(ib + 24);                      // inputBuffer @24 (out)
                AppendDiag($"[EncodeRaw] CreateInputBuffer hr=0x{hr:X8} buf=0x{inputBuf:X}");
                Console.WriteLine($"[NVENC] CreateInputBuffer hr=0x{hr:X8} buf=0x{inputBuf:X}");
                CoreLog.Trace("NVENC", () => $"CreateInputBuffer hr=0x{hr:X8} buf=0x{inputBuf:X}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateInputBuf: 0x{hr:X8}");

                new Span<byte>((void*)lb, LockInputBufferSize).Clear();
                *(uint*)lb = StructVer(_activeVersion, 1);              // NV_ENC_LOCK_INPUT_BUFFER_VER
                *(nint*)(lb + 8) = inputBuf;                            // inputBuffer
                hr = GetFuncDelegate<LockInputBufferFn>(GetFuncPtr(FnLockInputBuffer))(_encoder, lb);
                AppendDiag($"[EncodeRaw] LockInputBuffer hr=0x{hr:X8} ptr=0x{*(nint*)(lb + 16):X} pitch={*(int*)(lb + 24)}");
                Console.WriteLine($"[NVENC] LockInputBuffer hr=0x{hr:X8}");
                CoreLog.Trace("NVENC", () => $"LockInputBuffer hr=0x{hr:X8}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockInput: 0x{hr:X8}");
                inputLocked = true;
                nint srcData = *(nint*)(lb + 16);                       // bufferDataPtr
                int srcPitch = *(int*)(lb + 24);                        // pitch
                // ARGB(8-bit) 与 ARGB10(10-bit) 都是每像素 4 字节 ⇒ 拷贝逻辑完全一致。
                // ⚠ ARGB10 必须落在这个分支：漏掉它会掉进下面的 NV16 分支，
                // 按 Y/UV 平面解释打包像素，产出花屏且不报错。
                if (bufferFormat == NV_ENC_BUFFER_FORMAT_ARGB
                    || bufferFormat == NV_ENC_BUFFER_FORMAT_ARGB10)
                {
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(pixels, y * w * 4, srcData + y * srcPitch, w * 4);
                }
                else // NV16 半平面 YUV 4:2:2：Y 平面 h 行（行宽 w）+ 交错 UV 平面 h 行（行宽 w，色度水平减半）
                {
                    System.Diagnostics.Debug.Assert(bufferFormat == NV_ENC_BUFFER_FORMAT_NV16, $"[NVENC] EncodeRaw 未预期的输入格式: 0x{bufferFormat:X8}");
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(pixels, y * w, srcData + y * srcPitch, w);                    // Y
                    nint uvBase = srcData + srcPitch * h;
                    for (int y = 0; y < h; y++)
                        Marshal.Copy(pixels, w * h + y * w, uvBase + y * srcPitch, w);             // UV (w/2 组 U+V)
                }
                GetFuncDelegate<UnlockInputBufferFn>(GetFuncPtr(FnUnlockInputBuffer))(_encoder, inputBuf);

                new Span<byte>((void*)bb, CreateBitstreamSize).Clear();
                *(uint*)bb = StructVer(_activeVersion, 1);              // NV_ENC_CREATE_BITSTREAM_BUFFER_VER
                *(uint*)(bb + 4) = (uint)(bufSize * 2);                 // size (bytes) - REQUIRED!
                hr = GetFuncDelegate<CreateBitstreamFn>(GetFuncPtr(FnCreateBitstreamBuffer))(_encoder, bb);
                // NV_ENC_CREATE_BITSTREAM_BUFFER (4 字节对齐): version@0,size@4,memoryHeap@8,reserved@12,bitstreamBuffer@16
                bsBuf = *(nint*)(bb + 16);                         // bitstreamBuffer @16 (out)
                AppendDiag($"[EncodeRaw] CreateBitstreamBuffer hr=0x{hr:X8} buf=0x{bsBuf:X}");
                CoreLog.Trace("NVENC", () => $"CreateBitstreamBuffer hr=0x{hr:X8} buf=0x{bsBuf:X}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}");

                new Span<byte>((void*)pic, PicParamsSize).Clear();
                *(uint*)pic = StructVer(_activeVersion, 7, highBit: true);   // NV_ENC_PIC_PARAMS_VER = STRUCT_VERSION(7)|1<<31
                *(uint*)(pic + 4) = (uint)w; *(uint*)(pic + 8) = (uint)h;
                *(uint*)(pic + 12) = (uint)srcPitch;
                *(uint*)(pic + 16) = NV_ENC_PIC_FLAG_FORCEINTRA;   // ═══ 2026-09-14: 移除 EOS (EOS 是 flush 专用, 单帧不该带) ═══
                *(nint*)(pic + 40) = inputBuf;
                *(nint*)(pic + 48) = bsBuf;
                *(uint*)(pic + 64) = bufferFormat;   // bufferFmt @64 (匹配输入像素格式)
                *(uint*)(pic + 68) = 1;                              // pictureStruct = FRAME (0x01)
                hr = encFn(_encoder, pic);
                CoreLog.Trace("NVENC", () => $"EncodePicture hr=0x{hr:X8}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] EncodePicture: 0x{hr:X8}");

                // ═══ ptD=1 时 EncodePicture 可能返回 NEED_MORE_INPUT, 需再送 flush 帧 ═══
                // 单帧: 若 enablePTD=1 且返回 NEED_MORE_INPUT, 需再调一次空 EncodePicture
                // 但这里 FORCEINTRA 保证输出, 直接尝试 Lock

                new Span<byte>((void*)lk, LockBitstreamSize).Clear();
                // ═══ 2026-09-13 修正: NV_ENC_LOCK_BITSTREAM_VER = STRUCT_VERSION(2) 无高位置位 ═══
                *(uint*)lk = StructVer(_activeVersion, 2);   // NV_ENC_LOCK_BITSTREAM_VER
                *(uint*)(lk + 4) = 0;   // doNotWait=0 (同步阻塞)
                *(nint*)(lk + 8) = bsBuf;   // outputBitstream @8
                AppendDiag($"[EncodeRaw] LockBitstream pre-hr bsBuf=0x{bsBuf:X} lkVer=0x{*(uint*)lk:X} out=0x{*(nint*)(lk + 8):X}");
                hr = lockFn(_encoder, lk);
                CoreLog.Trace("NVENC", () => $"LockBitstream hr=0x{hr:X8}", LogCategory.Encoding);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockBitstream: 0x{hr:X8}");
                // NV_ENC_LOCK_BITSTREAM (4 字节 GUID 对齐):
                //   version@0, bitfields@4, outputBitstream@8, sliceOffsets@16,
                //   frameIdx@24, hwEncodeStatus@28, numSlices@32, bitstreamSizeInBytes@36,
                //   outputTimeStamp@40(8), outputDuration@48(8), bitstreamBufferPtr@56
                int bsSize = *(int*)(lk + 36);       // bitstreamSizeInBytes @36
                nint bsData = *(nint*)(lk + 56);     // bitstreamBufferPtr @56
                // ═══ 2026-09-17 P0 修复：驱动返回的 bsSize 先校验再用 ═══
                // 负值 → new byte[] 抛 OverflowException 且跳过 unlock；超界 → 越界读。
                if (bsSize < 0 || bsSize > bufSize * 4)
                    throw new InvalidOperationException($"[NVENC] LockBitstream 返回异常 bsSize={bsSize} (buf={bufSize})");
                var result = new byte[bsSize];
                Marshal.Copy(bsData, result, 0, bsSize);
                unlockFn(_encoder, lk);

                return result;
            }
        }
        finally
        {
            // ═══ 2026-09-17 P0/P1 修复：异常路径也要解锁 + 驱动资源必须销毁 ═══
            // 顺序与创建相反：unlock → destroy bitstream → destroy input。
            // 销毁失败仅记日志，不吞主异常。
            if (inputLocked)
            {
                try { GetFuncDelegate<UnlockInputBufferFn>(GetFuncPtr(FnUnlockInputBuffer))(_encoder, inputBuf); }
                catch (Exception ex) { AppendDiag($"[EncodeRaw] UnlockInputBuffer(清理) 异常: {ex.Message}"); }
            }
            if (bsBuf != 0)
            {
                try
                {
                    int dhr = GetFuncDelegate<DestroyBitstreamBufferFn>(GetFuncPtr(FnDestroyBitstreamBuffer))(_encoder, bsBuf);
                    if (dhr != NVENC_SUCCESS) AppendDiag($"[EncodeRaw] DestroyBitstreamBuffer hr=0x{dhr:X8}");
                }
                catch (Exception ex) { AppendDiag($"[EncodeRaw] DestroyBitstreamBuffer 异常: {ex.Message}"); }
            }
            if (inputBuf != 0)
            {
                try
                {
                    int dhr = GetFuncDelegate<DestroyInputBufferFn>(GetFuncPtr(FnDestroyInputBuffer))(_encoder, inputBuf);
                    if (dhr != NVENC_SUCCESS) AppendDiag($"[EncodeRaw] DestroyInputBuffer hr=0x{dhr:X8}");
                }
                catch (Exception ex) { AppendDiag($"[EncodeRaw] DestroyInputBuffer 异常: {ex.Message}"); }
            }
            Marshal.FreeHGlobal(ib);
            Marshal.FreeHGlobal(lb);
            Marshal.FreeHGlobal(bb);
            Marshal.FreeHGlobal(pic);
            Marshal.FreeHGlobal(lk);
        }
    }

    // ═══════════ BGRA → NV16 (8-bit 半平面 YUV 4:2:2) ═══════════
    // ═══ 2026-09-17：本转换器目前【只为 EncodeAv1_422 服务】，无其它调用方 ═══
    // 因 EncodeAv1_422 当前不可达（详见其 XML 注释），本方法同属"保留但未被使用"状态。
    // 它的唯一调用方就是 EncodeAv1_422 —— 请勿单独删除其一：
    // 删了本方法会让 EncodeAv1_422 编译失败；删了 EncodeAv1_422 则本方法成为未使用私有成员。
    // 要删请两个一起删。

    /// <summary>BGRA8 → NV16：Y 平面 (w×h) + 交错 UV 平面 (h 行 × w 字节，w/2 组 U+V)。
    /// ⚠ 4:2:2 = 色度水平减半、垂直全分辨率 ⇒ UV 行宽是 w（不是 2w），总量 2*w*h。
    /// 系数与 MftEncoderNative.BgraToNv12 相同（BT.601 limited range）。</summary>
    private static byte[] BgraToNv16(byte[] bgra, int w, int h)
    {
        var nv16 = new byte[w * h * 2];   // Y (w*h) + interleaved UV (w*h)
        int uvOff = w * h;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            int uvRow = uvOff + row;
            for (int x = 0; x < w; x += 2)   // 色度水平减半：每 2 像素一组 U/V
            {
                int idx = (row + x) * 4;
                byte b = bgra[idx], g = bgra[idx + 1], r = bgra[idx + 2];
                byte u = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);
                byte v = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);
                nv16[uvRow + x] = u;         // U 覆盖 x 与 x+1
                // ═══ 2026-09-16 修复（审查 P1-2）：奇数宽度时最后一组无右邻像素 ═══
                // x+1 >= w 时写 V 会跨行污染下一行（或末行直接越界抛异常被 Guard 吞掉）。
                // 截图管线通常偶数宽，但 DPI 缩放/裁剪路径不保证 —— 防御性收尾。
                if (x + 1 < w)
                    nv16[uvRow + x + 1] = v;
            }
            for (int x = 0; x < w; x++)
            {
                int idx = (row + x) * 4;
                byte b = bgra[idx], g = bgra[idx + 1], r = bgra[idx + 2];
                nv16[row + x] = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128) >> 8, 0, 255);      // Y
            }
        }
        return nv16;
    }

    // ═══ 2026-09-17: 原 BgraToArgb10 已删除 ═══
    // 它产出的 A2R10G10B10 确实"没必要"—— 但理由与最初记录的不同，务必按修正后的理解读：
    //   ✗ 旧（错误）："ARGB10 输入无效，硬件只认 8-bit"
    //   ✓ 新（实测）：输出位深由 outputBitDepth 决定。8-bit 输入 + outputBitDepth=10
    //     就够了，所以【若源本身只有 8-bit 精度】，专门转成 ARGB10 是白费一次全图拷贝 ——
    //     这一点没错，故删除是合理的。
    //     但【若源真有 10-bit 精度】（如 Float16 管线），ARGB10 输入是**有效且必需**的：
    //     实测（阶梯判据）ARGB10 输入产出真 10-bit 斜坡，8-bit 输入只能产出 4 级台阶。
    //     新增的 EncodeAv1Argb10 就是给这种情况用的。见
    //     docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md §3

    /// <summary>枚举驱动针对指定 codec 支持的 profile GUID，返回第一个。
    /// <para>
    /// NV_ENC_CONFIG.profileGUID 是 InitializeEncoder 的必填项。若为空，
    /// 驱动返回 UNSUPPORTED_PARAM(0xC) 或 NO_ENCODE_CAPABILITY(0x8)。
    /// 与其硬编码（AV1 的 profile GUID 值难以查证），不如直接问驱动。
    /// </para>
    /// </summary>
    /// <returns>profile GUID；枚举失败返回 Guid.Empty。</returns>
    private unsafe Guid PickProfileGuid(Guid codec)
    {
        if (_encoder == 0) return Guid.Empty;
        try
        {
            var countFn = GetFuncDelegate<GetProfileGuidCountFn>(GetFuncPtr(FnGetEncodeProfileGuidCount));
            int hr = countFn(_encoder, codec, out uint count);
            if (hr != NVENC_SUCCESS || count == 0 || count > 64) return Guid.Empty;

            var guidsFn = GetFuncDelegate<GetProfileGuidsFn>(GetFuncPtr(FnGetEncodeProfileGuids));
            int bytes = (int)count * 16;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hr = guidsFn(_encoder, codec, buf, count, out uint actual);
                if (hr != NVENC_SUCCESS || actual == 0) return Guid.Empty;
                return new Guid(new ReadOnlySpan<byte>((void*)buf, 16));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("NVENC", "profile 枚举异常", ex, LogCategory.Encoding);
        }
        return Guid.Empty;
    }

    /// <summary>枚举驱动针对指定 codec 支持的 preset GUID（诊断用）。</summary>
    public unsafe List<Guid> EnumeratePresetGuids(Guid codec)
    {
        var list = new List<Guid>();
        if (_encoder == 0) return list;
        try
        {
            var countFn = GetFuncDelegate<GetEncodePresetGuidCountFn>(GetFuncPtr(FnGetEncodePresetGuidCount));
            int hr = countFn(_encoder, codec, out uint count);
            if (hr != NVENC_SUCCESS || count == 0 || count > 64) return list;

            var guidsFn = GetFuncDelegate<GetEncodePresetGuidsFn>(GetFuncPtr(FnGetEncodePresetGuids));
            int bytes = (int)count * 16;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hr = guidsFn(_encoder, codec, buf, count, out uint actual);
                if (hr != NVENC_SUCCESS) return list;
                for (uint i = 0; i < actual && i < count; i++)
                    list.Add(new Guid(new ReadOnlySpan<byte>((void*)(buf + (int)i * 16), 16)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { }
        return list;
    }

    // ═══ 2026-09-17：10-bit 能力运行时查询 ═══
    // 能力表里 NVENC 的 MaxBitDepth 此前是硬编码的（8，现为 10），属于"凭文档推断"。
    // 驱动才是真相来源：NvEncGetInputFormats 直接列出该 codec 支持的输入格式，
    // NvEncGetEncodeCaps(NV_ENC_CAPS_SUPPORT_10BIT_ENCODE=39) 直接回答"能否 10-bit 编码"。
    // 函数表索引由 MSVC offsetof 实测（scripts/probes/nvoff.c）：
    //   GetInputFormatCount=5(off 48) / GetInputFormats=6(off 56) / GetEncodeCaps=7(off 64)
    private const int FnGetInputFormatCount = 5;
    private const int FnGetInputFormats = 6;
    private const int FnGetEncodeCaps = 7;
    private const uint NvEncCapsSupport10BitEncode = 39;   // NV_ENC_CAPS_SUPPORT_10BIT_ENCODE

    private delegate int GetInputFormatCountFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid, out uint count);
    private delegate int GetInputFormatsFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        nint formats, uint arraySize, out uint count);
    // NvEncGetEncodeCaps(void* encoder, GUID encodeGUID, NV_ENC_CAPS_PARAM* capsParam, int* capsVal)
    // ⚠ capsParam 是【指向结构体的指针】，不是按值传的枚举 —— 传错会直接 AV。
    // NV_ENC_CAPS_PARAM: version(0) + capsToQuery(4) + reserved[62](8) = 256 字节
    private delegate int GetEncodeCapsFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        nint capsParam, out int value);

    /// <summary>查询驱动针对指定 codec 支持的输入缓冲格式（NV_ENC_BUFFER_FORMAT 值列表）。</summary>
    /// <remarks>这是"硬件能否吃进 10-bit"的权威答案，取代按文档推断。</remarks>
    public unsafe uint[] QueryInputFormats(Guid codec)
    {
        if (_encoder == 0) return [];
        try
        {
            var countFn = GetFuncDelegate<GetInputFormatCountFn>(GetFuncPtr(FnGetInputFormatCount));
            int hr = countFn(_encoder, codec, out uint count);
            if (hr != 0 || count == 0 || count > 128) return [];

            var fmtsFn = GetFuncDelegate<GetInputFormatsFn>(GetFuncPtr(FnGetInputFormats));
            int bytes = (int)count * 4;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hr = fmtsFn(_encoder, codec, buf, count, out uint actual);
                if (hr != 0) return [];
                var result = new uint[actual];
                for (uint i = 0; i < actual; i++) result[i] = *(uint*)(buf + (int)i * 4);
                return result;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch { return []; }
    }

    /// <summary>查询单项编码能力（NV_ENC_CAPS_*）。失败返回 -1。</summary>
    public unsafe int QueryEncodeCap(Guid codec, uint cap)
    {
        if (_encoder == 0) return -1;
        const int CapsParamSize = 256;                 // NV_ENC_CAPS_PARAM
        const uint NvEncCapsParamVer = 0x7101000D;     // NV_ENC_CAPS_PARAM_VER（MSVC 实测，见 scripts/probes/nvoff.c）
        nint p = Marshal.AllocHGlobal(CapsParamSize);
        try
        {
            new Span<byte>((void*)p, CapsParamSize).Clear();
            *(uint*)(p + 0) = NvEncCapsParamVer;       // version
            *(uint*)(p + 4) = cap;                     // capsToQuery
            var fn = GetFuncDelegate<GetEncodeCapsFn>(GetFuncPtr(FnGetEncodeCaps));
            int hr = fn(_encoder, codec, p, out int v);
            return hr != 0 ? -1 : v;
        }
        catch { return -1; }
        finally { Marshal.FreeHGlobal(p); }
    }

    /// <summary>驱动是否支持该 codec 的 10-bit 编码。</summary>
    public bool Supports10BitEncode(Guid codec)
        => QueryEncodeCap(codec, NvEncCapsSupport10BitEncode) == 1;

    /// <summary>选择驱动实际支持的 preset GUID。
    /// <para>
    /// ═══ 2026-09-13 重写: 不再调用 GetEncodePresetConfig ═══
    /// 官方 nvEncodeAPI.h (SDK 13.1):
    ///   · NvEncGetEncodePresetConfig 自驱动 590 起废弃 (返回 UNSUPPORTED_PARAM)
    ///   · 该 API 不适用于 AV1
    /// 编码器参数改由调用方显式构造 NV_ENC_CONFIG, preset 仅作为驱动内部参考。
    /// 优先返回官方 NV_ENC_PRESET_P1_GUID (性能优先, 适合静态图), 否则取驱动枚举的第一个。
    /// </para>
    /// </summary>
    /// <param name="codec">编码器 codec GUID。</param>
    /// <returns>选中的 preset GUID。</returns>
    private unsafe Guid PickPresetGuid(Guid codec)
    {
        try
        {
            var countFn = GetFuncDelegate<GetEncodePresetGuidCountFn>(GetFuncPtr(FnGetEncodePresetGuidCount));
            int hr = countFn(_encoder, codec, out uint count);
            if (hr != NVENC_SUCCESS || count == 0 || count > 64) return PresetP1;

            var guidsFn = GetFuncDelegate<GetEncodePresetGuidsFn>(GetFuncPtr(FnGetEncodePresetGuids));
            int bytes = (int)count * 16;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hr = guidsFn(_encoder, codec, buf, count, out uint actual);
                if (hr != NVENC_SUCCESS || actual == 0) return PresetP1;

                for (uint i = 0; i < actual && i < count; i++)
                {
                    var pg = new Guid(new ReadOnlySpan<byte>((void*)(buf + (int)i * 16), 16));
                    if (pg == PresetP1) return pg;   // 官方 P1 优先
                }
                return new Guid(new ReadOnlySpan<byte>((void*)buf, 16)); // 取第一个
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("NVENC", "preset 枚举异常", ex, LogCategory.Encoding);
        }
        return PresetP1;
    }

    /// <summary>用 GetEncodePresetConfigEx (带 tuningInfo) 获取 preset 的 AV1 默认配置。
    /// <para>
    /// 官方 nvEncodeAPI.h 声明 GEncodePresetConfig 自驱动 590 起废弃且不适用 AV1,
    /// 但 GetEncodePresetConfigEx (索引 39) 有效 (FFmpeg nvenc.c 对 AV1 使用此 API)。
    /// </para>
    /// <param name="codec">codec GUID。</param>
    /// <param name="preset">preset GUID。</param>
    /// <param name="cfgBuf">接收复制的 NV_ENC_CONFIG 缓冲区 (成功时从 presetConfig 复制)。</param>
    /// <returns>NVENC 状态码。</returns>
    /// </summary>
    private unsafe int TryGetPresetConfig(Guid codec, Guid preset, nint cfgBuf)
    {
        if (_encoder == 0) return -1;
        try
        {
            var presetCfgFn = GetFuncDelegate<GetEncodePresetConfigExFn>(GetFuncPtr(FnGetEncodePresetConfigEx));
            nint presetBuf = Marshal.AllocHGlobal(PresetConfigSize);
            try
            {
                new Span<byte>((void*)presetBuf, PresetConfigSize).Clear();
                // NV_ENC_PRESET_CONFIG_VER = STRUCT_VERSION(5) | 1<<31 (13.1)
                *(uint*)presetBuf = StructVer(_activeVersion, 5, highBit: true);
                // ═══ 关键: 同时初始化内层 NV_ENC_CONFIG.version (offset 8) ═══
                // FFmpeg nvenc.c 在调用前同时设置:
                //   preset_config.version = NV_ENC_PRESET_CONFIG_VER
                //   preset_config.presetCfg.version = NV_ENC_CONFIG_VER
                // 不设置内层 version → 驱动返回 INVALID_VERSION (0xF)
                *(uint*)(presetBuf + 8) = StructVer(_activeVersion, 9, highBit: true);  // NV_ENC_CONFIG_VER

                // 用 HIGH_QUALITY (同 FFmpeg 默认)
                int hr = presetCfgFn(_encoder, codec, preset, NV_ENC_TUNING_INFO_HIGH_QUALITY, presetBuf);
                if (hr == NVENC_SUCCESS)
                {
                    // NV_ENC_PRESET_CONFIG: version(4) + padding(4) + NV_ENC_CONFIG presetCfg @8
                    byte* cfgSrc = (byte*)presetBuf + 8;
                    Buffer.MemoryCopy(cfgSrc, (void*)cfgBuf, EncConfigSize, EncConfigSize);
                }
                return hr;
            }
            finally { Marshal.FreeHGlobal(presetBuf); }
        }
        catch (Exception ex)
        {
            CoreLog.Warn("NVENC", "GetEncodePresetConfigEx 异常", ex, LogCategory.Encoding);
            return -1;
        }
    }

    // ═══ NV_ENCODE_API_FUNCTION_LIST 函数索引（对应 nvEncodeAPI.h 结构体布局）═══
    // 基址 8 = version(4) + reserved(4)，随后每个条目一个函数指针。
    // ⚠ 2026-08-31: 曾全部使用裸魔数且部分索引错误：
    //   · DestroyEncoder 误用 24（实为 NvEncUnregisterAsyncEvent）→ 编码器会话从未销毁
    //     → 多次探测后会话累积达上限 → OpenEncodeSessionEx 返回 UNSUPPORTED_DEVICE(2)
    //   · 纹理路径 RegisterResource/MapInputResource/UnmapInputResource 同样错位
    // 现统一为具名常量，避免再次出错。
    // ═══ 函数表完整顺序（依据 nvEncodeAPI.h 第 3048-3066 行）═══
    //  0 OpenEncodeSession | 1 GetEncodeGUIDCount | 2 GetEncodeProfileGUIDCount
    //  3 GetEncodeProfileGUIDs | 4 GetEncodeGUIDs | 5 GetInputFormatCount
    //  6 GetInputFormats | 7 GetEncodeCaps | 8 GetEncodePresetCount
    //  9 GetEncodePresetGUIDs | 10 GetEncodePresetConfig | 11 InitializeEncoder
    // 12 CreateInputBuffer | 13 DestroyInputBuffer | 14 CreateBitstreamBuffer
    // 15 DestroyBitstreamBuffer | 16 EncodePicture | 17 LockBitstream
    // 18 UnlockBitstream | 19 LockInputBuffer | 20 UnlockInputBuffer
    // 21 GetEncodeStats | 22 GetSequenceParams | 23 RegisterAsyncEvent
    // 24 UnregisterAsyncEvent | 25 MapInputResource | 26 UnmapInputResource
    // 27 DestroyEncoder | 28 InvalidateRefFrames | 29 OpenEncodeSessionEx
    // 30 RegisterResource | 31 UnregisterResource | 32 ReconfigureEncoder
    // 33 reserved | 34 CreateMVBuffer | 35 DestroyMVBuffer | 36 RunMotionEstimationOnly
    // 37 GetLastErrorString | 38 SetIOCudaStreams | 39 GetEncodePresetConfigEx
    // 40 GetSequenceParamEx | 41 RestoreEncoderState | 42 LookaheadPicture
    private const int FnOpenEncodeSession = 0;
    private const int FnGetEncodeGuidCount = 1;
    private const int FnGetEncodeProfileGuidCount = 2;
    private const int FnGetEncodeProfileGuids = 3;
    private const int FnGetEncodeGuids = 4;
    private const int FnGetEncodePresetGuidCount = 8;
    private const int FnGetEncodePresetGuids = 9;
    private const int FnGetEncodePresetConfig = 10;
    private const int FnInitializeEncoder = 11;
    private const int FnCreateInputBuffer = 12;
    private const int FnDestroyInputBuffer = 13;
    private const int FnCreateBitstreamBuffer = 14;
    private const int FnDestroyBitstreamBuffer = 15;
    private const int FnEncodePicture = 16;
    private const int FnLockBitstream = 17;
    private const int FnUnlockBitstream = 18;
    private const int FnLockInputBuffer = 19;
    private const int FnUnlockInputBuffer = 20;
    private const int FnMapInputResource = 25;
    private const int FnUnmapInputResource = 26;
    private const int FnDestroyEncoder = 27;
    private const int FnOpenEncodeSessionEx = 29;
    private const int FnRegisterResource = 30;
    private const int FnUnregisterResource = 31;
    // ═══ 2026-09-13: GetEncodePresetConfigEx (索引 39, SDK 13.1) ═══
    // 老的 GetEncodePresetConfig (索引 10) 自驱动 590 起废弃且不适用 AV1;
    // FFmpeg nvenc.c 使用 Ex 版本 (带 tuningInfo) 获取 AV1 preset 配置。
    private const int FnGetEncodePresetConfigEx = 39;
    // NvEncGetLastErrorString (索引 37) — 获取驱动对上次失败的详细描述
    private const int FnGetLastErrorString = 37;

    // 诊断写盘仅 Debug 生效（审查 P3：Release 版不应无条件往临时目录写 nvenc_diag.txt）
    [System.Diagnostics.Conditional("DEBUG")]
    private static void AppendDiag(string line)
    {
        try { System.IO.File.AppendAllText(Path.Combine(Path.GetTempPath(), "nvenc_diag.txt"), line + "\n"); } catch { }
    }

    /// <summary>获取驱动对上次 NVENC 调用的错误描述。</summary>
    public string GetLastErrorString()
    {
        try
        {
            nint fnPtr = GetFuncPtr(FnGetLastErrorString);
            if (fnPtr == 0) return "(函数不可用)";
            var fn = Marshal.GetDelegateForFunctionPointer<GetLastErrorFn>(fnPtr);
            nint strPtr = fn(_encoder);
            if (strPtr == 0) return "(无错误信息)";
            return Marshal.PtrToStringAnsi(strPtr) ?? "(null)";
        }
        catch (Exception ex) { return $"(获取失败: {ex.Message})"; }
    }
    private delegate nint GetLastErrorFn(nint enc);

    private nint GetFuncPtr(int idx) => *(nint*)(_funcTable + 8 + idx * 8);
    private static T GetFuncDelegate<T>(nint ptr) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(ptr);

    private delegate int CreateInstanceDelegate(nint funcList);
    private delegate int OpenSessionFn(nint params_, nint* encoder);
    private delegate int InitEncoderFn(nint enc, nint p);
    // ═══ 2026-09-05 修复: GUID 按值传递 (x64 ABI 修正) ═══
    // NVENC 的 GUID 参数 (16 字节结构体) 在 C/C++ ABI 中按值传递。
    // x64 下: 前 8 字节走 RCX/RDX/R8/R9，后 8 字节走栈。
    // C# `ref Guid` 传递的是指针 (8 字节)，驱动从指针地址读取 GUID →
    // 收到的是指针值被误读为 GUID 的一部分 → presetCfg=0xC (UNSUPPORTED_PARAM)。
    // 修复: 用 [MarshalAs(UnmanagedType.LPStruct)] Guid 按值传递 16 字节结构体。
    private delegate int GetEncodePresetConfigFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid presetGuid,
        nint presetConfig);
    // ═══ 2026-09-13: NvEncGetEncodePresetConfigEx (带 tuningInfo, 适用于 AV1) ═══
    private delegate int GetEncodePresetConfigExFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid presetGuid,
        uint tuningInfo,
        nint presetConfig);
    private delegate int GetEncodePresetGuidCountFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid, out uint count);
    private delegate int GetEncodePresetGuidsFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        nint presetGuids, uint arraySize, out uint count);
    private delegate int GetEncodeGuidCountFn(nint enc, out uint count);
    private delegate int GetEncodeGuidsFn(nint enc, nint guids, uint arraySize, out uint count);
    private delegate int GetProfileGuidCountFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid, out uint count);
    private delegate int GetProfileGuidsFn(nint enc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid encodeGuid,
        nint guids, uint arraySize, out uint count);
    private delegate int CreateInputBufferFn(nint enc, nint p);
    private delegate int CreateBitstreamFn(nint enc, nint p);
    private delegate int DestroyInputBufferFn(nint enc, nint buf);
    private delegate int DestroyBitstreamBufferFn(nint enc, nint buf);
    private delegate int UnregisterResourceFn(nint enc, nint registeredRes);
    private delegate int RegisterResourceFn(nint enc, nint p);
    private delegate int MapInputResourceFn(nint enc, nint p);
    private delegate int UnmapInputResourceFn(nint enc, nint p);
    private delegate int LockInputBufferFn(nint enc, nint p);
    private delegate int UnlockInputBufferFn(nint enc, nint p);
    private delegate int EncodePictureFn(nint enc, nint p);
    private delegate int LockBitstreamFn(nint enc, nint p);
    private delegate int UnlockBitstreamFn(nint enc, nint p);

    // ═══════════════════════════════════════════════════════
    //  GPU 纹理直通编码路径
    //  使用 NvEncRegisterResource + NvEncMapInputResource，
    //  让 NVENC 直接读取 D3D11 纹理，跳过 GPU→CPU 回读。
    //  节省 4K 下约 6-15ms 的 GPU→CPU 同步等待。
    // ═══════════════════════════════════════════════════════

    /// <summary>从 D3D11 纹理直接编码 AV1 帧（GPU 纹理直通路径）。
    /// 纹理格式必须为 B8G8R8A8_UNorm 或 R16G16B16A16_Float（HDR 时）。
    /// 使用 NVENC 的 RegisterResource + MapInputResource API，
    /// 避免 GPU→CPU 回读再上传的往返开销。</summary>
    public byte[] EncodeAv1FromTexture(ID3D11Texture2D texture, int w, int h, int qp)
        => EncodeRawFromTexture(texture, w, h, qp, CodecAv1);

    /// <summary>从 D3D11 纹理直接编码 AV1 帧，可指定输出位深（8 或 10）。</summary>
    /// <remarks>输入纹理为 8-bit BGRA，10-bit 输出由硬件内部转换（见 <see cref="ApplyAv1ColorConfig"/>）。</remarks>
    public byte[] EncodeAv1FromTexture(ID3D11Texture2D texture, int w, int h, int qp, int bitDepth)
        => EncodeRawFromTexture(texture, w, h, qp, CodecAv1, bitDepth);

    /// <summary>从 D3D11 纹理直接编码 HEVC 帧（GPU 纹理直通路径）。</summary>
    public byte[] EncodeHevcFromTexture(ID3D11Texture2D texture, int w, int h, int qp)
        => EncodeRawFromTexture(texture, w, h, qp, CodecHevc);

    private byte[] EncodeRawFromTexture(ID3D11Texture2D texture, int w, int h, int qp, Guid codec,
                                       int outputBitDepth = 8)
    {
        // ═══ 2026-09-17 P1 修复：与 Dispose 互斥（同 EncodeRaw）═══
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return EncodeRawFromTextureCore(texture, w, h, qp, codec, outputBitDepth);
        }
    }

    private byte[] EncodeRawFromTextureCore(ID3D11Texture2D texture, int w, int h, int qp, Guid codec,
                                       int outputBitDepth = 8)
    {
        qp = Math.Max(qp, AvifEncoderCapabilities.MinCrf(AvifEncoderKind.Nvenc));   // 同 EncodeRawCore

        var initFn = GetFuncDelegate<InitEncoderFn>(GetFuncPtr(FnInitializeEncoder));
        // ⚠ 原实现用 13/15/21，实为 DestroyInputBuffer/DestroyBitstreamBuffer/GetEncodeStats
        var registerResFn = GetFuncDelegate<RegisterResourceFn>(GetFuncPtr(FnRegisterResource));
        var mapInputFn = GetFuncDelegate<MapInputResourceFn>(GetFuncPtr(FnMapInputResource));
        var unmapInputFn = GetFuncDelegate<UnmapInputResourceFn>(GetFuncPtr(FnUnmapInputResource));
        var createBufFn = GetFuncDelegate<CreateBitstreamFn>(GetFuncPtr(FnCreateBitstreamBuffer));
        var encFn = GetFuncDelegate<EncodePictureFn>(GetFuncPtr(FnEncodePicture));
        var lockFn = GetFuncDelegate<LockBitstreamFn>(GetFuncPtr(FnLockBitstream));
        var unlockFn = GetFuncDelegate<UnlockBitstreamFn>(GetFuncPtr(FnUnlockBitstream));

        // ═══ 2026-09-13 重写: 与 EncodeRaw 对齐 (13.1 结构版本 + 正确偏移) ═══
        // 旧实现: INITIALIZE_PARAMS 用 VER(5)/偏移 +8/+24/+96, CONFIG 用 VER(6)/偏移 +120/+140
        // 全为旧版 SDK 或错位值 → InitializeEncoder/Register 必然失败 (纹理直通从未工作过)
        // NV_ENC_INITIALIZE_PARAMS 后直接跟 NV_ENC_CONFIG (4096 字节大), 分离避免覆盖
        nint initBuf = Marshal.AllocHGlobal(InitParamsSize);
        nint cfgBuf = Marshal.AllocHGlobal(EncConfigSize);
        int hr;
        try
        {
            unsafe
            {
                new Span<byte>((void*)initBuf, InitParamsSize).Clear();
                new Span<byte>((void*)cfgBuf, EncConfigSize).Clear();
                byte* p = (byte*)initBuf;

                var preset = PickPresetGuid(codec);
                var profile = PickProfileGuid(codec);
                var profileGuid = profile != Guid.Empty ? profile : CodecAv1ProfileMain;

                // ── 纹理路径初始化: 同样用官方推荐的 encodeConfig=NULL 纯 preset 模式 ──
                // 分区: initBuf 用于 init params, cfgBuf 用于重建备路径
                // ═══ Windows GUID 是 4 字节对齐 (Data1=uint32_t), 非 8 字节 ═══
                //   +0 version | +4 encodeGUID | +20 presetGUID | +36 encodeWidth
                //   +40 encodeHeight | +44 darWidth | +48 darHeight | +52 frameRateNum
                //   +56 frameRateDen | +60 enableEncodeAsync | +64 enablePTD
                //   +88 encodeConfig (nint) | +136 tuningInfo | +140 bufferFormat
                *(uint*)(p + 0) = StructVer(_activeVersion, 7, highBit: true);
                Buffer.MemoryCopy(&codec, p + 4, 16, 16);      // encodeGUID  @ 4
                Buffer.MemoryCopy(&preset, p + 20, 16, 16);    // presetGUID  @ 20
                *(uint*)(p + 36) = (uint)w;                    // encodeWidth @ 36
                *(uint*)(p + 40) = (uint)h;                    // encodeHeight@ 40
                *(uint*)(p + 44) = (uint)w;                    // darWidth    @ 44
                *(uint*)(p + 48) = (uint)h;                    // darHeight   @ 48
                *(uint*)(p + 52) = 30;                         // frameRateNum@ 52 (30fps)
                *(uint*)(p + 56) = 1;                          // frameRateDen@ 56
                *(uint*)(p + 64) = 1;                          // enablePTD   @ 64
                *(uint*)(p + 136) = NV_ENC_TUNING_INFO_HIGH_QUALITY;  // tuningInfo @136
                *(uint*)(p + 140) = NV_ENC_BUFFER_FORMAT_ARGB;        // bufferFormat @140 (ARGB = BGRA8 内存布局)
                *(nint*)(p + 88) = 0;                          // encodeConfig = NULL

                // ═══ 10-bit 必须走显式 config 路径 ═══
                // encodeConfig=NULL 时驱动套用 preset 默认配置，outputBitDepth 恒为 8，
                // 无法产出 10-bit ⇒ 直接进 config 分支（与 EncodeRaw 的"不静默降位深"一致）。
                hr = outputBitDepth >= 10 ? -1 : initFn(_encoder, initBuf);

                // ═══ 兜底: GetEncodePresetConfigEx 基础配置 + GOP/QP 修正 ═══
                if (hr != NVENC_SUCCESS)
                {
                    CoreLog.Warn("NVENC", $"InitEncoder(preset) 0x{hr:X8}，回退 config 模式 (纹理路径)", LogCategory.Encoding);
                    int hrPreset = TryGetPresetConfig(codec, preset, cfgBuf);
                    if (hrPreset == NVENC_SUCCESS)
                    {
                        byte* cfg = (byte*)cfgBuf;
                        // 实测偏移：gopLength@20、frameIntervalP@24、rcParams@40
                        // （旧值 24/28/44 整体错位，与 EncodeRaw 同一处笔误）
                        *(uint*)(cfg + CfgGopLength) = 1;        // gopLength = 1
                        *(int*)(cfg + CfgFrameIntervalP) = 0;    // frameIntervalP = 0 (I only)
                        *(uint*)(cfg + RcRateControlMode) = NV_ENC_PARAMS_RC_CONSTQP;
                        *(uint*)(cfg + RcConstQp + 0) = (uint)qp;   // constQP.qpInterP
                        *(uint*)(cfg + RcConstQp + 4) = (uint)qp;   // constQP.qpInterB
                        *(uint*)(cfg + RcConstQp + 8) = (uint)qp;   // constQP.qpIntra
                        // 纹理路径输入恒为 8-bit BGRA8（见 NV_ENC_BUFFER_FORMAT_ARGB @140）
                        ApplyAv1ColorConfig(cfg, codec, outputBitDepth, NV_ENC_BUFFER_FORMAT_ARGB, _avifCicp);
                        *(nint*)(p + 88) = cfgBuf;            // encodeConfig @88
                        hr = initFn(_encoder, initBuf);
                    }
                }
                if (hr != NVENC_SUCCESS)
                    throw new InvalidOperationException($"[NVENC] InitEncoder: 0x{hr:X8} (纹理路径; preset={preset}; {w}x{h})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initBuf);
            Marshal.FreeHGlobal(cfgBuf);
        }

        // ── Step 1: NvEncRegisterResource ──
        // 注册 D3D11 纹理到 NVENC。
        // ═══ 2026-09-17 修正：旧布局表全部错位（凭猜测定偏移）═══
        // 真实布局由 MSVC offsetof 实测（SDK 13.1 / x64）：
        //   +0  uint32 version
        //   +4  uint32 resourceType      = 0 (NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX)
        //   +8  uint32 width
        //   +12 uint32 height
        //   +16 uint32 pitch             = 0 (纹理)
        //   +20 uint32 subResourceIndex  = 0
        //   +24 void*  resourceToRegister ← 纹理指针（旧代码把 bufferUsage 写到这里）
        //   +32 NV_ENC_REGISTERED_PTR registeredResource（OUTPUT，旧代码却把纹理指针写进来）
        //   +40 uint32 bufferFormat      （旧代码写 20 → 实际 40；错位导致 bufferFormat=0 非法）
        //   +44 uint32 bufferUsage
        //   +48 NV_ENC_FENCE_POINT_D3D12* pInputFencePoint = NULL
        // ⇒ 结果：RegisterResource 必然失败，纹理直通路径从未真正工作过。
        nint rr = Marshal.AllocHGlobal(RegisterResourceSize);
        nint registeredRes = 0;
        try
        {
            unsafe
            {
                new Span<byte>((void*)rr, RegisterResourceSize).Clear();
                *(uint*)rr = StructVer(_activeVersion, 5);  // NV_ENC_REGISTER_RESOURCE_VER = STRUCT_VERSION(5) (13.1)
                *(uint*)(rr + 4) = 0;   // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 0（2 = CUDAARRAY，D3D11 纹理必须传 0）
                *(uint*)(rr + RrWidth) = (uint)w;
                *(uint*)(rr + RrHeight) = (uint)h;
                *(uint*)(rr + RrPitch) = 0;                 // pitch = 0 for texture
                *(uint*)(rr + RrSubResourceIndex) = 0;
                *(nint*)(rr + RrResourceToRegister) = texture.NativePointer;
                *(uint*)(rr + RrBufferFormat) = NV_ENC_BUFFER_FORMAT_ARGB;
                *(uint*)(rr + RrBufferUsage) = NV_ENC_INPUT_IMAGE;

                hr = registerResFn(_encoder, rr);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] RegisterResource: 0x{hr:X8}");
                registeredRes = *(nint*)(rr + RrRegisteredResource);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(rr);
        }

        // ── Step 2: NvEncMapInputResource ──
        // 映射已注册的资源，获取 NVENC 输入缓冲区。
        // ═══ 2026-09-17 修正：真实布局（MSVC offsetof 实测）═══
        //   +0  uint32 version
        //   +4  uint32 subResourceIndex = 0
        //   +8  void*  inputResource        （旧版遗留字段，保持 NULL）
        //   +16 NV_ENC_REGISTERED_PTR registeredResource ← step 1 的输出（旧代码写 8）
        //   +24 NV_ENC_INPUT_PTR mappedResource（OUTPUT）
        //   +32 NV_ENC_BUFFER_FORMAT mappedBufferFmt（OUTPUT；旧代码把它当 pitch 读）
        nint mr = Marshal.AllocHGlobal(MapInputResourceSize);
        // ═══ 2026-09-17 P0 修复：mapped 状态供 finally 解映射用（作用域须覆盖内外两层）═══
        bool mapped = false;
        try
        {
            unsafe
            {
                new Span<byte>((void*)mr, MapInputResourceSize).Clear();
                *(uint*)mr = StructVer(_activeVersion, 4);  // NV_ENC_MAP_INPUT_RESOURCE_VER = STRUCT_VERSION(4)
                *(uint*)(mr + 4) = 0;                       // subResourceIndex
                *(nint*)(mr + MirInputResource) = 0;        // inputResource（遗留字段，不用）
                *(nint*)(mr + MirRegisteredResource) = registeredRes;

                hr = mapInputFn(_encoder, mr);
                if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] MapInputResource: 0x{hr:X8}");
                mapped = true;
                nint mappedBuffer = *(nint*)(mr + MirMappedResource);
                // ⚠ 旧代码把 MirMappedBufferFmt (+32，输出的是 NV_ENC_BUFFER_FORMAT 枚举，
                //   形如 0x01000000) 当成 pitch 读出来，再写进 PIC_PARAMS.inputPitch —— 纯属错位。
                //   注册纹理由驱动自行推导 pitch，NVIDIA D3D11 示例同样不设置该字段 ⇒ 置 0。
                const int srcPitch = 0;

                // ── Step 3: Create bitstream buffer ──
                int bufSize = w * h * 4;
                // ═══ 结构体缓冲区：实测 sizeof（旧值 800/2400/2400 中 PIC_PARAMS 越界）═══
                nint bb = Marshal.AllocHGlobal(CreateBitstreamSize);
                nint pic = Marshal.AllocHGlobal(PicParamsSize);
                nint lk = Marshal.AllocHGlobal(LockBitstreamSize);
                // ═══ 2026-09-17 P0 修复：驱动侧句柄提出来，finally 里销毁/解映射 ═══
                nint bsBuf = 0;
                bool bitstreamLocked = false;
                try
                {
                    unsafe
                    {
                        new Span<byte>((void*)bb, CreateBitstreamSize).Clear();
                        *(uint*)bb = StructVer(_activeVersion, 1);   // NV_ENC_CREATE_BITSTREAM_BUFFER_VER
                        hr = createBufFn(_encoder, bb);
                        // NV_ENC_CREATE_BITSTREAM_BUFFER (4 字节对齐): bitstreamBuffer @16 (out)
                        bsBuf = *(nint*)(bb + 16);              // bitstreamBuffer @16 (out)
                        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}");

                        new Span<byte>((void*)pic, PicParamsSize).Clear();
                        *(uint*)pic = StructVer(_activeVersion, 7, highBit: true);   // NV_ENC_PIC_PARAMS_VER
                        *(uint*)(pic + 4) = (uint)w; *(uint*)(pic + 8) = (uint)h;
                        *(uint*)(pic + 12) = (uint)srcPitch;
                        *(uint*)(pic + 16) = NV_ENC_PIC_FLAG_FORCEINTRA;  // EOS 是 flush 专用，单帧不带（与 EncodeRaw 670 一致）
                        *(nint*)(pic + 40) = mappedBuffer;
                        *(nint*)(pic + 48) = bsBuf;
                        *(uint*)(pic + 64) = NV_ENC_BUFFER_FORMAT_ARGB;  // bufferFmt @64 (ARGB = BGRA8 内存布局)
                        *(uint*)(pic + 68) = 1;  // pictureStruct = FRAME (0x01)
                        hr = encFn(_encoder, pic);
                        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] EncodePicture: 0x{hr:X8}");

                        new Span<byte>((void*)lk, LockBitstreamSize).Clear();
                        *(uint*)lk = StructVer(_activeVersion, 2);   // NV_ENC_LOCK_BITSTREAM_VER（无高位 bit，与 EncodeRaw 一致）
                        *(nint*)(lk + 8) = bsBuf;
                        hr = lockFn(_encoder, lk);
                        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockBitstream: 0x{hr:X8}");
                        bitstreamLocked = true;
                        int bsSize = *(int*)(lk + 36);       // bitstreamSizeInBytes @36 (13.1)
                        nint bsData = *(nint*)(lk + 56);     // bitstreamBufferPtr @56 (13.1)
                        // ═══ 2026-09-17 P0 修复：bsSize 先校验再用（与 EncodeRaw 一致）═══
                        if (bsSize < 0 || bsSize > bufSize * 4)
                            throw new InvalidOperationException($"[NVENC] LockBitstream 返回异常 bsSize={bsSize} (buf={bufSize})");
                        var result = new byte[bsSize];
                        Marshal.Copy(bsData, result, 0, bsSize);
                        unlockFn(_encoder, lk);
                        bitstreamLocked = false;

                        // ── Cleanup: UnmapInputResource ──
                        unmapInputFn(_encoder, mr);
                        mapped = false;

                        return result;
                    }
                }
                finally
                {
                    // ═══ 2026-09-17 P0/P1 修复：异常路径也要解锁/解映射 + 驱动资源必须销毁 ═══
                    if (bitstreamLocked)
                    {
                        try { unlockFn(_encoder, lk); } catch (Exception ex) { AppendDiag($"[Texture] UnlockBitstream(清理) 异常: {ex.Message}"); }
                    }
                    if (mapped)
                    {
                        try { unmapInputFn(_encoder, mr); } catch (Exception ex) { AppendDiag($"[Texture] UnmapInputResource(清理) 异常: {ex.Message}"); }
                    }
                    if (bsBuf != 0)
                    {
                        try
                        {
                            int dhr = GetFuncDelegate<DestroyBitstreamBufferFn>(GetFuncPtr(FnDestroyBitstreamBuffer))(_encoder, bsBuf);
                            if (dhr != NVENC_SUCCESS) AppendDiag($"[Texture] DestroyBitstreamBuffer hr=0x{dhr:X8}");
                        }
                        catch (Exception ex) { AppendDiag($"[Texture] DestroyBitstreamBuffer 异常: {ex.Message}"); }
                    }
                    Marshal.FreeHGlobal(bb);
                    Marshal.FreeHGlobal(pic);
                    Marshal.FreeHGlobal(lk);
                }
            }
        }
        finally
        {
            // ═══ 2026-09-17 P0 修复：注册资源必须注销（FnUnregisterResource 此前零使用）═══
            // 需在 Unmap 之后（finally 晚于内层 unmap）执行；失败仅记日志。
            if (registeredRes != 0)
            {
                try
                {
                    int uhr = GetFuncDelegate<UnregisterResourceFn>(GetFuncPtr(FnUnregisterResource))(_encoder, registeredRes);
                    if (uhr != NVENC_SUCCESS) AppendDiag($"[Texture] UnregisterResource hr=0x{uhr:X8}");
                }
                catch (Exception ex) { AppendDiag($"[Texture] UnregisterResource 异常: {ex.Message}"); }
            }
            Marshal.FreeHGlobal(mr);
        }
    }

    public void Dispose()
    {
        // ═══ 2026-09-17 P1 修复：与编码互斥，避免 DestroyEncoder 落在编码中途 ═══
        lock (_gate)
        {
            if (_disposed) return; _disposed = true;
            // ⚠ 原实现用索引 24，实为 NvEncUnregisterAsyncEvent —— 编码器会话从未被真正销毁，
            // 多次调用后会话累积达驱动上限，后续 OpenEncodeSessionEx 返回 UNSUPPORTED_DEVICE(2)。
            if (_encoder != 0) GetFuncDelegate<DestroyEncoderFn>(GetFuncPtr(FnDestroyEncoder))(_encoder);
            if (_funcTable != 0) Marshal.FreeHGlobal(_funcTable);
            if (_dll != 0) NativeLibrary.Free(_dll);
            _encoder = 0; _funcTable = 0; _dll = 0;
        }
    }
    private delegate int DestroyEncoderFn(nint enc);
}

/// <summary>AV1 → AVIF (ISOBMFF) 容器写入器。替代旧的 IVF 写入，生成标准 AVIF 文件。</summary>
public static class IvfWriter
{
    /// <param name="hwCicp">NVENC 会话实际收到的那份色彩意图（与构造函数传入的必须是同一个值）。
    /// 非 null ⇒ colr(nclx) 的三个码点与 full_range <b>照抄它</b>，本函数不再自行推断 ——
    /// 硬件用什么矩阵只有下发时才知道，猜就是又一次"容器说一套、硬件做一套"。
    /// null = 非 NVENC 路径（MFT/QSV 的像素是我们自己按 BT.601 转的），沿用的是下方的老逻辑。</param>
    /// <param name="clli">HDR 内容亮度级别 <c>(MaxCLL, MaxFALL)</c>；null = 不写 <c>clli</c> 属性盒。
    /// 取值只问 <see cref="FormatHelper.AvifContentLightLevel"/>，与软件路 avifenc 的
    /// <c>--clli</c> 同函数同入参 ⇒ 同一张图两条路声明同一个亮度范围。</param>
    public static void WriteAvif(byte[] av1Bs, int w, int h, string path,
        string? colorSpaceTag = null, byte[]? iccProfile = null, bool hdrOutput = false,
        FormatHelper.AvifCicp? hwCicp = null, (ushort MaxCLL, ushort MaxFALL)? clli = null)
    {
        // 解析 AV1 OBU 流提取序列头参数
        var (profile, level, tier, bitDepth, mono, chromaSubX, chromaSubY, chromaPos, seqHeaderObu) = ParseAv1SequenceHeader(av1Bs);

        // ═══ 2026-08-16: colr 属性索引 (0=未写入; >0 时 ipma 关联) ═══
        int colrIndex = 0;

        // ═══ 原子写: 先写同目录临时文件, 成功后替换目标 (避免异常留下截断的 .avif) ═══
        string tempPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        FileStream fs;
        try { fs = File.Create(tempPath); }
        catch
        {
            // 临时文件创建失败 (权限/磁盘) → 退回直接写
            tempPath = path;
            fs = File.Create(path);
        }
        using var bw = new BinaryWriter(fs);

        // ═══ ftyp box ═══
        var ftyp = new MemoryStream();
        WriteBoxHeader(ftyp, "ftyp");
        WriteAscii(ftyp, "avif");       // major_brand
        WriteU32BE(ftyp, 0);            // minor_version
        WriteAscii(ftyp, "avif");       // compatible_brands
        WriteAscii(ftyp, "mif1");
        WriteAscii(ftyp, "miaf");
        WriteBoxToStream(bw, ftyp);

        // ═══ meta box (FullBox, version=0, flags=0) ═══
        var meta = new MemoryStream();
        WriteFullBoxHeader(meta, "meta", 0, 0);

        // hdlr box
        var hdlr = new MemoryStream();
        // ═══ 2026-09-19 修复：hdlr 是 FullBox，必须带 version/flags ═══
        // 旧代码用 WriteBoxHeader（只写 size+type）再直接写 pre_defined ⇒ 读者把随后的
        // 4 字节 0 当作 version/flags，把 'pict' 当作 pre_defined ⇒ libavif 直接报
        // "Box[hdlr] contains a pre_defined value that is nonzero" 并拒绝整份文件。
        // （盒体从 29 → 33 字节，与 avifenc 产物的 hdlr 尺寸一致，可作交叉核对。）
        // 此前所有"实机验证"都用 ffmpeg/ffprobe 或自家解析器，它们对这一处宽容，故一直没暴露。
        WriteFullBoxHeader(hdlr, "hdlr", 0, 0);
        WriteU32BE(hdlr, 0);            // pre_defined
        WriteAscii(hdlr, "pict");       // handler_type
        WriteU32BE(hdlr, 0); WriteU32BE(hdlr, 0); WriteU32BE(hdlr, 0); // reserved
        hdlr.WriteByte(0);              // name (null-terminated)
        WriteBoxToStream(meta, hdlr);

        // pitm box (primary item = 1)
        var pitm = new MemoryStream();
        WriteFullBoxHeader(pitm, "pitm", 0, 0);
        WriteU16BE(pitm, 1);            // item_ID
        WriteBoxToStream(meta, pitm);

        // iloc box (item location)
        // 需要在写入 mdat 后回填偏移，先计算 meta 大小
        // 使用 construction: offset_from_file = 0, 后续 patch
        var ilocPlaceholder = BuildIlocBox(0, av1Bs.Length); // placeholder offset
        long ilocBoxPos = WriteBoxToStream(meta, ilocPlaceholder); // 记录位置，供稍后精确回填

        // iinf box
        var iinf = new MemoryStream();
        WriteFullBoxHeader(iinf, "iinf", 0, 0);
        WriteU16BE(iinf, 1);            // entry_count
        // infe box
        var infe = new MemoryStream();
        WriteFullBoxHeader(infe, "infe", 2, 0);
        WriteU16BE(infe, 1);            // item_ID
        WriteU16BE(infe, 0);            // item_protection_index
        WriteAscii(infe, "av01");       // item_type
        WriteNullTermAscii(infe, "AV1 Image"); // item_name
        WriteBoxToStream(iinf, infe);
        WriteBoxToStream(meta, iinf);

        // iprp box
        var iprp = new MemoryStream();
        WriteBoxHeader(iprp, "iprp");

        // ipco box
        var ipco = new MemoryStream();
        WriteBoxHeader(ipco, "ipco");

        // av1C box
        // AV1CodecConfigurationRecord (ISO/IEC 14496-15 / AV1 ISOBMFF 规范):
        //   byte0 = marker(1)=1 | version(7)=1                    → 恒为 0x81
        //   byte1 = seq_profile(3) << 5 | seq_level_idx_0(5)
        //   byte2 = seq_tier_0(1) | high_bitdepth(1) | twelve_bit(1) | monochrome(1)
        //           | chroma_subsampling_x(1) | chroma_subsampling_y(1) | chroma_sample_position(2)
        //   byte3 = reserved(3) | initial_presentation_delay_present(1) | ...
        // ⚠ 之前把 profile/level 塞进 byte0、tier/bitdepth 塞进 byte1，且缺 byte3
        //   → 解码器按错误的 profile/位深初始化。
        var av1c = new MemoryStream();
        WriteBoxHeader(av1c, "av1C");
        byte byte0 = 0x81;              // marker=1, version=1
        byte byte1 = (byte)(((profile & 0x07) << 5) | (level & 0x1F));
        byte byte2 = (byte)(((tier & 0x01) << 7) | ((bitDepth > 8 ? 1 : 0) << 6) | ((bitDepth == 12 ? 1 : 0) << 5) |
                            ((mono ? 1 : 0) << 4) | ((chromaSubX & 0x01) << 3) | ((chromaSubY & 0x01) << 2) | (chromaPos & 0x03));
        byte byte3 = 0x00;              // initial_presentation_delay_present=0, reserved
        av1c.WriteByte(byte0);
        av1c.WriteByte(byte1);
        av1c.WriteByte(byte2);
        av1c.WriteByte(byte3);
        // configOBUs: 序列头 OBU (不含 temporal delimiter)
        if (seqHeaderObu is { Length: > 0 })
            av1c.Write(seqHeaderObu, 0, seqHeaderObu.Length);
        WriteBoxToStream(ipco, av1c);

        // ispe box
        var ispe = new MemoryStream();
        WriteFullBoxHeader(ispe, "ispe", 0, 0);
        WriteU32BE(ispe, (uint)w);
        WriteU32BE(ispe, (uint)h);
        WriteBoxToStream(ipco, ispe);

        // pixi box
        var pixi = new MemoryStream();
        WriteFullBoxHeader(pixi, "pixi", 0, 0);
        pixi.WriteByte((byte)(mono ? 1 : 3)); // num_channels
        byte bd = (byte)bitDepth;
        pixi.WriteByte(bd);
        if (!mono) { pixi.WriteByte(bd); pixi.WriteByte(bd); }
        WriteBoxToStream(ipco, pixi);

        // ═══ 2026-08-16 P2-10a 修复: colr box 声明色彩空间 (NVENC 路径曾丢失) ═══
        // 必须放入 ipco 属性容器 + ipma 关联才有意义 (直接放 meta 层解码器不认)。
        // NVENC 输入 byte[] BGRA8 是 SDR 像素, 用 SDR 转移 (13=sRGB)。
        // matrix=1 (BT.709) 与 libaom SDR 路径一致 (identity=0 兼容性差)。
        // ⚠ 必须在 WriteBoxToStream(iprp, ipco) 之前填充 ipco, 否则属性顺序错。
        // ═══ 2026-09-18 修复: HDR 分支 matrix 需与 avifenc HDR 路径对齐 ═══
        //   avifenc HDR 用 `--cicp <p>/16/9`（matrix=9 = BT.2020 NCL，见 NativeAvifEncoder
        //   BuildCicpArgs 的 2026-08-10 修复备注）；而本方法此前 HDR 分支也写 matrix=1(BT.709)
        //   ⇒ 同一 HDR 内容两条路径的 colr 声明不同, BT.2020 primaries + BT.709 matrix 语义矛盾
        //   （严格解码器或按 3DLUT 校正的管线可能错误解释）。SDR 保持 matrix=1 不变。
        try
        {
            var colr = new MemoryStream();
            WriteBoxHeader(colr, "colr");
            WriteAscii(colr, "nclx");
            byte primaries = TrueToneCap.Core.ColorManagement.ColorSpaceConverter.GetCicpPrimaries(
                colorSpaceTag is not (null or "System" or "sRGB") ? colorSpaceTag : "sRGB");
            // ═══ 2026-09-19 修复：nclx 的 primaries/transfer/matrix 恒为 **16-bit 大端** ═══
            // 原实现每个字段只写 1 字节（colr.WriteByte），盒体 16 字节，而合法 nclx 恒为
            // 8(盒头)+4(colour_type)+2+2+2+1 = **19** 字节。读者（ffmpeg/libavif）按 16-bit
            // 对齐解析 → 字段全部错位且盒体短 3 字节 → 整个 colr 被丢弃：实测 ffprobe 解我们的
            // NVENC AVIF 得 color_primaries/transfer = unknown，而同一内容的 avifenc 产物得
            // bt2020/smpte2084 —— 等于硬件路径的 HDR 内容对外**没有色彩声明**，查看器按 SDR
            // 解释会整体偏色。此前一直"看着对"是因为校验用的是自家解析器（交接 §3 同型问题）。
            if (hwCicp is { } hw)
            {
                // ═══ 2026-09-20：硬件路一律照抄下发给驱动的那一份 ═══
                // 这一处不再自行推断矩阵：NVENC 用哪张做 RGB→YUV，只有"下发了什么"才决定得了。
                // 老逻辑在这里写死 601（当时驱动默认就是 601）现在会变成新的谎报，因为驱动已按
                // 下发的码点转换（709/2020）。两者同源取值才谈得上"准确"。
                WriteU16BE(colr, hw.Primaries);
                WriteU16BE(colr, hw.Transfer);
                WriteU16BE(colr, hw.Matrix);
                // full_range 字节的位序：nclx 里是 MSB 优先位域 ⇒ bit7 = full_range_flag，
                // 0x80 = full（与软件路 avifenc 产物同一位序）。取值与写进
                // NV_ENC_CONFIG_AV1.colorRange 的同源（都来自 NvEncWantsFullRange）。
                colr.WriteByte(hw.FullRange ? (byte)0x80 : (byte)0x00);
            }
            else
            {
                WriteU16BE(colr, primaries);
                // ═══ 2026-09-18：transfer 按输出场景分派 —— SDR=13(sRGB)，HDR=16(ST.2084 PQ)。
                //     旧实现恒写 13，HDR 码流（NVENC HDR 直通）会被解码端按 sRGB 解释（过亮/过暗）。
                WriteU16BE(colr, (ushort)(hdrOutput ? FormatHelper.CicpTransferPq : FormatHelper.CicpTransferSrgb));
                // ═══ 非 NVENC 路（MFT/QSV）：像素是 BgraToNv12 用 BT.601 系数算出来的 ⇒ 声明 601。
                //     ⚠ 这一路的【摆幅】本机测不了（无 QSV/MFT AV1），下面沿用的 full_range 属未验证
                //     取值 —— 见交接 §32.7 开放项；纯色带探针的 CASES 已备好 qsv 一档，
                //     换到有 Intel 核显的机器上跑同一条判据即可判死。
                WriteU16BE(colr, hdrOutput
                    ? FormatHelper.AvifMatrixCoefficients(primaries)
                    : FormatHelper.CicpMatrixBt601);
                colr.WriteByte(NvEncoderNative.NvEncWantsFullRange ? (byte)0x80 : (byte)0x00);
            }
            // ═══ 2026-09-19 缺陷 B（交接 §30.9）：无 H.273 码点的色域【不许】写 nclx ═══
            // GetCicpPrimaries 对 AdobeRGB 返回 1（=BT.709），而 Adobe 原色既不是 709 也不是 P3
            // ⇒ 写下去就是一句谎话；更要命的是它与同一份文件里那个真 ICC(prof) 打架（实测产物里
            // 两个 colr 盒并存），采信码点的查看器会按 BT.709 解释 Adobe 像素 ⇒ 系统性偏色。
            // 判断一律问 FormatHelper.BuildSdrCicp —— 它对 AdobeRGB 返回 null，理由与 PNG 的 cICP、
            // avifenc 的 --cicp 跳过完全同源（H.273 Table 2 无此码点 ⇒ 只靠 ICC 声明），
            // 三条路径共用这一个事实源，不再各写各的。HDR 分支行为不变（HDR 恒为 BT.2020 家族）。
            // 跳过时 colrIndex 保持 0 ⇒ 下面的 ipma 不会关联一个不存在的属性
            //（该约束见 2026-09-18 "P1-ipma 索引副作用" 那条修复）。
            if (hdrOutput || FormatHelper.BuildSdrCicp(colorSpaceTag) is not null)
            {
                WriteBoxToStream(ipco, colr);
                colrIndex = 4;              // 属性索引 4 (av1C=1, ispe=2, pixi=3, colr=4)
            }
        }
        catch
        {
            colrIndex = 0;              // colr 失败 → 不写关联
        }

        // ═══ 可选 ICC colr box (type 'prof'): 与 nclx colr 共存合法 ═══
        int iccIndex = 0;
        // 只问一个判据（§31.3-9）：这里原先是 `Length > 0`，是全链路最松的一道 ——
        // 硬件容器宁可只有 nclx，也不要一份声明不了 RGB 色域的 ICC（例如 CMYK 件）。
        if (FormatHelper.IsEmbeddableIcc(iccProfile))
        {
            try
            {
                var iccColr = new MemoryStream();
                WriteBoxHeader(iccColr, "colr");
                WriteAscii(iccColr, "prof");
                iccColr.Write(iccProfile, 0, iccProfile.Length);
                WriteBoxToStream(ipco, iccColr);
                // ═══ 2026-09-18 修复（P1-ipma 索引副作用）═══
                // 原写法 `iccIndex = colrIndex > 0 ? 5 : (colrIndex = 4)` 把"ICC 取索引"与
                // "回写 colrIndex"耦合在一起：nclx 失败(colrIndex=0)时 ICC 抢占索引 4 的同时
                // 把 colrIndex 改成 4 —— 而 colr nclx box 根本没写进 ipco，ipma 却会按
                // hasNclx(colrIndex>0) 给一个不存在的属性essential=1 关联，违反 ISOBMFF/HEIF 约束
                //（ipma 不得引用未声明的属性索引），严格解析器会拒读整个文件。
                // 现改为无副作用的独立计算：nclx 与 ICC 各自独立取下一个空闲索引；
                // hasNclx 由下文 colrIndex>0 判定，本处不再回写 colrIndex。
                iccIndex = colrIndex > 0 ? 5 : 4;   // nclx 已占 4 → ICC=5; nclx 缺失 → ICC=4
            }
            catch
            {
                iccIndex = 0;
                CoreLog.Warn("WriteAvif", "ICC colr box 写入失败, 已跳过", LogCategory.Encoding);
            }
        }

        // ═══ 2026-09-20：HDR 的 clli（内容亮度级别）属性盒 —— 硬件路与软件路补齐（交接 §31.3-10）═══
        // 实测对照（libavif 自家产物 bands_libaom_hdr.avif）：clli 是 ipco 里的一个 12 字节属性盒
        //   （maxCLL u16 + maxFALL u16），ipma 以 essential=0 关联到主图；
        // 而本方法的产物此前**完全没有这一段** ⇒ 同一张 HDR 截图，走 NVENC 的那份对查看器
        // 等于"亮度范围未知"，tone-map 结果与走 avifenc 的那份不同（这是能目视出来的差别）。
        // 取值不在此处算：由调用方传入 FormatHelper.AvifContentLightLevel(peakNits) 的结果，
        // 与软件路 --clli 同一个函数、同一个入参。
        int clliIndex = 0;
        if (clli is { } cll)
        {
            try
            {
                var clliBox = new MemoryStream();
                WriteBoxHeader(clliBox, "clli");
                WriteU16BE(clliBox, cll.MaxCLL);
                WriteU16BE(clliBox, cll.MaxFALL);
                WriteBoxToStream(ipco, clliBox);
                // 索引 = 已占用属性数 + 1（av1C/ispe/pixi 恒 1..3，colr/icc 见上，clli 永远最后写）
                clliIndex = 3 + (colrIndex > 0 ? 1 : 0) + (iccIndex > 0 ? 1 : 0) + 1;
            }
            catch
            {
                clliIndex = 0;
                CoreLog.Warn("WriteAvif", "clli 盒写入失败, 已跳过", LogCategory.Encoding);
            }
        }

        WriteBoxToStream(iprp, ipco);

        // ipma box
        var ipma = new MemoryStream();
        // ═══ flags 恒为 0，与 avifenc（libavif 自家产物）逐字节一致 ═══
        // ISO 14496-12 的字面规则是"flags&1 决定 item_ID 宽度"，但 HEIF/AVIF 的既成约定是
        // item_ID 恒 16-bit、property index 恒 8-bit（essential 占 bit7）、flags 写 0。
        // 实测对照（scripts/precision/ipma_decode.py）：avifenc 与本路径的 ipma body 都只有
        // 【item_ID=16bit + index=8bit】这一种解释能刚好消费完，其余组合都留下游离字节。
        // 曾按字面规则把 flags 改成 0x000001 → libavif 立刻报 "illegal property index [385]"
        // 拒读整份文件，故此处保持 0。本路径真正的容器缺陷在别处：colr 的 nclx 字段宽度、
        // 以及 hdlr 漏写 version/flags（均 2026-09-19 修）。
        WriteFullBoxHeader(ipma, "ipma", 0, 0);
        WriteU32BE(ipma, 1);            // entry_count
        WriteU16BE(ipma, 1);            // item_ID
        // ═══ 2026-08-16: association_count 随 colr (nclx/ICC) 动态 (3-5)；2026-09-20 再加 clli ═══
        bool hasNclx = colrIndex > 0;
        bool hasIcc = iccIndex > 0;
        bool hasClli = clliIndex > 0;
        byte assocCount = (byte)(3 + (hasNclx ? 1 : 0) + (hasIcc ? 1 : 0) + (hasClli ? 1 : 0));
        ipma.WriteByte(assocCount);     // association_count (av1C, ispe, pixi[, colr nclx][, colr prof][, clli])
        ipma.WriteByte(0x81);           // essential=1, property_index=1 (av1C)
        ipma.WriteByte(0x82);           // essential=1, property_index=2 (ispe)
        ipma.WriteByte(0x03);           // essential=0, property_index=3 (pixi)
        if (hasNclx)
            ipma.WriteByte((byte)(0x80 | colrIndex)); // essential=1, property_index=4 (colr nclx)
        if (hasIcc)
            ipma.WriteByte((byte)(0x80 | iccIndex)); // essential=1, property_index=5 (colr prof)
        if (hasClli)
            ipma.WriteByte((byte)clliIndex);  // essential=0 —— 与 libavif 产物同写法（clli 是提示性元数据）
        WriteBoxToStream(iprp, ipma);

        WriteBoxToStream(meta, iprp);

        // ═══ 2026-08-16 P2-10a ⚠ 已移除: colr 现在写入 ipco (上方), 不在 meta 直属层 ═══

        // 计算 mdat 数据偏移 (ftyp_size + meta_size + mdat_header_size)
        long ftypSize = ftyp.Length;
        long mdatHeaderSize = 8; // size(4) + type(4)
        long mdatDataOffset = ftypSize + (meta.Length) + mdatHeaderSize;

        // 回填 iloc 中的偏移
        PatchIlocOffset(meta, ilocBoxPos, mdatDataOffset);

        WriteBoxToStream(bw, meta);

        // ═══ mdat box ═══
        // ⚠ BinaryWriter.Write(uint) 按"平台字节序"写入（x86/ARM 均为小端），
        // 而 ISOBMFF 要求所有 box size 为大端。之前直接 bw.Write(uint) 导致
        // mdat 长度被解析成天文数字 → 文件无法解码（影响 NVENC/MFT/QSV 三条硬件后端）。
        // 故此处手工按大端逐字节写入。
        uint mdatSize = (uint)(8 + av1Bs.Length);
        bw.Write((byte)(mdatSize >> 24));
        bw.Write((byte)(mdatSize >> 16));
        bw.Write((byte)(mdatSize >> 8));
        bw.Write((byte)mdatSize);
        WriteAsciiBE(bw, "mdat");
        bw.Write(av1Bs);
        bw.Flush();

        // ═══ 原子提交: 成功写完后替换目标文件 ═══
        if (tempPath != path)
        {
            bw.Dispose();
            try
            {
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* 忽略清理失败 */ }
                throw;
            }
        }
    }

    // ── AV1 OBU 解析 ──

    // internal：测试据此验证"请求 10-bit 时 NVENC 产出的码流确实带 10-bit 序列头"
    // （位深无法从文件名或大小判断，只能解析 AV1 序列头）。
    internal static (int profile, int level, int tier, int bitDepth, bool mono, int chromaSubX, int chromaSubY, int chromaPos, byte[]? seqHeaderObu)
        ParseAv1SequenceHeader(byte[] av1Bs)
    {
        // 默认值: Main profile, level 5.1, 8-bit, 4:2:0
        int profile = 0, level = 13, tier = 0, bitDepth = 8, chromaSubX = 1, chromaSubY = 1, chromaPos = 0;
        bool mono = false;
        byte[]? seqHeaderObu = null;

        try
        {
            const int MaxSeqHeaderSize = 128; // P2-4: 序列头上限, 超出视为损坏流
            int pos = 0;
            while (pos < av1Bs.Length - 2)
            {
                byte header = av1Bs[pos];
                int obuType = (header >> 3) & 0x0F;
                bool hasSizeField = (header & 0x02) != 0;
                bool hasExt = (header & 0x04) != 0;

                int headerSize = 1 + (hasExt ? 1 : 0);
                pos += headerSize;

                int obuSize = 0;
                if (hasSizeField)
                {
                    // LEB128 解码
                    int shift = 0;
                    while (pos < av1Bs.Length)
                    {
                        byte b = av1Bs[pos++];
                        obuSize |= (b & 0x7F) << shift;
                        shift += 7;
                        if ((b & 0x80) == 0) break;
                    }
                }
                else
                {
                    // ⚠ P2-4: 无 size 字段时不能按"剩余全部字节"处理,
                    // 否则后续帧数据会被当作序列头 payload。按上限截断。
                    obuSize = Math.Min(av1Bs.Length - pos, MaxSeqHeaderSize);
                    CoreLog.Trace("ParseAv1SequenceHeader",
                        () => $"OBU 无 size 字段, 按上限 {MaxSeqHeaderSize} 字节截断解析 (实际剩余 {av1Bs.Length - pos})", LogCategory.Encoding);
                }

                int realObuSize = obuSize;   // P2: 跳过 OBU 必须用真实尺寸
                if (obuSize > MaxSeqHeaderSize)
                {
                    CoreLog.Trace("ParseAv1SequenceHeader",
                        () => $"OBU 类型 {obuType} 大小 {obuSize} 超过序列头上限 {MaxSeqHeaderSize}, 截断解析", LogCategory.Encoding);
                    obuSize = MaxSeqHeaderSize;
                }

                if (obuType == 1) // OBU_SEQUENCE_HEADER
                {
                    int start = pos;
                    int len = Math.Min(obuSize, av1Bs.Length - start);
                    if (len > 4)
                    {
                        // 保存从 OBU header 字节开始的完整 OBU (header + [size] + payload)
                        int obuStart = start - headerSize - (hasSizeField ? Leb128Size(av1Bs, start - headerSize) : 0);
                        int obuEnd = Math.Min(start + len, av1Bs.Length);
                        if (obuStart >= 0 && obuStart < obuEnd)
                        {
                            seqHeaderObu = new byte[obuEnd - obuStart];
                            Array.Copy(av1Bs, obuStart, seqHeaderObu, 0, obuEnd - obuStart);
                        }

                        // ── 位级解析序列头 payload (AV1 规范 §5.5.1 / §5.5.2) ──
                        // 字段顺序与"哪些字段是【推断】、根本不占比特"的条款，按四家一致的实现核对：
                        //   规范 §5.5.1/§5.5.2 + §6.4.1/§6.4.2、libaom av1/decoder/{obu.c,decodeframe.c}、
                        //   dav1d src/obu.c、libavif src/obu.c（avifenc 自己填 av1C 用的就是这一份）。
                        // ═══ 2026-09-20 重写（这次是真的把 4:2:0 判成 4:4:4 的根因）═══
                        // 旧实现有两处"读不该读的位"：
                        //   ① profile 0/1 时 chroma_subsampling_x/y 是**推断值**（Main 恒 4:2:0、
                        //      High 恒单色平面），码流里没有这两位；旧代码无条件读 2 位，
                        //      读到的是 chroma_sample_position 的 00 ⇒ 判成 4:4:4 写进 av1C。
                        //      之前"看起来对"纯属侥幸：那时 NVENC 不写 color_description，
                        //      cdp=0 走的是另一条把 x/y 硬写成 1,1 的分支；2026-09-20 我们把
                        //      matrix 从 0 改成 1/9 之后才掉进 else 分支暴露出来。
                        //   ② reduced_still_picture_header=1 时，frame_id_numbers_present_flag 与
                        //      那 9 个 feature flag 全是推断值；旧代码照样读 ⇒ avifenc 那种
                        //      reduced 流上整体多吞 15 位。
                        // 现在两条都按规范分支读取；每条 else-if 的"读/不读"都写明出处。
                        var br = new BitReader(av1Bs, start, len);
                        profile = (int)br.ReadBits(3);                  // seq_profile
                        br.ReadBit();                                   // still_picture
                        bool reduced = br.ReadBit();                    // reduced_still_picture_header

                        bool decoderModelInfoPresent = false;
                        int bufferDelayLen = 0;
                        bool enableOrderHint = false;
                        if (!reduced)
                        {
                            if (br.ReadBit())                          // timing_info_present_flag
                            {
                                br.ReadBits(32);                       // num_units_in_display_tick
                                br.ReadBits(32);                       // time_scale
                                if (br.ReadBit()) br.ReadUvlc();       // equal_picture_interval → num_ticks_per_picture_minus_1
                                decoderModelInfoPresent = br.ReadBit();
                                if (decoderModelInfoPresent)
                                {
                                    bufferDelayLen = (int)br.ReadBits(5) + 1;   // buffer_delay_length_minus_1
                                    br.ReadBits(32);                   // num_units_in_decoding_tick
                                    br.ReadBits(5);                    // buffer_removal_time_length_minus_1
                                    br.ReadBits(5);                    // frame_presentation_time_length_minus_1
                                }
                            }
                            bool initialDisplayDelayPresent = br.ReadBit();
                            uint opCnt = br.ReadBits(5) + 1;           // operating_points_cnt_minus_1
                            for (uint i = 0; i < opCnt; i++)
                            {
                                br.ReadBits(12);                       // operating_point_idc[i]
                                uint seqLevelIdx = br.ReadBits(5);     // seq_level_idx[i]
                                if (i == 0) level = (int)seqLevelIdx;
                                // seq_tier[i] 只在 level > 7 时出现（level ≤ 7 推断 0）—— 顺序在 level 之后
                                if (seqLevelIdx > 7 && i == 0) tier = (int)br.ReadBits(1);
                                else if (seqLevelIdx > 7) br.ReadBits(1);
                                if (decoderModelInfoPresent && br.ReadBit())
                                {                                      // decoder_model_present_for_this_op[i]
                                    br.ReadBits(bufferDelayLen);       // decoder_buffer_delay[i]
                                    br.ReadBits(bufferDelayLen);       // encoder_buffer_delay[i]
                                    br.ReadBit();                      // low_delay_mode_flag[i]
                                }
                                if (initialDisplayDelayPresent && br.ReadBit())
                                    br.ReadBits(4);                    // initial_display_delay_minus_1[i]
                            }
                        }
                        else
                        {
                            // reduced=1：只有 seq_level_idx[0]；timing/decoder_model/op_cnt/idc/tier 全推断
                            level = (int)br.ReadBits(5);
                            tier = 0;
                        }

                        int frameWidthBits = (int)br.ReadBits(4) + 1;   // frame_width_bits_minus_1
                        int frameHeightBits = (int)br.ReadBits(4) + 1;  // frame_height_bits_minus_1
                        br.ReadBits(frameWidthBits);                    // max_frame_width_minus_1
                        br.ReadBits(frameHeightBits);                   // max_frame_height_minus_1
                        if (!reduced && br.ReadBit())                   // frame_id_numbers_present_flag（reduced 时推断 0）
                        {
                            br.ReadBits(4);                            // delta_frame_id_length_minus_2
                            br.ReadBits(3);                            // additional_frame_id_length_minus_1
                        }
                        br.ReadBit();                                   // use_128x128_superblock
                        br.ReadBit();                                   // enable_filter_intra
                        br.ReadBit();                                   // enable_intra_edge_filter
                        if (!reduced)
                        {
                            // 以下 9 项在 reduced=1 时全是规范规定的推断值，不占比特
                            br.ReadBit();                              // enable_interintra_compound
                            br.ReadBit();                              // enable_masked_compound
                            br.ReadBit();                              // enable_warped_motion
                            br.ReadBit();                              // enable_dual_filter
                            enableOrderHint = br.ReadBit();            // enable_order_hint
                            if (enableOrderHint)
                            {
                                br.ReadBit();                          // enable_jnt_comp
                                br.ReadBit();                          // enable_ref_frame_mvs
                            }
                            // choose=1 ⇒ SELECT_TOOL(2)、不读值；choose=0 ⇒ 读【1 位】
                            int forceScreenContentTools = br.ReadBit() ? 2 : (int)br.ReadBits(1);
                            if (forceScreenContentTools > 0 && !br.ReadBit())
                                br.ReadBits(1);                        // seq_force_integer_mv（1 位，旧实现读成 2 位）
                            if (enableOrderHint) br.ReadBits(3);       // order_hint_bits_minus_1
                        }
                        br.ReadBit();                                   // enable_superres
                        br.ReadBit();                                   // enable_cdef
                        br.ReadBit();                                   // enable_restoration

                        // ── color_config (§5.5.2) ──
                        bool highBitdepth = br.ReadBit();              // high_bitdepth（恒读，无 level 推断）
                        bool twelveBit = profile == 2 && highBitdepth && br.ReadBit();
                        bitDepth = highBitdepth ? (twelveBit ? 12 : 10) : 8;
                        // mono_chrome：profile 1（High Monochrome）推断为 0、不占比特；其余读 1 位
                        mono = profile != 1 && br.ReadBit();
                        bool colorDescriptionPresent = br.ReadBit();
                        // cdp=0 时三个值【全部】推断为 Unspecified(2) —— §6.4.2。
                        // 旧代码写的 (1,13,0) 是 2018 年 v1.0.0 草案里 "else if profile==0" 的条款，现行规范已删。
                        int primaries = 2, transfer = 2, matrix = 2;
                        if (colorDescriptionPresent)
                        {
                            primaries = (int)br.ReadBits(8);
                            transfer = (int)br.ReadBits(8);
                            matrix = (int)br.ReadBits(8);
                        }

                        if (mono)
                        {
                            br.ReadBit();                               // color_range（mono 分支仍读）
                            chromaSubX = 1; chromaSubY = 1; chromaPos = 0;
                            // §5.5.2 在此 return：chroma_sample_position 与 separate_uv_delta_q 都不占比特
                        }
                        else if (primaries == 1 && transfer == 13 && matrix == 0)
                        {
                            // CICP 恰为 sRGB-identity ⇒ color_range 推断 1（不读）、x=y=0（4:4:4 推断）、
                            // 不读 chroma_sample_position；只有链条末尾的 separate_uv_delta_q 要读。
                            chromaSubX = 0; chromaSubY = 0; chromaPos = 0;
                            br.ReadBit();                               // separate_uv_delta_q
                        }
                        else
                        {
                            br.ReadBit();                               // color_range
                            if (profile == 0)
                            {
                                // Main profile：码流【不写】subsampling，规范恒推断 4:2:0（这就是本函数此前判错的那一位）
                                chromaSubX = 1; chromaSubY = 1;
                            }
                            else if (profile == 1)
                            {
                                chromaSubX = 0; chromaSubY = 0;         // High：恒 4:4:4
                            }
                            else if (bitDepth == 12)
                            {
                                chromaSubX = br.ReadBit() ? 1 : 0;      // Pro 且 12-bit：才真的写在码流里
                                chromaSubY = chromaSubX == 1 ? (br.ReadBit() ? 1 : 0) : 0;
                            }
                            else
                            {
                                chromaSubX = 1; chromaSubY = 0;         // Pro 且 8/10-bit：恒 4:2:2
                            }
                            if (chromaSubX == 1 && chromaSubY == 1)
                                chromaPos = (int)br.ReadBits(2);        // chroma_sample_position
                            br.ReadBit();                               // separate_uv_delta_q
                        }
                        br.ReadBit();                                   // film_grain_params_present
                    }
                    break; // 只需要第一个序列头
                }

                // ═══ 2026-09-18 P2 修复：跳过非序列头 OBU 用【真实尺寸】 ═══
                // 旧实现 pos += 截断后的 obuSize（≤128），帧数据 OBU（远大于 128）会让
                // 扫描点落入 payload 内部，后续字节被误当 OBU 头解析 → 可能提取"伪序列头"。
                pos += realObuSize;
            }
        }
        catch { /* 解析失败使用默认值 */ }

        return (profile, level, tier, bitDepth, mono, chromaSubX, chromaSubY, chromaPos, seqHeaderObu);
    }

    /// <summary>AV1 序列头 payload 的简单 MSB-first 位读取器。</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _end;
        private int _pos;   // 字节下标
        private int _bit;   // 当前字节内位下标 (0=MSB)

        public BitReader(byte[] data, int offset, int length)
        {
            _data = data;
            _pos = offset;
            _end = Math.Min(offset + length, data.Length);
            _bit = 0;
        }

        public bool ReadBit()
        {
            if (_pos >= _end) return false; // 越界返回 0 (流损坏时安全降级)
            bool b = (_data[_pos] & (0x80 >> _bit)) != 0;
            if (++_bit == 8) { _bit = 0; _pos++; }
            return b;
        }

        public uint ReadBits(int n)
        {
            uint v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | (ReadBit() ? 1u : 0u);
            return v;
        }

        /// <summary>AV1 的 uvlc()：前导零个数 leadingZeros 决定位宽，值 = 2^n - 1 + n 位数据。</summary>
        public uint ReadUvlc()
        {
            int leading = 0;
            while (!ReadBit())
            {
                leading++;
                if (leading >= 32) return uint.MaxValue;   // 规范规定的哨兵值
            }
            if (leading == 0) return 0;
            return ((1u << leading) - 1) + ReadBits(leading);
        }
    }

    private static int Leb128Size(byte[] data, int pos)
    {
        int count = 0;
        while (pos + count < data.Length)
        {
            count++;
            if ((data[pos + count - 1] & 0x80) == 0) break;
        }
        return count;
    }

    // ── ISOBMFF Box 写入辅助 ──

    private static void WriteBoxHeader(Stream s, string type)
    {
        // 占位 size (后续 patch)
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0);
        WriteAscii(s, type);
    }

    private static void WriteFullBoxHeader(Stream s, string type, byte version, uint flags)
    {
        s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); s.WriteByte(0); // size placeholder
        WriteAscii(s, type);
        s.WriteByte(version);
        s.WriteByte((byte)((flags >> 16) & 0xFF));
        s.WriteByte((byte)((flags >> 8) & 0xFF));
        s.WriteByte((byte)(flags & 0xFF));
    }

    private static void WriteBoxToStream(BinaryWriter bw, MemoryStream box)
    {
        // Patch size
        long size = box.Length;
        box.Position = 0;
        box.WriteByte((byte)(size >> 24));
        box.WriteByte((byte)(size >> 16));
        box.WriteByte((byte)(size >> 8));
        box.WriteByte((byte)(size));
        box.Position = 0;
        box.CopyTo(bw.BaseStream);
    }

    /// <summary>把完整 box（补写 header 后）写入父流，返回该 box 在父流中的起始偏移。
    /// 返回值可忽略；需要按位置回填内容时使用（如 iloc 的 extent_offset）。</summary>
    private static long WriteBoxToStream(Stream parent, MemoryStream box)
    {
        long startPos = parent.Position;
        long size = box.Length;
        box.Position = 0;
        box.WriteByte((byte)(size >> 24));
        box.WriteByte((byte)(size >> 16));
        box.WriteByte((byte)(size >> 8));
        box.WriteByte((byte)(size));
        box.Position = 0;
        box.CopyTo(parent);
        return startPos;
    }

    private static MemoryStream BuildIlocBox(long dataOffset, int dataLength)
    {
        var iloc = new MemoryStream();
        WriteFullBoxHeader(iloc, "iloc", 0, 0);
        // ═══ 2026-09-18 P0-E 修复：offset_size / length_size 是【字节数】字段
        //     (ISO/IEC 14496-12 §8.11.3，HEIF 要求 ∈ {4, 8})，实际写入 4 字节 offset/length
        //     ⇒ 必须写 0x44 (offset_size=4, length_size=4)。此前写 0x00（声明 0 字节字段）
        //     与下方 WriteU32BE 自相矛盾，解码器 item 定位全错（"0=32位"编码不存在）。═══
        iloc.WriteByte(0x44); // offset_size=4 字节, length_size=4 字节
        iloc.WriteByte(0x00); // base_offset_size=0, reserved=0
        WriteU16BE(iloc, 1);  // item_count
        WriteU16BE(iloc, 1);  // item_ID
        WriteU16BE(iloc, 0);  // data_reference_index
        WriteU16BE(iloc, 1);  // extent_count
        WriteU32BE(iloc, (uint)dataOffset);  // extent_offset (placeholder)
        WriteU32BE(iloc, (uint)dataLength);  // extent_length
        return iloc;
    }

    /// <summary>回填 iloc 中 extent_offset 字段（大端）。</summary>
    /// <param name="ilocBoxPos">iloc box 在 meta 流中的起始偏移（由 WriteBoxToStream 返回）。</param>
    /// <remarks>
    /// iloc 布局: [size(4)][type(4)][version(1)][flags(3)][sizes(2)][item_count(2)]
    ///            [item_ID(2)][data_ref(2)][extent_count(2)][extent_offset(4)][extent_length(4)]
    /// 从 box 起始到 extent_offset = 4+4+1+3+2+2+2+2+2 = 22 字节。
    /// ⚠ 原实现有两个缺陷：
    ///   1) 用"扫描 iloc 四字节签名"定位 box 位置 —— 理论上可能误匹配 ICC/payload 中的相同字节；
    ///   2) 偏移量写成 i+4+12 = 20（应为 i+4+14 = 22），把 extent_count 与 offset 前 2 字节
    ///      覆盖成垃圾 → 偏移回填失效。
    /// 现改为直接接收写入时记录的精确位置，并按 22 字节偏移回填。
    /// </remarks>
    private static void PatchIlocOffset(MemoryStream meta, long ilocBoxPos, long actualOffset)
    {
        int offsetPos = (int)(ilocBoxPos + 22);
        if (offsetPos < 0 || offsetPos + 4 > meta.Length) return;

        byte[] metaBytes = meta.GetBuffer();
        metaBytes[offsetPos] = (byte)(actualOffset >> 24);
        metaBytes[offsetPos + 1] = (byte)(actualOffset >> 16);
        metaBytes[offsetPos + 2] = (byte)(actualOffset >> 8);
        metaBytes[offsetPos + 3] = (byte)(actualOffset);
    }

    private static void WriteAscii(Stream s, string str)
    {
        foreach (char c in str) s.WriteByte((byte)c);
    }

    private static void WriteNullTermAscii(Stream s, string str)
    {
        foreach (char c in str) s.WriteByte((byte)c);
        s.WriteByte(0);
    }

    private static void WriteAsciiBE(BinaryWriter bw, string str)
    {
        foreach (char c in str) bw.Write((byte)c);
    }

    private static void WriteU16BE(Stream s, ushort v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v & 0xFF));
    }

    private static void WriteU32BE(Stream s, uint v)
    {
        s.WriteByte((byte)(v >> 24));
        s.WriteByte((byte)(v >> 16));
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)(v & 0xFF));
    }
}
