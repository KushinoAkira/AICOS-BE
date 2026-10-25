"""Synthetic order history with taste segments (replace with real orders when the shop has data).

Each customer belongs to a hidden taste segment; 25% of picks are exploratory. The recommender never sees the segment.
"""
from __future__ import annotations

import random

SEGMENTS: dict[str, list[str]] = {
    "milk_coffee": ["p03", "p05", "p06", "p10", "p22"],
    "traditional": ["p01", "p02", "p09", "p24", "p04"],
    "fruity": ["p11", "p12", "p17", "p18", "p19", "p20"],
    "sweet": ["p13", "p21", "p16", "p08", "p23"],
    "healthy": ["p04", "p14", "p15", "p18", "p19", "p25"],
}
ALL = sorted({i for items in SEGMENTS.values() for i in items} | {f"p{n:02d}" for n in range(1, 26)})
POPULAR = ["p02", "p05", "p11", "p13", "p03"]


def simulate(n_customers: int = 900, seed: int = 11) -> list[dict]:
    rng = random.Random(seed)
    names = list(SEGMENTS)
    orders: list[dict] = []
    t = 0
    for c in range(n_customers):
        seg = rng.choice(names)
        for _ in range(rng.randint(6, 18)):
            t += 1
            hour = rng.choices([8, 10, 13, 15, 19], weights=[3, 2, 2, 3, 2])[0]
            items = []
            for _ in range(rng.choices([1, 2], weights=[0.7, 0.3])[0]):
                r = rng.random()
                pool = SEGMENTS[seg] if r < 0.75 else POPULAR if r < 0.85 else ALL
                items.append(rng.choice(pool))
            orders.append({"customer": f"c{c}", "t": t, "hour": hour, "items": sorted(set(items))})
    return orders
