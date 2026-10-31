// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/ManagedTiffEncoder.cs
// 托管 TIFF 编码器 — 零依赖，支持 8-bit 和 16-bit BGRA

namespace TrueToneCap.Core.Encoding;

/// <summary>托管 TIFF 编码器 — 8/16-bit BGRA, Deflate 压缩, ICC 嵌入, ExtraSamples=2 声明 alpha 为直通；
/// 大图按 ~1 MiB/strip 分条、逐行并行转 RGBA、逐条并行压缩（2026-10-31）。</summary>
public static class ManagedTiffEncoder
{
    /// <summary>
    /// 分条目标：每 strip 未压缩数据 ≈ 1 MiB。
    /// <para>为什么是 1 MiB：deflate 窗口只有 32 KiB，切成 ≥1 MiB 的条只损失条边界几个窗口的
    /// 长匹配，换来的是"每条一次独立 zlib 流 ⇒ 条间可并行"；再小（libtiff 默认 8 KiB）
    /// 压缩率退化开始可见，再大则 4K 帧只有几条、并行度不够。
    /// 小图（未压缩整帧 ≤ 1 MiB 或行数不足）自动落回单 strip ⇒ 压缩字节与分条前的历史实现逐字相同。
    /// ⚠ 2026-10-31 更正：这句原先写成"与历史容器逐字节一致"，补上 Tag 338 ExtraSamples 之后
    /// 那半句不再成立 —— IFD 多一条 12 字节的内联条目，值区与像素数据整体后移 12，
    /// 小图的产物字节也照样变。变的是【声明】不是像素：解压后的像素仍然逐字节不变。</para>
    /// </summary>
    private const long StripTargetBytes = 1 << 20;
    /// <summary>把 BGRA 像素编码为 TIFF 文件。</summary>
    /// <param name="inputBitDepth">输入像素位深（8 或 16）；null 时按数组长度推断。详见 Stream 重载说明。</param>
    /// <param name="effort">压缩强度档（2026-10-29）。默认 Smallest = 本文件历史上硬编码的
    /// level 9；2026-10-31 起压缩字节可能来自 libdeflate，承诺改"解压后逐字节相同"。</param>
    public static void Encode(byte[] bgra, int w, int h, string path, int bitDepth = 8,
        byte[]? iccProfile = null, int? inputBitDepth = null, CompressionEffort effort = CompressionEffort.Smallest)
    {
        // 2026-09-19：原先的 File.Create(path) 直写，在 Stream 重载抛 ArgumentException 时（参数无效 /
        // 像素长度不足都发生在写第一个字节之前）会在最终路径留下一个【0 字节但 File.Exists 为真】的
        // .tiff，上层照样把那个路径当作已产出的文件报告出去。现走同目录临时文件 + 成功后替换。
        AtomicFileWriter.Write(path, fs => Encode(bgra, w, h, fs, bitDepth, iccProfile, inputBitDepth, effort));
    }

    /// <summary>把 BGRA 像素编码为 TIFF 写入流。</summary>
    /// <param name="bgra">像素数据。8-bit 时每像素 4 字节；16-bit 时每像素 8 字节（通道顺序 BGRA、每通道大端）。</param>
    /// <param name="w">图像宽度。</param>
    /// <param name="h">图像高度。</param>
    /// <param name="output">目标流。</param>
    /// <param name="bitDepth">
    /// 请求的输出位深。TIFF 的 <c>BitsPerSample</c> 只有 8 与 16 合法，
    /// 故 &gt; 8 的请求一律落到 16（见方法内的钳制说明）。
    /// </param>
    /// <param name="iccProfile">可选 ICC 配置文件（长度需 &gt; 128 才会写入）。</param>
    /// <param name="inputBitDepth">
    /// 输入像素位深（8 或 16）。传 null 时按数组长度推断（兼容旧调用方）。
    /// <para>
    /// ⚠ 建议显式传入：按长度推断在"8-bit 数组 + 请求 16-bit 输出"这类组合下虽碰巧正确，
    /// 但语义模糊，一旦调用方复用缓冲（数组容量大于实际数据）就会误判为 16-bit。
    /// </para>
    /// </param>
    public static void Encode(byte[] bgra, int w, int h, Stream output, int bitDepth = 8,
        byte[]? iccProfile = null, int? inputBitDepth = null, CompressionEffort effort = CompressionEffort.Smallest)
    {
        // ═══ 2026-09-17 修复（审查 P2-7）：BPS 只有 8/16 合法，必须先钳制 ═══
        // 设置层曾对全部格式放行 10/12-bit，而本编码器以 bitDepth > 8 为界
        // 一律按【每通道 2 字节】写采样数据，却把 BitsPerSample 原样写成 10/12。
        // 结果：解码器按 12-bit 打包去读一片 16-bit 未打包的数据 → 图像错位、
        // 多数查看器直接报"文件损坏"。且不抛异常、无日志，是最难察觉的一类静默破坏。
        // 故在入口就地钳到 8/16 —— 请求更高精度一律落到 16（保精度优先于省体积）。
        // 放在这里而不是只改调用方，是为了让任何调用方都不可能写出非法 TIFF。
        if (bitDepth > 8) bitDepth = 16;

        long pixelCount = (long)w * h;
        if (bgra is null || w <= 0 || h <= 0)
            throw new ArgumentException($"TIFF 参数无效: w={w} h={h}");

        // 输入位深：优先用显式参数，未指定时回退到按长度推断（兼容旧调用）
        bool srcIs16 = inputBitDepth.HasValue
            ? inputBitDepth.Value > 8
            : bgra.Length >= pixelCount * 8;

        long required = srcIs16 ? pixelCount * 8 : pixelCount * 4;
        if (bgra.Length < required)
            throw new ArgumentException(
                $"TIFF 像素数据长度不足: len={bgra.Length} 需要={required} ({(srcIs16 ? "16" : "8")}-bit)");

        using var writer = new BinaryWriter(output);

        // TIFF header: little-endian
        writer.Write((byte)0x49); writer.Write((byte)0x49); // II
        writer.Write((ushort)42);                           // TIFF magic
        writer.Write((uint)8);                              // IFD offset (after header)

        // ── 值区布局（IFD 之后存放"4 字节放不下"的值）──
        // ⚠ 必须先规划好整片值区，再写像素数据。
        // 旧实现把 pixelDataStart 直接取为"写完 BitsPerSample 之后的位置"，
        // 而 StripOffsets/StripByteCounts/XRes/YRes 的值区都排在其后 →
        // 回填时用 Seek 覆盖了压缩数据前 24 字节 → 输出的 TIFF 解压后前若干像素损坏。
        bool hasIcc = FormatHelper.IsEmbeddableIcc(iccProfile);
        // ⚠ 必须与下方实际写入的条目数严格一致：256/257/258/259/262/273/277/278/279/282/283/296/338 = 13 个。
        // 之前写死 10，少算 2 个条目 = 24 字节 → 解析器只读前 10 条，随后把第 11 条
        // 的前 4 字节当作 next-IFD 偏移，且值区起点整体前移 24 字节 → 标签值与像素数据全部错位。
        // ═══ 2026-10-31：12 → 13，补上漏发的 Tag 338 ExtraSamples（见下方 338 那段）═══
        // 多出的这一条是【内联 SHORT】，不占值区槽位，但 IFD 本身长 12 字节 ⇒ 值区与像素数据整体后移 12。
        // 因此本文件里"小图与历史容器逐字节一致"的旧说法从这一刻起【不再成立】（下面两处已就地更正，
        // 不是删除：分条那一维的单 strip 分支仍然逐字相同，变的是 IFD）。历史产物并不缺像素，
        // 缺的是"第 4 个样本是什么"的声明 —— 那是容器契约缺陷，不是今天的改动引入的回归。
        const int baseTagCount = 13;
        int tagCount = baseTagCount + (hasIcc ? 1 : 0);
        long valueAreaStart = 8 + 2 + tagCount * 12 + 4;   // IFD 结束后即值区起点
        // ═══ 值区只放"4 字节放不下"的值（TIFF 6.0 §2：值 ≤ 4 字节必须【直接】写在条目里）═══
        // StripOffsets(273) / StripByteCounts(279)：单 strip 时 count=1 = 恰好 4 字节 ⇒ 必须内联，
        // 不能写值区指针（旧实现把它们也当"值区指针"写 → 读者把指针值（166）当成 strip 偏移、把下一个
        // 指针（170）当成字节数 → 读到的是 IFD 值区自己，解压失败、整幅图渲染为【全黑】，
        // 2026-09-19 实测：libtiff/ffmpeg 解我们的 TIFF 得到 0,0,0,0）。
        // 2026-10-31 起多 strip（count>1，数组 >4 字节）时才进值区，单 strip 路径保持内联 + 原位回填。
        // ── strip 规划（2026-10-31 多 strip 化）──
        // 单 strip（小图）时 273/279 仍按 TIFF 6.0 §2 内联、且【不额外占值区】⇒ 分条这一维的压缩字节
        // 与单 strip 时代的旧实现逐字相同。⚠ 但"容器与历史逐字节一致"这句 2026-10-31 起不再成立：
        // 同日补发的 338 ExtraSamples 也是内联条目（同样不占值区），它只把 IFD 加长 12 字节，
        // 于是值区起点与各条偏移整体 +12（实测：无 ICC 158→170、带 ICC 170→182，两条臂都由
        // 外部解码器复核过像素 max|Δ|=0）。
        // 多 strip 时 273/279 是 count>1 的 LONG 数组（>4 B ⇒ 必须间接），排在值区尾、像素数据之前，
        // "像素数据落在值区之后"的既有不变量与测试判据继续成立。
        int outStride = bitDepth > 8 ? 8 : 4;                  // RGBA，每通道 1 或 2 字节
        long rowBytes = (long)w * outStride;
        int rowsPerStrip = (int)Math.Clamp(StripTargetBytes / rowBytes, 1L, (long)h);
        int stripCount = (int)(((long)h + rowsPerStrip - 1) / rowsPerStrip);
        bool multiStrip = stripCount > 1;

        uint bpsOffset = (uint)valueAreaStart;             // BitsPerSample: 4×SHORT = 8
        uint xresOffset = (uint)(valueAreaStart + 8);      // XResolution: RATIONAL = 8
        uint yresOffset = xresOffset + 8;                  // YResolution: RATIONAL = 8
        long iccOffset = yresOffset + 8;                   // ICCProfile
        long valueCursor = hasIcc ? iccOffset + iccProfile!.Length : iccOffset;
        uint stripOffsetsArrayOffset = 0, stripCountsArrayOffset = 0;
        if (multiStrip)
        {
            stripOffsetsArrayOffset = (uint)valueCursor;
            valueCursor += 4L * stripCount;
            stripCountsArrayOffset = (uint)valueCursor;
            valueCursor += 4L * stripCount;
        }
        long expectedPixelDataStart = valueCursor;

        // ── IFD entries ──
        // ⚠ 每个 IFD 条目恒为 12 字节（tag2 + type2 + count4 + value4）
        writer.Write((ushort)tagCount);

        // Tag 256: ImageWidth (LONG count=1 → 内联 4 字节)
        writer.Write((ushort)256); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)w);

        // Tag 257: ImageLength
        writer.Write((ushort)257); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)h);

        // Tag 258: BitsPerSample (SHORT × 4 = 8 字节 > 4 → 写偏移)
        writer.Write((ushort)258); writer.Write((ushort)3); writer.Write((uint)4);
        writer.Write(bpsOffset);

        // Tag 259: Compression (8 = Adobe Deflate)
        WriteShortEntry(writer, 259, 8);

        // Tag 262: PhotometricInterpretation (2 = RGB)
        WriteShortEntry(writer, 262, 2);

        // ⚠ StripOffsets(273) / StripByteCounts(279)：单 strip 时 count=1 = 恰好 4 字节 ⇒ 必须内联
        // （旧实现把内联值也写成值区指针 → 读者把指针值当 strip 偏移 → 全黑，2026-09-19 实测）。
        // 多 strip（2026-10-31）时 count=stripCount，值 >4 字节 ⇒ 合法地转为值区数组 + 回填。
        // ⚠ BitsPerSample 值区恒为 4×ushort = 8 字节，与 bitDepth 无关。
        // 之前写成 4*bitDepth/8，8-bit 输出时算成 4 → 偏移错误，破坏后续结构。
        writer.Write((ushort)273); writer.Write((ushort)4); writer.Write((uint)stripCount);
        long stripOffsetValuePos = -1;
        if (multiStrip)
        {
            writer.Write(stripOffsetsArrayOffset);
        }
        else
        {
            stripOffsetValuePos = writer.BaseStream.Position;
            writer.Write((uint)0);
        }

        // Tag 277: SamplesPerPixel (4 = RGBA)
        WriteShortEntry(writer, 277, 4);

        // Tag 278: RowsPerStrip —— 各条等行高，仅末条可短（行数 = h − (n−1)×RowsPerStrip，
        // 读端按此推算，是 TIFF 6.0 的标准形状；不整除时短末条【合法】，不许把余数摊平）。
        writer.Write((ushort)278); writer.Write((ushort)4); writer.Write((uint)1);
        writer.Write((uint)rowsPerStrip);

        // Tag 279: StripByteCounts —— 同 273：单条内联回填，多条写值区数组偏移
        writer.Write((ushort)279); writer.Write((ushort)4); writer.Write((uint)stripCount);
        long byteCountValuePos = -1;
        if (multiStrip)
        {
            writer.Write(stripCountsArrayOffset);
        }
        else
        {
            byteCountValuePos = writer.BaseStream.Position;
            writer.Write((uint)0);
        }

        // Tag 282: XResolution (RATIONAL = 8 字节 > 4 → 写偏移)
        writer.Write((ushort)282); writer.Write((ushort)5); writer.Write((uint)1);
        writer.Write(xresOffset);

        // Tag 283: YResolution
        writer.Write((ushort)283); writer.Write((ushort)5); writer.Write((uint)1);
        writer.Write(yresOffset);

        // Tag 296: ResolutionUnit (2 = inch)
        WriteShortEntry(writer, 296, 2);

        // ═══ Tag 338: ExtraSamples = 2 (Unassociated alpha) —— 2026-10-31 补上的容器契约 ═══
        // 本编码器发 SamplesPerPixel=4 + PhotometricInterpretation=2(RGB)：RGB 只解释掉 3 个样本，
        // 第 4 个样本的含义【必须】由 338 声明，否则是留给读者猜。规范原文（TIFF 6.0 标签表，Tag 338）：
        //   "Specifies that each pixel has m extra components whose interpretation is defined by one
        //    of the values listed below. When this field is used, the SamplesPerPixel field has a
        //    value greater than the PhotometricInterpretation field suggests."
        //   可选值只有三个：0 = Unspecified data / 1 = Associated alpha data (with pre-multiplied
        //   color) / 2 = Unassociated alpha data。
        // 取 2 不是猜的，是按 §18 "Associated Alpha Handling" 的硬定义排除掉 1 的：那一节要求声明为 1
        // 时 "the RGB components must be stored premultiplied by the associated alpha component …
        // (A*r,A*g,A*b,A) must be stored in the file"。而 ConvertToRgba 全文只做通道搬运与位宽扩展
        // （8→16 是 v*257），没有任何一处把 R/G/B 乘过 A ⇒ 我们的字节是直通（straight）alpha，
        // 照 1 声明就是撒谎；0（unspecified）等于把已经存在的 alpha 说成"含义不明"，同样不诚实。
        // 实测（2026-10-31，外部解码器裁判，见 tests/manual/safety/ 同族做法）：同一批字节把值改成 1
        // 会被读者真的分派到预乘解释 —— Pillow 的 rawmode 由 RGBA 翻成 RGBa 并反乘（8-bit 显示值
        // 偏离直通真值 max|Δ|=248、2037/3072 个样本不同），tifffile 读出 ASSOCALPHA vs UNASSALPHA。
        // ⚠ 与 273/279 同一条铁律（TIFF 6.0 §2：值的字节数 ≤ 4 必须【直接】写在条目 value 域）：
        //   SHORT × count=1 = 2 字节 ⇒ 内联，绝不写值区指针。读者只看字节数，无从分辨"这是指针"，
        //   写错就是把值区绝对偏移的低 2 字节成语义值读走 —— 实测 tifffile 对那种文件报
        //   "182 is not a valid EXTRASAMPLE"，声明静默丢失，与 2026-09-19 那次全黑同一族。
        //   WriteShortEntry 的 value 域 = 2 字节值 + 2 字节 0 填充，条目恒 12 字节。
        // 位序：IFD 条目按 tag 升序 ⇒ 338 排在 296(ResolutionUnit) 之后、34675(ICC) 之前，
        //   不是排在 279 与 282 之间（338 > 296 > 282 > 279）。内联 ⇒ 不占值区，值区布局不变。
        WriteShortEntry(writer, 338, 2);

        // ICC Profile tag (if present)
        if (hasIcc)
        {
            // Tag 34675: ICCProfile (UNDEFINED)
            writer.Write((ushort)34675); writer.Write((ushort)7); writer.Write((uint)iccProfile!.Length);
            writer.Write((uint)iccOffset);
        }

        // Next IFD offset (0 = no more IFDs)
        writer.Write((uint)0);

        // ── 值区 ──
        // ⚠ 必须先占满整片值区再写像素数据：否则后续 Seek 回填会覆盖压缩数据。
        // BitsPerSample (4 × SHORT = 8 字节)
        for (int i = 0; i < 4; i++)
            writer.Write((ushort)bitDepth);

        // 占位（随后 Seek 回填）：XRes / YRes（RATIONAL 各 8 字节 > 4，允许间接）
        // ⚠ StripOffsets / StripByteCounts 只在【多 strip】时在此占位（见上）；单 strip 是内联值，
        //   回填位置见 stripOffsetValuePos / byteCountValuePos。
        writer.Write((uint)0); writer.Write((uint)0);   // XResolution 72/1
        writer.Write((uint)0); writer.Write((uint)0);   // YResolution 72/1
        if (hasIcc) writer.Write(iccProfile!);          // ICCProfile
        if (multiStrip)
        {
            // StripOffsets / StripByteCounts 数组占位（随后 Seek 回填）：各 stripCount 个 uint。
            for (int s = 0; s < stripCount; s++) writer.Write((uint)0);
            for (int s = 0; s < stripCount; s++) writer.Write((uint)0);
        }

        // ── Strip pixel data (Deflate compressed) ──
        // 此时流位置必须等于预先规划的值区终点，否则说明值区布局与写入顺序不一致。
        // ⚠ 这条必须是 Release 也生效的【拒绝写出】，不能停留在 Debug.Assert：
        //   多 strip 之后值区里多了两组 LONG 数组，规划值与写入顺序一旦分叉，产物是
        //   "文件能建、条偏移指向错误位置"的坏 TIFF —— 而坏 TIFF 的表现与 2026-09-19
        //   那次【全黑】同一族（读者按错偏移解出垃圾）。宁可这张图落不到盘上被保底接住，
        //   也不要把一个没人会发现的坏文件交出去。
        long pixelDataStart = writer.BaseStream.Position;
        if (pixelDataStart != expectedPixelDataStart)
            throw new InvalidOperationException(
                $"TIFF 值区布局与写入顺序不一致: 实际={pixelDataStart} 预期={expectedPixelDataStart}（拒绝写出坏件）");

        // ── 像素：转换直写各 strip 自己的缓冲（行并行），再逐条压缩（条并行）──
        // 2026-10-31：本文件此前全文 0 处 Parallel —— 转 RGBA 单线程逐像素、单 strip 的 deflate
        // 没有并行入口，全尺寸 TIFF 耗时至今零读数。strip 切开后两条都成了可并行的独立单元。
        // 不做"整帧一块 raw 再切片"：直写分条缓冲省掉一整帧的额外拷贝，峰值内存仍是 ~1 帧。
        var stripRaw = new byte[stripCount][];
        for (int s = 0; s < stripCount; s++)
        {
            long rows = Math.Min((long)rowsPerStrip, (long)h - (long)s * rowsPerStrip);
            stripRaw[s] = new byte[(int)(rows * rowBytes)];
        }
        ConvertToRgba(bgra, w, h, rowsPerStrip, stripRaw, srcIs16, bitDepth);

        // ═══ 压缩：Tag 8 = "Adobe Deflate" = 【zlib 封装】的 deflate（RFC1951+RFC1950 头）═══
        // DeflateStream 写的是【裸】deflate（无 zlib 头），libtiff/ffmpeg 按 tag 8 解时等头 →
        // "incorrect header check" → 整幅图解不出来。要么换成 tag 32946( raw Deflate )，
        // 要么保住 zlib 封装 —— 取后者：32946 的兼容性更差。DeflateZlib 两条臂都输出 zlib 封装。
        // 2026-10-29：档位接「压缩强度」；Smallest == 这里历史上硬编码的 level 9。
        // 2026-10-31：实现可选 libdeflate，缺失/失败自动回退内置 zlib-ng（见 DeflateZlib）。
        // 2026-10-31 多 strip：每 strip 一条独立 zlib 流（容器与压缩字节随分条变化，解压后像素不变）。
        // 单 strip 走与历史【逐字相同】的一次整帧压缩调用 —— 这条分支保的是"压缩字节不变"。
        // ⚠ 2026-10-31：原先写成"小图产物字节不变是这条分支的存在理由"，同日补 338 之后不再成立
        //   （IFD 长 12 字节 ⇒ 整个文件长 12 字节）。留下的承诺只有像素与 deflate 流这两层。
        var stripData = new byte[stripCount][];
        if (!multiStrip)
        {
            var raw = stripRaw[0];
            var compressed = DeflateZlib.Compress(raw, CompressionEffortMap.PngTiffDeflateLevel(effort));
            stripData[0] = compressed;
        }
        else
        {
            int level = CompressionEffortMap.PngTiffDeflateLevel(effort);
            // 整帧判一次后端，再逐条压：判据是"这一帧可不可压缩"，不是"这一条可不可压缩"，
            // 逐条各判一次既把几毫秒放大成 ~64 次，也会让同一张图的不同条落到不同后端。
            bool useNative = DeflateZlib.SelectNativeBackend(stripRaw, level);
            System.Threading.Tasks.Parallel.For(0, stripCount, s =>
            {
                // 每任务的输入/输出槽位互不相同 ⇒ 无跨条共享可变状态；
                // 压完立刻释放该条 raw，把并行期间的峰值内存压回 ~1 帧。
                stripData[s] = DeflateZlib.CompressWithDecision(stripRaw[s], level, useNative);
                stripRaw[s] = null!;
            });
        }

        long pos = pixelDataStart;
        var stripOffsets = new uint[stripCount];
        var stripByteCounts = new uint[stripCount];
        for (int s = 0; s < stripCount; s++)
        {
            stripOffsets[s] = (uint)pos;
            stripByteCounts[s] = (uint)stripData[s].Length;
            writer.Write(stripData[s]);
            pos += stripData[s].Length;
        }
        // classic TIFF 的偏移/计数都是 uint：>4 GiB 时上面的回写已静默回绕，必须在这里失败
        // （AtomicFileWriter 会删掉临时文件，最终路径不留坏件）。正常截图远达不到这条界。
        if (pos > uint.MaxValue)
            throw new InvalidOperationException($"TIFF(classic) 产物超过 4 GiB ({pos} B)，拒绝写出");

        // ── 回填 strip 偏移与字节数（单条 = 条目 value 域内联；多条 = 值区数组）──
        if (multiStrip)
        {
            writer.BaseStream.Seek(stripOffsetsArrayOffset, SeekOrigin.Begin);
            for (int s = 0; s < stripCount; s++) writer.Write(stripOffsets[s]);
            writer.BaseStream.Seek(stripCountsArrayOffset, SeekOrigin.Begin);
            for (int s = 0; s < stripCount; s++) writer.Write(stripByteCounts[s]);
        }
        else
        {
            writer.BaseStream.Seek(stripOffsetValuePos, SeekOrigin.Begin);
            writer.Write(stripOffsets[0]);
            writer.BaseStream.Seek(byteCountValuePos, SeekOrigin.Begin);
            writer.Write(stripByteCounts[0]);
        }

        // ── XResolution / YResolution (72 DPI) ──
        writer.BaseStream.Seek(xresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1); // 72/1
        writer.BaseStream.Seek(yresOffset, SeekOrigin.Begin);
        writer.Write((uint)72); writer.Write((uint)1);

        // ICC 已在值区占位阶段写入，此处无需再处理
    }

    /// <summary>写入 SHORT 类型、count=1 的 IFD 条目。
    /// ⚠ TIFF 每个 IFD 条目恒为 12 字节，value 域固定占 4 字节。
    /// SHORT 值只占前 2 字节（II 小端 → 低地址），剩余 2 字节必须是 padding。
    /// 之前只写 2 字节，每个短条目少 2 字节 → 其后所有条目被解析器错位读取 → 文件非法。</summary>
    private static void WriteShortEntry(BinaryWriter w, ushort tag, ushort value)
    {
        w.Write(tag);           // tag   (2)
        w.Write((ushort)3);     // type  (2) = SHORT
        w.Write((uint)1);       // count (4)
        w.Write(value);         // 值占前 2 字节
        w.Write((ushort)0);     // 2 字节 padding → 条目共 12 字节
    }

    /// <summary>BGRA → RGBA，并按需做 8/16-bit 转换；逐行并行直写各 strip 的缓冲。
    /// 16-bit 输入：每像素 8 字节、通道顺序 BGRA、每通道大端（与 PNG 16-bit 中转格式一致，
    /// 见 FormatHelper.Rgba16ToBgra16Bytes）。
    /// 输出恒为 TIFF 的 RGBA 顺序 + 小端（TIFF header 为 II）。
    /// ⚠ 之前 16-bit 路径按 si = i*4 步进（把 16-bit 数据当 8-bit 读）、Alpha 未 *257
    /// → 通道错位且整图近乎全透明。
    /// <para>2026-10-31 并行化：这是全尺寸 TIFF 压缩前的最长 CPU 段（4K 16-bit ≈ 50 MiB 逐像素
    /// 搬运）。映射是纯逐像素的，每行只读本行输入、只写所属 strip 缓冲中自己那一段 ⇒ 跨行无
    /// 共享可变状态，产物与串行旧版逐字节一致（可分条：行 → strip = y/rowsPerStrip 恒定切块）。</para></summary>
    private static void ConvertToRgba(byte[] bgra, int w, int h, int rowsPerStrip, byte[][] stripRaw,
        bool srcIs16, int bitDepth)
    {
        bool outIs16 = bitDepth > 8;
        int outStride = outIs16 ? 8 : 4;
        int rowBytes = w * outStride;

        System.Threading.Tasks.Parallel.For(0, h, y =>
        {
            int s = y / rowsPerStrip;
            var dst = stripRaw[s];
            int di0 = (y - s * rowsPerStrip) * rowBytes;
            long rowStart = (long)y * w;
            for (int x = 0; x < w; x++)
            {
                ushort r, g, b, a;
                if (srcIs16)
                {
                    int si = (int)((rowStart + x) * 8);
                    b = ReadU16BE(bgra, si);
                    g = ReadU16BE(bgra, si + 2);
                    r = ReadU16BE(bgra, si + 4);
                    a = ReadU16BE(bgra, si + 6);
                }
                else
                {
                    int si = (int)((rowStart + x) * 4);
                    b = (ushort)(bgra[si] * 257);      // 8→16 扩展: v * 65535 / 255
                    g = (ushort)(bgra[si + 1] * 257);
                    r = (ushort)(bgra[si + 2] * 257);
                    a = (ushort)(bgra[si + 3] * 257);
                }

                int di = di0 + x * outStride;
                if (outIs16)
                {
                    WriteU16LE(dst, di, r);
                    WriteU16LE(dst, di + 2, g);
                    WriteU16LE(dst, di + 4, b);
                    WriteU16LE(dst, di + 6, a);
                }
                else
                {
                    dst[di] = (byte)(r >> 8);
                    dst[di + 1] = (byte)(g >> 8);
                    dst[di + 2] = (byte)(b >> 8);
                    dst[di + 3] = (byte)(a >> 8);
                }
            }
        });
    }

    private static ushort ReadU16BE(byte[] buf, int off) => (ushort)((buf[off] << 8) | buf[off + 1]);

    private static void WriteU16LE(byte[] buf, int off, ushort v)
    {
        buf[off] = (byte)(v & 0xFF);
        buf[off + 1] = (byte)(v >> 8);
    }
}
