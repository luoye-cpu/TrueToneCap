"""新判据 Test_QualityTexts_CoreStringsAreAllCovered 的变异自检（三发，各自归属）。

这条判据一次钉了三件事，所以要三发分别证明每一半都有牙：
  M1 抽掉 App 清单里的一条      ⇒ "全被 App 清单覆盖" 必须红
  M2 Core 改写一句文案的措辞     ⇒ 同上红（这一发才对应真实风险：Core 改了字面量）
  M3 删掉 ZhToEn 里的一条译文     ⇒ "清单里每一条都在 ZhToEn 有译文" 必须红

M1/M3 不需要重编（判据读的是源码文本）；M2 读的是 Core 的运行时产出，必须重编。
每一发都：改前记 md5 → 改 → 跑 → 按判据名归属地找红 → 还原 → 复算 md5 必须逐字相同。
还原失败就立刻退出并且不覆盖上一步的日志——不许把"没还原"说成"已还原"。
"""
import hashlib
import io
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
EXE = ROOT / 'src/TrueToneCap.Test/bin/Release/net11.0-windows10.0.26100.0/TrueToneCap.Test.exe'
MW = ROOT / 'src/TrueToneCap.App/MainWindow.xaml.cs'
LM = ROOT / 'src/TrueToneCap.App/Services/LocaleManager.cs'
FE = ROOT / 'src/TrueToneCap.Core/Encoding/FormatEncoders.cs'
BAK = ROOT / 'tests/output/w9/mut-backup'
LOGDIR = ROOT / 'tests/output/w9/mut-i18n2'

# 每一发：(名字, 目标文件, 旧串, 新串, 期望红的那条判据前缀, 要不要重编)
MUTS = [
    ("M1-抽掉清单里一条", MW,
     '        "butteraugli 距离 (0.5-3.0)",\n', '',
     'Core 产出的中文质量串全被 App 清单覆盖', False),
    ("M2-Core 改写措辞", FE,
     '"质量 (50-100; 85+ 视觉无损)"', '"质量 (50-100; 85+ 视觉近乎完美)"',
     'Core 产出的中文质量串全被 App 清单覆盖', True),
    ("M3-删掉一条译文", LM,
     '            ["butteraugli 距离 (0.5-3.0)"] = "butteraugli distance (0.5-3.0)",\n', '',
     '清单里每一条都在 ZhToEn 有译文', False),
]


def md5(p: Path) -> str:
    return hashlib.md5(p.read_bytes()).hexdigest()


def run(tag: str) -> tuple[int, str]:
    out = LOGDIR / f'{tag}.log'
    r = subprocess.run([str(EXE), '--unit-tests'], cwd=str(ROOT),
                       stdout=open(out, 'wb'), stderr=subprocess.STDOUT)
    return r.returncode, out.read_text(encoding='utf-8', errors='replace')


def main() -> int:
    if not EXE.exists():
        print(f'❌ 找不到测试宿主 {EXE} ⇒ 本轮没跑，不是跑到 0 红')
        return 1
    BAK.mkdir(parents=True, exist_ok=True)
    LOGDIR.mkdir(parents=True, exist_ok=True)

    before = {p: md5(p) for _, p, *_ in [(m[0], m[1]) for m in MUTS]}
    fails = []
    for name, path, old, new, want, rebuild in MUTS:
        src = path.read_bytes()
        text = src.decode('utf-8-sig')
        # 锚点里的 \n 要按【该文件自己的行尾】落地：MainWindow.xaml.cs 是全 CRLF，
        # 另三份是全 LF（autocrlf=true 下 index 恒 LF，工作树却是混的）。写死 \n 会让
        # CRLF 那份恒命中 0 次——这不是"没有这行"，是锚点没对齐。
        nl = '\r\n' if '\r\n' in text else '\n'
        old, new = old.replace('\n', nl), new.replace('\n', nl) if new else new
        if text.count(old) != 1:
            print(f'❌ {name}：锚点命中 {text.count(old)} 次 ⇒ 这一发没法单独归因，跳过')
            fails.append(f'{name} 锚点不唯一')
            continue
        shutil.copy2(path, BAK / path.name)
        path.write_bytes(text.replace(old, new, 1).encode('utf-8'))
        try:
            if rebuild:
                b = subprocess.run(['dotnet', 'build', str(ROOT / 'TrueToneCap.slnx'),
                                    '-c', 'Release', '--nologo'], cwd=str(ROOT),
                                   capture_output=True, text=True, encoding='utf-8', errors='replace')
                if b.returncode != 0:
                    print(f'❌ {name}：重编失败 ⇒ 产物是旧的，这一发的读数作废')
                    fails.append(f'{name} 重编失败')
                    continue
            rc, log = run(name.split('-')[0])
            reds = re.findall(r'❌\s+(.+)', log)
            hit = [r for r in reds if r.startswith(want)]
            total = re.search(r'RESULT suite=(.*)', log)
            if hit:
                print(f'✅ {name}：被预期那半判据抓到（{total.group(1).strip() if total else "?"}）')
                print(f'     判了：{hit[0][:110]}')
            else:
                print(f'❌ {name}：{rc=} 但没看到「{want[:24]}…」报红 ⇒ 这一半判据没牙')
                print(f'     实得红条：{[r[:60] for r in reds][:6]}')
                fails.append(f'{name} 未被抓到')
        finally:
            path.write_bytes(src)
            if md5(path) != md5(BAK / path.name):
                print(f'❌ {name}：还原后指纹不一致 ⇒ 停机，先手工核对 {path.name}')
                return 2
            print(f'     还原：{path.name} md5 复算一致 ✓')

    if rebuild_needed := any(m[5] for m in MUTS):
        b = subprocess.run(['dotnet', 'build', str(ROOT / 'TrueToneCap.slnx'),
                            '-c', 'Release', '--nologo'], cwd=str(ROOT),
                           capture_output=True, text=True, encoding='utf-8', errors='replace')
        print(f'还原后重编 rc={b.returncode}（M2 把 Core.dll 改过，必须重编回来再复跑）')

    for p, h in before.items():
        if md5(p) != h:
            print(f'❌ 终检：{p.name} 指纹与轮前不同 ⇒ 变异漏在盘上')
            return 2
    rc, log = run('post-restore')
    tail = [l for l in log.splitlines() if 'RESULT suite' in l]
    print(f'复跑（全部还原后）rc={rc} {tail[-1].strip() if tail else "无 RESULT 行"}')
    if rc != 0:
        fails.append('还原后仍红')
    print('MUTATION ' + ('FAILED: ' + ' | '.join(fails) if fails else 'ALL-CAUGHT'))
    return 1 if fails else 0


if __name__ == '__main__':
    sys.exit(main())
