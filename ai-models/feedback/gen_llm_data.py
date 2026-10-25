"""Generate realistic, diverse labelled reviews with a large LLM (Groq) = "teacher" data for fine-tuning PhoBERT (the student).

    python -m feedback.gen_llm_data [calls]    -> data/feedback_llm.jsonl  (needs GROQ_API_KEY in .env)

Template data teaches the format, LLM data teaches real-world phrasing (slang, sarcasm, mixed feelings, no diacritics).
The hand-written gold set is never generated or trained on.
"""
from __future__ import annotations

import json
import os
import random
import sys
import time
from pathlib import Path

import httpx
from dotenv import load_dotenv

import common.tls  # noqa: F401  (local CA bundle, if any)

from feedback.gen_data import ASPECTS, SENTIMENTS

ROOT = Path(__file__).resolve().parent.parent
load_dotenv(ROOT / ".env")
OUT = ROOT / "data/feedback_llm.jsonl"

ASPECT_VI = {"drink": "đồ uống/đồ ăn (hương vị, chất lượng)", "service": "thái độ nhân viên/phục vụ", "price": "giá cả/ưu đãi/voucher",
             "ambience": "không gian, sạch sẽ, nhạc, chỗ ngồi", "speed": "tốc độ ra món/thời gian chờ"}
STYLES = [
    "ngắn gọn 5-12 từ, văn nói", "2-3 câu, giọng thân mật như nhắn tin", "viết không dấu tiếng Việt, hơi sai chính tả",
    "dùng teencode và emoji", "dài 3-4 câu, kể chuyện trải nghiệm", "giọng mỉa mai/châm biếm nhưng nghĩa rõ ràng",
    "khen chê lẫn lộn trong một đoạn", "trang trọng như email góp ý", "bình luận kiểu review Google Maps",
]
SENT_HINT = {"positive": "khen, hài lòng tổng thể", "negative": "chê, không hài lòng tổng thể",
             "neutral": "cảm xúc lẫn lộn hoặc trung tính: có khen có chê cân bằng, hoặc nhận xét bình thường"}


def prompt(sent: str, aspects: list[str], style: str, n: int) -> str:
    asp = "; ".join(f"{a} = {ASPECT_VI[a]}" for a in aspects) if aspects else "không nhắc khía cạnh cụ thể nào (chỉ cảm xúc chung chung)"
    return (f"Viết {n} đánh giá KHÁC NHAU của khách hàng về một quán cà phê ở Việt Nam.\n- Cảm xúc tổng thể: {sent} ({SENT_HINT[sent]}).\n"
            f"- Khía cạnh được nhắc tới: {asp}.\n- Văn phong: {style}.\n"
            f"Đa dạng món (cà phê, trà, bạc xỉu, matcha, bánh...), đa dạng cách diễn đạt, không lặp mẫu câu.\n"
            f'Chỉ trả về JSON hợp lệ dạng {{"reviews": ["...", "..."]}}, không giải thích.')


def call(key: str, content: str) -> list[str]:
    for attempt in range(5):
        res = httpx.post("https://api.groq.com/openai/v1/chat/completions", headers={"Authorization": f"Bearer {key}"}, timeout=90,
                         json={"model": "openai/gpt-oss-120b", "temperature": 1.0, "reasoning_effort": "low", "max_tokens": 3000,
                               "response_format": {"type": "json_object"}, "messages": [{"role": "user", "content": content}]})
        if res.status_code == 429:
            time.sleep(8 * (attempt + 1))
            continue
        if res.status_code >= 400:  # e.g. 400 json_validate_failed: just retry with a fresh sample
            continue
        try:
            return [t.strip() for t in json.loads(res.json()["choices"][0]["message"]["content"])["reviews"] if isinstance(t, str) and 4 < len(t) < 400]
        except (ValueError, KeyError):
            continue
    return []


def main(calls: int = 110, per_call: int = 8, seed: int = 11) -> None:
    key = os.environ.get("GROQ_API_KEY", "").split(",")[0].strip()
    if not key:
        raise SystemExit("GROQ_API_KEY missing (put it in .env)")
    rng = random.Random(seed)
    seen = {json.loads(l)["text"] for l in OUT.read_text(encoding="utf-8").splitlines()} if OUT.exists() else set()
    done = 0
    with OUT.open("a", encoding="utf-8") as f:
        for i in range(calls):
            sent = ["positive", "negative", "neutral"][i % 3]
            k = rng.choice([0, 1, 1, 1, 2, 2, 3]) if sent != "neutral" else rng.choice([1, 2, 2, 3])
            aspects = sorted(rng.sample(ASPECTS, k))
            for text in call(key, prompt(sent, aspects, rng.choice(STYLES), per_call)):
                if text not in seen:
                    seen.add(text)
                    f.write(json.dumps({"text": text, "sentiment": sent, "aspects": aspects}, ensure_ascii=False) + "\n")
                    done += 1
            f.flush()
            print(f"call {i + 1}/{calls}: total new {done}", flush=True)
            time.sleep(1.5)
    assert set(SENTIMENTS)


if __name__ == "__main__":
    main(int(sys.argv[1]) if len(sys.argv) > 1 else 200)

