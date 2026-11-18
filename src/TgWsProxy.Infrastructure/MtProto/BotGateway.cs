#nullable disable
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Exceptions;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>
/// Ядро локального Bot API-сервера: кэш MTProto-соединений по <c>botId</c>, восстановление/создание
/// файловой сессии, логин бота через <c>auth.importBotAuthorization</c> и диспетчеризация методов.
/// HTTP-контракт живёт в <c>TgWsProxy.Web.BotApiEndpoints</c>; сюда приходит уже разобранный вызов.
/// </summary>
public sealed partial class BotGateway(BotApiConfig config, ILogger<BotGateway> logger)
{
    private readonly BotSessionStore _store = new(config.DataDir);

    private readonly byte[] _secret = ResolveSecret(config.Secret);

    private readonly ConcurrentDictionary<long, Task<BotConnection>> _connections = new();

    /// <summary>In-memory кэш скачанных через getFile файлов (file_path → запись). LRU-вытеснение по LastAccessTicks.</summary>
    private readonly ConcurrentDictionary<string, FileCacheEntry> _fileCache = new();

    /// <summary>Суммарный размер байтов в <see cref="_fileCache"/> (ведётся через Interlocked).</summary>
    private long _fileCacheBytes;

    /// <summary>
    /// Кэш access_hash каналов (channel_id → access_hash). Заполняется при обработке <see cref="Channel"/>
    /// из updates/результатов (access_hash присутствует в Channel). Статический: access_hash глобален для канала
    /// и не зависит от конкретного бота/сессии.
    /// </summary>
    private static readonly ConcurrentDictionary<long, long> _channelAccessHash = new();

    /// <summary>
    /// Кэш access_hash пользователей (user_id → access_hash). Заполняется из updates/результатов RPC, где
    /// встречается <see cref="User"/> с access_hash. Нужен, чтобы реальный Bot API-контракт работал:
    /// клиент присылает только <c>chat_id</c> из update, без access_hash (в настоящем Bot API его и нет).
    /// access_hash глобален для аккаунта, поэтому кэш статический.
    /// </summary>
    private static readonly ConcurrentDictionary<long, long> _userAccessHash = new();

    /// <summary>
    /// Очередь ещё не подтверждённых клиентом Bot API-апдейтов по ботам. Telegram отдаёт разницу
    /// updates.difference пачками, а клиент просит не больше <c>limit</c> за вызов, поэтому
    /// невыданный хвост нельзя терять: состояние <c>pts</c> уже сдвинуто и повторно эти сообщения
    /// не придут. <c>update_id</c> присваивается при постановке в очередь и растёт монотонно.
    /// </summary>
    private readonly ConcurrentDictionary<long, UpdateQueue> _pendingUpdates = new();


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
        if (!config.Enabled)
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
                "sendRichMessage" => await SendRichMessageAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
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
                "deleteMessages" => await DeleteMessagesAsync(botId, botToken, apiId, apiHash, args, ct).ConfigureAwait(false),
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
                logger.LogWarning("Bot {BotId} auth key invalidated (401); session dropped for re-login", botId);
            }

            // Флуд-лимит Telegram приходит кодом 420 / FLOOD_WAIT_X, а Bot API отдаёт его как
            // 429 + parameters.retry_after. Без перевода клиент видит неизвестный ему 420 и
            // не знает, сколько ждать.
            if (e.Code == 420 || (e.Message?.StartsWith("FLOOD_WAIT_", StringComparison.Ordinal) ?? false))
            {
                var retryAfter = ParseFloodWaitSeconds(e.Message);
                logger.LogWarning("Bot {BotId} flood wait {RetryAfter}s", botId, retryAfter);
                return BotApiResult.TooManyRequests(retryAfter);
            }

            logger.LogWarning("Bot {BotId} RPC error {Code}: {Message}", botId, e.Code, e.Message);
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

            logger.LogWarning("Bot {BotId} transport error: {Message}; connection dropped", botId, e.Message);
            return BotApiResult.Fail(502, "Bot transport error");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Bot gateway error for {Method} (bot {BotId})", method, botId);
            return BotApiResult.Fail(502, "Internal bot gateway error");
        }
    }

    /// <summary>
    /// Секунды паузы из имени RPC-ошибки <c>FLOOD_WAIT_X</c>. Если разобрать не удалось,
    /// возвращается 1 — минимальная пауза, которую клиент обязан выдержать.
    /// </summary>
    internal static int ParseFloodWaitSeconds(string message)
    {
        if (!string.IsNullOrEmpty(message)
            && message.StartsWith(FloodWaitPrefix, StringComparison.Ordinal)
            && int.TryParse(
                message.AsSpan(FloodWaitPrefix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var seconds)
            && seconds > 0)
        {
            return seconds;
        }

        return 1;
    }
}
