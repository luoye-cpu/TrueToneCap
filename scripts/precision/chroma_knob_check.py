"""色度开关是否真的落地：逐格式两条判据（2026-09-19 全编码器可用性审计）。

① 字节同一性：两张样张之间**只有 ChromaSubsampling 不同**，若产物字节完全相同 ⇒ 这个开关
   根本没进编码器（不需要解码器参与，最硬的一条证据）。
② 外部解码器读回：ffprobe 的 pix_fmt 子采样是否等于请求值（不信本仓库自己的解析器）。
   有些容器/解码路径 ffprobe 只会报 RGBA（如 JPEG XL），此时读回不含子采样信息 ⇒ 该项
   不判，由判据 ① 负责。

用法: python scripts/precision/chroma_knob_check.py [--samples <dir>]
样张来自: dotnet run --project src/TrueToneCap.Tools -c Release -- --param-sweep
退出码: 0 通过 / 1 判据不通过 / 3 前置条件缺失（SKIP，见 precision_env.py）
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import hashlib
import json
import os
import subprocess
import sys

import precision_env as pe

# (基准名, 对比名, 格式, 各自请求的色度, 该旋钮是否真的存在于界面)
# 末项 None ⇒ 界面上有下拉且管线声明读取 ⇒ 字节相同就是回归（判据 ①）。
# 末项为字符串 ⇒ 该旋钮已按文档结论撤掉/本就无下拉 ⇒ 字节相同是【预期】，只报告不判失败。
PAIRS = [
    ("c_jpegli_420", "c_jpegli_444", "JPEG LI", "420", "444", None),
    ("c_jpegli_422", "c_jpegli_444", "JPEG LI", "422", "444", None),
    ("c_jxl_420", "c_jxl_444", "JPEG XL", "420", "444", None),
    ("c_gm_420", "c_gm_444", "JPEG GainMap", "420", "444", None),
    # AVIF 软件对照组：这条必须【不等】—— 实测 libaom 真给 422/444
    ("c_avifsw_420", "c_avifsw_444", "AVIF(libaom)", "420", "444", None),
    ("c_webp_420", "c_webp_444", "WebP", "420", "444",
     "色度下拉已撤：cwebp 无任何色度参数，有损 WebP 恒 4:2:0（CHANGELOG「全编码器诚实性审计」节）"),
    ("c_tiff_420", "c_tiff_444", "TIFF", "420", "444",
     "界面上本就没有 TIFF 色度下拉，ChromaTiff 字段已删（同上）"),
]


def path_of(name, directory):
    for f in os.listdir(directory):
        if f.split(".")[0] == name:
            return os.path.join(directory, f)
    return None


def sha(p):
    with open(p, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()[:16]


def pix_fmt(p, ffprobe):
    r = subprocess.run([ffprobe, "-v", "error", "-print_format", "json",
                        "-show_streams", p], capture_output=True, text=True)
    try:
        streams = json.loads(r.stdout)
    except Exception:
        return "ffprobe 解析失败"
    for s in streams.get("streams", []):
        if s.get("codec_type") == "video":
            return f"{s.get('pix_fmt')}/{s.get('profile', '')}"
    return "无视频流"


def subsample_of(pix):
    """从 pix_fmt 里取子采样档；RGBA/RGB 这类不降采样的读回不含该信息 ⇒ 返回 None。"""
    for k in ("444", "422", "420"):
        if k in pix:
            return k
    return None


def main():
    tools = pe.require_tools("ffprobe")
    directory = pe.samples_dir("sweep", pe.opt("--samples"))
    print("═══ 色度开关落地性检查 ═══")
    print("  样张: %s" % directory)
    print("  ffprobe: %s" % tools["ffprobe"])
    bad, unknown = [], []
    judged = 0
    for a, b, fmt, ca, cb, retired in PAIRS:
        pa, pb = path_of(a, directory), path_of(b, directory)
        if pa is None or pb is None:
            print(f"  ⏭ {fmt:<12} 缺样张（{a} / {b}）")
            unknown.append(fmt)
            continue
        judged += 1
        same = sha(pa) == sha(pb)
        ra, rb = pix_fmt(pa, tools["ffprobe"]), pix_fmt(pb, tools["ffprobe"])
        print(f"\n  {fmt}  请求 {ca} vs {cb}")
        if retired is None:
            print(f"    字节同一: {'❌ 完全相同 ⇒ 开关未进编码器' if same else '✅ 不同（开关确实改变了产物）'}"
                  f"   [{os.path.getsize(pa)} B vs {os.path.getsize(pb)} B]")
            if same:
                bad.append(fmt)
            # 判据 ②：读回能看出子采样时，必须等于各自的请求值
            for name, req, got in ((a, ca, ra), (b, cb, rb)):
                seen = subsample_of(got)
                if seen and seen != req:
                    bad.append("%s（ffprobe 读回 %s，请求 %s）" % (fmt, seen, req))
        else:
            print(f"    字节同一: {'相同（预期，见下）' if same else '不同'}"
                  f"   [{os.path.getsize(pa)} B vs {os.path.getsize(pb)} B]")
            print(f"    ℹ️ 该旋钮不计失败：{retired}")
        print(f"    ffprobe 读回: {ra}   |   {rb}")

    print("\n════════════════════════")
    if bad:
        print("结论：以下色度请求在字节层/码流层没有落地 ⇒ 设置项是装饰或被子静默改写：",
              "、".join(bad))
    else:
        print("结论：仍在界面上的色度下拉全部真实影响产物 ✅")
    if unknown:
        print("⚠ 未判定（缺样张）：%s ⇒ 判据未覆盖，不是通过" % "、".join(unknown))
    print("[RESULT] 判定 %d 组，失败 %d，缺样张 %d" % (judged, len(bad), len(unknown)))
    if bad or unknown:
        return pe.EXIT_FAIL
    return pe.EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
