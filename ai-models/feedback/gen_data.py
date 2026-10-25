"""Synthetic Vietnamese coffee-shop reviews (training data). The gold test set is hand-written in gold_data.py.

Every review is built from 1-3 aspect clauses; the label is derived from the clauses (so labels are consistent),
and text is made noisy (no diacritics, teen-code, typos, emoji) so the model does not just memorise templates.
"""
from __future__ import annotations

import json
import random
import re
import unicodedata
from pathlib import Path

ASPECTS = ["drink", "service", "price", "ambience", "speed"]
SENTIMENTS = ["negative", "neutral", "positive"]

# aspect -> polarity -> clause templates
CLAUSES: dict[str, dict[str, list[str]]] = {
    "drink": {
        "positive": [
            "{d} rất ngon, vị đậm đà", "{d} thơm và vừa miệng", "cà phê ở đây chuẩn vị, uống là nghiền",
            "{d} pha chuẩn, không bị chua hay đắng gắt", "đồ uống ngon hơn mình nghĩ", "{d} béo ngậy mà không ngấy",
            "trà trái cây tươi mát, topping giòn", "{d} đúng kiểu mình thích, sẽ gọi lại",
        ],
        "neutral": [
            "{d} tạm ổn, không có gì đặc biệt", "{d} uống được, vị bình thường", "đồ uống ở mức trung bình",
            "{d} cũng được thôi, không quá ấn tượng",
        ],
        "negative": [
            "{d} nhạt toẹt, như pha loãng", "{d} bị đắng gắt khó uống", "{d} quá ngọt, uống bị ngán",
            "đồ uống có mùi lạ, không tươi", "{d} chua, hình như hạt cũ", "ly {d} bị thiếu đá và nhạt nhẽo",
            "{d} pha sai món mình gọi", "trà bị nhạt, topping dai nhách",
        ],
    },
    "service": {
        "positive": [
            "nhân viên rất thân thiện và nhiệt tình", "bạn phục vụ lịch sự, tư vấn món dễ thương",
            "phục vụ chu đáo, nhớ cả tên khách", "bạn thu ngân vui vẻ, hỗ trợ tận tình",
        ],
        "neutral": ["nhân viên bình thường, không nổi bật", "phục vụ tạm được", "thái độ nhân viên ổn"],
        "negative": [
            "nhân viên thái độ khó chịu, trả lời cộc lốc", "phục vụ thiếu chuyên nghiệp, hỏi mãi mới trả lời",
            "bạn thu ngân tính nhầm tiền mà không xin lỗi", "nhân viên cứ mải nói chuyện, mặc kệ khách",
            "gọi mấy lần không ai ra phục vụ",
        ],
    },
    "price": {
        "positive": ["giá hợp lý, đáng tiền", "giá mềm so với chất lượng", "giá sinh viên dễ chịu", "nhiều ưu đãi, tích điểm có lợi"],
        "neutral": ["giá ngang mặt bằng chung", "giá hơi nhỉnh một chút nhưng chấp nhận được", "giá bình thường"],
        "negative": ["giá quá đắt so với chất lượng", "giá cao mà ly nhỏ xíu", "phụ thu lung tung, giá không rõ ràng", "đắt hơn quán bên cạnh mà không ngon hơn"],
    },
    "ambience": {
        "positive": ["quán đẹp, không gian ấm cúng", "chỗ ngồi thoải mái, nhạc nhẹ dễ chịu", "view đẹp, chụp ảnh sống ảo thích lắm", "quán yên tĩnh, làm việc rất tốt", "điều hòa mát, sạch sẽ"],
        "neutral": ["không gian bình thường", "quán nhỏ nhưng cũng ổn", "chỗ ngồi tạm được"],
        "negative": ["quán ồn ào, khó nói chuyện", "bàn ghế bẩn, nhiều ruồi", "không gian chật chội, nóng bức", "nhà vệ sinh dơ", "nhạc quá to, đau đầu"],
    },
    "speed": {
        "positive": ["lên món nhanh, chờ chưa tới 5 phút", "ra đồ rất nhanh dù đang đông", "không phải chờ lâu"],
        "neutral": ["thời gian chờ bình thường", "ra đồ không nhanh không chậm"],
        "negative": ["chờ gần 30 phút mới có nước", "lên món quá chậm, hỏi hoài", "đợi lâu mà còn làm sai", "đông khách là phục vụ rối tung"],
    },
}
DRINKS = ["cà phê sữa", "bạc xỉu", "latte", "cappuccino", "cold brew", "trà đào", "matcha", "americano", "trà sữa", "cà phê muối"]
OPEN = ["", "", "", "Hôm nay mình ghé quán, ", "Lần đầu ghé: ", "Cuối tuần rồi mà ", "Đi với bạn bè, ", "Review nhanh: "]
CLOSE = ["", "", "", " Sẽ quay lại.", " Nói chung vậy đó.", " Mọi người thử xem.", " Hơi tiếc.", " 👍", " ☕", " :(", ""]
TEEN = {"không": "ko", "được": "dc", "rất": "r", "quán": "quan", "nhân viên": "nv", "không gian": "ko gian"}


def strip_diacritics(s: str) -> str:
    s = s.replace("đ", "d").replace("Đ", "D")
    return "".join(c for c in unicodedata.normalize("NFD", s) if unicodedata.category(c) != "Mn")


def noisy(text: str, rng: random.Random) -> str:
    if rng.random() < 0.18:
        text = strip_diacritics(text)
    if rng.random() < 0.15:
        for k, v in TEEN.items():
            if rng.random() < 0.6:
                text = text.replace(k, v)
    if rng.random() < 0.1:
        i = rng.randrange(len(text))
        text = text[:i] + text[i] * 2 + text[i + 1:]
    if rng.random() < 0.1:
        text = text.lower()
    return re.sub(r"\s+", " ", text).strip()


def make_one(rng: random.Random) -> dict:
    n = rng.choices([1, 2, 3], weights=[0.35, 0.45, 0.2])[0]
    aspects = rng.sample(ASPECTS, n)
    mood = rng.choices(["positive", "negative", "neutral", "mixed"], weights=[0.34, 0.3, 0.16, 0.2])[0]
    drink = rng.choice(DRINKS)
    parts, pols = [], []
    for a in aspects:
        pol = mood if mood != "mixed" else rng.choice(["positive", "negative"])
        if mood in ("positive", "negative") and rng.random() < 0.12:
            pol = "neutral"
        pols.append(pol)
        parts.append(rng.choice(CLAUSES[a][pol]).replace("{d}", drink))
    joiner = rng.choice([", ", ". ", " và ", ", nhưng " if mood == "mixed" else ", "])
    body = joiner.join(parts)
    text = rng.choice(OPEN) + (body[0].lower() + body[1:] if OPEN and rng.random() < 0.5 else body) + rng.choice(CLOSE)
    pos, neg = pols.count("positive"), pols.count("negative")
    sentiment = "positive" if pos > neg else "negative" if neg > pos else "neutral"
    return {"text": noisy(text, rng), "sentiment": sentiment, "aspects": sorted(aspects, key=ASPECTS.index)}


def main(n_train: int = 4800, n_val: int = 600, seed: int = 7) -> None:
    rng = random.Random(seed)
    out = Path(__file__).resolve().parent.parent / "data"
    out.mkdir(exist_ok=True)
    seen: set[str] = set()
    rows: list[dict] = []
    while len(rows) < n_train + n_val:
        r = make_one(rng)
        if r["text"] in seen:
            continue
        seen.add(r["text"])
        rows.append(r)
    for name, part in (("feedback_train.jsonl", rows[:n_train]), ("feedback_val.jsonl", rows[n_train:])):
        (out / name).write_text("\n".join(json.dumps(r, ensure_ascii=False) for r in part), encoding="utf-8")
    print(f"train={n_train} val={n_val} -> {out}")


if __name__ == "__main__":
    main()
