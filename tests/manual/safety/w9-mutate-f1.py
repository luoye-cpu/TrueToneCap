"""变异 M-F1：把 723 那条降级改回 Debug 档，验证新卡会红。

为什么单独写成文件：上一版把这段塞进 bash 的 `py -c`，`\\$` 被 shell 吃掉 ⇒ 目标串没匹配上、
断言当场拦停，变异【根本没落地】，那一次的"1130 全绿"因此不能算验过。
本脚本里不出现任何需要 shell 解释的转义。
"""
import io, shutil, sys

P = 'src/TrueToneCap.App/Services/WgcCaptureService.cs'
BAK = r'C:\tmp\wgc-good.cs'
DOLLAR = chr(36)

if sys.argv[1] == 'arm':
    shutil.copyfile(P, BAK)
    s = io.open(P, encoding='utf-8', newline='').read()
    # 723 是【普通串】不是内插串（没有 $），上一版照 717 的写法加了 $ ⇒ count=0，
    # 变异又没落地。这里按盘上实际文本匹配，并且断言的是"改动确实发生了"，不是"我以为发生了"。
    tgt = 'LogInfo("[WGC] D3D11 设备创建于默认适配器（未按显示器匹配到适配器）");'
    assert s.count(tgt) == 1, s.count(tgt)
    s = s.replace(tgt, 'Log("[WGC] D3D11 设备创建于默认适配器（未按显示器匹配到适配器）");', 1)
    assert s.count('LogInfo("[WGC] D3D11 设备创建于默认适配器') == 0, '变异没生效'
    io.open(P, 'w', encoding='utf-8', newline='').write(s)
    print('已变异：723 改回 Debug 档')
else:
    shutil.copyfile(BAK, P)
    print('已还原')
