// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Models/TranslationPackageInfo.cs
// 离线翻译语言包元数据定义

namespace TrueToneCap.Core.Models;

/// <summary>
/// 离线翻译语言包信息。
/// 用于管理和展示可下载的 Argos Translate 语言包。
/// <para>⚠ 命名口径（2026-11-11 修正）：Argos 生态的包标识是
/// <c>{源语言}_{目标语言}</c>（如 <c>en_zh</c>），文件名即 <c>en_zh.argosmodel</c>。
/// 上一版用 <c>en-zh</c>/<c>en-jp</c> 连字符风格且日文写成 <c>jp</c>，
/// 与真实索引对不上 —— 本地文件名必须与索引键一致，下载/装包/翻译三处才接得上。</para>
/// <para>⚠ 下载地址不在本类硬编码：真实 URL 以运行时抓取的 Argos 官方索引为准
/// （见 <c>TranslationPackageManager.ResolveDownloadUrlAsync</c>），
/// 本类只保留一个兜底直链模板 —— 索引全挂时还能照旧下载。</para>
/// </summary>
public sealed class TranslationPackageInfo
{
    /// <summary>语言包标识符 (如 "en_zh"，即 Argos 的 from_to)。</summary>
    public string Identifier { get; init; } = "";

    /// <summary>英文名称 (如 "English → Chinese")。</summary>
    public string NameEnglish { get; init; } = "";

    /// <summary>中文名称 (如 "英语→中文")。</summary>
    public string NameChinese { get; init; } = "";

    /// <summary>预估文件大小 (字节)。索引与直链响应头都可能给不出长度，
    /// 仅供下载前的"约 xx MB"展示；装到本地后管理器会把实际文件长度写回来 ⇒ 必须可写。</summary>
    public long SizeBytes { get; set; }

    /// <summary>源语言代码 (Argos 口径，如 "en" / "zh" / "zt")。</summary>
    public string SourceLanguageCode { get; init; } = "";

    /// <summary>目标语言代码 (Argos 口径)。</summary>
    public string TargetLanguageCode { get; init; } = "";

    /// <summary>包版本（索引里的 package_version，兜底直链的文件名内嵌它，
    /// 如 translate-en_zh-1_9.argosmodel）。</summary>
    public string PackageVersion { get; init; } = "";

    /// <summary>是否已安装。</summary>
    public bool IsInstalled { get; set; }

    /// <summary>本地文件路径。</summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// 兜底下载直链（官方索引抓不到时的最后出路）。
    /// <para>⚠ 2026-11-11 全量可用性审查修正：真实下载主机是 <c>argos-net.com</c>，
    /// 文件名内嵌包版本；此前硬编码的 <c>argos-translate.acclivate.ninja</c> 本机 DNS
    /// 解析失败（死链），且命名与真实包不符。版本取自官方索引逐包 HEAD 实测
    /// （12/12 返回 200 且带 Content-Length）。</para>
    /// </summary>
    public string FallbackDownloadUrl =>
        $"https://argos-net.com/v1/translate-{Identifier}-{PackageVersion}.argosmodel";

    /// <summary>格式化大小 (如 "152.00 MB")。</summary>
    public string SizeFormatted => FormatSize(SizeBytes);

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1024 * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
        if (bytes >= 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / 1024.0:F2} KB";
    }

    /// <summary>从 Argos 标识符（en_zh 形态）取源/目标语言代码；不合法返回 null。</summary>
    public static (string From, string To)? ParseIdentifier(string identifier)
    {
        var parts = identifier.Split('_', 2);
        // Argos 语言码是 2-3 字母小写（en / zh / zt / pt / fil…），两侧都验，防止
        // 把 "some_file" 这类用户随手起名的文件当成语言对。
        if (parts.Length != 2 ||
            parts[0].Length is < 2 or > 3 || !parts[0].All(char.IsAsciiLetterLower) ||
            parts[1].Length is < 2 or > 3 || !parts[1].All(char.IsAsciiLetterLower))
            return null;
        return (parts[0], parts[1]);
    }

    /// <summary>
    /// 预设的语言包列表（2026-11-11 可用性审查后口径）。
    /// <para>版本与 SizeBytes 来自官方索引逐包 HEAD 实测（2026-11-10，全部 200 且带
    /// Content-Length）—— 不是估算值。⚠ 索引里<b>没有</b> zh↔zt（简↔繁）包，
    /// 传统中文的实包是 en↔zt（英↔繁中）。</para>
    /// </summary>
    public static readonly IReadOnlyList<TranslationPackageInfo> DefaultPackages = new[]
    {
        new TranslationPackageInfo
        {
            Identifier = "en_zh",
            NameEnglish = "English → Chinese",
            NameChinese = "英语→中文",
            SizeBytes = 70743021,      // 实测 Content-Length（~67.5MB）
            PackageVersion = "1.9",
            SourceLanguageCode = "en",
            TargetLanguageCode = "zh"
        },
        new TranslationPackageInfo
        {
            Identifier = "zh_en",
            NameEnglish = "Chinese → English",
            NameChinese = "中文→英语",
            SizeBytes = 74481402,      // ~71MB
            PackageVersion = "1.9",
            SourceLanguageCode = "zh",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "en_ja",
            NameEnglish = "English → Japanese",
            NameChinese = "英语→日语",
            SizeBytes = 120470284,     // ~115MB
            PackageVersion = "1.1",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ja"
        },
        new TranslationPackageInfo
        {
            Identifier = "ja_en",
            NameEnglish = "Japanese → English",
            NameChinese = "日语→英语",
            SizeBytes = 117155716,     // ~112MB
            PackageVersion = "1.1",
            SourceLanguageCode = "ja",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "en_ko",
            NameEnglish = "English → Korean",
            NameChinese = "英语→韩语",
            SizeBytes = 120789009,     // ~115MB
            PackageVersion = "1.1",
            SourceLanguageCode = "en",
            TargetLanguageCode = "ko"
        },
        new TranslationPackageInfo
        {
            Identifier = "ko_en",
            NameEnglish = "Korean → English",
            NameChinese = "韩语→英语",
            SizeBytes = 118852077,     // ~113MB
            PackageVersion = "1.1",
            SourceLanguageCode = "ko",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "en_fr",
            NameEnglish = "English → French",
            NameChinese = "英语→法语",
            SizeBytes = 65472327,      // ~62MB
            PackageVersion = "1.9",
            SourceLanguageCode = "en",
            TargetLanguageCode = "fr"
        },
        new TranslationPackageInfo
        {
            Identifier = "fr_en",
            NameEnglish = "French → English",
            NameChinese = "法语→英语",
            SizeBytes = 66585033,      // ~64MB
            PackageVersion = "1.9",
            SourceLanguageCode = "fr",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "de_en",
            NameEnglish = "German → English",
            NameChinese = "德语→英语",
            SizeBytes = 150512831,     // ~144MB
            PackageVersion = "1.3",
            SourceLanguageCode = "de",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "ru_en",
            NameEnglish = "Russian → English",
            NameChinese = "俄语→英语",
            SizeBytes = 156239112,     // ~149MB
            PackageVersion = "1.9",
            SourceLanguageCode = "ru",
            TargetLanguageCode = "en"
        },
        new TranslationPackageInfo
        {
            Identifier = "en_zt",
            NameEnglish = "English → Chinese (Traditional)",
            NameChinese = "英语→中文(繁)",
            SizeBytes = 70743045,      // ~67.5MB
            PackageVersion = "1.9",
            SourceLanguageCode = "en",
            TargetLanguageCode = "zt"
        },
        new TranslationPackageInfo
        {
            Identifier = "zt_en",
            NameEnglish = "Chinese (Traditional) → English",
            NameChinese = "中文(繁)→英语",
            SizeBytes = 74498034,      // ~71MB
            PackageVersion = "1.9",
            SourceLanguageCode = "zt",
            TargetLanguageCode = "en"
        }
    }.AsReadOnly();
}
