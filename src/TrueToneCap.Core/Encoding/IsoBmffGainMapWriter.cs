// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Core/Encoding/IsoBmffGainMapWriter.cs
//
// ISO-BMFF（AVIF）增益图【容器写出器】—— 纯托管、零外部依赖。
//
// 移植自隔壁 ffmpegPictureUI 的同名写出器（768 行），但三处【必须改】，照搬就是错的：
//   ① 不 patch 底图 colr：隔壁的底图来自 ffmpeg `avif` muxer，实测它【不采纳】-color_primaries
//      （写出 primaries=2/transfer=2 unspecified），所以隔壁必须重写一版正确的 nclx。
//      我们的底图来自 avifenc `--cicp`，2026-11-03 实测产物 colr 恒为 nclx 且 primaries/transfer
//      就是传进去的值 ⇒ patch 反而会把一份【已经对】的标注改坏。这里只在需要时给 tmap 项
//      追加一张【替代图（HDR 侧）】的 colr，底图原样搬运。
//   ② Temporal Delimiter 保留为【防御】：实测 avifenc 每个 item 首字节已是 0x12(TD)，
//      所以这段对当前输入是 no-op；留着是因为它廉价且能挡住"哪天换成无 TD 的编码器"。
//   ③ auxC（alpha 副 item）必须【拒绝】而非静默丢弃：写出器只搬主 item，若底图带了 alpha 副图
//      就会产出一张"看着成功、实际 alpha 没了"的件。实测 avifenc 对【恒不透明】的 RGBA 会丢掉
//      alpha（infe=1），只有真半透明才出 auxC ⇒ 检测到 auxC 就拒写，绝不出说谎的容器。
//
// 输入 = 「底图单图 AVIF + 增益图单图 AVIF + ISO 21496-1 tmap 载荷」，三者都由 avifenc 编出。
// 输出 = 带 tmap 派生项的三 item 容器，结构与 libavif 自家增益图产物同形（avifdec --info 认）：
//   ftyp: major=avif, compat 含 tmap
//   iinf: 1=av01(底图) 2=tmap 3=av01(增益图)
//   iref: dimg  from 2(tmap) -> [1(底图), 3(增益图)]   ← reference_count 必写，libavif 强校验
//   grpl: altr(tmap, 底图)                             ← 缺它 avifdec 报 "Gain map: Absent"
//   iloc: item2 的 extent = tmap 载荷（1 + 21 + 通道数×40 字节）
//
// ⚠ 偏移必须两遍算：meta 的长度依赖 iloc，而 iloc 里要填的偏移又依赖 meta 结束位置 ⇒
//   先用零偏移建一遍量长度、再重建。这是本格式最容易写错的地方（隔壁踩实过）。

namespace TrueToneCap.Core.Encoding;

using System.Text;

/// <summary>
/// 把两张单图 AVIF + 一份 ISO 21496-1 元数据组装成带 <c>tmap</c> 派生项的增益图容器。
/// <para>与 <see cref="JpegGainMapEncoder"/> 共用同一份 ISO 21496-1 生成器
/// （<see cref="BuildTmapPayload"/> 只是在前面加一个 version 字节），
/// 于是 JPEG 出口与 AVIF 出口的增益语义必然一致 —— 再写一份公式迟早漂移。</para>
/// </summary>
public static class IsoBmffGainMapWriter
{
    /// <summary>组装结果。<see cref="Data"/>=null ⇒ 失败，<see cref="FailReason"/> 点名原因。</summary>
    public sealed class BuildResult
    {
        public byte[]? Data;
        public string? FailReason;
        public int BaseItemBytes;
        public int GainMapItemBytes;
        public int TmapPayloadBytes;
        public bool Ok => Data is not null;
    }

    // 单个 item 的 id 约定（与 libavif 产物一致）
    private const int ItemBase = 1;
    private const int ItemTmap = 2;
    private const int ItemGainMap = 3;

    // tmap 载荷里 ISO 21496-1 flags 字节的偏移：载荷首字节是 u8 version，
    // ISO 头 = min_version(u16) + writer_version(u16) + flags(u8) ⇒ 1 + 4 = 5。
    internal const int TmapFlagsOffset = 1 + 4;
    // ISO 21496-1 flags 的 multi-channel 位（kIsMultiChannelMask）
    private const byte IsMultiChannelMask = 0x80;

    /// <summary>
    /// 组装带 <c>tmap</c> 派生项的 AVIF。
    /// </summary>
    /// <param name="baseAvif">底图（SDR 意图）单图 AVIF 字节。</param>
    /// <param name="gainmapAvif">增益图单图 AVIF 字节（Gray⇒1 平面 / Rgb⇒3 平面）。</param>
    /// <param name="tmapPayload">ISO 21496-1 tmap 载荷，<b>含首个 version 字节</b>（见 <see cref="BuildTmapPayload"/>）。</param>
    /// <param name="alternateTransfer">
    /// 替代图（HDR 侧）的 CICP transfer，写进给 tmap 追加的 colr；&lt; 0 ⇒ 不给 tmap 加 colr。
    /// 默认 16 = PQ —— 与 libavif 自家增益图样本的 "Alternate image / Transfer Char. : 16" 同档。
    /// <b>底图的 colr 一律原样保留</b>（avifenc 的 --cicp 已经写对，见文件头理由①）。</param>
    /// <param name="alternateMatrix">替代图 nclx 的 matrix；仅在 alternateTransfer ≥ 0 时生效。</param>
    /// <param name="alternatePrimaries">替代图 nclx 的 primaries；仅在 alternateTransfer ≥ 0 时生效。</param>
    public static BuildResult Build(byte[] baseAvif, byte[] gainmapAvif, byte[] tmapPayload,
        int alternateTransfer = 16, int alternateMatrix = 6, int alternatePrimaries = 1)
    {
        var r = new BuildResult();
        try
        {
            if (baseAvif is null || baseAvif.Length == 0) { r.FailReason = "[GainMap-Avif] 底图 AVIF 为空"; return r; }
            if (gainmapAvif is null || gainmapAvif.Length == 0) { r.FailReason = "[GainMap-Avif] 增益图 AVIF 为空"; return r; }
            if (tmapPayload is null || tmapPayload.Length == 0) { r.FailReason = "[GainMap-Avif] tmap 载荷为空"; return r; }

            if (!TryExtractSingle(baseAvif, "base", out var baseImg, out var bErr)) { r.FailReason = bErr; return r; }
            if (!TryExtractSingle(gainmapAvif, "gainmap", out var gmImg, out var gErr)) { r.FailReason = gErr; return r; }

            // 增益图不得大于底图（ISO 21496-1 要求增益图是底图的降采样；反向会解码错位）
            if (baseImg.Width > 0 && gmImg.Width > 0
                && (gmImg.Width > baseImg.Width || gmImg.Height > baseImg.Height))
            {
                r.FailReason = $"[GainMap-Avif] 增益图 {gmImg.Width}x{gmImg.Height} 大于底图 {baseImg.Width}x{baseImg.Height}（应为其降采样）";
                return r;
            }

            // 「声明 == 交付」的最后一道门：tmap flags bit7 声明的通道数必须等于增益图 pixi 的平面数。
            // 「声明多通道、数据是单平面」=说谎 ⇒ 在这里拒写，不静默出货。
            // （libavif 以交付格式定通道数：gray ⇒ 1；libultrahdr 读侧信流不信 flag ⇒ 一致性是生产者义务。）
            if (tmapPayload.Length > TmapFlagsOffset)
            {
                bool declaredMulti = (tmapPayload[TmapFlagsOffset] & IsMultiChannelMask) != 0;
                int declaredChannels = declaredMulti ? 3 : 1;
                int? deliveredPlanes = PixiChannelCount(gmImg.Props);
                if (deliveredPlanes.HasValue && deliveredPlanes.Value != declaredChannels)
                {
                    r.FailReason =
                        $"[GainMap-Avif] 声明与交付不符，已拒写：tmap flags bit7={(declaredMulti ? 1 : 0)} "
                      + $"⇒ 声明 {declaredChannels} 通道，而增益图 item 的 pixi 实为 {deliveredPlanes.Value} 平面。";
                    return r;
                }
            }

            r.BaseItemBytes = baseImg.ItemData.Length;
            r.GainMapItemBytes = gmImg.ItemData.Length;
            r.TmapPayloadBytes = tmapPayload.Length;

            // ── 属性盒（ipco）与 item→属性关联（ipma），索引 1-based ──
            var ipcoChildren = new List<byte[]>();
            var assocBase = new List<int>();
            foreach (var p in baseImg.Props) { ipcoChildren.Add(p); assocBase.Add(ipcoChildren.Count); }
            var assocGain = new List<int>();
            foreach (var p in gmImg.Props) { ipcoChildren.Add(p); assocGain.Add(ipcoChildren.Count); }

            // tmap 是派生项，本身无码流；给它挂底图的 ispe/pixi 以声明尺寸与精度（与 libavif 样本同形）
            var assocTmap = new List<int>();
            int ispeIdx = baseImg.Props.FindIndex(p => IsBox(p, "ispe"));
            if (ispeIdx >= 0) assocTmap.Add(ispeIdx + 1);
            var basePixi = baseImg.Props.Find(p => IsBox(p, "pixi"));
            if (basePixi is not null) { ipcoChildren.Add(basePixi); assocTmap.Add(ipcoChildren.Count); }
            // 替代图（HDR 侧）的色度声明：libultrahdr avifultrahdr.cpp 同口径为 tmap 单独建 nclx。
            // 缺它时容器仍可解，但替代图色度只能从底图 colr 猜 ⇒ 标注与语义不符。
            if (alternateTransfer >= 0)
            {
                bool baseFullRange = baseImg.Props.Find(p => IsBox(p, "colr")) is byte[] c && (ReadNclxFullRange(c) ?? false);
                ipcoChildren.Add(Box("colr", Concat(Ascii("nclx"), U16(alternatePrimaries), U16(alternateTransfer),
                    U16(alternateMatrix), new[] { (byte)(baseFullRange ? 0x80 : 0x00) })));
                assocTmap.Add(ipcoChildren.Count);
            }

            var ftyp = BuildFtyp();
            var baseBytes = baseImg.ItemData;
            var gmBytes = gmImg.ItemData;

            // 增益图 item 必须带 Temporal Delimiter（TD）。实测 avifenc 已带 ⇒ 这里是防御性补，
            // 首字节不是 TD（OBU type 2）时才补 0x12 0x00，避免对当前正确输入重复 prepend。
            if (gmBytes.Length >= 1 && ((gmBytes[0] >> 3) & 0x0F) != 2)
                gmBytes = Concat(new byte[] { 0x12, 0x00 }, gmBytes);

            // ── 两遍法：先零偏移量长度，再填真偏移 ──
            var metaProbe = BuildMeta(ipcoChildren, assocBase, assocTmap, assocGain, tmapPayload.Length, baseBytes.Length, gmBytes.Length, 0, 0, 0);
            int ftypLen = ftyp.Length;
            int metaLen = metaProbe.Length;
            const int mdatHeaderLen = 8;
            long o1 = ftypLen + metaLen + mdatHeaderLen;
            long o2 = o1 + baseBytes.Length;
            long o3 = o2 + tmapPayload.Length;
            if (o3 + gmBytes.Length > int.MaxValue) { r.FailReason = "[GainMap-Avif] 产物超过 2 GiB（32 位 box 长度上限）"; return r; }

            var meta = BuildMeta(ipcoChildren, assocBase, assocTmap, assocGain, tmapPayload.Length, baseBytes.Length, gmBytes.Length, (uint)o1, (uint)o2, (uint)o3);
            if (meta.Length != metaLen)
            {
                // 偏移是定长 u32 ⇒ 两遍长度必须一致；不一致说明实现被改坏了（不静默继续）
                r.FailReason = $"[GainMap-Avif] 内部错误：meta 两遍长度不一致（{metaLen} vs {meta.Length}）";
                return r;
            }

            var mdat = Box("mdat", Concat(baseBytes, tmapPayload, gmBytes));
            var outBuf = new byte[ftypLen + metaLen + mdat.Length];
            int w = 0;
            Array.Copy(ftyp, 0, outBuf, w, ftyp.Length); w += ftyp.Length;
            Array.Copy(meta, 0, outBuf, w, meta.Length); w += meta.Length;
            Array.Copy(mdat, 0, outBuf, w, mdat.Length);

            r.Data = outBuf;
            return r;
        }
        catch (Exception ex)
        {
            r.FailReason = $"[GainMap-Avif] 组装异常 {ex.GetType().Name}: {ex.Message}";
            return r;
        }
    }

    /// <summary>
    /// 构建 tmap 载荷 = <c>u8 version(0)</c> + ISO 21496-1 GainMapMetadata。
    /// 后半段与 JPEG APP2 载荷逐字节同构（libavif avifWriteToneMappedImagePayload），
    /// 故直接复用 <see cref="JpegGainMapEncoder.BuildIso21496Metadata"/>，只多一个前导 version 字节。
    /// </summary>
    public static byte[] BuildTmapPayload(byte[] iso21496Metadata)
    {
        var buf = new byte[1 + iso21496Metadata.Length];
        buf[0] = 0;                                      // version
        Array.Copy(iso21496Metadata, 0, buf, 1, iso21496Metadata.Length);
        return buf;
    }

    // ═══════════════════════════════════════════════════════════════
    //  容器构建
    // ═══════════════════════════════════════════════════════════════

    private static byte[] BuildFtyp()
        => Box("ftyp", Concat(Ascii("avif"), U32(0),
               Ascii("avif"), Ascii("mif1"), Ascii("miaf"), Ascii("MA1A"), Ascii("tmap")));

    private static byte[] BuildMeta(List<byte[]> ipcoChildren, List<int> assocBase, List<int> assocTmap,
        List<int> assocGain, int tmapLen, int baseLen, int gmLen, uint o1, uint o2, uint o3)
    {
        var hdlr = FullBox("hdlr", 0, 0, Concat(U32(0), Ascii("pict"), new byte[12], new byte[] { 0 }));
        var pitm = FullBox("pitm", 0, 0, U16(ItemBase));

        var iinfPayload = new MemoryStream();
        var iw = new BinaryWriter(iinfPayload);
        WriteBe16(iw, 3);
        iw.Write(BuildInfe(ItemBase, "av01"));
        iw.Write(BuildInfe(ItemTmap, "tmap"));
        iw.Write(BuildInfe(ItemGainMap, "av01"));
        iw.Flush();
        var iinf = FullBox("iinf", 0, 0, iinfPayload.ToArray());

        // ⚠ 必须写 reference_count：ISO/IEC 14496-12 的 SingleItemTypeReferenceBox 含该字段，
        //   libavif 强校验 —— 缺它 avifdec 报 "box[dimg] for 'tmap' item 2 must have exactly 2 entries"。
        var dimg = Box("dimg", Concat(U16(ItemTmap), U16(2), U16(ItemBase), U16(ItemGainMap)));
        var iref = FullBox("iref", 0, 0, dimg);

        var ipco = Box("ipco", Concat(ipcoChildren.ToArray()));
        var ipma = BuildIpma(assocBase, assocTmap, assocGain);
        var iprp = Box("iprp", Concat(ipco, ipma));

        // altr（alternative entity group）不可省：libavif 靠它把 tmap 识别为「替代图」，
        // 缺它容器仍可解、主图也能解，但 avifdec 报 "Gain map: Absent"。entity 顺序照 libavif：tmap 在前、底图在后。
        var altr = FullBox("altr", 0, 0, Concat(U32(6), U32(2), U32(ItemTmap), U32(ItemBase)));
        var grpl = Box("grpl", altr);

        var iloc = BuildIloc(tmapLen, baseLen, gmLen, o1, o2, o3);

        return FullBox("meta", 0, 0, Concat(hdlr, pitm, iinf, iref, iprp, grpl, iloc));
    }

    private static byte[] BuildInfe(int itemId, string itemType)
        => FullBox("infe", 2, 0, Concat(U16(itemId), U16(0), Ascii(itemType), new byte[] { 0 }));

    private static byte[] BuildIpma(List<int> assocBase, List<int> assocTmap, List<int> assocGain)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteBe32(w, 3);
        WriteIpmaEntry(w, ItemBase, assocBase);
        WriteIpmaEntry(w, ItemTmap, assocTmap);
        WriteIpmaEntry(w, ItemGainMap, assocGain);
        w.Flush();
        return FullBox("ipma", 0, 0, ms.ToArray());
    }

    private static void WriteIpmaEntry(BinaryWriter w, int itemId, List<int> assoc)
    {
        WriteBe16(w, itemId);
        w.Write((byte)assoc.Count);
        foreach (var a in assoc)
            w.Write((byte)(a > 0x7F ? 0x7F : (a | 0x80)));   // index 7 位 + essential bit7
    }

    private static byte[] BuildIloc(int tmapLen, int baseLen, int gmLen, uint o1, uint o2, uint o3)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write((byte)0x44);   // offset_size=4, length_size=4
        w.Write((byte)0x00);   // base_offset_size=0, index_size=0
        WriteBe16(w, 3);
        WriteIlocEntry(w, ItemBase, o1, baseLen);
        WriteIlocEntry(w, ItemTmap, o2, tmapLen);
        WriteIlocEntry(w, ItemGainMap, o3, gmLen);
        w.Flush();
        return FullBox("iloc", 0, 0, ms.ToArray());
    }

    private static void WriteIlocEntry(BinaryWriter w, int itemId, uint offset, int length)
    {
        WriteBe16(w, itemId);
        WriteBe16(w, 0);       // data_reference_index
        WriteBe16(w, 1);       // extent_count
        WriteBe32(w, offset);
        WriteBe32(w, (uint)length);
    }

    // ═══════════════════════════════════════════════════════════════
    //  单图 AVIF 解析（取 item 字节与属性盒）
    // ═══════════════════════════════════════════════════════════════

    private sealed class SingleImage
    {
        public byte[] ItemData = Array.Empty<byte>();
        public List<byte[]> Props = new();
        public int Width;
        public int Height;
        public int AuxItemCount;      // 除主图外的 item 数（alpha 副图会在这里 >0）
    }

    private static bool TryExtractSingle(byte[] d, string what, out SingleImage img, out string err)
    {
        img = new SingleImage();
        err = "";
        // 内联 ftyp 检查（隔壁调的是外部 IsoBmffGainMapProbe，本仓不引它）
        if (d.Length < 12 || Encoding.ASCII.GetString(d, 4, 4) != "ftyp")
        {
            err = $"[GainMap-Avif] {what} 不是 ISO-BMFF（缺 ftyp）";
            return false;
        }

        BoxInfo? meta = null;
        foreach (var b in Boxes(d, 0, d.Length)) if (b.Type == "meta") { meta = b; break; }
        if (meta is null) { err = $"[GainMap-Avif] {what} 缺 meta 盒"; return false; }

        int bodyStart = meta.Value.ContentOffset + 4;        // meta 是 FullBox
        int bodyEnd = meta.Value.Offset + meta.Value.Size;

        int primary = -1;
        BoxInfo? iinf = null, iloc = null, iprp = null;
        foreach (var b in Boxes(d, bodyStart, bodyEnd))
        {
            switch (b.Type)
            {
                case "pitm":
                    {
                        int q = b.ContentOffset;
                        if (b.ContentLength >= 6 && d[q] == 0) primary = Be16(d, q + 4);
                        else if (b.ContentLength >= 8) primary = (int)Be32(d, q + 4);
                        break;
                    }
                case "iinf": iinf = b; break;
                case "iloc": iloc = b; break;
                case "iprp": iprp = b; break;
            }
        }
        if (primary < 0) { err = $"[GainMap-Avif] {what} 缺 pitm（无法定位主图）"; return false; }
        if (iloc is null) { err = $"[GainMap-Avif] {what} 缺 iloc"; return false; }

        var items = new Dictionary<int, string>();
        if (iinf is not null) ParseIinfTypes(d, iinf.Value, items);
        img.AuxItemCount = items.Count - 1;
        if (items.TryGetValue(primary, out var ptype) && ptype != "av01")
        {
            err = $"[GainMap-Avif] {what} 主 item 类型 '{ptype}'（本写出器只支持 av01）";
            return false;
        }
        // 底图带副 item（半透明截图 ⇒ avifenc 写 auxC alpha）⇒ 拒绝：写出器只搬主 item，
        // 静默丢 alpha 就是"看着成功、实际少了一层"的谎件。调用方应改走 PQ 容器或先去 alpha。
        if (what == "base" && img.AuxItemCount > 0)
        {
            err = $"[GainMap-Avif] 底图含 {img.AuxItemCount} 个副 item（alpha）⇒ 拒写：容器只搬主图会静默丢 alpha";
            return false;
        }

        if (!TrySliceItem(d, iloc.Value, primary, out var itemBytes, out var sErr))
        {
            err = $"[GainMap-Avif] {what} {sErr}";
            return false;
        }
        img.ItemData = itemBytes;

        // ⚠ 必须先声明再 out：`if (cond) M(out var x);` 里 `out var` 的作用域只到该内嵌语句。
        int w = 0, h = 0;
        if (iprp is not null) ParseProps(d, iprp.Value, primary, img, out w, out h);
        img.Width = w;
        img.Height = h;
        return true;
    }

    private static void ParseIinfTypes(byte[] d, BoxInfo iinf, Dictionary<int, string> items)
    {
        int p = iinf.ContentOffset;
        int end = iinf.Offset + iinf.Size;
        if (p + 4 > end) return;
        int ver = d[p];
        p += 4;
        int count;
        if (ver == 0) { if (p + 2 > end) return; count = Be16(d, p); p += 2; }
        else { if (p + 4 > end) return; count = (int)Be32(d, p); p += 4; }
        for (int i = 0; i < count && p + 8 <= end; i++)
        {
            if (!TryReadBox(d, p, end, out var b) || b.Type != "infe") return;
            int q = b.ContentOffset;
            int iver = d[q];
            q += 4;
            int itemId;
            if (iver >= 3) { if (q + 4 > b.Offset + b.Size) return; itemId = (int)Be32(d, q); q += 4; }
            else { if (q + 2 > b.Offset + b.Size) return; itemId = Be16(d, q); q += 2; }
            q += 2;                                          // item_protection_index
            if (iver >= 2 && q + 4 <= b.Offset + b.Size)
                items[itemId] = Encoding.ASCII.GetString(d, q, 4);
            p = b.Offset + b.Size;
        }
    }

    private static void ParseProps(byte[] d, BoxInfo iprp, int primary, SingleImage img, out int width, out int height)
    {
        width = 0; height = 0;
        var props = new List<BoxInfo>();
        Dictionary<int, List<int>>? ipma = null;
        int end = iprp.Offset + iprp.Size;
        foreach (var b in Boxes(d, iprp.ContentOffset, end))
        {
            if (b.Type == "ipco")
            {
                foreach (var pb in Boxes(d, b.ContentOffset, b.Offset + b.Size)) props.Add(pb);
            }
            else if (b.Type == "ipma") ipma = ParseIpmaMap(d, b);
        }

        var wanted = new List<int>();
        if (ipma is not null && ipma.TryGetValue(primary, out var list)) wanted.AddRange(list);
        else for (int i = 1; i <= props.Count; i++) wanted.Add(i);   // 无 ipma ⇒ 全给（保守）

        foreach (var ix in wanted)
        {
            if (ix < 1 || ix > props.Count) continue;
            var pb = props[ix - 1];
            var raw = new byte[pb.Size];
            Array.Copy(d, pb.Offset, raw, 0, pb.Size);
            img.Props.Add(raw);
            if (pb.Type == "ispe" && pb.ContentLength >= 12)
            {
                width = (int)Be32(d, pb.ContentOffset + 4);
                height = (int)Be32(d, pb.ContentOffset + 8);
            }
        }
    }

    private static Dictionary<int, List<int>> ParseIpmaMap(byte[] d, BoxInfo ipma)
    {
        var map = new Dictionary<int, List<int>>();
        int p = ipma.ContentOffset;
        int end = ipma.Offset + ipma.Size;
        if (p + 8 > end) return map;
        int ver = d[p];                                       // FullBox version：决定 item_id 宽度
        int flags = (d[p + 1] << 16) | (d[p + 2] << 8) | d[p + 3];
        p += 4;
        int count = (int)Be32(d, p); p += 4;
        bool wide = (flags & 1) != 0;                          // flags bit0 = 32 位属性索引
        int idSize = ver >= 1 ? 4 : 2;
        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 1 > end) return map;
            int itemId = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int ac = d[p++];
            var list = new List<int>();
            for (int k = 0; k < ac; k++)
            {
                if (p + (wide ? 2 : 1) > end) return map;
                int v = wide ? Be16(d, p) : d[p];
                p += wide ? 2 : 1;
                list.Add(v & (wide ? 0x7FFF : 0x7F));
            }
            map[itemId] = list;
        }
        return map;
    }

    private static bool TrySliceItem(byte[] d, BoxInfo iloc, int itemId, out byte[] bytes, out string err)
    {
        bytes = Array.Empty<byte>();
        err = "";
        int p = iloc.ContentOffset;
        int end = iloc.Offset + iloc.Size;
        if (p + 8 > end) { err = "iloc too short"; return false; }
        int ver = d[p];
        p += 4;
        int b0 = d[p], b1 = d[p + 1]; p += 2;
        int offSize = b0 >> 4, lenSize = b0 & 0xF;
        int baseSize = b1 >> 4, idxSize = b1 & 0xF;
        int count, idSize;
        if (ver < 2) { count = Be16(d, p); p += 2; idSize = 2; }
        else { count = (int)Be32(d, p); p += 4; idSize = 4; }

        for (int i = 0; i < count; i++)
        {
            if (p + idSize + 2 > end) break;
            int id = idSize == 4 ? (int)Be32(d, p) : Be16(d, p);
            p += idSize;
            int method = 0;
            if (ver == 1 || ver == 2) { method = Be16(d, p) & 0xF; p += 2; }
            p += 2;                                          // data_reference_index
            long baseOff = baseSize > 0 ? ReadBig(d, p, baseSize) : 0;
            p += baseSize;
            if (p + 2 > end) break;
            int ec = Be16(d, p); p += 2;
            var extents = new List<(long Off, long Len)>();
            for (int k = 0; k < ec; k++)
            {
                p += idxSize;
                long o = offSize > 0 ? ReadBig(d, p, offSize) : 0; p += offSize;
                long l = lenSize > 0 ? ReadBig(d, p, lenSize) : 0; p += lenSize;
                extents.Add((o, l));
            }
            if (id != itemId) continue;
            if (method != 0) { err = $"item {itemId} uses construction_method={method} (only 0 supported)"; return false; }
            long total = 0;
            foreach (var (_, l) in extents) total += l;
            if (total <= 0 || total > int.MaxValue) { err = $"item {itemId} bad length ({total})"; return false; }
            var buf = new byte[total];
            int w = 0;
            foreach (var (o, l) in extents)
            {
                long abs = baseOff + o;
                if (abs < 0 || abs + l > d.Length) { err = $"item {itemId} extent out of range"; return false; }
                Array.Copy(d, abs, buf, w, l);
                w += (int)l;
            }
            bytes = buf;
            return true;
        }
        err = $"item {itemId} not in iloc";
        return false;
    }

    private static int? PixiChannelCount(List<byte[]> props)
    {
        foreach (var p in props)
        {
            if (!IsBox(p, "pixi")) continue;
            if (p.Length < 14) continue;
            return p[12];
        }
        return null;
    }

    private static bool? ReadNclxFullRange(byte[] colrBox)
    {
        if (colrBox is null || colrBox.Length < 8 + 11) return null;
        if (Encoding.ASCII.GetString(colrBox, 8, 4) != "nclx") return null;
        const int FullRangeOffset = 8 + 4 + 2 + 2 + 2;   // = 18（固定偏移，不用"末字节"）
        return (colrBox[FullRangeOffset] & 0x80) != 0;
    }

    // ═══════════════════════════════════════════════════════════════
    //  字节工具
    // ═══════════════════════════════════════════════════════════════

    private readonly struct BoxInfo
    {
        public readonly string Type;
        public readonly int Offset;
        public readonly int Size;
        public readonly int HeaderSize;
        public BoxInfo(string type, int offset, int size, int headerSize)
        { Type = type; Offset = offset; Size = size; HeaderSize = headerSize; }
        public int ContentOffset => Offset + HeaderSize;
        public int ContentLength => Size - HeaderSize;
    }

    private static IEnumerable<BoxInfo> Boxes(byte[] d, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            if (!TryReadBox(d, p, end, out var b)) yield break;
            yield return b;
            p = b.Offset + b.Size;
            if (b.Size <= 0) yield break;
        }
    }

    private static bool TryReadBox(byte[] d, int pos, int end, out BoxInfo box)
    {
        box = default;
        if (pos < 0 || pos + 8 > end) return false;
        long sz = ((long)d[pos] << 24) | ((long)d[pos + 1] << 16) | ((long)d[pos + 2] << 8) | d[pos + 3];
        var type = Encoding.ASCII.GetString(d, pos + 4, 4);
        int hdr = 8;
        if (sz == 1)
        {
            if (pos + 16 > end) return false;
            sz = 0;
            for (int i = 0; i < 8; i++) sz = (sz << 8) | d[pos + 8 + i];
            hdr = 16;
        }
        else if (sz == 0) sz = end - pos;
        if (sz < hdr || pos + sz > end) return false;
        box = new BoxInfo(type, pos, (int)sz, hdr);
        return true;
    }

    private static bool IsBox(byte[] raw, string type)
        => raw.Length >= 8 && Encoding.ASCII.GetString(raw, 4, 4) == type;

    private static byte[] Box(string type, byte[] payload)
    {
        var buf = new byte[8 + payload.Length];
        WriteBe32To(buf, 0, (uint)buf.Length);
        Encoding.ASCII.GetBytes(type, 0, 4, buf, 4);
        Array.Copy(payload, 0, buf, 8, payload.Length);
        return buf;
    }

    private static byte[] FullBox(string type, byte version, uint flags, byte[] payload)
    {
        var body = new byte[4 + payload.Length];
        body[0] = version;
        body[1] = (byte)((flags >> 16) & 0xFF);
        body[2] = (byte)((flags >> 8) & 0xFF);
        body[3] = (byte)(flags & 0xFF);
        Array.Copy(payload, 0, body, 4, payload.Length);
        return Box(type, body);
    }

    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p.Length;
        var buf = new byte[total];
        int w = 0;
        foreach (var p in parts) { Array.Copy(p, 0, buf, w, p.Length); w += p.Length; }
        return buf;
    }

    private static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }

    private static byte[] U16(int v) => new[] { (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };
    private static byte[] U32(uint v) => new[] { (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF) };

    private static void WriteBe16(BinaryWriter w, int v) { w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF)); }
    private static void WriteBe32(BinaryWriter w, uint v)
    {
        w.Write((byte)((v >> 24) & 0xFF)); w.Write((byte)((v >> 16) & 0xFF));
        w.Write((byte)((v >> 8) & 0xFF)); w.Write((byte)(v & 0xFF));
    }
    private static void WriteBe32To(byte[] b, int o, uint v)
    {
        b[o] = (byte)((v >> 24) & 0xFF); b[o + 1] = (byte)((v >> 16) & 0xFF);
        b[o + 2] = (byte)((v >> 8) & 0xFF); b[o + 3] = (byte)(v & 0xFF);
    }

    private static int Be16(byte[] d, int o) => (d[o] << 8) | d[o + 1];
    private static uint Be32(byte[] d, int o)
        => ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];
    private static long ReadBig(byte[] d, int o, int n)
    {
        long v = 0;
        for (int i = 0; i < n && i < 8; i++) v = (v << 8) | d[o + i];
        return v;
    }
}
