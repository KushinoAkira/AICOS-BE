"""Forward Smart Barista / reco / feedback calls to the ai-models service.

The gateway still routes /api/v1/ai/* here; this service no longer returns 501 for the
customer-facing AI endpoints. Forecast / attendance stay as separate stubs.
"""
from __future__ import annotations

import os
from typing import Any, Optional

import httpx
from fastapi import APIRouter, Depends, HTTPException, Request, Response, status

from app.security import CurrentUser, get_optional_user

router = APIRouter(tags=["AI models proxy"])

AI_MODELS_URL = os.environ.get("AI_MODELS_URL", "http://host.docker.internal:8090").rstrip("/")
TIMEOUT = float(os.environ.get("AI_MODELS_TIMEOUT", "30"))


async def _forward(method: str, path: str, request: Request) -> Response:
    url = f"{AI_MODELS_URL}{path}"
    headers = {}
    auth = request.headers.get("authorization")
    if auth:
        headers["Authorization"] = auth
    tenant = request.headers.get("x-tenant")
    if tenant:
        headers["X-Tenant"] = tenant
    body = await request.body()
    try:
        async with httpx.AsyncClient(timeout=TIMEOUT) as client:
            upstream = await client.request(
                method,
                url,
                content=body if body else None,
                headers={**headers, "Content-Type": request.headers.get("content-type", "application/json")},
            )
    except httpx.RequestError as exc:
        raise HTTPException(
            status.HTTP_503_SERVICE_UNAVAILABLE,
            "Dịch vụ AI models chưa sẵn sàng. Chạy ai-models trên :8090 hoặc đặt AI_MODELS_URL.",
        ) from exc
    return Response(content=upstream.content, status_code=upstream.status_code, media_type=upstream.headers.get("content-type"))


@router.post("/chat")
async def chat(request: Request, _user: Optional[CurrentUser] = Depends(get_optional_user)):
    return await _forward("POST", "/api/v1/ai/chat", request)


@router.post("/chat/stream")
async def chat_stream(request: Request, _user: Optional[CurrentUser] = Depends(get_optional_user)):
    return await _forward("POST", "/api/v1/ai/chat/stream", request)


@router.post("/recommendations")
async def recommendations(request: Request, _user: Optional[CurrentUser] = Depends(get_optional_user)):
    return await _forward("POST", "/api/v1/ai/recommendations", request)


@router.post("/feedback/analyze")
async def feedback_analyze(request: Request, _user: Optional[CurrentUser] = Depends(get_optional_user)):
    return await _forward("POST", "/api/v1/ai/feedback/analyze", request)


@router.get("/models")
async def models(request: Request):
    return await _forward("GET", "/api/v1/ai/models", request)


@router.post("/apriori/mine")
async def apriori_mine(request: Request, _user: Optional[CurrentUser] = Depends(get_optional_user)):
    return await _forward("POST", "/api/v1/ai/apriori/mine", request)
