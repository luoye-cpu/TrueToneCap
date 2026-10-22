# 打包与产物保留规范

制定于 2026-09-26。起因很直接：`publish/` 里同时躺着 4 份 414 MB 的展开目录
（`TrueToneCap-v0.3.5-beta`、`-s53`、`-cde6168`、`-i18n`）加两枚 09-24 的归档，共 2021 MB。
根因有两条：**目录后缀是我每轮现编的**（`-s53`/`-cde6168`/`-i18n`，名字既不说状态也不说能不能删），
以及**从来没有"留几份"的规则** ⇒ 每出一次包就多一份。`.gitignore` 第 52 行早给本文件留了位置
（`!publish/PACKAGE.md`），一直没写。

本规范只管**命名与保留**。构建正确性的判据在门禁脚本与
`docs/ledger/handoff-avif-10bit-wide-gamut-sdr-2026-09-17.md` 里，这里不重复。

---

## 1 `publish/` 这一层允许存在什么

| 允许 | 形态 | 依据 / 说明 |
| --- | --- | --- |
| 当前待测的**一份**展开目录 | 正式名，或 `_scratch/` 下的一份 | 同时最多一份；出新包前按 §5 处理旧的 |
| 已发布版本的归档 | `TrueToneCap-v<版本>-win-x64.7z` + `.sha256` | 每个已打 tag 的版本一枚；**未发布的树不出归档** |
| 原生依赖暂存 | `PLAN/tools/` | ⚠ **不是发布包**。`avifenc / cjpegli / cjxl / cwebp` + 依赖 DLL + 色域取证 `.raw`，组包从这里取料，删了下次发布断料 |
| 配套程序的产物 | `TTC日志查看器/` | `PublishLogViewer.ps1:10` 的默认 `-OutputDir`，与主程序版本号无耦合；要用就现出（约 1 分钟），别长期留着 |
| 本文件 | `PACKAGE.md` | `.gitignore` 里唯一被反选的常规文件 |

## 2 命名

- **正式产物**：`TrueToneCap-v<版本>-win-x64`。版本号实测来自 `Directory.Build.props:49`
  （`<Version Condition="'$(Version)' == ''">…</Version>`），**不在 `App.csproj`**；
  `Publish.ps1 -Version` 留空时读的就是它，为的是别让手填版本号漂。
- **未提交 / 实验产物**：`publish/_scratch/TrueToneCap-v<版本>-<MMDD>-<标记>/`，
  例：`publish/_scratch/TrueToneCap-v0.3.5-beta-0926-i18n/`。
- **叶子名必须以 `TrueToneCap-v` 开头**：实测 `Publish.ps1:448` 的白名单是
  `GetFileName($OutputDir) -notmatch '^TrueToneCap-v'` ⇒ `throw`，只有 `publish\` 前缀不够。
  （本规范初稿写的 `_scratch/<MMDD>-<标记>` 撞的就是这道闸门，属于"规则撞自己脚本"，已改正。）
  父目录 `_scratch/` 就是"一轮一删"的标记；`publish/*` 已被忽略，不进版本库。
- **禁止**再往正式目录名后面加现编后缀。要区分来源，写进标记或 `build-info.json` 旁边的说明，
  不要污染版本名。

## 3 出包与归档（参数逐条对过脚本）

```powershell
# 全新编译：先 --no-incremental 全量重建，再出包（原因见 §4）
dotnet build TrueToneCap.slnx -c Release --no-incremental

# 出包。脏树必须显式 -AllowDirty：脚本默认拒绝，因为 build-info 记的 commitSha 会与内容不符
.\Publish.ps1 -Configuration Release -AllowDirty `
    -OutputDir "publish\_scratch\TrueToneCap-v<版本>-<MMDD>-<标记>"
# 绝不用 -Force —— 它的含义是强杀正在运行的 TrueToneCap.exe（第 33 行注释写明）

# 归档只在"提交 + 打 tag"之后做，且 -OutputPath 必须给：
# 不给时 pack.ps1 第 97–99 行按版本算默认名，第 113–115 行对同名文件 Remove-Item -Force
# ⇒ 这就是"覆盖同版本号正式包"的确切机制
.\scripts\pack.ps1 -PackageDir <目录> -OutputPath "publish\TrueToneCap-v<版本>-win-x64.7z"

# 校验两件，缺一不可
.\scripts\verify-package.ps1 -PackageDir <目录> -ExpectedVersion <版本>
.\scripts\verify-package-content.ps1 -Pkg <目录>
```

## 4 校验与新鲜度

- **`RESULT content=PASS` 才算出过包。** `verify-package-content.ps1` 第一道是内容核对
  （本轮新串在 / 本轮删掉的旧串不在 / **同容器对照组**），第二道才是时间戳。
  XAML 文字只存在于 `.xbf`（不在 DLL），所以对照必须在 `.xbf` 里做——拿 DLL 里的串当对照会得出假结论。
- 时间戳那道判"比源码旧"时，**不要用"这是启发式误报"说服自己**：`.cs` 改动不会重生成 `.xbf`
  （`.xbf` 只随 XAML 变），所以"改了 C# 又跑回原样"这类操作把 mtime 推后，就足以让它红。
  正确动作：`dotnet build <slnx> -c Release --no-incremental` 后重出，让所有产物晚于所有源。
  2026-09-26 实测走过一遍：红只在 `[6]`，`[1]–[5]` 内容核对全过；全量重建重出后转 

- **分清"判据判红"与"工具抛红"**：`Publish.ps1` 的 `PUBLISH_EXIT=1` 不一定是测试红。
  实测有一次是脚本内部 `dotnet run` 撞上"MSBuild 服务器不可用 … 回退到进程内生成"，
  stderr 被 PowerShell 抛成 `NativeCommandError`，目标目录根本没建。
  判据：去日志里找 `RESULT suite=… failed=…` 与"❌ …套件…"行；只有红字异常、没有 RESULT 行
  ⇒ 是工具噪声，动作是 `dotnet build-server shutdown` 后重跑，绝不是去放宽判据。
- **产物目录深度不是契约**：把包挪进 `_scratch/` 后 `verify-package-content.ps1` 判 `FAIL(1)`，
  原因是它按"包目录往上数两层"推算仓库根（`publish\<名>` 的深度被写死），两上层落到 `publish\`
  ⇒ `git ls-files` 空 ⇒ 新鲜度整层"没判"（失败方向安全，但整层失效）。
  已改成问 git：`git -C $Pkg rev-parse --show-toplevel`，时间戳比较逻辑一字未动；
  新布局与旧布局各跑一次，同一个仓库根、126 个跟踪源、都是 `RESULT content=PASS`。
  ⇒ 任何脚本都不许用"往上数几层"定位仓库根。`PASS`。
- `build-info.json` 里 `dirtyWorktree: true` ⇒ **包内容 ≠ 它记的 commitSha**。
  对外只能称"本地待测产物"，不得当可复现产物引用。

## 5 保留与清理

保留：当前待测的一份展开目录 + 每个**已打 tag** 版本的 `.7z`/`.sha256`。其余算过时，可删。

删除前两步必做，因为展开目录里可能有手动测试跑出来的数据：

```bash
ls <目录>/TrueToneCap.settings.json ; du -sm <目录>/log     # ① 有没有用户数据
# ② 有就先挪走并核对，才允许删目录
```

挪到 `C:\PLAN\qoder-scratch\ttc-publish-userdata-<日期>\<包名>\`，逐个核 sha1 相同后才 `rm -rf`。
`Publish.ps1` 的 `-OutputDir` 是**先校验、门禁通过后才 `Remove-Item -Recurse`**（第 551 行；
第 435–443 行注释写明原先"先删后跑门禁"会把唯一能回退的东西一起毁掉）⇒ 出包失败不吃上一份可用包，
但**目录名写对仍是操作者的责任**（白名单只保证它在 `publish\` 内且叶子以 `TrueToneCap-v` 开头）。

## 6 禁止事项

1. 不给 `pack.ps1` 省略 `-OutputPath`（§3 写明它删同名归档）。
2. 不用 `Publish.ps1 -Force`，除非明确要求终止自己正在跑的实例。
3. 不在 `publish/` 放构建中间产物、日志、截图、临时脚本 —— 那些归 `tests/output/`。
4. 不把 `publish/PLAN/tools/` 当"过时的包"删。
5. 一轮结束不清理自己那份实验目录 —— 本规范要拦的就是这个。
6. 不给未提交的树出 `.7z` 归档（归档只属于 tag）。

## 7 清理记录

**2026-09-26 第一次**：删三份被取代的展开目录 `TrueToneCap-v0.3.5-beta/`、`-s53/`、`-cde6168/`
（414 MB × 3）。删前三份 `TrueToneCap.settings.json` 与四个日志文件先挪进
`qoder-scratch\ttc-publish-userdata-2026-09-26\`，三对 settings 哈希相同、`log` 清单哈希相同。
`publish/` 2021 MB → **782 MB，释放 1239 MB**。

**同日第二次**：删两枚 09-24 归档 + 两个 `.sha256`（254 MB）。判定依据（实测）：
仓库**没有 `v0.3.5-beta` 这个 tag**（最新是 `v0.3.4-beta`），两枚内 `build-info.json` 都是
`commitSha 614a0b1` + `dirtyWorktree: true`，即未发布预产且已被今天的树取代。
删除前留档：`…win-x64.7z` 126,802,233 B / sha256 `9b34cc1f92e317b1…`；
`…build2.7z` 126,958,073 B / sha256 `b17271613c347231…`（两个 `.sha256` 与实算一致）。

同日按本规范重出全新产物：`publish/_scratch/TrueToneCap-v0.3.5-beta-0926-i18n/`，
旧的非规范命名目录 `publish/TrueToneCap-v0.3.5-beta-i18n/` 经查**无** settings、**无** log
（未被运行过）后删除

**终态**：`publish/` = **540 MB** 四样东西 —— `PACKAGE.md`、`PLAN/`(43 MB 依赖暂存)、
`TTC日志查看器/`(84 MB 配套程序)、`_scratch/TrueToneCap-v0.3.5-beta-0926-i18n/`
(414 MB 当前待测 / 608 文件 / `builtAtUtc 2026-09-26T14:57:10Z` / `dirtyWorktree: true` /
门禁 7 套件 46.4 s 全过 + 冒烟过 + 两个校验器都 `PASS`)。自 2021 MB 起累计**释放 1481 MB**。。
