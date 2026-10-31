"""生成仓库根的 THIRD-PARTY-NOTICES.md（单一事实源 = licenses/INDEX.json + tools-manifest.json）。

为什么是生成的而不是手写：这份文件要逐字随包分发，手写清单必然与 licenses/ 下的全文漂移
（2026-09-20 实测已经漏过一次：sjpeg 静态链入 cjxl/cjpegli，INDEX 有它、manifest 的 licenseFiles
没有）。生成之后，"正文与全文是否同一批"就退化成一条可机检的判据：--check 重算一遍比对字节。

全文是**内联**的，不是链接到 licenses/*.txt —— 因为应用内的"第三方许可"窗口只读这一个文件
（见 src/TrueToneCap.App/ThirdPartyNoticesWindow.xaml.cs），做成链接用户就看不到许可正文。

用法:
    py scripts/gen-third-party-notices.py            # 写 THIRD-PARTY-NOTICES.md
    py scripts/gen-third-party-notices.py --check    # 只校验：漂移则退出码 1（门禁用）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import hashlib
import io
import json
import os
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
INDEX = os.path.join(REPO, "licenses", "INDEX.json")
MANIFEST = os.path.join(REPO, "src", "TrueToneCap.Core", "Resources", "tools-manifest.json")
OUT = os.path.join(REPO, "THIRD-PARTY-NOTICES.md")

# 超过这个体积的文件不内联（微软那族 third-party-notices 单份就有 300 KB，四份合计 843 KB，
# 全内联会把这份"给人读"的文件撑成 1 MB 且应用内窗口渲染不动）。
# 不内联 ≠ 不分发：licenses/ 整个目录都由 Publish.ps1 复制进包，义务照样闭环，
# 本文件里对每一份都会点名它的确切路径。
INLINE_MAX_BYTES = 100_000

# 分桶标题：按 shippedIn 的第一个归属值分组，顺序即文中出现顺序
BUCKET_TITLES = [
    ("avifenc.exe", "AVIF 编码器 avifenc.exe（本仓库自编译）"),
    ("cjpegli.exe", "JPEG / Gain Map 工具 cjpegli.exe"),
    ("cjxl.exe", "JPEG XL 工具 cjxl.exe"),
    ("cwebp.exe", "WebP 工具 cwebp.exe"),
    ("deflate.dll", "无损压缩库 deflate.dll（libdeflate）"),
    ("runtime", ".NET 运行时与微软组件"),
    ("models", "OCR 模型与词典"),
]

HEADER = r"""# Third-Party Notices / 第三方许可声明

> 本文件由 `scripts/gen-third-party-notices.py` 从 `licenses/INDEX.json` +
> `src/TrueToneCap.Core/Resources/tools-manifest.json` **生成**，请勿手工编辑正文；
> 许可全文的原始副本在 `licenses/` 目录（与本文件内联的内容逐字节相同，`INDEX.json`
> 记录每个文件的 SHA256 与来源）。校验新鲜度：`py scripts/gen-third-party-notices.py --check`。

TrueToneCap / 真色截图 自身以 Apache License 2.0 发布，见 [LICENSE](./LICENSE)。

下列组件由本程序**再分发**（静态链入原生工具、或随自包含发布携带），各自的许可全文内联在
本文末尾。每一项都标了它属于哪个产物、上游版本、以及该许可对"二进制再分发"提的具体要求。

## 我们据此做了什么

| 义务来源 | 要求 | 本项目如何满足 |
|---|---|---|
| BSD 系列（libavif / libaom / SVT-AV1 / libjxl / libwebp / libjpeg-turbo / skcms / ONNX Runtime / WebView2） | 再分发二进制须保留版权声明与免责条款 | 全文随包（`licenses/` + 本文件），并在下方逐条署名 |
| Apache-2.0（highway / sjpeg / PaddleOCR 模型权重） | §4(d) 须保留 NOTICE 与来源说明 | 全文随包；本文件逐条标注版本与来源 URL/commit |
| IJG（libjpeg） | 须注明"基于 Independent JPEG Group 软件" | 下方 IJG 条目原文即含该声明，且随包分发 |
| Zlib | 二进制分发须携带署名或 acknowledgement | 全文随包 |
| libpng License v2 | 分发须附许可文本 | 全文随包 |
| MIT（.NET 运行时及各扩展包） | 复制分发须含版权与许可声明 | 全文随包，含 .NET 自带的 third-party notices |
| Microsoft OEL / EULA（WindowsAppSDK、DirectML 二进制） | 按各自协议条款允许再分发，并随附 NOTICE | 全文与 NOTICE 随包；DirectML 的 `LICENSE-CODE.txt`(MIT) **不覆盖** `DirectML.dll`，后者受微软 EULA 约束，两者分列 |
| Windows 运行时投影（WinRT.Runtime.dll / Microsoft.Windows.SDK.NET.dll） | 见下方"未取得全文"一节 | 未闭环 |
| 界面图标轮廓（派生自系统 Segoe 符号字体） | 见下方"未取得全文"一节 | 未闭环 |

## ⚠️ 专利授权不在上面这些许可里 —— 需自行确认

上游 BSD/Apache 文本给了版权许可，但**编码标准的专利授权是另一回事**。逐条实测：

- **AV1**（`avifenc.exe` 内的 libaom 3.14.1 / SVT-AV1 v4.1.0）：SVT-AV1 采用
  **BSD-3-Clause Clear**，其原文第 23 行明写「*NO EXPRESS OR IMPLIED LICENSES TO ANY PARTY'S
  PATENT RIGHTS ARE GRANTED BY THIS LICENSE*」；libaom 附带的是 AOM 的
  `LicenseRef.AOMPatentGrant`（要求分发实现时随附该许可全文，本文已随附）。
  AV1 的**专利**授权需另行向 Alliance for Open Media 专利池确认，Apache-2.0 不提供。
- **JPEG XL**（`cjxl.exe` / `cjpegli.exe` 内的 libjxl 0.11.2）：BSD-3-Clause 正文里
  **一次都没出现 "patent"**；专利授权是单独的「Additional IP Rights Grant (Patents)」一节，
  由 Google 单方授予（本文一并随附该节全文）。JPEG XL 的专利池由 **Via LA** 独立 Licensing
  管理，本程序不提供任何保护。
- **WebP / VP8**（`cwebp.exe` 内的 libwebp 1.5.0）：同样是 BSD-3-Clause + 独立的
  Google WebM 专利授权节；WebM 专利池亦需单独确认。

一句话：本项目的 Apache-2.0 与上表所有第三方许可**都是版权许可，不构成任何专利许可**。

## 未取得全文的组件（诚实标注，不假装合规）

- **WinRT CsWinRT 投影**：`WinRT.Runtime.dll` 与 `Microsoft.Windows.SDK.NET.dll`
  在其 NuGet 包内**零许可文件**，nuspec 只有一个 `aka.ms/WinSDKLicenseURL` 短链，
  本环境无法解析。二者由 WindowsAppSDK 携带、受微软 Windows SDK 许可约束；
  在拿到可再分发的明确依据之前，这一项**未闭环**。

- **界面图标轮廓**：本程序的界面图标不是运行时引用系统字体画的，而是**离线把字形轮廓抽出来、
  以路径数据烘进 `TrueToneCap.dll`**（原因见 `docs/reviews/usability/pipeline-and-usability-review-2026-10-01.md` §11-②：
  本进程解析不出这些符号字体，图标会渲染成方块）。轮廓取自
  `C:\Windows\Fonts\SegoeIcons.ttf`（Family 名 `Segoe Fluent Icons`）与
  `C:\Windows\Fonts\segmdl2.ttf`（`Segoe MDL2 Assets`），共 32 条不同轮廓 / 46 处引用。
  这两个字体文件本身受**微软 Windows 桌面操作系统许可**约束，不属于上面任何一条开源许可，
  因此上表的 BSD/Apache/MIT 义务**都不覆盖这一项**；而"把字形轮廓派生后再分发"是否被该许可允许，
  本环境**没有可依据的条文**。在拿到明确依据之前，这一项与 WinRT 投影同样记为**未闭环**，
  不假装它已经被上面那些许可覆盖。
  替代路径（若要闭环）：改用 Fluent UI System Icons 等 **MIT 许可的 SVG 几何**，
  构建期转成路径数据——从此不再派生自微软字体。

其余条目的 `obligationNote` 见下表逐条列出。
"""


def load_json(path: str):
    with io.open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def pretty_source(e: dict) -> str:
    """把来源写成可移植的引用，而不是某台机器上的绝对路径。

    INDEX.json 里保留的是**取证时的真实路径**（那是它该待的地方 —— 便于本机复核），
    但这份文件要随包发给所有用户，`C:\\Users\\<某人>\\...` 既无意义又泄露本机信息。
    """
    kind = e.get("sourceKind", "")
    src = e.get("sourcePathOrUrl", "")
    if kind == "downloaded":
        return src
    base = os.path.basename(src.replace("\\", "/").rstrip("/"))
    if kind == "local-tree":
        return (f"上游源码树内的 `{base}`"
                "（libavif v1.4.2 的 CMake FetchContent 拉取的固定版本，"
                "各依赖的 tag 见 `src/TrueToneCap.Core/Resources/tools-manifest.json` 的 upstream 字段）")
    if kind == "nuget-cache":
        parts = [p for p in src.replace("\\", "/").split("/") if p]
        # 缓存里的形状是 …/.nuget/packages/<pkg 小写>/<版本>/<文件>
        try:
            i = parts.index("packages")
            pkg, ver = parts[i + 1], parts[i + 2]
            return f"NuGet 包 `{pkg}` 版本 `{ver}` 内的 `{base}`"
        except (ValueError, IndexError):
            return f"NuGet 包内的 `{base}`"
    return src


def render() -> str:
    idx = load_json(INDEX)
    man = load_json(MANIFEST)
    licenses = idx["licenses"]
    by_file = {e["file"]: e for e in licenses}

    out = io.StringIO()
    out.write(HEADER)

    out.write("\n## 逐组件清单\n\n")
    seen: set[str] = set()
    for bucket, title in BUCKET_TITLES:
        rows = [e for e in licenses if bucket in e.get("shippedIn", [])]
        if not rows:
            continue
        out.write(f"\n### {title}\n\n")
        out.write("| 组件 | 版本 | SPDX | 许可全文 | 再分发义务 |\n|---|---|---|---|---|\n")
        for e in sorted(rows, key=lambda x: (x["project"], x["file"])):
            seen.add(e["file"])
            note = e.get("obligationNote", "").replace("\n", " ").strip()
            if len(note) > 260:
                note = note[:257] + "..."
            ver = e.get("version", "").replace("\n", " ").strip()
            out.write(f"| {e['project']} | {ver} | `{e['spdx']}` | "
                      f"`licenses/{e['file']}` | {note} |\n")
    orphans = sorted({e["file"] for e in licenses} - seen)
    if orphans:
        out.write(f"\n> ⚠️ 有 {len(orphans)} 份许可全文没有归到任何产物桶：{orphans}\n")

    # 每个原生工具的 SPDX 表达式以 tools-manifest.json 为准（它是构建期的单一事实源）
    out.write("\n### 产物级 SPDX 表达式（来自 `src/TrueToneCap.Core/Resources/tools-manifest.json`）\n\n")
    out.write("| 产物 | 表达式 |\n|---|---|\n")
    for t in man["tools"]:
        out.write(f"| `{t['file']}` | `{t['license']}` |\n")

    out.write("\n## 许可全文\n\n")
    out.write("以下逐段为 `licenses/` 目录内文件的**原文内联**（字节相同），"
              "顺序与上面清单一致；每段前标注项目、版本、来源与本地文件。\n")
    order = [e for _b, _t in BUCKET_TITLES
             for e in sorted([x for x in licenses if _b in x.get("shippedIn", [])],
                             key=lambda x: (x["project"], x["file"]))]
    # 去重但保持首次出现顺序（同一份全文可能被多个产物引用）
    uniq: list[dict] = []
    hit: set[str] = set()
    for e in order:
        if e["file"] in hit:
            continue
        hit.add(e["file"])
        uniq.append(e)
    for e in uniq:
        path = os.path.join(REPO, "licenses", e["file"])
        raw = open(path, "rb").read()
        with io.open(path, "r", encoding="utf-8", errors="replace") as f:
            text = f.read()
        out.write(f"\n---\n\n### {e['project']} — {e['spdx']}\n\n")
        out.write(f"- 版本：{e.get('version','')}\n")
        out.write(f"- 随包于：{', '.join(e.get('shippedIn', []))}\n")
        out.write(f"- 来源：{pretty_source(e)}\n")
        out.write(f"- 本地副本：`licenses/{e['file']}`（SHA256 `{hashlib.sha256(raw).hexdigest()}`）\n\n")
        if len(raw) > INLINE_MAX_BYTES:
            out.write(f"> ⚠️ 这份有 {len(raw) // 1024} KB，未在此内联（否则本文件会被微软那族"
                      " third-party notices 撑到 1 MB，应用内也翻不动）。"
                      f"**完整原文仍随包分发**，路径 `licenses/{e['file']}`。\n")
            continue
        body = text.replace("\r\n", "\n").rstrip("\n")
        # 整段用 ~~~ 围栏，避免原文里出现 ``` 时把围栏提前闭合
        fence = "~~~"
        while fence in body:
            fence += "~"
        out.write(f"{fence}text\n{body}\n{fence}\n")

    return out.getvalue().replace("\r\n", "\n")


def main() -> int:
    check = "--check" in sys.argv[1:]
    content = render()
    data = content.encode("utf-8")           # 无 BOM
    if check:
        if not os.path.isfile(OUT):
            print("[FAIL] THIRD-PARTY-NOTICES.md 不存在")
            return 1
        cur = open(OUT, "rb").read()
        # 比"内容同步"，不比"盘上字节"：`.gitattributes` 给 `*.md` 打了 text，本机
        # `core.autocrlf=true` ⇒ 干净 checkout 会把这份 LF 换成 CRLF（实测 398,817 → 405,781 B），
        # 于是 CI / 新机器 / 新 worktree 上这条【必然判红】，而红的是换行不是同步。
        # 生成侧本来就 `.replace("\r\n","\n")` ⇒ 判侧同一条归一，两边才对称。
        cur_n = cur.replace(b"\r\n", b"\n")
        if cur_n != data:
            print(f"[FAIL] THIRD-PARTY-NOTICES.md 与 licenses/ + INDEX.json 不同步："
                  f"现有 {len(cur_n)} B（去 EOL 后）/ 应为 {len(data)} B")
            print("       修复：py scripts/gen-third-party-notices.py")
            return 1
        print(f"[RESULT] PASS — THIRD-PARTY-NOTICES.md 与清单同步（{len(data)} B，比对前两侧都按 LF 归一）")
        return 0
    with open(OUT, "wb") as f:
        f.write(data)
    inlined = content.count("\n~~~text\n")
    total = len(load_json(INDEX)["licenses"])
    print(f"已生成 THIRD-PARTY-NOTICES.md：{len(data)} B，{total} 条许可，"
          f"{inlined} 段全文内联，其余 {total - inlined} 段指向 licenses/ 副本")
    return 0


if __name__ == "__main__":
    sys.exit(main())
