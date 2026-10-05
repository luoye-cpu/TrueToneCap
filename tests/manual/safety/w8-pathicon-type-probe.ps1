# B0 的硬前提：PathIcon.Data 到底是什么类型、WinUI 里有没有可用的 Geometry 资源类型。
# 只反射，不起产品，不改任何文件。
$ErrorActionPreference = 'Continue'
$bin = 'C:\PLAN\TrueToneCap\src\TrueToneCap.App\bin\x64\Release\net11.0-windows10.0.26100.0\win-x64'
$dll = Join-Path $bin 'Microsoft.WinUI.dll'
Write-Output "Microsoft.WinUI.dll 存在 = $(Test-Path -LiteralPath $dll)"
if (-not (Test-Path -LiteralPath $dll)) { exit 2 }
$asm = [System.Reflection.Assembly]::LoadFrom($dll)

$pi = $asm.GetType('Microsoft.UI.Xaml.Controls.PathIcon')
if ($null -eq $pi) { Write-Output '★ 反射不到 PathIcon 类型'; exit 2 }
Write-Output "PathIcon 程序集 = $($pi.Assembly.GetName().Name)"
foreach ($p in @('Data','Fill','Width','Height')) {
    $pp = $pi.GetProperty($p)
    if ($pp) { Write-Output ("  属性 {0} : {1}" -f $p, $pp.PropertyType.FullName) }
    else { Write-Output "  属性 $p ：无" }
}
$dp = $pi.GetField('DataProperty')
if ($dp) {
    # 依赖属性的字段拿不到类型，改用静态字段反射拿 DependencyProperty 再读 OwnerType 没意义；
    # 这里退一步：列出所有以 Data 结尾的 DependencyProperty 字段
    Write-Output '  —— 含 Converter/TypeConverter 线索的自定义特性：'
    foreach ($ca in $pi.GetCustomAttributesData()) { Write-Output ("    {0}" -f $ca.AttributeType.Name) }
}
Write-Output ''
Write-Output '=== WinUI 里可当资源放的 Geometry 类型（决定 Icons.xaml 能用什么元素）==='
foreach ($t in @('StreamGeometry','PathGeometry','GeometryGroup','EllipseGeometry','RectangleGeometry','LineGeometry','Geometry')) {
    $ty = $asm.GetType("Microsoft.UI.Xaml.Media.$t")
    Write-Output ("  Microsoft.UI.Xaml.Media.{0} ：{1}" -f $t, $(if ($ty) { '存在' } else { '不存在' }))
}
Write-Output ''
Write-Output '=== PathGeometry 是否有构造函数接受路径字符串（mini-language）==='
if ($pg) { }
$pg = $asm.GetType('Microsoft.UI.Xaml.Media.PathGeometry')
if ($pg) { foreach ($c in $pg.GetConstructors()) { Write-Output ("  ctor({0})" -f ((($c.GetParameters() | ForEach-Object { $_.ParameterType.Name }) -join ', '))) } }
Write-Output ''
Write-Output '=== 有没有 Geometry 的 TypeConverter（这决定 StaticResource 一个 string 能不能被转换）==='
foreach ($t in @('Microsoft.UI.Xaml.Media.Geometry','Microsoft.UI.Xaml.Media.PathGeometry')) {
    $ty = $asm.GetType($t); if (-not $ty) { continue }
    $tc = $ty.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -match 'TypeConverter|ContentProperty' }
    Write-Output ("  {0} ⇒ {1}" -f $t, $(if ($tc) { ($tc | ForEach-Object { $_.AttributeType.Name + ' ' + (($_.ConstructorArguments | ForEach-Object { $_.Value }) -join '/') }) -join ' | ' } else { '（无 TypeConverter/ContentProperty 特性）' }))
}
