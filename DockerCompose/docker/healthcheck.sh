#!/bin/sh
# HEALTHCHECK образа tg-ws-proxy (F-6.5).
#
# Спрашиваем readiness, а не liveness и не TCP-порт:
#   /healthz/ready → 200 Healthy     — листенер прокси слушает, подсистемы живы;
#   /healthz/ready → 503 Degraded    — идёт перезапуск слушателя (F-7.7),
#                                      окно ≤10 с перекрывается --retries=3;
#   /healthz/ready → 503 Unhealthy   — листенер мёртв, контейнер нездоров.
#
# TCP-фолбэк удалён вместе с ветвью «web отключён» (F-1.4): открытый порт ничего
# не говорит о том, что листенер прокси принимает соединения.
#
# Переопределяемые переменные:
#   TGWS_HEALTHCHECK_HOST — адрес проверки (по умолчанию 127.0.0.1)
#   TGWS_HEALTHCHECK_PORT — порт Web API (по умолчанию 8081)
set -u

host="${TGWS_HEALTHCHECK_HOST:-127.0.0.1}"
port="${TGWS_HEALTHCHECK_PORT:-8081}"

response=$(curl --silent --show-error --max-time 4 \
    --write-out '\n%{http_code}' \
    "http://${host}:${port}/healthz/ready" 2>/dev/null)
curl_status=$?

if [ "$curl_status" -ne 0 ]; then
    printf 'healthz/ready: curl завершился с кодом %s\n' "$curl_status" >&2
    exit 1
fi

code=$(printf '%s\n' "$response" | tail -n 1)
if [ "$code" = "200" ]; then
    exit 0
fi

printf 'healthz/ready: HTTP %s — слушатель прокси не готов\n' "$code" >&2
exit 1
