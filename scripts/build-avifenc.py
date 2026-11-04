"""Build the avifenc.exe that ships in src/TrueToneCap.Core/Resources/.

Why this file is in version control
----------------------------------
The shipped avifenc is **no longer an upstream download**. libavif's official release
binaries are aom-only, so `--codec svt` never reached SVT-AV1 and the SvtAv1 option could
not be measured at all (handoff 2026-09-18 §18). Building it here makes SVT real, which
also means the release is only reproducible if the recipe is tracked: Publish.ps1's [0/6]
step pins the exe by SHA256 in Resources/tools-manifest.json, and a self-built binary has
no upstream URL to point at.

What the binary must contain (each of these cost one failed run — do not "simplify"):
  AVIF_ZLIBPNG=LOCAL  the software AVIF path feeds **PNG** (stdin `--input-format png`,
                      and EncodeFile's 16-bit intermediate). With this OFF avifenc cannot
                      read its own input format and the whole software path breaks.
                      libavif additionally forces AVIF_JPEG=LOCAL once AVIF_BUILD_APPS=ON.
  CMAKE_BUILD_TYPE=Release
                      without it the tree is Debug: the asm targets get `-gcv8`, and
                      NASM 3.02 segfaults (0xC0000005) on SVT's SSE2 asm. A Debug codec lib
                      in a shipped encoder would also be a quiet throughput regression.

Pinned revisions (libavif's own FetchContent tags, cloned during the build)
--------------------------------------------------------------------------
  libavif v1.4.2 · SVT-AV1 v4.1.0 (libavif's pin — **not** the standalone v4.2.0 tree)
  libaom 3.14.1 · zlib v1.3.2 · libpng v1.6.58 · libjpeg-turbo (libavif's pin)

Requires: VS 18 / MSVC 14.51 x64 (vcvarsall), CMake >= 3.25 + Ninja, NASM, git in PATH
(FetchContent clones), perl on PATH (aom's configure runs it).

⚠ 2026-11-04 补齐前置：本机 ninja 不在 PATH（VS 自带一份）、perl 也不在 PATH
（可借用隔壁 ffmpegPictureUI 打包的 exiftool perl）。脚本现在会自己找它们，
找不到就在 configure 之前【明确报错】，而不是让 cmake 抛一句 "CMAKE_MAKE_PROGRAM not set"。

Usage
-----
    git clone --branch v1.4.2 --depth 1 https://github.com/AOMediaCodec/libavif.git <workdir>/libavif
    py scripts/build-avifenc.py [--workdir <workdir>/libavif] [--configure-only|--build-only]

Then by hand — deliberately not automated, shipping a binary deserves a human step:
    copy <workdir>/libavif/build/avifenc.exe src/TrueToneCap.Core/Resources/avifenc.exe
    # recompute and backfill sha256 / sizeBytes in Resources/tools-manifest.json
    # drop stale extractions, otherwise tests keep running the OLD binary:
    #   find src -path '*/bin/*' -name avifenc.exe -delete
    dotnet run --project src/TrueToneCap.Tools -c Release -- --avif-caps --ffprobe

Why Python and not a .cmd: MSYS bash rewrites argv tokens (`nul` -> `/dev/null`) and strips
CRLF from generated batch files, which made `call vcvars64.bat` fail while still printing
"[OK]" — every compile then died on missing stddef.h. We instead harvest the env block from
a batch file that Python writes itself, and hand that env to cmake.
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
import shutil
import subprocess
import sys

VCVARSALL = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"
DEFAULT_SRC = os.path.join(os.environ["TEMP"], "svt-avifenc-build", "libavif")
SRC = os.environ.get("TTC_LIBAVIF_SRC", DEFAULT_SRC)
LOG = os.environ.get("TTC_AVIFENC_LOG",
                     os.path.join(os.getcwd(), ".workbuddy-ai", "rebuild-avifenc.log"))

CONFIGURE = [
    "cmake", "-S", ".", "-B", "build",
    "-DCMAKE_BUILD_TYPE=Release",
    "-DAVIF_CODEC_AOM=LOCAL",
    "-DAVIF_CODEC_SVT=LOCAL",
    "-DAVIF_ZLIBPNG=LOCAL",
    "-DAVIF_JPEG=LOCAL",
    "-DAVIF_BUILD_APPS=ON",
    # ═══ 2026-11-04：LIBSHARPYUV / LIBYUV 两个都【刻意保持 OFF】—— 试过，实测否决 ═══
    # 背景：本应用是截图工具，内容以文字/UI 为主；实测 4:2:0 在彩色文字边缘的误差
    # 是 4:4:4 的数倍（台账 §22）。`--sharpyuv` 看起来是对症开关，于是专门重构建验证。
    #
    # ⚠ 结论：**不开**。实测数据（彩色文字/UI，CRF 20，逐像素比对原始 RGBA）：
    #     配置            体积      平均|Δ|   边缘|Δ|   Y误差  Cb误差  Cr误差
    #     420 普通       28186B     4.60      5.75     1.56   3.46    3.20
    #     420 +sharpyuv  47823B     7.33      9.25     2.02   5.23    5.58
    #     444            37734B     2.54      3.22     1.16   1.81    1.56
    #   sharpyuv 让【亮度和色度误差同时变大】（边缘劣化 61%），体积还涨 70%，
    #   而且比 444 更差又更大 ⇒ 没有任何启用理由。它优化的是"主观边缘锐度"，
    #   代价是色度精度，在本仓的逐像素度量下是净损失。
    #   真正有效的解法是把默认色度改 4:4:4（体积在典型截图上几乎不变）。
    #
    # ⚠ 另外记一个坑（下次别再踩）：`--sharpyuv` 的编译开关是 **AVIF_LIBSHARPYUV**
    #   （libwebp 的 libsharpyuv），与 **AVIF_LIBYUV**（libyuv 的缩放/快速路径）是两个
    #   独立选项。只开 LIBYUV 时 `avifenc --version` 同样印 `libyuv : available`，
    #   但 `--sharpyuv` 一用就报 "Conversion to YUV failed" —— 隔壁 ffmpegPictureUI
    #   那枚二进制正是这种状态。判断 sharpyuv 能不能用【只能靠真实编码对比】。
    "-DAVIF_LIBSHARPYUV=OFF",
    "-DAVIF_LIBYUV=OFF",
    "-DAVIF_LIBXML2=OFF",
    "-DAVIF_ENABLE_WERROR=OFF",
    "-DBUILD_SHARED_LIBS=OFF",
    "-G", "Ninja",
]


def msvc_env(src):
    # A temp .bat is written by Python (correct CRLF, no MSYS argv rewriting) because
    # passing `call "C:\Program Files\..."` as a subprocess *list* makes Python emit
    # \" escapes that cmd.exe does not understand.
    bat = os.path.join(os.environ["TEMP"], "ttc-msvc-env.bat")
    with open(bat, "w", encoding="ascii", newline="\r\n") as f:
        f.write("@echo off\r\ncall \"%s\" x64\r\nset\r\n" % VCVARSALL)
    p = subprocess.run(["cmd", "/d", "/c", bat], capture_output=True, text=True, cwd=src)
    # Keys are upper-cased on purpose: cmd's `set` prints `Path=` while os.environ has
    # `PATH=`; the shell-spawned MSYS PATH must be *replaced*, not shadowed, or cl.exe
    # stays unreachable and every compile fails on missing stddef.h.
    env = {k.upper(): v for k, v in os.environ.items()}
    for line in p.stdout.splitlines():
        k, sep, v = line.partition("=")
        if sep and k:
            env[k.upper()] = v
    if not env.get("INCLUDE"):
        print(p.stdout[-2000:], p.stderr[-2000:])
        raise SystemExit("[FATAL] vcvarsall x64 produced no INCLUDE (see the dump above)")
    # vcvars' PATH has cl/link/nasm but not git or cmake; FetchContent needs git
    # (libjpeg/libpng get cloned during the build), so keep the original PATH behind it.
    env["PATH"] = env.get("PATH", "") + os.pathsep + os.environ.get("PATH", "")
    print("[OK] MSVC env: %s" % env["INCLUDE"].split(";")[0])
    return env


def run(cmd, env, src, log):
    print(">>> " + " ".join(cmd), flush=True)
    with open(log, "a", encoding="utf-8", errors="replace") as f:
        return subprocess.call(cmd, cwd=src, env=env, stdout=f, stderr=subprocess.STDOUT)


def find_tool(name, extra_candidates=()):
    """Locate a build tool: PATH first, then a list of known absolute candidates.

    Why this exists: on this machine ninja and perl are *installed* but not on PATH
    (ninja ships inside Visual Studio; perl ships with the neighbouring project's
    exiftool bundle). cmake reports a confusing "CMAKE_MAKE_PROGRAM not set" for the
    former and aom's configure dies mid-build for the latter, so we resolve them up
    front and fail loudly instead.
    """
    hit = shutil.which(name)
    if hit:
        return hit
    for cand in extra_candidates:
        if cand and os.path.isfile(cand):
            return cand
    return None


def main():
    args = list(sys.argv[1:])
    # ⚠ 2026-11-04 修：这里原来直接 `SRC = args[i+1]`，一旦让它赋值，Python 就把 SRC
    #   当成【整个函数的局部变量】⇒ 不传 --workdir 时 os.path.join(SRC, ...) 会抛
    #   UnboundLocalError（而不是回落到模块级的 DEFAULT_SRC）。改为局部别名 src，
    #   模块级 SRC 保持只读。
    src = SRC
    if "--workdir" in args:
        i = args.index("--workdir")
        src = args[i + 1]
        del args[i:i + 2]
    build = os.path.join(src, "build")
    if not os.path.isfile(os.path.join(src, "CMakeLists.txt")):
        print("[FATAL] no libavif checkout at %s\n"
              "        clone libavif v1.4.2 there first (see the module docstring) "
              "or pass --workdir" % src)
        return 1
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    open(LOG, "w").close()
    env = msvc_env(src)
    cl = shutil.which("cl", path=env["PATH"])
    if cl is None:
        raise SystemExit("[FATAL] cl.exe not found even inside the MSVC PATH")
    print("[OK] cl reachable: %s" % cl)

    # ── ninja / perl：两个"装了但不在 PATH"的工具，显式解析并注入 env ──
    # ninja：VS 自带的 CMake/Ninja 目录
    ninja = find_tool("ninja", (
        r"C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE"
        r"\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe",
    ))
    if ninja is None:
        raise SystemExit(
            "[FATAL] ninja not found. Install Ninja or edit this script to use the "
            "Visual Studio generator (-G \"Visual Studio 18 2026\" -A x64).")
    # CMAKE_MAKE_PROGRAM 让 cmake 直接用这份，不必把它塞进 PATH
    CONFIGURE.extend([f"-DCMAKE_MAKE_PROGRAM={ninja}"])
    print("[OK] ninja: %s" % ninja)

    # perl：aom 的 cmake/rtcd.pl 要它，而且**必须是完整发行版**。
    # ⚠ 2026-11-04 实测：隔壁 exiftool 打包的 perl 是精简版，@INC 只有它自己的 lib，
    #   缺 Getopt/Long.pm ⇒ aom 的 rtcd.pl 在 '#line 14' 直接 BEGIN failed，
    #   表现为一堆 "Can't locate Getopt/Long.pm in @INC"，但 configure 阶段看不出来。
    #   所以优先用 Strawberry Perl；找不到才退到精简版并**提前警告**（不要等编到一半才炸）。
    perl_full = r"C:\Strawberry\perl\bin\perl.exe"
    perl = find_tool("perl", (
        perl_full,
        r"C:\PLAN\ffmpegPictureUI\publish\PLAN\exiftool\exiftool_files\perl.exe",
    ))
    if perl is None:
        raise SystemExit("[FATAL] perl not found (aom's configure needs it)")
    if os.path.normcase(os.path.abspath(perl)) != os.path.normcase(os.path.abspath(perl_full)):
        # 精简版 perl 几乎一定缺 Getopt::Long；给出可执行的下一步，而不是让构建半途失败
        probe = subprocess.run([perl, "-MGetopt::Long", "-e", "1"],
                               capture_output=True, text=True)
        if probe.returncode != 0:
            raise SystemExit(
                "[FATAL] 找到的 perl 缺 Getopt::Long（%s）。\n"
                "        aom 的 cmake/rtcd.pl 需要它。请安装 Strawberry Perl（预期路径 %s）\n"
                "        或把完整版 perl 放到 PATH。" % (perl, perl_full))
    env["PATH"] = os.path.dirname(perl) + os.pathsep + env["PATH"]
    print("[OK] perl: %s" % perl)

    # nasm：SVT-AV1 / libaom 的汇编步骤要它（缺了会在编译期报找不到 nasm）
    nasm = find_tool("nasm", (r"C:\Program Files\NASM\nasm.exe",))
    if nasm is None:
        print("[WARN] nasm not found -- the codec asm targets may fail to build")
    else:
        env["PATH"] = os.path.dirname(nasm) + os.pathsep + env["PATH"]
        print("[OK] nasm: %s" % nasm)

    if "--build-only" not in args:
        if run(CONFIGURE, env, src, LOG) != 0:
            print("[FAIL] configure -- see %s" % LOG)
            return 1
        print("[OK] configure")
    if "--configure-only" in args:
        return 0
    if run(["cmake", "--build", "build", "--target", "avifenc", "--parallel"], env, src, LOG) != 0:
        print("[FAIL] build -- see %s" % LOG)
        return 1
    print("[OK] build")

    for cand in (os.path.join(build, "avifenc.exe"), os.path.join(build, "apps", "avifenc.exe")):
        if os.path.exists(cand):
            print("[OK] avifenc at %s (%d bytes)" % (cand, os.path.getsize(cand)))
            ver = subprocess.run([cand, "--version"], env=env,
                                 capture_output=True, text=True).stdout
            print(ver.strip())
            # ═══ 自检：不要求 sharpyuv（2026-11-04 实测否决，两个开关刻意 OFF）═══
            # 保留一条"如果将来有人重新打开它，必须验证真的生效"的提醒，但不作为失败条件。
            if "libyuv : available" in ver:
                print("[WARN] 本二进制带 libyuv（AVIF_LIBYUV 被改成非 OFF）。\n"
                      "       注意 `libyuv : available` 不代表 `--sharpyuv` 能用 —— 那要 AVIF_LIBSHARPYUV。\n"
                      "       台账 §23 记录了 sharpyuv 实测为负收益，默认不该开。")
            print("\n后续（人工确认，见文件头）：复制进 Resources/ → 回填 tools-manifest.json "
                  "→ 删除 bin 下的 native/avifenc.exe 陈旧副本 → 重跑 --avif-caps")
            print("⚠ 发布前还要用真实截图比对画质（本脚本只验『能改变产物』，不验『改得更好』）。")
            return 0
    print("[FAIL] avifenc.exe not produced")
    return 1
