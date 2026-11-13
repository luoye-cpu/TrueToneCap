# TrueToneCap Security Guide / 安全指南

## Reporting a Vulnerability / 漏洞报告

If you discover a security vulnerability in TrueToneCap, please report it responsibly:

如果发现 TrueToneCap 存在安全漏洞，请按以下方式负责任地报告：

**Email**: [Your Contact Email]  
**Expected Response Time**: Within 72 hours  

我们会在三小时内响应，并在确认问题后尽快发布安全补丁。

---

## Security Features / 安全特性

### Command Injection Prevention / 命令注入防护

**CWE-78 OS Command Injection** is fully mitigated through `PathSanitizer.SanitizeForEncoding`:

通过 `PathSanitizer.SanitizeForEncoding` 全面防御 **CWE-78 OS 命令注入**：

#### Protected Components / 受保护组件

| Encoder / 编码器 | File Path / 文件路径 | Sanitization Level / 清洗等级 |
|------------------|----------------------|-------------------------------|
| `avifenc` (AVIF) | Input PNG + Output AVIF | Full path validation |
| `cjxl` (JPEG XL) | Input PPM + Output JXL + Intermediate files | Full path validation |
| `cjpegli` (JPEG) | Input PPM + Output JPEG + Gain map | Full path validation |
| `cwebp` (WebP) | Input PNG + Output WebP | Full path validation |
| `ffmpeg` (Animation) | All input/output paths | Full path validation |

#### Invalid Characters Blocked / 禁止的字符

```csharp
// Windows invalid path characters
Path.GetInvalidPathChars() // < > : " / \ | ? * + 

// Shell metacharacters (command injection vectors)
";" "|" "&" "`" "$(" "${" ")" 
```

Any file path containing these characters will throw `SecurityException` before the external process is spawned.

包含这些字符的文件路径在启动外部进程前就会被拒绝并抛出 `SecurityException`。

#### Code Example / 代码示例

```csharp
// src/TrueToneCap.Core/Security/PathSanitizer.cs
public static string SanitizeForEncoding(string rawPath)
{
    var fullPath = Path.GetFullPath(rawPath);
    
    // Check for invalid path characters
    foreach (var c in s_invalidChars)
        if (fullPath.Contains(c))
            throw new ArgumentException($"Invalid character '{c}' in path", nameof(rawPath));
    
    // Check for shell metacharacters
    foreach (var metaChar in s_shellMetacharacters)
        if (fullPath.Contains(metaChar))
            throw new SecurityException(
                $"Path contains potentially dangerous shell metacharacter '{metaChar}'");
    
    return fullPath;
}
```

### Usage Pattern / 使用模式

All encoder calls now sanitize paths before passing to `Process.Start`:

所有编码器调用在传递给 `Process.Start` 之前都会先对路径进行清洗：

```csharp
// Before (vulnerable):
var psi = new ProcessStartInfo
{
    Arguments = $"\"{inputPath}\" \"{outputPath}\"".Trim(),
};

// After (secure):
var safeInputPath = PathSanitizer.SanitizeForEncoding(inputPath);
var safeOutputPath = PathSanitizer.SanitizeForEncoding(outputPath);
var psi = new ProcessStartInfo
{
    Arguments = $"\"{safeInputPath}\" \"{safeOutputPath}\"".Trim(),
};
```

---

## Privacy / 隐私

### Data Collection / 数据收集

TrueToneCap **does not collect any personal data by default**. By default:

默认情况下，TrueToneCap **不会收集任何个人数据**：

- ✅ Screenshot content stays on your device（截图内容保留在本机）
- ✅ No telemetry or analytics（无遥测或分析）
- ✅ No usage statistics sent to servers（无使用统计发送到服务器）

### Optional External Services / 可选的外部服务

Some features require third-party services:

部分功能需要第三方服务：

| Feature / 功能 | Service / 服务 | Data Transmitted / 传输数据 | Opt-out / 可关闭 |
|----------------|----------------|-----------------------------|------------------|
| Translation (free tier) | Edge/Bing/MyMemory/Google | Image text (OCR results) | ✅ Yes / 是 |
| Translation (LLM) | Custom LLM endpoint | Full screenshot (PNG ≤6MB) | ✅ Yes / 是 |

When opted-in, only the minimum necessary data is transmitted. See [PRIVACY.md](PRIVACY.md) for details.

开启这些功能时，只会传输最少必要的数据。详见 [PRIVACY.md](PRIVACY.md)。

---

## Build Security / 构建安全

### Dependency Verification / 依赖验证

CI pipeline runs `dotnet restore --locked-mode` to ensure dependencies match `packages.lock.json`:

CI 流水线运行 `dotnet restore --locked-mode` 确保依赖与 `packages.lock.json` 匹配：

```powershell
# scripts/verify-publish-gates.ps1
dotnet restore TrueToneCap.slnx --locked-mode
# Exit 0 = success (dependencies locked)
# Exit 1 = failure (dependency drift detected)
```

### Artifact Integrity / 产物完整性

All release packages include SHA256 checksums:

所有发布包都包含 SHA256 校验和：

```powershell
# Verify package integrity
Get-FileHash TrueToneCap-v0.3.15-win-x64.7z -Algorithm SHA256
# Compare with .sha256 file provided alongside the package
```

---

## Incident Response / 事件响应

In case of a security incident:

发生安全事件时：

1. **Immediate Mitigation**: Disable affected features via settings or hotkeys
   **立即缓解**：通过设置或热键禁用受影响的功能

2. **Log Analysis**: Check `log/app_YYYY-MM-DD.log` for suspicious patterns
   **日志分析**：检查 `log/app_YYYY-MM-DD.log` 中的可疑模式

3. **Report**: Email us with timeline, impact assessment, and reproduction steps
   **报告**：通过邮件联系我们，附上时间线、影响评估和复现步骤

---

## Version History / 版本历史

| Version / 版本 | Date / 日期 | Security Improvements / 安全改进 |
|----------------|-------------|----------------------------------|
| v0.3.16 | 2026-11-13 | No security-relevant changes; Store display-name localization only (app identity and code unchanged) |
| v0.3.15 | 2026-11-13 | No new CVE fixes this release; offline translation mode added (OCR text no longer leaves the device when selected) |
| v0.3.12 | 2026-11-08 | 🆕 Path sanitization for all external encoders (CWE-78 mitigation) |
| v0.3.11 | 2026-11-04 | Initial dependency update with CVE fixes (libpng → 1.6.59, cjxl → 0.12.0) |

---

## Compliance / 合规

TrueToneCap is designed to comply with:

TrueToneCap 设计符合以下标准：

- ✅ Microsoft Store Submission Guidelines (Section 4.7: Security & Privacy)
- ✅ GDPR Principles (Data Minimization by Design)
- ✅ OWASP Top 10 (A01:2021-Broken Access Control, A03:2021-Injection)

---

**Last Updated / 最后更新时间**: 2026-11-08  
**Maintainer / 维护者**: TrueToneCap Team
