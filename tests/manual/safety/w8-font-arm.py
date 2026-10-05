"""② 根因诊断臂：一次构建并排 6 种字体寻址形态，看哪一种能把 PUA 字形画出来。

背景：本机产品进程里所有 FontIcon 都渲染成豆腐块，四条旧假设（删第二份 XamlControlsResources／
元素上写死 FontFamily／删隐式 FontIcon 样式／自己补 SymbolThemeFontFamily）全部证伪，
但那一轮没有把"写死 FontFamily"的【形态】逐个拆开量过。

判据（对着同一张图读，不需要跨图比较）：
  1 友好名 Segoe Fluent Icons        — 画不出 = 家族名解析失败
  2 友好名 Segoe MDL2 Assets         — 同上，换一个注册名再验一次
  3 {ThemeResource SymbolThemeFontFamily} — 只有它画不出、1 画得出 ⇒ 是主题资源没解析到，不是字体问题
  4 文件路径 #家族名                  — 若这条画得出 ⇒ ②有解，且不必再靠抠轮廓
  5 Style Setter 下发 FontFamily      — 若这条画不出而 1 画得出 ⇒ 与本轮 B0 的"Style 下发 Data 丢失"同源
  6 Microsoft YaHei + 中文（阳性对照）— 这条必须画得出，否则整个探针失效（FontFamily 属性根本没生效）
参照物：左侧导航栏"系统设置"那枚齿轮是 PathIcon（同一张图里），它是"齿轮该长什么样"的地面真值。

本臂是一次性诊断，读完即撤；不写中文界面串（避免扰动本地化扫描面）。
"""
import io, sys

P = r'C:\PLAN\TrueToneCap\src\TrueToneCap.App\MainWindow.xaml'
G = '&#xE713;'

ARM = [
    '                    <!-- 一次性诊断臂（②根因，读完即撤）：6 种字体寻址形态并排 -->',
    '                    <StackPanel Spacing="4">',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="1 friendly-Fluent" Width="210" FontSize="12"/>',
    f'                            <TextBlock FontFamily="Segoe Fluent Icons" FontSize="30" Text="{G}"/>',
    '                        </StackPanel>',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="2 friendly-MDL2" Width="210" FontSize="12"/>',
    f'                            <TextBlock FontFamily="Segoe MDL2 Assets" FontSize="30" Text="{G}"/>',
    '                        </StackPanel>',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="3 themeResource-Symbol" Width="210" FontSize="12"/>',
    f'                            <TextBlock FontFamily="{{ThemeResource SymbolThemeFontFamily}}" FontSize="30" Text="{G}"/>',
    '                        </StackPanel>',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="4 filepath-hash-family" Width="210" FontSize="12"/>',
    f'                            <TextBlock FontFamily="C:/Windows/Fonts/SegoeIcons.ttf#Segoe Fluent Icons" FontSize="30" Text="{G}"/>',
    '                        </StackPanel>',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="5 style-setter" Width="210" FontSize="12"/>',
    f'                            <TextBlock FontSize="30" Text="{G}">',
    '                                <TextBlock.Style>',
    '                                    <Style TargetType="TextBlock">',
    '                                        <Setter Property="FontFamily" Value="Segoe Fluent Icons"/>',
    '                                    </Style>',
    '                                </TextBlock.Style>',
    '                            </TextBlock>',
    '                        </StackPanel>',
    '                        <StackPanel Orientation="Horizontal" Spacing="10">',
    '                            <TextBlock Text="6 CONTROL YaHei CJK" Width="210" FontSize="12"/>',
    '                            <TextBlock FontFamily="Microsoft YaHei" FontSize="30" Text="&#x4E2D;&#x6587;"/>',
    '                        </StackPanel>',
    '                    </StackPanel>',
]


def main():
    s = io.open(P, encoding='utf-8', newline='').read()
    assert '一次性诊断臂' not in s, '诊断臂已在场，拒绝重复插入'
    L = s.split('\r\n')
    assert 'x:Name="PageOutput"' in L[46], 'PageOutput 不在 line 47，锚点已漂移 ⇒ 不猜'
    L[47:47] = ARM
    io.open(P, 'w', encoding='utf-8', newline='').write('\r\n'.join(L))
    print('诊断臂已插入 PageOutput 顶部（6 形态 + 1 阳性对照）')


if __name__ == '__main__':
    main()
