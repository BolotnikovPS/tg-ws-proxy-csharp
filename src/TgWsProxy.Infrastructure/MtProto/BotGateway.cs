#nullable disable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using TgWsProxy.Domain;
using MtProtoMethods = TgWsProxy.Infrastructure.MtProto.TL.Methods;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Результат вызова метода Bot API. При <see cref="Ok"/>=true заполнен <see cref="Result"/>;
/// иначе — <see cref="ErrorCode"/> и <see cref="Description"/>. Соглашение «{ok, result}» /
/// «{ok:false, error_code, description}» соблюдает HTTP-слой (<c>TgWsProxy.Web.BotApiEndpoints</c>).
/// </summary>
public sealed class BotApiResult
{
    public bool Ok { get; init; }
    public object Result { get; init; }
    public int? ErrorCode { get; init; }
    public string Description { get; init; }

    public static BotApiResult Success(object result) => new() { Ok = true, Result = result };

    public static BotApiResult Fail(int errorCode, string description)
        => new() { Ok = false, ErrorCode = errorCode, Description = description };
}

/// <summary>Ошибка шлюза с кодом ошибки Bot API (401/403/...); не является ошибкой Telegram-RPC.</summary>
public sealed class BotGatewayException(int errorCode, string description) : Exception(description)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// Медиа-файл multipart-запроса Bot API в «инфраструктурном» виде (без ASP.NET-типов):
/// имя файла, MIME-тип и читаемый поток. Экземпляры создаёт Web-слой при разборе
/// <c>multipart/form-data</c> и передаёт сюда в словаре <c>args</c>.
/// </summary>
#nullable enable
public sealed record BotUploadFile(string FileName, string? MimeType, Stream Stream);
#nullable disable

/// <summary>
/// Ядро локального Bot API-сервера: кэш MTProto-соединений по <c>botId</c>, восстановление/создание
/// файловой сессии, логин бота через <c>auth.importBotAuthorization</c> и диспетчеризация методов.
/// HTTP-контракт живёт в <c>TgWsProxy.Web.BotApiEndpoints</c>; сюда приходит уже разобранный вызов.
/// </summary>
public sealed class BotGateway(BotApiConfig config, ILogger<BotGateway> logger)
{
    /// <summary>Заголовок с api_id (обязателен на первом логине бота).</summary>
    public const string ApiIdHeader = "X-Telegram-Api-Id";
    /// <summary>Заголовок с api_hash (обязателен на первом логине бота).</summary>
    public const string ApiHashHeader = "X-Telegram-Api-Hash";

    private readonly BotApiConfig _config = config ?? throw new ArgumentNullException(nameof(config));
    private readonly BotSessionStore _store = new(config.DataDir);
    private readonly byte[] _secret = ResolveSecret(config.Secret);
    private readonly ILogger<BotGateway> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly ConcurrentDictionary<long, Task<BotConnection>> _connections = new();

    /// <summary>In-memory кэш скачанных через getFile файлов (file_path → запись). LRU-вытеснение по LastAccessTicks.</summary>
    private readonly ConcurrentDictionary<string, FileCacheEntry> _fileCache = new();

    /// <summary>Суммарный размер байтов в <see cref="_fileCache"/> (ведётся через Interlocked).</summary>
    private long _fileCacheBytes;

    /// <summary>
    /// Кэш access_hash каналов (channel_id → access_hash). Заполняется при обработке <see cref="MtProtoTL.Channel"/>
    /// из updates/результатов (access_hash присутствует в Channel). Статический: access_hash глобален для канала
    /// и не зависит от конкретного бота/сессии.
    /// </summary>
    private static readonly ConcurrentDictionary<long, long> _channelAccessHash = new();

    /// <summary>
    /// Кэш access_hash пользователей (user_id → access_hash). Заполняется из updates/результатов RPC, где
    /// встречается <see cref="MtProtoTL.User"/> с access_hash. Нужен, чтобы реальный Bot API-контракт работал:
    /// клиент присылает только <c>chat_id</c> из update, без access_hash (в настоящем Bot API его и нет).
    /// access_hash глобален для аккаунта, поэтому кэш статический.
    /// </summary>
    private static readonly ConcurrentDictionary<long, long> _userAccessHash = new();

    /// <summary>
    /// Разбирает hex-секрет MTProxy в 16-байтный ключ: отбрасывает префикс <c>dd</c>/<c>ee</c> (17 байт).
    /// Пустая строка/null — прямое подключение к DC (ключ обфускации не нужен).
    /// </summary>
    public static byte[] ResolveSecret(string secretHex)
    {
        if (string.IsNullOrWhiteSpace(secretHex))
        {
            return null;
        }

        var bytes = Convert.FromHexString(secretHex.Trim());
        return bytes.Length switch
        {
            17 => bytes[1..], // префикс dd/ee + 16 байт секрета
            16 => bytes,
            _ => throw new FormatException(
                $"MTProxy secret must be 16 or 17 bytes (dd/ee prefix + 16), got {bytes.Length}"),
        };
    }

    /// <summary>
    /// Диспетчеризует метод Bot API. Возвращает согласованный результат, никогда не бросает наружу
    /// (кроме критических ошибок конфигурации при конструировании).
    /// </summary>
    public async Task<BotApiResult> HandleAsync(
        string botToken,
        string method,
        IReadOnlyDictionary<string, object> args,
        int? apiId,
        string apiHash,
        CancellationToken ct)
    {
        if (!_config.Enabled)
        {
            // Маршруты не регистрируются при botApi.enabled=false (BotApiEndpoints.MapBotApi
            // выходит сразу), поэтому сюда запрос не доходит. Ветка оставлена как защита от
            // прямого вызова HandleAsync (тесты, будущие транспорты).
            return BotApiResult.Fail(403, "Bot API is disabled");
        }

        long botId;
        try
        {
            botId = MtProtoClient.BotIdFromToken(botToken);
        }
        catch (Exception e)
        {
            return BotApiResult.Fail(400, e.Message);
        }

        // Чистая валидация аргументов идёт до логина: без неё запрос без chat_id или с
        // неверным parse_mode упирался в ImportBotAuthorization и отдавал 401 «нужны
        // заголовки api_id/api_hash», хотя заголовки к ошибке в параметрах отношения не имеют.
        try
        {
            ValidateArgsBeforeNetwork(method, args);
        }
        catch (BotGatewayException e)
        {
            return BotApiResult.Fail(e.ErrorCode, e.Message);
        }

        try
        {
            return method switch
            {
                "getMe" => await GetMeAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false),
                "getUpdates" => await GetUpdatesAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendMessage" => await SendMessageAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendPhoto" => await SendPhotoAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendDocument" => await SendDocumentAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendAudio" => await SendAudioAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendVideo" => await SendVideoAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendAnimation" => await SendAnimationAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendVoice" => await SendVoiceAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendVideoNote" => await SendVideoNoteAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendSticker" => await SendStickerAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendMediaGroup" => await SendMediaGroupAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getFile" => await GetFileAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "editMessageText" => await EditMessageTextAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "editMessageCaption" => await EditMessageCaptionAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "editMessageMedia" => await EditMessageMediaAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "deleteMessage" => await DeleteMessageAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "forwardMessage" => await ForwardMessageAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "copyMessage" => await CopyMessageAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "answerCallbackQuery" => await AnswerCallbackQueryAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getChat" => await GetChatAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getChatAdministrators" => await GetChatAdministratorsAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getChatMember" => await GetChatMemberAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getChatMemberCount" => await GetChatMemberCountAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "setChatTitle" => await SetChatTitleAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "setChatDescription" => await SetChatDescriptionAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "setChatPhoto" => await SetChatPhotoAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "leaveChat" => await LeaveChatAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "banChatMember" => await BanChatMemberAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "sendChatAction" => await SendChatActionAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "setMyCommands" => await SetMyCommandsAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "getMyCommands" => await GetMyCommandsAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                "answerInlineQuery" => await AnswerInlineQueryAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
                _ => BotApiResult.Fail(501, $"method '{method}' is not implemented"),
            };
        }
        catch (BotGatewayException e)
        {
            return BotApiResult.Fail(e.ErrorCode, e.Message);
        }
        catch (RpcException e)
        {
            // auth key истёк/не зарегистрирован → сбрасываем кэш соединения и сессию, чтобы следующий запрос
            // выполнил повторный логин (auth.importBotAuthorization на новом auth key). Ошибку отдаём клиенту.
            if (e.Code == 401 &&
                (e.Message?.Contains("AUTH_KEY", StringComparison.OrdinalIgnoreCase) == true ||
                 e.Message?.Contains("SESSION_REVOKED", StringComparison.OrdinalIgnoreCase) == true))
            {
                _connections.TryRemove(botId, out _);
                try { _store.Delete(botId); } catch { /* сессионный файл может отсутствовать */ }
                _logger.LogWarning("Bot {BotId} auth key invalidated (401); session dropped for re-login", botId);
            }

            _logger.LogWarning("Bot {BotId} RPC error {Code}: {Message}", botId, e.Code, e.Message);
            return BotApiResult.Fail(e.Code, e.Message);
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            // Транспортный сбой (разрыв сокета) — вытесняем мёртвое соединение, чтобы следующий
            // запрос переподключился, а не переиспользовал битый клиент.
            if (_connections.TryRemove(botId, out var dead) && dead.IsCompletedSuccessfully)
            {
                dead.Result.Client.Dispose();
            }

            _logger.LogWarning("Bot {BotId} transport error: {Message}; connection dropped", botId, e.Message);
            return BotApiResult.Fail(502, "Bot transport error");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Bot gateway error for {Method} (bot {BotId})", method, botId);
            return BotApiResult.Fail(502, "Internal bot gateway error");
        }
    }

    private async Task<BotApiResult> GetMeAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var users = await connection.Client.GetUsersAsync([new MtProtoTL.InputUserSelf()], ct).ConfigureAwait(false);
        if (users is not { Length: > 0 } || users[0] is not MtProtoTL.User user)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        return BotApiResult.Success(BuildUser(user));
    }

    /// <summary>
    /// getFile: скачивает файл по <c>file_id</c> фрагментами (512 КиБ) через upload.getFile
    /// и кладёт целиком в in-memory кэш; возвращает Bot API File-объект с неотгадываемым file_path.
    /// </summary>
    private async Task<BotApiResult> GetFileAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var fileId = ReadStringArg(args, "file_id");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return BotApiResult.Fail(400, "file_id is required for getFile");
        }

        // Маршрутизация по первому байту file_id: 2 = фото, иначе документ.
        // Сами дешифраторы повторно валидируют тип/длину и бросают BotGatewayException(400).
        var prefix = DecodeFileIdBytes(fileId);
        MtProtoTL.InputFileLocationBase location;
        if (prefix.Length > 0 && prefix[0] == 2)
        {
            var photo = DecodeInputPhoto(fileId);
            location = new MtProtoTL.InputPhotoFileLocation
            {
                id = photo.id,
                access_hash = photo.access_hash,
                file_reference = photo.file_reference,
                thumb_size = "", // пустая строка — оригинал, не миниатюру
            };
        }
        else
        {
            var document = DecodeInputDocument(fileId);
            location = new MtProtoTL.InputDocumentFileLocation
            {
                id = document.id,
                access_hash = document.access_hash,
                file_reference = document.file_reference,
                thumb_size = "", // пустая строка — оригинал, не миниатюру
            };
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        const int chunkSize = 512 * 1024;
        var cap = Math.Max(_config.MaxFileCacheBytes, 1L);
        var chunks = new List<byte[]>();
        long total = 0;
        var offset = 0L;

        while (true)
        {
            var part = await client.GetFileAsync(location, offset, chunkSize, ct).ConfigureAwait(false);
            if (part is MtProtoTL.Upload_FileCdnRedirect)
            {
                // ⚠️ CDN-файл: нужен переход на второй DC и расшифровка (encryption_key/iv + file_hashes);
                // в этом раунде не реализовано — требуется ручная интеграция.
                return BotApiResult.Fail(502, "CDN file download is not supported yet");
            }

            if (part is not MtProtoTL.Upload_File filePart || filePart.bytes is not { Length: > 0 })
            {
                break; // неизвестный/пустой блок — считаем концом файла
            }

            chunks.Add(filePart.bytes);
            total += filePart.bytes.Length;
            if (total > cap || total > int.MaxValue)
            {
                return BotApiResult.Fail(413, "file too large to cache");
            }

            offset += filePart.bytes.Length;
            if (filePart.bytes.Length < chunkSize)
            {
                break; // последний фрагмент
            }
        }

        var bytes = new byte[(int)total];
        var written = 0;
        foreach (var chunk in chunks)
        {
            Buffer.BlockCopy(chunk, 0, bytes, written, chunk.Length);
            written += chunk.Length;
        }

        var filePath = NewFileToken();
        var fileUniqueId = NewFileToken();
        StoreFile(filePath, bytes, contentType: null);

        return BotApiResult.Success(new Dictionary<string, object>
        {
            ["file_id"] = fileId,
            ["file_unique_id"] = fileUniqueId,
            ["file_size"] = bytes.Length,
            ["file_path"] = filePath,
        });
    }

    /// <summary>Случайный неотгадываемый маркер файла: base64url(RandomBytes(32)) без padding.</summary>
    private static string NewFileToken()
        => Convert.ToBase64String(MtProtoCrypto.RandomBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Кладёт байты в in-memory кэш и при необходимости вытесняет старейшие по LastAccessTicks (LRU).</summary>
    internal void StoreFile(string filePath, byte[] data, string contentType)
    {
        _fileCache[filePath] = new FileCacheEntry(data, contentType);
        Interlocked.Add(ref _fileCacheBytes, data.Length);
        EvictIfNeeded();
    }

    /// <summary>
    /// Попытка отдать скачанный файл по <c>file_path</c>. При попадании обновляет момент доступа (LRU).
    /// Возвращает false и пустые out-параметры при промахе.
    /// </summary>
#nullable enable
    public bool TryGetFile(string filePath, out ReadOnlyMemory<byte> data, out string? contentType)
    {
        if (_fileCache.TryGetValue(filePath, out var entry))
        {
            entry.LastAccessTicks = DateTime.UtcNow.Ticks;
            data = entry.Data;
            contentType = entry.ContentType;
            return true;
        }

        data = default;
        contentType = null;
        return false;
    }
#nullable disable

    /// <summary>LRU-вытеснение: пока суммарный размер превышает лимит, удаляет записи с самой старой LastAccessTicks.</summary>
    private void EvictIfNeeded()
    {
        var cap = Math.Max(_config.MaxFileCacheBytes, 1L);
        while (Interlocked.Read(ref _fileCacheBytes) > cap)
        {
            string victimKey = null;
            var oldest = long.MaxValue;
            foreach (var kv in _fileCache)
            {
                if (kv.Value.LastAccessTicks < oldest)
                {
                    oldest = kv.Value.LastAccessTicks;
                    victimKey = kv.Key;
                }
            }

            if (victimKey == null || !_fileCache.TryRemove(victimKey, out var victim))
            {
                break;
            }

            Interlocked.Add(ref _fileCacheBytes, -victim.Data.Length);
        }
    }

    /// <summary>Запись in-memory кэша скачанного файла: байты, Content-Type (может быть null) и момент доступа.</summary>
    private sealed class FileCacheEntry(byte[] data, string contentType)
    {
        public byte[] Data { get; } = data;
        public string ContentType { get; } = contentType;
        public long LastAccessTicks { get; set; } = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// Кэш соединений по botId: первый запрос выполняет логин, остальные ждут тот же Task.
    /// Живое соединение переиспользуется; оборванное вытесняется и освобождается, после чего
    /// выполняется повторный логин (неудавшийся логин также вытесняется).
    /// </summary>
    private async Task<BotConnection> GetConnectionAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        if (_connections.TryGetValue(botId, out var cached)
            && cached.IsCompletedSuccessfully
            && cached.Result.Client.IsConnected)
        {
            return cached.Result;
        }

        if (_connections.TryRemove(botId, out cached) && cached.IsCompletedSuccessfully)
        {
            cached.Result.Client.Dispose(); // освобождаем сокет мёртвого соединения
        }

        return await _connections.GetOrAdd(botId, _ => LoginAndCacheAsync(botId, botToken, apiId, apiHash, ct))
            .ConfigureAwait(false);
    }

    private async Task<BotConnection> LoginAndCacheAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        try
        {
            return await LoginAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        }
        catch
        {
            _connections.TryRemove(botId, out _);
            throw;
        }
    }

    private async Task<BotConnection> LoginAsync(long botId, string botToken, int? apiId, string apiHash, CancellationToken ct)
    {
        var session = _store.Load(botId);

        // Сессия без ApiId (файл прежней версии) не позволяет отправить initConnection,
        // обязательный первым сообщением новой сессии, — считаем её отсутствующей.
        var hadSession = session is { IsValid: true, ApiId: > 0 };

        // Первый вход требует api_id/api_hash; проверяем до сетевых операций (fail-fast).
        // При восстановленной сессии повторный importBotAuthorization не нужен: self-id берётся через users.getUsers.
        var aid = 0;
        if (!hadSession)
        {
            if (apiId is not int parsedApiId || string.IsNullOrWhiteSpace(apiHash))
            {
                throw new BotGatewayException(401,
                    $"{ApiIdHeader} and {ApiHashHeader} headers are required for the first request (login)");
            }
            aid = parsedApiId;
        }

        var client = new MtProtoClient(_config.Host, _config.Port, _secret);
        await client.ConnectAsync(session, testMode: false, ct).ConfigureAwait(false);

        MtProtoTL.UserBase me = null;
        if (!hadSession)
        {
            var auth = await client.ImportBotAuthorizationAsync(aid, apiHash, botToken, ct).ConfigureAwait(false);
            if (auth is not MtProtoTL.Auth_Authorization authorization)
            {
                throw new BotGatewayException(401, "auth.importBotAuthorization returned sign-up-required");
            }

            me = authorization.user;
        }

        _store.Save(botId, client.ExportSession());
        return new BotConnection(client, me);
    }

    /// <summary>Собирает объект <c>User</c> Bot API из MTProto-конструктора <c>user</c>.</summary>
    public static Dictionary<string, object> BuildUser(MtProtoTL.User u)
    {
        var flags = u.flags;
        var user = new Dictionary<string, object>
        {
            ["id"] = u.id,
            ["is_bot"] = flags.HasFlag(MtProtoTL.User.Flags.bot),
        };
        if (flags.HasFlag(MtProtoTL.User.Flags.has_first_name))
        {
            user["first_name"] = u.first_name;
        }

        if (flags.HasFlag(MtProtoTL.User.Flags.has_last_name))
        {
            user["last_name"] = u.last_name;
        }

        if (flags.HasFlag(MtProtoTL.User.Flags.has_username))
        {
            user["username"] = u.username;
        }

        if (flags.HasFlag(MtProtoTL.User.Flags.has_lang_code))
        {
            user["language_code"] = u.lang_code;
        }

        return user;
    }

    /// <summary>getUpdates: инкрементальная разница через updates.getState/getDifference (long-poll не реализован).</summary>
    private async Task<BotApiResult> GetUpdatesAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var offset = ReadIntArg(args, "offset", 0);
        var limit = ReadIntArg(args, "limit", 100);
        if (limit < 1)
        {
            limit = 1;
        }

        if (limit > 100)
        {
            limit = 100;
        }

        // long-poll: timeout — общее время ожидания новых сообщений (0 = вернуться сразу),
        // allowed_updates — фильтр типов (сейчас генерируем только "message").
        var timeout = ReadIntArg(args, "timeout", 0);
        if (timeout < 0)
        {
            timeout = 0;
        }

        if (timeout > 60)
        {
            timeout = 60;
        }

        var allowed = ReadStringListArg(args, "allowed_updates");
        var deadline = timeout > 0 ? DateTime.UtcNow.AddSeconds(timeout) : (DateTime?)null;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var session = client.ExportSession();
            var pts = (int)Math.Min(session.Pts, int.MaxValue);
            var qts = (int)Math.Min(session.Qts, int.MaxValue);
            var date = session.Date <= 0
                ? DateTime.UnixEpoch
                : DateTimeOffset.FromUnixTimeSeconds(session.Date).UtcDateTime;

            List<Dictionary<string, object>> updates;

            if (pts == 0)
            {
                // Первый запуск: фиксируем текущее состояние как базовую линию, история не отдаётся.
                await client.SyncUpdateStateAsync(ct).ConfigureAwait(false);
                _store.Save(botId, client.ExportSession());
                updates = [];
            }
            else
            {
                var diff = await client.GetDifferenceAsync(pts, date, qts, ct).ConfigureAwait(false);
                switch (diff)
                {
                    case MtProtoTL.Updates_DifferenceTooLong tooLong:
                        client.ApplyUpdatesState(new MtProtoTL.Updates_State
                        {
                            pts = tooLong.pts,
                            qts = qts,
                            date = date,
                            seq = (int)session.Seq,
                        });
                        _store.Save(botId, client.ExportSession());
                        updates = [];
                        break;

                    case MtProtoTL.Updates_DifferenceEmpty empty:
                        client.ApplyUpdatesState(new MtProtoTL.Updates_State
                        {
                            pts = pts,
                            qts = qts,
                            date = empty.date,
                            seq = empty.seq,
                        });
                        _store.Save(botId, client.ExportSession());
                        updates = [];
                        break;

                    case MtProtoTL.Updates_Difference d:
                        updates = BuildUpdates(d.new_messages, d.users, d.chats, offset);
                        client.ApplyUpdatesState(d.state);
                        break;

                    case MtProtoTL.Updates_DifferenceSlice s:
                        updates = BuildUpdates(s.new_messages, s.users, s.chats, offset);
                        client.ApplyUpdatesState(s.intermediate_state);
                        break;

                    default:
                        return BotApiResult.Fail(500, "unexpected updates.difference subtype");
                }

                _store.Save(botId, client.ExportSession());
            }

            updates = FilterAllowedUpdates(updates, allowed);
            if (updates.Count > limit)
            {
                updates = updates.GetRange(0, (int)limit);
            }

            if (updates.Count > 0)
            {
                return BotApiResult.Success(updates);
            }

            if (deadline == null || DateTime.UtcNow >= deadline)
            {
                return BotApiResult.Success(updates); // пустой список: нечего отдавать до таймаута
            }

            // Новых сообщений нет и timeout не истёк: короткая пауза и повтор getDifference.
            await Task.Delay(500, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Преобразует новые сообщения из updates.difference в Bot API update-объекты {update_id, message}.</summary>
    internal static List<Dictionary<string, object>> BuildUpdates(
        MtProtoTL.MessageBase[] newMessages,
        Dictionary<long, MtProtoTL.User> users,
        Dictionary<long, MtProtoTL.ChatBase> chats,
        long offset)
    {
        var result = new List<Dictionary<string, object>>();

        // Бот должен уметь отвечать по chat_id из update, не зная access_hash (как в настоящем Bot API):
        // запоминаем хэши всех участников, пришедших вместе с разницей updates.
        RememberUserAccessHashes(users);
        RememberChannelAccessHashes(chats);

        if (newMessages is { Length: > 0 })
        {
            foreach (var msg in newMessages)
            {
                if (msg is not MtProtoTL.Message m || m.id <= 0)
                {
                    continue; // MessageEmpty/MessageService и прочие пока не отображаются
                }

                if (offset > 0 && m.id < offset)
                {
                    continue; // клиент уже получил update_id ниже offset
                }

                result.Add(new Dictionary<string, object>
                {
                    ["update_id"] = m.id,
                    ["message"] = BuildMessage(m, users, chats),
                });
            }
        }

        result.Sort(static (a, b) => ((int)a["update_id"]).CompareTo((int)b["update_id"]));
        return result;
    }

    /// <summary>Минимальный Bot API message: message_id/date/text + from/chat, когда они разрешимы.</summary>
    internal static Dictionary<string, object> BuildMessage(
        MtProtoTL.Message m,
        Dictionary<long, MtProtoTL.User> users,
        Dictionary<long, MtProtoTL.ChatBase> chats)
    {
        var message = new Dictionary<string, object>
        {
            ["message_id"] = m.id,
            ["date"] = new DateTimeOffset(m.date.ToUniversalTime()).ToUnixTimeSeconds(),
        };

        var hasMedia = m.flags.HasFlag(MtProtoTL.Message.Flags.has_media) && m.media != null;

        if (!string.IsNullOrEmpty(m.message))
        {
            // У медиа-сообщений m.message — это подпись (caption), у обычных — текст.
            if (hasMedia)
            {
                message["caption"] = m.message;
            }
            else
            {
                message["text"] = m.message;
            }
        }

        if (hasMedia)
        {
            switch (m.media)
            {
                case MtProtoTL.MessageMediaPhoto { photo: MtProtoTL.Photo photo }:
                    message["photo"] = BuildPhotoSizes(photo);
                    break;
                case MtProtoTL.MessageMediaDocument { document: MtProtoTL.Document doc }:
                    message["document"] = BuildDocument(doc);
                    break;
            }
        }

        if (m.flags.HasFlag(MtProtoTL.Message.Flags.has_from_id) && m.from_id is MtProtoTL.PeerUser fromUser
            && users != null && users.TryGetValue(fromUser.user_id, out var u) && u is MtProtoTL.User sender)
        {
            message["from"] = BuildUser(sender);
        }

        var chat = BuildChat(m.peer_id, chats);
        if (chat != null)
        {
            message["chat"] = chat;
        }

        return message;
    }

    /// <summary>Минимальный Bot API chat {id, type} по типу пира; channel различает supergroup по megagroup-флагу.</summary>
    internal static Dictionary<string, object> BuildChat(
        MtProtoTL.Peer peer,
        IReadOnlyDictionary<long, MtProtoTL.ChatBase> chats)
    {
        switch (peer)
        {
            case MtProtoTL.PeerUser pu:
                return new Dictionary<string, object> { ["id"] = pu.user_id, ["type"] = "private" };
            case MtProtoTL.PeerChat pc:
                return new Dictionary<string, object> { ["id"] = pc.chat_id, ["type"] = "group" };
            case MtProtoTL.PeerChannel pch:
                var type = "channel";
                if (chats != null && chats.TryGetValue(pch.channel_id, out var c) && c is MtProtoTL.Channel ch)
                {
                    RememberChannelAccessHash(ch);
                    if (ch.flags.HasFlag(MtProtoTL.Channel.Flags.megagroup))
                    {
                        type = "supergroup";
                    }
                }

                return new Dictionary<string, object> { ["id"] = pch.channel_id, ["type"] = type };
            default:
                return null;
        }
    }

    /// <summary>Один (самый крупный) размер фото для Bot API; GetFile всё равно скачивает оригинал (thumb_size="").</summary>
    internal static Dictionary<string, object>[] BuildPhotoSizes(MtProtoTL.Photo photo)
    {
        if (photo == null)
        {
            return [];
        }

        var fileId = EncodeInputPhoto(photo.id, photo.access_hash, photo.dc_id, photo.file_reference);
        var uniqueId = BuildFileUniqueId(2, photo.id);

        MtProtoTL.PhotoSize largest = null;
        if (photo.sizes != null)
        {
            foreach (var s in photo.sizes)
            {
                if (s is MtProtoTL.PhotoSize ps && (largest == null || (long)ps.w * ps.h >= (long)largest.w * largest.h))
                {
                    largest = ps;
                }
            }
        }

        if (largest == null)
        {
            return [new Dictionary<string, object> { ["file_id"] = fileId, ["file_unique_id"] = uniqueId }];
        }

        return
        [
            new Dictionary<string, object>
            {
                ["file_id"] = fileId,
                ["file_unique_id"] = uniqueId,
                ["width"] = largest.w,
                ["height"] = largest.h,
                ["file_size"] = largest.size,
            }
        ];
    }

    /// <summary>Bot API document: file_id/file_unique_id/mime_type/file_size (+ file_name из атрибутов).</summary>
    internal static Dictionary<string, object> BuildDocument(MtProtoTL.Document doc)
    {
        var result = new Dictionary<string, object>
        {
            ["file_id"] = EncodeInputDocument(doc.id, doc.access_hash, doc.dc_id, doc.file_reference),
            ["file_unique_id"] = BuildFileUniqueId(5, doc.id),
            ["mime_type"] = doc.mime_type ?? string.Empty,
            ["file_size"] = doc.size,
        };

        if (doc.attributes != null)
        {
            foreach (var a in doc.attributes)
            {
                if (a is MtProtoTL.DocumentAttributeFilename fna && !string.IsNullOrEmpty(fna.file_name))
                {
                    result["file_name"] = fna.file_name;
                    break;
                }
            }
        }

        return result;
    }

    /// <summary>Читает целочисленный аргумент Bot API из GET (string) или POST JSON (JsonElement).</summary>
    internal static long ReadIntArg(IReadOnlyDictionary<string, object> args, string name, long fallback)
    {
        if (args == null || !args.TryGetValue(name, out var raw))
        {
            return fallback;
        }

        return raw switch
        {
            null => fallback,
            long l => l,
            int i => i,
            double d => (long)d,
            string s when long.TryParse(s, out var v) => v,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt64(out var jv) => jv,
            // Bot API принимает chat_id и строкой ("...") и числом. При десериализации
            // JSON в Dictionary<string, object> строковое значение приходит как JsonElement
            // со ValueKind.String, а не как string — без этой ветки ReadIntArg возвращает fallback.
            JsonElement je when je.ValueKind == JsonValueKind.String && long.TryParse(je.GetString(), out var jsv) => jsv,
            _ => fallback,
        };
    }

    /// <summary>Читает булев аргумент Bot API (POST JSON true/false, GET "true"/"false"/"1"/"0").</summary>
    internal static bool ReadBoolArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return false;
        }

        return raw switch
        {
            bool b => b,
            JsonElement je when je.ValueKind == JsonValueKind.True => true,
            JsonElement je when je.ValueKind == JsonValueKind.False => false,
            JsonElement je when je.ValueKind == JsonValueKind.String && bool.TryParse(je.GetString(), out var jb) => jb,
            string s when s == "1" => true,
            string s when s == "0" => false,
            string s when bool.TryParse(s, out var v) => v,
            _ => false,
        };
    }

    /// <summary>sendMessage: резолв пира → messages.sendMessage → Bot API message из результата Updates.</summary>
    private async Task<BotApiResult> SendMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var text = ReadStringArg(args, "text");
        if (string.IsNullOrEmpty(text))
        {
            return BotApiResult.Fail(400, "text is required for sendMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new MtProtoTL.InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not MtProtoTL.User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = ResolvePeer(args, selfUser.id);
        var (message, entities) = ResolveTextAndEntities(args, text);

        var request = new MtProtoMethods.Messages_SendMessage
        {
            flags = 0,
            peer = peer,
            message = message,
            random_id = NextRandomId(),
        };

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= MtProtoMethods.Messages_SendMessage.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= MtProtoMethods.Messages_SendMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= MtProtoMethods.Messages_SendMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildSentMessage(result, peer, selfUser, message));
    }

    // ===== Tier 2: editMessage*/deleteMessage/forwardMessage/copyMessage/answerCallbackQuery =====

    /// <summary>Возвращает self-пользователя бота (users.getUsers → User) или null при неудаче.</summary>
    private static async Task<MtProtoTL.User> GetSelfUserAsync(MtProtoClient client, CancellationToken ct)
    {
        var users = await client.GetUsersAsync([new MtProtoTL.InputUserSelf()], ct).ConfigureAwait(false);
        return users is { Length: > 0 } && users[0] is MtProtoTL.User u ? u : null;
    }

    /// <summary>Чистая валидация цели edit*: chat_id+message_id либо inline_message_id; ни одного/оба — 400.</summary>
    private static void ValidateEditTarget(IReadOnlyDictionary<string, object> args, string methodName)
    {
        var hasChat = args != null && args.ContainsKey("chat_id");
        var hasInline = !string.IsNullOrEmpty(ReadStringArg(args, "inline_message_id"));

        if (hasChat && hasInline)
        {
            throw new BotGatewayException(400, $"chat_id and inline_message_id are mutually exclusive for {methodName}");
        }

        if (!hasChat && !hasInline)
        {
            throw new BotGatewayException(400, $"chat_id or inline_message_id is required for {methodName}");
        }

        if (!hasChat)
        {
            // ⚠️ messages.editInlineBotMessage отсутствует в текущем TL-слое — inline-редактирование не реализовано.
            throw new BotGatewayException(400, $"inline_message_id editing is not supported for {methodName}");
        }
    }

    /// <summary>
    /// Методы, требующие цель-чат: <c>chat_id</c>, raw <c>peer</c> или (для edit*) <c>inline_message_id</c>.
    /// Проверяются до сети, чтобы отсутствие цели давало 400, а не 401 про заголовки логина.
    /// </summary>
    private static readonly HashSet<string> ChatTargetMethods = new(StringComparer.Ordinal)
    {
        "sendMessage",
        "sendPhoto",
        "sendDocument",
        "sendAudio",
        "sendVideo",
        "sendAnimation",
        "sendVoice",
        "sendVideoNote",
        "sendSticker",
        "sendMediaGroup",
        "sendChatAction",
        "deleteMessage",
        "forwardMessage",
        "copyMessage",
        "getChat",
        "getChatAdministrators",
        "getChatMember",
        "getChatMemberCount",
        "setChatTitle",
        "setChatDescription",
        "setChatPhoto",
        "leaveChat",
        "banChatMember",
    };

    /// <summary>Методы edit*: цель — пара chat_id+message_id либо inline_message_id.</summary>
    private static readonly HashSet<string> EditTargetMethods = new(StringComparer.Ordinal)
    {
        "editMessageText",
        "editMessageCaption",
        "editMessageMedia",
    };

    /// <summary>Методы, у которых <c>parse_mode</c> реально разбирается в текст/сущности.</summary>
    private static readonly HashSet<string> ParseModeMethods = new(StringComparer.Ordinal)
    {
        "sendMessage",
        "sendPhoto",
        "sendDocument",
        "sendAudio",
        "sendVideo",
        "sendAnimation",
        "sendVoice",
        "sendVideoNote",
        "sendSticker",
        "editMessageText",
        "editMessageCaption",
    };

    /// <summary>
    /// Валидация аргументов, не требующая сети: выполняется до <c>auth.importBotAuthorization</c>.
    /// Бросает <see cref="BotGatewayException"/> с кодом 400 — как это делает реальный Bot API
    /// для отсутствующих/некорректных параметров.
    /// </summary>
    internal static void ValidateArgsBeforeNetwork(string method, IReadOnlyDictionary<string, object> args)
    {
        if (args == null || string.IsNullOrEmpty(method))
        {
            return;
        }

        if (ParseModeMethods.Contains(method))
        {
            ValidateParseModeValue(ReadStringArg(args, "parse_mode"));
        }

        if (EditTargetMethods.Contains(method))
        {
            // У edit* своя пара требований; raw 'peer' тоже принимается как цель.
            if (!args.ContainsKey("peer"))
            {
                ValidateEditTarget(args, method);
            }

            return;
        }

        if (!ChatTargetMethods.Contains(method))
        {
            return;
        }

        if (args.ContainsKey("peer") || args.ContainsKey("chat_id"))
        {
            return;
        }

        throw new BotGatewayException(400, $"chat_id is required for {method}");
    }

    /// <summary>Проверяет только значение <c>parse_mode</c>, без построения сущностей.</summary>
    private static void ValidateParseModeValue(string parseMode)
    {
        if (string.IsNullOrWhiteSpace(parseMode) || parseMode == "HTML")
        {
            return;
        }

        if (parseMode is "Markdown" or "MarkdownV2")
        {
            throw new BotGatewayException(
                400, $"parse_mode '{parseMode}' is not implemented; pass 'entities' instead");
        }

        throw new BotGatewayException(400, $"unknown parse_mode '{parseMode}'");
    }

    /// <summary>Резолв цели редактирования (peer + message_id) после валидации; требует selfId для self-пира.</summary>
    private static (MtProtoTL.InputPeer Peer, int MessageId) ResolveEditTarget(
        IReadOnlyDictionary<string, object> args, long selfId, string methodName)
    {
        ValidateEditTarget(args, methodName);
        var peer = ResolvePeer(args, selfId);
        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            throw new BotGatewayException(400, $"message_id is required for {methodName}");
        }

        return (peer, (int)messageId);
    }

    /// <summary>editMessageText: messages.editMessage (has_message + no_webpage/entities/reply_markup).</summary>
    private async Task<BotApiResult> EditMessageTextAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var text = ReadStringArg(args, "text");
        if (string.IsNullOrEmpty(text))
        {
            return BotApiResult.Fail(400, "text is required for editMessageText");
        }

        ValidateEditTarget(args, "editMessageText");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = ResolveEditTarget(args, selfUser.id, "editMessageText");
        var (message, entities) = ResolveTextAndEntities(args, text);

        var request = new MtProtoMethods.Messages_EditMessage
        {
            flags = 0,
            peer = peer,
            id = messageId,
            message = message,
        };
        request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_message;

        if (ReadBoolArg(args, "disable_web_page_preview"))
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.no_webpage;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>editMessageCaption: messages.editMessage (message-поле = caption); пустая строка очищает подпись.</summary>
    private async Task<BotApiResult> EditMessageCaptionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var caption = ReadStringArg(args, "caption");
        ValidateEditTarget(args, "editMessageCaption");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = ResolveEditTarget(args, selfUser.id, "editMessageCaption");
        var (message, entities) = ResolveCaptionAndEntities(args, caption);

        var request = new MtProtoMethods.Messages_EditMessage { flags = 0, peer = peer, id = messageId };
        if (caption != null)
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_message;
            request.message = message ?? "";
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_entities;
            request.entities = entities;
        }

        var result = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>editMessageMedia: messages.editMessage (has_media; media собирается как элемент альбома Tier 1).</summary>
    private async Task<BotApiResult> EditMessageMediaAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var mediaEl = ReadJsonObjectArg(args, "media");
        if (mediaEl == null)
        {
            return BotApiResult.Fail(400, "media is required for editMessageMedia");
        }

        ValidateEditTarget(args, "editMessageMedia");

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var (peer, messageId) = ResolveEditTarget(args, selfUser.id, "editMessageMedia");
        var single = await BuildSingleMediaAsync(connection.Client, mediaEl.Value, args, ct).ConfigureAwait(false);

        var request = new MtProtoMethods.Messages_EditMessage
        {
            flags = MtProtoMethods.Messages_EditMessage.Flags.has_media,
            peer = peer,
            id = messageId,
            media = single.media,
        };

        if (!string.IsNullOrEmpty(single.message))
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_message;
            request.message = single.message;
        }

        if (single.entities is { Length: > 0 })
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_entities;
            request.entities = single.entities;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        var result = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        var edited = BuildEditedMessage(result, selfUser);
        return edited is null ? BotApiResult.Success(true) : BotApiResult.Success(edited);
    }

    /// <summary>deleteMessage: messages.deleteMessages(revoke) → true. chat_id валидируется, но в RPC не передаётся.</summary>
    private async Task<BotApiResult> DeleteMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            return BotApiResult.Fail(400, "message_id is required for deleteMessage");
        }

        if (ReadIntArg(args, "chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for deleteMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var request = new MtProtoMethods.Messages_DeleteMessages
        {
            flags = MtProtoMethods.Messages_DeleteMessages.Flags.revoke,
            id = [(int)messageId],
        };

        await connection.Client.InvokeAsync<MtProtoTL.Messages_AffectedMessages>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(true);
    }

    /// <summary>forwardMessage: messages.forwardMessages (silent=noforwards по флагам Bot API) → Message.</summary>
    private async Task<BotApiResult> ForwardMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (ReadIntArg(args, "from_chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "from_chat_id is required for forwardMessage");
        }

        if (ReadIntArg(args, "chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for forwardMessage");
        }

        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            return BotApiResult.Fail(400, "message_id is required for forwardMessage");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var toPeer = ResolvePeer(args, selfUser.id);
        var fromPeer = ResolvePeer(args, selfUser.id, "from_chat_id", "from_access_hash");

        var request = new MtProtoMethods.Messages_ForwardMessages
        {
            flags = 0,
            from_peer = fromPeer,
            to_peer = toPeer,
            id = [(int)messageId],
            random_id = [NextRandomId()],
        };

        if (ReadBoolArg(args, "disable_notification"))
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.silent;
        }

        if (ReadBoolArg(args, "protect_content"))
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.noforwards;
        }

        var result = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildForwardedMessage(result, toPeer, selfUser));
    }

    /// <summary>copyMessage: forwardMessages (drop_author, +drop_media_captions при заданном caption), затем messages.editMessage по caption/reply_markup → Message.</summary>
    private async Task<BotApiResult> CopyMessageAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (ReadIntArg(args, "from_chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "from_chat_id is required for copyMessage");
        }

        if (ReadIntArg(args, "chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for copyMessage");
        }

        var messageId = ReadIntArg(args, "message_id", 0);
        if (messageId == 0)
        {
            return BotApiResult.Fail(400, "message_id is required for copyMessage");
        }

        var caption = ReadStringArg(args, "caption");
        var replyMarkup = ReadReplyMarkupArg(args);

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var toPeer = ResolvePeer(args, selfUser.id);
        var fromPeer = ResolvePeer(args, selfUser.id, "from_chat_id", "from_access_hash");

        var request = new MtProtoMethods.Messages_ForwardMessages
        {
            flags = MtProtoMethods.Messages_ForwardMessages.Flags.drop_author,
            from_peer = fromPeer,
            to_peer = toPeer,
            id = [(int)messageId],
            random_id = [NextRandomId()],
        };

        if (caption != null)
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.drop_media_captions;
        }

        if (ReadBoolArg(args, "disable_notification"))
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.silent;
        }

        if (ReadBoolArg(args, "protect_content"))
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.noforwards;
        }

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= MtProtoMethods.Messages_ForwardMessages.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var result = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        var forwarded = BuildForwardedMessage(result, toPeer, selfUser);

        // Caption (или reply_markup) заданы явно → применяем их к скопированному сообщению вторым шагом:
        // messages.editMessage по to_peer + новому message_id. Forward сам не умеет задавать caption/reply_markup.
        var hasCaption = caption != null;
        var hasMarkup = replyMarkup != null;
        if (hasCaption || hasMarkup)
        {
            var newMessageId = Convert.ToInt32(forwarded["message_id"]);
            var edit = new MtProtoMethods.Messages_EditMessage { flags = 0, peer = toPeer, id = newMessageId };

            if (hasCaption)
            {
                var (text, entities) = ResolveCaptionAndEntities(args, caption);
                edit.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_message;
                edit.message = text ?? "";
                if (entities is { Length: > 0 })
                {
                    edit.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_entities;
                    edit.entities = entities;
                }
            }

            if (hasMarkup)
            {
                edit.flags |= MtProtoMethods.Messages_EditMessage.Flags.has_reply_markup;
                edit.reply_markup = replyMarkup;
            }

            var editResult = await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(edit, ct).ConfigureAwait(false);
            var edited = BuildEditedMessage(editResult, selfUser);
            return edited is null ? BotApiResult.Success(forwarded) : BotApiResult.Success(edited);
        }

        return BotApiResult.Success(forwarded);
    }

    /// <summary>answerCallbackQuery: messages.setBotCallbackAnswer → bool.</summary>
    private async Task<BotApiResult> AnswerCallbackQueryAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var queryId = ReadIntArg(args, "callback_query_id", 0);
        if (queryId == 0)
        {
            return BotApiResult.Fail(400, "callback_query_id is required for answerCallbackQuery");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var request = new MtProtoMethods.Messages_SetBotCallbackAnswer
        {
            flags = 0,
            query_id = queryId,
            cache_time = 0,
        };

        var text = ReadStringArg(args, "text");
        if (text != null)
        {
            request.flags |= MtProtoMethods.Messages_SetBotCallbackAnswer.Flags.has_message;
            request.message = text;
        }

        if (ReadBoolArg(args, "show_alert"))
        {
            request.flags |= MtProtoMethods.Messages_SetBotCallbackAnswer.Flags.alert;
        }

        var url = ReadStringArg(args, "url");
        if (!string.IsNullOrEmpty(url))
        {
            request.flags |= MtProtoMethods.Messages_SetBotCallbackAnswer.Flags.has_url;
            request.url = url;
        }

        request.cache_time = (int)ReadIntArg(args, "cache_time", 0);
        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>Достаёт updates/users/chats из UpdatesBase (составной или короткий); при неудаче — null-массив.</summary>
    private static void SplitUpdates(
        MtProtoTL.UpdatesBase result,
        out MtProtoTL.Update[] updates,
        out Dictionary<long, MtProtoTL.User> users,
        out Dictionary<long, MtProtoTL.ChatBase> chats)
    {
        users = null;
        chats = null;
        updates = null;
        switch (result)
        {
            case MtProtoTL.Updates u:
                updates = u.updates;
                users = u.users;
                chats = u.chats;
                break;
            case MtProtoTL.UpdatesCombined c:
                updates = c.updates;
                users = c.users;
                chats = c.chats;
                break;
            case MtProtoTL.UpdateShort s:
                updates = [s.update];
                break;
        }
    }

    /// <summary>Извлекает отредактированное сообщение из результата messages.editMessage; null — изменений не было.</summary>
    private static Dictionary<string, object> BuildEditedMessage(MtProtoTL.UpdatesBase result, MtProtoTL.User self)
    {
        SplitUpdates(result, out var updates, out var users, out var chats);
        if (updates == null)
        {
            return null;
        }

        foreach (var u in updates)
        {
            var message = u switch
            {
                MtProtoTL.UpdateEditMessage em => em.message,
                MtProtoTL.UpdateNewMessage nm => nm.message,
                _ => null,
            };

            if (message is MtProtoTL.Message m && m.id > 0)
            {
                var d = BuildMessage(m, users, chats);
                if (self != null)
                {
                    d["from"] = BuildUser(self);
                }

                return d;
            }
        }

        return null;
    }

    /// <summary>Извлекает пересланное сообщение (UpdateNewMessage/UpdateNewChannelMessage или UpdateMessageID).</summary>
    private static Dictionary<string, object> BuildForwardedMessage(
        MtProtoTL.UpdatesBase result, MtProtoTL.InputPeer toPeer, MtProtoTL.User self)
    {
        SplitUpdates(result, out var updates, out var users, out var chats);
        if (updates != null)
        {
            foreach (var u in updates)
            {
                if (u is MtProtoTL.UpdateNewMessage nm && nm.message is MtProtoTL.Message m && m.id > 0)
                {
                    var d = BuildMessage(m, users, chats);
                    if (self != null)
                    {
                        d["from"] = BuildUser(self);
                    }

                    return d;
                }
            }

            foreach (var u in updates)
            {
                if (u is MtProtoTL.UpdateMessageID um)
                {
                    var d = BuildSimpleMessage(um.id, DateTime.UtcNow, null, self);
                    var chat = BuildSentChat(toPeer, self?.id ?? 0);
                    if (chat != null)
                    {
                        d["chat"] = chat;
                    }

                    return d;
                }
            }
        }

        throw new BotGatewayException(500, "forwardMessages returned no message update");
    }

    // ===== Tier 3: чат-методы =====

    /// <summary>Кэширует access_hash канала (если он есть в объекте Channel).</summary>
    private static void RememberChannelAccessHash(MtProtoTL.Channel ch)
    {
        if (ch != null && ch.flags.HasFlag(MtProtoTL.Channel.Flags.has_access_hash) && ch.access_hash != 0)
        {
            _channelAccessHash[ch.id] = ch.access_hash;
        }
    }

    /// <summary>Кэширует access_hash всех каналов из словаря chats результата метода.</summary>
    private static void RememberChannelAccessHashes(IReadOnlyDictionary<long, MtProtoTL.ChatBase> chats)
    {
        if (chats == null)
        {
            return;
        }

        foreach (var kv in chats)
        {
            if (kv.Value is MtProtoTL.Channel ch)
            {
                RememberChannelAccessHash(ch);
            }
        }
    }

    /// <summary>Кэширует access_hash пользователя (если он есть в объекте User).</summary>
    private static void RememberUserAccessHash(MtProtoTL.User u)
    {
        if (u != null && u.flags.HasFlag(MtProtoTL.User.Flags.has_access_hash) && u.access_hash != 0)
        {
            _userAccessHash[u.id] = u.access_hash;
        }
    }

    /// <summary>Кэширует access_hash всех пользователей из словаря users результата метода/updates.</summary>
    private static void RememberUserAccessHashes(IReadOnlyDictionary<long, MtProtoTL.User> users)
    {
        if (users == null)
        {
            return;
        }

        foreach (var kv in users)
        {
            RememberUserAccessHash(kv.Value);
        }
    }

    /// <summary>Кэширует access_hash пользователей из массива результата users.getUsers и подобных.</summary>
    private static void RememberUserAccessHashes(IEnumerable<MtProtoTL.UserBase> users)
    {
        if (users == null)
        {
            return;
        }

        foreach (var u in users)
        {
            if (u is MtProtoTL.User user)
            {
                RememberUserAccessHash(user);
            }
        }
    }

    /// <summary>
    /// access_hash пользователя для InputUser/InputPeerUser: явный аргумент (<c>access_hash</c>/<c>user_access_hash</c>)
    /// имеет приоритет, иначе берётся из кэша, заполненного updates/результатами RPC. Настоящий Bot API
    /// не требует access_hash от клиента, поэтому 0 — нормальный случай.
    /// </summary>
    private static long ResolveUserAccessHash(
        IReadOnlyDictionary<string, object> args, long userId, string argName = "access_hash")
    {
        var accessHash = ReadIntArg(args, argName, 0);
        if (accessHash == 0)
        {
            _userAccessHash.TryGetValue(userId, out accessHash);
        }

        return accessHash;
    }

    /// <summary>Собирает InputChannel из Bot API chat_id, беря access_hash из кэша; иначе 400 с подсказкой.</summary>
    private static MtProtoTL.InputChannel ResolveInputChannel(long botApiChatId)
    {
        var channelId = -1000000000000L - botApiChatId;
        if (!_channelAccessHash.TryGetValue(channelId, out var accessHash) || accessHash == 0)
        {
            throw new BotGatewayException(
                400, "channel access_hash not resolved yet: bot must receive an update mentioning this chat first");
        }

        return new MtProtoTL.InputChannel { channel_id = channelId, access_hash = accessHash };
    }

    /// <summary>Требует chat_id группы/канала (без обращения к сети); иначе BotGatewayException(400).</summary>
    private static long RequireGroupChatId(IReadOnlyDictionary<string, object> args, string methodName)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            throw new BotGatewayException(400, $"chat_id is required for {methodName}");
        }

        return chatId;
    }

    /// <summary>Chat DTO Bot API ({id(-100…), type, title, username}) из Chat/Channel MTProto.</summary>
    private static Dictionary<string, object> BuildChatDto(MtProtoTL.ChatBase chatBase)
    {
        switch (chatBase)
        {
            case MtProtoTL.Chat c:
                return new Dictionary<string, object>
                {
                    ["id"] = -c.id,
                    ["type"] = "group",
                    ["title"] = c.title,
                };
            case MtProtoTL.Channel ch:
                RememberChannelAccessHash(ch);
                var d = new Dictionary<string, object>
                {
                    ["id"] = -1000000000000L - ch.id,
                    ["type"] = ch.flags.HasFlag(MtProtoTL.Channel.Flags.megagroup) ? "supergroup" : "channel",
                    ["title"] = ch.title,
                };
                if (ch.flags.HasFlag(MtProtoTL.Channel.Flags.has_username))
                {
                    d["username"] = ch.username;
                }

                return d;
            default:
                return null;
        }
    }

    /// <summary>ChatMember DTO Bot API: {status, user:{…}, custom_title?, until_date?}.</summary>
    private static Dictionary<string, object> BuildChatMember(
        long userId,
        IReadOnlyDictionary<long, MtProtoTL.User> users,
        string status,
        string customTitle = null,
        long? untilDate = null)
    {
        var m = new Dictionary<string, object> { ["status"] = status };
        if (users != null && users.TryGetValue(userId, out var u) && u is MtProtoTL.User user)
        {
            m["user"] = BuildUser(user);
        }
        else
        {
            m["user"] = new Dictionary<string, object> { ["id"] = userId };
        }

        if (!string.IsNullOrEmpty(customTitle))
        {
            m["custom_title"] = customTitle;
        }

        if (untilDate.HasValue)
        {
            m["until_date"] = untilDate.Value;
        }

        return m;
    }

    /// <summary>Извлекает (userId, status) из участника базовой группы.</summary>
    private static (long UserId, string Status) ExtractBasicParticipant(MtProtoTL.ChatParticipantBase p)
        => p switch
        {
            MtProtoTL.ChatParticipantCreator creator => (creator.user_id, "creator"),
            MtProtoTL.ChatParticipantAdmin admin => (admin.user_id, "administrator"),
            MtProtoTL.ChatParticipant member => (member.user_id, "member"),
            _ => (0, null),
        };

    /// <summary>Извлекает (userId, status, customTitle, untilDate) из участника канала/супергруппы.</summary>
    private static (long UserId, string Status, string CustomTitle, long? UntilDate) ExtractChannelParticipant(
        MtProtoTL.ChannelParticipantBase p)
    {
        switch (p)
        {
            case MtProtoTL.ChannelParticipantCreator c:
                return (c.user_id, "creator", string.IsNullOrEmpty(c.rank) ? null : c.rank, null);
            case MtProtoTL.ChannelParticipantAdmin a:
                return (a.user_id, "administrator", string.IsNullOrEmpty(a.rank) ? null : a.rank, null);
            case MtProtoTL.ChannelParticipantSelf s:
                return (s.user_id, "member", string.IsNullOrEmpty(s.rank) ? null : s.rank, null);
            case MtProtoTL.ChannelParticipant m:
                return (m.user_id, "member", string.IsNullOrEmpty(m.rank) ? null : m.rank, null);
            case MtProtoTL.ChannelParticipantBanned b:
                long bannedUntil = 0;
                if (b.banned_rights != null && b.banned_rights.until_date != default)
                {
                    bannedUntil = new DateTimeOffset(b.banned_rights.until_date.ToUniversalTime()).ToUnixTimeSeconds();
                }

                return (PeerUserId(b.peer), "kicked", null, bannedUntil);
            case MtProtoTL.ChannelParticipantLeft l:
                return (PeerUserId(l.peer), "left", null, null);
            default:
                return (0, null, null, null);
        }
    }

    private static long PeerUserId(MtProtoTL.Peer peer) => peer is MtProtoTL.PeerUser pu ? pu.user_id : 0;

    private static async Task<Dictionary<string, object>[]> GetChannelAdminsAsync(
        MtProtoClient client, long botApiChatId, CancellationToken ct)
    {
        var channel = ResolveInputChannel(botApiChatId);
        var res = await client.InvokeAsync<MtProtoTL.Channels_ChannelParticipants>(
            new MtProtoMethods.Channels_GetParticipants
            {
                channel = channel,
                filter = new MtProtoTL.ChannelParticipantsAdmins(),
                offset = 0,
                limit = 100,
                hash = 0,
            }, ct).ConfigureAwait(false);
        if (res == null)
        {
            return [];
        }

        RememberChannelAccessHashes(res.chats);
        var result = new List<Dictionary<string, object>>();
        foreach (var p in res.participants)
        {
            var (userId, status, customTitle, _) = ExtractChannelParticipant(p);
            if (userId == 0)
            {
                continue;
            }

            result.Add(BuildChatMember(userId, res.users, status, customTitle));
        }

        return [.. result];
    }

    /// <summary>getChat: private→users.getUsers; group→messages.getChats; channel→channels.getChannels.</summary>
    private async Task<BotApiResult> GetChatAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChat");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        if (chatId > 0)
        {
            var users = await client.GetUsersAsync(
                [new MtProtoTL.InputUser { user_id = chatId, access_hash = ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            // A missing user is a CLIENT error, not a server fault: the real Bot API answers
            // 400 "Bad Request: chat not found" for an unknown chat_id.
            if (users is not { Length: > 0 } || users[0] is not MtProtoTL.User u)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            var userChat = BuildUser(u);
            userChat["type"] = "private";
            return BotApiResult.Success(userChat);
        }

        if (chatId > -1000000000000L)
        {
            var mtChatId = -chatId;
            var res = await client.InvokeAsync<MtProtoTL.Messages_Chats>(
                new MtProtoMethods.Messages_GetChats { id = [mtChatId] }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            if (res.chats == null || !res.chats.TryGetValue(mtChatId, out var cb) || cb is not MtProtoTL.Chat c)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            return BotApiResult.Success(BuildChatDto(c));
        }

        var channel = ResolveInputChannel(chatId);
        var cres = await client.InvokeAsync<MtProtoTL.Messages_Chats>(
            new MtProtoMethods.Channels_GetChannels { id = [channel] }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(cres.chats);
        if (cres.chats == null || !cres.chats.TryGetValue(channel.channel_id, out var ccb) || ccb is not MtProtoTL.Channel ch)
        {
            return BotApiResult.Fail(400, "Bad Request: chat not found");
        }

        return BotApiResult.Success(BuildChatDto(ch));
    }

    /// <summary>getChatAdministrators: channel→Channels_GetParticipants(Admins); group→Messages_GetFullChat.participants.</summary>
    private async Task<BotApiResult> GetChatAdministratorsAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatAdministrators");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        if (chatId > 0)
        {
            // A private chat has no administrators, but the chat itself must exist.
            var users = await client.GetUsersAsync(
                [new MtProtoTL.InputUser { user_id = chatId, access_hash = ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not MtProtoTL.User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            return BotApiResult.Success(new object[0]);
        }

        if (chatId <= -1000000000000L)
        {
            return BotApiResult.Success(await GetChannelAdminsAsync(client, chatId, ct).ConfigureAwait(false));
        }

        var res = await client.InvokeAsync<MtProtoTL.Messages_ChatFull>(
            new MtProtoMethods.Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(res.chats);
        var result = new List<Dictionary<string, object>>();
        if (res.full_chat is MtProtoTL.ChatFull full && full.participants is MtProtoTL.ChatParticipants cp)
        {
            foreach (var p in cp.participants)
            {
                var (userId, status) = ExtractBasicParticipant(p);
                if (userId == 0 || status is "member" or null)
                {
                    continue;
                }

                result.Add(BuildChatMember(userId, res.users, status));
            }
        }

        return BotApiResult.Success(result);
    }

    /// <summary>getChatMember: channel→Channels_GetParticipant; group→Messages_GetFullChat; private→member(user).</summary>
    private async Task<BotApiResult> GetChatMemberAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatMember");
        }

        var userId = ReadIntArg(args, "user_id", long.MinValue);
        if (userId == long.MinValue)
        {
            return BotApiResult.Fail(400, "user_id is required for getChatMember");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        if (chatId > 0)
        {
            // Validate the chat first: an unknown chat_id must be a 400, not a synthetic "member".
            var users = await client.GetUsersAsync(
                [new MtProtoTL.InputUser { user_id = chatId, access_hash = ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not MtProtoTL.User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            // A private chat contains only the user and this bot; anything else is not a member.
            if (userId != chatId && userId != botId)
            {
                return BotApiResult.Fail(400, "Bad Request: user not found");
            }

            return BotApiResult.Success(BuildChatMember(userId, null, "member"));
        }

        if (chatId <= -1000000000000L)
        {
            var channel = ResolveInputChannel(chatId);
            var res = await client.InvokeAsync<MtProtoTL.Channels_ChannelParticipant>(
                new MtProtoMethods.Channels_GetParticipant
                {
                    channel = channel,
                    participant = new MtProtoTL.InputPeerUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            var (pUserId, status, customTitle, untilDate) = ExtractChannelParticipant(res.participant);
            if (pUserId == 0)
            {
                return BotApiResult.Success(BuildChatMember(userId, res.users, "left"));
            }

            return BotApiResult.Success(BuildChatMember(pUserId, res.users, status, customTitle, untilDate));
        }

        var fres = await client.InvokeAsync<MtProtoTL.Messages_ChatFull>(
            new MtProtoMethods.Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(fres.chats);
        if (fres.full_chat is MtProtoTL.ChatFull full && full.participants is MtProtoTL.ChatParticipants cp)
        {
            foreach (var p in cp.participants)
            {
                var (pUserId, status) = ExtractBasicParticipant(p);
                if (pUserId == userId)
                {
                    return BotApiResult.Success(BuildChatMember(pUserId, fres.users, status));
                }
            }
        }

        return BotApiResult.Success(BuildChatMember(userId, null, "left"));
    }

    /// <summary>getChatMemberCount: group→messages.getFullChat (participants_count); channel→channels.getFullChannel.</summary>
    private async Task<BotApiResult> GetChatMemberCountAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = ReadIntArg(args, "chat_id", long.MinValue);
        if (chatId == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for getChatMemberCount");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        if (chatId > 0)
        {
            // A private chat must exist before we report a member count; otherwise an unknown
            // chat_id would look successful. Real Bot API: 400 "Bad Request: chat not found".
            var users = await client.GetUsersAsync(
                [new MtProtoTL.InputUser { user_id = chatId, access_hash = ResolveUserAccessHash(args, chatId) }], ct)
                .ConfigureAwait(false);
            RememberUserAccessHashes(users);
            if (users is not { Length: > 0 } || users[0] is not MtProtoTL.User)
            {
                return BotApiResult.Fail(400, "Bad Request: chat not found");
            }

            // A private chat holds exactly two members: the user and the bot.
            return BotApiResult.Success(2);
        }

        if (chatId <= -1000000000000L)
        {
            var channel = ResolveInputChannel(chatId);
            var res = await client.InvokeAsync<MtProtoTL.Messages_ChatFull>(
                new MtProtoMethods.Channels_GetFullChannel { channel = channel }, ct).ConfigureAwait(false);
            RememberChannelAccessHashes(res.chats);
            return res.full_chat is MtProtoTL.ChannelFull cf
                ? BotApiResult.Success(cf.participants_count)
                : BotApiResult.Fail(400, "Bad Request: chat not found");
        }

        var fres = await client.InvokeAsync<MtProtoTL.Messages_ChatFull>(
            new MtProtoMethods.Messages_GetFullChat { chat_id = -chatId }, ct).ConfigureAwait(false);
        RememberChannelAccessHashes(fres.chats);
        if (fres.full_chat is MtProtoTL.ChatFull full && full.participants is MtProtoTL.ChatParticipants cp)
        {
            return BotApiResult.Success(cp.participants.Length);
        }

        if (fres.chats != null && fres.chats.TryGetValue(-chatId, out var cb) && cb is MtProtoTL.Chat c)
        {
            return BotApiResult.Success(c.participants_count);
        }

        return BotApiResult.Fail(400, "Bad Request: chat not found");
    }

    /// <summary>setChatTitle: group→messages.editChatTitle; channel/supergroup→channels.editTitle.</summary>
    private async Task<BotApiResult> SetChatTitleAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = RequireGroupChatId(args, "setChatTitle");
        var title = ReadStringArg(args, "title");
        if (string.IsNullOrEmpty(title))
        {
            return BotApiResult.Fail(400, "title is required for setChatTitle");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Channels_EditTitle { channel = ResolveInputChannel(chatId), title = title }, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Messages_EditChatTitle { chat_id = -chatId, title = title }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>setChatDescription: group→messages.editChatAbout (bool); channel/supergroup — не поддержано.</summary>
    private async Task<BotApiResult> SetChatDescriptionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = RequireGroupChatId(args, "setChatDescription");
        if (chatId <= -1000000000000L)
        {
            return BotApiResult.Fail(400, "setChatDescription is not supported for channels/supergroups yet");
        }

        var description = ReadStringArg(args, "description") ?? "";
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var ok = await connection.Client.InvokeBoolAsync(
            new MtProtoMethods.Messages_EditChatAbout
            {
                peer = new MtProtoTL.InputPeerChat { chat_id = -chatId },
                about = description,
            }, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>setChatPhoto: file_id→InputChatPhoto, multipart→InputChatUploadedPhoto; group→editChatPhoto / channel→editPhoto.</summary>
    private async Task<BotApiResult> SetChatPhotoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = RequireGroupChatId(args, "setChatPhoto");
        var photoRaw = ReadMediaArg(args, "photo");
        if (photoRaw == null)
        {
            return BotApiResult.Fail(400, "photo is required for setChatPhoto");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);

        MtProtoTL.InputChatPhotoBase chatPhoto;
        if (photoRaw is BotUploadFile upload)
        {
            var file = await UploadMediaAsync(connection.Client, upload, ct).ConfigureAwait(false);
            chatPhoto = new MtProtoTL.InputChatUploadedPhoto { flags = MtProtoTL.InputChatUploadedPhoto.Flags.has_file, file = file };
        }
        else
        {
            chatPhoto = new MtProtoTL.InputChatPhoto { id = DecodeInputPhoto((string)photoRaw) };
        }

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Channels_EditPhoto { channel = ResolveInputChannel(chatId), photo = chatPhoto }, ct)
                .ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Messages_EditChatPhoto { chat_id = -chatId, photo = chatPhoto }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>leaveChat: group→messages.deleteChatUser(self); channel→channels.leaveChannel.</summary>
    private async Task<BotApiResult> LeaveChatAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = RequireGroupChatId(args, "leaveChat");
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Channels_LeaveChannel { channel = ResolveInputChannel(chatId) }, ct).ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Messages_DeleteChatUser
                {
                    flags = 0,
                    chat_id = -chatId,
                    user_id = new MtProtoTL.InputUserSelf(),
                }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    /// <summary>banChatMember: channel→channels.editBanned(ChatBannedRights.view_messages); group→messages.deleteChatUser(kick).</summary>
    private async Task<BotApiResult> BanChatMemberAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var chatId = RequireGroupChatId(args, "banChatMember");
        var userId = ReadIntArg(args, "user_id", long.MinValue);
        if (userId == long.MinValue)
        {
            return BotApiResult.Fail(400, "user_id is required for banChatMember");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);

        if (chatId <= -1000000000000L)
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Channels_EditBanned
                {
                    channel = ResolveInputChannel(chatId),
                    participant = new MtProtoTL.InputPeerUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                    banned_rights = new MtProtoTL.ChatBannedRights
                    {
                        flags = MtProtoTL.ChatBannedRights.Flags.view_messages,
                        until_date = default,
                    },
                }, ct).ConfigureAwait(false);
        }
        else
        {
            await connection.Client.InvokeAsync<MtProtoTL.UpdatesBase>(
                new MtProtoMethods.Messages_DeleteChatUser
                {
                    flags = 0,
                    chat_id = -chatId,
                    user_id = new MtProtoTL.InputUser { user_id = userId, access_hash = ResolveUserAccessHash(args, userId, "user_access_hash") },
                }, ct).ConfigureAwait(false);
        }

        return BotApiResult.Success(true);
    }

    // ===== Tier 4: sendChatAction/setMyCommands/getMyCommands/answerInlineQuery =====

    /// <summary>Маппит строку action Bot API в SendMessageAction; неизвестное — BotGatewayException(400).</summary>
    private static MtProtoTL.SendMessageAction BuildSendMessageAction(string action)
        => action switch
        {
            "typing" => new MtProtoTL.SendMessageTypingAction(),
            "upload_photo" => new MtProtoTL.SendMessageUploadPhotoAction { progress = 0 },
            "upload_video" => new MtProtoTL.SendMessageUploadVideoAction { progress = 0 },
            "upload_voice" => new MtProtoTL.SendMessageUploadAudioAction { progress = 0 },
            "upload_document" => new MtProtoTL.SendMessageUploadDocumentAction { progress = 0 },
            "choose_sticker" => new MtProtoTL.SendMessageChooseStickerAction(),
            "find_location" => new MtProtoTL.SendMessageGeoLocationAction(),
            "record_video" => new MtProtoTL.SendMessageRecordVideoAction(),
            "record_voice" => new MtProtoTL.SendMessageRecordAudioAction(),
            _ => throw new BotGatewayException(400, $"unsupported chat action '{action}'"),
        };

    /// <summary>Резолв chat_id+access_hash в InputPeer (без обращения к args-словарю); self → InputPeerSelf.</summary>
    private static MtProtoTL.InputPeer ResolvePeerById(long chatId, long accessHash, long selfId)
    {
        if (chatId == selfId)
        {
            return new MtProtoTL.InputPeerSelf();
        }

        if (chatId > 0)
        {
            if (accessHash == 0)
            {
                throw new BotGatewayException(400, "private chat_id requires 'access_hash'");
            }

            return new MtProtoTL.InputPeerUser { user_id = chatId, access_hash = accessHash };
        }

        if (chatId > -1000000000000L)
        {
            return new MtProtoTL.InputPeerChat { chat_id = -chatId };
        }

        if (accessHash == 0)
        {
            throw new BotGatewayException(400, "channel chat_id requires 'access_hash'");
        }

        return new MtProtoTL.InputPeerChannel { channel_id = -1000000000000L - chatId, access_hash = accessHash };
    }

    /// <summary>Собирает BotCommandScope из JSON scope; null → BotCommandScopeDefault; неизвестное — 400.</summary>
    private static MtProtoTL.BotCommandScope BuildBotCommandScope(long selfId, IReadOnlyDictionary<string, object> args)
    {
        var scopeEl = ReadJsonObjectArg(args, "scope");
        if (scopeEl == null)
        {
            return null;
        }

        var type = GetJsonString(scopeEl.Value, "type") ?? "default";
        switch (type)
        {
            case "default":
                return null;
            case "all_private_chats":
                return new MtProtoTL.BotCommandScopeUsers();
            case "all_group_chats":
                return new MtProtoTL.BotCommandScopeChats();
            case "all_chat_administrators":
                return new MtProtoTL.BotCommandScopeChatAdmins();
            case "chat":
            case "chat_administrators":
                var chatId = GetJsonLong(scopeEl.Value, "chat_id");
                var peer = ResolvePeerById(chatId, GetJsonLong(scopeEl.Value, "access_hash"), selfId);
                return type == "chat"
                    ? new MtProtoTL.BotCommandScopePeer { peer = peer }
                    : new MtProtoTL.BotCommandScopePeerAdmins { peer = peer };
            default:
                throw new BotGatewayException(400, $"unsupported BotCommandScope type '{type}'");
        }
    }

    /// <summary>Парсит JSON-массив команд Bot API в BotCommand[]; ошибки — BotGatewayException(400).</summary>
    private static MtProtoTL.BotCommand[] BuildBotCommands(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new BotGatewayException(400, "'commands' must be a JSON array");
        }

        var list = new List<MtProtoTL.BotCommand>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new BotGatewayException(400, "each command must be an object");
            }

            var command = GetJsonString(el, "command");
            var description = GetJsonString(el, "description");
            if (string.IsNullOrEmpty(command))
            {
                throw new BotGatewayException(400, "command is required for each command entry");
            }

            if (string.IsNullOrEmpty(description))
            {
                throw new BotGatewayException(400, "description is required for each command entry");
            }

            list.Add(new MtProtoTL.BotCommand { flags = 0, command = command, description = description });
        }

        return [.. list];
    }

    /// <summary>sendChatAction: messages.setTyping(InputPeer + SendMessageAction) → bool.</summary>
    private async Task<BotApiResult> SendChatActionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (ReadIntArg(args, "chat_id", long.MinValue) == long.MinValue)
        {
            return BotApiResult.Fail(400, "chat_id is required for sendChatAction");
        }

        var action = ReadStringArg(args, "action");
        if (string.IsNullOrEmpty(action))
        {
            return BotApiResult.Fail(400, "action is required for sendChatAction");
        }

        var sendAction = BuildSendMessageAction(action);

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = ResolvePeer(args, selfUser.id);
        var request = new MtProtoMethods.Messages_SetTyping
        {
            flags = 0,
            peer = peer,
            action = sendAction,
        };

        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>setMyCommands: bots.setBotCommands(scope, lang_code, BotCommand[]) → bool.</summary>
    private async Task<BotApiResult> SetMyCommandsAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var commandsEl = ReadJsonArrayArg(args, "commands");
        if (commandsEl == null)
        {
            return BotApiResult.Fail(400, "commands is required for setMyCommands");
        }

        var commands = BuildBotCommands(commandsEl.Value);
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var request = new MtProtoMethods.Bots_SetBotCommands
        {
            scope = BuildBotCommandScope(selfUser.id, args),
            lang_code = ReadStringArg(args, "language_code") ?? "",
            commands = commands,
        };

        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>getMyCommands: bots.getBotCommands → BotCommand[] → [{command, description}…].</summary>
    private async Task<BotApiResult> GetMyCommandsAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var selfUser = await GetSelfUserAsync(connection.Client, ct).ConfigureAwait(false);
        if (selfUser == null)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var request = new MtProtoMethods.Bots_GetBotCommands
        {
            scope = BuildBotCommandScope(selfUser.id, args),
            lang_code = ReadStringArg(args, "language_code") ?? "",
        };

        var commands = await connection.Client.InvokeVectorAsync<MtProtoTL.BotCommand>(request, ct).ConfigureAwait(false);
        var result = new List<Dictionary<string, object>>(commands.Length);
        foreach (var c in commands)
        {
            result.Add(new Dictionary<string, object> { ["command"] = c.command, ["description"] = c.description });
        }

        return BotApiResult.Success(result);
    }

    /// <summary>Парсит результаты answerInlineQuery (только type="article") в InputBotInlineResultBase[].</summary>
    private static MtProtoTL.InputBotInlineResultBase[] BuildInlineResults(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new BotGatewayException(400, "'results' must be a JSON array");
        }

        var list = new List<MtProtoTL.InputBotInlineResultBase>();
        foreach (var el in array.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                throw new BotGatewayException(400, "each result must be an object");
            }

            var type = GetJsonString(el, "type");
            if (type != "article")
            {
                throw new BotGatewayException(400, $"unsupported inline result type '{type}' (only 'article' is supported)");
            }

            var id = GetJsonString(el, "id");
            if (string.IsNullOrEmpty(id))
            {
                throw new BotGatewayException(400, "id is required for inline result");
            }

            var title = GetJsonString(el, "title");
            if (string.IsNullOrEmpty(title))
            {
                throw new BotGatewayException(400, "title is required for article inline result");
            }

            if (!el.TryGetProperty("input_message_content", out var mc) || mc.ValueKind != JsonValueKind.Object)
            {
                throw new BotGatewayException(400, "input_message_content is required for article inline result");
            }

            var messageText = GetJsonString(mc, "message_text");
            if (string.IsNullOrEmpty(messageText))
            {
                throw new BotGatewayException(400, "message_text is required in input_message_content");
            }

            var entities = ParseEntitiesProp(mc, "entities");
            var text = messageText;
            if (entities is not { Length: > 0 })
            {
                (text, entities) = ApplyParseMode(messageText, GetJsonString(mc, "parse_mode"));
            }

            var sendMessage = new MtProtoTL.InputBotInlineMessageText { flags = 0, message = text };
            if (entities is { Length: > 0 })
            {
                sendMessage.flags |= MtProtoTL.InputBotInlineMessageText.Flags.has_entities;
                sendMessage.entities = entities;
            }

            var description = GetJsonString(el, "description");
            var flags = MtProtoTL.InputBotInlineResult.Flags.has_title;
            if (!string.IsNullOrEmpty(description))
            {
                flags |= MtProtoTL.InputBotInlineResult.Flags.has_description;
            }

            list.Add(new MtProtoTL.InputBotInlineResult
            {
                flags = flags,
                id = id,
                type = "article",
                title = title,
                description = description,
                send_message = sendMessage,
            });
        }

        return [.. list];
    }

    /// <summary>answerInlineQuery: messages.setInlineBotResults (только type="article") → bool.</summary>
    private async Task<BotApiResult> AnswerInlineQueryAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var queryId = ReadIntArg(args, "inline_query_id", 0);
        if (queryId == 0)
        {
            return BotApiResult.Fail(400, "inline_query_id is required for answerInlineQuery");
        }

        var resultsEl = ReadJsonArrayArg(args, "results");
        if (resultsEl == null)
        {
            return BotApiResult.Fail(400, "results is required for answerInlineQuery");
        }

        var results = BuildInlineResults(resultsEl.Value);
        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);

        var request = new MtProtoMethods.Messages_SetInlineBotResults
        {
            flags = 0,
            query_id = queryId,
            results = results,
            cache_time = (int)ReadIntArg(args, "cache_time", 300),
        };

        // ⚠️ is_personal ↔ private_ (по семантике Bot API: "кэшировать ответ для одного пользователя");
        // флаг gallery намеренно не ставим (соответствие gallery↔is_personal не подтверждаем).
        if (ReadBoolArg(args, "is_personal"))
        {
            request.flags |= MtProtoMethods.Messages_SetInlineBotResults.Flags.private_;
        }

        var nextOffset = ReadStringArg(args, "next_offset");
        if (!string.IsNullOrEmpty(nextOffset))
        {
            request.flags |= MtProtoMethods.Messages_SetInlineBotResults.Flags.has_next_offset;
            request.next_offset = nextOffset;
        }

        // ⚠️ 'button' (switch_pm) и InlineKeyboardMarkup в results не реализованы.
        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }

    /// <summary>
    /// Резолв Bot API <c>chat_id</c> (или raw-объекта <c>peer</c>) в MTProto <see cref="MtProtoTL.InputPeer"/>.
    /// Пользователям и каналам нужен access_hash: его передают отдельным аргументом, либо целиком как <c>peer</c>.
    /// </summary>
    internal static MtProtoTL.InputPeer ResolvePeer(IReadOnlyDictionary<string, object> args, long selfId)
    {
        if (args != null && args.TryGetValue("peer", out var rawPeer) && rawPeer is JsonElement pe)
        {
            return ParsePeerJson(pe);
        }

        return ResolvePeer(args, selfId, "chat_id", "access_hash");
    }

    /// <summary>
    /// Резолв пира по произвольному ключу chat_id (например, <c>from_chat_id</c>) с отдельным ключом access_hash.
    /// </summary>
    internal static MtProtoTL.InputPeer ResolvePeer(
        IReadOnlyDictionary<string, object> args, long selfId, string chatIdKey, string accessHashKey)
    {
        var chatId = ReadIntArg(args, chatIdKey, long.MinValue);
        if (chatId == long.MinValue)
        {
            throw new BotGatewayException(400, $"{chatIdKey} (or raw 'peer') is required");
        }

        if (chatId == selfId)
        {
            return new MtProtoTL.InputPeerSelf();
        }

        var accessHash = ReadIntArg(args, accessHashKey, 0);

        if (chatId > 0)
        {
            if (accessHash == 0)
            {
                // Настоящий Bot API не требует access_hash от клиента: он известен серверу.
                // Берём его из кэша, заполненного updates/результатами RPC.
                _userAccessHash.TryGetValue(chatId, out accessHash);
            }

            if (accessHash == 0)
            {
                throw new BotGatewayException(400, "Bad Request: chat not found");
            }

            return new MtProtoTL.InputPeerUser { user_id = chatId, access_hash = accessHash };
        }

        if (chatId > -1000000000000L)
        {
            // Базовый групповой чат: MTProto chat_id положительный (= -chat_id).
            return new MtProtoTL.InputPeerChat { chat_id = -chatId };
        }

        // Канал/супергруппа (-100…): channel_id = -1000000000000 - chat_id.
        if (accessHash == 0)
        {
            _channelAccessHash.TryGetValue(-1000000000000L - chatId, out accessHash);
        }

        if (accessHash == 0)
        {
            throw new BotGatewayException(
                400, "channel access_hash not resolved yet: bot must receive an update mentioning this chat first");
        }

        return new MtProtoTL.InputPeerChannel
        {
            channel_id = -1000000000000L - chatId,
            access_hash = accessHash,
        };
    }

    /// <summary>Разбирает raw MTProto peer: {"_":"inputPeerUser","user_id":…,"access_hash":…}.</summary>
    private static MtProtoTL.InputPeer ParsePeerJson(JsonElement pe)
    {
        if (pe.ValueKind != JsonValueKind.Object || !pe.TryGetProperty("_", out var t) ||
            t.ValueKind != JsonValueKind.String)
        {
            throw new BotGatewayException(400, "'peer' must be an object with a '_' discriminator");
        }

        return t.GetString() switch
        {
            "inputPeerSelf" => new MtProtoTL.InputPeerSelf(),
            "inputPeerUser" => new MtProtoTL.InputPeerUser
            {
                user_id = ReadPeerLong(pe, "user_id"),
                access_hash = ReadPeerLong(pe, "access_hash"),
            },
            "inputPeerChat" => new MtProtoTL.InputPeerChat { chat_id = ReadPeerLong(pe, "chat_id") },
            "inputPeerChannel" => new MtProtoTL.InputPeerChannel
            {
                channel_id = ReadPeerLong(pe, "channel_id"),
                access_hash = ReadPeerLong(pe, "access_hash"),
            },
            var other => throw new BotGatewayException(400, $"unsupported peer type '{other}'"),
        };
    }

    private static long ReadPeerLong(JsonElement pe, string name)
    {
        if (!pe.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var val))
        {
            throw new BotGatewayException(400, $"'peer.{name}' must be an integer");
        }

        return val;
    }

    /// <summary>Преобразует результат sendMessage (UpdatesBase) в минимальный Bot API message.</summary>
    internal static Dictionary<string, object> BuildSentMessage(
        MtProtoTL.UpdatesBase result,
        MtProtoTL.InputPeer target,
        MtProtoTL.User self,
        string text)
    {
        switch (result)
        {
            case MtProtoTL.UpdateShortSentMessage ssm:
                {
                    var msg = BuildSimpleMessage(ssm.id, ssm.date, text, self);
                    var chat = BuildSentChat(target, self?.id ?? 0);
                    if (chat != null)
                    {
                        msg["chat"] = chat;
                    }

                    return msg;
                }
            case MtProtoTL.UpdateShortMessage m:
                {
                    var msg = BuildSimpleMessage(m.id, m.date, m.message, self);
                    msg["chat"] = new Dictionary<string, object> { ["id"] = m.user_id, ["type"] = "private" };
                    return msg;
                }
            case MtProtoTL.UpdateShortChatMessage m:
                {
                    var msg = BuildSimpleMessage(m.id, m.date, m.message, self);
                    msg["chat"] = new Dictionary<string, object> { ["id"] = m.chat_id, ["type"] = "group" };
                    return msg;
                }
            case MtProtoTL.Updates u:
                return BuildSentFromUpdates(u.updates, u.users, u.chats, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            case MtProtoTL.UpdatesCombined c:
                return BuildSentFromUpdates(c.updates, c.users, c.chats, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            case MtProtoTL.UpdateShort s when s.update is MtProtoTL.UpdateNewMessage unm:
                return BuildSentFromUpdates([unm], null, null, self)
                    ?? throw new BotGatewayException(500, "sendMessage returned no message update");
            default:
                throw new BotGatewayException(500, $"unsupported sendMessage result type '{result?.GetType().Name}'");
        }
    }

    private static Dictionary<string, object> BuildSentFromUpdates(
        MtProtoTL.Update[] updates,
        Dictionary<long, MtProtoTL.User> users,
        Dictionary<long, MtProtoTL.ChatBase> chats,
        MtProtoTL.User self)
    {
        if (updates == null)
        {
            return null;
        }

        foreach (var u in updates)
        {
            if (u is MtProtoTL.UpdateNewMessage unm && unm.message is MtProtoTL.Message m && m.id > 0)
            {
                var d = BuildMessage(m, users, chats);
                if (self != null)
                {
                    d["from"] = BuildUser(self); // отправитель — сам бот
                }

                return d;
            }
        }
        return null;
    }

    private static Dictionary<string, object> BuildSimpleMessage(
        int id, DateTime date, string text, MtProtoTL.User self)
    {
        var msg = new Dictionary<string, object>
        {
            ["message_id"] = id,
            ["date"] = new DateTimeOffset(date.ToUniversalTime()).ToUnixTimeSeconds(),
        };
        if (!string.IsNullOrEmpty(text))
        {
            msg["text"] = text;
        }

        if (self != null)
        {
            msg["from"] = BuildUser(self);
        }

        return msg;
    }

    /// <summary>Bot API chat {id, type} по InputPeer цели отправки (для sendMessage).</summary>
    internal static Dictionary<string, object> BuildSentChat(MtProtoTL.InputPeer target, long selfId) => target switch
    {
        MtProtoTL.InputPeerUser u => new Dictionary<string, object> { ["id"] = u.user_id, ["type"] = "private" },
        MtProtoTL.InputPeerChat c => new Dictionary<string, object> { ["id"] = c.chat_id, ["type"] = "group" },
        MtProtoTL.InputPeerChannel ch => new Dictionary<string, object> { ["id"] = ch.channel_id, ["type"] = "channel" },
        MtProtoTL.InputPeerSelf => new Dictionary<string, object> { ["id"] = selfId, ["type"] = "private" },
        _ => null,
    };

    /// <summary>Тип отправляемого медиа — нужен для выбора InputMedia* и DocumentAttribute*.</summary>
    private enum MediaType
    {
        Photo,
        Document,
        Audio,
        Video,
        Animation,
        Voice,
        VideoNote,
        Sticker,
    }

    private Task<BotApiResult> SendPhotoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "photo", MediaType.Photo, "sendPhoto");

    private Task<BotApiResult> SendDocumentAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "document", MediaType.Document, "sendDocument");

    private Task<BotApiResult> SendAudioAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "audio", MediaType.Audio, "sendAudio");

    private Task<BotApiResult> SendVideoAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "video", MediaType.Video, "sendVideo");

    private Task<BotApiResult> SendAnimationAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "animation", MediaType.Animation, "sendAnimation");

    private Task<BotApiResult> SendVoiceAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "voice", MediaType.Voice, "sendVoice");

    private Task<BotApiResult> SendVideoNoteAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "video_note", MediaType.VideoNote, "sendVideoNote");

    private Task<BotApiResult> SendStickerAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
        => SendSingleMediaAsync(botId, botToken, apiId, apiHash, args, ct, "sticker", MediaType.Sticker, "sendSticker");

    /// <summary>Общий путь отправки одного медиа: upload (multipart) либо file_id, подпись, reply_to, reply_markup.</summary>
    private async Task<BotApiResult> SendSingleMediaAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct,
        string paramName, MediaType kind, string methodName)
    {
        var media = ReadMediaArg(args, paramName);
        if (media == null)
        {
            return BotApiResult.Fail(400, $"{paramName} is required for {methodName}");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new MtProtoTL.InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not MtProtoTL.User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = ResolvePeer(args, selfUser.id);

        MtProtoTL.InputMedia inputMedia;
        if (media is string fileId)
        {
            inputMedia = BuildReusedMedia(kind, fileId);
        }
        else
        {
            var upload = (BotUploadFile)media;
            var file = await UploadMediaAsync(client, upload, ct).ConfigureAwait(false);
            inputMedia = BuildUploadedMedia(kind, upload, file, args);
        }

        var (caption, entities) = ResolveTextAndEntities(args, ReadStringArg(args, "caption"));
        var request = new MtProtoMethods.Messages_SendMedia
        {
            flags = 0,
            peer = peer,
            media = inputMedia,
            message = caption ?? "",
            random_id = NextRandomId(),
        };

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= MtProtoMethods.Messages_SendMedia.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var replyMarkup = ReadReplyMarkupArg(args);
        if (replyMarkup != null)
        {
            request.flags |= MtProtoMethods.Messages_SendMedia.Flags.has_reply_markup;
            request.reply_markup = replyMarkup;
        }

        if (entities is { Length: > 0 })
        {
            request.flags |= MtProtoMethods.Messages_SendMedia.Flags.has_entities;
            request.entities = entities;
        }

        var result = await client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(BuildSentMessage(result, peer, selfUser, caption ?? ""));
    }

    /// <summary>sendMediaGroup: массив медиа через messages.sendMultiMedia; одно сообщение на элемент альбома.</summary>
    private async Task<BotApiResult> SendMediaGroupAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        var mediaArray = ReadJsonArrayArg(args, "media");
        if (mediaArray == null)
        {
            return BotApiResult.Fail(400, "media is required for sendMediaGroup");
        }

        var connection = await GetConnectionAsync(botId, botToken, apiId, apiHash, ct).ConfigureAwait(false);
        var client = connection.Client;

        var self = await client.GetUsersAsync([new MtProtoTL.InputUserSelf()], ct).ConfigureAwait(false);
        if (self is not { Length: > 0 } || self[0] is not MtProtoTL.User selfUser)
        {
            return BotApiResult.Fail(500, "users.getUsers returned no self user");
        }

        var peer = ResolvePeer(args, selfUser.id);

        var multi = new List<MtProtoTL.InputSingleMedia>();
        foreach (var item in mediaArray.Value.EnumerateArray())
        {
            multi.Add(await BuildSingleMediaAsync(client, item, args, ct).ConfigureAwait(false));
        }

        if (multi.Count == 0)
        {
            return BotApiResult.Fail(400, "media must contain at least one item for sendMediaGroup");
        }

        var request = new MtProtoMethods.Messages_SendMultiMedia
        {
            flags = 0,
            peer = peer,
            multi_media = [.. multi],
        };

        var replyTo = ReadReplyToArg(args);
        if (replyTo != null)
        {
            request.flags |= MtProtoMethods.Messages_SendMultiMedia.Flags.has_reply_to;
            request.reply_to = replyTo;
        }

        var result = await client.InvokeAsync<MtProtoTL.UpdatesBase>(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ExtractSentMessages(result, selfUser));
    }

    /// <summary>Собирает один элемент альбома (InputSingleMedia): upload по attach://… либо file_id.</summary>
    private static async Task<MtProtoTL.InputSingleMedia> BuildSingleMediaAsync(
        MtProtoClient client, JsonElement item, IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            throw new BotGatewayException(400, "each sendMediaGroup item must be an object");
        }

        var kind = MediaKindOf(GetJsonString(item, "type"));
        var mediaRef = GetJsonString(item, "media");
        if (string.IsNullOrEmpty(mediaRef))
        {
            throw new BotGatewayException(400, "'media' is required for a sendMediaGroup item");
        }

        MtProtoTL.InputMedia inputMedia;
        if (mediaRef.StartsWith("attach://", StringComparison.Ordinal))
        {
            var key = mediaRef["attach://".Length..];
            if (ReadMediaArg(args, key) is not BotUploadFile upload)
            {
                throw new BotGatewayException(400, $"attached media '{mediaRef}' was not found in the request");
            }

            var file = await UploadMediaAsync(client, upload, ct).ConfigureAwait(false);
            inputMedia = BuildGroupUploadedMedia(kind, upload, file, item);
        }
        else
        {
            inputMedia = BuildReusedMedia(kind, mediaRef);
        }

        var (caption, entities) = ResolveCaptionAndEntities(item);
        var single = new MtProtoTL.InputSingleMedia
        {
            flags = 0,
            media = inputMedia,
            random_id = NextRandomId(),
            message = caption ?? "",
        };

        if (entities is { Length: > 0 })
        {
            single.flags |= MtProtoTL.InputSingleMedia.Flags.has_entities;
            single.entities = entities;
        }

        return single;
    }

    private static MediaType MediaKindOf(string type) => type switch
    {
        "photo" => MediaType.Photo,
        "video" => MediaType.Video,
        "document" => MediaType.Document,
        "audio" => MediaType.Audio,
        _ => throw new BotGatewayException(400, $"unsupported media type '{type}' in sendMediaGroup"),
    };

    /// <summary>Извлекает все сообщения альбома из результата sendMultiMedia в Bot API message-объекты.</summary>
    private static Dictionary<string, object>[] ExtractSentMessages(MtProtoTL.UpdatesBase result, MtProtoTL.User self)
    {
        Dictionary<long, MtProtoTL.User> users = null;
        Dictionary<long, MtProtoTL.ChatBase> chats = null;

        MtProtoTL.Update[] updates;
        switch (result)
        {
            case MtProtoTL.Updates u:
                updates = u.updates;
                users = u.users;
                chats = u.chats;
                break;
            case MtProtoTL.UpdatesCombined c:
                updates = c.updates;
                users = c.users;
                chats = c.chats;
                break;
            case MtProtoTL.UpdateShort s when s.update is MtProtoTL.UpdateNewMessage:
                updates = [s.update];
                break;
            default:
                throw new BotGatewayException(500, $"unsupported sendMediaGroup result type '{result?.GetType().Name}'");
        }

        var list = new List<Dictionary<string, object>>();
        if (updates != null)
        {
            foreach (var u in updates)
            {
                if (u is MtProtoTL.UpdateNewMessage unm && unm.message is MtProtoTL.Message m && m.id > 0)
                {
                    var message = BuildMessage(m, users, chats);
                    if (self != null)
                    {
                        message["from"] = BuildUser(self);
                    }

                    list.Add(message);
                }
            }
        }

        if (list.Count == 0)
        {
            throw new BotGatewayException(500, "sendMediaGroup returned no message updates");
        }

        return [.. list];
    }

    /// <summary>Повторная отправка уже загруженного медиа по file_id (InputMediaPhoto / InputMediaDocument).</summary>
    private static MtProtoTL.InputMedia BuildReusedMedia(MediaType kind, string fileId)
        => kind == MediaType.Photo
            ? new MtProtoTL.InputMediaPhoto { flags = 0, id = DecodeInputPhoto(fileId) }
            : new MtProtoTL.InputMediaDocument { flags = 0, id = DecodeInputDocument(fileId) };

    /// <summary>Новое медиа из загруженного файла (InputMediaUploadedPhoto / InputMediaUploadedDocument).</summary>
    private static MtProtoTL.InputMedia BuildUploadedMedia(
        MediaType kind, BotUploadFile upload, MtProtoTL.InputFileBase file, IReadOnlyDictionary<string, object> args)
    {
        if (kind == MediaType.Photo)
        {
            return new MtProtoTL.InputMediaUploadedPhoto { flags = 0, file = file };
        }

        var mime = string.IsNullOrEmpty(upload.MimeType) ? "application/octet-stream" : upload.MimeType;
        return new MtProtoTL.InputMediaUploadedDocument
        {
            flags = 0,
            file = file,
            mime_type = mime,
            attributes = BuildDocumentAttributes(kind, upload, args),
        };
    }

    /// <summary>Документное медиа в альбоме (параметры читаются из JSON-элемента).</summary>
    private static MtProtoTL.InputMedia BuildGroupUploadedMedia(
        MediaType kind, BotUploadFile upload, MtProtoTL.InputFileBase file, JsonElement item)
    {
        if (kind == MediaType.Photo)
        {
            return new MtProtoTL.InputMediaUploadedPhoto { flags = 0, file = file };
        }

        var mime = string.IsNullOrEmpty(upload.MimeType) ? "application/octet-stream" : upload.MimeType;
        return new MtProtoTL.InputMediaUploadedDocument
        {
            flags = 0,
            file = file,
            mime_type = mime,
            attributes = BuildGroupDocumentAttributes(kind, upload, item),
        };
    }

    /// <summary>Собирает DocumentAttribute* для одиночной отправки (из аргументов Bot API).</summary>
    private static MtProtoTL.DocumentAttribute[] BuildDocumentAttributes(
        MediaType kind, BotUploadFile upload, IReadOnlyDictionary<string, object> args)
    {
        var fileName = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        switch (kind)
        {
            case MediaType.Document:
                return [new MtProtoTL.DocumentAttributeFilename { file_name = fileName }];
            case MediaType.Audio:
                {
                    var audio = new MtProtoTL.DocumentAttributeAudio { flags = 0, duration = (int)ReadIntArg(args, "duration", 0) };
                    var title = ReadStringArg(args, "title");
                    var performer = ReadStringArg(args, "performer");
                    if (!string.IsNullOrEmpty(title))
                    {
                        audio.flags |= MtProtoTL.DocumentAttributeAudio.Flags.has_title;
                        audio.title = title;
                    }

                    if (!string.IsNullOrEmpty(performer))
                    {
                        audio.flags |= MtProtoTL.DocumentAttributeAudio.Flags.has_performer;
                        audio.performer = performer;
                    }

                    return [audio, new MtProtoTL.DocumentAttributeFilename { file_name = fileName }];
                }
            case MediaType.Video:
                {
                    MtProtoTL.DocumentAttributeVideo.Flags flags = 0;
                    if (ReadIntArg(args, "supports_streaming", 1) != 0)
                    {
                        flags |= MtProtoTL.DocumentAttributeVideo.Flags.supports_streaming;
                    }

                    return
                    [
                        new MtProtoTL.DocumentAttributeVideo
                        {
                            flags = flags,
                            duration = ReadIntArg(args, "duration", 0),
                            w = (int)ReadIntArg(args, "width", 0),
                            h = (int)ReadIntArg(args, "height", 0),
                        },
                        new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Animation:
                return
                [
                    new MtProtoTL.DocumentAttributeAnimated(),
                    new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                ];
            case MediaType.Voice:
                return
                [
                    new MtProtoTL.DocumentAttributeAudio { flags = MtProtoTL.DocumentAttributeAudio.Flags.voice, duration = (int)ReadIntArg(args, "duration", 0) },
                    new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                ];
            case MediaType.VideoNote:
                {
                    var length = (int)ReadIntArg(args, "length", 0);
                    return
                    [
                        new MtProtoTL.DocumentAttributeVideo { flags = MtProtoTL.DocumentAttributeVideo.Flags.round_message, duration = ReadIntArg(args, "duration", 0), w = length, h = length },
                        new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Sticker:
                return
                [
                    new MtProtoTL.DocumentAttributeSticker { flags = 0, alt = ReadStringArg(args, "emoji") ?? "", stickerset = null },
                    new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                ];
            default:
                return [new MtProtoTL.DocumentAttributeFilename { file_name = fileName }];
        }
    }

    /// <summary>Собирает DocumentAttribute* для элемента альбома (из JSON-полей элемента).</summary>
    private static MtProtoTL.DocumentAttribute[] BuildGroupDocumentAttributes(
        MediaType kind, BotUploadFile upload, JsonElement item)
    {
        var fileName = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        switch (kind)
        {
            case MediaType.Video:
                {
                    MtProtoTL.DocumentAttributeVideo.Flags flags = 0;
                    if (!GetJsonFalse(item, "supports_streaming"))
                    {
                        flags |= MtProtoTL.DocumentAttributeVideo.Flags.supports_streaming;
                    }

                    return
                    [
                        new MtProtoTL.DocumentAttributeVideo
                        {
                            flags = flags,
                            duration = GetJsonLong(item, "duration"),
                            w = (int)GetJsonLong(item, "width"),
                            h = (int)GetJsonLong(item, "height"),
                        },
                        new MtProtoTL.DocumentAttributeFilename { file_name = fileName },
                    ];
                }
            case MediaType.Audio:
                {
                    var audio = new MtProtoTL.DocumentAttributeAudio { flags = 0, duration = (int)GetJsonLong(item, "duration") };
                    var title = GetJsonString(item, "title");
                    var performer = GetJsonString(item, "performer");
                    if (!string.IsNullOrEmpty(title))
                    {
                        audio.flags |= MtProtoTL.DocumentAttributeAudio.Flags.has_title;
                        audio.title = title;
                    }

                    if (!string.IsNullOrEmpty(performer))
                    {
                        audio.flags |= MtProtoTL.DocumentAttributeAudio.Flags.has_performer;
                        audio.performer = performer;
                    }

                    return [audio, new MtProtoTL.DocumentAttributeFilename { file_name = fileName }];
                }
            default:
                return [new MtProtoTL.DocumentAttributeFilename { file_name = fileName }];
        }
    }

    /// <summary>Загружает файл и строит InputFile / InputFileBig (порог 10 МиБ).</summary>
    private static async Task<MtProtoTL.InputFileBase> UploadMediaAsync(MtProtoClient client, BotUploadFile upload, CancellationToken ct)
    {
        var fileId = NextRandomId();
        var parts = await client.UploadFileAsync(fileId, upload.Stream, ct).ConfigureAwait(false);
        var name = string.IsNullOrEmpty(upload.FileName) ? "file" : upload.FileName;
        return upload.Stream.Length > MtProtoClient.BigFileThreshold
            ? new MtProtoTL.InputFileBig { id = fileId, parts = parts, name = name }
            : new MtProtoTL.InputFile { id = fileId, parts = parts, name = name, md5_checksum = "" };
    }

    private static long NextRandomId() => BitConverter.ToInt64(MtProtoCrypto.RandomBytes(8), 0);

    /// <summary>Читает медиа-аргумент: <c>BotUploadFile</c> (загрузка) или <c>string</c> file_id; отсутствует — null.</summary>
    internal static object ReadMediaArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        return raw is BotUploadFile ? raw : ReadStringArg(args, name);
    }

    /// <summary>Собирает InputReplyToMessage из <c>reply_to_message_id</c>; аргумент отсутствует — null.</summary>
    internal static MtProtoTL.InputReplyTo ReadReplyToArg(IReadOnlyDictionary<string, object> args)
    {
        var replyToMessageId = ReadIntArg(args, "reply_to_message_id", 0);
        if (replyToMessageId == 0)
        {
            return null;
        }

        return new MtProtoTL.InputReplyToMessage { flags = 0, reply_to_msg_id = (int)replyToMessageId };
    }

    /// <summary>Разбирает <c>reply_markup</c> (JSON-объект или строка) в ReplyMarkup; отсутствует/некорректен — null.</summary>
    internal static MtProtoTL.ReplyMarkup ReadReplyMarkupArg(IReadOnlyDictionary<string, object> args)
    {
        if (args == null || !args.TryGetValue("reply_markup", out var raw) || raw == null)
        {
            return null;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return BuildReplyMarkup(je);
        }

        var serialized = ReadStringArg(args, "reply_markup");
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(serialized);
            return BuildReplyMarkup(doc.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MtProtoTL.ReplyMarkup BuildReplyMarkup(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("inline_keyboard", out var inline) && inline.ValueKind == JsonValueKind.Array)
        {
            return new MtProtoTL.ReplyInlineMarkup { flags = 0, rows = BuildInlineButtons(inline) };
        }

        if (root.TryGetProperty("keyboard", out var keyboard) && keyboard.ValueKind == JsonValueKind.Array)
        {
            MtProtoTL.ReplyKeyboardMarkup.Flags flags = 0;
            if (!GetJsonFalse(root, "resize_keyboard"))
            {
                flags |= MtProtoTL.ReplyKeyboardMarkup.Flags.resize;
            }

            if (GetJsonTrue(root, "one_time_keyboard"))
            {
                flags |= MtProtoTL.ReplyKeyboardMarkup.Flags.single_use;
            }

            if (GetJsonTrue(root, "selective"))
            {
                flags |= MtProtoTL.ReplyKeyboardMarkup.Flags.selective;
            }

            var placeholder = GetJsonString(root, "input_field_placeholder");
            if (!string.IsNullOrEmpty(placeholder))
            {
                flags |= MtProtoTL.ReplyKeyboardMarkup.Flags.has_placeholder;
            }

            return new MtProtoTL.ReplyKeyboardMarkup { flags = flags, rows = BuildKeyboardButtons(keyboard), placeholder = placeholder };
        }

        if (GetJsonTrue(root, "force_reply"))
        {
            MtProtoTL.ReplyKeyboardForceReply.Flags flags = 0;
            if (GetJsonTrue(root, "selective"))
            {
                flags |= MtProtoTL.ReplyKeyboardForceReply.Flags.selective;
            }

            var placeholder = GetJsonString(root, "input_field_placeholder");
            if (!string.IsNullOrEmpty(placeholder))
            {
                flags |= MtProtoTL.ReplyKeyboardForceReply.Flags.has_placeholder;
            }

            return new MtProtoTL.ReplyKeyboardForceReply { flags = flags, placeholder = placeholder };
        }

        if (GetJsonTrue(root, "remove_keyboard"))
        {
            MtProtoTL.ReplyKeyboardHide.Flags flags = 0;
            if (GetJsonTrue(root, "selective"))
            {
                flags |= MtProtoTL.ReplyKeyboardHide.Flags.selective;
            }

            return new MtProtoTL.ReplyKeyboardHide { flags = flags };
        }

        return null;
    }

    private static MtProtoTL.KeyboardInlineButtonRow[] BuildInlineButtons(JsonElement rows)
    {
        var result = new List<MtProtoTL.KeyboardInlineButtonRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var buttons = new List<MtProtoTL.KeyboardInlineButton>();
            foreach (var b in row.EnumerateArray())
            {
                var button = BuildInlineButton(b);
                if (button != null)
                {
                    buttons.Add(button);
                }
            }

            result.Add(new MtProtoTL.KeyboardInlineButtonRow { buttons = [.. buttons] });
        }

        return [.. result];
    }

    private static MtProtoTL.KeyboardInlineButton BuildInlineButton(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var text = GetJsonString(b, "text");
        if (text == null)
        {
            return null;
        }

        var callbackData = GetJsonString(b, "callback_data");
        var url = GetJsonString(b, "url");
        var switchInline = GetJsonString(b, "switch_inline_query");
        var switchInlineCurrent = GetJsonString(b, "switch_inline_query_current_chat");
        var webApp = b.TryGetProperty("web_app", out var wa) ? wa : default;

        MtProtoTL.InlineButtonType type;
        if (callbackData != null)
        {
            type = new MtProtoTL.InlineButtonTypeCallback { flags = 0, data = Encoding.UTF8.GetBytes(callbackData) };
        }
        else if (url != null)
        {
            type = new MtProtoTL.InlineButtonTypeUrl { url = url };
        }
        else if (webApp.ValueKind == JsonValueKind.Object && GetJsonString(webApp, "url") is string webUrl and not null)
        {
            type = new MtProtoTL.InlineButtonTypeWebView { url = webUrl };
        }
        else if (switchInline != null)
        {
            type = new MtProtoTL.InlineButtonTypeSwitchInline { flags = 0, query = switchInline };
        }
        else if (switchInlineCurrent != null)
        {
            type = new MtProtoTL.InlineButtonTypeSwitchInline { flags = MtProtoTL.InlineButtonTypeSwitchInline.Flags.same_peer, query = switchInlineCurrent };
        }
        else
        {
            return null; // неизвестный тип inline-кнопки пропускаем
        }

        return new MtProtoTL.KeyboardInlineButton { flags = 0, text = text, type = type };
    }

    private static MtProtoTL.KeyboardButtonRow[] BuildKeyboardButtons(JsonElement rows)
    {
        var result = new List<MtProtoTL.KeyboardButtonRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var buttons = new List<MtProtoTL.KeyboardButton>();
            foreach (var b in row.EnumerateArray())
            {
                var button = BuildKeyboardButton(b);
                if (button != null)
                {
                    buttons.Add(button);
                }
            }

            result.Add(new MtProtoTL.KeyboardButtonRow { buttons = [.. buttons] });
        }

        return [.. result];
    }

    private static MtProtoTL.KeyboardButton BuildKeyboardButton(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var text = GetJsonString(b, "text");
        if (text == null)
        {
            return null;
        }

        MtProtoTL.ButtonType type = null; // buttonTypeDefault — обычная текстовая кнопка
        if (GetJsonTrue(b, "request_contact") || GetJsonTrue(b, "request_phone"))
        {
            type = new MtProtoTL.ButtonTypeRequestPhone();
        }
        else if (GetJsonTrue(b, "request_location"))
        {
            type = new MtProtoTL.ButtonTypeRequestGeoLocation();
        }
        else if (b.TryGetProperty("request_poll", out var poll) && poll.ValueKind == JsonValueKind.Object)
        {
            type = new MtProtoTL.ButtonTypeRequestPoll { flags = 0 };
        }
        else if (b.TryGetProperty("web_app", out var wa) && wa.ValueKind == JsonValueKind.Object
                 && GetJsonString(wa, "url") is string webUrl and not null)
        {
            type = new MtProtoTL.ButtonTypeSimpleWebView { url = webUrl };
        }

        return new MtProtoTL.KeyboardButton { flags = 0, text = text, type = type };
    }

    /// <summary>
    /// Читает сущности текста: готовый массив <c>entities</c> имеет приоритет; иначе применяется
    /// <c>parse_mode</c>. Возвращает (итоговый текст, сущности). HTML разбирается локально, Markdown — нет.
    /// </summary>
    internal static (string Text, MtProtoTL.MessageEntity[] Entities) ResolveTextAndEntities(
        IReadOnlyDictionary<string, object> args, string text)
    {
        var entities = ParseEntitiesArrayArg(args, "entities");
        if (entities is { Length: > 0 })
        {
            return (text, entities);
        }

        return ApplyParseMode(text, ReadStringArg(args, "parse_mode"));
    }

    private static (string Text, MtProtoTL.MessageEntity[] Entities) ApplyParseMode(string text, string parseMode)
    {
        if (string.IsNullOrWhiteSpace(parseMode))
        {
            return (text, null);
        }

        return parseMode switch
        {
            "HTML" => ParseHtml(text ?? ""),
            "Markdown" or "MarkdownV2" => throw new BotGatewayException(400, $"parse_mode '{parseMode}' is not implemented; pass 'entities' instead"),
            _ => throw new BotGatewayException(400, $"unknown parse_mode '{parseMode}'"),
        };
    }

    private static (string Text, MtProtoTL.MessageEntity[] Entities) ResolveCaptionAndEntities(JsonElement item)
    {
        var caption = GetJsonString(item, "caption");
        var entities = ParseEntitiesProp(item, "caption_entities");
        if (entities is { Length: > 0 })
        {
            return (caption, entities);
        }

        return ApplyParseMode(caption, GetJsonString(item, "parse_mode"));
    }

    /// <summary>Разрешает подпись из top-level аргументов Bot API (caption + caption_entities + parse_mode).</summary>
    private static (string Text, MtProtoTL.MessageEntity[] Entities) ResolveCaptionAndEntities(
        IReadOnlyDictionary<string, object> args, string caption)
    {
        var entities = ParseEntitiesArrayArg(args, "caption_entities");
        if (entities is { Length: > 0 })
        {
            return (caption, entities);
        }

        return ApplyParseMode(caption, ReadStringArg(args, "parse_mode"));
    }

    /// <summary>Читает <c>entities</c> из аргументов (JSON-массив или строка) и конвертирует в MTProto MessageEntity[].</summary>
    private static MtProtoTL.MessageEntity[] ParseEntitiesArrayArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return ConvertEntities(je);
        }

        var serialized = ReadStringArg(args, name);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(serialized);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? ConvertEntities(doc.RootElement) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MtProtoTL.MessageEntity[] ParseEntitiesProp(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return ConvertEntities(v);
    }

    private static MtProtoTL.MessageEntity[] ConvertEntities(JsonElement array)
    {
        var list = new List<MtProtoTL.MessageEntity>();
        foreach (var e in array.EnumerateArray())
        {
            var entity = BuildBotApiEntity(e);
            if (entity != null)
            {
                list.Add(entity);
            }
        }

        return list.Count == 0 ? null : [.. list];
    }

    /// <summary>Конвертирует Bot API MessageEntity-объект в MTProto MessageEntity (offset/length берутся как есть).</summary>
    private static MtProtoTL.MessageEntity BuildBotApiEntity(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = GetJsonString(e, "type");
        if (type == null)
        {
            return null;
        }

        var offset = (int)GetJsonLong(e, "offset");
        var length = (int)GetJsonLong(e, "length");
        switch (type)
        {
            case "bold": return new MtProtoTL.MessageEntityBold { offset = offset, length = length };
            case "italic": return new MtProtoTL.MessageEntityItalic { offset = offset, length = length };
            case "underline": return new MtProtoTL.MessageEntityUnderline { offset = offset, length = length };
            case "strikethrough": return new MtProtoTL.MessageEntityStrike { offset = offset, length = length };
            case "spoiler": return new MtProtoTL.MessageEntitySpoiler { offset = offset, length = length };
            case "blockquote": return new MtProtoTL.MessageEntityBlockquote { offset = offset, length = length };
            case "code": return new MtProtoTL.MessageEntityCode { offset = offset, length = length };
            case "pre": return new MtProtoTL.MessageEntityPre { offset = offset, length = length, language = GetJsonString(e, "language") ?? "" };
            case "text_link": return new MtProtoTL.MessageEntityTextUrl { offset = offset, length = length, url = GetJsonString(e, "url") ?? "" };
            case "custom_emoji": return new MtProtoTL.MessageEntityCustomEmoji { offset = offset, length = length, document_id = GetJsonLong(e, "custom_emoji_id") };
            case "text_mention":
                {
                    var userId = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? GetJsonLong(u, "id") : 0;
                    return new MtProtoTL.InputMessageEntityMentionName { offset = offset, length = length, user_id = new MtProtoTL.InputUser { user_id = userId, access_hash = 0 } };
                }
            case "mention": return new MtProtoTL.MessageEntityMention { offset = offset, length = length };
            case "hashtag": return new MtProtoTL.MessageEntityHashtag { offset = offset, length = length };
            case "cashtag": return new MtProtoTL.MessageEntityCashtag { offset = offset, length = length };
            case "bot_command": return new MtProtoTL.MessageEntityBotCommand { offset = offset, length = length };
            case "url": return new MtProtoTL.MessageEntityUrl { offset = offset, length = length };
            case "email": return new MtProtoTL.MessageEntityEmail { offset = offset, length = length };
            case "phone": return new MtProtoTL.MessageEntityPhone { offset = offset, length = length };
            default: return null;
        }
    }

    /// <summary>
    /// Микро-парсер HTML-разметки в текст + MessageEntity[]. Поддерживает b/strong, i/em, code, pre,
    /// a (href), s/strike/del, u, blockquote, tg-spoiler, span[class~=tg-spoiler]. Смещения — в UTF-16 code units.
    /// </summary>
    private static (string Text, MtProtoTL.MessageEntity[] Entities) ParseHtml(string html)
    {
        var sb = new StringBuilder(html.Length);
        var entities = new List<MtProtoTL.MessageEntity>();
        var stack = new Stack<HtmlTag>();

        var i = 0;
        while (i < html.Length)
        {
            if (html[i] != '<')
            {
                sb.Append(html[i]);
                i++;
                continue;
            }

            var closeBracket = html.IndexOf('>', i + 1);
            if (closeBracket < 0)
            {
                sb.Append(html[i]);
                i++;
                continue;
            }

            var raw = html.Substring(i + 1, closeBracket - i - 1);
            i = closeBracket + 1;
            if (raw.Length == 0)
            {
                continue;
            }

            var closing = raw[0] == '/';
            var body = closing ? raw[1..] : raw;
            var space = body.IndexOfAny([' ', '\t']);
            var tagName = (space < 0 ? body : body[..space]).TrimStart().ToLowerInvariant();
            var attrs = space < 0 ? "" : body[(space + 1)..];

            if (closing)
            {
                if (stack.Count > 0 && stack.Peek().Name == tagName)
                {
                    var open = stack.Pop();
                    entities.Add(open.Create(open.Offset, sb.Length - open.Offset));
                }

                continue;
            }

            var tag = CreateHtmlTag(tagName, attrs, sb.Length);
            if (tag != null)
            {
                stack.Push(tag);
            }
        }

        return (sb.ToString(), entities.Count == 0 ? null : [.. entities]);
    }

    /// <summary>Извлекает значение HTML-атрибута (href/language/class) из строки атрибутов.</summary>
    private static string GetHtmlAttribute(string attrs, string name)
    {
        var idx = attrs.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        idx = attrs.IndexOf('=', idx);
        if (idx < 0)
        {
            return null;
        }

        idx++;
        while (idx < attrs.Length && (attrs[idx] == ' ' || attrs[idx] == '"' || attrs[idx] == '\''))
        {
            idx++;
        }

        if (idx >= attrs.Length)
        {
            return null;
        }

        var quote = attrs[idx] is '"' or '\'' ? attrs[idx] : '\0';
        if (quote != '\0')
        {
            idx++;
        }

        var start = idx;
        while (idx < attrs.Length && (quote == '\0' ? !char.IsWhiteSpace(attrs[idx]) : attrs[idx] != quote))
        {
            idx++;
        }

        return attrs[start..idx];
    }

    private static HtmlTag CreateHtmlTag(string tagName, string attrs, int offset)
    {
        switch (tagName)
        {
            case "b":
            case "strong":
                return new HtmlTag("b", offset, (o, l) => new MtProtoTL.MessageEntityBold { offset = o, length = l });
            case "i":
            case "em":
                return new HtmlTag("i", offset, (o, l) => new MtProtoTL.MessageEntityItalic { offset = o, length = l });
            case "code":
                return new HtmlTag("code", offset, (o, l) => new MtProtoTL.MessageEntityCode { offset = o, length = l });
            case "pre":
                return new HtmlTag("pre", offset, (o, l) => new MtProtoTL.MessageEntityPre { offset = o, length = l, language = GetHtmlAttribute(attrs, "language") ?? "" });
            case "a":
                return new HtmlTag("a", offset, (o, l) => new MtProtoTL.MessageEntityTextUrl { offset = o, length = l, url = GetHtmlAttribute(attrs, "href") ?? "" });
            case "s":
            case "strike":
            case "del":
                return new HtmlTag("s", offset, (o, l) => new MtProtoTL.MessageEntityStrike { offset = o, length = l });
            case "u":
                return new HtmlTag("u", offset, (o, l) => new MtProtoTL.MessageEntityUnderline { offset = o, length = l });
            case "blockquote":
                return new HtmlTag("blockquote", offset, (o, l) => new MtProtoTL.MessageEntityBlockquote { offset = o, length = l });
            case "tg-spoiler":
                return new HtmlTag("tg-spoiler", offset, (o, l) => new MtProtoTL.MessageEntitySpoiler { offset = o, length = l });
            case "span":
                var cls = GetHtmlAttribute(attrs, "class") ?? "";
                return cls.Contains("tg-spoiler", StringComparison.OrdinalIgnoreCase)
                    ? new HtmlTag("span", offset, (o, l) => new MtProtoTL.MessageEntitySpoiler { offset = o, length = l })
                    : null;
            default:
                return null;
        }
    }

    private sealed class HtmlTag(string name, int offset, Func<int, int, MtProtoTL.MessageEntity> create)
    {
        public string Name { get; } = name;
        public int Offset { get; } = offset;
        public Func<int, int, MtProtoTL.MessageEntity> Create { get; } = create;
    }

    private static JsonElement? ReadJsonArrayArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            return je;
        }

        var serialized = ReadStringArg(args, name);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(serialized);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Читает JSON-объект из аргументов (POST JsonElement или GET-строка); иначе null.</summary>
    private static JsonElement? ReadJsonObjectArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            return je;
        }

        var serialized = ReadStringArg(args, name);
        if (string.IsNullOrWhiteSpace(serialized))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(serialized);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Собирает Bot API file_id фото (обратная к <see cref="DecodeInputPhoto"/> раскладка):
    /// [0]=type(2), [1:5]=dc_id(int32), [5:13]=id(int64), [13:21]=access_hash(int64), [21:]=file_reference.
    /// </summary>
    internal static string EncodeInputPhoto(long id, long accessHash, int dcId, byte[] fileReference)
    {
        var fr = fileReference ?? [];
        var payload = new byte[21 + fr.Length];
        payload[0] = 2; // photo
        BitConverter.GetBytes(dcId).CopyTo(payload, 1);
        BitConverter.GetBytes(id).CopyTo(payload, 5);
        BitConverter.GetBytes(accessHash).CopyTo(payload, 13);
        fr.CopyTo(payload, 21);
        return ToBase64Url(payload);
    }

    /// <summary>
    /// Собирает Bot API file_id документа (обратная к <see cref="DecodeInputDocument"/> раскладка):
    /// [0]=type(5), [1]=subtype(0), [2:6]=dc_id(int32), [6:14]=id(int64), [14:22]=access_hash(int64), [22:]=file_reference.
    /// </summary>
    internal static string EncodeInputDocument(long id, long accessHash, int dcId, byte[] fileReference)
    {
        var fr = fileReference ?? [];
        var payload = new byte[22 + fr.Length];
        payload[0] = 5; // document
        payload[1] = 0; // generic document subtype
        BitConverter.GetBytes(dcId).CopyTo(payload, 2);
        BitConverter.GetBytes(id).CopyTo(payload, 6);
        BitConverter.GetBytes(accessHash).CopyTo(payload, 14);
        fr.CopyTo(payload, 22);
        return ToBase64Url(payload);
    }

    /// <summary>Стабильный уникальный идентификатор файла для Bot API (не декодируется обратно).</summary>
    internal static string BuildFileUniqueId(byte kind, long id)
    {
        var raw = new byte[9];
        raw[0] = kind;
        BitConverter.GetBytes(id).CopyTo(raw, 1);
        return ToBase64Url(raw);
    }

    private static string ToBase64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// ⚠️ Расшифровка file_id фото в InputPhoto. Формат (Bot API, little-endian):
    /// [0]=type(2), [1:5]=dc_id(int32), [5:13]=id(uint64), [13:21]=access_hash(uint64), [21:]=file_reference.
    /// Смещения сверены с общепринятой раскладкой Bot API file_id, но требуют ручной проверки на реальных ID.
    /// </summary>
    internal static MtProtoTL.InputPhoto DecodeInputPhoto(string fileId)
    {
        var data = DecodeFileIdBytes(fileId);
        if (data.Length < 21 || data[0] != 2)
        {
            throw new BotGatewayException(400, "invalid photo file_id");
        }

        return new MtProtoTL.InputPhoto
        {
            id = BitConverter.ToInt64(data, 5),
            access_hash = BitConverter.ToInt64(data, 13),
            file_reference = data[21..],
        };
    }

    /// <summary>
    /// ⚠️ Расшифровка file_id документа в InputDocument. Формат (Bot API, little-endian):
    /// [0]=type(5|8), [1]=subtype, [2:6]=dc_id(int32), [6:14]=id(uint64), [14:22]=access_hash(uint64), [22:]=file_reference.
    /// Смещения сверены с общепринятой раскладкой Bot API file_id, но требуют ручной проверки на реальных ID.
    /// </summary>
    internal static MtProtoTL.InputDocument DecodeInputDocument(string fileId)
    {
        var data = DecodeFileIdBytes(fileId);
        if (data.Length < 22 || data[0] != 5 && data[0] != 8)
        {
            throw new BotGatewayException(400, "invalid document file_id");
        }

        return new MtProtoTL.InputDocument
        {
            id = BitConverter.ToInt64(data, 6),
            access_hash = BitConverter.ToInt64(data, 14),
            file_reference = data[22..],
        };
    }

    private static byte[] DecodeFileIdBytes(string fileId)
    {
        var normalized = fileId.Replace('-', '+').Replace('_', '/');
        normalized = (normalized.Length % 4) switch
        {
            2 => normalized + "==",
            3 => normalized + "=",
            _ => normalized,
        };

        try
        {
            return Convert.FromBase64String(normalized);
        }
        catch (FormatException)
        {
            throw new BotGatewayException(400, "invalid file_id");
        }
    }

    private static string GetJsonString(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long GetJsonLong(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static bool GetJsonTrue(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool GetJsonFalse(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.False;

    /// <summary>Читает строковый аргумент Bot API (GET string / POST JSON JsonElement).</summary>
    internal static string ReadStringArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            JsonElement je when je.ValueKind == JsonValueKind.Number => je.GetRawText(),
            _ => raw.ToString(),
        };
    }

    /// <summary>Читает allowed_updates: JSON-массив строк или строка через запятую; иначе null (без фильтра).</summary>
    internal static string[] ReadStringListArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        switch (raw)
        {
            case JsonElement je when je.ValueKind == JsonValueKind.Array:
                var list = new List<string>();
                foreach (var item in je.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        list.Add(item.GetString());
                    }
                }

                return [.. list];
            case string s:
                return s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            default:
                return null;
        }
    }

    /// <summary>Фильтр allowed_updates. Сейчас генерируем только "message"; остальные типы отбрасываются.</summary>
    internal static List<Dictionary<string, object>> FilterAllowedUpdates(
        List<Dictionary<string, object>> updates, string[] allowed)
    {
        if (updates == null || updates.Count == 0)
        {
            return updates ?? [];
        }

        if (allowed == null || allowed.Length == 0)
        {
            return updates;
        }

        var result = new List<Dictionary<string, object>>();
        foreach (var u in updates)
        {
            if (u.ContainsKey("message") && ArrayIndexOf(allowed, "message") >= 0)
            {
                result.Add(u);
            }
            // будущие типы (edited_message, channel_post, …) добавляются сюда по мере реализации
        }
        return result;
    }

    private static int ArrayIndexOf(string[] items, string value)
    {
        for (var i = 0; i < items.Length; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private sealed class BotConnection(MtProtoClient client, MtProtoTL.UserBase me)
    {
        public MtProtoClient Client { get; } = client;
        public MtProtoTL.UserBase Me { get; } = me;
    }
}
