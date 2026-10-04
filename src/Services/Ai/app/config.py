from __future__ import annotations

from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Configuration comes from environment variables (same names as docker-compose / .env.example)."""

    model_config = SettingsConfigDict(env_file=".env", extra="ignore")

    service_name: str = "ai"
    route_prefix: str = "/api/v1/ai"

    # Connects as role `ai_svc`, which only sees schema `ai` (pgvector lives there).
    database_url: str = "postgresql://ai_svc:aicos_dev@localhost:5432/aicos"
    redis_url: str = "redis://localhost:6379/0"

    # Must match the Jwt section of the .NET services.
    jwt_issuer: str = "aicos"
    jwt_audience: str = "aicos-clients"
    jwt_signing_key: str = "dev-only-signing-key-change-me-0123456789abcdef"

    groq_api_key: str = ""
    deepseek_api_key: str = ""


@lru_cache
def get_settings() -> Settings:
    return Settings()
