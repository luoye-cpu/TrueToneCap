# 如何申请上架微软商店 —— 逐步指南

> 日期：2026-10-07
> 依据：Partner Center 官方文档 + 商店政策 7.20（2026-09-15 发布，**2026-10-22 生效**）**实时核实**，
> 并**在本机对已产出的 MSIX 实跑了 Windows App Certification Kit（WACK）**。
> 凡"实测"字样均为本机真实执行结果。

---

## 0. 先看结论

| 问题 | 答案 |
|---|---|
| **要花多少钱？** | ✅ **免费** —— 官方原文：*"there are **no registration fees** for either account type"*（个人/公司都免费，**已不是**流传的 $19/$99） |
| **在哪申请？** | **[storedeveloper.microsoft.com](https://storedeveloper.microsoft.com)**（⚠️ 必须从这里进，其它入口会走旧的收费流程） |
| **要买代码签名证书吗？** | ❌ **不用** —— 商店认证通过后用**微软证书自动重签** |
| **当前包能过认证吗？** | ✅ **WACK 实测 `OVERALL_RESULT="PASS"`**（24 项测试） |
| **最大的拦路虎是什么？** | 🔴 **隐私政策** —— 政策 10.5.1 **强制**要求 Win32/Desktop Bridge 产品提供，**而本仓目前没有**；且本应用的 Vision 模式会**把整张截图上传到第三方 LLM**，必须如实披露 |

---

## 1. 申请开发者账号

### 1.1 选哪种账号

| 类型 | 适用 | 费用 |
|---|---|---|
| **Individual（个人）** | 个人开发者、业余/学校/个人项目，**发布与自己的商业/职业无关** | **免费** |
| **Company（公司）** | 以注册法律实体名义发布 | **免费** |

⚠️ **`Individual → Company` 不能转换**（官方明确 "not supported"）。若将来要转公司，得**重新注册一个 Company 账号**。

> 📌 对本项目的判断依据：政策 10.14 规定——若**合理消费者会把你的发布者名称理解为企业实体**，就必须用 Company 账号。
> 当前 `Directory.Build.props` 的发布者署名为 **"TrueToneCap contributors"**，且 `LICENSE` 用 Apache-2.0、
> 仓库地址是个人 GitHub（`luoye-cpu/TrueToneCap`）。**这两种都能走**，但发布者显示名要与此一致。

### 1.2 申请步骤（个人账号）

1. 打开 **<https://storedeveloper.microsoft.com>**
2. 点 **"Get started for free"**
3. 选 **Individual developer**
4. 用 **Microsoft 账号（MSA）**登录（没有就现建）
5. **身份验证**：政府签发证件 + **自拍**；需要用**手机在光线良好的环境拍原件**
6. 填资料 → 完成 → **"Go to Partner Center dashboard"**
7. 若没直接跳转：等约 5 分钟后刷新，或直接去 <https://aka.ms/submitwindowsapp>

> ⚠️ **关键提醒**：官方明确 *"This is the only supported entry point for the new flow. Other paths
> (e.g. direct via Partner Center, Xbox, or Visual Studio) will show the **legacy flow**."*
> —— 从别处进会看到**旧的收费流程**。**务必从 storedeveloper.microsoft.com 开始。**
>
> ⚠️ 账号是**不可逆**操作（Individual/Company 不可互换），选之前想清楚。

---

## 2. 预留应用名称（建议尽早做）

1. Partner Center → **Apps and games** → **New product**
2. 选 **MSIX or PWA app**（⚠️ **不是** Game）
3. 输入名称 → **Check availability** → 绿色勾即可用
4. **Reserve product name**

**要点**：
- 名称**全球唯一**；即使商店里看不到同名应用，也可能**已被他人预留**
- 预留**保留 3 个月**，到期未用会被释放
- 可**一次预留多个**名字，最后再定
- 命名要求：简短、原创、**不得用商标名**、不要带版本号/日期后缀、**不支持 emoji 与特殊字符**

> 💡 **对本应用的建议**：现用名 `TrueToneCap 真色截图` 含中文，商店显示名可保留中文，
> 但要注意**品牌一致性** —— `ApplicationTitle`、`Package.appxmanifest` 的 `DisplayName`、
> 商店预留名三处最好一致。

---

## 3. 🔴 上架前的硬性准备（本仓的真实缺口）

### 3.1 隐私政策 —— **必须有，且本仓目前没有**

政策 **10.5.1** 原文（这是最容易被忽略、又直接卡认证的一条）：

> If your product accesses, collects or transmits Personal Information... you must maintain a
> privacy policy. You must provide users with access to your privacy policy by **entering the
> privacy policy URL in Partner Center** when you submit your product.
>
> **Product types that inherently have access to Personal Information must always have privacy
> policies. These include, but are not limited to, Desktop Bridge and Win32 products.**

**本应用属于典型必须有的情形**：它**截取整个屏幕**，并且实测以下数据会离开设备：

| 功能 | 实际外发内容 | 实测依据 |
|---|---|---|
| **Vision 模式（翻译）** | 🔴 **整张截图（PNG → base64）上传到第三方 LLM** | `TranslationService.cs:854-855`（`data:image/png;base64,` + `Convert.ToBase64String(png)`） |
| LLM 文本翻译 | 截图上的 **OCR 文本** | 同上，走 chat completions |
| 免费翻译通道 | **OCR 文本**（URL query 参数） | `TranslationService.cs:404`（`translate.googleapis.com/...?q=`） |
| OCR（本地） | **不出设备** | 本地 ONNX 推理 |

外发目标域名（实测全仓 grep）：`api.deepseek.com`、`api.openai.com`、`api.moonshot.cn`、
`api.siliconflow.cn`、`open.bigmodel.cn`、`dashscope.aliyuncs.com`、`generativelanguage.googleapis.com`、
`translate.googleapis.com`、`clients5.google.com`、`openapi.youdao.com`、`cn.bing.com`、`www.bing.com`

⇒ **隐私政策至少要说清**：截屏内容可能被上传、上传给谁、用途、是否留存、用户如何关闭该功能。
**这是上架前必须补的第一件事**（本仓 `docs/` 与仓库根均无任何隐私政策文件 —— 已实测确认）。

> ⚠️ 政策还要求（10.5.1）：政策须说明**访问/收集/传输了什么**、**如何使用/存储/保护**、
> **披露给哪些第三方**、**用户有哪些控制权**、以及**如何访问自己的信息**。

📄 **已补**：仓库根新增 [`PRIVACY.md`](../PRIVACY.md)（中英双语），逐条对应上述要求。
并且**应用内已有可见入口** —— 设置页「隐私政策」卡片 → `PrivacyPolicyWindow`（见 §3.1c）。

#### 关于"托管"：**不需要另找托管，GitHub 仓库本身就是公网托管**

> ⚠️ 我先前把这一步写成"需托管到公网"，措辞过重了。**实测澄清**：本仓已是**公开仓库**，
> GitHub 直接提供匿名可访问的 URL，**不需要 Pages、不需要自建站点、不需要任何额外服务**。

**实测证据**（2026-10-07）：

| 检查 | 结果 |
|---|---|
| `github.com/luoye-cpu/TrueToneCap` | **HTTP 200** |
| 同一仓库同一分支的 `README.md`（raw） | **HTTP 200，11797 字节** |
| 同上，**用全新无凭据会话**再取一次 | **HTTP 200** ⇒ 匿名可访问 = 公开仓库 |
| `PRIVACY.md` 当前 | HTTP 404 —— **仅因为还没 commit/push** 而已 |

⇒ `PRIVACY.md` commit + push 之后，下面的 URL 立刻可用（**无需任何额外操作**）：

```
https://github.com/luoye-cpu/TrueToneCap/blob/main/PRIVACY.md      ← 填这个
```

**该填哪个 URL**：Partner Center 的隐私政策 URL 是给**用户与审核员点击**的普通网页链接，
所以用 `github.com/.../blob/...`（渲染成可读网页），**不要**用 `raw.githubusercontent.com`
（返回纯文本、没有页面导航，体验差且不像"一个政策页面"）。

> 💡 两个可选增强（都不是必须）：
> - **固定版本号**：把链接指向 tag（如 `blob/v0.3.7-beta/PRIVACY.md`），
>   政策快照就不会随 `main` 变动 —— 审核期间尤其稳妥。但**改功能后必须记得同步更新**该链接指向。
> - **更容易记的地址**：给仓库开 GitHub Pages，或把链接写进 README 顶部，
>   用户从商店页跳过来更容易找到。
>
> ⚠️ **唯一真正需要保证的是"匿名可访问"**：如果哪天仓库转成 private，
> 该 URL 会变成 404，审核直接失败。上面的实测方法（无凭据会话取一次）可随时复查。

### 3.1c ✅ 已实现：产品内隐私入口（渲染逻辑与许可窗口共用）

| 项 | 实现 |
|---|---|
| 文件 | `PRIVACY.md`（仓库根，中英双语） |
| 窗口 | `PrivacyPolicyWindow`（设置页「隐私政策」卡片 → 按钮） |
| 随包分发 | `App.csproj` 里注册为 `Content`，与 `THIRD-PARTY-NOTICES.md` 同一套写法 |
| 交付校验 | `scripts/verify-msix.ps1` 新增断言：包里必须有 `PRIVACY.md` 与 `THIRD-PARTY-NOTICES.md` |
| 实测 | MSIX 内含 `PRIVACY.md`（8.0 KB），且与仓库副本 **sha256 逐字节相同** |

**顺带做的一次重构（避免第二份渲染器）**：原 `ThirdPartyNoticesWindow` 是 467 行、
自带 Markdown 渲染 + 路径上溯 + 降级界面 + 生命周期清理的完整实现。
隐私窗口要做同样的事 —— 直接复制会产生"同一种渲染、两处维护"的分叉。
⇒ 抽出基类 **`MarkdownDocumentWindow`**，渲染/上溯/降级/清理只有一份；
两个窗口各自只剩：声明读哪个文件 + 中英文案 + 单实例登记。

> 💡 重构中踩到并记录在代码里的两点：
> ① 子类 XAML 的根元素必须写成**自定义基类**（`<local:MarkdownDocumentWindow …>`），
>    否则 XAML 编译器生成的那半会以 `Window` 为基类 ⇒ CS0263「分部声明指定了不同基类」；
> ② `x:Name` 生成的字段属于**子类** partial，基类看不见；基类声明抽象属性、
>    子类逐个转发（并把 XAML 里的名字加 `Control` 后缀避免 CS0108 隐藏）。
>    选这套写法而非"反射按名找控件"，是因为**漏掉控件会编译期报错**，
>    而不是界面上少一块还不报错。


### 3.1b ✅ 已修复：MSIX 下**设置不会保存**（原为两个实质缺口之一）

这一条是核查隐私政策（"数据如何存储"）时顺带查出来的，严重程度高于隐私政策本身。

**问题**：`SettingsService` 的设计是**便携优先**：设置只写**程序同目录**（`AppContext.BaseDirectory`），
且**刻意删掉了"不可写就降级到 %LOCALAPPDATA%"那条路**（源码注释给了理由：两个位置会让用户与代码各信一个）。
而 MSIX 包安装到 `C:\Program Files\WindowsApps\…`，官方原文：

> **Write inside the package — Not allowed. The package is read-only.**
> — [Understanding how packaged desktop apps run](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-behind-the-scenes)

⇒ 打成 MSIX 后 `CanPersist` 为 false ⇒ **用户改的每一项设置重启后都会回到默认**，
而界面不会有任何提示（只有日志里一行）。

**实测确认**：全仓 grep `Windows.Storage.ApplicationData` / `Package.Current` / `IsPackaged`
—— **不存在任何"打包感知"的存储分支**。

**修法**（`PackageRuntime` + `SettingsService`）：

| 形态 | 设置位置 |
|---|---|
| 未打包（绿色包） | `AppContext.BaseDirectory`（**语义完全不变**） |
| **已打包（MSIX）** | **`%LOCALAPPDATA%\TrueToneCap\`**（可写，且与密钥同一处） |

**⚠️ 这不是把 2026-09-25 删掉的"降级"又加回来** —— 那条路的问题在于**探测失败才换位置**，
于是"设置存哪"依赖运行时可写性、同一次安装里都能变，用户还看不出来。
新实现是**按运行形态静态决定**：打包就是打包、未打包就是未打包，**一个环境一个答案，不随写失败漂移**。

**判定方式**：`PackageRuntime.IsPackaged` 用 `GetCurrentPackageFullName`（P/Invoke，
不引入 WinRT 依赖），`15700` = 无包身份。**刻意保守**：拿不准一律当"未打包"
（维持程序同目录的原语义，绝不会更糟）；误判成"已打包"才会挪走绿色包的设置。

**顺带修的第二处同类问题**：`LogService` 原本写 `<程序目录>\log` —— MSIX 下同样写不进去。
原实现有 `%TEMP%` 兜底（不至于丢日志），但位置反直觉。现在打包形态直接写
`%LOCALAPPDATA%\TrueToneCap\log`，与设置/密钥同处，用户找得到。

**已核查、确认无需改动的第三处**：`NativeLibraryResolver` 会**尝试写** `BaseDirectory\native`，
但打包形态下两个条件同时成立使其无害 ——
① 4 枚 exe 已随包分发（实测解包确认 `native\` 下俱在）⇒ `File.Exists` 先命中并跳过写；
② 整个方法体在 `try/catch` 内，失败只记 `LastError` 并告警，不会崩启动。
已在源码注释里写明"若将来改成运行时提取，这两个条件会同时失效，必须改到可写目录"。

### 3.2 `Identity/@Publisher` 必须替换为商店给的值

当前 `Package.appxmanifest` 里是占位符：

```
Publisher="CN=PLACEHOLDER-REPLACE-WITH-PARTNER-CENTER-IDENTITY"
```

**预留名称后**，到 Partner Center 的 **Product management → 查看应用标识详细信息**，
把它给出的 `Publisher`（形如 `CN=xxxx-xxxx-xxxx`）**逐字**填回清单。

> ⚠️ 官方明确：值**区分大小写**，空格与标点也必须完全一致，否则上传失败且**报错不指向这里**。
> 本仓 `scripts/verify-msix.ps1` 已会在检测到 `PLACEHOLDER` 时给出 ⚠️ 提示。

### 3.3 商店展示素材

| 素材 | 要求 | 本仓状态 |
|---|---|---|
| 应用图标/磁贴 | 500×500 等 | ✅ 已生成（`scripts/gen-msix-assets.py`，13 个 PNG） |
| 截图 | 至少 1 张，建议 1366×768 以上 | ❌ **需自备** |
| 描述（短/长） | 须准确反映功能与**限制** | ❌ 需撰写 |
| 隐私政策 URL | **必须** | ❌ 见 §3.1 |
| 年龄分级（IARC 问卷） | 提交时在线填写 | ⏳ 提交时做 |
| 支持联系方式 | Company 账号在部分区域会展示 | ⏳ |

⚠️ 政策 **10.1.1** 要求元数据**准确描述功能与重要限制**。
对本应用而言，"需要 .NET/WinAppSDK 运行时（若用框架依赖包）"、"翻译需联网并会把截图内容发给第三方"
这类**限制必须写明**，否则可能因"误导"被拒。

---

## 4. 打包与提交

### 4.1 出提交用的包

```powershell
# 推荐：框架依赖，体积减半（最终实测 175.85 → 85.12 MB，−51.6%）
.\Publish.ps1 -Msix -FrameworkDependent

# 或：完全自包含，开箱即用
.\Publish.ps1 -Msix
```

产物位置：`src/TrueToneCap.App/AppPackages/…/*.msix`（以及 `.appxsym` 符号包）

**提交格式说明**（官方）：
- 商店接受 `.msix` / `.msixbundle` / `.msixupload` / `.appx` / `.appxbundle` / `.appxupload`
- **`.msixupload` 是商店提交专用**，它把各架构包 + **`.appxsym`（符号）**打成一个文件；
  有了符号包，Partner Center 才有**崩溃分析**
- 本仓目前出的是 `.msix` + `.appxsym` —— **体积与内容都合规**，
  若要 `.msixupload` 可用 Visual Studio 的"创建应用包"向导再包一层

> ✅ **不需要签名**：本仓 `AppxPackageSigningEnabled=false` 是对的 ——
> 商店会在认证通过后用**微软证书自动重签**。
> （只有**侧载/企业分发**才需要自购证书签名。）

### 4.2 提交步骤

1. Partner Center → 你的产品 → **Start submission**
2. **Packages**：上传 `.msix`（或 `.msixupload`）
3. **Pricing and availability**：免费 / 市场选择
4. **Properties**：类别、年龄分级问卷、**隐私政策 URL**
5. **Age ratings**：完成 IARC 问卷
6. **Store listing**：描述、截图、支持信息
7. **Notes for certification**（⚠️ **对本应用特别重要**，见下）
8. **Submit to the Store**

### 4.3 `Notes for certification` 建议填写内容（本应用特有）

认证人员需要知道怎么用、以及有哪些"看起来像缺陷但其实是设计"的行为：

- **OCR 与截图全程本地**，不需要账号、不需要登录
- **翻译功能需联网**；Vision 模式会把截图上传到用户**自行配置**的第三方 LLM 端点
  （API Key 由用户自己填，本应用**不内置**任何 Key）
- **首次运行可能提示安装运行时**（若用框架依赖包，商店会自动处理）
- **需要 Windows 11 24H2 (build 26100) 及以上**（清单 `MinVersion` 已声明）
- **无语言包时韩文 OCR 会自动走 Windows OCR 兜底**（属设计，非缺陷）

> 📌 政策 **10.3** 要求"产品必须可测"。本应用**无需登录即可测**主要功能（截图+OCR），
> 这对认证是**有利**的 —— 但翻译功能需要 API Key，**建议在认证说明里讲清"翻译为可选功能，
> 不配置 Key 时应用仍可正常截图与 OCR"**。

---

## 5. 认证会发生什么

| 阶段 | 内容 | 耗时 |
|---|---|---|
| 预处理 | 包上传后的初步校验 | 分钟级 |
| **安全测试** | 病毒/恶意软件扫描 | — |
| **技术合规** | **即 WACK**，含安装并运行你的应用 | — |
| **内容合规** | 人工审阅内容与商店信息 | — |
| 合计 | 官方：*"typically takes up to **three business days**"* | ≤3 工作日 |
| 发布 | 通过后约 **15 分钟**内可见 | — |

**通过后**：微软给包签名 → 进入 Publishing → 状态变 **In the Store**。
⚠️ 一旦进入发布阶段，**不能再取消提交或改发布日期**。

**被拒后**：认证报告会指出是哪项测试或哪条政策未过，修好后**新建一次提交**重跑。

---

## 6. ✅ 本机实测：WACK 结果

我已对当前产出的 MSIX **实跑了 WACK**（`appcert.exe test -appxpackagepath …`）：

```
OVERALL_RESULT = PASS
被测包: TrueToneCap 0.3.7.0   APP_TYPE=Centennial（Win32 打包应用）
共 24 项测试
```

**全部通过**的项包括（挑关键的）：应用清单、应用程序计数、**品牌**（防用 SDK 示例默认图 —— 我们自制的图标通过）、
企业功能、资源包、UAC 运行级别、DPIAwareness、平台的相应文件、调试配置等。

### 6.1 有两项 FAIL，但均为 `OPTIONAL="TRUE"`（不阻断）

诚实记录，避免上架后才发现：

**① 一般元数据正确性（FAIL，OPTIONAL）**
```
未找到文件 Microsoft.Graphics.Canvas.winmd 中由类型 …ICanvasSwapChainStatics 引用的类型
Microsoft.UI.WindowId。元数据文件中引用的所有类型都必须是可检测到的。
```
⇒ **Win2D（`Microsoft.Graphics.Canvas.winmd`）自带的 winmd 元数据引用了 WinAppSDK 的类型**，
WACK 在独立解析时找不到。**属上游 Win2D 的元数据问题，不是本仓引入的缺陷**；
且该测试标为 OPTIONAL ⇒ 不影响 `OVERALL_RESULT`。

**② 已阻止的可执行文件（FAIL，OPTIONAL）**
```
TrueToneCap.Core.dll 包含对"启动流程"相关 API System.Diagnostics.Process.Start 的引用
TrueToneCap.exe 包含对"启动流程"相关 API shell32.dll!ShellExecuteW 的引用
DirectML.dll 包含对 "reg" 的不可执行引用  …（共 19 条）
```
⇒ 前半是**本应用确实会启动原生编码器子进程**（avifenc/cjxl/cwebp/cjpegli）——
这是**核心功能，不是问题**，且该测试标为 OPTIONAL。
后半（DirectML/onnxruntime 里的 `"reg"`/`"cmd"` 字符串）是**字节串误报**（PE 里随机字节恰好像命令名）。

⇒ **两项都不阻断**（这就是 `OVERALL_RESULT=PASS` 的原因）。
**但建议在 `Notes for certification` 里主动说明"应用会调用随包分发的开源编码器子进程"**，
避免认证人员把它当问题而反复。

---

## 7. 行动清单（按顺序）

| # | 动作 | 状态 |
|---|---|---|
| 1 | **从 storedeveloper.microsoft.com 申请免费个人账号**（备好证件+自拍） | ⏳ 你做 |
| 2 | **预留应用名称**，拿到 **Publisher 标识** | ⏳ 你做 |
| 3 | ~~写隐私政策~~ | ✅ **已完成**（`PRIVACY.md` + 应用内入口 + 随包分发 + 门禁断言） |
| 3b | **把 `PRIVACY.md` commit + push 到仓库**（GitHub 公开仓库即公网托管，无需另找），把 `blob/main/PRIVACY.md` 填进 Partner Center | ⏳ 你做（只需一次 push） |
| 4 | ~~修 MSIX 设置不保存~~ | ✅ **已完成**（`PackageRuntime` 分支 + 日志目录一并修 + 测试覆盖） |
| 5 | 把 `Publisher` 填回 `Package.appxmanifest` | ⏳ 待做 |
| 6 | 准备商店素材：截图（1366×768+）、中英文描述、支持信息 | ⏳ 待做 |
| 7 | `.\Publish.ps1 -Msix -FrameworkDependent` 出包 | ✅ 已验证可用（85.12 MB） |
| 8 | 本机 WACK 复跑确认（本次已 PASS） | ✅ 已完成 |
| 9 | 提交，填好 `Notes for certification`（§4.3） | ⏳ |
| 10 | 等 ≤3 工作日认证；通过后 ~15 分钟上架 | ⏳ |

> ✅ **代码侧的阻断项已清空**（原 §3.1b 的设置丢失已修，并有测试锁住）。
> 现在只剩**流程性/人工**的三件事：**push 一次拿到隐私政策 URL**、填 Publisher、准备商店素材。
>
> ⚠️ 唯一**仍未实机验证**的一环：把 MSIX 装到**没有 .NET/WinAppSDK 运行时的干净机器**上跑一遍
> （包内已附带 `Add-AppDevPackage.ps1`）。本次修复针对的正是"打包后才暴露"的那类问题，
> 因此这一步值得在提交前做掉 —— 它会一次性验证：设置持久化、日志落点、子进程 + `%TEMP%`、
> 以及四种编码格式在沙箱内是否可用。

---

## 8. 官方链接

| 用途 | 链接 |
|---|---|
| **申请账号（唯一正确入口）** | <https://storedeveloper.microsoft.com> |
| Partner Center 提交页 | <https://aka.ms/submitwindowsapp> |
| 账号类型与流程 | [open-a-developer-account](https://learn.microsoft.com/en-us/windows/apps/publish/partner-center/open-a-developer-account) |
| 预留名称 | [reserve-your-apps-name](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/reserve-your-apps-name) |
| 包要求（含签名政策） | [app-package-requirements](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-package-requirements) |
| 认证流程 | [app-certification-process](https://learn.microsoft.com/en-us/windows/apps/publish/publish-your-app/msix/app-certification-process) |
| **商店政策 7.20**（10.5 隐私、10.1 元数据） | [store-policies](https://learn.microsoft.com/en-us/windows/apps/publish/store-policies) |
| WACK 文档 | [windows-app-certification-kit](https://learn.microsoft.com/en-us/windows/uwp/debug-test-perf/windows-app-certification-kit) |
| 认证/政策申诉 | <reportapp@microsoft.com> |
| 支持工单 | <https://aka.ms/windowsdevelopersupport> |

---

## 9. 复现方式（本机自检）

```powershell
# 1) 出包
.\Publish.ps1 -Msix -FrameworkDependent

# 2) 内容门禁（本仓自带）
.\scripts\verify-msix.ps1

# 3) 跑 WACK（商店认证用的同一套）
$wack = "C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe"
& $wack reset
$msix = (Get-ChildItem -Recurse src\TrueToneCap.App\AppPackages -Filter *.msix).FullName
& $wack test -appxpackagepath $msix -reportoutputpath "$env:TEMP\wack.xml"

# 4) 看总判定（注意：OVERALL_RESULT 在根节点属性上）
[xml]$x = Get-Content "$env:TEMP\wack.xml" -Raw
$x.REPORT.GetAttribute('OVERALL_RESULT')       # => PASS

# 5) 逐项看哪些非 PASS（并注意 OPTIONAL 标记 —— 它们是"不阻断"的）
$x.SelectNodes("//*[local-name()='TEST']") | ForEach-Object {
  $r = $_.SelectSingleNode("*[local-name()='RESULT']").InnerText.Trim()
  if ($r -ne 'PASS') { "$($_.GetAttribute('NAME')) => $r (OPTIONAL=$($_.GetAttribute('OPTIONAL')))" }
}
```

> ⚠️ **一个容易看错的细节**：`OVERALL_RESULT` 是**根节点的属性**，不是子元素；
> 而个别 `TEST` 的 `RESULT` 是**元素文本**。用错读法会得出"全部失败"的错误结论
> （我第一次就读错了，实际是 24 项里 22 PASS + 2 个 OPTIONAL FAIL）。
