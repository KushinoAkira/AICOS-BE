"""Smart Barista pipeline (UC-52). Order of work, cheapest first:

  mask PII -> intent model -> (handoff | smalltalk | shop facts: answered WITHOUT an LLM)
           -> constraint filter on structured menu data (allergy / diet are decided by data, never by the LLM)
           -> retrieval + personalised ranking -> LLM phrases the answer (8B for easy, 70B for hard) -> guardrail check
           -> template answer when no LLM is reachable or the guardrail rejects the text.
"""
from __future__ import annotations

import re
import time
from pathlib import Path
from typing import Any

import joblib

from common.menu import BY_ID, MENU, SHOP, TOPPINGS, Item
from rag.llm import LlmChain
from rag.retriever import Retriever
from reco.predict import Recommender

INTENT_MODEL = Path(__file__).resolve().parent.parent / "models/intent/intent.joblib"
PII = [(re.compile(r"\b0\d{9}\b"), "[sđt]"), (re.compile(r"[\w.+-]+@[\w-]+\.[\w.]+"), "[email]")]
SYSTEM = ("Bạn là Barista thông minh của quán cà phê, nói tiếng Việt thân thiện, ngắn gọn (tối đa 3 câu). "
          "CHỈ dùng thông tin trong phần DỮ LIỆU. Không bịa món, giá hay thành phần. Nếu thiếu thông tin, nói chưa chắc và gợi ý hỏi nhân viên.")


def mask(text: str) -> str:
    for rx, rep in PII:
        text = rx.sub(rep, text)
    return text


def constraints(q: str) -> dict[str, Any]:
    t = q.lower()
    c: dict[str, Any] = {}
    if re.search(r"dị ứng (với )?sữa|(không|chẳng|ko|kiêng|tránh|khỏi)\s+(có\s+|chứa\s+|dùng\s+|uống\s+|muốn\s+|được\s+)*(sữa|lactose)|lactose|uống sữa không được|không sữa", t): c["dairy"] = False
    avoid = [k for k, pat in (("gluten", r"gluten|bột mì"), ("trứng", r"trứng"), ("sữa", r"sữa")) if re.search(rf"dị ứng (với )?({pat})", t)]
    if avoid: c["avoid"] = avoid
    if re.search(r"ăn chay|vegan|thuần chay", t): c["vegan"] = True
    child = re.search(r"\b([0-9]|1[0-2])\s*tuổi|trẻ em|trẻ nhỏ|em bé|con (mình|tôi)|cháu", t)
    if re.search(r"(không|ko|kiêng|tránh|hạn chế)\s+(có\s+|chứa\s+|dùng\s+|uống\s+|muốn\s+)*(caffeine|cafein|cà phê|cafe)|mất ngủ|khó ngủ|bà bầu|mang thai", t) or child: c["caffeine"] = False
    if re.search(r"không đường|kiêng đường|tiểu đường|ít ngọt|không ngọt", t): c["max_sweet"] = 0 if re.search(r"không đường|kiêng đường|tiểu đường", t) else 1
    if re.search(r"ít calo|giảm cân", t): c["tag"] = "ít calo"
    if re.search(r"nóng bức|trời nóng|giải nhiệt|mát|mùa hè|lạnh|đá", t) and not re.search(r"ấm|mưa", t): c["cold"] = True
    if re.search(r"mưa|ấm|lạnh người|trời lạnh", t): c["hot"] = True
    if re.search(r"tỉnh táo|buồn ngủ|mệt", t): c["caffeine"] = True
    if re.search(r"(thức uống|đồ uống|món|đồ|uống)\s+nóng(?!\s*bức)", t) and not re.search(r"nóng bức|trời nóng", t): c["hot"] = True; c.pop("cold", None)
    if re.search(r"ăn kèm|bánh|ăn sáng", t): c["category"] = "Bánh"
    return c


def allowed(it: Item, c: dict[str, Any]) -> bool:
    return not (("dairy" in c and it.dairy) or any(a in it.allergens for a in c.get("avoid", [])) or (c.get("vegan") and not it.vegan) or ("caffeine" in c and it.caffeine != c["caffeine"])
                or ("max_sweet" in c and it.sweetness > c["max_sweet"]) or (c.get("tag") and c["tag"] not in it.tags)
                or (c.get("cold") and not it.cold) or (c.get("hot") and not it.hot) or (c.get("category") and it.category != c["category"]))


class Barista:
    def __init__(self, retriever: Retriever | None = None, llm: LlmChain | None = None):
        self.intent = joblib.load(INTENT_MODEL)
        self.retriever = retriever or Retriever()
        self.reco = Recommender()
        self.llm = llm or LlmChain()

    def classify(self, text: str) -> tuple[str, float]:
        proba = self.intent.predict_proba([text])[0]
        k = int(proba.argmax())
        return str(self.intent.classes_[k]), float(proba[k])

    async def reply(self, message: str, history: list[str] | None = None) -> dict[str, Any]:
        t0 = time.perf_counter()
        text = mask(message.strip())
        intent, conf = self.classify(text)
        route = {"intent": intent, "confidence": round(conf, 3), "llm": None, "retrieval": self.retriever.mode}

        def done(reply: str, sug: list[dict] | None = None, **extra: Any) -> dict[str, Any]:
            route["latency_ms"] = int((time.perf_counter() - t0) * 1000)
            return {"reply": reply, "suggestions": sug or [], "route": route, **extra}

        if conf < 0.30:
            return done("Mình chưa hiểu rõ ý bạn lắm. Bạn có thể hỏi về món, giá, gợi ý đồ uống, hoặc nhờ mình gọi nhân viên hỗ trợ nhé.", handoff=False)
        if intent == "human_handoff":
            return done("Mình sẽ chuyển bạn cho nhân viên của quán ngay. Bạn vui lòng chờ trong giây lát nhé.", handoff=True)
        if intent == "smalltalk":
            return done("Chào bạn! Mình là Barista thông minh của quán. Bạn muốn xem món, tìm đồ uống hợp khẩu vị hay hỏi về ưu đãi?", handoff=False)
        if intent == "shop_info":
            doc = self.retriever.search(text, 1, kinds=("shop",))[0][0]
            return done(doc.text.split(": ", 1)[1] + ".", handoff=False)
        if intent == "order_status":
            return done("Bạn mở mục Đơn hàng của tôi để xem trạng thái đơn theo thời gian thực nhé. Nếu cần hủy hoặc có sự cố, mình có thể nhờ nhân viên hỗ trợ.", handoff=False, action="open_orders")
        if intent == "loyalty_promo":
            return done(SHOP["loyalty"] + ". Bạn xem số điểm và voucher của mình ở mục Điểm thưởng nhé.", handoff=False, action="open_loyalty")

        c = constraints(text)
        if re.search(r"dị ứng", text.lower()) and not ({"dairy", "avoid", "vegan"} & c.keys()):
            # An allergy we cannot map to menu data: never guess, hand the decision to staff.
            return done("Mình chưa có đủ thông tin thành phần để xác nhận món an toàn với dị ứng của bạn. Bạn vui lòng nói rõ thành phần cần tránh (sữa, gluten, trứng...) hoặc để nhân viên kiểm tra giúp nhé.", handoff=True)
        pool = [it for it in MENU if allowed(it, c)]
        hits = self.retriever.search(text, 8, kinds=("item",))
        ranked_ids = [d.id for d, _ in hits if d.item and allowed(d.item, c)]
        if intent in ("recommend", "allergy_diet") or c:
            personal = self.reco.recommend(history or [], 8, allow={i.id for i in pool})
            order = {pid: n for n, pid in enumerate(ranked_ids)}
            personal.sort(key=lambda s: (order.get(s["productId"], 99) * 0.5) - s["score"])
            suggestions = personal[:3]
        else:
            suggestions = [{"productId": pid, "name": BY_ID[pid].name, "price": BY_ID[pid].price, "reason": "Khớp với câu hỏi của bạn"} for pid in ranked_ids[:3]]
        if not suggestions:
            return done("Mình chưa thấy món nào trong menu đáp ứng đủ yêu cầu này. Bạn có thể nói rõ hơn hoặc nhờ nhân viên tư vấn thêm nhé.", handoff=False)

        context = "\n".join(f"- {BY_ID[s['productId']].name}: {BY_ID[s['productId']].price}đ. {BY_ID[s['productId']].description}" for s in suggestions)
        if intent == "menu_info":
            context += "\nTopping: " + ", ".join(f"{n} +{p}đ" for n, p in TOPPINGS)
        template = self.template(intent, suggestions)
        result = await self.llm.complete(
            [{"role": "system", "content": SYSTEM}, {"role": "user", "content": f"Câu hỏi: {text}\nDỮ LIỆU:\n{context}"}],
            max_tokens=220, easy=intent == "menu_info",
        )
        if result and self.grounded(result.text, context, has_suggestions=True):
            route["llm"] = result.provider
            return done(result.text, suggestions, handoff=False)
        route["llm"] = "template" if not result else f"rejected:{result.provider}"
        return done(template, suggestions, handoff=False)

    @staticmethod
    def template(intent: str, sug: list[dict]) -> str:
        names = ", ".join(f"{s['name']} ({s['price']:,}đ)".replace(",", ".") for s in sug)
        lead = {"recommend": "Mình gợi ý bạn thử", "allergy_diet": "Các món phù hợp với yêu cầu của bạn là", "menu_info": "Theo menu, bạn có thể xem"}.get(intent, "Mình tìm được")
        return f"{lead}: {names}."

    @staticmethod
    def grounded(answer: str, context: str, has_suggestions: bool = False) -> bool:
        """Reject answers that mention a price or a menu item that is not in the retrieved data,
        deny that anything exists although items were found, or make sweeping claims ("tất cả/mọi món") the data cannot back."""
        low = answer.lower()
        if has_suggestions and re.search(r"(chưa|không|hết)\s+(có|còn)\s+(món|đồ|thức uống|lựa chọn)|xin lỗi.{0,40}(chưa|không) có", low):
            return False
        if re.search(r"(tất cả|toàn bộ|mọi)\s+(các\s+)?(món|đồ uống|thức uống|menu|thực đơn)", low):
            return False
        ctx_prices = {re.sub(r"\D", "", m) for m in re.findall(r"\d[\d.,]*\s?(?:đ|k|nghìn)", context)}
        for m in re.findall(r"\d[\d.,]*\s?(?:đ|k|nghìn|vnd)", answer, flags=re.I):
            digits = re.sub(r"\D", "", m)
            if digits and digits not in ctx_prices and digits + "000" not in ctx_prices:
                return False
        mentioned = [it for it in MENU if it.name.lower() in answer.lower()]
        return all(it.name.lower() in context.lower() for it in mentioned)
