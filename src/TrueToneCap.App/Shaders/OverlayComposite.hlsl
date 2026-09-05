// TrueToneCap.App/Shaders/OverlayComposite.hlsl
// HDR 截图预览 UI 合成着色器 (2026-08-11 Phase 2)
// 桌面帧(scRGB Float16 纹理) + UI 覆盖层(遮罩/边框/手柄/工具栏) 一次绘制到后台缓冲
// 替代 CPU CompositeUI 全屏像素混合 — 拖拽期间合成成本从 ~20-30ms 降到 GPU <2ms

Texture2D<float4> DesktopTex : register(t0); // 桌面帧 (scRGB 线性, 与交换链同尺寸)
Texture2D<float4> TextTex    : register(t1); // 工具栏文字图集 (BGRA8, alpha=覆盖率)
SamplerState Samp : register(s0);

cbuffer UIState : register(b0)
{
    float4 OverlayColor;   // 遮罩色 (线性 RGB + A)
    float4 BorderColor;    // 边框色 (线性 RGB + A)
    float4 SelRect;        // 选区 (x, y, w, h) 物理像素
    float4 ToolbarRect;    // 工具栏背景 (x, y, w, h)
    float4 ButtonLayout;   // (btnW, btnH, gap, btnCount)
    float4 PadHandle;      // (padX, padY, handleSize, 保留)
    float4 MousePos;       // (mx, my, 0, 0)
    float4 Flags;          // (selComplete, down, hoverIndex, dark)
    float4 FrameSize;      // (w, h, 0, 0)
    float4 TextSubs[40];   // 图集子矩形 (0-6 主工具栏, 7-17 标注工具栏, 18-25 文字槽 0-7, 26 悬停标题)
    float4 TextPos[40];    // 帧内位置 (0-6 主, 7-17 标注, 26 悬停标题位置)
    float4 AnnoState;      // (mode: 0/1, tool: 0..5, down: 0/1, 0)
    float4 AnnoColor;      // 标注颜色 (线性 RGB + A)
    float4 AnnoStroke;     // (strokeW, opacity, 0, 0)
    float4 HoverRect;      // 悬停窗口矩形 (x, y, w, h); w<=0 无悬停 (QQ截图式高亮)
    float4 AnnoLayers[256]; // 16 层 × 16 vec (V0: type,opacity,strokeW,0; V1: x1,y1,x2,y2; V2: 颜色; V3: 参数,槽,0,0; V4-15: 画笔段 12)
};

// 点到线段距离
float SegmentDist(float2 p, float2 a, float2 b)
{
    float2 ab = b - a;
    float len2 = dot(ab, ab);
    float t = len2 > 1e-6 ? saturate(dot(p - a, ab) / len2) : 0.0;
    return length(p - (a + ab * t));
}

// 圆角矩形 SDF: 返回 <0 在内部
float RoundRectSDF(float2 p, float2 center, float2 half, float rad)
{
    float2 d = abs(p - center) - half + rad;
    return length(max(d, 0.0)) - rad;
}

// 像素着色器输入 (与 FullscreenVS 输出结构一致, 含 SV_POSITION — 仅 TEXCOORD0 可能连接失败)
struct PSInput
{
    float4 pos : SV_POSITION;
    float2 uv  : TEXCOORD0;
};

float4 main(PSInput input) : SV_Target
{
    float2 uv = input.uv;
    float2 pos = uv * FrameSize.xy; // 物理像素坐标
    float4 base = DesktopTex.Sample(Samp, uv);

    float selComplete = Flags.x;
    float down = Flags.y;
    float dark = Flags.w;
    float mx = MousePos.x;
    float my = MousePos.y;

    float2 selMin = SelRect.xy;
    float2 selMax = SelRect.xy + SelRect.zw;

    // ═══ 1. 遮罩（选区外永远遮罩; 未框选时 SelRect=0 → 全屏遮罩）═══
    // 2026-08-11: 原 maskActive=(!selComplete||down) 在选区完成后取消遮罩 → 选区外显示桌面。
    // 需求: "没框选部分仍然灰色" → 无条件选区外遮罩。
    bool inSel = all(pos >= selMin) && all(pos <= selMax);
    if (!inSel)
    {
        base.rgb = lerp(base.rgb, OverlayColor.rgb, OverlayColor.a);
    }

    // ═══ 1.5 窗口悬停高亮（QQ截图式: 未选中时高亮悬停窗口, 窗口内不遮罩）═══
    bool inHover = HoverRect.z > 0.0
        && all(pos >= HoverRect.xy) && all(pos <= HoverRect.xy + HoverRect.zw);
    if (selComplete < 0.5 && inHover)
    {
        // 窗口内恢复桌面亮度（不遮罩）
        base.rgb = DesktopTex.Sample(Samp, uv).rgb;

        // 亮蓝色 3px 边框 (QQ截图风格)
        float2 dh = min(pos - HoverRect.xy, HoverRect.xy + HoverRect.zw - pos);
        float minDh = min(dh.x, dh.y);
        if (minDh >= -3.5 && minDh <= 3.5)
            base.rgb = lerp(base.rgb, float3(0.2, 0.51, 0.96), 0.95);

        // 标题 tooltip（左上角上方, 图集槽 26）
        float4 tipSub = TextSubs[26];
        float4 tipPos = TextPos[26];
        if (tipSub.z > 0.0 && all(pos >= tipPos.xy - 1.0) && all(pos <= tipPos.xy + tipPos.zw + 1.0))
        {
            float2 tipCenter = (tipPos.xy + tipPos.xy + tipPos.zw) * 0.5;
            float2 tipHalf = tipPos.zw * 0.5;
            float sdTip = RoundRectSDF(pos, tipCenter, tipHalf, 4.0);
            if (sdTip <= 0.0)
            {
                base.rgb = lerp(base.rgb, float3(0.03, 0.03, 0.03), 0.92);
                float2 atlTip = (pos - tipPos.xy) / tipPos.zw * tipSub.zw + tipSub.xy;
                float4 glyphTip = TextTex.Sample(Samp, atlTip);
                base.rgb = lerp(base.rgb, float3(1.0, 1.0, 1.0), glyphTip.a);
            }
        }
    }

    // ═══ 2. 选区边框（2px）═══
    // ═══ 2026-08-25 修复: 拖拽过程中 (down=true, selComplete=false) 也要画橡皮筋框 ═══
    // 旧实现仅 selComplete 后显示 → 用户按下拖拽时看不到选区框 → 误以为无法框选。
    if (selComplete > 0.5 || down > 0.5)
    {
        float2 d = min(pos - selMin, selMax - pos);
        float minD = min(d.x, d.y);
        if (minD >= -2.5 && minD <= 2.5)
            base.rgb = lerp(base.rgb, BorderColor.rgb, 0.9);
    }

    // ═══ 3. 四角手柄（白色方块, 选区完成时）═══
    if (selComplete > 0.5)
    {
        float hs = PadHandle.z * 0.5;
        float2 c0 = selMin;
        float2 c1 = float2(selMax.x, selMin.y);
        float2 c2 = float2(selMin.x, selMax.y);
        float2 c3 = selMax;
        float2 d0 = abs(pos - c0); if (d0.x <= hs && d0.y <= hs) base.rgb = 1.0;
        float2 d1 = abs(pos - c1); if (d1.x <= hs && d1.y <= hs) base.rgb = 1.0;
        float2 d2 = abs(pos - c2); if (d2.x <= hs && d2.y <= hs) base.rgb = 1.0;
        float2 d3 = abs(pos - c3); if (d3.x <= hs && d3.y <= hs) base.rgb = 1.0;
    }

    // ═══ 4. 工具栏（选区完成时）═══
    int btnCount = (int)(ButtonLayout.w + 0.5); // 6=主工具栏 10=标注工具栏
    float3 fg = dark ? float3(1.0, 1.0, 1.0) : float3(0.05, 0.05, 0.05);
    // 悬停按钮索引（由鼠标位置计算, 固定 10 次循环 + 有效数判断）
    int hover = -1;
    if (selComplete > 0.5 && ToolbarRect.z > 0.0)
    {
        float btnW = ButtonLayout.x;
        float btnH = ButtonLayout.y;
        float gap = ButtonLayout.z;
        float padX = PadHandle.x;
        float padY = PadHandle.y;
        float2 tbMin0 = ToolbarRect.xy;
        [unroll(10)]
        for (int i = 0; i < 10; i++)
        {
            if (i >= btnCount) continue;
            float2 b0 = tbMin0 + float2(padX + i * (btnW + gap), padY);
            if (mx >= b0.x && mx <= b0.x + btnW && my >= b0.y && my <= b0.y + btnH)
            {
                hover = i;
                break;
            }
        }
    }

    if (selComplete > 0.5 && ToolbarRect.z > 0.0)
    {
        float2 tbMin = ToolbarRect.xy;
        float2 tbMax = ToolbarRect.xy + ToolbarRect.zw;
        int subBase = AnnoState.x > 0.5 ? 7 : 0; // 标注工具栏文字槽偏移

        if (all(pos >= tbMin - 1.0) && all(pos <= tbMax + 1.0))
        {
            // 毛玻璃背景（圆角 6px, 88% 不透明）
            float2 tbCenter = (tbMin + tbMax) * 0.5;
            float2 tbHalf = (tbMax - tbMin) * 0.5;
            float sd = RoundRectSDF(pos, tbCenter, tbHalf, 6.0);
            if (sd <= 0.0)
            {
                float3 bg = dark ? float3(0.07, 0.07, 0.07) : float3(0.93, 0.93, 0.93);
                base.rgb = lerp(base.rgb, bg, 0.88);

                // 按钮区域（含悬停高亮）
                float btnW = ButtonLayout.x;
                float btnH = ButtonLayout.y;
                float gap = ButtonLayout.z;
                float padX = PadHandle.x;
                float padY = PadHandle.y;

                [unroll(10)]
                for (int i = 0; i < 10; i++)
                {
                    if (i >= btnCount) continue;
                    float2 btnMin = tbMin + float2(padX + i * (btnW + gap), padY);
                    float2 btnMax = btnMin + float2(btnW, btnH);
                    if (all(pos >= btnMin) && all(pos <= btnMax))
                    {
                        if (i == hover)
                        {
                            float3 hl = dark ? float3(0.15, 0.15, 0.15) : float3(0.85, 0.85, 0.85);
                            base.rgb = lerp(base.rgb, hl, 1.0);
                        }
                        // 文字: 采样图集子矩形 (TextPos 必须与 TextSubs 同偏移 — 2026-08-11 修复)
                        float4 sub = TextSubs[subBase + i];
                        if (sub.z > 0.0 && sub.w > 0.0)
                        {
                            float4 tp = TextPos[subBase + i];
                            float2 atl = (pos - tp.xy) / tp.zw * sub.zw + sub.xy;
                            float4 glyph = TextTex.Sample(Samp, atl);
                            base.rgb = lerp(base.rgb, fg, glyph.a);
                        }
                        break;
                    }
                }
            }

            // 悬停提示（工具栏下方/上方; 提示槽: 主=6, 标注=17）
            if (hover >= 0 && hover < btnCount)
            {
                float4 tipSub = AnnoState.x > 0.5 ? TextSubs[17] : TextSubs[6];
                float4 tipPos = AnnoState.x > 0.5 ? TextPos[17] : TextPos[6];
                if (tipSub.z > 0.0 && all(pos >= tipPos.xy - 1.0) && all(pos <= tipPos.xy + tipPos.zw + 1.0))
                {
                    float2 tipCenter = (tipPos.xy + tipPos.xy + tipPos.zw) * 0.5;
                    float2 tipHalf = tipPos.zw * 0.5;
                    float sdTip = RoundRectSDF(pos, tipCenter, tipHalf, 4.0);
                    if (sdTip <= 0.0)
                    {
                        float3 tipBg = dark ? float3(0.02, 0.02, 0.02) : float3(0.15, 0.15, 0.15);
                        base.rgb = lerp(base.rgb, tipBg, 0.92);
                        float2 atlTip = (pos - tipPos.xy) / tipPos.zw * tipSub.zw + tipSub.xy;
                        float4 glyphTip = TextTex.Sample(Samp, atlTip);
                        base.rgb = lerp(base.rgb, fg, glyphTip.a);
                    }
                }
            }
        }
    }

    // ═══ 5. 标注层（标注模式下, 选区内; 层 0-14 提交层, 层 15 预览层）═══
    if (AnnoState.x > 0.5 && inSel)
    {
        float2 ap = pos - selMin; // 帧坐标 → 选区图像坐标
        float defaultSW = AnnoStroke.x;
        float opacityMul = AnnoStroke.y;

        [unroll(16)]
        for (int i = 0; i < 16; i++)
        {
            float4 a0 = AnnoLayers[i * 16 + 0];
            float type = a0.x;
            if (type < 0.5) continue; // 空槽
            float layerA = a0.y * opacityMul;
            float sw = max(a0.z > 0.5 ? a0.z : defaultSW, 1.0);
            float4 a1 = AnnoLayers[i * 16 + 1]; // 几何 (x1,y1,x2,y2)
            float4 a2 = AnnoLayers[i * 16 + 2]; // 颜色 (线性 RGBA)
            float4 a3 = AnnoLayers[i * 16 + 3]; // (param, slot, 0, 0)

            // ── 1. 矩形描边 (2026-08-11 修复: d 必须取 abs, 否则矩形外所有点命中) ──
            if (type < 1.5)
            {
                float d = max(max(a1.x - ap.x, ap.x - a1.z), max(a1.y - ap.y, ap.y - a1.w));
                if (abs(d) <= sw * 0.5)
                    base.rgb = lerp(base.rgb, a2.rgb, a2.a * layerA);
            }
            // ── 2. 椭圆描边 ──
            else if (type < 2.5)
            {
                float2 c = (a1.xy + a1.zw) * 0.5;
                float2 r = max((a1.zw - a1.xy) * 0.5, 0.5);
                float2 q = (ap - c) / r;
                float d = length(q) - 1.0;
                float px = (sw * 0.5) / min(r.x, r.y);
                if (abs(d) <= px)
                    base.rgb = lerp(base.rgb, a2.rgb, a2.a * layerA);
            }
            // ── 3. 箭头（主线 + 头部 ±30° 两线）──
            else if (type < 3.5)
            {
                float head = max(a3.x, 6.0);
                float dMain = SegmentDist(ap, a1.xy, a1.zw);
                float2 dir = a1.zw - a1.xy;
                float len = length(dir);
                float2 u = len > 1e-4 ? dir / len : float2(1.0, 0.0);
                const float ca = 0.866025; // cos(30°)
                const float sa = 0.5;      // sin(30°)
                float2 h1 = a1.zw - head * float2(u.x * ca - u.y * sa, u.x * sa + u.y * ca);
                float2 h2 = a1.zw - head * float2(u.x * ca + u.y * sa, -u.x * sa + u.y * ca);
                float d = min(dMain, min(SegmentDist(ap, a1.zw, h1), SegmentDist(ap, a1.zw, h2)));
                if (d <= sw * 0.5)
                    base.rgb = lerp(base.rgb, a2.rgb, a2.a * layerA);
            }
            // ── 4. 画笔（≤12 段折线, V4-15 每段 (x1,y1,x2,y2), 无效段 z<-1000）──
            else if (type < 4.5)
            {
                float dMin = 1e10;
                [unroll(12)]
                for (int k = 0; k < 12; k++)
                {
                    float4 seg = AnnoLayers[i * 16 + 4 + k];
                    if (seg.z < -1000.0) break;
                    dMin = min(dMin, SegmentDist(ap, seg.xy, seg.zw));
                }
                if (dMin <= sw * 0.5)
                    base.rgb = lerp(base.rgb, a2.rgb, a2.a * layerA);
            }
            // ── 5. 文字（图集槽 18+slot, 采样字形）──
            else if (type < 5.5)
            {
                int slot = (int)(a3.y + 0.5);
                if (slot >= 0 && slot < 16)
                {
                    float4 sub = TextSubs[18 + slot];
                    if (sub.z > 0.0)
                    {
                        float2 atl = (ap - a1.xy) / a1.zw * sub.zw + sub.xy;
                        float4 glyph = TextTex.Sample(Samp, atl);
                        base.rgb = lerp(base.rgb, a2.rgb, glyph.a * layerA);
                    }
                }
            }
            // ── 6. 马赛克（3×3 块平均采样桌面）──
            else if (type < 6.5)
            {
                float bs = max(a3.x, 2.0);
                float2 c0 = floor(ap / bs) * bs + bs * 0.5;
                float3 acc = 0.0;
                [unroll(3)]
                for (int dy = -1; dy <= 1; dy++)
                {
                    [unroll(3)]
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        float2 fp = selMin + c0 + float2(dx, dy) * bs;
                        acc += DesktopTex.Sample(Samp, fp / FrameSize.xy).rgb;
                    }
                }
                base.rgb = lerp(base.rgb, acc / 9.0, layerA);
            }
        }
    }

    return float4(base.rgb, 1.0);
}



