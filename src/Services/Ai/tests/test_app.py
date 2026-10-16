from datetime import datetime, timedelta, timezone

import jwt
from fastapi.testclient import TestClient

from app.config import get_settings
from app.main import app

client = TestClient(app)
settings = get_settings()
PREFIX = settings.route_prefix


def make_token(role: str, **overrides) -> str:
    now = datetime.now(timezone.utc)
    claims = {
        "sub": "11111111-1111-1111-1111-111111111111",
        "name": "tester",
        "role": role,
        "iss": settings.jwt_issuer,
        "aud": settings.jwt_audience,
        "exp": now + timedelta(minutes=5),
    }
    claims.update(overrides)
    return jwt.encode(claims, settings.jwt_signing_key, algorithm="HS256")


def auth(role: str, **overrides) -> dict:
    return {"Authorization": f"Bearer {make_token(role, **overrides)}"}


def test_live_and_info():
    assert client.get("/health/live").status_code == 200
    body = client.get(f"{PREFIX}/_info").json()
    assert body["service"] == "ai"


def test_correlation_id_generated_and_preserved():
    assert client.get(f"{PREFIX}/_info").headers["X-Correlation-Id"]
    echoed = client.get(f"{PREFIX}/_info", headers={"X-Correlation-Id": "abc-123"})
    assert echoed.headers["X-Correlation-Id"] == "abc-123"


def test_chat_is_open_to_guests():
    assert client.post(f"{PREFIX}/chat").status_code == 501


def test_forecast_requires_authentication():
    assert client.post(f"{PREFIX}/forecast/demand").status_code == 401


def test_forecast_rejects_wrong_role():
    assert client.post(f"{PREFIX}/forecast/demand", headers=auth("Customer")).status_code == 403


def test_forecast_allows_manager():
    assert client.post(f"{PREFIX}/forecast/demand", headers=auth("Manager")).status_code == 501


def test_expired_token_is_rejected():
    expired = auth("Manager", exp=datetime.now(timezone.utc) - timedelta(minutes=5))
    assert client.post(f"{PREFIX}/forecast/demand", headers=expired).status_code == 401
