from __future__ import annotations

from typing import Optional

from fastapi import APIRouter, Depends, HTTPException, status

from app.security import CurrentUser, get_optional_user

router = APIRouter(prefix="/chat", tags=["Smart Barista (RAG chatbot) - FE-16"])


@router.post("")
async def chat(user: Optional[CurrentUser] = Depends(get_optional_user)):
    """Menu Q&A / personalised upselling. Open to guests and customers.

    TODO: LangChain retrieval over an approved pgvector table in `public` -> prompt -> Groq (fallback DeepSeek).
    """
    raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, "Not implemented yet")
