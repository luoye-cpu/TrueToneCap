// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.App/Shaders/OverlayComposite.hlsl
// HDR 截图预览 UI 合成着色器 (2026-08-11 Phase 2)
// 桌面帧(scRGB Float16 纹理) + UI 覆盖层(遮罩/边框/手柄/工具栏) 一次绘制到后台缓冲
// 替代 CPU CompositeUI 全屏像素混合 — 拖拽期间合成成本从 ~20-30ms 降到 GPU <2ms

Texture2D<float4> DesktopTex : register(t0); // 桌面帧 (scRGB 线性, 与交换链同尺寸)
Texture2D<float4> TextTex    : register(t1); // 工具栏文字图集 (BGRA8, alpha=覆盖率)
Texture2D<float4> BadgeTex   : register(t2); // 选区尺寸徽标 (BGRA8, alpha=覆盖率; 拖拽时逐帧更新)
Texture2D<float4> EditTex    : register(t3); // 输入中的文字预览 (BGRA8, alpha=覆盖率; 每次敲键重烘)
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
    float4 TextSubs[56];   // 图集子矩形 (0-7 主按钮, 8-17 标注, 18 标注提示, 19-26 文字层 0-7, 27 悬停标题, 28-35 主逐钮提示,
                           //               36 静置提示, 38/39 第二行, 40-43 文字行格子, 44-55 下拉列表项)
    float4 TextPos[56];    // 帧内位置 (0-7 主, 8-17 标注, 18 标注提示, 27 悬停标题, 36 静置提示, 37 主提示框,
                           //            38 选项行矩形, 39 选项行状态, 41 下拉面板, 42 下拉状态, 43 下拉当前项；
                           //            40 = 形态3 的四格宽度 / 形态2 的 (数值格宽, 徽标像素宽, 徽标像素高, 0))
    float4 AnnoState;      // (mode: 0/1, tool: 0..5, down: 0/1, 0)
    float4 AnnoColor;      // 标注颜色 (线性 RGB + A)
    float4 AnnoStroke;     // (strokeW, opacity, 0, 0)
    float4 HoverRect;      // 悬停窗口矩形 (x, y, w, h); w<=0 无悬停 (QQ截图式高亮)
    float4 SizeBadgeSub;   // 尺寸徽标纹理子矩形 (u0,v0,du,dv) — 动态小纹理, w<=0 不画
    float4 SizeBadgePos;   // 尺寸徽标帧内位置 (x, y, w, h)
    float4 AnnoLayers[256]; // 16 层 × 16 vec (V0: type,opacity,strokeW,0; V1: x1,y1,x2,y2; V2: 颜色; V3: 参数,槽,0,0; V4-15: 画笔段 12)
    float4 EditSub;        // 输入中文字纹理子矩形 (u0,v0,du,dv) — w<=0 不画
    float4 EditPos;        // 输入中文字帧内位置 (x, y, w, h)
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

// 马赛克块的均值。CPU 合成是"整块逐通道求均值"（在 sRGB 解到线性光之后求，见
// AnnotationRasterizer.s_srgbToLinear），GPU 抽不出整块 ⇒ 用块内 3×3 九点平均近似。
// 桌面纹理本来就是 scRGB 线性基，这里的平均天然与 CPU 同一个光照空间。
// 旧实现只取块中心一个像素：块内一有纹理，预览就成了"最邻近采样"（块看似原图、只是被切碎），
// 而文件里是真正的平均值 —— 又一处"预览 = 输出"的裂缝。
// blkMin = 块左上角（标注坐标系）；selMin = 选区左上角（帧坐标系），换算回桌面纹理。
float3 MosaicBlockMean(float2 blkMin, float bs, float2 selMin)
{
    float3 acc = 0.0;
    [unroll]
    for (int j = 0; j < 3; j++)
    {
        [unroll]
        for (int i = 0; i < 3; i++)
        {
            float2 o = blkMin + bs * float2((i + 0.5) / 3.0, (j + 0.5) / 3.0);
            acc += DesktopTex.Sample(Samp, (selMin + o) / FrameSize.xy).rgb;
        }
    }
    return acc / 9.0;
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

        // 标题 tooltip（左上角上方, 图集槽 27）
        float4 tipSub = TextSubs[27];
        float4 tipPos = TextPos[27];
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

    // ═══ 1.6 静置操作提示（未框选、未拖拽时居中; 图集槽 35, 原 SDR 兜底层用的也是这一句）═══
    if (selComplete < 0.5 && down < 0.5)
    {
        float4 hintSub = TextSubs[36];
        float4 hintPos = TextPos[36];
        if (hintSub.z > 0.0 && all(pos >= hintPos.xy) && all(pos <= hintPos.xy + hintPos.zw))
        {
            float2 atlHint = (pos - hintPos.xy) / hintPos.zw * hintSub.zw + hintSub.xy;
            float4 glyphHint = TextTex.Sample(Samp, atlHint);
            base.rgb = lerp(base.rgb, float3(1.0, 1.0, 1.0), glyphHint.a * 0.8);
        }
    }

    // ═══ 1.7 选区尺寸徽标（拖拽中/完成后, 选区上方居中; 与已删除的 SDR 兜底层 SizeLabel 同形）═══
    // 文字来自独立小纹理 (t2) 而非图集: 拖拽时每个像素都可能改数, 全图集重建是 5MB 级上传。
    if ((selComplete > 0.5 || down > 0.5) && SizeBadgePos.z > 0.0)
    {
        if (all(pos >= SizeBadgePos.xy - 1.0) && all(pos <= SizeBadgePos.xy + SizeBadgePos.zw + 1.0))
        {
            float2 bgCenter = SizeBadgePos.xy + SizeBadgePos.zw * 0.5;
            if (RoundRectSDF(pos, bgCenter, SizeBadgePos.zw * 0.5, 3.0) <= 0.0)
            {
                base.rgb = lerp(base.rgb, float3(0.27, 0.53, 1.0), 1.0);
                float2 atlBadge = (pos - SizeBadgePos.xy) / SizeBadgePos.zw * SizeBadgeSub.zw + SizeBadgeSub.xy;
                float4 glyphBadge = BadgeTex.Sample(Samp, atlBadge);
                base.rgb = lerp(base.rgb, float3(1.0, 1.0, 1.0), glyphBadge.a);
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
    int btnCount = (int)(ButtonLayout.w + 0.5); // 7=主工具栏 10=标注工具栏
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
        int subBase = AnnoState.x > 0.5 ? 8 : 0; // 标注工具栏文字槽偏移（主工具栏 7 按钮 + 提示占 0-7）

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
                        // 选中的绘图工具常亮高亮（AnnoState.y = 0..5 = 矩形/椭圆/箭头/画笔/文字/马赛克）。
                        // 之前只有悬停反馈：点完"箭头"工具栏毫无变化，用户不知道当前是什么工具。
                        bool activeTool = AnnoState.x > 0.5 && i < 6 && abs(AnnoState.y - (float)i) < 0.5;
                        if (i == hover)
                        {
                            float3 hl = dark ? float3(0.15, 0.15, 0.15) : float3(0.85, 0.85, 0.85);
                            base.rgb = lerp(base.rgb, hl, 1.0);
                        }
                        else if (activeTool)
                        {
                            base.rgb = lerp(base.rgb, float3(0.27, 0.53, 1.0), 1.0);
                        }
                        // 文字: 采样图集子矩形 (TextPos 必须与 TextSubs 同偏移 — 2026-08-11 修复)
                        float4 sub = TextSubs[subBase + i];
                        if (sub.z > 0.0 && sub.w > 0.0)
                        {
                            float4 tp = TextPos[subBase + i];
                            float2 atl = (pos - tp.xy) / tp.zw * sub.zw + sub.xy;
                            float4 glyph = TextTex.Sample(Samp, atl);
                            float3 btnFg = activeTool ? float3(1.0, 1.0, 1.0) : fg;
                            base.rgb = lerp(base.rgb, btnFg, glyph.a);
                        }
                        break;
                    }
                }
            }

            // 悬停提示（工具栏下方/上方; 提示槽: 主=28+hover 逐钮, 标注=18）。
            // 2026-10-26 修复：此前主工具条提示槽 7 是一句静态"保存截图到文件"，悬停哪枚都显示它。
            if (hover >= 0 && hover < btnCount)
            {
                bool annoMode = AnnoState.x > 0.5;
                float4 tipSub = annoMode ? TextSubs[18] : TextSubs[28 + hover];
                float4 tipPos = annoMode ? TextPos[18] : TextPos[37];
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

    // ═══ 4.5 第二行选项条：线色 / 马赛克方式 / 画笔粗细 / 文字字体 ═══
    // 槽 38 = (行左, 行上, 等宽格子宽, 格高)；槽 39 = (gap, 格数, 形态, 状态)；
    // 形态 0/1/2 等宽（宽 = 槽38.z），形态 3 = 文字行（家族/字号/B/I 四格宽度在槽 40，
    // 状态槽 39.w = 悬停格+1，加粗/斜体在槽 43.y/.z）。
    // 色板固定写死在这里（与 C# s_swatchColor 同一顺序）。
    {
        float4 rowRect  = TextPos[38];
        float4 rowNum   = TextPos[39];
        float4 rowW     = TextPos[40];
        float4 txtState = TextPos[43];
        if (selComplete > 0.5 && rowNum.y > 0.5)
        {
            static const float3 kSwatch[6] =
            {
                float3(1.0000, 0.0212, 0.0212), // 红 RGB(255,40,40)
                float3(1.0000, 0.6795, 0.0000), // 黄 RGB(255,215,0)
                float3(0.0212, 0.5776, 0.1005), // 绿 RGB(40,200,90)
                float3(0.0452, 0.2623, 1.0000), // 蓝 RGB(60,140,255)
                float3(1.0000, 1.0000, 1.0000), // 白
                float3(0.0000, 0.0000, 0.0000), // 黑
            };
            int   optCount = (int)(rowNum.y + 0.5);
            int   optMode  = (int)(rowNum.z + 0.5);
            float optGap   = rowNum.x;
            float cellH    = rowRect.w;
            int   hovIdx, selIdx;
            if (optMode >= 2)
            {
                // 形态 2（粗细滑条）/ 3（文字行）没有"选中第几格"：槽 39.w 只放 悬停格+1。
                // 只有形态 0/1 才是 sel+1+(hover+1)*16 的打包值 —— 用打包公式解滑条的悬停，
                // 结论恒为"无悬停"，还会把相邻格误判成选中
                hovIdx = (int)(rowNum.w + 0.5) - 1;
                selIdx = -1;
            }
            else
            {
                float dec = rowNum.w - 1.0;
                hovIdx = (int)floor(dec / 16.0 + 0.5) - 1;
                selIdx = (int)(dec - (hovIdx + 1) * 16.0 + 0.5);
            }
            bool bOn = txtState.y > 0.5;
            bool iOn = txtState.z > 0.5;

            float2 rowMin = rowRect.xy;
            if (pos.y >= rowMin.y - 1.0 && pos.y <= rowMin.y + cellH + 1.0 && pos.x >= rowMin.x - 1.0)
            {
                // 前缀 x：形态 3 的格子宽度各不相同，逐格累加（绘制与命中检测同源）
                float px[6];
                px[0] = 0.0;
                [unroll(5)]
                for (int k = 1; k < 6; k++)
                {
                    // 形态 2 只有两格且不等宽：左格（滑条）= rowRect.z，右格（数值）= rowW.x
                    float kw = (optMode == 3)
                        ? ((k == 1) ? rowW.x : (k == 2) ? rowW.y : (k == 3) ? rowW.z : rowW.w)
                        : ((optMode == 2 && k == 2) ? rowW.x : rowRect.z);
                    px[k] = px[k - 1] + kw + optGap;
                }

                [unroll(6)]
                for (int i = 0; i < 6; i++)
                {
                    if (i >= optCount) break;
                    float cw = (optMode == 3)
                        ? ((i == 0) ? rowW.x : (i == 1) ? rowW.y : (i == 2) ? rowW.z : rowW.w)
                        : ((optMode == 2 && i == 1) ? rowW.x : rowRect.z);
                    float2 cMin = rowMin + float2(px[i], 0.0);
                    float2 cMax = cMin + float2(cw, cellH);
                    if (!(all(pos >= cMin) && all(pos <= cMax))) continue;

                    bool sel = (i == selIdx);
                    bool hov = (i == hovIdx);
                    float3 frameCol = sel ? (dark ? float3(1.0, 1.0, 1.0) : float3(0.05, 0.05, 0.05))
                                          : (dark ? float3(0.35, 0.35, 0.35) : float3(0.60, 0.60, 0.60));
                    base.rgb = lerp(base.rgb, frameCol, 0.95);

                    float inset = sel ? 4.0 : 3.0;
                    float2 inMin = cMin + inset, inMax = cMax - inset;
                    bool inner = all(pos >= inMin) && all(pos <= inMax);
                    float3 hovBg = hov ? (dark ? float3(0.18, 0.18, 0.18) : float3(0.82, 0.82, 0.82))
                                       : (dark ? float3(0.10, 0.10, 0.10) : float3(0.90, 0.90, 0.90));
                    if (optMode == 0)
                    {
                        if (inner) base.rgb = kSwatch[i];
                    }
                    else if (optMode == 2)
                    {
                        // 画笔粗细滑条：左格 = 轨道 + 已选段 + 圆钮，右格 = 当前值（动态小纹理）
                        // 槽 43 = (当前值, 最小, 最大, 轨道内缩)；槽 40 = (数值格宽, 徽标宽, 徽标高, 0)
                        float3 cellBg = hov ? (dark ? float3(0.18, 0.18, 0.18) : float3(0.82, 0.82, 0.82))
                                            : (dark ? float3(0.10, 0.10, 0.10) : float3(0.90, 0.90, 0.90));
                        base.rgb = cellBg;
                        if (i == 0)
                        {
                            float inset = TextPos[43].w;
                            float v = TextPos[43].x;
                            float t = saturate((v - TextPos[43].y) / max(TextPos[43].z - TextPos[43].y, 1.0));
                            float tx0 = cMin.x + inset, tx1 = cMax.x - inset;
                            float ty = (cMin.y + cMax.y) * 0.5;
                            float trackH = max(4.0, cellH * 0.16);
                            float knobR = max(6.0, cellH * 0.28);
                            float knobX = tx0 + (tx1 - tx0) * t;
                            if (pos.x >= tx0 - 1.0 && pos.x <= tx1 + 1.0 && abs(pos.y - ty) <= trackH * 0.5)
                                base.rgb = (pos.x <= knobX)
                                    ? float3(0.27, 0.53, 1.0)
                                    : (dark ? float3(0.45, 0.45, 0.45) : float3(0.70, 0.70, 0.70));
                            float dk = length(pos - float2(knobX, ty));
                            base.rgb = lerp(base.rgb, dark ? float3(1.0, 1.0, 1.0) : float3(0.15, 0.15, 0.15),
                                            saturate(knobR - dk + 0.5));
                        }
                        else
                        {
                            // 当前值：与尺寸徽标同一张动态纹理（拖拽中逐帧改数，不能进图集）
                            // 槽 40.yz = 徽标像素尺寸：按原始大小居中贴。铺满整格会把字横向拉宽近一倍
                            float4 sub = SizeBadgeSub;
                            float2 bsz = rowW.yz;
                            if (sub.z > 0.0 && sub.w > 0.0 && bsz.x > 0.0 && bsz.y > 0.0)
                            {
                                float2 q0 = cMin + float2((cw - bsz.x) * 0.5, (cellH - bsz.y) * 0.5);
                                if (all(pos >= q0) && all(pos <= q0 + bsz))
                                {
                                    float2 atl = (pos - q0) / bsz * sub.zw + sub.xy;
                                    float4 glyph = BadgeTex.Sample(Samp, atl);
                                    base.rgb = lerp(base.rgb, fg, glyph.a);
                                }
                            }
                        }
                    }
                    else if (optMode == 3)
                    {
                        // 文字行：家族名 / 字号 / B / I
                        bool on = (i == 2 && bOn) || (i == 3 && iOn);
                        base.rgb = (sel || on) ? float3(0.27, 0.53, 1.0) : hovBg;
                        float4 sub = TextSubs[40 + i];
                        if (sub.z > 0.0 && sub.w > 0.0)
                        {
                            float2 atl = (pos - cMin) / float2(cw, cellH) * sub.zw + sub.xy;
                            float4 glyph = TextTex.Sample(Samp, atl);
                            base.rgb = lerp(base.rgb, (sel || on) ? float3(1.0, 1.0, 1.0) : fg, glyph.a);
                        }
                        // 家族/字号格右侧的小 ▼（提示"点开是列表"）
                        if (i < 2)
                        {
                            float2 mk = pos - float2(cMax.x - 16.0, cMin.y + cellH * 0.5 - 3.0);
                            if (mk.x >= 0.0 && mk.x <= 10.0 && mk.y >= 0.0 && mk.y <= 6.0
                                && abs(mk.x - 5.0) <= 5.0 * (1.0 - mk.y / 6.0))
                                base.rgb = fg;
                        }
                    }
                    else
                    {
                        base.rgb = sel ? float3(0.27, 0.53, 1.0) : hovBg;
                        float4 sub = TextSubs[38 + i];
                        if (sub.z > 0.0 && sub.w > 0.0)
                        {
                            float2 atl = (pos - cMin) / float2(cw, cellH) * sub.zw + sub.xy;
                            float4 glyph = TextTex.Sample(Samp, atl);
                            float3 fgm = sel ? float3(1.0, 1.0, 1.0) : fg;
                            base.rgb = lerp(base.rgb, fgm, glyph.a);
                        }
                    }
                    break;
                }
            }
        }
    }

    // ═══ 4.6 字体/字号下拉列表 ═══
    // 槽 41 = (面板 x, y, 宽, 行高)；槽 42 = (可见行数, 首行, 悬停项+1, 打开形态)；槽 43.x = 当前项。
    {
        float4 ddRect = TextPos[41];
        float4 ddSt   = TextPos[42];
        if (selComplete > 0.5 && ddRect.z > 0.0 && ddSt.x >= 0.5)
        {
            int vis  = (int)(ddSt.x + 0.5);
            int dtop = (int)(ddSt.y + 0.5);
            int dhov = (int)(ddSt.z + 0.5) - 1;
            int dsel = (int)(TextPos[43].x + 0.5);
            float panelH = vis * ddRect.w;
            if (pos.x >= ddRect.x - 1.0 && pos.x <= ddRect.x + ddRect.z + 1.0
                && pos.y >= ddRect.y - 1.0 && pos.y <= ddRect.y + panelH + 1.0)
            {
                base.rgb = dark ? float3(0.09, 0.09, 0.09) : float3(0.98, 0.98, 0.98);
                [unroll(12)]
                for (int r = 0; r < 12; r++)
                {
                    if (r >= vis) break;
                    float2 iMin = ddRect.xy + float2(0.0, r * ddRect.w);
                    float2 iMax = iMin + float2(ddRect.z, ddRect.w);
                    if (!(all(pos >= iMin) && all(pos <= iMax))) continue;
                    int absIdx = dtop + r;
                    bool selItem = (absIdx == dsel);
                    bool hovItem = (absIdx == dhov);
                    base.rgb = selItem ? float3(0.27, 0.53, 1.0)
                             : hovItem ? (dark ? float3(0.22, 0.22, 0.22) : float3(0.86, 0.86, 0.86))
                                       : (dark ? float3(0.11, 0.11, 0.11) : float3(0.97, 0.97, 0.97));
                    float4 sub = TextSubs[44 + r];
                    if (sub.z > 0.0 && sub.w > 0.0)
                    {
                        float2 atl = (pos - iMin) / float2(ddRect.z, ddRect.w) * sub.zw + sub.xy;
                        float4 glyph = TextTex.Sample(Samp, atl);
                        base.rgb = lerp(base.rgb, selItem ? float3(1.0, 1.0, 1.0) : fg, glyph.a);
                    }
                    break;
                }
                // 外框
                float2 pc = ddRect.xy + float2(ddRect.z, panelH) * 0.5;
                float sd = RoundRectSDF(pos, pc, float2(ddRect.z, panelH) * 0.5 - 1.0, 3.0);
                if (sd > -2.0 && sd <= 0.0)
                    base.rgb = dark ? float3(0.45, 0.45, 0.45) : float3(0.30, 0.30, 0.30);
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
            // ── 5. 文字（图集槽 19+slot 是【实测位图】, 贴到内框 = 外框减 Padding）──
            else if (type < 5.5)
            {
                int slot = (int)(a3.y + 0.5);
                float pad = max(a3.z, 0.0);
                float4 bg = AnnoLayers[i * 16 + 4]; // 文字背景色 (线性 RGBA, 与 CPU 合成同一份声明)
                // 背景：外框（含 Padding）
                if (a1.x <= ap.x && ap.x <= a1.z && a1.y <= ap.y && ap.y <= a1.w && bg.a > 0.001)
                    base.rgb = lerp(base.rgb, bg.rgb, bg.a * layerA);
                if (slot >= 0 && slot < 8)
                {
                    float4 sub = TextSubs[19 + slot];
                    float2 tmin = a1.xy + pad;
                    float2 tmax = a1.zw - pad;
                    if (sub.z > 0.0 && tmax.x > tmin.x && tmax.y > tmin.y)
                    {
                        // ⚠ 除数必须是【文字内框尺寸】：旧写法除以 a1.zw（=右上角坐标），
                        // 文字一旦不在 (0,0) 就被按"离原点多远"缩小 —— 离原点越远字越小、越糊。
                        float2 atl = (ap - tmin) / (tmax - tmin) * sub.zw + sub.xy;
                        if (all(ap >= tmin) && all(ap <= tmax))
                        {
                            float4 glyph = TextTex.Sample(Samp, atl);
                            base.rgb = lerp(base.rgb, a2.rgb, glyph.a * a2.a * layerA);
                        }
                    }
                }
            }
            // ── 6. 马赛克（3×3 块平均采样桌面）──
            else if (type < 6.5)
            {
                // ⚠ 这条分支以前没有边界判断：只要层存在，整块选区都被打成马赛克
                // （2026-10-28 实机"拖一小块，结果整个选区都花了"）。CPU 合成一直是按图层矩形做的，
                // 所以是预览独有的错 —— 判据"预览 = 输出"正是靠这里对齐。
                if (all(ap >= a1.xy) && all(ap <= a1.zw))
                {
                    float bs = max(a3.x, 2.0);
                    // 每块用本块内的九点均值（CPU 合成是整块逐通道的线性光均值、逐块硬边）。
                    // 旧实现的 3×3 邻块平均是在【邻块】上抹，预览被糊成一片，输出却是硬边方块。
                    float3 blk = MosaicBlockMean(floor(ap / bs) * bs, bs, selMin);
                    base.rgb = lerp(base.rgb, blk, layerA);
                }
            }
            // ── 7. 马赛克（画笔涂抹）：只糊笔迹半径内的方块（V4-15 = 折线段, a3.z = 涂抹直径）──
            else if (type < 7.5)
            {
                float bs = max(a3.x, 2.0);
                float rad = max(a3.z, 1.0) * 0.5;
                bool hit = false;
                [unroll(12)]
                for (int k = 0; k < 12; k++)
                {
                    float4 seg = AnnoLayers[i * 16 + 4 + k];
                    if (seg.z < -1000.0) break;
                    if (SegmentDist(ap, seg.xy, seg.zw) <= rad) { hit = true; break; }
                }
                if (hit)
                {
                    float3 blk = MosaicBlockMean(floor(ap / bs) * bs, bs, selMin);
                    base.rgb = lerp(base.rgb, blk, layerA);
                }
            }
        }
    }

    // ═══ 7. 输入中的文字（EDIT 子窗口在这块覆盖层上不可见，预览只能画进帧里）═══
    // 外观对齐提交后的文字层：半透明黑底 + 白字，位置就是输入框的位置。
    if (EditPos.z > 0.0 && EditPos.w > 0.0 && EditSub.z > 0.0)
    {
        float2 p0 = EditPos.xy - 6.0;
        float2 p1 = EditPos.xy + EditPos.zw + 6.0;
        if (all(pos >= p0) && all(pos <= p1))
        {
            base.rgb = lerp(base.rgb, float3(0.0, 0.0, 0.0), 0.6);
            if (all(pos >= EditPos.xy) && all(pos <= EditPos.xy + EditPos.zw))
            {
                float2 atl = (pos - EditPos.xy) / EditPos.zw * EditSub.zw + EditSub.xy;
                float4 glyph = EditTex.Sample(Samp, atl);
                base.rgb = lerp(base.rgb, float3(1.0, 1.0, 1.0), glyph.a);
            }
        }
    }

    return float4(base.rgb, 1.0);
}



