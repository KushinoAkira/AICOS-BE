"""Smart Barista pipeline (UC-52). Always grounded on ONE Catalog (tenant + branch).

  mask PII -> intent model -> (handoff | smalltalk | shop facts: answered WITHOUT an LLM)
           -> constraint filter on structured menu data (allergy / diet are decided by data, never by the LLM)
           -> retrieval + personalised ranking -> LLM phrases the answer -> guardrail check
           -> template answer when no LLM is reachable or the guardrail rejects the text.
"""
from __future__ import annotations

import re
import time
from pathlib import Path
from typing import Any

import joblib

from common.catalogs import Catalog
from common.item import Item
from common.menu import MENU
from common.pairings import partners_for
from common.policy_kb import INJECTION_REFUSE, RECIPE_REFUSE, SYSTEM_POLICY
from rag.llm import LlmChain
from rag.retriever import Retriever
from reco.predict import Recommender

INTENT_MODEL = Path(__file__).resolve().parent.parent / "models/intent/intent.joblib"
PII = [(re.compile(r"\b0\d{9}\b"), "[sđt]"), (re.compile(r"[\w.+-]+@[\w-]+\.[\w.]+"), "[email]")]
SYSTEM = SYSTEM_POLICY

_RECIPE_ASK = re.compile(
    r"công thức|cong thuc|định lượng|dinh luong|tỷ lệ pha|ty le pha|"
    r"bí quyết|bi quyet|sop\b|cách pha (chính xác|chi tiết)|cach pha (chinh xac|chi tiet)|"
    r"gram|ml\s*(espresso|sữa|sua)|recipe|secret formula",
    re.I,
)
_INJECTION_ASK = re.compile(
    r"ignore (all |previous |prior )?(instructions|rules|prompts)|"
    r"bỏ qua (mọi |tat ca |tất cả )?(quy tắc|quy tac|hướng dẫn|huong dan|system prompt)|"
    r"(in|hiện|hien|reveal|show|dump)\s+(ra\s+)?(system\s*)?prompt|"
    r"đóng vai (chủ quán|chu quan|admin|developer)|dong vai (chu quan|admin)|"
    r"jailbreak|DAN mode|you are now unrestricted",
    re.I,
)
_BUDGET_RE = re.compile(
    r"(?:có|co|khoảng|khoang|tầm|tam|dưới|duoi|dưới\s+tầm|max|tối đa|toi da|ngân sách|ngan sach)"
    r"\s*(?:khoảng|khoang|tầm|tam)?\s*"
    r"(\d{1,3}(?:[.\s]\d{3})+|\d+)\s*(k|nghìn|nghin)?|"
    r"(\d{1,3}(?:[.\s]\d{3})+|\d+)\s*(k|nghìn|nghin)?\s*(?:đ|d|vnd|đồng|dong)?\s*"
    r"(?:thôi|thoi|đổ lại|do lai|là vừa|la vua|gọi được|goi duoc)",
    re.I,
)


def asks_recipe(q: str) -> bool:
    return bool(_RECIPE_ASK.search(q))


def asks_injection(q: str) -> bool:
    return bool(_INJECTION_ASK.search(q))


def parse_budget(q: str) -> int | None:
    """Parse a guest budget in VND (e.g. 60.000, 60000, 60k)."""
    m = _BUDGET_RE.search(q.lower())
    if not m:
        return None
    raw = (m.group(1) or m.group(3) or "").replace(".", "").replace(" ", "").replace(",", "")
    if not raw.isdigit():
        return None
    n = int(raw)
    unit = (m.group(2) or m.group(4) or "").lower()
    if unit in ("k", "nghìn", "nghin") or (n < 1000 and "k" in q.lower()):
        n *= 1000
    if n < 5_000 or n > 5_000_000:
        return None
    return n

# Accent-flexible letter classes so "pâté" can be rewritten to menu "pate".
_FLEX_LETTER = {
    "a": "aáàảãạăắằẳẵặâấầẩẫậ",
    "e": "eéèẻẽẹêếềểễệ",
    "i": "iíìỉĩị",
    "o": "oóòỏõọôốồổỗộơớờởỡợ",
    "u": "uúùủũụưứừửữự",
    "y": "yýỳỷỹỵ",
    "d": "dđ",
}


def _flex_name_re(name: str) -> re.Pattern[str]:
    parts: list[str] = []
    for ch in name:
        low = ch.lower()
        if low in _FLEX_LETTER:
            chars = _FLEX_LETTER[low]
            parts.append(f"[{re.escape(chars)}{re.escape(chars.upper())}]")
        elif ch.isspace():
            parts.append(r"\s+")
        else:
            parts.append(re.escape(ch))
    return re.compile("".join(parts), re.IGNORECASE)


def find_mentioned(text: str, items: list[Item]) -> list[Item]:
    """Menu items named in the guest question. Prefer longest names so 'Latte' does not steal 'Matcha latte'."""
    low = text.lower()
    hits = sorted(
        [it for it in items if it.name.lower() in low],
        key=lambda it: len(it.name),
        reverse=True,
    )
    if not hits:
        hits = [
            it
            for it in items
            if any(re.search(rf"(?<!\w){re.escape(tok)}(?!\w)", low) for tok in it.name.lower().split() if len(tok) > 4)
        ]
        hits = sorted(hits, key=lambda it: len(it.name), reverse=True)
    # Drop shorter names that are only substrings of a longer hit (Latte ⊂ Matcha latte).
    kept: list[Item] = []
    for it in hits:
        n = it.name.lower()
        if any(n != o.name.lower() and n in o.name.lower() for o in hits):
            continue
        kept.append(it)
    return kept


def item_focus_ask(text: str) -> bool:
    """Guest is asking about a specific dish, not browsing generically."""
    return bool(
        re.search(
            r"giới thiệu|gioi thieu|cho biết|cho biet|là gì|la gi|thông tin|thong tin|"
            r"mô tả|mo ta|về món|ve mon|về ly|giá|gia |bao nhiêu|thành phần|thanh phan|"
            r"có bán|có món|có ly",
            text.lower(),
        )
    )


def polish_reply(text: str, catalog_items: list[Item] | None = None) -> str:
    """Strip markdown the UI cannot render; force exact menu spellings (pate not pâté)."""
    out = re.sub(r"\*\*([^*]+)\*\*", r"\1", text)
    out = re.sub(r"__([^_]+)__", r"\1", out)
    out = out.replace("**", "").replace("__", "").replace("`", "")
    out = re.sub(r"^#+\s*", "", out, flags=re.M)
    out = re.sub(r"^[-*]\s+", "", out, flags=re.M)
    if catalog_items:
        for it in sorted(catalog_items, key=lambda i: len(i.name), reverse=True):
            out = _flex_name_re(it.name).sub(it.name, out)
    return out.strip()

_COFFEE_RE = re.compile(
    r"cà phê|ca phe|latte|cappuccino|espresso|americano|bạc xỉu|bac xiu|cold brew|mocha|cà phê muối",
    re.I,
)
_SOFT_COFFEE_RE = re.compile(
    r"latte|bạc xỉu|bac xiu|cà phê sữa|ca phe sua|cappuccino|cold brew|cà phê muối|matcha latte|"
    r"ít đắng|vị êm|nhiều sữa|dễ uống|béo nhẹ|nhẹ\b",
    re.I,
)
_BITTER_COFFEE_RE = re.compile(r"cà phê đen|ca phe den|\bđen\b|espresso|americano|đắng rõ|đậm đà", re.I)
_FOOD_CAT_RE = re.compile(r"bánh|banh|ăn vặt|an vat", re.I)


def mask(text: str) -> str:
    for rx, rep in PII:
        text = rx.sub(rep, text)
    return text


def _blob(it: Item) -> str:
    return f"{it.name} {it.category} {it.description} {' '.join(it.tags)}".lower()


def is_coffee_drink(it: Item) -> bool:
    return bool(_COFFEE_RE.search(_blob(it)))


def is_soft_coffee(it: Item) -> bool:
    """Milk-forward / mellow cups — suitable when the guest wants coffee that is not bitter."""
    if not is_coffee_drink(it):
        return False
    if any(t in it.tags for t in ("nhẹ", "ít đắng", "êm", "béo")):
        return True
    if it.dairy:
        return True
    return bool(_SOFT_COFFEE_RE.search(_blob(it))) and not (
        _BITTER_COFFEE_RE.search(it.name) and not it.dairy
    )


def is_bold_coffee(it: Item) -> bool:
    if not is_coffee_drink(it):
        return False
    if any(t in it.tags for t in ("đậm",)):
        return True
    return bool(_BITTER_COFFEE_RE.search(_blob(it))) and not it.dairy


_DRINK_NAME_RE = re.compile(
    r"cà phê|ca phe|latte|cappuccino|espresso|americano|cold brew|trà|tra |soda|nước|nuoc|sinh tố|sinh to|matcha",
    re.I,
)
_FOOD_NAME_RE = re.compile(
    r"bánh|banh|croissant|tiramisu|pate|hạt điều|hat dieu|flan|cookie|sừng bò|sung bo|ăn vặt|an vat",
    re.I,
)


def is_food_side(it: Item) -> bool:
    """Pastry / snack / bánh — what F&B means by 'ăn kèm', never another drink."""
    if _FOOD_CAT_RE.search(it.category) or any(t in it.tags for t in ("ăn kèm", "ăn vặt", "ăn sáng", "bữa sáng")):
        return True
    if _FOOD_NAME_RE.search(it.name):
        return True
    if is_coffee_drink(it) or _DRINK_NAME_RE.search(f"{it.name} {it.category}"):
        return False
    return False


def side_reason(it: Item) -> str:
    """F&B pairing copy grounded on café practice (VN + classic pastry rules)."""
    n = f"{it.name} {it.description}".lower()
    if re.search(r"tiramisu|socola|chocolate", n):
        return "Bánh ngọt đậm — hợp cà phê đen / espresso"
    if re.search(r"croissant|sừng bò|sung bo|bơ", n):
        return "Bánh bơ nhẹ — hợp latte / cappuccino"
    if re.search(r"mì que|mi que|pate|bánh mì|banh mi", n):
        return "Mặn nhẹ kiểu Việt — hợp cà phê sữa / bạc xỉu"
    if re.search(r"hạt|hat |điều|dieu", n):
        return "Snack giòn — hợp trà trái cây / cold brew"
    return "Ăn kèm có trên menu chi nhánh này"


def constraints(q: str) -> dict[str, Any]:
    t = q.lower()
    c: dict[str, Any] = {}
    if re.search(r"dị ứng (với )?sữa|(không|chẳng|ko|kiêng|tránh|khỏi)\s+(có\s+|chứa\s+|dùng\s+|uống\s+|muốn\s+|được\s+)*(sữa|lactose)|lactose|uống sữa không được|không sữa", t):
        c["dairy"] = False
    avoid = [k for k, pat in (("gluten", r"gluten|bột mì"), ("trứng", r"trứng"), ("sữa", r"sữa")) if re.search(rf"dị ứng (với )?({pat})", t)]
    if avoid:
        c["avoid"] = avoid
    if re.search(r"ăn chay|vegan|thuần chay", t):
        c["vegan"] = True
    child = re.search(r"\b([0-9]|1[0-2])\s*tuổi|trẻ em|trẻ nhỏ|em bé|con (mình|tôi)|cháu", t)
    # Pregnancy phrasing varies: mẹ bầu, bà bầu, mang thai, mang tha (typo), đang bầu…
    pregnant = bool(
        re.search(
            r"bà bầu|mẹ bầu|đang bầu|bầu bí|mang thai|mang tha\b|phụ nữ mang|cho bầu|dành cho bầu",
            t,
        )
    )
    if (
        re.search(
            r"(không|ko|kiêng|tránh|hạn chế)\s+(có\s+|chứa\s+|dùng\s+|uống\s+|muốn\s+)*(caffeine|cafein|cà phê|cafe)|mất ngủ|khó ngủ",
            t,
        )
        or pregnant
        or child
    ):
        c["caffeine"] = False
        if pregnant or child:
            c["prefer_gentle"] = True
    if re.search(r"không đường|kiêng đường|tiểu đường|ít ngọt|không ngọt", t):
        c["max_sweet"] = 0 if re.search(r"không đường|kiêng đường|tiểu đường", t) else 1
    if re.search(r"ít calo|giảm cân", t):
        c["tag"] = "ít calo"
    if re.search(r"nóng bức|trời nóng|giải nhiệt|mát|mùa hè|đồ uống mát|uống mát|uống lạnh", t) and not re.search(r"ấm|mưa", t):
        c["cold"] = True
        c["drink_only"] = True
    elif re.search(r"lạnh|đá", t) and not re.search(r"ấm|mưa|lạnh người", t):
        c["cold"] = True
    if re.search(r"mưa|ấm|lạnh người|trời lạnh", t):
        c["hot"] = True
    if re.search(r"tỉnh táo|buồn ngủ|mệt", t):
        c["caffeine"] = True
    if re.search(r"(thức uống|đồ uống|món|đồ|uống)\s+nóng(?!\s*bức)", t) and not re.search(r"nóng bức|trời nóng", t):
        c["hot"] = True
        c.pop("cold", None)
    if re.search(r"ăn kèm|an kem|ăn sáng|combo|kết hợp|đi với|gọi kèm|đồ ăn kèm|có gì ăn|có bánh|banh gi", t):
        c["want_pairing"] = True
    # Bare "ăn kèm / bánh / snack" → ONLY food sides from this branch (never another drink).
    if re.search(
        r"ăn kèm|an kem|có gì ăn|đồ ăn kèm|bánh mặn|bánh ngọt|ăn vặt|an vat|snack|pastry|"
        r"có bánh|bánh gì|banh gi|bánh mì|banh mi|croissant|tiramisu",
        t,
    ) and not re.search(r"uống kèm|đồ uống kèm", t):
        c["side_only"] = True
    # Taste / family — deterministic filters so retrieval cannot drift to bánh / trà when guest asked for coffee.
    if re.search(r"cà phê|ca phe|cafe|coffee|latte|cappuccino|espresso|americano|bạc xỉu|bac xiu|cold brew", t):
        c["family"] = "coffee"
    if re.search(r"không đắng|ít đắng|không chát|không gắt|êm dịu|dễ uống|nhẹ miệng", t):
        c["soft_taste"] = True
    elif re.search(r"đắng|đậm|mạnh|đen nguyên chất", t) and not re.search(r"không|ít", t):
        c["bold_taste"] = True
    budget = parse_budget(t)
    if budget is not None:
        c["max_price"] = budget
    # Asking for sides alone must not also lock family=coffee from incidental words.
    if c.get("side_only") and not re.search(r"với|kèm theo|đi với|ăn kèm với", t):
        c.pop("family", None)
        c.pop("soft_taste", None)
        c.pop("bold_taste", None)
    return c


def allowed(it: Item, c: dict[str, Any]) -> bool:
    if (
        ("dairy" in c and it.dairy)
        or any(a in it.allergens for a in c.get("avoid", []))
        or (c.get("vegan") and not it.vegan)
        or ("caffeine" in c and it.caffeine != c["caffeine"])
        or ("max_sweet" in c and it.sweetness > c["max_sweet"])
        or (c.get("tag") and c["tag"] not in it.tags)
        or (c.get("cold") and not it.cold)
        or (c.get("hot") and not it.hot)
        or (c.get("max_price") is not None and it.price > c["max_price"])
    ):
        return False
    if c.get("side_only") and not is_food_side(it):
        return False
    if c.get("drink_only") and is_food_side(it):
        return False
    if c.get("family") == "coffee" and not c.get("want_pairing") and not c.get("side_only"):
        if not is_coffee_drink(it):
            return False
        if _FOOD_CAT_RE.search(it.category):
            return False
    if c.get("soft_taste") and c.get("family") == "coffee" and not c.get("want_pairing"):
        if not is_soft_coffee(it):
            return False
    if c.get("bold_taste") and c.get("family") == "coffee" and not c.get("want_pairing"):
        if not is_bold_coffee(it):
            return False
    return True


def suggestion_reason(it: Item, c: dict[str, Any]) -> str:
    if c.get("side_only") or (c.get("want_pairing") and is_food_side(it)):
        return side_reason(it)
    if c.get("prefer_gentle") and not it.caffeine:
        return "Không caffeine — phù hợp mẹ bầu / trẻ nhỏ"
    if c.get("soft_taste") and is_soft_coffee(it):
        return "Êm hơn, ít đắng — hợp yêu cầu của bạn"
    if c.get("bold_taste") and is_bold_coffee(it):
        return "Đậm vị — hợp người thích cà phê mạnh"
    if c.get("max_sweet") is not None:
        return "Ít ngọt hơn các lựa chọn khác"
    if c.get("cold"):
        return "Mát, giải nhiệt"
    if c.get("want_pairing"):
        return "Ăn kèm hợp vị"
    return "Có trên menu chi nhánh này"


class Barista:
    def __init__(self, retriever: Retriever | None = None, llm: LlmChain | None = None, reco: Recommender | None = None):
        self.intent = joblib.load(INTENT_MODEL)
        self.default_retriever = retriever or Retriever(use_embeddings=False)
        self.reco = reco or Recommender()
        self.llm = llm or LlmChain()

    def classify(self, text: str) -> tuple[str, float]:
        proba = self.intent.predict_proba([text])[0]
        k = int(proba.argmax())
        return str(self.intent.classes_[k]), float(proba[k])

    async def reply(
        self,
        message: str,
        history: list[str] | None = None,
        catalog: Catalog | None = None,
    ) -> dict[str, Any]:
        t0 = time.perf_counter()
        text = mask(message.strip())
        intent, conf = self.classify(text)
        items = list(catalog.items) if catalog else MENU
        by_id = catalog.by_id if catalog else {i.id: i for i in MENU}
        shop = catalog.shop if catalog else {"loyalty": "Mỗi 10.000đ được 1 điểm", "name": "quán"}
        toppings = list(catalog.toppings) if catalog else []
        retriever = Retriever(catalog, use_embeddings=False) if catalog else self.default_retriever
        route = {
            "intent": intent,
            "confidence": round(conf, 3),
            "llm": None,
            "retrieval": retriever.mode,
            "tenant": catalog.tenant_code if catalog else None,
            "branchId": catalog.branch_id if catalog else None,
            "menuSize": len(items),
        }

        def done(reply: str, sug: list[dict] | None = None, **extra: Any) -> dict[str, Any]:
            route["latency_ms"] = int((time.perf_counter() - t0) * 1000)
            return {"reply": reply, "suggestions": sug or [], "route": route, **extra}

        low = text.lower()
        # Policy guards (Phần VIII §16/25/30) — refuse before retrieval/LLM.
        if asks_injection(text):
            return done(INJECTION_REFUSE, handoff=False)
        if asks_recipe(text):
            return done(RECIPE_REFUSE, handoff=False)
        taste_ask = bool(
            re.search(
                r"gợi ý|goi y|ăn kèm|an kem|ít ngọt|it ngot|combo|kết hợp|"
                r"không đắng|ít đắng|không chát|dễ uống|mát|đậm|khẩu vị|nên (uống|gọi)|"
                r"ngân sách|ngan sach|có\s+\d|co\s+\d",
                low,
            )
        )
        diet_ask = bool(
            re.search(
                r"dị ứng|không sữa|lactose|vegan|ăn chay|thuần chay|"
                r"không caffeine|kiêng caffeine|mất ngủ|khó ngủ|"
                r"bà bầu|mẹ bầu|đang bầu|bầu bí|mang thai|mang tha\b|phụ nữ mang|cho bầu|dành cho bầu|"
                r"trẻ em|trẻ nhỏ|em bé|\d+\s*tuổi|ít calo|giảm cân|không đường|tiểu đường",
                low,
            )
        )
        if conf < 0.28:
            if taste_ask or diet_ask:
                intent, conf = ("allergy_diet" if diet_ask and not taste_ask else "recommend"), max(conf, 0.45)
                route["intent"], route["confidence"] = intent, round(conf, 3)
            else:
                return done("Mình chưa hiểu rõ ý bạn lắm. Bạn có thể hỏi về món, giá, gợi ý đồ uống, hoặc nhờ mình gọi nhân viên hỗ trợ nhé.", handoff=False)
        # Taste / diet questions must go through recommend even if the intent model said menu_info.
        if (taste_ask or diet_ask) and intent not in ("human_handoff", "shop_info", "order_status", "loyalty_promo"):
            intent, conf = ("allergy_diet" if diet_ask and not taste_ask else "recommend"), max(conf, 0.5)
            route["intent"], route["confidence"] = intent, round(conf, 3)
        if intent == "human_handoff":
            return done("Mình sẽ chuyển bạn cho nhân viên của quán ngay. Bạn vui lòng chờ trong giây lát nhé.", handoff=True)
        if intent == "smalltalk":
            where = shop.get("name") or "quán"
            return done(f"Chào bạn! Mình là Barista thông minh của {where}. Bạn muốn xem món, tìm đồ uống hợp khẩu vị hay hỏi về ưu đãi?", handoff=False)
        if intent == "shop_info":
            hits = retriever.search(text, 1, kinds=("shop",))
            if hits:
                return done(hits[0][0].text.split(": ", 1)[-1].rstrip(".") + ".", handoff=False)
            return done(f"{shop.get('name', 'Quán')} phục vụ theo giờ ghi trên bảng hiệu. Bạn hỏi nhân viên nếu cần thêm nhé.", handoff=False)
        if intent == "order_status":
            return done("Bạn mở mục Đơn hàng của tôi để xem trạng thái đơn theo thời gian thực nhé. Nếu cần hủy hoặc có sự cố, mình có thể nhờ nhân viên hỗ trợ.", handoff=False, action="open_orders")
        if intent == "loyalty_promo":
            return done(shop.get("loyalty", "Chương trình tích điểm đang áp dụng tại quán.") + ". Bạn xem số điểm và voucher của mình ở mục Điểm thưởng nhé.", handoff=False, action="open_loyalty")

        c = constraints(text)
        if re.search(r"dị ứng", low) and not ({"dairy", "avoid", "vegan"} & c.keys()):
            return done("Mình chưa có đủ thông tin thành phần để xác nhận món an toàn với dị ứng của bạn. Bạn vui lòng nói rõ thành phần cần tránh (sữa, gluten, trứng...) hoặc để nhân viên kiểm tra giúp nhé.", handoff=True)

        pool = [it for it in items if allowed(it, c)]
        # Soft/bold taste: prefer structured matches over BM25 noise (e.g. "không" matching "không caffeine").
        if c.get("soft_taste") and c.get("family") == "coffee":
            soft = [it for it in pool if is_soft_coffee(it)]
            if soft:
                pool = soft
        elif c.get("bold_taste") and c.get("family") == "coffee":
            bold = [it for it in pool if is_bold_coffee(it)]
            if bold:
                pool = bold
        hits = retriever.search(text, 8, kinds=("item", "pairing", "policy"))
        ranked_ids = [d.id for d, _ in hits if d.item and allowed(d.item, c)]
        if c.get("soft_taste") or c.get("bold_taste") or c.get("family") or c.get("side_only"):
            # Put constraint-matching pool first so ranking cannot promote bánh / trà (or drinks for ăn kèm).
            ranked_ids = [it.id for it in pool] + [pid for pid in ranked_ids if pid not in {i.id for i in pool}]

        raw_mentioned = find_mentioned(text, items)
        mentioned = [it for it in raw_mentioned if allowed(it, c)] if c else list(raw_mentioned)
        # Named dishes on this branch still win for "giới thiệu X" even if a soft filter would drop them.
        if not mentioned:
            mentioned = list(raw_mentioned)
        if mentioned:
            ranked_ids = [it.id for it in mentioned] + [pid for pid in ranked_ids if pid not in {it.id for it in mentioned}]
        pair_bases = [by_id[h] for h in (history or []) if h in by_id] + mentioned[:2]
        food_pool = [it for it in pool if is_food_side(it)] if c.get("side_only") or c.get("want_pairing") else []
        focus = bool(mentioned) and (item_focus_ask(low) or intent == "menu_info")

        suggestions: list[dict]
        if focus and not c.get("side_only") and not (c.get("want_pairing") and re.search(r"ăn kèm|combo|kết hợp|đi với|gọi kèm", low)):
            # "Giới thiệu Matcha latte" → that dish first, not a random reco list.
            suggestions = [
                {
                    "productId": it.id,
                    "name": it.name,
                    "price": it.price,
                    "reason": "Đúng món bạn hỏi — có trên menu chi nhánh này",
                }
                for it in mentioned[:3]
            ]
        elif c.get("want_pairing") or c.get("side_only") or (pair_bases and re.search(r"ăn kèm|combo|kết hợp|đi với|gọi kèm", low)):
            suggestions = []
            partner_catalog = food_pool or [it for it in items if is_food_side(it) and allowed(it, c)]
            for base in pair_bases:
                # When guest asks for ăn kèm, partners must be food — never another drink.
                for partner, why in partners_for(base, partner_catalog or pool or items, limit=3):
                    if partner.id in {s["productId"] for s in suggestions} or partner.id == base.id:
                        continue
                    if c.get("side_only") and not is_food_side(partner):
                        continue
                    suggestions.append({"productId": partner.id, "name": partner.name, "price": partner.price, "reason": why})
                if len(suggestions) >= 3:
                    break
            # No drink named / no history: list bánh & snack currently sold at THIS branch.
            if len(suggestions) < 3:
                have = {s["productId"] for s in suggestions}
                for it in (partner_catalog or food_pool):
                    if it.id in have:
                        continue
                    suggestions.append(
                        {"productId": it.id, "name": it.name, "price": it.price, "reason": side_reason(it)}
                    )
                    if len(suggestions) >= 3:
                        break
            # Never fall back to unrestricted drink reco for "ăn kèm".
        elif intent in ("recommend", "allergy_diet") or c:
            personal = self.reco.recommend(history or [], 8, allow={i.id for i in pool}, catalog=catalog)
            order = {pid: n for n, pid in enumerate(ranked_ids)}
            personal.sort(key=lambda s: (order.get(s["productId"], 99) * 0.5) - s["score"])
            suggestions = personal[:3]
            # Fill from pool if reco returned little (cold start / empty history).
            if len(suggestions) < 3:
                have = {s["productId"] for s in suggestions}
                for it in pool:
                    if it.id in have:
                        continue
                    suggestions.append(
                        {"productId": it.id, "name": it.name, "price": it.price, "reason": suggestion_reason(it, c), "score": 0.5}
                    )
                    if len(suggestions) >= 3:
                        break
        else:
            suggestions = [
                {"productId": pid, "name": by_id[pid].name, "price": by_id[pid].price, "reason": suggestion_reason(by_id[pid], c)}
                for pid in ranked_ids[:3]
                if pid in by_id
            ]

        # Hard isolation: drop anything not in this catalog / not allowed by constraints.
        # Taste / side questions get an explicit reason (don't keep generic reco copy).
        force_reason = bool(
            c.get("soft_taste")
            or c.get("bold_taste")
            or c.get("side_only")
            or c.get("prefer_gentle")
            or c.get("max_sweet") is not None
            or c.get("max_price") is not None
            or c.get("cold")
            or c.get("family")
        )
        suggestions = [
            {
                **s,
                "reason": (
                    suggestion_reason(by_id[s["productId"]], c)
                    if force_reason
                    else (s.get("reason") or suggestion_reason(by_id[s["productId"]], c))
                ),
            }
            for s in suggestions
            if s["productId"] in by_id and allowed(by_id[s["productId"]], c)
        ][:3]
        if not suggestions:
            if c.get("side_only"):
                return done(
                    "Chi nhánh này chưa có món bánh / ăn vặt trên menu để gợi ý ăn kèm. Bạn hỏi nhân viên xem hôm nay còn món nào khác nhé.",
                    handoff=False,
                )
            return done("Mình chưa thấy món nào trong menu chi nhánh này đáp ứng đủ yêu cầu. Bạn có thể nói rõ hơn hoặc nhờ nhân viên tư vấn thêm nhé.", handoff=False)

        context = "\n".join(
            f"- {by_id[s['productId']].name}: {by_id[s['productId']].price}đ. {by_id[s['productId']].description}"
            for s in suggestions
        )
        if intent == "menu_info" and toppings:
            context += "\nTopping: " + ", ".join(f"{n} +{p}đ" for n, p in toppings)
        if catalog:
            context += f"\n(Chỉ menu {catalog.branch_name} · brand {catalog.tenant_code})"
        if c.get("soft_taste"):
            context += "\n(Khách muốn vị êm / ít đắng — giới thiệu món sữa hoặc cold brew trong danh sách trên.)"
        if c.get("side_only"):
            context += "\n(Khách hỏi ĂN KÈM — chỉ nói về bánh/snack trong danh sách; không gợi ý thêm đồ uống.)"
        if c.get("max_price") is not None:
            context += f"\n(Khách có ngân sách tối đa {c['max_price']:,}đ — chỉ gợi ý món trong tầm giá.)".replace(",", ".")
        if focus and mentioned:
            context += f"\n(Khách hỏi cụ thể về: {mentioned[0].name} — giới thiệu đúng món này theo mô tả menu.)"
        template = self.template(intent, suggestions, c, focus_item=mentioned[0] if focus and mentioned else None)
        result = await self.llm.complete(
            [{"role": "system", "content": SYSTEM}, {"role": "user", "content": f"Câu hỏi: {text}\nDỮ LIỆU:\n{context}"}],
            max_tokens=220,
            easy=intent == "menu_info",
        )
        if result and self.grounded(result.text, context, catalog_items=items, has_suggestions=True):
            route["llm"] = result.provider
            return done(polish_reply(result.text, items), suggestions, handoff=False)
        route["llm"] = "template" if not result else f"rejected:{result.provider}"
        return done(template, suggestions, handoff=False)

    @staticmethod
    def template(
        intent: str,
        sug: list[dict],
        c: dict[str, Any] | None = None,
        focus_item: Item | None = None,
    ) -> str:
        c = c or {}
        if focus_item:
            price = f"{focus_item.price:,}đ".replace(",", ".")
            desc = (focus_item.description or "").strip().rstrip(".")
            return f"{focus_item.name} ({price}): {desc}." if desc else f"{focus_item.name} đang bán với giá {price} tại chi nhánh này."
        names = ", ".join(f"{s['name']} ({s['price']:,}đ)".replace(",", ".") for s in sug)
        if c.get("side_only"):
            return f"Theo menu chi nhánh này, món ăn kèm đang có: {names}."
        if c.get("max_price") is not None:
            budget = f"{c['max_price']:,}đ".replace(",", ".")
            return f"Trong tầm khoảng {budget}, menu chi nhánh này có: {names}."
        if c.get("soft_taste") and c.get("family") == "coffee":
            return f"Theo menu chi nhánh này, những ly cà phê êm / ít đắng hơn là: {names}."
        if c.get("bold_taste") and c.get("family") == "coffee":
            return f"Nếu bạn thích vị đậm, menu chi nhánh đang có: {names}."
        lead = {
            "recommend": "Mình gợi ý bạn thử",
            "allergy_diet": "Các món phù hợp với yêu cầu của bạn là",
            "menu_info": "Theo menu chi nhánh này, bạn có thể xem",
        }.get(intent, "Mình tìm được")
        return f"{lead}: {names}."

    @staticmethod
    def grounded(answer: str, context: str, catalog_items: list[Item] | None = None, has_suggestions: bool = False) -> bool:
        """Reject answers that mention a price or a menu item that is not in the retrieved data."""
        low = answer.lower()
        if has_suggestions and re.search(
            r"(chưa|không|hết)\s+(có|còn)\s+(món|đồ|thức uống|lựa chọn|thông tin|dữ liệu)|"
            r"chưa có thông tin|không (có|rõ) thông tin|không biết (chính xác|cụ thể)|"
            r"xin lỗi.{0,40}(chưa|không) có|"
            r"hỏi nhân viên.{0,40}(tư vấn|chi tiết)",
            low,
        ):
            return False
        if re.search(r"(tất cả|toàn bộ|mọi)\s+(các\s+)?(món|đồ uống|thức uống|menu|thực đơn)", low):
            return False
        ctx_prices = {re.sub(r"\D", "", m) for m in re.findall(r"\d[\d.,]*\s?(?:đ|k|nghìn)", context)}
        for m in re.findall(r"\d[\d.,]*\s?(?:đ|k|nghìn|vnd)", answer, flags=re.I):
            digits = re.sub(r"\D", "", m)
            if digits and digits not in ctx_prices and digits + "000" not in ctx_prices:
                return False
        universe = catalog_items if catalog_items is not None else MENU
        mentioned = [it for it in universe if it.name.lower() in answer.lower()]
        return all(it.name.lower() in context.lower() for it in mentioned)
