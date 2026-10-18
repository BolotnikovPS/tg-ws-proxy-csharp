#!/bin/bash
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
# Реализация — встроенный bash-механизм /dev/tcp: внешние утилиты (curl/wget)
# не нужны, поэтому в образе нет apt-слоя с curl (F-6.5).
#
# Переопределяемые переменные:
#   TGWS_HEALTHCHECK_HOST    — адрес проверки (по умолчанию 127.0.0.1)
#   TGWS_HEALTHCHECK_PORT    — порт Web API (по умолчанию 8081)
#   TGWS_HEALTHCHECK_TIMEOUT — таймаут чтения статусной строки, сек (по умолчанию 4)
set -u

host="${TGWS_HEALTHCHECK_HOST:-127.0.0.1}"
port="${TGWS_HEALTHCHECK_PORT:-8081}"
timeout="${TGWS_HEALTHCHECK_TIMEOUT:-4}"

# Открыть TCP-соединение на чтение/запись; дескриптор 3.
if ! exec 3<>"/dev/tcp/${host}/${port}" 2>/dev/null; then
    printf 'healthz/ready: не удалось подключиться к %s:%s\n' "$host" "$port" >&2
    exit 1
fi

# Минимальный HTTP/1.0-запрос; Kestrel закрывает соединение после ответа.
printf 'GET /healthz/ready HTTP/1.0\r\nHost: %s\r\nConnection: close\r\n\r\n' "$host" >&3

# Прочитать только статусную строку (Kestrel отвечает мгновенно).
IFS= read -r -t "$timeout" status <&3 || status=""
exec 3<&- 3>&-

status="${status%$'\r'}"
case "$status" in
    *" 200"*)
        exit 0
        ;;
    *)
        printf 'healthz/ready: %s — слушатель прокси не готов\n' "${status:-нет ответа}" >&2
        exit 1
        ;;
esac
