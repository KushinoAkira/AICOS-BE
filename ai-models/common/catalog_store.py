"""Resolve the active Catalog for a chat / recommendation request.

Priority:
1. FE menu snapshot (products the customer actually sees for this branch)
2. Live Menu API for `branchId`
3. Seed catalog for a *known* `tenantCode` (+ branch name heuristic)

Never invent a foreign brand menu: unknown tenant without snapshot/live → empty catalog.
When the FE sends `menuItems` (even empty), that list is authoritative — no seed merge.
"""
from __future__ import annotations

import logging
from typing import Any

from common.catalogs import SEED, Catalog, seed_catalog
from common.item import Item
from common.menu_client import enrich_with_seed_names, fetch_branch_menu, items_from_snapshot

log = logging.getLogger("aicos.catalog_store")


class CatalogStore:
    async def resolve(
        self,
        *,
        tenant_code: str | None,
        branch_id: str | None,
        branch_name: str | None = None,
        menu_items: list[dict[str, Any]] | None = None,
    ) -> Catalog:
        tenant = (tenant_code or "").strip().lower() or None
        known = tenant in SEED if tenant else False
        seed = seed_catalog(tenant, branch_name or branch_id) if known else None
        bid = (branch_id or (seed.branch_id if seed else "unknown")).strip()
        bname = (branch_name or (seed.branch_name if seed else "Chi nhánh")).strip()
        code = tenant or (seed.tenant_code if seed else "unknown")

        items: list[Item] | None = None
        source = "empty"

        # Explicit FE snapshot wins — including empty (sold-out / unpublished branch).
        if menu_items is not None:
            items = items_from_snapshot(menu_items)
            source = "snapshot"
        elif branch_id:
            live = await fetch_branch_menu(branch_id)
            if live is not None:
                items = enrich_with_seed_names(live, list(seed.items) if seed else [])
                source = "live"

        if items is None and seed is not None:
            log.info("catalog source=seed tenant=%s branch=%s items=%s", code, bid, len(seed.items))
            return Catalog(code, bid, bname, seed.items, seed.shop, seed.toppings)

        if items is None:
            log.warning("catalog source=empty tenant=%s branch=%s (no snapshot/live/seed)", code, bid)
            return Catalog(code, bid, bname, (), {"name": bname, "loyalty": ""}, ())

        shop = seed.shop if seed else {"name": bname or code, "loyalty": "Mỗi 10.000đ được 1 điểm"}
        toppings = seed.toppings if seed else ()
        log.info("catalog source=%s tenant=%s branch=%s items=%s", source, code, bid, len(items))
        return Catalog(code, bid, bname, tuple(items), shop, toppings)


STORE = CatalogStore()
