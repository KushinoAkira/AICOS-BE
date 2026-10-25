"""Intent classifier for the Smart Barista router (TF-IDF char+word n-grams + logistic regression, trains in seconds).

    python -m intent.train       -> models/intent/{intent.joblib, metrics.json}
Baseline for comparison: hand-written keyword rules (what you would write without training anything).
"""
from __future__ import annotations

import json
import time
import unicodedata
from pathlib import Path

import joblib
import numpy as np
from sklearn.feature_extraction.text import TfidfVectorizer
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import accuracy_score, f1_score
from sklearn.pipeline import FeatureUnion, Pipeline

from intent.data import INTENTS, TEST, TRAIN, norm

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "models/intent"




KEYWORDS = {
    "menu_info": ["giá", "menu", "thực đơn", "size", "topping"], "allergy_diet": ["dị ứng", "chay", "đường", "calo"],
    "recommend": ["gợi ý", "nên gọi", "đề xuất"], "order_status": ["đơn", "hủy"], "loyalty_promo": ["điểm", "voucher", "mã giảm", "khuyến mãi"],
    "shop_info": ["mở cửa", "địa chỉ", "ở đâu", "wifi", "đậu"], "human_handoff": ["nhân viên", "quản lý", "khiếu nại", "người thật"],
}


def keyword_rule(text: str) -> str:
    t = norm(text)
    for intent, words in KEYWORDS.items():
        if any(w in t for w in words):
            return intent
    return "smalltalk"


def build() -> Pipeline:
    feats = FeatureUnion([
        ("char", TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5), sublinear_tf=True, preprocessor=norm)),
        ("word", TfidfVectorizer(analyzer="word", ngram_range=(1, 2), sublinear_tf=True, preprocessor=norm)),
    ])
    return Pipeline([("tfidf", feats), ("clf", LogisticRegression(C=12, max_iter=2000, class_weight="balanced"))])


def main() -> None:
    X = [t for intent in INTENTS for t in TRAIN[intent]]
    y = [intent for intent in INTENTS for _ in TRAIN[intent]]
    X_test, y_test = [t for t, _ in TEST], [i for _, i in TEST]
    model = build().fit(X, y)
    t0 = time.perf_counter()
    pred = model.predict(X_test)
    ms = (time.perf_counter() - t0) / len(X_test) * 1000
    rule = [keyword_rule(t) for t in X_test]
    proba = model.predict_proba(X_test).max(1)
    metrics = {
        "train_size": len(X), "test_size": len(X_test),
        "keyword_rules": {"accuracy": round(accuracy_score(y_test, rule), 3), "macro_f1": round(f1_score(y_test, rule, average="macro"), 3)},
        "our_model": {"accuracy": round(accuracy_score(y_test, pred), 3), "macro_f1": round(f1_score(y_test, pred, average="macro"), 3), "ms_per_item": round(ms, 2)},
        "mean_confidence": round(float(np.mean(proba)), 3),
    }
    OUT.mkdir(parents=True, exist_ok=True)
    joblib.dump(model, OUT / "intent.joblib")
    (OUT / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()

