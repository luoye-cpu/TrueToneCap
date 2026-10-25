# 二维码识别功能 —— 设计与安全实现记录

> 日期：2026-10-26
> 需求（维护者原话）：「在截图完成后的框选区域上可以有一个识别二维码，提取二维码信息功能，
> 但是我需要做好防护，**防止恶意软件借助这个功能注入**」
> 方法：选型实测（NuGet 包 API 反射探查）+ 解码往返测试（47 条断言全绿）+ 全链路安全审计。

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| 二维码怎么识别？ | 选中区像素（合成后）→ Core `QrDecodeService`（ZXing.Net 0.16.11，纯托管）→ 多码解码 + 安全分类 |
| 怎么防注入？ | **解码与执行彻底分离**：解码层（Core）没有任何打开/执行/联网代码路径；唯一的"打开"动作在 App 结果窗，白名单只放 `http/https`，且 UI 判据之外点击时再复核一遍 |
| 日志会泄露吗？ | 不会。全部日志只记"尺寸/数量/动作"，与 OCR 不落原文同一纪律（PRIVACY.md 已同步） |
| 会联网吗？ | 不会。解码纯本机；不预取链接（不解析 DNS、不发 HEAD）；浏览器只在用户显式点击时启动 |
| 验证 | 新套件 `--qr-tests` 47 条全绿（往返/多码/反色/旋转/分类/转义/守卫）；App 构建 0 警告 0 错误 |

---

## 1. 功能形态

选区完成后的工具条新增第 7 枚按钮「二维码」（`SelectionOverlay` 主操作工具栏，
位于「翻译」与「取消」之间）。点击 → `Finish(ActionResult.Qr)` → MainWindow 分派
`CaptureAndQrFromPixelsAsync` → 新窗口 `QrResultWindow`。

- **像素来源**：与识字/翻译同一条纪律 —— 吃**合成后**的像素（`AnnotatedRegionPixels` 优先，
  回落 `ExtractRegionFromDesktop`）。用户用马赛克盖住的二维码**解不出来**，这是特性不是缺陷。
- **结果窗**：左侧截图区域回看（WriteableBitmap 同步渲染，同 OCR 预览窗模式），
  右侧逐条载荷卡片。窗口登记进 `_activeQrWindow`，应用退出时随全家一起强关。

## 2. 依赖选型

| 候选 | 结论 |
|---|---|
| **ZXing.Net 0.16.11**（NuGet `ZXing.Net`） | ✅ 采用。纯托管单包（netstandard2.0–net9.0 全 TFM）、无绑定附加包、许可 Apache-2.0 |
| Windows `BarcodeScanner` | ❌ 面向相机设备枚举，对静态像素缓冲不顺手，且需要额外能力声明 |
| 自研解码器 | ❌ QR 规范（ISO/IEC 18004）工作量大且易错，安全功能更不能用自研解码器兜底 |

引入位置：`Directory.Packages.props`（CPM 单一事实源）+ `TrueToneCap.Core.csproj`；
许可治理走既有三件套：`licenses/zxingnet-Apache-2.0.txt`（上游 COPYING 原文 + 署名头，
SHA256 入账）、`licenses/INDEX.json`（runtime 桶第 19 条）、`THIRD-PARTY-NOTICES.md`
（脚本重生成，`--check` PASS）。

**实测 API 探查**（PowerShell 反射，不靠文档）：

| 类 | 用法 | 备注 |
|---|---|---|
| `ZXing.RGBLuminanceSource` | `new(bytes, w, h, BitmapFormat.BGRA32)` | 直接吃截图管线的 BGRA32 缓冲，无格式转换 |
| `ZXing.Multi.QrCode.QRCodeMultiReader` | `decodeMultiple(bitmap, hints)` | 专用多码检测器（MultiFinderPatternFinder） |
| `ZXing.InvertedLuminanceSource` | 反相 pass | ⚠ `DecodeHintType.ALSO_INVERTED` 由高层 `BarcodeReader` 包装器实现，**直接调 reader 时无人消费**（实测 0 结果）——必须自己包一层反相 |

上述路径零反射（`MultiFormatReader` 家族在构造器里直接 new 各 reader），裁剪安全。

## 3. 安全模型（本功能的全部执行面）

### 3.1 威胁模型

二维码是**攻击者可以物理投递的载体**（打印张贴、网页、聊天图片让人截图）。
恶意 payload 的经典路径：

| 攻击 | 对应防护 |
|---|---|
| Follina 类诊断协议（`ms-msdt:`/`search-ms:` 拉起 Word+OLE） | `DangerousSchemes` 名单红牌 + 永无打开按钮（§3.2） |
| `file:`/`shell:`/`vbscript:`/`javascript:`/`data:` | 同上 |
| CRLF 注进 URL 再交给 shell（参数注入） | 含控制字符 ⇒ `CanOpenInBrowser=false`；且 `Uri.TryCreate(Absolute)` 对非法 host 直接拒绝 |
| **双向控制符视觉伪装**（U+202E 把 `evil.exe.moc.licnavi` 显示成 `navigator.com.exe`） | 展示层绝不渲染 Raw —— `DisplayText` 把所有 C0/C1、双向符、零宽字符转成 `\uXXXX` 可见转义 + 旗标警告 |
| 假冒域名（`https://expected.com@evil.com`） | 属浏览器地址栏职责，不越界处理；白名单机制不因之削弱 |
| "顺手预取"把用户截图里的 URL 泄给第三方（DNS/HTTP 探活） | **无任何网络代码**。打开动作只传 `uri.AbsoluteUri` 给系统默认浏览器 |
| 结果窗里 payload 串进日志 | 全部 `LogService` 调用只写尺寸/数量/动作（源码审计 + 测试哨兵） |

### 3.2 分层防线（为什么是三层）

```
解码层 Core.QrDecodeService          ── 只解码 + 分类，没有打开/执行/联网代码路径
    │   分类事实源 QrPayloadKind：UrlHttp / UrlOtherScheme / Text / DangerousScheme
    │   CanOpenInBrowser = UrlHttp 且无隐藏字符/双向符
    ▼
展示层 QrResultWindow                ── 只渲染 DisplayText（转义版），Raw 只进剪贴板
    │   打开按钮仅对 CanOpenInBrowser==true 的载荷生成（危险协议根本没有按钮）
    ▼
动作层 OnOpenLinkClick               ── 点击时以代码再验一遍（不信任 UI 判据）
    Uri.TryCreate(Absolute) → scheme 严格 in {http, https} → Host 非空 → 才 Process.Start
```

- **白名单制而非黑名单制**："能打开"只看 `Kind == UrlHttp`；`DangerousSchemes` 名单
  只决定红牌强度，名单漏了新危险 scheme 也只是少个红牌，**不破坏防线**。
- **分类器刻意写窄**：scheme 必须从位置 0 起、ASCII 字母开头、`[a-zA-Z0-9+-.]*:`。
  `" https://evil"`（前导空格）、`"https ://x"`、`"//evil"` 都不算 scheme ⇒ 归 `Text`，
  宁可少识别也不误放行。
- **复制给 Raw、展示用转义**：两者刻意分开 —— 剪贴板里是逐字原文（可用性），
  屏幕上是转义文本（防伪装）。每张卡片明示这一差别。

### 3.3 日志纪律（与 OCR 同一条）

| 位置 | 写什么 | 不写什么 |
|---|---|---|
| `MainWindow.CaptureAndQrFromPixelsAsync` | `{w}x{h}`、`{count} 个` | payload 任何部分 |
| `QrResultWindow.OnOpenLinkClick` | 动作 + "白名单已复核" | URL（连 host 都不写） |
| `QrResultWindow.OnCopyClick` | 动作 | payload |

另设**测试哨兵**（`Test_ToStringNeverLeaksPayload`）：`QrPayload`/`QrScanReport` 若被改成
`record`，默认 `ToString()` 会把 `Raw` 带出去 —— 任何日志里一句 `$"{payload}"` 就泄露内容。
哨兵断言两者 `ToString()` 不携带样例 payload，当场翻车。

## 4. 实现清单

| 文件 | 内容 |
|---|---|
| `Directory.Packages.props` | `ZXing.Net 0.16.11`（CPM 注明选型理由与安全边界） |
| `src/TrueToneCap.Core/Services/QrDecodeService.cs` | 解码 + 分类 + 展示转义 + `Rotate90Cw`；无状态、异常吞成空报告 |
| `src/TrueToneCap.App/SelectionOverlay.xaml(+.cs)` | 第 7 枚按钮 + `ActionResult.Qr` + `ToolbarFloorDip 932→1084` |
| `src/TrueToneCap.App/MainWindow.xaml.cs` | `case Qr` 分派 + `CaptureAndQrFromPixelsAsync` + `OpenQrResultWindow` + 退出强关 |
| `src/TrueToneCap.App/QrResultWindow.xaml(+.cs)` | 结果窗（§3.2 的展示/动作两层） |
| `src/TrueToneCap.App/Services/LocaleManager.cs` | ZhToEn 表 +21 条（按钮/提示/窗口/警告/动作） |
| `src/TrueToneCap.Test/QrDecodeTests.cs` | 新套件 `--qr-tests`，47 条断言 |
| `src/TrueToneCap.Test/Program.cs`、`scripts/verify.ps1` | 套件注册（8 headless，`--all` 9 个） |
| `licenses/zxingnet-Apache-2.0.txt`、`licenses/INDEX.json`、`THIRD-PARTY-NOTICES.md` | 许可三件套 |
| `PRIVACY.md`、`README.md` | 功能与隐私口径同步（中英双语） |

**本地化注意**：本仓门禁 `Test_Xaml_HardcodedChineseIsLocalized` 要求 XAML 中文与
ZhToEn 键逐字相等，且**键值不许含反斜杠** —— 首版警告文案里的 `\\u202E` 当场被门禁
语义顶回，改为"反向显示类"措辞（转义本身由代码渲染，不靠文案复述）。

## 5. 解码器行为细节（实测）

| 现象 | 处理 |
|---|---|
| CJK 解码乱成 `??` | 生成端不给 `CHARACTER_SET` 就按 ISO-8859-1 编码、不可编码字符**就地替换**；解码端默认同样 ISO-8859-1。两侧按行业惯例都取 UTF-8（带 ECI 段的码仍以 ECI 为准） |
| 白码黑底 | 显式 `InvertedLuminanceSource` 反相 pass（`ALSO_INVERTED` hint 直调 reader 无效） |
| 旋转 | FinderPattern 检测对 0/90/180/270 等价，另加转 90° 兜底 pass（实测覆盖侧贴码） |
| 多码重复上报 | `decodeMultiple` 在相邻切片可能重复报同一码 → 服务按 Raw 精确去重 |
| 病态输入（尺寸 0/缓冲不足/纯白图） | 一律空报告不抛异常 —— 解码是旁路功能，不值得让截图流程炸 |

## 6. 已知边界（诚实记录）

1. **单 QR 的"位置信息"未用**：结果窗不做"框选哪条对应图上哪个码"的高亮定位
   （`Result.ResultPoints` 有坐标，UI 留作后续增量）。
2. **`UrlOtherScheme` 不做细分**：`WIFI:`/`mailto:` 等只展示不解析（WIFI 密码以明文出现
   在展示文本里 —— 它本来就是码的内容，且转义可见）。
3. **反相 + 旋转 = 最多 3 次解码尝试**：极端大图（4K 区域）理论上最坏 ~3× 单次耗时；
   实测合成图毫秒级，与 OCR 同量级，未单列优化。
4. **商店素材**：新按钮需要补一张带「二维码」按钮的界面截图（与待办里的商店截图合并处理）。

## 7. 复现

```powershell
dotnet run --project src/TrueToneCap.Test -- --qr-tests   # 47 条断言
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64   # 0 警告 0 错误
py scripts/gen-third-party-notices.py --check                  # 许可台账同步
```
