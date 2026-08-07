"""
ONNX FP32 → FP16 量化脚本 v3
手动转换初始器权重为 FP16，不插入 Cast 节点，防止 LayerNorm 融合问题。
"""

import onnx
from onnx import helper, TensorProto, numpy_helper
import numpy as np
import sys
import os

def manual_convert_to_fp16(input_path, output_path, model_name):
    """
    手动将 ONNX 模型权重从 FP32 转换为 FP16。
    只转换 initializer 中的权重张量，不插入 Cast 节点，
    保持图结构完全不变，避免 LayerNorm 融合问题。
    """
    print(f"\n{'='*60}")
    print(f"转换: {model_name}")
    print(f"输入: {input_path}")
    print(f"输出: {output_path}")
    print(f"{'='*60}")
    
    model = onnx.load(input_path)
    
    in_size = os.path.getsize(input_path)
    print(f"  输入大小: {in_size / 1_000_000:.2f} MB")
    
    # 统计
    total_fp32 = 0
    converted = 0
    skipped = 0
    
    for init in model.graph.initializer:
        if init.data_type == TensorProto.FLOAT:
            total_fp32 += 1
            # 将 FP32 权重转换为 FP16
            float_array = numpy_helper.to_array(init)
            
            # 跳过非常小的张量（<4 元素，通常是 bias/scale 等）
            if float_array.size < 4:
                skipped += 1
                continue
            
            # float32 → float16 → 存回
            half_array = float_array.astype(np.float16)
            # 创建新的初始器，类型为 FLOAT16
            new_init = numpy_helper.from_array(half_array, init.name)
            # 替换初始器
            init.data_type = TensorProto.FLOAT16
            init.raw_data = new_init.raw_data
            # 清除旧的 float_data（如果存在）
            init.ClearField('float_data')
            init.ClearField('int32_data')
            init.ClearField('int64_data')
            init.ClearField('double_data')
            converted += 1
    
    # 保存
    onnx.save(model, output_path)
    out_size = os.path.getsize(output_path)
    
    print(f"  初始器: {total_fp32} F32 → {converted} 转换, {skipped} 跳过(小张量)")
    print(f"  输出大小: {out_size / 1_000_000:.2f} MB")
    print(f"  压缩率: {in_size / out_size:.2f}x")
    print(f"  节省: {(in_size - out_size) / 1_000_000:.1f} MB")
    
    # ONNX 检查
    try:
        onnx.checker.check_model(model)
        print(f"  ✅ ONNX 检查通过")
    except Exception as e:
        print(f"  ⚠️ ONNX 检查: {e}")
    
    return model


def verify_onnxruntime(input_path, model_name):
    """用 ONNX Runtime 验证模型加载和推理"""
    try:
        import onnxruntime as ort
        session = ort.InferenceSession(input_path, providers=['CPUExecutionProvider'])
        print(f"  ✅ ORT 加载成功: {model_name}")
        for i, inp in enumerate(session.get_inputs()):
            print(f"     输入[{i}]: {inp.name} shape={inp.shape} dtype={inp.type}")
        for i, out in enumerate(session.get_outputs()):
            print(f"     输出[{i}]: {out.name} shape={out.shape} dtype={out.type}")
        
        # 推理验证
        input_meta = session.get_inputs()[0]
        shape = [d if isinstance(d, int) and d > 0 else 1 for d in input_meta.shape]
        shape = [s if s is not None else 1 for s in shape]
        dummy = np.random.randn(*shape).astype(np.float32)
        outputs = session.run(None, {input_meta.name: dummy})
        print(f"     推理验证: 输出 shape={outputs[0].shape} dtype={outputs[0].dtype}")
        return True
    except Exception as e:
        print(f"  ❌ ORT 加载失败: {model_name}: {e}")
        return False


if __name__ == "__main__":
    base_dir = "C:/PLAN/TrueToneCap"
    models_dir = f"{base_dir}/publish/PLAN/models"
    output_dir = f"{base_dir}/publish/PLAN/models_fp16"
    os.makedirs(output_dir, exist_ok=True)
    
    # 转换检测模型
    manual_convert_to_fp16(
        f"{models_dir}/PP-OCRv6_medium_det.onnx",
        f"{output_dir}/PP-OCRv6_medium_det.onnx",
        "PP-OCRv6_medium_det"
    )
    verify_onnxruntime(
        f"{output_dir}/PP-OCRv6_medium_det.onnx",
        "PP-OCRv6_medium_det"
    )
    
    # 转换识别模型
    manual_convert_to_fp16(
        f"{models_dir}/PP-OCRv6_medium_rec.onnx",
        f"{output_dir}/PP-OCRv6_medium_rec.onnx",
        "PP-OCRv6_medium_rec"
    )
    verify_onnxruntime(
        f"{output_dir}/PP-OCRv6_medium_rec.onnx",
        "PP-OCRv6_medium_rec"
    )
    
    print(f"\n{'='*60}")
    print(f"FP16 量化完成！")
    print(f"输出目录: {output_dir}")
    
    # 总统计
    total_in = 0
    total_out = 0
    for name in ["PP-OCRv6_medium_det.onnx", "PP-OCRv6_medium_rec.onnx"]:
        in_path = f"{models_dir}/{name}"
        out_path = f"{output_dir}/{name}"
        if os.path.exists(in_path):
            total_in += os.path.getsize(in_path)
        if os.path.exists(out_path):
            total_out += os.path.getsize(out_path)
    print(f"  总大小: {total_in/1_000_000:.1f} MB → {total_out/1_000_000:.1f} MB")
    print(f"  总节省: {(total_in - total_out)/1_000_000:.1f} MB")