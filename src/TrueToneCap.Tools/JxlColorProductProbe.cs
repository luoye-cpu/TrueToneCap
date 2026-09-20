// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 TrueToneCap contributors
// TrueToneCap.Tools/JxlColorProductProbe.cs
// 产品代码路径上的 JXL 色彩声明生效性探针 (--jxl-color-product)
//
// 为什么需要它（2026-09-22，交接 §44）：`scripts/precision/jxl_color_space_discrimination.py`
// 证的是"cjxl 认这些枚举值"，用的输入是我手搓的 P6 PPM。而产品路径的中转文件是
// **我们自己写的**（8-bit 走 WriteBgraToPpm，≥10-bit 走 ManagedPngEncoder.Encode16 的 16-bit PNG）。
// 只要那个中转文件带上任何色彩元数据（iCCP / sRGB / gAMA / cHRM），`-x color_space=` 就会被
// cjxl 当作"输入已有信息"而**完全忽略** —— 实测那种情况下 8 种取值产出 md5 全同。
// 所以"改了映射表"不等于"产物变了"，必须拿产品入口自己证一次。
//
// 用法: dotnet run --project src/TrueToneCap.Tools -- --jxl-color-product
// 退出码: 0 = 每条位深路上、不同目标标签都产出不同字节；1 = 有相同（= 提示没抵达 cjxl）。
//   本探针没有 SKIP(3) 分支：cjxl 是仓库自带的（产品运行时用的就是那一份），
//   探不到就是检出缺件 ⇒ 判红而不是"没判"，这一点与 GainMap 探针第 4 层相反（那里是仓库外工具）。

using System.Security.Cryptography;
using TrueToneCap.Core.Encoding;

public static class JxlColorProductProbe
{
    static string H(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))[..16];

    public static int Run(string[] args)
    {
        if (!NativeJxlEncoder.IsAvailable)
        {
            Console.WriteLine("❌ cjxl.exe 不可用 ⇒ 本轮没有判据（不是通过）");
            return 1;
        }

        const int W = 64, H2 = 64;
        var bgra = new byte[W * H2 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        { bgra[i] = 200; bgra[i + 1] = 90; bgra[i + 2] = 40; bgra[i + 3] = 255; }

        var dir = Path.Combine(Path.GetTempPath(), "ttc_jxl_product_probe");
        // 每次从空目录开始：残留的 .jxl 会让"本轮没编码成功"被读成通过（判据比的是本轮 md5）
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        // 表里的三个不同落点 + 一个"未知标签"（应当兜到 sRGB，与 sRGB 同字节 ⇒ 证明永不吐出非法值）
        var tags = new (string? tag, string label)[]
        {
            ("sRGB",     "sRGB"),
            ("BT2020",   "BT2020 → RGB_D65_202_Rel_SRG"),
            ("DisplayP3","DisplayP3"),
            ("XYZZY",    "未知标签（应兜 sRGB）"),
        };

        int fail = 0;
        foreach (var (depth, name) in new (int, string)[] { (8, "8-bit / PPM 中转"), (10, "10-bit / 16-bit PNG 中转") })
        {
            Console.WriteLine($"\n── {name}（产品入口 NativeJxlEncoder.{(depth == 8 ? "Encode" : "Encode16Bgra16")}）──");
            var hashes = new List<(string label, string hash, string file)>();
            foreach (var (tag, label) in tags)
            {
                var f = Path.Combine(dir, $"d{depth}_{label.Replace(" ", "_").Replace("→", "-")}.jxl");
                // 先删同名旧产物：判据比的是【本轮字节的 md5】，上一轮留下的文件会直接顶替"没编码成功"
                if (File.Exists(f)) File.Delete(f);
                try
                {
                    if (depth == 8)
                        NativeJxlEncoder.Encode(bgra, W, H2, f, 1.0f, 8, null, null, tag);
                    else
                        NativeJxlEncoder.Encode16Bgra16(FormatHelper.Bgra8ToBgra16BE(bgra), W, H2, f,
                            1.0f, 10, null, null, tag);
                    hashes.Add((label, H(f), Path.GetFileName(f)));
                    Console.WriteLine($"  {label,-34} md5={H(f)}  {new FileInfo(f).Length} B");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {label,-34} ❌ 抛异常 {ex.GetType().Name}: {ex.Message}");
                    fail++;
                }
            }

            // 判据 1：sRGB / BT2020 / DisplayP3 三者必须两两不同
            var core = hashes.Where(x => !x.label.StartsWith("未知")).ToList();
            foreach (var a in core)
                foreach (var b in core)
                    if (string.CompareOrdinal(a.label, b.label) < 0 && a.hash == b.hash)
                    {
                        Console.WriteLine($"  ❌ {a.label} 与 {b.label} 字节相同 ⇒ color_space 没抵达 cjxl（中转文件带了元数据？）");
                        fail++;
                    }
            // 判据 2：未知标签必须与 sRGB 同值（兜底合法，不吐非法枚举）
            var s = core.FirstOrDefault(x => x.label == "sRGB").hash;
            var u = hashes.FirstOrDefault(x => x.label.StartsWith("未知")).hash;
            if (u != s)
            {
                Console.WriteLine($"  ❌ 未知标签没兜到 sRGB（{u} vs {s}）");
                fail++;
            }
            else Console.WriteLine("  ✅ 未知标签兜到 sRGB（不会吐出 cjxl 不认的值）");
        }

        Console.WriteLine(fail == 0
            ? "\n✅ 产品路径上：不同目标标签 ⇒ 不同字节（两条位深路各 3 种），中转文件没有吞掉色彩提示"
            : $"\n❌ {fail} 条不成立 ⇒ §44 的改动在这条路上是空转，别当成已修");
        Console.WriteLine($"   产物留在 {dir}（可直接喂判别脚本或以后接 djxl 复核语义）");
        return fail == 0 ? 0 : 1;
    }
}
