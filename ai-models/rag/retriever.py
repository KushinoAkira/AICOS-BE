"""Hybrid retrieval over menu + shop facts: multilingual-e5-small embeddings (self-hosted, CPU) blended with char TF-IDF.

In production the same documents live in pgvector (schema `ai`) filtered by `tenant_id`; here they are in memory.
"""
from __future__ import annotations

import logging
import os
from dataclasses import dataclass

import numpy as np
from sklearn.feature_extraction.text import TfidfVectorizer

from common.menu import MENU, SHOP, TOPPINGS, Item

log = logging.getLogger("aicos.rag")
EMBED_MODEL = os.environ.get("AICOS_EMBED_MODEL", "intfloat/multilingual-e5-small")


@dataclass
class Doc:
    id: str
    kind: str          # "item" | "shop" | "topping"
    text: str
    item: Item | None = None


def build_docs() -> list[Doc]:
    docs = []
    for it in MENU:
        flags = [("có sữa" if it.dairy else "không sữa"), ("có caffeine" if it.caffeine else "không caffeine"), ("thuần chay" if it.vegan else "không thuần chay"),
                 f"độ ngọt {['không đường', 'ít ngọt', 'ngọt vừa', 'rất ngọt'][it.sweetness]}", ("uống nóng" if it.hot else "") + (" uống lạnh" if it.cold else "")]
        docs.append(Doc(it.id, "item", f"{it.name} ({it.category}) giá {it.price}đ. {it.description} {', '.join(f for f in flags if f)}. {' '.join(it.tags)}", it))
    for key, value in SHOP.items():
        docs.append(Doc(f"shop-{key}", "shop", f"{key}: {value}"))
    docs.append(Doc("toppings", "topping", "Topping: " + ", ".join(f"{n} +{p}đ" for n, p in TOPPINGS)))
    return docs


class Retriever:
    def __init__(self, use_embeddings: bool = True):
        self.docs = build_docs()
        self.tfidf = TfidfVectorizer(analyzer="char_wb", ngram_range=(2, 5), sublinear_tf=True)
        self.sparse = self.tfidf.fit_transform([d.text for d in self.docs])
        self.model = None
        self.dense: np.ndarray | None = None
        if use_embeddings:
            try:
                from sentence_transformers import SentenceTransformer
                self.model = SentenceTransformer(EMBED_MODEL, device="cpu")
                self.dense = self.model.encode([f"passage: {d.text}" for d in self.docs], normalize_embeddings=True)
            except Exception as exc:  # offline or model missing: keep going with TF-IDF only
                log.warning("embedding model unavailable (%s); using TF-IDF only", exc)

    @property
    def mode(self) -> str:
        return "hybrid(e5+tfidf)" if self.dense is not None else "tfidf"

    def search(self, query: str, k: int = 5, kinds: tuple[str, ...] | None = None) -> list[tuple[Doc, float]]:
        s = (self.sparse @ self.tfidf.transform([query]).T).toarray().ravel()
        score = s
        if self.dense is not None and self.model is not None:
            q = self.model.encode([f"query: {query}"], normalize_embeddings=True)[0]
            d = self.dense @ q
            d = (d - d.min()) / (d.max() - d.min() + 1e-9)
            score = 0.65 * d + 0.35 * (s / (s.max() + 1e-9))
        order = np.argsort(-score)
        out = [(self.docs[i], float(score[i])) for i in order if kinds is None or self.docs[i].kind in kinds]
        return out[:k]
