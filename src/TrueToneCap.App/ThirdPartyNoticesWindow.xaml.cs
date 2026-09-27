// TrueToneCap.App/ThirdPartyNoticesWindow.xaml.cs
// 第三方许可致谢面：从磁盘读 THIRD-PARTY-NOTICES.md，按行渲染成一份可滚动的只读文本。
//
// 为什么必须有这个窗口：BSD / Apache-2.0 一类许可证要求"再分发时必须向最终用户展示
// 版权声明与许可全文"，只把话写在下载页或 README 里不满足该条件。本应用随包再分发
// libavif / libaom / SVT-AV1 / libpng / zlib / JPEG XL / WebP / .NET 运行时 /
// WindowsAppSDK / DirectML / Win2D 等，所以应用内要有一个看得见的入口。
//
// 刻意不做的（按需求划界，不是遗漏）：
//   · 不做完整 Markdown 引擎 —— 只认 ATX 标题、代码围栏、表格行；
//   · 不做"打开目录""复制文本"等额外功能 —— 只读展示；
//   · 不硬编码任何颜色 —— 见 XAML 顶部说明，只用主窗口已在用的那几个 ThemeResource 键。
//
// 建窗 / DPI / 主题 / 字体注入沿用 OcrPreviewWindow 的现成范式（同属独立窗口族）。

using System.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using Microsoft.UI.Text;   // FontWeights（WinUI 的字体权重类型在这个命名空间）
using TrueToneCap.App.Services;
using TrueToneCap.Core.Logging;   // LogLevel / LogCategory（定义已移到 Core）

namespace TrueToneCap.App;

public sealed partial class ThirdPartyNoticesWindow : Window
{
    /// <summary>致谢文件名。也是 csproj 里 Content 项的文件名 —— 单一事实源，两边不得各写一份。</summary>
    public const string NoticeFileName = "THIRD-PARTY-NOTICES.md";

    /// <summary>等宽字体：表格行/代码块靠它保持列对齐（与日志面板同一选择）。</summary>
    private const string MonoFont = "Consolas";

    /// <summary>
    /// 从 AppContext.BaseDirectory 逐级向上的最大层数（深度 0 即 BaseDirectory 本身）。
    /// 这个数不是拍的：本项目的输出形状实测为
    ///   src/TrueToneCap.App/bin/x64/&lt;Cfg&gt;/net11.0-windows10.0.26100.0/win-x64/
    /// （Platform + 配置 + TFM + RID 四段都在路径里），仓库根在它的【第 7 级父目录】
    /// —— 所以"只往上一级"根本够不到，见 CandidatePaths 的说明。留 1 级余量。
    /// </summary>
    private const int MaxSearchDepth = 8;

    // ── 单实例登记（本仓对窗口生命周期泄漏敏感，理由见 ShowOrActivate / OnClosed）──
    private static ThirdPartyNoticesWindow? s_active;

    private readonly List<TextBlock> _monoBlocks = new();

    // ═══════════════════════════════════════
    //  文案（zh / en 双语）
    // ═══════════════════════════════════════
    // 入口卡片的文案也放在这里：主窗口 ApplyLocale() 与本子窗口读同一组属性，
    // 切语言时不会出现"卡片说英文、窗口标题还是中文"的分叉。
    // （LocaleManager 不在本次可改范围内，故文案随本窗口一起维护。）

    private static bool En => LocaleManager.CurrentLanguage == AppLanguage.English;

    public static string CardTitleText => En ? "Third-party notices" : "第三方许可";
    public static string CardDescText => En
        ? "License texts of the third-party components redistributed with this app."
        : "随本应用一并再分发的第三方组件及其许可证全文。";
    public static string CardButtonText => En ? "View third-party notices" : "查看第三方许可";
    private static string WindowTitleText => En ? "TrueToneCap Third-party notices" : "TrueToneCap 第三方许可";

    // ═══════════════════════════════════════
    //  开窗
    // ═══════════════════════════════════════

    /// <summary>打开致谢窗口；若已开着则只是把它切到前台。</summary>
    public static void ShowOrActivate()
    {
        var existing = s_active;
        if (existing is not null)
        {
            try { existing.Activate(); return; }
            catch { s_active = null; }  // 登记已失效（窗口其实已经没了）：丢掉重建，不留僵尸引用
        }
        // 构造函数末尾自行登记 s_active，调用方不持有引用 ⇒ 关闭后主窗口侧没有任何残留状态
        new ThirdPartyNoticesWindow().Activate();
    }

    private ThirdPartyNoticesWindow()
    {
        InitializeComponent();
        MainWindow.HookLocaleSweep(this.Content as Microsoft.UI.Xaml.FrameworkElement);

        HeaderTitleTxt.Text = En ? "Third-party software licenses" : "第三方软件许可";
        HeaderHintTxt.Text = En
            ? "Full texts of the third-party components redistributed with this app (read only)."
            : "随本应用一并再分发的第三方组件及其许可证全文（只读展示）";
        CloseBtnTxt.Text = En ? "Close" : "关闭";

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

        // ── 窗口尺寸：按所在显示器工作区取比例，物理像素（DPI 交给系统按窗口显示器处理）──
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            var area = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Nearest);
            // 标题栏文字（XAML 里的 Title 只是兜底）：走 AppWindow.Title，语言切换后与窗口内容口径一致
            appWindow.Title = WindowTitleText;
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

        s_active = this;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        // ApplyFontToVisualTree 会把它走过的每个 TextBlock 都刷成界面字体，
        // 表格/代码块的等宽必须在其之后补回来 —— 否则列对齐直接散架（这是本函数的全部意义）。
        FontHelper.ApplyFontToVisualTree(RootGrid,
            FontLoader.GetEffectiveFontFamily(AppServices.Settings.Current.FontFamily));
        foreach (var tb in _monoBlocks) tb.FontFamily = new FontFamily(MonoFont);
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
    /// 两者要同时成立，只能给面板一个等于视口宽度的 MinWidth：正文照此折行，超宽表格照样撑出横向滚动条。
    /// </summary>
    private void SyncContentWidth()
    {
        double viewport = BodyScroll.ViewportWidth;
        if (viewport <= 0) return;
        double want = Math.Max(120, viewport - 32);   // BodyScroll.Padding 左右各 16
        // 收敛判定：MinWidth 改变 → 滚动条出现/消失 → ViewportWidth 变 → 又回到这里。
        // 不设阈值会在临界宽度上来回触发，视口每抖一次就多一轮布局。
        if (Math.Abs(ContentPanel.MinWidth - want) < 1) return;
        ContentPanel.MinWidth = want;
    }

    // ═══════════════════════════════════════
    //  磁盘查找
    // ═══════════════════════════════════════

    /// <summary>
    /// 依次查找的候选完整路径。顺序即需求指定的顺序：
    ///   1) AppContext.BaseDirectory —— 发布包与"直接跑 exe"，致谢文件与 exe 同目录
    ///      （csproj 已把它作为 Content 复制进输出目录）；
    ///   2) 再往上找 —— 开发态仓库根才有这个文件。
    /// ⚠ 这里刻意不是"只往上一级"：App 的输出把 Platform / 配置 / TFM / RID 四段都编进了路径
    ///   （见 MaxSearchDepth 上方实测），仓库根在第 7 级父目录上，
    ///   只走一级会停在 bin/x64/ 这一层，永远够不到仓库根 ⇒ 开发态必然显示"未找到"。
    ///   故按有限深度逐级上溯（超过 MaxSearchDepth 停，到磁盘根也停），是"再往上一级"的超集。
    /// 深度封顶是必要的：不封顶时，一旦 BaseDirectory 在某条网络盘/挂载目录上，
    /// 上溯会一路摸到磁盘根并在那里撞权限异常。
    /// </summary>
    private static List<string> CandidatePaths()
    {
        var list = new List<string>();
        var dir = SafeDirectory(AppContext.BaseDirectory);
        for (int depth = 0; dir is not null && depth <= MaxSearchDepth; depth++)
        {
            try { list.Add(Path.Combine(dir.FullName, NoticeFileName)); }
            catch { break; }
            dir = SafeParent(dir);
        }
        return list;
    }

    /// <summary>命中的致谢文件路径；两级（含逐级向上）都没有则 null ⇒ 走降级界面。</summary>
    private static string? ResolveNoticeFile()
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
        string? path = ResolveNoticeFile();
        if (path is null)
        {
            LogService.Warn("Licenses", $"未找到 {NoticeFileName}，界面走降级路径");
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
            LogService.Error("Licenses", $"读取 {path} 失败", ex);
            ShowMissing(ex.Message);
            return;
        }

        try
        {
            RenderNoticeText(lines);
        }
        catch (Exception ex)
        {
            LogService.Error("Licenses", $"渲染 {path} 失败", ex);
            ContentPanel.Children.Clear();
            _monoBlocks.Clear();
            ShowMissing(ex.Message);
            return;
        }

        FooterTxt.Text = (En ? "Source: " : "来源: ") + path
            + (En ? $"  ·  {lines.Length} lines" : $"  ·  {lines.Length} 行");
        LogService.Info("Licenses", $"第三方许可致谢面已加载 {path}（{lines.Length} 行）");
    }

    /// <summary>降级界面：明说"没找到"，并列出查找过的路径 —— 不抛未捕获异常，也不静默留白。</summary>
    private void ShowMissing(string? reason)
    {
        BodyScroll.Visibility = Visibility.Collapsed;
        MissingPanel.Visibility = Visibility.Visible;

        MissingTitleTxt.Text = En ? $"{NoticeFileName} was not found" : $"未找到 {NoticeFileName}";
        MissingHintTxt.Text = En
            ? "This build ships without its third-party license notice file, so the license texts cannot be displayed here. Please report this to the developer."
            : $"本构建没有随带 {NoticeFileName}，因此这里无法展示许可全文。请把该问题反馈给开发者。";

        var sb = new StringBuilder();
        foreach (var candidate in CandidatePaths()) sb.AppendLine(candidate);
        if (!string.IsNullOrEmpty(reason))
            sb.AppendLine((En ? "read error: " : "读取失败: ") + reason);
        MissingPathsTxt.Text = (En ? "Searched, in order:" : "按顺序查找过:") + Environment.NewLine +
                               sb.ToString().TrimEnd();

        FooterTxt.Text = En
            ? "Nothing was loaded — the message above is a fallback, not an empty notice list."
            : "未加载任何内容 —— 上面是降级提示，不是「许可为空」。";
    }

    /// <summary>
    /// 按行渲染。行内不做的东西（强调、链接、Setext 标题、列表嵌套）保留原文字面量 ——
    /// 许可文本里这些噪音无害，而"看着像 Markdown 源码"比"被猜错语法改掉字"更安全。
    /// 唯一改动的字面量是 ** 强调标记（会盖住正文），去掉它不改变任何一条声明。
    /// </summary>
    private void RenderNoticeText(string[] lines)
    {
        var prose = new StringBuilder();
        var mono = new StringBuilder();
        bool inFence = false;

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

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ═══════════════════════════════════════
    //  关闭：断开一切引用
    // ═══════════════════════════════════════

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 本仓对窗口生命周期泄漏敏感，这里逐条清：
        //   1) 静态登记：不摘掉就会让已关闭的窗口连同整棵可视化树常驻，而且下次点击再也开不了窗口；
        //   2) 自订阅事件：与 RootGrid 同生命周期、理论上会一起回收，但显式摘掉是这里的规矩；
        //      （关键点：本窗口【不订阅任何静态/全局事件】—— 静态事件才是真正会跨窗口留下来的东西）
        //   3) 内容块：一次渲染可能几百个 TextBlock 加整份许可正文，主动断开。
        if (ReferenceEquals(s_active, this)) s_active = null;
        RootGrid.Loaded -= OnRootLoaded;
        RootGrid.KeyDown -= OnRootKeyDown;
        BodyScroll.SizeChanged -= OnBodySizeChanged;
        _monoBlocks.Clear();
        ContentPanel.Children.Clear();
    }
}
