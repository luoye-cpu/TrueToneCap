# 依赖新旧程度核查（2026-10-26）

> 日期：2026-10-26
> 方法：NuGet 直接依赖用 `dotnet list package --outdated` 实测（nuget.org 源），
> 上游最新版逐个对注册表/仓库实时查询（nuget.azure.cn flatcontainer API、GitHub API）。
> 与 `dependency-update-analysis-2026-10-07.md` 的关系：那轮建议的执行情况核对 + 半个月后的重新对表。

---

## 0. 结论摘要

| 类别 | 新旧程度 | 落后项 |
|---|---|---|
| **NuGet 直接依赖** | ✅ **全部最新稳定版** | 0 项 |
| NuGet 传递依赖 | ✅ 漏洞扫描 0 项 | 1 项已知预发布隐患（见 §3） |
| **原生工具链** | ✅ 基本追平 | 2 项为 libavif pin 所致的已知落后 + 1 项可选（cwebp） |
| .NET SDK | 🟡 声明落后实际一个里程碑 | global.json 钉 rc.1，实际解析 rc.2（同 10-07 建议，未执行） |
| GitHub Actions | ✅ 已升 v7 | 0 项（10-07 建议已执行） |
| OCR 模型 | ✅ 最新 medium 档（沿用 10-07 结论） | — |

**10-07 那轮分析的执行情况**：第 1、2 批建议**全部落地**——WinAppSDK 2.4.0→2.5.1、
System.Drawing.Common→10.0.13、MEDI→10.0.13（提交 19bc5ff）、cjxl→0.12.0、
avifenc 内嵌 libpng→1.6.59（CVE-2026-46675 已消）、Actions v4→v7、
cjpegli 改自编译并补记 libjpeg-turbo 版本。唯一的漏网是 **global.json 声明未同步**（见 §2）。

---

## 1. NuGet 直接依赖（实测：`dotnet list package --outdated` 全部报"没有更新"）

| 包 | 当前 | 上游最新稳定 | 最新任意（含预发布） | 结论 |
|---|---|---|---|---|
| Microsoft.WindowsAppSDK | 2.5.1 | **2.5.1** | 2.5.4-experimental | ✅ 最新 |
| Microsoft.Graphics.Win2D | 1.4.0 | 1.4.0 | — | ✅ 最新 |
| Microsoft.Windows.AI.MachineLearning | 2.4.89 | **2.4.89** | 2.7.2021-experimental | ✅ 最新稳定（ORT 1.27） |
| Microsoft.Extensions.DependencyInjection | 10.0.13 | **10.0.13**（.NET 10 LTS 线） | 11.0.0-rc（尚无 11.x 稳定） | ✅ 刻意留在 LTS 线，已是线内最新 |
| Vortice.Direct3D11 / DXGI | 3.8.3 | 3.8.3 | — | ✅ 最新 |
| System.Drawing.Common | 10.0.13 | **10.0.13** | — | ✅ 最新 |

**漏洞**：`dotnet list package --vulnerable --include-transitive` 6 个项目**全部 0 项**
（`NuGetAuditMode=all` 覆盖完整依赖图 + 锁文件钉死传递版本，这条防线当前是绿的）。

---

## 2. .NET SDK：唯一建议动作（等 11-10 GA 一并处理）

- **.NET 11 尚未 GA**：RC2 发布于 2026-10-13，**GA 预计 2026-11-10**（.NET Conf 2026）。
  当前 rc.2 就是可获得的最新 —— `Directory.Build.props` 里"待 GA 后升回 11.x"的注释仍然成立。
- **声明与实际不符（10-07 建议未执行，现状加剧）**：`global.json` 钉 `11.0.100-rc.1.26425.128`，
  `rollForward: latestPatch` 实际解析到 **rc.2.26504.105**（本机实测构建输出即用此版本）。
  可复现性缺口与 10-07 描述一致，且从"落后一个 milestone"变成"声明 rc.1 / 实际 rc.2"。
- **建议**：现在把 `global.json` 改成 `11.0.100-rc.2.26504.105`（纯声明修正）；
  **2026-11-10 GA 后**做一轮整体升级：SDK → GA 稳定版 + `Microsoft.Extensions.*` 10.x → 11.x
  + `allowPrerelease: false` 收口。

---

## 3. 传递依赖：一项已知预发布隐患（无动作可做，记录在案）

`SharpGen.Runtime(.COM)` 2.4.2-beta —— 由 **Win2D 1.4.0 传递引入**，本仓无法直接控制。
上游现状：最新稳定仍是 **1.2.1**，最新任意仍是 **2.4.2-beta** ⇒ **没有可转正的稳定版**，
10-07 提出的"升级时复核是否转正"目前**无从执行**，只能继续观察。
风险面不变：预发布包被取消列出时，全新机器（无本地缓存）还原会 NU1101。

---

## 4. 原生工具链（tools-manifest.json 逐项对上游）

| 工具 / 内嵌组件 | 当前 | 上游最新 | 结论 |
|---|---|---|---|
| libavif（avifenc 本体） | 1.4.2 | **v1.4.2**（GitHub releases/latest，2026-05-26） | ✅ 最新 |
| libjxl（cjxl） | 0.12.0 | **v0.12.0**（releases/latest，2026-07-01） | ✅ 最新（10-07 已升） |
| cjpegli | 自编译（google/jpegli） | 上游**无任何 release** | ➖ 无"最新"可对 |
| libwebp（cwebp） | **1.5.0** | **v1.6.0**（tags 最新） | 🟢 落后一个次版本，无 CVE，可选升级（10-07 同判） |
| SVT-AV1（编入 avifenc） | v4.1.0 | **v4.2.0**（tags 最新；注意 releases/latest 返回的 v2.1.2 是 10-25 发布的旧分支回补，非最新版线） | 🟡 落后一档，**由 libavif v1.4.2 的 pin 决定**，非本仓可直接升级 |
| libaom（编入 avifenc） | 3.14.1 | 3.15.1（10-07 核实；本轮 googlesource 超时未复核到更新） | 🟡 同上，pin 所致 |
| zlib（编入 avifenc） | 1.3.2 | 1.3.2 | ✅ 最新 |
| **libpng（编入 avifenc）** | **1.6.59** | 1.6.59 | ✅ **CVE-2026-46675 已修复**（10-07 用 `ext/libpng/` 旁路重编译落地） |
| libjpeg-turbo（编入 cjpegli） | 3.1.2 | 3.x 线零已知 CVE | ✅ 无安全暴露 |

> cjxl（libjxl 0.12.0 预编译件）内嵌 libpng 仍为 1.6.50——**未变也变不了**（libjxl 自身 pin），
> 暴露面评估沿用 10-07：应用不解码不可信 PNG，实际可利用性低。

---

## 5. 建议（按优先级）

> ✅ **同日执行记录（2026-10-26，维护者拍板"完整迁移 .NET 11"）**：
> §2/§5-1/§5-4 提前合并落地 —— `global.json` → `11.0.100-rc.2.26504.105`（声明对齐实际），
> `Microsoft.Extensions.DependencyInjection` 与 `System.Drawing.Common` → `11.0.0-rc.2.26504.105`
> （go-live 支持，构建号与 SDK 同源；NuGet index 实测该版本存在）。
> 3 个受影响的 `packages.lock.json`（App/Test/Tools）已重生成；全量 Release 构建
> **0 警告 0 错误**；8 个测试套件全部通过；MSIX 重建 + 内容门禁全绿。
> 这是对本仓"不用预发布包"惯例的**一次明确豁免**：rc.2 距 GA（2026-11-10）仅两周，
> 维护者选择先吃满 .NET 11 基线、GA 后换 stable 收口。**GA 后必办**：
> 两个包 + SDK 换 stable，`global.json` 关掉 `allowPrerelease`。
> §5-2（cwebp 1.6.0）、§5-3（SVT-AV1/libaom 等 libavif pin）、§5-5（SharpGen 转正复核）维持原样。

| # | 动作 | 时机 |
|---|---|---|
| 1 | `global.json` → `11.0.100-rc.2.26504.105`（声明对齐实际，消除可复现性缺口） | 随时，1 分钟 |
| 2 | cwebp 1.5.0 → 1.6.0（官方预编译件，换二进制 + 重算 sha256 + 回填清单） | 可选，无安全驱动 |
| 3 | SVT-AV1 / libaom 升级 | **等 libavif 新版**带新 pin（或走 `ext/` 旁路自编译，收益低不推荐单独做） |
| 4 | .NET 11 GA 整体升级轮：SDK GA + `Microsoft.Extensions.*` 11.x + `allowPrerelease: false` | **2026-11-10 GA 后** |
| 5 | 复核 SharpGen.Runtime 2.4.2-beta 是否转正 | 每次升级 Win2D/WinAppSDK 时 |

---

## 6. 复现命令

```bash
# NuGet 直接依赖是否有更新（对本机配置的源实测）
dotnet list package --outdated

# 漏洞扫描（含传递依赖）
dotnet list package --vulnerable --include-transitive

# 上游最新版（nuget 镜像 flatcontainer API；GitHub 用 /releases/latest + /tags 双查，
# 注意 releases/latest 可能返回旧分支回补版，版本线以 tags 为准）
curl https://api.nuget.org/v3-flatcontainer/<包名小写>/index.json
```
