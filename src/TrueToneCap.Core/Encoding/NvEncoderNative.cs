// TrueToneCap.Core/Encoding/NvEncoderNative.cs
// NVENC 原生 SDK — P/Invoke nvEncodeAPI64.dll (NVIDIA 驱动自带，零依赖)
// D3D11 纹理直接输入，无需 CUDA 上下文

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

    // ── GUIDs ──
    internal static readonly Guid CodecAv1 = new(0x4E2599F1, 0x8A4F, 0x4D3A, 0xA5, 0x1A, 0xDD, 0x81, 0x80, 0x00, 0x9E, 0x06);
    private static readonly Guid CodecHevc = new(0x8AEDB2E3, 0x5A8E, 0x4EFC, 0x8E, 0xC4, 0x03, 0xAC, 0x94, 0xDE, 0x6F, 0x56);
    private static readonly Guid PresetP1 = new(0x61C29E14, 0x6AB9, 0x4B2E, 0x9A, 0x3F, 0xC9, 0x13, 0xE8, 0x92, 0xD0, 0x9D);

    private readonly nint _dll;
    private readonly nint _funcTable; // NV_ENCODE_API_FUNCTION_LIST*
    private nint _encoder;            // NV_ENC_HANDLE
    private readonly ID3D11Device _d3dDevice;
    private bool _disposed;

    public static bool IsAvailable
    {
        get
        {
            try { var h = NativeLibrary.Load("nvEncodeAPI64.dll"); NativeLibrary.Free(h); return true; }
            catch { return false; }
        }
    }

    public NvEncoderNative(ID3D11Device d3dDevice)
    {
        _d3dDevice = d3dDevice;
        _dll = NativeLibrary.Load("nvEncodeAPI64.dll");

        var createFn = NativeLibrary.GetExport(_dll, "NvEncodeAPICreateInstance");
        var create = Marshal.GetDelegateForFunctionPointer<CreateInstanceDelegate>(createFn);

        // 函数表 ~58 指针 = 464 字节 + version header
        _funcTable = Marshal.AllocHGlobal(512);
        new Span<byte>((void*)_funcTable, 512).Clear();
        *(uint*)_funcTable = 0x000C0000; // SDK 12.0

        int hr = create(_funcTable);
        if (hr != NVENC_SUCCESS)
            throw new InvalidOperationException($"NVENC 初始化失败: 0x{hr:X8}");

        // 打开 D3D11 编码会话
        var openFn = Marshal.GetDelegateForFunctionPointer<OpenSessionFn>(
            GetFuncPtr(35)); // NvEncOpenEncodeSessionEx
        nint enc = 0;
        var guid = NvEncoderNative.CodecAv1; // 使用 internal 副本
        hr = openFn(_funcTable, &guid, NV_ENC_DEVICE_TYPE_DIRECTX, _d3dDevice.NativePointer, &enc);
        if (hr != NVENC_SUCCESS)
            throw new InvalidOperationException($"NVENC D3D11 会话失败: 0x{hr:X8}");
        _encoder = enc;
    }

    // ═══════════ 编码 ═══════════

    /// <summary>将 BGRA 像素编码为 AV1 比特流。</summary>
    public byte[] EncodeAv1(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecAv1);

    /// <summary>将 BGRA 像素编码为 HEVC 比特流。</summary>
    public byte[] EncodeHevc(byte[] bgra, int w, int h, int qp)
        => EncodeRaw(bgra, w, h, qp, CodecHevc);

    private byte[] EncodeRaw(byte[] bgra, int w, int h, int qp, Guid codec)
    {
        var initFn = Marshal.GetDelegateForFunctionPointer<InitEncoderFn>(GetFuncPtr(13));
        var createBufFn = Marshal.GetDelegateForFunctionPointer<CreateBitstreamFn>(GetFuncPtr(17));
        var encFn = Marshal.GetDelegateForFunctionPointer<EncodePictureFn>(GetFuncPtr(19));
        var lockFn = Marshal.GetDelegateForFunctionPointer<LockBitstreamFn>(GetFuncPtr(20));
        var unlockFn = Marshal.GetDelegateForFunctionPointer<UnlockBitstreamFn>(GetFuncPtr(21));

        // ── 1. 初始化编码器 ──
        int paramSize = 256 + 512; // NV_ENC_INITIALIZE_PARAMS + NV_ENC_CONFIG
        byte* p = stackalloc byte[paramSize];
        new Span<byte>(p, paramSize).Clear();

        *(uint*)(p + 0) = 0x000C0000; // version
        var preset = PresetP1;
        Buffer.MemoryCopy(&codec, p + 8, 16, 16);
        Buffer.MemoryCopy(&preset, p + 24, 16, 16);
        *(uint*)(p + 40) = (uint)w; *(uint*)(p + 44) = (uint)h;
        *(uint*)(p + 48) = (uint)w; *(uint*)(p + 52) = (uint)h;
        *(uint*)(p + 56) = 1; *(uint*)(p + 60) = 1; // framerate
        *(nint*)(p + 96) = (nint)(p + 256); // encodeConfig 指针

        // NV_ENC_CONFIG @ offset 256
        *(uint*)(p + 256) = 0x000C0000;
        *(uint*)(p + 256 + 20) = (uint)w;       // gopLength
        *(uint*)(p + 256 + 24) = 1;             // frameIntervalP
        *(uint*)(p + 256 + 120) = (uint)qp;     // QP
        *(uint*)(p + 256 + 140) = (uint)qp;     // QP I-frame

        int hr = initFn(_encoder, (nint)p);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC Init: 0x{hr:X8}");

        // ── 2. 创建输入缓冲区 ──
        int bufSize = w * h * 4;
        byte* ib = stackalloc byte[64];
        new Span<byte>(ib, 64).Clear();
        *(uint*)ib = 0x000C0000;
        *(uint*)(ib + 4) = (uint)bufSize;
        nint inputBuf = 0;
        hr = GetFuncDelegate<CreateInputBufferFn>(GetFuncPtr(15))(_encoder, (nint)ib, &inputBuf);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC CreateInputBuf: 0x{hr:X8}");

        // ── 3. 写入像素到输入缓冲区 ──
        byte* lb = stackalloc byte[64];
        new Span<byte>(lb, 64).Clear();
        *(uint*)lb = 0x000C0000;
        *(nint*)(lb + 8) = inputBuf;
        hr = GetFuncDelegate<LockInputBufferFn>(GetFuncPtr(22))(_encoder, (nint)lb);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC LockInput: 0x{hr:X8}");
        nint srcData = *(nint*)(lb + 16);
        int srcPitch = *(int*)(lb + 24);
        // 复制 BGRA: 逐行
        for (int y = 0; y < h; y++)
            Marshal.Copy(bgra, y * w * 4, srcData + y * srcPitch, w * 4);
        GetFuncDelegate<UnlockInputBufferFn>(GetFuncPtr(23))(_encoder, (nint)lb);

        // ── 4. 创建比特流缓冲区 ──
        byte* bb = stackalloc byte[32];
        new Span<byte>(bb, 32).Clear();
        *(uint*)bb = 0x000C0000;
        *(uint*)(bb + 4) = (uint)(bufSize * 2);
        nint bsBuf = 0;
        hr = createBufFn(_encoder, (nint)bb, &bsBuf);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC CreateBS: 0x{hr:X8}");

        // ── 5. 编码一帧 ──
        byte* pic = stackalloc byte[128];
        new Span<byte>(pic, 128).Clear();
        *(uint*)pic = 0x000C0000;
        *(uint*)(pic + 4) = (uint)w; *(uint*)(pic + 8) = (uint)h;
        *(uint*)(pic + 12) = (uint)srcPitch;
        *(uint*)(pic + 16) = NV_ENC_PIC_FLAG_FORCEINTRA | NV_ENC_PIC_FLAG_EOS;
        *(nint*)(pic + 40) = inputBuf;
        *(nint*)(pic + 48) = bsBuf;
        *(uint*)(pic + 72) = NV_ENC_BUFFER_FORMAT_ARGB10;
        hr = encFn(_encoder, (nint)pic);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC Encode: 0x{hr:X8}");

        // ── 6. 读取比特流 ──
        byte* lk = stackalloc byte[64];
        new Span<byte>(lk, 64).Clear();
        *(uint*)lk = 0x000C0000;
        *(nint*)(lk + 8) = bsBuf;
        hr = lockFn(_encoder, (nint)lk);
        if (hr != NVENC_SUCCESS) throw new InvalidOperationException($"NVENC LockBS: 0x{hr:X8}");
        int bsSize = *(int*)(lk + 24);
        nint bsData = *(nint*)(lk + 16);
        var result = new byte[bsSize];
        Marshal.Copy(bsData, result, 0, bsSize);
        unlockFn(_encoder, (nint)lk);

        return result;
    }

    // ═══════════ 辅助 ═══════════

    private nint GetFuncPtr(int idx) => *(nint*)(_funcTable + 8 + idx * 8);
    private static T GetFuncDelegate<T>(nint ptr) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(ptr);

    private delegate int CreateInstanceDelegate(nint funcList);
    private delegate int OpenSessionFn(nint fnList, Guid* guid, uint type, nint device, nint* enc);
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
        if (_encoder != 0) GetFuncDelegate<DestroyEncoderFn>(GetFuncPtr(14))(_encoder);
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
        // IVF header
        bw.Write((short)0x4649); bw.Write((short)0x5649); // "DKIF"
        bw.Write((short)0); bw.Write((short)32);          // version, header len
        bw.Write(0x41563031);                               // "AV01"
        bw.Write((short)w); bw.Write((short)h);
        bw.Write(1u); bw.Write(1u);                        // fps
        bw.Write(1u); bw.Write(0u);                        // frame count, reserved
        // Frame
        bw.Write((uint)av1Bs.Length); bw.Write(0L);        // size, timestamp
        bw.Write(av1Bs);
    }
}
