# 默认配置审查（2026-09-15）

> ⚠ **时效标注（2026-09-20 整洁审计）**：本文对 `ChromaAvif` 默认值的判断（`:38`，"默认 4:4:4 视觉无损 ⇒ ✅"）
> **已失效**：2026-09-19 用户拍板 AVIF 默认色度改回 **4:2:0**，且 `Auto` 改为**按色度过滤编码器**
> （420⇒硬件、422/444⇒libaom）。两处是一个决定，**只改任一半都会重现"Auto 恒降级"**，
> 理由写在 `AppSettingsData.ChromaAvif` 的注释里。同理"可选目标色域"已从 6 项收窄为
> 跟随显示器 / sRGB / BT.2020（DisplayP3/DCI-P3/AdobeRGB 只在 Core 保留能力，界面不再暴露）。

对软件全部默认配置做了一次完整核对，判断依据有三条：

1. **与编码器实际区间是否对齐**（设置默认值 vs 各编码器 `GetQualityRange()`）
2. **与同层其他默认值是否自洽**（如 `AppSettingsData` vs `ToneMappingParams`）
3. **是否符合产品定位**（HDR 截图 + OCR 工具）

结论：**默认配置整体是合理的**，未发现"默认即错误"的项。发现 1 处需要产品决策的取舍、2 处设计隐患（当前不触发）。

---

## 一、质量默认值 —— 与编码器逐一对齐 ✅

各格式的 `min / max / default` 实测如下，**设置默认值与编码器默认值完全一致**：

| 格式 | 编码器区间 | 编码器默认 | 设置字段 | 设置默认 | 一致 |
|---|---|---|---|---|---|
| PNG | 100–100 | 100 | `QualityPng` | 100 | ✅ |
| JPEG LI | 0.5–3.0（butteraugli） | 1.0 | `QualityJpegLi` | 1.0 | ✅ |
| JPEG XL | 0.1–4.0（butteraugli） | 0.8 | `QualityJpegXl` | 0.8 | ✅ |
| AVIF | 0–63（CRF） | 18 | `QualityAvif` | 18 | ✅ |
| WebP | 50–100 | 92 | `QualityWebP` | 92 | ✅ |
| TIFF | 100–100 | 100 | `QualityTiff` | 100 | ✅ |
| GainMap | 0.5–3.0 | 1.0 | `QualityGainMap` | 1.0 | ✅ |

这一点做得好：两处（UI 设置 / 编码器）若各自漂移，就会出现"界面显示的值"与"实际编码用的值"打架。本次新增的 `SettingsService.Normalize()` 也采用了同一套区间。

> 注：PNG / TIFF 为无损格式，区间固定 100–100。已把 `Normalize()` 中这两项从宽松的 0–100 收紧为 100–100，严格对齐编码器。

## 二、位深与色度 —— 合理 ✅

| 项 | 默认 | 判断 |
|---|---|---|
| `BitDepthPng / JpegLi / WebP / Tiff / GainMap` | 8 | ✅ SDR 场景正确 |
| `BitDepthJpegXl / Avif` | 10 | ✅ 这两种格式承担 HDR 输出，需要更高位深 |
| `ChromaJpegLi / WebP` | 420 | ✅ 有损压缩常规选择，体积小 |
| `ChromaAvif / JpegXl / Tiff` | 444 | ✅ 截图推荐（色度不降采样，文字/UI 边缘更干净） |

## 三、HDR 与色彩 —— 合理 ✅

| 项 | 默认 | 判断 |
|---|---|---|
| `HdrEnabled` | `true` | ✅ 与产品主打的 HDR 截图一致 |
| `PaperWhiteNits` | 200 | ✅ 与 `ToneMappingParams.PaperWhiteNits = 200f` 一致 |
| `DisplayMaxNits` | 1000 | ✅ 与 `ToneMappingParams.DisplayMaxNits = 1000f` 一致（HDR/GainMap 路径使用） |
| `ColorSpaceIndex` | 0（System） | ✅ 跟随系统，最安全 |
| `IccBakeEnabled` | `false` | ✅ 保守优先，避免 ICC 引发的兼容性问题 |
| `GainMapMode` | Gray | ✅ 体积最小，符合默认取舍 |

> `DisplayMaxNits` 不在 SDR 色调映射里参与运算 —— 这是**正确设计**（三重约束下的唯一解），已在 `pipeline-review-2026-09-15.md` 中论证，不是缺陷。

## 四、OCR —— 合理，但有一个已知能力边界 ⚠️

| 项 | 默认 | 判断 |
|---|---|---|
| `OcrEngineMode` | `OnnxGpu` | ✅ 实测 DML 比 CPU 快约 9 倍且准确率持平，默认走 GPU 正确 |
| `OcrLanguage` | `""` | ✅ 由 `PopulateOcrLanguages()` 按引擎能力填充 |
| `TargetLanguage` | `zh-CN` | ✅ 符合主要用户群 |
| `LlmModel` | `deepseek-chat` | ✅ |

⚠️ **韩文不可用**：`ppocrv6_dict.txt`（18708 条）中谚文与韩文字母均为 **0** 个，模型本身不具备韩文能力。语言映射修复只解决了"错误地用中文引擎识别"，不改变这个边界。要让韩文可用需更换/扩充字典。

## 五、界面与行为 —— 合理 ✅

| 项 | 默认 | 判断 |
|---|---|---|
| `Language` | `zh` | ✅ |
| `ThemeMode` | `Default`（跟随系统） | ✅ |
| `ShowPreview` / `MinimizeToTray` | `true` | ✅ 截图工具应有预览与托盘 |
| `AutoStart` | `false` | ✅ **不应**默认开机自启 |
| `ToastOnCapture` 等三项 | `true` | ✅ 截图是异步操作，需要完成反馈 |
| `ToastPosition` | `BottomRight` | ✅ |
| `ArchiveEnabled` | `false` | ✅ 不默认改变文件组织方式 |
| `FormatIndex` | 2（JPEG LI） | ⚠️ 见下节 |
| 热键 `Ctrl+Shift+S/G/Q` | — | ✅ 不与常见全局快捷键冲突 |
| `OutputPath` | `""` → 图片库 | ✅ 由 `ApplySettingsToUI()` 兜底为 `MyPictures\TrueToneCap` |

---

## 六、需要你决策的一项：默认输出格式

`FormatIndex = 2`（JPEG LI）配合 `QualityJpegLi = 1.0`（butteraugli 距离）。

- **支持当前默认**：JPEG LI 体积小、兼容性最好、编码快（注释中的原始理由）。
- **值得斟酌**：截图内容以**文字与 UI 元素**为主，锐利边缘对有损压缩最敏感。butteraugli 1.0 大约处在"肉眼可接受"的下限，文字边缘可能出现轻微振铃。若把默认值收紧到 **0.8** 会更稳妥，代价是体积上升。

这属于**体积 / 质量的产品取舍**，不是代码缺陷，故未擅自更改。如果你倾向更保守的默认值，改 `AppSettingsData.QualityJpegLi` 一处即可（同时建议同步 `JpegLiEncoder.GetQualityRange()` 的 default，保持第一节的对齐关系）。

## 七、两处设计隐患（当前不触发，已加注释）

### 1. `EncodingSettings` 的兜底默认值对多格式非法

```csharp
public OutputFormat Format { get; set; } = OutputFormat.PNG;
public float Quality       { get; set; } = 90f;
```

- `Format` 默认 PNG，而产品默认（`AppSettingsData.FormatIndex = 2`）是 JPEG LI；
- `Quality = 90` 只对 WebP(50–100) 这类"百分比式"区间有意义，对 JPEG LI(0.5–3.0)、AVIF(0–63)、JPEG XL(0.1–4.0) 都是**越界值**。

**当前不构成 bug**：产品路径只有 `CapturePipelineService:189`，且它显式设置了 Format 与 Quality；其余 `new EncodingSettings()` 均出现在测试与 `--selftest` 中，也都显式指定。

隐患在于后来者：直接 `new EncodingSettings()` 而不赋值，有损格式会拿到荒谬的质量参数。已在字段上添加警示注释说明"必须显式指定 Format 与 Quality"。

### 2. 兼容字段 `Quality = 80` 量纲不同

`AppSettingsData.Quality` 是历史兼容字段（80），与各格式的 `Quality*` 量纲完全不同（如 JPEG LI 是 0.5–3.0）。它只在 `GetQuality()` 的 `default` 分支被使用，而 `FormatIndex` 合法时（0–6）走不到该分支，因此安全。保留它仅为兼容旧配置文件，不要在新代码中直接读取。

---

## 八、本次已修复的默认值相关问题

1. **清理 3 个僵尸设置**：`AnimAvifBackendIndex`（保存时硬编码写 0、无 UI、无消费方）、`ChromaPng`（PNG 不支持色度，硬编码 `"444"` 且无读取）、`SettingsVersion`（注释称用于迁移，实际零引用）。
2. **设置路径不一致**：`App.xaml.cs` 加载主题时注释写 `AppContext.BaseDirectory`、代码拼 `LocalApplicationData`（注释与实现相反）。现统一引用 `SettingsService.SettingsPath`，并改为软件同目录 + 降级 + 旧配置迁移。
3. **新增加载后规范化**：越界值（质量超出格式区间、索引越界、枚举字符串非法、色度/位深非法）就地修正，避免"界面显示默认值、内存里仍是错值"。
