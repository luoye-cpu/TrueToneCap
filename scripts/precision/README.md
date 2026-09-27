# scripts/precision —— 外部判据脚本

这些脚本的唯一职责：**用别人写的解码器读回本仓库写出来的文件**，判断"设置里的参数到底有没有
进码流""容器到底能不能被外部实现解析"。

为什么必须用外部工具：2026-09-19 的 TIFF 事故里，`StripOffsets` 写成值区指针 + tag 8 配裸
deflate，8 轮门禁全绿 —— 因为仓库内的 TIFF 解析器复刻了写入器自己的结构假设（交接 §3 纪律的
容器层版本）。**这些脚本一律不许 import 本仓库的任何解析实现**，判据来自
ffprobe / ffmpeg / libavif 的 avifdec。

本目录此前躺在被 gitignore 的 `.workbuddy-ai/` 里（工具路径还是硬编码常量），换台机器"跑不起来"
而门禁照绿 —— 等于回归保护形同虚设。现在入库，并被 `scripts/verify.ps1` 的 `[1.7/2]` 步骤调用。

## 退出码约定（所有脚本共用，`verify.ps1` 依赖它）

| 码 | 含义 |
|----|------|
| `0` | 判据全部通过 |
| `1` | 判据不通过 ⇒ 回归，必须修 |
| `3` | **前置条件不满足**（外部工具没装 / 样张没生成）⇒ 已打印"跳过 + 原因"，属 SKIP，**不是通过** |

任何脚本都不许"什么都没判还退 0"。已知且已归档的偏差走 `KNOWN` 白名单（见下），必须带出处。

## 目录与工具解析（`precision_env.py`）

* 外部工具：**PATH 优先**，其次常见安装位置 glob（`C:\ffmpeg*\bin`、`C:\Windows\ffmpeg-*\bin`、
  `C:\Program Files\ffmpeg*\bin`、winget `Gyan.FFmpeg*`、scoop、chocolatey）；
  可用环境变量强制：`TTC_FFPROBE` / `TTC_FFMPEG` / `TTC_AVIFDEC` / `TTC_AVIFENC`（指到不存在的
  路径 ⇒ 该判据 SKIP，不会退回硬编码常量）。
* 新增两件（色彩管线用，见下节）：`TTC_CJXL`、`TTC_ULTRAHDR_APP`。这两件按【候选表先于 PATH】排
  （`cjxl` 必须优先用仓库内 `src/TrueToneCap.Core/Resources/cjxl.exe` —— 产品运行时就是它，PATH 上
  别的版本证的不是产品行为）。候选表在 `precision_env.tool_candidates()` 一处维护，
  `verify.ps1` 问它要、再递给 C# —— 仓库外的绝对路径不许再抄进 C# 第二遍。
* ⚠ "探到文件存在"≠"能用"：`ultrahdr_app` 有两代互不兼容的 CLI（旧版不认 `-j/-z`，解不出产物），
  所以 C# 侧对每个候选先做一次能力探测，探不过 ⇒ 第 4 层 `SKIP`（退 3），不是通过。
* 样张输入：`--samples <dir>` 或 `TTC_PRECISION_SAMPLES`（**显式指定即权威，绝不回退**，否则会
  拿陈旧样张判新问题）；未指定时按 `artifacts/precision/<kind>` → `.workbuddy-ai/probe/<kind>` 找。
  `kind` = `sweep`（`--param-sweep`）/ `audit`（`--container-audit`）/ `wg`（`--wg-samples`）。
  ⚠ 样张的**产出**位置在 `src/TrueToneCap.Tools/FormatBench.cs` 里算死为
  `.workbuddy-ai/probe/<kind>`，脚本侧改不了；`verify.ps1` 生成后会复制一份到
  `artifacts/precision/sweep/` 再判，所以门禁判的始终是仓库内可见、当场产出的东西。
* 脚本自己的产物（解码中间文件、基准样张、构建日志）：`artifacts/precision/`，可用
  `TTC_ARTIFACT_DIR` 覆盖。该目录是纯产物，应在 `.gitignore` 里忽略。

## 逐个脚本

| 脚本 | 判什么 | 依赖 | 单独跑 | 退出码 |
|------|--------|------|--------|--------|
| `chroma_knob_check.py` | ① 只改 `ChromaSubsampling` 的两张样张**字节是否完全相同**（相同 ⇒ 开关没进编码器，不需要解码器参与的最硬证据）② ffprobe 读回的子采样档是否等于请求值（读回是 RGBA 的格式如 JXL 不判 ②） | ffprobe | `python scripts/precision/chroma_knob_check.py` | 生效中的下拉失败 ⇒ 1 |
| `param_sweep_check.py` | 逐旋钮把"请求值 vs 外部读回"摆平了看：色度（AVIF 420/422/444）、位深（PNG IHDR/sBIT、AVIF bits、TIFF BPS 只允许 8/16）、色彩声明（cICP 原色码点 sRGB 1 / P3 12 / BT.2020 9；AdobeRGB 必须无 cICP 且有 ICC）、quality **单调性**（按各格式自己的刻度：AVIF CRF↑=体积↓、WebP 质量分↑=体积↑、JXL/JPEG LI 的 butteraugli 距离↑=体积↓）。另：`c_avifsvt_444` 必须**无产物**（SVT 只支持 4:2:0，请求 444 要报错而不是静默降级） | ffprobe | `python scripts/precision/param_sweep_check.py` | 任一规则不满足 ⇒ 1 |
| `audit_roundtrip.py` | 每条编码路径的容器都按像素判活：SDR 要求"同色域跨容器解出同一张图"（无损容差 1、有损 8；TIFF 的 strip 偏移错、iloc 错、色域范围声明错都会以全黑/串通道现形）；HDR 要求 `transfer=smpte2084 + primaries=bt2020` 且**同通道 PQ 码单调**、非全黑；AVIF 另用 avifdec 做参考解析（ffprobe 读的是 AV1 流级 VUI，NVENC 不写 `color_description_present_flag` ⇒ "unknown" 不构成对容器的判据）；末节报"容器位深 / sBIT / bits_per_raw_sample 三者实际写法" | ffmpeg + ffprobe（必需）、avifdec（可选，缺失只降级） | `python scripts/precision/audit_roundtrip.py`（样张：`--container-audit`） | 解码失败/全黑/串通道/PQ 声明缺失 ⇒ 1 |
| `gamut_depth_consistency.py` | **广色域矩阵必须与输出位深无关**（§4.3 解耦的验收）：同一 P3 源在 d8/d16 两档 ⇒ ① 读回的 cICP 原色必须同为 12 ② 两档像素只允许差最后一次量化（差 >1 LSB 的采样占比必须为 0）③ 对照组：同为 8-bit 时 sRGB 与 P3 两张必须**明显不同**（否则矩阵根本没跑，①②会假绿） | ffmpeg + ffprobe | `python scripts/precision/gamut_depth_consistency.py`（样张：`--param-sweep` 的 `gd_*`） | 任一不满足 ⇒ 1 |
| `ramp_levels.py` | **容器实际交付了几级**（§29 指标②）：64 个紧邻台阶（相邻带在 10-bit 刻度上只差 1，且刻意放中间调）走 `EncoderFactory` 全链 ⇒ ffmpeg 解到 rgb48le、逐带取**中位数**再数级数。硬判三条：A 请求 16-bit ⇒ ≥48 级；B 同家族"高位深 ≥ 8-bit 的 2×"（这条才杀"声明高位深实为 8-bit"）；C 无损容器 8-bit ⇒ ≤24 级。⚠ 不对有损格式做级数**上限**（AV1/JXL 平坦区抖动会合法撑高）；也不再用"≥10-bit ⇒ ≥48"的绝对阈值（实测被 paper-white 归一 ×0.4 洗成 0.61 码/台阶 ⇒ 那量的是夹具的增益假设，见脚本头注） | ffmpeg + ffprobe | `python scripts/precision/ramp_levels.py`（样张：`--ramp-sweep`） | 任一不满足 ⇒ 1；判定 <3 张 ⇒ 3（SKIP） |
| `cicp_consistency.py` | **同一内容跨格式/跨后端，色彩声明是否互相一致**（§29 指标④）：`param_sweep_check` 判"每一行等不等于被告知的值"，这条判"**行与行是否自相矛盾**"—— 逐行各自达标仍可能 PNG 写 cICP=12、AVIF 写 cICP=12+ICC、WebP 写 primaries=1 不带 ICC，同一张图在三个查看器里有三种解释。四条：A 组内显式 primaries 必须彼此相同且等于该 tag 期望（期望表**复用** `param_sweep_check.TAG_PRIM`，不另写事实源）；B 不许声明成别的色域（Adobe 无 H.273 码点 ⇒ 写码即骗人）；C 无码又无 ICC 且非 srgb ⇒ 该图没有任何色彩声明；D 组内 transfer 不许混用；另加一条跨 tag 兜底"所有 tag 都声明成同一个码 ⇒ tag 根本没进容器" | ffprobe（解析器复用 `param_sweep_check` 的 `png_scan`/`avif_nclx`/`avif_has_icc`/`probe`） | `python scripts/precision/cicp_consistency.py`（样张：`--cicp-sweep`） | 不一致 ⇒ 1；可比组 <2 ⇒ 3（SKIP） |
| `wg_container_roundtrip.py` | 广色域样张的同色域跨容器像素一致性（`audit_roundtrip` 的窄版，只看 `--wg-samples` 那批） | ffmpeg | `python scripts/precision/wg_container_roundtrip.py` | 解码失败/全黑/不一致 ⇒ 1 |
| `wg_compat_check.py` | **读数表**：外部工具看到的元数据（ffprobe `color_*` + PNG 的 cICP/iCCP/sBIT 块名）与"忽略标记的查看器会显示的存储像素"，用来解释浏览器认/不认标记的实测差异 | ffprobe + ffmpeg | `python scripts/precision/wg_compat_check.py` | 有文件读不出 ⇒ 1；其余 0（浏览器侧要人看） |
| `ipma_decode.py` | 把 `ipma` 盒按四种 itemID/索引宽度解释各解一遍，看哪种"刚好消费完" —— 用来定"参考实现长什么样"，不猜位含义 | 无（纯 ISO/IEC 14496-12 结构走查） | `python scripts/precision/ipma_decode.py [file.avif ...]` | 文件缺失/无 ipma ⇒ 1 |
| `svt_caps_matrix.py` | **实测矩阵**：avifenc 的 svt/aom × 420/422/444 × 8/10/12 共 18 格，逐格用 ffprobe 读回 pix_fmt 是否等于请求。补的是 `AvifEncoderProbe` 按单轴测、看不见组合性失败（如 12-bit 4:4:4）的那块盲区 | avifenc + ffmpeg + ffprobe | `python scripts/precision/svt_caps_matrix.py [path-to-avifenc]` | 跑完即 0（FAIL/MISMATCH 是编码器能力结论，不是回归） |
| `svt_preset_sweep.py` | **基准**：SVT-AV1 vs libaom 在 `-s 0/2/4/6/8` 上的耗时与体积（输入用生产形状的 16-bit PNG，参数照抄 `NativeAvifEncoder.EncodeFile`）。`PickAuto` 的"libaom 优先"此前只有 `-s 6` 一个点，这条把整根轴补齐 | avifenc + ffmpeg + ffprobe | `python scripts/precision/svt_preset_sweep.py [width] [runs]` | 跑完即 0（只报数） |

| `icc_readback_matrix.py` | **门禁**：五类容器的 ICC 逐份判 —— PNG `iCCP` / JPEG `APP2` / TIFF `34675` / WebP `ICCP` / AVIF `colr(prof)`：① profile 自身结构（长度字段与自身一致、`acsp`@36、色彩空间 `RGB `、PCS `XYZ `、标签不越界）② ICC.1 硬不变量 `rXYZ+gXYZ+bXYZ` 之和 == D50 白（⚠ `wtpt` 允许 D50 或**原始 D65** 两种写法 —— 微软/Adobe 的 sRGB 件就这么记，写严了会误杀）③ Pillow 读回 == 自扫字节 ④ 同色域跨容器必须同一份 profile ⑤ 不同色域两两不同。⚠ JXL 明确报"不参与比对"（该脚本写作时本机无外部 JXL 解码器：`djxl` 不在、Pillow 无插件、imagecodecs 无 `JXL`）—— 不拿自家解析器凑"外部实现"。**2026-09-27 更新**：本机现在有了 libjxl 的 `jxlinfo`/`djxl`，JXL 那格改由 `jxl_hdr_p3_transfer_readback.py` 判（见下表下一行），`icc_readback_matrix.py` 自己的"不参与"措辞暂未改（它仍不读 JXL 容器） | Pillow | `python scripts/precision/icc_readback_matrix.py`（样张：`--cicp-sweep`） | 任一不符 ⇒ 1 |
| `jpeg_icc_readback.py` | **门禁**：我们的 JPEG 到底能不能被外部实现读出 ICC（§31.3-8：写成 APP1 或缺 `ICC_PROFILE\0`+seq+chunk 时，libjpeg-turbo / Pillow / ImageMagick / Chromium 四家全当没声明）＋ 分块字段形态与段长上限 | Pillow + ffprobe | `python scripts/precision/jpeg_icc_readback.py` | 读不到 ⇒ 1 |
| `av1c_vs_bitstream.py` | **门禁**：容器 `av1C` 声明的 profile/位深/子采样，与**自己按 AV1 序列头解出来**的是否一致（杀"av1C 说 4:4:4、码流其实 4:2:0"那类容器级谎报），另核 pix_fmt 与请求色度 | ffmpeg + ffprobe | `python scripts/precision/av1c_vs_bitstream.py` | 不一致 ⇒ 1 |
| `jxl_color_space_discrimination.py` | **门禁**：`cjxl -x color_space=<IDE名>` 到底生不生效。A：`sRGB`/`DisplayP3`/`RGB_D65_202_Rel_SRG` 三枚产物必须**两两字节不同**（相同 ⇒ 该值被忽略，产品路径不能依赖它）；B：故意编的假值必须**报错且不出文件**（A 的对照组，同时证明"拼错枚举不可能静默出货"）。⚠ 输入是自带的无元数据 P6 PPM —— 输入一旦自带色彩元数据，`-x` 会被 cjxl 完全忽略（实测 8 种取值 md5 全同）。⚠ 每枚产物编码前先删同名旧文件：不删时"编码器换成不是 cjxl 的程序"照样退 0（读的是上一轮残留，2026-09-23 实测抓到） | cjxl（自带输入，不吃样张） | `python scripts/precision/jxl_color_space_discrimination.py [<cjxl.exe>]` | 不成立 ⇒ 1；找不到 cjxl ⇒ 3（SKIP） |
| `jxl_hdr_p3_transfer_readback.py` | **取证（2026-09-27 补 §31.3-5，交接 §62）**：JXL 的 HDR 产物里【最后剩下的传递函数】到底是不是 PQ。三条：Q1 用产品自己那份 P3 ICC + 产品自己那个 16-bit PNG 中转，把 `icc_pathname` 与 `color_space=Rec2100PQ` 的四种形态各编一遍比字节（并存时谁赢）；Q2 用 `jxlinfo` 读回 Transfer/Primaries，再用 `djxl --icc_out` 导出【消费者实际拿到的那份 ICC】解 rTRC 与 IEC 61966-2-1 / ST 2084 两条参考曲线比偏差；Q3 判"那份 ICC 到底进没进容器"——只认 `jxlinfo` 的 "N-byte ICC profile" 行，并且必须先过**控制组**（一枚曲线表达不了、只能整份内嵌的 ICC：两个探测器都读得出来，"读不到"才算数）。⚠ 自家 ICC 解析器先在微软系统 sRGB profile 上校尺（曲线偏差 + 原色残差），校不过直接 SKIP；⚠ primaries 只认 `jxlinfo`，ffprobe 会把自定义原色归到最近 H.273 码点（§56.4 踩过） | cjxl + **jxlinfo + djxl**（libjxl 构建产物，在仓库外，要能加载同目录 `jxl.dll`）；样张 = `--jxl-color-product` 落在 `tests/output/jxl-hdr-p3-2026-09-27/` | `py scripts/precision/jxl_hdr_p3_transfer_readback.py` | 有 HDR 产物读回不是 PQ ⇒ 1；缺工具/缺样张/控制组或参照件不过 ⇒ 3 |
| `avif_bench.py` | **基准**（不进门禁）：libaom 在各 `-s` 档的耗时/体积 —— 生产参数 `SpeedArg = "-s 6"` 的出处；它同时负责造 `artifacts/bench/test4k.png` | avifenc（仓库内 `Resources/`） | `python scripts/precision/avif_bench.py` | 跑完即 0 |
| `avif_depth_bench.py` | **基准**（不进门禁）：固定 speed/quality，只变 `-d 8/10` × `-y 420/444`，量化"HDR 4K 慢"里位深与色度各占多少（§29/§30 引用其结论）。2026-09-20 从 `.workbuddy-ai/` 入库并改仓库相对路径 | avifenc + 4K 样张 | `python scripts/precision/avif_depth_bench.py` | 缺样张 ⇒ 1 并提示先跑 `avif_bench.py`；否则跑完即 0 |

**avifenc.exe 本身怎么重建不在这里**：仓库内已有 `scripts/build-avifenc.py`（带固定版本号与
"这个二进制必须含哪些开关"的来龙去脉，且被 `Resources/tools-manifest.json` 的 SHA256 钉住）。
它的早期副本 `.workbuddy-ai/build_avifenc.py` 与 `build-svt-avifenc2.cmd` **没有二次入库**（出现两份
构建配方就会分叉），且随 2026-09-20 的整洁审计连整个 `.workbuddy-ai/` 一并清除 ⇒ 现在只剩这一处配方。
真要改进探测逻辑（比如 vcvarsall 用 vswhere 找）请直接改那一处。

## 已知偏差白名单（不计失败，但必须带出处）

`param_sweep_check.py` 的 `KNOWN`：

* `c_avifnv_444` 色度 444→420 —— NVENC 的 AV1 管线只有 4:2:0（驱动从不写
  `chromaFormatIDC`），已靠 Auto 过滤兜住（422/444 走 libaom）。见 CHANGELOG
  "NVENC 不再静默改色度" 与交接 §26.4。

`chroma_knob_check.py` 里标了"该旋钮不计失败"的两条：WebP（cwebp 无任何色度参数，下拉已撤）、
TIFF（界面上本就没有该下拉，`ChromaTiff` 字段已删）。见 CHANGELOG "全编码器诚实性审计" 节。

**加条目的规矩**：必须写清是哪个格式、为什么不是缺陷、出处在哪一节。没有出处的白名单等于把
判据改绿。（`chroma_knob_check.py` 的 `PAIRS` 里 WebP/TIFF 之所以还留着，是为了在它们哪天被
重新接上下拉时立刻变红 —— 现在靠 `retired` 标记不计失败。）

## 色彩管线三判据（`verify.ps1` 的 `[1.8/2]`，默认执行、无跳过开关）

这三件合起来管"色彩声明到底有没有变成字节/像素"，各吃各的外部事实源：

| # | 判据 | 证什么（判红时长什么样） | 外部依赖 | 覆盖方式 |
|---|------|--------------------------|----------|----------|
| ① | `jxl_color_space_discrimination.py` | cjxl 认不认 `-x color_space=`：三值产物两两相同 ⇒ 标签被忽略，界面上的色域选择是空转 | `cjxl`（仓库内 `Resources/cjxl.exe`） | `TTC_CJXL`，或 argv[1] |
| ② | `dotnet TrueToneCap.Tools.dll --gainmap-color-product` | 产品 GainMap 编码器的三格对照（默认 sRGB / 显式 BT.2020 / 自动非显式）；第 4 层再用 `ultrahdr_app` 把三枚解回 RGBA —— 非显式与默认的重建必须逐字节相同、显式必须不同，否则"别人的实现"看不出这两张图有区别 | `ultrahdr_app`（libultrahdr 示例命令行，在**仓库外**） | `TTC_ULTRAHDR_APP`，或把路径作为 `--gainmap-color-product` 的参数递进去 |
| ③ | `dotnet TrueToneCap.Tools.dll --jxl-color-product` | 走产品自己的 JXL 入口（8-bit 经 PPM 中转、≥10-bit 经 16-bit PNG 中转）证明 `color_space` 真的改产物字节。与 ① 的差别就在"输入是我们自己写的中转文件"——它一旦带上 iCCP / sRGB / gAMA 之类元数据，`-x` 会被 cjxl 整段忽略 ⇒ ① 判得再绿产品路径照样白干。**2026-09-27 追加 HDR 层**：再走一次产品自己的 `JpegXlEncoder.EncodeAsync(HdrOutput=true)`（BT2020 / DisplayP3 / DCI_P3 / AdobeRGB / sRGB / 未知标签 / 手上有没有 ICC 共 8 格），把产物交给 `jxlinfo` 读回——判的是"像素是 PQ、容器声明是不是也还是 PQ"（§31.3-5 那一格实测【不是】，见 §62）。⚠ 这一层的退出码是三态：SDR 两层通过但 HDR 层没解码器可读 ⇒ 整体报 3（SKIP），不洗成绿 | 前两层无（cjxl 由产品代码自己调，仓库内那份）；HDR 层要 **jxlinfo**（+ `djxl`） | `TTC_JXLINFO` / `TTC_DJXL`，或把路径作为 `--jxl-color-product` 的后置参数递进去。⚠ `verify.ps1` 目前对 ③ 递的是空表 ⇒ 门禁里 HDR 层恒为 SKIP，要让它判上得在那一处补 `Extra = @(Get-ExternalToolCandidates -Tool "jxlinfo") + @(Get-ExternalToolCandidates -Tool "djxl")`（候选表已在 `precision_env._EXTRA_GLOBS` 里，本文件不重复实现优先级） |

* 退出码沿用同一约定：`0` 判了且通过 / `1` 判红（阻断整条门禁）/ `3` 前置不满足 ⇒ `SKIP`。
  ② 缺外部解码器时**只能**退 3：前三层全绿也不许把这轮洗成通过。
* 换台机器的后果只是"② 的第 4 层与 ① 变黄字 SKIP"，不再是"只有那台机器能跑"。
* `verify.ps1` 收尾会点名"本轮【没判】的：…（2/3 判了）"，一条都没判上时该步直接红
  （既没 python 又没 Tools 宿主 ⇒ 是接线断了，不是环境缺件）。

## 与门禁的接线

`scripts/verify.ps1` 的 `[1.7/2] 外部判据（可选）` 默认执行：构建 Tools → 跑 **两组**样张
（`--param-sweep` 渐变 + `--ramp-sweep` 64 级台阶）→ 各自复制到 `artifacts/precision/sweep/` 与
`artifacts/precision/ramp/` → 按"每条判据吃自己那组样张"跑 `chroma_knob_check.py`、
`param_sweep_check.py`、`gamut_depth_consistency.py`、`ramp_levels.py`。
探测不到可用的 python 或 ffprobe ⇒ 该步打印 `⚠️ 跳过` 并继续（不阻断），
但**判据真的不通过 ⇒ 退出码非 0，整条门禁红**；样张目录为空也直接红（"退出码 0 却没产出"不算通过）。

其余脚本（容器审计、广色域、SVT 矩阵/基准、ipma）判的是"重新实测编码器能力"级别的问题，
跑一次要编几十上百个文件，所以留在手工档位：改动 AVIF/编码路径时按上表单跑。

`[1.8/2] 色彩管线判据`（上节三条）紧跟 `[1.7/2]` 之后默认执行，**没有** `-Skip*` 开关：
它三步各用自己的前置（cjxl / ultrahdr_app / 无），并进 `[1.7/2]` 会让"没装 ffmpeg"连带
SKIP 掉三条本可以判的判据 —— 那是更粗粒度的假绿灯。
