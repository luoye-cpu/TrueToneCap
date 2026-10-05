"""把 3c 段（Core FormatColorFacts 的反向覆盖锁）插进门禁卡。

单独写脚本而不是手贴：这段含 C# 正则，而正则里的双引号在 Python/bash 两层转义下反复出错
（本次会话已栽三次）。统一用 \\u0022 表示正则里的引号 —— C# 逐字串里它是字面量反斜杠+u0022，
交给 .NET 正则解释成 `"`，于是 Python 侧、C# 侧都不需要再转义引号。
"""
import io

P = 'src/TrueToneCap.Test/CorePipelineTests.cs'

SEG = r'''
        // ── 3c) 第三个产出者：Core 的 FormatColorFacts.Declaration（色域映射卡那一句）──
        // 与第 1 步【反向】配对：第 1 步锁"Core 的质量接口不许吐中文"；这里承认
        // FormatColorFacts 就是要吐中文（它同时喂界面与日志，只能由调用方决定翻不翻），
        // 于是要求它吐出的每一条中文都被 App 清单点名、且表里有译文。
        // 谁在 Declaration 里新加一句中文而 App 没跟 ⇒ 英文界面整列漏翻，这里红并点名。
        var cfPath = FindRepoFile(Path.Combine("src", "TrueToneCap.Core", "Encoding", "FormatColorFacts.cs"));
        Assert("找到 Core 的 FormatColorFacts.cs（缺文件=这一半失明）", cfPath is not null);
        if (cfPath is not null)
        {
            const string StrLit = @"\u0022([^\u0022\n]*)\u0022";      // 一条 C# 字符串字面量
            var cfSrc = StripLineComments(ReadSourceNormalized(cfPath));
            var cfLits = Regex.Matches(cfSrc, StrLit).Select(m => m.Groups[1].Value)
                .Where(HasCjk).Distinct().ToList();
            Assert("量到了 Declaration 的中文模板（少于 20 条 = 这一半没有尺子）", cfLits.Count >= 20,
                $"实得 {cfLits.Count} 条");
            int cfAt = mw.IndexOf("CoreColorFactsTexts", StringComparison.Ordinal);
            int cfEnd = cfAt < 0 ? -1 : mw.IndexOf("};", cfAt, StringComparison.Ordinal);
            var cfKnown = (cfAt < 0 || cfEnd < 0) ? new List<string>()
                : Regex.Matches(mw.Substring(cfAt, cfEnd - cfAt), StrLit)
                    .Select(m => m.Groups[1].Value).ToList();
            Assert("App 侧点名了这份声明通道清单（切不出来=没有对照面）", cfKnown.Count >= 1,
                $"CoreColorFactsTexts 位置 {cfAt} / 收尾 }}; 位置 {cfEnd}，解析 {cfKnown.Count} 条");
            var cfUnlisted = cfLits.Where(s => !cfKnown.Contains(s, StringComparer.Ordinal)).ToList();
            Assert("Core 每一条声明通道文案都被 App 清单点名（未点名 = 英文界面漏翻）",
                cfUnlisted.Count == 0,
                $"未点名 {cfUnlisted.Count} 条：" + string.Join(" | ", cfUnlisted.Select(Cut)));
            var cfNoEn = cfKnown.Where(k => !table.Contains(k)).ToList();
            Assert("声明通道清单每一条都在 ZhToEn 有译文", cfNoEn.Count == 0,
                $"无译文 {cfNoEn.Count} 条：" + string.Join(" | ", cfNoEn.Select(Cut)));

            // 模板与译文的占位符数量必须一致：Core 那侧是 string.Format(T(tpl), args)，
            // 译文少一个 {1} 就漏代值、多一个 {2} 就当场抛 FormatException。
            var pairsEn = Regex.Matches(lmText.Substring(tabAt, tabEnd - tabAt),
                    @"\[(\u0022[^\u0022\n]*)\u0022\]\s*=\s*\u0022([^\u0022\n]*)\u0022")
                .Select(m => (Zh: m.Groups[1].Value, En: m.Groups[2].Value))
                .ToDictionary(x => x.Zh, x => x.En, StringComparer.Ordinal);
            var phBad = cfKnown
                .Where(k => pairsEn.TryGetValue(k, out var en)
                            && CountPlaceholders(k) != CountPlaceholders(en))
                .ToList();
            Assert("模板与译文的占位符数量一致（不一致会漏代值或直接抛 FormatException）",
                phBad.Count == 0,
                $"对不上 {phBad.Count} 条：" + string.Join(" | ", phBad.Select(Cut)));
            Assert("变异自检·夹具④：Core 新加一句未点名的中文声明要被抓到",
                !cfKnown.Contains("某新格式：没有通道可声明。", StringComparer.Ordinal)
                && new[] { "某新格式：没有通道可声明。" }
                    .Any(s => !cfKnown.Contains(s, StringComparer.Ordinal)),
                "夹具没被抓到 ⇒ 上面那条'未点名'是恒绿的装饰");
        }

'''

HELPER = r'''    /// <summary>数模板里 {0}/{1}… 这类占位符的个数（<c>{</c> 紧跟数字才算）。</summary>
    static int CountPlaceholders(string s) => Regex.Matches(s, @"\{\d+\}").Count;

'''


def main():
    t = io.open(P, encoding='utf-8', newline='').read()
    if '3c) 第三个产出者' in t:
        print('3c 段已存在 ⇒ 跳过（幂等）')
        return 0
    anchor = ('        Assert("变异自检·夹具③：后端清单外的新中文要被同一把尺子抓到",\n'
              '            HasCjk(UncoveredBackend("某个新后端（本机从没测过）")),\n'
              '            "夹具扣完清单后没有中文残留 ⇒ 减法做多了，这一半判据没牙");\n')
    assert t.count(anchor) == 1, ('夹具③锚点', t.count(anchor))
    t = t.replace(anchor, anchor + SEG)
    m = '    static void Test_LogViewer_Toolbar_HasDistinctColumns()'
    assert t.count(m) == 1, ('方法锚点', t.count(m))
    t = t.replace(m, HELPER + m)
    io.open(P, 'w', encoding='utf-8', newline='').write(t)
    print('3c 段与 CountPlaceholders 已接入')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
