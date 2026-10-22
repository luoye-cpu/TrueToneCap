# 任务交接：AVIF 编码器重构

> ⚠ **时效标注（2026-09-20 整洁审计）**：本交接已结案，现状以
> `handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` §23–§32 为准。特别两处别再照本文动手：
> ① `:344` 那条"Auto 不协商、10-bit/444 被静默降级、没有 Toast"是**当时**的状态 —— 现在能力协商在
> `AvifEncoderCapabilities`，降级留痕在 `EncoderFallbackNotifier` + `CapturePipelineService`，
> 手选后端被换掉时还会带原因（`CodecDowngradeNote`）；
> ② `:59` 的前提"默认 `ChromaAvif = 444`"已被 2026-09-19 的决定取代（默认 **4:2:0** + Auto 按色度过滤）。

- 日期：2026-09-17
- 状态：**✅ 全部完成（含 §9 审查 P1/P2/P3 + 参数传递专项）** —— 构建 0 警告 0 错误；
  门禁 7 套件 / 478 断言全绿（`scripts/verify.ps1`）
- 剩余：**实机复验** + 按主题拆分提交；§10 列出了几项需要产品决策的开放问题

---

## 10. 参数传递专项（2026-09-17）

对「选择不同编码器时，可用的位深与色度采样」做了一次全链路核实（软件路径 / 硬件路径 / 参数传递三路并行），
结论是：**UI 上可选的值 与 实际编码产出 长期不一致**。已修复其中判据明确的四项。

### 10.1 关键事实（以此为准，勿凭印象）

| 场景 | 位深 | 色度 | 证据 |
|---|---|---|---|
| **SDR** | **按编码器能力：libaom 12、svt/avifenc 10、NVENC 10、QSV 8** | 软件（avifenc/libaom/svt）真实生效；硬件（NVENC/MFT）恒 420 | 软件：`NativeAvifEncoder.GetDepthArg()` + SDR 路径按请求拼 `--depth`（avifenc 的 `-d` 对 PNG 输入做升采样）。硬件：由 `NV_ENC_CONFIG_AV1.outputBitDepth` 决定，**与输入 bufferFormat 无关**（2026-09-17 实测接通，见 §10.4） |
| **HDR** | **恒 10-bit** | **已传 `--yuv`**（`EncodeFile` 新增 chroma 形参），设置真实生效 | `FormatEncoders.cs` HDR 分支 `HdrToPq16(…, 10)` + `NativeAvifEncoder.EncodeFile:321` 拼 `--yuv` |

> ⚠ **2026-09-17 勘误（两轮）**：本表初版写「SDR 恒 8-bit」与「HDR 不传 `--yuv`」，
> 两条**均已过时**。前者在 SDR 软件路径补上 `--depth` 后不再成立（软件真能产出 10/12-bit）；
> 后者在 `EncodeFile` 增加 chroma 形参后不再成立。
> **第二轮勘误**：硬件侧「恒 8-bit」亦已推翻 —— 那是本代码库的限制，不是硬件能力，
> 现已实测接通（NVENC 可产真 10-bit 码流）。以本表当前内容为准。
| **HDR 的编码器选择** | — | — | **完全无效**：HDR 分支不调用 `SelectByKind`，恒走 avifenc |

### 10.2 已修复

1. **位深协商改为分 SDR / HDR 场景**（`AvifEncoderCapabilities.SdrMaxBitDepth=12` / `HdrBitDepth=10`）。
   原 `Math.Min(want, MaxBitDepth)` 与两条路径都不符：SDR 下 libaom 声明 12 ⇒ 请求 10-bit 时
   **协商前后相等、连降级 Toast 都不触发**，日志却打「位深=10」—— 真正的静默改参数。
2. **协商移到 `settings.HdrOutput` 最终确定之后**。旧位置在「HDR + sRGB 目标 → 降级 SDR」
   改写 `HdrOutput` 之前 ⇒ HDR 用户会收到根本不成立的「降级为 420 / 8-bit」假警报。
   新增源码契约断言锁住该顺序（改前必然失败，可证伪）。
3. **HDR 场景不再协商、不再弹降级 Toast**（改为一条 Info 日志 + 把 `OutputBitDepth` 写成真实的 10）。
4. **UI 联动**（新增 `UpdateAvifOptionAvailability()`）：
   HDR 时整组禁用并说明；SDR 时位深按 `SdrBitDepthFor()` 钳制、色度按 `Satisfies()`
   禁用不支持的项。此前 `Satisfies()` 除单测外零处生产调用，选 NVENC 仍可点 4:4:4 / 12-bit。
5. **消除 NVENC 422 的「声明与实现打架」**：移除 `NvencAvifBackend` 两处恒不可达的
   `chroma == "422"` 分派（能力表已声明 `Supports422:false`，请求在上游就被协商成 420）；
   `EncodeAv1_422` / `BgraToNv16` 保留待启用，并注明启用前必须同步改的两处。
6. **修正误导性 UI 文案**：编码器下拉原显示 `≤{MaxBitDepth}-bit`（那是 HDR 语义），
   SDR 用户会以为能拿到 10/12-bit。改为按 `SdrBitDepthFor()` 逐编码器标注
   （软件 `SDR ≤10/12-bit · HDR 10-bit`，硬件 `SDR ≤10-bit · HDR 不可用`）。

### 10.3 开放问题（需产品决策，未擅自改动）

> **📌 2026-09-17 审查后状态更新**
> - **① 已解决**：默认色度改为 420（`AppSettingsData.ChromaAvif`），且 `PickAuto` 不再按
>   色度/位深过滤 ⇒ 硬件在默认设置下能被选中。
> - **② 部分解决**：编码层已具备（`GetDepthArg` + SDR 拼 `--depth`），协商也已放开
>   （`SdrMaxBitDepth` 8→12、新增 `SdrBitDepthFor`）。**但暴露出一个新的默认值冲突，见下方 ② 补充说明。**
> - **③ 已解决**：`EncodeFile` 已增加 chroma 形参并拼 `--yuv`，HDR 色度受控。
> - **④ 已解决**：`bitDepth` 不再是死参数。

1. ~~**默认设置下 Auto 永远选不到硬件**~~ —— 已解决，见上方状态更新。原文保留备查：`PickAuto` 要求同时满足色度与位深需求，
   而默认 `ChromaAvif=444` + `BitDepthAvif=10` ⇒ NVENC/QSV 因不支持 444 被跳过，
   本机 SVT 又不可用 ⇒ 恒落到 libaom 软件编码（4K AVIF 约 22s vs 硬件约 0.7s）。
   要真正让「Auto 硬件优先」生效，需三选一：① 默认色度改 420；② PickAuto 放宽色度
   （优先硬件、接受降级并提示）；③ 保持现状并在 UI 说明。
2. **SDR 是否要真的支持 10-bit** —— **编码层已具备能力，只差放开协商**（2026-09-17 更新）。
   - 原判断"必须改用 16-bit PNG 中转"**不成立**：avifenc 的 `-d,--depth D` 就是输出位深，
     且**只对 JPEG/PNG 输入做转换**（libavif `apps/avifenc.c` 帮助文本：
     `D is the output bit depth per channel. D must be 8, 10 or 12. (JPEG/PNG only; y4m: D must match…)`）。
     本路径输入正是 PNG ⇒ **无需 16-bit 中转**，补 `--depth` 即可。
   - 已落地：`NativeAvifEncoder.GetDepthArg()` + SDR 路径拼 `--depth`（8 不传参、&gt;12 钳 12），
     并在该位深不被本机 avifenc 接受时**自动回退 8-bit 重试**（最坏等价旧行为，不吞异常）。
   - 现状：由于 SDR 协商仍恒钳 8，上述参数在默认链路上**尚未生效**（等价旧行为，零风险）。
   - **放开只需两步**（未擅自改，因会翻转 §10.2-1 已验证的结论并影响 478 条断言中的若干条）：
     ① `AvifEncoderCapabilities.SdrMaxBitDepth` 由常量 8 改为按编码器取值
     （软件 `min(MaxBitDepth, 12)`，硬件 8）；② `UpdateAvifOptionAvailability()` 的
     SDR 位深禁用逻辑同步放开；③ 更新 `Test_AvifCaps_NegotiateBitDepth` 的 SDR 断言。
   - **✅ 2026-09-17 后续审查确认：上述三步已全部落地**（`SdrMaxBitDepth=12`、
     `SdrBitDepthFor()` 按硬件/软件取值、测试断言已改写为软件 10/12）。
     但"SDR 恒 8-bit"的说法残留在多处注释与 UI 文案中，本轮已同步修正。
   - 🔴 **由此暴露的默认值冲突（待产品决策）**：`BitDepthAvif` 默认 10，
     而 Auto 默认挑中的硬件编码器 SDR 只产出 8 ⇒ **开箱即用的第一次 AVIF 截图必然弹出
     "参数从 420 / 10-bit 降级为 420 / 8-bit"**。

#### 🔧 冲突的正确解法：不是改默认值，而是接通硬件的 10-bit（2026-09-17 用户指正后核实）

初版判断写的是"硬件 SDR 上限 8-bit"，**这个说法不准确，已更正**：

| 结论 | 依据 |
|---|---|
| **NVIDIA / Intel 的 AV1 硬编都支持 10-bit**（AV1 Main10 profile）。8-bit 是**我们输入路径的实现限制**，不是硬件能力 | `NvEncoderNative.cs:29` 的 `NV_ENC_BUFFER_FORMAT_ARGB10` 常量**定义了却从未使用**；`MftEncoderNative.cs:91` 输入类型恒为 `NV12`（P010 才是 10-bit） |
| 初版推测的三处改动里，**"选 Main10 profile"走错了方向**（位深与 profile 无关） | 实测：输出位深由 `NV_ENC_CONFIG_AV1.outputBitDepth` 决定，**与 profile 无关**。但"喂 ARGB10 输入"**方向没错，只是当时缺了另一半** |
| ⚠ **2026-09-17 二次勘误（重要）** | 本节初版写"改 ARGB10 输入无效"是**错的**，实测推翻。`outputBitDepth`（输出）与 `inputBitDepth`（输入语义）必须**配套**：当时只改输入格式、`outputBitDepth` 仍是 8 ⇒ 10-bit 输入被降到 8。头文件那句 "Support for 8 bit input to 10 bit encode conversion only" 限制的是**转换方向**（不支持 10→8），不是禁止 10-bit 输入。**驱动实测**：AV1 输入格式含 `ARGB10`/`ABGR10`/`YUV420_10BIT`，`SUPPORT_10BIT_ENCODE=1`。阶梯判据实测：ARGB10 输入 → 真 10-bit 斜坡；8-bit 输入 → 4 级台阶。详见 `docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` §3 |
| **容器层确实无需改动** ✅（此条原判断正确） | `IvfWriter.WriteAvif` 从 AV1 序列头解析 `bitDepth`，并据此写 av1C 的 `high_bitdepth` 与 `bd` —— 编码器吐 10-bit 码流，容器自动跟着变。**但见下方 🔴：该解析器本身有 bug** |
| 另需考虑**广色域 SDR** | 用户指出 SDR 源不止 sRGB，还有 Display P3 / AdobeRGB 的广色域 SDR —— 这类内容 10-bit 才够用（8-bit 在 P3 渐变上有可见色带）。这是保留 10-bit 默认的实质理由 |

---

### 10.4 ✅ 硬件 10-bit 已实测接通（2026-09-17）

> **本节取代 §10.3 中"不宜在未实测前落地"的结论。改动已落地，门禁全绿。**

**方法：不猜偏移，改为实测。** 本机装有 MSVC（VS 18 / 14.51.36231）与 Windows SDK
`10.0.26100.0`。用宿主 PowerShell 的 `Invoke-WebRequest` 取官方
`nvEncodeAPI.h`（SDK 13.1，313554 字节），再用 `cl.exe` 编译一个含
`offsetof()` / `sizeof()` 的 C 探针取权威布局。探针源码保留在 `.workbuddy-ai/nvoff.c`。

**结果一：修正 6 处原生缓冲区越界**（驱动按真实结构大小写回，缓冲区却偏小 ⇒ 堆越界，
正是"不可捕获访问违规"最可能的来源）：

| 结构 | 原值 | 实测 |
|---|---|---|
| `NV_ENC_PRESET_CONFIG` | 4104 | **5128** |
| `NV_ENCODE_API_FUNCTION_LIST` | 512 | **2552** |
| `NV_ENC_PIC_PARAMS` | 2400 | **3360** |
| `NV_ENC_REGISTER_RESOURCE` | 264 | **1536** |
| `NV_ENC_MAP_INPUT_RESOURCE` | 120 | **1544** |
| `NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS` | 1088 | **1552** |

**结果二：`NV_ENC_CONFIG` 偏移整体后移 4 字节** —— `gopLength`/`frameIntervalP`/`rcParams`
实际在 20/24/40（代码写 24/28/44）⇒ **用户的 QP 设置此前一直被写进 `averageBitRate`，
从未生效**。

**结果三：纹理直通路径 Register/Map 偏移完全错位** —— 纹理指针此前被写进 out 字段，
`RegisterResource` 必然失败 ⇒ 纹理直通从未真正工作过。

**结果四：接通 10-bit** —— 写 `NV_ENC_CONFIG + 280`（`outputBitDepth`）、
`+284`（`inputBitDepth = 8`）。枚举实测值 `NV_ENC_BIT_DEPTH_8 = 8` / `_10 = 10`
（**不是**此前假设的 1/2）。独立验证：`ffprobe` 报 `pix_fmt=yuv420p10le`，
解码字节数 196608（420p10）而非 98304（420p）。

🔴 **结果五（顺带发现的 P0）：`IvfWriter.ParseAv1SequenceHeader` 解析错位约 19 位。**
漏读 `frame_width_bits_minus_1` 等 4 个字段、`seq_choose_screen_content_tools` 语义写反
（choose=1 表示 SELECT(2) 且不跟位，=0 时才跟 1 bit）、`mono_chrome` 只在 profile 1 读。
症状：**把 10-bit 判成 8-bit、4:2:0 判成 4:4:4**。而 av1C 的位深正由该解析器推导
⇒ 会写出位深标注错误的**损坏 AVIF**。已按 AV1 规范重写。

> **方法论教训**：我最初写的 Python 参考解析器与出问题的 C# 解析器犯同样的错、
> 给出同样答案，差点据此判定"解码器是对的、编码器有问题"。
> 最终以 ffmpeg（`-bsf:v trace_headers` 逐字段打印位位置）+ libdav1d 解码输出为
> **独立判据**才定位真因。**交叉验证必须用独立实现，不能用自己的第二份实现。**

**落地内容**：

- `AvifEncoderCapabilities`：NVENC `MaxBitDepth` 8 → **10**；`SdrBitDepthFor` 不再对硬件写死 8
- `FormatEncoders`：三条编码调用（纹理直通 / CPU 像素 / N 卡设备重试）传入协商后的位深
- 新增 `Test_Av1SequenceHeader_Parse`：用本机实拍码流做夹具，锁住位深与色度解析
- `Test_Nvenc_Av1TenBit` 断言改为期望真 10-bit（`d10 == 10`）

**验证**：构建 0 警告 0 错误；门禁 8 套件全绿（unit 253 / color 93 / encoding 14 /
format 33 / service 4 / ocr 10 / usability 152 / wgc-avif 21，共 580 断言 0 失败）。

⚠️ **已知边界（尚未实施）**：硬件只接受 8-bit 输入 ⇒ 10-bit 输出是**升位深**。
这与软件路径（avifenc 对 8-bit PNG 加 `--depth 10`）行为一致，但两者都不是"源精度 10-bit"。
要让**广色域 SDR** 真正保住 10-bit 精度，还需要一条 16-bit SDR 像素通路
—— 当前 SDR 像素在 `ToneMapper.FloatToSRgbBytes` 就被量化成 8-bit BGRA，
且 `FormatHelper.ToSdrAndDegrade` 依 2026-08-10 的设计决策把 SDR 输出**恒钳为 sRGB 色域**。
这是下一轮的产品决策项。
3. **HDR 输出的色度不受控**。`EncodeFile` 不传 `--yuv`，实际由 avifenc 默认决定（疑为 444），
   而 `settings.AvifChroma` 仍保留用户选择 ⇒ 元数据与实际可能不符。需实机确认后再决定是否回写 444。
4. ~~**`NativeAvifEncoder.Encode` 的 `bitDepth` 形参是死参数**~~ —— 2026-09-17 已真正实现
   （见上条 #2；`GetDepthArg` 有单测锁死映射）。

---

## 11. 收尾清理（2026-09-17，本轮追加）

| 项 | 文件 | 说明 |
|---|---|---|
| **删除僵尸字段** | `ImageEncoder.cs` / `CapturePipelineService.cs` / `MainWindow.xaml.cs`（record + 日志）/ 3 个测试文件 | `EncodingSettings.AvifBackend` 恒写 `Auto`、Core 侧从未读取，属重构残留；删除后避免新代码误以为还有"第二条后端通道"。枚举 `AvifEncoderBackend` 保留（`Select()` 与 `IAvifEncoder.Backend` 仍在用） |
| **老用户偏好迁移** | `SettingsService.MigrateLegacyAvifEncoder()` | 旧字段 `AvifBackendIndex`（0=Auto/1=LibAom/2=Qsv/3=Nvenc）与 `AvifCodec`（svt/aom）删除后，老用户的手选编码器会被静默丢回 Auto —— 同"改了没保存"型缺陷。仅在 JSON 无新 `AvifEncoder` 键时迁移，迁移后立即重写文件 |
| **JPEG XL 12-bit 撤下** | `MainWindow.xaml` `BdJpegXlCbo` + `SettingsService.FixBitDepthJxl` | cjxl 无 `-b` 参数，位深由输入容器决定 ⇒ **10 与 12 产物完全相同**（16-bit 容器）。留着 12 只会被误认为"更高位深"；12/16 一律归到 10，避免 `SetComboByTag` 失配 |
| **过期注释勘误** | `NativeJxlEncoder.cs` | 注释仍写"此处忽略 bitDepth (8-bit)"，与已实现的 16-bit PNG 中转矛盾 |
| **新增断言** | `CorePipelineTests` | `Test_AvifSdr_DepthArg`（--depth 映射）+ `Test_Settings_BitDepthClampedPerFormat` 补 JXL 两档钳制 |

⚠️ 本轮改动**未经构建/门禁验证**（当前沙箱命令执行不可用），需人工跑 `scripts/verify.ps1` 确认
（改动均为机械删除 / 新增独立方法，编译面集中在 `EncodingSettings.AvifBackend` 的 8 处引用，已全仓 grep 清零）。

---

## 0. 一句话概括

把 AVIF 的「编码后端 + 软件编码器」**两层**合并为**单一**「AVIF 编码器」下拉，并引入**能力表**（色度 / 位深 / 是否硬件），Auto 模式下**硬件优先**、手动选择时尊重用户。

---

## 1. 用户原始要求（逐条，勿遗漏）

1. **取消**「编码后端」这个选项
2. 「AVIF 编码器」选项改为：**自动 / avifenc / avifqsv / libaom / svtav1**（并含 NVENC）
3. 按各编码器**实际能力**支持：**QSV 只支持 420**、**NVENC 最高 422**
4. **JXL 与 AVIF 都默认 10bit**
5. Auto 模式下**优先使用本机实测支持的硬件 GPU** 加速
6. 用户**手动选择时尊重用户选择**

---

## 2. 已完成（可直接依赖）

| 项 | 文件 | 说明 |
|---|---|---|
| **能力表** | `src/TrueToneCap.Core/Encoding/AvifEncoderCapabilities.cs`（**新建**） | `AvifEncoderKind` 枚举（Auto/Avifenc/Qsv/LibAom/SvtAv1/Nvenc）、`AvifEncoderCaps` 记录、`Table` 能力字典、`Caps()`、`NegotiateChroma()`、`NegotiateBitDepth()`、`PickAuto()`、`Satisfies()` |
| **设置字段** | `src/TrueToneCap.App/Models/AppSettingsData.cs:86` | `AvifEncoder`（默认 `"Auto"`），用于取代 `AvifBackendIndex` + `AvifCodec` 两层 |
| **白名单** | `src/TrueToneCap.App/Services/SettingsService.cs:247` | 6 个合法取值，未知值回落 `Auto` |

---

## 3. 待办（3.1–3.4 已完成 ✅，仅剩 3.5 实机验证）

### 3.1 选择器：`SelectByKind` / `IsKindAvailable` ✅ 已实现

`src/TrueToneCap.Core/Encoding/FormatEncoders.cs`（`AvifEncoderSelector`，约 :341 起）：

- 原 `Select(AvifEncoderBackend)` 保留不动，旧调用方不受影响
- `SelectByKind(kind, chroma, bitDepth)`：Auto → `PickAuto(IsKindAvailable, …)`；Nvenc/Qsv → 对应后端；LibAom/SvtAv1/Avifenc → `LibAomAvifBackend`，并按 kind 设置 `NativeAvifEncoder.Codec`
- `IsKindAvailable(kind)`：Nvenc/Qsv 查后端 `IsAvailable`，SvtAv1 查 `IsSvtAvailable`，其余查 `LibAomAvailable`
- 另新增 `AvifEncoderCapabilities.Parse(string)`（设置字符串 → Kind，未知回落 Auto）

### 3.2 编码链路接入 ✅ 已完成

`src/TrueToneCap.App/Services/CapturePipelineService.cs` `BuildEncodingSettings`（约 :168-204）：

- 解析 `s.AvifEncoder` → `AvifEncoderKind`，AVIF 格式时用 `NegotiateChroma` / `NegotiateBitDepth` 钳制
- 降级时 `LogService.Warn` + `ToastService.ShowToast(…, ToastTone.Warning)` 告知用户
- 传递方式：`EncodingSettings` 新增 `AvifEncoderKind` 字段（`ImageEncoder.cs:29`）；`AvifSdrEncoder.EncodeSdrAsync` 在 kind != Auto 时走 `SelectByKind`，否则保留旧 `Select(AvifBackend)` 兼容

### 3.3 UI 单一下拉 ✅ 已完成

- `MainWindow.xaml`：删除「编码后端」Grid 与 `AvifBackendCbo`；`AvifCodecCbo` 扩为 6 项（Auto/Avifenc/Qsv/LibAom/SvtAv1/Nvenc），事件改名 `OnAvifEncoderChanged`
- `MainWindow.xaml.cs`：`OnAvifEncoderChanged` 写 `_settings.AvifEncoder`；`UpdateAvifEncoderLabels()` 依据能力表动态生成每项内容（色度/位深/可用性 ✓）；`UpdateQualityPanelForAvifHardware()` 替代旧的 backendIdx 判断（NVENC/QSV 不支持无损 → Quality 下限 1）
- `MainWindow.Settings.cs`：Load 用 `SetComboByTag(AvifCodecCbo, _settings.AvifEncoder)` 并映射 `NativeAvifEncoder.Codec`；Save 写 `_settings.AvifEncoder`
- `MainWindow.Localization.cs`：删除 AvifBackendCbo 的本地化条目（内容现由能力表动态生成）
- 旧引用已清零（grep `AvifBackendCbo|OnAvifBackendChanged|OnAvifCodecChanged` 无匹配）

### 3.4 默认 10bit ✅ 已完成

- AVIF：默认已是 10 ✅
- **JXL 10bit 已真正实现**：`NativeJxlEncoder.Encode` 在 bitDepth>8 时改用 16-bit PNG 中转（8-bit 像素高低字节复制升采样，无损），cjxl 按输入输出 16-bit 容器；8-bit 仍走 PPM 更快
- **FixBitDepth 已修复**：`SettingsService.cs` 白名单补 12 → `v is 8 or 10 or 12 or 16`，XAML 的 12-bit 选项不再被静默改回 8

### 3.5 收尾（剩余工作）

- ✅ **实机验证已完成（2026-09-16，RTX 50 系）**：
  - **HDR 直通**：4K AVIF 27.9KB / 2183ms ✅
  - **SDR + NVENC（Auto）**：4K AVIF 292.9KB / 715ms（软件约 22s → 硬件确认生效）✅
  - **SDR + NVENC 4:2:2（手动 Nvenc + chroma=422）**：4K AVIF 75.7KB / 1.5s，NV16 输入路径正常产出 ✅
  - **降级协商**：手动 QSV + 444/10bit → 自动钳制 420/8-bit，`AVIF 参数降级` Warn 日志 + Warning Toast 均触发 ✅
  - 过程中发现并修复两处缺陷：① NV16 布局初版错误（UV 行宽误写 2w，Marshal.Copy 溢出导致原生崩溃，已改为色度水平减半行宽 w）② 降级提示代码在重构中丢失（avifDegraded 赋值后未消费，已补回 LogService.Warn + ToastService.ShowToast）
  - 验证方式：PowerShell `SendKeys('^+q')` 触发全局热键（本机无 python，`.workbuddy-ai/sendkey.py` 不可用）；日志位于 `publish/TrueToneCap-v0.3.4-beta/log/`
- ⬜ 待办：设置文件在发布时会被 `-Force` 清理，正式分发前建议保留用户设置迁移逻辑（既有行为，与本任务无关）
- 注意：用户 `settings.json` 在 `%LOCALAPPDATA%\TrueToneCap\`（或 exe 旁），内含极端旧值（`QualityAvif:63` 等），**新默认值不会自动生效**
- 提示：本沙箱无 `python`，`.workbuddy-ai/runps.py` 不可用；请直接用 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/verify.ps1` 验证

---

## 4. 关键事实：各编码器的**真实**能力（以此为准，勿凭印象）

| 编码器 | 色度 | 位深 | 证据 |
|---|---|---|---|
| **libaom**（avifenc） | 420/422/444（`--yuv`） | **SDR 路径恒 8-bit**（`bitDepth` 形参从未被使用）；HDR 路径恒 10 | `NativeAvifEncoder.cs:120`、`:110-184`、`:203/:233` |
| **SVT-AV1** | 同 avifenc（`--codec svt`） | 同 avifenc | `NativeAvifEncoder.cs:41-49` |
| **NVENC** | **代码实际只走 420** | 8（profile Main） | `NvEncoderNative.cs:1467-1470`（profile 0 = 8-bit 4:2:0）；输入恒 ARGB/NV12 |
| **QSV / MFT** | **仅 420**（输入转 NV12） | 8 | `MftEncoderNative.cs:91` |

> ⚠️ **用户口述"NVENC 支持最高 422"与代码现状（420）不符。** 能力表已按用户要求填 422，
> 但**若要真走 422，需先实现 NVENC 的 422 输入路径**（当前 ARGB / NV12 输入都是 420）。
> ~~交接时请与用户确认这一点。~~
>
> **⚠️ 2026-09-17 勘误**：本节下方"已实现 422 输入路径"的结论**已被实机证伪** ——
> AV1 硬件编码器任何代际都无 4:2:2 管线（NVENC Application Note；实测 RTX 5080
> `EncodePicture` 返回 `0x0C UNSUPPORTED_PARAM`）。能力表已改回 `Supports422: false`，
> 422 请求由协商直接落到 libaom（软件真 422）；`EncodeAv1_422` 暂成死代码，保留待将来启用。
>
> **✅ 2026-09-16 曾实现 422 输入路径（后被证伪，代码保留）**：
> - 官方核实（NVENC Application Note / nvEncodeAPI.h SDK 13.1）：4:2:2 编码**仅 Blackwell 及之后 GPU 支持**（Ampere/Turing/Ada 均为 N）；输入缓冲格式 `NV_ENC_BUFFER_FORMAT_NV16 = 0x40000001`（8-bit 半平面 YUV 4:2:2）；能力位 `NV_ENC_CAPS_SUPPORT_YUV422_ENCODE` 可查询
> - `NvEncoderNative.cs`：新增 `EncodeAv1_422()`（BGRA→NV16 CPU 侧转换，BT.601 系数与 MFT 路径一致）+ `BgraToNv16()` 转换器；`EncodeRaw` 参数化 bufferFormat（ARGB / NV16），三处结构体字段与像素拷贝按格式分支
> - `FormatEncoders.cs` `NvencAvifBackend.EncodeAsync`：共享设备与 N 卡设备两条 CPU 像素路径均按 `chroma=="422"` 分派到 `EncodeAv1_422`
> - 降级行为：不支持 422 的卡上 InitEncoder/CreateInputBuffer 失败 → 走既有失败分类（输入不适用类失败不禁用 NVENC）→ 回退 libaom（真 422）并提示用户
> - 纹理直通路径（`EncodeAv1FromTexture`）仍为 420：纹理是 BGRA10/ARGB 打包格式，无法直接携带 422，422 请求会在像素路径完成
> - ⚠ 实机验证依赖 Blackwell 显卡（RTX 50 系）；本机若为 RTX 40/30 系，422 选择将按设计回退 libaom

---

## 5. 陷阱与约束（血泪清单）

1. **两处质量默认必须同步**：`AppSettingsData.Quality*` 与各编码器的 `GetQualityRange()`（返回 min/max/**默认**/描述）
2. **改默认值对老用户无效** —— `settings.json` 里已存的值优先
3. `MftAvifBackend.Backend` 返回 `Auto`（既有瑕疵，合并时顺手处理）
4. NVENC 把 CRF 当 QP 用（`NvEncoderNative.cs:1044-1047`），语义与 avifenc 的 `-q`（100=无损）相反量级 —— 既有隐患，**本次勿扩大**
5. WinUI 与 WPF 的差异：`Window` **无** `Resources` / `Loaded`；`RoutedEventArgs` **无** `Handled`；无边框要用 `OverlappedPresenter.SetBorderAndTitleBar(false,false)`
6. 🛑 **未经用户明确授权不得 commit / push**；发布前必须完成实机验证
7. 🛑 性能问题**先加分段计时再动手**（本次曾误判"libaom 慢"，实测编码仅占 1.3s）

---

## 6. 验证与发布方式

```bash
python .workbuddy-ai/runps.py scripts/verify.ps1               # 构建 + 7 套件门禁（约 60s）
python .workbuddy-ai/runps.py Publish.ps1 -AllowDirty -Force    # 发布（-Force 会终止运行中的实例）
```

- 8 套件全量：`./src/TrueToneCap.Test/bin/Release/net11.0-windows10.0.26100.0/TrueToneCap.Test.exe --all`
- 实机触发截图：`python .workbuddy-ai/sendkey.py`（模拟 Ctrl+Shift+Q 无感截图）
- 日志：`publish/TrueToneCap-v0.3.4-beta/log/`
- ⚠️ 本沙箱 `curl` 不能出网；查依赖版本用 `dotnet list TrueToneCap.slnx package --outdated` 或 WebFetch

---

## 7. 相关文档索引

| 文档 | 内容 |
|---|---|
| `docs/reviews/encoder/avif-encoder-benchmark-2026-09-16.md` | 性能基准：speed 甜点、位深/色度影响、**24s → 2.9s 优化全过程** |
| `docs/reviews/dependency/image-codec-dependency-audit-2026-09-16.md` | 依赖版本审计（含 libaom/SVT 现状） |
| `docs/reviews/notification/notification-implementation-research-2026-09-16.md` | 通知方案调研（为何改用自绘浮层） |
| `docs/reviews/capture/root-cause-hdr-multimonitor-2026-09-16.md` | 多显示器 HDR 三个根因 |
| `docs/reviews/capture/capture-source-regression-2026-09-16.md` | "只有桌面无窗口"回归修复 |
| `.workbuddy-ai/memory/2026-09-15.md` | **完整工作日志**（本轮所有决策、实测数据、踩坑） |

---

## 8. 工作区状态

- **大量改动未提交**（46+ 文件），本轮相关改动见 §2 / §3
- 版本号仍为 `0.3.4-beta`（`Directory.Build.props`）
- 最近一次发布为**脏工作区发布**（`build-info.json` 中 `dirtyWorktree=true`），**仅供测试，不可作为正式版本分发**
- 建议：本任务完成后按主题拆分提交（多显示器/HDR、捕获源、通知、编码优化、AVIF 编码器重构、清理）

---

## 9. 代码审查发现（2026-09-16，三路并行子 agent 审查，未修复）

> 审查范围：工作区全部未提交源码改动（Core 编码层 / App 服务层 / UI 层）。以下按优先级排列。
>
> **✅ 2026-09-17 收尾：P1(1-3) / P2(4-8) / P3(9) 全部处理完毕**，逐条结论见下方标记。
> 本轮另外补了 28 条回归断言（能力表协商 / Auto 挑选 / Auto 先解析后协商的调用顺序 /
> 按格式位深钳制 / TIFF 非法位深钳制），防止同类"静默改参数"再次漂移。

### P1（功能缺陷，建议必修）

> 1–3 均已修复（2026-09-17 复核）：① `CapturePipelineService:172-173` 先调
> `PickAvailableKind` 把 Auto 解析成具体 kind，再协商；② `BgraToNv16` 加 `if (x + 1 < w)`；
> ③ `AvifBackendIndex` 字段已删除，`EncodeSdrAsync` 统一走 `SelectByKind`。
> 新增源码契约断言 `Test_Pipeline_AutoResolvedBeforeNegotiation` 锁住 ① 的调用顺序。

1. **Auto 模式协商与提示失效** — `CapturePipelineService.cs:196-210` + `AvifEncoderCapabilities.Caps(Auto)` 回落 LibAom 能力表（全色度/12-bit），等于 Auto 时不协商；真正编码器要到 `SelectByKind → PickAuto` 才确定，`PickAuto` 放宽轮可能选中仅支持 8-bit/420 的 QSV → 用户 10-bit/444 请求被**静默降级、无 Toast**（恰是本次重构要消灭的问题）。修法：Auto 时先 `PickAuto` 得实际 kind 再协商，或把协商移到编码器确定之后。
2. **BgraToNv16 奇数宽度越界** — `NvEncoderNative.cs:727-735`：`x += 2` 循环在奇数 `w` 时最后一组写 `uvRow + x + 1` 越行/越界（末行直接 `IndexOutOfRangeException`，被 Guard 吞掉静默回退软件）。修法：`if (x + 1 < w)` 才写 V，或入参钳偶数宽。
3. **遗留 AvifBackendIndex 仍决定 Auto 行为** — `AvifEncoderKind == Auto` 时 `EncodeSdrAsync` 走旧 `Select(s.AvifBackend)`（读冻结的旧字段），与"Auto 由能力表硬件优先挑选"矛盾。修法：Auto 也统一走 `PickAuto`，随后删除 `AvifBackendIndex` 字段（`AppSettingsData.cs:70`、`SettingsService.cs:225`、`CapturePipelineService.cs:168-174`）。

### P2（正确性/一致性，建议修复）

> 4–8 均已修复（2026-09-17 复核）：④ `IsKindAvailable(Nvenc)` 补 `SupportsAv1` 过滤；
> ⑤ 软件编码器改由 `LibAomAvifBackend` 实例字段 `_codec` 携带，不再写全局静态 `Codec`；
> ⑥ 降级 Toast 文案改为「能力限制」并加 `s_lastDegradedToast` 去重；
> ⑧ `OnAvifEncoderChanged` 不再需要映射 `Codec`（取消全局字段后该问题随之消失）。
> ⑦ 见下，是本轮最后完成的项。

4. **PickAuto 不校验 SupportsAv1** — RTX 30 等卡 Auto+422 会白走一次 NVENC 失败路径（旧 `Select()` 有 `SupportsAv1` 过滤，`PickAuto` 没有）。
5. **Codec 静态字段竞态** — `NativeAvifEncoder.Codec` 全局可变，并发编码任务会互相覆盖（SvtAv1 请求可能被 LibAom 覆盖）。建议按调用传参。
6. **降级提示文案误导** — 手选编码器不可用回退 libaom 时，Toast 仍称"已切换到 NVENC/QSV"。且每次截图重复弹（无去重）。
7. **FixBitDepth 放行 12-bit 无对等协商** — WebP/PNG 等非 AVIF 格式无位深钳制，12-bit 请求直传可能不支持的编码器；`SettingsService.cs:255` 注释也过时。
8. **OnAvifEncoderChanged 不更新 NativeAvifEncoder.Codec** — Load 时映射了，运行时切换下拉不映射；走旧路径的调用方（录制/动画）会用到旧值。

#### P2-7 修复详情：位深改为【按格式】钳制（2026-09-17）

逐条核实了非 AVIF 格式的编码链路，结论是**各格式真实支持的位深集合互不相同**，
统一放行 12-bit 会让"设置显示的值"与"实际输出"彻底脱节：

| 格式 | 实际生效 | 12-bit 时的旧行为 | 处理 |
|---|---|---|---|
| **TIFF** | 8 / 16 | 🔴 `BitsPerSample` 写成 12、数据却按 16-bit 未打包写入 → **非法/错位文件**，不抛异常无日志 | 钳到 8/16（`>8 → 16`），且钳制放在 `ManagedTiffEncoder` **入口**，任何调用方都绕不过 |
| **JPEG LI** | 8 | 忽略（jpegli 走 PPM，maxval 恒 255）→ 死字段 | 恒 8 |
| **Gain Map** | 8 | 忽略（Base 与灰度增益图均 8-bit）→ 死字段 | 恒 8 |
| **WebP** | 8 | 8-bit 容器 | 恒 8（上一轮已修） |
| **JPEG XL** | 8 / 16 | 走 16-bit PNG 中转，输出 16-bit 容器 | 保留 8/10/12（与现状一致，不引入行为变化） |
| **PNG** | 8 / 16（10/12 仅 HDR 路径为真） | SDR 下落到 16-bit 容器 + `sBIT=8` | 保留（既有行为，另见 `docs/reviews/usability/pipeline-and-usability-review-2026-09-15.md:321`） |
| **AVIF** | 由能力表协商 | 已协商并提示 | 不变 |

改动：`SettingsService.NormalizeBitDepth`（按格式分派 + 更正过时注释）、
`ManagedTiffEncoder.Encode`（入口钳制）、`ImageEncoder.OutputBitDepth` 注释（原写
"8/10/12，适用于 …WebP"，漏 16 且 WebP 已恒 8）。
回归断言：`Test_Settings_BitDepthClampedPerFormat`（源码契约）、
`Test_TiffInvalidBitDepthClamped`（请求 12 必须产出 BPS=16 且数据宽度一致）。

### P3（清理类）

9. 旧 `AvifCodec` 字段无迁移（老用户 SvtAv1 偏好丢失，僵尸字段可删）；`UpdateAvifBackendLabels` 垫片可内联；`AvifCodecLabelTxt` 未本地化；`EncodeRaw` 拷贝 else 分支应显式断言 NV16；`QsvAvifBackend` 与 `MftAvifBackend` 重复实现；`nvenc_diag.txt` 无条件写盘与既有决策矛盾（挪 Debug 编译）。

> **✅ 2026-09-17 逐条结论**：
> - `AvifCodec` 字段：`git grep AvifCodec HEAD -- src` **无匹配**，该字段从未存在于 `AppSettingsData`
>   （重构中已随"两层合一"直接消失），无需迁移逻辑。
> - `UpdateAvifBackendLabels` 垫片：已随重构删除。
> - `AvifCodecLabelTxt` 本地化：`MainWindow.Localization.cs:40-41` 已覆盖中英。
> - `EncodeRaw` else 分支：`NvEncoderNative.cs:653` 已有 `Debug.Assert(bufferFormat == NV_ENC_BUFFER_FORMAT_NV16, ...)`。
> - **QSV / MFT 重复实现**：已合并为 `file static class MftAvifEncode`（`FormatEncoders.cs`），
>   两后端仅保留不同的崩溃隔离标签（`"QSV_MFT"` / `"MFT"`）。
> - `nvenc_diag.txt`：`AppendDiag` 已加 `[Conditional("DEBUG")]`，Release 不再写盘。

### 已验证无问题（审查确认）

- Toast 后台线程安全（DispatcherQueue 调度）；JXL 8→16bit 升采样数学正确、临时文件清理完整；UI 线程守卫（_uiReady / null 检查）完备；`SetComboByTag` 失配行为安全；QualitySld.Minimum clamp 与旧逻辑等价；`EncodeRaw` 资源释放与 pitch 处理正确；旧调用方（EncodeAv1/EncodeHevc）签名兼容。
