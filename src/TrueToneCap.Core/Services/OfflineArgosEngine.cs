// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Services/OfflineArgosEngine.cs
// 离线翻译执行引擎 —— Argos Translate 的 Python 桥
//
// 为什么是 Python 桥而不是 C# 直接推理（2026-11-11 设计裁决，实施方案 Phase 2）：
//   Argos 的 .argosmodel 是 CTranslate2 二进制 + SentencePiece 词表，官方推理运行时
//   只有 Python（argostranslate），没有 C# 绑定。要在 C# 里原生跑得自己实现
//   编码器-解码器 beam search + unigram 分词，工作量与错位风险都不可接受。
//   本机已装的 Python + pip install argostranslate 就是官方路径；桥脚本由本类生成，
//   文本经 stdin 进、JSON 出，全程只碰本机进程，不产生任何网络请求。
//
// 性能口径：OCR 是逐行的，但 Argos 每个进程要加载模型（1-3 s）。
// 逐行各起一个进程就是 N 次模型加载 —— 所以提供 TranslateBatchAsync，
// 一次进程装下整批（行级容错在桥内做：失败行回填原文并计入 failed）。

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.Core.Services;

/// <summary>
/// 离线翻译执行引擎（Argos Translate / Python 桥）。
/// <para>下载语言包用 <see cref="TranslationPackageManager"/>；本类只管"装好之后怎么翻"。</para>
/// </summary>
public static class OfflineArgosEngine
{
    /// <summary>单次批量翻译的硬超时。本地模型加载 + 短句翻译远快于此；
    /// 卡死（环境异常）时宁可超时报错也不能无限挂着 UI 的翻译态。</summary>
    private static readonly TimeSpan s_batchTimeout = TimeSpan.FromSeconds(180);

    /// <summary>运行时探测的单命令超时。find_spec 探测不真正 import，正常亚秒级。</summary>
    private static readonly TimeSpan s_probeTimeout = TimeSpan.FromSeconds(10);

    // ── 模型目录 ──

    private static string? s_modelsDirOverride;

    /// <summary>默认模型目录：%LOCALAPPDATA%\TrueToneCap\translation-models。
    /// 不污染系统目录，用户可整目录清空/迁移（实施方案"存储位置"一节）。</summary>
    public static string ResolveModelsDir()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(root, "TrueToneCap", "translation-models");
    }

    /// <summary>测试/特殊部署用：覆盖模型目录。传 null 恢复默认。</summary>
    public static void SetModelsDir(string? dir) => s_modelsDirOverride = dir;

    /// <summary>当前生效的模型目录。</summary>
    public static string EffectiveModelsDir => s_modelsDirOverride ?? ResolveModelsDir();

    // ── 语言码归一 ──

    /// <summary>
    /// BCP-47 / OCR 语言标记 → Argos 语言码。不认识返回 null。
    /// <para>关键映射：简体一律 <c>zh</c>，繁体一律 <c>zt</c>（Argos 的两个中文码）；
    /// ONNX 引擎的"中英混合"（<c>zh-en</c>）按英语处理 —— 混排内容里要翻的主要是英文，
    /// 默认目标语言又多是中文，<c>en→zh</c> 恰是最高频的用法。</para>
    /// </summary>
    public static string? NormalizeLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();

        // 精确表：OCR 语言 Id（zh-en 混合、Windows OCR 的 zh-Hans-CN 等）与常见 BCP-47
        var exact = c switch
        {
            "zh-en" or "en-zh" => "en",                 // 中英混合 → 按英语翻译（见上）
            "zh" or "zh-cn" or "zh-sg" or "zh-hans" or "zh-hans-cn" or "zh-hans-sg" => "zh",
            "zt" or "zh-tw" or "zh-hk" or "zh-mo" or "zh-hant" or "zh-hant-tw" or "zh-hant-hk" => "zt",
            _ => null
        };
        if (exact is not null) return exact;

        // 前缀规则：en-US → en，ja-JP → ja，pt-BR → pt …
        foreach (var lang in new[] { "en", "ja", "ko", "fr", "de", "es", "it", "pt", "ru", "th", "vi", "ar" })
            if (c == lang || c.StartsWith(lang + '-'))
                return lang;
        return null;
    }

    // ── 就绪检查 ──

    /// <summary>离线翻译未就绪的原因。</summary>
    public enum ReadinessState { Ready, ModelMissing, RuntimeMissing }

    /// <summary>就绪检查结果。From/To 是归一化后的 Argos 语言码；PackageIdentifier 是缺的包（ModelMissing 时非空）。</summary>
    public sealed record ReadinessResult(
        ReadinessState State,
        string From,
        string To,
        string? PackageIdentifier,
        bool PythonFound,
        bool ArgosAvailable);

    /// <summary>
    /// 翻译前的静态检查（不启动翻译进程，只探测运行时）。给 UI 在扇出之前用。
    /// <para>顺序：先查语言包文件（纯磁盘 IO，确定性），再探测 Python 运行时
    /// （要起进程）。两边都缺时 State 报 ModelMissing，但 PythonFound/ArgosAvailable
    /// 会如实带上 —— 提示文案可以一次把两件事都说清，不用用户跑两轮。</para>
    /// </summary>
    public static ReadinessResult CheckReady(string? sourceLangHint, string targetLang)
    {
        var from = NormalizeLang(sourceLangHint) ?? "en";
        var to = NormalizeLang(targetLang) ?? "en";
        var (pythonFound, argosAvailable) = GetRuntimeStatus();

        if (from != to)
        {
            var pkg = $"{from}_{to}";
            if (!File.Exists(Path.Combine(EffectiveModelsDir, pkg + ".argosmodel")))
                return new ReadinessResult(ReadinessState.ModelMissing, from, to, pkg, pythonFound, argosAvailable);
            if (!argosAvailable)
                return new ReadinessResult(ReadinessState.RuntimeMissing, from, to, null, pythonFound, argosAvailable);
        }
        return new ReadinessResult(ReadinessState.Ready, from, to, null, pythonFound, argosAvailable);
    }

    // ── 运行时探测 ──

    /// <summary>Python 运行时探测结果。Command+PrefixArgs 是"怎么起这个解释器"
    /// （py 启动器要带 -3）；PythonFound=false 时两者无意义。</summary>
    private sealed record RuntimeProbe(bool PythonFound, bool ArgosAvailable, string? Command, string[]? PrefixArgs);

    private static RuntimeProbe? s_probe;
    private static readonly object s_probeLock = new();

    /// <summary>探测（或取缓存）本机 Python + argostranslate 状态。
    /// <para>探测顺序：py -3 → python → python3。微软商店的 python3.exe 占位符
    /// 打印一段话就退 9009，等价于"没有"，会自然落到下一个候选。</para></summary>
    public static (bool PythonFound, bool ArgosAvailable) GetRuntimeStatus(bool forceRefresh = false)
    {
        lock (s_probeLock)
        {
            if (!forceRefresh && s_probe is not null)
                return (s_probe.PythonFound, s_probe.ArgosAvailable);
            s_probe = ProbeRuntime();
            return (s_probe.PythonFound, s_probe.ArgosAvailable);
        }
    }

    /// <summary>取缓存探测里"怎么起解释器"（锁内读，避免撕裂）。PythonFound=false 时 Command 为 null。</summary>
    private static (string? Command, string[]? Prefix) GetProbeCommand()
    {
        lock (s_probeLock)
            return s_probe is { PythonFound: true }
                ? (s_probe.Command, s_probe.PrefixArgs)
                : (null, null);
    }

    private static RuntimeProbe ProbeRuntime()
    {
        foreach (var (cmd, prefix) in new[] { ("py", new[] { "-3" }), ("python", Array.Empty<string>()), ("python3", Array.Empty<string>()) })
        {
            var (found, argos) = ProbeOne(cmd, prefix);
            if (found)
                return new RuntimeProbe(true, argos, cmd, prefix);
        }
        return new RuntimeProbe(false, false, null, null);
    }

    private static (bool Found, bool Argos) ProbeOne(string command, string[] prefixArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in prefixArgs) psi.ArgumentList.Add(a);
            // find_spec 只查存在性，不真正 import（argostranslate 完整 import 要拉起
            // ctranslate2，冷启动数秒）—— 探测必须快。
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import importlib.util,sys; sys.stdout.write('Y' if importlib.util.find_spec('argostranslate') else 'N')");

            using var proc = Process.Start(psi);
            if (proc is null) return (false, false);
            using var timeoutCts = new CancellationTokenSource(s_probeTimeout);
            try
            {
                var outTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var errTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                var output = outTask.Result.Trim();
                _ = errTask;   // stderr 只丢弃，但已观察（上面 Result 已让读泵跑完）
                proc.WaitForExit((int)s_probeTimeout.TotalMilliseconds);
                if (!proc.HasExited)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return (false, false);
                }
                if (proc.ExitCode != 0) return (false, false);
                return output.Contains('Y') ? (true, true) : output.Contains('N') ? (true, false) : (false, false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AggregateException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return (false, false);
            }
        }
        catch
        {
            // Win32Exception（命令不存在）等 —— 换下一个候选
            return (false, false);
        }
    }

    /// <summary>给 UI 的运行环境状态文案（中文，界面会经词表转英文）。</summary>
    public static string GetRuntimeStatusText()
    {
        var (python, argos) = GetRuntimeStatus();
        return python
            ? argos ? "✅ Python + argostranslate 就绪"
                    : "⚠ 已装 Python，缺 argostranslate 包"
            : "⚠ 未检测到 Python";
    }

    // ── 安装运行环境（用户在设置里点了"安装运行环境"才跑） ──

    /// <summary>安装结果。</summary>
    public sealed record InstallResult(bool Success, string Message);

    /// <summary>
    /// 经 pip 安装 argostranslate。耗时数分钟（ctranslate2 轮子 ~50MB），调用方负责
    /// 界面态与按钮禁用；本方法内部有 15 分钟硬超时。
    /// </summary>
    public static async Task<InstallResult> EnsureRuntimeAsync(CancellationToken ct = default)
    {
        var probe = GetRuntimeStatus();
        if (probe.ArgosAvailable)
            return new InstallResult(true, "✅ 运行环境已就绪（Python + argostranslate）");
        if (!probe.PythonFound)
            return new InstallResult(false,
                "未检测到 Python。请先安装 Python 3.10 及以上版本（python.org 或微软商店），装好后回到这里点「安装运行环境」。");

        var (cmd, prefix) = GetProbeCommand();
        if (cmd is null)
            return new InstallResult(false, "未检测到 Python。请先安装 Python 3.10+。");

        CoreLog.Info("OfflineTranslate", $"开始安装 argostranslate（pip，解释器 {cmd}）");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            if (prefix is { Length: > 0 })
                foreach (var a in prefix) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("pip");
            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("argostranslate");

            using var proc = Process.Start(psi);
            if (proc is null) return new InstallResult(false, "无法启动 pip 进程");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                var stdout = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
                var stderr = proc.StandardError.ReadToEndAsync(timeoutCts.Token);
                await proc.WaitForExitAsync(timeoutCts.Token);
                var tail = (await stdout).Trim();
                var err = (await stderr).Trim();
                if (proc.ExitCode == 0)
                {
                    GetRuntimeStatus(forceRefresh: true);
                    CoreLog.Info("OfflineTranslate", "argostranslate 安装成功");
                    return new InstallResult(true, "✅ argostranslate 安装完成，可以下载语言包了。");
                }
                CoreLog.Warn("OfflineTranslate", $"pip 安装失败（exit {proc.ExitCode}）: {err}");
                return new InstallResult(false, $"安装失败（pip 退出码 {proc.ExitCode}）：{Truncate(err, 300)}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return new InstallResult(false, "安装超时（15 分钟），请检查网络后重试。");
            }
        }
        catch (OperationCanceledException)
        {
            throw;   // 调用方主动取消，原样上抛
        }
        catch (Exception ex)
        {
            return new InstallResult(false, $"安装失败：{ex.Message}");
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── 批量翻译 ──

    /// <summary>批量翻译结果。FailedCount>0 表示有行被回填原文；FirstError 是第一条失败原因。</summary>
    public sealed record BatchResult(string[] Lines, int FailedCount, string? FirstError);

    /// <summary>
    /// 批量翻译一组 OCR 行（一个 Python 进程装载整批，行级容错在桥内做）。
    /// <para>异常口径：<see cref="TranslationNotConfiguredException"/> = 环境/语言包没准备好
    /// （该去下载或安装）；<see cref="TranslationException"/> = 环境在但翻译本身失败。</para>
    /// </summary>
    public static async Task<BatchResult> TranslateBatchAsync(
        IReadOnlyList<string> lines, string fromLang, string toLang, CancellationToken ct = default)
    {
        if (lines.Count == 0) return new BatchResult(Array.Empty<string>(), 0, null);
        var from = NormalizeLang(fromLang) ?? "en";
        var to = NormalizeLang(toLang) ?? "en";
        if (from == to)
        {
            // 同语言：没有对应的 Argos 包（en_en 不存在），语义上也不用翻，原样返回。
            return new BatchResult(lines.ToArray(), 0, null);
        }

        var modelsDir = EffectiveModelsDir;
        var pkgFile = Path.Combine(modelsDir, $"{from}_{to}.argosmodel");
        if (!File.Exists(pkgFile))
            throw new TranslationNotConfiguredException(
                $"缺少离线语言包 {from}_{to}：到 设置 → OCR/翻译 的「离线翻译包」卡片下载，或从本地文件导入。");

        var probe = GetRuntimeStatus();
        if (!probe.PythonFound)
            throw new TranslationNotConfiguredException(
                "未检测到 Python 运行环境：离线翻译需要 Python 3.10+ 与 argostranslate 包。"
                + "可到 设置 → OCR/翻译 的「离线翻译包」卡片点「安装运行环境」，或改用在线翻译。");
        if (!probe.ArgosAvailable)
            throw new TranslationNotConfiguredException(
                "已检测到 Python，但缺 argostranslate 包：到 设置 → OCR/翻译 的「离线翻译包」卡片点「安装运行环境」，或改用在线翻译。");

        var bridge = WriteBridgeScript();
        var payload = string.Join('\n', lines);

        var (cmd, prefix) = GetProbeCommand();
        var psi = new ProcessStartInfo
        {
            FileName = cmd ?? "python",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ⚠ 必须是无 BOM 的 UTF8：Encoding.UTF8 属性带 UTF-8 前导符，StreamWriter 会在
            // 首次写入时发射 BOM ⇒ 桥收到的第一行开头多出 U+FEFF（2026-11-11 端到端实测）。
            StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        if (prefix is { Length: > 0 })
            foreach (var a in prefix) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-X");
        psi.ArgumentList.Add("utf8");
        psi.ArgumentList.Add(bridge);
        psi.ArgumentList.Add(from);
        psi.ArgumentList.Add(to);
        psi.ArgumentList.Add(modelsDir);

        CoreLog.Info("OfflineTranslate", $"离线翻译 {from}→{to}，{lines.Count} 行，模型 {pkgFile}");
        var sw = Stopwatch.StartNew();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(s_batchTimeout);
        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception ex)
        {
            throw new TranslationException($"无法启动 Python 进程：{ex.Message}");
        }
        if (proc is null)
            throw new TranslationException("无法启动 Python 进程（Process.Start 返回 null）");

        try
        {
            // 读流任务先行启动：stdout/stderr 管道若有积压，写 stdin 的那头才不会被
            // 子进程的输出堵成死锁（先读后写是这类管道交互的固定顺序）。
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(timeoutCts.Token);

            await proc.StandardInput.WriteAsync(payload.AsMemory(), timeoutCts.Token);
            proc.StandardInput.Close();

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            await proc.WaitForExitAsync(timeoutCts.Token);

            if (proc.ExitCode != 0)
                throw new TranslationException(
                    $"离线翻译进程异常退出（exit {proc.ExitCode}）：{Truncate(stderr, 300)}");

            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            var outLines = root.GetProperty("lines").EnumerateArray()
                .Select(e => e.GetString() ?? "").ToArray();
            var failed = root.TryGetProperty("failed", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

            if (outLines.Length != lines.Count)
                throw new TranslationException(
                    $"离线翻译返回行数不符（输入 {lines.Count}，返回 {outLines.Length}）—— 不按顺序硬套，错位覆盖比失败更难被发现。");

            if (stderr.Length > 0)
                CoreLog.Debug("OfflineTranslate", $"桥 stderr: {Truncate(stderr, 500)}");
            CoreLog.Info("OfflineTranslate", $"离线翻译完成：{lines.Count} 行 / 失败 {failed}，耗时 {sw.ElapsedMilliseconds} ms");
            return new BatchResult(outLines, failed, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // 硬超时（不是用户取消）：清进程树，报人话
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw new TranslationException("离线翻译超时（180 秒）—— 本地模型可能未就绪或环境异常。");
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;   // 用户取消（关窗），原样上抛
        }
        catch (JsonException ex)
        {
            throw new TranslationException($"离线翻译返回无法解析：{Truncate(ex.Message, 200)}");
        }
        finally
        {
            proc.Dispose();
        }
    }

    /// <summary>桥脚本落盘路径（%TEMP%，随用随写，避免外部清理导致缺失 —— 见机器环境台账）。</summary>
    private static string WriteBridgeScript()
    {
        var path = Path.Combine(Path.GetTempPath(), "TrueToneCap_argos_bridge.py");
        File.WriteAllText(path, BridgeScriptPython, new System.Text.UTF8Encoding(false));
        return path;
    }

    /// <summary>桥脚本源码。协议：argv[1..3] = 源语言 目标语言 模型目录；stdin 一行一条原文；
    /// stdout 一份 JSON（lines/failed/error）。文本只在本机进程间流转。
    /// <para>⚠ API 兼容（2026-11-11 端到端实测）：argostranslate 1.11 把
    /// <c>translate_text</c> 改名为 <c>translate</c>（签名同为 (q, from_code, to_code)），
    /// 桥里用 getattr 兼容两个版本名 —— 装上即最新版时也能翻。</para></summary>
    private const string BridgeScriptPython = """
        # TrueToneCap Argos bridge — generated by OfflineArgosEngine.cs, do not edit.
        import sys, os, json

        # ⚠ 必须在 import argostranslate 之前设置（settings 在模块导入期读取）。
        # 默认 ARGOSTRANSLATE 分句档会向 huggingface.co 拉 stanza-en 分词模型
        # （2026-11-11 端到端实测：离线机器上翻译必挂，装好语言包也白搭）。
        # MINISBD 是纯本地分句，OCR 行本就是短行，粒度足够 —— "完全离线"由此成立。
        os.environ.setdefault('ARGOS_CHUNK_TYPE', 'MINISBD')

        def main():
            frm, to, models_dir = sys.argv[1], sys.argv[2], sys.argv[3]
            raw = sys.stdin.buffer.read().decode('utf-8')
            lines = raw.split('\n')
            out, failed, first_err = [], 0, None
            try:
                from argostranslate import package as apkg
                from argostranslate import translate as atrans
                have = {(p.from_code, p.to_code) for p in apkg.get_installed_packages()}
                if (frm, to) not in have:
                    pkg_path = os.path.join(models_dir, frm + '_' + to + '.argosmodel')
                    if os.path.isfile(pkg_path):
                        try:
                            apkg.install_from_path(pkg_path)
                        except Exception as e:
                            first_err = first_err or ('register model failed: %s' % e)
                # argostranslate 1.11 起函数改名 translate_text -> translate；新旧版本都接
                translate_fn = getattr(atrans, 'translate', None) or getattr(atrans, 'translate_text')
                for ln in lines:
                    if ln == '':
                        out.append(ln)
                        continue
                    try:
                        out.append(translate_fn(ln, frm, to))
                    except Exception as e:
                        failed += 1
                        first_err = first_err or ('%s->%s: %s' % (frm, to, e))
                        out.append(ln)   # 行级容错：失败行回填原文
            except Exception as e:
                sys.stderr.write('fatal: %s\n' % e)
                sys.exit(2)
            sys.stdout.buffer.write(json.dumps(
                {'lines': out, 'failed': failed, 'error': first_err},
                ensure_ascii=False).encode('utf-8'))

        main()
        """;
}
