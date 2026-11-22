// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/JpegLiNative.cs
// Google jpegli 编码器 —— 双路径：
//   主路径: ttc_jpegli.dll（P/Invoke 进程内编码，2026-11-12 起引入）
//           shim 源码在 scripts/jpegli-shim/，构建配方 scripts/build-cjpegli.py；
//           shim 内部走 DecodeBytes → EncodeJpeg，与 cjpegli.exe 完全同一条上游
//           代码路径，且采纳前有逐字节对照硬门禁（12 参数组合 + ICC 回读）。
//           附带收益：零临时文件 I/O（旧路径要写 11MB PPM 到 %TEMP%）、
//           2-3× 端到端提速（87ms vs 249ms @2560×1440 d0.5）、ICC 原生嵌入。
//   回退:   cjpegli.exe 子进程（与 avifenc/cwebp 相同的嵌入式工具链模式，
//           崩溃隔离）。DLL 缺失/装载失败/编码异常时自动落回此路。
// 产物一致性：lib 与 exe 同参数输出逐字节相同（门禁保证）；回退路径产出的
//   JPEG 仍合法，仅当回退件与 DLL 的 jpegli commit 不同时字节可能有微差。

using System.Globalization;
using System.Runtime.InteropServices;
using TrueToneCap.Core.ColorManagement;
using TrueToneCap.Core.Logging;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Core.Encoding;

/// <summary>
/// jpegli 编码器 —— ttc_jpegli.dll 进程内编码为主，cjpegli.exe 子进程为回退。
/// DLL 与 exe 同由 scripts/build-cjpegli.py 从 google/jpegli 源码树产出并嵌入
/// Resources/（经 NativeLibraryResolver 提取到 native/ 目录）。
/// </summary>
public static class JpegLiNative
{
    private static bool? _available;      // lib 或 exe 任一可用
    private static bool? _libAvailable;   // ttc_jpegli.dll 装载成功
    private static readonly object _libLock = new();
    private static IntPtr _libHandle;

    // ── ttc_jpegli.dll 的 C-ABI 绑定（scripts/jpegli-shim/ttc_shim.cc）──
    // 输出缓冲在本 DLL 内 malloc，必须经 ttc_jpegli_free 归还（同 CRT）；
    // 失败详情经 ttc_jpegli_error 取回（单线程语义，编码调用天然串行）。

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EncodeRgbFn(byte[] rgb, int w, int h, float distance,
        [MarshalAs(UnmanagedType.LPStr)] string chroma, int progressiveLevel,
        int xyb, byte[]? icc, nuint iccLen, out IntPtr jpeg, out nuint jpegLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EncodeGrayFn(byte[] gray, int w, int h, float distance,
        int progressiveLevel, int xyb, out IntPtr jpeg, out nuint jpegLen);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FreeFn(IntPtr p);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr ErrFn();

    private static EncodeRgbFn? _encodeRgb;
    private static EncodeGrayFn? _encodeGray;
    private static FreeFn? _free;
    private static ErrFn? _error;

    /// <summary>检测 JPEG LI 编码是否可用（lib 主路径或 exe 回退任一）。</summary>
    public static bool IsAvailable
    {
        get
        {
            if (_available.HasValue) return _available.Value;
            try
            {
                if (TryLoadLib()) { _available = true; return true; }
            }
            catch { /* 落到 exe 探针 */ }

            // exe 回退探针（原逻辑保留）：cjpegli.exe 先在 native 目录找，再回退 PATH。
            try
            {
                var exePath = GetExePath();
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-h");
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is null) { _available = false; return false; }
                bool probed = NativeProcess.DrainProbe(proc, NativeProcess.ProbeTimeoutMs,
                    out _, out _, out _);
                // cjpegli -h 返回 exit code 1，但我们只关心能否启动（启动即须自行退出）
                _available = probed;
            }
            catch { _available = false; }
            return _available.Value;
        }
    }

    /// <summary>ttc_jpegli.dll 是否装载成功（进程内主路径在用）。
    /// 测试与自检用它区分「真走了主路径」与「静默回退到 exe」——
    /// 行为级测试若不知情地落在回退路径上，等于永远测不到产品主路径（假绿）。</summary>
    public static bool IsLibAvailable => TryLoadLib();

    /// <summary>初始化检测。</summary>
    public static void Initialize()
    {
        if (TryLoadLib())
        {
            string? ver = _version is null ? null : Marshal.PtrToStringAnsi(_version());
            CoreLog.Info("JpegLiNative",
                $"ttc_jpegli.dll 就绪（进程内主路径{(ver is null ? "" : $"，shim {ver}")}）",
                LogCategory.Encoding);
        }
        else if (IsAvailable)
            CoreLog.Info("JpegLiNative", "cjpegli.exe 就绪（DLL 不可用，子进程回退模式）",
                LogCategory.Encoding);
        else
            CoreLog.Warn("JpegLiNative", "JPEG LI 编码器不可用（DLL 与 exe 均缺失）",
                LogCategory.Encoding);
    }

    private static VersionFn? _version;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VersionFn();

    /// <summary>
    /// 装载 ttc_jpegli.dll 并解析全部导出（幂等；任一导出缺失视为不可用并卸载）。
    /// 只在 native 目录找——shim 与产品同仓发布，不走 PATH 兜底（exe 才有 PATH 回退）。
    /// </summary>
    private static bool TryLoadLib()
    {
        if (_libAvailable.HasValue) return _libAvailable.Value;
        lock (_libLock)
        {
            if (_libAvailable.HasValue) return _libAvailable.Value;
            _libAvailable = false;
            try
            {
                NativeLibraryResolver.Initialize();
                string dllPath = Path.Combine(NativeLibraryResolver.GetNativeDirectory(),
                    "ttc_jpegli.dll");
                if (File.Exists(dllPath)
                    && NativeLibrary.TryLoad(dllPath, out IntPtr h)
                    && ResolveExports(h))
                {
                    _libHandle = h;
                    _libAvailable = true;
                }
            }
            catch { _libAvailable = false; }

            if (_libAvailable.Value)
                CoreLog.Info("JpegLiNative", "ttc_jpegli.dll 就绪（进程内主路径）",
                    LogCategory.Encoding);
            else
                CoreLog.Info("JpegLiNative",
                    "ttc_jpegli.dll 不可用，JPEG LI 编码走 cjpegli.exe 子进程回退",
                    LogCategory.Encoding);
            return _libAvailable.Value;
        }
    }

    private static bool ResolveExports(IntPtr handle)
    {
        _encodeRgb = GetProc<EncodeRgbFn>(handle, "ttc_jpegli_encode_rgb");
        _encodeGray = GetProc<EncodeGrayFn>(handle, "ttc_jpegli_encode_gray");
        _free = GetProc<FreeFn>(handle, "ttc_jpegli_free");
        _error = GetProc<ErrFn>(handle, "ttc_jpegli_error");
        _version = GetProc<VersionFn>(handle, "ttc_jpegli_version");
        bool ok = _encodeRgb is not null && _encodeGray is not null
                  && _free is not null && _error is not null;
        if (!ok)
        {
            _encodeRgb = null; _encodeGray = null; _free = null; _error = null; _version = null;
            NativeLibrary.Free(handle);
            CoreLog.Warn("JpegLiNative", "ttc_jpegli.dll 导出不全，拒载", LogCategory.Encoding);
        }
        return ok;
    }

    private static T? GetProc<T>(IntPtr handle, string name) where T : class
    {
        try
        {
            IntPtr p = NativeLibrary.GetExport(handle, name);
            return Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }
        catch
        {
            return null;
        }
    }

    private static string GetExePath()
    {
        try { return NativeLibraryResolver.GetExePath("cjpegli.exe"); }
        catch
        {
            // 回退到 PATH
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("cjpegli.exe")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-h");
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc is not null
                    && NativeProcess.DrainProbe(proc, NativeProcess.ProbeTimeoutMs, out int ec, out _, out _)
                    && ec == 0) return "cjpegli.exe";
            }
            catch { }
            throw new DllNotFoundException("[jpegli] cjpegli.exe 未找到。请将 cjpegli.exe 放入 native/ 目录或系统 PATH。");
        }
    }

    /// <summary>BGRA8→JPEG 编码（支持色度采样和 ICC 嵌入）。</summary>
    /// <param name="bgra">BGRA8 像素数据。</param>
    /// <param name="width">宽度。</param>
    /// <param name="height">高度。</param>
    /// <param name="distance">butteraugli 距离 (0.5~=无损, 1.0=高质量, 3.0=低质量)。</param>
    /// <param name="chromaSubsampling">色度采样: "444" / "422" / "420"。</param>
    /// <param name="iccProfile">可选 ICC Profile。<b>只有非 sRGB 会嵌入</b>：与仓库标准
    /// sRGB profile 相同的字节一律不嵌（sRGB 是 JPEG 隐含默认，嵌纯增体积；
    /// 2026-11-12 用户拍板，lib 主路径由上游 IsSRGB 语义兜底）。</param>
    /// <param name="forceBaseline">强制 Baseline Sequential (SOF0)。Gain Map Base JPEG 需要此模式。</param>
    /// <param name="xyb">色彩空间开关：true = XYB（体积通常更小，但少数软件无法正确解码），
    /// false = 传统 YCbCr（默认，兼容最好）。2026-11-12 用户拍板：xyb 不属于压缩强度档位
    ///（2026-10-07~2026-11-12 间它挂在档位映射上，「最小体积」档恒加），
    /// 改为独立的「色彩空间」用户开关；增益图底图同样适用，增益图层恒 false。</param>
    /// <returns>JPEG 字节流 (FFD8...FFD9)。</returns>
    public static byte[] Encode(byte[] bgra, int width, int height, float distance = 1.0f,
        string chromaSubsampling = "444", byte[]? iccProfile = null, bool forceBaseline = false,
        bool xyb = false)
    {
        // ═══ 参数规范化（lib/exe 两路径共用；门禁钉住这些代码形态）═══
        // cjpegli 的 --distance 参数是 butteraugli distance（浮点，越小画质越好），
        // 官方允许范围 [0.0, 25.0]；产品档位是 UI 滑块的 0.5-3.0（默认 0.5 —— 2026-11-06
        // 用户拍板由 1.0 收紧，物证见 quality-chain-evidence §JPEG LI：420 色度下 d=1.0 重度块效应）。
        // ⚠ 2026-11-06 量纲注释更正：上游（FormatEncoders / JpegGainMapEncoder）传的是
        //   每格式质量字段 QualityJpegLi —— 量纲即 distance（AppSettingsQuality.GetQuality）。
        //   旧注释"调用方可能传 quality 值 (0-100)"描述的是滑块覆盖时代（MainWindow
        //   BuildEncodingSettings 的二源覆盖）的形态，该覆盖已收口删除，不再成立。
        //   传整数 90 仍会被解释为 d90.000（超出范围导致错误），故保留 Clamp[0,25] 防御；
        //   钳制发生（输入 <0 或 >25，必然同时超出产品档位 0.5-3.0）必属量纲异常 ⇒
        //   Warn 留痕：把"静默钳到 25 = 最差画质"（2026-11-06 探针实测：PNG 量纲 100 →
        //   d25.0000，产物仅为 d0.5 的 1/4.25）变成"留痕钳制"。正常档位不会触发。
        float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);
        if (distance < 0.0f || distance > 25.0f)
        {
            CoreLog.Warn("JPEGLI",
                $"distance {distance:F4} 超出 cjpegli 合法范围 [0,25]，已钳到 {clampedDist:F4}"
                + "（超出产品档位 0.5-3.0 ⇒ 疑似量纲错位，请检查上游质量来源）", LogCategory.Encoding);
        }
        string distArg = $"{clampedDist:F4}";
        string chromaArg = chromaSubsampling switch
        {
            "420" => " --chroma_subsampling 420",
            "422" => " --chroma_subsampling 422",
            _ => "" // 444 默认
        };
        // Gain Map 需要 Baseline (SOF0)，常规 JPEG 用 Progressive (SOF2) 获得更小体积
        // --progressive_level 2: 默认 Progressive，体积小 5-10%
        // --progressive_level 0: Baseline，Gain Map 兼容性要求
        string progArg = forceBaseline ? " --progressive_level 0" : ""; // 默认 Progressive(2) 更省体积
        // ═══ 2026-11-12：--xyb 改由独立的「色彩空间」开关决定（用户拍板）═══
        // 此前（2026-10-07~2026-11-12）它挂在压缩强度档位映射上：「最小体积」档恒加。
        // 实测数据（保留备查）：2026-10-07 两张真实截图 × -d 0.5/1.0/2.0/3.0 全更小
        //   （样张 A −4.7%/−2.9%/−3.8%/−4.6%，样张 B −4.0%/−2.4%/−3.5%）；
        //   2026-10-29 换三张复测（-d 1.0）：−5.5% / −10.4% / 【+3.0%】反例 ⇒"一律更小"不成立，
        //   数据见 artifacts/tier_bench_2026-10-29.log。代价约 1.2~1.3× 时间。
        // 机理：XYB 是感知均匀色彩空间，量化误差按人眼敏感度分布 ⇒ 同感知质量下通常更省码率。
        // ⚠ 兼容性：XYB 编码的 JPEG 会被小部分软件解码出错（用户实测反馈）⇒ 默认传统，
        //   XYB 由用户显式选入。
        string xybArg = xyb ? " --xyb" : "";
        // 两条路径共用的参数语义串：exe 路径逐字进命令行，lib 路径逐字进 Debug 留痕。
        // Test_QualityChain_JpegLiDistanceVerbatim 行为腿咬住 --distance <F4> 的逐字出现。
        string argsFragment = $"--distance {distArg}{progArg}{xybArg}{chromaArg}";

        // ═══ ICC 嵌入口径（2026-11-12 用户拍板）：只有非 sRGB 才嵌 ═══
        // sRGB 是 JPEG 的隐含默认，嵌 2100B 标准 profile 纯增体积（小图上占比可达数十个百分点）。
        // 归一化做在 Encode 入口，lib 主路径与 exe 回退路径吃到同一份决定 ⇒ 两路径产物形态一致：
        //   · 与仓库标准 sRGB profile 逐字节相等 ⇒ 视同未传（产品的 sRGB 判据）；
        //   · 其余 profile 交给结构判据 IsEmbeddableIcc 过筛（CMYK/残件拒嵌）后嵌入；
        //   · 语义层的兜底在 lib 侧：非标准字形的 sRGB-ish profile 由上游 IsSRGB() 判定省略。
        // 唯一给 Encode 传标准 sRGB ICC 的调用方是 GainMap 底图（PrepareGainMapColorSpace），
        // 该处已改为直传 null（同一拍板）；这里的归一化是防"谁再塞回来"的收口闸。
        byte[]? iccToEmbed = null;
        if (iccProfile is not null
            && !iccProfile.AsSpan().SequenceEqual(ColorProfileProvider.GetDefaultSRgbIcc()))
        {
            iccToEmbed = iccProfile;
        }
        bool hasNonSrgbIcc = iccToEmbed is not null && FormatHelper.IsEmbeddableIcc(iccToEmbed);

        // ═══ 主路径：ttc_jpegli.dll 进程内编码（2026-11-12）═══
        if (TryLoadLib())
        {
            try
            {
                CoreLog.Debug("JPEGLI", $"lib: {argsFragment}", LogCategory.Encoding);
                return EncodeViaLib(bgra, width, height, clampedDist, chromaSubsampling,
                    forceBaseline, xyb, hasNonSrgbIcc ? iccToEmbed : null);
            }
            catch (Exception ex)
            {
                CoreLog.Warn("JPEGLI", "ttc_jpegli.dll 编码失败，回退 cjpegli.exe 子进程",
                    ex, LogCategory.Encoding);
            }
        }

        // ═══ 回退路径：cjpegli.exe 子进程 ═══
        return EncodeViaExe(bgra, width, height, clampedDist, chromaSubsampling, forceBaseline, xyb,
            hasNonSrgbIcc, iccToEmbed);
    }

    /// <summary>lib 主路径：BGRA→RGB24 内存转换后直调 shim（零临时文件）。</summary>
    private static byte[] EncodeViaLib(byte[] bgra, int width, int height, float distance,
        string chromaSubsampling, bool forceBaseline, bool xyb, byte[]? icc)
    {
        var rgb = ConvertBgraToRgb(bgra, width, height);
        // chroma 归一化：shim 只认 444/440/422/420，其余（含 null/空/未知）按 444，
        // 与 exe 路径的 switch 默认分支同语义。
        string chroma = chromaSubsampling switch
        {
            "420" => "420",
            "422" => "422",
            "440" => "440",
            _ => "444"
        };
        int rc = _encodeRgb!(rgb, width, height, distance, chroma,
            forceBaseline ? 0 : 2, xyb ? 1 : 0,
            icc, icc is null ? NUInt0 : (nuint)icc.Length,
            out IntPtr jpegPtr, out nuint jpegLen);
        if (rc != 0)
        {
            string err = Marshal.PtrToStringAnsi(_error!()) ?? "";
            throw new InvalidOperationException(
                $"[jpegli] ttc_jpegli.dll 编码失败 (rc={rc}) {err.Trim()}");
        }
        try
        {
            var jpeg = new byte[checked((int)jpegLen)];
            Marshal.Copy(jpegPtr, jpeg, 0, jpeg.Length);
            // ICC 口径（2026-11-12 用户拍板）：上游 EncodeJpeg 对 IsSRGB() 的 profile
            // 省略 APP2 嵌入——这正是产品要的行为（sRGB 不嵌，非 sRGB 由库原生按
            // ICC.1 规范分块写 APP2）。sRGB 判据已在 Encode 入口归一化
            // （标准 sRGB 字节 → null），这里不需要也不许再补嵌。
            return jpeg;
        }
        finally
        {
            _free!(jpegPtr);
        }
    }

    private static readonly nuint NUInt0 = 0;

    /// <summary>BGRA8 → 紧凑 RGB24（与 WriteBgraToPpm 的行转换同构，改为内存版）。</summary>
    private static byte[] ConvertBgraToRgb(byte[] bgra, int w, int h)
    {
        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4;
            int dst = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                rgb[dst++] = bgra[src + 2]; // R
                rgb[dst++] = bgra[src + 1]; // G
                rgb[dst++] = bgra[src];     // B
                src += 4;
            }
        }
        return rgb;
    }

    /// <summary>cjpegli.exe 子进程回退路径（参数全部走 ArgumentList，避免命令行字符串拼接）。</summary>
    private static byte[] EncodeViaExe(byte[] bgra, int width, int height,
        float clampedDist, string chromaSubsampling, bool forceBaseline, bool xyb,
        bool hasNonSrgbIcc, byte[]? iccProfile)
    {
        var exePath = GetExePath();

        // 写入临时 PPM 文件（cjpegli 支持 PPM 输入）
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jpegli_{Guid.NewGuid():N}.ppm");
        // 写入临时 ICC 文件
        string? tmpIcc = null;

        try
        {
            WriteBgraToPpm(bgra, width, height, tmpPpm);
            if (hasNonSrgbIcc)
            {
                tmpIcc = Path.Combine(Path.GetTempPath(), $"ttc_icc_{Guid.NewGuid():N}.icc");
                File.WriteAllBytes(tmpIcc, iccProfile!);
            }

            var (safeTmpPpm, safeOutPath) = Core.Security.PathSanitizer.SanitizePair(tmpPpm, tmpPpm + ".jpg");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--distance");
            psi.ArgumentList.Add(clampedDist.ToString("F4", CultureInfo.InvariantCulture));
            if (forceBaseline)
            {
                psi.ArgumentList.Add("--progressive_level");
                psi.ArgumentList.Add("0");
            }
            if (xyb)
                psi.ArgumentList.Add("--xyb");
            if (chromaSubsampling is "420" or "422")
            {
                psi.ArgumentList.Add("--chroma_subsampling");
                psi.ArgumentList.Add(chromaSubsampling);
            }
            psi.ArgumentList.Add(safeTmpPpm);
            psi.ArgumentList.Add(safeOutPath);

            // 2026-08-10 诊断代码（无条件写 %TEMP%\ttc_encoder_cmd.log）已移除：
            // 该日志在 Release 下也会持续写入并最终无限增长，且命令行只在排查时才有意义。
            // 需要排查参数传递时用 Debug 输出即可。
            CoreLog.Debug("JPEGLI", $"{psi.FileName} {NativeProcess.FormatArgsForLog(psi.ArgumentList)}", LogCategory.Encoding);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 cjpegli");
            var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 30_000, "cjpegli");

            if (exitCode != 0 || !File.Exists(safeOutPath))
                throw new InvalidOperationException($"[jpegli] cjpegli 失败 (exit={exitCode}) {stderr.Trim()}");

            var jpegBytes = File.ReadAllBytes(safeOutPath);

            // 后处理注入 ICC: 插在 APP0(JFIF) 之后，形态是 APP2 + 分块字段（见 InjectIccIntoJpeg）
            // cjpegli 不支持 --icc_profile，需要手动注入
            if (hasNonSrgbIcc && tmpIcc is not null)
            {
                jpegBytes = InjectIccIntoJpeg(jpegBytes, iccProfile!);
            }

            return jpegBytes;
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            try { File.Delete(tmpPpm + ".jpg"); } catch { }
            if (tmpIcc is not null) try { File.Delete(tmpIcc); } catch { }
        }
    }

    /// <summary>
    /// 把 ICC 以 <b>APP2 + 分块字段</b> 注入 JPEG —— 即 ICC.1（ISO 12233）规定的 JPEG 存放方式。
    /// </summary>
    /// <remarks>
    /// <para>每段布局：<c>FF E2</c> | <c>len(2B，含自身)</c> | <c>"ICC_PROFILE\0"(12B)</c> |
    /// <c>seq_no(1 基)</c> | <c>chunk_cnt</c> | 该块 profile 数据。</para>
    /// <para>权威依据：libjpeg-turbo <c>src/jcicc.c</c>（<c>ICC_MARKER = JPEG_APP0+2</c>，
    /// 标识符后依次写 seq 与 num_markers）与 <c>src/jdicc.c</c>（<c>data[12]=seq_no</c>、
    /// <c>data[13]=num_markers</c>）；Pillow <c>JpegImagePlugin</c> 同口径读 APP2。</para>
    /// <para>⚠ 为什么这次必须动它（2026-09-20，交接 §31.3-8）：本函数此前写 <b>APP1</b> 且<b>缺</b>
    /// 那两个分块字节，而 libjpeg-turbo / ImageMagick / Chromium-Skia / Pillow 只扫 APP2
    /// 并校验分块字段 ⇒ 实测这四家读我们产出的广色域 JPEG 一律得到"无 ICC"，
    /// 色域声明在最主流的读取链上<b>静默丢失</b>（不是"不合规但能看"）。</para>
    /// <para>分块不是假想需求：段长字段是 16-bit ⇒ 单段载荷上限 65519 B，而带 LUT 的显示器
    /// profile 可以超过 64KB ⇒ 超过就必须切成多段，每段的 cnt 等于总段数、seq 从 1 递增。</para>
    /// <para>插入位置在 APP0(JFIF) 之后：JFIF 要求 APP0 紧跟 SOI，ICC 惯例排在 APP0 后面，
    /// 旧实现直接插在 SOI 之后会把 cjpegli 自带的 APP0 挤到 ICC 之后。</para>
    /// <para>⚠ 2026-11-12 起 exe 回退路径专用；lib 主路径由 jpegli 原生嵌入（同规范）。</para>
    /// </remarks>
    internal static byte[] InjectIccIntoJpeg(byte[] jpeg, byte[] iccProfile)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return jpeg;

        const int iccIdLen = 12;                                    // "ICC_PROFILE\0"
        const int maxChunk = 65535 - 2 - iccIdLen - 2;              // 65519 B 载荷/段
        int chunkCnt = Math.Max(1, (iccProfile.Length + maxChunk - 1) / maxChunk);

        using var ms = new MemoryStream(jpeg.Length + iccProfile.Length + 16 * chunkCnt);
        int at = WriteSoiAndApp0(jpeg, ms);

        for (int i = 0; i < chunkCnt; i++)
        {
            int off = i * maxChunk;
            int len = Math.Min(maxChunk, iccProfile.Length - off);
            int segLen = 2 + iccIdLen + 2 + len;
            ms.WriteByte(0xFF);
            ms.WriteByte(0xE2);                        // APP2 = ICC 标记（不是 APP1）
            ms.WriteByte((byte)(segLen >> 8));
            ms.WriteByte((byte)(segLen & 0xFF));
            ms.Write(System.Text.Encoding.ASCII.GetBytes("ICC_PROFILE\0"));
            ms.WriteByte((byte)(i + 1));               // seq_no：1 基
            ms.WriteByte((byte)chunkCnt);              // chunk_cnt
            ms.Write(iccProfile, off, len);
        }
        ms.Write(jpeg, at, jpeg.Length - at);
        return ms.ToArray();
    }

    /// <summary>原样写出 SOI（以及紧随其后的 APP0/JFIF 段），返回 JPEG 中尚未写出的起始位置。</summary>
    private static int WriteSoiAndApp0(byte[] jpeg, Stream outStream)
    {
        outStream.Write(jpeg, 0, 2);
        int at = 2;
        // 只越过 APP0；其余段（含 cjpegli 可能自带的段）保持原顺序跟在 ICC 之后
        if (at + 4 <= jpeg.Length && jpeg[at] == 0xFF && jpeg[at + 1] == 0xE0)
        {
            int seg = (jpeg[at + 2] << 8) | jpeg[at + 3];
            if (seg >= 2 && at + 2 + seg <= jpeg.Length) at += 2 + seg;
        }
        outStream.Write(jpeg, 2, at - 2);
        return at;
    }

    /// <summary>将 BGRA8 像素写入 PPM 文件（彩色 P6 格式）。exe 回退路径专用。</summary>
    private static void WriteBgraToPpm(byte[] bgra, int w, int h, string path)
    {
        // 逐字节 WriteByte 在 4K 上是 2490 万次调用；按行组一个 RGB 缓冲一次写出。
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        var header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);

        var row = new byte[w * 3];
        for (int y = 0; y < h; y++)
        {
            int src = y * w * 4;
            int dst = 0;
            for (int x = 0; x < w; x++)
            {
                row[dst++] = bgra[src + 2]; // R
                row[dst++] = bgra[src + 1]; // G
                row[dst++] = bgra[src];     // B
                src += 4;
            }
            fs.Write(row, 0, row.Length);
        }
    }

    /// <summary>灰度 BGRA8 像素编码为 1 分量灰度 JPEG (YCbCr400)。
    /// ═══ 2026-08-10 新增: GainMap 单通道增益图必须用真灰度 JPEG ═══
    /// 对齐 libultrahdr (jpegr.cpp): 单通道增益图 = UHDR_IMG_FMT_8bppYCbCr400,
    /// 解码器 (MS Photos/Android) 按 JPEG 分量数判断增益图通道数:
    ///   - 1 分量灰度 → 单通道增益, 应用到所有通道 (规范要求!)
    ///   - 3 分量 RGB → 每通道独立增益 (即使像素相等也可能被当作多通道处理 → 偏色)
    /// 输入: 单通道灰度像素 (每像素 1 byte)。</summary>
    public static byte[] EncodeGray(byte[] gray, int width, int height,
        float distance = 1.0f, bool forceBaseline = false,
        bool xyb = false)
    {
        // 最终防线（2026-11-06 裁决：不加 Warn 留痕）：上游唯一调用链
        // JpegGainMapEncoder 主路已 Clamp(settings.Quality, 0.0f, 25.0f) 后传入，
        // 输出域恰好等于这里的 [0,25] ⇒ 本 Clamp 理论不可达，Warn 将是死代码。
        // 主路 Encode 的调用面宽（FormatEncoders 直链在调用点级无钳制，只靠字段级
        // Normalize 兜底），那里的 Warn 哨兵有真实价值；灰度路若上游契约真被破坏，
        // 下方 JPEGLI_GRAY 留痕里的 --distance 会显出异常值作间接痕迹。
        float clampedDist = Math.Clamp(distance, 0.0f, 25.0f);
        string progArg = forceBaseline ? " --progressive_level 0" : "";
        // 增益图层（灰度/彩色）恒传统 YCbCr：调用方不传 xyb（libultrahdr 解析端按标准
        // JPEG 读增益图，XYB 徒增解码风险；2026-10-07 实测 -p 0 路省 1.5%，收益不值得）。
        string xybArg = xyb ? " --xyb" : "";

        // ═══ 主路径：ttc_jpegli.dll（灰度直传 shim，PGM 容器在 shim 内构造）═══
        if (TryLoadLib())
        {
            try
            {
                CoreLog.Debug("JPEGLI_GRAY",
                    $"lib: --distance {clampedDist:F4}{progArg}{xybArg}", LogCategory.Encoding);
                int rc = _encodeGray!(gray, width, height, clampedDist,
                    forceBaseline ? 0 : 2, xyb ? 1 : 0,
                    out IntPtr jpegPtr, out nuint jpegLen);
                if (rc != 0)
                {
                    string err = Marshal.PtrToStringAnsi(_error!()) ?? "";
                    throw new InvalidOperationException(
                        $"[jpegli] ttc_jpegli.dll 灰度编码失败 (rc={rc}) {err.Trim()}");
                }
                try
                {
                    var jpeg = new byte[checked((int)jpegLen)];
                    Marshal.Copy(jpegPtr, jpeg, 0, jpeg.Length);
                    return jpeg;
                }
                finally
                {
                    _free!(jpegPtr);
                }
            }
            catch (Exception ex)
            {
                CoreLog.Warn("JPEGLI_GRAY", "ttc_jpegli.dll 灰度编码失败，回退 cjpegli.exe 子进程",
                    ex, LogCategory.Encoding);
            }
        }

        // ═══ 回退路径：cjpegli.exe 子进程 ═══
        var exePath = GetExePath();
        var tmpPpm = Path.Combine(Path.GetTempPath(), $"ttc_jpegli_gray_{Guid.NewGuid():N}.pgm");

        try
        {
            // PPM P5 灰度格式: "P5\n{w} {h}\n255\n" + 每像素 1 字节
            using (var fs = new FileStream(tmpPpm, FileMode.Create, FileAccess.Write))
            {
                using var writer = new System.IO.StreamWriter(fs, System.Text.Encoding.ASCII, leaveOpen: true);
                writer.Write($"P5\n{width} {height}\n255\n");
                writer.Flush();
                fs.Write(gray, 0, gray.Length);
            }

            var (safeTmpPpm, safeOutPath) = Core.Security.PathSanitizer.SanitizePair(tmpPpm, tmpPpm + ".jpg");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("--distance");
            psi.ArgumentList.Add(clampedDist.ToString("F4", CultureInfo.InvariantCulture));
            if (forceBaseline)
            {
                psi.ArgumentList.Add("--progressive_level");
                psi.ArgumentList.Add("0");
            }
            if (xyb)
                psi.ArgumentList.Add("--xyb");
            psi.ArgumentList.Add(safeTmpPpm);
            psi.ArgumentList.Add(safeOutPath);

            CoreLog.Debug("JPEGLI_GRAY", $"{psi.FileName} {NativeProcess.FormatArgsForLog(psi.ArgumentList)}", LogCategory.Encoding);

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) throw new InvalidOperationException("无法启动 cjpegli");
            var (exitCode, _, stderr) = NativeProcess.DrainAndFinish(proc, 30_000, "cjpegli(gray)");

            if (exitCode != 0 || !File.Exists(safeOutPath))
                throw new InvalidOperationException($"[jpegli] 灰度编码失败 (exit={exitCode}) {stderr.Trim()}");

            return File.ReadAllBytes(safeOutPath);
        }
        finally
        {
            try { File.Delete(tmpPpm); } catch { }
            try { File.Delete(tmpPpm + ".jpg"); } catch { }
        }
    }
}
