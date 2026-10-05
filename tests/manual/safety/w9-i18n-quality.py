"""英文界面补完 · 第二批：质量描述行 + 质量区间标签（接 ui-en2.png 的读数）。

取证：Format 提示已翻对，但 `无损 (固定 100%)` 仍是中文。根因是层位问题——Core 的编码器把
【用户可见文案】做成了中文成品串，而本地化表（LocaleManager.ZhToEn）只存在于 App 这一层。

本脚本做四件事：
  1) 10 条 zh→en 进 ZhToEn（6 条 GetQualityRange().Label + 2 条常量描述 + 2 条内插串片段）；
  2) App 侧建 CoreQualityTexts（"App 会翻哪些 Core 产出的串"）+ QualityText() 一处出口；
  3) 【两个】使用点都过 QualityText：滑块区间标签 :540 与描述行 SetQualityDescription
     ——上一版脚本只包了后者，前者会让英文模式整行漏翻；
  4) 新判据 Test_QualityTexts_CoreStringsAreAllCovered：枚举全部格式实跑 Core，
     "把清单扣掉后还剩中文"就红。

关于内插串 `$"距离: {q:F1}"`：App 拿到的是带数值的成品串，整串精确匹配永不命中 ⇒ 只能按
【已知片段】换。这不叫糊，因为它被第 4 步那条判据钉住：Core 一改措辞，片段替换落空、中文
残留、判据立刻红并点名，不会静默留半中半英。根治办法仍是让 Core 别产出文案（返回键+数值、
格式化上移到 App），那是签名级改动，作为架构发现上报，不在这里偷偷做。

中文模式下 QualityText 原样返回输入 ⇒ 界面上的字与改动前逐字相同。

写盘口径：C# 侧的插入文本一律用 raw 字符串，避免 Python 与 C# 两层转义互相咬。
"""
import io

LM = 'src/TrueToneCap.App/Services/LocaleManager.cs'
MW = 'src/TrueToneCap.App/MainWindow.xaml.cs'
CT = 'src/TrueToneCap.Test/CorePipelineTests.cs'

# 按长度降序（判据里会把这条次序钉住）：App 的片段替换循环不按长度排序，短的先换会拆坏长的。
PAIRS = [
    ("butteraugli 距离 (越小越清晰; 1.0 = 视觉无损)",
     "butteraugli distance (lower = sharper; 1.0 = visually lossless)"),
    ("CRF (0=无损, 63=最低; 15 ≈ 视觉无损)",
     "CRF (0=lossless, 63=lowest; 15 ≈ visually lossless)"),
    ("butteraugli 距离 (0.5-3.0)", "butteraugli distance (0.5-3.0)"),
    ("质量 (50-100; 85+ 视觉无损)", "Quality (50-100; 85+ is visually lossless)"),
    ("无损 (固定 100%)", "Lossless (fixed 100%)"),
    (" (越小越清晰)", " (lower = sharper)"),
    ("无损 (固定)", "Lossless (fixed)"),
    ("距离: ", "Distance: "),
    ("近无损", "Near-lossless"),
    ("无损", "Lossless"),
]

APP_BLOCK = r'''    /// <summary>
    /// Core 的编码器产出的、<b>App 会往回翻</b>的质量串清单，逐字抄自
    /// <c>FormatEncoders.cs</c> / <c>JpegGainMapEncoder.cs</c> 的
    /// <c>GetQualityRange().Label</c> 与 <c>GetQualityDescription</c>；译文在
    /// <see cref="LocaleManager.ZhToEn"/>。两边一一对由
    /// <c>Test_QualityTexts_CoreStringsAreAllCovered</c> 钉住：Core 新增或改写文案而没人补
    /// 这一行，那条判据就红并点名，不会静默在英文界面留中文。
    /// <para>末尾两条不是整串，而是 <c>距离: {q:F1}…</c> 里被拼进去的<b>片段</b>——整串带
    /// 数值，精确匹配永不命中，只能换片段。次序必须按长度降序（下面那条判据会量），
    /// 因为 <see cref="QualityText"/> 的片段循环不排序，「无损」先换会咬掉「无损 (固定)」。</para>
    /// </summary>
    private static readonly string[] CoreQualityTexts =
    {
        "butteraugli 距离 (越小越清晰; 1.0 = 视觉无损)",
        "CRF (0=无损, 63=最低; 15 ≈ 视觉无损)",
        "butteraugli 距离 (0.5-3.0)",
        "质量 (50-100; 85+ 视觉无损)",
        "无损 (固定 100%)",
        " (越小越清晰)",
        "无损 (固定)",
        "距离: ",
        "近无损",
        "无损",
    };

    /// <summary>把 Core 产出的质量文案翻到当前语言。<b>中文模式原样返回</b> ⇒ 界面上的字逐字不变。</summary>
    private static string QualityText(string fromCore)
    {
        if (string.IsNullOrEmpty(fromCore) || LocaleManager.CurrentLanguage != AppLanguage.English)
            return fromCore;
        var exact = LocaleManager.Tr(fromCore);
        if (!string.Equals(exact, fromCore, StringComparison.Ordinal)) return exact;
        // 整串匹配不上 ⇒ 是 Core 里就拼好的内插串，按已知片段换（清单按长度降序声明）。
        var s = fromCore;
        foreach (var frag in CoreQualityTexts)
            if (s.Contains(frag, StringComparison.Ordinal))
                s = s.Replace(frag, LocaleManager.Tr(frag), StringComparison.Ordinal);
        // 还有中文 = 出现了清单没覆盖的新文案。宁可退回整句中文，也不给半中半英；
        // 漏了哪一条由 Test_QualityTexts_CoreStringsAreAllCovered 报，不靠这里兜。
        return s.Any(c => c >= 0x4E00 && c <= 0x9FFF) ? fromCore : s;
    }

'''

TEST_BLOCK = r'''    /// <summary>
    /// Core 的编码器把用户可见文案做成了中文成品串（<c>GetQualityRange().Label</c> 与
    /// <c>GetQualityDescription</c>），而本地化表只存在于 App 这一层 ⇒ 只能在显示点往回翻，
    /// 能翻的集合就是 <c>MainWindow.xaml.cs</c> 里那份 <c>CoreQualityTexts</c>。
    /// 这条判据枚举<b>全部</b>格式 × {区间下界, 默认, 上界} 实跑 Core，问一句话：把清单里的
    /// 串从产出文案里<b>扣掉</b>后，还剩不剩中文？剩 = Core 新增或改写了文案而没人补翻译 ⇒
    /// 红并点名（英文界面那几处残留中文就是从这里来的）。
    /// <para>不在这里复刻 App 的替换算法：那是第二份真相。这里只做减法，方向与
    /// <c>QualityText</c> 一致而实现独立。清单 ↔ 对照表的对应关系也在这里查，
    /// 两边任一边走样都会红。</para>
    /// </summary>
    static void Test_QualityTexts_CoreStringsAreAllCovered()
    {
        var mwPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "MainWindow.xaml.cs"));
        var lmPath = FindRepoFile(Path.Combine("src", "TrueToneCap.App", "Services", "LocaleManager.cs"));
        if (mwPath is null || lmPath is null) { Skip("质量文案本地化覆盖", "未找到源文件"); return; }

        string Cut(string s) => s.Length > 30 ? s[..30] + "…" : s;
        bool HasCjk(string s) => s.Any(c => c >= 0x4E00 && c <= 0x9FFF);

        // ── 1) App 侧清单：切出 CoreQualityTexts 的初始化块（切不出来＝判据瞎了，不能记绿）
        var mw = StripLineComments(ReadSourceNormalized(mwPath));
        int at = mw.IndexOf("CoreQualityTexts", StringComparison.Ordinal);
        int end = at < 0 ? -1 : mw.IndexOf("};", at, StringComparison.Ordinal);
        var keys = (at < 0 || end < 0) ? new List<string>()
            : Regex.Matches(mw.Substring(at, end - at), @"""([^""\n]*)""")
                .Select(m => m.Groups[1].Value).ToList();
        Assert("从 MainWindow.xaml.cs 切出 App 会翻的 Core 串清单（位置与条数都要有）",
            at >= 0 && end > at && keys.Count >= 1,
            $"CoreQualityTexts 位置 {at} / 收尾 }}; 位置 {end}，解析 {keys.Count} 条");
        Assert("清单按长度降序声明：QualityText 的片段循环不排序，短的先换会拆坏长的",
            keys.SequenceEqual(keys.OrderByDescending(k => k.Length)),
            "实得次序：" + string.Join(" | ", keys.Select(Cut)));

        // ── 2) 清单 ↔ 对照表：有名无实的条目 = 英文模式那半句还是中文
        var lmText = File.ReadAllText(lmPath);
        int tabAt = lmText.IndexOf("ZhToEn =", StringComparison.Ordinal);
        int tabEnd = tabAt < 0 ? -1 : lmText.IndexOf("};", tabAt, StringComparison.Ordinal);
        var table = (tabAt < 0 || tabEnd < 0) ? new HashSet<string>(StringComparer.Ordinal)
            : Regex.Matches(lmText.Substring(tabAt, tabEnd - tabAt),
                    @"\[""([^""\n]*)""]\s*=\s*""([^""\n]*)""")
                .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert("对照表解析成功（解析不出条目 = 这一半判据没有牙）", table.Count > 100,
            $"只解析出 {table.Count} 条");
        var noTranslation = keys.Where(k => !table.Contains(k)).ToList();
        Assert("清单里每一条都在 ZhToEn 有译文", noTranslation.Count == 0,
            $"无译文 {noTranslation.Count} 条：" + string.Join(" | ", noTranslation.Select(Cut)));

        // ── 3) 正向：Core 实跑的每一条含中文质量串，扣掉清单后不许还剩中文
        var produced = new List<(string Where, string Text)>();
        foreach (var fmt in Enum.GetValues<OutputFormat>())
        {
            var enc = EncoderFactory.Create(fmt);
            var (min, max, def, label) = enc.GetQualityRange();
            produced.Add(($"{fmt}.Label", label));
            foreach (var (q, tag) in new (float, string)[] { (min, "min"), (def, "def"), (max, "max") })
                produced.Add(($"{fmt}.Desc.{tag}", enc.GetQualityDescription(q)));
        }
        var cjkProduced = produced.Where(p => HasCjk(p.Text)).ToList();
        Assert("量到了含中文的质量串（一条都没有 = 这一半判据是装饰）", cjkProduced.Count >= 6,
            $"Core 产出 {produced.Count} 条，含中文 {cjkProduced.Count} 条");
        string Uncovered(string s) => keys.OrderByDescending(k => k.Length)
            .Aggregate(s, (acc, k) => acc.Replace(k, "", StringComparison.Ordinal));
        var uncovered = cjkProduced.Where(p => HasCjk(Uncovered(p.Text))).ToList();
        Assert("Core 产出的中文质量串全被 App 清单覆盖（未覆盖 = 英文界面残留中文）",
            uncovered.Count == 0,
            $"未覆盖 {uncovered.Count} 条："
            + string.Join(" | ", uncovered.Select(p => $"{p.Where}={Cut(p.Text)}")));

        // ── 4) 夹具：Core 若新增一句没登记的中文文案，同一把尺子必须抓到
        var fixture = Uncovered("距离: 2.0 (未登记的新措辞)");
        Assert("变异自检·夹具：清单外的新中文要被同一把尺子抓到", HasCjk(fixture),
            $"夹具扣完清单后 = 「{Cut(fixture)}」 ⇒ 减法做多了，判据没牙");
    }

'''


def load(path):
    raw = io.open(path, encoding='utf-8', newline='').read()
    nl = '\r\n' if '\r\n' in raw else '\n'
    return raw.replace('\r\n', '\n'), nl


def store(path, text, nl):
    io.open(path, 'w', encoding='utf-8', newline='').write(
        text.replace('\n', '\r\n') if nl == '\r\n' else text)


def sub1(text, old, new, what):
    n = text.count(old)
    assert n == 1, f'{what}：锚点命中 {n} 次（要 1 次）'
    return text.replace(old, new, 1)


# ── 1) 进表 ──
lm, lm_nl = load(LM)
i = lm.index('ZhToEn =')
close = lm.index('\n        };', i)
assert '["保存设置"]' in lm[i:close], '表尾锚点前没看到已知条目 ⇒ 定位可能错'
add = [(z, e) for z, e in PAIRS if f'["{z}"]' not in lm[i:close]]
for z, _ in add:
    assert '"' not in z and '\\' not in z, f'键里有引号/反斜杠，判据的解析式处理不了：{z}'
rows = ['        // ── 2026-10-03 英文界面补完 · 第二批：Core 质量文案（键见 MainWindow.CoreQualityTexts）──']
rows += [f'            ["{z}"] = "{e}",' for z, e in add]
lm = lm[:close] + '\n' + '\n'.join(rows) + lm[close:]
store(LM, lm, lm_nl)
print(f'[1] 进表 {len(add)} 条（已在表里的 {len(PAIRS) - len(add)} 条跳过）')

# ── 2)+3) App：清单、出口函数、两个使用点 ──
mw, mw_nl = load(MW)
if 'CoreQualityTexts' not in mw:
    anchor = '    /// <summary>质量描述行的<b>唯一</b>写入点。</summary>'
    assert mw.count(anchor) == 1, f'SetQualityDescription 文档锚点命中 {mw.count(anchor)} 次'
    mw = mw.replace(anchor, APP_BLOCK + anchor, 1)
    print('[2] 已写入 CoreQualityTexts 清单 + QualityText 出口函数')
else:
    print('[2] CoreQualityTexts 已存在 ⇒ 跳过（幂等）')

mw = sub1(mw, '        QualityLabel.Text = label;',
          '        QualityLabel.Text = QualityText(label);', '区间标签使用点') \
    if 'QualityLabel.Text = QualityText(label);' not in mw else mw

old_fn = ('    private void SetQualityDescription(ImageEncoder encoder, double quality)\n'
          '        => QualityDescTxt.Text = encoder.QualityIsFixed\n'
          '            ? encoder.GetQualityRange().Label\n'
          '            : encoder.GetQualityDescription((float)quality);')
new_fn = ('    private void SetQualityDescription(ImageEncoder encoder, double quality)\n'
          '        => QualityDescTxt.Text = QualityText(encoder.QualityIsFixed\n'
          '            ? encoder.GetQualityRange().Label\n'
          '            : encoder.GetQualityDescription((float)quality));')
if new_fn in mw:
    print('[3] 描述行使用点已包 ⇒ 跳过（幂等）')
else:
    mw = sub1(mw, old_fn, new_fn, '质量描述使用点')
    print('[3] 两个使用点均已过 QualityText（区间标签 + 描述行）')
store(MW, mw, mw_nl)

# ── 4) 判据 ──
ct, ct_nl = load(CT)
call_anchor = '        Test_WgcCapture_DegradationIsVisibleAtDefaultVerbosity();'
if 'Test_QualityTexts_CoreStringsAreAllCovered();' not in ct:
    ct = sub1(ct, call_anchor, call_anchor + '\n        Test_QualityTexts_CoreStringsAreAllCovered();',
              '判据调用表')
m_anchor = '    static void Test_LogViewer_Toolbar_HasDistinctColumns()'
if 'static void Test_QualityTexts_CoreStringsAreAllCovered()' not in ct:
    ct = sub1(ct, m_anchor, TEST_BLOCK + m_anchor, '判据方法插入点')
store(CT, ct, ct_nl)
print('[4] 判据 Test_QualityTexts_CoreStringsAreAllCovered 已接进调用表')
print(f'RESOURCE pairs={len(PAIRS)} newly_in_table={len(add)}')
