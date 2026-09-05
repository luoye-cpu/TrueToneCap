// TrueToneCap.Core/Encoding/NvEncoderNative.cs
// NVENC 原生 SDK — P/Invoke nvEncodeAPI64.dll (NVIDIA 驱动自带)
// 支持: AV1 (RTX 40+), HEVC (GTX 10+), D3D11 纹理直通

using System.Runtime.InteropServices;
using Vortice.Direct3D11;

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
    private const uint NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 2;
    private const uint NV_ENC_BUFFER_FORMAT_ARGB10 = 7;
    private const uint NV_ENC_PIC_FLAG_FORCEINTRA = 0x01;
    private const uint NV_ENC_PIC_FLAG_EOS = 0x02;

    // ═══ NVENC 结构体缓冲区大小（bytes）═══
    // NV_ENC_INITIALIZE_PARAMS: encodeConfig 指针在 offset 88，需>=96；纹理路径用 256
    private const int InitParamsSize = 256;
    // NV_ENC_CONFIG: 包含 rateControl、QP、GOP 等完整配置，纹理路径用 512
    private const int EncConfigSize = 512;
    // NV_ENC_PRESET_CONFIG: version(4) + reserved(4) + NV_ENC_CONFIG
    private const int PresetConfigSize = 520;

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
    // NV_ENC_PRESET_P1_GUID = { 0x49df21c5, 0x6dfa, 0x4feb, { 0x97,0x81,0x51,0xbe,0xe5,0x2b,0x39,0x0c } }
    private static readonly Guid PresetP1 =
        new(0x49DF21C5, 0x6DFA, 0x4FEB, 0x97, 0x81, 0x51, 0xBE, 0xE5, 0x2B, 0x39, 0x0C);

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

    private readonly nint _dll;
    private readonly nint _funcTable;
    private nint _encoder;
    private readonly ID3D11Device _d3dDevice;
    private bool _disposed;
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
                System.Diagnostics.Debug.WriteLine($"[NVENC] 默认适配器探测失败，将枚举 N 卡: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine("[NVENC] ✅ NVIDIA 适配器会话打开成功");
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NVENC] NVIDIA 适配器会话打开失败: {ex.Message}");
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
    /// ⚠ **每个结构体的 n 各不相同**（见 nvEncodeAPI.h 第 938–1569 行）：
    /// <list type="bullet">
    /// <item>NV_ENC_INITIALIZE_PARAMS_VER = STRUCT_VERSION(5) | (1&lt;&lt;31)</item>
    /// <item>NV_ENC_CONFIG_VER            = STRUCT_VERSION(6) | (1&lt;&lt;31)</item>
    /// <item>NV_ENC_PIC_PARAMS_VER        = STRUCT_VERSION(4) | (1&lt;&lt;31)</item>
    /// <item>NV_ENC_REGISTER_RESOURCE_VER = STRUCT_VERSION(3)</item>
    /// <item>NV_ENC_MAP_INPUT_RESOURCE_VER= STRUCT_VERSION(4)</item>
    /// <item>其余（CREATE_INPUT_BUFFER / CREATE_BITSTREAM_BUFFER /
    ///       LOCK_BITSTREAM / LOCK_INPUT_BUFFER / OPEN_SESSION_EX_PARAMS）= STRUCT_VERSION(1)</item>
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
                        System.Diagnostics.Debug.WriteLine(
                            $"[NVENC] 适配器 {adapter.Description.Description.Trim()} 创建设备失败 hr=0x{hr:X8}");
                        continue;
                    }

                    // 立即上下文由 ID3D11Device.ImmediateContext 再次获取，此处释放原始引用
                    if (ctxPtr != 0) Marshal.Release(ctxPtr);

                    System.Diagnostics.Debug.WriteLine(
                        $"[NVENC] ✅ 在 {adapter.Description.Description.Trim()} 上创建设备");
                    return new ID3D11Device(devPtr);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[NVENC] 适配器 {adapter.Description.Description.Trim()} 创建设备失败: {ex.Message}");
                }
                finally { adapter.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NVENC] 适配器枚举失败: {ex.Message}");
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
            var table = Marshal.AllocHGlobal(512);
            try
            {
                foreach (uint ver in s_sdkVersions)
                {
                    new Span<byte>((void*)table, 512).Clear();
                    // 同上：version 字段需完整的 NVENCAPI_STRUCT_VERSION 位
                    *(uint*)table = ver | NvencStructVersionBits;
                    int hr = create(table);
                    if (hr == NVENC_SUCCESS)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NVENC] ProbeApiVersion: {(ver & 0xFF)}.{(ver >> 24) & 0xFF}");
                        return ver;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[NVENC] ProbeApiVersion 失败: {ex.Message}"); }
        finally { if (dll != 0) NativeLibrary.Free(dll); }
        return 0;
    }

    public NvEncoderNative(ID3D11Device d3dDevice)
    {
        _d3dDevice = d3dDevice;
        _dll = NativeLibrary.Load("nvEncodeAPI64.dll");
        var diag = (string msg) => { System.Diagnostics.Debug.WriteLine(msg); Console.WriteLine(msg); };
        diag($"[NVENC] DLL 已加载");

        var createFn = NativeLibrary.GetExport(_dll, "NvEncodeAPICreateInstance");
        var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(createFn);

        _funcTable = Marshal.AllocHGlobal(512);
        bool created = false;
        int lastHr = -1;

        foreach (uint ver in s_sdkVersions)
        {
            new Span<byte>((void*)_funcTable, 512).Clear();
            // ═══ 2026-08-31 修复: NV_ENCODE_API_FUNCTION_LIST 的 version 字段 ═══
            // 必须填 NV_ENCODE_API_FUNCTION_LIST_VER = NVENCAPI_VERSION | (1 << 16)。
            // 原实现只填了 NVENCAPI_VERSION（缺 1<<16），导致 NvEncodeAPICreateInstance
            // 恒返回 NV_ENC_ERR_INVALID_VERSION (15) —— **在任何系统上都无法初始化**，
            // 与显卡型号、驱动、适配器选择统统无关。
            // 实测：RTX 5080 + RTX 4060 均报 "所有 SDK 版本探测失败 hr=0x0000000F"。
            // 注: 下方 OpenEncodeSessionEx 的 params->version 已正确使用 | (1<<16) | (0x7<<28)，
            //     说明该模式作者已知，仅此处遗漏。
            *(uint*)_funcTable = ver | NvencStructVersionBits;
            int hr = create(_funcTable);
            System.Diagnostics.Debug.WriteLine($"[NVENC]   SDK {(ver & 0xFF)}.{(ver >> 24) & 0xFF:00}: hr=0x{hr:X8} {(hr == NVENC_SUCCESS ? "✓" : "✗")}");
            if (hr == NVENC_SUCCESS)
            {
                _activeVersion = ver;
                created = true;
                diag($"[NVENC] ✅ CreateInstance OK (SDK {(ver & 0xFF)}.{(ver >> 24) & 0xFF})");
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
        // ⚠ 真实大小为 1040 字节（含 reserved1[253]），原实现只分配 1024 → 驱动读写越界
        byte* osp = stackalloc byte[1088];
        new Span<byte>(osp, 1088).Clear();
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
                diag($"[NVENC] ✅ 编码会话 OK (AV1, deviceType={devType})");
                break;
            }
        }
        if (hr2 != NVENC_SUCCESS)
            throw new InvalidOperationException($"[NVENC] OpenEncodeSessionEx 失败: 0x{hr2:X8}");

        _encoder = enc;
    }

    // ═══════════ 编码 ═══════════

    public byte[] EncodeAv1(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecAv1);

    public byte[] EncodeHevc(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecHevc);

    private byte[] EncodeRaw(byte[] bgra, int w, int h, int qp, Guid codec)
    {
        var initFn = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(GetFuncPtr(FnInitializeEncoder));
        var createBufFn = Marshal.GetDelegateForFunctionPointer<CreateBitstreamFn>(GetFuncPtr(FnCreateBitstreamBuffer));
        var encFn = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(GetFuncPtr(FnEncodePicture));
        var lockFn = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(GetFuncPtr(FnLockBitstream));
        var unlockFn = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(GetFuncPtr(FnUnlockBitstream));

        nint initBuf = Marshal.AllocHGlobal(InitParamsSize);
        nint cfgBuf = Marshal.AllocHGlobal(EncConfigSize);
        nint presetBuf = Marshal.AllocHGlobal(PresetConfigSize);
        int hr = -1;   // 初始化结果；后续步骤复用该变量
        try
        {
            unsafe
            {
                new Span<byte>((void*)initBuf, InitParamsSize).Clear();
                new Span<byte>((void*)cfgBuf, EncConfigSize).Clear();
                new Span<byte>((void*)presetBuf, PresetConfigSize).Clear();

                // ── 1. 让驱动填充 NV_ENC_PRESET_CONFIG ──
                // NV_ENC_PRESET_CONFIG_VER = NVENCAPI_STRUCT_VERSION(4) | (1<<31)
                *(uint*)presetBuf = StructVer(_activeVersion, 4, highBit: true);

                // ═══ 2026-08-31 修复: 运行时枚举驱动支持的 preset ═══
                // 原实现硬编码 PresetP1，但该 GUID 值未被驱动接受：
                // GetEncodePresetConfig 返回 UNSUPPORTED_PARAM(0xC) → profileGUID 全零
                // → InitializeEncoder 必然失败。
                // 改为调用 NvEncGetEncodePresetGUIDs 让**驱动列出**它支持的 preset，
                // 再逐个尝试，取第一个可用的。完全避免硬编码 GUID 出错。
                var preset = PickPresetGuid(codec, presetBuf, cfgBuf, out int hrPreset);

                byte* cfgSrc;
                if (hrPreset == NVENC_SUCCESS)
                {
                    // NV_ENC_PRESET_CONFIG: 4 version | 4 padding | NV_ENC_CONFIG presetCfg
                    // x64: NV_ENC_CONFIG 要求 8 字节对齐 → presetCfg 实际偏移为 8 而非 4
                    cfgSrc = (byte*)presetBuf + 8;
                    System.Diagnostics.Debug.WriteLine(
                        $"[NVENC] 已获取驱动预设配置 (preset={preset})");
                }
                else
                {
                    // 预设获取失败 → 自行构造最小合法配置（仅置 version），
                    // 其余字段保持 0，驱动会填充默认值。
                    System.Diagnostics.Debug.WriteLine(
                        $"[NVENC] 预设配置获取失败 0x{hrPreset:X8}，回退最小配置");
                    cfgSrc = (byte*)cfgBuf;
                    *(uint*)cfgSrc = StructVer(_activeVersion, 7, highBit: true); // NV_ENC_CONFIG_VER
                }

                // ── 2. 复制出独立的 NV_ENC_CONFIG（供 INITIALIZE_PARAMS 引用）──
                Buffer.MemoryCopy(cfgSrc, (void*)cfgBuf, EncConfigSize, EncConfigSize);

                // ═══ 2026-09-01 修复: profileGUID 必须从驱动获取 ═══
                // NV_ENC_CONFIG.profileGUID（偏移 4）是 InitializeEncoder 的必填项。
                // 当 GetEncodePresetConfig 失败时该字段为全零 → 驱动报
                // UNSUPPORTED_PARAM(0xC) / NO_ENCODE_CAPABILITY(0x8)。
                // 现调用 NvEncGetEncodeProfileGUIDs 让驱动列出它支持的 profile，
                // 取第一个填入，保证 profileGUID 始终有效。
                byte* cfg2 = (byte*)cfgBuf;
                var zero = new ReadOnlySpan<byte>(cfg2 + 4, 16);
                if (zero.IndexOfAnyExcept((byte)0) < 0)   // 全零 → 需要填充
                {
                    var profile = PickProfileGuid(codec);
                    if (profile != Guid.Empty)
                    {
                        Buffer.MemoryCopy(&profile, cfg2 + 4, 16, 16);
                        System.Diagnostics.Debug.WriteLine(
                            $"[NVENC] 已填入驱动 profile: {profile}");
                    }
                }

                // ── 3. 构造 NV_ENC_INITIALIZE_PARAMS（配置部分见下方渐进回退）──
                //   0 version | 4 encodeGUID | 20 presetGUID | 36 encodeWidth
                //   40 encodeHeight | 44 darWidth | 48 darHeight | 52 frameRateNum
                //   56 frameRateDen | 60 enableEncodeAsync | 64 enablePTD
                //   88 encodeConfig
                byte* p = (byte*)initBuf;
                *(uint*)(p + 0) = StructVer(_activeVersion, 5, highBit: true);
                Buffer.MemoryCopy(&codec, p + 4, 16, 16);      // encodeGUID  @ 4
                Buffer.MemoryCopy(&preset, p + 20, 16, 16);    // presetGUID  @ 20
                *(uint*)(p + 36) = (uint)w;                    // encodeWidth @ 36
                *(uint*)(p + 40) = (uint)h;                    // encodeHeight@ 40
                *(uint*)(p + 44) = (uint)w;                    // darWidth    @ 44
                *(uint*)(p + 48) = (uint)h;                    // darHeight   @ 48
                *(uint*)(p + 52) = 1;                          // frameRateNum@ 52
                *(uint*)(p + 56) = 1;                          // frameRateDen@ 56
                *(nint*)(p + 88) = cfgBuf;                     // encodeConfig@ 88

                // ═══ 2026-08-31: NV_ENC_CONFIG 渐进回退 ═══
                // NVENC 的初始化参数校验极严（UNSUPPORTED_PARAM = 0xC 很常见），
                // 而不同驱动/SDK 版本对字段组合的接受度不同。
                // 与其猜"哪种组合一定对"，不如逐级降级并全部试一遍：
                //   L0 完整: gopLength=1 + CONSTQP + 三项 QP（静态图最理想）
                //   L1 仅 GOP: 只设 gopLength/frameIntervalP，码率控制用驱动预设
                //   L2 纯预设: 完全不修改驱动返回的 NV_ENC_CONFIG
                // 任一级别成功即采用，从而在各种驱动上最大化兼容性。
                byte* cfg = (byte*)cfgBuf;
                for (int level = 0; level < 3; level++)
                {
                    // 每级都从驱动预设副本重新开始，避免叠加修改
                    Buffer.MemoryCopy(cfgSrc, cfg, EncConfigSize, EncConfigSize);

                    if (level <= 1)
                    {
                        // 静态图像: 单帧 GOP、全 I 帧，避免插入 P/B 帧
                        *(uint*)(cfg + 20) = 1;   // gopLength = 1
                        *(int*)(cfg + 24) = 0;    // frameIntervalP = 0 (I only)
                    }

                    if (level == 0)
                    {
                        // 恒定 QP 模式（静态图最合适），设置三方 QP
                        *(uint*)(cfg + 40 + 4) = 0;          // rateControlMode = CONSTQP
                        *(uint*)(cfg + 40 + 8) = (uint)qp;   // constQP.qpInterP
                        *(uint*)(cfg + 40 + 12) = (uint)qp;  // constQP.qpInterB
                        *(uint*)(cfg + 40 + 16) = (uint)qp;  // constQP.qpIntra
                    }
                    // level == 2: 不修改，纯驱动预设

                    hr = initFn(_encoder, initBuf);
                    if (hr == NVENC_SUCCESS)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NVENC] InitEncoder 成功 (配置级别 L{level})");
                        break;
                    }

                    System.Diagnostics.Debug.WriteLine($"[NVENC] InitEncoder L{level} 失败 0x{hr:X8}，降级重试");
                }

                if (hr != NVENC_SUCCESS)
                {
                    // 诊断信息: 暴露 GetEncodePresetConfig 的结果。若它失败，
                    // cfgBuf 中只有 version 而 profileGUID 全零 → 必然 UNSUPPORTED_PARAM。
                    var profileGuid = new Guid(new ReadOnlySpan<byte>(cfg + 4, 16));
                    throw new InvalidOperationException(
                        $"[NVENC] InitEncoder: 0x{hr:X8} (三级配置均失败; " +
                        $"presetCfg=0x{hrPreset:X8}({(hrPreset == NVENC_SUCCESS ? "成功" : "失败→profileGUID无效")}); " +
                        $"profileGUID={profileGuid}; {w}x{h}; sdk=0x{_activeVersion:X8})");
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(initBuf);
            Marshal.FreeHGlobal(cfgBuf);
            Marshal.FreeHGlobal(presetBuf);
        }

        int bufSize = w * h * 4;
        byte* ib = stackalloc byte[64];
        new Span<byte>(ib, 64).Clear();
        *(uint*)ib = _activeVersion | NvencStructVersionBits;   // NV_ENC_CREATE_INPUT_BUFFER_VER
        *(uint*)(ib + 4) = (uint)bufSize;
        nint inputBuf = 0;
        hr = GetFuncDelegate<CreateInputBufferFn>(GetFuncPtr(FnCreateInputBuffer))(_encoder, (nint)ib, &inputBuf);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateInputBuf: 0x{hr:X8}");

        byte* lb = stackalloc byte[64];
        new Span<byte>(lb, 64).Clear();
        *(uint*)lb = _activeVersion | NvencStructVersionBits;   // NV_ENC_LOCK_INPUT_BUFFER_VER
        *(nint*)(lb + 8) = inputBuf;
        hr = GetFuncDelegate<LockInputBufferFn>(GetFuncPtr(FnLockInputBuffer))(_encoder, (nint)lb);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockInput: 0x{hr:X8}");
        nint srcData = *(nint*)(lb + 16);
        int srcPitch = *(int*)(lb + 24);
        for (int y = 0; y < h; y++)
            Marshal.Copy(bgra, y * w * 4, srcData + y * srcPitch, w * 4);
        GetFuncDelegate<UnlockInputBufferFn>(GetFuncPtr(FnUnlockInputBuffer))(_encoder, (nint)lb);

        byte* bb = stackalloc byte[32];
        new Span<byte>(bb, 32).Clear();
        *(uint*)bb = _activeVersion | NvencStructVersionBits;   // NV_ENC_CREATE_BITSTREAM_BUFFER_VER
        *(uint*)(bb + 4) = (uint)(bufSize * 2);
        nint bsBuf = 0;
        hr = createBufFn(_encoder, (nint)bb, &bsBuf);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}");

        byte* pic = stackalloc byte[128];
        new Span<byte>(pic, 128).Clear();
        *(uint*)pic = StructVer(_activeVersion, 4, highBit: true);   // NV_ENC_PIC_PARAMS_VER
        *(uint*)(pic + 4) = (uint)w; *(uint*)(pic + 8) = (uint)h;
        *(uint*)(pic + 12) = (uint)srcPitch;
        *(uint*)(pic + 16) = NV_ENC_PIC_FLAG_FORCEINTRA | NV_ENC_PIC_FLAG_EOS;
        *(nint*)(pic + 40) = inputBuf;
        *(nint*)(pic + 48) = bsBuf;
        *(uint*)(pic + 72) = NV_ENC_BUFFER_FORMAT_ARGB10;
        hr = encFn(_encoder, (nint)pic);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] EncodePicture: 0x{hr:X8}");

        byte* lk = stackalloc byte[64];
        new Span<byte>(lk, 64).Clear();
        *(uint*)lk = _activeVersion | NvencStructVersionBits;   // NV_ENC_LOCK_BITSTREAM_VER
        *(nint*)(lk + 8) = bsBuf;
        hr = lockFn(_encoder, (nint)lk);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockBitstream: 0x{hr:X8}");
        int bsSize = *(int*)(lk + 24);
        nint bsData = *(nint*)(lk + 16);
        var result = new byte[bsSize];
        Marshal.Copy(bsData, result, 0, bsSize);
        unlockFn(_encoder, (nint)lk);

        return result;
    }

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
            System.Diagnostics.Debug.WriteLine($"[NVENC] profile 枚举异常: {ex.Message}");
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

    /// <summary>选择一个驱动实际支持的 preset GUID，并尽量获取其预设配置。
    /// <para>
    /// 优先按优先级顺序尝试已知 preset；若都失败，则调用
    /// <c>NvEncGetEncodePresetGUIDs</c> 枚举驱动支持的 preset 并逐个尝试。
    /// 取第一个能成功返回预设配置者。
    /// </para>
    /// </summary>
    /// <param name="codec">编码器 codec GUID。</param>
    /// <param name="presetBuf">用于接收 NV_ENC_PRESET_CONFIG 的缓冲区。</param>
    /// <param name="cfgBuf">备用缓冲区（回退最小配置时使用）。</param>
    /// <param name="hr">最后一个（或成功的）GetEncodePresetConfig 返回值。</param>
    /// <returns>选中的 preset GUID。</returns>
    private unsafe Guid PickPresetGuid(Guid codec, nint presetBuf, nint cfgBuf, out int hr)
    {
        hr = -1;
        var presetCfgFn = GetFuncDelegate<GetEncodePresetConfigFn>(GetFuncPtr(FnGetEncodePresetConfig));

        // ① 先按优先级尝试已知 preset（P1 = 性能优先，适合静态图快速编码）
        var known = new[] { PresetP1 };
        var fallback = known[0];
        foreach (var g0 in known)
        {
            var g = g0;   // 取可写副本，避免对只读变量取 ref
            *(uint*)presetBuf = StructVer(_activeVersion, 4, highBit: true);
            hr = presetCfgFn(_encoder, codec, g, presetBuf);
            if (hr == NVENC_SUCCESS) return g;
            System.Diagnostics.Debug.WriteLine($"[NVENC] preset {g} 不可用: 0x{hr:X8}");
        }

        // ② 枚举驱动支持的 preset，逐个尝试
        try
        {
            var countFn = GetFuncDelegate<GetEncodePresetGuidCountFn>(GetFuncPtr(FnGetEncodePresetGuidCount));
            int hrC = countFn(_encoder, codec, out uint count);
            if (hrC != NVENC_SUCCESS || count == 0 || count > 64) return fallback;

            var guidsFn = GetFuncDelegate<GetEncodePresetGuidsFn>(GetFuncPtr(FnGetEncodePresetGuids));
            int bytes = (int)count * 16;
            nint buf = Marshal.AllocHGlobal(bytes);
            try
            {
                new Span<byte>((void*)buf, bytes).Clear();
                hrC = guidsFn(_encoder, codec, buf, count, out uint actual);
                if (hrC != NVENC_SUCCESS) return fallback;

                for (uint i = 0; i < actual && i < count; i++)
                {
                    var pg = new Guid(new ReadOnlySpan<byte>((void*)(buf + (int)i * 16), 16));
                    *(uint*)presetBuf = StructVer(_activeVersion, 4, highBit: true);
                    hr = presetCfgFn(_encoder, codec, pg, presetBuf);
                    Console.WriteLine($"[NVENC-DIAG] preset #{i} {pg} GetEncodePresetConfig hr=0x{hr:X8}");
                    if (hr == NVENC_SUCCESS)
                    {
                        Console.WriteLine($"[NVENC] ✅ 采用驱动枚举 preset {pg}");
                        return pg;
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[NVENC] preset 枚举异常: {ex.Message}");
        }

        // ═══ 2026-09-05 终极兜底: GetEncodePresetConfig 全失败 (RTX 5080 13.1 驱动限制) ═══
        // 枚举出的 preset GUID 本身有效，只是驱动不再提供其详细配置。
        // 强制返回第一个枚举到的 preset（若枚举成功），让 InitializeEncoder 自行填充默认值。
        Console.WriteLine($"[NVENC] ⚠️ GetEncodePresetConfig 全部失败，强制使用 fallback preset {fallback}");
        return fallback;
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
    private const int FnCreateBitstreamBuffer = 14;
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
    private delegate int CreateInputBufferFn(nint enc, nint p, nint* buf);
    private delegate int LockInputBufferFn(nint enc, nint p);
    private delegate int UnlockInputBufferFn(nint enc, nint p);
    private delegate int RegisterResourceFn(nint enc, nint p);
    private delegate int MapInputResourceFn(nint enc, nint p);
    private delegate int UnmapInputResourceFn(nint enc, nint p);
    private delegate int CreateBitstreamFn(nint enc, nint p, nint* buf);
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

    /// <summary>从 D3D11 纹理直接编码 HEVC 帧（GPU 纹理直通路径）。</summary>
    public byte[] EncodeHevcFromTexture(ID3D11Texture2D texture, int w, int h, int qp)
        => EncodeRawFromTexture(texture, w, h, qp, CodecHevc);

    private byte[] EncodeRawFromTexture(ID3D11Texture2D texture, int w, int h, int qp, Guid codec)
    {
        var initFn = GetFuncDelegate<InitEncoderFn>(GetFuncPtr(FnInitializeEncoder));
        // ⚠ 原实现用 13/15/21，实为 DestroyInputBuffer/DestroyBitstreamBuffer/GetEncodeStats
        var registerResFn = GetFuncDelegate<RegisterResourceFn>(GetFuncPtr(FnRegisterResource));
        var mapInputFn = GetFuncDelegate<MapInputResourceFn>(GetFuncPtr(FnMapInputResource));
        var unmapInputFn = GetFuncDelegate<UnmapInputResourceFn>(GetFuncPtr(FnUnmapInputResource));
        var createBufFn = GetFuncDelegate<CreateBitstreamFn>(GetFuncPtr(FnCreateBitstreamBuffer));
        var encFn = GetFuncDelegate<EncodePictureFn>(GetFuncPtr(FnEncodePicture));
        var lockFn = GetFuncDelegate<LockBitstreamFn>(GetFuncPtr(FnLockBitstream));
        var unlockFn = GetFuncDelegate<UnlockBitstreamFn>(GetFuncPtr(FnUnlockBitstream));

        int paramSize = 256 + 512;
        byte* p = stackalloc byte[paramSize];
        new Span<byte>(p, paramSize).Clear();
        *(uint*)(p + 0) = StructVer(_activeVersion, 5, highBit: true);   // NV_ENC_INITIALIZE_PARAMS_VER
        var preset = PresetP1;
        Buffer.MemoryCopy(&codec, p + 8, 16, 16);
        Buffer.MemoryCopy(&preset, p + 24, 16, 16);
        *(uint*)(p + 40) = (uint)w; *(uint*)(p + 44) = (uint)h;
        *(uint*)(p + 48) = (uint)w; *(uint*)(p + 52) = (uint)h;
        *(uint*)(p + 56) = 1; *(uint*)(p + 60) = 1;
        *(nint*)(p + 96) = (nint)(p + 256);
        *(uint*)(p + 128) = 5;
        *(uint*)(p + 256) = StructVer(_activeVersion, 6, highBit: true);   // NV_ENC_CONFIG_VER
        *(uint*)(p + 256 + 20) = (uint)w;
        *(uint*)(p + 256 + 24) = 1;
        *(uint*)(p + 256 + 120) = (uint)qp;
        *(uint*)(p + 256 + 140) = (uint)qp;

        int hr = initFn(_encoder, (nint)p);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] InitEncoder: 0x{hr:X8}");

        // ── Step 1: NvEncRegisterResource ──
        // 注册 D3D11 纹理到 NVENC。结构体布局 (64-bit):
        //   +0:  uint32   version
        //   +4:  uint32   resourceType = 2 (NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX)
        //   +8:  uint32   width
        //   +12: uint32   height
        //   +16: uint32   pitch = 0 (texture)
        //   +20: uint32   bufferFormat = 7 (NV_ENC_BUFFER_FORMAT_ARGB10)
        //   +24: uint32   bufferUsage = 1 (NV_ENC_INPUT_IMAGE)
        //   +28: int64    reserved (padding for pointer alignment)
        //   +32: nint     pResourceToRegister = texture.NativePointer
        //   +40: uint32   flags = 0
        //   +44: uint32   reserved
        //   +48: nint     pRegisteredResource (OUTPUT)
        byte* rr = stackalloc byte[128];
        new Span<byte>(rr, 128).Clear();
        *(uint*)rr = StructVer(_activeVersion, 3);  // NV_ENC_REGISTER_RESOURCE_VER = STRUCT_VERSION(3)
        *(uint*)(rr + 4) = 2;   // NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX
        *(uint*)(rr + 8) = (uint)w;
        *(uint*)(rr + 12) = (uint)h;
        *(uint*)(rr + 16) = 0;  // pitch = 0 for texture
        *(uint*)(rr + 20) = 7;  // NV_ENC_BUFFER_FORMAT_ARGB10
        *(uint*)(rr + 24) = 1;  // NV_ENC_INPUT_IMAGE
        *(nint*)(rr + 32) = texture.NativePointer;
        *(uint*)(rr + 40) = 0;  // flags

        hr = registerResFn(_encoder, (nint)rr);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] RegisterResource: 0x{hr:X8}");
        nint registeredRes = *(nint*)(rr + 48);

        // ── Step 2: NvEncMapInputResource ──
        // 映射已注册的资源，获取 NVENC 输入缓冲区
        //   +0:  uint32   version
        //   +4:  uint32   subResourceIndex = 0
        //   +8:  nint     pRegisteredResource (from step 1)
        //   +16: uint32   flags = 0
        //   +20: uint32   reserved
        //   +24: nint     pMappedBuffer (OUTPUT)
        //   +32: uint32   mappedBufferPitch (OUTPUT)
        byte* mr = stackalloc byte[64];
        new Span<byte>(mr, 64).Clear();
        *(uint*)mr = StructVer(_activeVersion, 4);  // NV_ENC_MAP_INPUT_RESOURCE_VER = STRUCT_VERSION(4)
        *(nint*)(mr + 8) = registeredRes;
        *(uint*)(mr + 16) = 0;  // flags

        hr = mapInputFn(_encoder, (nint)mr);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] MapInputResource: 0x{hr:X8}");
        nint mappedBuffer = *(nint*)(mr + 24);
        int srcPitch = *(int*)(mr + 32);

        // ── Step 3: Create bitstream buffer ──
        int bufSize = w * h * 4;
        byte* bb = stackalloc byte[32];
        new Span<byte>(bb, 32).Clear();
        *(uint*)bb = _activeVersion | NvencStructVersionBits;   // NV_ENC_CREATE_BITSTREAM_BUFFER_VER
        *(uint*)(bb + 4) = (uint)(bufSize * 2);
        nint bsBuf = 0;
        hr = createBufFn(_encoder, (nint)bb, &bsBuf);
        if (hr != NVENC_SUCCESS) { unmapInputFn(_encoder, (nint)mr); throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}"); }

        // ── Step 4: Encode picture ──
        byte* pic = stackalloc byte[128];
        new Span<byte>(pic, 128).Clear();
        *(uint*)pic = StructVer(_activeVersion, 4, highBit: true);   // NV_ENC_PIC_PARAMS_VER
        *(uint*)(pic + 4) = (uint)w; *(uint*)(pic + 8) = (uint)h;
        *(uint*)(pic + 12) = (uint)srcPitch;
        *(uint*)(pic + 16) = NV_ENC_PIC_FLAG_FORCEINTRA | NV_ENC_PIC_FLAG_EOS;
        *(nint*)(pic + 40) = mappedBuffer;
        *(nint*)(pic + 48) = bsBuf;
        *(uint*)(pic + 72) = 7;  // NV_ENC_BUFFER_FORMAT_ARGB10
        hr = encFn(_encoder, (nint)pic);
        if (hr != NVENC_SUCCESS) { unmapInputFn(_encoder, (nint)mr); throw new InvalidOperationException($"[NVENC] EncodePicture: 0x{hr:X8}"); }

        // ── Step 5: Read bitstream ──
        byte* lk = stackalloc byte[64];
        new Span<byte>(lk, 64).Clear();
        *(uint*)lk = _activeVersion | NvencStructVersionBits;   // NV_ENC_LOCK_BITSTREAM_VER
        *(nint*)(lk + 8) = bsBuf;
        hr = lockFn(_encoder, (nint)lk);
        if (hr != NVENC_SUCCESS) { unmapInputFn(_encoder, (nint)mr); throw new InvalidOperationException($"[NVENC] LockBitstream: 0x{hr:X8}"); }
        int bsSize = *(int*)(lk + 24);
        nint bsData = *(nint*)(lk + 16);
        var result = new byte[bsSize];
        Marshal.Copy(bsData, result, 0, bsSize);
        unlockFn(_encoder, (nint)lk);

        // ── Cleanup: UnmapInputResource ──
        unmapInputFn(_encoder, (nint)mr);

        return result;
    }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        // ⚠ 原实现用索引 24，实为 NvEncUnregisterAsyncEvent —— 编码器会话从未被真正销毁，
        // 多次调用后会话累积达驱动上限，后续 OpenEncodeSessionEx 返回 UNSUPPORTED_DEVICE(2)。
        if (_encoder != 0) GetFuncDelegate<DestroyEncoderFn>(GetFuncPtr(FnDestroyEncoder))(_encoder);
        if (_funcTable != 0) Marshal.FreeHGlobal(_funcTable);
        if (_dll != 0) NativeLibrary.Free(_dll);
    }
    private delegate int DestroyEncoderFn(nint enc);
}

/// <summary>AV1 → AVIF (ISOBMFF) 容器写入器。替代旧的 IVF 写入，生成标准 AVIF 文件。</summary>
public static class IvfWriter
{
    public static void WriteAvif(byte[] av1Bs, int w, int h, string path,
        string? colorSpaceTag = null)
    {
        // 解析 AV1 OBU 流提取序列头参数
        var (profile, level, tier, bitDepth, mono, chromaSubX, chromaSubY, chromaPos, seqHeaderObu) = ParseAv1SequenceHeader(av1Bs);

        // ═══ 2026-08-16: colr 属性索引 (0=未写入; >0 时 ipma 关联) ═══
        int colrIndex = 0;

        using var fs = File.Create(path);
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
        WriteBoxHeader(hdlr, "hdlr");
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
        try
        {
            var colr = new MemoryStream();
            WriteBoxHeader(colr, "colr");
            WriteAscii(colr, "nclx");
            byte primaries = TrueToneCap.Core.ColorManagement.ColorSpaceConverter.GetCicpPrimaries(
                colorSpaceTag is not (null or "System" or "sRGB") ? colorSpaceTag : "sRGB");
            colr.WriteByte(primaries);
            colr.WriteByte(13);         // transfer: sRGB
            colr.WriteByte(1);          // matrix: BT.709
            colr.WriteByte(0);          // full_range: 0 (limited)
            WriteBoxToStream(ipco, colr);
            colrIndex = 4;              // 属性索引 4 (av1C=1, ispe=2, pixi=3, colr=4)
        }
        catch
        {
            colrIndex = 0;              // colr 失败 → 不写关联
        }

        WriteBoxToStream(iprp, ipco);

        // ipma box
        var ipma = new MemoryStream();
        WriteFullBoxHeader(ipma, "ipma", 0, 0);
        WriteU32BE(ipma, 1);            // entry_count
        WriteU16BE(ipma, 1);            // item_ID
        // ═══ 2026-08-16: association_count 随 colr 动态 (3 或 4) ═══
        byte assocCount = colrIndex > 0 ? (byte)4 : (byte)3;
        ipma.WriteByte(assocCount);     // association_count (av1C, ispe, pixi[, colr])
        ipma.WriteByte(0x81);           // essential=1, property_index=1 (av1C)
        ipma.WriteByte(0x82);           // essential=1, property_index=2 (ispe)
        ipma.WriteByte(0x03);           // essential=0, property_index=3 (pixi)
        if (colrIndex > 0)
            ipma.WriteByte((byte)(0x80 | colrIndex)); // essential=1, property_index=4 (colr)
        WriteBoxToStream(iprp, ipma);

        WriteBoxToStream(meta, iprp);

        // ═══ 2026-08-16 P2-10a ⚠ 已移除: colr 现在写入 ipco (上方), 不在 meta 直属层 ═══

        // 计算 mdat 数据偏移 (ftyp_size + meta_size + mdat_header_size)
        long ftypSize = ftyp.Length;
        long metaSize = meta.Length + 8; // meta box header (size + type) 已在 FullBoxHeader 中
        // 实际上 meta 已经包含了 header，重新计算
        // meta 流已包含完整 box (header + content)
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
    }

    // ── AV1 OBU 解析 ──

    private static (int profile, int level, int tier, int bitDepth, bool mono, int chromaSubX, int chromaSubY, int chromaPos, byte[]? seqHeaderObu)
        ParseAv1SequenceHeader(byte[] av1Bs)
    {
        // 默认值: Main profile, level 5.1, 8-bit, 4:2:0
        int profile = 0, level = 13, tier = 0, bitDepth = 8, chromaSubX = 1, chromaSubY = 1, chromaPos = 0;
        bool mono = false;
        byte[]? seqHeaderObu = null;

        try
        {
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
                    obuSize = av1Bs.Length - pos;
                }

                if (obuType == 1) // OBU_SEQUENCE_HEADER
                {
                    int seqStart = pos;
                    if (pos + 4 <= av1Bs.Length)
                    {
                        // seq_profile (3 bits) + still_picture (1) + reduced_still_picture_header (1)
                        // + timing_info_present (1) + ...
                        // 简化解析: 读取前几个字节
                        int b0 = av1Bs[pos];
                        profile = (b0 >> 5) & 0x07;

                        // 跳过 timing info 等，找到 frame_width/height 后的 color config
                        // 对于 configOBUs，直接保存整个序列头 OBU（含 OBU header）
                        int obuTotalLen = headerSize + (hasSizeField ? Leb128Size(av1Bs, pos - (hasSizeField ? Leb128Size(av1Bs, pos) : 0)) : 0) + obuSize;
                        // 保存从 OBU header 开始的完整数据（不含 temporal delimiter）
                        int obuHeaderStart = seqStart - headerSize - (hasSizeField ? Leb128Size(av1Bs, seqStart - headerSize) : 0);
                        if (obuHeaderStart >= 0 && obuHeaderStart + headerSize + obuSize <= av1Bs.Length)
                        {
                            // 重新计算: 保存 header + size + payload
                            int fullStart = seqStart - headerSize;
                            // 回退到 size field 开始
                            if (hasSizeField)
                            {
                                int sizeBytes = Leb128Size(av1Bs, fullStart + 1 + (hasExt ? 1 : 0));
                                fullStart -= 0; // size field 紧跟 header
                            }
                            // 简化: 保存从当前 OBU 的 header 字节开始
                            int start = pos - obuSize - (hasSizeField ? Leb128Size(av1Bs, pos - obuSize) : 0) - headerSize;
                            if (start >= 0)
                            {
                                int len = pos + obuSize - start;
                                if (start + len <= av1Bs.Length)
                                {
                                    seqHeaderObu = new byte[len];
                                    Array.Copy(av1Bs, start, seqHeaderObu, 0, len);
                                }
                            }
                        }

                        // 尝试解析 bit depth 和 chroma (简化: 搜索 color_config 模式)
                        // 对于大多数硬件编码器输出，profile 0 = 8-bit 4:2:0
                        if (profile == 0) { bitDepth = 8; chromaSubX = 1; chromaSubY = 1; }
                        else if (profile == 1) { bitDepth = 8; chromaSubX = 0; chromaSubY = 0; }
                        else if (profile == 2) { bitDepth = 10; chromaSubX = 1; chromaSubY = 1; }
                    }
                    break; // 只需要第一个序列头
                }

                pos += obuSize;
            }
        }
        catch { /* 解析失败使用默认值 */ }

        return (profile, level, tier, bitDepth, mono, chromaSubX, chromaSubY, chromaPos, seqHeaderObu);
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
        // ⚠ ISOBMFF (ISO/IEC 14496-12) 规定 offset_size / length_size 只能取:
        //     0 = 32 位, 1 = 64 位, 2 = 16 位
        // 原实现写 0x44（即 offset_size=4, length_size=4）是**无效值** —— 4 不在允许集合内，
        // 且与下方用 WriteU32BE 实际写入的 4 字节长度自相矛盾（4 字节对应编码值 0）。
        // 解码器按 offset_size=4 解析会取到错误长度，导致 NVENC 路径生成的 AVIF 无法解码。
        iloc.WriteByte(0x00); // offset_size=0 (32 位), length_size=0 (32 位)
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
