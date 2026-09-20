// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Models/AppSettingsData.cs
// 应用设置数据模型 — 独立文件，支持 JSON 序列化（AOT 兼容）
// 所有属性必须有合理的默认值，新属性必须在此处添加默认值

using System.Text.Json.Serialization;

namespace TrueToneCap.App.Models;

/// <summary>应用设置数据模型。</summary>
public sealed class AppSettingsData
{
    // 2026-09-15 清理：原 SettingsVersion 字段已删除。
    //   它注释称"用于设置迁移，每次不兼容改动时递增"，但全仓零引用 —— 迁移机制从未实现，
    //   留着只是一个会误导后来者以为"改默认值时该递增它"的空字段。
    //   当前新增设置项靠 C# 属性初始化器兜底（旧 settings.json 缺该字段 → 用代码默认值），
    //   配合 SettingsService.Normalize() 修正越界值，已能覆盖实际需求。
    //   若将来真需要"把旧值转换成新值"的迁移，届时应连同迁移逻辑一起引入，而不是先加个空版本号。

    /// <summary>
    /// 取一份**按下截图那一刻**的设置快照，交给后台编码线程使用。
    /// <para>为什么需要：本类是可变单例（<c>SettingsService.Current</c>），UI 事件会在截图后
    /// 0.5–2s 内（JXL / libaom AVIF 的编码耗时量级）就地改写字段；而
    /// <c>CapturePipelineService.BuildEncodingSettings</c> 需要读位深/色度/AVIF 编码器选择等
    /// 七项每格式参数。读活对象 = 本次产物可能用上"按下之后才改的选择"，
    /// 也正是 §11.3 建 <c>EncodingUiState</c> 快照要杜绝的那类跨线程读取。</para>
    /// <para>浅拷贝在此处即等价于真快照：本类字段全是标量/字符串（无集合、无数组），
    /// 新增引用类型字段时必须改成深拷贝。</para>
    /// </summary>
    public AppSettingsData Snapshot() => (AppSettingsData)MemberwiseClone();

    // ── 输出格式 (0=PNG, 1=GainMap, 2=JPEG LI, 3=JPEG XL, 4=AVIF, 5=WebP, 6=TIFF) ──
    // 默认 JPEG LI (索引 2): 体积小 + 兼容性最佳 + 编码快 (jpegli), 适合默认截图
    public int FormatIndex { get; set; } = 2;

    // ── 质量 ──
    // 兼容字段：当前格式的质量（保留用于旧配置文件/其他读取点）。
    // 各格式独立质量见 QualityPng/QualityGainMap 等，切换格式时不再互相覆盖。
    public double Quality { get; set; } = 80;

    // ── 每格式独立质量（GetQualityRange 区间内）──
    public double QualityPng { get; set; } = 100;          // 无损 (0-100)
    public double QualityGainMap { get; set; } = 1.0;      // butteraugli 距离 (0.5-3.0)
    public double QualityJpegLi { get; set; } = 1.0;       // butteraugli 距离 (0.5-3.0)
    // ═══ 2026-09-16：默认质量统一对齐到"视觉无损" ═══
    // 取值依据（业界公认的视觉无损阈值）：
    //   · JPEG XL / JPEG LI：butteraugli 距离 d=1.0 即 cjxl 官方默认的 visually lossless
    //                        （0.8 比 1.0 更严格，体积明显变大而肉眼难辨，故放宽到 1.0）
    //   · AVIF：CRF ≤ 20 属视觉无损区间；取 15（≈ q76）留一档余量
    //   · WebP：q ≥ 85 即视觉无损，取 90
    //   · PNG / TIFF：本身就是无损格式，固定 100
    public double QualityJpegXl { get; set; } = 1.0;       // butteraugli 距离 (0.1-4.0)
    public double QualityAvif { get; set; } = 15;           // CRF (0-63)
    public double QualityWebp { get; set; } = 90;           // 质量 (50-100)
    public double QualityTiff { get; set; } = 100;          // 无损 (0-100)

    // ── 输出路径 ──
    public string OutputPath { get; set; } = "";
    public string FileNamePrefix { get; set; } = "TrueToneCap_";

    // ── 色彩 (0=System, 1=sRGB, 2=DisplayP3, 3=DCI_P3, 4=AdobeRGB, 5=BT.2020) ──
    public int ColorSpaceIndex { get; set; }

    // ── HDR / ICC ──
    public bool HdrEnabled { get; set; } = true;
    // IccBakeEnabled 已删除（2026-09-22 用户拍板）：色彩管理不可关，默认把像素映射进标准色域。
    // 老 settings.json 里残留的 "IccBakeEnabled" 键在反序列化时被忽略，不需要迁移代码。

    // ── 热键 ──
    public string Hotkey { get; set; } = "Ctrl+Shift+S";
    public string RecordHotkey { get; set; } = "Ctrl+Shift+G";
    public string SilentHotkey { get; set; } = "Ctrl+Shift+Q";
    public string SaveShortcut { get; set; } = "S";
    public string CancelShortcut { get; set; } = "Esc";

    // ── 行为 ──
    public bool AutoStart { get; set; }
    public bool ShowPreview { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;

    // ── PNG 后缀（提高兼容性） ──
    public bool AvifPngSuffix { get; set; }
    public bool JxlPngSuffix { get; set; }

    /// <summary>
    /// AVIF 编码器 —— **单一**选项，取代原来的"编码后端 + 软件编码器"两层。
    /// <para>取值（见 Core 的 <c>AvifEncoderKind</c>）：
    /// Auto / Avifenc / Qsv / LibAom / SvtAv1 / Nvenc。</para>
    /// <para>Auto = 严格按 NVENC → QSV → SVT-AV1 → libaom → avifenc 取第一个<b>可用</b>的
    /// （硬件优先）。2026-09-17 起<b>不再</b>因"不满足色度/位深需求"而跳过硬件 ——
    /// 那样会让默认色度下的硬件永远选不上；能力不足改由 Core 协商降级并明确提示。</para>
    /// <para>手动指定时尊重用户选择，若超出该编码器能力则由 Core 协商降级。</para>
    /// </summary>
    public string AvifEncoder { get; set; } = "Auto";

    /// <summary>
    /// 是否允许使用硬件编码加速（当前覆盖 NVENC）。
    /// 2026-09-15 新增：硬件编码器的原生崩溃在 .NET 上【不可捕获】，
    /// 崩溃时由 EncoderCrashSentinel 自动降级；本开关的作用有两个 ——
    ///   ① 用户主动关闭硬件加速（调试/兼容性）；
    ///   ② 重新勾选时清除崩溃哨兵，即在自动降级后恢复硬件编码。
    /// </summary>
    public bool HardwareEncodingEnabled { get; set; } = true;

    // 2026-09-17 清理：原 AvifChroma 字段已删除。
    //   它与 ChromaAvif 并存且同为 "444" 默认值，但两边并不同源：
    //     · 管线 CapturePipelineService 读的是 ChromaAvif；
    //     · 设置页的实时保存与界面回显却写/读 AvifChroma。
    //   于是用户改了 AVIF 色度后，实时保存只更新 AvifChroma，管线仍读 ChromaAvif 旧值
    //   ——表现为"改了设置却不生效"，且界面看起来是改成功的（回显读的是另一个字段）。
    //   AvifChroma 剩下的唯一读取点是按格式 switch 的 _ 兜底分支，而 format 合法时(0-6)不可达。
    //   现统一到 ChromaAvif（真正的每格式色度字段），消除双字段漂移。

    // ── 录制 ──
    // 2026-09-15 清理：原 AnimAvifBackendIndex 字段已删除。
    //   它是典型的"僵尸设置"：保存在 MainWindow.Settings.cs 里被硬编码写成 0，
    //   既没有 UI 控件，也没有任何代码读取 —— 录制路径实际读的是 AvifBackendIndex。
    //   也就是说无论怎么改它都不会生效。若要为录制单独配置 AVIF 后端，
    //   应连同 UI 与消费方一起实现，而不是留一个没人读的字段。
    public double RecordQuality { get; set; } = 80;

    // ── 归档 ──
    public bool ArchiveEnabled { get; set; }
    public string ArchiveMode { get; set; } = "Month";

    // ── 每格式位深 ──
    public int BitDepthPng { get; set; } = 8;
    public int BitDepthJpegLi { get; set; } = 8;
    public int BitDepthJpegXl { get; set; } = 10;
    public int BitDepthAvif { get; set; } = 10;
    public int BitDepthWebP { get; set; } = 8;
    public int BitDepthTiff { get; set; } = 8;
    public int BitDepthGainMap { get; set; } = 8;

    // ── 每格式色度采样 ──
    // 2026-09-15 清理：原 ChromaPng 字段已删除。PNG 是 RGB 无损格式，不支持色度子采样，
    //   该字段在保存时被硬编码写 "444"、且无任何读取方，属冗余。
    public string ChromaJpegLi { get; set; } = "420";
    public string ChromaJpegXl { get; set; } = "444";
    // ═══ 2026-09-19（用户决定）：GainMap 色度默认 4:2:0，最高允许手动选 4:4:4 ═══
    // 上一版这个字段确实存在过，但【主路径从不读它】（JpegGainMapEncoder 把 base 硬编码成 444，
    // 只有两条降级旁路读）⇒ 同格式因走哪条路而色度不同，字段本身是假配置，故当日被删（交接 §27）。
    // 现在主路径已改为读 settings.ChromaSubsampling ⇒ 这个字段是【唯一事实源】且真的生效：
    //   · 4:2:0 = Ultra HDR base 最兼容、最省的默认档（普通查看器按普通 JPEG 读它）；
    //   · 4:4:4 = 手动选择，色度不砍、体积更大。增益图平面恒按灰度/444 编，不受此开关影响。
    public string ChromaGainMap { get; set; } = "420";
    // ── AVIF 色度默认值：这一处已经来回改过三次，动手前请读完 ──
    //   2026-09-17 → 420：当时是为了让 Auto 选到硬件（硬件只支持 420）。
    //   2026-09-18 → 444：用户拍板"默认应视觉无损"，接受 Auto 因此走软件编码。
    //   2026-09-19 → 420（现行，用户拍板）：与上一条不同的地方是 PickAuto 从此【按色度过滤】——
    //     420 ⇒ NVENC/QSV（硬件），422/444 ⇒ libaom（软件真支持广色度），
    //     即"Auto 选一个不必降级的编码器"，不再是"用最快的编码器然后提示降级"。
    //     所以这次改默认值的同时，也消掉了"默认设置却恒降级"的那次取舍；
    //     要 444 的用户在下拉里明选即可（会走 libaom，慢但色度不砍）。
    //   ⚠ 必须与 XAML 的 AvifChromaCbo IsSelected 项同源，由 Test_AvifChroma_DefaultIs420 双向钉住
    //     （只改一处会出现"设置里 420、界面显示 444"或反之）。
    public string ChromaAvif { get; set; } = "420";
    // ═══ 2026-09-19 清理：原 ChromaWebP / ChromaTiff / ChromaGainMap 三个字段已删除 ═══
    //   删除依据是实测，不是"看着没人用"：同一样张只改 ChromaSubsampling，三种格式的产物
    //   字节完全相同，ffprobe 读数也不变，说明这个值从头到尾没有进入码流。
    //   · ChromaWebP：NativeWebPEncoder 压根没有色度形参，而有损 WebP 的格式定义就是 4:2:0
    //     （无损为 RGB）⇒ 任何取值都产不出对应结果，字段唯一的"作用"是让界面显得可选。
    //     界面侧的下拉已一并撤掉，换成一行静态说明。
    //   · ChromaTiff：TIFF 走 Photometric=RGB / SamplesPerPixel=4 的无损 RGBA（ManagedTiffEncoder），
    //     色度子采样这个概念在该容器里不存在；界面上也没有对应下拉，只在保存时被硬编码写 444。
    //   · ChromaGainMap：GainMap 的 base 与增益图 JPEG 在 JpegGainMapEncoder 里硬编码 "444"
    //     （base 恒 4:4:4 是兼容性要求），字段默认值 420 从没进过 GainMap 主路径；
    //     界面上也没有对应下拉，只在保存时被硬编码写回 420。
    //   保留这三条只会让后来人以为"色度可按格式配置"，改它即错。真正需要每格式色度的
    //   只有 JPEG LI / JPEG XL / AVIF 三项，即上面仍在的那些。

    // ── 翻译/LLM ──
    public bool UseCustomLlm { get; set; }
    public string TranslationMode { get; set; } = "Free";
    public string LlmEndpoint { get; set; } = "";
    /// <summary>LLM API Key。
    /// ⚠ 明文不进 settings.json —— 由 SettingsService 转存至 SecretStore（DPAPI 加密）。</summary>
    [JsonIgnore]
    public string LlmApiKey { get; set; } = "";
    public string LlmModel { get; set; } = "deepseek-chat";
    public string LlmSystemPrompt { get; set; } = "";
    public string TargetLanguage { get; set; } = "zh-CN";
    public string OcrLanguage { get; set; } = "";

    /// <summary>有道智云开放平台 应用ID（AppKey）。非敏感，随 settings.json 明文保存。</summary>
    public string YoudaoAppKey { get; set; } = "";

    /// <summary>有道智云开放平台 应用密钥（AppSecret）。
    /// ⚠ 明文不进 settings.json —— 由 SettingsService 转存至 SecretStore（DPAPI 加密）。</summary>
    [JsonIgnore]
    public string YoudaoAppSecret { get; set; } = "";

    /// <summary>
    /// 允许把 OCR 出来的原文发给 Google 的<b>非官方</b>端点（网页版 / Chrome 划词用的内部接口）。
    /// <para>默认 false，两件事都要求它默认关：① 那两条不是公开 API，无 key 调用不符合 Google
    /// 服务条款；② 截图里的文字可能含 token、身份证号、聊天记录，未经用户同意不外发。</para>
    /// <para>开启路径只有这一个勾选框，且界面上必须同时写明这两点（见 MainWindow.Settings.cs）。
    /// 关掉时 <c>TranslationService</c> 连请求都不会发，不是"发了但忽略结果"。</para>
    /// </summary>
    public bool AllowUnofficialTranslateEndpoints { get; set; }

    /// <summary>
    /// 日志详细度档位：<c>Brief</c> / <c>Diagnose</c> / <c>Trace</c>（直接取 Core.LogDetail 的枚举名，
    /// 不另设第二套词表），落点在 <c>LogService.ApplyDetailLevel</c>。
    /// <para>默认 Brief = Info 及以上，即 Debug/Trace 不进日志文件 —— 这正是此前那个
    /// 恒 false、全仓没人赋过值的 <c>FileDebugEnabled</c> 想管而从未管成的同一件事。
    /// 档位没有设置页入口时等于没有档位，所以这一项必须有 UI（见 设置 → 行为）。</para>
    /// </summary>
    public string LogVerbosity { get; set; } = "Brief";

    // ── 系统检测（运行时填充，不持久化到用户设置文件）──
    [JsonIgnore]
    public bool AcmeDetected { get; set; }
    [JsonIgnore]
    public bool NvencAvailable { get; set; }
    [JsonIgnore]
    public bool QsvAvailable { get; set; }
    // 2026-09-19 清理：原 [JsonIgnore] DisplayBitDepth 已删除。唯一读取点是
    //   CapturePipelineService 把它抄进 EncodingSettings.DisplayBitDepth，而 Core 侧零读取
    //   （那个字段一并删了，见 ImageEncoder.cs 的说明）⇒ 留着只是一条"检测了但没人用"的假通道。
    //   显示器位深的真实用途在 MainWindow 的 HDR 状态文案里，它直接用 cap.DisplayBitDepth。
    [JsonIgnore]
    public int OutputBitDepth { get; set; } = 8;
    /// <summary>系统实际 SDR 白点亮度 (nits)，从 DISPLAYCONFIG_SDR_WHITE_LEVEL 读取。
    /// 0 表示未检测到，使用用户 PaperWhiteNits。</summary>
    [JsonIgnore]
    public int SystemSdrWhiteLevel { get; set; }

    /// <summary>系统检测的 HDR 显示器峰值亮度 (nits)，从 DXGI MaxLuminance 读取。
    /// 0 表示未检测到，使用用户 DisplayMaxNits。</summary>
    [JsonIgnore]
    public int SystemMaxNits { get; set; }

    // ── 首次运行标记 ──
    // ⚠ 必须持久化（不能 JsonIgnore）：用于"仅首次运行"时应用系统检测默认值。
    // 若忽略该字段，则每次启动 FirstRun 都为 true，
    // 导致 DetectAndApplySystemCapabilitiesAsync 每次都覆盖用户已保存的 HDR/ICC/色域设置。
    public bool FirstRun { get; set; } = true;

    // ── 色调映射 ──
    public string GainMapMode { get; set; } = "Gray";
    public int PaperWhiteNits { get; set; } = 200;
    public int DisplayMaxNits { get; set; } = 1000;

    // ── 界面 ──
    public string Language { get; set; } = "zh";
    public string OcrEngineMode { get; set; } = "OnnxGpu";
    public string ThemeMode { get; set; } = "Default";
    public bool EnableUiAnimations { get; set; } = true;

    // ── Toast ──
    public bool ToastOnCapture { get; set; } = true;
    public bool ToastOnSilentCapture { get; set; } = true;
    public bool ToastOnRecording { get; set; } = true;
    public string ToastPosition { get; set; } = "BottomRight";

    // ── 预览界面颜色 ──
    // 2026-08-11: 遮罩默认改灰色 (半透明黑=灰), 需求: 未框选区域灰色, 选区正常亮度
    public string OverlayColor { get; set; } = "#80000000";
    public string BorderColor { get; set; } = "#FF4488FF";

    // ── 字体选择 ──
    public string FontFamily { get; set; } = "";
}

/// <summary>AppSettingsData 扩展：格式索引 ↔ 每格式质量字段映射。</summary>
public static partial class AppSettingsQuality
{
    /// <summary>按格式索引获取每格式独立质量。</summary>
    /// <param name="formatIndex">0=PNG,1=GainMap,2=JPEGLI,3=JPEGXL,4=AVIF,5=WebP,6=TIFF。</param>
    public static double GetQuality(this AppSettingsData s, int formatIndex) => formatIndex switch
    {
        0 => s.QualityPng,
        1 => s.QualityGainMap,
        2 => s.QualityJpegLi,
        3 => s.QualityJpegXl,
        4 => s.QualityAvif,
        5 => s.QualityWebp,
        6 => s.QualityTiff,
        _ => s.Quality,
    };

    /// <summary>按格式索引设置每格式独立质量。</summary>
    public static void SetQuality(this AppSettingsData s, int formatIndex, double value)
    {
        switch (formatIndex)
        {
            case 0: s.QualityPng = value; break;
            case 1: s.QualityGainMap = value; break;
            case 2: s.QualityJpegLi = value; break;
            case 3: s.QualityJpegXl = value; break;
            case 4: s.QualityAvif = value; break;
            case 5: s.QualityWebp = value; break;
            case 6: s.QualityTiff = value; break;
            default: s.Quality = value; break;
        }
    }
}