"""Sample menu of one demo brand. In production the AI service reads the same fields from the Menu service (per tenant)."""
from __future__ import annotations

from dataclasses import dataclass, field


@dataclass(frozen=True)
class Item:
    id: str
    name: str
    category: str
    price: int
    description: str
    sweetness: int            # 0 = none ... 3 = very sweet
    caffeine: bool
    dairy: bool
    vegan: bool
    hot: bool
    cold: bool
    tags: tuple[str, ...] = field(default_factory=tuple)
    allergens: tuple[str, ...] = field(default_factory=tuple)


def _i(*a, **k) -> Item:
    return Item(*a, **k)


MENU: list[Item] = [
    _i("p01", "Cà phê đen", "Cà phê", 25000, "Cà phê phin đậm đà, không đường, vị đắng rõ.", 0, True, False, True, True, True, ("đậm", "truyền thống")),
    _i("p02", "Cà phê sữa", "Cà phê", 29000, "Cà phê phin pha sữa đặc, béo ngọt, đậm vị Sài Gòn.", 2, True, True, False, True, True, ("bán chạy", "truyền thống"), ("sữa",)),
    _i("p03", "Bạc xỉu", "Cà phê", 32000, "Nhiều sữa ít cà phê, ngọt dịu, dễ uống cho người mới.", 2, True, True, False, True, True, ("nhẹ", "béo"), ("sữa",)),
    _i("p04", "Americano", "Cà phê", 35000, "Espresso pha loãng với nước, thanh và ít calo.", 0, True, False, True, True, True, ("ít calo", "tỉnh táo")),
    _i("p05", "Latte", "Cà phê", 45000, "Espresso với sữa tươi và lớp bọt mịn, béo nhẹ.", 1, True, True, False, True, True, ("béo", "bán chạy"), ("sữa",)),
    _i("p06", "Cappuccino", "Cà phê", 45000, "Espresso, sữa nóng và bọt dày, hơi đậm hơn latte.", 1, True, True, False, True, False, ("đậm", "bọt"), ("sữa",)),
    _i("p07", "Cold brew", "Cà phê", 49000, "Cà phê ủ lạnh 18 giờ, vị êm, ít chua, ít đắng.", 0, True, False, True, False, True, ("ít chua", "mùa hè")),
    _i("p08", "Cà phê muối", "Cà phê", 39000, "Cà phê sữa phủ lớp kem muối béo mặn, hot trend.", 2, True, True, False, False, True, ("hot", "béo"), ("sữa",)),
    _i("p09", "Espresso", "Cà phê", 30000, "Một shot espresso đậm đặc, dành cho người mê cà phê.", 0, True, False, True, True, False, ("đậm", "tỉnh táo")),
    _i("p10", "Matcha latte", "Trà", 48000, "Matcha Nhật pha sữa tươi, thơm trà, béo nhẹ.", 1, True, True, False, True, True, ("béo", "matcha"), ("sữa",)),
    _i("p11", "Trà đào cam sả", "Trà", 42000, "Trà đen, đào miếng, cam và sả, thơm mát giải nhiệt.", 1, True, False, True, False, True, ("giải nhiệt", "bán chạy", "mùa hè")),
    _i("p12", "Trà vải", "Trà", 40000, "Trà lài với vải tươi, vị thanh ngọt dịu.", 1, True, False, True, False, True, ("thanh", "giải nhiệt")),
    _i("p13", "Trà sữa trân châu", "Trà", 42000, "Trà sữa béo với trân châu đường đen dẻo.", 3, True, True, False, False, True, ("ngọt", "bán chạy"), ("sữa",)),
    _i("p14", "Trà gừng mật ong", "Trà", 38000, "Trà gừng ấm bụng, mật ong tự nhiên, không caffeine.", 2, False, False, True, True, False, ("ấm", "không caffeine", "ngày mưa")),
    _i("p15", "Trà hoa cúc", "Trà", 35000, "Hoa cúc khô pha mật ong, dịu nhẹ, không caffeine, dễ ngủ.", 1, False, False, True, True, True, ("không caffeine", "nhẹ")),
    _i("p16", "Sinh tố bơ", "Sinh tố", 49000, "Bơ sáp xay sữa đặc, béo mịn, bổ dưỡng.", 2, False, True, False, False, True, ("béo", "bổ dưỡng"), ("sữa",)),
    _i("p17", "Sinh tố xoài", "Sinh tố", 45000, "Xoài chín xay với đá, ngọt thanh, không sữa.", 2, False, False, True, False, True, ("trái cây", "mùa hè")),
    _i("p18", "Nước ép cam", "Nước ép", 40000, "Cam vắt nguyên chất, không đường, nhiều vitamin C.", 0, False, False, True, False, True, ("không đường", "vitamin", "giải nhiệt")),
    _i("p19", "Nước ép dưa hấu", "Nước ép", 38000, "Dưa hấu ép mát lạnh, ít calo, giải khát tức thì.", 0, False, False, True, False, True, ("ít calo", "giải nhiệt", "mùa hè")),
    _i("p20", "Soda chanh", "Nước giải khát", 32000, "Soda chanh tươi, sảng khoái, không caffeine.", 1, False, False, True, False, True, ("sảng khoái", "không caffeine")),
    _i("p21", "Socola nóng", "Khác", 45000, "Socola đậm pha sữa nóng, ngọt ấm, hợp ngày mưa.", 3, False, True, False, True, False, ("ngọt", "ấm", "ngày mưa"), ("sữa",)),
    _i("p22", "Croissant bơ", "Bánh", 35000, "Bánh sừng bò bơ giòn xốp, hợp ăn với cà phê.", 1, False, True, False, True, False, ("ăn kèm",), ("gluten", "sữa", "trứng")),
    _i("p23", "Bánh tiramisu", "Bánh", 45000, "Tiramisu vị cà phê, mềm mịn.", 2, True, True, False, False, True, ("ngọt", "ăn kèm"), ("gluten", "sữa", "trứng")),
    _i("p24", "Bánh mì que pate", "Bánh", 20000, "Bánh mì que giòn nhân pate, ăn nhẹ buổi sáng.", 0, False, False, False, True, False, ("ăn sáng",), ("gluten",)),
    _i("p25", "Hạt điều rang muối", "Ăn vặt", 28000, "Hạt điều rang muối giòn, ăn kèm nước uống.", 0, False, False, True, True, True, ("ăn vặt",), ("hạt",)),
]

TOPPINGS = [("Trân châu đường đen", 8000), ("Kem cheese", 12000), ("Thạch dừa", 7000), ("Shot espresso thêm", 10000)]
SHOP = {
    "name": "AICOS Coffee (demo)", "hours": "07:00 - 22:00 hằng ngày", "wifi": "Có wifi miễn phí, mật khẩu in trên bill",
    "parking": "Có chỗ để xe máy miễn phí; ô tô gửi ở bãi đối diện", "delivery": "Có nhận đơn mang đi qua QR hoặc app; chưa giao tận nơi",
    "booking": "Nhóm trên 8 người vui lòng báo trước với nhân viên",
    "loyalty": "Mỗi 10.000đ được 1 điểm; đủ điểm đổi voucher tại mục Điểm thưởng; hạng Bạc từ 100 điểm, Vàng từ 500 điểm",
}
BY_ID = {i.id: i for i in MENU}
