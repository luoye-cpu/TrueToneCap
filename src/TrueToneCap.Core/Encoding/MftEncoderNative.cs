// TrueToneCap.Core/Encoding/MftEncoderNative.cs
// Windows Media Foundation MFT 编码器 — 系统内置 AV1/HEVC 硬件编码器（非 Intel QSV 专属）
// Windows 11 24H2+ 内置 AV1/HEVC MFT

using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace TrueToneCap.Core.Encoding;

/// <summary>Media Foundation MFT 编码器 — 使用系统内置硬件编码器 (Intel QSV / NVIDIA / AMD)。</summary>
public sealed unsafe class MftEncoderNative : IDisposable
{
    // ── MFT CLSIDs ──
    private static readonly Guid CLSID_AV1EncMFT = new(0x7A5E5A7B, 0x7B3F, 0x4C8E, 0x9F, 0x8A, 0x73, 0x7B, 0x6E, 0x4C, 0x6E, 0x7A);
    private static readonly Guid CLSID_HEVCEncMFT = new(0xACCC0B8B, 0x6E3F, 0x4B4A, 0x9E, 0xC0, 0x86, 0x6B, 0x9D, 0x4F, 0xA5, 0x7E);
    private static readonly Guid MF_MT_FRAME_SIZE = new(0x1652C33D, 0xD6B2, 0x4012, 0xB8, 0x34, 0x72, 0x03, 0x08, 0x49, 0xA3, 0x7D);
    private static readonly Guid MF_MT_AVG_BITRATE = new(0x20332624, 0xFB4D, 0x4D9E, 0xBA, 0x04, 0x8E, 0x9D, 0xAC, 0x8A, 0x45, 0x66);
    private static readonly Guid MF_MT_SUBTYPE = new(0xF7E34C9A, 0x42E8, 0x4714, 0xB7, 0x4B, 0xCB, 0x29, 0xD7, 0x2C, 0x35, 0xE5);
    private static readonly Guid MF_MT_FRAME_RATE = new(0xC459A2E8, 0x3D2C, 0x4E44, 0xB1, 0x32, 0xFE, 0xE5, 0x15, 0x6A, 0x7B, 0xD0);
    private static readonly Guid MF_MT_INTERLACE_MODE = new(0xE2724BB8, 0xE676, 0x4806, 0xB4, 0x8A, 0xA8, 0xD6, 0xEF, 0xB4, 0x4D, 0x3F);
    private static readonly Guid MF_SA_D3D11_AWARE = new(0x206B4FC8, 0xFCF9, 0x4C51, 0xAF, 0xE3, 0x97, 0x64, 0x36, 0x9E, 0x33, 0xA8);

    // ── NV12 子类型 ──
    private static readonly Guid MFVideoFormat_NV12 = new(0x3231564E, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71);

    private nint _mft; // IMFTransform*
    private readonly int _width, _height;
    private bool _disposed;

    /// <summary>检测系统是否存在 Intel GPU（遍历所有适配器，非仅 #0）。</summary>
    public static bool IsIntelGpuAvailable
    {
        get
        {
            try
            {
                using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory7>();
                for (uint i = 0; ; i++)
                {
                    if (factory.EnumAdapters1(i, out var a).Failure || a is null) break;
                    try
                    {
                        if (a.Description.VendorId == 0x8086) return true;
                    }
                    finally { a.Dispose(); }
                }
                return false;
            }
            catch { return false; }
        }
    }

    /// <summary>检测系统 MFT 编码器是否可用。</summary>
    public static bool IsAv1MftAvailable
    {
        get
        {
            try
            {
                // 尝试 CoCreateInstance
                var c = CLSID_AV1EncMFT; var i = typeof(IMFTransform).GUID;
                int hr = CoCreateInstance(ref c, 0, 1, ref i, out nint mft);
                if (hr >= 0 && mft != 0) { Marshal.Release(mft); return true; }
                return false;
            }
            catch { return false; }
        }
    }

    public MftEncoderNative(int width, int height, bool useAv1 = true)
    {
        _width = width; _height = height;
        var clsid = useAv1 ? CLSID_AV1EncMFT : CLSID_HEVCEncMFT;
        var iid = typeof(IMFTransform).GUID;
        int hr = CoCreateInstance(ref clsid, 0, 1, ref iid, out _mft);
        if (hr < 0 || _mft == 0)
            throw new InvalidOperationException($"MFT 编码器创建失败: 0x{hr:X8}");

        ConfigureMft(useAv1);
    }

    private void ConfigureMft(bool av1)
    {
        // 设置输入类型: NV12, WxH
        nint inType;
        MFCreateMediaType(&inType);
        MFSetGUID(inType, MF_MT_SUBTYPE, MFVideoFormat_NV12);
        ulong frameSize = ((ulong)_height << 32) | (uint)_width;
        MFSetUINT64(inType, MF_MT_FRAME_SIZE, frameSize);
        MFSetUINT32(inType, MF_MT_INTERLACE_MODE, 2); // MFVideoInterlace_Progressive
        ulong fps = 1; // (1<<32) | 1
        MFSetUINT64(inType, MF_MT_FRAME_RATE, fps);

        SetInputType(0, inType, 0);
        Marshal.Release(inType);

        // 设置输出类型
        nint outType;
        MFCreateMediaType(&outType);
        MFSetGUID(outType, MF_MT_SUBTYPE, av1 ? new Guid(0x31305641, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71) : MFVideoFormat_NV12);
        MFSetUINT64(outType, MF_MT_FRAME_SIZE, frameSize);
        MFSetUINT32(outType, MF_MT_AVG_BITRATE, 10_000_000); // 10 Mbps 高质量

        SetOutputType(0, outType, 0);
        Marshal.Release(outType);
    }

    /// <summary>编码 BGRA 像素为 AV1/HEVC 比特流。</summary>
    public byte[] Encode(byte[] bgra)
    {
        // BGRA → NV12 转换
        var nv12 = BgraToNv12(bgra, _width, _height);

        // 创建输入采样
        nint sample;
        MFCreateSample(&sample);

        nint buffer;
        MFCreateMemoryBuffer((uint)nv12.Length, &buffer);
        nint data;
        MFMapLock(buffer, &data, null, null);
        Marshal.Copy(nv12, 0, data, nv12.Length);
        MFUnlock(buffer);
        MFSampleAddBuffer(sample, buffer);
        Marshal.Release(buffer);

        // 处理输入
        int hr = ProcessInput(0, sample, 0);
        Marshal.Release(sample);
        if (hr < 0) throw new InvalidOperationException($"MFT ProcessInput: 0x{hr:X8}");

        // 发送 drain 命令
        ProcessMessage(0x00000004, 0); // MFT_MESSAGE_COMMAND_DRAIN

        // 获取输出
        return GetOutput();
    }

    private byte[] GetOutput()
    {
        nint outSample;
        MFCreateSample(&outSample);

        nint outBuffer;
        MFCreateMemoryBuffer(0, &outBuffer); // 自动分配
        MFSampleAddBuffer(outSample, outBuffer);
        Marshal.Release(outBuffer);

        uint status = 0;
        int hr = IMFTransform_ProcessOutput(_mft, 0, 1, outSample, &status);
        if (hr < 0) { Marshal.Release(outSample); throw new InvalidOperationException($"MFT ProcessOutput: 0x{hr:X8}"); }

        // 从输出采样读取数据
        nint bsBuf;
        MFSampleGetBufferByIndex(outSample, 0, &bsBuf);
        uint bsSize;
        nint bsData;
        MFMapLock(bsBuf, &bsData, null, &bsSize);
        var result = new byte[bsSize];
        Marshal.Copy(bsData, result, 0, (int)bsSize);
        MFUnlock(bsBuf);
        Marshal.Release(bsBuf);
        Marshal.Release(outSample);

        return result;
    }

    // ═══════════ BGRA→NV12 ═══════════
    private static byte[] BgraToNv12(byte[] bgra, int w, int h)
    {
        var nv12 = new byte[w * h + (w * h / 2)]; // Y plane + UV plane
        int yOff = 0, uvOff = w * h;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w + x) * 4;
                byte b = bgra[idx], g = bgra[idx + 1], r = bgra[idx + 2];
                // BT.601 Y
                nv12[yOff + y * w + x] = (byte)Math.Clamp((66 * r + 129 * g + 25 * b + 128) >> 8, 0, 255);
                // UV subsampled 4:2:0
                if (y % 2 == 0 && x % 2 == 0)
                {
                    int uvIdx = uvOff + (y / 2) * w + (x & ~1);
                    nv12[uvIdx] = (byte)Math.Clamp((-38 * r - 74 * g + 112 * b + 128) >> 8 + 128, 0, 255);     // U
                    nv12[uvIdx + 1] = (byte)Math.Clamp((112 * r - 94 * g - 18 * b + 128) >> 8 + 128, 0, 255);   // V
                }
            }
        }
        return nv12;
    }

    // ═══════════ COM 互操作 ═══════════
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint ctx, [In] ref Guid iid, out nint ppv);
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(nint* ppType);
    [DllImport("mfplat.dll")] private static extern int MFSetGUID(nint type, Guid key, Guid val);
    [DllImport("mfplat.dll")] private static extern int MFSetUINT64(nint type, Guid key, ulong val);
    [DllImport("mfplat.dll")] private static extern int MFSetUINT32(nint type, Guid key, uint val);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(nint* ppSample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint cbMax, nint* ppBuf);
    [DllImport("mfplat.dll")] private static extern int MFMapLock(nint buf, nint* ppData, uint* pcbMax, uint* pcbCur);
    [DllImport("mfplat.dll")] private static extern int MFUnlock(nint buf);
    [DllImport("mfplat.dll")] private static extern int MFSampleAddBuffer(nint sample, nint buf);
    [DllImport("mfplat.dll")] private static extern int MFSampleGetBufferByIndex(nint sample, uint idx, nint* ppBuf);

    private void SetInputType(uint idx, nint type, uint flags) { var v = typeof(IMFTransform); IMFTransform_SetInputType(_mft, idx, type, flags); }
    private void SetOutputType(uint idx, nint type, uint flags) { IMFTransform_SetOutputType(_mft, idx, type, flags); }
    private int ProcessInput(uint idx, nint sample, uint flags) { return IMFTransform_ProcessInput(_mft, idx, sample, flags); }
    private int ProcessOutput(uint flags, uint count, nint samples, out uint status)
    { uint s = 0; int hr = IMFTransform_ProcessOutput(_mft, flags, count, samples, &s); status = s; return hr; }
    private int ProcessMessage(uint msg, nint param) { return IMFTransform_ProcessMessage(_mft, msg, param); }

    [DllImport("mfplat.dll")] private static extern int IMFTransform_SetInputType(nint mft, uint idx, nint type, uint flags);
    [DllImport("mfplat.dll")] private static extern int IMFTransform_SetOutputType(nint mft, uint idx, nint type, uint flags);
    [DllImport("mfplat.dll")] private static extern int IMFTransform_ProcessInput(nint mft, uint idx, nint sample, uint flags);
    [DllImport("mfplat.dll")] private static extern int IMFTransform_ProcessOutput(nint mft, uint flags, uint count, nint samples, uint* pStatus);
    [DllImport("mfplat.dll")] private static extern int IMFTransform_ProcessMessage(nint mft, uint msg, nint param);

    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    private interface IMFTransform { }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_mft != 0) { Marshal.Release(_mft); _mft = 0; }
    }
}
