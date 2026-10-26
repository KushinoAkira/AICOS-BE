from __future__ import annotations

import asyncio
import re
import uuid
from datetime import datetime, timezone
from urllib.parse import urlparse

from fastapi import FastAPI, Request, Response
from fastapi.responses import JSONResponse

from app.config import get_settings
from app.routers import attendance, chat, forecast, summary

settings = get_settings()

CORRELATION_HEADER = "X-Correlation-Id"
_VALID_CORRELATION_ID = re.compile(r"^[A-Za-z0-9_-]{1,64}$")

app = FastAPI(title="AICOS AI Service", version="0.1.0")


@app.middleware("http")
async def correlation_id_middleware(request: Request, call_next):
    """Same contract as CorrelationIdMiddleware in the .NET services."""
    incoming = request.headers.get(CORRELATION_HEADER, "")
    correlation_id = incoming if _VALID_CORRELATION_ID.match(incoming) else uuid.uuid4().hex
    request.state.correlation_id = correlation_id
    response: Response = await call_next(request)
    response.headers[CORRELATION_HEADER] = correlation_id
    return response


# --- health -----------------------------------------------------------------

async def _can_connect(url: str, default_port: int) -> bool:
    parsed = urlparse(url)
    try:
        _, writer = await asyncio.wait_for(
            asyncio.open_connection(parsed.hostname, parsed.port or default_port), timeout=2
        )
    except (OSError, asyncio.TimeoutError):
        return False
    writer.close()
    return True


@app.get("/health/live", include_in_schema=False)
async def live():
    return {"status": "Healthy"}


@app.get("/health/ready", include_in_schema=False)
async def ready():
    postgres, redis = await asyncio.gather(
        _can_connect(settings.database_url, 5432),
        _can_connect(settings.redis_url, 6379),
    )
    healthy = postgres and redis
    return JSONResponse(
        status_code=200 if healthy else 503,
        content={"status": "Healthy" if healthy else "Unhealthy", "postgres": postgres, "redis": redis},
    )


# --- api (mounted under the same /api/v1/{prefix} the gateway routes on) ------

@app.get(f"{settings.route_prefix}/_info", tags=["AI Service"])
async def info():
    return {
        "service": settings.service_name,
        "displayName": "AI Service",
        "schema": "public",
        "environment": "python",
        "utcNow": datetime.now(timezone.utc).isoformat(),
    }


for module in (chat, attendance, forecast, summary):
    app.include_router(module.router, prefix=settings.route_prefix)
