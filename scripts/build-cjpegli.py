# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
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

2026-11-12 起同时产出两件（shim 包装层在 scripts/jpegli-shim/，构建时复制到
<workdir>/ttc-shim/，单棵源码树、与 cjpegli.exe 完全一致的开关）：

    <workdir>/build-shim/jpegli_build/tools/cjpegli.exe   ← exe 回退路径
    <workdir>/build-shim/bin/ttc_jpegli.dll               ← P/Invoke 主路径

构建末尾强制运行逐字节对照门禁（ttc_selftest）：shim 与 cjpegli.exe 在
12 个参数组合下必须产出逐字节相同的 JPEG，ICC 原生嵌入回读必须逐字节一致；
不一致即非零退出，产物不得采纳。--skip-gate 仅限调试。

Then by hand — deliberately not automated, shipping a binary deserves a human step:
    copy <workdir>/build-shim/bin/ttc_jpegli.dll src/TrueToneCap.Core/Resources/ttc_jpegli.dll
    #（如需同时刷新 cjpegli.exe）copy <workdir>/build-shim/jpegli_build/tools/cjpegli.exe src/TrueToneCap.Core/Resources/cjpegli.exe
    # string-scan it for libpng/libjpeg-turbo versions (see the warning above)
    # recompute and backfill sha256 / sizeBytes in Resources/tools-manifest.json
    # drop stale extractions, otherwise tests keep running the OLD binary:
    #   find src -path '*/bin/*' -name 'cjpegli.exe' -o -name 'ttc_jpegli.dll' -delete
    dotnet run --project src/TrueToneCap.Tools -c Release -- --self-test-or-gainmap-probe
"""
import argparse
import hashlib
import os
import shutil
import struct
import subprocess
import sys

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))

VCVARSALL = r"C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvarsall.bat"

# 2026-11-12 起本配方同时产出两件原生件（同一棵源码树、同一套开关，见 ttc-shim/ 包装层）：
#   cjpegli.exe    —— JPEG LI 的 exe 回退路径（子进程调用）
#   ttc_jpegli.dll —— JPEG LI 的主路径（P/Invoke，进程内编码）
# 配置入口是 workdir 下 ttc-shim/ 的包装 CMakeLists（由本脚本从 scripts/jpegli-shim/
# 复制过去），它 add_subdirectory 整棵 jpegli 树并 FORCE 与下面完全一致的开关集。
SHIM_TARGETS = ["ttc_jpegli", "ttc_selftest", "cjpegli"]

CONFIGURE = [
    "cmake", "-S", "ttc-shim", "-B", "build-shim",
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


def _copy_shim_sources(src_root):
    """把 scripts/jpegli-shim/ 的三个文件复制到 workdir/ttc-shim/（幂等，整目录重建）。

    shim 源码是产品仓库的一部分（受门禁钉住），构建时新鲜复制进 jpegli 检出树，
    保证「源码在仓里、二进制可复现」——配方不依赖 workdir 里残留的旧 shim 副本。
    """
    shim_src = os.path.join(SCRIPT_DIR, "jpegli-shim")
    needed = ("CMakeLists.txt", "ttc_shim.cc", "ttc_selftest.cc")
    missing = [f for f in needed if not os.path.isfile(os.path.join(shim_src, f))]
    if missing:
        raise SystemExit("[FATAL] shim sources incomplete in %s: missing %s"
                         % (shim_src, ", ".join(missing)))
    shim_dst = os.path.join(src_root, "ttc-shim")
    os.makedirs(shim_dst, exist_ok=True)
    for f in needed:
        shutil.copy2(os.path.join(shim_src, f), os.path.join(shim_dst, f))
    return shim_dst


def _write_sample_ppm(path, w=768, h=512):
    """确定性样张：平滑渐变 + 高频噪声带 + 纯色块 + 文字状边缘，覆盖四类纹理。

    逐字节对照与图像内容无关（编码器对什么输入都走同一条管线），但要
    覆盖量化表/色度/熵编码的典型形态，避免"恰好一致"的假阳性样张。
    """
    data = bytearray()
    for y in range(h):
        for x in range(w):
            r = (x * 255) // (w - 1)
            g = (y * 255) // (h - 1)
            b = ((x * x + y * y) >> 5) & 255
            if (y // 64) % 2 == 1:  # 高频噪声带
                n = ((x * 73 + y * 151) ^ (x * 7 + y * 31)) & 255
                r = g = b = n
            if 96 <= x < 160 and 96 <= y < 160:      # 纯色块
                r, g, b = 255, 0, 0
            elif 96 <= x < 160 and 160 <= y < 224:
                r, g, b = 0, 255, 0
            elif 96 <= x < 160 and 224 <= y < 288:
                r, g, b = 0, 0, 255
            elif 200 <= x < 568 and 320 <= y < 384:  # 文字状条纹（模拟截图内容）
                bar = ((x - 200) // 8) % 2
                r = g = b = 255 if bar else 16
            data += bytes((r, g, b))
    with open(path, "wb") as f:
        f.write(b"P6\n%d %d\n255\n" % (w, h))
        f.write(bytes(data))


def _write_test_icc(path):
    """最小合法 RGB 矩阵 ICC v2 profile（sRGB 原色矩阵 + γ2.2 曲线）。

    用于 ttc_selftest 的 ICC 回读腿：验证 shim 经库原生 jpegli_write_icc_profile
    写出的 APP2 分块链重组后与输入 profile 逐字节一致。自合成而非取系统 profile，
    保证配方在任意机器上可复现，且内容不受机器显示器漂移影响。
    """
    def s15f16(v):
        return int(round(v * 65536.0))

    def xyz_tag(sig, x, y, z):
        return sig + b"\0\0\0\0" + struct.pack(">iii", s15f16(x), s15f16(y), s15f16(z))

    def curv_tag(gamma):
        body = b"curv" + b"\0\0\0\0" + struct.pack(">I", 1) + struct.pack(">H", int(round(gamma * 256)))
        return body + b"\0\0"  # 14 → 16，四字节对齐

    def desc_tag(name):
        ascii_body = name.encode("ascii") + b"\0"
        body = (b"desc" + b"\0\0\0\0" + struct.pack(">I", len(ascii_body)) + ascii_body
                + struct.pack(">II", 0, 0)          # unicode 语言码 + 计数（空）
                + struct.pack(">HB", 0, 0) + b"\0" * 67)  # scriptcode（空）
        body += b"\0" * ((4 - len(body) % 4) % 4)
        return body

    def text_tag(s):
        body = b"text" + b"\0\0\0\0" + s.encode("ascii") + b"\0"
        body += b"\0" * ((4 - len(body) % 4) % 4)
        return body

    tags = [
        (b"desc", desc_tag("TrueToneCap jpegli shim test profile")),
        (b"wtpt", xyz_tag(b"XYZ ", 0.9642, 1.0000, 0.8249)),
        (b"rXYZ", xyz_tag(b"XYZ ", 0.4360747, 0.3850649, 0.1430804)),
        (b"gXYZ", xyz_tag(b"XYZ ", 0.2225045, 0.7168786, 0.0606529)),
        (b"bXYZ", xyz_tag(b"XYZ ", 0.0139322, 0.0971045, 0.7141733)),
        (b"rTRC", curv_tag(2.2)),
        (b"gTRC", curv_tag(2.2)),
        (b"bTRC", curv_tag(2.2)),
        (b"cprt", text_tag("Public domain")),
    ]
    tag_table_size = 4 + 12 * len(tags)
    offset = 128 + tag_table_size
    table = bytearray(struct.pack(">I", len(tags)))
    chunks = bytearray()
    for sig, data in tags:
        table += sig + struct.pack(">II", offset, len(data))
        chunks += data
        offset += len(data)
    profile = bytearray(offset)
    struct.pack_into(">I", profile, 0, offset)            # 0: size（自洽）
    struct.pack_into(">I", profile, 8, 0x02400000)        # 8: v2.4
    profile[12:16] = b"mntr"                              # 12: device class
    profile[16:20] = b"RGB "                              # 16: color space
    profile[20:24] = b"XYZ "                              # 20: PCS
    profile[24:36] = struct.pack(">6H", 2026, 11, 12, 0, 0, 0)  # 24: date
    profile[36:40] = b"acsp"                              # 36: magic
    struct.pack_into(">iii", profile, 68, s15f16(0.9642), s15f16(1.0), s15f16(0.8249))  # 68: D50
    profile[128:128 + tag_table_size] = table
    profile[128 + tag_table_size:] = chunks
    with open(path, "wb") as f:
        f.write(bytes(profile))


def _sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--workdir", default=os.path.join(os.environ["TEMP"], "jpegli-src"),
                    help="path to the google/jpegli checkout (submodules must be initialized)")
    ap.add_argument("--configure-only", action="store_true")
    ap.add_argument("--build-only", action="store_true")
    ap.add_argument("--skip-gate", action="store_true",
                    help="跳过逐字节对照门禁（仅供调试，产物不得采纳进 Resources/）")
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

    shim_dst = _copy_shim_sources(src)

    env = msvc_env(src)
    env["PATH"] = env.get("PATH", "") + os.pathsep + r"C:\Program Files\NASM"
    print("[OK] MSVC env: %s" % env["INCLUDE"].split(os.pathsep)[0])

    if not args.build_only:
        print(">>> " + " ".join(CONFIGURE))
        if subprocess.call(CONFIGURE, env=env, cwd=src) != 0:
            raise SystemExit("[FATAL] configure failed")
        print("[OK] configure (%s)" % shim_dst)

    if not args.configure_only:
        build = ["cmake", "--build", "build-shim", "--target", *SHIM_TARGETS,
                 "--parallel"]
        print(">>> " + " ".join(build))
        if subprocess.call(build, env=env, cwd=src) != 0:
            raise SystemExit("[FATAL] build failed")

        exe = os.path.join(src, "build-shim", "jpegli_build", "tools", "cjpegli.exe")
        dll = os.path.join(src, "build-shim", "bin", "ttc_jpegli.dll")
        selftest = os.path.join(src, "build-shim", "bin", "ttc_selftest.exe")
        for p in (exe, dll, selftest):
            if not os.path.isfile(p):
                raise SystemExit("[FATAL] build reported success but %s is missing" % p)

        # ═══ 逐字节对照门禁：shim ↔ cjpegli.exe 同参数必须产出相同 JPEG ═══
        # 这是 ttc_jpegli.dll 采纳进 Resources/ 的前置硬门禁（2026-11-12 起写入配方）：
        # shim 的正确性论证是"与 cjpegli.exe 走完全相同的上游代码路径"，这条门禁
        # 把论证变成证据——任何一侧升级/改参后不一致即阻断。
        if not args.skip_gate:
            gate_dir = os.path.join(os.environ["TEMP"], "ttc_jpegli-gate")
            os.makedirs(gate_dir, exist_ok=True)
            sample = os.path.join(gate_dir, "sample.ppm")
            icc = os.path.join(gate_dir, "test.icc")
            _write_sample_ppm(sample)
            _write_test_icc(icc)
            print(">>> gate: ttc_selftest (%s) vs cjpegli.exe" % sample)
            r = subprocess.run([selftest, sample, exe, icc], cwd=os.path.dirname(selftest),
                               capture_output=True, text=True)
            print(r.stdout.rstrip())
            if r.stderr.strip():
                print("[gate stderr] " + r.stderr.strip())
            if r.returncode != 0:
                raise SystemExit("[FATAL] byte-identity gate FAILED — 产物不得采纳")
            print("[OK] byte-identity gate passed")

        print()
        print("[OK] cjpegli      %s (%d bytes)" % (exe, os.path.getsize(exe)))
        print("[OK] ttc_jpegli   %s (%d bytes)" % (dll, os.path.getsize(dll)))
        print("     dll sha256   %s" % _sha256(dll))
        print("     exe sha256   %s" % _sha256(exe))
        print()
        print("⚠️  NEXT (do not skip): string-scan the binaries for libpng / libjpeg-turbo versions.")
        print("    jpegli's pinned submodules are libpng 1.6.47 / libjpeg-turbo 2.1.5.1 —")
        print("    both older than what this project already ships. If the scan shows those,")
        print("    DO NOT adopt the binary until the drift is resolved (see the module docstring).")
        print()
        print("后续（人工确认，见文件头）：复制进 Resources/ → 字符串扫描核对内嵌版本 →")
        print("       回填 tools-manifest.json → 删除 bin 下的陈旧副本 → 重跑回归")
        print("       ⚠ 本配方产出的 cjpegli.exe 若与在库版本来源 commit 不同，")
        print("         覆盖前想清楚：exe 是回退路径，与 DLL 的 jpegli commit 不一致只会")
        print("         导致回退产物字节略有差异（均合法 JPEG），可接受但要在 manifest 里记明。")


if __name__ == "__main__":
    main()
