// TrueToneCap.Core/ColorManagement/GamutMapper.cs
// ACES 影视标准色域缩限 — 广色域/HDR → sRGB 单一路径

using ImageMagick;

namespace TrueToneCap.Core.ColorManagement;

/// <summary>ACES 色域映射引擎：Perceptual 感知意图，影视工业标准。</summary>
public static class GamutMapper
{
    /// <summary>将 BGRA8 像素从源色彩空间缩限到 sRGB（ACES Perceptual）。</summary>
    public static (byte[] pixels, byte[]? targetIcc) MapToSRgb(
        byte[] bgra, int w, int h, byte[]? sourceIcc)
    {
        if (sourceIcc is { Length: > 500 })
        {
            var (result, _) = ColorProfileProvider.BakeIccToTarget(bgra, w, h, sourceIcc, "sRGB");
            if (result is not null) return (result, null);
        }
        return (bgra, null); // 无 ICC 则直通
    }

    /// <summary>HDR scRGB → SDR sRGB（Tone Map + ACES Gamut Map）。</summary>
    public static byte[] HdrToSRgb(float[] scRgbPixels, int w, int h,
        Processing.ToneMappingParams toneParams)
    {
        var pixels = new float[scRgbPixels.Length];
        Array.Copy(scRgbPixels, pixels, pixels.Length);
        Processing.ToneMapper.ApplyToneMapping(pixels, w, h, toneParams);
        Processing.ToneMapper.LinearToSRgb(pixels);

        int pixelCount = w * h;
        var bgra = new byte[pixelCount * 4];
        Parallel.For(0, pixelCount, pi =>
        {
            int i = pi * 4;
            bgra[i]     = (byte)Math.Clamp((int)(pixels[i + 2] * 255f + 0.5f), 0, 255);
            bgra[i + 1] = (byte)Math.Clamp((int)(pixels[i + 1] * 255f + 0.5f), 0, 255);
            bgra[i + 2] = (byte)Math.Clamp((int)(pixels[i]     * 255f + 0.5f), 0, 255);
            bgra[i + 3] = (byte)Math.Clamp((int)(pixels[i + 3] * 255f + 0.5f), 0, 255);
        });
        return bgra;
    }
}
