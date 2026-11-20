"""End-to-end guest chat scenarios against CapheNha Quận 1 catalog.

Inspired by VN café chatbot scripts (menu taste, pairing, allergy, FAQ, loyalty).
Run:  .venv/Scripts/python.exe tests/eval_guest_chat.py
Optional live LLM: EVAL_LIVE=1 (calls http://127.0.0.1:8090/api/v1/ai/chat)
"""
from __future__ import annotations

import asyncio
import os
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Callable

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from common.catalogs import seed_catalog  # noqa: E402
from rag.barista import (  # noqa: E402
    Barista,
    allowed,
    constraints,
    is_bold_coffee,
    is_food_side,
    is_soft_coffee,
)


DRINK = re.compile(
    r"cà phê|ca phe|latte|cappuccino|espresso|americano|cold brew|trà|tra |soda|nước|nuoc|matcha|sinh tố",
    re.I,
)
FOOD = re.compile(r"bánh|banh|croissant|tiramisu|pate|hạt|hat |flan|sừng|sung bo", re.I)


@dataclass
class Case:
    id: str
    message: str
    check: Callable[[dict[str, Any]], str | None]
    live: bool = True  # also hit HTTP when EVAL_LIVE=1


def names(r: dict[str, Any]) -> list[str]:
    return [s["name"] for s in r.get("suggestions") or []]


def reply_text(r: dict[str, Any]) -> str:
    return (r.get("reply") or "").lower()


def expect_all_soft_coffee(r: dict[str, Any]) -> str | None:
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions"
    cat = seed_catalog("caphenha", "q1")
    by = cat.by_id
    for s in sug:
        it = by.get(s["productId"])
        if not it or not is_soft_coffee(it):
            return f"not soft coffee: {s['name']}"
        if is_food_side(it):
            return f"food leaked into soft coffee: {s['name']}"
    if re.search(r"chưa có thông tin|hỏi nhân viên.{0,30}chi tiết", reply_text(r)):
        return "denial reply despite soft coffee matches"
    return None


def expect_sides_only(r: dict[str, Any]) -> str | None:
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions"
    for n in names(r):
        if DRINK.search(n) and not FOOD.search(n):
            return f"drink leaked as ăn kèm: {n}"
        if not FOOD.search(n):
            return f"not a food side: {n}"
    return None


def expect_no_dairy(r: dict[str, Any]) -> str | None:
    cat = seed_catalog("caphenha", "q1")
    for s in r.get("suggestions") or []:
        it = cat.by_id.get(s["productId"])
        if it and it.dairy:
            return f"dairy item: {it.name}"
    return None


def expect_no_caffeine(r: dict[str, Any]) -> str | None:
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions (expected caffeine-free picks)"
    cat = seed_catalog("caphenha", "q1")
    for s in sug:
        it = cat.by_id.get(s["productId"])
        if it and it.caffeine:
            return f"caffeine item: {it.name}"
    return None


def expect_bold_coffee(r: dict[str, Any]) -> str | None:
    cat = seed_catalog("caphenha", "q1")
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions"
    for s in sug:
        it = cat.by_id.get(s["productId"])
        if not it or not is_bold_coffee(it):
            return f"not bold coffee: {s['name']}"
    return None


def expect_cold(r: dict[str, Any]) -> str | None:
    cat = seed_catalog("caphenha", "q1")
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions"
    for s in sug:
        it = cat.by_id.get(s["productId"])
        if not it or not it.cold:
            return f"not cold: {s['name']}"
        if is_food_side(it):
            return f"food leaked into cold drinks: {s['name']}"
    return None


def expect_low_sweet(r: dict[str, Any]) -> str | None:
    cat = seed_catalog("caphenha", "q1")
    sug = r.get("suggestions") or []
    if not sug:
        return "no suggestions"
    for s in sug:
        it = cat.by_id.get(s["productId"])
        if not it or it.sweetness > 1:
            return f"too sweet: {s['name']} sweet={getattr(it, 'sweetness', None)}"
    return None


def expect_handoff(r: dict[str, Any]) -> str | None:
    if not r.get("handoff"):
        return "expected handoff=True"
    return None


def expect_action(action: str) -> Callable[[dict[str, Any]], str | None]:
    def _c(r: dict[str, Any]) -> str | None:
        if r.get("action") != action:
            return f"expected action={action}, got {r.get('action')}"
        return None

    return _c


def expect_reply_has(*needles: str) -> Callable[[dict[str, Any]], str | None]:
    def _c(r: dict[str, Any]) -> str | None:
        t = reply_text(r)
        for n in needles:
            if n.lower() not in t:
                return f"reply missing '{n}'"
        return None

    return _c


def expect_in_branch(r: dict[str, Any]) -> str | None:
    cat = seed_catalog("caphenha", "q1")
    for s in r.get("suggestions") or []:
        if s["productId"] not in cat.by_id:
            return f"out of branch catalog: {s['productId']} {s['name']}"
    return None


def compose(*checks: Callable[[dict[str, Any]], str | None]) -> Callable[[dict[str, Any]], str | None]:
    def _c(r: dict[str, Any]) -> str | None:
        for fn in checks:
            err = fn(r)
            if err:
                return err
        return None

    return _c


CASES: list[Case] = [
    # --- Taste / recommend (tudongchat + FastBots café scripts) ---
    Case("soft_coffee", "Cà phê không đắng?", compose(expect_all_soft_coffee, expect_in_branch)),
    Case("soft_coffee_alt", "Cho mình ly cà phê dễ uống, ít đắng", compose(expect_all_soft_coffee, expect_in_branch)),
    Case("bold_coffee", "Cà phê đậm, mạnh", compose(expect_bold_coffee, expect_in_branch)),
    Case("low_sweet", "Món ít ngọt?", compose(expect_low_sweet, expect_in_branch)),
    Case("cold_drink", "Đồ uống mát?", compose(expect_cold, expect_in_branch)),
    Case("an_kem", "Có gì ăn kèm?", compose(expect_sides_only, expect_in_branch)),
    Case("an_kem_alt", "Có bánh gì không?", compose(expect_sides_only, expect_in_branch)),
    Case("pair_latte", "Ăn kèm với latte?", compose(expect_sides_only, expect_in_branch)),
    # --- Diet / allergy (must stay inside published menu attributes) ---
    Case("no_dairy", "Mình dị ứng sữa, có món nào không?", compose(expect_no_dairy, expect_in_branch)),
    Case("no_caffeine", "Không caffeine, mình khó ngủ", compose(expect_no_caffeine, expect_in_branch)),
    Case("pregnant", "Mình đang mang thai, uống gì được?", compose(expect_no_caffeine, expect_in_branch)),
    Case("me_bau", "món dành cho mẹ bầu", compose(expect_no_caffeine, expect_in_branch)),
    Case("kid", "Có gì cho trẻ em 5 tuổi?", compose(expect_no_caffeine, expect_in_branch)),
    Case("allergy_vague", "Mình bị dị ứng", expect_handoff, live=True),
    # --- Ops / FAQ intents ---
    Case("greeting", "Xin chào", expect_reply_has("barista"), live=True),
    Case("orders", "Đơn của mình tới đâu rồi?", expect_action("open_orders"), live=True),
    Case("loyalty", "Tích điểm thế nào?", expect_action("open_loyalty"), live=True),
    Case("handoff", "Cho mình gặp nhân viên", expect_handoff, live=True),
]


async def run_local(barista: Barista, case: Case) -> tuple[bool, str, dict[str, Any]]:
    cat = seed_catalog("caphenha", "q1")
    # Deterministic path: no LLM so tests don't flake on wording.
    async def _no_llm(*_a: Any, **_k: Any) -> None:
        return None

    barista.llm.complete = _no_llm  # type: ignore[method-assign]
    r = await barista.reply(case.message, history=[], catalog=cat)
    err = case.check(r)
    return err is None, err or "ok", r


async def run_live(case: Case) -> tuple[bool, str, dict[str, Any]]:
    import httpx

    async with httpx.AsyncClient(timeout=30.0) as client:
        res = await client.post(
            "http://127.0.0.1:8090/api/v1/ai/chat",
            json={"message": case.message, "tenantCode": "caphenha", "branchName": "Quận 1", "history": []},
        )
        res.raise_for_status()
        r = res.json()
    err = case.check(r)
    return err is None, err or "ok", r


def constraint_sanity() -> list[tuple[str, bool, str]]:
    """Offline constraint filter checks (no model needed)."""
    cat = seed_catalog("caphenha", "q1")
    rows: list[tuple[str, bool, str]] = []

    def check(qid: str, q: str, pred: Callable[[Any], bool]) -> None:
        c = constraints(q)
        picks = [it for it in cat.items if allowed(it, c)]
        ok = bool(picks) and all(pred(it) for it in picks)
        detail = ", ".join(it.name for it in picks[:5]) or "(empty)"
        rows.append((qid, ok, detail if ok else f"FAIL pool={detail} c={c}"))

    check("c_soft", "Cà phê không đắng?", is_soft_coffee)
    check("c_bold", "Cà phê đậm", is_bold_coffee)
    check("c_side", "Có gì ăn kèm?", is_food_side)
    check("c_dairy", "dị ứng sữa", lambda it: not it.dairy and "sữa" not in it.allergens)
    check("c_caf", "không caffeine", lambda it: not it.caffeine)
    check("c_sweet", "ít ngọt", lambda it: it.sweetness <= 1)
    check("c_cold", "đồ uống mát", lambda it: it.cold)
    return rows


async def main() -> int:
    print("=== Constraint filter (offline) ===")
    fails = 0
    for qid, ok, detail in constraint_sanity():
        mark = "PASS" if ok else "FAIL"
        if not ok:
            fails += 1
        print(f"  [{mark}] {qid}: {detail}")

    print("\n=== Barista reply (template path, CapheNha Q1) ===")
    barista = Barista()
    results: list[tuple[str, bool, str, list[str]]] = []
    for case in CASES:
        ok, msg, r = await run_local(barista, case)
        if not ok:
            fails += 1
        results.append((case.id, ok, msg, names(r)))
        mark = "PASS" if ok else "FAIL"
        print(f"  [{mark}] {case.id}: {msg} | sug={names(r)} | intent={r.get('route', {}).get('intent')}")

    if os.environ.get("EVAL_LIVE") == "1":
        print("\n=== Live HTTP /api/v1/ai/chat ===")
        for case in CASES:
            if not case.live:
                continue
            try:
                ok, msg, r = await run_live(case)
            except Exception as exc:  # noqa: BLE001
                ok, msg, r = False, f"http error: {exc}", {}
                fails += 1
                print(f"  [FAIL] {case.id}: {msg}")
                continue
            if not ok:
                fails += 1
            mark = "PASS" if ok else "FAIL"
            print(f"  [{mark}] {case.id}: {msg} | sug={names(r)} | llm={r.get('route', {}).get('llm')}")

    print(f"\nSummary: {fails} failure(s)")
    return 1 if fails else 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
