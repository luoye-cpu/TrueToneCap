// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/AnimatedContainerColor.cs
// 动图容器的色彩声明嵌入（2026-11-02 D 流第 5 步配套）。
//
// 背景（2026-11-03 本机 ffmpeg git-2026-10-27 实测，见台账 §11）：
//   · WebP muxer 不搬运输入 PNG 的 iCCP ⇒ 产物无 ICCP chunk；
//   · APNG muxer 不写任何声明 chunk；
//   · AVIF muxer 不写 prof(colr-ICC) box，但 nclx 可用 -color_primaries/-color_trc/
//     -colorspace 显式下发（那条路在 AnimationRecorder 里，不经过本类）；
//   · GIF 格式本身没有声明通道。
// 于是 WebP 的 ICCP 与 APNG 的 cICP/iCCP 只能由我们自己往【编好的容器】里插 chunk ——
// 这就是本文件的两个入口。两边都是纯结构操作：解析 → 重建 → 原子写回，
// 任何一处结构不合预期都返回失败（调用方记日志、产物保持无声明），绝不抛异常出去。
//
// 内存纪律：不为图像数据（无损 4K 可到 33MB）做二次拷贝 —— 重建时按 (offset, len)
// 引用原缓冲写流；输入 bytes、输出缓冲与 ToArray 结果在重建瞬间并存三份，属已知峰值。

using System.Buffers.Binary;

namespace TrueToneCap.Core.Encoding;

public static class AnimatedContainerColor
{
    /// <summary>把 cICP / iCCP chunk 插进 PNG/APNG 文件的 IHDR 之后（保留区，IDAT 之前）。
    /// <para>APNG 同样适用：cICP/iCCP 是全文件级色彩声明，acTL/fcTL/IDAT 都在它们之后。</para>
    /// 已存在的同名 chunk 跳过（不重复插、也不改写）。两个参数都为 null 时不动文件、返回成功。</summary>
    public static (bool Ok, string Reason) TryEmbedPngDeclaration(string path, byte[]? cicp, byte[]? iccProfile)
    {
        try
        {
            if (cicp is null && iccProfile is null) return (true, "无声明可嵌");
            if (cicp is { Length: not 4 }) return (false, "cICP 必须 4 字节");
            var bytes = File.ReadAllBytes(path);
            if (!IsPng(bytes)) return (false, "PNG 签名不符");
            // IHDR 恒 13 字节载荷（PNG 规范 11.2.2）；类型对但长度不对的畸形文件按契约拒绝，
            // 而不是让下面的 insertAt=33 切进载荷中间静默产出坏文件（审查轮 R2-P2-2）。
            if (bytes.Length < 8 + 12 + 13
                || BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)) != 13
                || !IsIhdr(bytes, 8))
                return (false, "第一个 chunk 不是 13 字节的 IHDR");

            bool hasCicp = FindChunk(bytes, "cICP") >= 0;
            bool hasIccp = FindChunk(bytes, "iCCP") >= 0;
            bool addCicp = cicp is { Length: 4 } && !hasCicp;
            bool addIccp = iccProfile is { Length: > 0 } && !hasIccp && FormatHelper.IsEmbeddableIcc(iccProfile);
            if (!addCicp && !addIccp) return (true, "声明已存在，跳过");

            int insertAt = 8 + 12 + 13;   // IHDR 恒 13 字节载荷，插在它之后
            using var ms = new MemoryStream(bytes.Length + 4096);
            ms.Write(bytes, 0, insertAt);
            // 顺序与 ManagedPngEncoder.Encode 一致：cICP 在前、iCCP 在后
            if (addCicp) WriteChunk(ms, "cICP"u8, cicp!);
            if (addIccp) WriteChunk(ms, "iCCP"u8, BuildIccpPayload(iccProfile!));
            ms.Write(bytes, insertAt, bytes.Length - insertAt);

            var output = ms.ToArray();
            AtomicFileWriter.Write(path, fs => fs.Write(output));
            var parts = new List<string>();
            if (addCicp) parts.Add("cICP");
            if (addIccp) parts.Add("iCCP");
            return (true, $"{string.Join("+", parts)} 已插入 IHDR 之后");
        }
        catch (Exception ex)
        {
            return (false, $"异常: {ex.Message}");
        }
    }

    /// <summary>把 ICC profile 以 ICCP chunk 形式嵌进 WebP（RIFF）文件。
    /// <para>WebP 规范：ICCP 要求文件处于扩展格式 ⇒ 没有 VP8X 时先补一个（顺带从 VP8/VP8L
    /// 码流里解析画布尺寸填进去），已有 VP8X 时置 ICC 标志位（0x20，最高有效位侧第 3 位）。
    /// ICCP 紧跟 VP8X 之后、图像数据 chunk 之前（规范规定的位置）。已有 ICCP 时替换其载荷。</para></summary>
    public static (bool Ok, string Reason) TryEmbedWebpIcc(string path, byte[] iccProfile)
    {
        try
        {
            if (iccProfile is not { Length: > 0 }) return (false, "空 ICC");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 12 || !IsFourCc(bytes, 0, "RIFF") || !IsFourCc(bytes, 8, "WEBP"))
                return (false, "RIFF/WEBP 签名不符");

            // ── 解析既有 chunk（只记引用，不拷载荷）──
            var refs = new List<(string Type, int Off, int Len)>();
            int pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                int len = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(pos + 4));
                if (len < 0 || pos + 8 + (long)len > bytes.Length) return (false, "chunk 越界");
                refs.Add((System.Text.Encoding.ASCII.GetString(bytes, pos, 4), pos + 8, len));
                pos += 8 + len + (len & 1);   // 奇数长度补一位 pad
            }
            if (refs.Count == 0) return (false, "没有图像 chunk");

            // ── 画布尺寸：VP8X / VP8 / VP8L 三处任取其一 ──
            string? dimsSource = null;
            (int W, int H) dims = (0, 0);
            byte vp8xFlags = 0;
            foreach (var (type, off, len) in refs)
            {
                if (type == "VP8X" && len >= 10)
                {
                    vp8xFlags = bytes[off];
                    dims = (ReadLe24(bytes, off + 4) + 1, ReadLe24(bytes, off + 7) + 1);
                    dimsSource = "VP8X";
                    break;
                }
                if (type == "VP8" && len >= 10)
                {
                    // VP8 关键帧头：3B 帧标签 + 0x9D 0x01 0x2A 起始码 + 2B 宽 + 2B 高（各 14-bit + 2-bit scale）
                    if (bytes[off + 3] == 0x9D && bytes[off + 4] == 0x01 && bytes[off + 5] == 0x2A)
                    {
                        dims = (BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 6)) & 0x3FFF,
                                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(off + 8)) & 0x3FFF);
                        dimsSource = "VP8";
                        break;
                    }
                }
                if (type == "VP8L" && len >= 5 && bytes[off] == 0x2F)
                {
                    // VP8L：签名后 14-bit 宽-1、14-bit 高-1，LSB 在前。
                    // ⚠ 高的 bit12-13 落在第 4 字节的 bit2-3，掩码必须 0x0F —— 写成 0x03 会把
                    // 高 >4096 的画布截断（7680×4320 解析成 7680×224，审查轮 R2-P1-1 实测算过）。
                    int wm1 = bytes[off + 1] | ((bytes[off + 2] & 0x3F) << 8);
                    int hm1 = (bytes[off + 2] >> 6) | (bytes[off + 3] << 2) | ((bytes[off + 4] & 0x0F) << 10);
                    dims = (wm1 + 1, hm1 + 1);
                    dimsSource = "VP8L";
                    break;
                }
            }
            if (dimsSource is null) return (false, "认不出画布尺寸（无 VP8X/VP8/VP8L 或头部异常）");

            // ── 重建：VP8X（置 ICC 位）→ ICCP → 其余 chunk（跳过旧 ICCP）──
            using var ms = new MemoryStream(bytes.Length + iccProfile.Length + 64);
            ms.Write("RIFF"u8);                       // 尺寸最后回填
            ms.Write([0, 0, 0, 0]);
            ms.Write("WEBP"u8);
            WriteRiffChunk(ms, "VP8X"u8, BuildVp8xPayload((byte)(vp8xFlags | 0x20), dims.W, dims.H));
            WriteRiffChunk(ms, "ICCP"u8, iccProfile);
            foreach (var (type, off, len) in refs)
            {
                if (type == "ICCP") continue;          // 旧的换新的
                WriteRiffChunk(ms, System.Text.Encoding.ASCII.GetBytes(type), bytes.AsSpan(off, len));
            }

            // 回填 RIFF 尺寸（不含 "RIFF" 四字与这 4 字节本身）
            var done = ms.ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(done.AsSpan(4), (uint)(done.Length - 8));

            AtomicFileWriter.Write(path, fs => fs.Write(done));
            return (true, $"ICCP {iccProfile.Length}B 已嵌入（画布 {dims.W}x{dims.H} 来自 {dimsSource}）");
        }
        catch (Exception ex)
        {
            return (false, $"异常: {ex.Message}");
        }
    }

    // ── 结构工具 ──

    private static bool IsPng(byte[] b) =>
        b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47
        && b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A;

    private static bool IsIhdr(byte[] b, int off) =>
        b[off + 4] == 'I' && b[off + 5] == 'H' && b[off + 6] == 'D' && b[off + 7] == 'R';

    private static bool IsFourCc(byte[] b, int off, string s) =>
        b[off] == s[0] && b[off + 1] == s[1] && b[off + 2] == s[2] && b[off + 3] == s[3];

    private static int FindChunk(byte[] b, string type)
    {
        int pos = 8;
        while (pos + 8 <= b.Length)
        {
            int len = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(pos));
            if (len < 0 || pos + 12 + (long)len > b.Length) return -1;
            if (System.Text.Encoding.ASCII.GetString(b, pos + 4, 4) == type) return pos;
            pos += 12 + len;
        }
        return -1;
    }

    private static int ReadLe24(byte[] b, int off) =>
        b[off] | (b[off + 1] << 8) | (b[off + 2] << 16);

    private static byte[] BuildVp8xPayload(byte flags, int w, int h)
    {
        var p = new byte[10];
        p[0] = flags;
        WriteLe24(p, 4, w - 1);
        WriteLe24(p, 7, h - 1);
        return p;
    }

    private static void WriteLe24(byte[] b, int off, int v)
    {
        b[off] = (byte)v; b[off + 1] = (byte)(v >> 8); b[off + 2] = (byte)(v >> 16);
    }

    /// <summary>RIFF/WebP chunk：type(4) + size(LE32) + payload(+奇数补位)，【无 CRC】——
    /// RIFF 不校验、也不该带。与下方 PNG chunk（长度在前 + CRC32）是两套约定，不能共用。</summary>
    private static void WriteRiffChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        s.Write(type);
        Span<byte> len4 = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(len4, (uint)payload.Length);
        s.Write(len4);
        s.Write(payload);
        if ((payload.Length & 1) != 0) s.Write([0]);
    }

    /// <summary>PNG chunk：length(BE32) + type + payload + CRC32。⚠ PNG 没有奇长补位（那是 RIFF
    /// 的约定）—— 2026-11-03 实机抓过：给奇数 iCCP 补一个 pad 字节，后面整条 chunk 链错位 1 字节，
    /// ffmpeg 直接 "chunk too big" 拒收整张图。</summary>
    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> len4 = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len4, payload.Length);
        s.Write(len4);
        s.Write(type);
        s.Write(payload);
        BinaryPrimitives.WriteUInt32BigEndian(len4, Crc32(type, payload));
        s.Write(len4);
    }

    /// <summary>iCCP 载荷 = profile 名\0 + 压缩方法字节(0x00) + zlib 流（PNG 规范 11.3.2.5）。
    /// 与 ManagedPngEncoder.BuildIccpChunk 同构；那边是 private，这里自持一份免得为动图路把
    /// 单帧编码器的内部拉成 public。⚠ 压缩方法字节不能漏——漏了 ffmpeg 解码会报
    /// "iCCP with invalid compression"（2026-11-03 实机抓到）。Fastest 即可——小体积、每次录制一份。</summary>
    private static byte[] BuildIccpPayload(byte[] profile)
    {
        using var ms = new MemoryStream();
        ms.Write("ICC profile\0"u8);
        ms.Write([0x00]);   // compression method: 0 = zlib
        using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(profile);
        return ms.ToArray();
    }

    private static uint Crc32(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        Crc32Span(Crc32Span(0xFFFFFFFFu, a), b) ^ 0xFFFFFFFFu;

    private static uint Crc32Span(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var x in data)
        {
            crc ^= x;
            for (int k = 0; k < 8; k++)
                crc = (crc >> 1) ^ (0xEDB88320u & ~(uint)((crc & 1) - 1));
        }
        return crc;
    }
}
