# 色彩声明 CICP 优先收口（2026-11-12）

> 一句话（2026-11-12 用户口径「优先写入 CICP」）：凡容器有码点通道，代码点可声明的空间
> **一律只写码点、不再嵌 ICC**；ICC 收缩为码点盲空间（AdobeRGB）与无码点容器（JPEG 家族 /
> WebP / TIFF）的通道。前提：本产品色彩方面只有 Rec.709 与 Rec.2020 相关（用户原话），
> DisplayP3 / DCI-P3 是跟随显示器可能出现的中间标签，同判。

## 1. 判据与实现

单一判据 `FormatHelper.CicpDeclarable(colorSpaceTag)`：`!= "AdobeRGB"` 即码点可声明
（H.273 Table 2 只有 AdobeRGB 没有自己的 primaries 码；硬写 9 会被采信码点的查看器
当成 BT.2020 解释——这条防线 2026-09-19 缺陷 B 就立过，本次只是把"不写码点"延伸成
"也不再嵌同义的 ICC"）。

| 出口 | 码点通道 | 改动 |
|---|---|---|
| PNG（含保底/动图 APNG） | cICP | `GetColorMetadata` 收口：`icc = !isSRgb && !CicpDeclarable(tag) && IsEmbeddableIcc(...)` ⇒ sRGB/P3/2020 只写 cICP，AdobeRGB 保留 iCCP |
| AVIF（软件 stdin16 / NVENC prof 盒） | colr(nclx) | 两处 NVENC 门 + stdin16 门改 `CicpDeclarable` ⇒ ICC/prof 只剩 AdobeRGB |
| AVIF 增益图底图 | colr(nclx) | `embedIcc = null` 恒定（底图只有 sRGB/2020 两种落点，nclx 都完整声明；旧形态广色域恒嵌 2.9KB 标准件） |
| JPEG XL SDR | color fields | 编码器与调用门双向收口：`IsEmbeddableIcc && !CicpDeclarable` 才交 icc_pathname ⇒ sRGB/P3/2020 走枚举；AdobeRGB（浮点入口已收口成 2020；字节入口 ICC 原样保留） |
| JPEG XL HDR | Rec2100PQ / ICC | **不变**：P3+PQ 是码点盲区（Rec2100PQ 硬编码 2020 原色）⇒ PQ-TRC ICC 照旧 |
| JPEG 家族 / WebP / TIFF | 无 | **不变**（ICC 是唯一通道，"只有非 sRGB 才嵌"的 2026-11-12 早间口径继续成立） |

动图（AnimationRecorder）按**容器能力**分派，不是一刀切：
- APNG 有 cICP ⇒ 与单帧 PNG 同判（`icc = CicpDeclarable ? null : StandardIccFor`）；
- WebP 无码点通道 ⇒ `webpIcc = 非 sRGB ? StandardIccFor : null`——BT2020/P3 全靠 ICC，
  砍了就静默错标成 sRGB；sRGB 不嵌（与单帧 WebP :748 门同口径）；
- AVIF/GIF：nclx flags / 无通道，行为不变。

## 2. 门禁同步（本次反转的判据面）

- `ColorPipelineTests.EncodedPng_IccpChunk_Valid` → 更名 `EncodedPng_Iccp_CicpFirst`
  （P3 + 真 ICC 夹具 ⇒ cICP [12,13,0,1] 在、iCCP 不在）。
- `ColorPipelineTests` 增益图 [BT2020] acsp 断言反转（恒嵌→恒不嵌）；[sRGB] 那条本来就是"缺席"，
  两档从此同判据。
- `IccProvenance_NoThirdPartyCopyrightBytes` 端到端出口 P3 → AdobeRGB（ICC 通道的新唯一 PNG 出口）。
- `UsabilityTests`：`SdrEncode_IccEmbedding` PNG 腿反转；`FormatHelper_GetColorMetadata_AllTags`
  DisplayP3 keepIcc→false；`Pipeline_ColorSpace_WithIcc_NonSrgbTarget` 重写（P3 丢 ICC + AdobeRGB 原样带回）。
- `CorePipelineTests` 形态钉：增益图 `embedIcc = null` 钉（替换 `widePixels ? baseIcc : null` 钉）；
  JXL icc_pathname 计数=2 钉改述（"只在码点盲分支"）；动图分派钉 `webpIcc`；
  avifenc 结构钉改述（机制保留的真实使用者只剩 AdobeRGB，防"零声明"回归）。
- 文案三处级联（Core 句 → MainWindow 镜像 → LocaleManager 译文，同步判据钉）：PNG/AVIF/JXL
  广色域句改为"只写码点"；JPEG 增益图"恒嵌一份 ICC"句（2026-11-12 早间已过期）改为
  "ICC 跟目标走"。

## 3. 最终契约（2026-11-12 用户确认：产品只输出两档）

用户目标只有两档 —— **广色域 ⇒ Rec.2020、sRGB ⇒ Rec.709**。全量实现后每条出口
对这两档的产物形态（CICP 优先后）：

| 容器 | Rec.709（tag=sRGB） | Rec.2020（tag=BT2020，SDR / HDR） |
|---|---|---|
| PNG | cICP `1/13/0/1`，无 iCCP | cICP `9/13/0/1`（SDR）/ `9/16/0/1`（PQ），无 iCCP |
| AVIF 软件 | `--cicp 1/13/1`，无 --icc | `--cicp 9/13/9`（SDR）/ `9/16/9`（PQ），无 --icc |
| AVIF NVENC | nclx `1/13/?`，无 prof 盒 | nclx `9/13/?`（SDR）/ `9/16/?`（PQ），无 prof 盒（matrix 槽由后端定：NVENC=下发给驱动的值、MFT/QSV=601，primaries/transfer 如列） |
| AVIF 增益图底图 | nclx `1/13/1`，无 ICC | nclx `9/13/9`，无 ICC（替代图 `9/16/9`） |
| JPEG XL SDR | color fields `sRGB` | color fields `RGB_D65_202_Rel_SRG` |
| JPEG XL HDR | —（无 HDR 交付） | color fields `Rec2100PQ` + intensity_target |
| JPEG 家族（JPEG LI / 增益图） | 什么都不写（JFIF 隐含默认） | APP2 ICC（容器无码点通道，ICC 是唯一声明） |
| WebP | 什么都不写（实测对照组无 ICCP） | ICCP（容器无码点通道） |
| TIFF | 标准 sRGB ICC | 标准 2020 ICC（容器无码点通道） |
| 动图 APNG | cICP `1/13/0/1`，无 iCCP | cICP `9/13/0/1`，无 iCCP |
| 动图 WebP | 无声明（隐含 sRGB） | ICCP（容器无码点通道） |

三个无码点容器（JPEG 家族 / WebP / TIFF）的 Rec.2020 声明本来就只有 ICC 一条路，
属于"ICC 的正当保留地"，不在 CICP 优先的收口范围内。

## 4. AdobeRGB 分支的定位（2026-11-12 用户确认后补记）

用户确认：**软件并不输出 AdobeRGB**——UI 可选面已被 `CollapseSelectableTag` 全量收口成
sRGB/BT2020，老配置的 Adobe 档也已被 legacyTags 迁移。实现里保留的 `!CicpDeclarable`
（== AdobeRGB）分支**不是功能，是旁路防御**：只服务绕过 UI 的调用（判据样张宿主直赋
`ColorSpaceTag`、编码器直调），与仓库既有的 7 处 AdobeRGB 防御点同源同向——H.273 无其
primaries 码，硬写 9 会被当成 Rec.2020 错标 ⇒ 该标签来一次就 fail-closed 一次（只嵌 ICC、
不冒充码点）。删掉它换不来任何真实产物变化，只会把"旁路标签来了"从"诚实嵌 ICC"变成
"零声明错件"。本节记录后，后续维护者不要再把它误读成一条 AdobeRGB 输出路径。

## 5. 外部裁判同步（verify 首跑抓到的两处旧口径）

- `scripts/precision/icc_readback_matrix.py`：广色域样张"读不到 ICC"的预期按**容器**分派——
  PNG/AVIF 有码点通道 ⇒ 无 ICC 是预期形态（码点本体由 cicp_consistency.py 判）；
  JPEG 家族/TIFF/WebP 无码点通道 ⇒ 照旧是缺陷。ICC 内容层判据（结构/不变量/外部读回/
  跨格式同源）收缩到无码点容器，⑥ 反空转仍由 TIFF/WebP/JPEG 的 ICC 覆盖。
  附：`FormatBench.cs` 的 cs_*_adobe 注释勘误——样张 tag 在 2026-09-27 就被
  MapAdobeTargetToWide 收口成 BT2020，旧注释"靠 ICC 声明"描述的是收口前的世界。
- `scripts/probes/icc_selfbuilt_referee.py`（lcms2 裁判）：输入源换血——PNG 里仍嵌自产
  ICC 的只剩码点盲空间 AdobeRGB（provenance_adobe.png，反证偏差实测 35 > P3 时代）；
  自产 sRGB profile 换从 TIFF 夹具读（无码点容器，sRGB 也嵌，Tag 34675 按公开布局解析）。
  lcms2 读回、字节序反证、cprt 合规三条判据全部保留，仅换宿主容器。

## 6. 兼容性边界

- 读取端：cICP/nclx/color fields 与 iCCP 语义等价（同一套原色）；主流查看器两者都认，
  cICP/nclx 反而是更新、更紧凑的通道（PNG 3.0 / RFC 9327 推荐 cICP 给新解码器）。
- 老解码器只认 iCCP 的场景：按用户口径接受——两通道同义，宁可省体积；
  真正需要 ICC 精确表达的空间（AdobeRGB）仍保留 iCCP。
- `BakeIccToTarget`（像素烘焙目标侧）契约不变——那管的是"像素搬不搬"，本批管的是"声明写几份"。
