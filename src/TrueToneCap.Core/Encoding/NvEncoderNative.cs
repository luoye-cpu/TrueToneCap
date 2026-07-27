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
    private delegate int CreateBitstreamFn(nint enc, nint p, nint* buf);
    private delegate int EncodePictureFn(nint enc, nint p);
    private delegate int LockBitstreamFn(nint enc, nint p);
    private delegate int UnlockBitstreamFn(nint enc, nint p);

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_encoder != 0) GetFuncDelegate<DestroyEncoderFn>(GetFuncPtr(27))(_encoder); // NvEncDestroyEncoder
        if (_funcTable != 0) Marshal.FreeHGlobal(_funcTable);
        if (_dll != 0) NativeLibrary.Free(_dll);
    }
    private delegate int DestroyEncoderFn(nint enc);
}

/// <summary>AV1 IVF 容器写入器。</summary>
public static class IvfWriter
{
    public static void WriteAvif(byte[] av1Bs, int w, int h, string path)
    {
        using var fs = File.Create(path);
        using var bw = new System.IO.BinaryWriter(fs);
        bw.Write((short)0x4649); bw.Write((short)0x5649); // DKIF
        bw.Write((short)0); bw.Write((short)32);
        bw.Write(0x41563031); // AV01
        bw.Write((short)w); bw.Write((short)h);
        bw.Write(1u); bw.Write(1u);
        bw.Write(1u); bw.Write(0u);
        bw.Write((uint)av1Bs.Length); bw.Write(0L);
        bw.Write(av1Bs);
    }
}
