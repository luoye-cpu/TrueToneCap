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
    private const uint NV_ENC_DEVICE_TYPE_DIRECTX = 2;
    private const uint NV_ENC_INPUT_RESOURCE_TYPE_DIRECTX = 2;
    private const uint NV_ENC_BUFFER_FORMAT_ARGB10 = 7;
    private const uint NV_ENC_PIC_FLAG_FORCEINTRA = 0x01;
    private const uint NV_ENC_PIC_FLAG_EOS = 0x02;

    // ── 支持的 SDK 版本 (NVENCAPI_VERSION = MAJOR | (MINOR << 24)) ──
    // SDK 13.1: 13 | (1 << 24) = 0x0100000D  (Blackwell, driver 570+, latest)
    // SDK 13.0: 13 | (0 << 24) = 0x0000000D  (Blackwell)
    // SDK 12.2: 12 | (2 << 24) = 0x0200000C  (Ada Lovelace)
    // SDK 12.1: 12 | (1 << 24) = 0x0100000C
    // SDK 12.0: 12 | (0 << 24) = 0x0000000C
    // SDK 11.1: 11 | (1 << 24) = 0x0100000B
    // SDK 11.0: 11 | (0 << 24) = 0x0000000B
    // 参考: FFmpeg nv-codec-headers/include/ffnvcodec/nvEncodeAPI.h
    private static readonly uint[] s_sdkVersions = [
        0x0100000D,  // SDK 13.1 (Blackwell, driver 570+)
        0x0000000D,  // SDK 13.0 (Blackwell)
        0x0200000C,  // SDK 12.2 (Ada)
        0x0100000C,  // SDK 12.1
        0x0000000C,  // SDK 12.0
        0x0100000B,  // SDK 11.1
        0x0000000B,  // SDK 11.0
    ];

    // ── GUIDs ──
    internal static readonly Guid CodecAv1 = new(0x4E2599F1, 0x8A4F, 0x4D3A, 0xA5, 0x1A, 0xDD, 0x81, 0x80, 0x00, 0x9E, 0x06);
    private static readonly Guid CodecHevc = new(0x8AEDB2E3, 0x5A8E, 0x4EFC, 0x8E, 0xC4, 0x03, 0xAC, 0x94, 0xDE, 0x6F, 0x56);
    private static readonly Guid PresetP1 = new(0x61C29E14, 0x6AB9, 0x4B2E, 0x9A, 0x3F, 0xC9, 0x13, 0xE8, 0x92, 0xD0, 0x9D);

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

    /// <summary>完整可用性检查：DLL 存在 + 能创建设备。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!IsDllPresent) return false;
            try
            {
                var device = D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
                using var nv = new NvEncoderNative(device);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NVENC] IsAvailable 探测失败: {ex.Message}");
                return false;
            }
        }
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
                    *(uint*)table = ver;
                    int hr = create(table);
                    if (hr == NVENC_SUCCESS)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NVENC] ProbeApiVersion: {ver>>16 & 0xFF}.{ver>>8 & 0xFF}");
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
            *(uint*)_funcTable = ver;
            int hr = create(_funcTable);
            System.Diagnostics.Debug.WriteLine($"[NVENC]   SDK {ver>>16 & 0xFF}.{ver>>8 & 0xFF:00}: hr=0x{hr:X8} {(hr == NVENC_SUCCESS ? "✓" : "✗")}");
            if (hr == NVENC_SUCCESS)
            {
                _activeVersion = ver;
                created = true;
                diag($"[NVENC] ✅ CreateInstance OK (SDK {ver>>16 & 0xFF}.{ver>>8 & 0xFF})");
                break;
            }
            lastHr = hr;
        }

        if (!created)
            throw new InvalidOperationException(
                $"[NVENC] 所有 SDK 版本探测失败 (最后 hr=0x{lastHr:X8})。驱动可能不支持 NVENC。");

        var openFn = Marshal.GetDelegateForFunctionPointer<OpenSessionFn>(GetFuncPtr(29));
        nint enc = 0;

        // NvEncOpenEncodeSessionEx 需要 NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS 结构体
        byte* osp = stackalloc byte[1024];
        new Span<byte>(osp, 1024).Clear();
        // NVENCAPI_STRUCT_VERSION(1) = NVENCAPI_VERSION | (1 << 16) | (0x7 << 28)
        *(uint*)osp = _activeVersion | (1u << 16) | (0x7u << 28);
        *(uint*)(osp + 4) = NV_ENC_DEVICE_TYPE_DIRECTX;
        *(nint*)(osp + 8) = _d3dDevice.NativePointer;
        *(uint*)(osp + 24) = _activeVersion;

        int hr2 = openFn((nint)osp, &enc);
        if (hr2 != NVENC_SUCCESS)
            throw new InvalidOperationException($"[NVENC] OpenEncodeSessionEx 失败: 0x{hr2:X8}");
        diag("[NVENC] ✅ 编码会话 OK (AV1)");

        _encoder = enc;
    }

    // ═══════════ 编码 ═══════════

    public byte[] EncodeAv1(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecAv1);

    public byte[] EncodeHevc(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecHevc);

    private byte[] EncodeRaw(byte[] bgra, int w, int h, int qp, Guid codec)
    {
        var initFn = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(GetFuncPtr(11)); // NvEncInitializeEncoder
        var createBufFn = Marshal.GetDelegateForFunctionPointer<CreateBitstreamFn>(GetFuncPtr(14)); // NvEncCreateBitstreamBuffer
        var encFn = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(GetFuncPtr(16)); // NvEncEncodePicture
        var lockFn = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(GetFuncPtr(17)); // NvEncLockBitstream
        var unlockFn = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(GetFuncPtr(18)); // NvEncUnlockBitstream

        int paramSize = 256 + 512;
        byte* p = stackalloc byte[paramSize];
        new Span<byte>(p, paramSize).Clear();

        *(uint*)(p + 0) = _activeVersion;
        var preset = PresetP1;
        Buffer.MemoryCopy(&codec, p + 8, 16, 16);
        Buffer.MemoryCopy(&preset, p + 24, 16, 16);
        *(uint*)(p + 40) = (uint)w; *(uint*)(p + 44) = (uint)h;
        *(uint*)(p + 48) = (uint)w; *(uint*)(p + 52) = (uint)h;
        *(uint*)(p + 56) = 1; *(uint*)(p + 60) = 1;
        *(nint*)(p + 96) = (nint)(p + 256);
        *(uint*)(p + 128) = 5; // UHQ Tuning (NV_ENC_TUNING_INFO_ULTRA_HIGH_QUALITY, Blackwell SDK 13.0+)

        *(uint*)(p + 256) = _activeVersion;
        *(uint*)(p + 256 + 20) = (uint)w;
        *(uint*)(p + 256 + 24) = 1;
        *(uint*)(p + 256 + 120) = (uint)qp;
        *(uint*)(p + 256 + 140) = (uint)qp;

        int hr = initFn(_encoder, (nint)p);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] InitEncoder: 0x{hr:X8}");

        int bufSize = w * h * 4;
        byte* ib = stackalloc byte[64];
        new Span<byte>(ib, 64).Clear();
        *(uint*)ib = _activeVersion;
        *(uint*)(ib + 4) = (uint)bufSize;
        nint inputBuf = 0;
        hr = GetFuncDelegate<CreateInputBufferFn>(GetFuncPtr(12))(_encoder, (nint)ib, &inputBuf); // NvEncCreateInputBuffer
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateInputBuf: 0x{hr:X8}");

        byte* lb = stackalloc byte[64];
        new Span<byte>(lb, 64).Clear();
        *(uint*)lb = _activeVersion;
        *(nint*)(lb + 8) = inputBuf;
        hr = GetFuncDelegate<LockInputBufferFn>(GetFuncPtr(19))(_encoder, (nint)lb); // NvEncLockInputBuffer
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] LockInput: 0x{hr:X8}");
        nint srcData = *(nint*)(lb + 16);
        int srcPitch = *(int*)(lb + 24);
        for (int y = 0; y < h; y++)
            Marshal.Copy(bgra, y * w * 4, srcData + y * srcPitch, w * 4);
        GetFuncDelegate<UnlockInputBufferFn>(GetFuncPtr(20))(_encoder, (nint)lb); // NvEncUnlockInputBuffer

        byte* bb = stackalloc byte[32];
        new Span<byte>(bb, 32).Clear();
        *(uint*)bb = _activeVersion;
        *(uint*)(bb + 4) = (uint)(bufSize * 2);
        nint bsBuf = 0;
        hr = createBufFn(_encoder, (nint)bb, &bsBuf);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}");

        byte* pic = stackalloc byte[128];
        new Span<byte>(pic, 128).Clear();
        *(uint*)pic = _activeVersion;
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
        *(uint*)lk = _activeVersion;
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

    private nint GetFuncPtr(int idx) => *(nint*)(_funcTable + 8 + idx * 8);
    private static T GetFuncDelegate<T>(nint ptr) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(ptr);

    private delegate int CreateInstanceDelegate(nint funcList);
    private delegate int OpenSessionFn(nint params_, nint* encoder);
    private delegate int InitEncoderFn(nint enc, nint p);
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
        var initFn = GetFuncDelegate<InitEncoderFn>(GetFuncPtr(11));
        var registerResFn = GetFuncDelegate<RegisterResourceFn>(GetFuncPtr(13));  // NvEncRegisterResource
        var mapInputFn = GetFuncDelegate<MapInputResourceFn>(GetFuncPtr(15));     // NvEncMapInputResource
        var unmapInputFn = GetFuncDelegate<UnmapInputResourceFn>(GetFuncPtr(21)); // NvEncUnmapInputResource
        var createBufFn = GetFuncDelegate<CreateBitstreamFn>(GetFuncPtr(14));
        var encFn = GetFuncDelegate<EncodePictureFn>(GetFuncPtr(16));
        var lockFn = GetFuncDelegate<LockBitstreamFn>(GetFuncPtr(17));
        var unlockFn = GetFuncDelegate<UnlockBitstreamFn>(GetFuncPtr(18));

        int paramSize = 256 + 512;
        byte* p = stackalloc byte[paramSize];
        new Span<byte>(p, paramSize).Clear();
        *(uint*)(p + 0) = _activeVersion;
        var preset = PresetP1;
        Buffer.MemoryCopy(&codec, p + 8, 16, 16);
        Buffer.MemoryCopy(&preset, p + 24, 16, 16);
        *(uint*)(p + 40) = (uint)w; *(uint*)(p + 44) = (uint)h;
        *(uint*)(p + 48) = (uint)w; *(uint*)(p + 52) = (uint)h;
        *(uint*)(p + 56) = 1; *(uint*)(p + 60) = 1;
        *(nint*)(p + 96) = (nint)(p + 256);
        *(uint*)(p + 128) = 5;
        *(uint*)(p + 256) = _activeVersion;
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
        *(uint*)rr = _activeVersion | (1u << 16) | (0x7u << 28);  // NV_ENC_REGISTER_RESOURCE_VER
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
        *(uint*)mr = _activeVersion | (1u << 16) | (0x7u << 28);  // NV_ENC_MAP_INPUT_RESOURCE_VER
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
        *(uint*)bb = _activeVersion;
        *(uint*)(bb + 4) = (uint)(bufSize * 2);
        nint bsBuf = 0;
        hr = createBufFn(_encoder, (nint)bb, &bsBuf);
        if (hr != NVENC_SUCCESS) { unmapInputFn(_encoder, (nint)mr); throw new InvalidOperationException($"[NVENC] CreateBitstream: 0x{hr:X8}"); }

        // ── Step 4: Encode picture ──
        byte* pic = stackalloc byte[128];
        new Span<byte>(pic, 128).Clear();
        *(uint*)pic = _activeVersion;
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
        *(uint*)lk = _activeVersion;
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
        if (_encoder != 0) GetFuncDelegate<DestroyEncoderFn>(GetFuncPtr(24))(_encoder); // NvEncDestroyEncoder
        if (_funcTable != 0) Marshal.FreeHGlobal(_funcTable);
        if (_dll != 0) NativeLibrary.Free(_dll);
    }
    private delegate int DestroyEncoderFn(nint enc);
}

/// <summary>AV1 → AVIF (ISOBMFF) 容器写入器。替代旧的 IVF 写入，生成标准 AVIF 文件。</summary>
public static class IvfWriter
{
    public static void WriteAvif(byte[] av1Bs, int w, int h, string path)
    {
        // 解析 AV1 OBU 流提取序列头参数
        var (profile, level, tier, bitDepth, mono, chromaSubX, chromaSubY, chromaPos, seqHeaderObu) = ParseAv1SequenceHeader(av1Bs);

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
        WriteBoxToStream(meta, ilocPlaceholder);

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
        var av1c = new MemoryStream();
        WriteBoxHeader(av1c, "av1C");
        byte byte0 = (byte)(0x80 | (profile & 0x07) << 5 | (level & 0x1F));
        byte byte1 = (byte)((tier << 7) | ((bitDepth > 8 ? 1 : 0) << 6) | ((bitDepth == 12 ? 1 : 0) << 5) |
                            ((mono ? 1 : 0) << 4) | (chromaSubX << 3) | (chromaSubY << 2) | (chromaPos & 0x03));
        av1c.WriteByte(byte0);
        av1c.WriteByte(byte1);
        av1c.WriteByte(0x00);           // initial_presentation_delay_present=0, reserved
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

        WriteBoxToStream(iprp, ipco);

        // ipma box
        var ipma = new MemoryStream();
        WriteFullBoxHeader(ipma, "ipma", 0, 0);
        WriteU32BE(ipma, 1);            // entry_count
        WriteU16BE(ipma, 1);            // item_ID
        ipma.WriteByte(3);              // association_count (av1C, ispe, pixi)
        ipma.WriteByte(0x81);           // essential=1, property_index=1 (av1C)
        ipma.WriteByte(0x82);           // essential=1, property_index=2 (ispe)
        ipma.WriteByte(0x03);           // essential=0, property_index=3 (pixi)
        WriteBoxToStream(iprp, ipma);

        WriteBoxToStream(meta, iprp);

        // 计算 mdat 数据偏移 (ftyp_size + meta_size + mdat_header_size)
        long ftypSize = ftyp.Length;
        long metaSize = meta.Length + 8; // meta box header (size + type) 已在 FullBoxHeader 中
        // 实际上 meta 已经包含了 header，重新计算
        // meta 流已包含完整 box (header + content)
        long mdatHeaderSize = 8; // size(4) + type(4)
        long mdatDataOffset = ftypSize + (meta.Length) + mdatHeaderSize;

        // 回填 iloc 中的偏移
        PatchIlocOffset(meta, ilocPlaceholder, mdatDataOffset);

        WriteBoxToStream(bw, meta);

        // ═══ mdat box ═══
        bw.Write((uint)(8 + av1Bs.Length)); // box size (big-endian)
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

    private static void WriteBoxToStream(Stream parent, MemoryStream box)
    {
        long size = box.Length;
        box.Position = 0;
        box.WriteByte((byte)(size >> 24));
        box.WriteByte((byte)(size >> 16));
        box.WriteByte((byte)(size >> 8));
        box.WriteByte((byte)(size));
        box.Position = 0;
        box.CopyTo(parent);
    }

    private static MemoryStream BuildIlocBox(long dataOffset, int dataLength)
    {
        var iloc = new MemoryStream();
        WriteFullBoxHeader(iloc, "iloc", 0, 0);
        // offset_size=4, length_size=4, base_offset_size=0, reserved=0
        iloc.WriteByte(0x44); // (4<<4)|4
        iloc.WriteByte(0x00); // base_offset_size=0, reserved=0
        WriteU16BE(iloc, 1);  // item_count
        WriteU16BE(iloc, 1);  // item_ID
        WriteU16BE(iloc, 0);  // data_reference_index
        WriteU16BE(iloc, 1);  // extent_count
        WriteU32BE(iloc, (uint)dataOffset);  // extent_offset (placeholder)
        WriteU32BE(iloc, (uint)dataLength);  // extent_length
        return iloc;
    }

    private static void PatchIlocOffset(MemoryStream meta, MemoryStream ilocBox, long actualOffset)
    {
        // 在 meta 流中找到 iloc 的 extent_offset 字段并 patch
        // iloc 结构: [size(4)][type(4)][version(1)][flags(3)][byte(1)][byte(1)][item_count(2)]
        //            [item_ID(2)][data_ref(2)][extent_count(2)][extent_offset(4)][extent_length(4)]
        // 从 iloc box 开头偏移: 4+4+1+3+1+1+2+2+2+2 = 22 字节到 extent_offset
        byte[] metaBytes = meta.GetBuffer();
        // 搜索 iloc box 在 meta 中的位置
        byte[] ilocSig = { (byte)'i', (byte)'l', (byte)'o', (byte)'c' };
        for (int i = 4; i < (int)meta.Length - 4; i++)
        {
            if (metaBytes[i] == ilocSig[0] && metaBytes[i + 1] == ilocSig[1] &&
                metaBytes[i + 2] == ilocSig[2] && metaBytes[i + 3] == ilocSig[3])
            {
                // iloc type 在 i, 内容从 i+4 开始
                // version(1)+flags(3)+sizes(2)+item_count(2)+item_ID(2)+data_ref(2)+extent_count(2) = 12
                int offsetPos = i + 4 + 12;
                if (offsetPos + 4 <= (int)meta.Length)
                {
                    metaBytes[offsetPos] = (byte)(actualOffset >> 24);
                    metaBytes[offsetPos + 1] = (byte)(actualOffset >> 16);
                    metaBytes[offsetPos + 2] = (byte)(actualOffset >> 8);
                    metaBytes[offsetPos + 3] = (byte)(actualOffset);
                }
                break;
            }
        }
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
