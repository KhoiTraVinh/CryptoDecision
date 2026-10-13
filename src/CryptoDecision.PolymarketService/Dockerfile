# ── Build stage ──────────────────────────────────────────────────────────────
# Cross-compiled, not emulated — same reasoning as the other services in this
# solution: --platform=$BUILDPLATFORM keeps restore and publish native on the build
# machine, and only the runtime stage below is the target architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY ["CryptoDecision.PolymarketService/CryptoDecision.PolymarketService.csproj", "CryptoDecision.PolymarketService/"]
RUN dotnet restore -a $TARGETARCH "CryptoDecision.PolymarketService/CryptoDecision.PolymarketService.csproj"
COPY . .
WORKDIR /src/CryptoDecision.PolymarketService
RUN dotnet publish -c Release -a $TARGETARCH -o /app/publish --no-restore

# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV DOTNET_ENVIRONMENT=Production

# No port and no health endpoint. This service talks only outbound — Gamma, the CLOB
# and the Data API — and keeps no state worth probing. Liveness is the log: a healthy
# bot prints one "Cycle:" line every ScanIntervalSeconds, whether or not it trades.
ENTRYPOINT ["dotnet", "CryptoDecision.PolymarketService.dll"]
