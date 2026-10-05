import os, sys, subprocess
sys.path.insert(0, os.path.join("scripts", "precision"))
import precision_env as pe
d = r"C:\PLAN\TrueToneCap\artifacts\precision\probe\jxlcolr"
samples = [os.path.join(d,f) for f in sorted(os.listdir(d)) if f.endswith(".jxl")] if os.path.isdir(d) else []
print("jxlcolr 目录存在:", os.path.isdir(d), "样本数:", len(samples))
cands = []
env_hit = os.environ.get("TTC_JXLINFO")
if env_hit: cands.append(("环境变量", env_hit))
w = pe.which("jxlinfo") if hasattr(pe, "which") else None
for c in pe.tool_candidates("jxlinfo"):
    cands.append(("候选表", c))
seen = set()
for label, p in cands:
    if not p or p in seen or not os.path.isfile(p): continue
    seen.add(p)
    dll = os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(p))), "lib", "Release")
    for mode, extra in (("裸", None), ("补 lib\Release", dll if os.path.isdir(dll) else None)):
        env = os.environ.copy()
        if extra: env["PATH"] = extra + os.pathsep + env.get("PATH","")
        args = [p] + samples[:1]
        try:
            r = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace", env=env, timeout=60)
            out = (r.stdout or "") + (r.stderr or "")
            print(f"{label:6} {mode:16} rc={r.returncode:<12} 首行={out.splitlines()[0][:60] if out.strip() else '(空)'}  {p}")
        except Exception as e:
            print(f"{label:6} {mode:16} 异常 {type(e).__name__}: {e}  {p}")
print("which('jxlinfo') =", w)
