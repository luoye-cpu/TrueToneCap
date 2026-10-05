// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/MftEncoderNative.cs
// Windows Media Foundation MFT 编码器 — 系统内置 AV1/HEVC 硬件编码器（非 Intel QSV 专属）
// Windows 11 24H2+ 内置 AV1/HEVC MFT
// ⚠ 上面两行说的是 Windows 那边有这个东西，不代表本文件已经用上它 —— 本类的 Encode 路径当前不可达，
//   逐条说明见下方 2026-09-27 那段更正，别把这里当成"硬件 AV1 已在跑"。
//
// ═══ 2026-09-19：本路径是 QsvEncoderNative（oneVPL/MFX P/Invoke）的替代者，那条路的教训移到这里 ═══
// 那条路留下的唯一价值是一条判据：**自造第三方原生结构体布局 = 静默失败**。
// 原实现把 mfxVideoParam / mfxInfoMFX / mfxFrameInfo / mfxFrameSurface1 / mfxFrameData / mfxBitstream
// 全部凭猜测重写，忽略官方结构里的 reserved[] 前置字段与 union 布局 ⇒ 参数落进保留区，
// MFXVideoENCODE_Init 收到的 CodecId ≈ 0；另有 TargetKbps 用 ushort 承载 4K 码率必然溢出、
// EncodeFrameAsync 缺 drain 循环。这类错误不报错、不改返回值，只能靠"读回产物"才发现。
// ⇒ 本文件的初衷是不造结构体、只走微软自己导出的扁平函数（MFCreate* / MFSet*）+ 官方 GUID，
//   不手写任何第三方结构体字段布局；新增属性请用 MFSet* 配官方 GUID 常量，别在此处造结构体。
//   "QSV" 这个名字说的是设计意图：Intel 显卡上 MFT 会把 AV1 编码调度到 Quick Sync。
//
// ═══ 2026-09-27 更正本文件的自述：Encode 那条路【当前不可达】 ═══
// 原先此处写的是"本文件因此只走微软自己导出的扁平函数（MFCreate* / MFSet* / IMFTransform_*）"——
// 三类里有一类整个是错的，还有一类没核对过。逐条说清：
//  · 确实是 DLL 的扁平导出、可按 LibraryImport 直接调的：
//    MFCreateMediaType / MFCreateSample / MFCreateMemoryBuffer（文档给了 Mfplat.dll），
//    加上 ole32.dll 的 CoCreateInstance。
//  · MFSetGUID / MFSetUINT32 / MFSetUINT64：文档把它们列为 mfapi.h 的属性辅助函数，实现是转调
//    IMFAttributes 的 COM 方法 ⇒ 到底有没有同名导出符号，本机没有导出清单可查，这里只标【待核】，
//    不替它们断言"能跑通"。
//  · 其实是 COM vtable 方法、却被 [LibraryImport("mfplat.dll")] 当成扁平导出声明的：
//    IMFTransform_SetInputType / _SetOutputType / _ProcessInput / _ProcessOutput / _ProcessMessage
//    （对应 IMFTransform 接口的五个方法），同族的 MFMapLock / MFUnlock / MFSampleGetBufferByIndex
//    （对应 IMFMediaBuffer::Lock、IMFMediaBuffer::Unlock、IMFSample::GetBufferByIndex）也一样。
//    mfplat 不导出这些名字 ⇒ 首次调用解析不到入口点就抛 EntryPointNotFoundException
//    （DllNotFoundException 之类同样在这一层），它是托管异常 ⇒ 被 MftAvifEncode 的失败分支当成
//    "MFT 后端本次失败"吞掉、回退 libaom，界面上只留一条降级说明。
//  · 后果：今天没有任何产物经过这条 Encode。而本机连构造函数都过不去（CoCreateInstance 拿不到
//    AV1 MFT ⇒ "MFT 编码器创建失败: 0x…"），于是"互操作声明写错了"与"本机没注册这个编码器"
//    读出来是同一句话 —— 这正是本轮要消灭的形状，所以此处只改文案、不改调用方式。
//  · 要接对就把那五个方法改走真正的 COM 互操作（[ComImport]/[Interface] 的 IMFTransform 声明，
//    或按 vtable 偏移取函数指针），并在【装了 AV1 MFT 的机器】上以产物字节为准验证；本机验不了。
//  · CLSID_AV1EncMFT / CLSID_HEVCEncMFT 这两枚值未对权威头/注册表核对过，本机也无从证伪 ——
//    只登记为待核，不在这里替它们下结论。同族待核的还有 MFSampleAddBuffer
//    （IMFSample::AddBuffer 同样是 COM 方法，按扁平导出声明存疑；手头没有 mfplat 导出清单，故不下判定）。

using System.Runtime.InteropServices;
using Vortice.Direct3D11;

namespace TrueToneCap.Core.Encoding;

/// <summary>Media Foundation MFT 编码器 — 目标是使用系统内置硬件编码器 (Intel QSV / NVIDIA / AMD)。
/// <para>⚠ 别把这段读成"它在用"：本类的 Encode 路径当前不可达（那五个 IMFTransform_* 是 COM vtable
/// 方法却按 mfplat 扁平导出声明，见文件头 2026-09-27 更正）。上面的说法描述的是设计意图，
/// 不是已经成立的行为。</para></summary>
public sealed unsafe partial class MftEncoderNative : IDisposable
{
    // ── MFT CLSIDs ──
    // ⚠ 这两枚 CLSID 没有对权威头文件/注册表核对过，本机（没注册 AV1 MFT）也无从证伪 ——
    //   这里只登记"未核对"，既不断言它对，也不断言它错。接对 COM 互操作那天要一并核对。
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

    // ── MFT_MESSAGE_TYPE (mftransform.h) ──
    private const int MftMessageCommandDrain = 0x00000001; // MFT_MESSAGE_COMMAND_DRAIN
    private const int MftMessageCommandTick = 0x00000004;  // MFT_MESSAGE_COMMAND_TICK（仅心跳，不触发排空）

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

    /// <summary>探的是"系统里注册了 AV1 编码 MFT 没有"（CoCreateInstance 能不能拿到实例）。
    /// <para>⚠ 别把 true 读成"这条编码路能用"：本类的 Encode 还卡在 IMFTransform_* 的互操作声明上
    /// （见文件头 2026-09-27 更正），探得到实例也照样编不出东西 —— 这两件事不能混着报。</para></summary>
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
        // ═══ 2026-09-19：这一行恒 10 Mbps ⇒ 质量滑块在 MFT/QSV 路径上【完全不生效】═══
        // 为什么恒：ConfigureMft 拿不到任何质量参数 —— 调用方 MftAvifEncode.EncodeAsync 只把
        // 像素交给本类，crf 仅用在失败后回退 libaom 的那一次，所以这里只能写死一个常量。
        // 旧注释"10 Mbps 高质量"没有任何判据（码率与画质无关、也没人核对过产物），保留常量、
        // 改掉说法，并在能力表的 Qsv 显示名里如实标注"固定 10 Mbps 码率，质量滑块不适用"。
        // 何时该改成按 crf 推导：先把 crf 透传进来（构造函数或 Encode 形参），再按
        // NativeAvifEncoder.CrfToQuality 那条线性关系折算目标码率；接通后同步删掉显示名里的限制。
        // ⚠ 2026-09-27：上面这段"质量滑块在这条路上不生效"说的是接线意图 —— 本文件头写明的那五个
        //   vtable 声明还没修好，调用根本走不到这里，所以它从来没有在产物上被证实过，别当实测结论引用。
        MFSetUINT32(outType, MF_MT_AVG_BITRATE, 10_000_000);

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
        // ⚠ MFT_MESSAGE_COMMAND_DRAIN = 0x00000001。之前误用 0x00000004
        // （MFT_MESSAGE_COMMAND_TICK），发 TICK 不会触发排空 → ProcessOutput 返回
        // MF_E_TRANSFORM_NEED_MORE_INPUT → MFT 后端 100% 失败并静默回退 libaom。
        ProcessMessage(MftMessageCommandDrain, 0);

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
                    nv12[uvIdx] = (byte)Math.Clamp(((-38 * r - 74 * g + 112 * b + 128) >> 8) + 128, 0, 255);     // U
                    nv12[uvIdx + 1] = (byte)Math.Clamp(((112 * r - 94 * g - 18 * b + 128) >> 8) + 128, 0, 255);   // V
                }
            }
        }
        return nv12;
    }

    // ═══════════ COM 互操作 ═══════════
    // 下面这组声明分三类，别当成一类（逐条依据见文件头 2026-09-27 更正）：
    //   · 无标记的：DLL 真导出的扁平函数，能按 LibraryImport 调；
    //   · 标【待核】的：MFSet* 在 SDK 头里是转调 IMFAttributes 的辅助函数，有没有同名导出符号本机无从核对；
    //   · 打了 ✗ 的：根本不是扁平导出，而是 COM 接口上的 vtable 方法 —— 用 [LibraryImport] 声明它们，
    //     首次调用就会在解析入口点时失败，永远到不了原生代码（接法见文件头）。
    [LibraryImport("ole32.dll")] private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint ctx, ref Guid iid, out nint ppv);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMediaType(nint* ppType);
    [LibraryImport("mfplat.dll")] private static partial int MFSetGUID(nint type, Guid key, Guid val);       // 待核：mfapi.h 的属性辅助函数，实现是转调 IMFAttributes::SetGUID
    [LibraryImport("mfplat.dll")] private static partial int MFSetUINT64(nint type, Guid key, ulong val);    // 待核：同上（IMFAttributes::SetUINT64）
    [LibraryImport("mfplat.dll")] private static partial int MFSetUINT32(nint type, Guid key, uint val);     // 待核：同上（IMFAttributes::SetUINT32）
    [LibraryImport("mfplat.dll")] private static partial int MFCreateSample(nint* ppSample);
    [LibraryImport("mfplat.dll")] private static partial int MFCreateMemoryBuffer(uint cbMax, nint* ppBuf);
    [LibraryImport("mfplat.dll")] private static partial int MFMapLock(nint buf, nint* ppData, uint* pcbMax, uint* pcbCur);   // ✗ 非导出：IMFMediaBuffer::Lock（vtable）
    [LibraryImport("mfplat.dll")] private static partial int MFUnlock(nint buf);                                              // ✗ 非导出：IMFMediaBuffer::Unlock（vtable）
    [LibraryImport("mfplat.dll")] private static partial int MFSampleAddBuffer(nint sample, nint buf);                         // ✗ 存疑待核：IMFSample::AddBuffer 也是 vtable 方法，手头无 mfplat 导出清单可判
    [LibraryImport("mfplat.dll")] private static partial int MFSampleGetBufferByIndex(nint sample, uint idx, nint* ppBuf);     // ✗ 非导出：IMFSample::GetBufferByIndex（vtable）

    private void SetInputType(uint idx, nint type, uint flags) { IMFTransform_SetInputType(_mft, idx, type, flags); }
    private void SetOutputType(uint idx, nint type, uint flags) { IMFTransform_SetOutputType(_mft, idx, type, flags); }
    private int ProcessInput(uint idx, nint sample, uint flags) { return IMFTransform_ProcessInput(_mft, idx, sample, flags); }
    private int ProcessOutput(uint flags, uint count, nint samples, out uint status)
    { uint s = 0; int hr = IMFTransform_ProcessOutput(_mft, flags, count, samples, &s); status = s; return hr; }
    private int ProcessMessage(uint msg, nint param) { return IMFTransform_ProcessMessage(_mft, msg, param); }

    // ↓ 以下五个是 IMFTransform 接口的 COM vtable 方法，mfplat.dll 不导出同名符号 ⇒
    //   以 [LibraryImport("mfplat.dll")] 声明它们，ConfigureMft/Encode/GetOutput 里的每一次调用
    //   都在解析入口点时就失败（托管异常，不是原生崩溃），这条路今天一个像素也没编过。
    [LibraryImport("mfplat.dll")] private static partial int IMFTransform_SetInputType(nint mft, uint idx, nint type, uint flags);      // ✗ vtable：IMFTransform::SetInputType
    [LibraryImport("mfplat.dll")] private static partial int IMFTransform_SetOutputType(nint mft, uint idx, nint type, uint flags);     // ✗ vtable：IMFTransform::SetOutputType
    [LibraryImport("mfplat.dll")] private static partial int IMFTransform_ProcessInput(nint mft, uint idx, nint sample, uint flags);    // ✗ vtable：IMFTransform::ProcessInput
    [LibraryImport("mfplat.dll")] private static partial int IMFTransform_ProcessOutput(nint mft, uint flags, uint count, nint samples, uint* pStatus); // ✗ vtable：IMFTransform::ProcessOutput
    [LibraryImport("mfplat.dll")] private static partial int IMFTransform_ProcessMessage(nint mft, uint msg, nint param);               // ✗ vtable：IMFTransform::ProcessMessage

    // 空接口：只贡献 GUID 给 CoCreateInstance 当 iid 用，一个方法都没承载 ⇒
    // 上面那五个扁平声明与它毫无关系，接对 vtable 互操作时方法应当由它来声明。
    [Guid("bf94c121-5b05-4e6f-8000-ba598961414d")]
    private interface IMFTransform { }

    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        if (_mft != 0) { Marshal.Release(_mft); _mft = 0; }
    }
}
