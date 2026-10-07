# 依赖与版本更新分析（全景）

> 日期：2026-10-07
> 状态：**✅ 已全部执行完毕**（同日）—— 逐项结果见文末 §9「执行记录」
> 方法：**逐个对上游注册表/仓库实时查询**（NuGet flatcontainer API、GitHub releases/Atom、libpng CHANGES、
> libavif 的 CMake 源），并用**二进制字符串提取**核实清单里未记录的版本。
> 与前作 `dependency-inventory-2026-09-14.md` 的关系：那份早于 CPM/锁定文件/本次 WinML 迁移，
> **已部分过时**（如仍写 `Microsoft.ML.OnnxRuntime.DirectML`、DI 为 `11.0.0-preview.6`）。本文为准。

---

## 0. 结论摘要

| 类别 | 有可用更新 | 建议立即处理 |
|---|---|---|
| NuGet 直接依赖 | 4 项（其中 1 项为 WinAppSDK 大版本） | 2 项 |
| NuGet 传递依赖 | 随 WinAppSDK 一并解决 | — |
| **原生工具链** | **5 项**（libjxl / libwebp / SVT-AV1 / libaom / libpng） | **1 项安全驱动** |
| .NET SDK | 1 项（rc.1） | 建议（解除预览基线） |
| GitHub Actions | 2 项（checkout v4→v7） | 建议 |
| OCR 模型 | **无**（已是最新 medium） | — |

**唯一的安全驱动项**：`avifenc.exe` 内静态链接的 **libpng 1.6.58 存在 CVE-2026-46675**（已修于 1.6.59）。
⚠ 但**实际可利用性很低**，理由见 §3.1 —— 本应用**只用 libpng 编码、从不解码不可信 PNG**。

---

## 1. NuGet 依赖（CPM 集中管理）

### 1.1 直接依赖现状 vs 最新

| 包 | 当前 | 最新稳定 | 最新任意 | 项目 | 评估 |
|---|---|---|---|---|---|
| `Microsoft.WindowsAppSDK` | **2.4.0** | **2.5.1** | 2.5.4-experimental | App, LogViewer | 🟡 有更新，牵动传递树 |
| `Microsoft.Graphics.Win2D` | **1.4.0** | **1.4.0** | — | App | ✅ 已最新 |
| `Microsoft.Extensions.DependencyInjection` | **10.0.12** | **10.0.12** | 11.0.0-rc.1 | App | ✅ 已最新（且刻意留在 10.x LTS） |
| `Microsoft.Windows.AI.MachineLearning` | **2.4.89** | **2.4.89** | 2.6.74-rc | Core | ✅ 已最新稳定（本次迁移刚定） |
| `Vortice.Direct3D11` | **3.8.3** | **3.8.3** | — | Core | ✅ 已最新 |
| `Vortice.DXGI` | **3.8.3** | **3.8.3** | — | Core | ✅ 已最新 |
| `System.Drawing.Common` | **10.0.11** | **10.0.12** | 11.0.0-rc.1 | Test, Tools | 🟢 补丁级，可升 |

**可升级且低风险**：`System.Drawing.Common` 10.0.11 → **10.0.12**（同一 LTS 线的补丁）。

### 1.2 `Microsoft.WindowsAppSDK` 2.4.0 → 2.5.1（唯一的大版本跨越）

这是**最有价值也最需要谨慎**的一项。传递依赖实测（`--include-transitive`）：

| 传递包 | 当前（2.4.0 带来） | 最新稳定 | 落后 |
|---|---|---|---|
| `Microsoft.WindowsAppSDK.Runtime` | 2.4.0 | 2.5.1 | 1 个小版本 |
| `Microsoft.WindowsAppSDK.AI` | 2.4.4 | 2.5.5 | |
| `Microsoft.WindowsAppSDK.Search` | 2.4.4 | 2.5.5 | |
| `Microsoft.WindowsAppSDK.Foundation` | 2.3.9 | 2.3.12 | |
| `Microsoft.WindowsAppSDK.WinUI` | 2.3.6 | 2.3.9 | |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | 2.1.6 | 2.1.9 | |
| `Microsoft.WindowsAppSDK.ML` | **2.1.74** | **2.1.94** | ← 影响 ORT！ |
| `Microsoft.WindowsAppSDK.Base` | 2.0.4 | 2.0.4 | 已最新 |
| `Microsoft.WindowsAppSDK.DWrite` | 2.1.0 | 2.1.0 | 已最新 |
| `Microsoft.WindowsAppSDK.Widgets` | 2.0.5 | 2.0.5 | 已最新 |
| `Microsoft.Web.WebView2` | 1.0.3719.77 | **1.0.4258.31** | 较旧 |
| `SharpGen.Runtime(.COM)` | 2.4.2-beta | （1.2.1 稳定 / 2.4.2-beta 更新） | 见下 |
| `System.Numerics.Tensors` | 9.0.0 | 10.0.12 | |

**⚠️ 与本仓的耦合点**：`Microsoft.WindowsAppSDK.ML` **2.1.74** 内含 **ORT 1.24**。
本次 WinML 迁移后，App 因显式引用 `Microsoft.Windows.AI.MachineLearning` **2.4.89**，由 NuGet
*nearest-wins* 正确解析到 **2.4.89 / ORT 1.27**（已实测输出确认）。
但 **LogViewer 仍解析到 2.1.74 / ORT 1.24**（它不引用 Core，不跑 OCR，属既有状态）。

⇒ 升 WinAppSDK 到 2.5.1 会让 `WindowsAppSDK.ML` 前进到 2.1.94，
**进一步降低**"传递引入的旧 ORT 与显式 WinML 共存"的复杂度。

**⚠️ SharpGen.Runtime 2.4.2-beta 是预发布版**，由 `Microsoft.Graphics.Win2D` 传递引入。
本仓 `Directory.Packages.props:26-32` 已明确记录过一次因预发布包（DI 11.0.0-rc.1）可能被下架
而导致 `NU1101` 的教训。**这条属于同类隐患**：`2.4.2-beta` 若被取消列出，全新机器还原会失败。
建议在升级 WinAppSDK / Win2D 时**复核它是否已转正**。

**升级代价（必须知道）**：本仓启用 `RestorePackagesWithLockFile=true`（`Directory.Build.props:82`）
且 CI 用 `dotnet restore --locked-mode`（`ci.yml:105`）。任何版本变动都必须**重新生成 5 个
`packages.lock.json`** 并一并提交，否则 CI 直接红。

**建议**：单独一轮做，先升 `System.Drawing.Common`（补丁级）验证流程，再评估 WinAppSDK 2.5.1
（需跑完整的 8 套件 + selftest + OCR 回归，因它牵动 WinUI/AI/ML 多个卫星包）。

---

## 2. .NET SDK / TFM

| 项 | 当前 | 最新 | 说明 |
|---|---|---|---|
| `global.json` SDK | **11.0.100-preview.7.26381.103** | **11.0.100-rc.1.26425.128** | 🟡 落后一个里程碑 |
| 实测使用中的 SDK | `11.0.100-rc.1.26425.128` | 同左 | 由 `rollForward: latestPatch` 自动提升 |
| TFM | `net11.0-windows10.0.26100.0` | — | 见下 |
| .NET 11 状态 | **go-live（非 GA）** | — | `eol` 仍为空 |

**关键事实**：`.NET 11` **尚未 GA**。官方 release metadata 显示 channel 11.0 为 `support-phase: go-live`，
最新产物就是 **rc.1（2026-09-08）**，其后**没有** RTM。

⇒ 本仓 `Directory.Packages.props:31-32` 写的"待 .NET 11 GA 后应整体评估升回 11.x"
**现在还不能执行** —— 没有 GA 可升。当前 rc.1 就是可获得的最新。

**但有一个真实问题**：`global.json` 钉的是 **preview.7**，而实际构建用的是 **rc.1**。
构建能通过纯靠 `rollForward: latestPatch` 的自动提升。这带来两个隐患：
1. **不可复现**：任何装有 `11.0.1xx` 其他补丁的机器，都会静默用另一个 SDK 构建，而 `global.json` 不反映真实基线。
2. **与 `dependency-management.md` 的"版本唯一、可复现"目标相悖**。

**建议**：把 `global.json` 的 `version` 更新为 **`11.0.100-rc.1.26425.128`**（与实际使用一致），
消除"声明与实际不符"。这属于**声明修正，不改变构建结果**，风险极低。
同时把 `Directory.Packages.props` 里"待 GA 后升回 11.x"的注释保留（仍然成立）。

---

## 3. 原生工具链

### 3.1 🔴 安全项：avifenc 内嵌 libpng 1.6.58（CVE-2026-46675）

**已从两处独立核实**：

1. **二进制实测**：从随包的 `avifenc.exe` 里提取出字符串 `1.6.58` — 证实清单记载属实。
2. **上游权威源**：libpng 自己的 `CHANGES` 原文（v1.6.59 条目）：

```
Version 1.6.59 [September 28, 2026]
  Fixed CVE-2026-46675 (medium severity):
    Use-after-free of zlib input in `png_read_end` after incomplete zTXt, iTXt
    or iCCP decompression.
```

> 注：NVD 查询 `totalResults: 0`、OSV 返回 404 —— 属**收录滞后**，不代表 CVE 不存在。
> 上游 CHANGES 是权威来源，已据其采信。

**⚠️ 但实际可利用性很低（重要，避免过度反应）**：

| 事实 | 依据 |
|---|---|
| 该 CVE 位于 **`png_read_end` 解码路径**，触发条件是**畸形/截断的 PNG 文本或 ICC 块** | 上游 CHANGES |
| 本应用**只编码 PNG、从不解码不可信 PNG** | 全仓搜索 `png_read`/`libpng`/`png_image_begin_read` 仅命中 **2 处，且均为注释**（`ThirdPartyNoticesWindow.xaml.cs:6` 的许可致谢列表、`NativeAvifEncoder.cs:30` 的构建说明）—— **无任何实际解码调用** |
| 应用内的 PNG 由**自研 `ManagedPngEncoder`** 生成（`FormatEncoders.cs:57/77/107/416`），非第三方解码器 | 源码实测 |
| 交给 avifenc 的中间 PNG 是**应用自己写的**（`ManagedPngEncoder.Encode16`，`FormatEncoders.cs:416`） | 源码实测 |
| ⇒ 攻击者需要先能**篡改本机临时目录里的中转 PNG**，才可能触发 | 推论 |

**结论**：这是**真实的版本落后**，但不是"用户打开恶意图片即中招"那类漏洞。
定级为**中等**、按常规升级节奏处理即可。

**⚠️ 升级比看起来复杂 —— 这是本次分析最容易被误判的一点**：

libpng 1.6.58 **不是本仓选的**，而是 **libavif v1.4.2 自己钉的**。
已从 `libavif/cmake/Modules/LocalZlibpng.cmake`（tag v1.4.2）核实原文：

```cmake
set(AVIF_ZLIB_GIT_TAG v1.3.2)
set(AVIF_LIBPNG_GIT_TAG v1.6.58)
```

且它是**普通变量（非 CACHE）** ⇒ **无法用 `-DAVIF_LIBPNG_GIT_TAG=...` 覆盖**。
而 libavif 最新版就是 **v1.4.2**（没有更新的版本来带 1.6.59）。

**可行的缓解路径（无需给 libavif 打补丁）**：同一个 CMake 文件里有现成的旁路 ——

```cmake
if(EXISTS "${AVIF_SOURCE_DIR}/ext/libpng")
    set(FETCHCONTENT_SOURCE_DIR_LIBPNG "${AVIF_SOURCE_DIR}/ext/libpng")
```

⇒ **把 libpng v1.6.59 源码放到 `libavif/ext/libpng/`，libavif 就会改用本地源码而跳过它的 git pin。**

**操作清单**（遵循 `tools-manifest.json` 自述的升级顺序）：
1. 取 libpng v1.6.59 源码 → 放入 `<workdir>/libavif/ext/libpng/`
2. 按 `scripts/build-avifenc.py` 重新构建（脚本头部会提示先 clone libavif v1.4.2）
3. 覆盖 `src/TrueToneCap.Core/Resources/avifenc.exe`；**重算 sha256/sizeBytes 回填 `tools-manifest.json`**
4. 删除旧的解压副本（`find src -path '*/bin/*' -name avifenc.exe -delete`），否则测试仍跑旧二进制
5. 跑 `--avif-caps --ffprobe` 确认编码能力；跑 `--ocr-*`/编码回归
6. 更新 `build-avifenc.py` 头部与 `tools-manifest.json` 的 `versionDetail`（libpng 1.6.58 → 1.6.59）

> ⚠️ 也可**顺带**把 SVT-AV1 从 v4.1.0 升到 v4.2.0、libaom 从 3.14.1 升到 3.15.1（见 §3.3），
> 因为它们同样由 libavif 的 pin 决定，一次构建可一并处理。**但建议分开**：安全升级与功能升级
> 混在一起，出问题难以二分定位。

### 3.2 原生工具全景

| 工具/组件 | 当前 | 最新上游 | 落后 | CVE | 建议 |
|---|---|---|---|---|---|
| `libavif`（avifenc 本体） | **1.4.2** | **1.4.2** | ✅ 已最新 | 无 | — |
| `libjxl`（cjxl / cjpegli） | **0.11.2** | **0.12.0** | 1 个次版本 | 无开放 CVE，但见下 | 🟡 建议升 |
| `libwebp`（cwebp） | **1.5.0** | **1.6.0** | 1 个次版本 | **无** | 🟢 可选 |
| `SVT-AV1`（编入 avifenc） | **v4.1.0** | **v4.2.0** | 1 个次版本 | 无 | 🟢 可选 |
| `libaom`（编入 avifenc） | **3.14.1** | **3.15.1** (2026-09-23) | 1 个次版本 | 无 | 🟢 可选 |
| `zlib`（编入 avifenc） | **1.3.2** | **1.3.2** | ✅ 已最新 | 无 | — |
| **`libpng`（编入 avifenc）** | **1.6.58** | **1.6.59** | 1 个补丁 | **CVE-2026-46675** | 🔴 **建议升级** |
| `libjpeg-turbo`（编入 cjpegli） | **3.1.2** ← 实测补全 | **3.2.0** | 1 个次版本 | **无**（3.x 全部无 CVE） | 🟢 可选 |

**关于 `libjpeg-turbo`（原清单写 "unspecified"）**：本次从二进制字符串提取**补全了**该信息 ——
`cjpegli.exe` 内含 `libjpeg-turbo version 3.1.2`。
这很重要：**libjpeg-turbo 的 CVE 全部止于 2.1.90**，3.x 系列**零已知 CVE** ⇒ 当前**无安全暴露**。
建议把该版本号回填进 `tools-manifest.json`（目前是 "unspecified"，无法审计）。

**libjxl 0.11.2 的细致判断**（避免误报）：
- 两个近期 CVE（CVE-2025-12474、CVE-2026-1837）的影响区间**均止于 0.11.1** ⇒ **0.11.2 无开放 CVE**
- 但 0.12.0 的 release notes 有**大段 Security 章节**（整数溢出/越界读写/缓冲区溢出等数十个加固 PR，**多数未单独分配 CVE**），
  且 libjxl 自己的 `SECURITY.md` 声明"发布可能包含未逐一记录的已修安全问题"
- ⇒ **无 CVE ≠ 无风险**。升到 0.12.0 仍是更安全的选择，但属"加固"而非"止血"

**⚠️ cjpegli/cjxl 的升级路径不同**：它们是**上游预编译发布件**（非自编译），
升级 = 换二进制 + 重算 sha256 + 更新清单；`cjpegli` 无 `--version` 输出，
清单里其版本是**按同批次发行推断**的（原文已注明"待上游确认"）。

### 3.3 上游 pin 的连带关系（升级时容易踩）

`avifenc.exe` 的 5 个内嵌组件版本**不由本仓决定**，而由 **libavif v1.4.2 自己的 FetchContent pin** 决定：

```
libavif v1.4.2
  ├─ SVT-AV1  v4.1.0   ← libavif 的 pin（不是 SVT-AV1 独立最新 v4.2.0）
  ├─ libaom   3.14.1
  ├─ zlib     v1.3.2
  ├─ libpng   v1.6.58  ← CVE 所在
  └─ libjpeg-turbo（libavif 的 pin）
```

⇒ 想升其中任何一个，都得用 §3.1 的 `ext/<dep>/` 旁路或等 libavif 更新。
**这也是为什么"版本号对不上"是正常现象**：`build-avifenc.py:24` 已明确写了
"SVT-AV1 v4.1.0（libavif 的 pin —— **不是**独立 v4.2.0 tree）"。

---

## 4. GitHub Actions

| Action | 当前 | 最新 | 落后 |
|---|---|---|---|
| `actions/checkout` | **v4** | **v7.0.1** | **3 个大版本** |
| `actions/upload-artifact` | **v4** | **v7.0.1** | **3 个大版本** |

（另有 `actions/setup-dotnet` 最新 v6.0.0，但本仓**未使用** —— CI 走 self-hosted runner + 本机 SDK，
`ci.yml:9-10` 注释已说明 preview 版在 setup-dotnet 里未必可用。）

**评估**：两者都落后 3 个大版本。v4 仍受支持，但：
- GitHub 对旧版 Action 的运行时会定期弃用（如 Node20 → Node24 迁移），
  届时会出现"CI 因运行时弃用而告警/失败"这类**与代码无关**的红灯
- 本仓是 self-hosted runner，升级动作需一并确认 runner 兼容性

**建议**：升到 v5（保守）或直接 v7（跟齐）。**风险低**（这两个 action 的用法极简：
checkout 无额外参数、upload-artifact 传 path），改完跑一次 CI 即可验证。

---

## 5. OCR 模型与字典

| 资产 | 当前 | 上游最新 | 结论 |
|---|---|---|---|
| `PP-OCRv6_medium_det.onnx` | FP16 31,146,237 B | = 官方 medium（FP32 62,032,837 B） | ✅ **已是最新档** |
| `PP-OCRv6_medium_rec.onnx` | FP16 38,399,862 B | = 官方 medium（FP32 76,554,979 B） | ✅ **已是最新档** |
| `ppocrv6_dict.txt` | 18,708 行 | 与官方 `inference.yml` 的 character_dict 逐条一致 | ✅ 一致 |

官方还有 **tiny（det 1.7 MB / rec 4.3 MB）** 与 **small（det 9.4 MB / rec 20.2 MB）** 档，
但它们相对 medium 是**精度降级**，不是"更新"。详见 `docs/ocr-model-replaceability-2026-10-07.md`。

**唯一"新能力"型更新**：官方有 **语言专用 rec 模型**（如 `korean_PP-OCRv5_mobile_rec.onnx` 12.8 MB，
实测可让韩文 0% → 100%）。这属于**功能新增**而非版本升级，已在该文档中单列建议。

---

## 6. 建议执行顺序

### 第 1 批（低风险，声明/补丁级，建议立即）

| # | 动作 | 风险 | 验证 |
|---|---|---|---|
| 1 | `global.json` SDK 版本 → `11.0.100-rc.1.26425.128`（与实际一致） | 极低（纯声明） | `dotnet --version` 一致 + 全量构建 |
| 2 | `System.Drawing.Common` 10.0.11 → **10.0.12** | 低（同 LTS 线补丁） | 重生成 lock + 8 套件 |
| 3 | `actions/checkout` / `upload-artifact` v4 → **v5 或 v7** | 低 | 跑一次 CI |
| 4 | `tools-manifest.json` 补记 `libjpeg-turbo 3.1.2`（结束 "unspecified"） | 极低（纯记录） | 重算 sha 不变，仅补字段 |

### 第 2 批（安全驱动，需重新编译，建议本轮处理）

| # | 动作 | 风险 | 验证 |
|---|---|---|---|
| 5 | **avifenc 内嵌 libpng 1.6.58 → 1.6.59**（用 `ext/libpng/` 旁路） | 中（需重建 + 重算哈希） | `--avif-caps --ffprobe` + 编码回归 + 删旧副本 |
| 6 | 顺带确认 libaom/SVT-AV1 是否借同一次构建升到 3.15.1 / v4.2.0 | 中 | 同上；**建议单独一轮**以便二分定位 |

### 第 3 批（功能/加固级，评估后决定）

| # | 动作 | 风险 | 备注 |
|---|---|---|---|
| 7 | `libjxl` 0.11.2 → **0.12.0**（加固 delta 大） | 中 | 换预编译件；需重跑颜色/JXL 回归 |
| 8 | `libwebp` 1.5.0 → 1.6.0 | 低-中 | 维护性发布，优先级低 |
| 9 | `Microsoft.WindowsAppSDK` 2.4.0 → **2.5.1** | **较高** | 牵动 WinUI/AI/ML/WebView2 多个卫星包 + 5 个 lock 文件；须全套回归 |

---

## 7. 需要留意的两个结构性问题（非本次更新范围，但值得记录）

1. **预发布依赖的存在**：`SharpGen.Runtime 2.4.2-beta`（经 Win2D 传递）是预发布版。
   本仓已因同类问题（DI 11.0.0-rc.1 可能被下架 → `NU1101`）降级过一次（`Directory.Packages.props:26-32`）。
   建议在升 WinAppSDK/Win2D 时一并复核它是否已转正。

2. **TFM 与 SDK 都在预览线上**：`net11.0-windows10.0.26100.0` + `11.0.100-rc.1`。
   .NET 11 尚未 GA（`support-phase: go-live`，无 `eol`）。GA 后应做一次**整体**评估
   （TFM、`System.Drawing.Common` 11.x、`Microsoft.Extensions.*` 11.x 一起升），
   而不是零散升包 —— 当前混用 10.x/11.x 是权宜之计，文件里已如实记录。

---

## 8. 复现方式

```powershell
# NuGet 最新版本（本报告全部数字来源）
$id = "microsoft.windowsappsdk"
(Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$id/index.json").Content

# 实际解析到的版本（含传递）
dotnet list src/TrueToneCap.App/TrueToneCap.App.csproj package --include-transitive

# 锁定模式还原（CI 同款门禁）
dotnet restore TrueToneCap.slnx --locked-mode

# .NET 发布状态（判断 .NET 11 是否 GA）
(Invoke-WebRequest "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/11.0/releases.json").Content

# 原生工具上游（GitHub Atom，规避 REST 限流）
[xml](Invoke-WebRequest "https://github.com/pnggroup/libpng/releases.atom").Content

# 二进制内嵌版本（清单未记录时用这招）
$b=[IO.File]::ReadAllBytes("src/TrueToneCap.Core/Resources/cjpegli.exe")
[Text.Encoding]::ASCII.GetString($b) -split "`0" | Select-String "libjpeg-turbo|libpng"

# libavif 的依赖 pin（决定 avifenc 内嵌组件版本）
(Invoke-WebRequest "https://raw.githubusercontent.com/AOMediaCodec/libavif/v1.4.2/cmake/Modules/LocalZlibpng.cmake").Content

# libpng CVE 权威来源
(Invoke-WebRequest "https://raw.githubusercontent.com/pnggroup/libpng/libpng16/CHANGES").Content
```

**关键文件定位**

| 关注点 | 位置 |
|---|---|
| NuGet 版本单一事实源 | `Directory.Packages.props` |
| 锁定文件开关 | `Directory.Build.props:82`（`RestorePackagesWithLockFile`） |
| CI 锁定模式门禁 | `.github/workflows/ci.yml:105` |
| SDK 钉版 | `global.json` |
| 原生工具版本/哈希/许可 | `src/TrueToneCap.Core/Resources/tools-manifest.json` |
| avifenc 构建配方 | `scripts/build-avifenc.py`（依赖 pin 说明在文件头 22-25 行） |
| OCR 模型清单 | `src/TrueToneCap.App/Models/models-manifest.json` |
| 依赖管理规范 | `docs/dependency-management.md` |

---

## 9. 执行记录（2026-10-07 同日完成）

### 9.1 逐项结果

| 批次 | 项目 | 原 | 新 | 结果 |
|---|---|---|---|---|
| 1a | `global.json` SDK | 11.0.100-preview.7 | **11.0.100-rc.1.26425.128** | ✅ 声明与实际一致 |
| 1b | `System.Drawing.Common` | 10.0.11 | **10.0.12** | ✅ lock 已重生成 |
| 1c | `actions/checkout` | v4 (node20) | **v7 (node24)** | ✅ YAML 经 PyYAML 校验 |
| 1c | `actions/upload-artifact` | v4 (node20) | **v7 (node24)** | ✅ |
| 1d | `libjpeg-turbo` 版本记录 | "unspecified" | **3.1.2**（二进制实测） | ✅ 回填 manifest |
| 2 | avifenc 内嵌 **libpng** | 1.6.58（CVE） | **1.6.59** | ✅ 重建 + 哈希回填 |
| 3a | `Microsoft.WindowsAppSDK` | 2.4.0 | **2.5.1** | ✅ 含 6 个卫星包前移 |
| 3b | **cjpegli** | libjxl 0.11.2 预编译件 | **google/jpegli 自编译（0.72 MB）** | ✅ 输出逐字节等价 |
| 3c | **cjxl** | 0.11.2 | **0.12.0** | ✅ 全量回归通过 |
| — | libwebp | 1.5.0 | （未升） | ⏸ 无 CVE，1.6.0 为维护性发布，优先级最低 |

### 9.2 执行中发现、计划里没预料到的三件事

1. **Node 20 已过期，Actions 升级是必须而非可选。**
   计划里把 Actions 列为"建议（低风险）"。实际查到 GitHub 官方 changelog 写明
   Node 20 *"will only work until we upgrade the runner and remove Node20 on September 23rd, 2026"* ——
   **该日期已过去**。v4 系列基于 node20 ⇒ 继续留在 v4 不是保守而是**已过期用法**。
   同时发现**升级有前置条件**：自托管 runner 必须 ≥ **v2.335.0**（v2.333.0 起为 node24 enforcement，
   v2.335.0 加入 node24 支持）。runner 不在本机，无法代查，已在两个 workflow 就地注明。

2. **libjxl 0.12.0 移除了整个 jpegli 代码库。**
   计划里"libjxl → 0.12.0"是一条独立项。实际执行时发现 0.12.0 的 Windows 发布包
   **不再含 `cjpegli.exe`**（0.11.2 有），而上游把它拆成了 `google/jpegli` ——
   该仓库**没有任何 Release**。本应用依赖 cjpegli 实现 JPEG LI（52 处引用 / 13 文件 / selftest 覆盖），
   故必须把 cjpegli 一并自编译才能成套升级。已新增 `scripts/build-cjpegli.py`。

3. **自编译 cjpegli 会引入依赖倒挂，已规避。**
   jpegli 的 submodule 钉的是 **libpng 1.6.47** 与 **libjpeg-turbo 2.1.5.1** ——
   都低于本仓现有基线（libpng 1.6.59 / libjpeg-turbo 3.1.2），且 libjpeg-turbo 2.x **确有已知 CVE**。
   通过 `-DJPEGLI_ENABLE_JPEGLI_LIBJPEG=OFF -DJPEGLI_ENABLE_SJPEG=OFF -DBUILD_TESTING=OFF`，
   成品**完全不链接**这两者（二进制内无版本串），体积 5.13 MB → **0.72 MB**，
   且与旧件在四种配置下产出**逐字节相同**的 JPEG。

### 9.3 验证结论

| 判据 | 结果 |
|---|---|
| 构建（5 项目 Release） | ✅ 0 警告 0 错误 |
| 测试（8 套件） | ✅ **1820 通过 / 0 失败** |
| `--selftest` | ✅ **11/11** |
| OCR 回归（`--ocr-bench-real`） | ✅ 与基线完全一致（6421/7052、202/292、14 失败样本） |
| JXL 色彩探针（`--jxl-color-product`） | ✅ **exit 0**（SDR 2 层 + HDR 层全判且通过） |
| 工具 sha256/sizeBytes | ✅ 4 个 exe 全部回填并校验 |
| 许可台账 | ✅ INDEX.json 44 条哈希全对；与 manifest 双向闭合 |

### 9.4 附带收益：JXL 探针的 HDR 层首次真正被判

执行中发现 `--jxl-color-product` 的 HDR 层此前一直 **SKIP**，原因是缺参考解码器
（找不到 `jxlinfo.exe` / `djxl.exe`）。而 **libjxl 0.12.0 的发布包里正好含这两个 exe**，
用 `TTC_JXLINFO` / `TTC_DJXL` 指过去后 HDR 层**首次被真正判定且通过**（exit 0）。
⇒ 这条探针从此可以全绿运行，不再以 SKIP 记账。

### 9.5 仍未做（明确记录）

- **libwebp 1.5.0 → 1.6.0**：无 CVE 驱动，且本仓用的是上游预编译 `cwebp.exe`；
  1.6.0 被上游标为维护性发布。**留待下次常规升级**。
- **cjxl 仍内含 libpng 1.6.50**：低于 CVE-2026-46675 的修复版 1.6.59。
  未自编译 cjxl 的理由见 §3 与 manifest 的 `buildNotes`：自建需完整 libjxl 工具链，
  成本远高于收益，且该版本由 libjxl 自身 pin 决定、自建也改不了。
  实际可利用性低（应用只喂自研编码器产出的 PNG，不解码不可信 PNG）。
- **SharpGen.Runtime 2.4.2-beta**（经 Win2D 传递引入）仍是**预发布版**。
  升到 WinAppSDK 2.5.1 后它**未被前移**（仍是 2.4.2-beta）。本仓此前已因同类问题
  （DI 11.0.0-rc.1 可能被下架导致 NU1101）降级过一次 ⇒ **建议持续关注其是否转正**。
