"""Hand-written intent data for the Smart Barista router. TRAIN is used for training; TEST is a separate hand-written set."""
from __future__ import annotations

import unicodedata


def norm(s: str) -> str:
    return unicodedata.normalize("NFC", s.lower().strip())


INTENTS = ["menu_info", "allergy_diet", "recommend", "order_status", "loyalty_promo", "shop_info", "human_handoff", "smalltalk"]

TRAIN: dict[str, list[str]] = {
    "menu_info": [
        "Latte giá bao nhiêu", "Quán có bán bạc xỉu không", "trong menu có những món gì", "cappuccino size L bao nhiêu tiền",
        "món này gồm những gì vậy", "có topping nào để thêm không", "ly cold brew bao nhiêu ml", "menu trà trái cây có gì",
        "cà phê muối làm từ gì", "có bánh ngọt kèm không", "size M với L khác nhau bao nhiêu", "matcha latte có đá không",
        "cho mình xem thực đơn", "americano hết hàng chưa", "món nào đang giảm giá", "trân châu thêm bao nhiêu tiền",
        "có nước ép không", "giá ly trà đào là bao nhiêu", "bạc xỉu với cà phê sữa khác gì nhau", "quán có món ăn sáng không",
    ],
    "allergy_diet": [
        "mình dị ứng sữa uống được món nào", "có món nào không đường không", "tôi ăn chay có gì uống được", "có sữa hạnh nhân không",
        "món nào ít calo", "đồ uống nào không chứa caffeine", "mình bị tiểu đường nên uống gì", "có sữa yến mạch không",
        "latte có lactose không", "mình dị ứng đậu phộng, có topping nào an toàn không", "bà bầu uống cà phê được không", "có món nào vegan không",
        "uống ít ngọt nhất là món nào", "không có gluten được không", "trẻ em uống được món nào", "có thể đổi sang sữa không lactose không",
        "mình kiêng đường", "món nào không cafein", "dị ứng hạt, bánh nào ăn được", "cho mình đồ uống tốt cho tim",
    ],
    "recommend": [
        "gợi ý cho mình món nào đó ít ngọt", "hôm nay trời nóng uống gì", "mình muốn thử món mới", "món nào bán chạy nhất",
        "đề xuất đồ uống hợp với bánh", "mình thích vị béo, nên gọi gì", "uống gì để tỉnh táo", "cho mình món giống lần trước",
        "trời mưa nên uống gì cho ấm", "món nào hot nhất tuần này", "chọn giúp mình một ly đi", "mình đang buồn ngủ uống gì",
        "nên gọi gì cho hai người", "có món nào mới không", "gợi ý nước mát cho mùa hè", "ly nào hợp với người không thích đắng",
        "mình theo đạo ăn chay, đề xuất đồ uống nhé", "mình dị ứng sữa, gợi ý món nào mát mát", "gợi ý món không đường cho mình", "mình ăn chay, nên gọi gì",
        "ăn bánh gì cho hợp với espresso", "ăn gì kèm với latte", "món nào hợp ăn cùng trà", "bạn gợi ý bánh nào đi cùng bạc xỉu",
        "đề xuất món không caffeine cho buổi tối", "mình kiêng đường nên chọn gì", "gợi ý đồ uống cho người dị ứng sữa", "nên ăn gì với cà phê đen",
        "tư vấn giúp mình món cà phê nhẹ", "recommend món nào ngon", "bạn thích món nào nhất", "mình hay uống latte, còn món nào tương tự",
    ],
    "order_status": [
        "đơn của mình đâu rồi", "đơn hàng bao lâu nữa có", "mình đặt 20 phút rồi chưa thấy", "kiểm tra giúp đơn ORD-123",
        "đơn mang đi của mình xong chưa", "mình muốn hủy đơn vừa đặt", "đơn đã thanh toán mà chưa thấy trạng thái", "sao đơn của mình chưa được pha",
        "cho mình xem trạng thái đơn", "đơn số 45 tới đâu rồi", "mình đổi giờ lấy được không", "tại sao đơn bị hủy",
        "đã trả tiền chưa vậy", "hóa đơn của mình ở đâu", "đơn mình gọi nhầm bàn", "xin hoàn tiền đơn vừa rồi",
    ],
    "loyalty_promo": [
        "mình có bao nhiêu điểm", "đổi điểm thành voucher như nào", "hiện có khuyến mãi gì", "mã giảm giá nhập ở đâu",
        "voucher của mình hết hạn chưa", "tích điểm thế nào", "có ưu đãi sinh nhật không", "hạng thành viên của mình là gì",
        "điểm bao lâu thì hết hạn", "áp dụng mã SAVE10 được không", "mua 2 tặng 1 còn không", "bao nhiêu điểm đổi được ly miễn phí",
        "chương trình thành viên gồm những gì", "voucher dùng được ở chi nhánh khác không", "mình quên nhập mã giảm giá", "thẻ quà tặng dùng thế nào",
    ],
    "shop_info": [
        "quán mở cửa lúc mấy giờ", "địa chỉ quán ở đâu", "có chỗ để xe không", "quán có wifi không",
        "mấy giờ đóng cửa", "chi nhánh gần nhất ở đâu", "quán có phòng riêng không", "có ổ cắm điện không",
        "số điện thoại của quán", "quán có chỗ cho nhóm 10 người không", "có nhận đặt bàn không", "quán có điều hòa không",
        "cuối tuần có mở không", "có cho mang thú cưng vào không", "có giao hàng không", "chi nhánh quận 1 mấy giờ mở",
    ],
    "human_handoff": [
        "cho mình gặp nhân viên", "mình muốn nói chuyện với quản lý", "kết nối người thật giúp mình", "mình muốn khiếu nại",
        "bot không hiểu, gọi người đi", "cho mình số hotline", "mình cần hỗ trợ trực tiếp", "tôi muốn phản ánh về nhân viên",
        "chuyển cho nhân viên hỗ trợ", "có người thật không", "mình bị tính sai tiền, cần người xử lý", "tôi muốn gặp chủ quán",
        "gọi quản lý ra đây", "tôi không muốn nói chuyện với bot", "nhờ nhân viên gọi lại cho mình",
    ],
    "smalltalk": [
        "xin chào", "hello", "cảm ơn bạn", "bạn là ai vậy", "chào buổi sáng", "tạm biệt nhé", "bạn khỏe không",
        "hi", "ok cảm ơn", "bạn tên gì", "hôm nay thế nào", "chúc quán buôn bán đắt hàng", "haha", "good morning", "thanks",
    ],
}

TEST: list[tuple[str, str]] = [
    ("Ly bạc xỉu size nhỏ giá nhiêu vậy bạn", "menu_info"), ("trong menu còn món gì ngoài cà phê không", "menu_info"),
    ("topping nào thêm được cho trà sữa", "menu_info"), ("cho mình hỏi thành phần của ly matcha", "menu_info"),
    ("mình uống sữa không được, có món nào thay thế", "allergy_diet"), ("món nào không có đường hoàn toàn", "allergy_diet"),
    ("đồ uống nào ít calo nhất", "allergy_diet"), ("bạn ơi mình ăn chay trường", "allergy_diet"),
    ("hôm nay nên gọi món gì nhỉ", "recommend"), ("gợi ý giúp mình món nào giải nhiệt", "recommend"),
    ("mình muốn một ly nhẹ nhàng không đắng", "recommend"), ("đồ uống nào hợp ăn bánh mì", "recommend"),
    ("đơn mình đặt khi nãy sao chưa có", "order_status"), ("mình muốn hủy đơn được không", "order_status"),
    ("check giúp đơn mang đi của mình", "order_status"), ("đơn của tôi bao lâu nữa xong", "order_status"),
    ("mình đang có mấy điểm thưởng", "loyalty_promo"), ("có mã giảm giá nào đang chạy không", "loyalty_promo"),
    ("đổi điểm lấy ly miễn phí được không", "loyalty_promo"), ("voucher này dùng thế nào", "loyalty_promo"),
    ("quán mấy giờ mở cửa vậy", "shop_info"), ("quán ở đâu vậy bạn", "shop_info"),
    ("có chỗ đậu ô tô không", "shop_info"), ("quán có nhận đặt chỗ cho nhóm không", "shop_info"),
    ("mình muốn gặp quản lý quán", "human_handoff"), ("nhân viên phục vụ rất tệ, mình muốn khiếu nại", "human_handoff"),
    ("cho mình nói chuyện với người thật", "human_handoff"), ("hỗ trợ trực tiếp giúp mình", "human_handoff"),
    ("chào bạn", "smalltalk"), ("cảm ơn nhiều nha", "smalltalk"), ("bạn là robot hả", "smalltalk"), ("bye bye", "smalltalk"),
]

