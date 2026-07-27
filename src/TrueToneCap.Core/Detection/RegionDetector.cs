// TrueToneCap.Core/Detection/RegionDetector.cs
// A+B 混合区域检测引擎
//   A: Windows UIA 窗口枚举 (EnumWindows + GetWindowRect)
//   B: 边缘投影矩形检测 (高对比度边界)

using System.Runtime.InteropServices;
using System.Text;

namespace TrueToneCap.Core.Detection;

/// <summary>屏幕区域自动检测引擎。</summary>
public static class RegionDetector
{
    // ═══════════════════════════════════════
    //  P/Invoke
    // ═══════════════════════════════════════

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(nint hwnd, out RECT lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetWindowText(nint hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int GetClassName(nint hwnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern long GetWindowLongPtr(nint hwnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint gaFlags);

    // DWM 精确窗口边框（排除 Win10/11 透明阴影）
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, uint dwAttribute, out RECT pvAttribute, uint cbAttribute);

    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const uint GA_ROOTOWNER = 3;

    // 排除的窗口类名（系统/不可见/全屏覆盖层）
    private static readonly HashSet<string> ExcludedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows.UI.Core.CoreWindow", "Progman", "WorkerW", "Shell_TrayWnd",
        "Button", "Static", "ScrollBar", "tooltips_class32",
        "SysShadow", "SysDragDrop", "MsgHelper", "IME",
        "MSCTFIME UI", "CiceroUIWndFrame", "Ghost", "Dwm",
        "WindowsForms10.STATIC", "WindowsForms10.BUTTON",
        "DesktopWindowXamlSource", "XamlIslandWindow",
    };

    // 排除的窗口标题关键词
    private static readonly string[] ExcludedTitleKeywords =
        ["Microsoft Text Input", "MSCTFIME", "Default IME", "IME Pad"];

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }

    /// <summary>获取窗口的可见边框：优先 DWM 精确边框（排除透明阴影），失败回退 GetWindowRect。</summary>
    private static bool GetVisibleWindowRect(nint hwnd, out RECT rect)
    {
        // DWMWA_EXTENDED_FRAME_BOUNDS 返回不含阴影/透明边距的真实可见矩形
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, (uint)Marshal.SizeOf<RECT>()) == 0
            && rect.Width > 0 && rect.Height > 0)
        {
            return true;
        }
        // 回退：传统 GetWindowRect（可能含阴影）
        return GetWindowRect(hwnd, out rect);
    }

    // ═══════════════════════════════════════
    //  A: UIA 窗口枚举
    // ═══════════════════════════════════════

    /// <summary>枚举虚拟桌面指定区域内的可见顶层窗口。</summary>
    public static List<DetectedRegion> DetectWindows(int vx, int vy, int vw, int vh)
    {
        var regions = new List<DetectedRegion>();
        var added = new HashSet<(int, int, int, int)>(); // 去重

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;

            // 跳过子窗口
            long style = GetWindowLongPtr(hwnd, GWL_STYLE);
            if ((style & WS_CHILD) != 0) return true;

            // 跳过工具窗口（除非有 APPWINDOW 标记）
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
                return true;

            // 获取窗口矩形（优先 DWM 精确边框，排除 Win10/11 透明阴影，使框贴合真实窗口）
            if (!GetVisibleWindowRect(hwnd, out var rect)) return true;
            int w = rect.Width, h = rect.Height;

            // 过滤：太小、太大、无效
            if (w < 40 || h < 40 || w > 8192 || h > 8192) return true;
            if (rect.Left == 0 && rect.Top == 0 && w == 0 && h == 0) return true;

            // 过滤：窗口不在捕获区域内
            if (rect.Right <= vx || rect.Left >= vx + vw ||
                rect.Bottom <= vy || rect.Top >= vy + vh) return true;

            // 过滤类名
            var cn = new StringBuilder(256);
            GetClassName(hwnd, cn, 256);
            string className = cn.ToString();
            if (ExcludedClasses.Contains(className)) return true;

            // 过滤标题
            var sb = new StringBuilder(256);
            GetWindowText(hwnd, sb, 256);
            string title = sb.ToString();
            if (string.IsNullOrWhiteSpace(title)) return true;
            foreach (var kw in ExcludedTitleKeywords)
                if (title.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;

            // 去重
            var key = (rect.Left, rect.Top, w, h);
            if (!added.Add(key)) return true;

            regions.Add(new DetectedRegion
            {
                X = rect.Left, Y = rect.Top,
                Width = w, Height = h,
                Title = title,
                ClassName = className,
                Source = RegionSource.Uia,
            });
            return true;
        }, 0);

        return regions;
    }

    // ═══════════════════════════════════════
    //  B: 边缘投影矩形检测
    // ═══════════════════════════════════════

    /// <summary>基于边缘投影检测矩形区域（回退方案）。</summary>
    /// <param name="bgra">BGRA8 像素数据。</param>
    /// <param name="w">图像宽度。</param>
    /// <param name="h">图像高度。</param>
    /// <param name="vx">虚拟桌面原点 X。</param>
    /// <param name="vy">虚拟桌面原点 Y。</param>
    /// <returns>检测到的矩形区域。</returns>
    public static List<DetectedRegion> DetectEdgeRegions(byte[] bgra, int w, int h, int vx, int vy)
    {
        // 缩小到 1/4 加速（ceiling 除法，不丢失边缘像素）
        int dsw = (w + 3) / 4, dsh = (h + 3) / 4;
        var gray = DownsampleToGray(bgra, w, h, dsw, dsh);

        // 水平/垂直梯度投影
        var hEdges = new float[dsh];
        var vEdges = new float[dsw];
        ComputeEdgeProjections(gray, dsw, dsh, hEdges, vEdges);

        // 找到强边缘的行/列
        float hThresh = FindThreshold(hEdges, 0.7f);
        float vThresh = FindThreshold(vEdges, 0.7f);
        var hLines = FindPeakRanges(hEdges, hThresh, 2);
        var vLines = FindPeakRanges(vEdges, vThresh, 2);

        // 水平线和垂直线配对形成矩形
        var regions = new List<DetectedRegion>();
        int scale = 4;

        for (int i = 0; i < hLines.Count - 1; i++)
        {
            for (int j = i + 1; j < hLines.Count; j++)
            {
                int top = hLines[i].Start * scale;
                int bottom = (hLines[j].End + 1) * scale;
                int regionH = bottom - top;
                if (regionH < 60 || regionH > h * 0.9f) continue;

                for (int k = 0; k < vLines.Count - 1; k++)
                {
                    for (int l = k + 1; l < vLines.Count; l++)
                    {
                        int left = vLines[k].Start * scale;
                        int right = (vLines[l].End + 1) * scale;
                        int regionW = right - left;
                        if (regionW < 60 || regionW > w * 0.9f) continue;

                        // 验证区域内包含实际内容（非全黑/全白）
                        if (!HasContent(bgra, w, h, left, top, regionW, regionH))
                            continue;

                        regions.Add(new DetectedRegion
                        {
                            X = vx + left, Y = vy + top,
                            Width = regionW, Height = regionH,
                            Source = RegionSource.Edge,
                        });

                        // 限制数量避免爆炸
                        if (regions.Count >= 30) return PostProcess(regions, w, h);
                    }
                }
            }
        }

        return PostProcess(regions, w, h);
    }

    // ═══════════════════════════════════════
    //  C: 合并
    // ═══════════════════════════════════════

    /// <summary>智能区域检测：UIA 窗口枚举为主，边缘检测仅作兜底。</summary>
    /// <remarks>
    /// 边缘投影检测会把壁纸纹理/图标网格/窗口内 UI 边界拼成"不对齐任何窗口"的伪矩形，
    /// 造成预览界面一堆莫名其妙的蓝框。因此正常情况（UIA 能枚举到窗口）只使用 UIA，
    /// 框精确贴合真实窗口；仅当 UIA 完全无结果（纯壁纸/全屏游戏/自绘窗口等极端情况）
    /// 才回退边缘检测，保证仍有可框选区域。漏检的窗口可由用户拖拽自定义框选兜底。
    /// </remarks>
    public static List<DetectedRegion> DetectAll(byte[] bgra, int vx, int vy, int vw, int vh)
    {
        // A: 窗口枚举（主力 — 框对齐真实窗口）
        var uiaRegions = DetectWindows(vx, vy, vw, vh);

        if (uiaRegions.Count > 0)
        {
            // 正常路径：仅 UIA，彻底避免边缘伪框
            uiaRegions.Sort((a, b) => b.Area.CompareTo(a.Area));
            return uiaRegions;
        }

        // B: 兜底 — UIA 一个窗口都没检测到时才用边缘检测
        var edgeRegions = DetectEdgeRegions(bgra, vw, vh, vx, vy);
        edgeRegions.Sort((a, b) => b.Area.CompareTo(a.Area));
        return edgeRegions;
    }

    // ═══════════════════════════════════════
    //  辅助
    // ═══════════════════════════════════════

    private static float OverlapRatio(DetectedRegion a, DetectedRegion b)
    {
        int ox = Math.Max(a.X, b.X), oy = Math.Max(a.Y, b.Y);
        int ow = Math.Min(a.X + a.Width, b.X + b.Width) - ox;
        int oh = Math.Min(a.Y + a.Height, b.Y + b.Height) - oy;
        if (ow <= 0 || oh <= 0) return 0;
        float overlapArea = ow * oh;
        float minArea = Math.Min(a.Area, b.Area);
        return overlapArea / Math.Max(minArea, 1);
    }

    private static List<DetectedRegion> PostProcess(List<DetectedRegion> regions, int w, int h)
    {
        // 移除过大（>85%全屏）和过小的区域
        int maxArea = (int)(w * h * 0.85f);
        regions.RemoveAll(r => r.Area > maxArea || r.Area < 2500);
        return regions;
    }

    // ── 图像处理辅助 ──

    private static byte[] DownsampleToGray(byte[] bgra, int w, int h, int dw, int dh)
        => PixelOps.DownsampleToGraySimd(bgra, w, h, dw, dh);

    private static void ComputeEdgeProjections(byte[] gray, int w, int h, float[] hEdges, float[] vEdges)
        => PixelOps.ComputeEdgeProjectionsSimd(gray, w, h, hEdges, vEdges);

    private static float FindThreshold(float[] values, float percentile)
    {
        var sorted = new float[values.Length];
        Array.Copy(values, sorted, values.Length);
        Array.Sort(sorted);
        int idx = (int)(sorted.Length * percentile);
        return sorted[Math.Min(idx, sorted.Length - 1)];
    }

    private struct Range { public int Start; public int End; }

    private static List<Range> FindPeakRanges(float[] values, float threshold, int minWidth)
    {
        var ranges = new List<Range>();
        int? start = null;
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] >= threshold)
            {
                start ??= i;
            }
            else if (start is int s)
            {
                if (i - s >= minWidth)
                    ranges.Add(new Range { Start = s, End = i - 1 });
                start = null;
            }
        }
        if (start is int s2 && values.Length - s2 >= minWidth)
            ranges.Add(new Range { Start = s2, End = values.Length - 1 });
        return ranges;
    }

    private static bool HasContent(byte[] bgra, int w, int h, int rx, int ry, int rw, int rh)
    {
        // 采样检查区域内是否有足够的颜色变化（非纯色空白区）
        int samples = 20;
        int prev = -1, changes = 0;
        for (int i = 0; i < samples; i++)
        {
            int sx = rx + rw * i / samples;
            int sy = ry + rh * i / samples;
            if (sx >= w || sy >= h) continue;
            int idx = (sy * w + sx) * 4;
            int gray = (bgra[idx] + bgra[idx + 1] + bgra[idx + 2]) / 3;
            if (prev >= 0 && Math.Abs(gray - prev) > 15) changes++;
            prev = gray;
        }
        return changes >= 3; // 至少 3 处颜色变化 = 有实际内容
    }
}
