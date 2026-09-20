#!/usr/bin/env python3
"""从 Windows 小转储里指认蓝屏的故障模块（不依赖 WinDbg）。

用法: py -3 scripts/probes/minidump_blame.py C:\\Windows\\Minidump\\*.dmp
      （2026-09-20 从被 gitignore 的 artifacts/ 搬进 scripts/probes/，否则"有出处但别人拿不到"）

为什么手写解析器：本机没有 WinDbg/kd，而"这次蓝屏是不是我的任务引起的"
必须落到【哪个驱动在哪条指令上崩的】这个粒度，凭 bugcheck 码猜会误判。
0x3B (SYSTEM_SERVICE_EXCEPTION) 的 param[1] 就是故障指令的内核地址，
小转储的 ModuleListStream 带完整模块基址/大小 ⇒ 地址减基址即可点名模块。
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import struct
import sys
from pathlib import Path

# MINIDUMP_STREAM_TYPE
STREAM_MODULE_LIST = 11
STREAM_SYSTEM_INFO = 7
STREAM_bugcheck = 15
STREAM_comment = 16


def read_string(data: bytes, rva: int) -> str:
    (length,) = struct.unpack_from("<I", data, rva)
    return data[rva + 4: rva + 4 + length].decode("utf-16-le", "replace")


def parse(path: Path):
    data = path.read_bytes()
    if data[:4] != b"MDMP":
        print(f"{path.name}: 不是 minidump（签名 {data[:4]!r}）")
        return
    n_streams, dir_rva = struct.unpack_from("<II", data, 8)
    streams = {}
    for i in range(n_streams):
        st, size, rva = struct.unpack_from("<III", data, dir_rva + i * 16)
        streams[st] = (size, rva)

    print(f"\n{'=' * 78}\n{path.name}  流数={n_streams}")

    # ── 时间戳（MINIDUMP_HEADER.TimeDateStamp @ 0x14，8 字节里的低 4 字节）──
    (ts,) = struct.unpack_from("<I", data, 0x14)
    if ts:
        import datetime
        print(f"  转储时刻(UTC) = {datetime.datetime.utcfromtimestamp(ts):%Y-%m-%d %H:%M:%S}")

    # ── BugCheckStream: type(4)+parameters(8*4)? 实际布局 ULONG32 BugCheckCode + 4*ULONG64 ──
    modules = []
    if STREAM_MODULE_LIST in streams:
        size, rva = streams[STREAM_MODULE_LIST]
        (count,) = struct.unpack_from("<I", data, rva)
        off = rva + 4
        for _ in range(count):
            base, img_size = struct.unpack_from("<QI", data, off)
            name_rva = struct.unpack_from("<I", data, off + 20)[0]
            tds = struct.unpack_from("<I", data, off + 16)[0]
            modules.append((base, img_size, read_string(data, name_rva), tds))
            off += 108   # sizeof(MINIDUMP_MODULE)
    if STREAM_bugcheck in streams:
        size, rva = streams[STREAM_bugcheck]
        code = struct.unpack_from("<I", data, rva)[0]
        params = struct.unpack_from("<4Q", data, rva + 8)
        names = {0x3B: "SYSTEM_SERVICE_EXCEPTION", 0x133: "DPC_WATCHDOG_VIOLATION",
                 0x50: "PAGE_FAULT_IN_NONPAGED_AREA", 0x1E: "KMODE_EXCEPTION_NOT_HANDLED",
                 0x7F: "UNEXPECTED_KERNEL_MODE_TRAP", 0xD1: "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
                 0x116: "VIDEO_TDR_FAILURE", 0x141: "VIDEO_TDR_TIMEOUT_DETECTED",
                 0x9F: "DRIVER_POWER_STATE_FAILURE", 0x7E: "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED"}
        print(f"  BugCheck 0x{code:08x} {names.get(code, '?')}")
        for i, p in enumerate(params):
            print(f"    param[{i}] = 0x{p:016x}")
        # 0x3B/0x1E/0x7E/0xD1 的 param[1] 是故障指令地址
        cand = params[1] if code in (0x3B, 0x1E, 0x7E, 0xD1, 0x50) else (params[0] if code == 0x116 else None)
        if code == 0x3B:
            cand = params[1]
            print(f"    （0x3B: param[0]=异常码 0x{params[0]:x}, param[1]=故障指令地址）")
    else:
        code = 0
        cand = None
        print("  无 BugCheckStream")

    def blame(addr, label):
        for base, sz, name, tds in modules:
            if base <= addr < base + sz:
                print(f"  {label} 0x{addr:016x} ⇒ {name.rsplit('\\\\', 1)[-1]}"
                      f"  +0x{addr - base:x}   (模块基址 0x{base:x}, 大小 0x{sz:x}, 时间戳 {tds:08x})")
                return name
        print(f"  {label} 0x{addr:016x} ⇒ 【不在任何已载入模块区间内】(可能是 JIT/被卸载驱动)")
        return None

    if cand:
        blame(cand, "故障指令")
    # 0x3B 的 param[2] 是 trap frame 地址（内核栈），param[0] 是异常码 —— 顺带点名堆栈所在模块
    for i, p in enumerate((params if cand else ()) or ()):
        if p > 0xFFFF00000000 and i != 1:
            blame(p, f"param[{i}]")

    # GPU 相关模块清单：报告版本，便于和"当前任务用了 NVENC/WGC"这条线索对照
    gpu = [m for m in modules if any(k in m[2].lower() for k in
           ("nv", "dxg", "d3d", "display", "gam", "rtk", "amdk", "intel"))]
    if gpu:
        print("  ── GPU/显示相关已载入驱动 ──")
        for base, sz, name, tds in gpu[:14]:
            print(f"     {name.rsplit('\\\\', 1)[-1]:<28} 基址 0x{base:016x} 大小 0x{sz:08x}")


if __name__ == "__main__":
    argv = sys.argv[1:] or [str(Path("C:/Windows/Minidump"))]
    targets = []
    for a in argv:
        p = Path(a)
        if p.is_dir():
            targets += sorted(p.glob("*.dmp"))
        elif "*" in a:
            targets += sorted(Path(a).parent.glob(Path(a).name))
        else:
            targets.append(p)
    for t in targets[-6:]:
        parse(t)
