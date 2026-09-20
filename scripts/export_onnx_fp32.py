"""将官方 Paddle 推理模型 (.pdmodel/.pdiparams) 转换为 FP32 ONNX。
用法: py export_onnx_fp32.py
输出: official_models/onnx_fp32/PP-OCRv6_medium_{det,rec}_infer.onnx
"""
# SPDX-License-Identifier: Apache-2.0
# Copyright 2026 TrueToneCap contributors
import os
from paddle2onnx.convert import export

BASE = r"C:\PLAN\TrueToneCap\official_models"
OUT = os.path.join(BASE, "onnx_fp32")
os.makedirs(OUT, exist_ok=True)

MODELS = {
    "det": "PP-OCRv6_medium_det_infer",
    "rec": "PP-OCRv6_medium_rec_infer",
}

for tag, name in MODELS.items():
    model_dir = os.path.join(BASE, name)
    pdmodel = os.path.join(model_dir, "inference.pdmodel")
    pdiparams = os.path.join(model_dir, "inference.pdiparams")
    out_path = os.path.join(OUT, f"PP-OCRv6_medium_{tag}_infer.onnx")

    print(f"\n{'='*60}")
    print(f"转换 {name}: FP32 → ONNX")
    print(f"  输入: {pdmodel}")
    print(f"  输出: {out_path}")
    print(f"{'='*60}")

    export(
        model_filename=pdmodel,
        params_filename=pdiparams,
        save_file=out_path,
        opset_version=11,
        enable_onnx_checker=True,
        export_fp16_model=False,  # 关键: 导出 FP32 (INT8 量化前必需)
    )
    size = os.path.getsize(out_path)
    print(f"  ✅ ONNX 已生成: {size/1e6:.2f} MB")

print("\n全部转换完成")
