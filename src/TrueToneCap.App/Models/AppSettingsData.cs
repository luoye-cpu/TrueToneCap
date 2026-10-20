// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Models/AppSettingsData.cs
// 应用设置数据模型 — 独立文件，支持 JSON 序列化（AOT 兼容）
// 所有属性必须有合理的默认值，新属性必须在此处添加默认值

using System.Text.Json.Serialization;
using TrueToneCap.Core.Encoding;   // OutputFormat —— 每格式参数以【枚举】为键，见下方 AppSettingsQuality

namespace TrueToneCap.App.Models;

/// <summary>应用设置数据模型。</summary>
public sealed class AppSettingsData
{
    // ═══ 设置版本与"默认值改不动"的根因（2026-09-24）═══
    // 2026-09-15 曾删掉一个同名 SettingsVersion 字段，理由是"全仓零引用、迁移机制从未实现"
    //   —— 那个判断当时是对的，但它依赖的前提是错的：注释写"旧 settings.json 缺该字段 → 用代码
    //   默认值"，而 Save() 走的是【全量序列化】，64 个键每次都整份写回，所以文件里【永远不会缺键】。
    //   结果：Normalize() 的 ClampTo 只在越界/NaN 时才回落默认 ⇒ 区间内的旧值永久保留 ⇒
    //   改代码默认值对任何已有 settings.json 的用户都不生效。这就是"默认值修了很久修不好"的机制。
    // 本次两件事一起做（不再留空版本号）：
    //   ① Save 改为只写【与当前默认不同】的键 ⇒ "缺键=用代码默认"这条兜底第一次真的成立，
    //      今后改默认值会自动传到所有没亲手碰过该控件的用户；
    //   ② 本版本号负责补历史账 —— 已经钉在文件里的旧值不会自己消失，需要一次显式重置。
    /// <summary>设置文件的默认值修订号。旧文件（无此字段）读作 0。</summary>
    /// <remarks>
    /// ⚠ 初值【必须留 0】，不得写成 <c>CurrentSettingsRevision</c>。System.Text.Json 反序列化
    /// 是"先跑属性初始化器、再用 JSON 里【出现过的】键覆盖"，所以初值就是"缺键时读到的值"：
    /// 一旦写成当前修订号，2026-09-24 之前那份【根本没有此字段】的老文件会被读成"已迁移"，
    /// <c>MigrateStaleDefaults</c> 对它唯一的受众永不触发（实机症状：2026-09-16 的全量序列化
    /// 旧文件里 <c>QualityAvif</c>=63 —— CRF 区间上界、最差档 —— 直达界面，且被当成用户自选）。
    /// 写盘侧由 <c>SettingsService.Save()</c> 统一钉成当前修订号，本初值只在"读"这一侧有意义。
    /// </remarks>
    public int SettingsRevision { get; set; }

    /// <summary>当前代码所认为的"默认值表"版本。改任何默认值都要 +1 并在迁移里点名受影响的键。</summary>
    /// <remarks>
    /// 1→2（2026-09-25）：<b>不是默认表变了，是 1 号从未生效过</b>。修订号 1 的整套机制靠
    /// "老文件缺 <c>SettingsRevision</c> ⇒ 读作 0"来触发，而缺键实际被属性初值填成了 1
    /// ⇒ 1 号迁移对它唯一的受众（2026-09-24 之前那份全量序列化文件）一次都没跑过。
    /// 于是那批文件里既有旧端点值、又顶着"已迁移"的章，光看内容无法与真正迁移过的文件区分，
    /// 只能把 1 号作废、按 2 号重发一次（受影响的只有 0.3.4/0.3.5-beta 期间写过盘的文件）。
    /// </remarks>
    public const int CurrentSettingsRevision = 2;

    /// <summary>
    /// 默认值表的<b>唯一事实源</b> —— 就是本类属性初始化器。
    /// <para><c>SettingsService.Normalize()</c> 的钳制回落值、以及 <c>Save()</c> 判断
    /// "这个键要不要写盘"的比较基准，都必须从这里取，不得再各写一份字面量。
    /// 2026-09-15 那份默认配置评审记的是 JXL 0.8 / AVIF 18 / WebP 92，而代码已是
    /// 1.0 / 15 / 90 —— 两处各写一份就一定会漂，这是那次漂移的来源。</para>
    /// <para>刻意做成<b>每次新建实例</b>的属性而不是 <c>static readonly</c> 字段：本类是可变对象
    /// （全部属性带 setter），共享单例意味着任何一处误写就会污染整张默认表，
    /// 而那正是本机制要防的"两处事实源打架"的第三种形态。调用频率只有"存盘/读盘"两次，代价可忽略。</para>
    /// </summary>
    public static AppSettingsData CreateDefaults() => new();

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

    // ── 输出格式：索引的含义由 Core 的 FormatOrder 单一定义（顺序就是持久化契约）──
    // 2026-09-26：GainMap 不再是独立格式 ⇒ 表从 7 项变 6 项，默认 JPEG LI。
    // ⚠ 这里【不再抄一遍索引表】：以前注释写"1=GainMap,2=JPEG LI"、AppSettingsQuality 里又复刻一份
    //   switch，删一项就得同步三处，漏一处就是静默改用户设置。现在只有 FormatOrder 一处。
    public int FormatIndex { get; set; } = TrueToneCap.Core.Settings.FormatOrder.DefaultIndex;

    /// <summary>
    /// 结构迁移标记：本文件是否已经经过"GainMap 并入 JPEG LI"的一次性重映射。
    /// <para>为什么不用 <see cref="SettingsRevision"/> 当触发器：那个计数表达的是<b>默认值表</b>的版本，
    /// 拿它去门控结构迁移，等于让所有老用户再被清一次质量档（<c>MigrateStaleDefaults</c> 会跟着触发）。
    /// 两件事必须各有自己的门。本标记由 <c>SettingsService.Save()</c> 在唯一的写盘出口统一盖章，
    /// 缺键（= 2026-09-26 之前的老文件）读作 false ⇒ 迁移触发一次。</para>
    /// </summary>
    public bool GainMapMerged { get; set; } = false;

    /// <summary>
    /// JPEG LI 是否附带增益图（Ultra HDR）。2026-09-26 起 GainMap 不再是独立输出格式，
    /// 而是 JPEG LI 的一个选项；打开时管线内部仍走 <see cref="TrueToneCap.Core.Encoding.OutputFormat.JPEG_GAINMAP"/>
    /// 那条编码路径，且强制 HDR 输出。
    /// </summary>
    public bool GainMapEnabled { get; set; } = false;

    // ── 质量 ──
    // 兼容字段：当前格式的质量（保留用于旧配置文件/其他读取点）。
    // 各格式独立质量见 QualityPng/QualityGainMap 等，切换格式时不再互相覆盖。
    public double Quality { get; set; } = 80;

    // ── 每格式独立质量（GetQualityRange 区间内）──
    public double QualityPng { get; set; } = 100;          // 无损 (0-100)

    /// <summary>
    /// <b>遗留字段</b>：GainMap 独立成格式时代的质量。2026-09-26 合并后界面与管线都不再读它
    /// （增益图态共用 <see cref="QualityJpegLi"/>），它只服务两件事：
    /// ① <c>SettingsService</c> 的一次性参数迁移要判断"用户是否真的改过 GainMap 那侧"；
    /// ② 老文件里的值仍要能被钳制，不能因为字段消失而读不进来。
    /// 删掉它会让老配置的这一维直接蒸发；重新接回界面则等于把刚合并掉的东西又拆回去。
    /// </summary>
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

    // ── 压缩强度三档（2026-10-07 用户口径）──
    // 只作用于 AVIF / JPEG XL / WebP 的 effort/speed 旋钮，不动质量。
    // 逐格式各一栏（用户要求"给这三个格式提供额外的一个条"），字面量取 "Smallest"/"Balanced"/"Fastest"：
    //   · 为什么不用 int 索引：索引会随枚举增删而静默改义（本仓 settings 的"裸索引静默指向别的格式"
    //     已经踩过一次，见 SettingsService 的 GainMap 并入记录）⇒ 用字符串，坏值由
    //     CompressionEffortMap.Parse 回落到均衡档，不会变成"最慢档"。
    //   · 默认 "Balanced" 对 AVIF/WebP 恰好等于改动前的硬编码值（-s 6 / cwebp 默认 -m 4）⇒ 产物不变；
    //     JPEG XL 则由 -e 9 变为 -e 5（实测 25.0s → 0.82s），这是用户明确要的提速。
    public string EffortAvif { get; set; } = "Balanced";
    public string EffortJpegXl { get; set; } = "Balanced";
    public string EffortWebp { get; set; } = "Balanced";
    // JPEG LI 是 2026-10-07 后加的（用户据实测要求纳入）：它的 --xyb 在两张真实截图 × 整个质量区间
    // 以及 GainMap 的 Baseline 路上**一律更小 1.5%~4.7%**，是唯一全正收益的小参。
    // 默认 Balanced ⇒ 不加 --xyb，产物与从前逐字节一致（与 AVIF/WebP 同一条承诺）。
    public string EffortJpegLi { get; set; } = "Balanced";

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
    public bool UseVSCap { get; set; }

    // ── PNG 后缀（提高兼容性） ──
    public bool AvifPngSuffix { get; set; }
    public bool JxlPngSuffix { get; set; }

    /// <summary>
    /// 【换格式保底】总闸（2026-09-29 用户口径：接上 PNG 保底、触发必须告诉用户、并且给保底做一个开关）。
    /// <para>开着（默认）：某一格式的后端这次编不出来时，把这张图改存为 <b>PNG</b> 并明确提示
    /// （"不是 AVIF / 不是 JPEG XL…"）。截图工具的失败模式应该是"无损但格式不对"，
    /// 不是"按下热键什么都没发生"。</para>
    /// <para>关掉：失败就照原样抛出去 ⇒ 产物**仍然是用户选的格式**，或者根本没有产物。
    /// 存在的理由很具体：有人按后缀/格式名接下游流程（.avif 目录、按 .webp 处理的脚本），
    /// 一张躺在 `.avif` 路径记录里的 PNG 对他比丢图更糟。</para>
    /// <para>它只管【换格式】。同一容器内的后端阶梯（AVIF 硬件→libaom、HDR→同格式 SDR 色调映射、
    /// 位深/色度协商）不受它影响——那不是换格式，且各有自己的留痕。</para>
    /// <para>默认值写在这里（属性初始化器）就是唯一事实源：<c>Save()</c> 只写与默认不同的键 ⇒
    /// 老用户文件里没有这个键 ⇒ 读到 <c>true</c>，行为与升级前一致，不需要迁移代码。</para>
    /// </summary>
    public bool PngFallbackEnabled { get; set; } = true;

    /// <summary>
    /// AVIF 编码器 —— **单一**选项，取代原来的"编码后端 + 软件编码器"两层。
    /// <para>取值（见 Core 的 <c>AvifEncoderKind</c>）：
    /// Auto / Qsv / LibAom / SvtAv1 / Nvenc。<c>Avifenc</c> 于 2026-09-24 从界面移除
    /// （它只是"不传 --codec"，与 LibAom 同义），但仍是<b>内部可达值</b>：管线在硬件不支持
    /// HDR 路径时改写为它，旧设置里的 "Avifenc" 由 SettingsService 迁到 LibAom。</para>
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

/// <summary>AppSettingsData 扩展：格式 ↔ 每格式质量字段映射。</summary>
/// <remarks>
/// 刻意以 <see cref="OutputFormat"/> 为键，<b>不是</b>以下拉索引为键。
/// 2026-09-26 之前这里是 <c>case 1 =&gt; QualityGainMap, case 2 =&gt; QualityJpegLi…</c> 的索引 switch，
/// 与 <c>MainWindow._formats</c> 是同一张顺序表的<b>第二次抄写</b> —— 把 GainMap 从下拉里摘掉时
/// 两处必须同时改，漏一处的表现是"拖 AVIF 的滑块写进了 GainMap 那格"这类静默错位。
/// 现在索引→格式只在 UI 那一层翻译一次（<c>FormatOrder.FormatAt</c>）。
/// <para>增益图态不单独占一格：它就是 JPEG LI 的另一个形态，共用 <c>QualityJpegLi</c>
/// （两者同为 butteraugli 0.5–3.0、默认同为 1.0，合并前也没有用户能区分）。</para>
/// </remarks>
public static partial class AppSettingsQuality
{
    /// <summary>该格式当前生效的独立质量。</summary>
    public static double GetQuality(this AppSettingsData s, OutputFormat f) => f switch
    {
        OutputFormat.PNG => s.QualityPng,
        OutputFormat.JPEG_LI => s.QualityJpegLi,
        OutputFormat.JPEG_GAINMAP => s.QualityJpegLi,   // 增益图态共用 JPEG LI 那一格
        OutputFormat.JPEG_XL => s.QualityJpegXl,
        OutputFormat.AVIF => s.QualityAvif,
        OutputFormat.WebP => s.QualityWebp,
        OutputFormat.TIFF => s.QualityTiff,
        _ => s.Quality,
    };

    /// <summary>写入该格式的独立质量（与 <see cref="GetQuality"/> 同一张键表）。</summary>
    public static void SetQuality(this AppSettingsData s, OutputFormat f, double value)
    {
        switch (f)
        {
            case OutputFormat.PNG: s.QualityPng = value; break;
            case OutputFormat.JPEG_LI:
            case OutputFormat.JPEG_GAINMAP: s.QualityJpegLi = value; break;
            case OutputFormat.JPEG_XL: s.QualityJpegXl = value; break;
            case OutputFormat.AVIF: s.QualityAvif = value; break;
            case OutputFormat.WebP: s.QualityWebp = value; break;
            case OutputFormat.TIFF: s.QualityTiff = value; break;
            default: s.Quality = value; break;
        }
    }
}