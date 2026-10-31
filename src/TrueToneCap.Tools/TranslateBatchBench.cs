// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/TranslateBatchBench.cs
// 翻译「逐行×N vs 批量 1×N」的真实读数仪表（全仓性能审查 2026-10-31 §未量项 ②）。
//
//   dotnet run --project src/TrueToneCap.Tools -- --bench-translate [--out DIR] [--rows 14] [--conc 4]
//                                                  [--reps 5] [--target zh-Hans] [--source en] [--keep]
//
// 约定（口径同 SavePathBench.cs）：结论走 Environment.ExitCode + 落盘 CSV；
//   0=通过 / 1=回归或仪表不自证 / 3=可批量的端点不可达(SKIP)。**跑不通就退，不打印纸面换算。**
//
// 测的是什么：一屏 OCR 的行数 N，产品今天发 N 个【各含 1 条】的请求（并发 4），
//   而 Edge translatetext 的 body 本来就是【裸 JSON 字符串数组】⇒ 一条请求可以装 N 条。
//   两臂只差在"一次请求放几条"，URL / UA / 超时 / 并发 全部取产品自己的口径：
//     · 请求形状  TranslationService.EdgeFreeEndpointRequest()（反射调用，签名变了立刻抛）
//     · UA        TranslationService.EdgeClientUa（少这一个头就一条都发不出去）
//     · 单请求时限 TryEdgeFreeEndpointAsync() 里的 8 s 链接 CTS（外层 HttpClient.Timeout=15 s）
//     · 连接池    s_http 同款 SocketsHttpHandler{ConnectTimeout=10 s}
//     · 逐行并发  SemaphoreSlim(4)（见 TranslationService.cs:572 注释：一次点击翻译并发发多行）
//   解析：单条腿走产品的 ParseEdgeResponse()；批量腿产品【还没有】逐元素解析（那正是要评的改动），
//   本文件按同一形状逐元素取 translations[0].text，并在 CSV 里把这一列标成 bench-side 解析。
//
// ⚠ 只有 Edge 口能批量：必应 ttranslatev3 实测"同名三个 text 只翻第一条"（BingWebRequest 的注释），
//   MyMemory 是单条 q= ⇒ 若 Edge 不可达，这一项就量不了，退 3 并说明，不拿别的端点凑一个数。
// ⚠ 只发合成字符串（"hello world 测试"这一类），绝不发真实截图文本，绝不带任何凭据。
// ⚠ 正控：① N=1 时两臂同形 ⇒ 比值必须≈1（在这里不收敛就是仪表在量别的东西）；
//   ② 标尺会动 ⇒ 逐行腿的 wall 必须随 N 增长（N=rows vs N=4 ≥1.5×）；
//   ③ 批量腿与逐行腿的译文逐条对照（免费端点本身有近义词抖动，这一列只报数不判死）。

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using TrueToneCap.Core.Services;

/// <summary>翻译批量化的真实读数：1 个含 N 条的请求 vs N 个各含 1 条的请求（并发 4）。</summary>
public static class TranslateBatchBench
{
    // 合成语料：一眼假的数据，不含任何真实截图文本
    private static readonly string[] Corpus =
    [
        "hello world", "file edit view", "save to folder", "copy to clipboard",
        "the quick brown fox jumps", "测试 一行 中文 文本", "settings and privacy",
        "retry later please", "capture region now", "ocr preview window",
        "translate this line", "good afternoon everyone", "please confirm the save path",
        "network unreachable here",
    ];

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(10),   // 与 TranslationService.s_http 同值
    })
    {
        Timeout = TimeSpan.FromSeconds(15),           // 与 s_http 同值
    };

    private static readonly TimeSpan PerRequestLimit = TimeSpan.FromSeconds(8); // quickCts 同值

    public static int Run(string[] args)
    {
        string outDir = ResolveOut(args);
        int rows = Math.Max(2, GetIntOpt(args, "--rows", 14));
        int conc = Math.Max(1, GetIntOpt(args, "--conc", 4));
        int reps = Math.Max(2, GetIntOpt(args, "--reps", 5));
        string target = NonEmptyOpt(args, "--target", "zh-Hans");
        string? source = GetOpt(args, "--source");           // 空 ⇒ 自动检测（产品：整个 from= 不发）
        bool keep = Array.IndexOf(args, "--keep") >= 0;

        Console.WriteLine($"═══ [bench-translate] 逐行×N vs 批量 1×N（Edge translatetext）═══ out={outDir}");
        Console.WriteLine($"rows={rows} conc={conc} reps={reps} target={target} source="
            + (string.IsNullOrWhiteSpace(source) ? "<不发 from=>(auto)" : source));

        // ── 反射取产品的请求形状（internal static；签名变了立刻抛）──
        var tsType = typeof(TranslationService);
        var buildMi = tsType.GetMethod("EdgeFreeEndpointRequest", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("TranslationService.EdgeFreeEndpointRequest 不见了（仪表按此反射）");
        var parseMi = tsType.GetMethod("ParseEdgeResponse", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("TranslationService.ParseEdgeResponse 不见了（仪表按此反射）");
        var uaField = tsType.GetField("EdgeClientUa", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException("TranslationService.EdgeClientUa 不见了（少这个头一条都发不出去）");
        string ua = (string)uaField.GetValue(null)!;
        string url = BuildUrl(buildMi, Corpus[0], target, source);
        Console.WriteLine($"  请求形状取自产品：url={url}  UA=\"{ua}\"");

        var reach = new List<string>();
        var probe = PostSingle(parseMi, url, ua, "hello world 测试");
        reach.Add($"Edge(translatetext)={(probe.Text is not null ? "可达" : $"不可达(HTTP {(int)probe.Status} {probe.Note})")}");
        reach.Add($"MyMemory(单条腿，天生不可批量)={ProbeMyMemory(target)}");
        reach.Add($"必应凭据页(单条腿，仅出口探测)={ProbeBingCredsPage()}");
        Console.WriteLine("  出口探测：" + string.Join(" | ", reach));

        if (probe.Text is null)
        {
            Console.WriteLine("⏭ SKIP —— 唯一能批量的 Edge 口不可达（见上一行）。"
                + "网络出口未通 ⇒ 本轮翻译批量化【仍未量】：不打印任何由请求数推算的数（退出码 3）");
            WriteCsv(outDir, "bench_translate", new[]
            {
                "arm,rows,conc,reps,wall_ms_median,http_round_trips,per_request_ms_median,parse_side,note",
                $"SKIP_no_egress,{rows},{conc},{reps},-1,-1,-1,-,{CsvSafe(string.Join(";", reach))}",
            });
            return 3;
        }
        Console.WriteLine($"  首发译文：{Quote(probe.Text)}");

        var corpus = Enumerable.Range(0, rows).Select(i => Corpus[i % Corpus.Length]).ToArray();

        // ── 臂 A：逐行×N（产品现状，并发 conc）──
        var wallA = new List<long>();
        string?[] textsA = new string?[rows];
        for (int r = 0; r < reps; r++)
        {
            var sw = Stopwatch.StartNew();
            var tasks = RunPerRow(parseMi, url, ua, corpus, conc);
            sw.Stop();
            wallA.Add(sw.ElapsedMilliseconds);
            if (r == 0) textsA = tasks;
        }

        // ── 臂 B：批量 1×N（一次请求装 N 条）──
        var wallB = new List<long>();
        string?[] textsB = new string?[rows];
        int batchFails = 0;
        for (int r = 0; r < reps; r++)
        {
            var s = Stopwatch.StartNew();
            var got = PostBatch(url, ua, corpus);
            s.Stop();
            if (got is not null) { wallB.Add(s.ElapsedMilliseconds); textsB = got; }
            else { batchFails++; Console.WriteLine($"  ⚠ 批量腿第 {r + 1} 次失败（超时或形状不对）"); }
        }
        if (wallB.Count == 0)
        {
            Console.WriteLine("❌ 批量腿一次都没成功（单条腿却成功）—— 这不是慢一点，是数组 body 这条路不通"
                + $"（失败 {batchFails} 次）");
            return 1;
        }

        long medA = Median(wallA), medB = Median(wallB);
        double ratio = medB > 0 ? (double)medA / medB : 0.0;
        // 单请求耗时：并发把排队掩盖了，所以再串行走 5 条取分布
        var serialMs = new List<double>();
        for (int i = 0; i < Math.Min(rows, 5); i++)
        {
            var s = Stopwatch.StartNew();
            var t = PostSingle(parseMi, url, ua, corpus[i]);
            s.Stop();
            if (t.Text is not null) serialMs.Add(s.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine($"\n  臂A 逐行×{rows} (并发{conc}) wall 中位 = {medA} ms  HTTP 往返 = {rows}"
            + (serialMs.Count > 0 ? $"  单请求 ms 中位 = {MedianD(serialMs):F0}" : ""));
        Console.WriteLine($"  臂B 批量 1×{rows}          wall 中位 = {medB} ms  HTTP 往返 = 1");
        Console.WriteLine($"  比值 A/B = {ratio:F2}x");

        // ── 正控 ①：N=1 两臂同形 ⇒ 比值必须≈1 ──
        var w1a = new List<long>();
        var w1b = new List<long>();
        for (int r = 0; r < Math.Max(2, reps / 2); r++)
        {
            var s1 = Stopwatch.StartNew();
            _ = PostSingle(parseMi, url, ua, corpus[0]);
            s1.Stop();
            w1a.Add(s1.ElapsedMilliseconds);
            var s2 = Stopwatch.StartNew();
            _ = PostBatch(url, ua, [corpus[0]]);
            s2.Stop();
            w1b.Add(s2.ElapsedMilliseconds);
        }
        double convRatio = (double)Median(w1a) / Math.Max(1, Median(w1b));
        bool convOk = convRatio >= 0.6 && convRatio <= 1.7;
        Console.WriteLine($"  [正控·N=1 收敛] 逐行={Median(w1a)}ms 批量={Median(w1b)}ms 比值={convRatio:F2}x"
            + $"（两臂本就同形 ⇒ 期望≈1；0.6–1.7 之外即仪表在量别的东西）：{(convOk ? "通过" : "不通过")}");

        // ── 正控 ②：标尺会动 ⇒ 逐行腿 wall 随 N 增长（N=4 对照）──
        long medA4;
        {
            var s = Stopwatch.StartNew();
            _ = RunPerRow(parseMi, url, ua, corpus.Take(4).ToArray(), conc);
            s.Stop();
            medA4 = s.ElapsedMilliseconds;
        }
        double growRatio = (double)medA / Math.Max(1, medA4);
        bool ruler = growRatio >= 1.5;
        Console.WriteLine($"  [正控·标尺会动] 逐行腿 wall N=4 → {medA4}ms，N={rows} → {medA}ms，增长 "
            + $"{growRatio:F2}×（≥1.5 才算尺子会动）：{(ruler ? "通过" : "不通过")}");

        // ── 语义对撞：批量译文 vs 逐行译文（同 N 条源文）──
        int same = 0, both = 0;
        var diffs = new List<string>();
        for (int i = 0; i < rows; i++)
        {
            if (textsB[i] is null || textsA[i] is null) continue;
            both++;
            if (textsB[i] == textsA[i]) same++;
            else diffs.Add($"    行{i}: 源={Quote(corpus[i])} 逐行={Quote(textsA[i]!)} 批量={Quote(textsB[i]!)}");
        }
        Console.WriteLine($"  [语义] 批量与逐行同串 {same}/{both}（免费端点本身有近义词抖动，"
            + "这一列只报数不判死；并发首跑样本受网络抖动影响）");
        foreach (var d in diffs.Take(8)) Console.WriteLine(d);

        var csv = new List<string>
        {
            "arm,rows,conc,reps,wall_ms_median,http_round_trips,per_request_ms_median,parse_side,note",
            $"per-row,{rows},{conc},{reps},{medA},{rows},"
            + $"{(serialMs.Count > 0 ? MedianD(serialMs).ToString("F0", CultureInfo.InvariantCulture) : "-1")},"
            + "product.ParseEdgeResponse,产品现状",
            $"batch,{rows},{conc},{wallB.Count},{medB},1,-1,bench-side-per-element,产品尚无逐元素解析",
            $"control_n1_perrow,1,{conc},{w1a.Count},{Median(w1a)},1,-1,-,正控·应收敛",
            $"control_n1_batch,1,1,{w1b.Count},{Median(w1b)},1,-1,-,正控·应收敛",
            $"control_perrow_n4,4,{conc},1,{medA4},4,-1,-,正控·标尺",
            $"equality,{rows},-,-,-,-,{same}/{both},-,批量 vs 逐行同串",
            $"reachability,-,-,-,-,-,-,-,{CsvSafe(string.Join(";", reach))}",
        };
        var csvPath = WriteCsv(outDir, "bench_translate", csv);
        Console.WriteLine($"\nCSV → {csvPath}");
        if (keep)
            File.WriteAllText(Path.Combine(outDir, "keep_translate_corpus.json"),
                JsonSerializer.Serialize(corpus), new UTF8Encoding(false));

        // ── 判据 ──
        if (!convOk)
        {
            Console.WriteLine($"❌ non-discriminating:convergence —— N=1 时两臂本同形却差 {convRatio:F2}x，本次不判");
            return 1;
        }
        if (!ruler)
        {
            Console.WriteLine($"❌ non-discriminating:timing —— 逐行腿 N=4→{rows} 只涨 {growRatio:F2}×，"
                + "请求数变化没带动 wall，本次不判");
            return 1;
        }
        if (ratio < 1.5)
        {
            Console.WriteLine($"⚠ 读数：批量只快 {ratio:F2}×（<1.5×）⇒ 这条改动收益不成立"
                + "（仪表自证，判据退 1，把这一行当证据用）");
            return 1;
        }
        Console.WriteLine($"✅ bench-translate 通过：逐行×{rows}={medA}ms/{rows} 次往返 vs 批量 1×{rows}="
            + $"{medB}ms/1 次往返 ⇒ {ratio:F2}×（真实读数，非请求数换算）");
        return 0;
    }

    // ── 发送：全部走产品的请求形状；单请求 8 s 时限用链接 CTS（与 TryEdgeFreeEndpointAsync 同法）──

    private static string BuildUrl(MethodInfo buildMi, string text, string target, string? source)
    {
        var boxed = buildMi.Invoke(null, new object?[] { true, text, target, source })
            ?? throw new MissingMethodException(
                "EdgeFreeEndpointRequest(allowed:true,…) 返回 null —— 免费端点闸门口径变了");
        var t = (ValueTuple<string, string, string, string>)boxed;
        return t.Item1;
    }

    private static (string? Text, HttpStatusCode Status, string Note) PostSingle(
        MethodInfo parseMi, string url, string ua, string text)
    {
        var (status, body) = Send(url, ua, JsonSerializer.Serialize(new[] { text }));
        if (body is null) return (null, status, "请求失败/超时");
        var boxed = parseMi.Invoke(null, new object[] { (int)status, body })
            ?? throw new MissingMethodException("ParseEdgeResponse 返回 null（不该发生）");
        var t = (ValueTuple<string?, string>)boxed;
        return (t.Item1, status, t.Item2);
    }

    private static string?[]? PostBatch(string url, string ua, string[] texts)
    {
        var (status, body) = Send(url, ua, JsonSerializer.Serialize(texts));
        if (body is null) { Console.WriteLine($"  批量腿 HTTP {(int)status}"); return null; }
        try
        {
            using var doc = JsonDocument.Parse(body.Trim());
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine($"  批量腿响应不是数组（HTTP {(int)status}）");
                return null;
            }
            return doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("translations", out var tr)
                        && tr.ValueKind == JsonValueKind.Array && tr.GetArrayLength() > 0
                        && tr[0].TryGetProperty("text", out var t) ? t.GetString() : null)
                .ToArray();
        }
        catch (JsonException)
        {
            Console.WriteLine("  批量腿响应 JSON 解析失败");
            return null;
        }
    }

    private static (HttpStatusCode Status, string? Body) Send(string url, string ua, string jsonBody)
        => SendAsync(url, ua, jsonBody).GetAwaiter().GetResult();

    /// <summary>臂 A 的形状：N 个各含 1 条的请求，并发 = conc（产品 SemaphoreSlim(4)）。</summary>
    private static string?[] RunPerRow(MethodInfo parseMi, string url, string ua, string[] corpus, int conc)
    {
        using var gate = new SemaphoreSlim(conc, conc);
        var tasks = new Task<string?>[corpus.Length];
        for (int i = 0; i < corpus.Length; i++)
        {
            string text = corpus[i];
            tasks[i] = Task.Run(async () =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var (status, body) = await SendAsync(url, ua,
                        JsonSerializer.Serialize(new[] { text })).ConfigureAwait(false);
                    if (body is null) return null;
                    var boxed = parseMi.Invoke(null, new object[] { (int)status, body });
                    if (boxed is null) return null;
                    return ((ValueTuple<string?, string>)boxed).Item1;
                }
                catch (Exception) { return null; }
                finally { gate.Release(); }
            });
        }
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToArray();
    }

    private static async Task<(HttpStatusCode Status, string? Body)> SendAsync(string url, string ua, string jsonBody)
    {
        using var cts = new CancellationTokenSource(PerRequestLimit);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(jsonBody, new UTF8Encoding(false), "application/json"),
            };
            req.Headers.TryAddWithoutValidation("User-Agent", ua);
            using var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return (resp.StatusCode, text);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  发送失败 {ex.GetType().Name}: {Truncate(ex.Message, 90)}");
            return (HttpStatusCode.RequestTimeout, null);
        }
    }

    // ── 另两条端点只做"出口通不通"的探测：它们形状上就装不下 N 条，不参与两臂 ──
    private static string ProbeMyMemory(string target)
    {
        try
        {
            using var cts = new CancellationTokenSource(PerRequestLimit);
            string url = "https://api.mymemory.translated.net/get?q="
                + Uri.EscapeDataString("hello world 测试") + "&langpair=en|" + Uri.EscapeDataString(target);
            using var resp = Http.GetAsync(url, cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode ? "可达" : $"不可达(HTTP {(int)resp.StatusCode})";
        }
        catch (Exception ex) { return "不可达(" + ex.GetType().Name + ")"; }
    }

    private static string ProbeBingCredsPage()
    {
        try
        {
            using var cts = new CancellationTokenSource(PerRequestLimit);
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.bing.com/translator");
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
                + "Chrome/124.0.0.0 Safari/537.36");   // 与 BingBrowserUa 同值
            using var resp = Http.SendAsync(req, cts.Token).GetAwaiter().GetResult();
            return resp.IsSuccessStatusCode ? "可达" : $"不可达(HTTP {(int)resp.StatusCode})";
        }
        catch (Exception ex) { return "不可达(" + ex.GetType().Name + ")"; }
    }

    // ── 共用小件（口径同 SavePathBench.cs，刻意自带以免跨文件改私有件）──
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TrueToneCap.slnx"))
                || Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }

    private static string ResolveOut(string[] args)
    {
        string? o = GetOpt(args, "--out");
        if (string.IsNullOrWhiteSpace(o)) o = Path.Combine(FindRepoRoot(), "artifacts", "bench");
        o = Path.GetFullPath(o);
        Directory.CreateDirectory(o);
        return o;
    }

    private static string GetOpt(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null!;
    }

    private static string NonEmptyOpt(string[] args, string key, string def)
    {
        var v = GetOpt(args, key);
        return string.IsNullOrWhiteSpace(v) ? def : v;
    }

    private static int GetIntOpt(string[] args, string key, int def)
        => int.TryParse(GetOpt(args, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static long Median(List<long> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        return s[s.Length / 2];
    }

    private static double MedianD(List<double> xs)
    {
        if (xs.Count == 0) return 0;
        var s = xs.OrderBy(x => x).ToArray();
        return s[s.Length / 2];
    }

    private static string WriteCsv(string outDir, string prefix, IEnumerable<string> lines)
    {
        var path = Path.Combine(outDir,
            $"{prefix}_{DateTime.Now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture)}.csv");
        File.WriteAllLines(path, lines, new UTF8Encoding(false));
        return path;
    }

    private static string Quote(string s) => "\"" + s + "\"";
    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
    private static string CsvSafe(string s)
        => s.Replace(",", ";").Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
}
