// TrueToneCap.Core/Processing/GpuEffectProcessor.cs
// GPU 加速后处理效果（马赛克、模糊等）— 使用 HLSL 着色器 + D3D11

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TrueToneCap.Core.Processing;

/// <summary>GPU 后处理效果处理器。</summary>
public sealed class GpuEffectProcessor : IDisposable
{
    private readonly ID3D11Device _device;
    private ID3D11PixelShader? _mosaicShader;
    private ID3D11VertexShader? _fullscreenVs;
    private ID3D11SamplerState? _sampler;
    private ID3D11InputLayout? _inputLayout;
    private bool _disposed;

    public bool IsAvailable => _mosaicShader is not null && _fullscreenVs is not null;

    public GpuEffectProcessor(ID3D11Device device)
    {
        _device = device;
        InitializeShaders();
    }

    private void InitializeShaders()
    {
        try
        {
            // 加载马赛克着色器
            var mosaicBytes = LoadShaderBytes("MosaicEffect.hlsl.cso");
            if (mosaicBytes is not null)
                _mosaicShader = _device.CreatePixelShader(mosaicBytes);

            // 加载全屏直通 VS（与 ToneMapper 相同）
            var vsBytes = LoadShaderBytes("FullscreenVS.cso");
            if (vsBytes is not null)
            {
                _fullscreenVs = _device.CreateVertexShader(vsBytes, null);
                _inputLayout = _device.CreateInputLayout(
                [
                    new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                    new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 0, 12)
                ], vsBytes);
            }

            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipLinear,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0, MaxLOD = float.MaxValue
            };
            _sampler = _device.CreateSamplerState(samplerDesc);

            System.Diagnostics.Debug.WriteLine($"[GpuEffectProcessor] 初始化: Mosaic={(IsAvailable ? "✓" : "✗")}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GpuEffectProcessor] 初始化失败: {ex.Message}");
            _mosaicShader = null;
        }
    }

    private static byte[]? LoadShaderBytes(string name)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", "Shaders", name);
            if (File.Exists(path)) return File.ReadAllBytes(path);

            // 嵌入资源回退
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase));
            if (resName is not null)
            {
                using var stream = asm.GetManifestResourceStream(resName);
                if (stream is not null)
                {
                    var bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                    return bytes;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>GPU 马赛克处理（像素化）。</summary>
    public byte[] ApplyMosaic(byte[] bgra, int w, int h, int blockSize = 8)
    {
        if (!IsAvailable || blockSize <= 1)
            return CpuMosaic(bgra, w, h, blockSize);

        try { return ApplyMosaicGpu(bgra, w, h, blockSize); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GpuEffectProcessor] GPU 马赛克失败: {ex.Message}");
            return CpuMosaic(bgra, w, h, blockSize);
        }
    }

    private byte[] ApplyMosaicGpu(byte[] bgra, int w, int h, int blockSize)
    {
        // 创建输入纹理
        var texDesc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.Write
        };
        using var inputTex = _device.CreateTexture2D(texDesc);
        var ctx = _device.ImmediateContext;

        var mapped = ctx.Map(inputTex, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            byte* dstBase = (byte*)mapped.DataPointer.ToPointer();
            int dstRowPitch = (int)mapped.RowPitch;
            int srcStride = w * 4;
            fixed (byte* src = bgra)
            {
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy(src + row * srcStride, dstBase + row * dstRowPitch, srcStride, srcStride);
            }
        }
        ctx.Unmap(inputTex, 0);

        using var srv = _device.CreateShaderResourceView(inputTex);

        // 输出纹理
        var outDesc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
        };
        using var outputTex = _device.CreateTexture2D(outDesc);
        using var rtv = _device.CreateRenderTargetView(outputTex);

        // 渲染
        ctx.OMSetRenderTargets(rtv);
        ctx.RSSetViewport(0, 0, w, h);
        ctx.PSSetShader(_mosaicShader!);
        ctx.VSSetShader(_fullscreenVs!);
        ctx.PSSetShaderResource(0, srv);
        ctx.PSSetSampler(0, _sampler!);
        ctx.IASetInputLayout(_inputLayout);
        ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleStrip);
        ctx.Draw(3, 0);

        // 读回
        var stagingDesc = new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read
        };
        using var stagingTex = _device.CreateTexture2D(stagingDesc);
        ctx.CopyResource(stagingTex, outputTex);

        var mappedOut = ctx.Map(stagingTex, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var result = new byte[w * h * 4];
        unsafe
        {
            byte* srcBase = (byte*)mappedOut.DataPointer.ToPointer();
            int srcRowPitch = (int)mappedOut.RowPitch;
            int dstStride = w * 4;
            fixed (byte* dst = result)
            {
                for (int row = 0; row < h; row++)
                    Buffer.MemoryCopy(srcBase + row * srcRowPitch, dst + row * dstStride, dstStride, dstStride);
            }
        }
        ctx.Unmap(stagingTex, 0);
        return result;
    }

    /// <summary>CPU 马赛克回退。</summary>
    public static byte[] CpuMosaic(byte[] bgra, int w, int h, int blockSize)
    {
        if (blockSize <= 1) return bgra;
        var result = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, result, 0, bgra.Length);

        int bs = Math.Max(2, blockSize);
        for (int y = 0; y < h; y += bs)
        {
            int bh = Math.Min(bs, h - y);
            for (int x = 0; x < w; x += bs)
            {
                int bw = Math.Min(bs, w - x);
                int rSum = 0, gSum = 0, bSum = 0, cnt = 0;
                for (int dy = 0; dy < bh; dy++)
                {
                    for (int dx = 0; dx < bw; dx++)
                    {
                        int idx = ((y + dy) * w + (x + dx)) * 4;
                        bSum += result[idx]; gSum += result[idx + 1]; rSum += result[idx + 2];
                        cnt++;
                    }
                }
                byte ar = (byte)(rSum / cnt), ag = (byte)(gSum / cnt), ab = (byte)(bSum / cnt);
                for (int dy = 0; dy < bh; dy++)
                {
                    for (int dx = 0; dx < bw; dx++)
                    {
                        int idx = ((y + dy) * w + (x + dx)) * 4;
                        result[idx] = ab; result[idx + 1] = ag; result[idx + 2] = ar;
                    }
                }
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mosaicShader?.Dispose();
        _fullscreenVs?.Dispose();
        _sampler?.Dispose();
        _inputLayout?.Dispose();
    }
}
