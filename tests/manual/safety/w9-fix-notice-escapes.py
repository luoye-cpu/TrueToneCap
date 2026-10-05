"""把生成器的 HEADER 改成原始字符串（前缀小写 r），一次解决所有反斜杠。

上一版逐个把 C:\\Windows 双写是打地鼠：路径里还有 \\SegoeIcons.ttf 与 \\segmdl2.ttf 两个分隔符，
补完报 \S、再补报 \s。HEADER 是纯字面块、里面没有任何需要转义的序列，
所以正解是整块改成原始串——反斜杠按字面进产物，Python 也不再报 SyntaxWarning。

前置：先把上一版双写的两处还原成单反斜杠，否则原始串会把两个都留在产物里。
"""
import io

BS = chr(92)
P = 'scripts/gen-third-party-notices.py'
s = io.open(P, encoding='utf-8', newline='\n').read()

# 1) 还原上一版的双写（连同文件名前的分隔符一起处理，避免漏）
pairs = [
    ('C:' + BS*2 + 'Windows' + BS*2 + 'Fonts', 'C:' + BS + 'Windows' + BS + 'Fonts'),
    (BS*2 + 'SegoeIcons.ttf', BS + 'SegoeIcons.ttf'),
    (BS*2 + 'segmdl2.ttf', BS + 'segmdl2.ttf'),
]
for a, b in pairs:
    s = s.replace(a, b)

# 2) HEADER 改成原始字符串
old = 'HEADER = """'
assert s.count(old) == 1, s.count(old)
s = s.replace(old, 'HEADER = r"""', 1)

io.open(P, 'w', encoding='utf-8', newline='\n').write(s)
# 自查：HEADER 段里不该再有任何转义序列
i = s.index('HEADER = r"""')
j = s.index('"""', i + 15)
body = s[i:j]
print('HEADER 长度 =', len(body))
print('含双反斜杠 =', (BS*2) in body, ' 含单反斜杠 =', BS in body)
