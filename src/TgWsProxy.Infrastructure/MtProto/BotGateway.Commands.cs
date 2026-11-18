#nullable disable
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>Команды бота, inline-режим и chat actions.</summary>
public sealed partial class BotGateway
{
    // ===== Tier 4: sendChatAction/setMyCommands/getMyCommands/answerInlineQuery =====

    /// <summary>Маппит строку action Bot API в SendMessageAction; неизвестное — BotGatewayException(400).</summary>
    private static SendMessageAction BuildSendMessageAction(string action)
        => action switch
        {
            "typing" => new SendMessageTypingAction(),
            "upload_photo" => new SendMessageUploadPhotoAction { progress = 0 },
            "upload_video" => new SendMessageUploadVideoAction { progress = 0 },
            "upload_voice" => new SendMessageUploadAudioAction { progress = 0 },
            "upload_document" => new SendMessageUploadDocumentAction { progress = 0 },
            "choose_sticker" => new SendMessageChooseStickerAction(),
            "find_location" => new SendMessageGeoLocationAction(),
            "record_video" => new SendMessageRecordVideoAction(),
            "record_voice" => new SendMessageRecordAudioAction(),
            _ => throw new BotGatewayException(400, $"unsupported chat action '{action}'"),
        };

    /// <summary>Собирает BotCommandScope из JSON scope; null → BotCommandScopeDefault; неизвестное — 400.</summary>
    private static BotCommandScope BuildBotCommandScope(long selfId, IReadOnlyDictionary<string, object> args)
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
                return new BotCommandScopeUsers();
            case "all_group_chats":
                return new BotCommandScopeChats();
            case "all_chat_administrators":
                return new BotCommandScopeChatAdmins();
            case "chat":
            case "chat_administrators":
                var chatId = GetJsonLong(scopeEl.Value, "chat_id");
                var peer = ResolvePeerById(chatId, GetJsonLong(scopeEl.Value, "access_hash"), selfId);
                return type == "chat"
                    ? new BotCommandScopePeer { peer = peer }
                    : new BotCommandScopePeerAdmins { peer = peer };
            default:
                throw new BotGatewayException(400, $"unsupported BotCommandScope type '{type}'");
        }
    }

    /// <summary>Парсит JSON-массив команд Bot API в BotCommand[]; ошибки — BotGatewayException(400).</summary>
    private static BotCommand[] BuildBotCommands(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new BotGatewayException(400, "'commands' must be a JSON array");
        }

        var list = new List<BotCommand>();
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

            list.Add(new BotCommand { flags = 0, command = command, description = description });
        }

        return [.. list];
    }

    /// <summary>sendChatAction: messages.setTyping(InputPeer + SendMessageAction) → bool.</summary>
    private async Task<BotApiResult> SendChatActionAsync(
        long botId, string botToken, int? apiId, string apiHash,
        IReadOnlyDictionary<string, object> args, CancellationToken ct)
    {
        if (!HasChatIdArg(args, "chat_id"))
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

        var peer = await ResolvePeerAsync(connection.Client, args, selfUser.id, ct).ConfigureAwait(false);
        var request = new Messages_SetTyping
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

        var request = new Bots_SetBotCommands
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

        var request = new Bots_GetBotCommands
        {
            scope = BuildBotCommandScope(selfUser.id, args),
            lang_code = ReadStringArg(args, "language_code") ?? "",
        };

        var commands = await connection.Client.InvokeVectorAsync<BotCommand>(request, ct).ConfigureAwait(false);
        var result = new List<Dictionary<string, object>>(commands.Length);
        foreach (var c in commands)
        {
            result.Add(new Dictionary<string, object> { ["command"] = c.command, ["description"] = c.description });
        }

        return BotApiResult.Success(result);
    }

    /// <summary>Парсит результаты answerInlineQuery (только type="article") в InputBotInlineResultBase[].</summary>
    private static InputBotInlineResultBase[] BuildInlineResults(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            throw new BotGatewayException(400, "'results' must be a JSON array");
        }

        var list = new List<InputBotInlineResultBase>();
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

            var sendMessage = new InputBotInlineMessageText { flags = 0, message = text };
            if (entities is { Length: > 0 })
            {
                sendMessage.flags |= InputBotInlineMessageText.Flags.has_entities;
                sendMessage.entities = entities;
            }

            var description = GetJsonString(el, "description");
            var flags = InputBotInlineResult.Flags.has_title;
            if (!string.IsNullOrEmpty(description))
            {
                flags |= InputBotInlineResult.Flags.has_description;
            }

            list.Add(new InputBotInlineResult
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

        var request = new Messages_SetInlineBotResults
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
            request.flags |= Messages_SetInlineBotResults.Flags.private_;
        }

        var nextOffset = ReadStringArg(args, "next_offset");
        if (!string.IsNullOrEmpty(nextOffset))
        {
            request.flags |= Messages_SetInlineBotResults.Flags.has_next_offset;
            request.next_offset = nextOffset;
        }

        // ⚠️ 'button' (switch_pm) и InlineKeyboardMarkup в results не реализованы.
        var ok = await connection.Client.InvokeBoolAsync(request, ct).ConfigureAwait(false);
        return BotApiResult.Success(ok);
    }
}
