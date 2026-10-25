"""Compare three ways to label the hand-written gold set:
  1) rule on stars (what the backend does today)   2) LLM zero-shot (Groq, only if GROQ_API_KEY is set)   3) our model.
    python -m feedback.evaluate
"""
from __future__ import annotations

import json
import os
import time
from pathlib import Path

import httpx
import numpy as np
from dotenv import load_dotenv
from dotenv import load_dotenv
from sklearn.metrics import f1_score

from feedback.gen_data import ASPECTS, SENTIMENTS
from feedback.gold_data import rows as gold_rows
from feedback.predict import FeedbackAnalyzer

import common.tls  # noqa: F401  (local CA bundle, if any)

import common.tls  # noqa: F401  (local CA bundle, if any)

ROOT = Path(__file__).resolve().parent.parent
load_dotenv(ROOT / ".env")
load_dotenv(ROOT / ".env")


def rule_baseline(rows):
    return ["positive" if r["rating"] >= 4 else "negative" if r["rating"] <= 2 else "neutral" for r in rows]


def llm_zero_shot(rows):
    key = os.environ.get("GROQ_API_KEY", "").split(",")[0].strip()
    if not key:
        return None
    preds, t0 = [], time.time()
    for r in rows:
        body = {"model": "openai/gpt-oss-20b", "temperature": 0, "max_tokens": 400, "reasoning_effort": "low",
                "messages": [{"role": "user", "content": f"Phân loại cảm xúc đánh giá quán cà phê. Chỉ trả lời một từ: positive, negative hoặc neutral.\nĐánh giá: {r['text']}"}]}
        res = httpx.post("https://api.groq.com/openai/v1/chat/completions", json=body, headers={"Authorization": f"Bearer {key}"}, timeout=30)
        word = (res.json()["choices"][0]["message"].get("content") or "").strip().lower() if res.status_code == 200 else "neutral"
        preds.append(next((s for s in SENTIMENTS if s in word), "neutral"))
        time.sleep(0.6)
    return preds, (time.time() - t0) / len(rows) * 1000


def main() -> None:
    rows = gold_rows()
    y = [r["sentiment"] for r in rows]
    report = {"gold_size": len(rows)}

    rule = rule_baseline(rows)
    hard = [i for i in range(len(y)) if rule[i] != y[i]]  # stars contradict the text: where a text model must help
    report["hard_subset_size"] = len(hard)

    def add(name, pred, ms=None):
        report[name] = {"accuracy": round(float(np.mean([a == b for a, b in zip(y, pred)])), 3),
                        "accuracy_on_hard_subset": round(float(np.mean([pred[i] == y[i] for i in hard])), 3),
                        "macro_f1": round(float(f1_score(y, pred, average="macro")), 3), **({"ms_per_item": round(ms, 1)} if ms else {})}

    add("rule_on_stars", rule_baseline(rows))
    llm = llm_zero_shot(rows)
    if llm:
        add("llm_zero_shot_groq_gpt_oss_20b", llm[0], llm[1])
    analyzer = FeedbackAnalyzer()
    texts = [r["text"] for r in rows]
    t0 = time.time()
    res = analyzer.analyze(texts)
    ms = (time.time() - t0) / len(rows) * 1000
    add("our_phobert_int8", [x["sentiment"].lower() for x in res], ms)
    y_a = np.array([[1 if a in r["aspects"] else 0 for a in ASPECTS] for r in rows])
    p_a = np.array([[1 if a in x["aspects"] else 0 for a in ASPECTS] for x in res])
    report["our_phobert_int8"]["aspect_micro_f1"] = round(float(f1_score(y_a, p_a, average="micro", zero_division=0)), 3)
    (ROOT / "models/feedback/comparison.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

