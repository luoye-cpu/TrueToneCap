// Quick test: generate PNG files with all bit depths and analyze them
using TrueToneCap.Core.Encoding;
using System.IO.Compression;

var outDir = Path.Combine(Path.GetTempPath(), "TrueToneCap_PngDump");
Directory.CreateDirectory(outDir);
Console.WriteLine($"Output: {outDir}");

// ── SDR: 8-bit BGRA pattern ──
var bgra = new byte[16 * 16 * 4];
var rng = new Random(42);
for (int i = 0; i < bgra.Length; i += 4)
{
    bgra[i] = (byte)rng.Next(256);
    bgra[i+1] = (byte)rng.Next(256);
    bgra[i+2] = (byte)rng.Next(256);
    bgra[i+3] = 255;
}
foreach (int bd in new[] { 8, 10, 12 })
{
    var path = Path.Combine(outDir, $"sdr_{bd}bit.png");
    ManagedPngEncoder.Encode(bgra, 16, 16, path, bd);
    Console.WriteLine($"SDR {bd}-bit: {new FileInfo(path).Length} bytes");
}

// ── HDR: 模拟 WGC Float16 像素 (scRGB linear) ──
// scRGB 中 1.0 = 80 nits。创建 0.5 scRGB (~40 nits) 的均匀测试图
float[] scRgbPixels = new float[16 * 16 * 4];
for (int i = 0; i < scRgbPixels.Length; i += 4)
{
    scRgbPixels[i]   = 0.5f;  // R
    scRgbPixels[i+1] = 0.3f;  // G
    scRgbPixels[i+2] = 0.7f;  // B
    scRgbPixels[i+3] = 1.0f;  // A
}

var hdrFrame = new HdrFrameData
{
    Pixels = scRgbPixels,
    Width = 16,
    Height = 16
};

// 使用修复后的 HdrToPq16 测试不同位深
foreach (int bd in new[] { 10, 12, 16 })
{
    // 调用 HdrToPq16 并传入 bitDepth 参数 → 直接量化到目标位深再左对齐
    var pq16 = FormatHelper.HdrToPq16(hdrFrame, "BT2020", bd);
    var bgra16 = FormatHelper.Rgba16ToBgra16Bytes(pq16, 16, 16);
    byte[] cicp = [9, 16, 0, 1];
    var path = Path.Combine(outDir, $"hdr_{bd}bit.png");
    ManagedPngEncoder.Encode16(bgra16, 16, 16, path, cicp: cicp, bitDepth: bd);
    Console.WriteLine($"HDR {bd}-bit: {new FileInfo(path).Length} bytes");

    // 验证像素值精度: 检查低 6/4 位是否为 0
    bool allLowBitsZero = true;
    int maxLowBits = 0;
    foreach (var val in pq16)
    {
        int lowBits = val & ((1 << (16 - bd)) - 1);
        if (lowBits != 0)
        {
            allLowBitsZero = false;
            if (lowBits > maxLowBits) maxLowBits = lowBits;
        }
    }
    Console.Write($"  -> 精度验证: 低 {16-bd} 位{(allLowBitsZero ? " 全零 ✅" : $" 有非零值(最大 {maxLowBits}) ❌")}");
    // 验证最大值范围
    int maxVal = pq16.Max();
    int expectedMax = bd == 16 ? 65535 : ((1 << bd) - 1) << (16 - bd);
    Console.Write($", max={maxVal}, 期望最大={expectedMax}");
    if (maxVal <= expectedMax)
        Console.WriteLine(" ✅");
    else
        Console.WriteLine(" ❌ 超出范围!");
}

Console.WriteLine("\n=== DEEP ANALYSIS ===");

foreach (var f in new DirectoryInfo(outDir).GetFiles("*.png"))
{
    Console.WriteLine($"\n## {f.Name} ({f.Length} bytes)");
    var bytes = File.ReadAllBytes(f.FullName);
    
    // Parse chunks
    int pos = 8; // Skip signature
    while (pos < bytes.Length - 4)
    {
        int len = (bytes[pos] << 24) | (bytes[pos+1] << 16) | (bytes[pos+2] << 8) | bytes[pos+3];
        string type = System.Text.Encoding.ASCII.GetString(bytes, pos+4, 4);
        byte[] data = bytes.AsSpan(pos+8, len).ToArray();
        
        Console.Write($"  {type} ({len}B)");
        
        switch (type)
        {
            case "IHDR":
                int w = (data[0]<<24)|(data[1]<<16)|(data[2]<<8)|data[3];
                int h = (data[4]<<24)|(data[5]<<16)|(data[6]<<8)|data[7];
                int bd = data[8]; int ct = data[9];
                Console.Write($" -> {w}x{h} bit_depth={bd} color_type={ct}");
                if (ct == 6 && bd is 8 or 16) Console.Write(" ✅");
                else if (ct == 6) Console.Write(" ❌ INVALID for color_type 6!");
                break;
            case "sBIT":
                Console.Write($" -> R:{data[0]} G:{data[1]} B:{data[2]} A:{data[3]} sig bits");
                break;
            case "cICP":
                Console.Write($" -> primaries={data[0]} transfer={data[1]} matrix={data[2]} range={data[3]}");
                break;
            case "IDAT":
                // Decompress and check content (ZLibStream, not raw Deflate)
                try
                {
                    using var ms = new MemoryStream(data);
                    using var zlib = new ZLibStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    zlib.CopyTo(outMs);
                    var raw = outMs.ToArray();
                    int nonZero = raw.Count(b => b != 0);
                    Console.Write($" -> decompressed={raw.Length}B non-zero={nonZero}");
                    if (nonZero == 0) Console.Write(" ⚠️ ALL ZEROS!");
                }
                catch (Exception ex)
                {
                    Console.Write($" -> decompress error: {ex.Message}");
                }
                break;
            case "IEND":
                break;
        }
        Console.WriteLine();
        
        pos += 12 + len;
    }
}