// TrueToneCap.Core/Encoding/QsvEncoderNative.cs
// Intel Quick Sync Video (QSV) AV1 编码器 — 通过 oneVPL/MFX 官方 API
// 要求: Intel Arc / Xe / UHD 显卡 + 最新驱动 (提供 libvpl.dll 或 libmfx64-gen.dll)

using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace TrueToneCap.Core.Encoding;

public sealed unsafe class QsvEncoderNative : IDisposable
{
    private nint _session;
    private bool _disposed;

    // ── 库加载 ──
    private static readonly string[] DllNames = ["libvpl.dll", "libmfx64-gen.dll", "libmfxhw64.dll"];
    private static nint _dll;

    public static bool IsAvailable
    {
        get
        {
            try
            {
                // 先确认 Intel GPU 存在
                if (!HasIntelGpu()) return false;
                return TryLoadLibrary();
            }
            catch { return false; }
        }
    }

    private static bool HasIntelGpu()
    {
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory7>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var a).Failure || a is null) break;
                try { if (a.Description.VendorId == 0x8086) return true; }
                finally { a.Dispose(); }
            }
            return false;
        }
        catch { return false; }
    }

    private static bool TryLoadLibrary()
    {
        if (_dll != 0) return true;
        foreach (var name in DllNames)
        {
            try
            {
                _dll = NativeLibrary.Load(name);
                return true;
            }
            catch { }
        }
        return false;
    }

    // ── 构造函数 ──
    public QsvEncoderNative()
    {
        if (!ResolveExports())
            throw new InvalidOperationException("无法加载 Intel QSV 编码库 (libvpl/libmfx)");

        var ver = new mfxVersion { Major = 2, Minor = 0 };
        int hr = _mfxInit!(0x0002, ref ver, out _session); // MFX_IMPL_HARDWARE
        if (hr < 0 || _session == 0)
            throw new InvalidOperationException($"MFXInit 失败: 0x{hr:X8}");
    }

    // ── AV1 编码 ──
    public byte[] EncodeAv1(byte[] bgra, int w, int h, int quality)
    {
        if (_session == 0) throw new ObjectDisposedException(nameof(QsvEncoderNative));

        var param = new mfxVideoParam();
        param.mfx.CodecId = 0x41563120; // 'AV1 '
        param.mfx.TargetUsage = (ushort)Math.Clamp(7 - quality * 7 / 63, 1, 7);
        param.mfx.TargetKbps = (ushort)(w * h * quality / 10);
        param.mfx.RateControlMethod = 2; // VBR
        param.mfx.FrameInfo.FourCC = 0x3231564E; // NV12
        param.mfx.FrameInfo.Width = (ushort)((w + 1) & ~1);
        param.mfx.FrameInfo.Height = (ushort)((h + 1) & ~1);
        param.mfx.FrameInfo.CropW = (ushort)w;
        param.mfx.FrameInfo.CropH = (ushort)h;
        param.mfx.FrameInfo.FrameRateExtN = 30;
        param.mfx.FrameInfo.FrameRateExtD = 1;
        param.mfx.FrameInfo.ChromaFormat = 1;
        param.IOPattern = 0x04; // MFX_IOPATTERN_IN_SYSTEM_MEMORY
        param.AsyncDepth = 1;

        int hr = _encInit!(_session, ref param);
        if (hr < 0) throw new InvalidOperationException($"MFXVideoENCODE_Init 失败: 0x{hr:X8}");

        try
        {
            int aw = param.mfx.FrameInfo.Width, ah = param.mfx.FrameInfo.Height;
            var nv12 = BgraToNV12(bgra, w, h, aw, ah);

            var surface = new mfxFrameSurface1();
            surface.Info = param.mfx.FrameInfo;
            surface.Data.Y = Marshal.AllocHGlobal(aw * ah);
            surface.Data.UV = Marshal.AllocHGlobal(aw * ah / 2);
            surface.Data.Pitch = aw;
            Marshal.Copy(nv12, 0, surface.Data.Y, aw * ah);
            Marshal.Copy(nv12, aw * ah, surface.Data.UV, aw * ah / 2);

            var bs = new mfxBitstream();
            int bsSize = w * h * 2;
            bs.Data = Marshal.AllocHGlobal(bsSize);
            bs.MaxLength = bsSize;
            bs.DataLength = 0;

            hr = _encFrame!(_session, 0, ref surface, ref bs, out nint syncPoint);
            if (hr >= 0 && syncPoint != 0)
                _syncOp!(_session, syncPoint, uint.MaxValue);

            byte[] result = [];
            if (bs.DataLength > 0)
            {
                result = new byte[bs.DataLength];
                Marshal.Copy(bs.Data, result, 0, bs.DataLength);
            }

            Marshal.FreeHGlobal(surface.Data.Y);
            Marshal.FreeHGlobal(surface.Data.UV);
            Marshal.FreeHGlobal(bs.Data);
            return result;
        }
        finally
        {
            _encClose!(_session);
        }
    }

    private static byte[] BgraToNV12(byte[] bgra, int w, int h, int aw, int ah)
    {
        var nv12 = new byte[aw * ah + aw * ah / 2];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                int di = y * aw + x;
                byte b = bgra[si], g = bgra[si + 1], r = bgra[si + 2];
                nv12[di] = (byte)((66 * r + 129 * g + 25 * b + 128) >> 8);
                if (x % 2 == 0 && y % 2 == 0)
                {
                    int uvOff = aw * ah + (y / 2) * aw + (x & ~1);
                    nv12[uvOff] = (byte)((-38 * r - 74 * g + 112 * b + 128) >> 8);
                    nv12[uvOff + 1] = (byte)((112 * r - 94 * g - 18 * b + 128) >> 8);
                }
            }
        }
        return nv12;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session != 0) { _mfxClose?.Invoke(_session); _session = 0; }
    }

    // ════════════════════════════════
    // P/Invoke: oneVPL / Intel Media SDK (动态加载，非 [DllImport])
    // ════════════════════════════════

    private delegate int MFXInit_t(uint impl, ref mfxVersion ver, out nint session);
    private delegate int MFXClose_t(nint session);
    private delegate int MFXVideoENCODE_Init_t(nint session, ref mfxVideoParam param);
    private delegate int MFXVideoENCODE_EncodeFrameAsync_t(nint session, uint flags, ref mfxFrameSurface1 surface, ref mfxBitstream bs, out nint syncPoint);
    private delegate int MFXVideoCORE_SyncOperation_t(nint session, nint syncPoint, uint wait);
    private delegate int MFXVideoENCODE_Close_t(nint session);

    private static MFXInit_t? _mfxInit;
    private static MFXClose_t? _mfxClose;
    private static MFXVideoENCODE_Init_t? _encInit;
    private static MFXVideoENCODE_EncodeFrameAsync_t? _encFrame;
    private static MFXVideoCORE_SyncOperation_t? _syncOp;
    private static MFXVideoENCODE_Close_t? _encClose;

    private static bool ResolveExports()
    {
        if (_mfxInit != null) return true;
        if (!TryLoadLibrary()) return false;
        try
        {
            _mfxInit = Marshal.GetDelegateForFunctionPointer<MFXInit_t>(NativeLibrary.GetExport(_dll, "MFXInit"));
            _mfxClose = Marshal.GetDelegateForFunctionPointer<MFXClose_t>(NativeLibrary.GetExport(_dll, "MFXClose"));
            _encInit = Marshal.GetDelegateForFunctionPointer<MFXVideoENCODE_Init_t>(NativeLibrary.GetExport(_dll, "MFXVideoENCODE_Init"));
            _encFrame = Marshal.GetDelegateForFunctionPointer<MFXVideoENCODE_EncodeFrameAsync_t>(NativeLibrary.GetExport(_dll, "MFXVideoENCODE_EncodeFrameAsync"));
            _syncOp = Marshal.GetDelegateForFunctionPointer<MFXVideoCORE_SyncOperation_t>(NativeLibrary.GetExport(_dll, "MFXVideoCORE_SyncOperation"));
            _encClose = Marshal.GetDelegateForFunctionPointer<MFXVideoENCODE_Close_t>(NativeLibrary.GetExport(_dll, "MFXVideoENCODE_Close"));
            return true;
        }
        catch { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxVersion { public ushort Minor, Major; }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxFrameInfo
    {
        public uint FourCC;
        public ushort Width, Height, CropX, CropY, CropW, CropH;
        public ushort FrameRateExtN, FrameRateExtD;
        public ushort AspectRatioW, AspectRatioH;
        public ushort ChromaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxFrameData { public nint Y, UV, V, A; public int Pitch; }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxFrameSurface1 { public mfxFrameInfo Info; public mfxFrameData Data; }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxBitstream { public nint Data; public int MaxLength, DataLength; public long TimeStamp; }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxInfoMFX
    {
        public ushort TargetUsage, TargetKbps, RateControlMethod;
        public mfxFrameInfo FrameInfo;
        public uint CodecId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct mfxVideoParam { public mfxInfoMFX mfx; public int IOPattern, AsyncDepth; }
}
