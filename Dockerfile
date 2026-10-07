# =====================================================================
# =====================================================================
# Multi-stage Dockerfile für DatasheetAnalyzer.Blazor (.NET 8)
# ---------------------------------------------------------------------
# Stage 1 (build):   .NET 8 SDK – restore & publish
# Stage 2 (runtime): ASP.NET 8 Runtime – schlankes Deployment-Image
#
# Build:
#   docker build -t datasheet-analyzer:latest .
#
# Run:
#   docker run --rm -p 8080:8080 \
#     -e ASPNETCORE_ENVIRONMENT=Production \
#     datasheet-analyzer:latest
# =====================================================================

ARG DOTNET_VERSION=8.0

# ---------- Build-Stage ----------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

# nuget.config zuerst kopieren (Auth zum SHAPE-Feed)
COPY nuget.config ./

# Projektdateien für effizientes Layer-Caching
COPY DatasheetAnalyzer.Blazor/DatasheetAnalyzer.Blazor.csproj DatasheetAnalyzer.Blazor/
COPY DatasheetAnalyzer.Core/DatasheetAnalyzer.Core.csproj     DatasheetAnalyzer.Core/

RUN dotnet restore DatasheetAnalyzer.Blazor/DatasheetAnalyzer.Blazor.csproj

# Sourcen kopieren und publishen
COPY DatasheetAnalyzer.Blazor/ DatasheetAnalyzer.Blazor/
COPY DatasheetAnalyzer.Core/   DatasheetAnalyzer.Core/
COPY Azure_Prompts/            Azure_Prompts/

RUN dotnet publish DatasheetAnalyzer.Blazor/DatasheetAnalyzer.Blazor.csproj \
        -c Release \
        -o /app/publish \
        --no-restore \
        /p:UseAppHost=false \
        /p:PublishReadyToRun=false

# ---------- Runtime-Stage --------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

# curl für Healthcheck (aspnet-Image hat es nicht standardmäßig)
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl tzdata \
 && rm -rf /var/lib/apt/lists/*

# Non-root User anlegen
RUN groupadd --system --gid 1001 app \
 && useradd  --system --uid 1001 --gid app --home /app app

COPY --from=build /app/publish ./
COPY --from=build /src/Azure_Prompts ./Azure_Prompts

RUN chown -R app:app /app
USER app

ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTPS_PORT= \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    TZ=Europe/Berlin

EXPOSE 8080

# Healthcheck nutzt /health Endpoint aus Program.cs
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/health || exit 1

# OCI-Metadaten (werden von CI via --label überschrieben/ergänzt)
LABEL org.opencontainers.image.title="datasheet-analyzer" \
      org.opencontainers.image.description="Blazor-Server App zur Datenblatt-Analyse (DMS + Azure OpenAI)" \
      org.opencontainers.image.source="https://code.rsint.net/plm/mdmdev/cloud_systems/datasheet-analyzer" \
      org.opencontainers.image.vendor="Rohde & Schwarz"

ENTRYPOINT ["dotnet", "DatasheetAnalyzer.Blazor.dll"]
