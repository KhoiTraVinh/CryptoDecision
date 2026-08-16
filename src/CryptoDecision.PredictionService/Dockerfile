# ── Build / dependency stage ──────────────────────────────────────────────────
FROM python:3.12-slim AS build
WORKDIR /install
COPY requirements.txt .
RUN pip install --no-cache-dir --prefix=/install/deps -r requirements.txt

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM python:3.12-slim AS runtime
WORKDIR /app

# Copy pre-installed deps from build stage
COPY --from=build /install/deps /usr/local

# Copy application source
COPY prediction_service/ ./prediction_service/

# Create non-root user and the model persistence directory.
# Must be done as root BEFORE switching to appuser so chown works.
RUN addgroup --system appgroup \
 && adduser --system --ingroup appgroup appuser \
 && mkdir -p /app/models \
 && chown appuser:appgroup /app/models

USER appuser

ENV PYTHONUNBUFFERED=1
ENV PYTHONDONTWRITEBYTECODE=1
EXPOSE 8080 9091

# Use Python's stdlib urllib — no wget/curl needed
HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD python -c "import urllib.request; urllib.request.urlopen('http://localhost:8080/health', timeout=5)" || exit 1

ENTRYPOINT ["python", "-m", "prediction_service.main"]
