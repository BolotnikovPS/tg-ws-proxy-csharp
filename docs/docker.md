# Docker

[← Оглавление](README.md)

`Dockerfile` рассчитан на **мультиархитектурную** сборку через BuildKit: этап `build` идёт на архитектуре билд-машины (`BUILDPLATFORM`), `dotnet publish` получает RID из `TARGETARCH` (`linux-arm64` для arm64), финальный слой берётся под целевую платформу. Контекст сборки — корень репозитория (скрипт healthcheck копируется из `DockerCompose/docker/healthcheck.sh`), поэтому путь к файлу передаётся флагом `-f`.

```bash
# только AArch64
docker buildx build -f Dockerfile --platform linux/arm64 -t tg-ws-proxy:arm64 --load .

# один тег для amd64 + arm64 в registry
docker buildx build -f Dockerfile --platform linux/amd64,linux/arm64 -t YOUR_REGISTRY/tg-ws-proxy:3.1.0 --push .

# на самом ARM-сервере, без эмуляции
docker build -f Dockerfile -t tg-ws-proxy:local .
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
cp config.docker.json config/config.json     # слушатели всегда 0.0.0.0
sudo chown -R 1654:1654 config               # UID пользователя app в образе
docker compose up -d

docker compose logs -f tg-ws-proxy
docker compose down
```

`command:` не передаёт флагов конфигурации — все настройки в `config.json`. Слушатели всегда привязаны ко всем интерфейсам (`0.0.0.0`), иначе проброшенные порты не дойдут до приложения. Панель (Web API и метрики) всегда слушает все интерфейсы на порту `80` и отдельно не настраивается. Переменные `TG_PORT` / `TG_WEB_PROXY_PORT` управляют только **публикацией** портов (`${TG_PORT:-1443}:1443`, `${TG_WEB_PROXY_PORT:-443}:8443`); порты внутри контейнера задают ключи `port` и `webProxy.port`.

Ссылки `t.me/proxy` в контейнере строятся из `publicHost`: слушатель привязан ко всем интерфейсам, поэтому внешний адрес подставить невозможно — задайте домен или публичный IP с опубликованным портом (`"publicHost": "proxy.example.com:1443"`) — в файле или прямо в панели, ключ применяется без перезапуска.

## WEB proxy в Docker

Bridge (browser-relay) включается ключом `webProxy.enabled` и, в отличие от `1443`/`80`, не объявлен через `EXPOSE` — его порт пробрасывается в `ports:` compose:

```yaml
- "${TG_WEB_PROXY_PORT:-443}:8443"   # снаружи 443, внутри 8443 (должно совпадать с webProxy.port)
```

Образ работает не от root, поэтому порты `<1024` внутри контейнера недоступны: слушайте непривилегированный порт (в образце — `8443`), а наружу публикуйте стандартный `443`. В `t.me/webproxy`-ссылке порт не участвует — он всегда `443`, так что NAT `443→8443` ничего не меняет для клиента.

Для bridge в `config/config.json` задайте:

- `webProxy.port: 8443` — правая часть `ports:` обязана совпадать с этим ключом (слушатель привязан ко всем интерфейсам);
- `webProxy.publicHost: "<домен>"` — внешний домен (например `pboldev.ru`), покрытый сертификатом;
- `webProxy.certPath` — путь **внутри контейнера**: файл `.pfx` монтируется compose'ом в `/app/certs` (read-only), поэтому указывайте `/app/certs/<файл>.pfx`, а не путь на хосте.

Том для сертификата прописан в compose: `../certs:/app/certs:ro` (каталог `certs` в корне репозитория). Если нужно слушать `443` и внутри контейнера — запустите сервис от root (`user: "0:0"`) либо добавьте `sysctls: [net.ipv4.ip_unprivileged_port_start=0]`.

## Конфигурация и состояние

В образе объявлен том **`/app/config`**, переменная `TGWS_CONFIG` указывает на `/app/config/config.json`:

- положите свой `config.json` в примонтированный каталог — прокси подхватит его при старте;
- изменения из панели пишутся **в этот же файл** и переживают `docker compose down` / `up`;
- если файла нет — прокси стартует на значениях по умолчанию.

`docker-compose.yml` монтирует `./config:/app/config` (bind-mount); в комментариях есть вариант с именованным томом.

Образ работает **не от root**, слушает `1443` (прокси), `80` (Web API/панель) и — при включённом WEB proxy — порт `webProxy.port`, а `HEALTHCHECK` опрашивает `/healthz/ready`. Интервал 30 с, стартовое окно 20 с, 3 повтора — этого достаточно, чтобы пережить короткое окно `Degraded` при перезапуске листенера.

**Дополнительные настройки** (`verbose`, `logs.path`, `port`) задавайте в `config/config.json` — это ключи «только старт». Остальное (`tokens`, CF Proxy, `fakeTlsDomain`, `webApiToken`, `allowIp`/`denyIp`, `maxConnections`, `rateLimitPerIp`) меняется и в панели.

> **Панель в контейнере.** Панель слушает все интерфейсы на порту `80`, поэтому **обязательно** задайте `webApiToken`: без него в лог уйдёт предупреждение, а метрики и правка конфигурации будут доступны любому, кто дотянется до порта. Порт `80` публикуйте только туда, где это допустимо, или не публикуйте вовсе и ходите через SSH-туннель.

## Логи в файл (`logs.path`)

По умолчанию (`logs.path` пустой) прокси пишет логи **только в stdout** — в контейнере они попадают в `docker compose logs` и далее в драйвер логирования Docker. Чтобы дополнительно складывать их в файл, задайте в блоке `logs` путь **внутри контейнера** (а не путём на хосте), например `/app/log/tgwsproxy.jsonl`. Ключ относится к «только старт»: правка применяется после перезапуска контейнера.

Serilog пишет файл **JSON-строками** (CompactJson), в консоль — как обычно. Ротация зависит от `logs.maxMegabytes`:

- `> 0` (по умолчанию `5`) — перекат по размеру файла; хранится `logs.retainedFileCount + 1` файлов (`logs.retainedFileCount` по умолчанию `1`, диапазон 1–1000);
- `0` — почасовая ротация (по имени файла интервалом в час).

Каталог с логом должен существовать и быть доступным на запись пользователю `app` (UID 1654). В образе создан только `/app/config`, поэтому под логи смонтируйте отдельный каталог хоста и отдайте его тому же UID. В `docker-compose.yml` раскомментируйте бинд-маунт:

```yaml
    volumes:
      - ./config:/app/config
      - ./log:/app/log          # каталог для logs.path
```

Подготовка на хосте:

```bash
cd DockerCompose
mkdir -p log
sudo chown -R 1654:1654 log
```

и в `config/config.json`:

```json
"logs": {
  "path": "/app/log/tgwsproxy.jsonl",
  "maxMegabytes": 20,
  "retainedFileCount": 5
}
```

После `docker compose up -d` лог появится в `./log/` на хосте. Следить можно за stdout (`docker compose logs -f -t tg-ws-proxy`) и за файлом (`tail -f log/tgwsproxy.jsonl`).
