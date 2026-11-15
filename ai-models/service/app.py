"""Stand-alone AI service (same routes the web app expects: /api/v1/ai/*).

    uvicorn service.app:app --port 8090

Chat / recommendations are scoped by tenantCode + branchId (and optional menu snapshot)
so suggestions never cross brands or branches.
"""
from __future__ import annotations

import asyncio
import json
import os
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any, Dict, List, Optional, Union

from fastapi import FastAPI, HTTPException
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

from apriori.mine import apriori
from common.catalog_store import STORE
from feedback.predict import ASPECT_LABEL, FeedbackAnalyzer
from rag.barista import Barista
from reco.predict import Recommender

ROOT = Path(__file__).resolve().parent.parent
state: Dict[str, Any] = {}


@asynccontextmanager
async def lifespan(_: FastAPI):
    state["barista"] = Barista()
    state["reco"] = state["barista"].reco
    feedback_dir = ROOT / "models/feedback"
    state["feedback"] = FeedbackAnalyzer() if (feedback_dir / "feedback.int8.onnx").exists() else None
    yield


app = FastAPI(title="AICOS AI models service", version="0.2.0", lifespan=lifespan)
ORIGINS = [o.strip() for o in os.environ.get("CORS_ORIGINS", "http://localhost:8443,http://localhost:5173").split(",") if o.strip()]
app.add_middleware(CORSMiddleware, allow_origins=ORIGINS, allow_methods=["GET", "POST"], allow_headers=["Content-Type", "Authorization", "X-Tenant"])
P = "/api/v1/ai"


class MenuItemIn(BaseModel):
    id: str
    name: str
    price: Union[float, int] = 0
    category: Optional[str] = None
    categoryName: Optional[str] = None
    description: Optional[str] = None
    tags: List[str] = Field(default_factory=list)
    basePrice: Optional[Union[float, int]] = None


class ChatIn(BaseModel):
    message: str = Field(min_length=1, max_length=600)
    history: List[str] = Field(default_factory=list, description="product ids the customer ordered before")
    tenantCode: Optional[str] = Field(None, max_length=40)
    branchId: Optional[str] = Field(None, max_length=64)
    branchName: Optional[str] = Field(None, max_length=120)
    menuItems: Optional[List[MenuItemIn]] = None


class AnalyzeIn(BaseModel):
    texts: List[str] = Field(min_length=1, max_length=100)


class RecoIn(BaseModel):
    history: List[str] = Field(default_factory=list)
    k: int = Field(5, ge=1, le=12)
    tenantCode: Optional[str] = Field(None, max_length=40)
    branchId: Optional[str] = Field(None, max_length=64)
    branchName: Optional[str] = Field(None, max_length=120)
    menuItems: Optional[List[MenuItemIn]] = None


@app.get("/health/live", include_in_schema=False)
def live():
    return {"status": "Healthy"}


async def _catalog(body: ChatIn | RecoIn):
    snap = [m.model_dump() for m in body.menuItems] if body.menuItems else None
    cat = await STORE.resolve(
        tenant_code=body.tenantCode,
        branch_id=body.branchId,
        branch_name=body.branchName,
        menu_items=snap,
    )
    if not cat.items:
        raise HTTPException(422, "Chi nhánh này chưa có món để gợi ý")
    return cat


@app.post(f"{P}/chat")
async def chat(body: ChatIn):
    catalog = await _catalog(body)
    return await state["barista"].reply(body.message, body.history, catalog=catalog)


@app.post(f"{P}/chat/stream")
async def chat_stream(body: ChatIn):
    catalog = await _catalog(body)
    result = await state["barista"].reply(body.message, body.history, catalog=catalog)

    async def events():
        words = result["reply"].split(" ")
        for i in range(0, len(words), 3):
            yield f"event: delta\ndata: {json.dumps(' '.join(words[i:i + 3]) + ' ', ensure_ascii=False)}\n\n"
            await asyncio.sleep(0.03)
        yield f"event: done\ndata: {json.dumps(result, ensure_ascii=False)}\n\n"

    return StreamingResponse(events(), media_type="text/event-stream")


@app.post(f"{P}/feedback/analyze")
def analyze(body: AnalyzeIn):
    analyzer: FeedbackAnalyzer | None = state["feedback"]
    if analyzer is None:
        return {"available": False, "items": []}
    t0 = time.perf_counter()
    items = analyzer.analyze(body.texts)
    for it in items:
        it["aspectLabels"] = [ASPECT_LABEL[a] for a in it["aspects"]]
    return {"available": True, "items": items, "latencyMs": int((time.perf_counter() - t0) * 1000), "model": "phobert-base-v2 int8"}


@app.post(f"{P}/recommendations")
async def recommendations(body: RecoIn):
    catalog = await _catalog(body)
    reco: Recommender = state["reco"]
    allow = {i.id for i in catalog.items}
    items = reco.recommend(body.history, body.k, allow=allow, catalog=catalog)
    return {
        "items": items,
        "tenantCode": catalog.tenant_code,
        "branchId": catalog.branch_id,
        "branchName": catalog.branch_name,
        "menuSize": len(catalog.items),
    }


class AprioriIn(BaseModel):
    transactions: List[List[str]] = Field(default_factory=list)
    minSupport: float = Field(0.05, ge=0.001, le=1.0)
    minConfidence: float = Field(0.4, ge=0.01, le=1.0)
    maxItemsetSize: int = Field(3, ge=2, le=5)


@app.post(f"{P}/apriori/mine")
def mine_apriori(body: AprioriIn):
    """Market-basket association rules (Apriori) for combo promotions."""
    rules = apriori(body.transactions, body.minSupport, body.minConfidence, body.maxItemsetSize)
    return {"rules": rules, "transactionCount": len(body.transactions), "ruleCount": len(rules)}


@app.get(f"{P}/models")
def models():
    info = {}
    for name in ("feedback", "intent", "reco"):
        f = ROOT / "models" / name / "metrics.json"
        info[name] = json.loads(f.read_text(encoding="utf-8")) if f.exists() else None
    return info
