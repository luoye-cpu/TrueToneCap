import os, subprocess, sys
exe = r"C:\PLAN\ffmpegPictureUI\tools\src\libjxl\build\tools\Release\jxlinfo.exe"
def rc(label):
    p = os.environ.get("PATH", "")
    r = subprocess.run([exe], capture_output=True, text=True, encoding="utf-8", errors="replace")
    print(f"{label:22} rc={r.returncode:<12} PATH 长度={len(p)} PATH 项数={len(p.split(os.pathsep))}")
    return p
before = rc("import 前")
sys.path.insert(0, os.path.join("scripts", "precision"))
import precision_env as pe          # noqa
after = rc("import 后")
print("PATH 是否被改:", before != after)
if before != after:
    b, a = set(before.split(os.pathsep)), set(after.split(os.pathsep))
    print("  新增:", sorted(a - b)[:6])
    print("  消失:", sorted(b - a)[:6])
