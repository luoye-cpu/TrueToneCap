// TrueToneCap.App/PrivacyPolicyWindow.xaml.cs
// 隐私政策面：从磁盘读 PRIVACY.md，按行渲染成一份可滚动的只读文本。
//
// ═══ 为什么必须有这个窗口 ═══
// 微软商店政策 **10.5.1** 对 Win32 / Desktop Bridge 产品【强制】要求提供隐私政策，
// 且要求"用户能够访问"。官方给的两种方式：① 填 Partner Center 的隐私政策 URL；
// ② 在产品内提供入口。本仓**同时做两者**：
//   · 产品内入口就是本窗口（URL 会随托管/域名变化，产品内入口不会失效）；
//   · 另需把 PRIVACY.md 的公开 URL 填进 Partner Center（那是提交表单的必填项）。
//
// ═══ 为什么这个应用【确实需要】隐私政策（不是走过场）═══
// 本应用"大部分功能在本地"，但**视觉翻译会把整张截图（PNG）上传到用户配置的第三方 LLM**，
// 见 Core/Services/TranslationService.cs:854-855。这属于"访问个人信息"，
// 正是 10.5.1 要求披露的行为 —— 不能因为"多数功能在本地"就省略。
// PRIVACY.md 已逐条写明：哪些功能不出设备、哪些模式会外发什么、发给谁、如何关闭。
//
// ═══ 与 ThirdPartyNoticesWindow 的关系 ═══
// 渲染/路径上溯/降级/生命周期全部在 MarkdownDocumentWindow 基类里，只有一份实现。
// 本类只声明：读哪个文件、文案怎么写、单实例登记。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.App.Services;

namespace TrueToneCap.App;

public sealed partial class PrivacyPolicyWindow : MarkdownDocumentWindow
{
    /// <summary>隐私政策文件名。也是 csproj 里 Content 项的文件名 —— 单一事实源，两边不得各写一份。</summary>
    public const string PolicyFileName = "PRIVACY.md";

    // ── 单实例登记（同致谢窗口：本仓对窗口生命周期泄漏敏感）──
    private static PrivacyPolicyWindow? s_active;

    private static bool En => LocaleManager.CurrentLanguage == AppLanguage.English;

    // 入口卡片文案：与主窗口 ApplyLocale() 读同一组属性，避免"卡片英文、窗口中文"的分叉。
    public static string CardTitleText => En ? "Privacy policy" : "隐私政策";
    public static string CardDescText => En
        ? "Screenshots and OCR run entirely on this device. Content is sent to third parties only when you use translation — Vision translation uploads the whole screenshot."
        : "截图与文字识别均在本机完成。仅在你使用翻译功能时会向第三方发送内容——视觉翻译会上传整张截图，请阅读详情。";
    public static string CardButtonText => En ? "View privacy policy" : "查看隐私政策";
    private static string WindowTitleText => En ? "TrueToneCap Privacy Policy" : "TrueToneCap 隐私政策";

    // ── 基类要求的文案/身份 ──

    protected override string DocumentFileName => PolicyFileName;
    protected override string LogTag => "Privacy";
    protected override bool IsEnglish => En;

    // ── 转发 XAML 生成的控件（见 MarkdownDocumentWindow 文件头的说明）──
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
        HeaderTitleTxt.Text = En ? "Privacy policy" : "隐私政策";
        HeaderHintTxt.Text = En
            ? "How this app handles your data (read only)."
            : "本应用的数据处理方式（只读展示）";
        CloseBtnTxt.Text = En ? "Close" : "关闭";
    }

    /// <summary>打开隐私政策窗口；若已开着则只是把它切到前台。</summary>
    public static void ShowOrActivate()
    {
        var existing = s_active;
        if (existing is not null)
        {
            try { existing.Activate(); return; }
            catch { s_active = null; }  // 登记已失效：丢掉重建，不留僵尸引用
        }
        new PrivacyPolicyWindow().Activate();
    }

    private PrivacyPolicyWindow()
    {
        InitializeComponent();
        InitializeDocumentWindow();
        s_active = this;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        // 同致谢窗口：先摘静态登记（不摘会让已关闭窗口连同可视化树常驻、且下次再也开不出来），
        // 事件订阅与内容块交给基类统一断开。
        if (ReferenceEquals(s_active, this)) s_active = null;
        TeardownDocumentWindow();
    }
}
