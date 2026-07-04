using Vortice.Direct3D11;
using Vortice.Direct3D;
using TrueToneCap.Core.Capture;

Console.WriteLine("==== TrueToneCap 混合架构验证 (D3D11 捕获 + D3D12 后处理就绪) ====");
try {
    Console.Write("[1/4] D3D11 设备... "); using var d11 = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport); Console.WriteLine("OK");
    Console.Write("[2/4] D3D12 设备... "); using var d12 = new D3D12CaptureDevice(); Console.WriteLine("OK");
    Console.WriteLine("      混合架构: D3D11 捕获 + D3D12 后处理 ✅");
    Console.Write("[3/4] D3D11 DXGI 捕获... ");
    using var cap = new ScreenCapture(d11, 0);
    Console.WriteLine($"{cap.CaptureFormat} ({(cap.IsHdrCapture?"HDR":"SDR")})");
    Console.Write("[4/4] 捕获帧 + 读回... ");
    using var frame = cap.TryAcquireNextFrame(500);
    if (frame is null) { Console.WriteLine("TIMEOUT (可能需管理员权限或非远程桌面)"); }
    else { Console.WriteLine($"{frame.Width}x{frame.Height} {(frame.IsHdr?"HDR":"SDR")}"); var p = await frame.GetFloatPixelsAsync(); float min=float.MaxValue,max=float.MinValue; for(int i=0;i<Math.Min(p.Length,50000);i++){min=Math.Min(min,p[i]);max=Math.Max(max,p[i]);} Console.WriteLine($"Min={min:F4} Max={max:F4} {(max>1?"HDR!":"SDR")}"); }
} catch(Exception ex) { Console.WriteLine($"ERROR: {ex.Message}"); }
