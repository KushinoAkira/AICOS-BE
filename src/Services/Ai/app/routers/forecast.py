from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, status

from app.security import CurrentUser, require_roles

router = APIRouter(prefix="/forecast", tags=["Demand forecasting - FE-15"])


@router.post("/demand")
async def forecast_demand(user: CurrentUser = Depends(require_roles("SystemAdmin", "Manager", "WarehouseStaff"))):
    """Daily ingredient / cup demand per branch.

    TODO: XGBoost with calendar + rolling-average features (moving-average baseline first).
    """
    raise HTTPException(status.HTTP_501_NOT_IMPLEMENTED, "Not implemented yet")
