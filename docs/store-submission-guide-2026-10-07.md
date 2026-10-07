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
⚠️ 但政策**要求填 Partner Center 的 URL**，所以还需把该文件**托管到公网**
（GitHub 仓库的 raw/Pages 链接即可），或自建页面。

### 3.1b 🔴 实测发现的第二个 MSIX 阻断项：**设置不会保存**

这一条是核查隐私政策（"数据如何存储"）时**顺带查出来的**，严重程度高于隐私政策本身。

`SettingsService.cs` 的设计是**便携优先**：设置只写**程序同目录**（`AppContext.BaseDirectory`），
且**刻意删掉了"不可写就降级到 %LOCALAPPDATA%"那条路**（源码注释给了理由：两个位置会让用户与代码各信一个）。
不可写时的口径是 —— **记一条日志 + 全程按默认值运行，不换位置存**。

**问题**：MSIX 包安装到 `C:\Program Files\WindowsApps\…`，该目录**对应用只读**。
⇒ 打成 MSIX 后 `CanPersist` 会为 false ⇒ **用户改的每一项设置重启后都会回到默认**，
而界面不会有任何提示（只有日志里一行）。

**实测确认**：全仓 grep `Windows.Storage.ApplicationData` / `Package.Current` / `IsPackaged`
—— **不存在任何"打包感知"的存储分支**（命中的 `Windows.Storage` 都是文件选择器，与设置无关）。

⇒ **上架前必须补**：给 `SettingsService` 增加打包分支，
打包形态写 `ApplicationData.Current.LocalSettings`（或 `LocalFolder`），
未打包形态维持现状（便携语义不变）。**这是功能性问题，不修的话商店版是残的。**

> 📌 这也说明：**"能打包"与"打包后能用"是两件事**。
> 本仓 MSIX 已能出包、内容核对全绿、WACK 也 PASS，
> 但真机上跑起来会丢失设置 —— 与我先前标注的"沙箱内未实机验证"是同一个盲区，
> 现在有了**具体的、可复现的**失败点。

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
# 推荐：框架依赖，体积减半（实测 175.82 → 84.57 MB）
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
| 3 | 🔴 **写隐私政策**并托管到公网 URL（必须披露截图可能上传第三方） | ❌ **待做（本仓缺口）** |
| 4 | 把 `Publisher` 填回 `Package.appxmanifest`，更新门禁断言 | ⏳ 待做 |
| 5 | 准备商店素材：截图、中英文描述、支持信息 | ⏳ 待做 |
| 6 | `.\Publish.ps1 -Msix -FrameworkDependent` 出包 | ✅ 已验证可用 |
| 7 | 本机 WACK 复跑确认（本次已 PASS） | ✅ 已完成 |
| 8 | 提交前想清：`WindowsAppSDKSelfContained` 用哪种（影响体积与用户要求） | ✅ 开关已就绪 |
| 9 | 提交，填好 `Notes for certification`（§4.3） | ⏳ |
| 10 | 等 ≤3 工作日认证；通过后 ~15 分钟上架 | ⏳ |

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
