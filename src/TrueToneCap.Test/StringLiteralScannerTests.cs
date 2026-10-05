// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Test/StringLiteralScannerTests.cs
// 字面量扫描器自身的对抗用例 —— 它给"HDR 抓帧窗烘焙中文"当尺子，尺子坏了整条锁就是假的
// 运行: dotnet run --project src/TrueToneCap.Test -c Release --no-build -- --unit-tests

namespace TrueToneCap.Test;

/// <summary>
/// 夹具全是【故意写坏】的 C#：注释里藏引号、字符字面量里藏引号与撇号、插值洞里再套串。
/// 每条用例都问同一个问题——"扫描器若在这条规则上偷懒，这里的数会不会变？"
/// 答案必须是"会变"，否则那条规则根本没被判到（本仓 §"证伪夹具要比现实更苛刻"）。
/// </summary>
public static class StringLiteralScannerTests
{
    /// <summary>由 <c>CorePipelineTests.RunAll</c> 调用；断言出口复用它的 <c>Assert</c>。
    /// 不复用它记账就等于第二套 passed/failed 计数器 —— 两个事实源会互相说谎。</summary>
    public static void RunAll(Action<string, bool, string?> assert)
    {
        Console.WriteLine("── StringLiteralScanner：复合夹具（一次过所有构造）──");
        CompositeFixture(assert);
        Console.WriteLine("── StringLiteralScanner：未闭合引号（负向夹具）──");
        UnterminatedQuoteIsLoud(assert);
        Console.WriteLine("── StringLiteralScanner：插值洞里的串 ──");
        HoleInnerLiteralCountedOnce(assert);
        Console.WriteLine("── StringLiteralScanner：注释排除 ──");
        CommentsExcluded(assert);
        Console.WriteLine("── StringLiteralScanner：字符字面量 ──");
        CharLiteralsNotCounted(assert);
        Console.WriteLine("── StringLiteralScanner：插值转义花括号 ──");
        EscapedBraces(assert);
        Console.WriteLine("── StringLiteralScanner：原始串（覆盖范围外的两种坏法）──");
        RawStringFailsLoud(assert);
    }

    /// <summary>边界：原始字符串字面量 <c>"""…"""</c> 不在覆盖范围内（目标文件一处都没有）。
    /// 这条不是"顺手支持它"，是把两种坏法都量出来 —— 谁说"不支持就等于失明"，读数在此：
    /// 跨行那种当场报未闭合（调用方判红），单行那种条数变化撞基线。<b>都不许静默数出一堆碎片。</b></summary>
    static void RawStringFailsLoud(Action<string, bool, string?> assert)
    {
        var multi = StringLiteralScanner.Scan("var s = \"\"\"\n烘焙中文在原始串里\n\"\"\";");
        assert("跨行的原始串：未闭合判红、且不产出任何【完整】字面量（丢中文必伴随红，不是静默）",
            multi.UnterminatedAt >= 0 && StringLiteralScanner.CjkLiterals(multi).Count == 0,
            $"@{multi.UnterminatedAt}｜{multi.UnterminatedNote}｜{string.Join(" | ", multi.Literals.Select(l => l.Text))}");

        var single = StringLiteralScanner.Scan("var s = \"\"\"行内原始串\"\"\";");
        assert("单行的原始串：切成三条（两条空串 + 一条含文案）⇒ 条数与基线对不上就红",
            single.UnterminatedAt == -1 && single.Literals.Count == 3 &&
            single.Literals.Any(l => l.Text == "\"行内原始串\""),
            $"实得 {single.Literals.Count} 条：{string.Join(" | ", single.Literals.Select(l => l.Text))}");
    }

    /// <summary>一条输入把全部构造凑齐：手点 = 13 条字面量，其中 11 条含中文。</summary>
    static void CompositeFixture(Action<string, bool, string?> assert)
    {
        const string Fixture = """
            // 行注释里的 "注释中文" 不算
            /* 块注释 "块内中文"
               延续行的 "同样不算" */
            /// <summary>文档注释 "文档中文" 不算</summary>
            class Demo {
                // 孤撇号别想带偏：don't, it's fine
                void M() {
                    string a = "普通 \"转义\" 结尾";
                    string b = @"逐字 ""双写"" 中文";
                    string c = $"插值 {b} 尾巴";
                    string d = $@"插值逐字 {c} ""x"" 完";
                    string e = @$"前缀反过来 {d}";
                    string f = $"洞里套串 {Get("洞内中文")} 完";
                    string g = $"嵌套花括号 {new[]{1, 2}.Length} 个";
                    string h = $"外层 {$"内层{a}"} 结束";
                    char q1 = '{';
                    char q2 = '"';
                    char q3 = '\'';
                    string u = "http://not-a-comment.example/路径";
                    string v = "don't panic";
                    string w = "";
                }
            }
            """;

        var r = StringLiteralScanner.Scan(Fixture);
        string All() => string.Join(" | ", r.Literals.Select(l => l.Text));

        // ═══ 总数：手点数出来的 13，少一条=漏，多一条=注释/字符字面量被当成了串 ═══
        // 逐条：a / b / c / d($@) / e(@$) / f外 / f内 / g / h外 / h内 / u / v / w
        assert("复合夹具扫出 13 条字面量（少一条=漏构造，多一条=注释或字符字面量被误计）",
            r.Literals.Count == 13, $"实得 {r.Literals.Count} 条：{All()}");
        assert("复合夹具里 11 条含中文（v 的英文串与 w 的空串不该进；注释里那 4 段中文串也不该进）",
            StringLiteralScanner.CjkLiterals(r).Count == 11,
            $"实得 {StringLiteralScanner.CjkLiterals(r).Count}：{string.Join(" | ", StringLiteralScanner.CjkLiterals(r).Select(l => l.Text))}");
        assert("复合夹具全程闭合（UnterminatedAt 必须是 -1，否则上面两个数不是字面量计数）",
            r.UnterminatedAt == -1, r.UnterminatedNote);

        // 转义：\" 不许提前收尾；Text 保留源码原形（计数单位是源码字面量，不是解码后的值）
        assert("普通串的 [反斜杠+引号] 是转义、不提前收尾（Text 保留源码原形）",
            r.Literals.Any(l => l.Text == "\"普通 \\\"转义\\\" 结尾\""), All());

        // 逐字串："" 是转义引号、不是收尾
        assert(@"逐字串 """" 双写引号不提前收尾",
            r.Literals.Any(l => l.Text == "@\"逐字 \"\"双写\"\" 中文\"" && l.Kind == StringLiteralScanner.Kind.Verbatim), All());

        // $@ 与 @$ 两种前缀都要认（漏一种就是这两条从计数里消失）
        assert("$@\"…\" 认成插值逐字串",
            r.Literals.Any(l => l.Kind == StringLiteralScanner.Kind.InterpolatedVerbatim && l.Text.Contains("插值逐字")), All());
        assert("@$\"…\" 反写前缀也认",
            r.Literals.Any(l => l.Kind == StringLiteralScanner.Kind.InterpolatedVerbatim && l.Text.Contains("前缀反过来")), All());

        // 插值洞：洞内源码整段剔除，但洞里的串单独计一次
        var fOuter = r.Literals.FirstOrDefault(l => l.Text.Contains("洞里套串"));
        assert("洞里套串：外层这条存在且 Text 不含洞的源码（Get 与 洞内中文 都不许在内）",
            fOuter is not null && !fOuter.Text.Contains("Get") && !fOuter.Text.Contains("洞内中文"),
            fOuter?.Text ?? "(未找到外层)");
        assert("洞里套串：内层那条恰好计一次",
            r.Literals.Count(l => l.Text == "\"洞内中文\"") == 1, All());

        // 洞里的嵌套花括号（数组初始化器）不许提前结束洞
        assert("嵌套花括号：洞里的 {1, 2} 不许被当成洞的收尾（外层这条必须完整、且不吞源码）",
            r.Literals.Count(l => l.Text.Contains("嵌套花括号")) == 1 &&
            !r.Literals.Any(l => l.Text.Contains("new[]")), All());

        // 洞里再套一条插值串：两条都计，外层不含内层源码
        var hOuter = r.Literals.FirstOrDefault(l => l.Text.Contains("外层"));
        assert("洞里再套插值串：内层单独计一次、外层 Text 不含内层",
            r.Literals.Count(l => l.Text.StartsWith("$\"内层")) == 1 && hOuter is not null && !hOuter.Text.Contains("内层"),
            hOuter?.Text ?? "(没扫到外层那条 ⇒ 计数已经先错了)");

        // 字符字面量：'"' 里的引号不许开一条串（真被开走的话后面整段会被吞成一条怪串）
        assert("字符字面量 '\"' 没被当成串开头（没有任何 Text 吞掉后续代码）",
            !r.Literals.Any(l => l.Text.Contains("char") || l.Text.Contains("q2") || l.Text.Contains("q3")), All());
        assert("串里的撇号 don't 不许被当成字符字面量开头",
            r.Literals.Any(l => l.Text == "\"don't panic\""), All());

        // 串里的 // 不许被当成行注释（否则这条中文串连同收尾引号一起蒸发）
        assert("串里的 http:// 没被读成行注释",
            r.Literals.Any(l => l.Text == "\"http://not-a-comment.example/路径\""), All());

        assert("空串 \"\" 也算一条字面量（计数单位是字面量，不是文案）",
            r.Literals.Count(l => l.Text == "\"\"") == 1, All());

        var aLit = r.Literals.FirstOrDefault(l => l.Text.Contains("普通"));
        assert("行号跟着走：a 那条落在夹具第 8 行（换行计数错了就报不准位置）",
            aLit is not null && aLit.Line == 8, aLit is null ? "(没扫到 a 那条)" : $"实得第 {aLit.Line} 行");

        assert("注释里的 4 段中文串一条都没进结果",
            !r.Literals.Any(l => l.Text.Contains("注释中文") || l.Text.Contains("块内中文")
                || l.Text.Contains("同样不算") || l.Text.Contains("文档中文")), All());
    }

    /// <summary>负向夹具：未闭合引号的处置必须是【显式】的——整条丢弃 + 记下位置，
    /// 既不抛异常（一把尺不该把自己炸掉），也不留一条残破字面量在计数里。</summary>
    static void UnterminatedQuoteIsLoud(Action<string, bool, string?> assert)
    {
        const string Broken = """
            string ok = "先一条闭合的";
            string bad = "引号没关上这行就断了
            string after = "后面这条还得数到";
            """;

        var r = StringLiteralScanner.Scan(Broken);
        assert("未闭合的普通串：不产出该条字面量（只留前后两条完整的 ⇒ 计数=2）",
            r.Literals.Count == 2 && r.Literals.All(l => !l.Text.Contains("引号没关上")),
            $"实得 {r.Literals.Count} 条：{string.Join(" | ", r.Literals.Select(l => l.Text))}");
        assert("未闭合的位置必须记在 UnterminatedAt（调用方据此判红，不能默默数下去）",
            r.UnterminatedAt == Broken.IndexOf("引号没关上", StringComparison.Ordinal) - 1 &&
            r.UnterminatedNote.Contains("第 2 行"),
            $"@{r.UnterminatedAt} {r.UnterminatedNote}");
        assert("未闭合不许把后面那条也带偏（下一个串照常数到）",
            r.Literals.Any(l => l.Text == "\"后面这条还得数到\""),
            string.Join(" | ", r.Literals.Select(l => l.Text)));

        // 逐字串按定义可跨行 ⇒ 未闭合就是吃到文件尾，同样要报位置
        const string BrokenVerbatim = """
            string v = @"逐字串没有收尾 一直写到文件尾
            """;
        var rv = StringLiteralScanner.Scan(BrokenVerbatim);
        // Start/UnterminatedAt 指向前缀的起始（@" 的 @）—— 与 Literal.Start 同一定义，别两处各指一处
        assert("未闭合的逐字串（跨行合法 ⇒ 吃到文件尾）同样只报位置、不产出字面量",
            rv.Literals.Count == 0 && rv.UnterminatedAt == BrokenVerbatim.IndexOf('@') &&
            rv.UnterminatedNote.Contains("文件尾"),
            $"实得 {rv.Literals.Count} 条，位置 {rv.UnterminatedAt}｜{rv.UnterminatedNote}");

        // 孤撇号：字符字面量没收尾 ⇒ 跳过它自己继续，不许把后面的代码吞掉
        const string LoneApostrophe = """
            char x = 'a;
            string y = "这条还得数到";
            """;
        var ra = StringLiteralScanner.Scan(LoneApostrophe);
        assert("孤撇号没收尾时扫描器就地恢复（后面那条仍计到，且不谎报未闭合的串）",
            ra.Literals.Count == 1 && ra.Literals[0].Text == "\"这条还得数到\"" && ra.UnterminatedAt == -1,
            $"实得 {string.Join(" | ", ra.Literals.Select(l => l.Text))} @{ra.UnterminatedAt}");
    }

    /// <summary>要求 (c)：洞里的串计一次，外层 Text 不含洞的源码。</summary>
    static void HoleInnerLiteralCountedOnce(Action<string, bool, string?> assert)
    {
        const string Hole = """
            var t = $"计数 {Get("次")} 次";
            """;
        var r = StringLiteralScanner.Scan(Hole);
        var outer = r.Literals.FirstOrDefault(l => l.Text.Contains("计数"));

        assert("插值洞含串：一共两条字面量（外层 + 洞内，谁都不许多算一次）",
            r.Literals.Count == 2, string.Join(" | ", r.Literals.Select(l => l.Text)));
        assert("插值洞含串：洞内那条原文恰好计一次",
            r.Literals.Count(l => l.Text == "\"次\"") == 1, string.Join(" | ", r.Literals.Select(l => l.Text)));
        assert("插值洞含串：外层 Text 用占位符替掉洞的源码 ⇒ 文案归属不会算错",
            outer is not null && outer.Text == "$\"计数 {…} 次\"", outer?.Text ?? "(未找到外层)");
        assert("插值洞含串：两条都含中文 ⇒ 这条 CJK 计数是 2（各算各的文案）",
            StringLiteralScanner.CjkLiterals(r).Count == 2, string.Join(" | ", r.Literals.Select(l => l.Text)));
    }

    /// <summary>要求 (d)：注释文本必须被排除。四段中文串都藏在注释里 ⇒
    /// 一旦注释跳过规则坏掉，条数就从 1 涨到 5，这条用例必然变红。</summary>
    static void CommentsExcluded(Action<string, bool, string?> assert)
    {
        const string Cmt = """
            /// <summary>文档里的 "中文串甲" 必须被排除</summary>
            // 行里的 "中文串乙" 也必须被排除 —— don't panic, it's fine
            /* 块里的 "中文串丙"
               续行 "中文串丁" */
            var s = "真串";   // 行尾注释里的 "中文串戊" 也别算
            """;
        var r = StringLiteralScanner.Scan(Cmt);

        // 夹具前提：这段注释里确实带撇号（否则下面那条"撇号不带偏"是在空转）
        assert("注释里的引号串不进结果（此处 4 段注释串 + 1 段行尾串：规则一坏条数就涨）",
            r.Literals.Count == 1, $"实得 {r.Literals.Count} 条：{string.Join(" | ", r.Literals.Select(l => l.Text))}");
        assert("唯一算数的那条就是代码里的 \"真串\"",
            r.Literals.Count == 1 && r.Literals[0].Text == "\"真串\"",
            string.Join(" | ", r.Literals.Select(l => l.Text)));
        assert("注释里的撇号（夹具确实写了 don't / it's）不许被当字符字面量把扫描带偏",
            Cmt.Contains("don't") && r.UnterminatedAt == -1 && r.Literals.Count == 1,
            r.UnterminatedNote);
    }

    /// <summary>字符字面量一律不计为串，且它们内部的引号/撇号不许改变扫描状态。</summary>
    static void CharLiteralsNotCounted(Action<string, bool, string?> assert)
    {
        const string Chars = """
            char a = '"';
            char b = '\'';
            char c = '{';
            char d = '\\';
            string s = "只此一条";
            """;
        var r = StringLiteralScanner.Scan(Chars);

        assert("四种字符字面量（引号 / 撇号 / 花括号 / 反斜杠）一条都不计成串",
            r.Literals.Count == 1 && r.Literals[0].Text == "\"只此一条\"",
            $"实得 {r.Literals.Count} 条：{string.Join(" | ", r.Literals.Select(l => l.Text))}");
        assert("字符字面量也不谎报未闭合", r.UnterminatedAt == -1, r.UnterminatedNote);
    }

    /// <summary>插值串里的 <c>{{</c> / <c>}}</c> 是转义的花括号，不能被当成洞的开/关。</summary>
    static void EscapedBraces(Action<string, bool, string?> assert)
    {
        const string Braces = """
            var x = $"{{ 字面左括号 {v} 完 }}";
            """;
        var r = StringLiteralScanner.Scan(Braces);

        assert("{{ 与 }} 是转义花括号：整条只算一次、洞只挖走真正那对",
            r.Literals.Count == 1 && r.Literals[0].Text == "$\"{{ 字面左括号 {…} 完 }}\"",
            $"实得 {r.Literals.Count} 条：{string.Join(" | ", r.Literals.Select(l => l.Text))}");
    }
}
