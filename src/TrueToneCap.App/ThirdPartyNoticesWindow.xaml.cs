// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/ThirdPartyNoticesWindow.xaml.cs
// 第三方许可致谢面：从磁盘读 THIRD-PARTY-NOTICES.md，按行渲染成一份可滚动的只读文本。
//
// 为什么必须有这个窗口：BSD / Apache-2.0 一类许可证要求"再分发时必须向最终用户展示
// 版权声明与许可全文"，只把话写在下载页 / README 里不满足该条件。本应用随包再分发
// libavif / libaom / SVT-AV1 / libpng / zlib / JPEG XL / WebP / .NET 运行时 /
// WindowsAppSDK / DirectML / Win2D 等，所以应用内要有一个看得见的入口。
//
// ═══ 2026-10-07：渲染与生命周期抽到基类 ═══
// 加隐私政策窗口时（商店政策 10.5.1 要求 Win32 产品提供隐私政策），本类已是一份 467 行、
// 自带 Markdown 按行渲染 + 路径上溯 + 降级界面 + 窗口清理的完整实现。直接复制会产生
// 第二份"同一种渲染、两处维护"的代码 —— 本仓对这类分叉有明确教训（见 Directory.Build.props
// 多处注释：同一事实两个出口，迟早不一致）。
// ⇒ 渲染器/路径解析/降级/生命周期全部上移到 MarkdownDocumentWindow，本类只剩：
//   ① 声明读哪个文件；② 提供本窗口的中英文案；③ 单实例登记。
// 本文件【不再包含】任何渲染逻辑 —— 那不是被删掉了，是搬到了基类里。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.App.Services;

namespace TrueToneCap.App;

public sealed partial class ThirdPartyNoticesWindow : MarkdownDocumentWindow
{
    /// <summary>致谢文件名。也是 csproj 里 Content 项的文件名 —— 单一事实源，两边不得各写一份。</summary>
    public const string NoticeFileName = "THIRD-PARTY-NOTICES.md";

    // ── 单实例登记（本仓对窗口生命周期泄漏敏感，理由见 ShowOrActivate / OnClosed）──
    private static ThirdPartyNoticesWindow? s_active;

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

    // ── 基类要求的三项文案/身份 ──

    protected override string DocumentFileName => NoticeFileName;
    protected override string LogTag => "Licenses";
    protected override bool IsEnglish => En;

    // ── 基类要求转发 XAML 生成的控件 ──
    // ⚠️ 这些不是冗余：x:Name 生成的字段是 private、且属于本类的 partial，
    //    基类看不见 ⇒ 必须显式转发。漏掉任何一个都会**编译期报错**（而不是界面上少一块还不报错），
    //    这正是选这套写法而非"反射按名查找"的原因。
    protected override Grid RootGrid => RootGridControl;
    protected override ScrollViewer BodyScroll => BodyScrollControl;
    protected override StackPanel ContentPanel => ContentPanelControl;
    protected override Border MissingPanel => MissingPanelControl;
    protected override TextBlock MissingTitleTxt => MissingTitleTxtControl;
    protected override TextBlock MissingHintTxt => MissingHintTxtControl;
    protected override TextBlock MissingPathsTxt => MissingPathsTxtControl;
    protected override TextBlock FooterTxt => FooterTxtControl;

    protected override void ApplyLocalizedChrome()
    {
        Title = WindowTitleText;
        HeaderTitleTxt.Text = En ? "Third-party software licenses" : "第三方软件许可";
        HeaderHintTxt.Text = En
            ? "Full texts of the third-party components redistributed with this app (read only)."
            : "随本应用一并再分发的第三方组件及其许可证全文（只读展示）";
        CloseBtnTxt.Text = En ? "Close" : "关闭";
    }

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
        InitializeDocumentWindow();
        s_active = this;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ═══════════════════════════════════════
    //  关闭：断开一切引用
    // ═══════════════════════════════════════

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 本仓对窗口生命周期泄漏敏感，这里逐条清：
        //   1) 静态登记：不摘掉就会让已关闭的窗口连同整棵可视化树常驻，而且下次点击再也开不了窗口；
        //   2) 自订阅事件与内容块：交给基类统一断开（一次渲染可能几百个 TextBlock）。
        //      （关键点：本窗口【不订阅任何静态/全局事件】—— 静态事件才是真正会跨窗口留下来的东西）
        if (ReferenceEquals(s_active, this)) s_active = null;
        TeardownDocumentWindow();
    }
}
