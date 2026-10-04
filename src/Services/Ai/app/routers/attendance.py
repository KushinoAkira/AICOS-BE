from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status

from app.security import CurrentUser, require_roles

router = APIRouter(prefix="/attendance", tags=["Face-scan attendance - FE-11"])


@router.post("/verify")
async def verify_face(user: CurrentUser = Depends(require_roles("SystemAdmin", "Manager", "Cashier", "WarehouseStaff"))):
    """Compare a captured face against the stored (encrypted) embedding with InsightFace; below the confidence
    threshold the caller must fall back to PIN / manager confirmation (LI-3).

    TODO: InsightFace `buffalo_l` embedding + cosine similarity.
    """
    raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, "Not implemented yet")
