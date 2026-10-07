<!--
  TrueToneCap/PRIVACY.md
  双语隐私政策 —— 中英对照。商店提交时把本文件的公开 URL 填进 Partner Center 的
  「隐私政策 URL」字段（政策 10.5.1 对 Win32/Desktop Bridge 产品为强制要求）。

  ⚠️ 维护须知（改功能时必读）：
    本文件的内容必须与代码实际行为一致。以下每一条都已在代码里核对过并注明出处；
    若日后改动这些路径，请同步改本文件，否则构成商店政策 10.1「误导」风险。
      · Vision 模式上传截图      → Core/Services/TranslationService.cs:854-855
      · 免费通道以 query 传文本  → Core/Services/TranslationService.cs:404
      · 不把 OCR 原文写进日志    → Core/Services/TranslationService.cs:263
      · 设置写程序同目录         → App/Services/SettingsService.cs（SettingsPath）
      · 密钥用 DPAPI 加密存本地  → App/Services/SecretStore.cs
    本仓库为 Apache-2.0；本文件是该项目的隐私声明，不改变许可条款。
-->

# 隐私政策 / Privacy Policy

**TrueToneCap（真色截图）**
最后更新 / Last updated: 2026-10-07

---

## 中文

### 一、总则

TrueToneCap 是一款**本地优先**的 Windows 截图与文字识别（OCR）工具。
**截图、图像处理与 OCR 文字识别全部在你的设备上完成，不会上传。**
只有当你**主动使用"翻译"功能**时，相关内容才会发送到第三方服务。

我们不运营任何服务器，不收集、不存储你的任何个人数据，也不含广告或行为分析 SDK。

### 二、完全在本机进行、不对外传输的功能

以下功能**不产生任何网络请求**：

- **截图与录屏**（含 HDR / 广色域捕获）
- **图像处理与格式编码**（PNG / JPEG / JPEG XL / WebP / AVIF 等）
- **OCR 文字识别**（内置 PP-OCRv6 模型，本地推理）
- **本地保存与图库管理**
- **图像标注**

### 三、会在你使用"翻译"时对外发送的数据

翻译是**可选功能**。**你不使用翻译，就不会有任何数据外发。** 具体如下：

| 翻译模式 | 发送内容 | 发送对象 |
|---|---|---|
| **免费翻译** | 识别出的**文字** | 有道翻译 / Google 翻译公开接口 |
| **LLM 翻译** | 识别出的**文字** | **你自己配置**的大模型 API 端点 |
| **视觉翻译（Vision）** | 🔴 **整张截图的图像内容（PNG）** | **你自己配置**的大模型 API 端点 |

> ⚠️ **请特别注意"视觉翻译"**：它不做本地 OCR，而是把**整张截图**（含画面中所有可见内容，
> 可能包含聊天记录、邮件、文档、个人信息等）直接发送给你所配置的第三方大模型服务。
> 单次上传的图像上限为 6 MB。**请勿在包含敏感信息的画面上使用该模式。**
> 若只想翻译文字而不上传图像，请使用"免费翻译"或"LLM 翻译"。

**"你自己配置的端点"**指：你在设置中填写的 API 地址与密钥。数据最终由**该服务提供方**处理，
其留存与使用规则适用**他们的隐私政策**，我们无法控制。常见提供方包括 OpenAI、DeepSeek、
Google、Moonshot、智谱、阿里云百炼、硅基流动等 —— 以你实际填写者为准。
本应用**不内置任何 API 密钥**，不会替你选择服务商。

### 四、本机存储的数据

| 数据 | 位置 | 说明 |
|---|---|---|
| 应用设置 | 程序所在目录的 `TrueToneCap.settings.json` | 界面偏好等 |
| API 密钥与凭据 | `%LOCALAPPDATA%\TrueToneCap\secrets.bin` | 使用 **Windows DPAPI 加密**，仅当前用户可解密 |
| 截图结果 | **由你指定的位置** | 应用不另存副本 |

我们不会把这些数据上传到任何服务器。卸载应用不会自动删除上述文件（便于你保留设置），
可手动删除。

### 五、日志

应用会在本地写运行日志，用于排查故障。**日志中刻意不记录 OCR 识别出的文字内容**
（源码中对错误响应的处理亦遵循此原则，避免把原文带入日志）。

### 六、儿童

本应用不面向 13 岁以下儿童，也不会有意收集儿童的个人信息。

### 七、你的控制权

- **不使用翻译** → 完全没有数据外发
- **不使用视觉翻译** → 不上传任何图像
- **删除本机数据** → 删除上述设置文件与 `%LOCALAPPDATA%\TrueToneCap\` 目录
- **随时卸载** → 通过 Windows"设置 → 应用"

### 八、政策变更

功能变化导致数据处理方式改变时，我们会更新本文件并修改顶部的"最后更新"日期。

### 九、联系方式

如有疑问，请通过本项目的仓库 Issue 提出：
<https://github.com/luoye-cpu/TrueToneCap/issues>

---

## English

### 1. Summary

TrueToneCap is a **local-first** screenshot and OCR utility for Windows.
**Screenshots, image processing, and OCR all run entirely on your device and are never uploaded.**
Content leaves your device **only** when you actively use the **translation** feature.

We operate no servers. We do not collect or store any personal data.
There are no ads and no analytics SDKs.

### 2. Features that never transmit anything

- **Screen capture** (including HDR / wide-gamut capture)
- **Image processing and encoding** (PNG / JPEG / JPEG XL / WebP / AVIF, etc.)
- **OCR** (bundled PP-OCRv6 model, local inference)
- **Local saving and library management**
- **Image annotation**

### 3. Data sent when you use translation

Translation is **optional**. **If you don't use it, nothing is sent.** Specifically:

| Mode | What is sent | Where it goes |
|---|---|---|
| **Free translation** | Recognized **text** | Youdao / Google public translation endpoints |
| **LLM translation** | Recognized **text** | The LLM endpoint **you configure** |
| **Vision translation** | 🔴 **The entire screenshot image (PNG)** | The LLM endpoint **you configure** |

> ⚠️ **Note on "Vision translation"**: it skips local OCR and sends the **whole screenshot** —
> including everything visible on screen, which may contain chats, email, documents, or personal
> information — directly to the third-party LLM service you configured. The per-request image
> limit is 6 MB. **Do not use this mode on screens containing sensitive information.**
> To translate text without uploading an image, use Free or LLM translation.

**"The endpoint you configure"** means the API URL and key you enter in Settings. Your data is
ultimately processed by **that provider** under **their** privacy policy, which we do not control.
Typical providers include OpenAI, DeepSeek, Google, Moonshot, Zhipu, Alibaba Cloud (Bailian),
and SiliconFlow — whichever you enter. This app **ships with no API keys** and does not choose a
provider for you.

### 4. Data stored on your device

| Data | Location | Notes |
|---|---|---|
| App settings | `TrueToneCap.settings.json` next to the executable | UI preferences |
| API keys and credentials | `%LOCALAPPDATA%\TrueToneCap\secrets.bin` | Encrypted with **Windows DPAPI**, decryptable only by the current user |
| Captured images | **Wherever you choose** | The app keeps no additional copy |

None of this is uploaded anywhere. Uninstalling does not delete these files (so you can keep your
settings); you may delete them manually.

### 5. Logs

The app writes local logs for troubleshooting. **Recognized OCR text is deliberately not written
to logs** (the same rule is applied when handling error responses, so source text never leaks in).

### 6. Children

This app is not directed to children under 13 and does not knowingly collect their personal
information.

### 7. Your controls

- **Don't use translation** → no data leaves your device at all
- **Don't use Vision translation** → no image is ever uploaded
- **Delete local data** → remove the settings file and `%LOCALAPPDATA%\TrueToneCap\`
- **Uninstall** at any time via Windows Settings → Apps

### 8. Changes

If a change in functionality alters how data is handled, we will update this document and the
"Last updated" date above.

### 9. Contact

Questions? Please open an issue at:
<https://github.com/luoye-cpu/TrueToneCap/issues>
