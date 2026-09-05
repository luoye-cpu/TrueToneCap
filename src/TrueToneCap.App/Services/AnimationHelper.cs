// TrueToneCap.App/Services/AnimationHelper.cs
// 过渡 UI 动画辅助 (可开关) — 2026-08-25 任务6
// 用 Composition KeyFrameAnimation 做页面切换淡入/位移、状态文本反馈; 开关关闭时全部短路。

using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;

namespace TrueToneCap.App.Services;

/// <summary>界面过渡动画辅助。所有方法在 EnableUiAnimations=false 时零成本短路。</summary>
public static class AnimationHelper
{
    /// <summary>动画是否启用（读设置, 异常时默认启用）。</summary>
    public static bool Enabled
    {
        get
        {
            try { return AppServices.Settings.Current.EnableUiAnimations; }
            catch { return true; }
        }
    }

    /// <summary>页面切换过渡: 新页面淡入 + 轻微上移 (180ms)。未启用时仅确保可见。</summary>
    public static void PageTransition(FrameworkElement pageContent)
    {
        pageContent.Visibility = Visibility.Visible;
        pageContent.Opacity = 1;
        if (!Enabled) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(pageContent);
            var compositor = visual.Compositor;

            // 起始状态: 下移 12px + 全透明
            visual.Offset = new System.Numerics.Vector3(0, 12, 0);
            visual.Opacity = 0f;

            // 淡入
            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = TimeSpan.FromMilliseconds(180);

            // 位移 12px → 0 (Vector3 属性路径 "Offset")
            var offset = compositor.CreateVector3KeyFrameAnimation();
            offset.InsertKeyFrame(0f, new System.Numerics.Vector3(0, 12, 0));
            offset.InsertKeyFrame(1f, new System.Numerics.Vector3(0, 0, 0),
                compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f)));
            offset.Duration = TimeSpan.FromMilliseconds(180);

            visual.StartAnimation("Opacity", fade);
            visual.StartAnimation("Offset", offset);
        }
        catch { /* 动画失败静默降级 */ }
    }

    /// <summary>状态文本更新反馈: 轻微闪烁提示 (120ms)。未启用或异常时无操作。</summary>
    public static void TextUpdateFeedback(TextBlock textBlock)
    {
        if (!Enabled) return;
        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(textBlock);
            visual.StopAnimation("Opacity");
            visual.Opacity = 0.35f;
            var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
            anim.InsertKeyFrame(0f, 0.35f);
            anim.InsertKeyFrame(1f, 1f);
            anim.Duration = TimeSpan.FromMilliseconds(120);
            visual.StartAnimation("Opacity", anim);
        }
        catch { /* 静默降级 */ }
    }
}
