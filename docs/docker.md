# Docker

[← Оглавление](README.md)

`Dockerfile` лежит в `Solution Items/` (в Solution Explorer он сгруппирован со служебными файлами) и рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64), финальный слой берётся под целевую платформу. Контекст сборки — корень репозитория (скрипт healthcheck копируется из `DockerCompose/docker/healthcheck.sh`), поэтому путь к файлу передаётся флагом `-f`.

```bash
# только AArch64 (Dockerfile лежит в Solution Items/)
docker buildx build -f "Solution Items/Dockerfile" --platform linux/arm64 -t tg-ws-proxy:arm64 --load .

# один тег для amd64 + arm64 в registry
docker buildx build -f "Solution Items/Dockerfile" --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:3.1.0 --push .

# на самом ARM-сервере, без эмуляции
docker build -f "Solution Items/Dockerfile" -t tg-ws-proxy:local .
```

## CI/CD (GitHub Actions)

Workflow [`.github/workflows/docker-build.yml`](../.github/workflows/docker-build.yml) срабатывает на пуш в `main`, на тег `v*` / `[0-9]*` и вручную:

1. гоняет `dotnet test` (сборка + тесты);
2. собирает мультиархитектурный образ `linux/amd64,linux/arm64` через BuildKit;
3. пушит в Docker Hub `pboldev/tg-ws-proxy`: пуш в `main` — тег `latest`; стабильный тег (например `3.1.0`) — теги `3.1.0` и `3.1`; prerelease (например `3.1.0-beta2`) — только `3.1.0-beta2`, стабильный `3.1` не перезаписывается. Теги веток не публикуются.

## Docker Compose

```bash
cd DockerCompose
mkdir -p config
cp config.docker.json config/config.json     # образец: host/metricsHost = 0.0.0.0
sudo chown -R 1654:1654 config               # UID пользователя app в образе
docker compose up -d

docker compose logs -f tg-ws-proxy
docker compose down
```

`command:` не передаёт флагов конфигурации — все настройки в `config.json`, включая `host`/`metricsHost: "0.0.0.0"`, иначе проброшенные порты не дойдут до приложения. Переменные `TG_PORT` / `TG_METRICS_PORT` управляют только **публикацией** портов (`${TG_PORT:-1443}:1443`); порты внутри контейнера задают ключи `port` и `metricsPort`.

Ссылки `t.me/proxy` в контейнере строятся из `publicHost`: `host` здесь служебный, поэтому задайте домен или публичный IP с опубликованным портом (`"publicHost": "proxy.example.com:1443"`) — в файле или прямо в панели, ключ применяется без перезапуска.

## Конфигурация и состояние

В образе объявлен том **`/app/config`**, переменная `TGWS_CONFIG` указывает на `/app/config/config.json`:

- положите свой `config.json` в примонтированный каталог — прокси подхватит его при старте;
- изменения из панели пишутся **в этот же файл** и переживают `docker compose down` / `up`;
- если файла нет — прокси стартует на значениях по умолчанию.

`docker-compose.yml` монтирует `./config:/app/config` (bind-mount); в комментариях есть вариант с именованным томом.

Образ работает **не от root**, слушает `1443` (прокси) и `8081` (Web API), а `HEALTHCHECK` опрашивает `/healthz/ready`. Интервал 30 с, стартовое окно 20 с, 3 повтора — этого достаточно, чтобы пережить короткое окно `Degraded` при перезапуске листенера.

**Дополнительные настройки** (`verbose`, `logPath`, `port`, `host`) задавайте в `config/config.json` — это ключи «только старт». Остальное (`tokens`, CF Proxy, `fakeTlsDomain`, `webApiToken`, `allowIp`/`denyIp`, `maxConnections`, `rateLimitPerIp`) меняется и в панели.

> **Панель в контейнере.** Образец `config.docker.json` слушает `0.0.0.0`, поэтому **обязательно** задайте `webApiToken`: без него в лог уйдёт предупреждение, а метрики и правка конфигурации будут доступны любому, кто дотянется до порта. Порт `8081` публикуйте только туда, где это допустимо, или не публикуйте вовсе и ходите через SSH-туннель.
