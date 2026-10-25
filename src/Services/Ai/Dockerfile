# Build from the Ai folder:  docker build -t aicos/ai src/Services/Ai
#
# Two stages so the final image carries only the application and its libraries: pip, setuptools and wheel (and the copies bundled for ensurepip)
# vendor libraries with known CVEs and are not needed once the dependencies are installed (Trivy gate in CI).

FROM python:3.11-slim AS build
ENV PIP_NO_CACHE_DIR=1 PIP_DISABLE_PIP_VERSION_CHECK=1
WORKDIR /build
COPY requirements.txt .
RUN pip install --upgrade pip && pip install --target /opt/site -r requirements.txt

FROM python:3.11-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1

RUN pip uninstall -y pip setuptools wheel \
    && rm -rf /usr/local/lib/python3.11/ensurepip/_bundled \
    && useradd --system --uid 10001 aicos

WORKDIR /srv
COPY --from=build /opt/site /usr/local/lib/python3.11/site-packages
COPY app ./app

USER aicos

EXPOSE 8000
CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8000"]
