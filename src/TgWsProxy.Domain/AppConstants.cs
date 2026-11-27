namespace TgWsProxy.Domain;

/// <summary>
/// Единый источник истины для всех констант решения.
/// </summary>
/// <remarks>
/// Все числовые и строковые константы продуктового кода собраны здесь, чтобы значения
/// не разъезжались между слоями и файлами: домены, таймауты, лимиты, имена заголовков,
/// content-type-ы, пути, префиксы протоколов и magic-числа бинарных форматов.
/// Группы — вложенные статические классы по подсистемам; внутри группы имена совпадают
/// с прежними именами полей, поэтому файлы-потребители подключают нужную группу через
/// <c>using static TgWsProxy.Domain.AppConstants.&lt;Группа&gt;;</c> и обращаются к константам
/// по короткому имени, а из других подсистем — квалифицированно.
/// </remarks>
public static class AppConstants
{
    /// <summary>
    /// HTTP-контракт панели: content-type-ы ответов, запрет кэширования, схема авторизации.
    /// </summary>
    public static class Http
    {
        /// <summary>Content-Type для <c>ProblemDetails</c>-ответов.</summary>
        public const string ProblemContentType = "application/problem+json";

        /// <summary>Content-Type для обычных JSON-ответов с ручными заголовками.</summary>
        public const string JsonContentType = "application/json; charset=utf-8";

        /// <summary>
        /// Запрет кэширования ответов, содержащих секрет токена в открытом виде (F-8.8).
        /// </summary>
        public const string NoStoreHeader = "no-store";

        public const string BearerScheme = "Bearer ";

    }

    /// <summary>
    /// Telegram Bot API: заголовки логина бота, лимиты очередей, параметры скачивания файлов и разметки.
    /// </summary>
    public static class BotApi
    {
        /// <summary>Заголовок с api_id (обязателен на первом логине бота).</summary>
        public const string ApiIdHeader = "X-Telegram-Api-Id";

        /// <summary>Заголовок с api_hash (обязателен на первом логине бота).</summary>
        public const string ApiHashHeader = "X-Telegram-Api-Hash";

        /// <summary>
        /// Подстановочный thumb_size для фото-file_id старого формата (type=2): type размера в нём
        /// не сохранён, а с пустой строкой upload.getFile отвечает FILE_REFERENCE_EXPIRED.
        /// </summary>
        public const string LegacyPhotoThumbSize = "x";

        /// <summary>Потолок очереди на бота: защита от клиента, который вообще не вызывает getUpdates.</summary>
        public const int MaxPendingUpdates = 10_000;

        public const string FloodWaitPrefix = "FLOOD_WAIT_";

        public const int FileChunkBytes = 512 * 1024;

        /// <summary>Максимальная глубина вложенности разметки Markdown (защита от патологического входа).</summary>
        public const int MaxMarkdownDepth = 16;

        public const string MentionPrefix = "tg://user?id=";

        /// <summary>Предел вложенности rich-блоков при «сплющивании» в текст: защита от циклов.</summary>
        public const int MaxRichTextDepth = 8;

    }

    /// <summary>
    /// Формат файла сохранённой сессии бота (magic и номера версий).
    /// </summary>
    public static class BotSession
    {
        public const uint Magic = 0x53544F42; // "BOTS" в little-endian

        public const int Version = 3;

        public const int VersionWithoutApiId = 1;

        public const int VersionWithoutUpdateId = 2;

    }

    /// <summary>
    /// Константы протокола MTProto/WebSocket, выровненные с upstream <c>proxy/utils.py</c>.
    /// </summary>
    public static class Protocol
    {
        /// <summary>Длина MTProto handshake (HANDSHAKE_LEN).</summary>
        public const int HandshakeLen = 64;

        /// <summary>Смещение prekey+iv внутри handshake (SKIP_LEN).</summary>
        public const int SkipLen = 8;

        /// <summary>Длина prekey (PREKEY_LEN).</summary>
        public const int PreKeyLen = 32;

        /// <summary>Длина ключа AES (KEY_LEN).</summary>
        public const int KeyLen = 32;

        /// <summary>Длина IV/counter (IV_LEN).</summary>
        public const int IvLen = 16;

        /// <summary>Смещение proto tag в handshake (PROTO_TAG_POS).</summary>
        public const int ProtoTagPos = 56;

        /// <summary>Смещение dc_idx в handshake (DC_IDX_POS).</summary>
        public const int DcIdxPos = 60;

        /// <summary>Abridged-протокол (PROTO_ABRIDGED_INT).</summary>
        public const int ProtoAbridgedInt = unchecked((int)0xEFEFEFEF);

        /// <summary>Intermediate-протокол (PROTO_INTERMEDIATE_INT).</summary>
        public const int ProtoIntermediateInt = unchecked((int)0xEEEEEEEE);

        /// <summary>Padded intermediate-протокол (PROTO_PADDED_INTERMEDIATE_INT).</summary>
        public const int ProtoPaddedIntermediateInt = unchecked((int)0xDDDDDDDD);

        /// <summary>Путь WebSocket-эндпоинта (WS_PATH).</summary>
        public const string WsPath = "/apiws";

        /// <summary>Путь тестового WebSocket-эндпоинта (WS_PATH_TEST).</summary>
        public const string WsPathTest = "/apiws_test";

        /// <summary>Порт WebSocket-эндпоинта Telegram.</summary>
        public const int WsPort = 443;

        /// <summary>Порт WebSocket-эндпоинта при <c>--no-secure</c> (обычный ws://).</summary>
        public const int WsPlainPort = 80;

        /// <summary>Максимальная длина WS-сообщения (MAX_MESSAGE_LEN, 16 МиБ).</summary>
        public const int MaxMessageLen = 16 * 1024 * 1024;

        /// <summary>Путь HTTP/2-эндпоинта Cloudflare (CF /api).</summary>
        public const string H2ApiPath = "/api";

        /// <summary>Максимальный размер MTProto-пакета в HTTP/2-транспорте (MAX_PACKET, 4 МиБ).</summary>
        public const int H2MaxPacket = 4 * 1024 * 1024;

        /// <summary>Максимальный размер случайного padding в padded-пакете (MAX_PLAIN_PADDING).</summary>
        public const int H2MaxPlainPadding = 256;

    }

    /// <summary>
    /// RPC-клиент MTProto: очереди подтверждений, keepalive, размеры конверта и загрузки файлов.
    /// </summary>
    public static class MtProto
    {
        /// <summary>Максимальная длина строки PROXY protocol v1 (спецификация: 107 байт).</summary>
        public const int MaxProxyHeaderLength = 108;

        /// <summary>Предел очереди подтверждений: защита от роста, если ack'и почему-то не отправляются.</summary>
        public const int MaxPendingAcks = 4096;

        /// <summary>Пауза молчания, после которой перед RPC отправляется ping_delay_disconnect.</summary>
        public const int KeepAliveIdleMs = 30_000;

        /// <summary>Сколько секунд сервер должен держать соединение после ping_delay_disconnect.</summary>
        public const int KeepAliveDisconnectDelaySeconds = 300;

        public const int AuthKeyPartSize = 32;

        public const int EnvelopeHeaderSize = 32; // salt..message_data_length

        /// <summary>Максимальный размер части upload.saveFilePart — 512 КиБ (ограничение Telegram).</summary>
        public const int MaxFilePartSize = 512 * 1024;

        /// <summary>Файлы крупнее 10 МиБ загружаются через upload.saveBigFilePart (InputFileBig).</summary>
        public const long BigFileThreshold = 10L * 1024 * 1024;

    }

    /// <summary>
    /// TL-схема: версия слоя и ctor-id служебных объектов.
    /// </summary>
    public static class Tl
    {
        public const int Version = 229;                 // fetched 08/26/2026 00:30:42

        public const uint VectorCtor = 0x1CB5C415;

        public const uint NullCtor = 0x56730BCC;

        public const uint RpcResultCtor = 0xF35C6D01;

        public const uint RpcErrorCtor = 0x2144CA19;

        public const uint MsgContainerCtor = 0x73F1F8DC;

        public const uint BadMsgCtor = 0xA7EFF811;

        public const uint GZipedCtor = 0x3072CFA1;

    }

    /// <summary>
    /// Fake-TLS: типы записей, смещения ClientHello/ServerHello, маскирующий прокси.
    /// </summary>
    public static class FakeTls
    {
        /// <summary>Тип записи TLS «handshake» (TLS_RECORD_HANDSHAKE).</summary>
        public const byte RecordHandshake = 0x16;

        /// <summary>Тип записи TLS «change cipher spec» (TLS_RECORD_CCS).</summary>
        public const byte RecordChangeCipherSpec = 0x14;

        /// <summary>Тип записи TLS «application data» (TLS_RECORD_APPDATA).</summary>
        public const byte RecordApplicationData = 0x17;

        /// <summary>Смещение client_random в ClientHello (CLIENT_RANDOM_OFFSET).</summary>
        public const int ClientRandomOffset = 11;

        /// <summary>Длина client_random / server_random.</summary>
        public const int RandomLength = 32;

        /// <summary>Смещение session id в ClientHello (SESSION_ID_OFFSET).</summary>
        public const int SessionIdOffset = 44;

        /// <summary>Длина session id.</summary>
        public const int SessionIdLength = 32;

        /// <summary>Допустимое отклонение метки времени, секунды (TIMESTAMP_TOLERANCE).</summary>
        public const int TimestampToleranceSeconds = 120;

        /// <summary>Максимальный размер полезной нагрузки одной TLS-записи (TLS_APPDATA_MAX).</summary>
        public const int ApplicationDataMax = 16384;

        /// <summary>Минимальная длина ClientHello для проверки: 5 + 6 + 32.</summary>
        public const int MinClientHelloLength = 43;

        /// <summary>Смещение server_random в ServerHello (_SH_RANDOM_OFF).</summary>
        public const int ServerHelloRandomOffset = 11;

        /// <summary>Расширение «supported_versions» (EXT_SUPPORTED_VERSIONS).</summary>
        public const ushort ExtensionSupportedVersions = 0x002b;

        /// <summary>Расширение «key_share» (EXT_KEY_SHARE).</summary>
        public const ushort ExtensionKeyShare = 0x0033;

        /// <summary>Именованная группа X25519 (GROUP_X25519).</summary>
        public const ushort GroupX25519 = 0x001d;

        /// <summary>Гибридная пост-квантовая группа X25519MLKEM768 (GROUP_X25519MLKEM768).</summary>
        public const ushort GroupX25519MlKem768 = 0x11ec;

        /// <summary>Длина публичного ключа X25519 (X25519_KEY_LEN).</summary>
        public const int X25519KeyLength = 32;

        /// <summary>Длина клиентского share в X25519MLKEM768, которую мы зеркалим.</summary>
        public const int X25519MlKem768ClientShareLength = 1216;

        /// <summary>Длина «шифртекста» MLKEM768 в ответном share (MLKEM768_CIPHERTEXT_LEN).</summary>
        public const int MlKem768CiphertextLength = 1088;

        /// <summary>Нижняя граница размера единственной записи ApplicationData (APPDATA_MIN).</summary>
        public const int AppDataMinLength = 64;

        /// <summary>Нижняя граница размера записи без профиля сайта (FALLBACK_APPDATA_RANGE).</summary>
        public const int FallbackAppDataMinLength = 64;

        /// <summary>
        /// Верхняя граница размера записи без профиля сайта. Держимся у 64-байтовой записи,
        /// с которой telemt прошёл DPI, и далеко от 1900-2100 байт классического ответа MTProxy.
        /// </summary>
        public const int FallbackAppDataMaxLength = 160;

        /// <summary>Минимальная пауза перед отправкой ServerHello, мс (SERVER_HELLO_DELAY).</summary>
        public const int ServerHelloDelayMinMs = 8;

        /// <summary>Максимальная пауза перед отправкой ServerHello, мс.</summary>
        public const int ServerHelloDelayMaxMs = 24;

        /// <summary>Таймаут замера профиля у сайта маскировки, с (PROFILE_PROBE_TIMEOUT).</summary>
        public const int ProfileProbeTimeoutSeconds = 5;

        /// <summary>Размер буфера пересылки.</summary>
        public const int BufferSize = 16384;

        /// <summary>Порт домена маскировки.</summary>
        public const int MaskingPort = 443;

    }

    /// <summary>
    /// Бинарный кодек кадров HTTP/2-транспорта.
    /// </summary>
    public static class Wire
    {
        /// <summary>Минимальный допустимый размер MTProto-пакета.</summary>
        public const int MinPacketLength = 24;

    }

    /// <summary>
    /// Носитель WEB proxy: пределы тел carrier-API, политика Permissions-Policy, префиксы WS-подпротоколов.
    /// </summary>
    public static class Carrier
    {
    /// <summary>Предел тела <c>POST /session</c> — единственный HELLO-кадр (8 + 1 байт).</summary>
    public const int MaxCreateBodyBytes = 64;

    /// <summary>Предел тела <c>POST /up</c> (2 МиБ).</summary>
    public const int MaxBodyBytes = 2 * 1024 * 1024;

    /// <summary>Предел нисходящего батча и <c>batchLimit</c> bridge-страницы (2 МиБ).</summary>
    public const int CarrierBatchBytes = 2 * 1024 * 1024;

    /// <summary>Политика Permissions-Policy для bridge-страницы.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), clipboard-read=(), clipboard-write=(), display-capture=(), encrypted-media=(), fullscreen=(), geolocation=(), gyroscope=(), hid=(), idle-detection=(), magnetometer=(), microphone=(), midi=(), payment=(), picture-in-picture=(), publickey-credentials-create=(), publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), web-share=(), xr-spatial-tracking=()";

    /// <summary>Префикс WS-подпротокола носителя (<c>Sec-WebSocket-Protocol</c>).</summary>
    public const string WebSocketProtocolPrefix = "tproxy-v1.";

    /// <summary>Префикс WS-подпротокола «полосы» носителя.</summary>
    public const string WebSocketLaneProtocolPrefix = "tproxy-lane-v1.";

    }

    /// <summary>
    /// WEB proxy: кадры relay-протокола, capability-контекст и ссылки-приглашения.
    /// </summary>
    public static class WebProxy
    {
        /// <summary>Префикс контекста v1 (без <c>basePath</c>).</summary>
        public const string ContextV1Prefix = "tdesktop-web-proxy-bridge-v1\n";

        /// <summary>Префикс контекста v2 (с <c>basePath</c>).</summary>
        public const string ContextV2Prefix = "tdesktop-web-proxy-bridge-v2\n";

        /// <summary>Максимальная длина <c>basePath</c> после нормализации.</summary>
        public const int MaxBasePathLength = 128;

        /// <summary>Длина capability в байтах (HMAC-SHA256).</summary>
        public const int CapabilityByteLength = 32;

        /// <summary>Размер заголовка кадра в байтах (<c>kFrameHeaderSize</c>).</summary>
        public const int FrameHeaderSize = 8;

        /// <summary>Максимальный размер payload кадра (<c>kMaxFramePayload</c> = 1 МиБ).</summary>
        public const int MaxFramePayload = 1024 * 1024;

        /// <summary>Максимум кадров в одном пакете (<c>kMaxBatchFrames</c>).</summary>
        public const int MaxBatchFrames = 4096;

        /// <summary>Начальное приёмное окно потока (<c>kInitialStreamWindow</c> = 4 МиБ).</summary>
        public const int InitialStreamWindow = 4 * 1024 * 1024;

        /// <summary>Максимальное значение <c>streamId</c> (24-битное поле).</summary>
        public const int MaxStreamId = 0x00FF_FFFF;

        /// <summary>Маркер секрета ссылки WEB proxy (<c>kWebProxyLinkSecretMarker</c>).</summary>
        public const byte WebProxyLinkSecretMarker = 0x70;

        /// <summary>Единственный допустимый payload кадра <see cref="RelayFrameType.Hello"/> — версия протокола 1.</summary>
        public const byte HelloProtocolVersion = 0x01;

        /// <summary>Размер payload кадра <see cref="RelayFrameType.Window"/> (BE uint32).</summary>
        public const int WindowCreditSize = 4;

        /// <summary>Схема ссылки-приглашения, которую понимает Telegram (SPEC §6).</summary>
        public const string ShareLinkPrefix = "https://t.me/webproxy";

        /// <summary>Эквивалентная схема deep-link для клиента (не зарегистрирована в t.me).</summary>
        public const string DeepLinkPrefix = "tg://webproxy";

        /// <summary>Максимальная длина имени хоста (как в <c>NormalizeWebProxyHost</c> клиента).</summary>
        public const int MaxHostLength = 253;

    }

    /// <summary>
    /// Прямой WebSocket-транспорт: коалесинг кадров, таймауты и SNI для domain fronting.
    /// </summary>
    public static class Ws
    {
        /// <summary>
        /// Предельный суммарный размер пачки кадров, которые склеиваются в один write.
        /// Выше этого порога кадры отправляются по одному, чтобы не арендовать
        /// крупный буфер из пула.
        /// </summary>
        public const int MaxCoalescedBatchBytes = 256 * 1024;

        /// <summary>Верхняя граница на TCP+TLS фазу (как upstream <c>min(timeout, 10)</c>).</summary>
        public const double TcpTlsHandshakeCapSeconds = 10;

        /// <summary>Максимальный размер блока чтения заголовков ответа.</summary>
        public const int HeaderReadChunk = 8 * 1024;

        /// <summary>SNI для domain fronting (upstream использует этот домен вместо реального).</summary>
        public const string FrontingSni = "sprinthost.ru";

        /// <summary>Таймаут подключения в режиме fronting (секунды).</summary>
        public const int FrontedTimeoutSeconds = 7;

        /// <summary>Таймаут обычного подключения (секунды).</summary>
        public const int PlainTimeoutSeconds = 8;

        /// <summary>
        /// Таймаут ожидания закрытия соединения при <c>Close</c> (CLOSE_TIMEOUT, 2 с).
        /// Дренаж close-кадра и ожидание <c>wait_closed</c> не должны висеть вечно.
        /// </summary>
        public const double CloseTimeoutSeconds = 2.0;

        /// <summary>
        /// Таймаут отправки relay-init при старте сессии (upstream <c>send_init</c>, 10 с).
        /// Отличие от обычного <c>send</c>: по истечении соединение аварийно закрывается,
        /// иначе подвисшая отправка держала бы сессию и сокет.
        /// </summary>
        public const double SendInitTimeoutSeconds = 10.0;

    }

    /// <summary>
    /// HTTP/2-канал Cloudflare: пределы запросов, окно replay и диагностика.
    /// </summary>
    public static class CfH2Channel
    {
        public const int MaxChannelRequests = 8;

        public const long MaxChannelBytes = 8L * 1024 * 1024;

        public const int QueueCapacity = 16;

        public const int RequestTimeoutSeconds = 65;

        public const double ReplayIdleSeconds = 1.0;

        public const double ReplayRequestSeconds = 3.0;

        public const double ReplayCheckSeconds = 0.25;

        public const double ReplayMaxAge = 30.0;

        public const int ReplayHistoryBytes = 64 * 1024;

        public const int ReplayHistoryPackets = 16;

        public const int MaxChannelReplays = 2;

        public const double ReplaySlotSeconds = 30.0;

        public const double ReplayRetrySeconds = 2.0;

        public const int ReplayMaxAttempts = 3;

        public const double DiagnosticIntervalSeconds = 5.0;

    }

    /// <summary>
    /// HTTP/2 «полоса» Cloudflare: пределы, таймауты и охлаждение доменов.
    /// </summary>
    public static class CfH2Lane
    {
        public const int MaxLaneRequests = 64;

        public const long MaxLaneBytes = 32L * 1024 * 1024;

        public const long MaxLaneReplyBytes = 32L * 1024 * 1024;

        public const double DomainCooldownSeconds = 30.0;

        public const int RequestTimeoutSeconds = 65;

        public const int ConnectTimeoutSeconds = 8;

        public const int PreflightTimeoutSeconds = 10;

        public const double SlowResponseSeconds = 3.0;

        /// <summary>Размер буфера чтения тела H2-ответа (арендуется в пуле на запрос).</summary>
        public const int ReadChunkBytes = 64 * 1024;

    }

    /// <summary>
    /// Пул HTTP/2-полос Cloudflare.
    /// </summary>
    public static class CfH2Pool
    {
        public const double DomainCooldownSeconds = 30.0;

        public const double SetupTimeoutSeconds = 8.0;

    }

    /// <summary>
    /// Пул воркеров Cloudflare по дата-центрам.
    /// </summary>
    public static class CfWorkerPool
    {
        /// <summary>Максимум соединений на один DC (PER_DC_LIMIT).</summary>
        public const int PerDcLimit = 1;

        /// <summary>Таймаут подключения к воркеру (секунды).</summary>
        public const int ConnectTimeoutSeconds = 8;

        /// <summary>Максимальный возраст соединения воркера в пуле, с (WS_POOL_MAX_AGE).</summary>
        public const int MaxAgeSeconds = 100;

        /// <summary>Период обслуживания пула воркеров, с (WS_POOL_CHECK_INTERVAL).</summary>
        public const double CheckIntervalSeconds = 5.0;

        /// <summary>Начальная задержка backoff наполнения, с (REFILL_BACKOFF_INITIAL).</summary>
        public const double RefillBackoffInitialSeconds = 1.0;

        /// <summary>Максимальная задержка backoff наполнения, с (REFILL_BACKOFF_MAX).</summary>
        public const double RefillBackoffMaxSeconds = 3600.0;

        /// <summary>
        /// Охлаждение воркера после транспортной ошибки, с (FAILURE_COOLDOWN).
        /// Покрывает HTTP 5xx и сетевые ошибки; HTTP 429 — отдельный случай (лимит free-плана).
        /// </summary>
        public const double FailureCooldownSeconds = 60.0;

    }

    /// <summary>
    /// Стратегия подключения к Cloudflare по WebSocket (порт upstream <c>proxy/cf_websocket.py</c>).
    /// Охлаждение пары «домен, TLS» после таймаута и лестница таймаутов попыток.
    /// </summary>
    public static class CfWebSocket
    {
        /// <summary>
        /// Охлаждение пары (домен, use_tls) после таймаута, с (CF_TIMEOUT_COOLDOWN).
        /// Пока пара в охлаждении, к ней не возвращаются — вместо этого чередуется режим TLS.
        /// </summary>
        public const double TimeoutCooldownSeconds = 30.0;

        /// <summary>
        /// Лестница таймаутов попыток (CF_CONNECT_TIMEOUTS). Каждый таймаут переключает
        /// режим TLS и выдаёт следующей попытке больший лимит; длина массива — предел числа
        /// последовательных таймаутов.
        /// </summary>
        public static readonly double[] ConnectTimeoutSeconds = [3.0, 3.0, 5.0, 5.0, 10.0];

        /// <summary>Таймаут подключения по умолчанию для CF-доменов (секунды).</summary>
        public const double DefaultConnectTimeoutSeconds = 10.0;

    }

    /// <summary>
    /// Фоновое обновление списка доменов Cloudflare.
    /// </summary>
    public static class CfDomainRefresher
    {
        /// <summary>URL со списком закодированных доменов (CFPROXY_DOMAINS_URL).</summary>
        public const string DomainsUrl =
            "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";

        /// <summary>Минимальное число валидных доменов, чтобы принять новый список.</summary>
        public const int MinValidDomains = 3;

    }

    /// <summary>
    /// Слушатели и TCP-fallback: проверка живости, пауза пересоздания, буферы чтения.
    /// </summary>
    public static class Server
    {
        /// <summary>Период проверки живости слушающего сокета (порт upstream <c>LISTENER_CHECK_INTERVAL</c>).</summary>
        public const double ListenerCheckIntervalSeconds = 5.0;

        /// <summary>Пауза перед пересозданием слушателя (порт upstream <c>LISTENER_RESTART_DELAY</c>).</summary>
        public const int ListenerRestartDelayMs = 1000;

        /// <summary>Размер буфера чтения от клиента (как 65536 в upstream <c>bridge.py</c>).</summary>
        public const int ClientReadBufferSize = 65536;

        /// <summary>
        /// Период опроса сокета клиента, пока идёт дозвон в fallback: если клиент ушёл,
        /// дозвон отменяется (порт upstream <c>client_setup</c> в <c>event_loop.py</c>).
        /// </summary>
        public const int ClientDisconnectPollMs = 200;

    }

    /// <summary>
    /// Проверка доступности дата-центров.
    /// </summary>
    public static class Diagnostics
    {
        /// <summary>Сколько доменов встроенного пула пробовать, когда cfProxy.domains пуст.</summary>
        public const int BuiltinCfProxySample = 3;

    }

    /// <summary>
    /// Метрики Prometheus и история временных рядов.
    /// </summary>
    public static class Metrics
    {
        /// <summary>Content-Type экспозиции Prometheus (text format 0.0.4).</summary>
        public const string ContentType = "text/plain; version=0.0.4; charset=utf-8";

        /// <summary>Префикс глобальных метрик прокси.</summary>
        public const string Prefix = "tgwsproxy";

        /// <summary>Префикс метрик по парам (IP, токен).</summary>
        public const string PeerPrefix = "tgws_peer";

        /// <summary>Размер страницы <c>/api/per-ip</c> по умолчанию (F-4.7).</summary>
        public const int DefaultPerIpLimit = 50;

        /// <summary>Верхняя граница размера страницы <c>/api/per-ip</c> (F-4.7).</summary>
        public const int MaxPerIpLimit = 500;

        /// <summary>Шаг сэмплирования по умолчанию (совпадает с шагом по умолчанию для API).</summary>
        public const int DefaultStepSeconds = 5;

        /// <summary>Окно по умолчанию для запроса временного ряда — 15 минут.</summary>
        public const int DefaultWindowSeconds = 900;

        /// <summary>Глубина истории в секундах: один час независимо от шага.</summary>
        public const int CapacitySeconds = 3600;

        /// <summary>Максимальная глубина истории — один час при шаге по умолчанию.</summary>
        public const int Capacity = CapacitySeconds / DefaultStepSeconds;

    }

    /// <summary>
    /// Имена структурированных свойств логов, ключи <c>HttpContext.Items</c>, заголовок корреляции и шумовые пути.
    /// </summary>
    public static class Logging
    {
        /// <summary>Имя HTTP-заголовка корреляции запроса.</summary>
        public const string CorrelationIdHeader = "X-Correlation-Id";

        /// <summary>Ключ в <c>HttpContext.Items</c> для CorrelationId.</summary>
        public const string CorrelationIdItemKey = "CorrelationId";

        /// <summary>Имя структурированного свойства CorrelationId в событиях лога.</summary>
        public const string CorrelationIdProperty = "CorrelationId";

        /// <summary>Имя свойства идентификатора соединения (TCP-сессия).</summary>
        public const string ConnectionId = "ConnectionId";

        /// <summary>Имя свойства адреса клиента.</summary>
        public const string Peer = "Peer";

        /// <summary>Имя свойства идентификатора токена доступа.</summary>
        public const string TokenId = "TokenId";

        /// <summary>Имя свойства номера дата-центра (DC).</summary>
        public const string Dc = "Dc";

        /// <summary>Имя свойства признака media-DC.</summary>
        public const string Media = "Media";

        /// <summary>Имя свойства TraceId (W3C, из <c>Activity</c>).</summary>
        public const string TraceId = "TraceId";

        /// <summary>Имя свойства SpanId (W3C).</summary>
        public const string SpanId = "SpanId";

        /// <summary>Имя свойства хоста запроса (request-logging).</summary>
        public const string RequestHost = "RequestHost";

        /// <summary>Имя свойства User-Agent (request-logging).</summary>
        public const string UserAgent = "UserAgent";

        /// <summary>Имя глобального свойства «приложение».</summary>
        public const string Application = "Application";

        /// <summary>Имя глобального свойства «версия».</summary>
        public const string Version = "Version";

        /// <summary>Имя приложения, зашитое в глобальный энричер.</summary>
        public const string ApplicationName = "tg-ws-proxy";

        /// <summary>Значение, которым маскируются секреты при деструктуризации.</summary>
        public const string MaskedSecret = "***";

        /// <summary>Префикс пути health-проверок (шум request-logging).</summary>
        public const string HealthzPathPrefix = "/healthz";

        /// <summary>Префикс пути метрик Prometheus (шум request-logging).</summary>
        public const string MetricsPathPrefix = "/metrics";

        /// <summary>Префикс пути Swagger (шум request-logging).</summary>
        public const string SwaggerPathPrefix = "/swagger";

        /// <summary>Сегмент пути WEB-proxy carrier (<c>/api/v1/*</c>, шум request-logging).</summary>
        public const string ApiV1PathSegment = "/api/v1/";

    /// <summary>Типы, чьи объекты целиком маскируются (защитная сетка <c>{@…}</c>).</summary>
    public static readonly IReadOnlySet<string> SensitiveTypeNames =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Config",
            "ConfigDto",
            "ProxyToken",
            "ProxyTokenEntry",
        };

    }

    /// <summary>
    /// Per-IP/токен учёт: разрезы статистики, лимит отслеживаемых записей и период фоновой очистки.
    /// </summary>
    public static class Peers
    {
        /// <summary>Верхняя граница числа отслеживаемых адресов/пар в per-IP учёте и ограничителе частоты.</summary>
        public const int MaxTrackedPeers = 100_000;

        /// <summary>Период между фоновыми проходами очистки устаревших записей (секунды).</summary>
        public const double SweepIntervalSeconds = 30.0;

        /// <summary>Разрез по паре «адрес, токен» (по умолчанию).</summary>
        public const string GroupPair = "pair";

        /// <summary>Разрез по адресу: токен не учитывается.</summary>
        public const string GroupIp = "ip";

        /// <summary>Разрез по токену: адрес не учитывается.</summary>
        public const string GroupToken = "token";

    }

    /// <summary>
    /// Конфигурация и стартовый бутстрап: имена файлов, легаси-ключи, пределы валидации.
    /// </summary>
    public static class Config
    {
        /// <summary>Прежний флаг пути к файлу конфигурации. Удалён: путь задаёт только <c>TGWS_CONFIG</c>.</summary>
        public const string ConfigFlag = "--config";

        /// <summary>Имя файла конфигурации, если путь не задан явно.</summary>
        public const string DefaultFileName = "config.json";

        /// <summary>
        /// Ключ прежних версий с единственным секретом (F-8.3). Принимается при чтении и
        /// переносится в <c>tokens</c>; при записи больше не создаётся.
        /// </summary>
        public const string LegacySecretKey = "secret";

        /// <summary>
        /// Плоские ключи логов прежних версий, перенесённые во вложенный блок <c>logs</c>.
        /// Принимаются при чтении и переносятся в <c>logs</c>; при записи больше не создаются.
        /// </summary>
        public const string LegacyLogPathKey = "logPath";

        public const string LegacyLogMaxMegabytesKey = "logMaxMegabytes";

        public const string LegacyLogRetainedFileCountKey = "logRetainedFileCount";

        /// <summary>Максимальное число токенов доступа (F-8.2).</summary>
        public const int MaxTokens = 64;

        /// <summary>Максимальная длина имени токена (F-8.1).</summary>
        public const int MaxTokenNameLength = 64;

        /// <summary>Максимальная длина <c>webProxy.basePath</c> (совпадает с MaxBasePathLength в verifier).</summary>
        public const int MaxWebProxyBasePathLength = 128;

        /// <summary>Предел объёма исходящей очереди одной сессии (верхняя граница — 64 МиБ).</summary>
        public const int MaxWebProxyPendingBytes = 64 * 1024 * 1024;

        /// <summary>Идентификатор токена, созданного миграцией прежнего единственного секрета (F-8.3).</summary>
        public const string DefaultTokenId = "default";

        /// <summary>Имя токена, созданного миграцией прежнего единственного секрета.</summary>
        public const string DefaultTokenName = "default";

    }

}
