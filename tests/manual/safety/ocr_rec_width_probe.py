# SPDX-License-Identifier: Apache-2.0
# tests/manual/safety/ocr_rec_width_probe.py
# OCR rec「固定 max side vs 批内真实最大宽」的一次性仪表（CPU EP 复刻，不改产品、不进门禁）。
#
#   py C:\PLAN\TrueToneCap\tests\manual\safety\ocr_rec_width_probe.py [--rows 40] [--batch 4] [--reps 7] [--step 8]
#
# 复刻口径全部对齐 src/TrueToneCap.Core/Services/OnnxOcrEngine.cs：
#   预处理  FillRecognitionInput()  : crop→bilinear(左对齐采样 srcX=cropX+dx/recW*cropW)→(v/255-0.5)/0.5→BGR→右侧补 0
#   边距    同上                    : padX=max(2,bw/10)（子框 max(2,min(bw/10,bh))）、padY=max(2,bh/5)
#   解码    DecodeRecognitionRow()  : 逐时间步 argmax；跳 blank(0)/重复；maxVal<0.5 丢
#   形状    RunRecognitionBatch()   : [4,3,48,1280]，批内不足 4 框也照发 4 行
# 输入 = 本文件合成的确定性文本条（渲染真值框，非 det 输出）⇒ 结果是指示性的，不是发货判据。

import argparse
import json
import math
import os
import statistics
import sys
import time

import numpy as np
from PIL import Image, ImageDraw, ImageFont

try:
    sys.stdout.reconfigure(encoding="utf-8")   # 解码出的中文要能落进文件（MSYS 管道默认 codepage 会花屏）
except Exception:  # noqa: BLE001
    pass

REC_H = 48
REC_MAX_W = 1280          # OcrModelConfig.Default.RecImgMaxW（产品固定 max side）
CTC_BLANK_MIN = 0.5       # DecodeRecognitionRow 的 ctcConfidenceThreshold

LINES = [
    "文件 编辑 视图 帮助", "保存到文件夹", "复制到剪贴板", "OCR 识字并翻译",
    "the quick brown fox jumps over the lazy dog", "设置 与 隐私",
    "今天下午三点开会记得带上笔记本电脑和充电器", "retry", "确定",
    "capture region and translate every detected text line here",
    "好的，我马上发给你", "预览 复制 保存 取消 应用",
    "WinUI 3 screenshot tool with PP-OCRv6 on DirectML and CPU fallback",
    "网络不可达", "应用需要管理员权限才能修改系统设置吗", "hello world 测试",
]


def build_page(rows):
    """确定性合成页面：每行一条文字，行宽随文本长度自然变化（这就是"批内宽度分布"）。"""
    rng = np.random.default_rng(20261031)
    font_px = 26
    try:
        font = ImageFont.truetype("msyh.ttc", font_px)
    except OSError:
        font = ImageFont.truetype("arial.ttf", font_px)
    w, h = 1400, 60 * rows + 40
    img = Image.new("RGB", (w, h), "white")
    d = ImageDraw.Draw(img)
    boxes = []
    for i in range(rows):
        text = LINES[i % len(LINES)]
        y = 20 + i * 60
        d.text((20, y), text, fill="black", font=font)
        bb = d.textbbox((20, y), text, font=font)
        # 真实 det 给的框会比字形宽一点：这里加一个固定的少量外扩，分布用种子决定
        grow = int(rng.integers(0, 6))
        boxes.append((max(0, bb[0] - grow), max(0, bb[1] - grow),
                      min(w, bb[2] + grow), min(h, bb[3] + grow)))
    al = np.full((h, w, 1), 255, dtype=np.uint8)
    # 产品的输入约定是 BGRA（B=0,G=1,R=2,A=3）；PIL 给的是 RGB ⇒ 反通道再接 alpha
    rgb = np.asarray(img, dtype=np.uint8)
    return np.concatenate([rgb[:, :, ::-1], al], axis=2), boxes


def fill(bgra_img, box, batch, arr, max_w, frag=False):
    """一个框 → arr[batch] 的第 48×max_w 面；右侧补 0（与产品一致：清 0 后再写 recW 列）。"""
    img_w, img_h = bgra_img.shape[1], bgra_img.shape[0]
    x, y = int(box[0]), int(box[1])
    bw, bh = int(box[2] - box[0]), int(box[3] - box[1])
    if bw <= 0 or bh <= 0:
        return 0
    pad_x = max(2, min(bw // 10, bh)) if frag else max(2, bw // 10)
    pad_y = max(2, bh // 5)
    cx = max(0, x - pad_x)
    cy = max(0, y - pad_y)
    cw = min(img_w - cx, bw + pad_x * 2)
    ch = min(img_h - cy, bh + pad_y * 2)
    if cw <= 0 or ch <= 0:
        return 0
    aspect = cw / ch
    rec_w = min(int(math.ceil(REC_H * aspect)), max_w)
    rec_w = max(4, rec_w)
    # 产品的采样点：srcX = cropX + dx/recW*cropW（左对齐，非半像素中心）
    fx = cx + np.arange(rec_w, dtype=np.float64) / rec_w * cw
    fy = cy + np.arange(REC_H, dtype=np.float64) / REC_H * ch
    x0 = np.clip(fx.astype(np.int64), 0, img_w - 1)
    y0 = np.clip(fy.astype(np.int64), 0, img_h - 1)
    x1 = np.clip(x0 + 1, 0, img_w - 1)
    y1 = np.clip(y0 + 1, 0, img_h - 1)
    wx = (fx - x0).astype(np.float32).reshape(1, rec_w, 1)
    wy = (fy - y0).astype(np.float32).reshape(REC_H, 1, 1)
    crop = bgra_img[np.ix_(y0, x0)]
    crop_x1 = bgra_img[np.ix_(y0, x1)]
    crop_y1 = bgra_img[np.ix_(y1, x0)]
    crop_xy = bgra_img[np.ix_(y1, x1)]
    v0 = crop * (1 - wx) + crop_x1 * wx
    v1 = crop_y1 * (1 - wx) + crop_xy * wx
    rgb = (v0 * (1 - wy) + v1 * wy)                                        # HxWxBGRA
    bgr = rgb[:, :, 0:3].astype(np.float32) / 255.0
    bgr = (bgr - 0.5) / 0.5
    arr[batch, :, :, :rec_w] = np.transpose(bgr, (2, 0, 1))
    return rec_w


def ctc_decode(out, row, vocab, stat=None):
    """产品的 DecodeRecognitionRow 逐字复刻：跳 blank(0) / 跳重复 / conf<0.5 丢，
    且字典映射带 .Where(d => d-1 < _dict.Length) —— 模型的第 18710 类（尾随特殊类）
    在产品里被这一句丢掉，不进气泡。stat 累计被这一句滤掉的次数。"""
    timesteps, num_classes = out.shape[1], out.shape[2]
    last = -1
    chars = []
    for t in range(timesteps):
        vec = out[row, t]
        idx = int(np.argmax(vec))
        if idx != last and idx > 0 and float(vec[idx]) >= CTC_BLANK_MIN:
            if idx - 1 < len(vocab):
                chars.append(vocab[idx - 1])
            elif stat is not None:
                stat[0] += 1          # 尾随特殊类：产品 string.Concat 前就滤掉
        last = idx
    return "".join(chars)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rows", type=int, default=40)
    ap.add_argument("--batch", type=int, default=4)
    ap.add_argument("--reps", type=int, default=7)
    ap.add_argument("--step", type=int, default=8, help="收窄宽度向上取整的步长（0=不量化）")
    ap.add_argument("--models", default=r"C:\PLAN\TrueToneCap\src\TrueToneCap.App\Models")
    ap.add_argument("--out", default=r"C:\PLAN\TrueToneCap\artifacts\bench")
    args = ap.parse_args()

    import onnxruntime as ort
    rec_path = os.path.join(args.models, "PP-OCRv6_medium_rec.onnx")
    dict_path = os.path.join(args.models, "ppocrv6_dict.txt")
    if not os.path.exists(rec_path):
        print(f"❌ 前置条件缺失：找不到 rec 模型 {rec_path}（退出码 3）")
        return 3
    os.makedirs(args.out, exist_ok=True)
    vocab = [l.rstrip("\r\n") for l in open(dict_path, encoding="utf-8").read().splitlines()]

    # ⚠ 必须禁用 SimplifiedLayerNormFusion —— 与本仓同一个理由：该 fusion 会破坏 PP-OCRv6 输出
    #   （OnnxOcrEngine.CreateSessionOptions 里用 session config entry 关它；这里用 disabled_optimizers 关它）。
    so = ort.SessionOptions()
    # 与产品 CreateSessionOptions 同一档：ORT_ENABLE_BASIC + 关 SimplifiedLayerNormFusion
    so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_BASIC
    so.add_session_config_entry("optimization.disable_simplified_layer_norm_fusion", "1")
    sess = ort.InferenceSession(rec_path, sess_options=so, providers=["CPUExecutionProvider"])
    inp = sess.get_inputs()[0]
    print(f"═══ [ocr-rec-width] 固定 max side({REC_MAX_W}) vs 批内真实最大宽 ═══")
    print(f"  EP=CPUExecutionProvider (⚠ 产品主路是 DirectML/WinML FP16；此处是产品 CPU 兜底臂的形状)")
    print(f"  模型={os.path.basename(rec_path)} 输入名={inp.name} 声明形状={inp.shape} 类数={len(vocab) + 2}")

    bgra, boxes = build_page(args.rows)
    natural = []
    for b in boxes:
        # 与 fill() 同一式子推出 recW（max_w 先按固定臂 1280）
        bw, bh = int(b[2] - b[0]), int(b[3] - b[1])
        px = max(2, bw // 10)
        py = max(2, bh // 5)
        cx = max(0, int(b[0]) - px)
        cw = min(bgra.shape[1] - cx, bw + px * 2)
        cy = max(0, int(b[1]) - py)
        chh = min(bgra.shape[0] - cy, bh + py * 2)
        natural.append(max(4, min(int(math.ceil(REC_H * cw / chh)), REC_MAX_W)))
    print(f"  合成 {len(boxes)} 框，批 {args.batch} 框/批 ⇒ 批内真实最大宽 = "
          f"{[max(natural[i:i + args.batch]) for i in range(0, len(natural), args.batch)]}"
          f"（固定臂恒 {REC_MAX_W}）")
    print(f"  输入分布 = 合成文本条（菜单 2~20 字 + 长句 60~200 字），字体 msyh 26px，"
          f"行框宽 40~1300px ⇒ 收窄潜力随分布而变，本表只对这一种分布负责\n")

    rows_csv = ["arm,batch_idx,n_rows,tensor_w,timesteps,ms_median,texts_equal_to_fixed,detail"]
    mismatches = []
    arm_a_total = 0.0
    arm_b_total = 0.0
    batch_ms = []
    conv_pairs = 0     # 正控：批内真实最大宽 == 固定 max side 的批（两臂张量逐字节相同）
    conv_eq = True
    conv_max_rel = 0.0
    disc_pairs = 0

    for start in range(0, len(boxes), args.batch):
        grp = boxes[start:start + args.batch]
        n = len(grp)
        nat = max(natural[start:start + args.batch])
        narrow = nat if args.step <= 0 else min(REC_MAX_W, int(math.ceil(nat / args.step) * args.step))

        def run(width):
            arr = np.zeros((args.batch, 3, REC_H, width), dtype=np.float32)
            recws = [fill(bgra, grp[i], i, arr, width) for i in range(n)]
            ts = []
            texts = None
            dropped = [0]
            for _ in range(args.reps):
                t0 = time.perf_counter()
                out = sess.run(None, {inp.name: arr})[0]
                ts.append((time.perf_counter() - t0) * 1000.0)
                texts = [ctc_decode(out, i, vocab, dropped) for i in range(n)]
            return statistics.median(ts), texts, out.shape[1], recws, dropped[0]

        ms_a, tx_a, t_a, recws, drop_a = run(REC_MAX_W)
        ms_b, tx_b, t_b, _, drop_b = run(narrow)
        arm_a_total += ms_a
        arm_b_total += ms_b
        batch_ms.append((start, n, REC_MAX_W, narrow, ms_a, ms_b, tx_a == tx_b))
        eq = tx_a == tx_b
        if not eq:
            mismatches.append((start, n, REC_MAX_W, narrow, tx_a, tx_b))
        if narrow == REC_MAX_W:
            conv_pairs += 1
            conv_note = "正控·收敛批（两臂同形，ms 差应≈噪声）"
            conv_eq = conv_eq and eq
            conv_max_rel = max(conv_max_rel, abs(ms_a - ms_b) / max(1.0, max(ms_a, ms_b)))
        else:
            disc_pairs += 1
            conv_note = ""
        rows_csv.append(f"fixed,{start},{n},{REC_MAX_W},{t_a},{ms_a:.3f},yes,tail_class_dropped={drop_a}")
        rows_csv.append(f"narrow,{start},{n},{narrow},{t_b},{ms_b:.3f},{'yes' if eq else 'NO'},"
                        f"tail_class_dropped={drop_b}")
        print(f"  批{start:>3} n={n} 固定{REC_MAX_W}(T={t_a})={ms_a:7.2f}ms | "
              f"收窄{narrow:>4}(T={t_b})={ms_b:7.2f}ms | Δ={ms_a - ms_b:7.2f}ms "
              f"({(ms_a / ms_b if ms_b else 0):.2f}x) 文本相同={eq} 尾类滤除 固定/收窄="
              f"{drop_a}/{drop_b} {conv_note}")
        if not eq:
            for i, (a, b) in enumerate(zip(tx_a, tx_b)):
                if a != b:
                    print(f"      row{i}: 固定={a!r}  收窄={b!r}")

    print(f"\n  合计：固定={arm_a_total:.1f}ms 收窄={arm_b_total:.1f}ms "
          f"⇒ 端到端 rec 段差 {(arm_a_total - arm_b_total):.1f}ms "
          f"({(arm_a_total / arm_b_total if arm_b_total else 0):.3f}x)")
    print(f"  判别批={disc_pairs} 收敛批(正控)={conv_pairs} 文本不一致批={len(mismatches)}")

    # 正控 2：均匀宽条（一整批每框都顶到固定 max side）⇒ 两臂张量逐字节相同 ⇒ 必须收敛。
    # 判据就是上面收敛批那几格：文本必须相同，且 Δms 落在噪声内（<10%）。
    print(f"  [正控·收敛批] 批内真实最大宽 == 固定 {REC_MAX_W} 的批 = {conv_pairs} 个；"
          f"其中文本全同={conv_eq}，最大相对 ms 差={conv_max_rel * 100:.1f}%（应≈噪声）"
          f" —— 两臂同形时给出同一读数，说明仪表没在判自己")
    if conv_pairs == 0:
        print("❌ 正控缺失 —— 没有任何一批两臂同形，无法证明仪表在应收敛处收敛")
        return 1
    if not conv_eq or conv_max_rel > 0.10:
        print("❌ non-discriminating:convergence —— 两臂同形的批却不收敛，仪表有别的变量在动")
        return 1

    # 正控 3：仪表本身会动吗？同一形状跑两次，且把宽度加大一倍看 ms 是否随之上升
    scale_ts = []
    for width in (256, 512, 1024, 1280):
        arr = np.zeros((4, 3, REC_H, width), dtype=np.float32)
        ts = []
        outs = None
        for _ in range(3):
            t0 = time.perf_counter()
            outs = sess.run(None, {inp.name: arr})[0]
            ts.append((time.perf_counter() - t0) * 1000.0)
        scale_ts.append((width, statistics.median(ts), outs.shape[1]))
    print("  [正控·标尺会动] 单批 4 行 × 不同宽度 ms/T： " +
          " ".join(f"W{w}={m:.2f}ms(T={t})" for w, m, t in scale_ts))
    ruler_moves = scale_ts[-1][1] > scale_ts[0][1] * 1.15
    # 宽度必须是 8 的倍数吗？（上面 T=W/8 就是证据）—— 给实现者钉一条：非 8 倍数会怎样
    odd_w = 273
    try:
        o = sess.run(None, {inp.name: np.zeros((1, 3, REC_H, odd_w), dtype=np.float32)})[0]
        print(f"  [口径] 宽度非 8 倍数（{odd_w}）会话【能跑】⇒ T={o.shape[1]}；"
              f"8 的倍数只是 T=W/8 的自然结果，收窄按 step 向上取整仍是对的（省得最多且不猜）")
    except Exception as ex:  # noqa: BLE001
        print(f"  [口径] 宽度非 8 倍数（{odd_w}）被会话拒绝：{type(ex).__name__} "
              "⇒ 收窄必须向上取整到 8 的倍数")

    csv_path = os.path.join(args.out, "ocr_rec_width_probe.csv")
    with open(csv_path, "w", encoding="utf-8", newline="") as f:
        f.write("\n".join(rows_csv) + "\n")
    print(f"CSV → {csv_path}")

    if len(mismatches) > 0:
        print(f"❌ 收窄改变了识别文本（{len(mismatches)} 批）—— 这是正确性问题，不是性能收益")
        return 1
    if not ruler_moves:
        print("❌ non-discriminating —— 宽度 ms 曲线在 256→1280 上不随宽度上升，标尺是死的")
        return 1
    if disc_pairs == 0:
        print("❌ non-discriminating —— 没有任何一批的两臂宽度不同")
        return 1
    print("✅ 通过（两臂文本逐批相同；标尺随宽度移动；存在判别批与收敛批）")
    return 0


if __name__ == "__main__":
    sys.exit(main())
