"""Drink ↔ food pairing knowledge used by RAG and the recommender.

Sources distilled for AICOS (coffee-chain VN + classic café pairings):
- Espresso / black coffee with bold chocolate or coffee desserts (tiramisu)
- Latte / cappuccino with buttery pastry (croissant)
- Vietnamese cà phê sữa with bánh mì / bánh mì que
- Matcha with sweeter pastry; fruit tea / soda with light snacks
- Cold brew with savoury light bites; hot chocolate / ginger tea on rainy days

Rules match by normalised name fragments. At runtime they are resolved ONLY against
the active branch catalog so brand A never suggests items from brand B.
"""
from __future__ import annotations

from dataclasses import dataclass

from common.item import Item


@dataclass(frozen=True)
class PairingRule:
    left: tuple[str, ...]
    right: tuple[str, ...]
    reason: str
    tags: tuple[str, ...] = ()


_ACCENT = str.maketrans(
    "áàảãạăắằẳẵặâấầẩẫậéèẻẽẹêếềểễệíìỉĩịóòỏõọôốồổỗộơớờởỡợúùủũụưứừửữựýỳỷỹỵđ"
    "ÁÀẢÃẠĂẮẰẲẴẶÂẤẦẨẪẬÉÈẺẼẸÊẾỀỂỄỆÍÌỈĨỊÓÒỎÕỌÔỐỒỔỖỘƠỚỜỞỠỢÚÙỦŨỤƯỨỪỬỮỰÝỲỶỸỴĐ",
    "aaaaaaaaaaaaaaaaaeeeeeeeeeeeiiiiiooooooooooooooooouuuuuuuuuuuyyyyyd"
    "AAAAAAAAAAAAAAAAAEEEEEEEEEEEIIIIIOOOOOOOOOOOOOOOOOUUUUUUUUUUUYYYYYD",
)


def _norm(s: str) -> str:
    return " ".join(s.lower().translate(_ACCENT).split())


PAIRING_RULES: list[PairingRule] = [
    PairingRule(
        ("espresso", "ca phe den", "cà phê đen", "americano"),
        ("tiramisu", "banh tiramisu", "socola", "sô cô la", "chocolate"),
        "Cà phê đậm cân vị ngọt và béo của bánh/socola.",
        ("ăn kèm", "đậm"),
    ),
    PairingRule(
        ("latte", "cappuccino", "ca phe sua", "cà phê sữa", "bac xiu", "bạc xỉu"),
        ("croissant", "banh sung bo", "bánh sừng bò", "banh mi que", "bánh mì que"),
        "Sữa trong ly làm mềm vị bánh bơ hoặc bánh mì mặn nhẹ.",
        ("ăn kèm", "bữa sáng"),
    ),
    PairingRule(
        ("ca phe sua", "cà phê sữa", "bac xiu", "bạc xỉu", "ca phe den", "cà phê đen"),
        ("banh mi", "bánh mì", "pate", "bánh mì que"),
        "Cặp sáng kiểu Việt: đắng/ngọt của cà phê cân nhân mặn béo.",
        ("ăn sáng", "truyền thống"),
    ),
    PairingRule(
        ("matcha", "tra xanh", "trà xanh"),
        ("croissant", "banh", "tiramisu", "hat dieu", "hạt điều"),
        "Vị matcha thanh cắt độ ngọt/béo của bánh hoặc đồ ăn nhẹ.",
        ("ăn kèm", "matcha"),
    ),
    PairingRule(
        ("tra dao", "trà đào", "tra vai", "trà vải", "tra cam sa", "cam sả"),
        ("hat dieu", "hạt điều", "banh mi que", "bánh mì que", "an vat"),
        "Trà trái cây thanh mát hợp ăn nhẹ giòn, không át vị trà.",
        ("giải nhiệt", "mùa hè"),
    ),
    PairingRule(
        ("cold brew", "coldbrew", "ca phe muoi", "cà phê muối"),
        ("croissant", "hat dieu", "hạt điều", "banh mi que"),
        "Cà phê ủ lạnh/kem muối hợp đồ giòn nhẹ, không quá ngọt.",
        ("mùa hè", "ăn kèm"),
    ),
    PairingRule(
        ("tra gung", "trà gừng", "socola nong", "socola nóng", "hoa cuc", "hoa cúc"),
        ("croissant", "tiramisu", "banh"),
        "Đồ uống ấm hợp ngày mưa hoặc khi muốn món ngọt nhẹ.",
        ("ấm", "ngày mưa"),
    ),
    PairingRule(
        ("tra sua", "trà sữa", "tran chau", "trân châu"),
        ("hat dieu", "hạt điều", "banh"),
        "Trà sữa ngọt nên kèm đồ giòn mặn nhẹ để đỡ ngán.",
        ("ngọt", "ăn vặt"),
    ),
    PairingRule(
        ("nuoc ep cam", "nước ép cam", "dua hau", "dưa hấu", "soda chanh", "sinh to", "sinh tố"),
        ("hat dieu", "hạt điều", "banh mi que"),
        "Nước trái cây/soda hợp snack nhẹ, không caffeine.",
        ("giải nhiệt", "không caffeine"),
    ),
]

PAIRING_DOCS: list[str] = [
    "Nguyên tắc ghép món: đồ uống đậm (espresso, cà phê đen) hợp bánh ngọt đậm như tiramisu hoặc socola.",
    "Latte, cappuccino hoặc cà phê sữa hợp croissant bơ / bánh mì que vì sữa cân vị bánh.",
    "Buổi sáng Việt Nam: cà phê sữa đá hoặc bạc xỉu hay đi với bánh mì / bánh mì que pate.",
    "Trà đào cam sả, trà vải hợp ngày nóng; nên gợi ý thêm snack nhẹ nếu khách muốn ăn kèm.",
    "Matcha latte hợp bánh ngọt hoặc hạt điều rang — vị trà cắt độ ngọt.",
    "Khách dị ứng sữa: ưu tiên cà phê đen, americano, cold brew, trà trái cây, nước ép — không gợi ý latte/trà sữa.",
    "Trẻ em / mang thai / khó ngủ: tránh caffeine; gợi ý trà hoa cúc, trà gừng mật ong, nước ép, soda.",
    "Chỉ được gợi ý món đang bán tại đúng chi nhánh khách đang xem; không lấy món của quán/chi nhánh khác.",
]


def matches(name: str, fragments: tuple[str, ...]) -> bool:
    n = _norm(name)
    return any(_norm(f) in n for f in fragments)


def partners_for(item: Item, catalog: list[Item], limit: int = 3) -> list[tuple[Item, str]]:
    """Return other items in *this* catalog that pair with `item`."""
    out: list[tuple[Item, str]] = []
    seen: set[str] = set()
    for rule in PAIRING_RULES:
        left_hit = matches(item.name, rule.left) or matches(item.category, rule.left) or any(matches(t, rule.left) for t in item.tags)
        right_hit = matches(item.name, rule.right) or matches(item.category, rule.right) or any(matches(t, rule.right) for t in item.tags)
        if not left_hit and not right_hit:
            continue
        want = rule.right if left_hit else rule.left
        for other in catalog:
            if other.id == item.id or other.id in seen:
                continue
            if matches(other.name, want) or matches(other.category, want) or any(matches(t, want) for t in other.tags):
                seen.add(other.id)
                out.append((other, rule.reason))
                if len(out) >= limit:
                    return out
    return out


def pairing_boost(a_id: str, b_id: str, by_id: dict[str, Item]) -> float:
    a, b = by_id.get(a_id), by_id.get(b_id)
    if not a or not b:
        return 0.0
    for rule in PAIRING_RULES:
        a_l = matches(a.name, rule.left) or any(matches(t, rule.left) for t in a.tags)
        a_r = matches(a.name, rule.right) or any(matches(t, rule.right) for t in a.tags)
        b_l = matches(b.name, rule.left) or any(matches(t, rule.left) for t in b.tags)
        b_r = matches(b.name, rule.right) or any(matches(t, rule.right) for t in b.tags)
        if (a_l and b_r) or (a_r and b_l):
            return 0.35
    return 0.0
