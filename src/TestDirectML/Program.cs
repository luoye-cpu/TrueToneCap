using TrueToneCap.Core.Services;
using System;
using System.Diagnostics;
using System.IO;

Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  DirectML 检测测试");
Console.WriteLine("══════════════════════════════════════\n");

// ── 1. 检查 DirectML 可用性 ──
Console.WriteLine("── 1. 检查 DirectML 可用性 ──");
bool dmlAvailable = OnnxOcrEngine.IsDirectMLAvailable();
Console.WriteLine($"DirectML 可用: {dmlAvailable}");
Console.WriteLine();

// ── 2. 测试 CPU 引擎 ──
Console.WriteLine("── 2. 测试 CPU 引擎 ──");
try
{
    var cpuEngine = new OnnxOcrEngine(OnnxExecutionProvider.Cpu);
    Console.WriteLine($"  引擎名称: {cpuEngine.Info.Name}");
    Console.WriteLine($"  可用: {cpuEngine.Info.IsAvailable}");
    Console.WriteLine($"  模式: {cpuEngine.Info.Mode}");
}
catch (Exception ex)
{
    Console.WriteLine($"  初始化失败: {ex.Message}");
}
Console.WriteLine();

// ── 3. 测试 DirectML 引擎 ──
Console.WriteLine("── 3. 测试 DirectML 引擎 ──");
try
{
    var dmlEngine = new OnnxOcrEngine(OnnxExecutionProvider.DirectML);
    Console.WriteLine($"  引擎名称: {dmlEngine.Info.Name}");
    Console.WriteLine($"  可用: {dmlEngine.Info.IsAvailable}");
    Console.WriteLine($"  模式: {dmlEngine.Info.Mode}");
}
catch (Exception ex)
{
    Console.WriteLine($"  初始化失败: {ex.Message}");
}
Console.WriteLine();

// ── 4. 检查模型文件 ──
Console.WriteLine("── 4. 模型文件 ──");
string modelDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrueToneCap", "onnx_models");
if (Directory.Exists(modelDir))
{
    foreach (var f in Directory.GetFiles(modelDir, "*.onnx"))
    {
        var fi = new FileInfo(f);
        Console.WriteLine($"  {fi.Name}: {fi.Length / 1024.0 / 1024.0:F1} MB");
    }
}
else
{
    Console.WriteLine("  模型目录不存在");
}
Console.WriteLine();

Console.WriteLine("══════════════════════════════════════");
Console.WriteLine("  测试完成");
Console.WriteLine("══════════════════════════════════════");
