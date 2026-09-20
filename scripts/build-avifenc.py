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


def main():
    args = list(sys.argv[1:])
    if "--workdir" in args:
        i = args.index("--workdir")
        SRC = args[i + 1]
        del args[i:i + 2]
    build = os.path.join(SRC, "build")
    if not os.path.isfile(os.path.join(SRC, "CMakeLists.txt")):
        print("[FATAL] no libavif checkout at %s\n"
              "        clone libavif v1.4.2 there first (see the module docstring) "
              "or pass --workdir" % SRC)
        return 1
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    open(LOG, "w").close()
    env = msvc_env(SRC)
    cl = shutil.which("cl", path=env["PATH"])
    if cl is None:
        raise SystemExit("[FATAL] cl.exe not found even inside the MSVC PATH")
    print("[OK] cl reachable: %s" % cl)

    if "--build-only" not in args:
        if run(CONFIGURE, env, SRC, LOG) != 0:
            print("[FAIL] configure -- see %s" % LOG)
            return 1
        print("[OK] configure")
    if "--configure-only" in args:
        return 0
    if run(["cmake", "--build", "build", "--target", "avifenc", "--parallel"], env, SRC, LOG) != 0:
        print("[FAIL] build -- see %s" % LOG)
        return 1
    print("[OK] build")

    for cand in (os.path.join(build, "avifenc.exe"), os.path.join(build, "apps", "avifenc.exe")):
        if os.path.exists(cand):
            print("[OK] avifenc at %s (%d bytes)" % (cand, os.path.getsize(cand)))
            subprocess.call([cand, "--version"], env=env)
            print("\n后续（人工确认，见文件头）：复制进 Resources/ → 回填 tools-manifest.json "
                  "→ 删除 bin 下的 native/avifenc.exe 陈旧副本 → 重跑 --avif-caps")
            return 0
    print("[FAIL] avifenc.exe not produced")
    return 1


if __name__ == "__main__":
    sys.exit(main())
