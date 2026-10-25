"""LLM provider chain with key rotation, short timeouts and a circuit breaker:
Groq (several keys) -> DeepSeek -> Ollama (local) -> None (caller falls back to a template answer)."""
from __future__ import annotations

import logging
import os
import time
from dataclasses import dataclass
from pathlib import Path

import httpx
from dotenv import load_dotenv

import common.tls  # noqa: F401  (local CA bundle, if any)

load_dotenv(Path(__file__).resolve().parent.parent / ".env")
log = logging.getLogger("aicos.llm")


@dataclass
class LlmResult:
    text: str
    provider: str
    latency_ms: int


class Breaker:
    """Skip a provider for `cooldown` seconds after `threshold` consecutive failures."""

    def __init__(self, threshold: int = 3, cooldown: float = 60.0):
        self.threshold, self.cooldown, self.fails, self.opened_at = threshold, cooldown, 0, 0.0

    def allow(self) -> bool:
        return self.fails < self.threshold or time.time() - self.opened_at > self.cooldown

    def ok(self) -> None:
        self.fails = 0

    def bad(self) -> None:
        self.fails += 1
        if self.fails >= self.threshold:
            self.opened_at = time.time()


def _keys(name: str) -> list[str]:
    return [k.strip() for k in os.environ.get(name, "").split(",") if k.strip()]


class LlmChain:
    def __init__(self, timeout: float = 12.0):
        self.timeout = timeout
        self.breakers = {"groq": Breaker(), "deepseek": Breaker(), "ollama": Breaker()}

    async def _openai_style(self, client: httpx.AsyncClient, base: str, key: str, model: str, messages: list[dict], max_tokens: int) -> str:
        body: dict = {"model": model, "messages": messages, "temperature": 0.2, "max_tokens": max_tokens}
        if "gpt-oss" in model:  # reasoning models: keep thinking short so the token budget is left for the answer
            body.update(reasoning_effort="low", max_tokens=max_tokens + 400)
        res = await client.post(f"{base}/chat/completions", headers={"Authorization": f"Bearer {key}"}, json=body)
        res.raise_for_status()
        return (res.json()["choices"][0]["message"].get("content") or "").strip()

    async def complete(self, messages: list[dict], max_tokens: int = 300, easy: bool = False) -> LlmResult | None:
        groq_model = "openai/gpt-oss-20b" if easy else os.environ.get("GROQ_MODEL", "openai/gpt-oss-120b")
        async with httpx.AsyncClient(timeout=self.timeout) as client:
            attempts: list[tuple[str, list]] = [
                ("groq", [("https://api.groq.com/openai/v1", k, groq_model) for k in _keys("GROQ_API_KEY")]),
                ("deepseek", [("https://api.deepseek.com", k, "deepseek-chat") for k in _keys("DEEPSEEK_API_KEY")]),
            ]
            for provider, candidates in attempts:
                if not self.breakers[provider].allow():
                    continue
                for base, key, model in candidates:  # rotate keys: a rate-limited key just moves on to the next
                    t0 = time.perf_counter()
                    try:
                        text = await self._openai_style(client, base, key, model, messages, max_tokens)
                        self.breakers[provider].ok()
                        return LlmResult(text, f"{provider}:{model}", int((time.perf_counter() - t0) * 1000))
                    except Exception as exc:
                        log.warning("%s failed: %s", provider, type(exc).__name__)
                        self.breakers[provider].bad()
            ollama = os.environ.get("OLLAMA_URL", "")
            if ollama and self.breakers["ollama"].allow():
                t0 = time.perf_counter()
                try:
                    res = await client.post(f"{ollama.rstrip('/')}/api/chat", json={"model": os.environ.get("OLLAMA_MODEL", "qwen2.5:3b"), "messages": messages, "stream": False})
                    res.raise_for_status()
                    self.breakers["ollama"].ok()
                    return LlmResult(res.json()["message"]["content"].strip(), "ollama", int((time.perf_counter() - t0) * 1000))
                except Exception as exc:
                    log.warning("ollama failed: %s", type(exc).__name__)
                    self.breakers["ollama"].bad()
        return None
