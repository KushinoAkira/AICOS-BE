"""Tenant / branch isolation: suggestions must stay inside the active catalog."""
import asyncio

from common.catalog_store import CatalogStore
from common.catalogs import seed_catalog
from common.pairings import partners_for
from rag.barista import Barista, allowed, constraints
from reco.predict import Recommender


def test_caphenha_and_trasuamay_menus_do_not_overlap():
    coffee = seed_catalog("caphenha", "q1")
    tea = seed_catalog("trasuamay", "default")
    coffee_names = {i.name for i in coffee.items}
    tea_names = {i.name for i in tea.items}
    assert "Trà sữa trân châu đường đen" not in coffee_names
    assert "Espresso" not in tea_names
    assert coffee_names.isdisjoint(tea_names) or coffee_names & tea_names <= {"Trà đào", "Trà đào cam sả"}


def test_thu_duc_menu_is_strict_subset_of_brand_ideas():
    q1 = {i.name for i in seed_catalog("caphenha", "q1").items}
    td = {i.name for i in seed_catalog("caphenha", "td").items}
    assert "Sinh tố xoài" in td
    assert "Sinh tố xoài" not in q1
    assert "Espresso" in q1
    assert "Espresso" not in td


def test_pairings_only_resolve_inside_catalog():
    tea = seed_catalog("trasuamay")
    latteish = next(i for i in tea.items if "matcha" in i.name.lower())
    partners = partners_for(latteish, list(tea.items))
    assert partners
    assert all(p.id in tea.by_id for p, _ in partners)


def test_recommender_cannot_emit_foreign_ids():
    coffee = seed_catalog("caphenha", "q1")
    tea = seed_catalog("trasuamay")
    reco = Recommender()
    out = reco.recommend(["t02"], 5, allow={i.id for i in tea.items}, catalog=tea)
    assert out
    assert all(s["productId"] in tea.by_id for s in out)
    leaked = reco.recommend(["p05"], 5, allow={i.id for i in coffee.items}, catalog=coffee)
    assert all(s["productId"] in coffee.by_id for s in leaked)
    assert all(s["productId"] not in tea.by_id for s in leaked)


def test_snapshot_overrides_seed_and_keeps_isolation():
    store = CatalogStore()
    snap = [
        {"id": "live-1", "name": "Bạc xỉu", "price": 45000, "category": "Cà phê"},
        {"id": "live-2", "name": "Croissant bơ", "price": 35000, "category": "Bánh"},
    ]
    cat = asyncio.run(
        store.resolve(tenant_code="caphenha", branch_id="b-q1", branch_name="Quận 1", menu_items=snap)
    )
    assert {i.id for i in cat.items} == {"live-1", "live-2"}
    assert "Trà sữa truyền thống" not in {i.name for i in cat.items}


def test_barista_reply_stays_in_catalog():
    barista = Barista()
    cat = seed_catalog("trasuamay")
    result = asyncio.run(barista.reply("Gợi ý món ngọt", [], catalog=cat))
    assert result["suggestions"]
    assert all(s["productId"] in cat.by_id for s in result["suggestions"])
    assert result["route"]["tenant"] == "trasuamay"


def test_dairy_free_still_filters():
    c = constraints("mình bị dị ứng sữa, có món nào không?")
    cat = seed_catalog("caphenha", "q1")
    safe = [it for it in cat.items if allowed(it, c)]
    assert safe
    assert all(not it.dairy and "sữa" not in it.allergens for it in safe)
