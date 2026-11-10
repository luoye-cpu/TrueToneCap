// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/OfflineTranslationTests.cs
// 离线翻译包套件 —— 包管理器扫描/装卸/导入 + 语言码归一 + 离线路的前置检查与异常口径。
//
// 设计说明：
//   · 全部走真实磁盘（%TEMP% 下的临时目录，见机器环境台账：别在 C:\PLAN 下建临时目录）。
//   · 不依赖 Python/argostranslate 的机器状态：所有断言都走"语言包文件缺失"这条
//     纯磁盘 IO 的确定性路径（引擎检查顺序：模型文件 → 运行时探测），探测根本不会发生。
//   · 在线翻译链路（TranslationService 其余分支）不在本套件 —— 本套件只钉离线的语义：
//     未就绪必须抛 TranslationNotConfiguredException（不是笼统的失败），且绝无在线回退。

using System.Net;
using System.Net.Sockets;
using System.Text;
using TrueToneCap.Core.Models;
using TrueToneCap.Core.Services;

namespace TrueToneCap.Test;

/// <summary>离线翻译包测试集。</summary>
public static class OfflineTranslationTests
{
    private static int _passed, _failed;

    static void Assert(string name, bool condition, string? detail = null)
    {
        if (condition) { _passed++; Console.WriteLine($"  ✅ {name}"); }
        else { _failed++; Console.WriteLine($"  ❌ {name}{(detail != null ? $": {detail}" : "")}"); }
    }

    /// <summary>临时模型目录（%TEMP% 子目录；台账：C:\PLAN 下自建目录会被外部清空）。</summary>
    private static string NewTempModelsDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ttc_offline_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WritePackageFile(string dir, string identifier, int bytes = 16)
    {
        File.WriteAllBytes(Path.Combine(dir, identifier + ".argosmodel"), new byte[bytes]);
    }

    /// <summary>套件级的引擎覆盖目录（RunAll 开头创建，结尾删除 —— 只删这一个目录，不碰别的临时内容）。</summary>
    private static string? _suiteModelsDir;

    public static int RunAll()
    {
        _passed = 0; _failed = 0;
        Console.WriteLine("══════════════════════════════════════");
        Console.WriteLine("  TrueToneCap 离线翻译包测试");
        Console.WriteLine("══════════════════════════════════════\n");

        // 引擎里的模型目录是静态覆盖位：进套件先指向空目录，套件内各用例自建目录；
        // 出套件恢复默认，避免污染其它套件。
        _suiteModelsDir = NewTempModelsDir();
        OfflineArgosEngine.SetModelsDir(_suiteModelsDir);
        try
        {
            Test_NormalizeLang_MapsKnownCodes();
            Test_NormalizeLang_UnknownReturnsNull();
            Test_ParseIdentifier_ValidAndInvalid();
            Test_DefaultPackages_IdentifiersAreArgosShaped();
            Test_Manager_ScanInstallUninstallRoundtrip();
            Test_Manager_UnknownFileNeverBecomesNullEntry();
            Test_Manager_LocalImport();
            Test_Manager_Download_FullFlow_LocalServer();
            Test_Manager_Download_LengthMismatch_FailsClean();
            Test_AnyBackendAvailable_OfflineFlagCounts();
            Test_CheckReady_ModelMissing_IsDeterministicDiskCheck();
            Test_TranslateAsync_OfflineNotReady_ThrowsNotConfigured();
            Test_DescribeBackends_OfflineOnly_PointsAtRealControls();
        }
        finally
        {
            OfflineArgosEngine.SetModelsDir(null);
            if (_suiteModelsDir is not null)
            {
                try { Directory.Delete(_suiteModelsDir, recursive: true); } catch { /* 清理失败不影响结果 */ }
                _suiteModelsDir = null;
            }
        }

        Console.WriteLine($"\n══════════════════════════════════════");
        TestSummary.Report("--offline-tests", _passed, _failed, 0, null, 0);
        Console.WriteLine($"══════════════════════════════════════\n");
        return _failed > 0 ? 1 : 0;
    }

    // ═══════════════════════════════════════
    //  语言码归一
    // ═══════════════════════════════════════

    private static void Test_NormalizeLang_MapsKnownCodes()
    {
        var cases = new (string? Input, string Expected)[]
        {
            ("zh-CN", "zh"), ("zh-Hans-CN", "zh"), ("zh", "zh"),
            ("zh-TW", "zt"), ("zh-Hant-HK", "zt"), ("zt", "zt"),
            ("en-US", "en"), ("en-GB", "en"), ("en", "en"),
            ("ja-JP", "ja"), ("ko-KR", "ko"), ("fr-FR", "fr"),
            ("de", "de"), ("ru", "ru"), ("es-ES", "es"),
            ("zh-en", "en"),   // ONNX 中英混合 → 按英语（混合内容里要翻的主要是英文）
        };
        int bad = 0;
        foreach (var (input, expected) in cases)
        {
            var got = OfflineArgosEngine.NormalizeLang(input);
            if (got != expected) { bad++; Console.WriteLine($"      {input} → {got ?? "(null)"}（期望 {expected}）"); }
        }
        Assert("NormalizeLang 已知语言码全部映射正确", bad == 0, $"{bad} 条不对");
    }

    private static void Test_NormalizeLang_UnknownReturnsNull()
    {
        Assert("NormalizeLang 空串返回 null", OfflineArgosEngine.NormalizeLang("") is null);
        Assert("NormalizeLang null 返回 null", OfflineArgosEngine.NormalizeLang(null) is null);
        Assert("NormalizeLang 不认识的码返回 null", OfflineArgosEngine.NormalizeLang("xx-YY") is null);
    }

    private static void Test_ParseIdentifier_ValidAndInvalid()
    {
        var ok = TranslationPackageInfo.ParseIdentifier("en_zh");
        Assert("ParseIdentifier 合法标识符拆出语言对", ok is { From: "en", To: "zh" });
        Assert("ParseIdentifier 连字符风格被拒（Argos 用下划线）",
            TranslationPackageInfo.ParseIdentifier("en-zh") is null);
        Assert("ParseIdentifier 无下划线被拒",
            TranslationPackageInfo.ParseIdentifier("foo") is null);
        Assert("ParseIdentifier 过长语言码被拒",
            TranslationPackageInfo.ParseIdentifier("engg_zh") is null);
    }

    private static void Test_DefaultPackages_IdentifiersAreArgosShaped()
    {
        var ids = TranslationPackageInfo.DefaultPackages.Select(p => p.Identifier).ToList();
        Assert("预设语言包标识符无重复", ids.Count == ids.Distinct().Count());
        Assert("预设语言包标识符全部是 from_to 形态",
            ids.All(id => TranslationPackageInfo.ParseIdentifier(id) is not null),
            string.Join(",", ids.Where(id => TranslationPackageInfo.ParseIdentifier(id) is null)));
        Assert("日文用 ja 不是 jp（2026-11-11 修正的回归）",
            ids.Contains("en_ja") && ids.Contains("ja_en") && !ids.Contains("en_jp"));
        // ⚠ 官方索引没有 zh↔zt（简↔繁）包（zh_zt 直链 404 实测）；传统中文的实包是 en↔zt
        Assert("传统中文用 en↔zt 而非不存在的 zh↔zt",
            ids.Contains("en_zt") && ids.Contains("zt_en") && !ids.Contains("zh_zt"));
        Assert("预设包都带版本号且兜底直链为 argos-net.com 真实命名",
            TranslationPackageInfo.DefaultPackages.All(p =>
                p.PackageVersion.Length > 0 &&
                p.FallbackDownloadUrl == $"https://argos-net.com/v1/translate-{p.Identifier}-{p.PackageVersion}.argosmodel"));
        Assert("预设包 SizeBytes 来自真实 Content-Length（62-160MB 区间）",
            TranslationPackageInfo.DefaultPackages.All(p => p.SizeBytes is > 60_000_000 and < 160_000_000));
    }

    // ═══════════════════════════════════════
    //  包管理器（真实磁盘）
    // ═══════════════════════════════════════

    private static void Test_Manager_ScanInstallUninstallRoundtrip()
    {
        var dir = NewTempModelsDir();
        try
        {
            WritePackageFile(dir, "en_zh", 1024);
            var mgr = new TranslationPackageManager(dir);
            Assert("扫描后 en_zh 已安装", mgr.IsPackageInstalled("en_zh"));
            Assert("已安装列表恰好 1 条", mgr.InstalledPackages.Count == 1);
            Assert("已安装包的 SizeBytes 是实际文件长度", mgr.GetPackage("en_zh")?.SizeBytes == 1024);
            Assert("未安装的包 IsPackageInstalled 为 false", !mgr.IsPackageInstalled("zh_en"));

            bool removed = mgr.UninstallPackage("en_zh");
            Assert("卸载返回 true", removed);
            Assert("卸载后磁盘文件已删", !File.Exists(Path.Combine(dir, "en_zh.argosmodel")));
            Assert("卸载后 IsPackageInstalled 为 false", !mgr.IsPackageInstalled("en_zh"));
            Assert("卸载不存在的包返回 false", !mgr.UninstallPackage("en_zh"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static void Test_Manager_UnknownFileNeverBecomesNullEntry()
    {
        // 上一版的回归：磁盘上出现不在预设清单里的文件时，`Add(pkg!)` 会把 null 塞进列表。
        var dir = NewTempModelsDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "en_pt.argosmodel"), new byte[8]);   // 真实形态的罕见语言对
            var mgr = new TranslationPackageManager(dir);
            Assert("目录里多出的 .argosmodel 不产生 null 条目",
                mgr.AvailablePackages.All(p => p is not null && !string.IsNullOrEmpty(p.Identifier)));
            Assert("罕见语言对被登记为已安装（可经 UI 删除）", mgr.IsPackageInstalled("en_pt"));
            Assert("预设清单没有被目录内容挤掉", mgr.GetPackage("en_zh") is not null);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static void Test_Manager_LocalImport()
    {
        var dir = NewTempModelsDir();
        var srcDir = NewTempModelsDir();
        try
        {
            var src = Path.Combine(srcDir, "en_fr.argosmodel");
            File.WriteAllBytes(src, new byte[64]);
            var mgr = new TranslationPackageManager(dir);

            var bad = mgr.InstallFromFileAsync(Path.Combine(srcDir, "notapackage.bin")).GetAwaiter().GetResult();
            Assert("非语言包命名的导入被拒", !bad.Success);

            var ok = mgr.InstallFromFileAsync(src).GetAwaiter().GetResult();
            Assert("合法 .argosmodel 本地导入成功", ok.Success, ok.Message);
            Assert("导入后目标目录出现同名文件", File.Exists(Path.Combine(dir, "en_fr.argosmodel")));
            Assert("导入后 IsPackageInstalled 为 true", mgr.IsPackageInstalled("en_fr"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
            try { Directory.Delete(srcDir, recursive: true); } catch { }
        }
    }

    // ═══════════════════════════════════════
    //  本地伪造源站：真实走完 下载→校验→落盘，不碰外网
    // ═══════════════════════════════════════

    /// <summary>起一个只应答一次 GET 的本地 HTTP 源站（原始 TcpListener，免 URLACL）。
    /// declaredLength 允许与实际发送字节数不符 —— 测"宣称 N 只发一半"的校验/清理路径。</summary>
    private static (int Port, Task ServerTask) StartFakeServer(byte[] payload, long? declaredLength)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var task = Task.Run(async () =>
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync();
                await using NetworkStream ns = client.GetStream();
                var buf = new byte[8192];
                var readAll = 0;
                while (readAll < buf.Length)
                {
                    int n = await ns.ReadAsync(buf.AsMemory(readAll));
                    if (n == 0) break;
                    readAll += n;
                    if (Encoding.ASCII.GetString(buf, 0, readAll).Contains("\r\n\r\n")) break;
                }
                string head =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: application/octet-stream\r\n" +
                    $"Content-Length: {declaredLength ?? payload.Length}\r\n" +
                    "Connection: close\r\n\r\n";
                await ns.WriteAsync(Encoding.ASCII.GetBytes(head));
                await ns.WriteAsync(payload, 0, payload.Length);
                await ns.FlushAsync();
            }
            catch { /* 客户端提前断开等 —— 断言会体现 */ }
            finally { listener.Stop(); }
        });
        return (port, task);
    }

    private static void Test_Manager_Download_FullFlow_LocalServer()
    {
        // 钉 2026-11-11 E2E 实测缺陷的回归：下载完成后 File.Move 抢在 FileStream 释放之前，
        // 67MB 全下完也在最后一步报"被另一进程占用"。本地源站给满 Content-Length ⇒ 必须完整落盘。
        var dir = NewTempModelsDir();
        var payload = new byte[1_000_000];
        Random.Shared.NextBytes(payload);
        var (port, serverTask) = StartFakeServer(payload, declaredLength: null);
        try
        {
            TranslationPackageManager.ForceResolvedUrlForTests("en_zh", $"http://127.0.0.1:{port}/pkg");
            var mgr = new TranslationPackageManager(dir);
            var result = mgr.DownloadPackageAsync("en_zh").GetAwaiter().GetResult();
            Assert("本地源站下载完整走通（下载→校验→Move 落盘）", result.Success, result.Message);
            Assert("落盘文件字节与源站一致",
                File.ReadAllBytes(Path.Combine(dir, "en_zh.argosmodel")).SequenceEqual(payload));
            Assert("临时文件无残留（%TEMP% 无 ttc_en_zh_*.tmp）",
                !Directory.GetFiles(Path.GetTempPath(), "ttc_en_zh_*.tmp").Any());
            Assert("安装状态就地更新且 SizeBytes 为实际长度",
                mgr.IsPackageInstalled("en_zh") && mgr.GetPackage("en_zh")?.SizeBytes == payload.Length);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        serverTask.Wait(3000);
    }

    private static void Test_Manager_Download_LengthMismatch_FailsClean()
    {
        // 源站宣称 Content-Length=2N 只发 N（.NET 会在流上抛"响应提前结束"，
        // 即便不抛也会撞我们的长度对账）⇒ 必须失败、半截文件不得装上、临时文件清干净
        // （那条早退/异常路径原先不清理临时文件）。
        var dir = NewTempModelsDir();
        var payload = new byte[500_000];
        Random.Shared.NextBytes(payload);
        var (port, serverTask) = StartFakeServer(payload, declaredLength: 1_000_000);
        try
        {
            TranslationPackageManager.ForceResolvedUrlForTests("zh_en", $"http://127.0.0.1:{port}/pkg");
            var mgr = new TranslationPackageManager(dir);
            var result = mgr.DownloadPackageAsync("zh_en").GetAwaiter().GetResult();
            Assert("长度不符被拒", !result.Success, result.Message);
            Assert("半截文件没被装上", !File.Exists(Path.Combine(dir, "zh_en.argosmodel")));
            Assert("失败路径临时文件也清理了（%TEMP% 无 ttc_zh_en_*.tmp）",
                !Directory.GetFiles(Path.GetTempPath(), "ttc_zh_en_*.tmp").Any());
            Assert("失败后安装状态为 false", !mgr.IsPackageInstalled("zh_en"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
        serverTask.Wait(3000);
    }

    // ═══════════════════════════════════════
    //  离线路前置检查与异常口径
    // ═══════════════════════════════════════

    private static void Test_AnyBackendAvailable_OfflineFlagCounts()
    {
        Assert("UseOfflineArgos 单独成立时 AnyBackendAvailable 为 true",
            TranslationService.AnyBackendAvailable(new LlmConfig { UseOfflineArgos = true }));
        // ⚠ AllowUnofficialTranslateEndpoints 默认 true（免费端点是 opt-out），
        // "全空"必须显式关掉它才真的是一条后端都没有。
        Assert("显式关掉免费端点且无其它配置时为 false",
            !TranslationService.AnyBackendAvailable(new LlmConfig { AllowUnofficialTranslateEndpoints = false }));
    }

    private static void Test_CheckReady_ModelMissing_IsDeterministicDiskCheck()
    {
        // 模型检查在运行时探测之前：空目录 + 机器上有没有 Python 都不影响这条结论。
        var ready = OfflineArgosEngine.CheckReady("en-US", "zh-CN");
        Assert("缺语言包 → ModelMissing",
            ready.State == OfflineArgosEngine.ReadinessState.ModelMissing, ready.State.ToString());
        Assert("缺的包标识符是归一化后的 en_zh", ready.PackageIdentifier == "en_zh");
        Assert("From/To 是 Argos 语言码", ready.From == "en" && ready.To == "zh");
    }

    private static void Test_TranslateAsync_OfflineNotReady_ThrowsNotConfigured()
    {
        // 空目录 ⇒ 模型缺失 ⇒ TranslationNotConfiguredException（不是笼统 TranslationException）。
        // "没配置"与"配置了但失败"必须分开 —— 与在线链路的口径一致。
        var svc = new TranslationService(new LlmConfig
        {
            UseOfflineArgos = true,
            OfflineSourceLangHint = "en-US",
            TargetLanguage = "zh-CN",
        });
        try
        {
            svc.TranslateAsync("hello", "zh-CN", "en").GetAwaiter().GetResult();
            Assert("未就绪的离线翻译必须抛异常", false, "居然正常返回了");
        }
        catch (TranslationNotConfiguredException ex)
        {
            Assert("未就绪的离线翻译抛 TranslationNotConfiguredException", true);
            Assert("异常文案点名「离线翻译包」（界面上真实存在的卡片）", ex.Message.Contains("离线翻译包"), ex.Message);
        }
        catch (Exception ex)
        {
            Assert("未就绪的离线翻译必须抛 TranslationNotConfiguredException", false,
                $"实际抛了 {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Test_DescribeBackends_OfflineOnly_PointsAtRealControls()
    {
        var text = TranslationService.DescribeBackends(new LlmConfig { UseOfflineArgos = true });
        Assert("离线-only 的 DescribeBackends 点名「离线翻译包」卡片", text.Contains("离线翻译包"), text);
        Assert("同时点名「安装运行环境」按钮", text.Contains("安装运行环境"), text);
        Assert("不再是'后端都没返回结果'的网络口径", !text.Contains("都没返回结果"), text);
    }
}
