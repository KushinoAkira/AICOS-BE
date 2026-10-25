"""Fine-tune PhoBERT for Vietnamese feedback: sentiment (3 classes) + aspects (5 labels, multi-label).

    python -m feedback.train            # trains on data/feedback_train.jsonl, evaluates on val + gold
Outputs models/feedback/{model.pt, tokenizer files, metrics.json}.
"""
from __future__ import annotations

import json
import random
import time
from pathlib import Path

import numpy as np
import torch
from sklearn.metrics import f1_score
from torch import nn
from torch.utils.data import DataLoader
from transformers import AutoModel, AutoTokenizer, get_linear_schedule_with_warmup

import common.tls  # noqa: F401  (local CA bundle, if any)
from feedback.gen_data import ASPECTS, SENTIMENTS
from feedback.gold_data import rows as gold_rows

ROOT = Path(__file__).resolve().parent.parent
BASE = "vinai/phobert-base-v2"
MAX_LEN = 96


class FeedbackModel(nn.Module):
    def __init__(self, base: str = BASE):
        super().__init__()
        self.encoder = AutoModel.from_pretrained(base)
        h = self.encoder.config.hidden_size
        self.drop = nn.Dropout(0.1)
        self.sentiment = nn.Linear(h, len(SENTIMENTS))
        self.aspects = nn.Linear(h, len(ASPECTS))

    def forward(self, input_ids, attention_mask):
        out = self.encoder(input_ids=input_ids, attention_mask=attention_mask).last_hidden_state[:, 0]
        out = self.drop(out)
        return self.sentiment(out), self.aspects(out)


def load(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def encode(tok, rows: list[dict]):
    enc = tok([r["text"] for r in rows], truncation=True, max_length=MAX_LEN, padding="max_length", return_tensors="pt")
    y_s = torch.tensor([SENTIMENTS.index(r["sentiment"]) for r in rows])
    y_a = torch.tensor([[1.0 if a in r["aspects"] else 0.0 for a in ASPECTS] for r in rows])
    return enc["input_ids"], enc["attention_mask"], y_s, y_a


@torch.no_grad()
def predict(model, tok, rows: list[dict], device, bs: int = 64):
    model.eval()
    ids, mask, _, _ = encode(tok, rows)
    ps, pa = [], []
    for i in range(0, len(rows), bs):
        s, a = model(ids[i:i + bs].to(device), mask[i:i + bs].to(device))
        ps.append(s.softmax(-1).cpu().numpy())
        pa.append(a.sigmoid().cpu().numpy())
    return np.concatenate(ps), np.concatenate(pa)


def score(model, tok, rows, device) -> dict:
    ps, pa = predict(model, tok, rows, device)
    y_s = np.array([SENTIMENTS.index(r["sentiment"]) for r in rows])
    y_a = np.array([[1 if a in r["aspects"] else 0 for a in ASPECTS] for r in rows])
    return {
        "sentiment_accuracy": float((ps.argmax(1) == y_s).mean()),
        "sentiment_macro_f1": float(f1_score(y_s, ps.argmax(1), average="macro")),
        "aspect_micro_f1": float(f1_score(y_a, (pa > 0.5).astype(int), average="micro", zero_division=0)),
    }


def main(epochs: int = 4, bs: int = 32, lr: float = 3e-5, seed: int = 7) -> None:
    random.seed(seed); np.random.seed(seed); torch.manual_seed(seed)
    device = torch.device("cuda" if torch.cuda.is_available() else "cpu")
    tok = AutoTokenizer.from_pretrained(BASE)
    train, val, gold = load(ROOT / "data/feedback_train.jsonl"), load(ROOT / "data/feedback_val.jsonl"), gold_rows()
    llm_path = ROOT / "data/feedback_llm.jsonl"  # realistic teacher-generated reviews (feedback.gen_llm_data); the gold set is never mixed in
    llm_val: list[dict] = []
    if llm_path.exists():
        gold_texts = {g["text"] for g in gold}
        llm = [r for r in load(llm_path) if r["text"] not in gold_texts]
        random.Random(seed).shuffle(llm)
        cut = max(1, len(llm) // 10)
        llm_val, llm_train = llm[:cut], llm[cut:]
        train = train + llm_train * 2  # real-world phrasing is rarer than templates, so it is up-weighted
        val = val + llm_val
        print(f"llm data: {len(llm_train)} train (x2) / {len(llm_val)} val", flush=True)
    ids, mask, y_s, y_a = encode(tok, train)
    ds = list(zip(ids, mask, y_s, y_a))
    loader = DataLoader(ds, batch_size=bs, shuffle=True, collate_fn=lambda b: tuple(torch.stack(x) for x in zip(*b)))

    model = FeedbackModel().to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=lr, weight_decay=0.01)
    total = epochs * len(loader)
    sched = get_linear_schedule_with_warmup(opt, int(0.1 * total), total)
    scaler = torch.cuda.amp.GradScaler(enabled=device.type == "cuda")
    ce, bce = nn.CrossEntropyLoss(), nn.BCEWithLogitsLoss()

    t0 = time.time()
    for epoch in range(1, epochs + 1):
        model.train()
        running = 0.0
        for b_ids, b_mask, b_s, b_a in loader:
            b_ids, b_mask, b_s, b_a = (x.to(device) for x in (b_ids, b_mask, b_s, b_a))
            with torch.autocast(device_type=device.type, enabled=device.type == "cuda"):
                ls, la = model(b_ids, b_mask)
                loss = ce(ls, b_s) + bce(la, b_a)
            opt.zero_grad()
            scaler.scale(loss).backward()
            scaler.unscale_(opt)
            nn.utils.clip_grad_norm_(model.parameters(), 1.0)
            scaler.step(opt); scaler.update(); sched.step()
            running += loss.item()
        print(f"epoch {epoch}/{epochs} loss={running / len(loader):.4f} val={score(model, tok, val, device)}", flush=True)

    metrics = {"val": score(model, tok, val, device), "gold": score(model, tok, gold, device),
               **({"llm_val": score(model, tok, llm_val, device)} if llm_val else {}),
               "train_seconds": round(time.time() - t0), "base_model": BASE, "train_size": len(train), "device": str(device)}
    out = ROOT / "models/feedback"
    out.mkdir(parents=True, exist_ok=True)
    torch.save(model.state_dict(), out / "model.pt")
    tok.save_pretrained(out)
    (out / "metrics.json").write_text(json.dumps(metrics, indent=2), encoding="utf-8")
    print(json.dumps(metrics, indent=2))


if __name__ == "__main__":
    main()
