"""Synthetic order history with taste segments (replace with real orders when the shop has data).

Each customer belongs to a hidden taste segment; 25% of picks are exploratory.
The recommender never sees the segment.

All product IDs are taken from the active MENU catalog. Preferred segment recipes are
intersected with that catalog so training never emits IDs the recommender cannot index.
"""
from __future__ import annotations

import random
from collections.abc import Iterable, Sequence

from common.menu import MENU, Item

# Preferred product IDs per taste segment (subset of the full CapheNha sample menu).
# Missing IDs are dropped when the active catalog is narrower.
_PREFERRED_SEGMENTS: dict[str, tuple[str, ...]] = {
    "milk_coffee": ("p03", "p05", "p06", "p10", "p22"),
    "traditional": ("p01", "p02", "p09", "p24", "p04"),
    "fruity": ("p11", "p12", "p17", "p18", "p19", "p20"),
    "sweet": ("p13", "p21", "p16", "p08", "p23"),
    "healthy": ("p04", "p14", "p15", "p18", "p19", "p25"),
}

_PREFERRED_POPULAR: tuple[str, ...] = ("p02", "p05", "p11", "p13", "p03")

# Tag / category heuristics used when a preferred segment is empty after catalog filter.
_SEGMENT_HINTS: dict[str, tuple[str, ...]] = {
    "milk_coffee": ("sữa", "béo", "latte", "matcha"),
    "traditional": ("truyền thống", "đậm", "espresso", "đen"),
    "fruity": ("giải nhiệt", "trà", "trái cây", "mùa hè"),
    "sweet": ("ngọt", "hot", "socola", "tiramisu"),
    "healthy": ("ít calo", "không caffeine", "vitamin", "không đường"),
}


def _catalog_ids(items: Sequence[Item] | None = None) -> list[str]:
    src = list(items) if items is not None else list(MENU)
    if not src:
        raise ValueError("recommender catalog is empty")
    return [it.id for it in src]


def _hint_matches(it: Item, hints: tuple[str, ...]) -> bool:
    blob = " ".join((it.name, it.category, *it.tags, *getattr(it, "allergens", ()))).lower()
    return any(h.lower() in blob for h in hints)


def build_pools(items: Sequence[Item] | None = None) -> tuple[dict[str, list[str]], list[str], list[str]]:
    """Return (segments, all_ids, popular) using only IDs present in *items* / MENU."""
    catalog = list(items) if items is not None else list(MENU)
    by_id = {it.id: it for it in catalog}
    all_ids = _catalog_ids(catalog)
    allowed = set(all_ids)

    segments: dict[str, list[str]] = {}
    for name, preferred in _PREFERRED_SEGMENTS.items():
        pool = [pid for pid in preferred if pid in allowed]
        if len(pool) < 2:
            hints = _SEGMENT_HINTS.get(name, ())
            pool = [it.id for it in catalog if _hint_matches(it, hints)]
        if not pool:
            pool = list(all_ids)
        segments[name] = pool

    popular = [pid for pid in _PREFERRED_POPULAR if pid in allowed]
    if len(popular) < 2:
        # Fall back to a stable slice of the catalog so exploration still works.
        popular = all_ids[: min(5, len(all_ids))]
    return segments, all_ids, popular


# Module-level pools for the default MENU (tests and train.py import these).
SEGMENTS, ALL, POPULAR = build_pools()


def simulate(
    n_customers: int = 900,
    seed: int = 11,
    items: Sequence[Item] | None = None,
) -> list[dict]:
    """Generate synthetic orders whose every product ID exists in the selected catalog."""
    segments, all_ids, popular = build_pools(items)
    if not all_ids:
        raise ValueError("cannot simulate orders for an empty catalog")
    allowed = set(all_ids)
    rng = random.Random(seed)
    names = list(segments)
    orders: list[dict] = []
    t = 0
    for c in range(n_customers):
        seg = rng.choice(names)
        for _ in range(rng.randint(6, 18)):
            t += 1
            hour = rng.choices([8, 10, 13, 15, 19], weights=[3, 2, 2, 3, 2])[0]
            picked: list[str] = []
            for _ in range(rng.choices([1, 2], weights=[0.7, 0.3])[0]):
                r = rng.random()
                pool = segments[seg] if r < 0.75 else popular if r < 0.85 else all_ids
                picked.append(rng.choice(pool))
            unique = sorted(set(picked))
            if any(pid not in allowed for pid in unique):
                raise RuntimeError(f"simulate emitted IDs outside catalog: {unique}")
            orders.append({"customer": f"c{c}", "t": t, "hour": hour, "items": unique})
    return orders


def assert_orders_in_catalog(orders: Iterable[dict], items: Sequence[Item] | None = None) -> None:
    allowed = set(_catalog_ids(items))
    missing = sorted({pid for o in orders for pid in o["items"] if pid not in allowed})
    if missing:
        raise AssertionError(f"order items missing from catalog: {missing}")
