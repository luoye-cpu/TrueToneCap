<!--
  TrueToneCap/SECURITY.md
  双语安全漏洞披露政策 —— 中英逐条对照。

  ⚠️ 维护须知：本文件里每一条"已具备的机制"都必须是仓库里真实存在的东西，
  并在括号里注明出处；改动了那些机制（例如取消 --locked-mode、改掉密钥存储方式）
  必须同步改本文件，否则这里就变成一份误导人的声明。
    · DPAPI 加密本地密钥      → src/TrueToneCap.App/Services/SecretStore.cs
    · 日志不写 OCR 原文        → src/TrueToneCap.Core/Services/TranslationService.cs
    · 依赖版本单一事实源        → Directory.Packages.props + 各项目 packages.lock.json
    · CI 锁定模式还原          → .github/workflows/ci.yml（dotnet restore --locked-mode）
    · 包内校验和与构建元数据    → Publish.ps1 产出的 SHA256SUMS.txt 与 build-info.json
    · 隐私政策（数据流向）      → PRIVACY.md
-->

# 安全政策 / Security Policy

## 报告漏洞 / Reporting a Vulnerability

本仓库支持 GitHub 的 **Private vulnerability reporting**：
This repository supports GitHub **private vulnerability reporting**:

在仓库页面 **Security → Report a vulnerability** 提交，不要在公开 issue 里贴可被利用的细节。
Open it from the repository's **Security → Report a vulnerability** tab; do not post exploitable details in a public issue.

如果该入口对本仓库不可用，请先开一个**只写现象、不含利用细节**的 issue，并把细节留到私聊渠道。
If that entry point is unavailable for this repository, open an issue describing **only the symptom, without exploitation details**, and hold the details for a private channel.

作者是单人维护者，因此**不承诺固定响应时限**；但会做到两件事：修复落在带 tag 的版本里，
The maintainer is a single person, so **no fixed response SLA is promised**; but two things are guaranteed: the fix lands in a tagged version,

以及在公告里写清受影响版本范围。
and the advisory states the affected version range.

## 支持范围 / Supported Versions

只有**最新的 tag** 是安全支持版本；更早的 beta 不回填补丁，只在 CHANGELOG 里留痕。
Only the **latest tag** is a supported version for security purposes; older beta releases do not receive back-ported fixes, only a note in the CHANGELOG.

| 版本 / Version | 是否支持 / Supported |
|---|---|
| 最新 `v*` tag | ✅ |
| 更早的 `v*-beta` | ❌（请升级到最新版 / upgrade to the latest） |

## 范围内 / In Scope

- 应用自身的代码：截图、编码、OCR、翻译、设置与密钥存储。
  The application's own code: capture, encoding, OCR, translation, settings and secret storage.
- 构建与发布链路：`Publish.ps1`、`scripts/verify*.ps1`、两份 GitHub Actions workflow。
  The build and release pipeline: `Publish.ps1`, `scripts/verify*.ps1`, and both GitHub Actions workflows.
- 随包分发的原生工具与模型：**问题请同时报上游**，本仓负责升级并记录版本号
  Vendored native tools and models shipped in the package: **report upstream as well**; this repo owns bumping them and recording versions
  （出处：`build-info.json` 的 `tools[]` 与 `SHA256SUMS.txt`）。
  (see `tools[]` in `build-info.json` and `SHA256SUMS.txt`).

## 范围外 / Out of Scope

- 需要已在用户机器上执行代码、或物理访问设备的攻击。
  Attacks that already require code execution on the user's machine, or physical access to the device.
- 用户自行填入的第三方服务（翻译后端）的账户安全问题。
  Account-level issues of third-party services the user configures themselves (translation backends).
- 对未安装本应用的系统做的扫描类报告。
  Scans of systems where the application is not installed.

## 已具备的机制（可自行核对）/ Existing Mechanisms (Independently Verifiable)

| 机制 / Mechanism | 出处 / Where |
|---|---|
| 依赖版本集中在 `Directory.Packages.props`，CI 用 `--locked-mode` 还原，版本漂移会在还原期失败 / Centralized versions plus `--locked-mode` restore in CI | `Directory.Packages.props`, `.github/workflows/ci.yml` |
| 每个包附 `SHA256SUMS.txt` 与 `build-info.json`（含 commit SHA、SDK 版本、工具版本），产物可追溯到提交 / Every package carries checksums and build metadata tying it to a commit | `Publish.ps1` 产出 / emitted by `Publish.ps1` |
| 翻译密钥用 Windows DPAPI 加密后存本地，不落明文；日志不写 OCR 原文 / Translation keys are DPAPI-encrypted at rest; OCR text is not written to logs | `SecretStore.cs`, `TranslationService.cs` |
| 用户数据的流向（哪些图像会离开设备、发给谁、单次上限）在隐私政策里逐条写明 / What data leaves the device, to whom, and the per-request cap | `PRIVACY.md` |
| 发布前跑 7 套件门禁并做包内内容核对（"包里是不是这一轮的代码"），失败即阻断发布 / Pre-release gate plus in-package content verification; failure blocks the release | `scripts/verify.ps1`, `scripts/verify-package-content.ps1` |

## 报告时请附上 / What to Include in a Report

复现步骤、受影响版本（`TrueToneCap.exe` 属性页里的 `ProductVersion` 已带 commit SHA）、
Reproduction steps, the affected version (the `ProductVersion` shown in `TrueToneCap.exe`'s properties already carries the commit SHA),

以及**脱敏后的**日志。请勿直接上传 `TrueToneCap.settings.json` 或原始日志——它们可能含你本人的密钥与本机路径。
and **redacted** logs. Please do not upload `TrueToneCap.settings.json` or raw logs — they may contain your own credentials and local paths.
