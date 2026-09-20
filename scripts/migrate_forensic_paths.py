"""一次性迁移：把源码注释里指向被 gitignore 目录 `.workbuddy-ai/` 的取证路径，重指到版本控制内的路径。

为什么必须做：这些注释是**证据的出处**（例如 NvEncoderNative 里那批硬编码结构体偏移写着
"由 cl.exe + sizeof()/offsetof() 实测，探针源码见 .workbuddy-ai/nvoff.c"）。而 .workbuddy-ai/ 在
.gitignore 第 151 行 ⇒ 仓库外。后果与本轮 §30 遇到的完全同类：一个"看起来有出处"的结论其实无法复验。

跑法：py -3 scripts/migrate_forensic_paths.py            # 只报告
      py -3 scripts/migrate_forensic_paths.py --apply    # 落盘
      （2026-09-20：本脚本原先躺在被 gitignore 的 artifacts/ 里 ⇒ "有出处但别人拿不到"，
       正是它自己要修的那类问题，已搬进 scripts/。）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import pathlib
import sys

APPLY = "--apply" in sys.argv

# 取证资产先要真的在版本控制里 —— 这两条是"先复制再改引用"的前提，缺一条就不该改注释。
COPY_FIRST = [
    (".workbuddy-ai/nvoff.c", "scripts/probes/nvoff.c"),
    (".workbuddy-ai/avif_bench.py", "scripts/precision/avif_bench.py"),
]

# 注释/字符串里的路径重指（顺序无关，都是整段唯一子串）
REPOINT = [
    ('.workbuddy-ai/nvoff.c', 'scripts/probes/nvoff.c'),
    ('.workbuddy-ai/avif_bench.py', 'scripts/precision/avif_bench.py'),
    ('.workbuddy-ai/svt_caps_matrix.py', 'scripts/precision/svt_caps_matrix.py'),
    ('.workbuddy-ai/svt_preset_sweep.py', 'scripts/precision/svt_preset_sweep.py'),
    ('.workbuddy-ai/ipma_decode.py', 'scripts/precision/ipma_decode.py'),
    ('.workbuddy-ai/audit_roundtrip.py', 'scripts/precision/audit_roundtrip.py'),
    ('.workbuddy-ai/build_avifenc.py', 'scripts/build-avifenc.py'),
    # 探针**产物输出目录**：留在 gitignore 里是对的，但要跟门禁样张同一处（artifacts/），
    # 而不是寄居在第三方 agent 的私有目录下。
    ('".workbuddy-ai", "probe"', '"artifacts", "precision", "probe"'),
    ('.workbuddy-ai/wg_compat_check.py', 'scripts/precision/wg_compat_check.py'),
    ('.workbuddy-ai/param_sweep_check.py', 'scripts/precision/param_sweep_check.py'),
    ('.workbuddy-ai/avif_depth_bench.py', 'scripts/precision/avif_depth_bench.py'),
    # 陈旧的"下一步"提示（指向已经不存在的跑法）
    ('py .workbuddy-ai/', 'py scripts/precision/'),
    # ↑↑ 注释类；下面是**功能性**耦合，必须与 FormatBench 的输出目录同批改，否则门禁断桥：
    # verify.ps1 里硬编码了"样张落点"，precision_env.py 的兜底候选也列着旧目录。
    (r'".workbuddy-ai\probe\sweep"', r'"artifacts\precision\probe\sweep"'),
    ('算死的 .workbuddy-ai/probe/sweep', '算死的 artifacts/precision/probe/sweep'),
    ('（脚本侧改不了，也不该为此改产品代码），所以先复制进',
     '（2026-09-19 起落点已搬进仓库内的 artifacts/ 下；复制这一步仍保留 ⇒ 判的始终是当场产出的那一份），所以先复制进'),
    # precision_env 的候选顺序说明（注释）。⚠ 不要再单独插 cands 条目：
    # 上面那条 `".workbuddy-ai", "probe"` 规则已经把兜底候选本身搬走了，再插一份就是重复候选。
    ('.workbuddy-ai/probe/<kind>', 'artifacts/precision/probe/<kind>'),
]

ROOT = pathlib.Path(".")
TARGETS = [
    "src/TrueToneCap.Core/Encoding/AvifEncoderCapabilities.cs",
    "src/TrueToneCap.Core/Encoding/FormatEncoders.cs",
    "src/TrueToneCap.Core/Encoding/NativeAvifEncoder.cs",
    "src/TrueToneCap.Core/Encoding/NvEncoderNative.cs",
    "src/TrueToneCap.Test/CorePipelineTests.cs",
    "src/TrueToneCap.Tools/FormatBench.cs",
    # 功能性耦合（不是注释）：FormatBench 的输出目录一改，这两处必须同批改，否则 [1.7/2] 断桥
    "scripts/verify.ps1",
    "scripts/precision/precision_env.py",
]

# 复制取证资产（保留原文件：.workbuddy-ai/ 不是我建的目录，不擅自清）
for src, dst in COPY_FIRST:
    s, d = ROOT / src, ROOT / dst
    if not s.is_file():
        print(f"!! 源缺失 {src} ⇒ 与之相关的重指不能做")
        continue
    if d.exists() and d.read_bytes() == s.read_bytes():
        print(f"=  {d} 已是最新")
        continue
    text = s.read_text(encoding="utf-8", errors="replace")
    if dst.endswith("nvoff.c"):
        header = (
            "// scripts/probes/nvoff.c\n"
            "//\n"
            "// 【为什么留这个文件】NvEncoderNative.cs 里那批硬编码的 struct 尺寸与函数表索引，\n"
            "// 出处就是这里：凭注释/文档猜偏移错过一次（错得离谱，见 NvEncoderNative.cs:43 那条注释），\n"
            "// 之后一律以 MSVC 实测的 sizeof()/offsetof() 为准。\n"
            "//\n"
            "// 【怎么复跑】需要 Windows SDK 的 nvEncodeAPI.h（随 NVIDIA Video Codec SDK 分发）：\n"
            "//   cl /I <path-to>/Interface scripts\\probes\\nvoff.c && nvoff.exe > nvoff.txt\n"
            "// 然后把输出与 NvEncoderNative.cs 里的常量逐条对，不要手改数字。\n\n"
        )
        text = header + text
    if dst.endswith("avif_bench.py"):
        # 基准脚本的产物目录跟着一起离开第三方目录（它不进门禁，只是可复跑的基准）
        text = text.replace(
            r'OUT = r"C:\PLAN\TrueToneCap\.workbuddy-ai\bench"',
            'OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), "artifacts", "bench")',
        )
        if ".workbuddy-ai" in text:
            print("!! avif_bench.py 里仍有 .workbuddy-ai 残留，检查 OUT 是否替换成功")
    if APPLY:
        d.parent.mkdir(parents=True, exist_ok=True)
        d.write_text(text, encoding="utf-8", newline="")
    print(f"{'→' if APPLY else '·'} 复制 {src} ⇒ {dst}")

print()
total = 0
for rel in TARGETS:
    p = ROOT / rel
    if not p.is_file():
        print(f"!! 文件不存在 {rel}")
        continue
    text = p.read_text(encoding="utf-8", errors="replace")
    orig = text
    hits = []
    for old, new in REPOINT:
        n = text.count(old)
        if n:
            text = text.replace(old, new)
            hits.append(f"{old} ×{n}")
    if text != orig:
        left = text.count(".workbuddy-ai")
        print(f"改 {rel}: {'; '.join(hits)}  （剩余 .workbuddy-ai 提及 {left} 处）")
        total += sum(int(h.split("×")[1]) for h in hits)
        if APPLY:
            p.write_text(text, encoding="utf-8", newline="")
    else:
        print(f"   {rel}: 无匹配")
print(f"\n合计替换 {total} 处；{'已落盘' if APPLY else '未落盘（加 --apply）'}")
