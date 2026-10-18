# syntax=docker/dockerfile:1
# Стадия build на нативной архитектуре билд-машины (BUILDPLATFORM), publish — кросс-сборка под TARGETARCH.
# Пример: docker buildx build --platform linux/arm64 -t tg-ws-proxy:arm64 --load .

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0 AS build
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
    dotnet restore TgWsProxy/TgWsProxy.csproj -r "$RID" -p:PublishReadyToRun=true

COPY . .
WORKDIR /src/TgWsProxy
RUN case "$TARGETARCH" in \
      arm64|arm64v8|arm64-v8|linux-arm64-v8)  RID=linux-arm64 ;; \
      amd64)  RID=linux-x64 ;; \
      arm)    RID=linux-arm ;; \
      *)      RID=linux-${TARGETARCH} ;; \
    esac && \
    dotnet publish TgWsProxy.csproj -c Release -o /app/publish -r "$RID" --self-contained false --no-restore -p:PublishReadyToRun=true

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS final
WORKDIR /app

# curl/wget не нужны: HEALTHCHECK реализован на встроенном bash-механизме /dev/tcp,
# поэтому в образе нет apt-слоя с пакетами для проверки (F-6.5).

COPY --from=build /app/publish .

# Скрипт лежит рядом с compose-файлами; контекст сборки — корень репозитория (F-6.5).
COPY DockerCompose/docker/healthcheck.sh /usr/local/bin/healthcheck.sh
RUN chmod +x /usr/local/bin/healthcheck.sh

# Каталог конфигурации — том, доступный непривилегированному пользователю на запись (F-6.1, F-6.3).
# Права важны: сохранение конфигурации из UI пишет временный файл рядом и переименовывает его.
RUN mkdir -p /app/config && chown -R app:app /app/config

# Путь к конфигурации читается приложением из TGWS_CONFIG, если не задан --config (F-6.1).
ENV TGWS_CONFIG=/app/config/config.json \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

VOLUME ["/app/config"]

# 1443 — прокси, 8081 — Web API и информационная панель (F-6.4).
EXPOSE 1443
EXPOSE 8081

# Непривилегированный пользователь из базового образа (F-6.3).
USER $APP_UID

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/usr/local/bin/healthcheck.sh"]

ENTRYPOINT ["dotnet", "TgWsProxy.dll"]
