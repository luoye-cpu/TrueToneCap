// TrueToneCap.Core/Processing/GpuToneMapper.cs
// GPU 加速色调映射 — 使用预编译 HLSL 着色器 + D3D11 渲染管线
// 需要先运行 shaders/CompileShaders.ps1 生成 ToneMapping.hlsl.cso + FullscreenVS.hlsl.cso

using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace TrueToneCap.Core.Processing;

/// <summary>GPU 加速色调映射器。将 HDR scRGB (Float16) 转换为 SDR sRGB (BGRA8)。</summary>
public sealed class GpuToneMapper : IDisposable
{
    private readonly ID3D11Device _device;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11SamplerState? _sampler;
    private bool _disposed;

    // ═══ P1: 纹理池化 — 按尺寸缓存，避免每次截图创建/销毁 ═══
    private ID3D11Texture2D? _pooledInputTex;
    private ID3D11ShaderResourceView? _pooledInputSrv;
    private ID3D11Texture2D? _pooledOutputTex;
    private ID3D11RenderTargetView? _pooledOutputRtv;
    private ID3D11Texture2D? _pooledStagingTex;
    private int _pooledW, _pooledH;
    private readonly object _poolLock = new();

    // 预编译着色器字节码缓存
    private static byte[]? s_cachedPixelShader;
    private static byte[]? s_cachedVertexShader;
    private static readonly object s_loadLock = new();

    /// <summary>GPU 色调映射是否可用。</summary>
    public bool IsAvailable => _pixelShader is not null && _vertexShader is not null;

    public GpuToneMapper(ID3D11Device device)
    {
        _device = device;
        InitializeShaders();
    }

    private void InitializeShaders()
    {
        try
        {
            var (psBytes, vsBytes) = LoadShaderBytecode();
            if (psBytes is null || vsBytes is null)
            {
                System.Diagnostics.Debug.WriteLine("[GpuToneMapper] 着色器字节码未找到，GPU 路径不可用（使用 CPU 回退）");
                return;
            }

            _pixelShader = _device.CreatePixelShader(psBytes);
            _vertexShader = _device.CreateVertexShader(vsBytes, null);
            // 注意: FullscreenVS 使用 SV_VertexID 程序化生成顶点，无需 InputLayout 和顶点缓冲区

            // 采样器
            var samplerDesc = new SamplerDescription
            {
                Filter = Filter.MinMagMipPoint,
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                AddressW = TextureAddressMode.Clamp,
                ComparisonFunc = ComparisonFunction.Never,
                MinLOD = 0, MaxLOD = float.MaxValue
            };
            _sampler = _device.CreateSamplerState(samplerDesc);

            // 常量缓冲区
            var cbDesc = new BufferDescription
            {
                ByteWidth = 16, // Mode(uint) + Exposure(float) + PaperWhite(float) + MaxNits(float)
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ConstantBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            };
            _constantBuffer = _device.CreateBuffer(cbDesc);

            System.Diagnostics.Debug.WriteLine("[GpuToneMapper] GPU 着色器初始化成功");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] 初始化失败: {ex.Message}，使用 CPU 回退");
            _pixelShader = null;
            _vertexShader = null;
        }
    }

    private static (byte[]? ps, byte[]? vs) LoadShaderBytecode()
    {
        lock (s_loadLock)
        {
            if (s_cachedPixelShader is not null && s_cachedVertexShader is not null)
                return (s_cachedPixelShader, s_cachedVertexShader);

            try
            {
                // 使用 ShaderLoader 统一加载（文件系统优先，嵌入资源回退）
                s_cachedPixelShader ??= ShaderLoader.Load("ToneMapping.hlsl.cso");
                s_cachedVertexShader ??= ShaderLoader.Load("FullscreenVS.hlsl.cso");

                if (s_cachedPixelShader is not null && s_cachedVertexShader is not null)
                    System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] 加载着色器成功: PS={s_cachedPixelShader.Length}B VS={s_cachedVertexShader.Length}B");
                else
                    System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] 着色器缺失: PS={(s_cachedPixelShader is not null ? "✓" : "✗")} VS={(s_cachedVertexShader is not null ? "✓" : "✗")}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] 加载着色器失败: {ex.Message}");
            }

            return (s_cachedPixelShader, s_cachedVertexShader);
        }
    }

    /// <summary>GPU 色调映射: Float16 HDR → BGRA8 SDR。</summary>
    /// <param name="colorSpaceTag">目标色域标签，用于 CPU 回退路径的动态亮度权重。</param>
    /// <remarks>
    /// GPU 路径始终输出 sRGB（HLSL 着色器固定使用 BT.709 权重），
    /// 若需广色域输出，请使用 CPU 路径（ConvertFloat16ToSdrBgra）。
    /// GPU 路径失败时自动回退 CPU，此时 colorSpaceTag 生效。
    /// </remarks>
    public Task<byte[]> ToneMapToSdrAsync(float[] hdrPixels, int width, int height, ToneMappingParams p,
        string? colorSpaceTag = null)
    {
        if (!IsAvailable)
        {
            // CPU 回退
            return Task.FromResult(ToneMapper.FloatToSRgbBytes(hdrPixels, width, height, p, colorSpaceTag));
        }

        return Task.Run(() =>
        {
            try
            {
                return ToneMapToSdrGpu(hdrPixels, width, height, p);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] GPU 失败，CPU 回退: {ex.Message}");
                return ToneMapper.FloatToSRgbBytes(hdrPixels, width, height, p, colorSpaceTag);
            }
        });
    }

    /// <summary>确保池化纹理尺寸匹配，不匹配则重建。</summary>
    private void EnsurePooledTextures(int w, int h)
    {
        lock (_poolLock)
        {
            if (_pooledW == w && _pooledH == h && _pooledInputTex is not null)
                return;

            // 释放旧纹理
            _pooledInputSrv?.Dispose(); _pooledInputSrv = null;
            _pooledInputTex?.Dispose(); _pooledInputTex = null;
            _pooledOutputRtv?.Dispose(); _pooledOutputRtv = null;
            _pooledOutputTex?.Dispose(); _pooledOutputTex = null;
            _pooledStagingTex?.Dispose(); _pooledStagingTex = null;

            // 输入纹理 (Float16, Dynamic, CPU 可写)
            _pooledInputTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.R16G16B16A16_Float,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write
            });
            _pooledInputSrv = _device.CreateShaderResourceView(_pooledInputTex);

            // 输出纹理 (BGRA8, Default, RTV)
            _pooledOutputTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget
            });
            _pooledOutputRtv = _device.CreateRenderTargetView(_pooledOutputTex);

            // Staging 纹理 (BGRA8, CPU 可读)
            _pooledStagingTex = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });

            _pooledW = w;
            _pooledH = h;
            System.Diagnostics.Debug.WriteLine($"[GpuToneMapper] 纹理池重建: {w}x{h}");
        }
    }

    private byte[] ToneMapToSdrGpu(float[] hdrPixels, int width, int height, ToneMappingParams p)
    {
        // 序列化所有 GPU 操作：纹理池 + ImmediateContext 非线程安全
        lock (_poolLock)
        {
        EnsurePooledTextures(width, height);
        var ctx = _device.ImmediateContext;

        // ═══ 上传像素（Float32 → Float16，使用 PixelOps SIMD 加速）═══
        var mapped = ctx.Map(_pooledInputTex!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe
        {
            byte* dstBase = (byte*)mapped.DataPointer.ToPointer();
            int dstRowPitch = (int)mapped.RowPitch;
            int halfsPerRow = width * 4;

            fixed (float* src = hdrPixels)
            {
                for (int row = 0; row < height; row++)
                {
                    byte* dstRow = dstBase + row * dstRowPitch;
                    float* srcRow = src + row * halfsPerRow;
                    // 使用 PixelOps 批量转换（JIT 自动向量化 → F16C VCVTPS2PH）
                    TrueToneCap.Core.PixelOps.ConvertFloatToHalfRow(srcRow, (ushort*)dstRow, halfsPerRow);
                }
            }
        }
        ctx.Unmap(_pooledInputTex!, 0);

        // ═══ 渲染 ═══
        ctx.OMSetRenderTargets(_pooledOutputRtv!);
        ctx.RSSetViewport(0, 0, width, height);
        ctx.PSSetShader(_pixelShader!);
        ctx.VSSetShader(_vertexShader!);
        ctx.PSSetShaderResource(0, _pooledInputSrv!);
        ctx.PSSetSampler(0, _sampler!);
        // 无 InputLayout — SV_VertexID 驱动，无需顶点缓冲区
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(Vortice.Direct3D.PrimitiveTopology.TriangleList);

        // 更新常量缓冲区
        var cbData = new ToneMappingCb
        {
            Mode = (uint)p.Mode,
            Exposure = p.Exposure,
            PaperWhiteNits = p.PaperWhiteNits,
            DisplayMaxNits = p.DisplayMaxNits
        };
        var mappedCb = ctx.Map(_constantBuffer!, 0, Vortice.Direct3D11.MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        unsafe { Buffer.MemoryCopy(&cbData, mappedCb.DataPointer.ToPointer(), 16, 16); }
        ctx.Unmap(_constantBuffer!, 0);
        ctx.PSSetConstantBuffer(0, _constantBuffer!);

        // 绘制全屏三角形（SV_VertexID 生成 3 顶点，无需 VB）
        ctx.Draw(3, 0);

        // ═══ 读回结果 ═══
        ctx.CopyResource(_pooledStagingTex!, _pooledOutputTex!);
        ctx.Flush();

        var mappedOut = ctx.Map(_pooledStagingTex!, 0, Vortice.Direct3D11.MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        var result = new byte[width * height * 4];
        unsafe
        {
            byte* srcBase = (byte*)mappedOut.DataPointer.ToPointer();
            int srcRowPitch = (int)mappedOut.RowPitch;
            int dstStride = width * 4;
            fixed (byte* dst = result)
            {
                for (int row = 0; row < height; row++)
                    Buffer.MemoryCopy(srcBase + row * srcRowPitch, dst + row * dstStride, dstStride, dstStride);
            }
        }
        ctx.Unmap(_pooledStagingTex!, 0);

        return result;
        } // end lock (_poolLock)
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ToneMappingCb
    {
        public uint Mode;
        public float Exposure;
        public float PaperWhiteNits;
        public float DisplayMaxNits;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _constantBuffer?.Dispose();
        _sampler?.Dispose();
        lock (_poolLock)
        {
            _pooledInputSrv?.Dispose();
            _pooledInputTex?.Dispose();
            _pooledOutputRtv?.Dispose();
            _pooledOutputTex?.Dispose();
            _pooledStagingTex?.Dispose();
        }
    }
}
