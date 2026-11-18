#nullable disable
using System.Globalization;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Чтение и валидация аргументов запросов Bot API (числа, флаги, JSON, списки).</summary>
public sealed partial class BotGateway
{
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

    /// <summary>
    /// Bot API <c>disable_notification</c> → <c>flags.silent</c>: доставить без звука на клиентах получателя.
    /// </summary>
    internal static bool ReadSilentFlag(IReadOnlyDictionary<string, object> args) => ReadBoolArg(args, "disable_notification");

    /// <summary>
    /// Bot API <c>protect_content</c> → <c>flags.noforwards</c>: запрет пересылки и сохранения контента.
    /// </summary>
    internal static bool ReadNoForwardsFlag(IReadOnlyDictionary<string, object> args) => ReadBoolArg(args, "protect_content");

    /// <summary>
    /// Bot API <c>until_date</c> (Unix-время снятия бана) → <see cref="ChatBannedRights.until_date"/>.
    /// Ноль и отрицательные значения означают бессрочный бан: <c>WriteTLStamp(default)</c> пишет 0,
    /// что Telegram трактует как «навсегда».
    /// </summary>
    internal static DateTime ToBanUntilDate(long untilDate)
        => untilDate > 0 ? DateTimeOffset.FromUnixTimeSeconds(untilDate).UtcDateTime : default;

    /// <summary>
    /// Отключение превью ссылок → <c>flags.no_webpage</c>. Новый Bot API присылает
    /// <c>link_preview_options.is_disabled</c>, устаревший — <c>disable_web_page_preview</c>;
    /// учитываем оба варианта, чтобы старые клиенты не теряли поведение.
    /// </summary>
    internal static bool ReadNoWebpageFlag(IReadOnlyDictionary<string, object> args)
    {
        var options = ReadJsonObjectArgOrUndefined(args, "link_preview_options");
        if (options.ValueKind == JsonValueKind.Object
            && options.TryGetProperty("is_disabled", out var disabled))
        {
            // link_preview_options приоритетнее устаревшего аргумента: если клиент прислал
            // объект, именно он описывает желаемое поведение (в т.ч. is_disabled = false).
            return disabled.ValueKind == JsonValueKind.True;
        }

        return ReadBoolArg(args, "disable_web_page_preview");
    }

    /// <summary>
    /// Разбирает аргумент <c>rich_message</c> (JSON-объект либо JSON-строка с объектом) в
    /// <c>inputRichMessageHTML</c> или <c>inputRichMessageMarkdown</c>.
    /// Приоритет — <c>html</c>; при отсутствии обоих полей возвращает null.
    /// </summary>
    internal static InputRichMessageBase ReadRichMessageArg(IReadOnlyDictionary<string, object> args)
    {
        if (args == null || !args.TryGetValue("rich_message", out var raw) || raw == null)
        {
            return null;
        }

        JsonElement element;
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            element = je;
        }
        else if (raw is JsonElement js && js.ValueKind == JsonValueKind.String)
        {
            element = ParseJsonObjectOrUndefined(js.GetString());
        }
        else
        {
            var serialized = ReadStringArg(args, "rich_message");
            element = ParseJsonObjectOrUndefined(serialized);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rtl = element.TryGetProperty("rtl", out var rtlProp) && rtlProp.ValueKind == JsonValueKind.True;
        var noAutoLink = element.TryGetProperty("disable_autolink", out var nlProp) && nlProp.ValueKind == JsonValueKind.True;

        // rtl/noautolink объявлены как отдельные вложенные enum в HTML- и Markdown-варианте,
        // поэтому биты считаем как uint и приводим к типу нужного варианта.
        var flagBits = (rtl ? 0x1u : 0u) | (noAutoLink ? 0x2u : 0u);

        var html = GetJsonString(element, "html");
        if (!string.IsNullOrEmpty(html))
        {
            return new InputRichMessageHTML { flags = (InputRichMessageHTML.Flags)flagBits, html = html };
        }

        var markdown = GetJsonString(element, "markdown");
        if (!string.IsNullOrEmpty(markdown))
        {
            return new InputRichMessageMarkdown { flags = (InputRichMessageMarkdown.Flags)flagBits, markdown = markdown };
        }

        return null;
    }

    /// <summary>Парсит JSON-объект из строки; при ошибке разбора или не-объекте возвращает <see cref="JsonValueKind.Undefined"/>.</summary>
    private static JsonElement ParseJsonObjectOrUndefined(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : default;
        }
        catch (JsonException)
        {
            return default;
        }
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
    private static readonly HashSet<string> ChatTargetMethods =
    [
        with(StringComparer.Ordinal),
        "sendMessage",
        "sendRichMessage",
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
        "deleteMessages",
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
    ];

    /// <summary>Методы edit*: цель — пара chat_id+message_id либо inline_message_id.</summary>
    private static readonly HashSet<string> EditTargetMethods =
    [
        with(StringComparer.Ordinal),
        "editMessageText",
        "editMessageCaption",
        "editMessageMedia",
    ];

    /// <summary>Методы, у которых <c>parse_mode</c> реально разбирается в текст/сущности.</summary>
    private static readonly HashSet<string> ParseModeMethods =
    [
        with(StringComparer.Ordinal),
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
    ];

    /// <summary>
    /// Методы управления webhook. Шлюз работает только через long-poll (getUpdates),
    /// поэтому вместо общего 501 отвечаем понятной ошибкой 400.
    /// </summary>
    private static readonly HashSet<string> WebhookMethods =
    [
        with(StringComparer.Ordinal),
        "setWebhook",
        "deleteWebhook",
        "getWebhookInfo",
    ];

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

        // Webhook невозможен по конструкции шлюза: апдейты отдаются только через getUpdates.
        if (WebhookMethods.Contains(method))
        {
            throw new BotGatewayException(400, "webhooks are not supported; this gateway is long-poll only (use getUpdates)");
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

    /// <summary>
    /// Проверяет только значение <c>parse_mode</c>, без построения сущностей. Все три режима
    /// Bot API разбираются локально (<c>HTML</c>, <c>Markdown</c>, <c>MarkdownV2</c>), поэтому
    /// проверка сводится к списку известных значений.
    /// </summary>
    private static void ValidateParseModeValue(string parseMode)
    {
        if (string.IsNullOrWhiteSpace(parseMode) || parseMode is "HTML" or "Markdown" or "MarkdownV2")
        {
            return;
        }

        throw new BotGatewayException(400, $"unknown parse_mode '{parseMode}'");
    }

    /// <summary>Читает медиа-аргумент: <c>BotUploadFile</c> (загрузка) или <c>string</c> file_id; отсутствует — null.</summary>
    internal static object ReadMediaArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        return raw is BotUploadFile ? raw : ReadStringArg(args, name);
    }

    /// <summary>
    /// Собирает <c>InputReplyToMessage</c> из аргументов Bot API: устаревшего <c>reply_to_message_id</c>
    /// и/или объекта <c>reply_parameters</c> (<c>message_id</c>, <c>quote</c>, <c>quote_entities</c>,
    /// <c>quote_position</c>), плюс <c>message_thread_id</c> для форумных топиков.
    /// Аргументов нет — null. <c>allow_sending_without_reply</c> аналога в MTProto не имеет и игнорируется.
    /// </summary>
    internal static InputReplyTo ReadReplyToArg(IReadOnlyDictionary<string, object> args)
    {
        if (args == null)
        {
            return null;
        }

        var replyToMessageId = ReadIntArg(args, "reply_to_message_id", 0);
        var threadId = ReadIntArg(args, "message_thread_id", 0);
        string quoteText = null;
        MessageEntity[] quoteEntities = null;
        var quotePosition = 0;
        var hasQuotePosition = false;

        // Bot API 7.0+: reply_parameters объектом (или JSON-строкой) вместо reply_to_message_id.
        var parameters = ReadJsonObjectArgOrUndefined(args, "reply_parameters");
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            var messageId = ReadIntProp(parameters, "message_id", 0);
            if (messageId != 0)
            {
                replyToMessageId = messageId;
            }

            if (parameters.TryGetProperty("quote", out var quote) && quote.ValueKind == JsonValueKind.String)
            {
                quoteText = quote.GetString();
            }

            quoteEntities = ParseEntitiesProp(parameters, "quote_entities");

            if (parameters.TryGetProperty("quote_position", out var position)
                && position.ValueKind == JsonValueKind.Number
                && position.TryGetInt32(out var pos))
            {
                quotePosition = pos;
                hasQuotePosition = true;
            }
        }

        if (replyToMessageId is <= 0 or > int.MaxValue)
        {
            replyToMessageId = 0;
        }

        // Reply в топик: top_msg_id нужен только когда id топика отличается от id сообщения и это не General (1).
        var topMsgId = 0;
        if (threadId is > 1 and <= int.MaxValue)
        {
            if (replyToMessageId == 0)
            {
                // Отправка «в топик» без явного reply — это reply на корневое сообщение топика.
                replyToMessageId = threadId;
            }

            if (threadId != replyToMessageId)
            {
                topMsgId = (int)threadId;
            }
        }

        if (replyToMessageId == 0)
        {
            return null;
        }

        var flags = 0u;
        if (topMsgId > 0)
        {
            flags |= (uint)InputReplyToMessage.Flags.has_top_msg_id;
        }

        if (!string.IsNullOrEmpty(quoteText))
        {
            flags |= (uint)InputReplyToMessage.Flags.has_quote_text;
            if (quoteEntities is { Length: > 0 })
            {
                flags |= (uint)InputReplyToMessage.Flags.has_quote_entities;
            }

            if (hasQuotePosition)
            {
                flags |= (uint)InputReplyToMessage.Flags.has_quote_offset;
            }
        }
        else
        {
            // Цитата не задана — сопутствующие поля не отправляются и не должны «зависать» в объекте.
            quoteEntities = null;
            quotePosition = 0;
        }

        return new InputReplyToMessage
        {
            flags = (InputReplyToMessage.Flags)flags,
            reply_to_msg_id = (int)replyToMessageId,
            top_msg_id = topMsgId,
            quote_text = quoteText,
            quote_entities = quoteEntities,
            quote_offset = quotePosition,
        };
    }

    /// <summary>Читает JSON-объект из аргументов (JsonElement или сериализованная строка); иначе Undefined.</summary>
    private static JsonElement ReadJsonObjectArgOrUndefined(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return default;
        }

        return raw switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.Object => je,
            JsonElement => default,
            string s => ParseJsonObjectOrUndefined(s),
            _ => ParseJsonObjectOrUndefined(raw.ToString()),
        };
    }

    /// <summary>Читает целочисленное свойство JSON-объекта; отсутствует/не число — fallback.</summary>
    private static long ReadIntProp(JsonElement parent, string name, long fallback)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v))
        {
            return fallback;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => fallback,
        };
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

    /// <summary>
    /// Читает список чисел Bot API (<c>message_ids</c>): JSON-массив чисел, одиночное число или
    /// строка через запятую; null при отсутствии. Нечисловые элементы массива пропускаются.
    /// </summary>
    internal static List<int> ReadIntListArg(IReadOnlyDictionary<string, object> args, string name)
    {
        if (args == null || !args.TryGetValue(name, out var raw) || raw == null)
        {
            return null;
        }

        if (raw is JsonElement { ValueKind: JsonValueKind.Array } array)
        {
            return ReadIntList(array);
        }

        var text = ReadStringArg(args, name);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!text.Contains(',', StringComparison.Ordinal))
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single)
                ? [single]
                : null;
        }

        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static List<int> ReadIntList(JsonElement array)
    {
        var ids = new List<int>();
        foreach (var item in array.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.Number when item.TryGetInt32(out var number):
                    ids.Add(number);
                    break;
                case JsonValueKind.String when int.TryParse(
                    item.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    ids.Add(parsed);
                    break;
            }
        }

        return ids;
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
}
