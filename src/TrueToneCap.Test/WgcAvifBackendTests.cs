// TrueToneCap.Test/WgcAvifBackendTests.cs
// WGC 真实捕获 + AVIF 各后端编码验证
//
// 运行: dotnet run --project src/TrueToneCap.Test -- --wgc-avif-tests
//
// 目的:
//   本项目的 AVIF 硬件后端（NVENC / QSV）在 Auto 模式下长期无法被选中
//   （原实现把内嵌的 libaom 置于最高优先级，硬件分支成为死代码），
//   因此 NVENC 路径的容器写入代码（mdat 大端、iloc 偏移、av1C 配置、iloc sizes）
//   从未在真实场景中执行过。
//   本测试用 WGC 捕获真实桌面帧，逐一驱动每个 AVIF 后端编码，
//   并用独立解析器校验输出容器结构，同时对比各后端耗时。
//
// ⚠ 必须在 STA 线程执行 WGC（帧投递依赖消息泵），故 RunAll 内部新建 STA 线程。

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using TrueToneCap.Core;
using TrueToneCap.Core.Encoding;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace TrueToneCap.Test;

// ⚠ 必须声明 partial：本类使用 [LibraryImport] 源生成器，
// 其生成的实现部分是同一个 partial 类型的另一个分部。
public static partial class WgcAvifBackendTests
{
    private static int _passed, _failed;
    private static readonly string OutDir =
        Path.Combine(Path.GetTempPath(), "TrueToneCap_WgcAvifTest");

    public static int RunAll()
    {
        Directory.CreateDirectory(OutDir);
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  WGC 捕获 + AVIF 后端编码验证");
        Console.WriteLine($"  输出目录: {OutDir}");
        Console.WriteLine("══════════════════════════════════════\n");

        // WGC 帧投递依赖消息泵 → 必须在 STA 线程执行
        int exit = 0;
        var thread = new Thread(() =>
        {
            try { exit = RunCore(); }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 未处理异常: {ex}");
                exit = 1;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(10)))
        {
            Console.WriteLine("  ❌ 测试超时（10 分钟）");
            return 1;
        }

        Console.WriteLine($"\n══════════════════════════════════════");
        Console.WriteLine($"  结果: {_passed} 通过, {_failed} 失败");
        Console.WriteLine($"══════════════════════════════════════");
        return exit != 0 || _failed > 0 ? 1 : 0;
    }

    private static int RunCore()
    {
        // ═══ 1. WGC 捕获真实帧 ═══
        Console.WriteLine("── 1. WGC 捕获 ──");
        var frame = CaptureFrame(out int w, out int h, out ID3D11Device? device);
        if (frame is null || device is null)
        {
            Assert("WGC 捕获", false, "未能捕获桌面帧（无显示器 / 权限 / WGC 不可用）");
            return 1;
        }
        Assert("WGC 捕获", true, $"{w}x{h}, {frame.Length / 1024 / 1024} MB BGRA8");

        // 校验捕获内容非空（全黑通常是失败特征）
        int nonZero = 0;
        for (int i = 0; i < frame.Length; i += 4 * 997)
            if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 0) nonZero++;
        Assert("WGC 捕获内容非空", nonZero > 0, $"抽样 {nonZero} 个非黑像素");

        // NVENC 需要共享 D3D 设备（生产环境由 WgcCaptureService 注入）
        NvencAvifBackend.SetSharedD3DDevice(device);

        // ═══ 2. 显示探测到的 GPU 编码器能力 ═══
        Console.WriteLine("\n── 2. GPU 编码器能力探测 ──");
        ReportGpuEncoders();

        // ═══ 3. Auto 模式后端选择结果 ═══
        Console.WriteLine("\n── 3. Auto 模式后端选择 ──");
        try
        {
            var auto = AvifEncoderSelector.Select(AvifEncoderBackend.Auto);
            Assert("Auto 后端选择", true, $"选中 {auto.Backend}");
            Console.WriteLine($"  ℹ Auto 选中: {auto.Backend}");
            if (auto.Backend == AvifEncoderBackend.LibAom)
                Console.WriteLine("     （若本机有支持 AV1 的 N 卡，此处应为 Nvenc）");
        }
        catch (Exception ex)
        {
            Assert("Auto 后端选择", false, ex.Message);
        }

        // ═══ 4. 逐后端编码验证 ═══
        Console.WriteLine("\n── 4. 逐后端编码 ──");
        var backends = new[]
        {
            AvifEncoderBackend.Nvenc,
            AvifEncoderBackend.LibAom,
        };

        foreach (var b in backends)
        {
            Console.WriteLine($"\n  ─── {b} ───");
            TestBackend(b, frame, w, h);
        }

        device.Dispose();
        return 0;
    }

    private static void TestBackend(AvifEncoderBackend backend, byte[] frame, int w, int h)
    {
        string path = Path.Combine(OutDir, $"wgc_{backend}_{Guid.NewGuid():N}.avif");
        var settings = new EncodingSettings
        {
            Format = OutputFormat.AVIF,
            Quality = 50f,
            HdrOutput = false,
            ChromaSubsampling = "420",
            OutputBitDepth = 8,
            DisplayBitDepth = 8,
            AvifBackend = backend,
        };

        try
        {
            IAvifEncoder encoder;
            try { encoder = AvifEncoderSelector.Select(backend); }
            catch (Exception ex)
            {
                Assert($"{backend}/后端可用", false, $"选择失败: {ex.Message}");
                return;
            }
            Assert($"{backend}/后端可用", true, "");

            if (!encoder.IsAvailable)
            {
                Console.WriteLine($"  ⚠ {backend} 不可用，跳过编码");
                Assert($"{backend}/跳过(不可用)", true, "");
                return;
            }

            var sw = Stopwatch.StartNew();
            encoder.EncodeAsync(frame, w, h, 50, path, CancellationToken.None,
                chroma: "420", displayBitDepth: 8).GetAwaiter().GetResult();
            sw.Stop();

            if (!File.Exists(path))
            {
                Assert($"{backend}/编码产出文件", false, "文件未生成");
                return;
            }

            var len = new FileInfo(path).Length;
            Assert($"{backend}/编码产出文件", len > 0,
                $"{len / 1024.0:F1} KB, 耗时 {sw.Elapsed.TotalSeconds:F2}s");

            // ═══ 独立解析容器结构 ═══
            var bytes = File.ReadAllBytes(path);
            ValidateAvifContainer(backend.ToString(), bytes, w, h);
        }
        catch (Exception ex)
        {
            Assert($"{backend}/编码未抛异常", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>用独立解析器校验 AVIF 容器结构（不复用被测代码）。</summary>
    private static void ValidateAvifContainer(string tag, byte[] d, int expectW, int expectH)
    {
        var boxes = ParseBoxes(d);
        Assert($"{tag}/box 解析", boxes.Count >= 3,
            string.Join(",", boxes.Select(b => b.Type)));

        Assert($"{tag}/含 ftyp", boxes.Any(b => b.Type == "ftyp"), "");
        Assert($"{tag}/含 meta", boxes.Any(b => b.Type == "meta"), "");
        var mdat = boxes.FirstOrDefault(b => b.Type == "mdat");
        Assert($"{tag}/含 mdat", mdat is not null, "");

        // 顶层 box 链应精确覆盖整个文件
        long covered = boxes.Sum(b => (long)b.TotalSize);
        Assert($"{tag}/box 链精确覆盖文件", covered == d.Length, $"覆盖 {covered} / 文件 {d.Length}");

        // mdat 长度必须按大端解析（回归要点）
        if (mdat is not null)
        {
            bool sane = mdat.PayloadLength > 0 && mdat.HeaderSize + mdat.PayloadLength <= d.Length;
            Assert($"{tag}/mdat 长度大端解析合理", sane,
                $"size={mdat.TotalSize} payload={mdat.PayloadLength} 文件={d.Length}");
        }

        // meta 内解析 ispe(尺寸) 与 iloc(偏移)
        var meta = boxes.FirstOrDefault(b => b.Type == "meta");
        if (meta is null) { Assert($"{tag}/meta 存在", false, ""); return; }

        int? ispeW = null, ispeH = null;
        long ilocOffset = -1, ilocLength = -1;
        ScanMeta(d, meta, ref ispeW, ref ispeH, ref ilocOffset, ref ilocLength);

        if (ispeW is not null && ispeH is not null)
            Assert($"{tag}/ispe 尺寸正确", ispeW == expectW && ispeH == expectH,
                $"{ispeW}x{ispeH} (期望 {expectW}x{expectH})");

        // iloc 的 extent_offset 必须落在 mdat 数据区内
        if (ilocOffset >= 0 && mdat is not null)
        {
            long mdatDataStart = mdat.Offset + mdat.HeaderSize;
            bool inRange = ilocOffset >= mdatDataStart && ilocOffset + ilocLength <= d.Length;
            Assert($"{tag}/iloc 偏移落在 mdat 内", inRange,
                $"extent_offset={ilocOffset} len={ilocLength}, mdat 数据区 [{mdatDataStart}, {d.Length}]");
        }
    }

    // ═══════════════════════════════════════
    //  ISOBMFF 解析（大端）
    // ═══════════════════════════════════════

    private sealed record Box(string Type, long Offset, uint HeaderSize, long PayloadLength)
    {
        public long TotalSize => HeaderSize + PayloadLength;
    }

    private static List<Box> ParseBoxes(byte[] d)
    {
        var list = new List<Box>();
        long pos = 0;
        while (pos + 8 <= d.Length)
        {
            uint size = ReadU32BE(d, (int)pos);
            string type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
            if (size == 1)
            {
                if (pos + 16 > d.Length) break;
                ulong large = ReadU64BE(d, (int)pos + 8);
                if (large < 16) break;
                list.Add(new Box(type, pos, 16, (long)large - 16));
                pos += (long)large;
                continue;
            }
            if (size == 0)
            {
                list.Add(new Box(type, pos, 8, d.Length - pos - 8));
                break;
            }
            if (size < 8) break;
            list.Add(new Box(type, pos, 8, size - 8));
            pos += size;
        }
        return list;
    }

    /// <summary>扫描 meta 内的 ispe 与 iloc。</summary>
    private static void ScanMeta(byte[] d, Box meta, ref int? ispeW, ref int? ispeH,
        ref long ilocOffset, ref long ilocLength)
    {
        long end = meta.Offset + meta.TotalSize;
        long pos = meta.Offset + meta.HeaderSize + 4; // meta 是 FullBox
        while (pos + 8 <= end)
        {
            uint size = ReadU32BE(d, (int)pos);
            string type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
            if (size < 8 || pos + size > end) break;

            if (type == "ispe" && pos + 20 <= end)
            {
                ispeW = (int)ReadU32BE(d, (int)pos + 12);
                ispeH = (int)ReadU32BE(d, (int)pos + 16);
            }
            else if (type == "iloc")
            {
                // [size4][type4][ver1][flags3][sizes1][reserved1][item_count2][item_ID2]...
                // extent_offset 位于 box 起始 +22（当 sizes 的 offset/length 各为 4 字节时）
                int off = (int)(pos + 22);
                if (off + 8 <= d.Length)
                {
                    ilocOffset = ReadU32BE(d, off);
                    ilocLength = ReadU32BE(d, off + 4);
                }
            }
            pos += size;
        }
    }

    private static uint ReadU32BE(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static ulong ReadU64BE(byte[] d, int o) =>
        ((ulong)d[o] << 56) | ((ulong)d[o + 1] << 48) | ((ulong)d[o + 2] << 40) |
        ((ulong)d[o + 3] << 32) | ((ulong)d[o + 4] << 24) | ((ulong)d[o + 5] << 16) |
        ((ulong)d[o + 6] << 8) | d[o + 7];

    // ═══════════════════════════════════════
    //  GPU 编码器能力报告
    // ═══════════════════════════════════════

    /// <summary>穷举 NV_ENCODE_API_FUNCTION_LIST.version 的候选编码，找出驱动实际接受的值。
    /// 用于诊断 NvEncodeAPICreateInstance 返回 NV_ENC_ERR_INVALID_VERSION (15) 的根因。</summary>
    private static void ProbeNvencVersion()
    {
        Console.WriteLine("\n  ── NVENC version 字段穷举 ──");
        try
        {
            var dll = NativeLibrary.Load("nvEncodeAPI64.dll");
            try
            {
                var fnPtr = NativeLibrary.GetExport(dll, "NvEncodeAPICreateInstance");
                var create = Marshal.GetDelegateForFunctionPointer<NvEncCreateInstance>(fnPtr);

                var found = new List<string>();
                nint table = Marshal.AllocHGlobal(1024);
                try
                {
                    // 候选: (major, minor) 组合 × version 变体
                    foreach (uint major in new uint[] { 13, 12, 11, 10 })
                    foreach (uint minor in new uint[] { 2, 1, 0 })
                    {
                        uint nvencVer = major | (minor << 24);
                        var variants = new (uint Val, string Name)[]
                        {
                            (0,                          "裸版本号"),
                            (1u << 16,                   "|1<<16"),
                            (2u << 16,                   "|2<<16"),
                            ((1u << 16) | (0x7u << 28),  "|1<<16|0x7<<28"),
                        };
                        foreach (var (variant, name) in variants)
                        {
                            uint ver = nvencVer | variant;
                            // 清零后写入 version 字段（避免依赖 unsafe 上下文）
                            for (int b = 0; b < 1024; b += 8) Marshal.WriteInt64(table, b, 0L);
                            Marshal.WriteInt32(table, (int)ver);
                            int hr = create(table);
                            if (hr == 0)
                            {
                                found.Add($"major={major} minor={minor} {name} → 0x{ver:X8}");
                                Console.WriteLine($"  ✅ SDK {major}.{minor} {name} (0x{ver:X8}) 成功");
                            }
                        }
                    }
                }
                finally { Marshal.FreeHGlobal(table); }

                if (found.Count == 0)
                    Console.WriteLine("  ❌ 所有 48 种组合均失败 —— 驱动可能未安装 NVENC 组件");
            }
            finally { NativeLibrary.Free(dll); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 版本探测异常: {ex.Message}");
        }
    }

    /// <summary>诊断：枚举适配器，在每块 N 卡上用 P/Invoke 创建 D3D11 设备并尝试打开 NVENC 会话。
    /// 用于定位 CreateNvidiaDevice 返回 null 的原因。</summary>
    private static void ProbeNvidiaDeviceCreation()
    {
        Console.WriteLine("\n  ── N 卡设备创建诊断 ──");
        try
        {
            using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
            for (uint i = 0; ; i++)
            {
                if (factory.EnumAdapters1(i, out var adapter).Failure || adapter is null) break;
                try
                {
                    var desc = adapter.Description;
                    Console.WriteLine($"  适配器[{i}] {desc.Description.Trim()} vendor=0x{desc.VendorId:X4}");
                    if (desc.VendorId != 0x10DE) continue;

                    int hr = D3D11CreateDeviceNativeDiag(
                        adapter.NativePointer, 0, nint.Zero,
                        (uint)Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,
                        nint.Zero, 0, 7,
                        out var devPtr, out _, out var ctxPtr);
                    Console.WriteLine($"    P/Invoke D3D11CreateDevice hr=0x{hr:X8} dev=0x{devPtr:X}");

                    if (hr >= 0 && devPtr != 0)
                    {
                        if (ctxPtr != 0) Marshal.Release(ctxPtr);
                        using var dev = new Vortice.Direct3D11.ID3D11Device(devPtr);
                        Console.WriteLine("    ✅ 设备创建成功");
                        try
                        {
                            using var nv = new NvEncoderNative(dev);
                            Console.WriteLine("    ✅✅ NVENC 会话打开成功！");

                            // 枚举驱动报告的 codec GUID，与标准值对照
                            // 注: NvEncoderNative.CodecAv1 是 internal（Core 未设 InternalsVisibleTo），
                            // 故此处本地定义标准 AV1 GUID 用于比对。
                            // 2026-09-01: 实测驱动（RTX 5080/4060）报告的 AV1 GUID
                            var expectedAv1 = new Guid(0x0A352289, 0x0AA7, 0x4759,
                                0x86, 0x2D, 0x5D, 0x15, 0xCD, 0x16, 0xD2, 0x54);
                            Console.WriteLine("    ── 驱动支持的 codec GUID ──");
                            var list = nv.EnumerateCodecGuids();
                            if (list.Count == 0) Console.WriteLine("      (枚举失败或为空)");
                            foreach (var g in list)
                            {
                                string tag = g == expectedAv1 ? "  ← AV1 ✅"
                                    : g.ToString().StartsWith("790cdc88") ? "  ← HEVC"
                                    : g.ToString().StartsWith("6bc82762") ? "  ← H264" : "";
                                Console.WriteLine($"      {g}{tag}");
                            }
                            Console.WriteLine($"      标准 AV1 GUID  : {expectedAv1}");
                            Console.WriteLine($"      AV1 受支持     : {(list.Contains(expectedAv1) ? "✅" : "❌ 驱动未报告该 GUID")}");

                            // 枚举驱动支持的 preset GUID（诊断 GetEncodePresetConfig 失败原因）
                            Console.WriteLine("    ── 驱动支持的 preset GUID (AV1) ──");
                            try
                            {
                                foreach (var pg in nv.EnumeratePresetGuids(expectedAv1))
                                    Console.WriteLine($"      {pg}");
                            }
                            catch (Exception pex)
                            {
                                Console.WriteLine($"      (枚举失败: {pex.Message})");
                            }

                            // 直接测试实际编码（小帧，快速失败以便定位）
                            int tw = 320, th = 180;
                            var testFrame = new byte[tw * th * 4];
                            for (int q = 0; q < testFrame.Length; q += 4)
                            {
                                testFrame[q] = (byte)(q / 4 % 256);
                                testFrame[q + 1] = 128;
                                testFrame[q + 2] = 200;
                                testFrame[q + 3] = 255;
                            }
                            try
                            {
                                var sw = System.Diagnostics.Stopwatch.StartNew();
                                var bs = nv.EncodeAv1(testFrame, tw, th, 30);
                                sw.Stop();
                                Console.WriteLine($"    ✅✅ 实际编码成功: {bs.Length} 字节, {sw.ElapsedMilliseconds}ms");
                            }
                            catch (Exception encEx)
                            {
                                Console.WriteLine($"    ❌ 实际编码失败: {encEx.GetType().Name}: {encEx.Message}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"    ❌ 会话打开失败: {ex.Message}");

                            // 对照: Vortice 包装的 NativePointer 与原始指针是否一致
                            Console.WriteLine($"    原始 devPtr = 0x{devPtr:X}, Vortice NativePointer = 0x{dev.NativePointer:X}");

                            // 会话失败 → 穷举索引/SDK 组合定位可行配置
                            ProbeOpenSessionIndex(devPtr);
                        }
                    }
                }
                finally { adapter.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ 诊断异常: {ex.Message}");
        }
    }

    /// <summary>穷举函数表索引，找出 NvEncOpenEncodeSessionEx 的真实位置。
    /// x64 下多余参数走寄存器/栈，调用参数较少的 NVENC 函数不会崩溃，只会忽略多余参数。</summary>
    private static void ProbeOpenSessionIndex(nint rawDevicePtr)
    {
        // 直接用 P/Invoke 得到的原始 ID3D11Device* ，绕过 Vortice 包装，
        // 以排查 NativePointer 是否为 NVENC 期望的接口指针。
        Console.WriteLine("\n  ── OpenEncodeSessionEx 索引穷举 ──");
        try
        {
            var dll = NativeLibrary.Load("nvEncodeAPI64.dll");
            try
            {
                var create = Marshal.GetDelegateForFunctionPointer<NvEncCreateInstance>(
                    NativeLibrary.GetExport(dll, "NvEncodeAPICreateInstance"));

                // 用 SDK 12.2 建表（广泛支持）
                uint sdkVer = 12 | (2u << 24);
                nint table = Marshal.AllocHGlobal(2048);
                try
                {
                    for (int b = 0; b < 2048; b += 8) Marshal.WriteInt64(table, b, 0L);
                    Marshal.WriteInt32(table, unchecked((int)(sdkVer | (1u << 16) | (0x7u << 28))));
                    int hr = create(table);
                    if (hr != 0) { Console.WriteLine($"  ❌ CreateInstance 失败 0x{hr:X8}"); return; }

                    // 构造 NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS（真实大小 1040 字节）
                    nint osp = Marshal.AllocHGlobal(1088);
                    try
                    {
                        for (int b = 0; b < 1088; b += 8) Marshal.WriteInt64(osp, b, 0L);
                        Marshal.WriteInt32(osp, unchecked((int)(sdkVer | (1u << 16) | (0x7u << 28)))); // version
                        Marshal.WriteInt32(osp, 4, 2);                        // deviceType = DX11
                        Marshal.WriteIntPtr(osp, 8, rawDevicePtr);            // device（原始指针）
                        Marshal.WriteInt32(osp, 24, unchecked((int)sdkVer));   // apiVersion

                        // 固定 SDK 12.2 + 索引 29，遍历 deviceType
                        // 注: 旧版 nvEncodeAPI.h 只有 DIRECTX(0)/CUDA(1)，现代 SDK 为
                        // CUDA(0)/DX9(1)/DX11(2)/OPENGL(3)。此处实测各取值以确认。
                        uint v = 12 | (2u << 24);
                        for (int b = 0; b < 2048; b += 8) Marshal.WriteInt64(table, b, 0L);
                        Marshal.WriteInt32(table, unchecked((int)(v | (1u << 16) | (0x7u << 28))));
                        if (create(table) != 0) { Console.WriteLine("  ❌ CreateInstance 失败"); return; }

                        Marshal.WriteInt32(osp, unchecked((int)(v | (1u << 16) | (0x7u << 28))));
                        Marshal.WriteInt32(osp, 24, unchecked((int)v));

                        nint fnPtr = Marshal.ReadIntPtr(table, 8 + 29 * 8);
                        if (fnPtr == 0) { Console.WriteLine("  ❌ [29] 空指针"); return; }
                        var fn = Marshal.GetDelegateForFunctionPointer<NvEncOpenSessionEx>(fnPtr);

                        foreach (var (dt, dtName) in new (int, string)[]
                                 { (0, "0 (旧:DIRECTX / 新:CUDA)"), (1, "1 (旧:CUDA / 新:DX9)"),
                                   (2, "2 (新:DX11)"), (3, "3 (新:OPENGL)") })
                        {
                            Marshal.WriteInt32(osp, 4, dt);
                            Marshal.WriteIntPtr(osp, 8, rawDevicePtr);
                            nint enc = 0;
                            int h;
                            try { h = fn(osp, ref enc); }
                            catch (Exception ex) { Console.WriteLine($"  deviceType={dtName}: 异常 {ex.Message}"); continue; }

                            Console.WriteLine(h == 0
                                ? $"  ✅ deviceType={dtName}: 会话打开成功！encoder=0x{enc:X}"
                                : $"  ❌ deviceType={dtName}: hr=0x{h:X8}");
                            if (h == 0) { Console.WriteLine($"  → 可行 deviceType = {dt}"); return; }
                        }
                    }
                    finally { Marshal.FreeHGlobal(osp); }
                }
                finally { Marshal.FreeHGlobal(table); }
            }
            finally { NativeLibrary.Free(dll); }
        }
        catch (Exception ex) { Console.WriteLine($"  ❌ 索引穷举异常: {ex.Message}"); }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncOpenSessionEx(nint sessionParams, ref nint encoder);

    [DllImport("d3d11.dll", EntryPoint = "D3D11CreateDevice", ExactSpelling = true)]
    private static extern int D3D11CreateDeviceNativeDiag(
        nint pAdapter, int driverType, nint software, uint flags,
        nint pFeatureLevels, uint featureLevelCount, uint sdkVersion,
        out nint ppDevice, out int pFeatureLevel, out nint ppImmediateContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NvEncCreateInstance(nint functionList);

    private static void ReportGpuEncoders()
    {
        try
        {
            var encoders = GpuCapability.DetectEncoders();
            if (encoders.Count == 0)
            {
                Console.WriteLine("  ⚠ 未探测到任何 GPU 编码器");
                return;
            }
            foreach (var e in encoders)
            {
                Console.WriteLine($"  {e.Type,-6} 可用={e.Available,-5} AV1={e.SupportsAv1,-5} " +
                                  $"HEVC={e.SupportsHevc,-5} {e.AdapterName}");
            }
            var nvAv1 = encoders.FirstOrDefault(x =>
                x.Type == GpuEncoderType.NVENC && x.Available && x.SupportsAv1);
            Console.WriteLine(nvAv1 is not null
                ? "  ✅ 检测到支持 AV1 编码的 NVIDIA GPU → Auto 应选中 Nvenc"
                : "  ⚠ 未检测到支持 AV1 编码的 NVIDIA GPU（RTX 40/50 系列才支持 AV1）");

            // ═══ 分步诊断：定位 NVENC 不可用的具体环节 ═══
            Console.WriteLine("\n  ── NVENC 分步诊断 ──");
            Console.WriteLine($"  DLL 存在: {NvEncoderNative.IsDllPresent}");
            ProbeNvencVersion();
            ProbeNvidiaDeviceCreation();
            try
            {
                // 步骤1: 默认适配器
                try
                {
                    using var d = Vortice.Direct3D11.D3D11.D3D11CreateDevice(
                        Vortice.Direct3D.DriverType.Hardware,
                        Vortice.Direct3D11.DeviceCreationFlags.BgraSupport);
                    using var nv = new NvEncoderNative(d);
                    Console.WriteLine("  ✅ 默认适配器: NVENC 会话打开成功");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ❌ 默认适配器失败: {ex.Message}");
                }

                // 步骤2: N 卡专用设备
                using var nvDev = NvEncoderNative.CreateNvidiaDevice();
                if (nvDev is null)
                {
                    Console.WriteLine("  ❌ CreateNvidiaDevice 返回 null（未找到 N 卡或创建设备失败）");
                }
                else
                {
                    Console.WriteLine("  ✅ CreateNvidiaDevice: 已在 N 卡上创建设备");
                    try
                    {
                        using var nv = new NvEncoderNative(nvDev);
                        Console.WriteLine("  ✅ N 卡设备: NVENC 会话打开成功");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  ❌ N 卡设备会话打开失败: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ❌ 诊断异常: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ 能力探测失败: {ex.Message}");
        }
    }

    // ═══════════════════════════════════════
    //  WGC 捕获
    // ═══════════════════════════════════════

    private static byte[]? CaptureFrame(out int w, out int h, out ID3D11Device? device)
    {
        w = 0; h = 0; device = null;

        ID3D11Device? dev = null;
        try
        {
            dev = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            device = dev;

            using var winrtDevice = CreateDirect3DDevice(dev);
            nint hmon = MonitorFromPoint(default, MONITOR_DEFAULTTOPRIMARY);
            // 注: GraphicsCaptureItem 不实现 IDisposable（其为 WinRT 对象，由 RCW 管理生命周期）
            var item = CreateItemForMonitor(hmon);
            if (item is null) return null;

            int iw = item.Size.Width, ih = item.Size.Height;
            if (iw <= 0 || ih <= 0) return null;

            byte[]? pixels = null;
            bool captured = false;

            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

            pool.FrameArrived += (sender, _) =>
            {
                if (captured) return;
                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is null) return;

                    using var surface = GetDxgiSurface(frame.Surface);
                    if (surface is null) return;
                    using var tex = surface.QueryInterface<ID3D11Texture2D>();
                    if (tex is null) return;

                    var ctx = dev.ImmediateContext;
                    using var staging = dev.CreateTexture2D(new Texture2DDescription
                    {
                        Width = (uint)iw, Height = (uint)ih, MipLevels = 1, ArraySize = 1,
                        Format = Vortice.DXGI.Format.B8G8R8A8_UNorm,
                        SampleDescription = new(1, 0),
                        Usage = ResourceUsage.Staging, BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                    });
                    ctx.CopyResource(staging, tex);
                    var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                    try
                    {
                        var buf = new byte[iw * ih * 4];
                        unsafe
                        {
                            byte* src = (byte*)mapped.DataPointer;
                            int srcPitch = (int)mapped.RowPitch;
                            int dstPitch = iw * 4;
                            fixed (byte* dst = buf)
                                for (int row = 0; row < ih; row++)
                                    Buffer.MemoryCopy(src + row * srcPitch, dst + row * dstPitch, dstPitch, dstPitch);
                        }
                        pixels = buf;
                    }
                    finally { ctx.Unmap(staging, 0); }

                    PixelOps.FixAlphaChannel(pixels!);
                    captured = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ⚠ 帧处理异常: {ex.Message}");
                }
            };

            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try { session.IsBorderRequired = false; } catch { }
            session.StartCapture();

            var sw = Stopwatch.StartNew();
            while (!captured && sw.ElapsedMilliseconds < 15000)
            {
                PumpMessages();
                Thread.Sleep(1);
            }

            if (!captured) return null;
            w = iw; h = ih;
            return pixels;
        }
        catch
        {
            dev?.Dispose();
            device = null;
            throw;
        }
    }

    // ═══════════════════════════════════════
    //  COM / WinRT 互操作
    // ═══════════════════════════════════════

    private static IDirect3DDevice CreateDirect3DDevice(ID3D11Device d3dDevice)
    {
        using var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>()
            ?? throw new InvalidOperationException("QI for IDXGIDevice 失败");

        var winrtGuid = typeof(IDirect3DDevice).GUID;
        int hr = Marshal.QueryInterface(dxgiDevice.NativePointer, in winrtGuid, out var winrtPtr);
        if (hr >= 0 && winrtPtr != 0)
            return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);

        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var d3d11Ptr);
        if (hr < 0 || d3d11Ptr == 0)
            throw new InvalidOperationException($"CreateDirect3D11DeviceFromDXGIDevice 失败 hr=0x{hr:X8}");

        hr = Marshal.QueryInterface(d3d11Ptr, in winrtGuid, out winrtPtr);
        Marshal.Release(d3d11Ptr);
        if (hr < 0 || winrtPtr == 0)
            throw new InvalidOperationException($"回退 QI 失败 hr=0x{hr:X8}");
        return MarshalInterface<IDirect3DDevice>.FromAbi(winrtPtr);
    }

    /// <summary>从 HMONITOR 创建 GraphicsCaptureItem。
    /// <para>
    /// 与生产代码 WgcCaptureService.CreateItemForMonitor 保持一致的三级回退：
    /// ① CsWinRT As&lt;T&gt;() ② RoGetActivationFactory + vtable ③ TryCreateFromWindowId。
    /// </para>
    /// <para>
    /// ⚠ IGraphicsCaptureItemInterop 是**激活工厂接口**，不在 GraphicsCaptureItem 实例上，
    /// 因此 As&lt;T&gt;() 通常失败（"Specified cast is not valid"），真正生效的是方法 ②。
    /// </para>
    /// </summary>
    private static GraphicsCaptureItem? CreateItemForMonitor(nint hmonitor)
    {
        // ═══ 使用 WinRT 托管 API，避免 COM 互操作 ═══
        // 生产代码 WgcCaptureService 用 IGraphicsCaptureItemInterop.CreateForMonitor 精确匹配显示器，
        // 但该路径需要 RoGetActivationFactory + HSTRING（combase.dll P/Invoke）。
        // 本测试只需"任一真实桌面帧"用于编码验证，故采用更简单的托管路径：
        // Windows.UI.WindowId 的值就是 HWND（WinUI 的 Win32Interop.GetWindowIdFromWindow 亦如此实现），
        // 直接以桌面窗口句柄构造即可，无需任何 COM 互操作。

        foreach (var hwnd in new[] { GetShellWindow(), GetDesktopWindow() })
        {
            if (hwnd == 0) continue;
            try
            {
                var windowId = new Windows.UI.WindowId((ulong)hwnd);
                var item = GraphicsCaptureItem.TryCreateFromWindowId(windowId);
                if (item is not null && item.Size.Width > 0 && item.Size.Height > 0)
                    return item;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ⚠ TryCreateFromWindowId(0x{hwnd:X}) 失败: {ex.Message}");
            }
        }

        Console.WriteLine("  ⚠ 无法创建 GraphicsCaptureItem（桌面窗口捕获失败）");
        return null;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint GetDesktopWindow();

    private static IDXGISurface? GetDxgiSurface(IDirect3DSurface surface)
    {
        // IDirect3DDxgiInterfaceAccess::GetSurface
        try
        {
            if (surface is IWinRTObject winrtObj)
            {
                nint thisPtr = winrtObj.NativeObject.GetRef();
                try
                {
                    var accessGuid = new Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
                    int hr = Marshal.QueryInterface(thisPtr, in accessGuid, out var accessPtr);
                    if (hr >= 0 && accessPtr != 0)
                    {
                        try
                        {
                            var dxgiGuid = typeof(IDXGISurface).GUID;
                            hr = VtblCall3(accessPtr, ref dxgiGuid, out var dxgiPtr);
                            if (hr >= 0 && dxgiPtr != 0)
                                return new IDXGISurface(dxgiPtr);
                        }
                        finally { Marshal.Release(accessPtr); }
                    }
                }
                finally { Marshal.Release(thisPtr); }
            }
        }
        catch { }
        return null;
    }

    private static int VtblCall3(nint pThis, ref Guid riid, out nint result)
    {
        nint vtable = Marshal.ReadIntPtr(pThis);
        nint methodPtr = Marshal.ReadIntPtr(vtable + 3 * nint.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<GetSurfaceDelegate>(methodPtr);
        return fn(pThis, ref riid, out result);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSurfaceDelegate(nint pThis, ref Guid riid, out nint result);

    private static void PumpMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    private const int MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint d3d11Device);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(POINT pt, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(ref MSG lpMsg);

    [LibraryImport("user32.dll")]
    private static partial nint DispatchMessageW(ref MSG lpMsg);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam, lParam;
        public uint time;
        public POINT pt;
    }

    private static void Assert(string name, bool condition, string detail = "")
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name} — {detail}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name} — {detail}"); }
    }
}
