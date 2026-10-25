"""Export the fine-tuned model to ONNX and quantise to int8 (CPU serving).  python -m feedback.export_onnx"""
from __future__ import annotations

from pathlib import Path

import torch
from onnxruntime.quantization import QuantType, quantize_dynamic
from transformers import AutoTokenizer

from feedback.train import MAX_LEN, FeedbackModel

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "models/feedback"


def main() -> None:
    model = FeedbackModel()
    model.load_state_dict(torch.load(OUT / "model.pt", map_location="cpu"))
    model.eval()
    tok = AutoTokenizer.from_pretrained(OUT)
    enc = tok(["quán rất ngon"], return_tensors="pt", padding="max_length", max_length=MAX_LEN, truncation=True)
    fp32 = OUT / "feedback.onnx"
    torch.onnx.export(
        model, (enc["input_ids"], enc["attention_mask"]), str(fp32),
        input_names=["input_ids", "attention_mask"], output_names=["sentiment", "aspects"],
        dynamic_axes={"input_ids": {0: "batch"}, "attention_mask": {0: "batch"}, "sentiment": {0: "batch"}, "aspects": {0: "batch"}},
        opset_version=17, dynamo=False,
    )
    int8 = OUT / "feedback.int8.onnx"
    quantize_dynamic(str(fp32), str(int8), weight_type=QuantType.QInt8)
    print(f"fp32 {fp32.stat().st_size / 1e6:.0f} MB -> int8 {int8.stat().st_size / 1e6:.0f} MB")


if __name__ == "__main__":
    main()
