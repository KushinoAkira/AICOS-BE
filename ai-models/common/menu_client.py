"""Fetch the public branch menu from the Menu service and map it into Item rows."""
from __future__ import annotations

import logging
import os
import re
from typing import Any

import httpx

from common.item import Item, item

log = logging.getLogger("aicos.menu_client")

MENU_API_BASE = os.environ.get("MENU_API_BASE_URL", "").rstrip("/")


def _infer(name: str, category: str, description: str, tags: list[str]) -> dict[str, Any]:
    blob = f"{name} {category} {description} {' '.join(tags)}".lower()
    dairy = bool(re.search(r"sữa|latte|cappuccino|bạc xỉu|bac xiu|macchiato|kem|yogurt|flan", blob))
    caffeine = bool(re.search(r"cà phê|ca phe|espresso|americano|cold brew|matcha|trà sữa|tra sua|hồng trà|hong tra|trà đen|tra den", blob)) and not bool(
        re.search(r"hoa cúc|hoa cuc|gừng mật ong|gung mat ong|không caffeine|khong caffeine", blob)
    )
    if re.search(r"nước ép|nuoc ep|soda|sinh tố|sinh to|dưa hấu|dua hau", blob):
        caffeine = False
    vegan = not dairy and not bool(re.search(r"trứng|trung|pate|thịt|thit|bơ|bo |phô mai|pho mai", blob))
    hot = bool(re.search(r"nóng|nong|nóng|ấm|am ", blob)) or ("cà phê" in blob and "đá" not in blob and "cold" not in blob)
    cold = bool(re.search(r"đá|da |cold|ủ lạnh|u lanh|đá xay|da xay", blob)) or not hot
    if re.search(r"bánh|banh|ăn vặt|an vat|hạt|hat ", blob):
        hot, cold = True, True
    sweet = 0
    if re.search(r"không đường|khong duong|đen|den\b|americano|espresso", blob):
        sweet = 0
    elif re.search(r"ít ngọt|it ngot|latte|cappuccino|matcha", blob):
        sweet = 1
    elif re.search(r"trân châu|tran chau|socola|tiramisu|rất ngọt|rat ngot", blob):
        sweet = 3
    elif dairy or re.search(r"sữa|ngọt|ngot|đào|dao|vải|vai", blob):
        sweet = 2
    inferred_tags = list(tags)
    for label, pat in (
        ("bán chạy", r"best|bán chạy"),
        ("giải nhiệt", r"đào|vải|dưa|soda|cold brew|giải nhiệt"),
        ("ăn kèm", r"bánh|croissant|tiramisu|flan"),
        ("ăn vặt", r"hạt|khoai|ăn vặt"),
        ("truyền thống", r"phin|bạc xỉu|cà phê sữa|cà phê đen"),
        ("không caffeine", r"hoa cúc|gừng|nước ép|soda"),
        ("mùa hè", r"cold|đá|đào|dưa|xoài"),
        ("ngày mưa", r"gừng|socola nóng"),
        ("matcha", r"matcha"),
        ("ngọt", r"trà sữa|trân châu|socola|tiramisu"),
    ):
        if re.search(pat, blob) and label not in inferred_tags:
            inferred_tags.append(label)
    allergens: list[str] = []
    if dairy:
        allergens.append("sữa")
    if re.search(r"bánh|banh|croissant|bột|gluten|mì que|mi que", blob):
        allergens.append("gluten")
    if re.search(r"trứng|tiramisu|flan", blob):
        allergens.append("trứng")
    if re.search(r"hạt điều|hat dieu|đậu phộng", blob):
        allergens.append("hạt")
    return {
        "sweetness": sweet,
        "caffeine": caffeine,
        "dairy": dairy,
        "vegan": vegan and "gluten" not in allergens,
        "hot": hot,
        "cold": cold,
        "tags": tuple(inferred_tags),
        "allergens": tuple(dict.fromkeys(allergens)),
    }


def items_from_public_menu(payload: dict[str, Any]) -> list[Item]:
    out: list[Item] = []
    for cat in payload.get("categories") or []:
        cat_name = str(cat.get("name") or "Khác")
        for p in cat.get("products") or []:
            if p.get("isAvailable") is False:
                continue
            name = str(p.get("name") or "").strip()
            if not name:
                continue
            pid = str(p.get("id") or "")
            price = int(float(p.get("price") if p.get("price") is not None else p.get("basePrice") or 0))
            desc = str(p.get("description") or "")
            tags = [str(t) for t in (p.get("tags") or []) if t]
            meta = _infer(name, cat_name, desc, tags)
            out.append(
                item(
                    pid,
                    name,
                    cat_name,
                    price,
                    desc or f"{name} — {cat_name}",
                    meta["sweetness"],
                    meta["caffeine"],
                    meta["dairy"],
                    meta["vegan"],
                    meta["hot"],
                    meta["cold"],
                    meta["tags"],
                    meta["allergens"],
                )
            )
    return out


def items_from_snapshot(rows: list[dict[str, Any]]) -> list[Item]:
    """Accept a compact FE snapshot: {id,name,category?,price,description?,tags?}."""
    out: list[Item] = []
    for p in rows:
        name = str(p.get("name") or "").strip()
        pid = str(p.get("id") or "").strip()
        if not name or not pid:
            continue
        cat = str(p.get("category") or p.get("categoryName") or "Khác")
        price = int(float(p.get("price") or p.get("basePrice") or 0))
        desc = str(p.get("description") or "")
        tags = [str(t) for t in (p.get("tags") or []) if t]
        meta = _infer(name, cat, desc, tags)
        out.append(
            item(
                pid,
                name,
                cat,
                price,
                desc or f"{name} — {cat}",
                meta["sweetness"],
                meta["caffeine"],
                meta["dairy"],
                meta["vegan"],
                meta["hot"],
                meta["cold"],
                meta["tags"] or tuple(tags),
                meta["allergens"],
            )
        )
    return out


async def fetch_branch_menu(branch_id: str, timeout: float = 4.0) -> list[Item] | None:
    if not MENU_API_BASE or not branch_id:
        return None
    url = f"{MENU_API_BASE}/public/branches/{branch_id}/menu"
    try:
        async with httpx.AsyncClient(timeout=timeout) as client:
            res = await client.get(url)
            res.raise_for_status()
            items = items_from_public_menu(res.json())
            log.info("loaded %s live menu items for branch %s", len(items), branch_id)
            return items
    except Exception as exc:
        log.warning("menu fetch failed for %s: %s", branch_id, type(exc).__name__)
        return None


def enrich_with_seed_names(live: list[Item], seed: list[Item]) -> list[Item]:
    """When live items lack rich tags, copy attributes from seed items with the same name."""
    by_name = {s.name.lower(): s for s in seed}
    out: list[Item] = []
    for it in live:
        seed_hit = by_name.get(it.name.lower())
        if not seed_hit or it.tags:
            out.append(it)
            continue
        out.append(
            item(
                it.id,
                it.name,
                it.category or seed_hit.category,
                it.price or seed_hit.price,
                it.description or seed_hit.description,
                seed_hit.sweetness,
                seed_hit.caffeine,
                seed_hit.dairy,
                seed_hit.vegan,
                seed_hit.hot,
                seed_hit.cold,
                seed_hit.tags,
                seed_hit.allergens,
            )
        )
    return out
