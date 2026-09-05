// TrueToneCap.App/MainWindow.ColorManagement.cs
// MainWindow 的「色彩管理 UI」职责（partial class 拆分）
//
// 拆分说明（2026-08-29）：MainWindow.xaml.cs 过大，按职责拆分为多个 partial class 文件。
// 本文件承载色彩管理相关逻辑：源色域检测、色域映射 UI 提示、色彩空间标签读取。
//
// ⚠ partial class 拆分不改变运行时行为：所有成员仍属同一个 MainWindow 类型，
//   私有字段（_settings / ColorCbo / HdrSwitch 等）跨文件直接可见。

using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TrueToneCap.Core.Capture;
using TrueToneCap.Core.ColorManagement;

namespace TrueToneCap.App;

public sealed partial class MainWindow : Window
{
    /// <summary>获取当前鼠标所在显示器的原生色域标签（ACM 感知）。</summary>
    private string GetDisplayNativeGamut()
    {
        try
        {
            var monitor = DisplayEnumerator.GetMonitorUnderCursor();
            return ColorProfileProvider.GetDisplayNativeGamutTag(monitor);
        }
        catch { return "sRGB"; }
    }

    /// <summary>检测当前显示器色域并显示在 UI 中（ACM 感知）。</summary>
    private void DetectAndShowSourceGamut()
    {
        try
        {
            var displays = DisplayEnumerator.EnumerateDisplays();
            var primary = displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (primary is not null)
            {
                string csName;
                if (primary.IsHdr)
                {
                    csName = $"HDR (BT.2020/PQ, {primary.BitsPerColor}-bit)";
                }
                else if (primary.SupportsHdr)
                {
                    csName = $"HDR 未开启 (BT.2020 硬件, {primary.BitsPerColor}-bit)";
                }
                else if (_settings.AcmeDetected)
                {
                    // ACM 启用时检测显示器原生色域
                    var nativeGamut = GetDisplayNativeGamut();
                    csName = $"SDR ({nativeGamut}, ACM, {primary.BitsPerColor}bit)";
                }
                else
                {
                    csName = $"SDR (sRGB, {primary.BitsPerColor}bit)";
                }
                SourceGamutTxt.Text = csName;
            }
            else
            {
                SourceGamutTxt.Text = "sRGB (默认)";
            }
        }
        catch
        {
            SourceGamutTxt.Text = "sRGB (默认)";
        }
    }

    /// <summary>更新色域映射 UI（HDR 感知 + ACM 感知）。</summary>
    private void UpdateGamutMappingUI()
    {
        var sourceTag = SourceGamutTxt.Text;
        var targetTag = (ColorCbo.SelectedItem as ComboBoxItem)?.Tag as string ?? "System";
        bool hdrOn = HdrSwitch.IsOn && HdrSwitch.IsEnabled;

        // 判断源显示器特性
        bool sourceIsHdr = sourceTag.Contains("HDR (BT.2020/PQ");
        bool sourceIsHdrCapable = sourceTag.Contains("HDR 未开启");
        bool sourceIsAcm = sourceTag.Contains("ACM");
        bool sourceIsWide = sourceIsHdr || sourceIsHdrCapable
            || sourceTag.Contains("P3") || sourceTag.Contains("BT.2020") || sourceTag.Contains("AdobeRGB");

        // 有效目标色域：HDR 开启时 "System" 解析为 BT.2020
        string effectiveTarget = targetTag == "System" && hdrOn ? "BT2020" : targetTag;
        bool targetIsExplicitWide = effectiveTarget is "BT2020" or "DisplayP3" or "DCI_P3" or "AdobeRGB";
        bool targetIsSystem = effectiveTarget == "System";
        bool targetIsSystemWide = targetIsSystem && sourceIsAcm && sourceIsWide;
        bool targetIsWide = targetIsExplicitWide || targetIsSystemWide;

        // 构造目标色域显示名
        string targetName = targetTag switch
        {
            "System" when hdrOn => "BT.2020 (HDR10)",
            "System" when sourceIsHdr => "sRGB (色调映射)",
            "System" when sourceIsAcm && sourceIsWide => sourceTag.Replace("SDR (", "").Replace(", ACM", "").Replace(", 8bit)", "").Replace(", 10bit)", ""),
            "System" => sourceTag.Contains("HDR") ? "sRGB" : sourceTag,
            "sRGB" => "sRGB",
            "DisplayP3" => "Display P3",
            "DCI_P3" => "DCI-P3",
            "AdobeRGB" => "Adobe RGB",
            "BT2020" => "BT.2020",
            _ => "sRGB"
        };
        TargetGamutTxt.Text = targetName;

        if (hdrOn)
        {
            // HDR 开启 → 色域矩阵 → PQ → CICP，保留用户选择的色域
            string matrixDesc = targetTag switch
            {
                "DisplayP3" or "DCI_P3" => "scRGB→P3 矩阵",
                "AdobeRGB" => "scRGB→AdobeRGB 矩阵",
                "BT2020" => "scRGB→BT.2020 矩阵",
                _ => "scRGB→BT.2020 矩阵"
            };
            byte cicpP = targetTag switch
            {
                "DisplayP3" or "DCI_P3" => 12,
                "AdobeRGB" => 1,
                _ => 9  // BT.2020 / System
            };
            MappingArrow.Text = $"→ HDR 直通 ({targetName})";
            GamutMapHintTxt.Text = $"HDR 编码路径：WGC Float16 → {matrixDesc} → PQ ST.2084 → CICP(primaries={cicpP}, transfer=16)。"
                + (targetTag is "DisplayP3" or "DCI_P3" or "AdobeRGB"
                    ? "\n⚠ 注意：HDR 输出使用非标准 HDR10 容器色域。部分播放器/显示器可能无法正确解析。"
                    : "");
        }
        else if (targetIsWide)
        {
            // HDR 关闭 + 广色域目标 → WGC Float16 捕获 → 色域转换 → 色调映射到 SDR
            bool canUseFloat16 = sourceIsWide || sourceIsAcm || _settings.IccBakeEnabled;
            if (canUseFloat16)
            {
                MappingArrow.Text = "→ Float16 广色域捕获 → 色域映射";
                GamutMapHintTxt.Text = $"WGC Float16 捕获完整广色域 → 3×3 矩阵转换到 {targetName} → 色调映射 (分段 Reinhard) → SDR 输出。";
            }
            else
            {
                MappingArrow.Text = "→ 分段 Reinhard 缩限到";
                GamutMapHintTxt.Text = $"SDR 捕获 → ICC 烘焙 → {targetName}。";
            }
        }
        else
        {
            bool needsMapping = targetIsWide || (targetTag == "System" && (sourceIsHdr || sourceIsHdrCapable));
            MappingArrow.Text = needsMapping ? "→ 分段 Reinhard 缩限到" : "→ 直通（同色域）";
            GamutMapHintTxt.Text = needsMapping
                ? $"SDR 捕获 → ICC 烘焙 → {targetName}。"
                : "当前显示器色域与目标一致，无需转换。";
        }
    }

    /// <summary>从 ColorCbo 获取当前选择的色彩空间标签。
    /// <para>
    /// ⚠ 仅供 UI 线程调用。后台编码线程请改用 <see cref="CaptureEncodingUiState"/> 采集的
    /// <c>EncodingUiState.ColorSpaceTag</c> —— 跨线程访问 WinUI 控件会抛异常，
    /// 而此处的空 catch 会把异常吞掉并静默返回 "System"，
    /// 导致用户选了 Display P3 / BT.2020 却实际输出 System 色域且无任何提示。
    /// </para>
    /// </summary>
    private string GetSelectedColorSpaceTag()
    {
        try
        {
            if (ColorCbo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
                return tag;
        }
        catch { }
        return "System";
    }
}
