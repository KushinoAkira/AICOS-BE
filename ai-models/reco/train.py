"""Item recommender: implicit-feedback ALS + item-item cosine blend, compared with the "most popular" baseline.

    python -m reco.train     -> models/reco/{reco.npz, metrics.json}
Split by time: every customer's LAST order is held out; we recommend K items from the earlier history and check the hit.
"""
from __future__ import annotations

import json
import math
from collections import defaultdict
from pathlib import Path

import numpy as np

from common.menu import MENU
from reco.simulate import simulate

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "models/reco"
IDS = [i.id for i in MENU]
IDX = {k: n for n, k in enumerate(IDS)}


def als(R: np.ndarray, factors: int = 12, reg: float = 0.1, alpha: float = 20.0, iters: int = 15, seed: int = 3):
    """Hu-Koren-Volinsky implicit ALS. R: users x items counts."""
    rng = np.random.default_rng(seed)
    U, I = R.shape
    P = (R > 0).astype(float)
    C = 1 + alpha * R
    X = rng.normal(0, 0.1, (U, factors)); Y = rng.normal(0, 0.1, (I, factors))
    eye = reg * np.eye(factors)
    for _ in range(iters):
        for u in range(U):
            Cu = C[u]
            X[u] = np.linalg.solve((Y.T * Cu) @ Y + eye, (Y.T * Cu) @ P[u])
        for i in range(I):
            Ci = C[:, i]
            Y[i] = np.linalg.solve((X.T * Ci) @ X + eye, (X.T * Ci) @ P[:, i])
    return X, Y


def item_cosine(R: np.ndarray) -> np.ndarray:
    B = (R > 0).astype(float)
    co = B.T @ B
    norms = np.sqrt(np.diag(co)) + 1e-9
    return co / np.outer(norms, norms)


def split(orders):
    by_c: dict[str, list[dict]] = defaultdict(list)
    for o in orders:
        by_c[o["customer"]].append(o)
    train, test = [], {}
    for c, os_ in by_c.items():
        os_.sort(key=lambda o: o["t"])
        train += os_[:-1]
        test[c] = os_[-1]["items"]
    return train, test


def matrix(orders, users):
    R = np.zeros((len(users), len(IDS)))
    for o in orders:
        for it in o["items"]:
            R[users[o["customer"]], IDX[it]] += 1
    return R


def metrics(rank_fn, users, R, test, k=5):
    hits, ndcg, n = 0, 0.0, 0
    for c, truth in test.items():
        seen = set(np.nonzero(R[users[c]])[0])
        ranked = [j for j in rank_fn(users[c]) if j not in seen][:k]
        truth_idx = {IDX[t] for t in truth}
        got = [j in truth_idx for j in ranked]
        hits += any(got)
        ndcg += sum(1 / math.log2(r + 2) for r, g in enumerate(got) if g) / sum(1 / math.log2(r + 2) for r in range(min(len(truth_idx), k)))
        n += 1
    return {f"hit_rate@{k}": round(hits / n, 3), f"ndcg@{k}": round(ndcg / n, 3)}


def main() -> None:
    orders = simulate()
    train, test = split(orders)
    users = {c: n for n, c in enumerate(sorted(test))}
    R = matrix(train, users)
    pop = np.argsort(-R.sum(0))
    X, Y = als(R)
    S = item_cosine(R)
    als_scores = X @ Y.T
    blend = lambda u: 0.15 * als_scores[u] / (np.abs(als_scores[u]).max() + 1e-9) + 0.85 * (R[u] @ S) / (np.abs(R[u] @ S).max() + 1e-9)
    report = {
        "orders": len(orders), "customers": len(users), "catalog": len(IDS),
        "popularity_baseline": metrics(lambda u: list(pop), users, R, test),
        "item_knn": metrics(lambda u: list(np.argsort(-(R[u] @ S))), users, R, test),
        "als": metrics(lambda u: list(np.argsort(-als_scores[u])), users, R, test),
        "our_blend": metrics(lambda u: list(np.argsort(-blend(u))), users, R, test),
        "data_note": "synthetic orders with hidden taste segments (replace by real order history)",
    }
    OUT.mkdir(parents=True, exist_ok=True)
    np.savez(OUT / "reco.npz", item_factors=Y, similarity=S, popularity=R.sum(0), ids=np.array(IDS))
    (OUT / "metrics.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()

