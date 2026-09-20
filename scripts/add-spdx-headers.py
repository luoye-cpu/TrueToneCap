"""给仓库内的源码批量加 SPDX 标识头（Apache-2.0 要求"应用本许可的作品须附上声明"）。

为什么是脚本而不手改：涉及上百个文件，手改必然漏、且极易顺手弄坏行尾/BOM —— 本脚本按字节处理，
只在原内容前面插一段注释，改完自己复核"行尾与 BOM 未变、除新增行外内容逐字节相同"。

插入位置的讲究：
  · .cs / .xaml  → 插在最前面（.xaml 用 <!-- -->；本脚本默认不动 xaml，见 EXT_LANGS）。
  · .ps1         → 插在最前面，但若首行是 `#!` 则插在其后（PowerShell 的 shebang 约定）。
  · .py          → **必须插在模块 docstring 之后**：插到 docstring 前面会把它降级成一个
                   普通字符串表达式，模块 __doc__ 就没了。用 ast 定位 docstring 的结束行。

用法:
    py scripts/add-spdx-headers.py            # 实际写入
    py scripts/add-spdx-headers.py --dry-run  # 只报告将要改哪些文件
"""
from __future__ import annotations

import argparse
import ast
import os
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

SPDX = "SPDX-License-Identifier: Apache-2.0"
COPYRIGHT = "Copyright 2026 TrueToneCap contributors"

# 扩展名 → (行前缀, 行后缀)。只动源码；md/json/txt 靠 LICENSE + THIRD-PARTY-NOTICES 表意。
EXT_LANGS = {
    ".cs": ("// ", ""),
    ".ps1": ("# ", ""),
    ".py": ("# ", ""),
    ".c": ("// ", ""),
    ".h": ("// ", ""),
    ".hlsl": ("", ""),          # 特殊处理：见下（HLSL 用 // 同样可行）
}
EXT_LANGS[".hlsl"] = ("// ", "")


def tracked_files() -> list[str]:
    """只处理 git 跟踪的文件 —— 未跟踪的样张/临时件不该被盖章。"""
    out = subprocess.run(["git", "ls-files"], cwd=REPO, capture_output=True, text=True, check=True)
    files = []
    for line in out.stdout.splitlines():
        line = line.strip().replace("\\", "/")
        ext = os.path.splitext(line)[1].lower()
        if ext in EXT_LANGS:
            files.append(line)
    return files


def docstring_end_line(text: str) -> int:
    """返回模块 docstring 结束后的行号（0 表示没有 docstring）。"""
    try:
        mod = ast.parse(text)
    except SyntaxError:
        return 0
    body = mod.body
    if not body:
        return 0
    first = body[0]
    if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) \
            and isinstance(first.value.value, str):
        return first.end_lineno or 0
    return 0


def build_header(ext: str) -> list[str]:
    prefix, suffix = EXT_LANGS[ext]
    return [f"{prefix}{SPDX}{suffix}", f"{prefix}{COPYRIGHT}{suffix}"]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--dry-run", action="store_true")
    args = ap.parse_args()

    changed, skipped, already = [], 0, 0
    for rel in tracked_files():
        path = os.path.join(REPO, rel.replace("/", os.sep))
        raw = open(path, "rb").read()
        if SPDX.encode() in raw[:4000]:
            already += 1
            continue
        bom = raw.startswith(b"\xef\xbb\xbf")
        body = raw[3:] if bom else raw
        crlf = body.count(b"\r\n")
        nl = b"\r\n" if crlf else b"\n"
        try:
            text = body.decode("utf-8")
        except UnicodeDecodeError:
            skipped += 1
            continue

        ext = os.path.splitext(rel)[1].lower()
        lines = text.split("\r\n" if nl == b"\r\n" else "\n")
        at = 0
        if ext == ".py":
            at = docstring_end_line(text)
            if at:
                # ast 的行号按"通用换行"数，而本仓库存在 CRLF/LF 混用的文件：
                # 两套行号一旦错位，头注释就会插进 docstring 中间，把文档字符串切成两半。
                # 于是插进去之前先证明"我数的这一行确实是 docstring 的收尾行"，证不了就不动这个文件。
                if at > len(lines) or '"""' not in lines[at - 1] and "'''" not in lines[at - 1]:
                    print(f"[SKIP] docstring 结束行与换行计数不一致，放弃: {rel}")
                    skipped += 1
                    continue
        elif lines and lines[0].startswith("#!"):
            at = 1
        header = build_header(ext)
        new_lines = lines[:at] + header + lines[at:]
        new_text = ("\r\n" if nl == b"\r\n" else "\n").join(new_lines)
        if args.dry_run:
            changed.append(rel)
            continue
        data = new_text.encode("utf-8")
        if bom:
            data = b"\xef\xbb\xbf" + data
        # 复核（两条都要过）：
        #   ① 往返：把新增的两行摘掉，必须与原始文本逐字符相同（切片没有吃掉任何内容）
        #   ② 体积：多出来的字节数必须恰好等于"两行 + 两个行尾"，即除插入外无任何改写
        # 原先这里比的是 `\r\n` 计数不变 —— 那是错的：往 CRLF 文件里插两行本来就会多两个 CRLF，
        # 结果 CRLF 文件全被误判成"行尾变化"而跳过，只有 LF 文件被写。2026-09-20 实测踩到。
        probe = data[3:] if bom else data
        probe_text = probe.decode("utf-8")
        back = new_lines[:at] + new_lines[at + len(header):]
        grew = len(probe) - len(body)
        expect = sum(len(h.encode("utf-8")) + len(nl) for h in header)
        if ("\r\n" if nl == b"\r\n" else "\n").join(back) != text or grew != expect:
            print(f"[SKIP] 往返/体积校验不通过（+{grew} B，应为 +{expect} B）: {rel}")
            skipped += 1
            continue
        open(path, "wb").write(data)
        changed.append(rel)

    verb = "将写入" if args.dry_run else "已写入"
    print(f"{verb} {len(changed)} 个文件；已有 SPDX {already} 个；跳过 {skipped} 个")
    for rel in changed[:12]:
        print(f"   · {rel}")
    if len(changed) > 12:
        print(f"   … 其余 {len(changed) - 12} 个略")
    return 0


if __name__ == "__main__":
    sys.exit(main())
