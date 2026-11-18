"""Pure-Python Apriori: frequent itemsets → association rules (support / confidence / lift)."""
from __future__ import annotations

from collections import defaultdict
from itertools import combinations
from typing import Iterable


def _fro(items: Iterable[str]) -> frozenset[str]:
    return frozenset(items)


def apriori(
    transactions: list[list[str]],
    min_support: float = 0.05,
    min_confidence: float = 0.4,
    max_itemset_size: int = 3,
) -> list[dict]:
    """
    Mine association rules from baskets of product ids (or names).

    Returns dicts: antecedent, consequent, support, confidence, lift, transactionCount.
    """
    if not transactions:
        return []
    n = len(transactions)
    baskets = [ _fro(t) for t in transactions if t ]
    if not baskets:
        return []

    # Count 1-itemsets
    item_count: dict[frozenset[str], int] = defaultdict(int)
    for basket in baskets:
        for item in basket:
            item_count[_fro([item])] += 1

    min_count = max(1, int(min_support * n + 1e-9))
    freq: dict[int, dict[frozenset[str], int]] = {
        1: {k: v for k, v in item_count.items() if v >= min_count}
    }
    k = 2
    while k <= max_itemset_size and freq.get(k - 1):
        candidates: dict[frozenset[str], int] = defaultdict(int)
        prev = list(freq[k - 1])
        for i in range(len(prev)):
            for j in range(i + 1, len(prev)):
                union = prev[i] | prev[j]
                if len(union) != k:
                    continue
                # prune: all (k-1) subsets must be frequent
                if any(_fro(subset) not in freq[k - 1] for subset in combinations(union, k - 1)):
                    continue
                candidates[union] = 0
        for basket in baskets:
            for cand in candidates:
                if cand <= basket:
                    candidates[cand] += 1
        freq[k] = {c: cnt for c, cnt in candidates.items() if cnt >= min_count}
        k += 1

    # Support lookup for all frequent sets
    support_count: dict[frozenset[str], int] = {}
    for level in freq.values():
        support_count.update(level)

    rules: list[dict] = []
    for itemset, count in support_count.items():
        if len(itemset) < 2:
            continue
        support = count / n
        for r in range(1, len(itemset)):
            for antecedent in combinations(itemset, r):
                ant = _fro(antecedent)
                cons = itemset - ant
                if not cons or ant not in support_count:
                    continue
                conf = count / support_count[ant]
                if conf < min_confidence:
                    continue
                cons_count = support_count.get(cons)
                if cons_count is None:
                    # consequent may not be stored alone if size>1 and never frequent alone as that set —
                    # approximate with product of singles when missing
                    cons_sup = 1.0
                    for c in cons:
                        single = support_count.get(_fro([c]))
                        if not single:
                            cons_sup = 0.0
                            break
                        cons_sup *= single / n
                else:
                    cons_sup = cons_count / n
                lift = conf / cons_sup if cons_sup > 0 else 0.0
                rules.append(
                    {
                        "antecedent": sorted(ant),
                        "consequent": sorted(cons),
                        "support": round(support, 6),
                        "confidence": round(conf, 6),
                        "lift": round(lift, 6),
                        "transactionCount": n,
                    }
                )

    rules.sort(key=lambda r: (-r["lift"], -r["confidence"], -r["support"]))
    return rules
