# AICOS AI models

Hybrid AI: own models + LLM API chain. Windows: `python -m venv .venv`, install `requirements.txt` (train) or `requirements-serve.txt` (serve).

    python -m feedback.gen_data && python -m feedback.train && python -m feedback.export_onnx && python -m feedback.evaluate
    python -m intent.train ; python -m reco.train ; python -m rag.evaluate
    uvicorn service.app:app --port 8090

## Tenant / branch isolation

Chat and recommendations require (or strongly expect) `tenantCode` + `branchId`. Optional `menuItems` is the menu the customer currently sees — AI **only** suggests from that catalog. Without a snapshot it loads `MENU_API_BASE_URL/public/branches/{branchId}/menu`, then falls back to seed catalogs (`caphenha` / `trasuamay`). Pairing knowledge never invents items from another brand.

## Apriori (market basket)

`POST /api/v1/ai/apriori/mine` — body `{ transactions: string[][], minSupport, minConfidence }` → association rules (support / confidence / lift). Used by Ordering to fill `association_rules` and promote combo vouchers. Pure Python in `apriori/mine.py` (no mlxtend).

Optional env: `GROQ_API_KEY(S)`, `DEEPSEEK_API_KEY`, `OLLAMA_URL`, `MENU_API_BASE_URL`, `CORS_ORIGINS`. Without LLM keys the Barista answers from grounded templates.

Gateway service `ai` (`:5111`) proxies `/api/v1/ai/chat|recommendations|feedback/analyze|apriori/mine` to this process (`AI_MODELS_URL`, default `http://host.docker.internal:8090`).

Metrics: `models/*/metrics.json`, `models/feedback/comparison.json`, `models/rag_eval.json`.
