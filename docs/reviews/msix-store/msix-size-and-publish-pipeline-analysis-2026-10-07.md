# 编译 / 发布管线分析 + MSIX 包体积与压缩

> 日期：2026-10-07
> 方法：**全部实测** —— 真实构建、真实解包、逐文件压缩测量、逐项配置翻转对比。
> 本文所有体积数字都是本机跑出来的，不是估算。

---

## 0. 结论摘要

| 问题 | 结论 |
|---|---|
| **MSIX 是否过大？** | **不算过大，但确实偏胖**。150.9 MB / 上架上限 25 GB ⇒ 只占 **0.6%** |
| **MSIX 有压缩吗？** | ✅ **有，而且已经在压** —— 333 MB 内容压到 150.9 MB（**压缩率 44.5%**） |
| **能压得更狠吗？** | ❌ **不能** —— `makeappx` 只有 `/nc`（**关闭**压缩），**没有"压得更狠"的选项**；算法固定为 Deflate |
| **那怎么变小？** | ✅ **靠"少放东西"，不是靠"压得更紧"**。实测最大可省 **66 MB（-44%）** |
| **应用自身占多少？** | **仅 1.5 MB（0.5%）** —— 99.5% 是运行时/框架/模型 |

**一句话**：压缩这一侧已经到顶了（Deflate 44.5%，无参数可调）；真正的空间在**依赖分发形态**
—— 两个 `SelfContained` 开关各能省 29 MB 与 37 MB，**同时关掉可省 66 MB（150.9 → 84.6 MB）**。

---

## 1. MSIX 压缩：现状实测

### 1.1 已经压了，且压得不差

| 指标 | 实测值 |
|---|---|
| 包内内容（解包后） | **333.0 MB** |
| MSIX 包实际大小 | **150.9 MB** |
| **压缩率** | **44.5%**（省下 182 MB） |
| 条目数 | 566（**552 个被压缩**，14 个未压缩） |

那 **14 个未被压缩的条目全部是 PNG**（磁贴图标 + WinUI 自带资源）——
这是**正确行为**：PNG 已自带压缩，再套一层 Deflate 只会更慢且几乎不省。

### 1.2 逐类可压缩性（实测）

| 类别 | 原始 | Deflate 后 | 压缩率 |
|---|---|---|---|
| 全部 `.dll` | 232.1 MB | 89.4 MB | **38.5%** |
| 原生编码器 | 22.1 MB | 9.2 MB | 41.7% |
| OCR 模型（`.onnx`） | 66.4 MB | 45.0 MB | **67.8%** ← 最差 |
| **合计** | **333.0 MB** | **148.1 MB** | **44.5%** |

**个别文件的压缩率**（说明为什么模型拖后腿）：

| 文件 | 原始 | Deflate 后 | 压缩率 |
|---|---|---|---|
| `PP-OCRv6_medium_rec.onnx` | 36.62 MB | 31.01 MB | **84.7%** ← 几乎压不动 |
| `PP-OCRv6_medium_det.onnx` | 29.70 MB | 13.95 MB | 47.0% |
| `Microsoft.Windows.SDK.NET.dll` | 25.99 MB | 6.41 MB | 24.6% |
| `onnxruntime.dll` | 22.02 MB | 7.03 MB | 31.9% |
| `DirectML.dll` | 17.83 MB | 9.02 MB | 50.6% |
| `System.Private.CoreLib.dll` | 17.38 MB | 7.19 MB | 41.4% |
| `avifenc.exe` | 15.68 MB | 6.41 MB | 40.9% |

**为什么 `rec.onnx` 压不动**：它是 **FP16 权重的神经网络**，浮点权重近似随机分布 ⇒ **熵很高** ⇒ Deflate 无从下手。
这在数学上无法通过换压缩器解决（见 §1.4 的 FP32 对照）。

### 1.3 `makeappx` 的压缩选项：**只有"关"，没有"调"**

从工具自身帮助文本（权威来源，非文档转述）实测：

```
/nc, /noCompress: Prevents MakeAppx from compressing files in the package.
    By default, files in the package are compressed based on detected file type.
```

**没有** `--compression-level`、`--best`、`--ultra` 之类的参数。
⇒ **压缩算法固定为 Deflate，压缩强度不可调。**

对 ZIP 容器而言 Deflate 就是可用算法的全部（MSIX 规范不接受 LZMA/Zstd 等）。

### 1.4 换模型的对照实验（回答"能不能靠换权重格式变小"）

拿官方 FP32 版本对比同一压缩器：

| 版本 | 原始 | Deflate 后 |
|---|---|---|
| **FP16（当前）** | 36.62 MB | **31.01 MB** |
| FP32（官方） | 73.01 MB | 63.66 MB |

⇒ **FP16 即使压不动，仍然是压倒性更优**（31 MB vs 64 MB）。
**当前用 FP16 是正确的选择**，不存在"换 FP32 反而更小"这种反直觉空间。

### 1.5 其他"压缩"手段的可行性

| 手段 | 结论 |
|---|---|
| `makeappx /nc`（关闭压缩） | ❌ 反方向，只会更大 |
| 换更强压缩算法 | ❌ MSIX 规范 + makeappx 都不支持 |
| **预先压缩内容**（如把 dll 打包成 cab 再包） | ❌ 应用运行时无法直接使用压缩态文件 |
| **`.msixbundle` + 按架构分包** | ⚠️ 仅在本应用出多架构时才有意义；当前**只出 x64**，无收益 |
| **按语言资源分包（resource package）** | ⚠️ 理论可省，但本包多语言资源只占 **3.4 MB**（`.mui`），**收益 <3 MB，不值得增加复杂度** |
| **流式安装 / 按需下载模型** | ✅ **有真实空间**（见 §3.3） |

---

## 2. 包内容构成（实测，决定"该往哪儿省"）

解包后按类别统计（333.0 MB）：

| 类别 | 体积 | 占比 | 能否省 |
|---|---|---|---|
| **WinAppSDK**（原生+托管） | **84.0 MB** | **25.2%** | ✅ 可改为框架依赖 |
| **.NET 运行时** | **76.4 MB** | **22.9%** | ✅ 可改为框架依赖 |
| **OCR 模型 + 字典** | **66.4 MB** | **19.9%** | ⚠️ 可改按需下载 |
| **ONNX Runtime + DirectML** | **40.1 MB** | **12.0%** | 🟡 难（推理必需） |
| 原生编码器 4 个 | 22.1 MB | 6.6% | 🟡 可考虑按需 |
| 着色器 / 杂项 | 5.6 MB | 1.7% | ❌ |
| **应用自身代码** | **1.5 MB** | **0.5%** | ❌ |
| **合计** | **333.0 MB** | 100% | |

**关键洞察：应用自己的代码只占 0.5%。**
⇒ 任何"优化自己的代码/资源"的努力上限就是 1.5 MB，**毫无意义**。
体积问题**100% 是依赖分发形态问题**。

---

## 3. 实测：能省多少（逐项翻转验证）

### 3.1 杠杆一：WinAppSDK 改框架依赖 —— **省 29 MB**

把 `WindowsAppSDKSelfContained` 从 `true` 改 `false`，实测：

| 配置 | MSIX 大小 |
|---|---|
| 自包含（当前） | 150.90 MB |
| **WinAppSDK 框架依赖** | **121.89 MB** |
| **节省** | **-29.0 MB（-19%）** |

官方文档已确认打包形态下可这样用，且**商店会自动安装对应的框架包依赖**。

### 3.2 杠杆二：.NET 运行时改框架依赖 —— **省 37 MB**

把 `SelfContained` 从 `true` 改 `false`，实测：

| 配置 | MSIX 大小 |
|---|---|
| 自包含（当前） | 150.90 MB |
| **.NET 框架依赖** | **113.58 MB** |
| **节省** | **-37.3 MB（-25%）** |

### 3.3 杠杆一 + 二同时 —— **省 66 MB（-44%）**

| 配置 | MSIX 大小 |
|---|---|
| 自包含（当前） | 150.90 MB |
| **两者都框架依赖** | **84.57 MB** |
| **总节省** | **-66.3 MB（-44%）** |

### 3.4 三个杠杆的取舍对比

> ⚠️ **2026-10-07 实现后的更正**：下面第一版数字是用 `dotnet build` 量的；
> 而**真实发布路径是 `Publish.ps1` → `dotnet publish`**，两者产出的 MSIX **不一样**：
> `dotnet publish` 会把复合 R2R 映像（122 MB）也打进包，`dotnet build` 不会。
> 以真实发布路径为准的完整矩阵见 §3.4b。

| 方案 | 大小 | 用户机器要求 | 商店适配 | 绿色包影响 |
|---|---|---|---|---|
| **保持自包含**（当前） | 150.9 MB | **无**（开箱即用） | ✅ 可用 | 无 |
| WinAppSDK 框架依赖 | 121.9 MB | 需 WinAppSDK 运行时 | ✅ 商店自动装 | ⚠️ 绿色包需另附运行时 |
| both 框架依赖 | **84.6 MB** | 需 .NET + WinAppSDK 运行时 | ✅ 商店自动装 | ⚠️ 绿色包显著复杂化 |

### 3.4b 实测体积矩阵（区分 build 与 publish）—— **以 publish 列为准**

| 模式 | `dotnet build` | **`dotnet publish`（真实发布路径）** |
|---|---|---|
| 自包含（默认） | 150.90 MB | **175.82 MB**（含 122 MB 复合 R2R 映像） |
| **框架依赖** | 84.57 MB | **84.57 MB**（R2R 被强制关闭） |

⇒ **用户的真实收益是 175.82 → 84.57 MB = −91.25 MB（−52%）**，
比按 `dotnet build` 估算的 −44% 更大 —— 因为默认模式在 publish 路径下还额外背着 R2R 映像。

> 📌 这也解释了一个先前没解释清楚的现象：**MSIX 里的 R2R 映像取决于用哪个命令**。
> 用 `dotnet build` 打时它不进包（打包枚举声明的项，R2R 是构建产物）；
> 用 `dotnet publish` 打时会进包。**发布一律走 `Publish.ps1`（publish 路径）**，
> 所以真实基线是 175.82 MB。


> ⚠️ **关键取舍**：本仓当前**同时**维护"绿色包（GitHub 直发，解压即用）"与"商店 MSIX"两条分发路径。
> 改框架依赖会让**绿色包**要求用户预装运行时（或附带安装器），
> 即**用绿色包的易用性换商店包的体积**。
> ⇒ 建议：**商店包用框架依赖（84.6 MB），绿色包保持自包含** —— 两者本就是不同产物，可各自配置。
> 实现上只需给 `Publish.ps1` 加一个 `-FrameworkDependent` 开关，把两个属性传给 MSIX 那一侧。

### 3.5 杠杆三：OCR 模型按需下载（最大潜力，但需产品决策）

OCR 模型占 **66.4 MB（19.9%）**，而且**压不动**（Deflate 只能省 32%）。
若改为"首启按需下载"：

| 场景 | 包体 |
|---|---|
| 内置模型（当前） | 150.9 MB |
| **模型不入包** | **约 -45 MB（压缩后口径）** |

⚠️ 但这会让**首次使用 OCR 必须联网**，与"纯本地、离线可用"的产品定位冲突。
**不建议为商店上架而改**，可作为独立的产品决策。

**折中方案**：官方还有 **small 档**（det 9.4 MB / rec 20.2 MB，FP32 口径），
换成 small 可显著减小包体，但**精度下降** —— 需要基准测试才能判断是否可接受。

---

## 4. 编译 / 发布管线全景

### 4.1 当前两条产物路径

```
Publish.ps1（9 步）
  [0/6]   验证依赖完整性（含 hashes / 锁定文件）
  [1/6]   准备输出目录
  [1.5/6] 发布前测试门禁（8 套件，按断言数下限卡）
  [2/6]   编译并发布（Release / win-x64，R2R 复合映像）
  [3/6]   复制原生工具链 → native/
  [4/6]   复制 OCR 模型   → data/Models/
  [5/6]   复制静态文件并校验运行时
  [5.5/6] 出包冒烟验证（verify-package.ps1）
  [5.6/6] MSIX 内容核对（仅 -Msix，verify-msix.ps1）   ← 本次新增
  [6/6]   生成校验和与构建元数据（SHA256SUMS.txt / build-info.json）

产物 A：绿色包   publish/TrueToneCap-<ver>/  → scripts/pack.ps1 打 7z（有 7z 时）或 zip
产物 B：MSIX     src/TrueToneCap.App/AppPackages/…/*.msix（仅 -Msix）
```

### 4.2 发布管线里与体积相关的两个开关

| 开关 | 现状 | 对体积影响 |
|---|---|---|
| `PublishReadyToRun` + `Composite` | **默认开启** | 复合 R2R 把全部依赖原生码合并成一个巨大映像。**但它进不了 MSIX 包**（见下） |
| `-NoReadyToRun` | 可选 | 绿色包会变小，但启动变慢 |

> 📌 **一个值得注意的事实**（⚠️ 2026-10-07 实现后更正）：
> MSIX 里是否有 R2R 映像，**取决于用哪条命令打包** ——
> `dotnet build` 打的包里**没有** `TrueToneCap.r2r.dll`（打包枚举声明的项，R2R 是构建产物）；
> 而 `dotnet publish`（`Publish.ps1` 实际走的路）**会把它打进包**（实测 122.46 MB）。
> 也正因如此，**真实发布基线是 175.82 MB，不是 150.90 MB**（后者是 build 口径）。
>
> 两个现象的完整解释：
> 1. 为什么"build 口径"的 MSIX 比绿色包（412.9 MB）小得多 —— 不只是压缩，还少了 R2R 映像；
> 2. ⚠️ R2R 与**目标运行时**绑定：框架依赖模式下再打 R2R，会产出一个与布局不匹配的
>    122 MB 映像（实测把 MSIX 从 84.6 MB 顶回 175.8 MB）⇒ **框架依赖时已强制关闭 R2R**。

### 4.3 发布链的健康度（实测）

| 检查 | 结果 |
|---|---|
| 5 项目 Release 构建 | ✅ 0 警告 0 错误 |
| 8 套件测试 | ✅ 1820 通过 / 0 失败 |
| `--selftest` | ✅ 通过 |
| 冒烟验证 | ✅ 通过 |
| **MSIX 内容核对** | ✅ 4 原生工具 + 3 OCR 资产 + 清单身份全通过 |
| `Publish.ps1 -Msix` 端到端 | ✅ exit 0 |

---

## 5. 建议

### 5.1 直接回答"包是否过大"

**按商店标准：不大。** 150.9 MB 对商店 25 GB 上限只占 0.6%；
而且同类应用（自带 AI 推理 + 桌面运行时）普遍在 100–300 MB 区间。

**但从"下载体验"看偏胖**：150 MB 对首次下载不算小。

### 5.2 建议做的事（按性价比）

| 优先级 | 动作 | 收益 | 代价 |
|---|---|---|---|
| **P1** | 给 MSIX 加 `-FrameworkDependent` 开关，商店包用框架依赖 | **-66 MB（-44%）→ 84.6 MB** | 商店自动装运行时；绿色包不受影响 |
| **P2** | 把 R2R 产物也声明进 MSIX（可选） | 体积 +，但**启动变快** | 需实测启动收益是否值得 |
| **P3** | 评估 OCR 模型按需下载 | **约 -45 MB** | 首次用 OCR 需联网（与离线定位冲突） |
| **P4** | 评估 OCR 换 small 档 | 约 -40 MB | **精度下降**，需基准验证 |

### 5.3 不建议做的事

| 动作 | 原因 |
|---|---|
| 换更强压缩 | ❌ **不存在该选项**（makeappx 只有 `/nc`，算法固定 Deflate） |
| 优化应用自身代码/资源 | ❌ 上限仅 1.5 MB（占 0.5%） |
| 换 FP32 模型"减少压缩开销" | ❌ 实测 FP32 压缩后 63.7 MB > FP16 的 31.0 MB |
| 按语言拆资源包 | ❌ 多语言资源仅 3.4 MB，收益 <3 MB |
| 拆 `.msixbundle` | ❌ 当前只出 x64，无多架构可拆 |

---

## 6. 复现方式

```powershell
# 1) 出 MSIX 并看大小
dotnet build src/TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true
Get-ChildItem -Recurse src/TrueToneCap.App/AppPackages -Filter *.msix

# 2) 解包对比"内容 vs 压缩后"
makeappx unpack /p <pkg>.msix /d $env:TEMP\u /o
(Get-ChildItem -Recurse $env:TEMP\u -File | Measure-Object Length -Sum).Sum / 1MB   # => 333.0

# 3) 复现"压缩不可调"（工具自身帮助即权威）
makeappx pack /?     # => 只有 /nc，没有压缩级别参数

# 4) 复现三个杠杆的体积（改 csproj 两个属性后重新打包）
#    WindowsAppSDKSelfContained=false  => 121.89 MB  (-29.0)
#    SelfContained=false               => 113.58 MB  (-37.3)
#    两者都 false                       =>  84.57 MB  (-66.3)

# 5) 逐文件压缩率
py -c "import zlib,sys; d=open(sys.argv[1],'rb').read(); print(len(d)/1048576, len(zlib.compress(d,9))/1048576)" <file>
```

**关键文件定位**

| 关注点 | 位置 |
|---|---|
| MSIX 打包开关与属性 | `TrueToneCap.App.csproj:26-78` |
| **两个自包含开关** | `TrueToneCap.App.csproj:8,15` |
| 原生工具 Content 声明（进包关键） | `TrueToneCap.App.csproj` 尾部 ItemGroup |
| 发布 9 步 | `Publish.ps1`（`[0/6]`~`[6/6]`） |
| 绿色包压缩（7z/zip） | `scripts/pack.ps1:14,96,107` |
| MSIX 内容门禁 | `scripts/verify-msix.ps1` |
| 发布目录核对 | `scripts/verify-package.ps1` |
| OCR 模型清单 | `TrueToneCap.App/Models/models-manifest.json` |

---

## 7. 待确认

1. **框架依赖后 MSIX 的实机启动表现** —— 未实测（本次只验证体积与打包成功）
2. **MSIX 缺 R2R 对启动时间的实际影响** —— 未量化；若要补 R2R 需先测收益
3. **商店对框架依赖包的处理细节** —— 官方称会自动装框架包，但未在本机验证安装流程
4. **small 档 OCR 模型的精度损失** —— 需按既有基准（`--ocr-bench-real`）实测后才能接受

---

## 8. 实现记录：`-FrameworkDependent`（2026-10-07 同日完成）

### 8.1 改动清单

| 文件 | 改动 |
|---|---|
| `TrueToneCap.App.csproj` | 新增 `TtcFrameworkDependent` 开关；把 `SelfContained` / `WindowsAppSDKSelfContained` 从写死改为**条件默认值** |
| `Publish.ps1` | 新增 `-FrameworkDependent` 开关；**修复自包含参数被命令行硬编码覆盖的缺陷**；框架依赖时强制关 R2R |

### 8.2 实现中发现的缺陷（比开关本身更重要）

第一次接完开关实测得到 **175.82 MB** —— 不但没瘦，反而比基线更大。查出**两个叠加原因**：

**① 命令行 `-p:` 覆盖 csproj 属性。**
`Publish.ps1` 原本硬编码：

```powershell
& dotnet publish $AppProj ... --self-contained true -p:WindowsAppSDKSelfContained=true ...
```

命令行优先级**高于** csproj ⇒ 我在 csproj 里写的 `false` 被完全覆盖。
⇒ 改为按 `$FrameworkDependent` 组装 `$scArgs`。

**② R2R 映像与框架依赖不匹配。**
`Publish.ps1` 默认传 `-p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true`。
R2R 编译产物与**目标运行时**绑定；框架依赖模式再打 R2R，
会产出一个 **122 MB 的 `TrueToneCap.r2r.dll`** 并被打进包 ⇒ 体积从 84.6 MB 反弹到 175.8 MB。
⇒ 框架依赖时**强制关闭 R2R**。

**修正后实测：84.57 MB** —— 与设计目标一致。

### 8.3 顺带厘清：`dotnet build` 与 `dotnet publish` 产出的 MSIX 不同

| 模式 | `dotnet build` | `dotnet publish`（`Publish.ps1` 实际用） |
|---|---|---|
| 自包含 | 150.90 MB | **175.82 MB** |
| 框架依赖 | 84.57 MB | **84.57 MB** |

差异来自 R2R：`publish` 路径会把它打进包，`build` 路径不会
（打包枚举声明的项，R2R 是构建产物）。
⇒ **真实基线应以 publish 列为准**，即 **175.82 MB**；
**实际收益 −91.25 MB（−52%）**，比按 build 口径估算的 −44% 更大。

> 这也修正了 §2 里"MSIX 不含 R2R"的表述 —— 那只在 `dotnet build` 下成立。

### 8.4 三种模式的验证结果

| 模式 | 命令 | MSIX | 打包内容 |
|---|---|---|---|
| 默认（自包含） | `.\Publish.ps1 -Msix` | 175.82 MB | 含 .NET + WinAppSDK 运行时 + R2R |
| **框架依赖** | `.\Publish.ps1 -Msix -FrameworkDependent` | **84.57 MB** | 不含运行时、不含 R2R |
| 仅绿色包 | `.\Publish.ps1` | 不产 MSIX | 自包含（行为与改动前一致） |

框架依赖包**仍完整保留**应用必需内容（实测核对）：
4 枚原生编码器 ✅ / 3 项 OCR 资产 ✅ / `onnxruntime.dll` ✅ / `Microsoft.Windows.SDK.NET.dll` ✅；
而 .NET 运行时（`System.Private.CoreLib.dll`、`coreclr.dll`、`hostfxr.dll`）与
WinAppSDK 原生件（`Microsoft.ui.xaml.dll`）**已按预期移除**。

### 8.5 告警设计（防止误用）

`-FrameworkDependent` 不加 `-Msix` 时，会产出一个**框架依赖的绿色包** ——
与"解压即用"定位冲突。实测该组合是合法的（构建成功），故**不报错**，
但 `Publish.ps1` 会打出显式告警说明后果。这样既保留合法用法（本地验证依赖是否齐全），
又不让发布者误发一个解压后跑不起来的绿色包。

### 8.6 验收读数

| 项 | 结果 |
|---|---|
| 5 项目 Release 构建 | ✅ 0 警告 0 错误 |
| 8 套件测试 | ✅ **1840 通过 / 0 失败** |
| `--selftest` | ✅ 通过 |
| `Publish.ps1 -Msix`（默认） | ✅ exit 0，MSIX 内容核对通过 |
| `Publish.ps1 -Msix -FrameworkDependent` | ✅ exit 0，MSIX 内容核对通过，**84.57 MB** |
| 脚本语法 | ✅ `Publish.ps1` 解析干净 |

### 8.7 用法

```powershell
# 商店包（推荐：体积减半）
.\Publish.ps1 -Msix -FrameworkDependent     # => 84.57 MB

# 商店包（完全自包含，开箱即用）
.\Publish.ps1 -Msix                          # => 175.82 MB

# 只要绿色包（行为与改动前完全一致）
.\Publish.ps1
```

### 8.8 仍未做

1. **框架依赖包的实机安装/启动未验证** —— 本次验证到"包内容正确 + 体积达标"，
   尚未在**没有 .NET/WinAppSDK 运行时的干净机器**上装一次。上架前建议补这一步。
2. **`Identity/@Publisher` 仍是占位符**（同前）。
3. **MSIX 缺 R2R 对启动时间的影响未量化** —— 若实测启动明显变慢，
   可考虑为框架依赖包单独打开 R2R（但那会抵消部分体积收益，需权衡）。

---

## 9. ⚠️ 体积数字更正（2026-10-07 完整验证时实测）

**本文件正文里的体积为当时（实现当日）测量值。之后同日又加入了隐私政策窗口与若干修复，
代码量增加 ⇒ 数字已小幅上移。以下为**最终验证时的实测值**，以本节为准。**

| 模式 | 本文件正文旧值 | **最终实测值** | 差异 |
|---|---|---|---|
| `dotnet build`（自包含） | 150.90 MB | **150.91 MB** | +0.01 |
| `dotnet publish`（自包含，默认） | 175.82 MB | **175.85 MB** | +0.03 |
| `dotnet publish`（**框架依赖**） | 84.57 MB | **85.12 MB** | +0.55 |

⇒ **真实收益：175.85 → 85.12 MB = −90.73 MB（−51.6%）**（原记 −52%，差异可忽略）。

**为什么框架依赖那档涨得相对多（+0.55 MB）**：
本次新增了 `PrivacyPolicyWindow`、抽出 `MarkdownDocumentWindow`（360 行）、
`TranslationSnapshot`、`PackageRuntime`，并把 `StartupManager` 从反射改为
**强类型引用 `Windows.ApplicationModel.StartupTask` / `StartupTaskState`** ——
后者会引入 WinRT 投影的 TypeRef/MemberRef 元数据，而反射版本没有这些引用。
**这是为"可编译期校验"付的合理代价**（换来的是消除了 3 个静默 bug）。

> 复现命令：
> ```powershell
> .\Publish.ps1 -Msix -FrameworkDependent -SkipTests -AllowDirty   # => 85.12 MB
> .\Publish.ps1 -Msix -SkipTests -AllowDirty                       # => 175.85 MB
> dotnet build src\TrueToneCap.App -c Release -p:Platform=x64 -p:TtcMsixPackage=true  # => 150.91 MB
> ```
