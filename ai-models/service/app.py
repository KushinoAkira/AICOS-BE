"""Stand-alone AI service exposing the contract the web app expects (same routes as Backend/src/Services/Ai: /api/v1/ai/*).

    uvicorn service.app:app --port 8090
Models are loaded once at start-up and run on CPU (onnxruntime int8 for feedback, joblib for intent, numpy for recommender).
Auth is intentionally left to the gateway / the Ai service's security.py when this module is merged into it.
"""
from __future__ import annotations

import asyncio
import json
import time
from contextlib import asynccontextmanager
from pathlib import Path
from typing import Any

from fastapi import FastAPI
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import StreamingResponse
from pydantic import BaseModel, Field

from feedback.predict import ASPECT_LABEL, FeedbackAnalyzer
from rag.barista import Barista
from reco.predict import Recommender

ROOT = Path(__file__).resolve().parent.parent
state: dict[str, Any] = {}


@asynccontextmanager
async def lifespan(_: FastAPI):
    state["barista"] = Barista()
    state["reco"] = state["barista"].reco
    feedback_dir = ROOT / "models/feedback"
    state["feedback"] = FeedbackAnalyzer() if (feedback_dir / "feedback.int8.onnx").exists() else None
    yield


app = FastAPI(title="AICOS AI models service", version="0.1.0", lifespan=lifespan)
app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])
P = "/api/v1/ai"


class ChatIn(BaseModel):
    message: str = Field(min_length=1, max_length=600)
    history: list[str] = Field(default_factory=list, description="product ids the customer ordered before")


class AnalyzeIn(BaseModel):
    texts: list[str] = Field(min_length=1, max_length=100)


class RecoIn(BaseModel):
    history: list[str] = Field(default_factory=list)
    k: int = Field(5, ge=1, le=12)


@app.get("/health/live", include_in_schema=False)
def live():
    return {"status": "Healthy"}


@app.post(f"{P}/chat")
async def chat(body: ChatIn):
    return await state["barista"].reply(body.message, body.history)


@app.post(f"{P}/chat/stream")
async def chat_stream(body: ChatIn):
    """Server-sent events: the answer arrives in small chunks, then a final `done` event with suggestions and route info."""
    result = await state["barista"].reply(body.message, body.history)

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
def recommendations(body: RecoIn):
    return {"items": state["reco"].recommend(body.history, body.k)}


@app.get(f"{P}/models")
def models():
    info = {}
    for name in ("feedback", "intent", "reco"):
        f = ROOT / "models" / name / "metrics.json"
        info[name] = json.loads(f.read_text(encoding="utf-8")) if f.exists() else None
    return info
