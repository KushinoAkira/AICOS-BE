"""Recommender synthetic data must only emit IDs that exist in the selected catalog."""
from __future__ import annotations

from common.menu import MENU
from reco.simulate import ALL, POPULAR, SEGMENTS, assert_orders_in_catalog, build_pools, simulate
from reco.train import IDS, matrix, split


def test_default_pools_subset_of_menu():
    allowed = {it.id for it in MENU}
    assert set(ALL) <= allowed
    assert set(POPULAR) <= allowed
    for name, pool in SEGMENTS.items():
        assert pool, f"segment {name} empty"
        assert set(pool) <= allowed


def test_simulate_orders_only_use_catalog_ids():
    orders = simulate(n_customers=120, seed=7)
    assert_orders_in_catalog(orders, MENU)
    allowed = set(IDS)
    for order in orders:
        assert order["items"]
        assert set(order["items"]) <= allowed


def test_train_matrix_accepts_simulated_orders():
    orders = simulate(n_customers=80, seed=3)
    train, test = split(orders)
    users = {c: n for n, c in enumerate(sorted(test))}
    R = matrix(train, users)
    assert R.shape == (len(users), len(IDS))
    assert R.sum() > 0


def test_narrow_catalog_never_emits_missing_ids():
    """Mirrors CapheNha Q1-style menus that omit some sample IDs (e.g. p12/p13)."""
    narrow = [it for it in MENU if it.id not in {"p12", "p13", "p16", "p17", "p19"}]
    assert len(narrow) < len(MENU)
    segments, all_ids, popular = build_pools(narrow)
    allowed = {it.id for it in narrow}
    assert set(all_ids) == allowed
    assert set(popular) <= allowed
    for pool in segments.values():
        assert set(pool) <= allowed
    orders = simulate(n_customers=100, seed=19, items=narrow)
    assert_orders_in_catalog(orders, narrow)
    # Training index for the narrow catalog must cover every emitted id.
    narrow_idx = {pid: n for n, pid in enumerate(all_ids)}
    for order in orders:
        for pid in order["items"]:
            assert pid in narrow_idx


def test_empty_catalog_rejected():
    try:
        build_pools([])
        assert False, "expected ValueError"
    except ValueError:
        pass
