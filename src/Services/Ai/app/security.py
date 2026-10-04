from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

import jwt
from fastapi import Depends, HTTPException, status
from fastapi.security import HTTPAuthorizationCredentials, HTTPBearer

from app.config import Settings, get_settings

_bearer = HTTPBearer(auto_error=False)


@dataclass(frozen=True)
class CurrentUser:
    """Mirrors ICurrentUser in the .NET BuildingBlocks (same claim names)."""

    user_id: str
    name: Optional[str]
    role: Optional[str]
    branch_id: Optional[str]


def get_optional_user(
    credentials: Optional[HTTPAuthorizationCredentials] = Depends(_bearer),
    settings: Settings = Depends(get_settings),
) -> Optional[CurrentUser]:
    """Returns the caller if a valid bearer token was sent, None for anonymous, 401 for a bad token."""
    if credentials is None:
        return None
    try:
        claims = jwt.decode(
            credentials.credentials,
            settings.jwt_signing_key,
            algorithms=["HS256"],
            audience=settings.jwt_audience,
            issuer=settings.jwt_issuer,
            leeway=30,
        )
    except jwt.PyJWTError as exc:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Invalid or expired access token") from exc

    return CurrentUser(
        user_id=claims["sub"],
        name=claims.get("name"),
        role=claims.get("role"),
        branch_id=claims.get("branch_id"),
    )


def get_current_user(user: Optional[CurrentUser] = Depends(get_optional_user)) -> CurrentUser:
    if user is None:
        raise HTTPException(status.HTTP_401_UNAUTHORIZED, "Authentication required")
    return user


def require_roles(*roles: str):
    """Dependency factory: `Depends(require_roles("Manager", "SystemAdmin"))`."""

    def checker(user: CurrentUser = Depends(get_current_user)) -> CurrentUser:
        if user.role not in roles:
            raise HTTPException(status.HTTP_403_FORBIDDEN, "You do not have permission to perform this action.")
        return user

    return checker
