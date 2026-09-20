// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/SettingsService.cs
// 设置持久化服务 — 从 MainWindow 提取，负责 JSON 配置的加载/保存
// 使用 System.Text.Json 源生成器实现 AOT 兼容
// ⚠ 敏感字段（LLM API Key）不进 JSON，改由 SecretStore 加密存储

using System.Text.Json;
using TrueToneCap.App.Models;

namespace TrueToneCap.App.Services;

/// <summary>应用设置持久化服务。</summary>
public sealed class SettingsService
{
    /// <summary>
    /// 设置文件路径。2026-09-15 起为【软件同目录】（便携/绿色用法：设置跟着程序走），
    /// 不可写时自动降级到 %LOCALAPPDATA%。
    /// </summary>
    /// <remarks>
    /// ⚠️ 同目录【不一定可写】：装到 Program Files、放在只读介质、或以无权限账户运行时，
    ///    写入会失败。若不降级，表现是「用户改了设置 → 重启后回到默认」且毫无线索，
    ///    属于最难排查的一类问题。故这里做可写性探测 + 降级，并把实际路径写进日志。
    ///
    /// ⚠️ 本属性是【唯一事实源】：任何其他地方需要读设置文件（如 App 构造期加载主题）
    ///    都必须引用它，不得各自拼路径 —— 历史上一度出现注释说 BaseDirectory、
    ///    代码却拼 LocalApplicationData 的不一致。
    /// </remarks>
    public static string SettingsPath { get; } = ResolveSettingsPath();

    private const string SettingsFileName = "TrueToneCap.settings.json";

    private static string ResolveSettingsPath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, SettingsFileName);
        try
        {
            var dir = Path.GetDirectoryName(portable);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                // 文件尚不存在 → 同目录可用；已存在 → 必须不是只读
                if (!File.Exists(portable) ||
                    !File.GetAttributes(portable).HasFlag(FileAttributes.ReadOnly))
                    return portable;
            }
        }
        catch
        {
            // 探测失败按不可用处理，落到下面的降级分支
        }

        return LegacySettingsPath;
    }

    /// <summary>迁移前的旧位置（%LOCALAPPDATA%\TrueToneCap\）。</summary>
    public static string LegacySettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrueToneCap", SettingsFileName);

    /// <summary>
    /// 从旧位置迁移设置文件到软件同目录。
    /// 不做这件事的话，改用同目录存储会让老用户【已有的全部设置看起来消失】——
    /// 程序静默回到默认值，而旧文件还好好躺在 LocalApplicationData，极难察觉。
    /// 只在新位置尚无文件时搬一次；旧文件保留，便于回退。
    /// </summary>
    private static void MigrateFromLegacyLocation()
    {
        try
        {
            if (File.Exists(SettingsPath)) return;
            if (SettingsPath == LegacySettingsPath) return;   // 已降级到旧位置，无需迁移
            if (!File.Exists(LegacySettingsPath)) return;

            var dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.Copy(LegacySettingsPath, SettingsPath, overwrite: false);
            LogService.Info("SettingsService", $"已将设置迁移到软件同目录: {SettingsPath}");
        }
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"设置迁移失败，将在当前位置继续使用: {ex.Message}");
        }
    }

    // ═══ AOT 兼容：使用源生成器上下文，避免运行时反射 ═══
    private static readonly AppJsonContext s_jsonContext = AppJsonContext.Default;

    /// <summary>当前设置实例（内存中）。</summary>
    public AppSettingsData Current { get; private set; } = new();

    /// <summary>从磁盘加载设置。文件不存在或损坏时返回默认值。</summary>
    public AppSettingsData Load()
    {
        try
        {
            MigrateFromLegacyLocation();

            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                // 使用源生成器反序列化（AOT 兼容）
                // 注: LlmApiKey 已标记 [JsonIgnore]，不会从 JSON 读取
                Current = JsonSerializer.Deserialize(json, s_jsonContext.AppSettingsData) ?? new();

                // v0.3.3 迁移: 旧版本把 API Key 明文写在 settings.json
                var migrated = MigratePlaintextApiKey(json);

                // 2026-09-16 重构迁移: 旧版「编码后端 + 软件编码器」两层 → 单一 AvifEncoder
                migrated |= MigrateLegacyAvifEncoder(json, Current);

                // 从加密凭据库读取敏感凭据
                Current.LlmApiKey = SecretStore.Get(SecretStore.LlmApiKeyName) ?? "";
                Current.YoudaoAppSecret = SecretStore.Get(SecretStore.YoudaoAppSecretName) ?? "";

                // 若发生迁移，立即重写 settings.json 以清除其中的明文
                if (migrated) Save();

                LogService.Info("SettingsService", $"设置已从 {SettingsPath} 加载");
                Normalize(Current);
            }
            else
            {
                Current = new();
                LogService.Info("SettingsService", "设置文件不存在，使用默认值");
            }
        }
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"设置加载失败，使用默认值: {ex.Message}");
            Current = new();
        }
        return Current;
    }

    /// <summary>保存当前设置到磁盘。</summary>
    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 敏感字段不写入 JSON（[JsonIgnore]），改存加密凭据库
        SaveSecret(SecretStore.LlmApiKeyName, Current.LlmApiKey);
        SaveSecret(SecretStore.YoudaoAppSecretName, Current.YoudaoAppSecret);

        // 使用源生成器序列化（AOT 兼容）
        // 注意: 源生成器 API 不支持传入 JsonSerializerOptions，输出为紧凑格式
        var json = JsonSerializer.Serialize(Current, s_jsonContext.AppSettingsData);

        // ═══ 2026-09-15 修复：原子写入（与 SecretStore.cs:121-137 对齐）═══
        // 原实现直接 File.WriteAllText：写入过程中崩溃/断电会留下【截断的 settings.json】，
        // 下次启动 Deserialize 抛 JsonException → 整份设置静默重置为默认值
        // （输出路径、热键、质量、文件名前缀全部丢失，且无备份）。
        // 改为"写临时文件 → Move 覆盖"，任一时刻磁盘上都存在一个完整版本。
        // 临时文件用随机名：避免可预测路径的 TOCTOU 风险。
        var dir2 = Path.GetDirectoryName(SettingsPath) ?? Path.GetTempPath();
        var tmp = Path.Combine(dir2, $"settings_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }

        LogService.Info("SettingsService", $"设置已保存到 {SettingsPath}");
    }

    /// <summary>静默保存（不抛异常）。</summary>
    public void SaveQuiet()
    {
        try { Save(); } catch { }
    }

    /// <summary>
    /// 迁移旧版 AVIF「编码后端 + 软件编码器」两层设置到单一的 <c>AvifEncoder</c>。
    /// <para>旧字段 <c>AvifBackendIndex</c>（0=Auto / 1=LibAom / 2=Qsv / 3=Nvenc）与
    /// <c>AvifCodec</c>（avifenc 内部编码器，如 svt / aom）已随重构从 AppSettingsData 删除。
    /// 不做迁移的话：老用户手工挑的编码器会被静默丢回 Auto，界面上却看不出任何异常
    /// —— 与"改了设置没保存"同型的缺陷。</para>
    /// <para>仅当 JSON 里【没有】新的 <c>AvifEncoder</c> 键时才迁移（新设置优先）。</para>
    /// </summary>
    private static bool MigrateLegacyAvifEncoder(string json, AppSettingsData s)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (root.TryGetProperty(nameof(AppSettingsData.AvifEncoder), out _)) return false;   // 已是新格式

            // 软件编码器偏好优先于后端索引：它是更具体的选择
            if (root.TryGetProperty("AvifCodec", out var codecProp) &&
                codecProp.ValueKind == JsonValueKind.String)
            {
                var codec = codecProp.GetString();
                if (!string.IsNullOrWhiteSpace(codec))
                {
                    // rav1e 等无对应下拉项 → 忽略，交给下面的后端索引或默认 Auto
                    if (codec.Contains("svt", StringComparison.OrdinalIgnoreCase)) { s.AvifEncoder = "SvtAv1"; return true; }
                    if (codec.Contains("aom", StringComparison.OrdinalIgnoreCase)) { s.AvifEncoder = "LibAom"; return true; }
                }
            }

            if (root.TryGetProperty("AvifBackendIndex", out var idxProp) && idxProp.TryGetInt32(out int idx))
            {
                s.AvifEncoder = idx switch { 1 => "LibAom", 2 => "Qsv", 3 => "Nvenc", _ => "Auto" };
                return true;
            }
            return false;
        }
        catch (JsonException) { return false; }   // 非法 JSON：Load 的外层 try 会兜住
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"AVIF 编码器设置迁移失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>把单个敏感字段写入加密凭据库；值为空时删除对应条目。
    /// <para>
    /// ⚠ 失败必须与"设置保存"隔离：加密存储可能因 DPAPI 不可用、磁盘满、
    /// 权限不足等原因失败。若让它抛出，会连带导致全部非敏感设置（格式/质量/热键…）
    /// 都保存不了。故此处只记录日志，保证用户其余设置不受影响。
    /// </para>
    /// </summary>
    private static void SaveSecret(string name, string value)
    {
        try
        {
            if (!string.IsNullOrEmpty(value))
                SecretStore.Set(name, value);
            else
                SecretStore.Delete(name);
        }
        catch (Exception ex)
        {
            LogService.Error("SettingsService", $"凭据保存失败（{name}），其余设置仍将保存", ex);
        }
    }

    /// <summary>
    /// 加载后规范化：把越界 / 非法的旧值修正回合法范围，保证「默认设置确实生效」。
    ///
    /// 为什么必须做：<c>UpdateQualityPanel</c> 在 UI 层遇到越界值会改用格式默认值显示，
    /// 但 <c>_settings</c> 里仍是那个越界值 —— 任何绕过 UI 直接读 <c>_settings</c> 的路径
    /// （命令行、后续新增功能）都会拿到错误质量，而界面看起来一切正常。
    /// 故在加载时就地修正，让内存中的值始终是合法的。
    ///
    /// 区间与各 <c>Quality*</c> 字段注释保持一致；修正只发生在越界时，
    /// 合法的用户自定义值（哪怕是最差档）一律保留。
    /// </summary>
    private static void Normalize(AppSettingsData s)
    {
        // ── 每格式质量（min, max, default）──
        // 区间与各编码器的 GetQualityRange() 逐一对齐，Default 也与 AppSettingsData 的
        // 属性初始化器一致 —— 两处一旦漂移，UI 默认值与编码器默认值就会打架。
        // PNG / TIFF 为无损格式，编码器固定 (100,100,100)，故这里同样收紧为 100。
        ClampTo(s.QualityPng,      v => s.QualityPng = v,      100, 100, 100);
        ClampTo(s.QualityGainMap,  v => s.QualityGainMap = v,  0.5, 3.0, 1.0);
        ClampTo(s.QualityJpegLi,   v => s.QualityJpegLi = v,   0.5, 3.0, 1.0);
        // ═══ 2026-09-17 修复：这三个兜底默认值仍停留在"视觉无损"对齐之前的旧值 ═══
        // 旧的 (0.8 / 18 / 92) 与 AppSettingsData 的属性初始化器 (1.0 / 15 / 90) 已不一致，
        // 于是"越界 → 回填默认"这条路径会把用户值改成【一个产品上已被废弃的旧默认】，
        // 且它构成第二处事实源 —— 上面 :258 的注释声称"与 AppSettingsData 一致"，实际并不成立。
        // 现统一到 1.0 / 15 / 90（与 AppSettingsData 及 GetQualityRange().Default 三方一致）。
        ClampTo(s.QualityJpegXl,   v => s.QualityJpegXl = v,   0.1, 4.0, 1.0);
        ClampTo(s.QualityAvif,     v => s.QualityAvif = v,     0,   63,  15);
        ClampTo(s.QualityWebp,     v => s.QualityWebp = v,     50,  100, 90);
        ClampTo(s.QualityTiff,     v => s.QualityTiff = v,     100, 100, 100);
        ClampTo(s.RecordQuality,   v => s.RecordQuality = v,   0,   100, 80);

        // ── 索引类：越界会让 ComboBox 选中项静默漂移 ──
        if (s.FormatIndex     is < 0 or > 6) s.FormatIndex = 2;        // 默认 JPEG LI
        if (s.ColorSpaceIndex is < 0 or > 5) s.ColorSpaceIndex = 0;    // 默认 System

        // ── 枚举型字符串：非法值会让 SetComboByTag 匹配不到 → 控件停留在 XAML 默认项，
        //    与 _settings 不一致，表现为"改了设置却没保存" ──
        if (!IsOneOf(s.ThemeMode, "Default", "Light", "Dark", "OLED")) s.ThemeMode = "Default";
        if (!IsOneOf(s.Language, "zh", "en")) s.Language = "zh";
        // ═══ 2026-09-15 修复：白名单与 XAML 实际 Tag 不一致 ═══
        // 原白名单为 "Windows"/"Acme"，而 MainWindow.xaml:325-327 的 ComboBoxItem Tag
        // 实为 "OnnxGpu"/"OnnxCpu"/"WindowsOcr" ⇒ 用户选"Windows OCR"存下的 "WindowsOcr"
        // 恒不合法，每次启动被改回 "OnnxGpu"，表现为"选了却没保存、下拉框还停在第一项"。
        // "Acme" 全仓仅此一处（占位残留），一并移除。
        if (!IsOneOf(s.OcrEngineMode, "OnnxCpu", "OnnxGpu", "WindowsOcr")) s.OcrEngineMode = "OnnxGpu";
        if (!IsOneOf(s.GainMapMode, "Gray", "RGB")) s.GainMapMode = "Gray";
        if (!IsOneOf(s.ArchiveMode, "Day", "Week", "Month", "Year")) s.ArchiveMode = "Month";
        // ═══ 2026-09-16 修复：白名单漏了 "WindowsNotify" ═══
        // XAML（MainWindow.xaml:485 起）有 5 个选项，其中包含 "WindowsNotify"，
        // 但白名单只列了 4 个 ⇒ 用户选"Windows 系统通知"后，每次启动都被改回右下角，
        // 且下拉框停在第 4 项，用户完全无法察觉（与 OcrEngineMode 同型的缺陷）。
        // 另：MainWindow.xaml.cs 的 ShowCaptureToast 依赖该值判断是否走系统通知。
        if (!IsOneOf(s.ToastPosition, "TopLeft", "TopRight", "BottomLeft", "BottomRight", "WindowsNotify"))
            s.ToastPosition = "BottomRight";
        // AVIF 编码器（单一选项）。未知值一律回退 Auto。
        if (!IsOneOf(s.AvifEncoder, "Auto", "Avifenc", "Qsv", "LibAom", "SvtAv1", "Nvenc"))
            s.AvifEncoder = "Auto";

        // ── 色度采样：仅这几个合法值 ──
        NormalizeChroma(s);

        // ── 位深：按格式钳制（各格式实际支持的集合不同，详见 NormalizeBitDepth）──
        NormalizeBitDepth(s);
    }

    private static void ClampTo(double current, Action<double> set, double min, double max, double def)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current < min || current > max)
            set(def);
    }

    private static bool IsOneOf(string? v, params string[] allowed)
    {
        if (string.IsNullOrEmpty(v)) return true;   // 空值交由下游按默认处理，不强行改
        foreach (var a in allowed)
            if (string.Equals(v, a, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void NormalizeChroma(AppSettingsData s)
    {
        // ═══ 2026-09-19：这里原先还规范化 ChromaWebP / ChromaTiff 两项，已随字段一并删除 ═══
        //   规范化一个"值永远进不了码流"的字段是自欺：WebP 编码器无色度形参（有损恒 4:2:0、无损 RGB）、
        //   TIFF 是 RGB 无损（无子采样概念），二者界面上也都没有下拉。理由全文见 AppSettingsData。
        //   ChromaGainMap 当天曾一并删除，随后用户定为"默认 420、可手动 444"⇒ 主路径改为真读它，
        //   字段随之恢复并进入本表（现在下面四项都是"编码器确实会读"的）。
        s.ChromaJpegLi  = FixChroma(s.ChromaJpegLi);
        s.ChromaJpegXl  = FixChroma(s.ChromaJpegXl);
        s.ChromaAvif    = FixChroma(s.ChromaAvif);
        s.ChromaGainMap = FixChroma(s.ChromaGainMap);   // 2026-09-19：主路径已真读它（默认 420，可到 444）
    }

    // 非法值回落 420：与 AVIF 的默认色度（2026-09-17 产品决策）以及 Core 的
    // AvifEncoderCapabilities.NormalizeChroma 保持一致，避免两处兜底值打架。
    private static string FixChroma(string? v) =>
        v is "444" or "422" or "420" ? v : "420";

    /// <summary>
    /// 位深规范化 —— **按格式**钳制，而不是对所有格式统一放行 8/10/12/16。
    /// <para>
    /// 2026-09-17 修复（审查 P2-7）：此前 <c>FixBitDepth</c> 对所有格式一律放行 12-bit，
    /// 但各格式的真实能力并不相同（逐条核实过编码链路）：
    /// <list type="bullet">
    ///   <item>只有 PNG（HDR 路径）与 AVIF 真正实现了 10/12-bit 量化；</item>
    ///   <item>JPEG LI 与 Gain Map 的编码器【根本没有位深概念】（jpegli 走 PPM，maxval 恒 255），
    ///         这两个字段是历史遗留的死字段，界面上也没有对应下拉；</item>
    ///   <item>TIFF 最危险：<c>BitsPerSample</c> 会被原样写成 12，而采样数据是 16-bit 未打包的
    ///         → 产出解码器打不开的非法文件（已在 <c>ManagedTiffEncoder</c> 入口一并兜底）。</item>
    /// </list>
    /// 若继续统一放行，就会出现"设置显示 12-bit、实际输出 8-bit / 16-bit / 损坏文件"
    /// 的静默改参数 —— 正是本轮重构要消灭的问题。
    /// </para>
    /// </summary>
    private static void NormalizeBitDepth(AppSettingsData s)
    {
        s.BitDepthPng     = FixBitDepth(s.BitDepthPng);        // SDR 落到 16-bit 容器；HDR 路径真支持 10/12
        s.BitDepthJpegLi  = FixBitDepth8(s.BitDepthJpegLi);    // jpegli 仅 8-bit（PPM maxval=255），无位深参数
        s.BitDepthJpegXl  = FixBitDepthJxl(s.BitDepthJpegXl);  // 见 FixBitDepthJxl：只有 8 与「>8」两档
        s.BitDepthAvif    = FixBitDepthAvif(s.BitDepthAvif);   // 之后再由 AvifEncoderCapabilities 按编码器协商
        s.BitDepthWebP    = FixBitDepth8(s.BitDepthWebP);      // WebP 容器仅 8-bit
        s.BitDepthTiff    = FixBitDepth8Or16(s.BitDepthTiff);  // TIFF 的 BitsPerSample 只有 8/16 合法
        s.BitDepthGainMap = FixBitDepth8(s.BitDepthGainMap);   // Ultra HDR 的 Base 与增益图均 8-bit
    }

    // ═══ 2026-09-16 修复：XAML 位深下拉含 12-bit 选项，旧白名单只认 8/10/16，
    // 会把 12 静默改回 8。补充 12。
    // 注（2026-09-17 勘误）：带 12-bit 选项的是 AVIF / PNG / JPEG XL 三张卡片；
    // JPEG LI 与 Gain Map 的卡片【没有】位深下拉，其 BitDepth* 字段从未被编码链路读取。
    private static int FixBitDepth(int v) => v is 8 or 10 or 12 or 16 ? v : 8;

    /// <summary>
    /// AVIF 位深：只认 8 / 10 / 12 三档，其余一律收敛到该档。
    /// <para>为什么不能复用 <see cref="FixBitDepth"/>：它放行 16，而 AVIF 卡片的下拉只有 8/10/12
    /// （avifenc 的 <c>--depth</c> 上限就是 12）。旧配置里残留的 16 会让
    /// <c>SetComboByTag</c> 匹配不到 → 下拉停在默认项，<c>_settings</c> 里却仍是 16
    /// ⇒ 界面显示与实际输出不符，且用户无从察觉。</para>
    /// </summary>
    private static int FixBitDepthAvif(int v) => v switch
    {
        >= 12 => 12,
        >= 10 => 10,
        _ => 8,
    };

    /// <summary>该格式的编码器完全没有位深概念 → 恒 8。</summary>
    private static int FixBitDepth8(int _) => 8;

    /// <summary>TIFF：BitsPerSample 只有 8/16 合法；&gt; 8 一律落到 16（保精度优先于省体积）。</summary>
    private static int FixBitDepth8Or16(int v) => v > 8 ? 16 : 8;

    /// <summary>
    /// JPEG XL：三档 —— <c>8</c>、<c>10</c>（16-bit 容器 + sBIT=10）、<c>16</c>（16-bit 容器 + 全精度）。
    /// <para>cjxl v0.11.2 没有 <c>-b</c> 参数，位深由输入容器决定：8-bit 走 PPM 输出 8-bit；
    /// &gt;8 走 16-bit PNG 中转（携带 sBIT），JXL 容器随之报 bits_per_raw_sample。</para>
    /// <para>⚠ 2026-09-19 修正：本函数原先把"任何 &gt;8"一律归到 10，而 §12.2 已给下拉加了
    /// 【16-bit（Float16 源真精度）】项 —— 于是用户选 16 后【重启就被静默改回 10】，
    /// 表现为"设置没保存"。实测（--param-sweep）三者产物确实不同：raw_bits 报 10 / 12 / 16，
    /// 体积 881 / 828 / 935 B ⇒ 10 与 12 并非"完全相同"，16 更不等价于 10。
    /// 现在 16 保持 16，仅把越界值与老配置里的 12（§12.2 之前下拉是 8/10/12）归到 10。</para>
    /// </summary>
    private static int FixBitDepthJxl(int v) => v switch { 8 => 8, 16 => 16, _ => 10 };

    /// <summary>检测并把旧版本明文存储的 API Key 迁移到加密凭据库。</summary>
    /// <returns>确实发生了迁移时返回 true（调用方需重写 settings.json 清除明文）。</returns>
    private static bool MigratePlaintextApiKey(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(nameof(AppSettingsData.LlmApiKey), out var prop) &&
                prop.ValueKind == JsonValueKind.String)
            {
                var plain = prop.GetString();
                if (!string.IsNullOrWhiteSpace(plain))
                {
                    SecretStore.Set(SecretStore.LlmApiKeyName, plain);
                    LogService.Info("SettingsService", "已将明文 API Key 迁移到加密存储");
                    return true;
                }
            }
        }
        catch (JsonException) { /* 非法 JSON：无需迁移 */ }
        catch (Exception ex)
        {
            LogService.Warn("SettingsService", $"API Key 迁移失败: {ex.Message}");
        }
        return false;
    }
}
