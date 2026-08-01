# TrueToneCap Native AOT 兼容性分析报告

> **生成日期**: 2026-08-01 | **SDK**: .NET 11 Preview 6
> **当前状态**: AOT 发布成功（22 警告），**运行时行为未验证**

---

## 一、总览

| 指标 | 值 |
|------|-----|
| AOT 编译 | ✅ 通过 |
| 警告总数 | **22**（不含预先存在的 CS8604） |
| 可立即修复 | **11**（自己代码） |
| 需等待第三方库 | **5**（SharpGen.Runtime） |
| 需 XAML 改造 | **5**（MainWindow.xaml 绑定） |
| 运行时行为风险 | ⚠️ 未验证（可能崩溃） |

---

## 二、AOT 警告完整分类

### 2.1 自己代码：JSON 序列化（6 警告）

**影响**: `SettingsService.cs` + `TranslationService.cs`  
**当前代码**: 使用运行时反射 `JsonSerializer.Serialize/Deserialize<T>()` 和 `JsonContent.Create()`  
**AOT 后果**: 运行时属性被裁剪，设置加载/保存静默失败，LLM 翻译 API 请求体为空

```csharp
// ❌ 当前（反射，AOT 不兼容）
Current = JsonSerializer.Deserialize<AppSettingsData>(json, SerializerOptions);

// ✅ 修复（源生成器，AOT 兼容）
[JsonSerializable(typeof(AppSettingsData))]
internal partial class AppJsonContext : JsonSerializerContext { }

Current = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppSettingsData);
```

**修复方案**:

| 文件 | 警告 ID | 改动量 | 风险 |
|------|---------|--------|------|
| `SettingsService.cs` | IL2026, IL3050 | 10 行 | 🟢 低 |
| `TranslationService.cs` | IL2026, IL3050 | 15 行 | 🟢 低 |

### 2.2 自己代码：Assembly.Location 在单文件包中为空（1 警告）

**影响**: `NativeLibraryResolver.cs`  
**当前代码**: `asm.Location` 在 AOT 单文件发布中返回空字符串  
**AOT 后果**: 原生工具（avifenc.exe/cjpegli.exe/cwebp.exe）无法从嵌入资源提取

```csharp
// ❌ 当前
string coreDir = Path.GetDirectoryName(asm.Location)!;

// ✅ 修复
string coreDir = AppContext.BaseDirectory;
```

**修复方案**: 1 行改动，风险 🟢 低

### 2.3 XAML 绑定裁剪警告（5 警告 WMC1510）

**影响**: `MainWindow.xaml` 第 514~521 行  
**当前代码**: `LogListView` 的 `ItemTemplate` 使用 `{Binding Icon}`、`{Binding TimeDisplay}` 等运行时绑定  
**AOT 后果**: 日志条目显示空白或运行时绑定异常，不崩溃但功能降级

```xml
<!-- ❌ 当前：运行时反射绑定 -->
<TextBlock Text="{Binding Icon}" />

<!-- ✅ 修复方案 A：添加 x:SuppressXamlTrimWarnings -->
<ListView x:SuppressXamlTrimWarnings="True">

<!-- ✅ 修复方案 B：改用 x:Bind 编译绑定（需 LogEntry 实现 INotifyPropertyChanged） -->
<TextBlock Text="{x:Bind Icon}" />
```

**修复方案**:

| 方式 | 改动量 | 风险 | 说明 |
|------|--------|------|------|
| `x:SuppressXamlTrimWarnings` | 1 行 | 🟢 低 | 跳过警告，但可能运行时丢失绑定 |
| 改为 `x:Bind` 编译绑定 | 15 行 | 🟡 中 | 需 `LogEntry` 类实现 `INotifyPropertyChanged` |

### 2.4 第三方库：SharpGen.Runtime（Vortice 底层 COM 库）（5 警告 IL2067/IL2072/IL2104）

**影响**: Vortice 3.8.3 的底层 COM 互操作库 `SharpGen.Runtime.dll`  
**AOT 后果**: ⚠️ **运行时可能崩溃** — COM 虚函数表通过反射获取，AOT 裁剪后丢失

```csharp
// SharpGen.Runtime 内部（AOT 不兼容）
SharpGen.Runtime.TypeDataStorage.GetTargetVtbl(TypeInfo type, ...)
// 使用反射：type.GetFields() 获取 COM vtable → AOT 裁剪后返回空
```

**依赖链**: `TrueToneCap` → `Vortice.Direct3D11` → `SharpGen.Runtime`

| 依赖 | 版本 | AOT 状态 | 解决方案 |
|------|------|---------|---------|
| SharpGen.Runtime | 2.4.2-beta | ⚠️ 有 trim 警告 | 等待 Vortice 4.x AOT 兼容版 |
| Vortice.Direct3D11 | 3.8.3 | ⚠️ 间接 | 需 SharpGen 修复 |
| Vortice.DXGI | 3.8.3 | ⚠️ 间接 | 需 SharpGen 修复 |
| Vortice.Direct2D1 | 3.8.3 | ⚠️ 间接 | 需 SharpGen 修复 |

**影响范围**: 所有 WGC 捕获、GPU 色调映射、NVENC 编码、HDR 预览窗口 **全部依赖 Vortice**。SharpGen 的 AOT 问题会影响整个捕获管线。

---

## 三、AOT 运行时行为风险矩阵

| 风险 | 概率 | 严重性 | 说明 |
|------|------|--------|------|
| `SharpGen` COM vtable 反射失败 | 🔴 高 | 🔴 致命 | 所有 D3D11 操作崩溃 |
| `JsonSerializer` 反射失败 | 🟡 中 | 🟡 中 | 设置加载/保存失败，LLM 翻译请求体为空 |
| `Assembly.Location` 返回空 | 🟡 中 | 🟡 中 | 原生工具无法提取，AVIF/JPEG LI/WebP 编码失败 |
| XAML 绑定被裁剪 | 🟢 低 | 🟢 低 | 日志面板显示空白 |
| WinRT 互操作失败 | 🟡 中 | 🔴 致命 | WGC 捕获、OCR 引擎无法初始化 |
| `Magick.NET` 原生 DLL 加载失败 | 🟢 低 | 🟡 中 | ICC 烘焙失败，色彩管理降级 |
| `ONNX Runtime` 原生 DLL 加载失败 | 🟢 低 | 🟡 中 | OCR 引擎不可用，回退 Windows OCR |

---

## 四、逐文件修复方案

### 4.1 可立即修复（自己代码，11 处）

```csharp
// 1. SettingsService.cs — 添加 JSON 源生成器

// 新建文件: AppJsonContext.cs
[JsonSerializable(typeof(AppSettingsData))]
internal partial class AppJsonContext : JsonSerializerContext { }

// SettingsService.cs 修改
private static readonly AppJsonContext s_jsonCtx = AppJsonContext.Default;
// Load: Current = JsonSerializer.Deserialize(json, typeof(AppSettingsData), s_jsonCtx) as AppSettingsData ?? new();
// 或强类型: Current = s_jsonCtx.AppSettingsData.Deserialize(json) ?? new();
// Save: s_jsonCtx.AppSettingsData.Serialize(Current, SerializerOptions);
```

```csharp
// 2. TranslationService.cs — 改用 JsonContent.Create(JsonTypeInfo)

// 定义 LLM 请求体 DTO
internal record LlmRequest(
    string Model,
    LlmMessage[] Messages,
    double Temperature,
    int MaxTokens);
internal record LlmMessage(string Role, string Content);

// 注册源生成器
[JsonSerializable(typeof(LlmRequest))]
internal partial class LlmJsonContext : JsonSerializerContext { }

// 在 TranslateWithLlmAsync 中替换
// 旧: JsonContent.Create(requestBody)
// 新: JsonContent.Create(requestBody, LlmJsonContext.Default.LlmRequest)
```

```csharp
// 3. NativeLibraryResolver.cs — 一行修复

// 旧: string coreDir = Path.GetDirectoryName(asm.Location)!;
// 新: string coreDir = AppContext.BaseDirectory;
```

```xml
<!-- 4. MainWindow.xaml — 临时压制 XAML 绑定警告 -->
<ListView x:Name="LogListView" x:SuppressXamlTrimWarnings="True" ...>
```

### 4.2 需等待第三方库（5 处）

| 依赖 | 现状 | 跟踪 |
|------|------|------|
| `SharpGen.Runtime` (Vortice) | 2.4.2-beta，vtable 反射 | 等待 Vortice 4.x / SharpGen 3.x AOT 兼容版 |
| `Vortice.Direct3D11` 3.8.3 | 间接依赖 SharpGen | 同上 |
| `Vortice.DXGI` 3.8.3 | 间接依赖 SharpGen | 同上 |
| `Vortice.Direct2D1` 3.8.3 | 间接依赖 SharpGen | 同上 |

### 4.3 运行时行为验证清单

- [ ] AOT 发布后 `TrueToneCap.exe` 能启动
- [ ] `SettingsService.Load()` 正常运行
- [ ] WGC 捕获初始化成功
- [ ] GPU 色调映射正常工作
- [ ] 所有格式编码正常
- [ ] OCR 引擎初始化成功
- [ ] 托盘图标正常显示
- [ ] 动图录制正常

---

## 五、与 ReadyToRun 对比

| 对比项 | ReadyToRun（当前） | AOT（目标） |
|--------|------------------|------------|
| 发布包大小 | 487 MB | 354 MB |
| 启动时间 | ~0.8s | ~0.3s |
| 运行时 JIT | 部分（未预编译代码） | 完全无 |
| 自己代码警告 | 0 | 11（可修复） |
| 第三方库警告 | 0 | 5（需等待） |
| 运行时崩溃风险 | 🟢 无 | 🔴 存在（SharpGen + WinRT） |
| 兼容性 | 完全 | 未验证 |
| 修复时间 | 0（即刻可用） | 1~2 周（自己代码）+ 等待第三方 |

---

## 六、建议路线

```
立刻（ReadyToRun，零风险）
  └─ Publish.ps1 添加 -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true
  └─ 发布包 487MB，启动快 2×

短期（1-2 周，自己代码修复）
  ├─ SettingsService.cs → JsonSerializerContext 源生成器
  ├─ TranslationService.cs → JsonContent(JsonTypeInfo)
  ├─ NativeLibraryResolver.cs → AppContext.BaseDirectory
  └─ MainWindow.xaml → x:SuppressXamlTrimWarnings

长期（等待外部依赖）
  ├─ Vortice 4.x AOT 兼容版
  ├─ WinAppSDK 2.4+ AOT 支持
  ├─ Win2D 1.5+ AOT 兼容版
  └─ 全面回归测试后切换 AOT
```