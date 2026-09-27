$cls = Get-CimClass -Namespace root\wmi -ClassName WmiMonitorDescriptorMethods -ErrorAction SilentlyContinue
foreach ($m in $cls.CimClassMethods) {
    $ps = ($m.Parameters | ForEach-Object { "$($_.Name):$($_.CimType)" }) -join ', '
    Write-Host "$($m.Name)  入参=[$ps]"
}
