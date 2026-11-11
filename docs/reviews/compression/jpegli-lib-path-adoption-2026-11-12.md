# JPEG LI 进程内库主路径采纳记录（2026-11-12）

> 一句话：JPEG LI 编码从「cjpegli.exe 子进程」升级为「ttc_jpegli.dll 进程内主路径 + exe 回退」，
> 产物与 cjpegli.exe **逐字节相同**（有强制门禁），ICC 契约在 C# 边界保持不变。

## 1. 动机

| 维度 | exe 子进程（旧主路径） | DLL 进程内（新主路径） |
|---|---|---|
| 临时文件 | 写 11MB PPM 到 %TEMP%（4K 更大）再读回 | 零临时文件 |
| 端到端耗时（2560×1440 d0.5 444） | 249ms（含进程启动+PPM I/O） | 87ms（**2-3×** 提速） |
| ICC 嵌入 | 编码后 C# 手工注入 APP2 | 库原生写 APP2（sRGB 省略情形由 C# 补嵌，见 §4） |
| 崩溃隔离 | 子进程天然隔离 | 无隔离 ⇒ 编码异常会落在 App 进程内 |
| 解锁能力 | 仅 CLI 旗标 | 库 API 全集（app_data/自定义量化表/PSNR 目标等，后续可选） |

崩溃隔离的取舍：编码在 `Task.Run` 的编码线程上跑，进程内崩溃 = 整个 App 崩
（WinUI 下 FailFast）。缓解：shim 全链 try/catch 兜底（`ttc_shim.cc` 顶层）+
C# 侧 catch 后自动回退 exe 路径；jpegli 自身错误走 setjmp（上游既有机制）。
真崩溃（原生层硬崩）仍无解 ⇒ 这是本次升级用稳定性换性能的明确代价，
收益是每次 JPEG/GainMap 保存都免掉一次 11MB 磁盘往返。

## 2. shim 设计（scripts/jpegli-shim/）

**正确性论证 = 与 cjpegli.exe 同一条上游代码路径**：shim 不复刻任何编码逻辑——
输入像素在内存里包一层 PPM/PGM 容器，交给上游 `jpegli::extras::DecodeBytes`
解出 PackedPixelFile（与 cjpegli.exe 从磁盘读 PPM 得到的结构逐字段相同），
再交给同一份 `EncodeJpeg`。差异面为零。

- 参数默认值取 `JpegSettings{}`，与 cjpegli.exe 未传参时的默认一致
  （adaptive quantization 开、optimize_coding 开、std_quant 关）。
- ICC：`ppf.icc + primary=kIccIsPrimary` ⇒ `jpegli_write_icc_profile` 按 ICC.1 规范分块写 APP2。
- 导出面（C-ABI，`__declspec(dllexport)`）：
  `ttc_jpegli_encode_rgb` / `ttc_jpegli_encode_gray` / `ttc_jpegli_free` /
  `ttc_jpegli_error` / `ttc_jpegli_version`。
  输出缓冲在 DLL 内 malloc，必须用 `ttc_jpegli_free` 归还（同 CRT）。
- 链接闭包（build.ninja 实测）：`jpegli-static + jpegli_extras-internal +
  jpegli_threads + jpegli_cms + hwy + lcms2`，无 sjpeg/libpng/zlib/
  libjpeg-turbo/brotli/skcms（开关集与 cjpegli 配方一致，含 `-DBUILD_SHARED_LIBS=OFF`
  —— 否则 jpegli_cms 会编成独立 DLL、extras 按 import 符号解析导致 LNK2019）。

## 3. 逐字节对照门禁（采纳的硬前提）

`scripts/build-cjpegli.py` 末尾强制运行（`--skip-gate` 仅限调试，产物不得采纳）：

- 样张：确定性合成（768×512，平滑渐变 + 高频噪声带 + 纯色块 + 条纹），配方内生成，不依赖外图。
- 12 个参数组合：{444, 420} × {XYB±} × {Progressive, Baseline} × {彩色, 灰度}，
  shim 输出与同树构建的 cjpegli.exe 输出逐字节相同。
- ICC 回读 3 例：非 XYB 两例重组 APP2 链与输入 profile 逐字节一致；
  XYB 一例验「合法 ICC 头 + 不等于输入」（库恒写 XYB 色彩空间 ICC，这正是
  少数解码器翻车的根源，属预期行为）。
- 2026-11-12 实测 **15/15 PASS**；二进制字符串扫描 0 处 libpng/libjpeg-turbo 版本串
  （依赖漂移哨兵，配方内置）。
- 性能基准（同一门禁运行顺带产出，768×512）：shim 8-58ms vs exe 79-268ms。

## 4. ICC 契约：上游优化与产品纪律的边界（重要决策）

上游 `EncodeJpeg` 对 `IsSRGB()` 的 profile **直接省略** APP2 嵌入（sRGB 是 JPEG
隐含默认，cjpegli 同款）。但产品契约是「调用方给了 ICC 就必须嵌」——
GainMap 底图恒嵌标准 sRGB ICC，`GainMapColorProductProbe` 逐字节钉着
（2026-09-20 嵌入纪律：广色域读取链只认 ICC 声明）。

**决策：在 C# 边界补嵌，不改上游语义。** `EncodeViaLib` 产物缺 ICC 而调用方给了
⇒ 用与 exe 路径同一个 `InjectIccIntoJpeg` 补嵌（同 ICC.1 分块规范，插在 APP0 后）。
XYB 模式例外：库恒写 XYB ICC，此时不补——两条 APP2 链并存是旧 exe 路径
XYB+ICC 的坏形态，不复活。

门禁：`UsabilityTests.Jpeg_IccSrgbEmbedContractOnLibPath`（sRGB 嵌入 + 单块 +
位置 + XYB 单链四断言）。这次 verify 首跑抓到的 6 红（色彩管线套件）就是
本契约——上游行为≠产品契约的实例，修在 C# 边界而非放宽探针。

## 5. 交付与回退

- 单一事实源 `src/TrueToneCap.Core/Resources/ttc_jpegli.dll`（731,136 B，
  静态闭合，sha256 见 tools-manifest.json 条目）。
- `NativeLibraryResolver` 就位清单加入 DLL（与 exe 同一条文件系统回退链：
  BaseDirectory/Resources/ → data/Tools/ → PLAN/tools/ → native/）。
- `Directory.Build.props` 把 `Resources\*.dll` 随流到消费项目输出——**测试必须
  走到主路径**，否则行为级测试全在静默测 exe 回退（假绿，与 exe 陈旧副本教训同形状）；
  配套判据 `JpegLiNative.IsLibAvailable`（交付链测试内断言「本进程主路径真的装载成功」）。
- 回退语义：`TryLoadLib()` 失败（缺文件/导出不全/装载异常）或 lib 编码抛异常
  ⇒ 自动落回 cjpegli.exe 子进程（原实现原样保留，含 ICC 手工注入）。
  exe 回退产物仍是合法 JPEG；在库 exe（2026-10-07 build）与 DLL（2026-11-12，
  commit 031a007）的 jpegli commit 不同 ⇒ 回退产物字节可能有微差，已记 manifest。
- `IsAvailable` = lib 或 exe 任一可用；`IsLibAvailable` 单独暴露给测试/自检。

## 6. 门禁同步清单（本次动过的判据面）

- `Test_QualityChain_JpegLiLibPathDeliveryChain`（新增）：8 环交付链 + 主路径装载判据。
- `UsabilityTests.Jpeg_IccSrgbEmbedContractOnLibPath`（新增）：§4 契约。
- `Test_QualityChain_JpegLiDistanceVerbatim` / `DimensionClampWarn` /
  `Test_SettingsWhitelist` 的 xyb 三元钉：**代码形态刻意保留**——参数规范化
  （Clamp/distArg/chromaArg/progArg/xybArg）仍是 Encode 方法体的公共前缀，
  lib 路径把这串参数逐字写进 `Debug("JPEGLI", "lib: --distance …")` 留痕，
  行为腿不需要改就继续咬住值直传。
- `Test_NativeEncodeTimeoutIsJudged` 的 Drain* 计数：JpegLiNative 仍贡献 4 处
  （IsAvailable/GetExePath 探针 + 两条 exe 回退编码路径），lib 路径零新增。
- `UsabilityTests.Jpeg_IccLargeProfile`：改用合成非 sRGB profile（理由见其注释）
  ——sRGB profile 走 §4 契约去了新判据；分块机制（70KB→两段）仍由
  `InjectIccIntoJpeg` 直测腿覆盖。
- 打包链：verify-package.ps1（尺寸下限 256KB）/ verify-msix.ps1（包内必在）/
  Publish.ps1（必需件）/ App.csproj（TtcNativeLibs 计数=2）/ tools-manifest.json /
  licenses/INDEX.json（+apngdis-Zlib 补审计条目，cjpegli.exe 的 licenseFiles 回补）/
  THIRD-PARTY-NOTICES.md（生成器重跑）。

## 7. 已知边界与后续可选

- 灰度 + XYB 组合在上游有慢路径（自测器 486ms vs 43ms，768×512）——产品增益图
  层恒传统 YCbCr，该路径在产品里不可达；API 保留 xyb 参数是为对称。
- `--target_size`（二分搜索目标体积）与 440 色度档是库 API 已解锁的后续可选项
  （JpegSettings.target_size / chroma "440"），产品暂不暴露。
- 上游 commit 031a007 之后若出新版：重跑配方即可（门禁自动重跑），manifest 回填。
