# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/HopperJobQueue.Api/HopperJobQueue.Api.csproj src/HopperJobQueue.Api/
RUN dotnet restore src/HopperJobQueue.Api/HopperJobQueue.Api.csproj

COPY src/ src/
RUN dotnet publish src/HopperJobQueue.Api/HopperJobQueue.Api.csproj -c Release -o /app/publish

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# curl sert uniquement au HEALTHCHECK
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app/publish .

# Pas de root dans le conteneur
USER $APP_UID

# Traefik termine TLS ; le conteneur n'écoute qu'en HTTP interne
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

ENTRYPOINT ["dotnet", "HopperJobQueue.Api.dll"]
