from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status

from app.security import CurrentUser, require_roles

router = APIRouter(prefix="/summary", tags=["AI business summary - FE-14"])


@router.post("")
async def business_summary(user: CurrentUser = Depends(require_roles("SystemAdmin", "Manager", "AiBusinessAssistant"))):
    """Natural-language summary + pricing/promotion suggestions from report data.

    TODO: feed Reporting data into the LLM prompt (same Groq/DeepSeek client as the chatbot).
    """
    raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, "Not implemented yet")
