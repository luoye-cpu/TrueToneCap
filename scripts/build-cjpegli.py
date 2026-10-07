"""Build the cjpegli.exe that ships in src/TrueToneCap.Core/Resources/.

Why this file exists (2026-10-07)
---------------------------------
libjxl **v0.12.0 removed the entire jpegli codebase** — its CHANGELOG states:

    - The `jpegli` codebase has been removed as it is now maintained as a separate
      project at google/jpegli. (#4657)

Consequences, all verified by downloading the actual release archives:

  * libjxl v0.11.2's `jxl-x64-windows-static.zip` **contains cjpegli.exe** (5.13 MB).
  * libjxl v0.12.0's `jxl-x64-windows-static.zip` **does not** — it ships only cjxl.exe
    (this holds for the non-static zip too).
  * google/jpegli publishes **no GitHub Releases at all** → there is no prebuilt
    Windows cjpegli to download from anywhere.

This project depends on cjpegli for the JPEG LI output format (52 references across
13 files, covered by `--selftest`), so upgrading cjxl to 0.12.0 without a matching
cjpegli would ship a half-upgraded JXL pair. Hence: build cjpegli from source.

Why Python and not a .cmd
-------------------------
Same reason as scripts/build-avifenc.py: MSYS bash rewrites argv tokens and strips CRLF
from generated batch files, which made `call vcvars64.bat` fail while still printing
"[OK]" — every compile then died on missing stddef.h. We harvest the env block from a
batch file that Python writes itself, and hand that env to cmake.

Submodules are required
-----------------------
google/jpegli uses git submodules (third_party/{highway,libjpeg-turbo,libpng,zlib,
skcms,sjpeg,lcms}). A plain `git clone` leaves those directories EMPTY and the build
fails. Per its BUILDING.md:

    git clone --recursive --shallow-submodules https://github.com/google/jpegli.git
    # or, after a plain clone:
    git submodule update --init --recursive --depth 1 --recommend-shallow

⚠️  Dependency-drift warning (measured, not assumed)
----------------------------------------------------
The pinned submodules are what the *build* links, and two of them are older than the
project's own hardening baseline:

    third_party/libpng       PNG_LIBPNG_VER_STRING "1.6.47"   (project ships 1.6.59)
    third_party/libjpeg-turbo  VERSION 2.1.5.1                (project ships 3.1.2)

libjpeg-turbo 2.x carries known CVEs (all CVEs in that project stop at 2.1.90; 3.x has
none). Before shipping a binary from this recipe, verify with the string-scan step below
which versions actually ended up linked, and record them in
`src/TrueToneCap.Core/Resources/tools-manifest.json` — the same discipline that caught
the libpng 1.6.58 exposure in avifenc.exe. If the scan shows 2.x libjpeg-turbo inside
cjpegli, that is a regression against the currently shipped 3.1.2 build and must be
resolved before the binary is adopted.

Requires: VS 18 / MSVC x64 (vcvarsall), CMake + Ninja, git (submodules), NASM.

Usage
-----
    git clone --depth 1 https://github.com/google/jpegli.git <workdir>
    cd <workdir> && git submodule update --init --recursive --depth 1 --recommend-shallow
    py scripts/build-cjpegli.py --workdir <workdir>

Then by hand — deliberately not automated, shipping a binary deserves a human step:
    copy <workdir>/build/tools/cjpegli.exe src/TrueToneCap.Core/Resources/cjpegli.exe
    # string-scan it for libpng/libjpeg-turbo versions (see the warning above)
    # recompute and backfill sha256 / sizeBytes in Resources/tools-manifest.json
    # drop stale extractions, otherwise tests keep running the OLD binary:
    #   find src -path '*/bin/*' -name cjpegli.exe -delete
    dotnet run --project src/TrueToneCap.Tools -c Release -- --self-test-or-gainmap-probe
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import argparse
import os
import subprocess
import sys

VCVARSALL = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"

CONFIGURE = [
    "cmake", "-S", ".", "-B", "build",
    "-G", "Ninja",
    "-DCMAKE_BUILD_TYPE=Release",
    "-DBUILD_SHARED_LIBS=OFF",
    "-DJPEGLI_ENABLE_TOOLS=ON",
    # ⚠ 测试开关叫 BUILD_TESTING（CTest 标准变量），**不是** JPEGLI_ENABLE_TESTING ——
    #   后者不存在，写了会被 CMake 静默忽略，然后 lib/jpegli_tests.cmake 因找不到
    #   PNG/JPEG/ZLIB 而直接 configure 失败（实测踩过）。
    "-DBUILD_TESTING=OFF",
    "-DJPEGLI_ENABLE_BENCHMARK=OFF",
    "-DJPEGLI_ENABLE_SJPEG=OFF",
    "-DJPEGLI_ENABLE_JPEGLI_LIBJPEG=OFF",
]


def msvc_env(src):
    """Harvest the vcvarsall x64 environment block (same technique as build-avifenc.py)."""
    bat = os.path.join(os.environ["TEMP"], "ttc-msvc-env.bat")
    with open(bat, "w", encoding="ascii", newline="\r\n") as f:
        f.write("@echo off\r\ncall \"%s\" x64\r\nset\r\n" % VCVARSALL)
    p = subprocess.run(["cmd", "/d", "/c", bat], capture_output=True, text=True, cwd=src)
    # Keys upper-cased on purpose: cmd's `set` prints `Path=` while os.environ has `PATH=`.
    # The MSYS PATH must be *replaced*, not shadowed, or cl.exe stays unreachable.
    env = {k.upper(): v for k, v in os.environ.items()}
    for line in p.stdout.splitlines():
        k, sep, v = line.partition("=")
        if sep and k:
            env[k.upper()] = v
    if not env.get("INCLUDE"):
        print(p.stdout[-2000:], p.stderr[-2000:])
        raise SystemExit("[FATAL] vcvarsall x64 produced no INCLUDE (see the dump above)")
    return env


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workdir", default=os.path.join(os.environ["TEMP"], "jpegli-src"),
                    help="path to the google/jpegli checkout (submodules must be initialized)")
    ap.add_argument("--configure-only", action="store_true")
    ap.add_argument("--build-only", action="store_true")
    args = ap.parse_args()

    src = os.path.abspath(args.workdir)
    if not os.path.isfile(os.path.join(src, "tools", "cjpegli.cc")):
        raise SystemExit("[FATAL] %s does not look like a google/jpegli checkout "
                         "(tools/cjpegli.cc missing)" % src)

    # Submodules are load-bearing; catch an uninitialized clone up front with a clear message.
    empty = [m for m in ("highway", "libjpeg-turbo", "libpng", "zlib", "skcms")
             if not os.listdir(os.path.join(src, "third_party", m)) if os.path.isdir(
                 os.path.join(src, "third_party", m))]
    if empty:
        raise SystemExit(
            "[FATAL] submodules not initialized: %s\n"
            "        run: git submodule update --init --recursive --depth 1 --recommend-shallow"
            % ", ".join(empty))

    env = msvc_env(src)
    env["PATH"] = env.get("PATH", "") + os.pathsep + r"C:\Program Files\NASM"
    print("[OK] MSVC env: %s" % env["INCLUDE"].split(os.pathsep)[0])

    if not args.build_only:
        print(">>> " + " ".join(CONFIGURE))
        if subprocess.call(CONFIGURE, env=env, cwd=src) != 0:
            raise SystemExit("[FATAL] configure failed")
        print("[OK] configure")

    if not args.configure_only:
        print(">>> cmake --build build --target cjpegli --parallel")
        if subprocess.call(["cmake", "--build", "build", "--target", "cjpegli", "--parallel"],
                           env=env, cwd=src) != 0:
            raise SystemExit("[FATAL] build failed")

        exe = os.path.join(src, "build", "tools", "cjpegli.exe")
        if not os.path.isfile(exe):
            raise SystemExit("[FATAL] build reported success but %s is missing" % exe)
        print("[OK] cjpegli at %s (%d bytes)" % (exe, os.path.getsize(exe)))
        print()
        print("⚠️  NEXT (do not skip): string-scan the binary for libpng / libjpeg-turbo versions.")
        print("    jpegli's pinned submodules are libpng 1.6.47 / libjpeg-turbo 2.1.5.1 —")
        print("    both older than what this project already ships. If the scan shows those,")
        print("    DO NOT adopt the binary until the drift is resolved (see the module docstring).")
        print()
        print("后续（人工确认，见文件头）：复制进 Resources/ → 字符串扫描核对内嵌版本 →")
        print("       回填 tools-manifest.json → 删除 bin 下的 cjpegli.exe 陈旧副本 → 重跑回归")


if __name__ == "__main__":
    main()
