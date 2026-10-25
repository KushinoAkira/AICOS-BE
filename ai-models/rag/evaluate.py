"""Offline evaluation of the Smart Barista on a hand-written golden set (no LLM key needed: measures routing,
retrieval and the safety rules that do not depend on an LLM).   python -m rag.evaluate
"""
from __future__ import annotations

import asyncio
import json
import statistics
from pathlib import Path

from common.menu import BY_ID, MENU
from rag.barista import Barista

ROOT = Path(__file__).resolve().parent.parent

# (question, expected intent, expected product ids (any of them in the suggestions), forbidden check)
GOLDEN: list[tuple[str, str, set[str], str | None]] = [
    ("Mình dị ứng sữa, uống được món nào?", "allergy_diet", set(), "dairy"),
    ("Mình bị dị ứng sữa nên muốn một ly không sữa", "allergy_diet", set(), "dairy"),
    ("có món nào thuần chay không", "allergy_diet", set(), "vegan"),
    ("tôi ăn chay, gợi ý đồ uống giúp tôi", "recommend", set(), "vegan"),
    ("đồ uống nào không caffeine", "allergy_diet", set(), "caffeine"),
    ("bà bầu uống gì được ạ", "allergy_diet", set(), "caffeine"),
    ("mình kiêng đường, có món nào không đường", "allergy_diet", set(), "sweet"),
    ("món nào ít calo nhất", "allergy_diet", {"p04", "p19"}, None),
    ("hôm nay trời nóng bức uống gì giải nhiệt", "recommend", {"p11", "p19", "p18", "p20", "p12", "p07", "p17"}, "cold"),
    ("trời mưa nên uống gì cho ấm", "recommend", {"p14", "p21", "p15"}, "hot"),
    ("mình muốn ly cà phê béo nhẹ", "recommend", {"p05", "p03", "p06", "p10", "p08"}, None),
    ("Ly latte giá bao nhiêu vậy", "menu_info", {"p05"}, None),
    ("cold brew là gì", "menu_info", {"p07"}, None),
    ("cà phê muối có gì đặc biệt", "menu_info", {"p08"}, None),
    ("matcha latte có sữa không", "menu_info", {"p10"}, None),
    ("trà đào cam sả có những gì", "menu_info", {"p11"}, None),
    ("bánh nào ăn kèm cà phê", "recommend", {"p22", "p23"}, None),
    ("mấy giờ quán mở cửa", "shop_info", set(), None),
    ("quán có wifi không", "shop_info", set(), None),
    ("đơn của mình sao chưa có", "order_status", set(), None),
    ("mình có bao nhiêu điểm", "loyalty_promo", set(), None),
    ("cho mình gặp quản lý", "human_handoff", set(), None),
    ("xin chào", "smalltalk", set(), None),
    # --- trap / paraphrase cases (not in the intent training data) ---
    ("con mình 5 tuổi uống gì được nhỉ", "allergy_diet", set(), "caffeine"),
    ("uống gì không có sữa mà vẫn béo béo", "recommend", set(), "dairy"),
    ("dạo này hay mất ngủ, chiều tối nên uống gì", "recommend", set(), "caffeine"),
    ("trời nóng mà mình không muốn caffeine", "recommend", set(), "caffeine"),
    ("món nào vừa không đường vừa không sữa", "allergy_diet", set(), "dairy"),
    ("mình ăn thuần chay, có món ăn kèm nào không", "allergy_diet", set(), "vegan"),
    ("thức uống nóng nào không chứa sữa", "recommend", set(), "dairy"),
    ("quán đóng cửa lúc nào", "shop_info", set(), None),
    ("nhập mã giảm giá ở đâu vậy", "loyalty_promo", set(), None),
    ("hủy đơn vừa đặt giúp mình với", "order_status", set(), None),
    ("tôi muốn khiếu nại nhân viên", "human_handoff", set(), None),
    ("bạn là robot hả", "smalltalk", set(), None),
]


def violates(rule: str | None, ids: list[str]) -> bool:
    items = [BY_ID[i] for i in ids]
    return {None: lambda: False, "dairy": lambda: any(i.dairy for i in items), "vegan": lambda: any(not i.vegan for i in items),
            "caffeine": lambda: any(i.caffeine for i in items), "sweet": lambda: any(i.sweetness > 0 for i in items),
            "cold": lambda: any(not i.cold for i in items), "hot": lambda: any(not i.hot for i in items)}[rule]()


async def run() -> dict:
    barista = Barista()
    ok_intent = hits = hit_total = violations = no_llm = rejected = 0
    lat: list[int] = []
    failures = []
    safety_cases = []
    for q, intent, expect, rule in GOLDEN:
        r = await barista.reply(q)
        ids = [s["productId"] for s in r["suggestions"]]
        got = r["route"]["intent"]
        ok_intent += got == intent
        if got != intent:
            failures.append({"q": q, "expected": intent, "got": got})
        if expect:
            hit_total += 1
            hits += bool(expect & set(ids))
        named = [it.id for it in MENU if it.name.lower() in r["reply"].lower()]  # items the wording itself recommends
        bad = violates(rule, list(dict.fromkeys(ids + named)))
        violations += bad
        if bad:
            safety_cases.append({"q": q, "rule": rule, "items": ids, "reply": r["reply"][:160]})
        rejected += str(r["route"]["llm"]).startswith("rejected")
        no_llm += r["route"]["llm"] in (None, "template")
        lat.append(r["route"]["latency_ms"])
    n = len(GOLDEN)
    return {"questions": n, "intent_accuracy": round(ok_intent / n, 3), "retrieval_hit_rate": round(hits / max(1, hit_total), 3),
            "safety_violations": violations, "answered_without_llm": round(no_llm / n, 3), "llm_answers_rejected_by_guardrail": rejected,
            "latency_ms_p50": int(statistics.median(lat)), "latency_ms_max": max(lat), "retrieval": barista.retriever.mode, "intent_failures": failures, "safety_cases": safety_cases}


if __name__ == "__main__":
    report = asyncio.run(run())
    (ROOT / "models").mkdir(exist_ok=True)
    (ROOT / "models/rag_eval.json").write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(json.dumps(report, indent=2, ensure_ascii=False))
