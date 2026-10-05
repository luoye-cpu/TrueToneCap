import os, subprocess, sys, re
exe = r"C:\PLAN\ffmpegPictureUI\tools\src\libjxl\build\tools\Release\jxlinfo.exe"
d = r"C:\PLAN\TrueToneCap\artifacts\precision\probe\jxlcolr"
prods = [os.path.join(d,f) for f in sorted(os.listdir(d)) if re.match(r'^hdr_.*\.jxl$', f)] if os.path.isdir(d) else []
print("样本数:", len(prods), prods[:1])
def try_env(label, dirs):
    env = os.environ.copy()
    if dirs: env["PATH"] = os.pathsep.join(dirs) + os.pathsep + env.get("PATH", "")
    p = subprocess.run([exe] + (prods[:1] if prods else []), capture_output=True, text=True,
                       encoding="utf-8", errors="replace", env=env)
    print(f"[{label}] rc={p.returncode} out={ (p.stdout or p.stderr or '').strip()[:90] !r}")
if not prods:
    print("找不到 hdr_*.jxl 样本 ⇒ 本轮先不比结果，只比退出码"); try_env("无参", [])
else:
    try_env("裸 PATH", [])
    try_env("加 build\lib\Release", [r"C:\PLAN\ffmpegPictureUI\tools\src\libjxl\build\lib\Release"])
    try_env("加 exe 同目录", [os.path.dirname(exe)])
