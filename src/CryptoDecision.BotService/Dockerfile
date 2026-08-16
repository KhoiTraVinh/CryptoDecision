# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CryptoDecision.BotService/CryptoDecision.BotService.csproj", "CryptoDecision.BotService/"]
COPY ["CryptoDecision.Shared/CryptoDecision.Shared.csproj", "CryptoDecision.Shared/"]
RUN dotnet restore "CryptoDecision.BotService/CryptoDecision.BotService.csproj"
COPY . .
WORKDIR /src/CryptoDecision.BotService
RUN dotnet publish -c Release -o /app/publish --no-restore
# ── Runtime stage ─────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/runtime:9.0 AS runtime
WORKDIR /app

# Install wget for the HEALTHCHECK directive.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV DOTNET_ENVIRONMENT=Production
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CryptoDecision.BotService.dll"]
