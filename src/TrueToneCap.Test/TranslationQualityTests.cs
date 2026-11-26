// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/TranslationQualityTests.cs
// 翻译内容覆盖测试 — 不只验证“请求返回了”，还验证“每一行是否真的翻译了”。
// 运行: dotnet run --project src/TrueToneCap.Test -- --translation-quality-tests
//
// 覆盖四类：
//   1) 纯判定矩阵：Windows/资源管理器/浏览器/混合中英/URL/版本号/快捷键/品牌名/半译；
//   2) 伪造 LLM 端点：整行回显、半译、text 字段回显、补译成功/补译仍失败；
//   3) 视觉路：行数正确但某行回显原文时，仍能通过 OCR 源文做内容补译；
//   4) 段落合并：折行段落先整段请求、再按原行 id 回填；模型压成一条/重复整段时回退逐行。

using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

/// <summary>翻译内容覆盖与补译测试集。</summary>
public static class TranslationQualityTests
{
    private static int _passed, _failed;

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 翻译内容覆盖测试");
        Console.WriteLine("══════════════════════════════════════\n");

        Console.WriteLine("── 逐行质量判定 ──");
        Test_AssessmentMatrix();
        Test_RealWorldSceneMatrix();
        Test_ReportCounts();
        Test_TargetLanguageAndNonTranslatable();
        Test_StatusFormatter();
        Test_SingleLineJsonUnwrap();

        Console.WriteLine("\n── 批量补译（伪造 LLM）──");
        Test_BatchRepairsEchoedLine();
        Test_BatchReportsUnrepairedEchoes();
        Test_BatchRepairsPartialLine();
        Test_BatchRepairsObjectTextFieldEcho();
        Test_BatchDoesNotRepairNonTranslatableOrAlreadyTarget();
        Test_ParagraphGroupTranslation();
        Test_ParagraphGroupsAreBatched();
        Test_ParagraphGroupFallsBackWhenModelCollapses();
        Test_ParagraphGroupRejectsDuplicatedTranslation();

        Console.WriteLine("\n── 视觉路补译（伪造 LLM）──");
        Test_VisionRepairsEchoedLine();

        Console.WriteLine("\n══════════════════════════════════════");
        TestSummary.Report("--translation-quality-tests", _passed, _failed);
        Console.WriteLine("══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  纯判定矩阵
    // ═══════════════════════════════════════

    static void Test_AssessmentMatrix()
    {
        Check("英文正常翻译", "Open file", "打开文件", "zh-CN", TranslationLineQuality.Ok);
        Check("英文整行回显", "Open file", "Open file", "zh-CN", TranslationLineQuality.Untranslated);
        Check("译文为空", "Open file", "", "zh-CN", TranslationLineQuality.Untranslated);
        Check("只翻半句", "Open file", "打开 file", "zh-CN", TranslationLineQuality.Partial);
        Check("源文+译文并排", "Open file", "Open file（打开文件）", "zh-CN", TranslationLineQuality.Partial);
        Check("目标语言缺失", "Open file", "Open file", "zh-CN", TranslationLineQuality.Untranslated);
        Check("中译英正常", "打开文件", "Open file", "en-US", TranslationLineQuality.Ok);
        Check("中译英原样", "打开文件", "打开文件", "en-US", TranslationLineQuality.Untranslated);
        Check("中文源已是目标语言", "设置", "设置", "zh-CN", TranslationLineQuality.AlreadyTargetLanguage);
        Check("英文源已是目标语言", "Settings", "Settings", "en-US", TranslationLineQuality.AlreadyTargetLanguage);
        Check("英文源翻成中文但含品牌名", "Windows 11 Pro", "Windows 11 专业版", "zh-CN", TranslationLineQuality.Ok);
        Check("品牌名整行未翻不告警", "Microsoft Store", "Microsoft Store", "zh-CN", TranslationLineQuality.NonTranslatable);
        Check("Windows 版本整行未翻不告警", "Windows 11", "Windows 11", "zh-CN", TranslationLineQuality.NonTranslatable);
        Check("Windows 版本+正文翻译", "Windows 11", "Windows 11 专业版", "zh-CN", TranslationLineQuality.Ok);
        Check("品牌名+正文并排不告警", "Microsoft Store", "Microsoft Store 微软商店", "zh-CN", TranslationLineQuality.Ok);
        Check("驼峰品牌词整行不告警", "GitHub", "GitHub", "zh-CN", TranslationLineQuality.NonTranslatable);
        Check("功能名整行仍需翻译", "Task Manager", "Task Manager", "zh-CN", TranslationLineQuality.Untranslated);
        Check("功能名译成中文正常", "Task Manager", "任务管理器", "zh-CN", TranslationLineQuality.Ok);
        Check("资源管理器名称整行仍需翻译", "File Explorer", "File Explorer", "zh-CN", TranslationLineQuality.Untranslated);
        Check("普通单词 Store 仍需翻译", "Store", "Store", "zh-CN", TranslationLineQuality.Untranslated);
        Check("按钮 OK 仍需翻译", "OK", "OK", "zh-CN", TranslationLineQuality.Untranslated);
        Check("中文残留的英译", "打开文件", "Open 文件", "en-US", TranslationLineQuality.Partial);
    }

    static void Test_RealWorldSceneMatrix()
    {
        var cases = new (string Scene, string Source, string Translation, string Target, TranslationLineQuality Expected)[]
        {
            ("Windows 设置-系统", "System", "系统", "zh-CN", TranslationLineQuality.Ok),
            ("Windows 设置-显示", "Display", "显示", "zh-CN", TranslationLineQuality.Ok),
            ("Windows 设置-声音", "Sound", "声音", "zh-CN", TranslationLineQuality.Ok),
            ("Windows 设置-电源", "Power & battery", "电源和电池", "zh-CN", TranslationLineQuality.Ok),
            ("资源管理器-快速访问", "Quick access", "快速访问", "zh-CN", TranslationLineQuality.Ok),
            ("资源管理器-此电脑", "This PC", "此电脑", "zh-CN", TranslationLineQuality.Ok),
            ("资源管理器-网络", "Network", "网络", "zh-CN", TranslationLineQuality.Ok),
            ("浏览器-后退", "Back", "后退", "zh-CN", TranslationLineQuality.Ok),
            ("浏览器-前进", "Forward", "前进", "zh-CN", TranslationLineQuality.Ok),
            ("浏览器-刷新", "Reload", "刷新", "zh-CN", TranslationLineQuality.Ok),
            ("浏览器-地址栏", "Address bar", "地址栏", "zh-CN", TranslationLineQuality.Ok),
            ("登录错误对话框", "We can't sign you in", "我们无法让你登录", "zh-CN", TranslationLineQuality.Ok),
            ("按钮-确认", "OK", "确定", "zh-CN", TranslationLineQuality.Ok),
            ("按钮-取消", "Cancel", "取消", "zh-CN", TranslationLineQuality.Ok),
            ("按钮-确认未翻", "OK", "OK", "zh-CN", TranslationLineQuality.Untranslated),
            ("品牌名未翻", "Microsoft Store", "Microsoft Store", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("品牌名+正文翻译", "Microsoft Store", "微软商店", "zh-CN", TranslationLineQuality.Ok),
            ("Windows 版本未翻", "Windows 11", "Windows 11", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("Windows 版本+正文翻译", "Windows 11", "Windows 11 专业版", "zh-CN", TranslationLineQuality.Ok),
            ("功能名未翻", "Task Manager", "Task Manager", "zh-CN", TranslationLineQuality.Untranslated),
            ("版本号", "v1.2.3", "v1.2.3", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("百分比", "100%", "100%", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("网址", "https://example.com", "https://example.com", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("文件路径", @"C:\Users\Public\Desktop", @"C:\Users\Public\Desktop", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("占位符", "{name}", "{name}", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("快捷键", "Ctrl+Shift+Esc", "Ctrl+Shift+Esc", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("技术缩写", "CPU", "CPU", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("CPU 读数", "CPU 45%", "CPU 45%", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("GPU 读数", "GPU 78%", "GPU 78%", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("分辨率刷新率", "1920x1080 @ 144Hz", "1920x1080 @ 144Hz", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("USB 版本", "USB 3.0", "USB 3.0", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("UI 缩写", "UI", "UI", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("HDMI 缩写", "HDMI", "HDMI", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("磁盘占用仍需翻译", "Disk 45%", "Disk 45%", "zh-CN", TranslationLineQuality.Untranslated),
            ("CPU 使用率仍需翻译", "CPU usage 45%", "CPU usage 45%", "zh-CN", TranslationLineQuality.Untranslated),
            ("Wi-Fi", "Wi-Fi", "Wi-Fi", "zh-CN", TranslationLineQuality.NonTranslatable),
            ("中文界面已是目标语言", "蓝牙", "蓝牙", "zh-CN", TranslationLineQuality.AlreadyTargetLanguage),
            ("英文界面已是目标语言", "Bluetooth", "Bluetooth", "en-US", TranslationLineQuality.AlreadyTargetLanguage),
            ("中英混合行正常", "Wi-Fi 已连接", "Wi-Fi 已连接", "zh-CN", TranslationLineQuality.AlreadyTargetLanguage),
            ("中英混合行漏翻", "Wi-Fi Connected", "Wi-Fi Connected", "zh-CN", TranslationLineQuality.Untranslated),
            ("带占位符的正文", "Save as {name}", "另存为 {name}", "zh-CN", TranslationLineQuality.Ok),
            ("带 URL 的正文", "Visit https://example.com", "访问 https://example.com", "zh-CN", TranslationLineQuality.Ok),
            ("带快捷键的正文", "Press Ctrl+C to copy", "按 Ctrl+C 复制", "zh-CN", TranslationLineQuality.Ok),
            ("带路径的正文", @"Open C:\Temp", @"打开 C:\Temp", "zh-CN", TranslationLineQuality.Ok),
            ("错误提示", "Something went wrong", "出了点问题", "zh-CN", TranslationLineQuality.Ok),
            ("半译-单个常见词", "Display settings", "Display 设置", "zh-CN", TranslationLineQuality.Partial),
            ("半译-网络设置", "Network settings", "Network 设置", "zh-CN", TranslationLineQuality.Partial),
            ("半译-地址栏", "Address bar", "Address 栏", "zh-CN", TranslationLineQuality.Partial),
            ("半译-登录按钮", "Sign in", "Sign 登录", "zh-CN", TranslationLineQuality.Partial),
            ("半译-短按钮", "Not now", "Not 现在", "zh-CN", TranslationLineQuality.Partial),
            ("半译-电池节电", "Battery saver", "Battery 节电模式", "zh-CN", TranslationLineQuality.Partial),
            ("半译-中英紧贴", "Open file", "打开file", "zh-CN", TranslationLineQuality.Partial),
            ("半译-中英紧贴-短词", "OK", "OK确定", "zh-CN", TranslationLineQuality.Partial),
        };

        foreach (var c in cases)
            Check($"{c.Scene}：{c.Source} → {c.Translation}", c.Source, c.Translation, c.Target, c.Expected);
    }

    static void Test_ReportCounts()
    {
        var report = TranslationQuality.Analyze(
            ["System", "Microsoft Store", "100%", "设置", "Open file", "打开文件", "Settings"],
            ["系统", "Microsoft Store", "100%", "设置", "打开 file", "Open file", "Settings"],
            "zh-CN");
        Assert("整批报告：Ok/未译/不可译/已目标/半译计数闭合",
            report.Total == 7 && report.Ok == 2 && report.Untranslated == 1
            && report.NonTranslatable == 2 && report.AlreadyTargetLanguage == 1
            && report.Partial == 1 && report.IssueCount == 2,
            $"total={report.Total} ok={report.Ok} untrans={report.Untranslated} non={report.NonTranslatable} " +
            $"already={report.AlreadyTargetLanguage} partial={report.Partial}");
    }

    static void Test_TargetLanguageAndNonTranslatable()
    {
        Assert("纯数字/符号不参与告警",
            TranslationQuality.Assess("100%", "100%", "zh-CN").Quality == TranslationLineQuality.NonTranslatable
            && TranslationQuality.Assess("...", "...", "zh-CN").Quality == TranslationLineQuality.NonTranslatable);
        Assert("URL/路径/占位符/版本/快捷键不参与告警",
            new[] { "https://example.com", @"C:\Temp\a.txt", "{name}", "v1.2.3", "Ctrl+C" }
                .All(s => TranslationQuality.Assess(s, s, "zh-CN").Quality
                    == TranslationLineQuality.NonTranslatable));
        Assert("目标语言脚本检查：英文源翻中文必须出现中文",
            TranslationQuality.Assess("Open file", "Open file", "zh-CN").Quality
                == TranslationLineQuality.Untranslated
            && TranslationQuality.Assess("Open file", "打开文件", "zh-CN").Quality
                == TranslationLineQuality.Ok);
        Assert("品牌名单词原样不告警",
            TranslationQuality.Assess("OpenAI", "OpenAI", "zh-CN").Quality
                == TranslationLineQuality.NonTranslatable);
        Assert("单字母源文译成目标语言",
            TranslationQuality.Assess("A", "译1", "zh-CN").Quality
                == TranslationLineQuality.Ok,
            $"actual={TranslationQuality.Assess("A", "译1", "zh-CN").Quality}");
    }

    static void Test_StatusFormatter()
    {
        string clean = TranslationStatusFormatter.Build(
            6, 0, 0, 0, 0, 0, 0, null, null);
        Assert("状态栏：全干净才 ✅",
            clean.StartsWith("✅ 翻译完成（6 行）", StringComparison.Ordinal),
            clean);

        string soft = TranslationStatusFormatter.Build(
            6, 0, 2, 0, 0, 0, 0, null, null);
        Assert("状态栏：2 行疑似未翻译时不得 ✅",
            soft.StartsWith("⚠ 翻译完成（2/6 行疑似未翻译或不完整）", StringComparison.Ordinal)
            && !soft.Contains('✅', StringComparison.Ordinal),
            soft);

        string hard = TranslationStatusFormatter.Build(
            6, 6, 0, 0, 0, 0, 0, null, "401");
        Assert("状态栏：全行硬失败显示 ❌",
            hard.StartsWith("❌ 翻译失败：6/6 行都没出译文", StringComparison.Ordinal),
            hard);

        string mixed = TranslationStatusFormatter.Build(
            6, 2, 1, 0, 0, 0, 0, null, "429");
        Assert("状态栏：硬失败与疑似未翻译分列，不把疑似藏起来",
            mixed.StartsWith("⚠ 翻译完成（2/6 行失败，1/6 行疑似未翻译或不完整）", StringComparison.Ordinal),
            mixed);

        string degraded = TranslationStatusFormatter.Build(
            6, 0, 0, 0, 0, 0, 1, "Edge", null);
        Assert("状态栏：跨后端降级成功也必须警告",
            degraded.StartsWith("⚠ 翻译完成（6 行），但曾发生错误或降级", StringComparison.Ordinal)
            && degraded.Contains("Edge", StringComparison.Ordinal),
            degraded);

        string recovered = TranslationStatusFormatter.Build(
            6, 0, 0, 0, 0, 2, 0, null, null);
        Assert("状态栏：内容补译恢复行数要可见，但仍可 ✅",
            recovered.StartsWith("✅ 翻译完成（6 行）", StringComparison.Ordinal)
            && recovered.Contains("2 行经内容补译恢复", StringComparison.Ordinal),
            recovered);
    }

    static void Test_SingleLineJsonUnwrap()
    {
        Assert("单行入口：一元字符串数组拆成一行",
            TranslationService.SanitizeSingleLine("[\"译1\"]") == "译1");
        Assert("单行入口：多元素数组必须拒收，不能偷取第一条",
            TranslationService.SanitizeSingleLine("[\"译1\",\"译2\"]") is null);
        Assert("单行入口：translation 对象拆成一行",
            TranslationService.SanitizeSingleLine("{\"id\":1,\"translation\":\"译1\"}") == "译1");
        Assert("单行入口：{name} 占位符不能被当成畸形 JSON 拒收",
            TranslationService.SanitizeSingleLine("{name}") == "{name}");
    }

    // ═══════════════════════════════════════
    //  批量补译
    // ═══════════════════════════════════════

    static void Test_BatchRepairsEchoedLine()
    {
        using var server = new FakeLlmServer((idx, n) => n > 0
            ? (200, ChatResponseLines("打开文件", "Settings"), null)
            : (200, ChatResponseSingle("设置"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["Open file", "Settings"], "zh-CN").GetAwaiter().GetResult();

        Assert("批量第二行回显原文：同 LLM 单行补译救回，不再算成功",
            result.Lines.SequenceEqual(["打开文件", "设置"])
            && result.FailedCount == 0 && result.UnchangedCount == 0 && result.PartialCount == 0
            && result.ContentRetryCount == 1 && result.Mode.Contains("内容补译", StringComparison.Ordinal)
            && server.RequestCount == 2,
            $"lines={string.Join("|", result.Lines)} failed={result.FailedCount} unchanged={result.UnchangedCount} " +
            $"partial={result.PartialCount} contentRetry={result.ContentRetryCount} mode={result.Mode} req={server.RequestCount}");
    }

    static void Test_BatchReportsUnrepairedEchoes()
    {
        using var server = new FakeLlmServer((_, n) => n > 0
            ? (200, ChatResponseLines("Open file", "Settings"), null)
            : (200, ChatResponseSingle("Settings"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["Open file", "Settings"], "zh-CN").GetAwaiter().GetResult();

        Assert("批量两行都回显：补译仍失败时 UnchangedCount=2，不伪装成 0 失败",
            result.FailedCount == 0 && result.UnchangedCount == 2 && result.ContentRetryCount == 0
            && result.Lines.SequenceEqual(["Open file", "Settings"])
            && server.RequestCount == 3,
            $"failed={result.FailedCount} unchanged={result.UnchangedCount} contentRetry={result.ContentRetryCount} " +
            $"lines={string.Join("|", result.Lines)} req={server.RequestCount}");
    }

    static void Test_BatchRepairsPartialLine()
    {
        using var server = new FakeLlmServer((idx, n) => n > 0
            ? (200, ChatResponseLines("Open file 打开文件"), null)
            : (200, ChatResponseSingle("打开文件"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["Open file"], "zh-CN").GetAwaiter().GetResult();

        Assert("批量半译（保留整段源文）：补译后 PartialCount 归零",
            result.Lines.SequenceEqual(["打开文件"])
            && result.FailedCount == 0 && result.UnchangedCount == 0 && result.PartialCount == 0
            && result.ContentRetryCount == 1 && server.RequestCount == 2,
            $"lines={string.Join("|", result.Lines)} partial={result.PartialCount} " +
            $"contentRetry={result.ContentRetryCount} req={server.RequestCount}");
    }

    static void Test_BatchRepairsObjectTextFieldEcho()
    {
        // 有些 OpenAI 兼容网关会把输入对象原样带回：{"id":1,"text":"Open file"}。
        // 旧解析器把 text 当译文 ⇒ 非空 ⇒ 成功；这里验证内容判定会把它揪出来并补译。
        using var server = new FakeLlmServer((idx, n) => n > 0
            ? (200, ChatResponseObjectText(1, "Open file"), null)
            : (200, ChatResponseSingle("打开文件"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["Open file"], "zh-CN").GetAwaiter().GetResult();

        Assert("对象 text 字段回显：不把源文当译文，补译成功",
            result.Lines.SequenceEqual(["打开文件"])
            && result.UnchangedCount == 0 && result.ContentRetryCount == 1
            && server.RequestCount == 2,
            $"lines={string.Join("|", result.Lines)} unchanged={result.UnchangedCount} " +
            $"contentRetry={result.ContentRetryCount} req={server.RequestCount}");
    }

    static void Test_BatchDoesNotRepairNonTranslatableOrAlreadyTarget()
    {
        using var server = new FakeLlmServer((_, n) =>
            (200, ChatResponseLines("100%", "https://example.com", "中文"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["100%", "https://example.com", "中文"], "zh-CN").GetAwaiter().GetResult();

        Assert("不可译行/已是目标语言行原样返回：不触发多余补译请求",
            result.Lines.SequenceEqual(["100%", "https://example.com", "中文"])
            && result.FailedCount == 0 && result.UnchangedCount == 0 && result.PartialCount == 0
            && result.ContentRetryCount == 0 && server.RequestCount == 1,
            $"lines={string.Join("|", result.Lines)} unchanged={result.UnchangedCount} " +
            $"partial={result.PartialCount} req={server.RequestCount}");
    }

    static void Test_ParagraphGroupTranslation()
    {
        using var server = new FakeLlmServer((_, n) => n > 0
            ? (200, ChatResponseLines("这是一个很长的句子，", "下一行继续写。"), null)
            : (200, ChatResponseSingle("设置"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["This is a long sentence that", "continues on the next line."], "zh-CN")
            .GetAwaiter().GetResult();

        Assert("段落合并：两行折句按一个段落请求，仍按原行 id 回填",
            result.Lines.SequenceEqual(["这是一个很长的句子，", "下一行继续写。"])
            && result.ParagraphGroupCount == 1 && result.ParagraphGroupRows == 2
            && result.Mode.Contains("段落合并", StringComparison.Ordinal)
            && server.RequestCount == 1
            && server.LastRequestBody is not null
            && server.LastRequestBody.Contains("\"paragraphs\"", StringComparison.Ordinal),
            $"lines={string.Join("|", result.Lines)} groups={result.ParagraphGroupCount} " +
            $"rows={result.ParagraphGroupRows} mode={result.Mode} req={server.RequestCount} " +
            $"bodyHasParagraphs={server.LastRequestBody?.Contains("\"paragraphs\"", StringComparison.Ordinal)}");
    }

    static void Test_ParagraphGroupsAreBatched()
    {
        using var server = new FakeLlmServer((_, n) => n > 0
            ? (200, ChatResponseLines("第一段第一行，", "第一段第二行。", "第二段第一行，", "第二段第二行。"), null)
            : (200, ChatResponseSingle("设置"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            [
                "This is a long sentence that",
                "continues on the next line.",
                "Another long sentence that",
                "also continues here."
            ], "zh-CN").GetAwaiter().GetResult();

        Assert("段落合并：同屏多个段落可合并到一个请求，仍按原行顺序回填",
            result.Lines.SequenceEqual(["第一段第一行，", "第一段第二行。", "第二段第一行，", "第二段第二行。"])
            && result.ParagraphGroupCount == 2 && result.ParagraphGroupRows == 4
            && server.RequestCount == 1
            && server.LastRequestBody is not null
            && Regex.Matches(server.LastRequestBody, "\"lineIds\"").Count == 2,
            $"lines={string.Join("|", result.Lines)} groups={result.ParagraphGroupCount} " +
            $"rows={result.ParagraphGroupRows} req={server.RequestCount}");
    }

    static void Test_ParagraphGroupFallsBackWhenModelCollapses()
    {
        // 段落请求只回一条“整段译文”时不能硬拆；应回到逐行批量，避免把整段塞进第一行。
        using var server = new FakeLlmServer((idx, n) => idx == 1
            ? (200, ChatResponseLines("整段译文"), null)
            : (200, ChatResponseLines("这是第一行。", "这是第二行。"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["This is a long sentence that", "continues on the next line."], "zh-CN")
            .GetAwaiter().GetResult();

        Assert("段落合并：模型把段落压成一条时退回逐行批量，不硬拆",
            result.Lines.SequenceEqual(["这是第一行。", "这是第二行。"])
            && result.ParagraphGroupCount == 0
            && result.FailedCount == 0
            && server.RequestCount == 2,
            $"lines={string.Join("|", result.Lines)} groups={result.ParagraphGroupCount} " +
            $"failed={result.FailedCount} req={server.RequestCount} error={result.FirstError}");
    }

    static void Test_ParagraphGroupRejectsDuplicatedTranslation()
    {
        // 两行源文不同、段落响应却给两条一模一样的整段译文：不能当成正常译文，必须逐行回退。
        using var server = new FakeLlmServer((idx, n) => idx == 1
            ? (200, ChatResponseLines("整段译文", "整段译文"), null)
            : (200, ChatResponseLines("第一行译文。", "第二行译文。"), null));

        var result = ServiceFor(server).TranslateBatchAsync(
            ["This is a long sentence that", "continues on the next line."], "zh-CN")
            .GetAwaiter().GetResult();

        Assert("段落合并：源行不同但译文全相同 ⇒ 判定为整段重复，退回逐行",
            result.Lines.SequenceEqual(["第一行译文。", "第二行译文。"])
            && result.ParagraphGroupCount == 0
            && server.RequestCount == 2,
            $"lines={string.Join("|", result.Lines)} groups={result.ParagraphGroupCount} " +
            $"req={server.RequestCount} error={result.FirstError}");
    }

    // ═══════════════════════════════════════
    //  视觉路
    // ═══════════════════════════════════════

    static void Test_VisionRepairsEchoedLine()
    {
        using var server = new FakeLlmServer((idx, _) => idx == 1
            ? (200, ChatResponseVisionLines("打开文件", "Settings"), null)
            : (200, ChatResponseSingle("设置"), null));

        var config = new LlmConfig
        {
            UseCustomLlm = true,
            UseVision = true,
            ApiEndpoint = server.Endpoint,
            AllowUnofficialTranslateEndpoints = false,
        };
        var service = new TranslationService(config);
        var lines = service.TranslateImageWithVisionAsync(
            [0x89, 0x50, 0x4E, 0x47], "zh-CN", 2, default, ["Open file", "Settings"])
            .GetAwaiter().GetResult();

        Assert("视觉路行数正确但某行回显原文：按 OCR 源文补译，并露出补译计数",
            lines.SequenceEqual(["打开文件", "设置"])
            && service.LastContentRetryCount == 1
            && server.RequestCount == 2,
            $"lines={string.Join("|", lines)} contentRetry={service.LastContentRetryCount} req={server.RequestCount}");
    }

    // ═══════════════════════════════════════
    //  工具
    // ═══════════════════════════════════════

    static TranslationService ServiceFor(FakeLlmServer server) => new(new LlmConfig
    {
        UseCustomLlm = true,
        ApiEndpoint = server.Endpoint,
        AllowUnofficialTranslateEndpoints = false,
    });

    static void Check(string name, string source, string translation, string target,
        TranslationLineQuality expected)
    {
        var actual = TranslationQuality.Assess(source, translation, target);
        Assert(name, actual.Quality == expected,
            $"expected={expected} actual={actual.Quality} reason={actual.Reason}");
    }

    static string ChatResponseLines(params string[] lines) =>
        ChatResponseContent(JsonSerializer.Serialize(lines));

    static string ChatResponseVisionLines(params string[] lines) =>
        ChatResponseContent(string.Join('\n', lines));

    static string ChatResponseSingle(string text) =>
        ChatResponseContent(text);

    static string ChatResponseObjectText(int id, string text) =>
        ChatResponseContent(JsonSerializer.Serialize(new[] { new { id, text } }));

    static string ChatResponseContent(string content) =>
        JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } });

    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else
        {
            _failed++;
            Console.WriteLine(string.IsNullOrEmpty(detail)
                ? $"  ❌ {name}"
                : $"  ❌ {name} —— {detail}");
        }
    }

    /// <summary>
    /// 本地伪造 OpenAI 兼容端点。只实现 POST /v1/chat/completions：
    /// 读请求体、数 "id":N,"text" 的批量行数，按 (第几次请求, 批量行数) 回内容。
    /// 视觉请求/单行补译的 batchIds 都是 0，测试用 idx 区分。
    /// </summary>
    private sealed class FakeLlmServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Func<int, int, (int Status, string Body, string? ExtraHeaders)> _responder;
        private readonly Task _acceptLoop;
        private int _requests;

        public string Endpoint { get; }
        public int RequestCount => Volatile.Read(ref _requests);
        /// <summary>最近一次请求体（UTF-8 解码 + 反转义），用于断言段落请求确实带 paragraphs。</summary>
        public string? LastRequestBody { get; private set; }

        public FakeLlmServer(Func<int, int, (int Status, string Body, string? ExtraHeaders)> responder)
        {
            _responder = responder;
            _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
            int port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            Endpoint = $"http://127.0.0.1:{port}/v1";
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (SocketException) { break; }
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    using var ms = new MemoryStream();
                    var buf = new byte[8192];
                    int headerEnd = -1;
                    bool chunked = false;
                    int contentLength = -1;
                    while (true)
                    {
                        int n = await stream.ReadAsync(buf.AsMemory(), _stop.Token);
                        if (n == 0) break;
                        ms.Write(buf, 0, n);
                        if (headerEnd < 0)
                        {
                            string text = Encoding.ASCII.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                            int idx = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (idx >= 0)
                            {
                                headerEnd = idx;
                                string headers = text[..idx];
                                contentLength = ParseContentLength(headers);
                                chunked = headers.Contains("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase);
                            }
                        }
                        if (headerEnd >= 0)
                        {
                            if (chunked)
                            {
                                await ReadChunkedBodyAsync(stream, ms, headerEnd + 4, _stop.Token);
                                break;
                            }
                            if (contentLength >= 0 && ms.Length >= headerEnd + 4 + contentLength) break;
                            if (contentLength < 0) break;
                        }
                    }

                    if (headerEnd < 0) return;
                    int bodyStart = headerEnd + 4;
                    string body = Encoding.UTF8.GetString(
                        ms.GetBuffer(), bodyStart, (int)(ms.Length - bodyStart));
                    string decoded = Regex.Replace(body, @"\\u0022", "\"", RegexOptions.IgnoreCase)
                        .Replace("\\\"", "\"");
                    LastRequestBody = decoded;
                    int batchIds = Regex.Matches(decoded,
                        @"""id""\s*:\s*\d+\s*,\s*""text""", RegexOptions.IgnoreCase).Count;
                    int requestNo = Interlocked.Increment(ref _requests);
                    var (status, responseBody, extraHeaders) = _responder(requestNo, batchIds);

                    byte[] bodyBytes = Encoding.UTF8.GetBytes(responseBody);
                    string head =
                        $"HTTP/1.1 {status} OK\r\n" +
                        "Content-Type: application/json; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: close\r\n" +
                        (extraHeaders ?? "") + "\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(head).AsMemory(), _stop.Token);
                    await stream.WriteAsync(bodyBytes.AsMemory(), _stop.Token);
                    await stream.FlushAsync(_stop.Token);
                }
            }
            catch
            {
                // 客户端提前断开 / 测试结束：断言在调用侧体现。
            }
        }

        private static async Task<bool> EnsureBytesAsync(NetworkStream stream,
            MemoryStream ms, int needed, CancellationToken ct)
        {
            var buf = new byte[8192];
            while (ms.Length < needed)
            {
                int n = await stream.ReadAsync(buf.AsMemory(), ct);
                if (n == 0) return false;
                ms.Write(buf, 0, n);
            }
            return true;
        }

        private static int IndexOfCrLf(MemoryStream ms, int start)
        {
            var bytes = ms.GetBuffer();
            for (int i = start; i + 1 < ms.Length; i++)
                if (bytes[i] == (byte)'\r' && bytes[i + 1] == (byte)'\n') return i;
            return -1;
        }

        private static async Task<bool> ReadChunkedBodyAsync(NetworkStream stream,
            MemoryStream ms, int pos, CancellationToken ct)
        {
            while (true)
            {
                int lineEnd = IndexOfCrLf(ms, pos);
                while (lineEnd < 0)
                {
                    if (!await EnsureBytesAsync(stream, ms, (int)ms.Length + 1, ct)) return false;
                    lineEnd = IndexOfCrLf(ms, pos);
                }
                string sizeText = Encoding.ASCII.GetString(ms.GetBuffer(), pos, lineEnd - pos);
                int semi = sizeText.IndexOf(';');
                if (semi >= 0) sizeText = sizeText[..semi];
                int size = Convert.ToInt32(sizeText.Trim(), 16);
                pos = lineEnd + 2;
                if (size == 0) return true;
                if (!await EnsureBytesAsync(stream, ms, pos + size + 2, ct)) return false;
                pos += size + 2;
            }
        }

        private static int ParseContentLength(string headers)
        {
            foreach (var line in headers.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line["Content-Length:".Length..].Trim(), out int len))
                    return len;
            }
            return -1;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _acceptLoop.Wait(2000); } catch { }
        }
    }
}
