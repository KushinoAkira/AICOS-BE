"""Resolve the active Catalog for a chat / recommendation request.

Priority:
1. FE menu snapshot (products the customer actually sees for this branch)
2. Live Menu API for `branchId`
3. Seed catalog for `tenantCode` (+ branch name heuristic)
"""
from __future__ import annotations

import logging
from typing import Any

from common.catalogs import Catalog, seed_catalog
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
        seed = seed_catalog(tenant_code, branch_name or branch_id)
        tenant = (tenant_code or seed.tenant_code).strip().lower()
        bid = (branch_id or seed.branch_id).strip()
        bname = (branch_name or seed.branch_name).strip()

        items: list[Item] | None = None
        source = "seed"
        if menu_items:
            items = items_from_snapshot(menu_items)
            source = "snapshot"
        if not items and branch_id:
            live = await fetch_branch_menu(branch_id)
            if live:
                items = enrich_with_seed_names(live, list(seed.items))
                source = "live"

        if not items:
            log.info("catalog source=seed tenant=%s branch=%s items=%s", tenant, bid, len(seed.items))
            return Catalog(tenant, bid, bname, seed.items, seed.shop, seed.toppings)

        log.info("catalog source=%s tenant=%s branch=%s items=%s", source, tenant, bid, len(items))
        return Catalog(tenant, bid, bname, tuple(items), seed.shop, seed.toppings)


STORE = CatalogStore()
