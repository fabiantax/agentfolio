# syntax=docker/dockerfile:1
# AgentFolio.Api — production container (#100).
# Multi-stage, non-root, framework-dependent publish; the sqlite-vec vec0.so
# ships in the publish output via the runtimes/linux-x64/native NuGet asset
# (verified in the local publish dir before wiring). Static UI (wwwroot) is
# tracked in-repo — no node/bun stage needed.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# csproj layer first for cache: package restore survives source edits.
COPY Directory.Packages.props ./Directory.Packages.props
COPY Directory.Build.props ./Directory.Build.props
COPY src/AgentFolio.Contracts/AgentFolio.Contracts.csproj src/AgentFolio.Contracts/
COPY src/AgentFolio.Platform/AgentFolio.Platform.csproj src/AgentFolio.Platform/
COPY src/AgentFolio.Agents/AgentFolio.Agents.csproj src/AgentFolio.Agents/
COPY src/AgentFolio.Api/AgentFolio.Api.csproj src/AgentFolio.Api/
COPY src/Slices/Chat/Slices.Chat.csproj src/Slices/Chat/
COPY src/Slices/Tenancy/Slices.Tenancy.csproj src/Slices/Tenancy/
COPY src/Slices/Auth2/Slices.Auth2.csproj src/Slices/Auth2/
COPY src/Slices/Uploads/Slices.Uploads.csproj src/Slices/Uploads/
COPY src/Slices/Share/Slices.Share.csproj src/Slices/Share/
COPY src/Slices/Stars/Slices.Stars.csproj src/Slices/Stars/
RUN dotnet restore src/AgentFolio.Api/AgentFolio.Api.csproj
COPY src/ ./src/
RUN dotnet publish src/AgentFolio.Api/AgentFolio.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
# curl for the HEALTHCHECK — aspnet:10.0 ships neither curl nor wget.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
USER app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -sf http://127.0.0.1:8080/healthz || exit 1
ENTRYPOINT ["dotnet", "AgentFolio.Api.dll"]
