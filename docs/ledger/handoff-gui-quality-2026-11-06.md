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
---

## 7. 2026-11-06 深夜更正（用户拍板，口径执行员执行；本节只追加，§0-§6 历史正文不改，冲突处以本节为准）

### 7.1 JPEG LI 默认口径变更（色度 420→444、质量 d 1.0→0.5）

- **色度默认 420 → 444**：原口径"JPEG LI 还是默认 420"（§2.2 可疑点 D 与管线注释引用的那条"已拍板"）**作废**。
- **质量默认 1.0 → 0.5**（butteraugli distance，视觉无损最稳档）：原"d=1.0 即视觉无损默认档"的取值依据对截图内容不成立。
- 物证：`docs\ledger\quality-chain-evidence-2026-11-06.md` §JPEG LI —— 420 色度下 d=1.0 仍有**重度块效应（1.73/1.77）**+ **色度高频损失 93%**。这两条正是拍板动机。
- 已同步的全部联动点（文件:行号，改后坐标）：
  - `src\TrueToneCap.App\Models\AppSettingsData.cs:137`：`QualityJpegLi = 0.5`（取值依据注释块 138-149 重写）；
    `:352`：`ChromaJpegLi = "444"`（口径注释 339-351，含"三处同源"纪律与 GAINMAP 分家声明）。
  - `src\TrueToneCap.Core\Encoding\FormatEncoders.cs:119`：`JpegLiEncoder.GetQualityRange()` def **0.5f**
    （与字段默认成对，`Test_Settings_QualityDefaultsSingleSource` 对撞钉住）——UI 滑块区间回落档（MainWindow.xaml.cs:876-878 的 `def`）与初值都从这里动态来，无硬编码残留。
  - `src\TrueToneCap.App\MainWindow.xaml:161`：`ChromaJpegLiCbo` 的 `IsSelected` 从 420 项移到 **444 项**。
  - `src\TrueToneCap.App\MainWindow.Settings.cs:423`：SaveSettings 兜底 `?? "444"`（`Test_Settings_ComboFallbacksMatchDefaults` 强制兜底↔默认一致）。
  - `src\TrueToneCap.App\Services\SettingsService.cs:773`：JPEG LI 非法色度回落 `FixChroma(…, fallback: "444")`（Normalize 路径；:785-788 注释同步）。
  - `src\TrueToneCap.App\Services\SelfTest.cs:1364`：越界色度回落期望 420→**444**；`:1511`：GAINMAP_MIGRATION 夹具换用
    2026-09-24 起差集序列化窗口的真实老文件形态（缺键=当时没动过），"搬 GainMap 侧偏好"的判据路径保留。
  - `src\TrueToneCap.Core\Encoding\JpegLiNative.cs:114-116` / `CapturePipelineService.cs:256-260` / `SelfTest.cs:318-325`：过期口径注释同步（代码零改动）。
  - `src\TrueToneCap.Test\CorePipelineTests.cs`：`Test_GainMap_ChromaIsRealSetting` 两断言改 444（:11159 / :11167）；同测试与其余 QualityChain 判据注释同步（:10137 / :10258 / :11085-11093 / :11113-11121）。
- **JPEG_GAINMAP 共用关系结论**：质量侧**共用** `QualityJpegLi`（`GetQuality(JPEG_GAINMAP)` 同格）⇒ 默认 0.5 对增益图态一次改动双生效；色度侧**不共用**（管线对 JPEG_GAINMAP 硬编码 "444"，CapturePipelineService.cs:266，2026-11-04 分家口径不变）⇒ 色度改动只落在 JPEG LI 普通档，`ChromaGainMap`/`QualityGainMap` 两个遗留字段刻意保持旧默认（迁移拿"是否等于默认"判用户改没改过，漂了会误搬）。
- **设置迁移影响**（迁移逻辑零改动）：差集序列化下，现有用户文件缺键 = 新默认（0.5/444）自动生效，正是本次拍板意图；显式保存过非默认值的用户不受影响。已知近似：`MigrateGainMapMerge` 用**当前**默认（`d.QualityJpegLi`/`d.ChromaJpegLi`）近似"当时默认"，对 2026-09-24 前整份写回、钉着旧值 1.0/420 的老文件会把"没动过"误判成"JPEG LI 侧改过"⇒ 不搬 GainMap 侧偏好（人群极小：需 2026-09-26 上线后从未再保存过；自检夹具已换差集窗口形态保留"搬"路径判据）。
- **验证**：`dotnet run --project src\TrueToneCap.Test -- --unit-tests --no-incremental` ⇒ **2043 通过 / 0 失败 / 2 跳过**，与基线（2043/2）一致，不红。

### 7.2 "假 HDR"判定撤回

- 此前按"PQ 538 ↔ 120nit"读数怀疑产物 HDR 标注异常的判定**撤回**：该换算与用户的 SDR 白设定自洽（SDR 白不是标称 80nit 时，UI/管线按系统白点换算，两个读数的比值正是该设定的产物），不构成"假 HDR"。
- clli **417nit 是上界声明**（MaxCLL 的"不超过"语义），不是实测亮度异常，不能作为"假 HDR"证据。
- 本档 **§2.2 可疑点 D 的强表述一并撤回**："那是已拍板的产品口径、不是缺陷"不再成立 —— 420 的观感危害是真的（物证同 7.1），且口径已按 7.1 更正。
- 保留为**待查线索**（本轮不展开）：**PNG 与 AVIF 两路 HDR 处理不一致**。
### 7.3 PNG/AVIF 两路 HDR 处理不一致 —— 用户裁决：设计而非缺陷（2026-11-06 深夜，遗留项②关闭）

- 物证回顾：PNG 路径日志称 PQ 10-bit、实产 8-bit SDR（cICP transfer=13，三重证据一致）；AVIF 路径对同一 HDR 源保留 PQ 10-bit。
- 用户裁决：**两条路采用不同的输出路线是设计口径**——PNG 走 tone-map 到 SDR 的路线，AVIF 保留 PQ —— 不是缺陷，不做对齐改造。
- 因此 7.2 末尾保留的"待查线索"就此关闭；AVIF colr box 19 字节非标写法（低危观察项）维持记录不动。

### 7.4 遗留项③（商店包体积实测 / OCR 模型按需下载）—— 用户裁决：不追查（2026-11-06 深夜，遗留项③关闭）

- §4 待办 #3（Publish.ps1 -Msix -Trim 包体积实测）与 #4（OCR 模型按需下载）维持待办状态，本轮明确不投入。

### 7.5 本轮处置收束

- 全部改动分两笔提交入库（fix+口径 / docs 台账），连同上一轮交接提交（f529306）一并 push；`artifacts\quality-probe\` 探针按 `artifacts/` 既有忽略规则留在本机不入库；`.resultverify/` 验证流程落盘加入忽略。
- 滑块接线最终结论（承接 `quality-slider-wiring-2026-11-06.md`）：接线 7 环闭合，"失效"体验根源为旧版单字段质量无迁移；新版滑块拖动→字段→编码链路完整，且新默认（0.5/444）即为本轮拍板口径。
