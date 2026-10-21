// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/MarkdownDocumentWindow.cs
// 只读 Markdown 文档窗口的共用基类 —— 被两个合规窗口复用：
//   · ThirdPartyNoticesWindow  → 展示 THIRD-PARTY-NOTICES.md（BSD/Apache 要求再分发时展示许可全文）
//   · PrivacyPolicyWindow      → 展示 PRIVACY.md（微软商店政策 10.5.1 要求 Win32 产品提供隐私政策）
//
// ═══ 为什么要抽基类，而不是复制一份 ═══
// 2026-10-07 加隐私政策窗口时，ThirdPartyNoticesWindow 已经是一份 467 行、自带
// Markdown 按行渲染器 + 路径上溯 + 降级界面 + 生命周期清理的完整实现。
// 直接复制会立刻产生第二份"同一种渲染、两处维护"的代码 —— 而本仓已经吃过这类亏
// （见 Directory.Build.props 多处注释：同一事实两个出口，迟早分叉）。
// 两者要显示的东西本质相同：**磁盘上一份 Markdown，只读渲染，可滚到底**。
// ⇒ 抽成基类：子类只声明"读哪个文件、文案怎么写"，渲染与生命周期只有一份实现。
//
// ═══ 控件如何被基类取到（这里有个 WinUI 的硬约束，别改成别的写法）═══
// XAML 里的 x:Name 控件由 XAML 编译器生成到**子类**的 partial 上（见 obj/**.g.i.cs），
// 基类看不到它们。反过来，如果基类声明抽象属性、子类 XAML 又生成同名字段，
// 编译器会报 CS0108（成员隐藏）。
// 实测可行的做法：**基类声明抽象属性，子类 XAML 生成的字段天然满足实现**
// （generated 字段是 private 的，故子类里还要显式写一行 `protected override X Y => _y;`）。
// ⚠️ 没有走"反射按名查找控件"那条路 —— 那会把编译期错误推迟到运行时，
//    而本仓的取向是"宁可编译不过，也不要界面上少一块还不报错"。

using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;   // FontWeights
using Windows.Graphics;
using WinRT.Interop;
using TrueToneCap.App.Services;
using TrueToneCap.Core.Logging;

namespace TrueToneCap.App;

/// <summary>
/// 只读 Markdown 文档窗口基类。子类提供 XAML 与文案，并逐个转发必需的控件。
/// </summary>
public abstract partial class MarkdownDocumentWindow : Window
{
    // ── 子类转发：XAML 生成的控件（见文件头说明）──
    protected abstract Grid RootGrid { get; }
    protected abstract ScrollViewer BodyScroll { get; }
    protected abstract StackPanel ContentPanel { get; }
    protected abstract Border MissingPanel { get; }
    protected abstract TextBlock MissingTitleTxt { get; }
    protected abstract TextBlock MissingHintTxt { get; }
    protected abstract TextBlock MissingPathsTxt { get; }
    protected abstract TextBlock FooterTxt { get; }

    /// <summary>等宽字体：表格行/代码块靠它保持列对齐（与日志面板同一选择）。</summary>
    private const string MonoFont = "Consolas";

    /// <summary>
    /// 从 AppContext.BaseDirectory 逐级向上的最大层数（深度 0 即 BaseDirectory 本身）。
    /// 这个数不是拍的：本项目的输出形状实测为
    ///   src/TrueToneCap.App/bin/x64/&lt;Cfg&gt;/net11.0-windows10.0.26100.0/win-x64/
    /// （Platform + 配置 + TFM + RID 四段都在路径里），仓库根在它的【第 7 级父目录】
    /// —— 所以"只往上一级"根本够不到。留 1 级余量。
    /// </summary>
    private const int MaxSearchDepth = 8;

    private readonly List<TextBlock> _monoBlocks = new();

    // HTML 注释跨行状态（<!-- ... --> 用于文档顶部的维护说明，不该显示给用户）
    private bool _inHtmlComment;

    /// <summary>要加载的文件名（仅文件名，目录由路径上溯决定）。</summary>
    protected abstract string DocumentFileName { get; }

    /// <summary>日志分类前缀，例如 "Licenses" / "Privacy"。</summary>
    protected abstract string LogTag { get; }

    /// <summary>该窗口是否处于英文界面（子类接 LocaleManager）。</summary>
    protected abstract bool IsEnglish { get; }

    /// <summary>子类在此把标题/提示/按钮等处文案按当前语言填好。</summary>
    protected abstract void ApplyLocalizedChrome();

    /// <summary>子类 XAML InitializeComponent() 之后调用，完成文案、主题、接线与首次渲染。</summary>
    protected void InitializeDocumentWindow()
    {
        MainWindow.HookLocaleSweep(Content as FrameworkElement);

        // ── 主题：与 App.OnLaunched / MainWindow.SyncWindowTheme 完全同一套映射 ──
        // Application.RequestedTheme 未必被窗口内容元素继承，OLED 尤其要显式落到 Content 上，
        // 否则这里会是"控件深色、正文按浅色取色"的老缺陷形态。
        var effective = App.ResolveEffectiveTheme(App.CurrentTheme);
        if (Content is FrameworkElement fe)
        {
            fe.RequestedTheme = effective switch
            {
                AppThemeMode.Light => ElementTheme.Light,
                AppThemeMode.Dark or AppThemeMode.OLED => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        ApplyLocalizedChrome();

        // ── 窗口尺寸：按所在显示器工作区取比例（物理像素；DPI 交给系统按窗口显示器处理）──
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            // 标题栏文字（XAML 里的 Title 只是兜底）：走 AppWindow.Title，语言切换后与窗口内容口径一致
            appWindow.Title = Title;
            int w = Math.Min(Math.Clamp((int)(area.WorkArea.Width * 0.62), 640, 1240), area.WorkArea.Width - 40);
            int h = Math.Min(Math.Clamp((int)(area.WorkArea.Height * 0.78), 480, 1040), area.WorkArea.Height - 60);
            appWindow.MoveAndResize(new RectInt32(
                area.WorkArea.X + (area.WorkArea.Width - w) / 2,
                area.WorkArea.Y + (area.WorkArea.Height - h) / 2, w, h));
        }
        catch { /* 量不到工作区就用默认尺寸，不影响阅读 */ }

        // ── 字体注入（与其余独立窗口一致：用用户选的界面字体；等宽块在 OnRootLoaded 里补回）──
        if (RootGrid.IsLoaded) OnRootLoaded(RootGrid, null!);
        else RootGrid.Loaded += OnRootLoaded;

        // ── 键盘：Esc 关闭；横向滚动随视口同步内容宽度 ──
        // 用 RootGrid.KeyDown 而不是 PreviewKeyDown：WinUI 3 里 Preview* 键盘事件挂在 Control 上，
        // Grid（Panel）没有这个成员；而冒泡的 KeyDown 正是 OcrPreviewWindow 用的同一套做法。
        RootGrid.KeyDown += OnRootKeyDown;
        BodyScroll.SizeChanged += OnBodySizeChanged;
        SyncContentWidth();

        LoadAndRender();
    }

    // ═══════════════════════════════════════
    //  路径解析
    // ═══════════════════════════════════════

    /// <summary>
    /// 候选路径：从 BaseDirectory 起逐级向上找同名文件。
    /// <para>为什么上溯而不是固定几级：见 <see cref="MaxSearchDepth"/> 上方实测。
    /// 深度封顶也是必要的 —— 不封顶时若 BaseDirectory 落在网络盘/挂载目录，
    /// 上溯会一路摸到磁盘根并在那里撞权限异常。</para>
    /// </summary>
    private List<string> CandidatePaths()
    {
        var list = new List<string>();
        var dir = SafeDirectory(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth <= MaxSearchDepth; depth++)
        {
            try { list.Add(Path.Combine(dir.FullName, DocumentFileName)); }
            catch { break; }
            dir = SafeParent(dir);
        }
        return list;
    }

    private string? ResolveDocumentFile()
    {
        foreach (var candidate in CandidatePaths())
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* 无权限 / 非法路径：当未命中，继续向上 */ }
        }
        return null;
    }

    private static DirectoryInfo? SafeDirectory(string? path)
    {
        try { return string.IsNullOrEmpty(path) ? null : new DirectoryInfo(path); }
        catch { return null; }
    }

    private static DirectoryInfo? SafeParent(DirectoryInfo dir)
    {
        try { return dir.Parent; }   // 到磁盘根时为 null
        catch { return null; }       // UNC / 无权限时也会抛
    }

    // ═══════════════════════════════════════
    //  读取 + 渲染
    // ═══════════════════════════════════════

    private void LoadAndRender()
    {
        string? path = ResolveDocumentFile();
        if (path is null)
        {
            LogService.Warn(LogTag, $"未找到 {DocumentFileName}，界面走降级路径");
            ShowMissing(null);
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);   // 纯文本，整份读入即可（几十 KB ~ 几百 KB 量级）
        }
        catch (Exception ex)
        {
            LogService.Error(LogTag, $"读取 {path} 失败", ex);
            ShowMissing(ex.Message);
            return;
        }

        try
        {
            RenderDocument(lines);
        }
        catch (Exception ex)
        {
            LogService.Error(LogTag, $"渲染 {path} 失败", ex);
            ContentPanel.Children.Clear();
            _monoBlocks.Clear();
            ShowMissing(ex.Message);
            return;
        }

        FooterTxt.Text = (IsEnglish ? "Source: " : "来源: ") + path
            + (IsEnglish ? $"  ·  {lines.Length} lines" : $"  ·  {lines.Length} 行");
        LogService.Info(LogTag, $"{DocumentFileName} 已加载 {path}（{lines.Length} 行）");
    }

    /// <summary>降级界面：明说"没找到"，并列出查找过的路径 —— 不抛未捕获异常，也不静默留白。</summary>
    private void ShowMissing(string? reason)
    {
        BodyScroll.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;

        MissingTitleTxt.Text = IsEnglish
            ? $"{DocumentFileName} was not found"
            : $"未找到 {DocumentFileName}";
        MissingHintTxt.Text = IsEnglish
            ? $"This build ships without {DocumentFileName}, so its contents cannot be displayed here. Please report this to the developer."
            : $"本构建没有随带 {DocumentFileName}，因此这里无法展示其内容。请把该问题反馈给开发者。";

        var sb = new StringBuilder();
        foreach (var candidate in CandidatePaths()) sb.AppendLine(candidate);
        if (!string.IsNullOrEmpty(reason))
            sb.AppendLine((IsEnglish ? "read error: " : "读取失败: ") + reason);
        MissingPathsTxt.Text = (IsEnglish ? "Searched, in order:" : "按顺序查找过:") + Environment.NewLine +
                               sb.ToString().TrimEnd();

        FooterTxt.Text = IsEnglish
            ? "Nothing was loaded — the message above is a fallback, not empty content."
            : "未加载任何内容 —— 上面是降级提示，不是「内容为空」。";
    }

    /// <summary>
    /// 按行渲染。行内不做的东西（强调、链接、Setext 标题、列表嵌套）保留原文字面量 ——
    /// 文档里这些噪音无害，而"看着像 Markdown 源码"比"被猜错语法改掉字"更安全。
    /// 唯一改动的字面量是 ** 强调标记（会盖住正文），去掉它不改变任何一条声明。
    /// </summary>
    private void RenderDocument(string[] lines)
    {
        var prose = new StringBuilder();
        var mono = new StringBuilder();
        bool inFence = false;
        _inHtmlComment = false;

        void FlushProse()
        {
            if (prose.Length == 0) return;
            AddProse(CleanInline(prose.ToString().TrimEnd('\r', '\n')));
            prose.Clear();
        }
        void FlushMono()
        {
            if (mono.Length == 0) return;
            AddMono(mono.ToString().TrimEnd('\r', '\n'));
            mono.Clear();
        }
        void FlushAll() { FlushProse(); FlushMono(); }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd('\r', '\n');
            string trimmed = line.TrimStart();

            // 代码围栏：``` 自身是语法噪音，显示它没有意义，只用来切换等宽态
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
            {
                FlushAll();
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                FlushProse();
                mono.AppendLine(line);
                continue;
            }

            // HTML 注释：PRIVACY.md 顶部有维护说明（改功能时必读那一段），不该显示给用户。
            // ⚠️ 只处理"整行注释"这一种形态 —— 它是本仓文档实际使用的写法；
            //    做完整 HTML 解析得不偿失（与"不做完整 Markdown 引擎"同一取舍）。
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            {
                FlushAll();
                if (!trimmed.Contains("-->", StringComparison.Ordinal)) _inHtmlComment = true;
                continue;
            }
            if (_inHtmlComment)
            {
                if (trimmed.Contains("-->", StringComparison.Ordinal)) _inHtmlComment = false;
                continue;
            }

            int heading = HeadingLevelOf(line);
            if (heading > 0)
            {
                FlushAll();
                AddHeading(heading, CleanInline(line.TrimStart('#').Trim().TrimEnd('#').Trim()));
                continue;
            }

            // 分隔线（--- / *** / ___）刻意不渲染：这里不允许出现硬编码 Brush，
            // 而主题里没有可直接取用的分隔线画刷 ⇒ 用标题的段前距承担分节。
            if (IsThematicBreak(trimmed))
            {
                FlushAll();
                continue;
            }

            // 表格行：等宽、不折行（配合 MinWidth 撑出的横向滚动条保持列对齐）
            if (IsTableRow(trimmed))
            {
                FlushProse();
                mono.AppendLine(line);
                continue;
            }

            FlushMono();
            prose.AppendLine(line);
        }

        FlushAll();
    }

    private static string CleanInline(string text) => text.Replace("**", "");

    /// <summary>ATX 标题层级；0 表示不是标题（Setext 式下划线标题本文件不出现，故不做）。</summary>
    private static int HeadingLevelOf(string line)
    {
        int i = 0;
        while (i < line.Length && line[i] == '#') i++;
        if (i == 0 || i > 6) return 0;
        if (i >= line.Length) return 0;                                  // 只有井号、没有文字
        if (line[i] != ' ' && line[i] != '\t') return 0;                 // #hashtag 之类不算标题
        return i;
    }

    private static bool IsThematicBreak(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char c = trimmed[0];
        if (c is not ('-' or '*' or '_')) return false;
        foreach (char ch in trimmed)
            if (ch != c && ch != ' ') return false;
        return true;
    }

    /// <summary>
    /// 表格行的判定：Markdown 表格每行都以 | 起、以 | 止，分隔行也可能写成 ---|---。
    /// 只在含 | 且首尾之一就是 | 时才当表格 —— 正文里偶然出现一个 | 不会被误判成等宽块。
    /// </summary>
    private static bool IsTableRow(string trimmed)
        => trimmed.Length > 1 && trimmed.Contains('|') && (trimmed[0] == '|' || trimmed[^1] == '|');

    private void AddProse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,   // 允许手动选中查看，但不提供"复制全部"这类额外功能
        });
    }

    private void AddMono(string text)
    {
        if (text.Length == 0) return;
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontFamily = new FontFamily(MonoFont),
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        _monoBlocks.Add(tb);   // 界面字体注入之后要把等宽补回来，见 OnRootLoaded
        ContentPanel.Children.Add(tb);
    }

    private void AddHeading(int level, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ContentPanel.Children.Add(new TextBlock
        {
            Text = text,
            // 层级只用字号/字重/段前距区分，不改颜色 —— 颜色在深色与 OLED 下必须跟着主题走
            FontSize = level switch { 1 => 20, 2 => 16, 3 => 14, _ => 13 },
            FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, level switch { 1 => 18, 2 => 14, _ => 10 }, 0, 2),
        });
    }

    // ═══════════════════════════════════════
    //  根元素事件
    // ═══════════════════════════════════════

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // ApplyFontToVisualTree 会把它走过的每个 TextBlock 都刷成界面字体，
        // 表格/代码块的等宽必须在其之后补回来 —— 否则列对齐直接散架（这是本函数的全部意义）。
        FontHelper.ApplyFontToVisualTree(RootGrid,
            FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily));
        foreach (var tb in _monoBlocks) tb.FontFamily = new FontFamily(MonoFont);
        SyncContentWidth();
        BodyScroll.Focus(FocusState.Programmatic);   // 打开即可用键盘/滚轮
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnBodySizeChanged(object sender, SizeChangedEventArgs e) => SyncContentWidth();

    /// <summary>
    /// 把内容面板钉在视口宽度上。
    /// ScrollViewer 允许横向滚动后是用「无限宽」去量内容的，正文的 TextWrapping 会因此完全失效
    /// （长段永不折行，看上去像换行没做）；而等宽表格行又确实需要横向滚动才能保持列对齐。
    /// 两者要同时成立，只能给面板一个等于视口宽度的 MinWidth。
    /// </summary>
    private void SyncContentWidth()
    {
        double viewport = BodyScroll.ViewportWidth;
        if (viewport <= 0) return;
        ContentPanel.MinWidth = viewport;
    }

    /// <summary>子类在 OnClosed 里调用，断开本窗口持有的一切引用。</summary>
    protected void TeardownDocumentWindow()
    {
        RootGrid.Loaded -= OnRootLoaded;
        RootGrid.KeyDown -= OnRootKeyDown;
        BodyScroll.SizeChanged -= OnBodySizeChanged;
        _monoBlocks.Clear();
        ContentPanel.Children.Clear();
    }
}
