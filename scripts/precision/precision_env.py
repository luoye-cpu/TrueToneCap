"""外部判据脚本的共用前置：外部工具探测、样张/产物目录解析、退出码约定。

为什么要这一层（2026-09-19 门禁审计）：
  判据脚本此前躺在被 gitignore 的 .workbuddy-ai/ 里，ffprobe 路径是硬编码常量，
  换台机器就"跑不起来"而门禁依旧全绿 —— 等于回归保护形同虚设。迁进仓库后
  工具路径必须【探测】而不是【假定】。

纪律（交接 §3 的容器层版本）：判据只用外部实现（ffprobe / ffmpeg / libavif 的 avifdec），
绝不 import 本仓库自己的容器解析代码 —— 仓库的 TIFF 解析器复刻了写入器的结构假设，
所以 8 轮门禁全绿而外部解码器拒读。

退出码约定（scripts/verify.ps1 的 [1.7/2] 步骤依赖它区分「失败」与「跳过」）：
  0  判据全部通过
  1  判据不通过 ⇒ 回归，必须修
  3  前置条件不满足（外部工具没装 / 样张没生成）⇒ 已打印"跳过 + 原因"，属 SKIP，
     【不是】通过；调用方必须把它显示成 SKIP 而不是绿。
脚本一律不允许"什么都不判还退 0"。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
from __future__ import annotations

import glob
import os
import shutil
import sys

# 判据输出里有 ✅/❌ 这类字符，而 Windows 控制台默认 cp936 会在 print 时抛
# UnicodeEncodeError —— 那会被门禁误判成"脚本失败"。统一换成 UTF-8（errors=replace 兜底）。
for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")
    except Exception:
        pass

EXIT_OK = 0
EXIT_FAIL = 1
EXIT_SKIP = 3

# Tools 的 --param-sweep / --container-audit / --wg-samples 仍把样张写在
# artifacts/precision/probe/<kind>（该路径在 src/TrueToneCap.Tools/FormatBench.cs 里是
# 相对 AppContext.BaseDirectory 算出来的，脚本侧改不动），所以样张按
# 【显式参数 > 环境变量 > artifacts/precision/<kind> > artifacts/precision/probe/<kind>】找。
SAMPLES_ENV = "TTC_PRECISION_SAMPLES"
ARTIFACTS_ENV = "TTC_ARTIFACT_DIR"
GENERATOR = {
    "sweep": "dotnet run --project src/TrueToneCap.Tools -c Release -- --param-sweep",
    "audit": "dotnet run --project src/TrueToneCap.Tools -c Release -- --container-audit",
    "wg": "dotnet run --project src/TrueToneCap.Tools -c Release -- --wg-samples",
    # 2026-09-19 §29 指标②④：漏一条，SKIP 信息里就会打印"(未知 kind)"，
    # 读的人无从知道该跑什么去补齐 —— 那等于把"没判"伪装成"环境问题"。
    "ramp": "dotnet run --project src/TrueToneCap.Tools -c Release -- --ramp-sweep",
    "cicp": "dotnet run --project src/TrueToneCap.Tools -c Release -- --cicp-sweep",
}


def repo_root() -> str:
    return os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


# ── 外部工具探测 ────────────────────────────────────────────────────────────
# 顺序：环境变量显式指定 > PATH > 常见安装位置。找不到就 SKIP（见 require_tools），
# 绝不"没工具就当通过"。
TOOL_ENV = {"ffprobe": "TTC_FFPROBE", "ffmpeg": "TTC_FFMPEG",
            "avifdec": "TTC_AVIFDEC", "avifenc": "TTC_AVIFENC",
            # 2026-09-22 色彩管线三判据新增：cjxl（JXL 标签判别）、
            # ultrahdr_app（GainMap 探针第 4 层的外部参考解码器）
            "cjxl": "TTC_CJXL", "ultrahdr_app": "TTC_ULTRAHDR_APP"}

# 展开式安装布局（winget/手动解压）目录名带版本号，所以要 glob 而不是常量。
_DIR_GLOBS = (
    r"C:\ffmpeg*\bin",
    r"C:\Windows\ffmpeg-*\bin",
    r"C:\Program Files\ffmpeg*\bin",
    r"C:\Program Files (x86)\ffmpeg*\bin",
    os.path.expandvars(r"%LOCALAPPDATA%\Microsoft\WinGet\Packages\Gyan.FFmpeg*\**\bin"),
    os.path.expandvars(r"%LOCALAPPDATA%\Programs\ffmpeg*\bin"),
    os.path.expandvars(r"%USERPROFILE%\scoop\apps\ffmpeg\current\bin"),
)
_DIR_CONST = (
    r"C:\ProgramData\chocolatey\bin",
)

# avifenc / avifdec 是 libavif 自家构建（构建配方见仓库内 scripts/build-avifenc.py）：
# avifenc 仓库内跟着 Core/Resources 走（产品运行时也用它），avifdec 只在构建树里。
_EXTRA_GLOBS = {
    "avifenc": (
        os.path.join(repo_root(), r"src\TrueToneCap.Core\Resources\avifenc.exe"),
        os.path.expandvars(r"%TEMP%\svt-avifenc-build\libavif\build\avifenc.exe"),
        os.path.expandvars(r"%TEMP%\*-avifenc-build\libavif\build\avifenc.exe"),
    ),
    "avifdec": (
        os.path.join(repo_root(), r"src\TrueToneCap.Core\Resources\avifdec.exe"),
        os.path.expandvars(r"%TEMP%\svt-avifenc-build\libavif\build\avifdec.exe"),
        os.path.expandvars(r"%TEMP%\*-avifenc-build\libavif\build\avifdec.exe"),
    ),
    # cjxl 仓库内自带一份，而且产品运行时用的就是它 ⇒ 判据与产品路径同源的才是同一版行为。
    "cjxl": (
        os.path.join(repo_root(), r"src\TrueToneCap.Core\Resources\cjxl.exe"),
    ),
    # ultrahdr_app（libultrahdr 的示例命令行）：仓库内那份 publish/PLAN/tools/ 是旧 CLI
    # （不认 -j/-z，实测解不出产物），只作兜底候选；能用的那版在【仓库之外】另一个项目的
    # 构建目录里。列成候选表而不是把绝对路径写死进 C#，是为了：换台机器只是"这一层探不到
    # ⇒ SKIP"，而不是"只有这台机器能跑"。谁被选中由调用方探测（见 tool_candidates 注释）。
    "ultrahdr_app": (
        r"C:\PLAN\ffmpegPictureUI\tools\src\libultrahdr\build\Release\ultrahdr_app.exe",
        os.path.join(repo_root(), r"publish\PLAN\tools\ultrahdr_app.exe"),
    ),
}


def _iter_candidates(tool: str):
    for pattern in _DIR_GLOBS:
        for d in sorted(glob.glob(pattern, recursive=True)):
            yield os.path.join(d, tool + ".exe")
    for d in _DIR_CONST:
        yield os.path.join(d, tool + ".exe")
    for p in _EXTRA_GLOBS.get(tool, ()):
        for hit in (glob.glob(p, recursive=True) or [p]):
            yield hit


def find_tool(tool: str) -> str | None:
    """PATH 优先，其次常见安装位置；返回绝对路径或 None。"""
    cands = tool_candidates(tool)
    return cands[0] if cands else None


# 这两件按【候选表先于 PATH】排，与 ffprobe 那一族相反，理由是"版本必须确定"：
#   · cjxl —— 判据要证的就是产品运行时那一份的行为，PATH 上恰好有个别的版本时，
#     拿它跑出来的结论不能安到产品头上；仓库内那份才是同源。
#   · ultrahdr_app —— 同名两代 CLI（旧版不认 -j/-z），更得挑有出处的那份。
_PATH_LAST = {"cjxl", "ultrahdr_app"}


def tool_candidates(tool: str) -> list[str]:
    """排出候选【存在】的路径，顺序即优先级，去重保序。

    优先级：环境变量显式指定 > （候选表 / PATH，见 _PATH_LAST）> 另一头。
    ⚠ "文件存在"不等于"能用"：调用方拿到的是【一串待探测的候选】，不保证第一个就能干活
      —— 例如 ultrahdr_app 的旧版会直接解不出产物，必须自己探测后再决定 SKIP 还是判红，
      绝不能因为"候选表命中"就假定这一层判过了。
    ⚠ 环境变量一旦设了就【只认它】：指错路径要显式落到"没探到"（SKIP），不静默回退到候选表
      —— 否则 -SkipPrecision 式的"我知道我在少判什么"会变成没人知道的静默降级。
    """
    override = os.environ.get(TOOL_ENV.get(tool, ""), "").strip()
    if override:
        return [override] if os.path.isfile(override) else []

    listed = [c for c in _iter_candidates(tool) if os.path.isfile(c)]
    which = shutil.which(tool)
    ordered = (listed + [which] if tool in _PATH_LAST else [which] + listed) if which else listed
    seen: set = set()
    out: list[str] = []
    for c in ordered:
        k = os.path.normcase(os.path.abspath(c))
        if k not in seen:
            seen.add(k)
            out.append(os.path.abspath(c))
    return out


def require_tools(*tools: str) -> dict:
    """探到全部工具则返回 {tool: path}；缺任何一个都打印跳过原因并以 EXIT_SKIP 退出。"""
    resolved, missing = {}, []
    for t in tools:
        p = find_tool(t)
        if p:
            resolved[t] = p
        else:
            missing.append(t)
    if missing:
        print("⏭ 跳过：%s 未找到外部工具 %s" % (os.path.basename(sys.argv[0]),
                                                "、".join(missing)))
        print("   已探测：PATH，以及 %s 等常见安装位置；也可用环境变量显式指定（%s）。"
              % (", ".join(_DIR_GLOBS[:2]), " / ".join(sorted(TOOL_ENV[t] for t in missing if t in TOOL_ENV))))
        print("   原因：这些判据【只信外部解码器】，没有它们就没有判据 —— 不能当通过。")
        sys.exit(EXIT_SKIP)
    return resolved


# ── 目录 ────────────────────────────────────────────────────────────────────
def artifact_dir(*parts: str) -> str:
    """脚本自己的产物（解码中间文件、基准样张、日志）落仓库内 artifacts/precision/。"""
    base = os.environ.get(ARTIFACTS_ENV, "").strip() or os.path.join(repo_root(), "artifacts", "precision")
    d = os.path.join(base, *parts)
    os.makedirs(d, exist_ok=True)
    return d


def opt(flag: str, argv=None):
    """取 --flag value / --flag=value 两种写法，没有则 None。"""
    argv = sys.argv[1:] if argv is None else argv
    for i, a in enumerate(argv):
        if a == flag and i + 1 < len(argv):
            return argv[i + 1]
        if a.startswith(flag + "="):
            return a.split("=", 1)[1]
    return None


def samples_dir(kind: str, explicit: str | None = None) -> str:
    """定位样张目录。

    显式指定（--samples / TTC_PRECISION_SAMPLES）时【不回退】—— 指到哪就判哪，
    目录空或不存在就 SKIP。否则 verify.ps1 传进来的新样张一旦缺失，脚本会静默改用
    .workbuddy-ai 里的陈旧样张，门禁判的是昨天的产物。
    """
    root = repo_root()
    env = os.environ.get(SAMPLES_ENV, "").strip()
    forced = explicit or env
    cands = []
    if forced:
        # 指到 probe/artifacts 根目录时也补上 kind 子目录，两种写法都能用
        cands.append(forced if os.path.isdir(os.path.join(forced, kind)) else forced)
    else:
        cands += [os.path.join(repo_root(), "artifacts", "precision", kind),
                  os.path.join(root, "artifacts", "precision", "probe", kind)]
    for d in cands:
        if d and os.path.isdir(d) and os.listdir(d):
            return os.path.abspath(d)
    print("⏭ 跳过：%s 找不到 %s 样张目录（找过：%s）"
          % (os.path.basename(sys.argv[0]), kind, " | ".join(c for c in cands if c)))
    print("   先生成样张：%s" % GENERATOR.get(kind, "(未知 kind)"))
    print("   或用 %s / --samples 指到实际位置。" % SAMPLES_ENV)
    sys.exit(EXIT_SKIP)
