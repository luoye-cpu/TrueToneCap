# Changelog

本文件记录**面向用户**的版本变更。工程化/内部改动（构建、测试、发布脚本）也一并记录，
因为它们会影响"能不能复现这个包"。

格式参考 [Keep a Changelog](https://keepachangelog.com/)；版本号遵循语义化版本，
发布时用 `.\Publish.ps1 -Tag` 打 `v<版本>` 附注标签。

## 本文件怎么读

只记**用户可感知的能力变化**与**工程规范**，一条一行、写现象和影响。
缺陷的根因分析、判据设计、被实测推翻的旧结论，都在维护者文档里
（`docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` 及同目录其它 `docs/*.md`），
本节只留指针。09-15 之前写的长段落属历史遗留，完整版同样在 `docs/` 里，不再新增那种写法。

---

## [0.3.6-beta] — 2026-09-26 ~ 09-27

> 沿用"一条提交 = 一版本"。`v0.3.5-beta`（`cde6168`）**单独打了附注标签** ⇒ 那一版的内容仍可按
> SHA 取出、可复现构建；本节是它之后的六轮（交接 §54 ~ §62）。主线只有两笔，
> 是维护者 2026-09-27 定的"历史提交合并精简"：`614a0b1`(v0.3.4) → 本笔(v0.3.6)。
> ⚠ 下面所有"实测 / 读数"都是本机跑出来的，出处逐条指到 `tests/output/*.log` 或判据脚本。

### 界面与格式：一条回归、四轮改名、默认值改 PNG

- **修掉一条"整张卡片永远看不见"的回归**：AVIF 的**编码器**下拉（自动 / QSV / libaom / SVT-AV1 /
  NVIDIA NVENC）连同色度采样、`.png 后缀`两行，从 `v0.3.5-beta` 那个发布提交起就**完全不显示** ——
  卡片本身在界面定义里是"收起"，而唯一一行"把它展开"的代码被那次提交删掉了。表现就是
  "AVIF 没有选编码器的地方"，而构建、日志、测试全都不响。现在恢复了，并加了一条常驻判据：
  **界面上每一个"以收起起步"的命名控件，代码里必须存在点亮它的那一行**（本机这一层扫出 16 个控件、
  孤儿 0 个；把那一行删掉判据就红，并直接点名 `AvifOptionsCard`）。
- **目标色域对用户只叫两个名字：Rec.709 / Rec.2020**（此前同一档在界面叫 sRGB、卡片叫 BT.709/sRGB、
  声明文案叫 BT.2020、灰字里还写过"非 sRGB"，而"BT.2020 (HDR)"那种写法把【原色】和【动态范围】
  混进一个词）。下拉项文字、"→ 映射到"那行、逐格式声明文案、审计卡、日志与降级提示同步改名。
  ⚠ 盘上那个值**没动**（仍是 `sRGB` / `BT2020`）：那是老配置迁移表与下拉匹配的键，跟着改名
  等于再造一次"索引指向别的东西"的事故。名字现在只有一个来源（Core 的显示名函数），
  界面标签向它要，另有 4 条判据钉住"三处表面必须给出同一个名字"。
- **增益图那一格的描述改准了**：以前写"选 BT.2020 ⇒ 三件全走 2020"，读起来像容器在声明 HDR。
  现在明写**底图恒为 SDR**（这是 Ultra HDR 的结构），「目标色域」决定的是底图与增益图**共用哪套原色**。
- **同构审计（改名余波）**：把"用户可见文本"逐面扫了一遍，抓到三处漏网的旧叫法——审计卡里
  `增益图 + sRGB 底图` / `JPEG XL + sRGB 目标`（同一张表里 BT.2020 半边已改、sRGB 半边没动，
  表内自相矛盾）、WebP 那句界面说明"（sRGB 目标不写声明）"、以及 HDR 收口的降级 Toast 还在写
  "按 HDR 家族的 BT.2020 输出"。三处都改成现行名。
  并补一条常驻判据 `Test_VisibleToastAndHintStrings_UseCurrentNames`：**格式能力表的 Hint** 与
  **降级 Toast 正文**这两面此前【一条锁都没有】（XAML 可见文字、翻译表两侧、逐格式声明卡都有锁，
  所以旧叫法恰好从这两个缝里活下来）。判据带 fail-closed 下限（Hint 要枚举到 ≥5 条、Toast 赋值点
  要 >0），命中旧名就把违规字面量原文打出来。牙是量的：把 WebP 那句 Hint 改回 `sRGB 目标` ⇒ 1 红。
  ⚠ 有意不判的两类：日志（`CoreLog`/`LogService`）继续用内部标签名，那是既定分工；
  ICC 里的描述文本（`"BT.2020"`、`"BT.2020 (PQ)"`）是**要写进文件的字节**，不是界面称呼。
- **非 HDR 的格式说清了自己能做什么**：JPEG / WebP 的灰字与声明文案现在分开两件事——
  没有的是 **HDR 通道**（开关会落空成 8-bit SDR），而**广色域原色的声明是有的**，
  实测证据写进文案（WebP 产物里 ICCP 块在偏移 30、profile 2100 字节；JPEG LI 的 APP2/ICC_PROFILE 在、
  `acsp` 在偏移 56；同一目标的 sRGB 对照组则两者都不写）。
  ⚠ 按"非 HDR 格式只提供 sRGB"把 Rec.2020 从这两个格式里**禁掉**的做法没有采纳，
  原因就是上面那组字节：那会砍掉一条实测成立的能力，并与"广色域面板 ⇒ Rec.2020 + SDR"这条已拍板的
  三档策略正面冲突（自动档照样解析成 2020 ⇒ 变成"界面不让你选、产物却在做"）。
  要按字面收窄请说一句，实现已经写过一遍、能直接接回。

### 格式列表、默认值与 BMP

- **修掉一条"点谁都不是谁"的格式下拉错位**：界面本地化时另抄了一份格式标签数组，
  上一节把增益图从格式里摘掉之后这份数组没跟着改 ⇒ 语言切换与**开机首帧**都会把下拉换成
  `PNG / JPEG Gain Map (HDR) / JPEG LI / JPEG XL / AVIF / WebP / BMP` 这 7 项，而编码侧仍按 6 项的
  顺序解释选中项：点「JPEG LI」出的是 JPEG XL、点「JPEG XL」出的是 AVIF、点「WebP」出的是 TIFF、
  点「BMP」也出 TIFF，而真正的 TIFF 用标签选不到。现在项序与数量只有一个来源（`_formats`），
  标签按格式逐项向本地化层要，并有 4 条门禁判据钉住（含"每个界面格式必须有显式标签档"）。
- **默认编码器改成 PNG**（此前默认 JPEG LI）。⚠ 影响面只有**盘上没有格式键**的人：设置是差量写盘，
  显式选过格式的用户那一条还在，不会被这次改动盖掉。
- **修掉一条"格式下拉开机就是空白"**：重建下拉时换掉整份标签列表会把选中项打回"无选中"
  （WinUI 的行为），而重建排在"按设置选好"之后 ⇒ 每次开机、每次切语言都会把刚选好的格式抹成空白，
  界面空白、编码却按默认值跑。现在重建由自己取回选中项并钳进合法区间；同时任何下拉在
  "设置值在选项里找不到对应项"时**记一条日志**（此前静默空白，直到保存时被兜底值悄悄覆盖）。
- **BMP 不再是"支持的格式"**：`OutputFormat`、编码器工厂、界面下拉里本来就没有它，
  唯一调用点是门禁自己的两个专项测试 ⇒ 删掉这个托管编码器与那 7 条断言。
  测一个用户拿不到的格式，只会让它看起来仍然被支持。同时删掉一份**没有任何调用点**、
  内容还停在旧口径（"AVIF 默认 4:4:4 + 硬件加速"）的格式提示表。
- **下拉里那一档就叫「JPEG」**（2026-09-27 用户口径）。增益图（Ultra HDR）早在 09-26 就并进了 JPEG，
  它**不是第二个格式选项**；而标签当时写的却是 `JPEG LI`——那是编码器实现名（jpegli）与内部枚举名。
  于是一页之内出现三套称呼：下拉叫 JPEG LI、卡片标题叫「JPEG LI 选项」、开关那一格叫「增益图」，
  读起来像界面上还有两种 JPEG。现在**面向用户的每一面都叫 JPEG**：下拉标签、卡片标题、灰字提示、
  逐格式声明卡（增益图那一格改成「JPEG（附带增益图）」）、JPEG XL 的"想要 4:2:2 请改用 JPEG 或 AVIF"、
  日志与降级提示、以及自称"=界面卡片那行"的审计卡。内部枚举名、日志里的格式名与判据消息仍用
  `JPEG_LI` / `JPEG_GAINMAP`（那是实现身份，不卖给用户）。
  新增 7 条判据钉住这件事（含"翻译表两侧都不许留着实现名"与"声明文案七格×两档×HDR 开关都不许出现
  `JPEG LI`/`JPEG Gain Map`"），把标签改回实现名会当场报红。
### 英文界面：一条机制、一处结构性缺陷

- **英文界面补完**：写死在界面里的中文全部有对照（本轮改名后对照表 **189 条 / 0 重复**；此前英文模式下有
  **37 处主窗口文字**、
  以及**五个副窗口整窗**——标注 / OCR 预览 / 选区框选 / 截图缩略图提示 / 第三方许可——仍是中文）。
  按钮悬停说明（ToolTip）此前完全不在翻译范围内（48 处 / 45 条）。
  ⚠ 本轮**未做英文模式真机目视**（#36 同一件事）：判据是源码级的，外加应用启动时一条"英文模式下
  仍有 N 处显示中文"的告警计数，那条计数目前还没有过一次实际读数。
- 修掉一条"有译文但界面看不到"的结构性缺陷：翻译扫描器按控件类型挑着看，未声明的类型静默跳过
  （5 个导航项就是这样），而门禁当时只问"这句话在代码里出现过吗"⇒ 一直绿。
  现在扫描范围是一张显式声明表，界面里出现表外的中文控件**门禁直接报红**，而不是不翻就算了。

### 色彩与 JPEG XL

- **AdobeRGB 作为目标一律收口进 Rec.2020**（2026-09-27 用户口径）。这不是加一条映射，而是**消掉三份互相
  矛盾的映射**：界面侧与 HDR 路早就把 Adobe 并进 BT.2020，只有 JPEG XL 的 SDR 分支把它退成 **sRGB**
  （那条降级的理由"libjxl 会把承载不了的 ICC 归到最近枚举并丢弃 profile"已在 §56.4 被实测推翻）。
  现在三处共用同一个函数（`MapAdobeTargetToWide`：只把 AdobeRGB 换成 BT2020，其余原样通过），
  判据样张宿主也过同一个函数 ⇒ 宿主不再能造出"界面上根本不存在的第四种形态"。
  ⚠ DisplayP3 / DCI_P3 **不受影响**：它们各有忠实出路（cICP 码点 12、P3 的 ICC），
  所以没有一并收口进 2020。BT.2020 比 AdobeRGB 宽 ⇒ 收口不裁色域；HDR 路仍留一行告知。
  ⚠ 但"P3 的 ICC 是忠实出路"这半句在 **HDR 路**当时不成立，见下一条。
- **确证一条挂了 7 天的机制，并把"一旦可达就交出矛盾产物"的那一支改对**（§31.3-5，取证见交接 §62）：
  HDR + DisplayP3 / DCI-P3 目标下，JXL 产物用 libjxl 自己的 `jxlinfo` 读回是 **`Primaries=[P3]` +
  `Transfer=[sRGB]`**，而同一条声明里 `intensity_target` 还写着 1000 nits —— 一张图同时自称"SDR 曲线"与
  "HDR 峰值"，PQ 码值会被按 sRGB 曲线解释（同一批像素实测：203 nits 档读成 295.8 nits = 1.46×，
  100 nits 档差 22.1×）。当年这条记的是"缺产物级实测，本机无 JXL 解码器"，今天一条 `find` 就找到
  `jxlinfo/djxl` ⇒ **一条负断言的有效期只到那次取证为止**。
  ⚠ **可达性先说清楚，别当成正在出货的错**：界面侧唯一的收口点 `CollapseSelectableTag` 把
  DisplayP3 / DCI_P3 一律并成 BT.2020 ⇒ 今天从界面走不进这一支。它值得修是因为分支一直在、
  而且它的注释与同文件 TIFF 那一路的规则**互相矛盾**（TIFF 的注释写着 HDR 非用 PQ 曲线 ICC 不可），
  加一档或改解析顺序就会把它接通，而接通的瞬间产物就是上面那枚。
  根因是同一规则漏了第三处同步：ICC 在 cjxl 里**压过** color fields（`icc_pathname` 与
  `-x color_space=Rec2100PQ` 的四种先后给法，带 ICC 的三枚产物逐字节相同 ⇒ 那行"ICC 缺席才兜底"的
  `color_space` 在 P3 这一档从来没轮到过），而旧写法递进去的那份 P3 profile **只换了原色、TRC 仍是
  sRGB 模板那条 256 点 curv**。HDR TIFF 那一路早就在用带 PQ 曲线的 `GetHdrStandardIccProfile`，
  注释里写着"非用 PQ TRC ICC 不可"。
  ⇒ 改法不是降级，是让 JXL 走同一条：P3 / DCI_P3 递那份 **PQ ICC**。随包 cjxl v0.11.2 把它归约成
  Custom P3 原色 + `Transfer=[PQ]`（192 B → 209 B），且与本来就正确的 BT2020 格逐样本 `max|Δ|=1`
  ⇒ **只动声明、不动像素**，原色与传递函数两个维度不再互相牺牲。用户那份面板 ICC 在 HDR 路不再透传：
  它按定义是 SDR 曲线的，而声明要说的是【编码后】的空间，不是说面板。
  新增的 HDR 层带三条判据（Transfer 必须是 PQ / P3 原色不许塌成 sRGB / DisplayP3 与 DCI_P3 同字节
  且必须不同于 BT2020），牙照旧是量的：把那行改回旧写法 ⇒ `--jxl-color-product` 当场 RC=1 并点名
  `hdr_DisplayP3`（还原后 RC=0）。
  ⚠ 同场两条更正：① `EncodeHdr` 的文档注释说"cjxl 不支持 P3 原色与 PQ 的组合"—— 只对
  `-x color_space=` 的**简写名**成立（确实没有 `Rec2100P3_PQ` 这一枚），容器与 ICC 通路都表达得下；
  ② 这条判据原先在门禁里拿的是**空候选表** ⇒ HDR 层永远 SKIP（一把没有齿的锁），且 `jxlinfo.exe` 的
  运行时 DLL 在 `build/lib/<Config>/` 而不在 exe 同目录，直接跑退 `0xC0000135`——探针里"设
  WorkingDirectory 就能加载同目录 dll"那句是错的（实测无效），已改成把构建根的 dll 目录追加进
  子进程 PATH，并给 `precision_env` 补上这两个工具的真实候选位。
- 判据的期望表跟着这次口径改了三处，并各加了一条**正证**（不是"少判一点"）：
  adobe 的期望码点由"无码点、只带 ICC"改成 **9**；两个 ICC 读回判据把 `*_adobe` 归进 bt2020 组，
  并新增"adobe 样张必须与 bt2020 是【同一份】profile"——一旦哪一侧漏了收口就当场红。
  实测：`cicp_consistency` 判 28 文件 / 4 组 **不符 0 项**（png / jxl / avifsw / avifnv 在 adobe 列
  都写 `primaries=9`，tiff / jpegli / webp 老实只带 ICC），`jpeg_icc_readback`
  **✅ adobe 与 bt2020 同一份 profile**，`icc_readback_matrix` 52 份样张 / 2 个色域组同源。
- **JPEG XL 说清楚了两件事**（原来一条说法是错的，见交接 §56）：
  ① 它**没有 YUV/matrix 这一维**，且**有损产物把像素量化进 XYB** —— 容器里写的是**原图**的三原色与
  传递函数，不是码流里通道值的含义；② color fields 与 ICC 二选一，同时给只走 ICC、fields 被丢弃。
  据此删掉 HDR 分支里一条**永远不可能生效**的 `color_space` 参数（过去当 ICC 缺席时的兜底，实测
  两条同时给时它就被扔掉 —— 兜底是死的）。
- **JPEG XL 的色度档不再有"4:2:2"这个选项，界面直接写明原因**：cjxl 对非 JPEG 输入只有整幅
  1x1 / 2x2 降采样这一种形状（`--resampling` 是整幅盒子降采样，不是"只减色度"），逐轴子采样
  仅在转码 JPEG 时可用 ⇒ 界面上的灰字因此写"想要真 4:2:2 请改用 JPEG 或 AVIF"。可选值同时收进编码器自己那份表，
  界面与命令行不再各写一份（此前界面能选出编码器不认的值）。
### 工程、判据与文档

- 本轮**最终**读数（文档同构审计收尾后重跑，见交接 §61）：全量门禁 `scripts/verify.ps1 -Configuration Debug`
  **exit 0，一个跳过开关都没用**（`tests/output/gate-docs.log`；同参数复跑 `gate-docs2.log` 总用时 **105 s**）
  —— [1/2] 构建 0 警告 0 错误（增量 9.6 s / `--no-incremental` 21.2 s）、**[1.6/2] 发布门禁回归通过**、
  **[1.7/2] 外部判据 12 条全过**
  （含 `cicp_consistency` / `icc_readback_matrix` / `jpeg_icc_readback` / `icc_profile_id_check` /
  `icc_selfbuilt_referee` / `chroma_knob_check` / NVENC 矩阵探针）、**[1.8/2] 色彩管线判据 3/3**
  （cjxl 标签判别、`--gainmap-color-product` 第 4 层、`--jxl-color-product`）另加 `--avif-hdr-depth` 过、
  **[1.9/2] 宿主新鲜度**（2 个宿主都不早于 126 份源码）、**[2/2] 7 套件 1365 断言全绿（32.1 s，复跑 32.8 s）**
  （`869 + 213 + 14 + 4 + 12 + 43 + 210`，跳过 4）。
  套件的数与 §56 的 1347 差 18 条 = 改名单源 4 + 卡片可见性 3 + 切片锚点 1 + JPEG 命名锁 7 +
  Hint/Toast 命名锁 3。
  ⚠ §57 / §60 那两次读数都是 `-SkipPublishGates` 跑的，缺 `[1.6/2]` 那一层：不是"子进程隔离所以跑得动"
  —— 我先前那句是错的，隔离确实有，但 `Publish.ps1` 的单实例守卫照样拒（实测报 `4/5 个场景不符`，
  真实原因一行写的是 `TrueToneCap.exe 正在运行 (PID 57940)`）。**上面这组数是实例关掉之后跑的，
  那一层判到了**；断言总数与跳过计数（1365 / 4）与 §60 逐项相同 ⇒ 这一轮纯文档改动，没有判据被绕过。
  §62（JXL HDR 取证 + 那条 HDR 分支的修复）之后再跑同一套全量：`tests/output/gate-final-62c.log`
  **exit 0**、`[1/2]` 0 警告 0 错误、`[1.8/2]` 由"2/3 判了"变成 **3/3 全部判了且通过**
  （新加的 HDR 层这次真被门禁判到，不再恒 SKIP）、`[1.9/2]` 过、`[2/2]` 仍 **1365 断言**（33.8 s）。
  ⚠ 中间有一轮被自己的守卫判废（`gate-final-62.log` exit 1）：我只改了 Test 项目的一句注释，
  Tools 宿主与它没有依赖关系、被 MSBuild 正确跳过 ⇒ 全局新鲜度判据按字面把那一轮作废。
  处置是**不放宽守卫**、按它给的处方 `--no-incremental` 全解重编后重跑。
- 中途一次整轮被自己的守卫判废：`[1.9/2]` 报"Tools 宿主输出目录（00:40）早于最新源码（00:48）"
  ⇒ 按它给的处方 `dotnet build … --no-incremental` 重编后重跑，才拿到上面那组数。
  （增量构建会合理跳过没动的 Tools 宿主 ⇒ 绝对值不可比、也不许拿上一轮读数顶。）
- 两条新锁都走了**变异实测**证明有牙，其中一条第一次写出来是无牙的：格式下拉那条"形态哨兵"在原文上
  匹配，而函数体内的注释也写着同一个标识符 ⇒ 把"先读后写"改成"先写后读"（就是这个缺陷本身）
  门禁**照样全绿**；改成先剥注释再匹配后该变异报红。细节与另一次旧二进制造成的假读数见交接 §56.5。
- **源码判据不再依赖行尾**（2026-09-27）：四条"切方法体"的判据原先拿 `Environment.NewLine` 当锚点，
  而这个仓库存的是 LF、检出可能是 CRLF ⇒ 一次无关的行尾变化报出三条**内容完全不相干**的红
  （"Auto 分支不该弹 Toast"之类）。现在读源码先归一行尾，且**锚点找不到就判红并停手**，
  不许退化成"整份文件都算方法体"——那会让否定断言恒假、肯定断言恒真。
- **文档同构审计收尾**（交接 §61，逐条回代码核实）：README 整份没跟上这四轮改名（版本徽章与包名停在
  v0.3.4-beta、正文仍写 `sRGB / BT.2020` 与 `JPEG LI`、门禁步骤表缺 `[1.7/2] [1.8/2] [1.9/2]`、
  "一次跑完 8 个套件"实为默认 7），还有两条**方向说反**的陈述：`[1.6/2]` 写"观察期只告警不阻断"而
  `verify.ps1:203` 起早已是 `exit 1`，以及"SDK 由 `global.json` 固定"实为 `rollForward: latestPatch`
  （本机 `dotnet --version` 解析到 `11.0.100-rc.1.26425.128`）。
  ⚠ 一条**用户可见**的灰字说反了实现：设置页写"降级顺序：有道智云开放平台 → 自定义 LLM"，而
  `TranslationService.cs:134-152` 是 **LLM（若启用）→ 有道 → 必应 → Google**，且免费档
  `UseCustomLlm=false`（`MainWindow.Settings.cs:310`）那条链里根本不进 LLM 分支 ⇒ 中英文两侧同改，
  并核过"界面字面量与翻译表键逐字节相同"。另有 12 处坏引用与过期数字一并修掉
  （`§32.8`×3 → `§32.9-4`、`§35.6` → `§36`、`CHANGELOG.md:984` 行号引用改成按标题定位、
  64 行重复片段、`185 条` → 实测 **189 键 / 0 重复**、"七种格式" → 六档）。
  **这一类没补判据**：它错在"文案说了实现的事"，不在"用了旧名字"，命名锁的词表管不到；
  为一条 200 字灰字造一张声明表不划算 ⇒ 改成把"文档里可执行的陈述"列为每轮改名的收尾清单。

## [0.3.5-beta] — 2026-09-24 ~ 09-26

> 沿用"一条提交 = 一个版本"。本轮**不改写历史** ⇒ `v0.3.4-beta`（`614a0b1`）就是本提交的直接祖先，
> 不需要 `refs/backup/*` 与离机 bundle。
> ⚠ 本机已存在一份 `TrueToneCap-v0.3.5-beta-win-x64.7z`（09-25 出，只含下面的"设置与首启"前两条，
> 且 `build-info.json` 记 `dirtyWorktree: true`）⇒ 它的 commitSha `614a0b1` 并不代表它的内容；
> 复核那枚包对应哪份源码，以交接 §49/§50 为准。本提交之后的包才与 commitSha 一致。
> **（2026-09-27 复核：那份 7z 已不在盘上，`publish/` 下现在没有任何压缩包，只有 `_scratch/` 里
> 一份未压缩的暂存目录 ⇒ 上面这段是对当时情形的告诫，别去找那个文件。）**





### 设置与首启

- 设置文件只认**软件同目录**一个位置；改任何设置即时落盘。此前有三处只在内存里改（OCR 语言、
  翻译后端模式、色域索引迁移），表现为"改了 → 重启 → 回到旧值"。
- 存不下去时（装进 Program Files 被 ACL 拒写、只读介质、策略拦写）**记一条日志并按默认值运行**，
  不再悄悄降级到 `%LOCALAPPDATA%`。旧探测写的是"文件不存在 ⇒ 同目录可写"，恰好把真正存不下去的
  那一类全放行，于是症状正是它声称要防的那个；而设置一旦有两个答案，两边内容可以完全不同。
- 修好一道**对老文件从未触发过**的默认值迁移门（实机取证：上一版遗留的 `QualityAvif=63` 被原样
  带上界面）。⚠ 副作用：老设置里你手动调过的**每格式质量档**会在首次启动时回到当前默认表 ——
  这是设计意图（默认表变了就该交还），日志有一行 `默认值修订 2：…` 留痕。
- 程序自己刷新滑块不再被当成"用户改了设置"（实机：存 `2.0` 重启变 `0.5`）。

### 格式与界面

- **JPEG Gain Map 不再是独立格式**，改成 JPEG LI 卡片里的「附带增益图」开关；「目标色域」下拉
  选的就是这张**底图**的色彩空间（sRGB ⇒ base 与增益同在 BT.709/sRGB；显式 BT.2020 ⇒ 三件同走 2020）。
  ⚠ 唯一一处可能"看起来变了"：老设置里的格式号会重映射（旧 `1`=GainMap ⇒ JPEG LI + 开关打开），
  日志记一行；参数只在"你改过增益图侧而 JPEG LI 侧仍是默认"时搬过去。
- 各下拉项只留名字（AVIF 编码器 / 色度 / 位深 / 色彩空间 / 增益图模式 / OCR 引擎 / 日志档位…），
  附加说明统一挪到下方灰字；`自动` 一档不再声称硬件加速。
- libaom + HDR 现在可选 **10/12-bit**（此前 HDR 恒被钉 10）：位深能力从"恒定值"改成
  "下限 10 + 上限按编码器"（QSV 仍 8 且改走 avifenc）。产物侧的 10 vs 12 差异以 `--avif-hdr-depth`
  判据实测，不再靠推断。
- 无损格式（PNG / TIFF）不再渲染一根划不动的质量滑块，整行收起。

### 色彩与 HDR

- 「色域映射」卡片不再"HDR 一开就写 HDR 直通"。它现在按**屏幕证据 / 格式通道 / 目标色域**给出
  五种真实形态之一：容器写 PQ、增益图（底图 SDR + gain map）、屏幕不授权、sRGB 容器装不下 PQ、
  该格式没有 HDR 通道 —— 同一块 HDR 屏上，七种格式就能落进四种不同答案。
- 同一枚目标色域**逐格式的声明通道**现在看得见：PNG 写 cICP `1/13/0`；AVIF 是 YUV 码流，matrix 只能
  随三原色取 `1/13/1` 或 `9/13/9`，不许写 identity（写 0 时部分解码器自行套错矩阵）；JPEG LI 与 WebP
  只有 ICC 且 **sRGB 目标什么都不写**；TIFF 没有代码点通道，连 sRGB 也老实嵌一份；JPEG XL 的
  color fields 与 ICC 二选一。所谓"有些格式是 BT.709"指的是 matrix 那一维；transfer 只有 sRGB(13)
  与 PQ(16)，没有 14。
- **修掉一条**：开着增益图开关、底图显式选 sRGB ⇒ 出的是**没有增益图的普通 JPEG**（"HDR + sRGB ⇒ 降级"
  那条规则原先无条件生效，而 sRGB 底图恰是 Ultra HDR 的约定形状）。这一格现在出真增益图；
  此前"底图 sRGB"只能靠选"跟随显示器"间接拿到 —— 两格本该同一个产物，实际一个有增益图一个没有。
- HDR 开关与产物不再打架，并且都会告知：HDR + PNG 时容器位深至少升到 10-bit（旧行为是 8-bit 默认
  把整张图退回 SDR 色调映射，即"看着在出 HDR、实际出的是 SDR"）；HDR + TIFF 固定 16-bit 时说明原因；
  所有降级提示统一按消息内容去重（此前一个进程只弹一次，第一条之后**所有其他种类**的降级永久无声）。
- 编码设置日志的 HDR 一栏改成"布尔 + 交付形态"：此前 JPEG LI 会记成 `HDR=True`，而产物是 8-bit SDR。
- 硬件 AVIF 一次偶发的参数被拒（`CRF 0` = AV1 无损，本机驱动明确不支持）不再**永久报废整个进程的
  硬件编码**；改按"本次输入不适用"处理。⚠ 顺带一条实测事实：avifenc 的逐位无损硬性要求 4:4:4 +
  full range + identity/YCgCo 矩阵，与"BT.709 矩阵声明"按实现不可兼得 ⇒ 软件档 `CRF 0` 的上限是
  ±1 LSB（MAD 0.3268 / PSNR 52.99），不是"少传一个参数"。

### 工程与判据

- `--display-audit` 新增两块：逐格式交付形态表，以及**真调一次 `BuildEncodingSettings` 回读
  `HdrOutput`** —— "界面的判定"与"编码的落点"之间那条缝过去没人量（它在 App 层，门禁工程只引用
  Core 测不到），本轮的增益图缺陷正是断在这条缝上。审计跑在单实例锁之前，不抢正在运行的实例。
- 门禁：`--avif-hdr-depth` 挂进 `[1.8/2]`（红则拦）；色彩决定新增 140 格全扫（4 屏态 × 5 标签 ×
  7 格式）+ 三条不变式；新增"界面能力表必须等于编码器自己的 `SupportsHdr`"的漂移判据；
  设置落盘点从"点名"改为**扫描式**判据（116 处赋值 / 16 处落盘数不清，只能扫）。
- 全量门禁本轮读数：**7 套件 1320 断言全绿**，`[1.6/2]` 发布门禁回归 5/5，`[1.7/2]` 外部判据与
  `[1.8/2]` 色彩管线判据（含 `--gainmap-color-product` 第 4 层）全部"判了且通过"。
- 本轮新增的 `Core/Settings/`（差量写盘 / 可写探测 / 格式顺序表）、`Core/Encoding/FormatColorFacts.cs`
  与两个取证探针随本提交入库 —— 门禁对未跟踪文件失明，不入库等于判据不存在。

## [0.3.4-beta] — 2026-09-14

> ⚠ **发布状态标注（2026-09-20 整洁审计；同日版本收口更新）**：本节的版本收口方式是
> **"一条提交 = 一个版本"** —— 0.3.4-beta 在 `main` 上只有**一个**发布提交（把此前 5 条
> `build:` / `release:` / `fix:` / `docs:` / `chore:` 折在一起），并打附注标签 `v0.3.4-beta`。
> 合并前的 5 个 SHA 不在 `main` 上；原备份包 `C:\PLAN\pre-squash-v0.3.4.bundle` **现已不在磁盘上**，
> 09-22 收口另建了离机退路 `C:\PLAN\pre-amend-v0.3.4.bundle`（含 `main`、`v0.3.4-beta`、四条
> `refs/backup/*` 与 `refs/backup/pre-amend-v0.3.4`，`git bundle verify` 通过、records a complete history）。
> 本文其余部分按 SHA 的引用（如 `85d876a`）都是**合并前的历史事件**，不是对当前历史的断言。
> 下面的日期 2026-09-14 只是本节的起始日，实际内容一直写到 09-22。
> ⚠ 09-20 深夜又并入一批改动，并在 09-21 ~ 09-22 继续加（许可与版权合规 §33 + 日志/隐私地板 §34
> + 迁移落地与翻译告知 §35 + 必应免密端点接入 §36 + WGC 捕获根因与三档精度策略 §39
> + 广色域需 EDID 面板自证与逐屏读取修复 §40
> + 色彩管线横向审查（两份决定/四套阈值/两块屏）§41 + 收成 ColorDecision 单点 §42
> + 色彩管理不再有开关（默认映射进标准色域）§43 + JXL 色域声明改走 color_space 枚举 §44
> + GainMap 两模式（默认 BT.709 / 显式选 2020 则整张走 2020）§45
> + 三件色彩判据挂进门禁 + 修掉一条既存假绿灯 §46）。
> 版本口径由维护者拍定（2026-09-21）：**并入 `v0.3.4-beta`**，不升新号 ⇒ 提交时按
> "一条提交 = 一个版本"重做这一条发布提交，并把附注标签 `v0.3.4-beta` 移到新提交
> （移动标签的既有做法与校验步骤见交接 §32.14）。


> **本版本为合并发布**，包含两部分此前都未对外发布的内容：
> ① 9-13 那批功能改动（只做了提交，从未打 tag）；
> ② 本批工程化改动（构建 / 测试 / 发布链路）。
> `0.3.4-beta` 从未单独发布过，因此不再拆成两个版本号。
> ⚠️ `publish/TrueToneCap-v0.3.4-beta/` 下若存在旧产物，是从脏工作树构建的临时包，
> 元数据事后补写且 `build-info.json` 缺少 `tools` / `smokeTested` 字段，**不作为发布依据**。

### 🎯 核心修复
- **OCR 崩溃**：`InferenceSession.Run()` 线程安全崩溃 —— 双层信号量串行化（全局 + 引擎级）
- **张量生命周期**：显式拷贝避免 use-after-free
- **Dispose 竞态**：`Dispose()` 等待在途推理完成（5s 超时）后再释放原生会话
- **LockBitstream**：改为同步阻塞；修正 `NV_ENC_LOCK_BITSTREAM_VER` 高位

### 🔤 OCR（2026-09-15 实机测试轮，真实渲染语料 7052 字符）

- **长行整行漏检（此前完全识别不出）**：根因不是模型，而是长行粘连成单个连通域后，
  框宽高比超过 `ExtractBoxes` 硬编码的 `<50` 被当成噪声**整行丢弃**。
  改为可配置上限 **400**，并对超宽框（AR>90）在**无墨列**处拆分成子框分别识别。
  真实渲染字符准确率 **77.35% → 91.48%**（CPU），失败样本 21 → 13
- **DirectML 路径 100% 崩溃（P0，此前从未被覆盖）**：`AsTensor<float>()` 返回的是
  **零拷贝视图**，旧代码把输出张量带出 `using var results` 作用域后再读 → use-after-free。
  CPU 下因内存 arena 未立即归还 OS 而"碰巧可用"从而长期掩盖；DirectML 下触发
  `AccessViolationException`（进程直接终止，`try/catch` 无效）。det 与 rec 两处均已修复
- **DirectML 准确率低于 CPU 4.07pp**：真凶是 GPU 检测前处理的放大上限 `maxRatio=8` 过大
  （已证伪"FP16 精度"与"后端数值差异"两种解释）。两个 EP 统一为 **6.0f** →
  DirectML **87.41% → 91.51%**，并消除长行过召回（109%/115% → 103%/101%）
- **DirectML 性能**：`EnableMemoryPattern` 对 DML 必须为 `false`（ORT 官方要求
  `DisableMemPattern` + `ORT_SEQUENTIAL`），此前对所有 EP 一律设 `true`。
  修正后 **2364ms → 389ms（快 5.9 倍）**，准确率不变
- **DirectML 纹理上限**：D3D12 纹理边长硬上限 16384，超限后检测质量**静默崩塌**
  （300 字符长行 66%）。已按比例收紧 → 100%
- **净效果**（生产默认命中 DirectML）：**87.41% @ 525ms → 91.51% @ 389ms**，
  即准确率 +4.10pp 的同时再快 1.35 倍；相对 CPU（91.45% @ 3502ms）**快 9 倍**
- 已知能力边界：**韩文识别恒为 0%**（模型字典 18,708 条中无谚文），未处理

### ⚡ NVENC 硬件编码
- GUID 按 **4 字节**对齐（此前按 8 字节）→ 修正全部 NVENC 结构体偏移
- 改用 `GetEncodePresetConfigEx`（SDK 12+ 已废弃旧版）
- `tuningInfo=HIGH_QUALITY`（`InitializeEncoder` 与 `GetEncodePresetConfig` 均需）
- `NV_ENC_CREATE_BITSTREAM_BUFFER.bitstreamBuffer` 偏移修正为 @16
- 缓冲区格式 `NV_ENC_BUFFER_FORMAT_ARGB`
- **性能**：4K AVIF 约 0.12s（libaom 约 22s，**约 180 倍**）

### 🧪 功能验证
- 单元测试 24/24、可用性 144/144、编码 14/14 通过
- WGC + NVENC 集成：4K AVIF 455 KB @ 0.12s
- OCR 准确率 85.7%（771/900），无回归

以下部分**不改变产品行为**，只改变"怎么构建、怎么验证、怎么发布"。

### 🏗️ 构建与依赖
- **配置集中化**：新增 `Directory.Build.props`（公共 MSBuild 属性）、`Directory.Packages.props`（依赖版本，CPM）、
  `global.json`（固定 SDK）；各 csproj 不再重复声明
- **依赖锁定**：启用 `RestorePackagesWithLockFile`，5 个 `packages.lock.json` 入库
- **还原源固定**：新增 `NuGet.config`（clear + nuget.org + auditSources）
- **警告即错误**：`TreatWarningsAsErrors=true` 全局启用（含 Debug）
- **移除空壳包** `libwebp 1.0.0`（零资产，实际 WebP 编码走 `cwebp.exe`）
- **着色器双路径对齐**：运行时 `ShaderCompiler` 与构建期 `CompileShaders.ps1` 统一为 **fxc + SM5**
  （此前运行时仍是 dxc + SM6，触发时会产出 D3D11 加载不了的着色器）；
  两侧均改为**枚举 Windows SDK 版本**，不再硬编码 `10.0.26100.0`
- **修复「全新克隆首次构建/发布必定缺少着色器」**：`App.csproj` 原先在静态 ItemGroup 里
  用 `Content Include="Shaders\*.cso"` 收集着色器，而静态 glob 在**项目评估阶段**展开，
  `CompileShaders` Target 在其之后才生成 `.cso` —— 本轮展开时文件尚不存在，于是不会复制。
  因 `*.cso` 不入库，全新克隆与 CI 干净检出的首次 `publish` 都会产出 0 个着色器，
  被 `verify-package.ps1` 拒绝（第二次才正常）。
  改为在 `CollectCompiledShaders` Target 内动态收集。
  实测：清空 `.cso` 后单次 build / 单次 publish，`data/Shaders` 均有 3 个 `.cso`
- **`CompileShaders` 不再写死 `powershell`**：改为运行时探测，`pwsh` 优先、回退 Windows PowerShell 5.1
- **`ShaderCompiler` 移除 2 个无源文件的死项**（`CopyTexture.hlsl` / `MosaicEffect.hlsl`），
  与 `Shaders/` 实际文件对齐

### 🧪 测试基础设施
- 8 个套件的总结输出统一为 `TestSummary.Report`（机器可读 `RESULT` 行），修复了此前 3 种写法导致统计漏套件
- 修复不可证伪断言（色彩 ICC 契约、PQ(0) 容差、ShaderLoader 不可达场景）
- `--all` 补全 8 套件；WGC 套件以独立子进程隔离（独占 STA + GPU）
- **`--usability-tests` 已可在 headless 环境运行**：仅显示器相关 2 个用例在无显示器时显式跳过
- **门禁覆盖率 199 → 352 断言（94%）**
- **修复「伪装跳过」的恒真断言（8 处）**：`TestSummary.cs` 早已明令禁止在环境不满足时
  用 `Assert(..., true, "跳过…")` 伪装成通过，但 `UsabilityTests`(4)、
  `FormatStructureTests`(2)、`EncodingIntegrationTests`(1)、`WgcAvifBackendTests`(1)
  仍在使用；后三个套件甚至**根本没有 `_skipped` / `Skip()` 机制**。
  现已全部改为显式 `Skip()`，并把 `skipped` 计入 `RESULT` 行末尾。
  验证：断言总数 373 不变，usability 由 153 通过变为 152 通过 + 1 跳过，退出码仍为 0

### 📦 发布
- 依赖校验缺失即阻断；新增**原生工具链 SHA256 校验**（`tools-manifest.json`）
- 新增 **[5.5/6] 出包冒烟验证**（`scripts/verify-package.ps1`）：清单 / 尺寸 / PE 头 /
  版本资源 / **二进制内嵌 commit == HEAD** / native 可启动性；不通过则不生成校验和
- 新增 `scripts/pack.ps1`：固定命名压缩 + **压缩包哈希回写 `SHA256SUMS.txt`**
- 新增 `scripts/verify.ps1`：一键构建 + 门禁测试
- **`verify-package.ps1` 的 native 探针不再漏判缺运行库**：此前只 `catch` 异常、无条件打印
  "✅ 可启动"；而缺 VC++ / MinGW 运行库时 `Process.Start` **不抛异常**，只返回
  `0xC0000135` / `0xC0000142` / `0xC000007B`。现显式识别这三类 NTSTATUS 并判失败 ——
  这正是该步骤存在的意义（"缺运行库"此前会一路静默到用户手上）
- **`verify.ps1` 不再把「零断言执行」当成通过**：退出码 0 但解析不到 `RESULT` 行的套件
  现在判为失败。此前该情况只打印"通过（未解析到 RESULT 行）"，与运行器
  "未知参数也返回 0"叠加，构成一条完整的假绿灯路径（典型诱因：套件名拼错）
- `build-info.json` 新增 `windowsSdk` / `lockFilesSha256` / `tools` / `smokeTested` / `dirtyWorktree` / `testsRun`
- **修复依赖锁定文件漂移**：`dotnet publish -r win-x64` 会把 `RuntimeIdentifier` 沿依赖链传递给
  `TrueToneCap.Core`，在其 `packages.lock.json` 追加一段 `net11.0-windows10.0.26100/win-x64`；
  而 Core 是库、自身不声明 RID，提交这一段会让无 RID 的普通还原报 NU1004
  （"项目的运行时标识符已更改"），CI 的 `dotnet restore --locked-mode` 直接失败。
  现于发布后补一次无 RID 还原做归一化，且**必须早于 `lockFilesSha256` 计算**，
  否则 `build-info.json` 记录的是漂移后的指纹

### 🔒 自动拦截
- **新增 git 钩子**（`scripts/git-hooks/`）：`pre-commit` 只构建（约 27s）、
  `pre-push` 构建 + 7 个门禁套件（约 110s）。用 `scripts/install-git-hooks.ps1`
  通过 `core.hooksPath` 挂载 —— **新克隆后需跑一次安装脚本**
- `scripts/verify.ps1` 新增 `-BuildOnly`（只构建不跑测试）；`-SkipBuild` 与 `-BuildOnly`
  同时使用会以退出码 2 报错，避免"以为跑过测试了"
- **`scripts/verify.ps1` 新增 [1.6/2] 发布门禁回归**：调用 `scripts/verify-publish-gates.ps1`
  验证 `Publish.ps1` 的两个门禁分支（工作区干净 / 发布前测试）。该脚本此前
  **没有任何自动调用方**（不被 CI、git 钩子或 `verify.ps1` 引用），回归保护形同虚设。
- **修复门禁回归脚本的套件数漂移**：`Publish.ps1` 加入 `--usability-tests` 后门禁为 7 个套件，
  而 `verify-publish-gates.ps1` 的场景 B/C/D 仍期望 `dotnet run` 6 次 —— 3/5 场景必红。
  由于 [1.6/2] 当时处于"观察期只告警"，这个红拦不住任何人，观察期也就永远无法转绿。
  现已改为 7 并加防漂移注释（**改动 `Publish.ps1` 的套件列表时必须同步此处**）
- **`Publish.ps1 -KeepLast` 的安全阀此前是死代码**：判断写的是
  `$OutputDir -notlike "$publishRoot*"`，而 `$publishRoot` 就是 `$OutputDir` 的父目录，
  条件恒为 false —— `-OutputDir` 指向任意目录时都会在别处按 `TrueToneCap-*` 删目录，
  "绝不误删"的承诺并不成立。现改为与仓库的 `publish\` 目录比较
- **测试运行器不再对未知参数返回 0**：`--bogus-tests` / 无参数现在以退出码 1 结束
  （新增 `--help` / `-h` 返回 0 并打印用法）
- **修复 `--all` 下 WGC 子进程的启动方式**：`dotnet run` 启动时
  `Environment.ProcessPath` 是 `dotnet.exe`，原实现会执行 `dotnet.exe --wgc-avif-tests`
  （缺少程序集路径参数）必然失败；现自动补上 `<Test>.dll`。门禁不受影响（门禁不用 `--all`）
  以子进程隔离执行 —— 它打 `global:git` / `global:dotnet` 桩，同会话执行会污染后续
  `dotnet build`；宿主路径取 `(Get-Process -Id $PID).Path`，不写死 `pwsh`。
  ⚠️ **首次接入为观察期：失败只告警不阻断**，稳定后可升级为阻断门禁
- 新增 `.github/workflows/ci.yml`：preflight 检查（pinned SDK / fxc / 锁定文件）+
  `--locked-mode` 还原 + 调用 `scripts/verify.ps1`。默认 self-hosted Windows 运行器

### 🧹 仓库与文档
- **解除 `publish/PLAN/tools/` 9 个二进制的跟踪**（约 27 MB）：4 个与 `src/TrueToneCap.Core/Resources/`
  逐字节相同的重复副本；5 个已无消费方（`ultrahdr_app.exe` + `libuhdr.dll` 已由
  `JpegGainMapEncoder` 用 jpegli 自行实现而移除外部依赖，另 3 个是其 MinGW 运行库）。
  文件保留在磁盘，仅退出 git 跟踪
- **移除 `publish/PLAN/models/` 3 个重复模型副本**（与 `src/TrueToneCap.App/Models/` 相同）
- `.gitignore`：删除误伤仓库根 `NuGet.config` 的规则；`!publish/PLAN/**` 收窄为 `!publish/PLAN/README.md`；
  新增 `publish/PLAN/tools/` 与 `.workbuddy-ai/`
- **文档修正**：`publish/PACKAGE.md` 流程按脚本实际顺序重写；`docs/ocr-parallel-analysis.md`
  标注其建议与代码实际实现相反；README 发布流程改为 3 步、下载文件名改为实际的 `.7z`；
  Windows SDK 要求由写死的 `10.0.26100.0` 改为「≥ 10.0.26100.0，构建脚本自动枚举最高版本」
- **审查报告自我更正**：`tests/manual/` 不入库是**有意设计**（`.gitignore:85`，由 `3f8b3be`
  引入且 `tests/README.md` 有明确约定），此前被误判为缺陷。
  ⚠️ 同时发现其中 3 个 `glm_*.py` **硬编码明文 API Key** —— 因该目录被忽略，
  密钥从未进入 git 历史（已全历史检索确认），但建议轮换并改为环境变量读取

### 🔍 软件管线审查修复（2026-09-15）

对构建 / 依赖 / CI / 发布 / 测试 / 运行时全链路做了一次审查（详见
`docs/pipeline-review-2026-09-15.md`），并修复其中判据明确的部分。

**构建与依赖**
- **构建治理层首次入库**：`Directory.Build.props` / `Directory.Packages.props` /
  `NuGet.config` / `global.json` / `.editorconfig` / `ci.yml` / 5 个 `packages.lock.json`
  此前从未进入任何 commit —— CI 检出后 `restore --locked-mode` 必然失败。已作为独立提交落地
- **依赖注入降级** `Microsoft.Extensions.DependencyInjection` 11.0.0-rc.1 → **10.0.12**
  （.NET 10 LTS 最新稳定版；11.0 目前只有 preview / rc，尚无 GA）
- 漏洞审计改为覆盖传递依赖（`NuGetAuditMode=all`），并对 NU19xx 免于阻断构建，
  避免上游 CVE 公告突然卡住出包
- fxc 编译器版本可钉定（`-WinSdkVersion`），未命中直接失败而非静默回退

**OCR**
- **语言映射补齐**：`ko` / `ja` / `nl` / `pl` / `th` / `vi` / `ar` 此前全部漏映射，
  落到默认分支返回 `null`，随后被当成 `zh-Hans-CN` 处理 ——
  即**用中文引擎去识别其他语言**。另修正 `zh-en`（中英混合）被误判为合法 BCP-47
  传入 `TryCreateFromLanguage`
- ⚠️ **韩文仍然不可用**：上面的修复只解决了「错误地用中文引擎识别」，
  但 `ppocrv6_dict.txt`（18708 条）中**谚文与韩文字母均为 0 个** ——
  模型本身不具备韩文能力。要让韩文可用需更换 / 扩充字典，属模型层面工作，不在本次范围。
  对照：字典含 180 个日文假名字符，故**日文**在本次修复后具备识别条件
- 识别超时内层 60s → 40s（须严格小于外层 45s，否则下次请求会在信号量上多等 15s）

**崩溃与稳定性**
- **新增崩溃哨兵**：P/Invoke 触发的访问违规在 .NET 上**无法捕获**，进程会直接终止，
  既没有回退机会也来不及写日志（已由隔离探针实测确认）。哨兵在编码前落盘、
  正常返回后清除；若进程被杀死则残留，下次启动据此自动停用硬件编码并回退软件编码
- 设置页新增「硬件编码」开关：可主动关闭加速，也可重新勾选以清除哨兵、恢复硬件编码
- `GpuToneMapper` 三处 `Map` / `Unmap` 补 `try/finally` —— 异常时漏掉 Unmap
  会让后续截图永久阻塞在锁上（界面假死且无法自愈）
- WGC 释放 D3D11 设备时通知编码层丢弃缓存引用，避免 NVENC 使用已释放的设备

**测试门禁**
- 清理约 15 条恒真 / 伪装通过断言（AVIF 编码失败也判通过、7 条循环内空断言等）
- 环境不满足的用例改为**显式跳过**而非伪装通过，跳过数纳入 RESULT 行
- 发布门禁改为校验 `RESULT` 行：退出码 0 但零断言执行判为失败；新增断言总数下限
- git 钩子的 6 处「环境异常 → 静默放行」改为阻断
- 发布脚本：输出目录加白名单、清理动作移到门禁之后（门禁红时不破坏上一版产物）、
  锁文件归一化失败改为硬失败

**设置系统**
- **设置文件改为保存在软件同目录**（`TrueToneCap.settings.json`），便于整目录拷贝的
  绿色/便携用法。同目录不可写时（装到 Program Files、只读介质）自动降级到
  `%LOCALAPPDATA%`，不会静默丢失设置
- **自动迁移旧配置**：首次在同目录启动时，会把 `%LOCALAPPDATA%` 下的旧设置搬过来，
  旧文件保留以便回退 —— 否则改路径会导致"设置凭空消失"
- **修正路径不一致**：`App.xaml.cs` 加载主题时注释写着 `AppContext.BaseDirectory`、
  代码却拼 `LocalApplicationData`（注释与实现相反）。现统一引用
  `SettingsService.SettingsPath` 这一唯一事实源
- **新增加载后规范化**：越界的旧值（质量超出该格式区间、索引越界、枚举字符串非法、
  色度/位深非法）就地修正。此前越界值只在界面上被替换成默认值显示，
  内存里仍是错的 —— 任何绕过 UI 直接读设置的路径都会拿到错误值
- **清理 3 个僵尸设置**：`AnimAvifBackendIndex`（无 UI、无消费方，保存时硬编码写 0）、
  `ChromaPng`（PNG 不支持色度子采样，硬编码写 "444" 且无读取）、
  `SettingsVersion`（注释称用于迁移，实际全仓零引用）。旧配置文件中的这些字段会被忽略

**构建与测试基础设施**
- **修正 `InternalsVisibleTo` 从未生效**：`Core.csproj` 里把它写成 PropertyGroup 下的属性，
  但它是 **Item**，必须放在 ItemGroup 下（`<InternalsVisibleTo Include="TrueToneCap.Test" />`）。
  后果是测试访问 Core 的 `internal` 成员会报 `CS0117`（未包含该定义，而非"不可访问"，
  很有迷惑性）。修复后测试可直接覆盖 Core 内部逻辑
- **新增 OCR 语言映射回归断言 11 条**：`ja` / `ko` / `nl` / `pl` / `th` / `vi` / `ar` 等
  每个受支持的 Id 都钉住映射结果，并验证 `zh-en`（中英混合）不会被当成合法 BCP-47 原样返回。
  映射表缺项不会报错、只会让识别率归零，必须靠断言防漂移

**发布门禁回归（此前形同虚设）**
- **修复两个自身回归**：`scripts/verify-publish-gates.ps1` 实跑发现 4/5 场景失败 ——
  ① 新加的输出目录白名单把回归脚本的临时目录拦下，B/C/D/E 场景根本没走到被测分支；
  ② 门禁改为解析 RESULT 行后，桩函数不输出 RESULT 会被判"零断言执行"而全部失败。
  现回归脚本改用合规输出目录、桩按套件输出模拟 RESULT，**实测 5/5 通过**
- **该步由"观察期只告警"升级为阻断**。此前不阻断，正是这两个回归长期无人发现的原因 ——
  不阻断的门禁等于没有门禁。若某台机器因 PowerShell 宿主差异失败，
  用 `-SkipPublishGates` 临时跳过并上报，不要改回只告警

**发布与验证**
- 新增 `TrueToneCap.exe --selftest` 无头自测：真实启动主程序跑一遍编码链路，
  产出 `selftest.json` 并以退出码表达成败，供 CI / 发布校验使用
- 新增 `models-manifest.json`：模型由「只查存在」升级为 SHA256 强校验
  （仅收 onnx 二进制 —— 文本文件受 eol 规范化影响会导致哈希不稳定）
- 新增 release workflow（手动触发）：打包 → 校验 → 无头自测 → 上传产物
- 着色器四处清单（构建脚本 / 磁盘 hlsl / 运行时 `ShaderCompiler` / csproj `Inputs`）
  增加一致性自检，Profile 不同步直接终止构建
- `download_models.ps1` 不再把 rapidocr 的 small 模型复制进项目目录
  （项目实际使用 medium 模型，该副本既不被加载又易造成混淆）

### 🖼️ AVIF 编码器重构（2026-09-16 ~ 17）

**面向用户的变更**

- **「编码后端」选项取消**，「AVIF 编码器」合并为**单一**下拉：
  自动 / avifenc / Intel QSV / libaom / SVT-AV1 / NVIDIA NVENC
  （原先需要在「编码后端」与「软件编码器」两层里分别选择，且两层会互相打架）
- **Auto 模式改为硬件优先**：严格按 NVENC → QSV → SVT-AV1 → libaom → avifenc 的顺序
  取第一个**可用**的编码器。⚠️ 初版还要求"兼顾色度 / 位深需求"，但那会让默认参数下的
  硬件永远选不上（硬件只支持 420/8-bit），"硬件优先"形同虚设；现改为取最快的编码器，
  能力不足的部分由协商降级并**明确提示** —— 即"用最快的编码器 + 如实告知"
- **手动选择尊重用户**：选定后不再被 Auto 逻辑覆盖；若所选编码器不可用才回退 libaom
- **下拉项动态标注能力**：直接显示各编码器支持的色度与位深上限，并标记本机是否可用（✓）
- **JPEG XL 真正支持 10-bit**：此前 bitDepth 参数被忽略，恒输出 8-bit；
  现 >8-bit 请求改用 16-bit PNG 中转（8-bit 像素无损升采样）
- **参数降级会明确告知**：所选编码器不支持请求的色度 / 位深时，
  日志告警 + 浮层提示实际使用的参数（同一组合只提示一次，不反复打扰），
  不再是"默默改参数、用户拿到与预期不符的文件"
- **默认质量统一对齐视觉无损**：JPEG XL / JPEG LI 的 butteraugli 距离 0.8 → **1.0**
  （cjxl 官方 visually lossless）、AVIF CRF 18 → **15**、WebP 92 → **90**
  （⚠️ 已存在的 `settings.json` 里的旧值优先，新默认值不会覆盖用户的旧设置）

**正确性与清理**

- **TIFF 不再产出非法文件**：设置层曾对所有格式统一放行 12-bit，而 TIFF 会把
  `BitsPerSample` 原样写成 12、数据却按 16-bit 未打包写入 → 图像错位 / 查看器报损坏。
  现按格式钳制（TIFF 只 8/16），且钳制落在编码器入口，任何调用方都绕不过
- **位深改为按格式钳制**：JPEG LI 与 Gain Map 的编码器根本没有位深概念（jpegli 走 PPM，
  maxval 恒 255），这两个字段此前是"能存能显示、实际不生效"的死字段，现恒为 8
- **Auto 协商顺序修正**：必须先由能力表把 Auto 解析成具体编码器、再协商色度/位深。
  顺序颠倒时协商会拿 libaom 的宽松能力表判定"无需降级"，用户请求 444/10-bit 却
  拿到 420/8-bit 且无任何提示
- **消除编码器选择的并发竞态**：软件编码器（libaom / SVT-AV1）不再写入全局静态字段，
  改为按调用实例化携带，并发编码任务不会互相覆盖
- **NVENC 可用性判定补 `SupportsAv1` 过滤**：此前只看"会话能否创建"，
  RTX 30 及更早显卡会被误判可用 → 走一次必然失败的硬件路径再回退
- **QSV 与 MFT 后端去重**：两者自 2026-09-16 起同为 MFT 路径，此前两份近乎逐字的
  重复实现，现合并为单一实现
- **位深协商改为按 SDR / HDR 分场景**：此前按编码器种类一刀切，而两条路径的真实能力完全不同
  （SDR 恒 8-bit、HDR 恒 10-bit）。后果是 SDR 下请求 10-bit 时"协商前后相等"，
  连降级提示都不触发，日志却写着 10-bit —— 用户拿到 8-bit 文件毫不知情。现按场景协商并如实提示
- **HDR 下不再弹错误的降级提示**：AVIF 的 HDR 分支不参与编码器/位深/色度协商
  （位深由 avifenc 固定为 10-bit），此前会照常提示"降级为 420 / 8-bit"却产出 10-bit。
  现改为如实记录日志，并在 HDR 开启时禁用这三项设置、说明原因
- **色度/位深下拉随编码器联动**：选了只支持 4:2:0 / 8-bit 的硬件编码器后，
  不支持的选项会直接从下拉中**隐藏**（不是置灰）并给出说明，而不是等到截图失败后才提示 ——
  置灰项用户仍可能点到，届时只能在截图那一刻降级提示（事后告知），隐藏才是事前阻止
- **修正 AVIF 色度设置不生效**：设置页改色度后只更新了一个冗余字段，而编码管线读的是另一个，
  表现为"改了设置却不生效"，且界面看起来是改成功的
- **NVENC 诊断日志改为仅 Debug 写盘**：Release 版不再无条件往临时目录写 `nvenc_diag.txt`
- 新增回归断言 28 条：能力表协商 / Auto 挑选 / Auto 先解析后协商的调用顺序 /
  按格式位深钳制 / TIFF 非法位深钳制

### 🔎 交接后审查补全（2026-09-17）

上一轮交接留下了若干"注释与实现相反 / 两处事实源不一致"的问题，本轮逐条核实并修复：

- **修复编译阻断**：`CorePipelineTests` 的字符串字面量里混入了真实换行，28 个语法错误，
  仓库此前处于**不可构建**状态
- **消除质量默认值的第二处事实源**：`SettingsService.Normalize()` 的越界兜底仍停留在
  对齐前的旧值（JPEG XL 0.8 / AVIF 18 / WebP 92），与产品默认（1.0 / 15 / 90）相反 ——
  任何越界或 NaN 的质量值都会被"修正"成一个已被废弃的旧默认
- **修复色度兜底抵消产品决策**：设置保存时 AVIF 色度兜底值仍是 `444`，
  ComboBox 无选中项时会把用户选的 `420` 静默改回 `444`，
  恰好抵消"默认色度改 420 以启用硬件编码器"这一决策
- **修复 HDR + Auto 的假警报**：旧实现不区分"用户显式选了硬件"与"Auto 自动解析到硬件"，
  一律弹 Warning Toast ⇒ **用默认设置 + HDR 第一次截图必弹"NVENC 不支持 HDR"**，
  而用户从未选过 NVENC。现 Auto 的自动回落只记 Info 日志，仅用户显式选择时才提示
- **修复降级提示的去重失效**：未降级时会把去重标志清空，
  于是"降级 → 未降级 → 再降级"会重复弹出同一条提示
- **修复关 HDR 后静默改回用户已放弃的编码器**：`_avifEncoderHiddenByHdr` 在用户改选到
  支持 HDR 的编码器后不清除，关 HDR 时仍无条件"恢复"旧的硬件选项
- **修正位深标注与注释**：编码器下拉对所有编码器写死"SDR 8-bit"，
  但软件路径经 avifenc `--depth` 实际可达 10/12-bit，UI 也确实暴露了这些选项
  ⇒ 标注与可选项自相矛盾。现按硬件 / 软件分别标注
- **`Satisfies()` 增加场景参数**：原签名没有 `hdr`，恒按 HDR 语义比较位深，
  与分场景的协商函数存在分歧风险；现直接复用协商结果，保证"UI 判断"与"实际产出"同源
- **AVIF 位深按档收敛**：旧配置里的 `16` 会让下拉匹配不到而停在默认项，
  `_settings` 里却仍是 16（界面与实际不符）；现收敛到 8 / 10 / 12
- **修正多处"描述方向与实现相反"的注释**（SDR 位深、Auto 挑选策略、MFT 回退是否为死代码），
  这类注释比没有注释更危险 —— 后来者会按注释去"修正"正确代码
- 新增回归断言：Satisfies 的 SDR / HDR 场景语义、软件 SDR 10-bit 可达性

### 🔬 AVIF 硬件 10-bit 实测与接线（已落地）

曾长期认为"硬件编码器 SDR 只支持 8-bit"，实测确认那只是**本代码库的限制**，
而非硬件能力：NVIDIA / Intel 支持 AV1 编码的显卡均支持 10-bit
（AV1 Main profile 覆盖 8 与 10-bit）。

**第一阶段实测**（`Test_Nvenc_Av1TenBit`，本机 NVENC 真机编码 + 解析 AV1 序列头）：

| 验证项 | 结果 |
|---|---|
| 改 `bufferFormat` 为 ARGB10 + 喂 A2R10G10B10 | 驱动**接受**，正常产出码流，不崩溃 |
| 产出码流的实际位深 | **仍为 8-bit** |

⇒ 输出位深由 `NV_ENC_CONFIG_AV1.outputBitDepth` 决定（preset 默认给 8），
与输入 bufferFormat 无关。官方头文件明确：*"HW will do the bitdepth conversion
internally from inputBitDepth -> outputBitDepth if bit depths differ. Support for
8 bit input to 10 bit encode conversion only"* ——
即**输入保持 8-bit ARGB 即可产出 10-bit 码流**。

⚠ **2026-09-17 二次勘误**：上文"改成 ARGB10 输入无效"是**错的**，实测已推翻。
真相是 `outputBitDepth`（输出）与 `inputBitDepth`（输入语义）必须**配套**：
当时只改了输入格式、`outputBitDepth` 仍是 8，于是 10-bit 输入被降到 8-bit。
头文件那句 "Support for 8 bit input to 10 bit encode conversion only" 限制的是
**转换方向**（不支持 10→8），不是禁止 10-bit 输入。实测：ARGB10 输入 +
`outputBitDepth=10` ⇒ 解码亮度是真 10-bit 斜坡；8-bit 输入 ⇒ 4 级台阶（升位深）。
详见 `docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` §3。

**第二阶段：权威布局 + 接线落地**

不再靠猜偏移（猜错即不可捕获的访问违规），改为用本机 MSVC 对官方 `nvEncodeAPI.h`
（SDK 13.1）逐字段 `offsetof` 实测，探针源码已入库在 `scripts/probes/nvoff.c`
（2026-09-19 §30 之前它只在被 gitignore 的 `.workbuddy-ai/` 里，那句"保留在 .workbuddy-ai"当时是真话、后来成了假话）。

- **修复 6 处原生缓冲区越界**（驱动按真实结构大小写回，缓冲区却偏小）：
  `NV_ENC_PRESET_CONFIG` 4104→**5128**、`NV_ENCODE_API_FUNCTION_LIST` 512→**2552**、
  `NV_ENC_PIC_PARAMS` 2400→**3360**、`NV_ENC_REGISTER_RESOURCE` 264→**1536**、
  `NV_ENC_MAP_INPUT_RESOURCE` 120→**1544**、`NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS`
  1088→**1552**。这是"不可捕获 AV"最可能的来源
- **修复 `NV_ENC_CONFIG` 偏移整体后移 4 字节**：`gopLength`/`frameIntervalP`/`rcParams`
  实际在 20/24/40（代码写 24/28/44）⇒ **用户的 QP 设置此前一直被写进 `averageBitRate`，
  从未生效**
- **修复纹理直通路径 Register/Map 偏移完全错位**：纹理指针此前被写进 out 字段，
  `RegisterResource` 必然失败 ⇒ 纹理直通从未真正工作过
- **接通硬件 10-bit**：写 `NV_ENC_CONFIG + 280`（`outputBitDepth`）、
  `+284`（`inputBitDepth=8`）。独立验证：`ffprobe` 报 `pix_fmt=yuv420p10le`
- **修复 AV1 序列头解析器（P0，会导致产出损坏 AVIF）**：漏读
  `frame_width_bits_minus_1` 等 4 个字段、`seq_choose_screen_content_tools` 语义写反，
  累积错位约 19 位 ⇒ **把 10-bit 判成 8-bit、4:2:0 判成 4:4:4**。
  而 `IvfWriter.WriteAvif` 的 `av1C` 位深正是由该解析器推导 ⇒
  写出的 AVIF 会声明错误位深。现已按 AV1 规范重写，并用本机实拍码流做夹具回归
  （`Test_Av1SequenceHeader_Parse`）

**能力表与生产路径**：NVENC 的 `MaxBitDepth` 由 8 放开为 **10**，
`FormatEncoders` 的三条编码调用（纹理直通 / CPU 像素 / N 卡设备重试）现传入协商后的位深。
默认位深仍是 8，只有用户显式选择 10-bit 才真正走 10-bit 编码。

⚠️ **已知边界**：硬件只支持 8-bit 输入 ⇒ 目前 10-bit 输出是**升位深**，
与软件路径（avifenc 对 8-bit PNG 加 `--depth 10`）行为一致，但都不是"源精度 10-bit"。
要让**广色域 SDR** 真正保住 10-bit 精度，还需一条 16-bit SDR 像素通路
（当前 SDR 像素在 `ToneMapper.FloatToSRgbBytes` 就被量化成 8-bit BGRA），
此项尚未实施。

**第三阶段：真 10-bit 输入通路接线（2026-09-17 二轮接手完成）**

上文"已知边界"已被打通：`AvifEncoder.EncodeAsync` 的 SDR 分支新增真 10-bit 分支 ——
当协商位深 ≥10 且后端支持真 10-bit 输入（当前仅 NVENC `ARGB10`）时，
用 `ToneMapper.FloatToArgb10Bytes`（与 8-bit 路径共用同一色调映射内核）量化到
A2R10G10B10 后走 `EncodeArgb10Async`；Float16 源的精度由此真正保留
（实测判据见交接文档 §3：解码亮度从 4 级台阶变为 54/64 级真斜坡）。
8-bit 源不升位深（源就只有 8-bit，升了不产生信息）。
NVENC 失败自动回退 8-bit 通路，不丢文件。仍待办：实机截图观感/体积/耗时验证（交接 §5-5）。

**原生层修复（同日审查后落地，构建 0 警告 0 错误 + 7 套件 561 断言全绿）**

- **P0 缓冲区泄漏**：`EncodeRaw` 每帧创建的 NVENC input/bitstream buffer 此后从不销毁
  （DestroyInputBuffer=索引 13 / DestroyBitstreamBuffer=索引 15 连常量都未定义）⇒
  逐帧编码持续泄漏 VRAM/驱动句柄。现于 finally 中按逆序销毁；纹理路径补
  `UnregisterResource`（此前 `FnUnregisterResource` 已定义但零使用）
- **P0 异常路径悬挂锁**：Lock/Map 后若 `Marshal.Copy` 等抛异常，input buffer 锁 /
  bitstream 锁 / 纹理映射永久悬挂。现以状态标记 + finally 保证解锁/解映射；
  驱动返回的 `bsSize` 先校验（0 ≤ bsSize ≤ bufSize×4）再分配拷贝
  （负值此前会抛 OverflowException 且跳过 unlock）
- **P1 构造泄漏**：SDK 探测全失败 / OpenEncodeSessionEx 失败时 `_funcTable`（HGlobal）
  与 `_dll`（模块句柄）永久泄漏 —— `IsAvailable` 每次失败探测泄漏一次。
  构造改为 try/catch 包装，失败释放后 rethrow
- **P1 Dispose 并发**：`DestroyEncoder` 可落在编码中途（原生 use-after-free）。
  新增 `_gate` 锁：编码入口持锁 + `ObjectDisposedException` 检查，Dispose 持锁销毁
- 未解决（记录在案）：REGISTER/MAP 实测偏移与公开头文件定义顺序不符且仓库内不可审计，
  建议把 `nvoff.c` 实测输出提交入库

**双路径位深策略（2026-09-17 第四轮接手落地，门禁 7 套件 563 断言全绿）**

全格式分析（交接 §8 第四轮）确认「8-bit 源升位深输出」普遍存在于 PNG/JXL/TIFF/软件 AVIF
的 SDR 高位深档位。据此按源形态拆成两条诚实路径：

- **8-bit BGRA 源**（普通 SDR 采集，`EncodeAndSaveAsync(byte[])` 两个重载）：
  恒输出 **8-bit** —— 源只有 8-bit，升位深不产生信息，只徒增体积
- **Float16 scRGB 源**（HDR/高级颜色采集，`EncodeHdrFrameAsync` 的 SDR 目标分支）：
  AVIF 委托 `EncodeAsync` 自行分派 —— 用户选 ≥10-bit 且后端支持时走
  **真 10-bit 输入**（保留源精度），否则回退 8-bit；其它格式维持 `ToSdr` 预映射
  （真 10-bit 中转待后续格式接线）

AVIF 编码器提示文案同步：说明 10-bit 仅在 Float16 源下保留真精度、8-bit 截图恒 8-bit。

**双路径全格式接线（2026-09-17 第五轮，目标修正：喂编码器它能接受的最高质量输入）**

按用户修正的目标重新设计——不是"特意量化 10-bit"，而是让编码器拿到它能接受的
最高质量输入。新增 `ToneMapper.FloatToBgra16Bytes`（与 8/10-bit 共用同一色调映射
内核与 gamma 查表，量化到 16-bit 大端 BGRA——Float16 约 11-bit 有效精度，16-bit
容器几乎无损），各格式 SDR 分支在「Float16 源 + 协商位深 ≥10」时接线：

- **PNG**：`FloatToBgra16Bytes` → `ManagedPngEncoder.Encode16`（IHDR 16-bit 容器，
  sBIT 按请求位深声明实际精度）
- **JXL**：新增 `NativeJxlEncoder.Encode16Bgra16`（16-bit PNG 中转 → cjxl）；
  byte[] 源入口钳 8
- **TIFF**：BPS 只有 8/16 ⇒ ≥10-bit 请求统一 16-bit 输入输出
- **AVIF · NVENC**：维持 ARGB10 真输入（编码器上限即 10-bit，在该边界降位）
- **AVIF · 软件**（libaom/avifenc）：新增 16-bit PNG 中转分支（此前吃 8-bit BGRA 即升位深）
- **WebP / JPEG LI / GainMap**：容器无 >8-bit 档 ⇒ 入口诚实钳 8（防回退 PNG 时升采样）

byte[]（8-bit BGRA 源）入口的 `EncodeSdrAsync` 同步钳 8：PNG/JXL/TIFF 三处。
门禁 7 套件 563 断言全绿。

**全格式实机验证 + 完整门禁（2026-09-17 第五轮·续）**

`Tools --sdr16-bench` 实测（Float16 源 512² 渐变+纹理，位深请求 10）：
PNG `rgba64be`（3.2KB/42ms）、TIFF `rgba64le`（14.1KB/14ms）、JXL
`rgba64le,bits=10`（1.8KB/450ms）均为真高位深产物；AVIF Auto→NVENC ARGB10
（0.6KB/63ms，ffmpeg 解码有效）；WebP 诚实 `yuv420p` 8-bit；8-bit 源 + 请求 10
被双路径钳制正确压回。详见 `docs/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` §8。
完整 `scripts/verify.ps1`（含 [1.6/2] 发布门禁回归，此前被运行中的旧发布实例阻断）
在用户授权关闭该实例后全绿。

**AVIF 选项 UI 与能力标注修正（2026-09-18）**

- **NVENC 的 HDR 不再标注为"不可用"**：HDR 10-bit 硬件直通（PQ 10-bit 输入 + 容器 colr
  声明 PQ，AV1 仅 4:2:0，失败自动回退 avifenc）已接通，能力表 `SupportsHdrPath` 亦已放开
  NVENC。UI 此前按"是否硬件"一刀切地写"HDR 不可用"，与管线实际行为相反 ⇒
  改为统一按 `SupportsHdrPath` 标注；HDR 模式下只隐藏 QSV（NV12 8-bit 输入）
- **位深"真不真"如实呈现**：编码器下拉新增「真 10-bit 输入」标注（当前仅 NVENC，直接查
  后端 `SupportsTrue10BitInput`，UI 不另立清单）。只有「源为 Float16」且「编码器支持真
  10-bit 输入」时 >8-bit 才保留源精度，其余是容器级升位深 —— 提示文案按此区分，
  避免"设了 10-bit 就以为精度真的提高了"
- 位深下拉标注 12-bit 仅软件编码器可达；AVIF 格式提示补充 HDR / 广色域 SDR / 硬件 4:2:0 边界
- 修正能力表中"ARGB10 输入无效"这一已被实测推翻的结论（见交接文档 §3）
- 构建 0 警告 0 错误 + 8 套件 584 断言全绿

**AVIF 编码器实测能力探测（2026-09-18）**

下拉可选项此前依据的是**手写静态能力表**（"文档说它能"）。现改为**本机实测**：
对所选编码器真编码小样张，再解析产物 AVIF 的 `av1C` 配置盒读回**实际**位深与色度
（判据：请求 12-bit 只读回 10-bit ⇒ 判 12-bit 不可用；请求 4:4:4 只读回 4:2:0 ⇒ 判 4:4:4 不可用）。

- 探测在后台跑一次并缓存；未完成时按静态表**宽松**显示（绝不把没测过的选项当成不支持）
- 编码器下拉与提示区分标注「实测 / 声明」；自动模式会显示本机实际解析到的编码器
- 判据经**外部 ffprobe 交叉验证**（不用本仓库自己的第二份实现）：
  `420/8→yuv420p`、`444/8→yuv420p`、`420/10→yuv420p10le`、`420/12→yuv420p10le` 四组全一致
- 本机实测（RTX 5080）：NVENC 仅 4:2:0 / ≤10-bit（与声明一致）；SvtAv1、avifenc 实测可达 12-bit
  （静态表只声明 10）；QSV 本机无 AV1 MFT ⇒ 不可用
- 新增诊断命令 `dotnet run --project src/TrueToneCap.Tools -- --avif-caps [--ffprobe]`
- 构建 0 警告 0 错误 + 8 套件 598 断言全绿

**SVT-AV1 实测 + 查官方仓库（2026-09-18）**

用户要求对 SVT-AV1 做实测并查官方仓库。关键事实：**本机内嵌 avifenc 1.4.2 只编译了 aom
（`--version` 仅 `aom [enc]:3.14.1`），完全没有 SVT-AV1** ⇒ 无法在本 build 上实测 SVT-AV1，
此前那行"SvtAv1 实测 ≤12-bit"其实是 **aom 的结果被误归因**（已用 `IsKindAvailable(kind)`
闸门堵住）。

- 官方结论（AOMediaCodec）：libavif `src/codec_svt.c` 对 420/422/444 与 >10-bit（12-bit 走
  Professional profile）均有代码支撑；SVT-AV1 官方《Encoder User Guide》对独立 app 只声明
  8-bit yuv420p 与 10-bit yuv420p10le（app 输入限制 ≠ 库能力）。两口径不一致，
  在换成"带 svt 的 avifenc 构建"前无法定论，故能力表对 SvtAv1 位深保守取 10
- 新增 `NativeAvifEncoder.VersionText` / `SvtUnavailableReason`：把"不可用"升级为
  **可行动的原因**（如"本机 avifenc 未编译 SVT-AV1"），实测探测与 UI 提示共用
- UI 选中不可用的 SvtAv1 时提示带出具体原因；新增测试 `Test_AvifProbe_SvtNotCompiledIn`
  断言 svt 未编译时 `IsKindAvailable=false` 且**绝不给出实测值**（防 aom 误归因）
- 8 套件 **603 断言 / 0 失败**（unit 276）

**SVT-AV1 落地：内嵌 avifenc 换成自编译构建 + 实测能力收敛（2026-09-19）**

上一条留下的"无法定论"现已定论 —— 办法是自己编一个带 SVT 的 avifenc 换进去。

- 内嵌 `Resources/avifenc.exe` 由官方 aom-only 构建换成**自编译**的 libavif 1.4.2
  （`--version` = `aom [enc/dec]:3.14.1, svt [enc]:v4.1.0`）⇒ `--codec svt` 第一次真的下发到
  编码器，SvtAv1 选项从"纸面可用"变成"可实测、可选用"
- 构建配方**入库**为 `scripts/build-avifenc.py`（不再是下载件，发布可追溯性依赖它）。
  三个必开开关各挡过一次失败：`AVIF_ZLIBPNG=LOCAL`（本项目软件路径喂的是 **PNG**，关掉就读不了自己的输入）、
  `AVIF_JPEG=LOCAL`（libavif 在 `AVIF_BUILD_APPS=ON` 时强制要求）、`CMAKE_BUILD_TYPE=Release`
  （Debug 下汇编带 `-gcv8`，NASM 3.02 在 SVT 的 SSE2 汇编上直接 0xC0000005 崩溃）
- **SVT-AV1 实测能力（两条独立判据：本仓库 av1C 解析 + 外部 ffprobe 读回 pix_fmt）= 仅 4:2:0、≤10-bit**：
  色度×位深九宫格里 `--codec svt` 只有 420/8 与 420/10 产出，422/444 与 12-bit 一律
  `Failed to encode image: Encoding of color planes failed`；**同一输入 `--codec aom` 九宫格全绿**
  ⇒ 不是容器或输入的问题。此前"SvtAv1 实测全色度 / ≤12-bit"是 aom 的结果被误归因（§17.3 的坑）
  - 能力表 `SvtAv1` 因此由「全色度 / ≤10-bit」改为「仅 420 / ≤10-bit」；`Avifenc`（不传 `--codec`，
    默认解析到 aom）位深由 10 改为实测的 12
  - `PickAuto` 的软件兜底顺序改为 **libaom 在 SVT 之前**：换二进制后 `IsKindAvailable(SvtAv1)` 真的为
    true，这条顺序从此决定无硬件机器的默认编码器。本机实测（4K/420/10-bit、管线固定 `-s 6`，各 3 次取中位）
    svt 0.42s/228KB vs aom 0.37s/193KB —— "SVT 通常更快"在该档位下不成立，且它只有 420/≤10-bit，
    让 Auto 选它等于把 422/444/12-bit 无谓降级。手动选 SVT 不受影响（新增 2 条 PickAuto 断言固化）
  - 兼容性验证：新旧二进制在 SDR 8/10-bit、16-bit 中转、HDR PQ + clli、`--icc`、stdin PNG 各路径
    产物的 pix_fmt / 色彩标记**逐条一致**（外部 ffprobe 对照），无回归
- `tools-manifest.json` 同步：avifenc 的 sha256 / sizeBytes / 来源改为"自编译 + 各依赖 pin + 配置开关"，
  `$comment` 的升级步骤补上"官方件 / 自编译件"两条路径。⚠ 包内体积 12.4 MB → **16.4 MB**（+4 MB）
- 🔧 **顺带修掉一处假绿来源**：Core 的 `Resources/*.exe` 既没有嵌入也没有任何复制规则，
  `NativeLibraryResolver` 的"嵌入资源提取"分支从未命中；此前 Tools/Test 跑到的 `native/avifenc.exe`
  是某次遗留在 `bin/` 里的陈旧副本 —— **换掉 `Resources/` 下的 exe 不会生效**。
  现在 `Directory.Build.props` 把四个 exe 复制进非发布项目输出的 `Resources/`（resolver 的第 1 条兜底路径），
  `TrueToneCap.App` 刻意排除，避免发布包里多存一份 16 MB
- 验证：构建 0 警告 0 错误；完整 `scripts/verify.ps1`（含 [1.6/2] 发布门禁回归）**7 套件 583 断言全绿**
  + `--wgc-avif-tests` 21 断言 = 8 套件 604 断言 / 0 失败

**🔴 TIFF 输出对标准解码器呈全黑（2026-09-19 实机编码测试发现并修复）**

做广色域兼容性样张时顺手解码了自家 TIFF，结果**整幅全黑**（`0,0,0,0`）。两个结构性缺陷叠加：

- `StripOffsets(273)` / `StripByteCounts(279)` 是 `LONG count=1` = 恰好 4 字节，
  按 TIFF 6.0 §2 **必须内联在 IFD 条目的 value 域**；编码器却写成了"值区指针"。
  于是读者把指针值（166）当 strip 偏移、把下一个指针（170）当字节数 → 解压的是 IFD 自己 → 全黑。
- `Compression = 8`（Adobe Deflate）规范含义是 **zlib 封装**的 deflate，而 `DeflateStream` 写的是**裸流**
  → 即便偏移正确，libtiff 也会 `incorrect header check`。改用 `ZLibStream`（保留 tag 8 语义，
  比换 tag 32946 兼容性好）。

**为什么门禁一直是绿的**：`FormatStructureTests` 自带的 TIFF 解析器**复刻了写入器的同一个错误假设**
（把 273 的 value 域当指针再解引用）——正是交接 §3 那条"交叉验证绝不能用自己的第二份实现"。
判据已改为按规范取值（内联），并新增"strip 前两字节必须是合法 zlib CMF/FLG"断言；
像素内容本身一直是对的（BGRA→RGBA、16-bit、Alpha 不透明、广色域矩阵均已生效），坏的只是容器结构。

修复后实测：`ffmpeg`（libtiff）解 `sRGB_tiff` 得 `(80,0,0,255)`、`BT2020_tiff` 得 `(64,17,4,255)`，
与同名 PNG 的存储值一致 ✅。

**广色域 SDR 双标记的查看器兼容性实测（2026-09-19，交接 §4.5 风险① / §16.4 遗留）**

用产品自己的编码路径产出 8 色块图案（含中性灰对照组）× 3 色域（sRGB / DisplayP3 / BT2020）
× 4 容器，样张在 `.workbuddy-ai/probe/wg/`（生成命令 `Tools --wg-samples`）。

- **元数据写出确认**（外部 ffprobe / PNG 块名，非本仓库解析器）：
  PNG `cICP[prim=9 trc=13 matrix=0 range=1]` + `iCCP`(3144B) **双标记齐备**；
  P3 为 `prim=12`；sRGB 只写 `cICP[prim=1...]` 不写 ICC ✅；AVIF 容器 `color_primaries=bt2020/smpte432`
  + `transfer=iec61966-2-1` ✅；TIFF 无 CICP 机制、只带 ICC（符合 §4.5 预判）
- **Edge（Chromium）实测：认标记。** 同一引擎内两次读数 —— 正常解码 vs `colorSpaceConversion:'none'`：
  `BT2020_png` 存储 `(63,16,4)` → 渲染 `(80,0,0)`，**正好回到 sRGB 对照图的 (80,0,0)**，也就是编码前的源色；
  `DisplayP3_png` `(72,8,4)` → `(79,0,0)` 同理；AVIF 两例同向；
  sRGB 对照组 `auto == none`（不做多余转换）✅ ⇒ §4.5 风险①（按 sRGB 直读导致过饱和/偏色）
  在现代浏览器上**不成立**；且 `none` 读数与 ffmpeg 逐块一致（±1 舍入），两个独立实现互证
- JXL 与 TIFF 浏览器不支持解码（不是标记问题），系统查看器 / Photoshop 一条腿仍待人工过
  （样张已备好，直接双击 `.workbuddy-ai/probe/wg/*.png` 与 `BT2020_*.avif` 对比即可）
- **preset 维度补测**（`-s 0/2/4/6/8`，1280×720 16-bit 输入，各点 3 次取中位）：
  SVT 在 `-s 0~4` 确实比 aom 快 30~60%，但管线固定 `-s 6` 上两者持平且 SVT 体积更大
  ⇒ `PickAuto` 的 libaom-优先结论不变，注释与断言已改写为"优势档在 -s ≤4、本产品走不到"
- 验证：完整 `scripts/verify.ps1` + `--wgc-avif-tests` = **8 套件 605 断言 / 0 失败**，构建 0 警告 0 错误

**🔴 硬件 AVIF 容器两处结构缺陷：libavif 一直拒绝解析我们的 NVENC 产物（2026-09-19）**

全格式容器审计（新增 `Tools --container-audit` + `.workbuddy-ai/audit_roundtrip.py`，判据全部
来自**外部解码器**）发现：ffmpeg 能解我们的 NVENC AVIF，但 **libavif（AVIF 的参考实现）直接拒读
整份文件**。两个独立缺陷：

- `hdlr` 是 FullBox，写入时漏了 version/flags 4 字节 ⇒ 读者把随后的 `pict` 当成 `pre_defined`
  ⇒ `Box[hdlr] contains a pre_defined value that is nonzero`（盒体 29 → 33 字节，与 avifenc 一致）
- `colr` 的 nclx 三个字段（primaries / transfer / matrix）按规范是 **16-bit 大端**，代码每个只写
  1 字节 ⇒ 盒体 16 字节（合法恒 **19**）⇒ 整个 `colr` 被丢弃 ⇒ **NVENC 的 HDR 内容对外没有色彩
  声明**，查看器按 SDR 解释会整体偏色

修复后 libavif 完整解析并读回正确声明：SDR `prim=1 trc=13 mat=1`、HDR `prim=9 trc=16 mat=9`。
（Range：NVENC=Limited 与驱动实际输出一致，avifenc=Full —— 属编码器既有差异，非容器缺陷。）

⚠ 判据教训（与上一条 TIFF 同类）：此前"实机验证 AVIF 成功"的依据是 **ffprobe 有 pix_fmt +
自家 av1C 解析器读到 10-bit**，而 ffmpeg 对这两处都宽容。过程中我曾按 ISO 14496-12 的字面规则
（flags bit0 决定 item_ID 宽度）把 `ipma` flags 改成 0x01，结果 libavif 反而报
`illegal property index [385]` —— 与 avifenc 产物逐字节对照后确认 AVIF 的既成约定是
flags=0 + item_ID 16-bit + index 8-bit，已回退，对照方法固化为 `.workbuddy-ai/ipma_decode.py`。
回归测试补 6 条（`--wgc-avif-tests` 21 → 27）：hdlr 的 FullBox 形状、colr 盒体 = 19 字节、
nclx 字段可解码为合法码点 —— 修复前这些必然为红。

顺带一条**假警报记录**（防后人重踩）：审计首轮报 JPEG LI / GainMap 与 PNG 差 17，实为样张传了
`Quality=90` —— 这两个格式的 Quality 刻度是 **butteraugli 距离 0.5–3.0**（见各自
`GetQualityRange`），90 被 clamp 到 25 = 最差档；改回 1.0 后误差 ≤2。判产品有 bug 之前先确认刻度。

审计现状：7 格式 × SDR/HDR × 软件/硬件（含 NVENC 容器）共 17 份样张 **全部通过** ——
SDR 要求同色域跨容器像素一致（无损容差 1、有损 8–10），HDR 要求容器/码流声明为 PQ + BT.2020
且同通道单调。样张在 `.workbuddy-ai/probe/audit/`。

**高位深输出的"精度声明"实测与决定（2026-09-19，不改代码）**

新增审计项（`audit_roundtrip.py` 的"精度声明审计"）测得：Float16 源（binary16 ⇒ 有效位 ≈11）
走生产形状（§11.2 把 SDR 的 OutputBitDepth 提到 16）时——

| 输出 | 容器位深 | 声明的有效位 |
|---|---|---|
| PNG 16-bit (rgba64) | 16 | **不写 sBIT** ⇒ 视同 16 位全有效 |
| JXL | rgba64，`bits_per_raw_sample=16` | 16 |
| TIFF | BPS=16 | 16（容器无声明机制） |

即三者都按**容器位深**呈现，而不按源的真实 11 位有效精度标注。
**决定（用户拍板）：维持现状** —— 认为"16-bit 容器、低位为源精度之外的噪声"是可接受的常见做法，
不改元数据也不改像素。

顺带把两条遗留项用实测关掉：
- §15 记的"SDR ≥10 路径 sBIT 少报实际 16-bit 精度"**方向反了**：真实管线 Float16+SDR 恒提到 16 ⇒
  根本不写 sBIT；测到的 `sBIT=10` 只在"调用方直传 10"时出现（产品路径不产生该组合）。
- §20 担心的"JXL 的 10-bit 声明依赖 sBIT 链路、未逐字节审查"**已实测无误**：请求 10 → `bits_per_raw_sample=10`，请求 16 → 16。
- byte[] 8-bit 源的 `sBIT=8`（"标记实际精度而非容器位深"）行为不变。

**编码管线全量分析 + 参数传递实测（2026-09-19）**

三路读码（软件链 / 硬件链 / 设置→`EncodingSettings` 链）＋ 自建参数扫描
（`Tools --param-sweep`，判据由外部 ffprobe 读回产物，共 34 张样张）。
完整结论与未修清单见 `docs/handoff-…-2026-09-17.md` §26。

### 🐛 修复
- **JPEG XL 选「16-bit」后重启被静默改回 10**：`SettingsService.FixBitDepthJxl` 仍是 §12 之前的
  `v > 8 ? 10 : 8`，而 §12.2 已给下拉加了 16-bit 档 ⇒ 表现为"设置没保存"。实测三档产物确实不同
  （`bits_per_raw_sample` 10/12/16，体积 881/828/935 B），故 16 保留：`v switch { 8=>8, 16=>16, _=>10 }`。
  同时更正被实测证伪的旧断言"10 与 12 产物完全相同"。
  新增泛化回归 `Test_Settings_BitDepthOptionsSurviveNormalize`：**XAML 位深下拉的每个档位都必须被
  对应 `FixBitDepth*` 原样保留** —— 拦住"UI 加了档、加载器不认"这一整类漂移（unit 277 → **293**）。
- **慢编码进度提示跨线程读控件**：`System.Threading.Timer` 回调里内插 `FormatCbo.SelectedIndex`；
  `SetStatus` 自身会 marshal，但参数表达式仍在后台线程求值 ⇒ 改为在 UI 线程先取好格式名。

### 📊 实测结论（参数传递是否正确）
- **quality 单调性 4/4 通过，但三把尺子不同**：AVIF 软件 = CRF（752→460→394 B，值越大越省）、
  WebP = 真质量分（470→564→832 B）、JPEG XL / JPEG LI = **butteraugli 距离**
  （1541→981→543 / 4929→3925→2667 B，值越大越省）。改任一档都要按各自刻度判断。
- **色度**：AVIF 软件 420/422/444 全部如实产出；SVT 请求 444 **按预期报错**而非静默降级；
  NVENC 请求 444 会**无声产出 420**（chromaFormatIDC 从不写，今天只靠 UI 协商挡住 —— 已记为待修）。
- **位深**：PNG(IHDR 8/16 + sBIT)、TIFF(8/16)、AVIF(av1C/pixi 8/10/12)、JXL(raw_bits 8/10/12/16)
  均与请求一致。
- **色域**：PNG cICP 1/12/9 分派正确；AdobeRGB 正确"只写 iCCP 不写 cICP"；
  AVIF 的 ICC 经 16-bit 中转 PNG 成功进入容器（此前怀疑会丢，实测成立）。
- **未修清单（8 项，含 1 个 P1）**已写入交接 §26.4：最关键的一条是
  `CapturePipelineService.BuildEncodingSettings` 在后台线程读 `_settings.Current`
  的位深/色度/AVIF 编码器选择，**绕过了 §11.3 的快照纪律**（且快照里的 `AvifEncoderKind` 只进了日志）。
  → 该 P1 与第 2、7 项已于同日修掉，见下一小节。

### 🔒 编码设置快照纪律（2026-09-19，交接 §26.6）
- **P1 · 后台编码线程不再读活设置**：`BuildEncodingSettings` 的快照参数改为**必传**
  （`AppSettingsData.Snapshot()`，UI 线程按下截图那一刻取），方法体内不再有 `_settings.Current`；
  唯一自建设置的 `EncodeAndSaveAsync(byte[], …, OutputFormat, …)` 重载**整段删除**
  （删除依据是编译证据：删后 0 错误 ⇒ 无调用点）。
  用户可见后果：按下截图后 0.5–2s 内（JXL / libaom AVIF 的编码耗时量级）改位深/色度/编码器下拉，
  **本次产物不再被中途改掉**，与"按下那一刻"的界面选择一致。
  同源顺带修：`.png 后缀`开关改由快照决定（原先编码器用快照值、文件名用活值）；
  删掉只进日志不参与编码的 `EncodingUiState.AvifEncoderKind`（编码器选择回归单一事实源）。
  新增回归 `Test_Pipeline_EncodeSettingsRequireSnapshot` 钉住这条纪律（unit 293 → **297**）。
- **P2 · 删除三个"写了没人读"的编码参数**：`EncodingSettings.AvifChroma` / `DisplayBitDepth` /
  `PreferGpuEncode`（＋只喂给它们的 `AppSettingsData.DisplayBitDepth`）。它们不会生效，
  却让人以为存在"AVIF 专用色度""按显示器位深匹配精度""GPU 编码总开关"三条通道。
  行为变化：**无**（色度的事实源仍是 `ChromaSubsampling`，位深仍是 `OutputBitDepth`，
  GPU 直通仍由是否传入纹理决定）。
- **P2 · 系统 nits 覆盖用户设置不再静默**：检测到系统 SDR 白点 / HDR 峰值时记一条 Info 日志
  说明"纸白 X→Y、峰值 …（系统检测值优先）"，日志里能看出用户设的值被谁盖掉了。
- **P2 · NVENC 不再"静默改色度"**：驱动从不写 chromaFormatIDC ⇒ 请求 4:4:4 实际产出 4:2:0 且不报错。
  产品路径本有能力协商挡着，但后端被直接调用时无人知晓。现在只要码流确实由 NVENC 产出，
  就把这件事写进既有的降级原因通道（日志 + 一次性提示）。**编码行为未变**，变的是"是否有痕迹"。
  判据由 unit 用例钉住（420/未指定 ⇒ 不留痕，避免默认设置弹假警告），接线由实机用例钉住
  （真用 444 编一次 NVENC 后回读原因）。

### 🖥️ AVIF 编码器下拉补全（2026-09-19，交接 §26.8）
- 下拉此前只有 5 项，而设置校验接受 **6** 个值 ⇒ 手工写进 settings.json 的
  `"Avifenc"`（由 avifenc 自选内部编码器）**实际生效、界面却显示 Auto**。现补上该项。
  既有用例 `Test_SettingsWhitelist_MatchesXamlTags` 从只查 OCR 一条下拉泛化为
  按 (ComboBox, 属性) 成对检查，现同时覆盖 OCR 与 AVIF 编码器两条（unit 301 → **304**）。

### 🎨 AVIF 默认色度改回 4:2:0，Auto 改为「按色度挑编码器」（2026-09-19，用户拍板）
- **默认色度**：AVIF 的色度采样默认值 4:4:4 → **4:2:0**（设置里的下拉默认项、字段默认值、
  以及运行时会覆盖界面文本的中英文案，三处一并改）。
- **Auto 的规则**：4:2:0 ⇒ NVENC / QSV（硬件）；4:2:2、4:4:4 ⇒ libaom（软件真支持广色度）。
  也就是 Auto 现在"选一个不必降级的编码器"，不再是"用最快的编码器然后提示降级"。
  用户可见后果：**默认设置直接吃到硬件加速**（4K AVIF 约 0.1–0.7s，libaom 约 22s），
  并且默认路径不再出现"能力限制：参数从 444 降级为 420"那条提示。
- **手动选编码器时**：选 NVENC / QSV 只暴露 4:2:0；选 libaom / SVT-AV1 按**本机实测**能力暴露
  （实测优先、未实测时按能力表；不支持的选项直接隐藏而不是置灰）。这条此前已经成立，本轮核实未改。
- ⚠ 行为边界：**HDR + 4:2:2/4:4:4 会走软件路径**（NVENC 的 AV1 管线只有 4:2:0），HDR 用户应保持 4:2:0；
  **位深仍不参与 Auto 过滤**（12-bit 请求在硬件上仍会被钳到 10-bit 并提示）。
- 取舍理由与三次改动的历史写在 `AppSettingsData.ChromaAvif` 的注释里
  （2026-09-17 → 420、2026-09-18 → 444、2026-09-19 → 420 + 过滤，**第三次不是简单回退**）。
  回归：`Test_AvifCaps_PickAuto` 11 条、`Test_AvifChroma_DefaultIs420` 6 条（含本地化那处）。
- ⚠ 待人工核对：色度下拉三项与编码器下拉新增项的**文字长度/观感**（中英双份都变长了）。

### 🔍 全编码器诚实性审计与修复（2026-09-19，交接 §27）
逐格式用**外部判据**验证"设置里的参数到底有没有进码流"：同一样张只改一个参数看产物**字节是否相同**、
`ffprobe` 读回 `pix_fmt`、以及编码器自己的 `-h` 帮助。抓到并修掉：
- **JPEG XL 的色度下拉此前是装饰**（4:4:4 与 4:2:0 产物逐字节相同）。现已接 cjxl 的
  `--resampling`（444→1、420→2；cjxl 没有 4:2:2 档，故 422/未知一律不传，绝不拿 4x4 冒充）。
  真编码器复验：两档产物现在确实不同，且 4:2:0 更小。
- **WebP 的色度下拉已撤掉**，改为静态说明。理由不是"我们没接线"，而是**格式本身**：
  cwebp 没有任何色度参数，有损 WebP 恒 4:2:0、无损为 RGB（ffprobe 亦恒 `yuv420p`）。
  同时删掉 `ChromaWebP` / `ChromaTiff` / `ChromaGainMap` 三个只写不读的设置字段
  （旧 settings.json 里残留的键按 System.Text.Json 默认行为被忽略，不影响加载）。
- **三处"Release 下完全无声"的静默保底现在都会告知**（日志 + 每会话一次提示）：
  cwebp 缺失 → 产物悄悄变成 `.png`；QSV/MFT 失败 → 回退 libaom；avifenc 拒绝所请求位深 → 悄悄按 8-bit 重编。
- **QSV / 系统 MFT 的质量参数此前被静默丢弃**（固定 10 Mbps 码率）。编码器名称现在明写这一点，
  不再让人以为拖质量滑块有用。
- **PNG / TIFF 产物改为原子写**：此前直接写最终路径，编码中途出错会在磁盘留下半成品坏文件而文件名看起来"已保存"。
- **JPEG LI 的色度实测确认三档全生效**（`yuvj420p/422p/444p`），无需改动；自检里 `Quality=80f`
  误喂给 butteraugli 刻度（合法 0.5–3.0）的问题一并修正。
- 删掉零调用方的 `QsvEncoderNative.cs`（`IsAvailable` 恒 false 的停用路径），教训保留在 MFT 路径的注释里。

新增回归：`Test_EncoderHonesty_Audit20260919`（六条契约）与 `Test_Jxl_ChromaActuallyChangesOutput`
（真调 cjxl 编两档、断言产物必须不同且 4:2:0 更小）。审计当时的未修清单见交接 §27
（GainMap 色度与 QSV 同源两条已在下面两节完成，仍剩"位深下拉在 §4.3 下无实际效果"等待拍板）。

### 🎚 GainMap 色度成为真设置（2026-09-19，用户决定）
- JPEG Gain Map（Ultra HDR）新增**色度采样**下拉：**默认 4:2:0**（兼容性最好、体积最小），
  可手动选 4:2:2 或 **4:4:4**（色度不压缩、体积更大）。
- 此前这不是"设置没生效"而是**主路径根本不读它**：base JPEG 被硬编码成 4:4:4，只有 HDR 关闭时的
  降级旁路读设置 ⇒ 同一格式因走哪条路而色度不同。现在两条路同源。
  实测（256² 样张）：修前 420 与 444 **逐字节相同**（都 7043 B）；修后 4:2:0 = 6110 B（`yuvj420p`）、
  4:4:4 = 7043 B（`yuvj444p`）⇒ **默认档省约 13%**。增益图平面仍按单通道灰度编，不受该开关影响。
- 顺带修掉一处**过期的兜底值**：AVIF 色度的 `?? "444"` 在今天把默认改成 4:2:0 时漏改
  （默认值有三处要同步：字段默认 / 下拉 `IsSelected` / 保存兜底）。新增成对回归：
  每个还在的色度下拉都必须"存在于界面 **且**被管线读取"（AVIF / JPEG LI / JPEG XL / GainMap 四条）。

### 🧭 两处判定同源 + 仓库跟踪修复（2026-09-19）
- **QSV 可用性判定同源**：Auto 选编码器时"有没有 QSV"原先只看是不是 Intel 显卡，而后端真正要求是
  "系统里有 AV1 编码 MFT" ⇒ 在没有 AV1 MFT 的 Intel 机器上会选中一个必然失败回退的后端（白跑一次）。
- **25 个从未进入版本控制的必需文件已 `git add`**：包括 `AvifEncoderCapabilities.cs`、
  `AvifEncoderProbe.cs`、`EncoderCrashSentinel.cs`、`D3DDeviceLost.cs`、`SelfTest.cs`（被 `App.xaml.cs` 调用）、
  `models-manifest.json`、两个 Tools 诊断文件、本轮新建的两个 Core 文件、**`.github/workflows/release.yml`（CI 配置）**、
  `scripts/build-avifenc.py` 与 13 份 `docs/*.md`（含交接文档本身）。
  ⇒ 之前**新克隆无法编译**。未跟踪列表已清零，也确认没有"已跟踪却被忽略规则命中"的反向问题；
  根目录 `nvoff.exe` 与 `.atomcode/memory.md` 两个本机 scratch 改写入 `.gitignore`。
  ⚠ 尚未 commit（需要用户授权），所以"克隆即可编译"要在这次提交之后才真正成立。

### 🌄 截图源精度改由显示器能力决定（2026-09-19，用户对 §4.3 的修正）
- 此前"用不用高精度（Float16 scRGB）源"取决于**用户这次的输出意图**（HDR 开关是否打开、目标色域
  是不是广色域）。后果：在 10-bit / HDR / ACM 显示器上，只要没开 HDR 输出，抓取就直接落到 8-bit BGRA，
  之后的色域矩阵、色调映射、gamma 全只能在 8-bit 数据上做。
- 现在**恒按能力决定**：Windows 给得出 >8-bit 就用高精度源，给不出才退回 8-bit BGRA；
  与输出格式、目标色域、是否输出 HDR **无关**。能力判据 = 显示器处于 PQ/HLG、或 BitsPerColor≥10 且
  峰值亮度>200、或系统开启高级颜色管理（ACM）。
- **给不出的显示器不会被拖慢**：该能力判定同时写入进程内的能力缓存，所以不会每次截图都去等一次
  Float16 首帧超时（原先的顾虑正是它，才把开关绑在输出意图上）。缓存只由能力判定写入 ——
  瞬时启动失败仍会下次重试（保留 2026-09-15 那条修复的语义）。
- 🔬 本机实机确认：`\\.\DISPLAY5 2560x1440 IsHdr=True ColorSpace=RgbFullG2084NoneP2020`
  ⇒ 判据给出"Float16 (>8-bit)"，这条改动在本机是**真的生效**、不是空跑。
- ⚠ **需要你目视比对**：在 Windows HDR 模式开着、而软件里 HDR 输出关着的组合下，
  现在截图会走"浮点源 + 应用内色调映射"，成品观感可能与之前（直接用 DWM  tone-mapped 的 8-bit 画面）
  不同。实机套件每次会打印这块显示器的判据，方便复核。
- 🧵 仍待落地：**输出位深要能真的选 8** —— 目前 Float16 源分支会把位深强制提到 16，
  所以 PNG/TIFF/JXL 的位深下拉在这条路上无效；这处不能一行删除（会连带丢掉广色域矩阵变换），
  施工单与风险写在交接 §28。

### 🔬 高精度色彩管线四路审查（2026-09-19，交接 §29）
- 对"捕获 → 色彩数学 → 编码"整条链做了四路只读审查（捕获层 / 色彩数学链 / 各产品路径实际精度 /
  可验证性），并在主循环逐条核实。
- **本轮即修**：截图捕获体原先**同步**跑在 UI 线程上，而里面有最长 3 秒的"等首帧"硬阻塞 ——
  单屏与多屏两个入口现在都搬到线程池（帧池本就是 free-threaded，不需要创建线程的消息泵）。
  这个阻塞在改动前就存在，但"源精度改由显示器能力决定"之后会多等一次，风险被放大，故先修。
- 审查的主要产出不是代码而是**账**：高精度源目前只在少数路径兑现（选区复制、标注系、OCR、录制
  即便手上是 float 也只取 8-bit）；广色域矩阵只存在于高位深分支，所以"让输出能选 8-bit"必须先做
  解耦；而**精度这件事在门禁里没有任何端到端判据**（实机套件的帧池硬编码 8-bit、`verify.ps1` 不跑
  任何外部解码器判据、判据脚本还在被 gitignore 的目录里）⇒ 已列成 P0/P1/P2 施工顺序，含 4 个
  可自动化的代理指标与 4 处"名字承诺了但断言没做"的假绿灯。详见交接 §29。

验证：构建 0 警告 0 错误；`scripts/verify.ps1` 7 套件 **647** 断言 + `--wgc-avif-tests` 28
= **675 断言 / 0 失败**（真实截图的观感与线程改动仍需人工确认一次）。

### 🧪 探针判据自检 + 整机蓝屏归因（2026-09-19，交接 §30）
- **这一轮修的不是管线，是"量管线的那把尺子"**。高精度源探针（`--hdr-probe`）首次真机运行只给 SKIP，
  读下去发现判据自身两个死缺陷，且都不是"真机跑一次"能发现的：
  · 中性像素计数从未 `++` ⇒ `NeutralN` 恒 0 ⇒ 样本量闸门恒成立 ⇒ 探针在任何机器上最多只能 SKIP。
    现场证据就在上一版报告里：`中性像素 0 个 → 去重量级 942` —— 两者取自同一个 `if` 块，
    级数能攒到 942 就证明有几十万个像素被判为中性，缺的只是那行 `++`。
  · 整格容差 `0.6 LSB8 > 0.5`：测的是 `|q − round(q)|`，而这个量**恒 ≤ 0.5** ⇒ 判据"落在 1/256 整格上"
    恒为 100%，真机那句"raw 域 100.00%"看着像证据，其实是恒真式在自证（还会反过来支持结论）。
    现取 0.25 = FP16 噪声上界 0.124 的两倍。
- **合成帧自检**（`--hdr-probe` 现在先跑它，无环境依赖）：256 级台阶帧必须判 FAIL、1024 级必须判 PASS，
  且与真机走**同一条**判定分支。实测 `整格 100.00% / 256 级` → FAIL ✅、`50.98% / 1024 级` → PASS ✅
  ⇒ 判据会红也会绿，后面的真机读数才有资格被相信。
- 修好后第一次可信的真机读数：`\\.\DISPLAY5`（HDR / 10-bit / P2020）Float16 帧 3840×2160，
  中性像素 40169 个 / **去重 832 级**（破 256），`>1.0` 占 **19.30%**，整格占比 **49.63%**
  ⇒ §28「源精度由显示器能力决定」端到端兑现：下游拿到的确是高精度源，不是升位深的 8-bit 台阶。
- 新增 C# 行为断言：逐屏高精度能力**真值表 7 条**（判据本体已搬进 Core 纯函数，单屏与多屏同源），
  以及 §4.3 解耦的 4 条行为证据 —— 矩阵在 8-bit 容器上真跑（有彩通道 22/33 差 >2 LSB）、
  中性像素不受 tag 影响（最大差 **0** LSB ⇒ 没夹白带点缩放）、位深只改量化（8 vs 16-bit 最大差 1 LSB）、
  16-bit 低字节非零 45/48（⇒ 不是把 8-bit 左移 8 位的假高位深）。
- `scripts/verify.ps1` 两处门禁缺陷：**①** `$ErrorActionPreference=Stop` 下，子进程**一行正常的 stderr**
  （编码器崩溃隔离警告）会被包成终止性错误 ⇒ 门禁在编码套件处**静默截断**，后面 4 套连"跑没跑过"都不留
  记录；**②** 截断时不等子进程退出 ⇒ 留下一个孤儿 Test 进程继续在 NVENC 上跑。现在统一走
  "调用处降 EAP + 等完"的 `Invoke-CapturedNative`。
- **18:38 整机蓝屏归因**：`0x3B / 0xc0000005`，WER 桶 `AV_nvlddmkm!_mapInsertFixup` ⇒ 故障点在 NVIDIA
  内核驱动的显存映射记账里。用户态 C# 写不进内核内存，与本仓库代码逻辑无关；同一天 06:47、06:55 还各崩一次，
  桶为 `AV_eaanticheat!DriverEntry`（EA 反作弊驱动），近 14 天只有今天这 3 次。崩溃前 4 分钟 NVENC 开始返回
  `InitEncoder: 0x8`（同一套件当天早前仍全绿），同时段 `3FCompare.exe` 在 `dxgi.dll` 里反复 AV
  ⇒ **触发者不能唯一确定**，但这台机器今天的 GPU 相关判据读数要打折看待。

- **取证资产入库**（这条影响"换台机器还能不能复验"）：`src/` 注释里有 21 处把**生产结论的出处**指向被
  gitignore 的 `.workbuddy-ai/`，其中包括 `NvEncoderNative.cs` 那批硬编码结构体偏移所依据的 `nvoff.c`
  （MSVC `sizeof()/offsetof()` 实测）。已全部重指到版本控制内路径，并把 `scripts/probes/nvoff.c`
  （附复跑方法）与 `scripts/precision/avif_bench.py` 正式入库；两处**功能性**耦合（`verify.ps1` 的样张落点、
  `precision_env` 的兜底候选）同批改 ⇒ 探针产物统一落到 `artifacts/precision/probe/<kind>`。
- `GetSdrWhiteLevel` 的 `DISPLAYCONFIG_PATH_INFO` 步长由写死的 `100` 改为按头文件算出的 `72`：缓冲区原本是多分的
  （不存在越界写），但从第 2 条路径起会偏出上一条记录 ⇒ 那块屏的 SDR 白点查询失败，最坏情况查到**另一块屏**的值。
  本机只有一条路径 ⇒ 一直没暴露；改前改后 `--fmt-check` 都是 `120 nits ✅`（单路径行为不变、无回归面）。

- **新增端到端精度判据 `ramp_levels.py`（§29 指标②落地）**：`TrueToneCap.Tools.exe --ramp-sweep` 把 64 个
  紧邻台阶（相邻带在 10-bit 刻度上只差 1，刻意放中间调）送进 `EncoderFactory` **全链**，判据用 ffmpeg 解到
  rgb48le 后逐带取中位数**数容器实际交付了几级**。实测 PNG 12(d8)→44(d10)→64(d16)、TIFF 12→64、
  JXL 12→64、AVIF 软件 11→28、AVIF/NVENC 11→34。
  ⚠ 首版按"请求 ≥10-bit ⇒ ≥48 级"下硬判时 `r_png_d10` 只有 44 —— 查下来是**夹具的错**：生产链对线性值做
  paper-white 归一（默认 200nits ⇒ ×0.4），台阶到输出只剩 0.61 码，并掉三成正是 10-bit 量化的定义。
  ⇒ 换成增益无关的关系式（16-bit ⇒ ≥48；同家族高位深 ≥ 8-bit 的 2×；无损容器 8-bit ⇒ ≤24，有损不设上限），
  并**实测**验证它会红：把 d16 样张换成 d8 的产物 ⇒ 两条同时红、退出码 1。
- **跟踪修正（重要）**：`scripts/precision/` 整个外部判据层 **11 个文件此前从未进入版本控制** —— 门禁
  `[1.7/2]` 依赖的东西在 fresh clone 里根本不存在（好在该步是显式红、不是静默跳过）。已全部 `git add`
  （未提交），全仓未跟踪计数 11 → 0。

- **§29 指标④落地：`cicp_consistency.py` + `TrueToneCap.Tools.exe --cicp-sweep`** —— 同一张 64×64 图
  （中性灰阶 + 高饱和色块，值域钉在 [0,1]）跨 **7 个格式/后端 × 4 个色域 tag** 各编一次，判据用外部工具
  读回每张容器的 cICP / colr / ICC，断言"同一内容不许被声明成不同色域"。它与 `param_sweep_check` 不重复：
  后者判"每行等不等于被告知的值"，本条判"**行与行是否自相矛盾**"。
  首跑报出 2 项，核实后**一项是我的伪报、一项是真缺陷**：
  · ~~TIFF 装广色域像素却零声明~~ ⇒ **撤回**：像素侧确实是广色域（与 PNG 最大差 0.5/8bit LSB，
    且与自身 sRGB 版有 3776 个通道差 >4/255），但"没声明"错了 —— 我的判据用 `b"ICC"` 子串找 ICC，
    而 TIFF 的标签名叫 `InterColorProfile`、ffprobe 的 TIFF 解码器也不报 ICC ⇒ 漏检。
    已用 Pillow 独立复核（`icc_profile` 有值、tag 34675 与 `acsp` 都在）⇒ 判据改为一律找 `acsp` 魔数。
  · **JXL 的 AdobeRGB 是真缺陷，已修**：把产物 PNG 里那份 3144 字节 AdobeRGB1998 ICC 直接喂 cjxl，
    产物被读成 `primaries=bt2020` 且 profile 消失 —— libjxl 把无法精确匹配的 ICC 归到最近枚举并丢弃，
    而 cjxl 没有"原样保留"开关 ⇒ 处置是 **JXL + AdobeRGB 退回 sRGB 像素 + sRGB 声明**（不再产出
    "声明 BT.2020 的 Adobe 文件"），代价经降级留痕可见。实测该行由 `prim=9 无 ICC` 变为 `prim=1 无 ICC`。
    ⚠ **2026-09-26 这条的机制解释被实测推翻**（原句按发布记录保留、不改写）：`primaries=bt2020` 是
    **ffprobe 在读取方**把 Custom 原色归到最近 H.273 码点，同一文件用 `jxlinfo` 读回是
    `Custom(0.639998,0.329997)` —— Adobe 原色逐字还在；"没有 `acsp` ⇒ profile 被丢"也不是有效判据
    （JXL 的 ICC 走 delta 压缩）。真正存在的限制是版本能力：`color_space=Adobe98` 在随包 v0.11.2 失败、
    v0.12 才接受。⇒ 上面那个"退回 sRGB"的处置**理由不再成立、行为暂未撤**，取证与待办见交接 §56.4。
  · 仍开放一项：`cc_avifnv_adobe` 的 `nclx(1)` 与 ICC 并存 —— 我在 `IvfWriter.WriteAvif` 加的守卫
    经探针证实**对该通路无效**（那组容器不是这个函数写的），根因调查中。
  两条判据自身的伪报也已修（`EXT_OF_FMT` 漏登记 avif；把 H.273 码 2 "unspecified" 当撒谎）。
- **假绿灯审计（三路并行）+ 已全部修完并集成**：4 个测试文件里查出约 35 处"名字承诺了、断言没做"，最重的三类是
  ①"PQ 曲线精度"三条调的是**测试文件自带的第二份 PQ 实现**（生产 `FormatHelper.HdrToPq16` 从未执行）
  ②`Test_ConvertFloatToHalf_*` 全程只调 BCL，手写 SIMD 的 `PixelOps.ConvertFloatToHalfRow` 从未被跑到
  ③结构上不可能为假的断言（`Assert(..., true)` 字面量、`byte` 上判 0–255、`ushort` 上判 ≤65535、
  被自身析取式吸收的曝光比较、"不崩溃"却把异常记成不参与退出码的警告），以及**Skip 理由与代码不符**
  （CPU -only 的 `BakeIccToTarget` 被以"无 D3D 设备"跳过 = 把产品故障伪装成环境限制）。
  主循环抽样复核 5/5 属实，另抓到 reviewer 报错 1 条（引用了不存在的文档行）⇒ 未照抄。

验证：构建 0 警告 0 错误。**假绿灯三路修复后的新基线** —— `--unit` **362** / `--color` **134** /
`--encoding` **14** / `--service` **4** / `--ocr` **10** / `--format` **35** / `--usability` **208**
= **767 断言 / 0 失败**，再加 `--wgc-avif-tests` **33** = **800 断言 / 0 失败** ✅
（审计前是 691 + 28 = 719 ⇒ 净增 81 条，全部来自"把只判非空/恒真的断言换成真判语义"）。
`--hdr-probe` PASS；外部判据**五条**：chroma_knob / param_sweep / gamut_depth_consistency / ramp_levels
四条真通过（前三条走门禁跑，ramp_levels 单跑 + 变异自验），第 5 条 `cicp_consistency` 按【已知未修】
只报告不阻断（它查到的两类缺陷见上）。`[1.6/2]` 解除阻塞后要把五条一起复跑一次确认接线。
- 附带确认：`--encoding-tests` 在崩溃前那次报 `InitEncoder: 0x8`，重启后单独跑同一套**全绿**
  （`CreateInstance OK` + `编码会话 OK`）⇒ 那个状态码是驱动崩溃前的先兆，不是代码问题；今后再见到它，
  优先怀疑驱动而不是管线。
- ⚠ 唯一没复跑的是 `verify.ps1` 的 `[1.6/2]` 发布门禁回归：被「`TrueToneCap.exe` 正在运行」守卫挡住
  （PID 24788 = `publish/TrueToneCap-v0.3.4-beta/`，18:41 随重启自启）⇒ **守卫按预期工作，不是回归**，
  关闭该实例后复跑即可。本轮 7 套的结果是绕开该步骤逐个直接跑出来的。

### 🎨 七格式色彩管线全枚举 → 修 7 类"声明与像素矛盾 / 改了设置没反应"（2026-09-19 夜，交接 §31）

- **AVIF 对 AdobeRGB 谎报色域**（SDR 软件路）：`--cicp 1/13/1` 与 ICC 并下 ⇒ 采信码点的查看器按 BT.709 解释 Adobe 像素。
  现与 PNG cICP / NVENC colr 同问 `BuildSdrCicp`——**无码点就不下发码点**。
  产物级验证：`cc_avifnv_adobe` 由 `1/13/1`+ICC 变为"无码点 + ICC"，跨格式声明一致性判据 28 文件 / 4 组 **0 不符**，
  该判据同时从"只报告不阻断"转为**阻断项**。
- **HDR 分支全线缺 Adobe 守卫**（PNG cICP / NVENC colr / cjxl color_space 三处同根）：SDR 的守卫挂在
  `BuildSdrCicp` 上，HDR 声明路径根本不问它，且 HDR 一律不嵌 ICC ⇒ "HDR + AdobeRGB"必产出矛盾文件。
  现统一在**色域矩阵之前**归一到 BT.2020（像素与声明同取一处）并给 Toast。TIFF 例外：它靠 ICC 声明，
  "Adobe 原色 + PQ TRC"的 profile 合法存在，归一反而把用户要的色域裁窄。
- **ICC 转换失败仍嵌目标 ICC**：`BakeIccToTarget` 在矩阵不可解析时"像素直通 + 目标 ICC"，而调用方以
  返回值非空判成功 ⇒ "没转"与"转好了"不可分辨；`PreparePixelsWithIcc` 的"无显示器 ICC"分支同形。
  现在没转换就不返回目标 ICC，并由调用方把 tag 与 ICC **一起**退回像素真实所在色域 + 留痕。
- **HDR 关着时 AVIF 位深被钉死 10-bit**：源精度事实（WGC 给没给出 Float16 帧）被当编码场景传进设置构建
  ⇒ HDR 关掉的截图也走 HDR 协商，用户下拉的 8 / 12-bit 永远不可达，还会为非 HDR 截图弹"硬件不支持 HDR"提示。
  现按"这次真的走 HDR 直通吗"传场景，三处 Float16 入口同步。
- **sRGB 目标下的 TIFF 零声明**：TIFF 没有 cICP 通道，ICC 是唯一说得清色域的标签 ⇒ 旧写法在 sRGB 下什么都不写。
  现在 8-bit 源与 ≥10-bit 源两条通路都显式嵌标准 sRGB profile。
- **ICC 烘焙的 D50/D65 混乘**（本轮唯一会改颜色的修复）：源矩阵读自 profile 的 **D50 PCS** 标签，
  目标逆矩阵是硬编码的 **D65 引用**值，旧实现直接相乘 ⇒ 复合矩阵里藏了一次未做的白点适配，
  连"同一张 sRGB profile 自己转自己"都判不出单位阵而真去乘一遍。现先把源矩阵换算回 D65 域。
  适配矩阵由新入库的 `scripts/probes/d50_adaptation_derive.py` 从微软系统 sRGB profile 实测反推，
  三条自检"白点必须映到白点"——该脚本**第一次跑就因转置方向报错**，改对后才允许写进产品代码。
- **子进程超时处置**：全仓 6 处编码通路丢弃 `proc.WaitForExit(ms)` 的返回值 ⇒ 超时后读 `ExitCode` 抛
  "进程状态非法"掩盖根因，还把吃满 CPU 的子进程留在后台。新增 `NativeProcess.WaitForExitOrKill` 统一处置。
- **可追溯性补 4 处静默降级**：PNG 的"HDR+8-bit→静默色调映射"、AVIF 的 QSV→avifenc 换执行者、
  AVIF HDR 硬件回退、GainMap 的两条封装失败旁路（找不到 SOS / ISO 段插不进），现走 Toast + 日志。
- **JXL 中转位深自洽**：中转 16-bit PNG 的 sBIT 报请求位深、像素却按 16-bit 铺满 ⇒ cjxl 读不读 sBIT
  决定低位命运。现与 AVIF 同源：按请求位深量化一次再左对齐。

新增 15 条断言（其中 4 条自带"必须敢输出 FAIL"的合成自检）；7 套测试共 **810** 条、0 失败，
5 个外部判据脚本全部 `rc=0`。

⚠ 顺手抓出门禁脚本自己的第 10 个缺陷（交接 §31.6）：`scripts/verify.ps1` 里 `Invoke-CapturedNative`
把参数命名为 `$Args` —— 那是 PowerShell 自动变量，绑定不生效 ⇒ splat 出**空参数**，七个套件其实
一个测试都没执行。同文件的 python 侧 helper 因为叫 `-Params` 而幸免。抓住它的是上一批刚修的
"未识别参数 ⇒ 退出码 1、不许当通过"；若沿用旧行为，这一步会被读成"七套全绿"。已改名 `$ArgList`
并加"零参数直接 throw"的硬约束。

### 🎨 可选色彩空间收两项：sRGB 与 BT.2020（2026-09-20，交接 §32）

- **界面**：色彩空间下拉从 6 项收到 **跟随显示器 / sRGB / BT.2020**。Display P3、DCI-P3、Adobe RGB
  不再可选，但 Core 保留它们的矩阵、ICC 模板与 H.273 映射 —— 检测到的显示器原生色域、烘焙失败时的
  降级声明、跨格式判据样张都要靠它们如实描述像素。
- **P3 / Adobe 面板上的 SDR 截图现在一律按 BT.2020 处理**，而且是"**搬进** BT.2020"而不是
  "把 P3 像素**标成** BT.2020"：收口发生在色域矩阵之前，浮点路与 8-bit 烘焙路都会真做转换 ⇒
  声明与像素同源，BT.2020 更宽所以不裁色域。
- **旧配置按标签迁移**：`ColorSpaceIndex` 存的是下拉索引，项数变了若只按新项数 clamp，
  旧索引 2/3/4 会被读成 sRGB/BT2020/sRGB —— 用户没动任何设置就换了输出色域，保存时还会把错的格子
  固化回设置。现在走"旧表取标签 → 收口 → 新表找索引"，幂等且有日志。
- GainMap 的"锁定 BT.2020"改为**按 Tag 查找**（原来写死 `SelectedIndex = 5`，新项序里那一格不存在）。
- 📖 **回答一个常见问题：AVIF 里的 sRGB 到底是 sRGB 还是 BT.709？** 实测容器 `colr(nclx)`：
  sRGB 目标写 `(primaries 1, transfer 13, matrix 1, full)`。H.273 没有独立的"sRGB 原色"码点 ——
  sRGB 与 BT.709 共用同一组色度坐标与 D65 白点，**"是 sRGB"这件事由 transfer=13 承载**；
  `matrix=1` 只是 YUV↔RGB 系数（AV1 是 YUV 码流，PNG 存 RGB 采样所以写 identity=0），不表达色域。
  所以 ffprobe 印 `color_primaries=bt709` 是命名遗留，图片仍然是 sRGB。BT.2020 目标则写 `(9,13,1,full)`。
- ⚠ 顺带纠正一条被记成"硬件限制"的事实：`NV_ENC_CONFIG_AV1` 其实有
  `colorPrimaries` / `transferCharacteristics` / `matrixCoefficients` / **`colorRange`** 四个字段，
  我们一个都没填 ⇒ 硬件 AVIF"码流里不写色彩描述、range 与软件路不一致（tv vs pc）"是**漏配**，
  不是驱动不给做（真限制只有一条：AV1 输入不支持 4:2:4/4:4:4）。统一 range 需要连带实测硬件实际用的
  YUV 矩阵，作为下一批（交接 §32.3 / 任务批次 8）。同理 `outputMaxCll` / `outputMasteringDisplay`
  存在 ⇒ 悬置的"硬件 HDR 不写 clli/mdcv"有官方入口。

新增 1 个用例（29 条断言，含"收口幂等"与"迁移按标签"两面钉子）。

### 💾 连拍 4K HDR 后"内存 3GB 不回落"——查明不是泄漏并已修（2026-09-20，交接 §32.5）

真机连拍 5 张 4K HDR：私有字节 1479 → 3303 MB，静置 40s 不降。为判断"真被引用"还是"GC 没归还"，
在 HDR 编码两端加了 `内存(起)/内存(终)` 日志（托管堆 + 私有 + 工作集）。数据显示一次回收就掉 1.5 GB
（2232 MB → 682 MB）⇒ **是工作站 GC 不主动 decommit，不是泄漏**：截图工具的节奏正是
"每张造 ~280 MB 瞬时分配 → 空闲很久"，永远等不到那次压力回收。

现在编码队列清空 8 秒后，确认"无捕获在途、无编码在跑"才做一次 Aggressive 回收并打日志：
**同样的 5 连拍，静置后从 3222 MB 降到 1034 MB（归还 1.9 GB）**。
取证脚本入库为 `scripts/probes/mem_growth_probe.ps1`。

顺带清掉两处：
- 删除 `ShouldUseFloat16ForWideGamut()` —— §4.3 之后源精度不再由它决定，它只剩两行日志在消费，
  而实测本机打印 `Float16广色域=False` 却照样拿到 Float16 帧，这种日志比没有更坏。
  两行日志改为"源精度=按显示器能力判定"，真实精度由紧随的"HDR/SDR 帧捕获成功"报。
- 色域索引迁移的结果现在写回设置（此前文件里长期留着旧索引，每次启动重算）。

每张 ~280 MB 的瞬时分配链（float[] → 矩阵输出 → 16-bit 中转 → PNG 字节各一份）**没有**改成缓冲区复用
——那是性能优化不是缺陷，且会牵动每个编码器的中转布局，已记入交接备查。

### 🎯 硬件 AVIF 的 YUV 矩阵声明错了（实测出是 BT.601，此前一直报 BT.709）（2026-09-20，交接 §32.6）

新增纯色带探针 `--nvenc-color-probe` + `scripts/probes/nvenc_matrix_probe.py`：让硬件与软件各编一张
8 条已知颜色的图，解回 YUV 后对 (601/709/2020) × (limited/full) 六个候选做拟合。
脚本带两道自检——**量尺自检**（同一套方法必须先判出软件路我们自己声明的 709/full，误差 4.0/1023）
与**判定余量**（最优与次优差距 <30/1023 就直接报"判不出来"并退出非 0）。

实测：**NVENC 自己做 RGB→YUV 用的是 BT.601 系数 + limited 摆幅**（误差 20.0，次优候选差 57.9），
而容器 nclx 一直写着 `matrix=1 (BT.709)` ⇒ 采信该字段的解码器会拿逆 709 去解 601 编码的像素，
红/绿方向偏。MFT/QSV 路同理（那条路的转换是我们自己用 601 整数系数写的）。
现硬件 SDR 路声明改为 `matrix=5 (BT.601-7/SMPTE 170M)`，复跑探针：容器声明与实测一致 ✅。

⚠ 这条会改变硬件 AVIF 的观感（解码端换了一张逆矩阵）——理论上变正确，请目视对比。
**没顺手改的**：① 码流序列头的色彩字段仍不填（AV1 的 `matrix_coefficients` 枚举与 H.273 不是同一套
编号，拿容器域的常量写进码流比不填更坏，需先实测映射表）；② HDR 路的矩阵仍写 9（探针只覆盖了 SDR，
未测不猜）；③ 两条后端的采样范围仍不同（硬件只能给 limited，软件是 full，统一要牺牲图片观感，留待拍板）。

> **上面这三条"没顺手改的"与"limited 摆幅"已被下一节整体推翻并落地**（同一天，交接 §32.7）。
> 这一节的价值只剩"如何发现声明错了"这套方法；结论请以下一节为准。

### 🎯 硬件 AVIF 不再降级：驱动按下发的矩阵转换，SDR/广色域/HDR 三档全部实测对上（2026-09-20，交接 §32.7）


用户口径：**要准确和高精度，不要各种降级**。上一节的修法（把容器改成 BT.601）是让声明去追硬件，
而"硬件只会 601"这个前提本身没被验证过——它只是**从不下发 `matrixCoefficients`** 时的默认值。
于是给探针加了一个码点扫描模式（`--nvenc-color-probe sweep`）：逐个把 H.273 码点写进
`NV_ENC_CONFIG_AV1.matrixCoefficients`，再解回 YUV 反推驱动实际用了哪张矩阵。

扫描结果：**驱动照做**（1→BT.709 误差 ≤1.2、9→BT.2020 NCL 误差 ≤1.0、2/5/6→BT.601、
0→驱动直接拒绝 `invalid color configuration`），SDR 与 HDR(PQ) 两条通路行为一致。
所以现在的做法是：把要用的矩阵**下发给驱动**，容器声明同一组值——硬件与软件两路给出同一份像素，
没有任何一路被降级去迁就另一路。

顺带修掉一条同根的口径分裂：`AvifMatrixCoefficients` 原先按"是否 HDR"选矩阵（`hdrOutput ? 9 : 1`），
于是广色域 SDR 拿 709 权重去装 BT.2020 原色的像素，而 HDR 用 2020 权重；同一条规则被拆成两套，
还留着产出 "primaries=1 + matrix=9" 这种自相矛盾声明的入口。现在 matrix 只看三原色，
软件路（avifenc `--cicp`）、硬件路（驱动配置）、容器（nclx）三处问同一个函数、取同一个值。

过程中判据自己错过两次，都补了闸：
- **判定余量**从"最优比次优好 ≥30/1023"改为"容差 6/1023 内恰好一个候选"——三张矩阵两两距离本就不等
  （601↔709 差 24.9，709↔2020 只差 9.8），固定余量会把一个精确到 1.2/1023 的结论判成"判不出"。
- **样张必须真出自硬件**：第一轮扫描里六份"硬件样张"字节数完全相同——第一个码点让 `InitEncoder`
  失败后被记进失败缓存，其余五份全部静默回退 avifenc，而回退产物照样是合法 AVIF。现在每份都查
  降级原因，判为回退即删除样张并标注作废；HDR 那份再加一道 pix_fmt 闸（退到 8-bit 会被升位、拟合全盘作废）。

改后实测收口（三档 × 硬件/软件两路，全部一致）：SDR sRGB `1/13/1`、广色域 SDR `9/13/9`、
HDR `9/16/9`，摆幅一律 full（黑带 Y=0.0/1023 实测）。
另外填了这三个字段之后 NVENC **自己**就把 color_config 写进了 AV1 序列头：ffprobe 从
`color_primaries=? transfer=?` 变成 `bt2020 / smpte2084`，所以"手工往码流插 CDI"那一步不再需要。
`scripts/verify.ps1` 新增一条外部判据常驻把关（硬件实际转换与容器声明不一致即阻断，
本机无硬件样张时记 SKIP 而不是冒充通过）。全量：884 断言 / 8 套件 + 6 判据，退出码 0。

⚠ 这条同样会改变硬件 AVIF 的观感（换了一张逆矩阵，且广色域 SDR 的 YUV 权重变了）——请目视对比。
仍欠一项、本机量不出来所以没动：**MFT/QSV 那一路的摆幅**（矩阵可断言是 601，但它的 NV12 是被 MFT
原样透传还是再解释一次，只有 Intel 核显上能实测；探针已备好 `qsv` 一档，容器目前沿用 full_range=1
并在代码里就地标注为未验证）。

### 🎨 色彩声明全链路：八格式逐条对齐，ICC 改按结构判定（2026-09-20，交接 §31 / §32）

**能力变化（用户可见）**

- **广色域图在主流查看器里终于"是广色域"**：此前我们 JPEG 里的 ICC 写成 APP1 且缺分块字段，
  libjpeg-turbo / Pillow / ImageMagick / Chromium 四家一律读不到，广色域截图被当成 sRGB 解释
  （表现为饱和度塌陷）。现按 JPEG/ICC 规范写 APP2 + 分块字段，并支持超长 profile 分片。
- **同一次截图，软件编码与硬件编码给出同一份色彩声明**：ICC 字节一致、色域码点一致、
  HDR 的 `clli`（内容亮度限幅）两路都写。此前两条路容器形状不同（一个 `prof+nclx`、一个只有 `nclx`）。
- **硬件 AVIF 不再错报色域**：驱动被证实会按我们下发的矩阵与摆幅转换，容器声明与码流实际一致
  （此前容器一直报 BT.709，而码流走的是 BT.601；`av1C` 也曾谎报 4:4:4）。
- **ICC 能否嵌入改问结构**：长度字段自洽 + 色彩空间是 `RGB ` + 三原色与白点齐全 + 传递函数在。
  旧的"字节数阈值"两头都错——会误杀合法的紧凑 profile，也拦不住与 RGB 截图无关的 CMYK 打印件。
- **Gain Map 量程单源**：像素、XMP 与 ISO 元数据用同一个数值，且始终满足"容量上限严格大于下限"
  （此前 `headroom ≤ 1` 时两份元数据各自算成 0，消费端权重公式可能除零）。
- **可选目标色域收窄**：界面只留「跟随显示器 / sRGB / BT.2020」，其余从下拉移除（检测与描述能力保留）；
  旧配置按标签迁移，不会因为选项变少而被静默改成别的色域。

**稳定性与规范**

- **编码器子进程超时真正可达**：两条输出管道并行排空、超时即杀整棵进程树；
  此前只要编码器不退出，5 秒 / 60 秒的超时档压根执行不到（表现为整条截图链被拖住）。
- **一次偶发崩溃不再永久关闭硬件编码**：崩溃哨兵改为读后即消费（只禁用下一次启动的一个会话，
  一次成功即解除），并把 MFT 一并纳入哨兵覆盖。
- **常驻外部判据 +3**：跨容器 ICC 读回一致性、JPEG ICC 可读性、容器 `av1C` 与 AV1 码流一致性；
  判据一律用外部实现读回，不允许拿仓库自己的第二份解析器自证。
- **取证脚本入库**：门禁判据与探针此前只存在于被忽略的本地目录，新克隆既跑不了也验不了；
  现已全部落在 `scripts/precision/` 与 `scripts/probes/`，被 `verify.ps1` 直接调用。

> 判据的可证伪性（变异实验）、各处阈值的标定过程与实测数字：交接 §32.9 – §32.14。

---

### ⚖️ 许可与版权合规 + 🌐 翻译告知 + 📋 日志出口（2026-09-20 深夜 ~ 09-21 上午，交接 §33 / §34 / §35）

**用户可感知**

- **导出文件不再携带第三方版权文本**：此前 Display P3 / BT.2020 / Adobe RGB 截图会把微软系统
  sRGB 里的 HP 版权声明一起嵌进 PNG / TIFF / AVIF / JPEG；现在这份 profile 由本程序自产，
  版权声明是自己的 Apache-2.0，并且这份 profile 的字节**不再取决于本机系统里装了什么**
  （此前换一台机器、或系统那份文件缺失走回退，嵌进产物的就不是同一份）。
- **翻译默认不再把 OCR 文字发给 Google 的非官方接口**：需要的人在 设置 → 翻译 里显式勾选，
  勾选框旁写明两件事 —— 原文会发给第三方、且该接口不符合其服务条款。
- 设置 → 翻译 新增有道智云开放平台 AppID / 密钥输入框（此前只能手改 `settings.json`，
  界面上根本没有入口）。输入框下方写清凭据从哪来、怎么计费：官方文档写明新用户注册赠 50 元体验金，
  常见语种 48 元/百万字符 ≈ 104 万字符，先扣体验金再扣余额 —— **到账时效与是否需要实名未实测**，故只转述不承诺。
- **翻译不再"静默失败"**：点翻译时若一个后端都没配，直接当面列出三条可走的路（有道官方 / 自建 LLM /
  显式勾选 Google 非官方端点），而不是发一次请求再报"失败 N 行"。真跑了之后按行数分开说：
  `M/N 行失败（已保留原文）` 与 `N/N 行都没出译文` 是两句话，后者会带上原因示例
  （如 `有道 errorCode 108：AppID 不存在或输错`），不再靠关键字猜测改写。
- **非官方端点这条路上多了必应**：只勾选那一个开关、不填任何密钥，现在能翻整句了
  （2026-09-21 国内实测：必应网页版接口英→中、中→成都出译文，1.1–2.4 秒/句）。它排在 Google 前面，
  因为同日实测 Google 那两个端点在境内连接超时。**它仍然默认关闭** —— 这些是各家网页前端的自用接口、
  不是公开 API，且必应的凭据约 1 小时过期、对方页面一改版就会静默断掉，不具备当默认路径的资格。
- 设置里那条勾选改名为「允许回退到非官方翻译端点（必应网页版 / Google）」
  （原名只提 Google，接入必应后就成了假陈述）；已保存的老配置照常读取，不用重设。
- 设置 → 系统 新增「第三方许可」窗口，可直接读完随包的许可清单。
- 设置 → 行为 新增「日志详细度」（简 / 诊断 / 全）。档位模型本来就有，但**没有任何入口**，
  于是 `Debug` 级永远进不了日志文件 —— 与被删掉的那个从来没人能赋值的 `FileDebugEnabled`
  是同一件事，这次是真接上了。
- **日志能按"一次截图"过滤了**：按下热键到落盘/翻译完成之间的所有行，现在带同一个 `#opId` 前缀
  （以前缀形式出现，不影响日志查看器的行格式）。跨窗口的动作也接得上——选区确认后另起的 UI 回调、
  OCR 预览窗口里的复制/翻译/保存都归到同一拍。动因是连拍报障时"这 12 行属于哪一张"根本分不开。
  ⚠ 观感需人工确认一次：跑真机截两张图，看 `log/` 里两拍的 ID 是否不同、且各自行数齐全。
- 日志加了隐私地板：任何档位落盘前都会刮掉 URL 参数值与凭据赋值（`q=` / `text=` / `key=` /
  `token=` 等）以及成块的高熵 base64。动因很直白：`HttpRequestException` 的文本可能带请求 URI，
  而翻译请求的 URI 里挂着 `q=<OCR 原文>`。**不刮** 64 位小写十六进制 —— 发布校验与工具链哈希
  要靠它，刮掉等于自毁诊断能力。
- 包内 `SHA256SUMS.txt` 改为标准格式，`sha256sum -c SHA256SUMS.txt` 可整包校验
  （此前 BOM + CRLF + 反斜杠三样叠加，只剩 `certutil` 逐文件手算可用）。

**工程规范**

- `licenses/` 42 份第三方许可全文入库（初收 43，末条删掉一份死引用后收敛为 42），
  `THIRD-PARTY-NOTICES.md` 由 `licenses/` + 清单**生成**
  （不手写，避免与全文漂移）并随包分发；出包冒烟现在会因"许可文本缺失"直接变红，
  包里也不再残留第三方私有 `.pdb`（少 11.76 MB，自家 PDB 保留以便报障时有行号）。
- 许可清单三处（原生工具 manifest / `INDEX.json` / 磁盘全文）收成一条闭合判据；
  `Directory.Build.props` 补 `Copyright` / `Authors` / `PackageLicenseExpression` / `RepositoryUrl`；
  152 个源码文件加 SPDX 标识头。
- 门禁外部判据 9 → 12 条：新增 ICC ProfileID 配方标定、lcms2 读回自产 ICC（带"字节序改回小端
  必须失效"的反证）、许可清单新鲜度检查。全门禁 **8 套件 1125 断言 / 0 失败**（外部判据仍是 12 条，
  免密端点的三条探针刻意**不**接进门禁）。
- 未闭环项**公开挂账**（清单正文与交接 §33.6）：微软 WinRT 投影的许可全文未取得、
  Win2D 的 EULA / MIT 冲突待人工裁定、以及最重要的一条 —— AV1 与 JPEG XL 的**专利**授权
  不在任何随包许可之内（Apache-2.0 与所有 BSD 文本都只给版权许可）。
- 删掉一个死引用：`MozJpegSharp.Core` 全仓 0 处使用、却随包再分发 28 KB DLL 并背一份 MIT 义务，
  且包内没有对应原生库（真调用即 `DllNotFound`）。移除后依赖锁文件与许可清单同步收敛（43 → 42 条）。
- 遗留的未提交日志重构核查完毕，并且**这批改动把它真正做完了**：Core/App 里裸 `Debug.WriteLine`
  迁移到统一出口，白名单外计数 **0**（6 个文件的注释里还留着"原先只有 Debug.WriteLine"这样的说法，
  判据实读代码不看注释）。调用点普查（`grep` 实测，非估算）：Core 侧 `CoreLog.*` **143** 处
  （Warn/Error 77、Trace 13），App 侧 `LogService.*` **285** 处（Warn/Error 138），
  且**没有任何一处**吃默认的 `LogCategory.System`。
  配套一条防回潮判据（unit 套件）：白名单外出现裸写即红，白名单内计数还要 ≥5 —— 两个方向都钉，
  否则半年后要么长回来、要么有人把白名单整个删了也没人发现。
- 免密翻译端点的取证脚本入库三条：`scripts/probes/{youdao,baidu,bing}_free_endpoint_probe.py`，
  一律**手动跑、不接进门禁**（判据依赖出网），退出码沿用 0/1/3=通过/回归/SKIP 的约定。
  门禁侧则加了一条**默认跳过**的端到端用例：`TTC_LIVE_TRANSLATE=1 dotnet run --project src\TrueToneCap.Test
  -c Release -- --service-tests` 才会真发一次请求 —— 离线判据能钉住 URL 形状与解析，
  钉不住"上游页面改版后那三个正则还抓不抓得到东西"，而那一环坏掉时没有任何人会发现。
- 门禁加了一道**前置**判据（`verify.ps1` 的 `[1.9/2]`）：测试/样张宿主的输出目录只要早于任何一份源码，
  就当场判红并写"随后所有 RESULT 读数作废"（带 `-SkipBuild` 时降级为醒目警告）。
  它填的是"构建失败即作废 RESULT"那条纪律的反面漏洞 —— **构建成功但产物是旧的**会让八个套件照样全绿，
  症状还更难发现：失败是绿的，成功也是绿的。判据自己首跑假红两次，都是"比错了对象"：
  先比 apphost(`.exe`，只改托管代码时不会重写)，再比宿主自己的 `.dll`（只改被引用项目时也不会重写），
  最后改成比**输出目录里最晚的一次写入**才是那个真问题。红/绿两条路径都实测过。
- ⚠ 本轮实测到**同一棵工作树被两个 Qoder 会话并行编辑**（另一会话在 12:3x–12:4x 写入视觉翻译链路的
  实现与约 46 条断言）。⇒ 结论有两条：任何"上一轮是 N 条"的断言数都不能当基线（数字现跑现取）；
  收口成一个发布提交之前必须先确认两个写入者都收口，否则会把别人的半成品一起折进版本标签。

### 🔤 OCR 路由与翻译（2026-09-21 可用性实测轮）

**用户可感知**

- 选中 ONNX 引擎时"超时之后由 Windows OCR 兜底"这句一直存在的说明**从未兑现过**：主引擎与兜底
  共用同一份取消令牌，主引擎到点后那份令牌已经过期，兜底一进去就被判超时，Windows OCR 一毫秒都没跑到。
  现在每次引擎调用各自持有预算，兜底拿独立的 20 秒。
  ⚠ 证据归属写清楚：机理是读代码 + 常驻断言 `Test_OcrTimeoutStillReachesFallbackEngine` 确证的；
  而"同一张图 ONNX 753 ms、Windows OCR 308 ms 就出 349 字全文"这组数字来自一次性探针，
  探针是拿外部取消当截止用的 —— 同一条短路，但不是内部 45 秒预算那一次测量，两回事不要混引。
- 韩文整页不再被当成"识别成功"。旧判据只认文本为空，而 PP-OCRv6 对韩文输出的是汉字/拉丁
  乱码（实测 "E三\n是"，0/15 字符）⇒ 用户拿到没有报错的错字，且永远轮不到兜底。现在按字符域判：
  请求韩文而结果里没有谚文即走兜底；兜底也不行就连带原因一起说
  （"已尝试 Windows OCR 兜底但未出结果（无法创建 OCR 引擎（语言 ko-KR 未安装语言包））"）。
- 主引擎切到 Windows OCR 时，设置里残留的 ONNX 短码（如 "ch"）此前原样下发，换来一句
  "请安装语言包"（而 zh-Hans-CN 明明装着）⇒ 报障方向指错。现在主选路与兜底共用同一份语言映射。
- 取消一次 OCR 不再把英文原句 `A task was canceled.` 打进状态栏；Windows OCR 那条路此前
  **完全不接收取消**（实测 300 ms 的取消请求仍跑满 307 ms 交回全文），现在会。
- 「视觉翻译」从下拉里的死开关变成真实现：整张截图（PNG，不把 OCR 文本喂给模型）直传给
  支持图片输入的 OpenAI 兼容端点，按图中顺序逐行回填。返回行数与图中不符时报错而不是错位覆盖；
  超过 6 MB 直接拒发并给出路（缩小选区 / 切回文字路）。
- LLM 翻译的请求体键名此前是 `Model` / `Messages` / `Content` 这种 PascalCase 形态，
  而 OpenAI 兼容端点只认小写键 ⇒ 文本路与新接的视觉路同时修好，并加一条判据直接钉序列化后的字节形状
  （这条链路本机没有可用密钥，发不出去的部分只能这样钉）。
- 密钥填错不再被说成"多半是网络、额度或接口变更"：401/403 归为密钥或模型权限、429 归为额度、
  400 提示模型名填错或该端点不吃图片，只有拿不到状态码才叫连不上。
- OCR 设置页那句"截图后自动识别图中文字，支持 50+ 语言"换成实测口径：不自动触发（要点覆盖层的
  识字/翻译），中文与英文最准，日文需字高 ≥24 px，韩文不支持（转系统 OCR，且要系统装了对应语言包）。
- 清掉零调用代码：`ShowOcrResultAsync`、`ShowTranslationResultAsync`、`LlmConfig.SourceLanguage`
  （从来没人赋值），以及整枚 `ToastType` 枚举 —— 全仓 0 处引用，`ToastService` 对外是一组具名方法
  （`ShowCaptureSuccess` / `ShowCaptureFailed` / …），没有按类型分派的入口，也不参与序列化。
- 视觉路的成功计数不靠猜：预览窗口原先统一用"译文 == 原文"数失败行，同语言行与专有名词会被算成失败；
  视觉路要么整批交出 N 行、要么整批抛异常，因此显式记一笔 0 失败。
- 兜底超时的说明改为直接引用异常自带的时间预算，不再在调用点重抄时限
  （变异实验里实测到：预算被改成 0 秒时，用户可见的说明仍写着"超过 5 秒未返回"）。

**工程规范**

- `MultiOcrService.Initialize` 加锁，且 `_initialized` 移到注册完成之后：启动期就有两个并发调用点
  （App 与 MainWindow 各一次 `Task.Run`），此前两个线程可同时通过 `if (_initialized) return;` ⇒ 引擎注册两遍。
- 两个 OCR/翻译入口不再硬编码 `%LOCALAPPDATA%\TrueToneCap\onnx_models`，改与启动期同一个
  `ResolveModelDir()`。硬编码那份在干净安装的机器上指向空目录，一旦抢在启动初始化之前，
  ONNX 两档全部注册失败、只剩中文很弱的 Windows OCR，而界面上看不出自己降级了。
- 路由决策（实际用了哪个引擎 / 是否兜底 / 是否超时）迁到统一日志出口：`Debug.WriteLine` 在 Release
  包里是编译期移除的，实测发布产物中搜不到那条 "OCR] 使用引擎" ⇒ 交付物里这三件事原本零可观测。
- `--unit-tests` 新增 47 条常驻断言（兜底预算 / 字符域判据 / 语言映射 / 取消语义 / 视觉路行映射与
  请求体形状）。其中"超时→兜底"这条做过**变异验证**：把预算改回复用过期令牌的形状后 4 条当场变红
  （`calls=0`），确认它不是构造恒等。两路共用的端点拼接抽成 `ChatCompletionsUrl` 一处。
- 删掉 `LlmProviders`（Core 里那份 11 提供方 / 27 模型的第三份清单）：它全仓唯一的消费者就是
  `--ocr-tests` 的三条断言 ⇒ 门禁一直在绿一个用户碰不到的表，而真正生效的三处
  （`LlmProviderCbo` / `LlmModelCbo` / `OnLlmProviderChanged` 的 tag→endpoint 映射）之间没有任何判据。
  判据搬到这三处活表上（3 条 → 5 条：提供方全覆盖、无僵尸 case、注入的默认模型必须存在于模型列表、
  Tag 唯一、endpoint 一律 https）。实测三处活表本就闭合 —— 漂移只在被删的那份死表上。
- `--ocr-bench` 的尺子补齐并标明引擎：原先只报"按位置命中的字符比例"，多吐的幻觉字符完全不扣分，
  且不论用哪条路跑都不写引擎名（历史上它一直固定量 CPU，而生产默认是 DirectML GPU）。
  现在同时报 召回 / 精度 / 多余字符三项，汇总行标引擎，第三参数 `gpu` 可复量 DirectML 路。
  实测两路在本语料上逐项相同（召回 765/900 = 85.0%、精度 765/829 = 92.3%、**多余字符 0**）⇒
  这 7.7% 的精度缺口全部来自替换与漏字，不是幻觉；召回与旧基线逐位一致，改判据没动过读数。

### 🎯 WGC 按屏捕获的根因修复 + 精度/色域改按"这块屏在用什麼"决定（2026-09-21 ~ 09-22，交接 §39）

外部用户报"无法为显示器 0x10001 创建 WGC 会话"，追下去是两个叠在一起的 GUID 误用：
`IGraphicsCaptureItemInterop` 被填成了 `GraphicsCaptureItem` 的**类 IID**；而 `CreateForMonitor`
的 `riid` 又被填成 CsWinRT 投影类的 GUID（实测 `e430267b-…`，不是类 IID）。后果不是崩溃而是
**恒 `E_NOINTERFACE`** —— 两条按-hmonitor 的主路径在所有机器上都没生效过，捕获一直由
`TryCreateFromDisplayId` 兜着（这也解释了 0.3.3 那次"DisplayId 恒取主显示器"为什么影响那么广）。

- **捕获主路径修好**：真值取自 SDK `Windows.Graphics.Capture.Interop.h`（`3628E81B-…`）。
  影响：按屏取源不再依赖 WinUI3 `DisplayArea` 反查那条脆弱链路；副屏/多屏拼接同受其益。
- **回退腿失败不再静默**：DisplayId 反查的三档失效（句柄失效 / 中心点不属于任何 DisplayArea /
  抛异常）与 `TryCreateFromDisplayId` 返回 null，全部落 Info 日志。
- **会话建不起的根因现在默认档就能看见**：`CreateItem 失败`、各条路径的 HRESULT 原本只写 Debug 级
  （在旧构建里 Debug 永不落盘），现场日志因此永远缺判据。
- **三档策略（用户口径）**：sRGB 屏 ⇒ 只用 8-bit SDR，不建 Float16 源；广色域 SDR 屏 ⇒ BT.2020 +
  SDR 传输；HDR 屏 ⇒ BT.2020 + HDR。**根因**是旧判据把 `advancedColorSupported` 当成逐屏证据 ——
  那是系统能力，纯 sRGB 8-bit 副屏也报"支持"，实测本机 800x600@100% 副屏就因此白建了浮点会话、
  产物还被写成 BT.2020 声明。位深仍由用户的位深下拉决定，不因本条改变。
- **多屏拼接的 HDR 源**：只接受逐屏有明确证据的屏（系统级 ACM 单值从此选不出源），且 HDR 档优先于
  广色域档，不再"主屏给得出就用主屏"。
- **新增 `TrueToneCap.exe --display-audit`**：逐屏一张卡，给"判据输入 → 分类 → 实得源 → 浮点像素
  实测动态范围"，输出 `display-audit.txt`。排"这台机器到底走的哪一档"不必再靠读代码。
- 双屏混合 DPI 实机验证（3840x2160@150% HDR + 800x600@100%）：逐屏取到自己尺寸的源、拼接
  4640x2160、副屏转 8-bit sRGB 声明（cICP 1/13 无 iCCP）、主屏仍走 BT.2020；非增量全量重建后
  门禁 7 套件 1125 断言 0 失败。
- **"广色域"改由面板自己的 EDID 原色确证**（同日续，交接 §40）：上一版把 DWM 的 `mode=1(WCG)` +
  "广色域已启用"当证据，而那是**用户开关的镜像**。实测一块 100% sRGB 的 2560x1600 笔电屏开 ACM 后
  这两个读数全绿，被判广色域 ⇒ 产物被声明成 BT.2020。现在必须是 EDID 硬件原色确证
  （绿点相对 BT.709 拉伸 ΔG≥0.05）才算广色域：同一块屏 ΔG=0.0246 ⇒ 退回 8-bit SDR，
  另一块真 P3 屏 ΔG=0.0963 ⇒ 维持原档。用户可见的影响：**窄色域屏不再产出"声明 BT.2020、
  实际只有 sRGB 内容"的文件**。位深仍按用户的位深下拉，不因本条改变。
- **修掉"EDID 从来不按屏读"**：`ReadEdidFromRegistry` 收了显示器句柄却全程没用，返回注册表里
  **第一个**合法 EDID ⇒ 多显示器下每块屏读到的都是同一份面板原色。影响面不止上面的分档，
  还包括"按原生色域自动选色彩空间"这条从 0.3.3 起就在错的功能。现在按句柄解析该屏自己的注册表实例，
  **解析不到就报"不知道"**，绝不拿别块屏的数据顶上。
- 同日再复跑（3840x2160@150% HDR + **2560x1600** 副屏）：主屏 `Hdr / ΔG=0.0963 / Float16=有`，
  副屏 `NarrowSdr / ΔG=0.0246 / Float16=无`（副屏 ACM 开与关两种状态下分类相同，只有 AC 关时
  走的是合成模式那条判据）；非增量全量重建后门禁 **8 套件 1174 断言 0 失败**。
- **同一次截图的色彩决定收成一份**（同日第三轮，交接 §41/§42）：上一轮只把"声明"按屏收口，
  而**像素那条路**仍各自解析一次你的色域下拉、并且用另一套更松的原生色域判据取"源色域"。
  后果是一枚自相矛盾的产物：**像素被按 DisplayP3 搬进 BT.2020，容器却声明 sRGB**。
  现在解析、收口、源色域、目标色域、HDR 许可全在一次计算里出（Core 的 `ColorDecision`），
  烘焙与声明消费同一份 ⇒ 这类分叉从结构上不可能再出现。用户可见的影响：
  ① 确证的 sRGB 屏上，选 BT.2020 不再产生"搬过色却写 sRGB"的文件（改为不搬、如实写 sRGB）；
  ② **"跟随显示器"这一档现在也受同一收口约束**（上一轮它被显式放过 ⇒ 窄屏照样拿到 BT.2020 声明）；
  ③ 色彩决定改按**被截的那块屏**算，不再按光标停在哪儿算（区域截图、多屏混合排列时结果会变）。
- **自动档不再猜广色域**：EDID 原色读不到的屏，选"跟随显示器"时按 sRGB 出片（过去可能靠 ICC 兜底
  猜成 P3 并据此搬色）。要在这类屏上出广色域，请显式选 BT.2020 —— 那会做一次真实的 sRGB→BT.2020 搬运，
  声明与像素仍然一致。确证为 P3/AdobeRGB 的面板不受影响（照常拿到 BT.2020 声明并真搬）。
- **色彩管理不再有开关**（同日第四轮，用户拍板，交接 §43）：界面里的"ICC 烘焙"开关与设置项
  `IccBakeEnabled` 已删除，截图**默认就把像素映射进标准色域**（sRGB / BT.2020）。
  影响：不再可能出现"像素一个没动、容器却声明 BT.2020"的那类文件（那是开关关着时的默认产物）；
  老设置里残留的该字段自动失效，不需要重设任何项。
  ⚠ 一个诚实的副作用：8-bit 截图在**确实需要搬色**时不再走 NVENC 的 GPU 纹理直通（必须过 CPU 矩阵，
  因为驱动下发的 H.273 矩阵系数是 YUV↔RGB 系数、不是色域转换）；同色域无需搬色时仍走直通，性能不变。
- **`AnimationRecorder`（动图录制）整条路在仓库里没有任何调用点**（本轮 grep 实证）：界面上的
  "录制"是录快捷键。⇒ 之前记为缺陷的"动图丢色彩声明"今天不可能触发；这条腿是删还是接回来属产品决定。
- **JPEG GainMap（Ultra HDR）现在有两种色彩模式**（同日第六轮，交接 §45）：
  ① 默认（含"跟随显示器"）base 与增益图都在 sRGB / BT.709 里；② **手动**选 BT.2020 时，
  整张增益图全部走 BT.2020 —— 像素真搬、base 换嵌 2020 ICC、增益比在同一套原色上算，三者同源。
  自动在一块 P3 屏上解析出 BT.2020 **不**触发第二种（那不是你手动选的）；取不到可嵌的 2020 ICC 时
  整条退回默认模式（JPEG 只有 ICC 一条声明通道，声明不了就不搬，免得产出错件）。
- **JPEG XL 的色域声明不再一律回落 sRGB**（同日第五轮，交接 §44）：没有可嵌 ICC 时过去主动写
  `color_space=sRGB` ⇒ 目标选 BT.2020 的 JXL 会被声明成 sRGB（别的格式最多"不声明"，只有它是"写错"）。
  现在按色彩决定写对应枚举（sRGB / `RGB_D65_202_Rel_SRG` / `DisplayP3`），有 ICC 时仍只给 ICC。
- **门禁新增 `[1.8/2] 色彩管线判据`（三件，默认执行）**：cjxl 的 `color_space` 标签判别、
  产品 JXL 入口、GainMap 四层（含 libultrahdr 外部解码）。外部工具路径统一成一处事实源，
  可用 `TTC_CJXL` / `TTC_ULTRAHDR_APP` 覆盖；找不到工具时报 **SKIP 并明写"这一轮没判"**，
  不与"通过"混记（三态：0 判了 / 1 判红 / 3 没判）。
- **修掉一条既存假绿灯**：色彩判据脚本跑前不删上一轮产物 ⇒ 外部工具被换成不相干程序时仍可能"通过"。
  现在每次运行先清目录，且"文件存在"不再被当作"能用"（两代 `ultrahdr_app` 的 CLI 互不兼容，改为先做能力探测）。
- 判据产物一律落在 gitignore 的 `artifacts/` 下（此前写在 `scripts/precision/` 会脏工作区，
  而发布门禁是硬拦脏树的）。
- **新增 `TrueToneCap.exe --display-audit-emit`**：在逐屏审计之外，每屏再各落两枚真 PNG
  （"跟随显示器"与"BT.2020"两种下拉），并把**从盘上读回来**的 cICP/iCCP 与"像素搬没搬"印进审计卡。
  排"这台机器到底出的什么色"不必再靠人肉截屏，也不需要移动鼠标。

## [0.3.3-beta] — 2026-08-29 ~ 08-31
- NVENC 硬件编码修复（此前从未成功初始化，实测定位并修复 6 处缺陷）

## [0.3.2-beta]
- 版本发布

## [0.3.1-beta]
- 版本号更新并发布打包
