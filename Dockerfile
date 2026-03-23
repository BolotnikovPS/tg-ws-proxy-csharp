# syntax=docker/dockerfile:1
# Стадия build на нативной архитектуре билд-машины (BUILDPLATFORM), publish — кросс-сборка под TARGETARCH.
# Пример: docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY TgWsProxy.Domain/TgWsProxy.Domain.csproj TgWsProxy.Domain/
COPY TgWsProxy.Application/TgWsProxy.Application.csproj TgWsProxy.Application/
COPY TgWsProxy.Infrastructure/TgWsProxy.Infrastructure.csproj TgWsProxy.Infrastructure/
COPY TgWsProxy/TgWsProxy.csproj TgWsProxy/
COPY Directory.Packages.props /
COPY Directory.Build.props /

# arm64v8 / arm64-v8 / linux-arm64-v8 — варианты linux/arm64/v8; RID в .NET: linux-arm64
RUN case "$TARGETARCH" in \
      arm64|arm64v8|arm64-v8|linux-arm64-v8)  RID=linux-arm64 ;; \
      amd64)  RID=linux-x64 ;; \
      arm)    RID=linux-arm ;; \
      *)      RID=linux-${TARGETARCH} ;; \
    esac && \
    dotnet restore TgWsProxy/TgWsProxy.csproj -r "$RID"

COPY . .
WORKDIR /src/TgWsProxy
RUN case "$TARGETARCH" in \
      arm64|arm64v8|arm64-v8|linux-arm64-v8)  RID=linux-arm64 ;; \
      amd64)  RID=linux-x64 ;; \
      arm)    RID=linux-arm ;; \
      *)      RID=linux-${TARGETARCH} ;; \
    esac && \
    dotnet publish TgWsProxy.csproj -c Release -o /app/publish -r "$RID" --self-contained false --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TgWsProxy.dll"]
