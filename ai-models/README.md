# AICOS AI models

Hybrid AI: own models + LLM API chain. Windows: `python -m venv .venv`, install `requirements.txt`.

    python -m feedback.gen_data && python -m feedback.train && python -m feedback.export_onnx && python -m feedback.evaluate
    python -m intent.train ; python -m reco.train ; python -m rag.evaluate
    uvicorn service.app:app --port 8090

Optional env: GROQ_API_KEY(S), DEEPSEEK_API_KEY, OLLAMA_URL. Without keys the Barista answers from grounded templates.
Metrics are written to `models/*/metrics.json`, `models/feedback/comparison.json`, `models/rag_eval.json`.
