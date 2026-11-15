"""Seed catalogs per brand / branch for offline demo and as fallback when Menu API is down.

Every suggestion path MUST go through a Catalog so brand A never leaks items into brand B.
"""
from __future__ import annotations

from dataclasses import dataclass, field

from common.item import Item, item


@dataclass(frozen=True)
class Catalog:
    tenant_code: str
    branch_id: str
    branch_name: str
    items: tuple[Item, ...]
    shop: dict[str, str] = field(default_factory=dict)
    toppings: tuple[tuple[str, int], ...] = ()

    @property
    def by_id(self) -> dict[str, Item]:
        return {i.id: i for i in self.items}


def _shop(name: str, hours: str = "07:00 - 22:00 hằng ngày") -> dict[str, str]:
    return {
        "name": name,
        "hours": hours,
        "wifi": "Có wifi miễn phí, mật khẩu in trên bill",
        "parking": "Có chỗ để xe máy miễn phí",
        "delivery": "Nhận đơn mang đi qua QR hoặc app; chưa giao tận nơi",
        "booking": "Nhóm trên 8 người vui lòng báo trước với nhân viên",
        "loyalty": "Mỗi 10.000đ được 1 điểm; đủ điểm đổi voucher tại mục Điểm thưởng",
    }


TOPPINGS_COFFEE = (("Shot espresso thêm", 10000), ("Kem cheese", 12000), ("Syrup vani", 8000))
TOPPINGS_TEA = (("Trân châu đường đen", 8000), ("Thạch dừa", 7000), ("Kem cheese", 12000))

# --- Cà phê Nhà · Quận 1 (đầy đủ) -------------------------------------------------
CAPHENHA_Q1: tuple[Item, ...] = (
    item("p01", "Cà phê đen", "Cà phê", 25000, "Cà phê phin đậm đà, không đường, vị đắng rõ.", 0, True, False, True, True, True, ("đậm", "truyền thống")),
    item("p02", "Cà phê sữa", "Cà phê", 29000, "Cà phê phin pha sữa đặc, béo ngọt, đậm vị Sài Gòn.", 2, True, True, False, True, True, ("bán chạy", "truyền thống"), ("sữa",)),
    item("p03", "Bạc xỉu", "Cà phê", 32000, "Nhiều sữa ít cà phê, ngọt dịu, dễ uống cho người mới.", 2, True, True, False, True, True, ("nhẹ", "béo"), ("sữa",)),
    item("p04", "Americano", "Cà phê", 35000, "Espresso pha loãng với nước, thanh và ít calo.", 0, True, False, True, True, True, ("ít calo", "tỉnh táo")),
    item("p05", "Latte", "Cà phê", 45000, "Espresso với sữa tươi và lớp bọt mịn, béo nhẹ.", 1, True, True, False, True, True, ("béo", "bán chạy"), ("sữa",)),
    item("p06", "Cappuccino", "Cà phê", 45000, "Espresso, sữa nóng và bọt dày, hơi đậm hơn latte.", 1, True, True, False, True, False, ("đậm", "bọt"), ("sữa",)),
    item("p07", "Cold brew", "Cà phê", 49000, "Cà phê ủ lạnh 18 giờ, vị êm, ít chua, ít đắng.", 0, True, False, True, False, True, ("ít chua", "mùa hè")),
    item("p08", "Cà phê muối", "Cà phê", 39000, "Cà phê sữa phủ lớp kem muối béo mặn, hot trend.", 2, True, True, False, False, True, ("hot", "béo"), ("sữa",)),
    item("p09", "Espresso", "Cà phê", 30000, "Một shot espresso đậm đặc, dành cho người mê cà phê.", 0, True, False, True, True, False, ("đậm", "tỉnh táo")),
    item("p10", "Matcha latte", "Trà", 48000, "Matcha Nhật pha sữa tươi, thơm trà, béo nhẹ.", 1, True, True, False, True, True, ("béo", "matcha"), ("sữa",)),
    item("p11", "Trà đào cam sả", "Trà", 42000, "Trà đen, đào miếng, cam và sả, thơm mát giải nhiệt.", 1, True, False, True, False, True, ("giải nhiệt", "bán chạy", "mùa hè")),
    item("p14", "Trà gừng mật ong", "Trà", 38000, "Trà gừng ấm bụng, mật ong tự nhiên, không caffeine.", 2, False, False, True, True, False, ("ấm", "không caffeine", "ngày mưa")),
    item("p15", "Trà hoa cúc", "Trà", 35000, "Hoa cúc khô pha mật ong, dịu nhẹ, không caffeine.", 1, False, False, True, True, True, ("không caffeine", "nhẹ")),
    item("p18", "Nước ép cam", "Nước ép", 40000, "Cam vắt nguyên chất, không đường, nhiều vitamin C.", 0, False, False, True, False, True, ("không đường", "vitamin", "giải nhiệt")),
    item("p20", "Soda chanh", "Nước giải khát", 32000, "Soda chanh tươi, sảng khoái, không caffeine.", 1, False, False, True, False, True, ("sảng khoái", "không caffeine")),
    item("p21", "Socola nóng", "Khác", 45000, "Socola đậm pha sữa nóng, ngọt ấm, hợp ngày mưa.", 3, False, True, False, True, False, ("ngọt", "ấm", "ngày mưa"), ("sữa",)),
    item("p22", "Croissant bơ", "Bánh", 35000, "Bánh sừng bò bơ giòn xốp, hợp ăn với cà phê.", 1, False, True, False, True, False, ("ăn kèm", "bữa sáng"), ("gluten", "sữa", "trứng")),
    item("p23", "Bánh tiramisu", "Bánh", 45000, "Tiramisu vị cà phê, mềm mịn.", 2, True, True, False, False, True, ("ngọt", "ăn kèm"), ("gluten", "sữa", "trứng")),
    item("p24", "Bánh mì que pate", "Bánh", 20000, "Bánh mì que giòn nhân pate, ăn nhẹ buổi sáng.", 0, False, False, False, True, False, ("ăn sáng",), ("gluten",)),
    item("p25", "Hạt điều rang muối", "Ăn vặt", 28000, "Hạt điều rang muối giòn, ăn kèm nước uống.", 0, False, False, True, True, True, ("ăn vặt",), ("hạt",)),
)

# --- Cà phê Nhà · Thủ Đức (menu hẹp hơn — cố ý khác Quận 1) -------------------------
CAPHENHA_TD: tuple[Item, ...] = (
    item("p01", "Cà phê đen", "Cà phê", 25000, "Cà phê phin đậm đà, không đường.", 0, True, False, True, True, True, ("đậm", "truyền thống")),
    item("p02", "Cà phê sữa", "Cà phê", 29000, "Cà phê phin pha sữa đặc.", 2, True, True, False, True, True, ("bán chạy",), ("sữa",)),
    item("p03", "Bạc xỉu", "Cà phê", 30000, "Nhiều sữa ít cà phê — giá chi nhánh Thủ Đức.", 2, True, True, False, True, True, ("nhẹ",), ("sữa",)),
    item("p07", "Cold brew", "Cà phê", 45000, "Ủ lạnh, hợp trời nóng Thủ Đức.", 0, True, False, True, False, True, ("mùa hè",)),
    item("p11", "Trà đào cam sả", "Trà", 40000, "Trà đào cam sả giải nhiệt.", 1, True, False, True, False, True, ("giải nhiệt", "mùa hè")),
    item("p17", "Sinh tố xoài", "Sinh tố", 42000, "Xoài chín xay đá — món riêng chi nhánh Thủ Đức.", 2, False, False, True, False, True, ("trái cây", "mùa hè")),
    item("p19", "Nước ép dưa hấu", "Nước ép", 35000, "Dưa hấu ép mát lạnh.", 0, False, False, True, False, True, ("giải nhiệt",)),
    item("p24", "Bánh mì que pate", "Bánh", 18000, "Bánh mì que giòn nhân pate.", 0, False, False, False, True, False, ("ăn sáng",), ("gluten",)),
    item("p25", "Hạt điều rang muối", "Ăn vặt", 25000, "Hạt điều rang muối.", 0, False, False, True, True, True, ("ăn vặt",), ("hạt",)),
)

# --- Trà Sữa Mây (brand khác — không được lẫn sang Cà phê Nhà) ----------------------
TRASUAMAY: tuple[Item, ...] = (
    item("t01", "Trà sữa truyền thống", "Trà sữa", 39000, "Trà đen pha sữa, ngọt vừa.", 2, True, True, False, False, True, ("bán chạy", "ngọt"), ("sữa",)),
    item("t02", "Trà sữa trân châu đường đen", "Trà sữa", 45000, "Trà sữa với trân châu đường đen dẻo.", 3, True, True, False, False, True, ("ngọt", "bán chạy"), ("sữa",)),
    item("t03", "Trà sữa matcha", "Trà sữa", 48000, "Matcha pha sữa, thơm trà.", 2, True, True, False, False, True, ("matcha",), ("sữa",)),
    item("t04", "Hồng trà macchiato", "Trà", 42000, "Hồng trà phủ kem muối nhẹ.", 2, True, True, False, False, True, ("béo",), ("sữa",)),
    item("t05", "Trà đào", "Trà", 40000, "Trà đào miếng, ít ngọt tuỳ chọn.", 1, True, False, True, False, True, ("giải nhiệt",)),
    item("t06", "Sữa tươi trân châu đường đen", "Trà sữa", 43000, "Sữa tươi và trân châu đường đen.", 3, False, True, False, False, True, ("ngọt",), ("sữa",)),
    item("t07", "Bánh flan", "Bánh", 20000, "Bánh flan mềm, ăn kèm trà sữa.", 2, False, True, False, False, True, ("ăn kèm", "ngọt"), ("trứng", "sữa")),
    item("t08", "Khoai môn viên", "Ăn vặt", 15000, "Khoai môn chiên giòn.", 1, False, False, True, True, True, ("ăn vặt",)),
)

SEED: dict[str, dict[str, Catalog]] = {
    "caphenha": {
        "q1": Catalog("caphenha", "q1", "Quận 1", CAPHENHA_Q1, _shop("Cà phê Nhà · Quận 1"), TOPPINGS_COFFEE),
        "td": Catalog("caphenha", "td", "Thủ Đức", CAPHENHA_TD, _shop("Cà phê Nhà · Thủ Đức"), TOPPINGS_COFFEE),
        "default": Catalog("caphenha", "default", "Cà phê Nhà", CAPHENHA_Q1, _shop("Cà phê Nhà"), TOPPINGS_COFFEE),
    },
    "trasuamay": {
        "default": Catalog("trasuamay", "default", "Trà Sữa Mây", TRASUAMAY, _shop("Trà Sữa Mây"), TOPPINGS_TEA),
    },
    "demo": {
        "default": Catalog("demo", "default", "AICOS Coffee (demo)", CAPHENHA_Q1, _shop("AICOS Coffee (demo)"), TOPPINGS_COFFEE),
    },
}


def seed_catalog(tenant_code: str | None, branch_key: str | None = None) -> Catalog:
    tenant = (tenant_code or "demo").strip().lower() or "demo"
    brands = SEED.get(tenant) or SEED["demo"]
    key = (branch_key or "default").strip().lower()
    if key in brands:
        return brands[key]
    # Heuristic when FE sends a real branch UUID / Vietnamese name.
    blob = f"{branch_key or ''}".lower()
    if tenant == "caphenha":
        if "thu" in blob or "đức" in blob or "duc" in blob or blob.endswith("td"):
            return brands.get("td", brands["default"])
        if "quan" in blob or "q1" in blob or "hue" in blob:
            return brands.get("q1", brands["default"])
    return brands.get("default") or next(iter(brands.values()))
