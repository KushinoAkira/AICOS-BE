"""Runtime recommender scoped to one Catalog.

Uses ALS/item-item when history ids match the trained demo matrix; otherwise falls back to
tag similarity + pairing boost + popularity — always filtered to `allow` (branch menu only).
"""
from __future__ import annotations

from pathlib import Path

import numpy as np

from common.catalogs import Catalog
from common.item import Item
from common.menu import BY_ID as DEMO_BY_ID
from common.pairings import pairing_boost, partners_for

MODEL = Path(__file__).resolve().parent.parent / "models/reco/reco.npz"


class Recommender:
    def __init__(self, path: Path = MODEL):
        if path.exists():
            z = np.load(path, allow_pickle=False)
            self.ids = [str(i) for i in z["ids"]]
            self.sim, self.pop = z["similarity"], z["popularity"]
            Y = z["item_factors"]
            self.Y = Y / (np.linalg.norm(Y, axis=1, keepdims=True) + 1e-9)
            self.index = {k: n for n, k in enumerate(self.ids)}
        else:
            self.ids, self.sim, self.pop, self.Y, self.index = [], np.zeros((0, 0)), np.zeros(0), np.zeros((0, 0)), {}

    def recommend(
        self,
        history: list[str],
        k: int = 5,
        exclude: set[str] | None = None,
        allow: set[str] | None = None,
        catalog: Catalog | None = None,
    ) -> list[dict]:
        by_id: dict[str, Item] = catalog.by_id if catalog else DEMO_BY_ID
        allow_ids = set(allow) if allow is not None else set(by_id)
        exclude = exclude or set()

        # Prefer trained matrix when enough overlap; else content + pairings on this catalog only.
        seen_trained = [self.index[h] for h in history if h in self.index]
        use_matrix = bool(seen_trained) and any(pid in self.index and pid in allow_ids for pid in by_id)

        if use_matrix:
            return self._from_matrix(history, k, exclude, allow_ids, by_id)

        return self._from_catalog(history, k, exclude, allow_ids, by_id, catalog)

    def _from_matrix(self, history: list[str], k: int, exclude: set[str], allow: set[str], by_id: dict[str, Item]) -> list[dict]:
        seen = [self.index[h] for h in history if h in self.index]
        pop = self.pop / (self.pop.max() + 1e-9) if len(self.pop) else self.pop
        if seen:
            profile = np.zeros(len(self.ids))
            np.add.at(profile, seen, 1.0)
            knn = profile @ self.sim
            taste = (profile @ self.Y) @ self.Y.T
            score = 0.45 * knn / (knn.max() + 1e-9) + 0.4 * taste / (np.abs(taste).max() + 1e-9) + 0.15 * pop
            reason = "Hợp với những món bạn hay gọi"
        else:
            score, reason = pop, "Món được nhiều khách chọn nhất"
        out: list[dict] = []
        for j in np.argsort(-score):
            pid = self.ids[j]
            if pid in exclude or pid not in allow or pid not in by_id:
                continue
            boost = max((pairing_boost(h, pid, by_id) for h in history), default=0.0)
            it = by_id[pid]
            why = reason
            if boost:
                why = "Hay gọi kèm với món bạn từng chọn"
            out.append({"productId": pid, "name": it.name, "price": it.price, "reason": why, "score": round(float(score[j]) + boost, 3)})
            if len(out) == k:
                break
        return out

    def _from_catalog(
        self,
        history: list[str],
        k: int,
        exclude: set[str],
        allow: set[str],
        by_id: dict[str, Item],
        catalog: Catalog | None,
    ) -> list[dict]:
        items = [by_id[i] for i in allow if i in by_id and i not in exclude]
        if not items:
            return []
        hist_items = [by_id[h] for h in history if h in by_id]
        scores: dict[str, float] = {it.id: 0.05 for it in items}
        reasons: dict[str, str] = {it.id: "Gợi ý trong menu chi nhánh này" for it in items}

        # Popularity proxy from tags
        for it in items:
            if "bán chạy" in it.tags:
                scores[it.id] += 0.25
                reasons[it.id] = "Món được nhiều khách chọn tại quán"
            if "giải nhiệt" in it.tags or "mùa hè" in it.tags:
                scores[it.id] += 0.05

        # Taste from history tags / categories
        if hist_items:
            hist_tags = {t for h in hist_items for t in h.tags}
            hist_cats = {h.category for h in hist_items}
            for it in items:
                if it.id in {h.id for h in hist_items}:
                    scores[it.id] -= 0.5
                    continue
                overlap = len(hist_tags & set(it.tags))
                if it.category in hist_cats:
                    overlap += 1
                if overlap:
                    scores[it.id] += 0.15 * overlap
                    reasons[it.id] = "Hợp với những món bạn hay gọi"

            # Explicit pairing partners of the last ordered items
            pool = list(catalog.items) if catalog else items
            for h in hist_items[-3:]:
                for partner, why in partners_for(h, pool, limit=4):
                    if partner.id in scores:
                        scores[partner.id] += 0.4
                        reasons[partner.id] = why

        ranked = sorted(scores.items(), key=lambda kv: -kv[1])
        out: list[dict] = []
        for pid, sc in ranked:
            if pid in {h.id for h in hist_items}:
                continue
            it = by_id[pid]
            out.append({"productId": pid, "name": it.name, "price": it.price, "reason": reasons[pid], "score": round(sc, 3)})
            if len(out) == k:
                break
        return out
