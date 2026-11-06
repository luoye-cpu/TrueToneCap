# 质量链路证据报告 · 2026-11-06

> 调查人：质量链路实证调查员（只读审计 + 探针实证；未修改 src\ 下任何文件，未 commit/push）
> 交接文档：`docs\ledger\handoff-gui-quality-2026-11-06.md`（§2.1 已确认正确的部分不再重复：映射表、字段默认值、OnQualityChanged 守卫、JPEG LI `--distance` 参数）
> 探针项目：`artifacts\quality-probe\QualityProbe\`（保留未删），产物与命令行证据在 `artifacts\quality-probe\out\`

---

## 0. 三个裁决（先给结论）

| # | 裁决 | 结论 | 一句话依据 |
|---|---|---|---|
| ① | **编码器段质量是否生效** | ✅ **生效（坐实）** | 三个编码器在差异质量值下产物字节比 1.54x~2.12x（判据 ≥1.5 全过）；JPEG LI 命令行逐字取证 `--distance` 与传入值一致。质量值只要以正确量纲到达编码器，就真实改变产物 |
| ② | **可疑点 A（二源覆盖）** | ✅ **坐实，但用户案例另有主嫌** | `MainWindow.xaml.cs:3404` 的 `settings.Quality = ui.Quality` 是第二事实源，全部 7 个编码入口经它；常规时刻冗余无害，**贴图窗 forceFormat 路径（xaml.cs:2988/2989）量纲错位可达**。但用户日志三行全是默认值 ⇒ 本次"糟糕"案例更可能来自 `Quality:1` 死键（用户质量偏好从未迁移，裁决③）或 420 色度观感（交接可疑点 D） |
| ③ | **遗留 Quality 字段是否参与编码** | ❌ **不参与（死键）** | 读取点仅 `AppSettingsQuality.GetQuality/SetQuality` 的 default 分支（AppSettingsData.cs:508/523），当前 7 格枚举全被显式 case 覆盖 ⇒ 不可达；无迁移、无钳制；用户文件实证见 §4.3 |

---

## 1. 任务 A · 编码器消费段端到端实证

### 1.1 静态确认：Quality → 命令行参数换算（读代码）

| 编码器 | 包装类:行号 | 换算规则 | 方向 |
|---|---|---|---|
| JPEG LI | `FormatEncoders.cs:158` → `JpegLiNative.cs:117-118,155` | `s.Quality` **直接**作 butteraugli distance；`Math.Clamp(d, 0.0f, 25.0f)` 后 `{d:F4}` → `--distance X.XXXX` | 越小越好（0.5≈无损，25=最差） |
| AVIF | `FormatEncoders.cs:706`（软件路 498/HDR 443/ARGB10 465/GainMap 602）→ `NativeAvifEncoder.cs:204,339-340` | `CrfToQuality(crf) = (int)Math.Clamp(100 - crf*100.0/63, 0, 100)` → `-q q`（avifenc 的 `-q` 越大越好，与 CRF 方向**相反**） | CRF 越小越好 |
| WebP | `FormatEncoders.cs:754` → `NativeWebPEncoder.cs:68` | `-q {(int)quality}`；`lossless = quality >= 100` 时改传 `-lossless` | 越大越好 |

**换算正确性**：三条映射与各编码器 CLI 语义一致（cjpegli `--distance [0,25]`、avifenc `-q [0,100]`、cwebp `-q [0,100]`）。CRF=20 → `-q 68`（质量位≈视觉无损档）换算方向无误。

### 1.2 实测：双质量值编码对比（真实编码器，800×600 程序生成图）

测试图：横向/纵向/对角三通道渐变 + 5×7 字模文字 "QUALITY PROBE 2026" + 8px 棋盘格 + 1px 竖条纹 + 灰阶条 + 4px 横条纹 + 同心圆环（`reference-input.png` 349317 B）。编码参数与产品默认口径对齐：JPEG LI 色度 420 + Effort Smallest（`--xyb`）、AVIF LibAom/444/10-bit + Balanced（`-s 6`）、WebP Effort Smallest（`-m 6`）。

| 组 | 传入值 | 实际命令行（CoreLog 原文，路径略） | 产物字节 | 对比值 | 判定 |
|---|---|---|---|---|---|
| JPEG LI d=0.5 | 0.5 | `cjpegli.exe --distance 0.5000 --xyb --chroma_subsampling 420 "tmp.ppm" "tmp.ppm.jpg"` | **42677** | — | — |
| JPEG LI d=1.0 | 1.0 | `--distance 1.0000 --xyb --chroma_subsampling 420 …` | 35106 | — | — |
| JPEG LI d=3.0 | 3.0 | `--distance 3.0000 --xyb --chroma_subsampling 420 …` | **24156** | vs 0.5 = **1.77x** | **PASS** |
| JPEG LI q=100（边界） | 100 | `--distance 25.0000 --xyb --chroma_subsampling 420 …` | **10036** | vs 0.5 = **4.25x** | **量纲灾难** |
| AVIF CRF=20 | 20 | （无命令行日志，静态换算 → `-q 68`） | **8204** | — | — |
| AVIF CRF=63 | 63 | （静态换算 → `-q 0`） | **3863** | vs 20 = **2.12x** | **PASS** |
| AVIF q=100（边界） | 100 | （CrfToQuality(100)=clamp(-58.7,0,100)=0 → `-q 0`） | **3863** | **与 CRF=63 产物同大小** | 量纲灾难（反向：灌到无损端） |
| WebP q=50 | 50 | （无命令行日志，静态换算 → `-q 50`） | **13766** | — | — |
| WebP q=100 | 100 | （`lossless=true` 分支 → `-lossless`） | **21240** | vs 50 = **1.54x** | **PASS** |
| WebP q=1（边界） | 1 | （静态换算 → `-q 1`） | 7514 | — | 量纲灾难（JPEG LI 量纲灌入 → 全糊） |

判据（交接任务给定）：同格式两组产物比值 ≥ 1.5 视为"质量生效"——三组主实验全部 PASS。

### 1.3 量纲灾难路径（交接怀疑点，已实证）

`JpegLiNative.cs:117` 的 `Math.Clamp(distance, 0.0f, 25.0f)`：传入 PNG 量纲 100 时**不报错、不换算，静默钳到 25.0 = cjpegli 合法范围的最差画质端**。实测产物 10036 B，仅为 d=0.5 产物的 1/4.25、比 d=3.0（本身已是低质量档）还小 58%。注释原文（`JpegLiNative.cs:114-116`）说明了钳制动机（"传整数 90 会被解释为 d90.000 超出范围导致错误"），但代价是把"量纲错误"从"编码失败"变成了"静默输出最差画质"。同类钳制还有 `JpegGainMapEncoder.cs:182/220/270/293` 的 `Math.Clamp(settings.Quality, 0.5f, 25.0f)`（下限 0.5，上限同为 25）与 `NativeJxlEncoder.cs:200/308/396`（[0,25]）。

`NativeAvifEncoder.CrfToQuality`（339-340）的 clamp 方向相反：PNG 量纲 100 → q=0 = avifenc 无损端（产物体积爆大，质量"过好"而非"过差"）。WebP 无任何钳制（`-q {(int)quality}` 直取整）：JPEG LI 量纲 1.0 → `-q 1` 全糊。

**可达形态（联动任务 B 裁决②）**：当前格式 WebP 滑块 50~63 → 贴图窗"保存为 AVIF" → CRF 50~63 → `-q 0~20`（画质差）；当前格式 JPEG LI 滑块 0.5~3.0 → 贴图窗"保存为 WebP" → `-q 0~3`（全糊）；当前格式 PNG/TIFF 100 → 贴图窗"保存为 AVIF" → CRF 100 → `-q 0`（无损，体积爆炸）。

### 1.4 留痕缺口（新增发现）

handoff §3 第 3 步要求"各编码器实际收到的命令行"取证。实测：**`JpegLiNative.cs:165` 是全部三个包装类中唯一有命令行 Debug 日志的**；`NativeAvifEncoder`（psi.Arguments 组装于 :480/:235-239）与 `NativeWebPEncoder`（:76-86）组装完参数后**不落任何日志**——本探针的 CoreLog(Trace) sink 只能抓到 JPEG LI 的命令行，AVIF/WebP 只能以"静态换算 + 产物字节差"作证。若日后要按 §3 的"命令行数字必须逐字等于日志质量="判据排查 AVIF/WebP，需要先补这条日志通道。

### 1.5 探针复跑方法

```powershell
dotnet run --project artifacts\quality-probe\QualityProbe -c Debug -- <产物目录>
```
原生 exe 由仓库根 `Directory.Build.props` 的 None Include 规则自动复制到探针输出 `Resources\`，再由 `NativeLibraryResolver.Initialize()` 提取到 `native\`（与产品同一套解析链，探针日志可见 4 枚 exe 的复制记录）。产物文件（.jpg/.avif/.webp/.png）与命令行原文保留在产物目录。

---

## 2. 任务 B · 全编码入口质量源审计

### 2.1 质量的唯一正确源头

`CapturePipelineService.cs:291-294`：App 层**唯一**的 `new EncodingSettings` 产品调用点，质量取 `Quality = (float)GetPerFormatQuality(s, format)`，而 `GetPerFormatQuality`（:594-595）就是 `s.GetQuality(format)`（每格式独立字段，AppSettingsData.cs:499-509）。**这条源头是安全的**。

### 2.2 入口清单（穷举）

grep `BuildEncodingSettings`（49 处）/ `new EncodingSettings`（69 处）/ `Pipeline.EncodeAndSaveAsync|EncodeHdrFrameAsync`（7 处）全量核对后：

| # | 入口 | 文件:行号 | 质量来源 | 结论 |
|---|---|---|---|---|
| 1 | 快照构建（源头） | CapturePipelineService.cs:294 | 每格式字段 `GetQuality(format)` | ✅ 安全 |
| 2 | **MainWindow 包装** | MainWindow.xaml.cs:3401→**3404** | 管线取好后被 `settings.Quality = ui.Quality`（滑块值，xaml.cs:3361）**无条件覆盖** | 🔴 **红旗 A** |
| 3 | 选区/贴图/粘贴保存（SDR byte[] 路） | xaml.cs:1683（ExecuteEncodeSaveAsync） | 经 #2 | 受覆盖影响（无 forceFormat 时滑块与当前格式同步，覆盖=冗余） |
| 4 | 无感截图 HDR 帧 | xaml.cs:1833（ExecuteSilentEncodeAsync） | 经 #2 | 同上 |
| 5 | 无感截图 SDR 帧 | xaml.cs:1886 | 经 #2 | 同上 |
| 6 | 剪贴板另存/复制 | xaml.cs:2298（EncodeAndCopyAsync） | 经 #2 | 同上 |
| 7 | 选区 HDR 保存 | xaml.cs:2506（EncodeAndSaveHdrAsync） | 经 #2 | 同上 |
| 8 | 贴图窗"保存为 PNG"（forceFormat） | xaml.cs:**2988** → 1626 `ui with {Format=forced}` → 3404 | ui.Format 被强制为 PNG，**滑块值仍是原格式的量纲** | 🟡 红旗但无害（PNG 编码器不读质量） |
| 9 | **贴图窗"保存为 AVIF"（forceFormat）** | xaml.cs:**2989** → 1626 → 3404 | ui.Format 被强制为 AVIF，滑块值=原格式量纲 ⇒ **量纲错位实锤** | 🔴 **红旗 B（危害路径，见 §1.3 可达形态）** |
| 10 | 贴图窗"复制" | xaml.cs:2990（forceFormat=null） | 当前格式，滑块同步 | ✅ 安全 |
| 11 | 动画录制（ffmpeg 动图） | xaml.cs:2040 → AnimationRecorder.cs:353-354 | `RecordingConfig.Quality` ← `_settings.RecordQuality`（**独立字段**，AppSettingsData 的 RecordQuality） | ✅ 安全（与静态截图编码无关；AVIF 动图换算 `(100-q)*63/100` 方向正确） |
| 12 | 自检探针 | SelfTest.cs:297/299/327/328/502-504 | 管线快照 `GetQuality` | ✅ 安全（自检工具） |
| 13 | Test/Tools 直接 `new EncodingSettings` | CorePipelineTests / ColorPipelineTests / FormatBench / SavePathBench / AvifBackendLadder / 各 probe 等 | 硬编码质量值 | 不属产品入口（测量夹具） |

**无绕过入口**：`Pipeline.EncodeAndSaveAsync/EncodeHdrFrameAsync` 的全部 7 处调用（xaml.cs:1687/1839/1861/1889/2307/2518/2543）都在 MainWindow 内且都在 BuildEncodingSettings 之后——**产品内不存在绕过 `GetPerFormatQuality`（cs:594）取质量的编码路径**（红旗 c 排除）。管线 2026-09-19 修复时已删除"内部自建设置"的旁路重载（CapturePipelineService.cs:648-653 注释）。

### 2.3 红旗 A 的两种不同步时刻（裁决②细化）

**时刻 1 · forceFormat（实锤可达）**：贴图窗（PinWindow）"保存为 PNG/AVIF"（xaml.cs:2988/2989）把 `ui.Format` 换掉，而 `ui.Quality`（滑块）没有跟着换区间——1606 的注释自己承认了这一点："其余设置（质量/色域/HDR）仍取 UI 快照——贴图只是'另存一份'"。危害方向不对称（见 §1.3）。

**时刻 2 · 滑块超界回落（低危）**：`UpdateQualityPanelCore` 遇越界 savedQ 回落 `def`（xaml.cs:876-878）时，若字段未同步回写，3404 会拿滑块的 def 编码而字段还是旧超界值——方向是"把非法值洗成合法档"，不构成质量灾难，但会造成管线日志（覆盖前）与 MainWindow 日志（覆盖后）不一致。

**不构成问题的时刻**：用户正常拖滑块（OnQualityChanged 即时写 SetQuality）与切格式（UpdateQualityPanel 把滑块设回 GetQuality 值）下，滑块与字段同步，3404 的覆盖是冗余但等值。交接日志实测两行一致（17:28:59/17:29:22/17:30:54 三条）正说明用户那三次编码走的是同步时刻。

### 2.4 与用户案例的关联判断

用户三次编码日志全是默认值（交接 §2.1 反证）⇒ 用户案例**不在** forceFormat 路径（那只出现在贴图窗操作）。结合 §4.3 的设置文件证据（`Quality:1` 死键、每格式字段全缺键），用户"调过质量"的痕迹只留在了死键上——**"滑块 → 字段 → 落点"没有断，断的是更上游的"旧版质量偏好 → 新版每格式字段"迁移**（从未实现）。若用户"糟糕"的观感出现在 JPEG LI 产物上，420 色度（交接可疑点 D）仍是待用户确认的候选。

---

## 3. 任务 C · 遗留 Quality 字段消费方

### 3.1 定义与全部读写方

- 定义：`AppSettingsData.cs:119` — `public double Quality { get; set; } = 80;`（注释"兼容字段：当前格式的质量（保留用于旧配置文件/其他读取点）"）
- **写入方**（1 处）：`MainWindow.Settings.cs:363` — `_settings.Quality = QualitySld.Value;`（保存设置时镜像滑块值，下一行 364 同时写每格式字段 `SetQuality(CurrentFormat(), …)`）
- **读取方**（2 处，均不可达）：`AppSettingsData.cs:508`（`GetQuality` 的 `_ => s.Quality`）与 `:523`（`SetQuality` 的 `default: s.Quality = value`）。switch 已显式覆盖 `OutputFormat` 全部 7 个枚举值（PNG/JPEG_LI/JPEG_GAINMAP/JPEG_XL/AVIF/WebP/TIFF，:501-507/:516-522）⇒ **default 分支仅在将来新增枚举值时可达**。

### 3.2 编码路径参与判定

**不参与**。管线取质量的唯一入口是 `GetQuality(format)`（CapturePipelineService.cs:294→594）；`EncodingSettings.Quality` 的实际来源是管线每格式字段 + xaml.cs:3404 的滑块覆盖，与遗留字段无任何数据流交集。`AnimationRecorder` 的 `_config.Quality`（:353）来自 `RecordingConfig.Quality`（:21，默认 80），由 `xaml.cs:2040` 从 `_settings.RecordQuality` 赋值——是另一个独立字段，与遗留 `Quality` 无关（已核实赋值点）。

### 3.3 用户设置文件实证

```
%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json   →  "Quality":1        （唯一的质量键）
%LOCALAPPDATA%\TrueToneCap\TrueToneCap.bak-x\…json      →  "Quality":100
```
活动文件中**所有每格式字段（QualityPng/QualityJpegLi/QualityAvif/QualityWebp/…）全部缺键** ⇒ 反序列化全走 C# 默认值（100/1.0/20/90/100）——与交接日志里三个默认值（JPEG_LI=1.0 / AVIF=20 / PNG=100）完全吻合。`"Quality":1` 说明滑块曾到过 1（在 JPEG LI 0.5-3.0 / AVIF 0-63 / JXL 0.1-4.0 的区间内合法，且都靠近"高质量"端），但它写进的是死键。

### 3.4 迁移与钳制缺口

- `SettingsService.Normalize`（SettingsService.cs:448-465）对 7 个每格式字段 + RecordQuality 做区间钳制，**不碰遗留 `Quality`**
- `MigrateStaleDefaults` 的 Take 列表（SelfTest.cs:1238 夹具与 SettingsService 内同构逻辑）也只有每格式字段 ⇒ 遗留键被原样保留
- **不存在任何 "遗留 Quality → 每格式字段" 的迁移**。旧版本（每格式字段引入前）用户拖出的质量偏好，在新版里永远丢失且无痕迹

### 3.5 处置建议（仅建议，未改代码）

1. **最少动作**：把 `AppSettingsData.cs:117-118` 的注释改为"死键：GetQuality/SetQuality 的 default 分支在当前枚举下不可达，仅为不破坏老 JSON 反序列化而保留；真正生效的是下方每格式字段"——现行注释"保留用于旧配置文件/其他读取点"会误导人以为它还活着（本次调查初期即被误导）。
2. **推荐动作**：删除 `MainWindow.Settings.cs:363` 的镜像写入（它每保存一次就把滑块值写进死键，继续制造"这键有用"的假象）；属性本身可在 `SettingsRevision` 下一版迁移时从 JSON 契约中摘除（`AppJsonContext` 序列化差集后自然消失）。
3. **可选动作**：若想挽救老用户的质量偏好，可做一次性迁移——无 `SettingsRevision`/低修订号的文件里若 `Quality ≠ 80` 且当前格式字段等于默认值，把 `Quality` 搬进**当时格式**对应的每格式字段。难点：JSON 里没有"当年的格式"记录（FormatIndex 语义已随格式表改版漂移过两次），搬错格式的代价可能比不搬更大 ⇒ 建议先只落一条日志提示，或干脆放弃迁移（用户重拖一次滑块即可）。
4. **顺手收口**：`JpegLiNative.cs:114-117` 的钳制注释建议补一句"⚠ 上游量纲错误（如 PNG 的 0-100）会被静默钳到 25=最差画质，正确做法是上游保证量纲"——本次探针实证了这条路径的真实危害。

---

## 4. 证据索引

| 类别 | 位置 |
|---|---|
| 探针项目 | `artifacts\quality-probe\QualityProbe\`（QualityProbe.csproj + Program.cs） |
| 探针产物 | `artifacts\quality-probe\out\`（reference-input.png / jpegli_d050.jpg / jpegli_d100.jpg / jpegli_d300.jpg / jpegli_q100.jpg / avif_crf20.avif / avif_crf63.avif / avif_q100.avif / webp_q50.webp / webp_q100.webp / webp_q1.webp） |
| 命令行原文 | §1.2 表（JPEG LI 为 CoreLog 实抓；AVIF/WebP 为静态换算，原因见 §1.4） |
| 用户设置文件 | `%LOCALAPPDATA%\TrueToneCap\TrueToneCap.settings.json`（`"Quality":1`） |
| 关键代码坐标 | JpegLiNative.cs:117/118/155/165 · NativeAvifEncoder.cs:204/235-239/339-340/480 · NativeWebPEncoder.cs:68/76 · FormatEncoders.cs:158/443/465/498/602/706/754 · JpegGainMapEncoder.cs:182/220/270/293 · NativeJxlEncoder.cs:200/308/396 · CapturePipelineService.cs:291-294/594-595 · MainWindow.xaml.cs:1626/1683/1833/1886/2291/2298/2506/2988/2989/2990/3361/3404 · MainWindow.Settings.cs:363-364 · AppSettingsData.cs:119/499-509/512-524 · SettingsService.cs:448-465 · AnimationRecorder.cs:21/353-354 · xaml.cs:2040 |

## 5. 本轮未做 / 下一轮建议

1. handoff §3 第 1 步（用户拖非默认值看两行日志）仍需用户配合——本轮的死键证据提供了新解读：用户即使拖了，若拖在旧版或保存时机不对，痕迹只在 `Quality:1`。
2. 给 AVIF/WebP 包装类补命令行 Debug 日志（对齐 JpegLiNative.cs:165 的既有形态），否则"命令行数字逐字等于日志质量="判据对这两个格式永远无法落地。
3. 贴图窗 forceFormat 的量纲错位修法（二选一，不许并存）：删 xaml.cs:3404 覆盖让管线作唯一来源；或把 ui.Quality 改成 `ui.Settings.GetQuality(ui.Format)`。修前建议先按 handoff §3 第 2 步加临时分叉日志坐实触发频率。