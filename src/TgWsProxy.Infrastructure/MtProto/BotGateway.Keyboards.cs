#nullable disable
using System.Text;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>reply_markup: inline- и reply-клавиатуры, эхо разметки в ответе.</summary>
public sealed partial class BotGateway
{
    /// <summary>Разбирает <c>reply_markup</c> (JSON-объект или строка) в ReplyMarkup; отсутствует/некорректен — null.</summary>
    internal static ReplyMarkup ReadReplyMarkupArg(IReadOnlyDictionary<string, object> args)
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

    private static ReplyMarkup BuildReplyMarkup(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (root.TryGetProperty("inline_keyboard", out var inline) && inline.ValueKind == JsonValueKind.Array)
        {
            return new ReplyInlineMarkup { flags = 0, rows = BuildInlineButtons(inline) };
        }

        if (root.TryGetProperty("keyboard", out var keyboard) && keyboard.ValueKind == JsonValueKind.Array)
        {
            ReplyKeyboardMarkup.Flags flags = 0;
            if (!GetJsonFalse(root, "resize_keyboard"))
            {
                flags |= ReplyKeyboardMarkup.Flags.resize;
            }

            if (GetJsonTrue(root, "one_time_keyboard"))
            {
                flags |= ReplyKeyboardMarkup.Flags.single_use;
            }

            if (GetJsonTrue(root, "selective"))
            {
                flags |= ReplyKeyboardMarkup.Flags.selective;
            }

            var placeholder = GetJsonString(root, "input_field_placeholder");
            if (!string.IsNullOrEmpty(placeholder))
            {
                flags |= ReplyKeyboardMarkup.Flags.has_placeholder;
            }

            return new ReplyKeyboardMarkup { flags = flags, rows = BuildKeyboardButtons(keyboard), placeholder = placeholder };
        }

        if (GetJsonTrue(root, "force_reply"))
        {
            ReplyKeyboardForceReply.Flags flags = 0;
            if (GetJsonTrue(root, "selective"))
            {
                flags |= ReplyKeyboardForceReply.Flags.selective;
            }

            var placeholder = GetJsonString(root, "input_field_placeholder");
            if (!string.IsNullOrEmpty(placeholder))
            {
                flags |= ReplyKeyboardForceReply.Flags.has_placeholder;
            }

            return new ReplyKeyboardForceReply { flags = flags, placeholder = placeholder };
        }

        if (GetJsonTrue(root, "remove_keyboard"))
        {
            ReplyKeyboardHide.Flags flags = 0;
            if (GetJsonTrue(root, "selective"))
            {
                flags |= ReplyKeyboardHide.Flags.selective;
            }

            return new ReplyKeyboardHide { flags = flags };
        }

        return null;
    }

    private static KeyboardInlineButtonRow[] BuildInlineButtons(JsonElement rows)
    {
        var result = new List<KeyboardInlineButtonRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var buttons = new List<KeyboardInlineButton>();
            foreach (var b in row.EnumerateArray())
            {
                var button = BuildInlineButton(b);
                if (button != null)
                {
                    buttons.Add(button);
                }
            }

            result.Add(new KeyboardInlineButtonRow { buttons = [.. buttons] });
        }

        return [.. result];
    }

    private static KeyboardInlineButton BuildInlineButton(JsonElement b)
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

        InlineButtonType type;
        if (callbackData != null)
        {
            type = new InlineButtonTypeCallback { flags = 0, data = Encoding.UTF8.GetBytes(callbackData) };
        }
        else if (url != null)
        {
            type = new InlineButtonTypeUrl { url = url };
        }
        else if (webApp.ValueKind == JsonValueKind.Object && GetJsonString(webApp, "url") is string webUrl and not null)
        {
            type = new InlineButtonTypeWebView { url = webUrl };
        }
        else if (switchInline != null)
        {
            type = new InlineButtonTypeSwitchInline { flags = 0, query = switchInline };
        }
        else if (switchInlineCurrent != null)
        {
            type = new InlineButtonTypeSwitchInline { flags = InlineButtonTypeSwitchInline.Flags.same_peer, query = switchInlineCurrent };
        }
        else
        {
            return null; // неизвестный тип inline-кнопки пропускаем
        }

        return new KeyboardInlineButton { flags = 0, text = text, type = type };
    }

    /// <summary>
    /// Обратное преобразование: <c>replyInlineMarkup</c> из MTProto в объект Bot API
    /// <c>InlineKeyboardMarkup</c> (поле <c>reply_markup</c> сообщения). Кнопки, которые в Bot API
    /// не выражаются (buy, game, disabled, user_profile), отдаются без параметров — клиент увидит
    /// только текст, но раскладка не потеряется.
    /// </summary>
    internal static Dictionary<string, object> BuildInlineMarkupJson(ReplyInlineMarkup markup)
    {
        var rows = new List<object>();
        foreach (var row in markup.rows ?? [])
        {
            var buttons = new List<object>();
            foreach (var button in row.buttons ?? [])
            {
                if (button == null)
                {
                    continue;
                }

                var json = new Dictionary<string, object> { ["text"] = button.text ?? "" };
                switch (button.type)
                {
                    case InlineButtonTypeCallback callback when callback.data is { Length: > 0 }:
                        json["callback_data"] = Encoding.UTF8.GetString(callback.data);
                        break;
                    case InlineButtonTypeUrl urlButton:
                        json["url"] = urlButton.url ?? "";
                        break;
                    case InlineButtonTypeWebView webView:
                        json["web_app"] = new Dictionary<string, object> { ["url"] = webView.url ?? "" };
                        break;
                    case InlineButtonTypeSwitchInline switchInline:
                        if (switchInline.flags.HasFlag(InlineButtonTypeSwitchInline.Flags.same_peer))
                        {
                            json["switch_inline_query_current_chat"] = switchInline.query ?? "";
                        }
                        else
                        {
                            json["switch_inline_query"] = switchInline.query ?? "";
                        }

                        break;
                    case InlineButtonTypeCopy copy:
                        json["copy_text"] = new Dictionary<string, object> { ["text"] = copy.copy_text ?? "" };
                        break;
                }

                buttons.Add(json);
            }

            rows.Add(buttons);
        }

        var result = new Dictionary<string, object> { ["inline_keyboard"] = rows };
        if (markup.flags.HasFlag(ReplyInlineMarkup.Flags.force_reply))
        {
            result["force_reply"] = true;
        }

        return result;
    }

    private static KeyboardButtonRow[] BuildKeyboardButtons(JsonElement rows)
    {
        var result = new List<KeyboardButtonRow>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var buttons = new List<KeyboardButton>();
            foreach (var b in row.EnumerateArray())
            {
                var button = BuildKeyboardButton(b);
                if (button != null)
                {
                    buttons.Add(button);
                }
            }

            result.Add(new KeyboardButtonRow { buttons = [.. buttons] });
        }

        return [.. result];
    }

    private static KeyboardButton BuildKeyboardButton(JsonElement b)
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

        ButtonType type = null; // buttonTypeDefault — обычная текстовая кнопка
        if (GetJsonTrue(b, "request_contact") || GetJsonTrue(b, "request_phone"))
        {
            type = new ButtonTypeRequestPhone();
        }
        else if (GetJsonTrue(b, "request_location"))
        {
            type = new ButtonTypeRequestGeoLocation();
        }
        else if (b.TryGetProperty("request_poll", out var poll) && poll.ValueKind == JsonValueKind.Object)
        {
            type = new ButtonTypeRequestPoll { flags = 0 };
        }
        else if (b.TryGetProperty("web_app", out var wa) && wa.ValueKind == JsonValueKind.Object
                 && GetJsonString(wa, "url") is string webUrl and not null)
        {
            type = new ButtonTypeSimpleWebView { url = webUrl };
        }

        return new KeyboardButton { flags = 0, text = text, type = type };
    }
}
