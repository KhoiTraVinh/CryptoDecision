# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["CryptoDecision.ApiService/CryptoDecision.ApiService.csproj", "CryptoDecision.ApiService/"]
COPY ["CryptoDecision.Shared/CryptoDecision.Shared.csproj", "CryptoDecision.Shared/"]
RUN dotnet restore "CryptoDecision.ApiService/CryptoDecision.ApiService.csproj"
COPY . .
WORKDIR /src/CryptoDecision.ApiService
RUN dotnet publish -c Release -o /app/publish --no-restore
# ── Runtime stage ─────────────────────────────────────────────────────────────
# aspnet runtime is larger than dotnet/runtime but includes the ASP.NET Core libs.
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app

# dotnet/aspnet:9.0 (Debian Bookworm-slim) — curl is included, wget is not.
# Use wget for consistency with other services.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget \
    && rm -rf /var/lib/apt/lists/*

RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_ENVIRONMENT=Production
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=30s --retries=3 \
    CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "CryptoDecision.ApiService.dll"]
