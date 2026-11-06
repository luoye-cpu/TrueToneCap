# GUI 质量选项生效性排查 · 交接（2026-11-06）

> 交接人：本次会话（0.3.11 上架 + 捕获优化 + 文档清理那一轮）
> 被交接人：下一轮接手者
> 状态：**排查进行中，未定位根因**。已确认的链路与反证全部记在下面，不要重复走。

---

## 0. 一句话状态

用户报"输出图片质量极其糟糕"，怀疑 GUI 质量选项没被真实输入。**链路代码与日志显示质量值确实进了
`EncodingSettings`（1.0 / 20.0 / 100.0 三个正数），但用户最近三次编码用的全是各格式默认值** ——
若用户当时确实调过滑块，那问题在"滑块 → 落点"这一段而不是"落点 → 编码器"。复现方式见 §3。

---

## 1. 已完成（本会话成果，均已入库并 push）

| 提交 | 内容 |
|---|---|
| `0d9d127` | 真图标接入：TTC.jpg 裁剪母版（1515×1521，圆角 20.8% 拟合）→ 48 个 MSIX 变体 + 多分辨率 ico；`gen-msix-assets.py` 新增 `--src/--ico` |
| `47392c1` | 翻译默认口径统一（README / TranslationService 残留的"默认关闭"旧口径） |
| `8fdf659` | 对抗审查 🟡 三处收口（F4 口径 / CPU 回退注释 / VRR 日志）+ `_toolbarHover` 硬化 |
| `6151a7c` | **捕获启动优化 727→666ms**：白点 120s TTL 缓存、拼接画布免零分配、`BgraToScrgbLinearInto` 直写 |
| `2043793` | README 清理（只写已实现：删 VS-Cap 假功能、英文导语口径、套件数字、Release Notes 补 0.3.7~0.3.11；取证移台账 §78） |
| `23cb5e8` | gitignore 忽略外部工具残留 `.arts/` |
| 更早 | `af817ee` v0.3.11 发布；`7cfc4e0`/`0fc4057`/`c966d51` 商店身份三件套 |

**外部状态**：v0.3.11 已上架 Microsoft Store（用户确认发布成功）。工作区已从 11.22 GB 清到 2.02 GB。

---

## 2. 进行中：质量排查（已确认事实 / 可疑点 / 未验证）

### 2.1 已确认正确的部分（不要再查）

**质量链路**：`QualitySld` → `OnQualityChanged`(MainWindow.xaml.cs:~1346) →
`_settings.SetQuality(CurrentFormat(), val)` → 管线 `CapturePipelineService.cs:294`
`Quality = (float)GetPerFormatQuality(s, format)` → `EncodingSettings.Quality` → 编码器。

- **映射表正确**：`AppSettingsQuality.GetQuality/SetQuality`（AppSettingsData.cs:496-526），
  PNG/JPEG_LI/JPEG_GAINMAP/JPEG_XL/AVIF/WebP/TIFF 七格齐全，`JPEG_GAINMAP` 共用 `QualityJpegLi`。
- **字段默认值合理**（AppSettingsData.cs:119-145）：
  `QualityPng=100`、`QualityJpegLi=1.0`(butteraugli 0.5-3.0)、`QualityJpegXl=1.0`、
  `QualityAvif=20`(CRF 0-63)、`QualityWebp=90`、`QualityTiff=100`。
- **程序性赋值有守卫**：`_qualityPanelUpdating`（xaml.cs:3417），区间钳制见 xaml.cs:876-878
  （`savedQ` 超界回落 `def`）。
- **JPEG LI 参数正确**：`JpegLiNative.cs:155` → `--distance {distArg}`（butteraugli，**不是** `-q`），
  `distance=1.0` = 官方视觉无损档；`--chroma` 由色度设置给（用户口径 JPEG LI 默认 420）。
- **日志实测反证**（`%LOCALAPPDATA%\TrueToneCap\log\app_2026-11-06.log`）：

  ```
  17:28:59 编码设置: JPEG_LI 质量=1.0  位深=8  色度=420
  17:29:22 编码设置: AVIF    质量=20.0 位深=10 色度=444 色域=BT2020
  17:30:54 编码设置: AVIF    质量=20.0 位深=10 色度=444
  ```

  三条 Pipeline 行与 MainWindow 的 `BuildEncodingSettings: 质量=` 行**数值一致**
  ⇒ "质量值没传下去"这个假设被日志否证。

### 2.2 高度可疑点（按优先级）

**A（最高）· 滑块值是第二事实源**
`MainWindow.xaml.cs:3404`：
```csharp
var settings = AppServices.Pipeline.BuildEncodingSettings(...);  // 管线已按 s.GetQuality(format) 取好
settings.Quality = ui.Quality;   // ← 无条件覆盖成"滑块当前值"
```
`ui.Quality` 来自 `CaptureEncodingUiState()`（xaml.cs:3361 `Quality: (float)QualitySld.Value`）。
滑块是**全局单值、随格式换区间**（PNG 锁 100 / JPEG LI 0.5-3.0 / AVIF 0-63 / WebP 50-100），
而每格式质量是**独立持久化字段**。二者不同步的时刻（切格式后立刻截图、粘贴/剪贴板另存等
不经 `UpdateQualityPanel` 的入口）就会把**另一个格式的量纲**灌进本次编码。
历史同型缺陷：`AvifChroma` vs `ChromaAvif` 双字段分裂（已修，判据在 CorePipelineTests）。
**验证方式**：见 §3 的第 2 步。

**B（高）· 用户三次编码全是默认值**
日志里 1.0 / 20.0 / 100.0 恰好是三个格式的默认值，**没有一个非默认值**。
若用户自称"调过质量"而日志无变化，则"滑块 → 字段 → 落点"在用户那条路径上断了。
**需要用户复现确认**（§3 第 1 步），不能凭日志推断用户是否真调过。

**C（中）· Effort 默认值集中在 `Smallest`**
`AppSettingsData.cs:175-193`：`EffortWebp/EffortJpegLi/EffortPng/EffortTiff = "Smallest"`。
⚠ 按 `CompressionEffort.cs:55` 的枚举注释，`Smallest` = "**体积优先**：把压缩参数拉满，编码最慢"，
但 `CompressionEffort.cs:64-67` 又写"用户给 PNG/TIFF/WebP/JPEG LI 定的菜单是两档 ——
「均衡」作默认（= Tag `Smallest` = 参数拉满）"。**两处注释对 `Smallest` 的"默认值"口径不一致**，
且"体积优先"这个档名与"默认档"混在一起。已确认的映射（`CompressionEffort.cs:192-208`）：
JPEG LI 的 `Smallest` 只是**加不加 `--xyb`**（色彩空间开关，不改质量旋钮，理论不影响质量）。
WebP 是 `-m`（方法档，不影响 `-q`）。**结论：Effort 理论上不毁质量，但这套命名与注释需要一次收口**
（若日后再有人把 `Smallest` 理解成"最低质量"，就会真写错）。

**D（待确定）· 420 色度的观感**
日志显示 JPEG_LI 走 `色度=420`。对**文字/UI 截图**，420 会让彩色小字与图标边缘发糊 ——
如果用户"糟糕"的观感来自这里，那是**已拍板的产品口径**（"JPEG LI 还是默认 420"），不是缺陷。
但需要用户确认他看的产物是 420 还是 444。

### 2.3 未验证（下一轮要做的）

- 用户调到**非默认**质量后，日志两行是否都跟着变（§3 第 1 步）。
- `ui.Quality` 覆盖后是否与 `ui.Settings.GetQuality(format)` 不等（§3 第 2 步，可加一条临时日志）。
- 各编码器**实际收到的命令行**（avifenc `-q/--crf`、cwebp `-q`、cjpegli `--distance`）——
  当前日志只到 `EncodingSettings`，没有命令行级取证。
- 粘贴/预览/剪贴板另存这几条**不走主管线**的入口是否也传对质量。

---

## 3. 下一步验证方案（照着做，每步都有明确判据）

**第 1 步 · 确认滑块是否真的落到字段（用户操作）**
1. 打开应用 → 选 AVIF → 把质量滑块从 20 拖到 **8**（明显不同的值）→ 截图保存。
2. 看日志两行：`编码设置: AVIF ... 质量=?` 与 `BuildEncodingSettings: 质量=?`。
   - **都是 8.0** ⇒ 落点正常，问题在编码器消费（转第 3 步）。
   - **仍是 20.0** ⇒ 滑块接线断了（查 `OnQualityChanged` 的 `format` 与 `CurrentFormat()` 是否同一格、
     以及 `_arc` 是否被 `_qualityPanelUpdating` 误挡）。
   - **8.0 但产物依旧差** ⇒ 问题在编码器参数（第 3 步查命令行）。

**第 2 步 · 证明/否证可疑点 A（改代码，5 分钟）**
在 `MainWindow.xaml.cs:3404` 后加一条临时对比日志（跑完删掉）：
```csharp
var fieldQ = ui.Settings.GetQuality(format);
if (Math.Abs(fieldQ - ui.Quality) > 1e-6)
    LogService.Warn("MainWindow", $"质量二源分叉: 滑块={ui.Quality:F2} 字段={fieldQ:F2} format={format}");
```
然后跑「切格式后立刻截图」「粘贴另存」两类入口。**出现这条 Warn 即坐实可疑点 A**。
修法（若坐实）：删掉 3404 的覆盖，让管线的 `GetPerFormatQuality` 作唯一来源；
或把 `ui.Quality` 改成 `ui.Settings.GetQuality(ui.Format)`（两者取其一，不许并存）。

**第 3 步 · 编码器命令行级取证**
在各 native 编码器的 `Arguments =` 附近加 Debug 日志（或临时 Info），确认：
- AVIF：`avifenc ... -q <CRF>` / `--crf`，值是否等于 `20`（或用户设的值）
- WebP：`cwebp -q <90>`
- JPEG LI：`cjpegli --distance <1.0> --chroma <420|444>`
判据：**命令行里的数字必须逐字等于日志里的 `质量=`**。不等 ⇒ 编码器映射错（查该编码器的
`Quality → 参数` 换算，尤其"越小越好"与"越大越好"两个方向）。

---

## 4. 待办清单（含本会话遗留）

| # | 事项 | 状态 |
|---|---|---|
| 1 | **质量排查 §3 三步** | 未开始（本文档主体） |
| 2 | `CompressionEffort` 的 `Smallest` 命名/注释收口（"体积优先" vs "默认档"两处口径打架） | 待办（可疑点 C） |
| 3 | 商店包体积：`Publish.ps1 -Msix -Trim` 实测（预期 180 → ~135 MB，需实机启动回归） | 待办（用户已知，未拍板） |
| 4 | OCR 模型按需下载（66.4MB → 可省 ~40% 包体积） | 待办（用户已知，未拍板） |
| 5 | 质量链路补一条端到端判据（"滑块非默认值必须抵达编码器"），目前只有落点判据 | 待办 |
| 6 | 用户截图未传完 → Store 一览截图（用户侧） | 用户侧 |

---

## 5. 关键索引

**文件**
- `src\TrueToneCap.App\MainWindow.xaml.cs`：`OnQualityChanged`(~1346)、`UpdateQualityPanelCore`(660)、
  `CaptureEncodingUiState`(3355)、`BuildEncodingSettings(UI)`(3396)、`CurrentFormat()`(3257)
- `src\TrueToneCap.App\Services\CapturePipelineService.cs`：`BuildEncodingSettings`(≈230-320)、
  `GetPerFormatQuality`(594)
- `src\TrueToneCap.App\Models\AppSettingsData.cs`：质量字段(119-145)、`AppSettingsQuality`(496)
- `src\TrueToneCap.Core\Encoding\JpegLiNative.cs`：`--distance` 构造(155/313)
- `src\TrueToneCap.Core\Encoding\CompressionEffort.cs`：档位枚举与映射(52-208)

**命令**
```powershell
dotnet run --project src\TrueToneCap.Test -- --unit-tests     # 1974 通过 / 2 跳过
dotnet run --project src\TrueToneCap.Test -- --color-tests    # 290
# 质量相关判据：Test_EncodingSettings_Defaults / Test_Settings_ComboFallbacksMatchDefaults
```

**日志**
`%LOCALAPPDATA%\TrueToneCap\log\app_<日期>.log`（商店版）或 `publish\TrueToneCap-v0.3.11\log\`（便携版）。
关键两行：`[系统/Pipeline] 编码设置: ... 质量=` 与 `[系统/MainWindow] BuildEncodingSettings: 质量=`。

**陷阱（本会话踩过的）**
- **`git checkout -- <file>` 会连带毁掉未提交的改动** —— 验证用的文件恢复一律走 `.bak` 拷贝。
- 改 Core 后跑测试必须 `--no-incremental`，否则吃到旧 DLL 会出假红。
- pre-push 门禁的发布回归**要求 TrueToneCap.exe 未运行**，否则 push 被拦。
- 探针（`--capture-freshness-probe` 等）被单实例机制挡：跑之前必须关掉常驻实例。

---

## 6. 给接手者的一句话

先做 **§3 第 1 步**（用户调一个非默认值 + 看两行日志），它是判断"接线断"还是"编码器错"的分水岭；
在拿到这个读数之前，**不要改代码** —— 现有证据只支持"可疑点 A"的假设，尚未坐实。
