// TrueToneCap.Core/Capture/D3D12CaptureDevice.cs
// D3D12 设备封装 — 核心 GPU 后处理管线

using Vortice.Direct3D12;
using Vortice.Direct3D;
using Vortice.DXGI;

namespace TrueToneCap.Core.Capture;

public sealed class D3D12CaptureDevice : IDisposable
{
    private bool _disposed;

    public D3D12CaptureDevice()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory7>();
        factory.EnumAdapters1(0u, out var adapter);
        Device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_1);
        adapter.Dispose();

        CommandQueue = Device.CreateCommandQueue<ID3D12CommandQueue>(
            new CommandQueueDescription { Type = CommandListType.Direct });
        Fence = Device.CreateFence<ID3D12Fence>(0);
    }

    public ID3D12Device Device { get; }
    public ID3D12CommandQueue CommandQueue { get; }
    public ID3D12Fence Fence { get; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Fence.Dispose();
        CommandQueue.Dispose();
        Device.Dispose();
    }
}
