"""Backward-compatible default catalog (demo brand). Prefer CatalogStore for tenant/branch scope. """
from __future__ import annotations

from common.catalogs import SEED
from common.item import Item  # noqa: F401 — re-export for older imports

_demo = SEED["demo"]["default"]
MENU: list[Item] = list(_demo.items)
SHOP = dict(_demo.shop)
TOPPINGS = list(_demo.toppings)
BY_ID = {i.id: i for i in MENU}
