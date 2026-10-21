# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
# 把当前未提交状态存成一份可复现、可自证的退路补丁
#
# 为什么要有这个脚本而不是手敲命令：本仓的树被打回过四次，恢复靠的是"恰好命中一次的脚本"；
# 手敲的那一次没法重跑，也就没法证明补丁完整。
# 三条硬要求（都是付过学费的）：
#   ① `git diff` 看不见未跟踪文件 ⇒ 未跟踪那半边逐段用 `git diff --no-index /dev/null <f>` 补；
#   ② 补丁自己（和它的 tgz 兄弟）必须排除在外，否则哈希自环；
#   ③ 落盘后要当场自证：跟踪那半边用 `git apply -R --check` 反打一遍（能反打 = 与当前树逐字相符），
#      未跟踪那半边数段数并复算 md5，两把尺都对上才写"已存"。
import hashlib
import os
import subprocess
import sys

REPO = r'C:\PLAN\TrueToneCap'
# 每一轮存新日期那一份，别覆盖已经被用过一次的那份：
# uncommitted-2026-10-01b.patch 是 10-02 从两个被打坏成 NUL 填充的源文件里恢复时
# 恰好命中唯一一次的补丁，它的"曾经可用"是一条有日期的证据，覆盖掉就没了。
OUT = (os.path.join(REPO, 'tests', 'output', sys.argv[1])
       if len(sys.argv) > 1
       else os.path.join(REPO, 'tests', 'output', 'uncommitted-2026-10-01b.patch'))
EXCLUDE_PREFIX = ('tests/output/uncommitted-', 'tests/output/legacy-backups-')


def git(*args):
    return subprocess.run(['git'] + list(args), cwd=REPO, capture_output=True)


def is_excluded(p):
    return p.startswith(EXCLUDE_PREFIX)


tracked = git('diff', '--binary').stdout
if git('diff', '--binary').returncode not in (0, 1):
    sys.exit('git diff 退出码异常')

others = [l for l in git('ls-files', '--others', '--exclude-standard').stdout.decode('utf-8', 'replace')
          .splitlines() if l.strip() and not is_excluded(l)]

segs = [tracked]
missing = []
for f in others:
    absf = os.path.join(REPO, f.replace('/', os.sep))
    if not os.path.isfile(absf):
        missing.append(f)
        continue
    # ⚠ 必须传【仓内相对路径】：传绝对路径会让段头写成 a/C:/... ⇒ `git apply` 直接
    #   "invalid path"，自证那半边就废了（本轮实测：49 跟踪段能反打、50 未跟踪段全体 invalid）。
    d = subprocess.run(['git', 'diff', '--no-index', '--binary', '--', os.devnull, f],
                       cwd=REPO, capture_output=True)
    # git diff --no-index 用 1 表示"有差异"，2 才是错
    if d.returncode not in (0, 1):
        sys.exit(f'未跟踪段生成失败 {f}: rc={d.returncode} {d.stderr[:200]}')
    body = d.stdout
    if not body.strip():
        missing.append(f + '（空段）')
        continue
    segs.append(body)

blob = b'\n'.join(s for s in segs if s and s.strip())
tmp = OUT + '.new'
with open(tmp, 'wb') as fh:
    fh.write(blob)
os.replace(tmp, OUT)          # 临时件 + replace：绝不用 open(OUT,'w') 直写被验证的那份

md5 = hashlib.md5(blob).hexdigest()
n_tracked_files = sum(1 for l in tracked.splitlines() if l.startswith(b'diff --git'))
print(f'补丁 = {OUT}')
print(f'字节 {len(blob)}  md5 {md5}')
print(f'跟踪段 {n_tracked_files} 个文件 ／ 未跟踪 {len(others) - len(missing)} 个文件 ／ 跳过 {len(missing)}')
if missing:
    print('★ 跳过清单（必须处理，不能当"没有"）：')
    for m in missing:
        print('   ', m)

# 自证 ①：跟踪那半边反打 —— 能反打 = 补丁与当前工作树逐字相符
chk = subprocess.run(['git', 'apply', '-R', '--check', '--', OUT], cwd=REPO, capture_output=True)
loose = subprocess.run(['git', 'apply', '-R', '--check', '--ignore-whitespace', '--', OUT],
                       cwd=REPO, capture_output=True)
print('反打自检·严格模式 rc =', chk.returncode,
      ('OK' if chk.returncode == 0 else chk.stderr[:300].decode('utf-8', 'replace').replace(chr(10), ' / ')))
print('反打自检·忽略空白 rc =', loose.returncode,
      ('OK' if loose.returncode == 0 else loose.stderr[:300].decode('utf-8', 'replace').replace(chr(10), ' / ')))
# 本机 core.autocrlf=true：工作树是 CRLF、index 与 git diff 输出是 LF，
# 于是【严格模式对一份好补丁也会假红】（10-03 实测：MainWindow.xaml 那一段）。
# 判"补丁可用"以忽略空白那一档为准 —— 它才是"内容对不对"；严格档只作为一条信息留着，
# 它红了不拦，但真内容不符时忽略空白那一档一定也红（那一档才是 fail-closed 的门）。
if loose.returncode != 0:
    print('❌ 忽略空白也反打不上 ⇒ 补丁内容与当前树不符，这一份不能当退路用')
    raise SystemExit(2)
