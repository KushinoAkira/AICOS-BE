"""Offline unit tests: no model weights, no network, no LLM key needed."""
from rag.barista import Barista, allowed, constraints, mask
from common.menu import MENU


def test_mask_removes_phone_and_email():
    out = mask("goi 0912345678 hoac a@b.com")
    assert "0912345678" not in out and "a@b.com" not in out


def test_dairy_free_filters_milk_items():
    c = constraints("mình bị dị ứng sữa, có món nào không?")
    safe = [it for it in MENU if allowed(it, c)]
    assert safe, "expected at least one safe item"
    assert all("sữa" not in it.allergens and "milk" not in it.allergens for it in safe)


def test_grounded_rejects_denial_when_suggestions_exist():
    ctx = "Món chay: Trà đào"
    assert not Barista.grounded("Quán chưa có món chay nào", ctx, has_suggestions=True)


def test_grounded_accepts_plain_answer():
    assert Barista.grounded("Bạn thử Trà đào nhé.", "Trà đào 45.000đ", has_suggestions=True)
