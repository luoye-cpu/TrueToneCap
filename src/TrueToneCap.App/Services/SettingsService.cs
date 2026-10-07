// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Services/SettingsService.cs
// 设置持久化服务 — 从 MainWindow 提取，负责 JSON 配置的加载/保存
// 使用 System.Text.Json 源生成器实现 AOT 兼容
// ⚠ 敏感字段（LLM API Key）不进 JSON，改由 SecretStore 加密存储

using System.Text.Json;
using System.Text.Json.Nodes;
using TrueToneCap.App.Models;

namespace TrueToneCap.App.Services;

/// <summary>应用设置持久化服务。</summary>
public sealed class SettingsService
{
    /// <summary>
    /// 设置文件路径：<b>只有</b>软件同目录这一个位置（便携/绿色用法：设置跟着程序走）。
    /// 2026-09-25 起删掉"不可写就降级到 %LOCALAPPDATA%"那条路，理由见 <see cref="CanPersist"/>。
    /// </summary>
    /// <remarks>
    /// ⚠️ 本属性是【唯一事实源】：任何其他地方需要读设置文件（如 App 构造期加载主题）
    ///    都必须引用它，且同时问一次 <see cref="CanPersist"/> —— 历史上一度出现注释说 BaseDirectory、
    ///    代码却拼 LocalApplicationData 的不一致。
    /// </remarks>
    public static string SettingsPath { get; } = Path.Combine(AppContext.BaseDirectory, SettingsFileName);

    private const string SettingsFileName = "TrueToneCap.settings.json";

    /// <summary>
    /// 这个位置到底存得下来吗 —— 由 <c>SettingsWriteProbe</c> <b>真写一次</b>测出，不是看属性位。
    /// <para>false 时的口径：<b>把问题记进日志 + 全程按默认值表运行</b>（不读、不写，也不换位置存）。</para>
    /// <para>为什么原来那条"降级到 %LOCALAPPDATA%"从未生效：它的探测是"文件不存在 ⇒ 同目录可用"，
    /// 而真实世界存不下去的主因（装进 Program Files 被 ACL 拒写、只读介质、策略拦写）
    /// 恰恰是"文件还不存在"的形态 ⇒ 探测一路放行，随后每次 <c>Save()</c> 抛异常，
    /// 而调用方全走 <c>SaveQuiet()</c>（原先是空的 <c>catch { }</c>，连日志都不记）
    /// ⇒ 症状恰好就是那段防护声称要防的"用户改了设置 → 重启后回到默认，且毫无线索"。</para>
    /// <para>为什么降级这条路本身也要拆：设置存在哪一旦有两个答案，同目录那份与 LocalAppData 那份
    /// 内容可以完全不同，用户与代码各信一个 —— 那比"不保存"更难查。</para>
    /// </summary>
    public static bool CanPersist { get; } =
        TrueToneCap.Core.Settings.SettingsWriteProbe.CanPersistSettings(SettingsPath);

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

    /// <summary>本次 <see cref="Load"/> 是否真的从盘上读到了设置（凭据字段也刚从凭据库回填）。</summary>
    /// <remarks>
    /// 文件缺失或反序列化失败时 <c>Current = new()</c>，凭据字段是空的 —— 那来自<b>读失败</b>，
    /// 不是<b>用户清空</b>。不区分二者的话，本会话内任何一次 <c>Save()</c> 都会走
    /// <c>SaveSecret</c> 的空值分支，把 secrets.bin（在 %LOCALAPPDATA%，与同目录的 settings.json
    /// 是两个文件 —— 前者还在、后者丢了是完全可能的状态）里的 API Key/AppSecret 永久删掉且无备份。
    /// </remarks>
    private bool _loadedFromDisk;

    /// <summary>
    /// 本会话是否成功<b>写过</b>那份设置（与 <see cref="_loadedFromDisk"/> 一样按"当前内存里这份可不可信"
    /// 说话，只是来源相反：一个来自读，一个来自写）。
    /// <para>为什么要分开：新装机首启时根本没有 settings.json，<c>Load()</c> 走 else 分支 ⇒
    /// <see cref="_loadedFromDisk"/> 整会话为假。用户在这一次会话里先填 API Key、再清空，
    /// 清空那次 <c>Save()</c> 会走 <c>SaveSecret</c> 的"不许删"分支，只留一条 Warn：
    /// SecretStore 里那条凭据活了下来，而界面上已经显示为空 —— 下次启动 <c>Load()</c> 又把它回填出来，
    /// 用户视角是"我删掉的密钥自己回来了"。写成功之后，"字段为空"就只剩"用户清空"一种来源了。</para>
    /// <para><see cref="Load"/> 开头一并复位：读失败会把 <c>Current</c> 换成默认值表，
    /// 之前那次写的可信度随之作废，此时又得回到"空值不许删凭据"。</para>
    /// </summary>
    private bool _savedToDisk;

    /// <summary>凭据字段为空可不可以当成"用户要删这条凭据"（读进来过 或 本会话写成功过，二者之一）。</summary>
    private bool SecretClearanceIsUserIntent => _loadedFromDisk || _savedToDisk;

    /// <summary>
    /// 上一次 <see cref="Load"/> 是否真的把盘上那份读了进来（<b>读失败与文件不存在是两种情况，
    /// 都必须能被区分</b>）。
    /// <para>2026-09-29 实测撞出来的需要：审计卡当时只按"文件在不在"说话，于是一份<b>语法坏掉</b>的
    /// settings.json 被报成"喂给管线的是你磁盘上那份"，而实况是 catch 之后用的默认值表 ——
    /// 排查的人会拿这句话当凭据，它就必须在事实相反时说"我用的是默认值"。
    /// 日志里那条 Warn（"设置加载失败，使用默认值"）一直都在，但卡片是给维护者当场读的那份凭据。</para>
    /// </summary>
    internal bool LastLoadCameFromDisk => _loadedFromDisk;

    /// <summary>从磁盘加载设置。文件不存在或损坏时返回默认值。</summary>
    public AppSettingsData Load()
    {
        // 先按"不可信"起算：只有走完整条读盘+归一+迁移重写的路径才置 true（见 _loadedFromDisk）
        _loadedFromDisk = false;
        _savedToDisk = false;   // 见 _savedToDisk：读失败把 Current 换成默认值表，之前那次写的可信度随之作废
        try
        {
            if (!CanPersist)
            {
                // 存不下来的位置也不值得读：读了就等于把"界面上的值"和"下次启动的值"分成两套。
                LogPersistBlocked();
                Current = new();
                return Current;
            }

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

                // 结构迁移：GainMap 并入 JPEG LI（与"默认值表版本"各用各的门，见 GainMapMerged）
                migrated |= MigrateGainMapMerge(Current);

                // 默认值修订号迁移（必须早于 Normalize：先补历史账，再钳越界值）
                migrated |= MigrateStaleDefaults(Current);

                // 从加密凭据库读取敏感凭据
                Current.LlmApiKey = SecretStore.Get(SecretStore.LlmApiKeyName) ?? "";
                Current.YoudaoAppSecret = SecretStore.Get(SecretStore.YoudaoAppSecretName) ?? "";

                LogService.Info("SettingsService", $"设置已从 {SettingsPath} 加载");
                Normalize(Current);

                // 若发生迁移，立即重写 settings.json 以清除其中的明文。
                // ⚠ 必须排在 Normalize 之后：写在前面会把【未归一的越界值】钉回盘上，而 Save() 顺手
                //   盖的 SettingsRevision/GainMapMerged 章又让迁移门永不再开 ⇒ 盘上坏值就此固化，
                //   只有内存里那份是归一的（任何绕过 Load() 的直接反序列化会读到越界值）。
                //   清除明文的时效不受影响：SecretStore.Set 已在 MigratePlaintextApiKey 里当场完成，
                //   这里只是把带明文的那份文件重写掉，仍在同一次 Load() 返回之前。
                if (migrated) Save();

                // 走到这里 ⇒ 盘上那份确实读成功了，凭据字段也是刚从凭据库回填的。
                // 此后 Current 里的空凭据只可能来自"用户在设置页清空"，才允许 Save 去删凭据库。
                _loadedFromDisk = true;
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
        if (!CanPersist) { LogPersistBlocked(); return; }

        var dir = Path.GetDirectoryName(SettingsPath);
        if (dir is not null && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        // 敏感字段不写入 JSON（[JsonIgnore]），改存加密凭据库
        // "空值 = 用户要删这条凭据"这个前提，只在本次内存里这份可信时成立（见 SecretClearanceIsUserIntent）
        SaveSecret(SecretStore.LlmApiKeyName, Current.LlmApiKey, SecretClearanceIsUserIntent);
        SaveSecret(SecretStore.YoudaoAppSecretName, Current.YoudaoAppSecret, SecretClearanceIsUserIntent);

        // 使用源生成器序列化（AOT 兼容）
        // ═══ 2026-09-24：只写【与当前默认不同】的键 ═══
        // 旧版这里直接 Serialize(Current) ⇒ 64 个键每次整份写回，文件里永远不会缺键，
        // 于是"缺键 → 回落代码默认值"这条兜底形同虚设，改默认值传不到老用户手上。
        // 改成存差集后：没被用户碰过的键不落盘 ⇒ 默认值改了会自动生效；碰过的键照旧保留。
        // SettingsRevision 例外，必须永远写（否则新文件读作 0，迁移会反复触发）。
        // 修订号在【唯一的写盘出口】钉死，而不是靠 AppSettingsData 的属性初值：
        // 初值必须留 0 才能让缺键的老文件被判定为未迁移（见 AppSettingsData.SettingsRevision 的 ⚠ 注），
        // 而"本程序写过的文件就是当前默认表"这件事只有在这里说才算说到位 ——
        // 少了这一行，新装机存盘会得到 SettingsRevision:0，下次启动迁移门就把用户刚挑的质量档清成默认。
        Current.SettingsRevision = AppSettingsData.CurrentSettingsRevision;
        // 结构迁移标记同理盖章：本 build 写出的文件必然已是"GainMap 并入 JPEG LI"之后的形态。
        Current.GainMapMerged = true;
        var json = SerializeDiffs(Current);

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

        // 写盘成功 ⇒ 内存里这份从此与盘上一致：凭据字段为空只可能是"用户在设置页清空"，
        // 不再可能是"读盘失败回落默认"。这正是新装机那一格（见 _savedToDisk）。
        _savedToDisk = true;

        LogService.Info("SettingsService", $"设置已保存到 {SettingsPath}");
    }

    /// <summary>
    /// 序列化成"只含与默认值不同的键"的 JSON。
    /// <para>走 <see cref="JsonSerializer.SerializeToNode(object?, JsonSerializerContext)"/> 而不是
    /// 反射式 options：源生成上下文要保 AOT，且比较必须发生在【同一套命名/转换器】之下，
    /// 否则"当前值"和"默认值"两份 JSON 会因为格式化差异被判成不同 ⇒ 每个键都照写，等于没改。</para>
    /// </summary>
    private static string SerializeDiffs(AppSettingsData s)
    {
        var live = JsonSerializer.SerializeToNode(s, s_jsonContext.AppSettingsData)?.AsObject();
        var defs = JsonSerializer.SerializeToNode(AppSettingsData.CreateDefaults(),
            s_jsonContext.AppSettingsData)?.AsObject();
        if (live is null || defs is null) return "{}";
        // 剪枝算法本体在 Core（SettingsDiffPruner）—— Test 只引用 Core，放那儿才测得到语义而不只是调用点。
        // SettingsRevision 必须永远写：它若被剪掉，下次读文件得到 0，迁移会反复触发。
        return TrueToneCap.Core.Settings.SettingsDiffPruner.PruneToJson(
            live, defs, [nameof(AppSettingsData.SettingsRevision)]);
    }

    /// <summary>
    /// 静默保存（不抛异常，但<b>必须留下线索</b>）。
    /// <para>原先是 <c>catch { }</c> 空吞：设置存不下去时程序一切如常，界面上看不出任何异常，
    /// 日志里也一个字都没有 —— 而"改了设置没反应"是本项目最难排查的一类症状。
    /// 现在按【内容】去重记一条 Error（与降级提示同一规则，见 §28/#28）：
    /// 同一种失败刷屏没有信息量，而不同原因的失败一条都不能被前一条吃掉。</para>
    /// </summary>
    public void SaveQuiet()
    {
        try { Save(); }
        catch (Exception ex) { LogOnce($"设置保存失败（改动只在本次运行内有效）：{ex.Message}"); }
    }

    private static string? s_lastLoggedIssue;

    /// <summary>按消息内容去重的错误日志：同一失败只记一次，不同失败各记一次。</summary>
    private static void LogOnce(string msg)
    {
        if (s_lastLoggedIssue == msg) return;
        s_lastLoggedIssue = msg;
        LogService.Error("SettingsService", msg);
    }

    // ⚠ 这句原先写死"设置目录不可写"，而它管不住【文件被标只读】那一臂：CanPersistSettings 有两种
    //   false（目录写不进 / 那份文件带 ReadOnly 位，见 Core 的 SettingsWriteProbe），而真实世界里
    //   后者更常见（用户自己勾了只读、同步盘占位、从只读介质拷出来）。把成因写死 = 排查的人照着
    //   "去改目录权限"忙一场，而问题根本不在那儿。2026-09-30 用只读文件跑这一格时当场读到这句假话。
    private static void LogPersistBlocked() => LogOnce(
        $"设置存不下来 ⇒ 不读取也不保存 {SettingsPath}，本次运行按【默认值表】；"
        + "两种成因都走这里：① 这个目录真的写不进（装进 Program Files 又没提权、只读介质、被策略拦写）；"
        + "② 目录可写，但那份设置文件被标了只读。分不清就在同目录随手新建一个文件试试。"
        + "要让设置留住：①换可写目录或以可写权限运行；②摘掉该文件的只读位");

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

    /// <summary>把单个敏感字段写入加密凭据库；值为空<b>且这份空值可归因为用户清空</b>时才删除对应条目。
    /// <para>
    /// ⚠ 失败必须与"设置保存"隔离：加密存储可能因 DPAPI 不可用、磁盘满、
    /// 权限不足等原因失败。若让它抛出，会连带导致全部非敏感设置（格式/质量/热键…）
    /// 都保存不了。故此处只记录日志，保证用户其余设置不受影响。
    /// </para>
    /// <para>
    /// <c>allowDelete</c>（由 <see cref="SecretClearanceIsUserIntent"/> 给出）：设置文件缺失或损坏时
    /// <c>Current</c> 的凭据字段是读失败留下的空值，与"用户在设置页亲手清空"在数据上无法区分 ——
    /// 无条件走删除分支，就等于用一次坏文件无备份地毁掉 secrets.bin 里的凭据。非空值不受此限制
    /// （写入不会丢东西）。可信有两个来源：刚从盘上读进来，<b>或本会话已成功写过一次设置</b>
    /// （后者管的是新装机那一格，见 <see cref="_savedToDisk"/>）。
    /// </para>
    /// </summary>
    private static void SaveSecret(string name, string value, bool allowDelete)
    {
        try
        {
            if (!string.IsNullOrEmpty(value))
                SecretStore.Set(name, value);
            else if (allowDelete)
                SecretStore.Delete(name);
            else
                LogService.Warn("SettingsService",
                    $"本次未清除已保存的凭据（{name}）：这里的空值还不能当成用户清空的意思"
                    + "（要等本次读取确认成功、或本会话已成功写过设置，才允许删凭据库，删掉就没有备份了）。"
                    + "⚠ 别按字面读成设置加载失败：迁移触发时 Load() 内部的这次 Save() 也走这一支，"
                    + "那次读取其实是成功的，只是标志要到 Load() 末尾才抬。");
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
    /// 合法的用户自定义值（哪怕是最差档）一律保留。字符串型枚举例外：合法但拼法与界面 Tag
    /// 不一致（大小写）时被【改写成 Tag 的拼法】而非回落默认，理由见 <see cref="FitTag"/>。
    /// <para><c>internal</c> 而非 <c>private</c>：与 <see cref="MigrateStaleDefaults"/> 同一纪律——
    /// 自检必须<b>直接调用这条分支</b>（见 <c>SelfTest.RunNormalizeGateCheck</c>）。走 <c>Load()</c>
    /// 的话就要把夹具写进产品同目录那份 settings.json（本仓的设置只认同目录一个答案），
    /// 为了测试去改用户机器上的设置是不可接受的。</para>
    /// </summary>
    internal static void Normalize(AppSettingsData s)
    {
        // ── 每格式质量（min, max, default）──
        // 区间与各编码器的 GetQualityRange() 逐一对齐。
        // ⚠ default 一律从 AppSettingsData.CreateDefaults() 取，【不得再写字面量】：
        //   2026-09-17 就修过一次"这里写 0.8/18/92、初始化器写 1.0/15/90"的漂移，当时的注释
        //   已经承认"它构成第二处事实源"——但只是把数字对齐，没有消掉第二处，所以它一定会再漂。
        //   现在唯一事实源是属性初始化器，本函数只负责区间；越界回填必然回到"当前默认"。
        // PNG / TIFF 为无损格式，编码器固定 (100,100,100)，故这里同样收紧为 100。
        var dft = AppSettingsData.CreateDefaults();
        ClampTo(s.QualityPng,      v => s.QualityPng = v,      100, 100, dft.QualityPng);
        ClampTo(s.QualityGainMap,  v => s.QualityGainMap = v,  0.5, 3.0, dft.QualityGainMap);
        ClampTo(s.QualityJpegLi,   v => s.QualityJpegLi = v,   0.5, 3.0, dft.QualityJpegLi);
        ClampTo(s.QualityJpegXl,   v => s.QualityJpegXl = v,   0.1, 4.0, dft.QualityJpegXl);
        ClampTo(s.QualityAvif,     v => s.QualityAvif = v,     0,   63,  dft.QualityAvif);
        ClampTo(s.QualityWebp,     v => s.QualityWebp = v,     50,  100, dft.QualityWebp);
        ClampTo(s.QualityTiff,     v => s.QualityTiff = v,     100, 100, dft.QualityTiff);
        ClampTo(s.RecordQuality,   v => s.RecordQuality = v,   0,   100, dft.RecordQuality);

        // ── 索引类：越界会让 ComboBox 选中项静默漂移 ──
        // 上界取自 Core 的 FormatOrder（顺序就是持久化契约），不写死数字：
        // 2026-09-26 摘掉 GainMap 那一项后这里若仍写 6，就会放过"索引指向不存在的格式"。
        // （用显式比较而不是 `is < 0 or > Max` 模式：关系模式要求右侧是编译期常量。）
        if (s.FormatIndex < 0 || s.FormatIndex > TrueToneCap.Core.Settings.FormatOrder.MaxIndex)
            s.FormatIndex = TrueToneCap.Core.Settings.FormatOrder.DefaultIndex;
        // ⚠ 这个上界是**旧 6 项色域表**的最后一项（BT2020），不是当前界面那 3 项：
        //   老文件里的 3/4/5（DCI_P3/AdobeRGB/BT2020）必须活着交给 MainWindow.Settings.cs 的
        //   legacyTags→currentTags 迁移按【标签】收口，这里越界判它 = 静默把用户的 Rec.2020
        //   换成"跟随显示器"。表长在 CorePipelineTests 的"上界==legacyTags.Length-1"里对撞，
        //   --settings-migration 的 NORMALIZE 自检另有 5→5 那一臂（本机老文件实测就是 5）。
        if (s.ColorSpaceIndex is < 0 or > 5) s.ColorSpaceIndex = 0;    // 默认 System

        // ── 枚举型字符串：非法值会让 SetComboByTag 匹配不到 → 控件停留在 XAML 默认项，
        //    与 _settings 不一致，表现为"改了设置却没保存" ──
        // ⚠ 参数表 = MainWindow.xaml 里那张 ComboBoxItem Tag 表，一字不差（含大小写）；
        //   这条等式由 CorePipelineTests 的「设置白名单 == XAML Tag」逐轴对撞，7 个轴全在内。
        //   默认值一律取 CreateDefaults()，与上面质量轴同一条纪律（不得再写字面量）。
        FitTag(s.ThemeMode, v => s.ThemeMode = v, dft.ThemeMode, "Default", "Light", "Dark", "OLED");
        FitTag(s.Language, v => s.Language = v, dft.Language, "zh", "en");
        // ═══ 2026-09-15 修复：白名单与 XAML 实际 Tag 不一致 ═══
        // 原白名单为 "Windows"/"Acme"，而 MainWindow.xaml:325-327 的 ComboBoxItem Tag
        // 实为 "OnnxGpu"/"OnnxCpu"/"WindowsOcr" ⇒ 用户选"Windows OCR"存下的 "WindowsOcr"
        // 恒不合法，每次启动被改回 "OnnxGpu"，表现为"选了却没保存、下拉框还停在第一项"。
        // "Acme" 全仓仅此一处（占位残留），一并移除。
        FitTag(s.OcrEngineMode, v => s.OcrEngineMode = v, dft.OcrEngineMode, "OnnxCpu", "OnnxGpu", "WindowsOcr");
        // ═══ 2026-09-29（批次 E）：XAML 挂的是 Tag="Rgb"，白名单当年写成 "RGB" ═══
        // 旧写法把 "RGB" 当成合法值放行 ⇒ 编码按 Rgb 走、界面按 == 匹配不到 ⇒ 下拉空白，
        // 且下一次保存被 `?? "Gray"` 兜底把用户的 RGB 增益静默翻成灰度。现在按 Tag 拼法放行，
        // 大小写变体（"rgb"/"RGB"）由 FitTag 改写成 "Rgb" 而不是回落默认 —— 意图保住，拼法对齐。
        FitTag(s.GainMapMode, v => s.GainMapMode = v, dft.GainMapMode, "Gray", "Rgb");
        // ⚠ 2026-09-29 摘掉 "Week"：界面从来没有这一项（Tag 表只有 Year/Month/Day），
        //   归档路径 GetArchivePath 也没有按周的规则（落到 _ ⇒ yyyy-MM，与 Month 同）。
        //   放行它 = 白名单里多一个"选不到、也没有行为"的值，手改配置的人只会看到空白下拉。
        FitTag(s.ArchiveMode, v => s.ArchiveMode = v, dft.ArchiveMode, "Day", "Month", "Year");
        // ═══ 2026-09-16 修复：白名单漏了 "WindowsNotify" ═══
        // XAML（MainWindow.xaml:485 起）有 5 个选项，其中包含 "WindowsNotify"，
        // 但白名单只列了 4 个 ⇒ 用户选"Windows 系统通知"后，每次启动都被改回右下角，
        // 且下拉框停在第 4 项，用户完全无法察觉（与 OcrEngineMode 同型的缺陷）。
        // 另：MainWindow.xaml.cs 的 ShowCaptureToast 依赖该值判断是否走系统通知。
        FitTag(s.ToastPosition, v => s.ToastPosition = v, dft.ToastPosition,
            "TopLeft", "TopRight", "BottomLeft", "BottomRight", "WindowsNotify");
        // AVIF 编码器（单一选项）。未知值一律回退 Auto。
        // ═══ 2026-09-24：界面摘掉 "Avifenc" 档，旧值显式迁到 LibAom ═══
        // "Avifenc" 只是"不传 --codec"，而内嵌这份 avifenc 的默认 codec 就是 aom
        // （自报 1.4.2 (aom [enc/dec]:3.14.1, svt [enc]:v4.1.0)）⇒ 与 LibAom 同义，且不如它
        // 确定（LibAom 显式传 --codec aom）。留在界面上只会让人以为存在一个叫 avifenc 的
        // 编码器 —— 它是工具，不是编码器。
        // ⚠ 迁移目标必须是 LibAom 而不是 Auto：Auto 优先走硬件，那等于把"用户选了软件
        //   编码器"翻转成硬件路径 —— 与迁移前行为不同，正是本仓库反复踩的"迁移改变语义"。
        //   AvifEncoderKind.Avifenc 本身保留：管线层在"硬件不支持 HDR 路径"时仍改写为它
        //   （见 CapturePipelineService），它只是不再是用户可选项。
        // 比较必须 IgnoreCase：下面那行 FitTag 认大小写变体（"auto"/"AVIFENC" 都算数），这道门若按精确
        // 大小写比对，手写成 "avifenc" 的那份就【逃过迁移】、直接被白名单回落成 Auto ⇒ 优先走硬件，
        // 正是上面 ⚠ 禁止的那个翻转。两道工具的口径必须同向，否则"随手写小写"反而比写大写更糟。
        if (string.Equals(s.AvifEncoder, "Avifenc", StringComparison.OrdinalIgnoreCase))
        {
            s.AvifEncoder = "LibAom";
            LogService.Info("SettingsService",
                "AVIF 编码器设置迁移：Avifenc → LibAom（旧档已从界面移除；二者对内嵌 avifenc 同义，"
                + "且 LibAom 显式传 --codec aom 更确定。没有落到 Auto，因为 Auto 会优先走硬件）");
        }
        FitTag(s.AvifEncoder, v => s.AvifEncoder = v, dft.AvifEncoder, "Auto", "Qsv", "LibAom", "SvtAv1", "Nvenc");

        // ── 色度采样：仅这几个合法值 ──
        NormalizeChroma(s);

        // ── 位深：按格式钳制（各格式实际支持的集合不同，详见 NormalizeBitDepth）──
        NormalizeBitDepth(s);

        // ── 压缩强度三档（2026-10-07 用户口径）──
        // 逐格式各一栏，只作用于 AVIF / JPEG XL / WebP 的 effort 旋钮。
        // 用 FitTag 而不是自己写 if：它是本文件既有的"合法值收口"通道，同时负责
        // 「大小写不符时改写成 Tag 拼法」—— 自己写比较会漏掉那一半（见 FitTag 的备注）。
        // 三个字段的合法值相同但各自独立：用户可以对三者分别设不同档。
        // ⚠ Core 侧 CompressionEffortMap.Parse 对未识别值也回落均衡档 ⇒ 编码器侧的兜底与这里
        //   口径一致（设置被绕过时仍安全），是有意的双保险而非重复。
        FitTag(s.EffortAvif, v => s.EffortAvif = v, dft.EffortAvif, "Smallest", "Balanced", "Fastest");
        FitTag(s.EffortJpegXl, v => s.EffortJpegXl = v, dft.EffortJpegXl, "Smallest", "Balanced", "Fastest");
        FitTag(s.EffortWebp, v => s.EffortWebp = v, dft.EffortWebp, "Smallest", "Balanced", "Fastest");
        FitTag(s.EffortJpegLi, v => s.EffortJpegLi = v, dft.EffortJpegLi, "Smallest", "Balanced", "Fastest");
    }

    /// <summary>
    /// 默认值修订号迁移：把<b>历史上被钉死在文件里的旧默认值</b>交还给当前默认表。
    /// <para>为什么必须有这一步：旧版 <c>Save()</c> 走全量序列化 ⇒ 文件里 64 个键一个不缺，
    /// "缺键才回落代码默认"这条兜底从未生效（2026-09-15 删 <c>SettingsVersion</c> 时注释里
    /// 依赖的正是这条兜底，前提不成立）。于是 <c>Normalize()</c> 的 ClampTo 只在越界时回落，
    /// 区间内的旧值永久保留 —— 改代码默认值对老用户完全不生效，这就是"默认值修不好"的机制。</para>
    /// <para>刻意<b>只重置质量与色域</b>两类：色度（<c>ChromaGainMap</c>=420、<c>ChromaAvif</c>=420）
    /// 是 2026-09-19 明确拍板的产品决定，位深牵涉 HDR 容器策略尚未定，都不该在这里被动。</para>
    /// <para><c>internal</c> 而非 <c>private</c>：自检必须<b>直接调用这条分支</b>（见
    /// <c>SelfTest.RunSettingsMigrationCheck</c>）—— 走 <c>Load()</c> 外层入口的话，正常机器上
    /// 老文件早已被迁移过，测试实际量到的是环境而不是门。</para>
    /// </summary>
    internal static bool MigrateStaleDefaults(AppSettingsData s)
    {
        if (s.SettingsRevision >= AppSettingsData.CurrentSettingsRevision) return false;

        var d = AppSettingsData.CreateDefaults();
        var reset = new List<string>();
        void Take(string name, double from, double to, Action<double> set)
        {
            if (from == to) return;
            set(to);
            reset.Add($"{name} {from}→{to}");
        }
        Take(nameof(s.QualityGainMap), s.QualityGainMap, d.QualityGainMap, v => s.QualityGainMap = v);
        Take(nameof(s.QualityJpegLi),  s.QualityJpegLi,  d.QualityJpegLi,  v => s.QualityJpegLi = v);
        Take(nameof(s.QualityJpegXl),  s.QualityJpegXl,  d.QualityJpegXl,  v => s.QualityJpegXl = v);
        Take(nameof(s.QualityAvif),    s.QualityAvif,    d.QualityAvif,    v => s.QualityAvif = v);
        Take(nameof(s.QualityWebp),    s.QualityWebp,    d.QualityWebp,    v => s.QualityWebp = v);
        if (s.ColorSpaceIndex != d.ColorSpaceIndex)
        {
            reset.Add($"ColorSpaceIndex {s.ColorSpaceIndex}→{d.ColorSpaceIndex}");
            s.ColorSpaceIndex = d.ColorSpaceIndex;
        }

        s.SettingsRevision = AppSettingsData.CurrentSettingsRevision;
        LogService.Info("SettingsService",
            $"默认值修订 {AppSettingsData.CurrentSettingsRevision}：旧文件里的值被钉死、代码默认改不动，"
            + $"本次交还默认表 —— {(reset.Count == 0 ? "无需改动" : string.Join("，", reset))}。"
            + "若其中有你有意选的档位，请重新设置（此后只有与默认不同的值才会写盘）。");
        return true;
    }

    /// <summary>
    /// 一次性结构迁移：把「JPEG Gain Map」并入「JPEG LI」。
    /// <para>为什么必须有、且必须<b>不能被跳过</b>：<c>FormatIndex</c> 存的是<b>裸索引</b>，
    /// 旧表里 1=GainMap、2=JPEG LI，新表里 1=JPEG LI。而全仓对它的唯一防线是越界钳制
    /// （<c>is &lt; 0 or &gt; 6</c>），删掉一项后旧值 1 与新的默认值都仍在合法区间内 ⇒ 钳制永不触发 ⇒
    /// GainMap 用户静默变 JPEG LI、JPEG LI 用户静默变 JPEG XL，且界面看不出任何异常。
    /// 同型缺陷在 <c>ColorSpaceIndex</c> 上已经发生过一次（那次靠 legacyTags 表修）。</para>
    /// <para>门是 <c>GainMapMerged</c> 而不是 <c>SettingsRevision</c>：后者表达"默认值表版本"，
    /// 借它触发就会让所有老用户再被 <c>MigrateStaleDefaults</c> 清一次质量档。</para>
    /// <para>参数并格的规则刻意保守：只有"用户真的调过 GainMap 那侧、而 JPEG LI 那侧仍是默认"才搬过去，
    /// 两侧都调过时保留 JPEG LI 侧并在日志里说明 —— 合并不能顺手把用户的一个选择变成另一个。</para>
    /// </summary>
    internal static bool MigrateGainMapMerge(AppSettingsData s)
    {
        if (s.GainMapMerged) return false;

        var legacy = TrueToneCap.Core.Settings.FormatOrder.LegacyUiFormats;
        int oldIdx = s.FormatIndex;
        string oldName = oldIdx >= 0 && oldIdx < legacy.Length
            ? legacy[oldIdx].ToString() : $"越界({oldIdx})";

        var (newIdx, gm) = TrueToneCap.Core.Settings.FormatOrder.RemapLegacy(oldIdx);
        s.FormatIndex = newIdx;
        if (gm) s.GainMapEnabled = true;

        var d = AppSettingsData.CreateDefaults();
        var notes = new List<string>
        {
            $"格式 {oldName}→{TrueToneCap.Core.Settings.FormatOrder.FormatAt(newIdx)}"
            + (gm ? "（增益图开关自动打开）" : ""),
        };

        void Take(string label, double from, double toCur, double def, System.Action<double> set)
        {
            if (from == def) return;                       // 用户没调过 GainMap 侧 ⇒ 没什么可搬
            if (toCur != def) { notes.Add($"{label}两侧都改过，保留 {toCur}（GainMap 侧 {from} 未采用）"); return; }
            set(from);
            notes.Add($"{label} {def}→{from}");
        }
        Take("质量", s.QualityGainMap, s.QualityJpegLi, d.QualityJpegLi, v => s.QualityJpegLi = v);
        if (s.ChromaGainMap != d.ChromaGainMap && s.ChromaJpegLi == d.ChromaJpegLi)
        {
            notes.Add($"色度 {s.ChromaJpegLi}→{s.ChromaGainMap}");
            s.ChromaJpegLi = s.ChromaGainMap;
        }

        s.GainMapMerged = true;
        LogService.Info("SettingsService",
            "GainMap 已并入 JPEG LI（一次性重映射，此前格式列表少一项会让裸索引静默指向别的格式）："
            + string.Join("，", notes) + "。增益图现在是 JPEG LI 面板里的开关。");
        return true;
    }

    private static void ClampTo(double current, Action<double> set, double min, double max, double def)
    {
        if (double.IsNaN(current) || double.IsInfinity(current) || current < min || current > max)
            set(def);
    }

    /// <summary>
    /// 字符串型设置：只放行界面上真实存在的 <c>ComboBoxItem Tag</c>，并把<strong>大小写变体改写成 Tag
    /// 的确切拼法</strong>，匹配不到才回落默认。
    /// <para>为什么"校验通过"这一步还不够：白名单历来用 <c>OrdinalIgnoreCase</c> 比对，而把值回填进
    /// 下拉框的 <c>SetComboByTag</c> 写的是 <code>(string)item.Tag == tag</code>（区分大小写）。
    /// 于是盘上的 <c>"rgb"</c> / <c>"Dark"</c> 一类变体能活着穿过校验、编码路径也确实按它走，界面那一侧
    /// 却匹配不到任何项 ⇒ 下拉显示空白并留一条 Warn，而用户下一次保存<b>任何</b>设置时该字段被
    /// <code>?? 默认</code> 兜底静默翻转成另一档 —— 与 2026-09-15（"Windows"）、2026-09-16（漏
    /// "WindowsNotify"）两次缺陷同一个形状，只是发生在大小写上，所以那两次的修法都防不住它。</para>
    /// <para>空值保持原纪律：不改写，交由下游按默认处理。</para>
    /// </summary>
    private static void FitTag(string current, Action<string> set, string def, params string[] tags)
    {
        if (string.IsNullOrEmpty(current)) return;
        foreach (var t in tags)
            if (string.Equals(current, t, StringComparison.OrdinalIgnoreCase)) { set(t); return; }
        set(def);
    }

    private static void NormalizeChroma(AppSettingsData s)
    {
        // ═══ 2026-09-19：这里原先还规范化 ChromaWebP / ChromaTiff 两项，已随字段一并删除 ═══
        //   规范化一个"值永远进不了码流"的字段是自欺：WebP 编码器无色度形参（有损恒 4:2:0、无损 RGB）、
        //   TIFF 是 RGB 无损（无子采样概念），二者界面上也都没有下拉。理由全文见 AppSettingsData。
        //   ChromaGainMap 当天曾一并删除，随后用户定为"默认 420、可手动 444"⇒ 主路径改为真读它，
        //   字段随之恢复并进入本表（现在下面四项都是"编码器确实会读"的）。
        s.ChromaJpegLi  = FixChroma(s.ChromaJpegLi);
        // JPEG XL 单独走自己的值域（NativeJxlEncoder.ChromaOptions）：共享白名单放行 422，
        // 而 JXL 下拉没有 422 项 ⇒ 盘上一旦有 422，按 Tag 选中会落空、下拉显示空白。
        s.ChromaJpegXl  = TrueToneCap.Core.Encoding.NativeJxlEncoder.NormalizeChroma(s.ChromaJpegXl);
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
