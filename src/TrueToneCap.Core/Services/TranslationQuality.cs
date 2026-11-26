// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/TranslationQuality.cs
//
// 逐行译文质量判定：回答的不是“请求有没有成功”，而是“这一行到底有没有被翻译”。
//
// 为什么需要它（2026-11-27 实机定位）：
//   LLM 批量接口只把 null 当失败；模型把原文抄回来、只翻半句、或从输入对象回显
//   `text` 字段时，结果都是非空字符串，于是 FailedCount=0、状态栏显示 ✅，而画面上
//   那几行仍是原文。这里把“原文回显 / 目标语言缺失 / 原文片段残留 / 不可译行”拆开，
//   让 UI 能如实显示“仍有 N 行疑似未翻译”，而不是继续把“返回了字符串”说成翻译成功。
//
// 判定纪律：
//   · 只产生“疑似”结论，不把品牌名/URL/版本号等硬判成失败；
//   · 不可译行（纯数字/符号/URL/路径/占位符/版本号/快捷键/技术缩写）不参与告警；
//   · 源文本身已是目标语言时不告警（例如中文界面翻成中文，原样返回是正确行为）；
//   · 目标语言脚本缺失、整段源文被保留、或常见 UI 词/小写源词残留时才告警。

using System.Text;
using System.Text.RegularExpressions;

namespace TrueToneCap.Core.Services;

/// <summary>单行译文的覆盖状态。</summary>
public enum TranslationLineQuality
{
    /// <summary>译文可用，或至少不是“明显没翻”。</summary>
    Ok,

    /// <summary>这一行本来就不需要翻译（纯数字/符号/URL/路径/占位符/版本号/快捷键/技术缩写）。</summary>
    NonTranslatable,

    /// <summary>源文本身已经是目标语言，原样返回是正确行为。</summary>
    AlreadyTargetLanguage,

    /// <summary>源文需要翻译，但译文仍是原文/没有目标语言脚本——疑似整行未翻译。</summary>
    Untranslated,

    /// <summary>译文有目标语言，但仍保留了整段源文或明显的源文词——疑似只翻了一部分。</summary>
    Partial,
}

/// <summary>单行判定结果；<see cref="Reason"/> 只写通用原因，不携带 OCR 原文。</summary>
public sealed record TranslationLineAssessment(TranslationLineQuality Quality, string? Reason)
{
    public bool IsIssue => Quality is TranslationLineQuality.Untranslated or TranslationLineQuality.Partial;
}

/// <summary>整批覆盖报告；计数用于 UI 状态栏，不把“疑似”混进硬失败。</summary>
public sealed record TranslationQualityReport(
    int Total,
    int Ok,
    int NonTranslatable,
    int AlreadyTargetLanguage,
    int Untranslated,
    int Partial)
{
    public int IssueCount => Untranslated + Partial;

    // 兼容预览窗状态栏的旧命名（UntranslatedCount/PartialCount）；只读别名，不改语义。
    public int UntranslatedCount => Untranslated;
    public int PartialCount => Partial;
}

/// <summary>
/// 不依赖网络/模型的逐行译文质量判定。UI 与批量服务共用同一套口径，
/// 避免“服务层说 0 失败、UI 说 ✅、用户看到原文”的三方分叉。
/// </summary>
public static class TranslationQuality
{
    private static readonly Regex s_urlLine = new(
        @"^(?:https?://|ftp://|www\.)\S+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_emailLine = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_windowsPathLine = new(
        @"^(?:[A-Za-z]:\\|\\\\)[^\r\n]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_unixPathLine = new(
        @"^(?:/|~/)[^\r\n]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_placeholderLine = new(
        @"^(?:%[sd]|\{[^{}\s]+\}|<[^<>\s]+>|[%$]\w+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_versionLine = new(
        @"^v?\d+(?:\.\d+){1,4}(?:[-+][\w.]+)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_shortcutLine = new(
        @"^(?:Ctrl|Control|Alt|Shift|Win|Windows|Cmd|Command|Option|Meta)(?:\s*\+\s*(?:Ctrl|Control|Alt|Shift|Win|Windows|Cmd|Command|Option|Meta|[A-Za-z0-9]+))+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_unitLine = new(
        @"^\d+(?:\.\d+)?\s*(?:%|px|dp|ms|s|fps|B|KB|MB|GB|TB|Hz|kHz|MHz|GHz)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_urlToken = new(
        @"(?:https?://|ftp://|www\.)\S+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_emailToken = new(
        @"[^@\s]+@[^@\s]+\.[^@\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex s_pathToken = new(
        @"(?:[A-Za-z]:\\|\\\\)[^\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_placeholderToken = new(
        @"(?:%[sd]|\{[^{}\s]+\}|<[^<>\s]+>)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_shortcutToken = new(
        @"(?:Ctrl|Control|Alt|Shift|Win|Windows|Cmd|Command|Option|Meta)(?:\s*\+\s*(?:Ctrl|Control|Alt|Shift|Win|Windows|Cmd|Command|Option|Meta|[A-Za-z0-9]+))+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>行内计量单位/百分比（不是整行）："CPU 45%" / "1920x1080 @ 144Hz"
    /// 这类监控读数没有可翻译的词，原样返回不该被状态栏当成漏译。</summary>
    private static readonly Regex s_metricUnitToken = new(
        @"(?<![\p{L}])(?:%|kHz|MHz|GHz|Hz|fps|ms|px|dp|nits|mm|cm|°C|°F|KB|MB|GB|TB|B|s)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>分辨率里的 1920x1080 的 x 是尺寸分隔符，不是英文单词。</summary>
    private static readonly Regex s_dimensionSeparator = new(
        @"(?<=\d)[xX](?=\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_wordToken = new(
        @"[\p{L}]{4,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>2–3 个字母的常见 UI 词（OK / No / Not / Now / App / Tab）。
    /// 这些词单独看很短，但半译时留在中文句子里非常显眼，不能被“词长 ≥4”漏掉。
    /// 只匹配 ASCII 字母，中文「确定」不会被当成短英文词；即使英文词紧贴中文也能命中。</summary>
    private static readonly Regex s_shortWordToken = new(
        @"(?<![A-Za-z])[A-Za-z]{2,3}(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> s_technicalAcronyms = new(
        [
            "AI", "API", "AR", "AVIF", "CLI", "CPU", "DNS", "DPI", "DP", "DVI", "FPS",
            "GB", "GPU", "GUI", "HDD", "HDMI", "HDR", "HTTP", "HTTPS", "ID", "IP",
            "JPEG", "JPG", "KB", "LCD", "LED", "MB", "ML", "NPU", "NVMe", "OCR",
            "OLED", "PCIe", "PC", "PDF", "PNG", "PPI", "PX", "RAM", "RGB", "RGBA", "RJ45",
            "ROM", "SATA", "SDR", "SDK", "SSD", "TB", "TCP", "UDP", "UHD", "UI",
            "URI", "URL", "USB", "UX", "VGA", "VR", "WEBP", "WiFi", "Wi-Fi",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>行内技术缩写（不是整行）：用于把 "Wi-Fi 已连接" 判成“已是目标语言”，
    /// 同时不放过 "Wi-Fi Connected" 里的 Connected。</summary>
    private static readonly Regex s_embeddedTechnicalToken = new(
        @"(?<![\p{L}\p{N}])(?:" +
        string.Join("|", s_technicalAcronyms.Select(Regex.Escape)) +
        @")(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> s_commonUiWords = new(
        [
            "access", "account", "address", "all", "app", "apps", "back", "bar", "battery",
            "billing", "cancel", "close", "confirm", "continue", "control", "copy", "cut",
            "delete", "device", "devices", "disable", "display", "edit", "enable", "exit",
            "file", "find", "forward", "help", "install", "manager", "network", "new",
            "next", "no", "not", "now", "off", "ok", "on", "open", "panel", "paste", "pause",
            "personalization", "play", "power", "previous", "printer", "privacy", "pull",
            "quick", "refresh", "reload", "rename", "reset", "retry", "save", "search",
            "security", "select", "send", "settings", "setup", "share", "show", "sign",
            "sound", "start", "stop", "storage", "system", "tab", "task", "update", "view",
            "yes",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>品牌/产品名整行原样保留时的判定辅助。不是“所有专名都不翻”，
    /// 而是只放过“整行就是品牌名”这种本来就可能保留原样的行；带正文的仍按正文判。</summary>
    private static readonly Regex s_latinWordToken = new(
        @"[A-Za-z][A-Za-z0-9'’_-]*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> s_properNounStopWords = new(
        [
            "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "here",
            "how", "in", "is", "it", "my", "of", "on", "or", "our", "that", "the",
            "their", "these", "this", "those", "to", "what", "when", "where", "which",
            "who", "why", "with", "your",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>常见截图 UI 里会原样保留的单品牌词。启发式只能认“首字母大写 + 内部大写”，
    /// 认不出 Microsoft/Notepad 这类单词品牌，故用这张小表兜底；它只影响是否亮黄灯，不改变译文。</summary>
    private static readonly HashSet<string> s_knownBrandWords = new(
        [
            "Adobe", "Amazon", "Android", "Apple", "Bing", "Chrome", "Discord", "Edge",
            "Excel", "Firefox", "GitHub", "Google", "Intel", "JetBrains", "Linux",
            "Microsoft", "Netflix", "Notepad", "NVIDIA", "Office", "OneDrive", "OpenAI",
            "Outlook", "Photoshop", "PowerPoint", "PowerShell", "Slack", "Spotify",
            "Steam", "Teams", "Ubuntu", "Visual", "Windows", "Word", "Xbox", "YouTube",
        ],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>整批逐行判定。译文数组短于源文时，缺失的行按“未翻译”计。</summary>
    public static TranslationQualityReport Analyze(
        IReadOnlyList<string> sources,
        IReadOnlyList<string>? translations,
        string? targetLang)
    {
        int ok = 0, nonTranslatable = 0, alreadyTarget = 0, untranslated = 0, partial = 0;
        for (int i = 0; i < sources.Count; i++)
        {
            string? translated = translations is not null && i < translations.Count
                ? translations[i]
                : null;
            var assessment = Assess(sources[i], translated, targetLang);
            switch (assessment.Quality)
            {
                case TranslationLineQuality.NonTranslatable: nonTranslatable++; break;
                case TranslationLineQuality.AlreadyTargetLanguage: alreadyTarget++; break;
                case TranslationLineQuality.Untranslated: untranslated++; break;
                case TranslationLineQuality.Partial: partial++; break;
                default: ok++; break;
            }
        }
        return new TranslationQualityReport(
            sources.Count, ok, nonTranslatable, alreadyTarget, untranslated, partial);
    }

    /// <summary>
    /// 判断单行译文。顺序很重要：先排除“本来就不该翻”的行，再判断目标语言脚本，
    /// 最后才看是否保留了源文片段；否则 URL/品牌名会被误报成漏译。
    /// </summary>
    public static TranslationLineAssessment Assess(
        string? source, string? translation, string? targetLang)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new(TranslationLineQuality.NonTranslatable, "空白行");

        if (string.IsNullOrWhiteSpace(translation))
            return new(TranslationLineQuality.Untranslated, "译文为空");

        string normalizedSource = Normalize(source);
        string normalizedTranslation = Normalize(translation);

        if (normalizedSource == normalizedTranslation)
        {
            if (IsNonTranslatable(source))
                return new(TranslationLineQuality.NonTranslatable, "不可译行原样返回");
            if (IsProperNounOnlyLine(source))
                return new(TranslationLineQuality.NonTranslatable, "整行是品牌/专名，可保留");
            if (IsAlreadyTargetLanguage(source, targetLang))
                return new(TranslationLineQuality.AlreadyTargetLanguage, "源文已是目标语言");
            return new(TranslationLineQuality.Untranslated, "译文与原文相同");
        }

        // 源文本身不可译/已是目标语言时，模型做任何改写都不按漏译告警——
        // 这里没有“必须逐字保留”的产品承诺，状态栏也不该为它亮黄灯。
        if (IsNonTranslatable(source))
            return new(TranslationLineQuality.Ok, "不可译行已改写");
        if (IsAlreadyTargetLanguage(source, targetLang))
            return new(TranslationLineQuality.Ok, "源文已是目标语言");

        // 整行是品牌/产品名时，译文常见形态是“品牌原样 + 正文已翻译”
        // （Windows 11 专业版 / Microsoft Store 微软商店）。不能因为品牌原样保留
        // 就把整行判成半译；但若译文完全没有目标语言字符，仍走下面的漏译判定。
        if (IsProperNounOnlyLine(source)
            && ContainsTargetScript(translation, targetLang))
            return new(TranslationLineQuality.Ok, "品牌/专名原样保留，正文已翻译");

        // 目标语言一个字符都没有，而源文确实含可译字母：整行明显没翻。
        if (!ContainsTargetScript(translation, targetLang)
            && HasTranslatableLetters(source, targetLang))
            return new(TranslationLineQuality.Untranslated, "译文不含目标语言字符");

        if (ContainsWholeSource(normalizedSource, normalizedTranslation))
            return new(TranslationLineQuality.Partial, "译文保留了整段原文");

        if (ContainsSignificantSourceWord(source, normalizedTranslation, targetLang))
            return new(TranslationLineQuality.Partial, "译文仍保留明显的原文词");

        if (IsLatinTarget(targetLang)
            && ContainsHan(source) && ContainsHan(translation))
            return new(TranslationLineQuality.Partial, "译文仍保留中文片段");

        return new(TranslationLineQuality.Ok, null);
    }

    /// <summary>源文是否是“不需要翻译”的纯符号/数字/URL/路径/占位符/版本号/快捷键/技术缩写。</summary>
    public static bool IsNonTranslatable(string source)
    {
        string trimmed = source.Trim();
        if (trimmed.Length == 0) return true;

        // 一个字母都没有：数字、货币、标点、进度条、项目符号等。
        if (!trimmed.Any(char.IsLetter)) return true;

        if (s_urlLine.IsMatch(trimmed)
            || s_emailLine.IsMatch(trimmed)
            || s_windowsPathLine.IsMatch(trimmed)
            || s_unixPathLine.IsMatch(trimmed)
            || s_placeholderLine.IsMatch(trimmed)
            || s_versionLine.IsMatch(trimmed)
            || s_shortcutLine.IsMatch(trimmed)
            || s_unitLine.IsMatch(trimmed))
            return true;

        // 纯技术读数（CPU 45% / GPU 78% / RAM 8.2GB / 144Hz）没有可翻译的词。
        // 先剥掉 URL/路径/快捷键/技术缩写/计量单位；只剩数字与标点时才算不可译。
        // 注意：Disk 45% / CPU usage 45% 会留下字母，仍按“需要翻译”处理。
        string metricValue = StripNonTranslatableTokens(trimmed);
        if (metricValue.Any(char.IsDigit) && !metricValue.Any(char.IsLetter))
            return true;

        // 允许 "(API)" / "CPU:" 这类带包装的技术缩写，但不把 "OK" 当成不可译：
        // "确定/取消" 是中文界面的常见按钮，模型原样返回 OK 应该被提示。
        string core = trimmed.Trim(
            '(', ')', '[', ']', '{', '}', '<', '>', ':', ';', '.', ',', '!', '?', '-', '—', '·', '•');
        return s_technicalAcronyms.Contains(core);
    }

    /// <summary>
    /// 整行是否只是品牌/产品名（原样保留不告警）。只用于“译文 == 原文”的兜底，
    /// 不用于“正文里保留品牌名”的判定；带正文的行仍按正文是否漏翻处理。
    /// <para>多词要求每个词都首字母大写（或带内部大写的 iPhone/macOS 这类），
    /// 且不含常见 UI 词/功能词；单词要求是已知品牌或带内部大写的驼峰词。
    /// 这样 Microsoft Store / Windows 11 Pro / GitHub 会放过，Settings / Task Manager /
    /// File Explorer / OK / Not now 仍会正常提示。</para>
    /// </summary>
    public static bool IsProperNounOnlyLine(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        string trimmed = source.Trim();
        if (trimmed.Any(c => IsHan(c) || IsKana(c) || IsHangul(c))) return false;
        // 有句子标点的不是“纯品牌行”：Welcome! / Error: ...
        if (Regex.IsMatch(trimmed, @"[.!?。！？；;，,、]")) return false;

        var matches = s_latinWordToken.Matches(trimmed);
        if (matches.Count == 0) return false;

        var words = new List<string>(matches.Count);
        foreach (Match match in matches)
        {
            string word = match.Value;
            if (s_technicalAcronyms.Contains(word)) continue;
            if (s_commonUiWords.Contains(word) || s_properNounStopWords.Contains(word))
                return false;
            words.Add(word);
        }
        if (words.Count == 0) return false;

        bool IsTitleOrCamel(string word)
        {
            bool startsUpper = char.IsUpper(word[0]);
            bool hasLower = word.Any(char.IsLower);
            bool internalUpper = word.Skip(1).Any(char.IsUpper);
            return startsUpper || (hasLower && internalUpper) || s_knownBrandWords.Contains(word);
        }

        if (words.Count == 1)
            return s_knownBrandWords.Contains(words[0])
                || (words[0].Any(char.IsLower) && words[0].Skip(1).Any(char.IsUpper));

        return words.All(IsTitleOrCamel);
    }

    /// <summary>源文是否已经完全是目标语言（允许数字/标点/技术缩写，但不允许夹杂其它语言单词）。</summary>
    public static bool IsAlreadyTargetLanguage(string source, string? targetLang)
    {
        if (string.IsNullOrWhiteSpace(targetLang)) return false;
        string stripped = StripNonTranslatableTokens(source);
        bool hasTarget = false;
        foreach (char c in stripped)
        {
            if (!char.IsLetter(c)) continue;
            if (IsInTargetScript(c, targetLang)) hasTarget = true;
            else return false;
        }
        return hasTarget;
    }

    /// <summary>源文是否含“目标语言之外”的可译字母。纯数字/符号/技术缩写不算。</summary>
    public static bool HasTranslatableLetters(string source, string? targetLang)
    {
        if (string.IsNullOrWhiteSpace(targetLang))
            return source.Any(char.IsLetter);
        string stripped = StripNonTranslatableTokens(source);
        foreach (char c in stripped)
        {
            if (char.IsLetter(c) && !IsInTargetScript(c, targetLang))
                return true;
        }
        return false;
    }

    /// <summary>译文里是否出现目标语言脚本。未知目标语言时不做这项拦截。</summary>
    public static bool ContainsTargetScript(string text, string? targetLang)
    {
        if (string.IsNullOrWhiteSpace(targetLang)) return true;
        foreach (char c in text)
        {
            if (!char.IsLetter(c)) continue;
            if (IsInTargetScript(c, targetLang)) return true;
        }
        return false;
    }

    private static bool ContainsWholeSource(string normalizedSource, string normalizedTranslation)
    {
        // 太短的源文（如 "OK"）全量包含很容易被 "(OK) 确定" 这类正常排版命中，不据此告警。
        if (normalizedSource.Length < 6) return false;
        if (normalizedTranslation.Length <= normalizedSource.Length) return false;
        return normalizedTranslation.Contains(normalizedSource, StringComparison.Ordinal);
    }

    private static bool ContainsSignificantSourceWord(
        string source, string normalizedTranslation, string? targetLang)
    {
        string stripped = StripNonTranslatableTokens(source);
        var matches = s_wordToken.Matches(stripped);
        var shortMatches = s_shortWordToken.Matches(stripped);

        // 单个常见 UI 词也足以证明“这一行只翻了一半”：Settings 设置 / Display 设置 /
        // Sign 登录。短词（OK / No / Not / Now / App / Tab）单独走 2–3 字母匹配。
        foreach (Match match in matches)
        {
            string word = match.Value;
            if (s_technicalAcronyms.Contains(word)) continue;
            if (s_commonUiWords.Contains(word)
                && ContainsWholeWord(normalizedTranslation, word.ToLowerInvariant()))
                return true;
        }
        foreach (Match match in shortMatches)
        {
            string word = match.Value;
            if (s_technicalAcronyms.Contains(word)) continue;
            if (s_commonUiWords.Contains(word)
                && ContainsWholeWord(normalizedTranslation, word.ToLowerInvariant()))
                return true;
        }

        if (matches.Count < 2) return false;

        int remaining = 0;
        int strongRemaining = 0;
        foreach (Match match in matches)
        {
            string word = match.Value;
            if (s_technicalAcronyms.Contains(word)) continue;
            if (string.IsNullOrWhiteSpace(targetLang) || !IsInTargetScript(word[0], targetLang))
            {
                if (normalizedTranslation.Contains(word.ToLowerInvariant(), StringComparison.Ordinal))
                {
                    remaining++;
                    // 小写源词（file / settings）或常见 UI 动词（Open / Save）是明显的漏翻；
                    // 单个首字母大写的品牌/产品名（Windows / Microsoft）不据此告警。
                    if (char.IsLower(word[0]) || s_commonUiWords.Contains(word))
                        strongRemaining++;
                }
            }
        }

        return strongRemaining > 0 || remaining >= 2;
    }

    /// <summary>按英文单词边界查译文里是否保留了某个源文词；避免 "on" 命中 "Windows"，
    /// 也允许 "OK确定" / "打开file" 这种中英紧贴、没有空格的排版。</summary>
    private static bool ContainsWholeWord(string normalizedText, string normalizedWord)
        => Regex.IsMatch(
            normalizedText,
            $@"(?<![A-Za-z]){Regex.Escape(normalizedWord)}(?![A-Za-z])",
            RegexOptions.CultureInvariant);

    private static string StripNonTranslatableTokens(string source)
    {
        string s = s_urlToken.Replace(source, " ");
        s = s_emailToken.Replace(s, " ");
        s = s_pathToken.Replace(s, " ");
        s = s_placeholderToken.Replace(s, " ");
        s = s_shortcutToken.Replace(s, " ");
        s = s_metricUnitToken.Replace(s, " ");
        s = s_dimensionSeparator.Replace(s, " ");
        s = s_embeddedTechnicalToken.Replace(s, " ");
        return s;
    }

    private static bool IsLatinTarget(string? targetLang)
    {
        string primary = PrimaryLanguage(targetLang);
        return primary is "en" or "fr" or "de" or "es" or "it" or "pt" or "nl"
            or "pl" or "sv" or "da" or "no" or "nb" or "nn" or "fi" or "cs" or "hu"
            or "ro" or "tr" or "id" or "in" or "ms" or "tl" or "fil" or "vi";
    }

    private static string PrimaryLanguage(string? targetLang)
    {
        if (string.IsNullOrWhiteSpace(targetLang)) return "";
        return targetLang.Trim().Split('-')[0].ToLowerInvariant();
    }

    private static bool IsInTargetScript(char c, string targetLang)
    {
        if (!char.IsLetter(c)) return false;
        string primary = PrimaryLanguage(targetLang);
        return primary switch
        {
            "zh" or "yue" => IsHan(c),
            "ja" or "jp" => IsHan(c) || IsKana(c),
            "ko" or "kr" => IsHangul(c),
            "ru" or "uk" or "bg" or "sr" or "mk" or "be" => IsCyrillic(c),
            "ar" or "fa" or "ur" => IsArabic(c),
            "he" or "iw" => IsHebrew(c),
            "el" => IsGreek(c),
            "hi" or "mr" or "ne" or "sa" => IsDevanagari(c),
            "th" => IsThai(c),
            "" => true,
            _ => IsLatin(c),
        };
    }

    private static bool IsHan(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF')
        || (c >= '\u3400' && c <= '\u4DBF')
        || (c >= '\uF900' && c <= '\uFAFF')
        || (c >= '\u3005' && c <= '\u3007');

    private static bool IsKana(char c) =>
        (c >= '\u3040' && c <= '\u30FF')
        || (c >= '\u31F0' && c <= '\u31FF');

    private static bool IsHangul(char c) =>
        (c >= '\uAC00' && c <= '\uD7AF')
        || (c >= '\u1100' && c <= '\u11FF')
        || (c >= '\u3130' && c <= '\u318F');

    private static bool IsLatin(char c) =>
        (c >= '\u0041' && c <= '\u005A')
        || (c >= '\u0061' && c <= '\u007A')
        || (c >= '\u00C0' && c <= '\u024F')
        || (c >= '\u1E00' && c <= '\u1EFF')
        || (c >= '\u2C60' && c <= '\u2C7F')
        || (c >= '\uA720' && c <= '\uA7FF');

    private static bool IsCyrillic(char c) =>
        (c >= '\u0400' && c <= '\u052F')
        || (c >= '\u2DE0' && c <= '\u2DFF')
        || (c >= '\uA640' && c <= '\uA69F');

    private static bool IsArabic(char c) =>
        (c >= '\u0600' && c <= '\u06FF')
        || (c >= '\u0750' && c <= '\u077F')
        || (c >= '\u08A0' && c <= '\u08FF');

    private static bool IsHebrew(char c) =>
        c >= '\u0590' && c <= '\u05FF';

    private static bool IsGreek(char c) =>
        c >= '\u0370' && c <= '\u03FF';

    private static bool IsDevanagari(char c) =>
        c >= '\u0900' && c <= '\u097F';

    private static bool IsThai(char c) =>
        c >= '\u0E00' && c <= '\u0E7F';

    private static bool ContainsHan(string text)
    {
        foreach (char c in text) if (IsHan(c)) return true;
        return false;
    }

    private static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) pendingSpace = true;
                continue;
            }
            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
