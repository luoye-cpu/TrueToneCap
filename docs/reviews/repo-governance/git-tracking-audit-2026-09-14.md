# Git 历史误跟踪文件审查报告

> **日期**：2026-09-14
> **范围**：全部 24 个提交（`HEAD` = `42cc8d8`），155 个被跟踪文件，`.git` 体积 170 MB
> **结论**：**未发现经典的"垃圾文件误入库"**（无 bin/obj/node_modules/pycache/日志/临时文件），
> 但**存在两处结构性问题**：① 原生工具链双份重复；② 模型文件多代累积导致历史膨胀。

---

## 一、好消息：经典误跟踪模式全部为 0

对以下高风险模式做了全量扫描，**零命中**：

| 模式 | 结果 |
|------|:---:|
| `bin/`、`obj/`（构建产物） | ✅ 无 |
| `.vs/`、`.idea/`、`node_modules/` | ✅ 无 |
| `__pycache__/`、`*.pyc` | ✅ 无 |
| `*.log`、`*.tmp`、`*.dmp`、`*.user` | ✅ 无 |
| `.DS_Store`、`Thumbs.db`、`Desktop.ini` | ✅ 无 |
| `TestResults/`、`testdata/` | ✅ 无 |
| `*.cache`、`*.pdb` | ✅ 无 |

**根目录跟踪文件**（9 项）全部为合理配置：`.gitignore`、`LICENSE`、`README.md`、
`Publish.ps1`、`PublishLogViewer.ps1`、`TrimmerRoots.xml`、`TrueToneCap.slnx`、
`dotnet-tools.json`、`download_models.ps1`。

---

## 二、问题 1 🔴 原生工具链存在双份（27 MB 重复跟踪）

### 事实

`publish/PLAN/tools/` 下的 **9 个二进制全部**与 `src/TrueToneCap.Core/Resources/` **逐字节相同**
（blob hash 一致）：

| 文件 | blob | 大小 |
|------|------|:---:|
| `avifenc.exe` | `0dbf94249c` | 11.8 MB |
| `cjpegli.exe` | `c7504364bd` | 5.1 MB |
| `cjxl.exe` | `9e5d8de96b` | 4.5 MB |
| `cwebp.exe` | `aed70eb32a` | 0.7 MB |
| `libgcc_s_seh-1.dll` | — | 0.7 MB |
| `libstdc++-6.dll` | — | 2.2 MB |
| `libuhdr.dll` | — | 1.2 MB |
| `libwinpthread-1.dll` | — | 0.09 MB |
| `ultrahdr_app.exe` | — | 1.2 MB |
| **合计** | | **27 MB** |

### 权威来源已明确

`Publish.ps1` 第 5、97-98 行明确声明：

```
#   · 单一事实源: 原生工具链以 src/TrueToneCap.Core/Resources/ 为准，复制到发布包
# 原生工具链单一事实源: Core/Resources/（Publish 时复制进发布包）
$toolsSrc = Join-Path $RepoRoot "src\TrueToneCap.Core\Resources"
```

全脚本**不存在**任何对 `publish/PLAN/tools` 的读取。
且 `git log` 显示 `Core/Resources` 的引入时间（`18737de`，07-30）**早于**
`publish/PLAN/tools`（`befd0d0`，08-01），`publish/PLAN/tools` 是历史遗留副本。

### 风险

- 双份人工同步 → **漂移隐患**（与已记录的 `models` 问题同源）
- 27 MB 冗余跟踪，且每次更新工具链都会**再产生一份新 blob**（旧版永久留存）

### 建议

```
git rm -r --cached publish/PLAN/tools
```

**但注意**：`publish/PLAN/tools/` 目录本身也被 `.gitignore` 白名单放行
（`!publish/PLAN/**`），删除后需补规则：

```gitignore
publish/PLAN/tools/
```

> ⚠️ `libuhdr.dll` / `ultrahdr_app.exe` 等 5 个文件虽然在两处都存在，
> 但**只有 `Core/Resources/` 是发布时真正使用的**（`Publish.ps1` 的 `$toolFiles` 清单引用它），
> 故删除 `publish/PLAN/tools/` 无功能影响。

---

## 三、问题 2 🔴 模型文件多代累积（历史膨胀约 132 MB）

### 事实

同一个逻辑模型在历史中留下了**两代不同大小的 blob**，且**旧版永久占用空间**：

| 路径 | 第 1 代 blob | 第 2 代 blob |
|------|:---:|:---:|
| `PP-OCRv6_medium_rec.onnx` | `3093b25bc8` **73.1 MB** | `f5ce81949f` 36.6 MB |
| `PP-OCRv6_medium_det.onnx` | `f8633ab31e` **59.2 MB** | `8c05b608c4` 29.7 MB |

**git 的跨路径去重起了作用**（两处副本共享同一 blob），但**跨版本不去重** ——
所以 73.1 + 59.2 = **132 MB 的旧版模型至今仍压在 `.git` 里**，
而它们对应当前源码已不再需要（现有模型是第 2 代较小的版本）。

### 与已处理问题的关系

本次刚提交的 `42cc8d8` 删除了 `publish/PLAN/models/` 的重复副本，
但**只是从索引移除**；其历史 blob **仍然存在**。这解释了为什么 `.git` 有 170 MB：

```
.git 总体积            170 MB
├─ 第 2 代模型（在用）   66.3 MB   （36.6 + 29.7）
├─ 第 1 代模型（历史）  132.3 MB   ← 纯冗余
├─ 工具链（1 份）        27 MB
└─ 源码 + 文档          ~10 MB
                        ────────
（模型两代合计 > 170 是因压缩后体积不同，此处为逻辑大小）
```

### 建议（需重写历史，谨慎操作）

仅删除文件**无法**释放空间，必须重写历史 + 垃圾回收：

```bash
# 方式 A：git-filter-repo（推荐，需单独安装）
git filter-repo --path publish/PLAN/models --path publish/PLAN/tools --invert-paths

# 方式 B：原生（较慢）
git filter-branch --force --index-filter \
  'git rm -r --cached --ignore-unmatch publish/PLAN' \
  --prune-empty -- --all

# 两种方式后都必须：
git reflog expire --expire=now --all
git gc --prune=now --aggressive
```

> ⚠️ **重写历史会改变所有提交 SHA**，若已推送到远程需 `--force` 推送，
> 且**所有协作者必须重新克隆**。若仓库已公开/多人协作，建议先评估。
>
> 若采用**长期方案**（模型不入库、改由 `download_models.ps1` 按需获取，
> 见 `docs/repo-hygiene-review.md` 第 23 行的既有计划），
> 则历史重写是必要的前置步骤。

---

## 四、问题 3 🟢 `.gitignore` 白名单规则过宽

### 事实

```gitignore
publish/*
!publish/PACKAGE.md
!publish/PLAN/
!publish/PLAN/**
```

`!publish/PLAN/**` 这一条**无条件放行 PLAN 下的一切**，
导致后续必须用多条补丁规则逐项排除：

```gitignore
publish/PLAN/tools/*.raw          # 排除 raw
publish/PLAN/models_fp16/         # 排除
publish/PLAN/models_fp32/         # 排除
publish/PLAN/models/              # 排除（本次新增）
```

这种"先全放行、再逐项排除"的结构**每加一类文件就要补一条规则**，
是 `publish/PLAN/tools/*.raw` 这类临时产物曾误入库的根因。

### 建议（可选重构）

改为白名单式（只放行明确需要的）：

```gitignore
publish/*
!publish/PACKAGE.md
!publish/PLAN/
!publish/PLAN/README.md
```

配合删除 `publish/PLAN/tools/` 与 `publish/PLAN/models/` 跟踪，
可一次性消除全部补丁规则。

---

## 五、问题 4 🟢 `publish/PLAN/` 定位已经过时

`publish/PLAN/` 目录名与位置（在 `publish/` 发布产物目录下）**极易被误解为构建输出**，
但它实际被纳入版本控制并作为"预整理资料"。

`docs/repo-hygiene-review.md` 第 90 行已记录过类似判断：
> 长期建议：模型文件迁移到 Git LFS 或纯下载方案，仓库体积可从 168MB 降至 <40MB

**建议**：把 `publish/PLAN/` 中真正需要版本控制的内容（如 `README.md`）
迁至 `docs/` 或根目录，然后整体解除该目录的跟踪。这与问题 2、3 的解决方案合并执行最省事。

---

## 六、审查结论汇总

| # | 问题 | 严重度 | 冗余体积 | 建议动作 |
|:--:|------|:---:|:---:|------|
| 1 | 原生工具链双份重复 | 🔴 | 27 MB | `git rm -r --cached publish/PLAN/tools` + 补忽略规则 |
| 2 | 模型多代累积（历史膨胀） | 🔴 | ~132 MB | 历史重写 + `git gc`（需评估协作影响） |
| 3 | `.gitignore` 白名单过宽 | 🟢 | — | 改为白名单式 |
| 4 | `publish/PLAN/` 定位过时 | 🟢 | — | 迁移必要内容后解除跟踪 |
| — | 经典垃圾文件（bin/obj/log/pycache…） | ✅ | 0 | **无问题** |

**总体评价**：这个仓库的 `.gitignore` 维护得**相当克制**（无任何经典垃圾误入库），
24 个提交的历史也干净。真正的问题**只有一类：大二进制文件的重复与版本累积**，
这正是 `publish/PLAN/` 这个"既像产物又入库"的特殊目录带来的。

**最经济的组合动作**：一次性解除 `publish/PLAN/` 全目录跟踪（迁移 `README.md`），
随后做一次历史重写，可把仓库从 **170 MB 压到约 40 MB**（与既有 roadmap 的目标一致）。

---

## 附：核查方法（可复用）

```bash
# 1) 列出全部跟踪文件
git ls-files

# 2) 扫描经典垃圾模式
git ls-files | grep -iE "bin/|obj/|\.vs/|node_modules|__pycache__|\.pyc$|\.log$|\.tmp$|\.cache|TestResults"

# 3) 找出历史中所有大 blob（含已删除的！）
git rev-list --objects --all \
  | git cat-file --batch-check='%(objecttype) %(objectname) %(objectsize) %(rest)' \
  | awk '$1=="blob" && $3 > 500000 {print $3, $4}' | sort -rn

# 4) 检查同一路径是否有多个历史版本（体积膨胀主因）
for c in $(git log --all --format=%H -- "<path>"); do
  git rev-parse "$c:<path>"; done | sort -u \
  | while read b; do echo "$b $(git cat-file -s $b)"; done

# 5) 判断两处副本是否重复（blob hash 相同即重复）
git rev-parse HEAD:<pathA>; git rev-parse HEAD:<pathB>

# 6) 查看 .git 实际占用
du -sh .git
```
