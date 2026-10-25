"""CPU inference with onnxruntime (what the service uses)."""
from __future__ import annotations

from pathlib import Path

import numpy as np
import onnxruntime as ort
from transformers import AutoTokenizer

from feedback.gen_data import ASPECTS, SENTIMENTS

MODEL_DIR = Path(__file__).resolve().parent.parent / "models/feedback"
MAX_LEN = 96

ASPECT_LABEL = {"drink": "Đồ uống", "service": "Phục vụ", "price": "Giá", "ambience": "Không gian", "speed": "Tốc độ"}


class FeedbackAnalyzer:
    def __init__(self, model_dir: Path = MODEL_DIR, file: str = "feedback.int8.onnx"):
        self.tok = AutoTokenizer.from_pretrained(model_dir)
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 4
        self.session = ort.InferenceSession(str(model_dir / file), opts, providers=["CPUExecutionProvider"])

    def analyze(self, texts: list[str]) -> list[dict]:
        enc = self.tok(texts, truncation=True, max_length=MAX_LEN, padding="max_length", return_tensors="np")
        s, a = self.session.run(None, {"input_ids": enc["input_ids"].astype(np.int64), "attention_mask": enc["attention_mask"].astype(np.int64)})
        s = np.exp(s - s.max(1, keepdims=True)); s /= s.sum(1, keepdims=True)
        a = 1 / (1 + np.exp(-a))
        out = []
        for i in range(len(texts)):
            k = int(s[i].argmax())
            out.append({
                "sentiment": SENTIMENTS[k].upper(),
                "confidence": round(float(s[i][k]), 3),
                "aspects": [ASPECTS[j] for j in range(len(ASPECTS)) if a[i][j] > 0.5],
            })
        return out
