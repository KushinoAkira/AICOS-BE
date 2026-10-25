"""Runtime recommender: scores items from a customer's past item ids (item-item similarity blended with ALS item factors)."""
from __future__ import annotations

from pathlib import Path

import numpy as np

from common.menu import BY_ID

MODEL = Path(__file__).resolve().parent.parent / "models/reco/reco.npz"


class Recommender:
    def __init__(self, path: Path = MODEL):
        z = np.load(path, allow_pickle=False)
        self.ids = [str(i) for i in z["ids"]]
        self.sim, self.pop = z["similarity"], z["popularity"]
        Y = z["item_factors"]
        self.Y = Y / (np.linalg.norm(Y, axis=1, keepdims=True) + 1e-9)
        self.index = {k: n for n, k in enumerate(self.ids)}

    def recommend(self, history: list[str], k: int = 5, exclude: set[str] | None = None, allow: set[str] | None = None) -> list[dict]:
        seen = [self.index[h] for h in history if h in self.index]
        pop = self.pop / (self.pop.max() + 1e-9)
        if seen:
            profile = np.zeros(len(self.ids)); np.add.at(profile, seen, 1.0)
            knn = profile @ self.sim
            taste = (profile @ self.Y) @ self.Y.T
            score = 0.45 * knn / (knn.max() + 1e-9) + 0.4 * taste / (np.abs(taste).max() + 1e-9) + 0.15 * pop
            reason = "Hợp với những món bạn hay gọi"
        else:
            score, reason = pop, "Món được nhiều khách chọn nhất"
        out = []
        for j in np.argsort(-score):
            pid = self.ids[j]
            if (exclude and pid in exclude) or (allow is not None and pid not in allow) or pid not in BY_ID:
                continue
            it = BY_ID[pid]
            out.append({"productId": pid, "name": it.name, "price": it.price, "reason": reason, "score": round(float(score[j]), 3)})
            if len(out) == k:
                break
        return out
