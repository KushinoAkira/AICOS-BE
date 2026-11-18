"""Apriori: milk tea often bought with cake → strong association rule."""
from __future__ import annotations

from apriori.mine import apriori


def test_milk_tea_with_cake():
    # 10 baskets: 6 have both trà sữa + bánh; 2 only trà sữa; 2 only other
    txs = [
        ["tra-sua", "banh-ngot"],
        ["tra-sua", "banh-ngot"],
        ["tra-sua", "banh-ngot"],
        ["tra-sua", "banh-ngot"],
        ["tra-sua", "banh-ngot"],
        ["tra-sua", "banh-ngot"],
        ["tra-sua"],
        ["tra-sua"],
        ["ca-phe"],
        ["nuoc-ep"],
    ]
    rules = apriori(txs, min_support=0.2, min_confidence=0.5)
    pair = next(
        r
        for r in rules
        if r["antecedent"] == ["tra-sua"] and r["consequent"] == ["banh-ngot"]
    )
    assert pair["support"] >= 0.5
    assert pair["confidence"] >= 0.7
    assert pair["lift"] > 1.0
    assert pair["transactionCount"] == 10


def test_empty_transactions():
    assert apriori([]) == []
    assert apriori([[], []]) == []
