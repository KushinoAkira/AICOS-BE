# AICOS AI models service (FastAPI, CPU only).
#   docker build -t aicos/ai-models ./ai-models
#
# The image trains the lightweight models (intent router, recommender) at build time so it works out of the box.
# The PhoBERT ONNX model is large and is NOT baked in: mount it at /app/models/feedback (see compose.yml).
# Without it /feedback/analyze answers available=false and everything else keeps working.

# ---- builder: resolve wheels into a venv -------------------------------------------------------
FROM python:3.11-slim AS builder
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
RUN python -m venv /opt/venv
ENV PATH="/opt/venv/bin:$PATH"
WORKDIR /build
COPY requirements-serve.txt .
RUN pip install -r requirements-serve.txt

# ---- trainer: produce the small models --------------------------------------------------------
FROM builder AS trainer
WORKDIR /app
COPY common ./common
COPY intent ./intent
COPY reco ./reco
RUN python -m intent.train && python -m reco.train

# ---- runtime -----------------------------------------------------------------------------------
FROM python:3.11-slim AS runtime
ENV PYTHONUNBUFFERED=1 PYTHONDONTWRITEBYTECODE=1 \
    PATH="/opt/venv/bin:$PATH" \
    HF_HOME=/tmp/hf TRANSFORMERS_CACHE=/tmp/hf
RUN groupadd --system --gid 10001 app && useradd --system --uid 10001 --gid app --no-create-home app
WORKDIR /app
COPY --from=builder /opt/venv /opt/venv
COPY --chown=app:app common ./common
COPY --chown=app:app intent ./intent
COPY --chown=app:app reco ./reco
COPY --chown=app:app rag ./rag
COPY --chown=app:app feedback ./feedback
COPY --chown=app:app service ./service
COPY --from=trainer --chown=app:app /app/models ./models
USER 10001:10001
EXPOSE 8000
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=5 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8000/health/live', timeout=2).status==200 else 1)"
CMD ["uvicorn", "service.app:app", "--host", "0.0.0.0", "--port", "8000", "--proxy-headers", "--no-server-header"]

LABEL org.opencontainers.image.title="aicos-ai-models" \
      org.opencontainers.image.source="https://github.com/KushinoAkira/AICOS-BE"
