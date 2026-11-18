#nullable disable
using System.Globalization;
using System.Text;
using System.Text.Json;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>entities, HTML-разбор и рендер rich message.</summary>
public sealed partial class BotGateway
{
    /// <summary>
    /// Читает сущности текста: готовый массив <c>entities</c> имеет приоритет; иначе применяется
    /// <c>parse_mode</c> (HTML / Markdown / MarkdownV2 разбираются локально). Возвращает (итоговый текст, сущности).
    /// </summary>
    internal static (string Text, MessageEntity[] Entities) ResolveTextAndEntities(
        IReadOnlyDictionary<string, object> args, string text)
    {
        var entities = ParseEntitiesArrayArg(args, "entities");
        if (entities is { Length: > 0 })
        {
            return (text, entities);
        }

        return ApplyParseMode(text, ReadStringArg(args, "parse_mode"));
    }

    private static (string Text, MessageEntity[] Entities) ApplyParseMode(string text, string parseMode)
    {
        if (string.IsNullOrWhiteSpace(parseMode))
        {
            return (text, null);
        }

        return parseMode switch
        {
            "HTML" => ParseHtml(text ?? ""),
            "Markdown" => ParseMarkdown(text ?? "", v2: false),
            "MarkdownV2" => ParseMarkdown(text ?? "", v2: true),
            _ => throw new BotGatewayException(400, $"unknown parse_mode '{parseMode}'"),
        };
    }

    private static (string Text, MessageEntity[] Entities) ResolveCaptionAndEntities(JsonElement item)
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
    private static (string Text, MessageEntity[] Entities) ResolveCaptionAndEntities(
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
    private static MessageEntity[] ParseEntitiesArrayArg(IReadOnlyDictionary<string, object> args, string name)
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

    private static MessageEntity[] ParseEntitiesProp(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return ConvertEntities(v);
    }

    private static MessageEntity[] ConvertEntities(JsonElement array)
    {
        var list = new List<MessageEntity>();
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
    internal static MessageEntity BuildBotApiEntity(JsonElement e)
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
            case "bold": return new MessageEntityBold { offset = offset, length = length };
            case "italic": return new MessageEntityItalic { offset = offset, length = length };
            case "underline": return new MessageEntityUnderline { offset = offset, length = length };
            case "strikethrough": return new MessageEntityStrike { offset = offset, length = length };
            case "spoiler": return new MessageEntitySpoiler { offset = offset, length = length };
            case "blockquote": return new MessageEntityBlockquote { offset = offset, length = length };
            case "code": return new MessageEntityCode { offset = offset, length = length };
            case "pre": return new MessageEntityPre { offset = offset, length = length, language = GetJsonString(e, "language") ?? "" };
            case "text_link": return new MessageEntityTextUrl { offset = offset, length = length, url = GetJsonString(e, "url") ?? "" };
            case "custom_emoji": return new MessageEntityCustomEmoji { offset = offset, length = length, document_id = GetJsonLong(e, "custom_emoji_id") };
            case "text_mention":
                {
                    var userId = e.TryGetProperty("user", out var u) && u.ValueKind == JsonValueKind.Object ? GetJsonLong(u, "id") : 0;
                    return new InputMessageEntityMentionName { offset = offset, length = length, user_id = new InputUser { user_id = userId, access_hash = 0 } };
                }
            case "mention": return new MessageEntityMention { offset = offset, length = length };
            case "hashtag": return new MessageEntityHashtag { offset = offset, length = length };
            case "cashtag": return new MessageEntityCashtag { offset = offset, length = length };
            case "bot_command": return new MessageEntityBotCommand { offset = offset, length = length };
            case "url": return new MessageEntityUrl { offset = offset, length = length };
            case "email": return new MessageEntityEmail { offset = offset, length = length };
            case "phone": return new MessageEntityPhone { offset = offset, length = length };
            case "bank_card_number": return new MessageEntityBankCard { offset = offset, length = length };
            default: return null;
        }
    }

    /// <summary>
    /// Микро-парсер HTML-разметки в текст + MessageEntity[]. Поддерживает b/strong, i/em, code, pre,
    /// a (href), s/strike/del, u, blockquote, tg-spoiler, span[class~=tg-spoiler]. Смещения — в UTF-16 code units.
    /// </summary>
    private static (string Text, MessageEntity[] Entities) ParseHtml(string html)
    {
        var sb = new StringBuilder(html.Length);
        var entities = new List<MessageEntity>();
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
        while (idx < attrs.Length && (attrs[idx] == ' ' || attrs[idx] == '\t'))
        {
            idx++;
        }

        if (idx >= attrs.Length)
        {
            return null;
        }

        // Кавычку нельзя «пропускать» вместе с пробелами: иначе она больше не распознаётся как
        // разделитель и попадает в значение — href="https://x" превращался в https://x" ,
        // а Telegram отбрасывал такой text_link вместе с разметкой ссылки.
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
                return new HtmlTag("b", offset, (o, l) => new MessageEntityBold { offset = o, length = l });
            case "i":
            case "em":
                return new HtmlTag("i", offset, (o, l) => new MessageEntityItalic { offset = o, length = l });
            case "code":
                return new HtmlTag("code", offset, (o, l) => new MessageEntityCode { offset = o, length = l });
            case "pre":
                return new HtmlTag("pre", offset, (o, l) => new MessageEntityPre { offset = o, length = l, language = GetHtmlAttribute(attrs, "language") ?? "" });
            case "a":
                return new HtmlTag("a", offset, (o, l) => new MessageEntityTextUrl { offset = o, length = l, url = GetHtmlAttribute(attrs, "href") ?? "" });
            case "s":
            case "strike":
            case "del":
                return new HtmlTag("s", offset, (o, l) => new MessageEntityStrike { offset = o, length = l });
            case "u":
                return new HtmlTag("u", offset, (o, l) => new MessageEntityUnderline { offset = o, length = l });
            case "blockquote":
                return new HtmlTag("blockquote", offset, (o, l) => new MessageEntityBlockquote { offset = o, length = l });
            case "tg-spoiler":
                return new HtmlTag("tg-spoiler", offset, (o, l) => new MessageEntitySpoiler { offset = o, length = l });
            case "span":
                var cls = GetHtmlAttribute(attrs, "class") ?? "";
                return cls.Contains("tg-spoiler", StringComparison.OrdinalIgnoreCase)
                    ? new HtmlTag("span", offset, (o, l) => new MessageEntitySpoiler { offset = o, length = l })
                    : null;
            default:
                return null;
        }
    }


    /// <summary>
    /// Разворачивает дерево page-блоков rich-сообщения в простой текст: блоки разделяются
    /// переводом строки, элементы списка получают префикс, форматирование отбрасывается.
    /// </summary>
    internal static string RenderRichBlocks(PageBlock[] blocks)
    {
        if (blocks == null || blocks.Length == 0)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        AppendRichBlocks(blocks, lines, 0);
        return string.Join("\n", lines.Where(line => line.Length > 0));
    }

    private static void AppendRichBlocks(PageBlock[] blocks, List<string> lines, int depth)
    {
        if (blocks == null || depth > MaxRichTextDepth)
        {
            return;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case PageBlockTitle title:
                    lines.Add(RenderRichText(title.text, depth));
                    break;
                case PageBlockSubtitle subtitle:
                    lines.Add(RenderRichText(subtitle.text, depth));
                    break;
                case PageBlockHeader header:
                    lines.Add(RenderRichText(header.text, depth));
                    break;
                case PageBlockSubheader subheader:
                    lines.Add(RenderRichText(subheader.text, depth));
                    break;
                // Заголовки разметки: в актуальном слое это отдельные типы pageBlockHeading1..6
                // (pageBlockHeader/Subheader остались для старых сообщений).
                case PageBlockKicker kicker:
                    lines.Add(RenderRichText(kicker.text, depth));
                    break;
                case PageBlockHeading1 heading1:
                    lines.Add(RenderRichText(heading1.text, depth));
                    break;
                case PageBlockHeading2 heading2:
                    lines.Add(RenderRichText(heading2.text, depth));
                    break;
                case PageBlockHeading3 heading3:
                    lines.Add(RenderRichText(heading3.text, depth));
                    break;
                case PageBlockHeading4 heading4:
                    lines.Add(RenderRichText(heading4.text, depth));
                    break;
                case PageBlockHeading5 heading5:
                    lines.Add(RenderRichText(heading5.text, depth));
                    break;
                case PageBlockHeading6 heading6:
                    lines.Add(RenderRichText(heading6.text, depth));
                    break;
                case PageBlockMath math:
                    lines.Add(math.source ?? string.Empty);
                    break;
                case PageBlockThinking thinking:
                    lines.Add(RenderRichText(thinking.text, depth));
                    break;
                case PageBlockAuthorDate authorDate:
                    lines.Add(RenderRichText(authorDate.author, depth));
                    break;
                case PageBlockTable table:
                    AppendRichTable(table, lines, depth);
                    break;
                case PageBlockCover cover:
                    AppendRichBlocks([cover.cover], lines, depth + 1);
                    break;
                case PageBlockParagraph paragraph:
                    lines.Add(RenderRichText(paragraph.text, depth));
                    break;
                case PageBlockPreformatted preformatted:
                    lines.Add(RenderRichText(preformatted.text, depth));
                    break;
                case PageBlockFooter footer:
                    lines.Add(RenderRichText(footer.text, depth));
                    break;
                case PageBlockBlockquote quote:
                    lines.Add(RenderRichText(quote.text, depth));
                    lines.Add(RenderRichText(quote.caption, depth));
                    break;
                case PageBlockPullquote pullquote:
                    lines.Add(RenderRichText(pullquote.text, depth));
                    lines.Add(RenderRichText(pullquote.caption, depth));
                    break;
                case PageBlockList list:
                    AppendRichListItems(list.items, lines, depth);
                    break;
                case PageBlockOrderedList ordered:
                    AppendRichOrderedItems(ordered, lines, depth);
                    break;
                case PageBlockDetails details:
                    lines.Add(RenderRichText(details.title, depth));
                    AppendRichBlocks(details.blocks, lines, depth + 1);
                    break;
                case PageBlockBlockquoteBlocks blockquoteBlocks:
                    AppendRichBlocks(blockquoteBlocks.blocks, lines, depth + 1);
                    lines.Add(RenderRichText(blockquoteBlocks.caption, depth));
                    break;
                case PageBlockRelatedArticles related:
                    lines.Add(RenderRichText(related.title, depth));
                    foreach (var article in related.articles ?? [])
                    {
                        lines.Add("- " + (article.title ?? article.url ?? string.Empty));
                    }

                    break;
                case PageBlockPhoto photo:
                    lines.Add(RenderRichText(photo.caption?.text, depth));
                    break;
                case PageBlockVideo video:
                    lines.Add(RenderRichText(video.caption?.text, depth));
                    break;
                case PageBlockDocument document:
                    lines.Add(RenderRichText(document.caption?.text, depth));
                    break;
                case PageBlockDivider:
                    lines.Add(string.Empty);
                    break;
            }
        }
    }

    private static void AppendRichListItems(PageListItem[] items, List<string> lines, int depth)
    {
        foreach (var item in items ?? [])
        {
            switch (item)
            {
                case PageListItemText text:
                    lines.Add("- " + RenderRichText(text.text, depth + 1));
                    break;
                case PageListItemBlocks nested:
                    AppendRichBlocks(nested.blocks, lines, depth + 1);
                    break;
            }
        }
    }

    private static void AppendRichOrderedItems(PageBlockOrderedList list, List<string> lines, int depth)
    {
        var number = list.flags.HasFlag(PageBlockOrderedList.Flags.has_start) ? list.start : 1;
        foreach (var item in list.items ?? [])
        {
            switch (item)
            {
                case PageListOrderedItemText text:
                    lines.Add(number++.ToString(CultureInfo.InvariantCulture) + ". " + RenderRichText(text.text, depth + 1));
                    break;
                case PageListOrderedItemBlocks nested:
                    AppendRichBlocks(nested.blocks, lines, depth + 1);
                    break;
            }
        }
    }

    private static void AppendRichTable(PageBlockTable table, List<string> lines, int depth)
    {
        lines.Add(RenderRichText(table.title, depth));
        foreach (var row in table.rows ?? [])
        {
            var cells = (row.cells ?? [])
                .Select(cell => RenderRichText(cell?.text, depth + 1).Trim())
                .Where(cell => cell.Length > 0);
            var line = string.Join(" | ", cells);
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }
    }

    private static string RenderRichText(RichText text, int depth)
    {
        if (text == null || depth > MaxRichTextDepth)
        {
            return string.Empty;
        }

        return text switch
        {
            TextPlain plain => plain.text ?? string.Empty,
            TextConcat concat => string.Concat((concat.texts ?? []).Select(item => RenderRichText(item, depth + 1))),
            TextBold bold => RenderRichText(bold.text, depth + 1),
            TextItalic italic => RenderRichText(italic.text, depth + 1),
            TextUnderline underline => RenderRichText(underline.text, depth + 1),
            TextStrike strike => RenderRichText(strike.text, depth + 1),
            TextFixed fixedText => RenderRichText(fixedText.text, depth + 1),
            TextMarked marked => RenderRichText(marked.text, depth + 1),
            TextSubscript subscript => RenderRichText(subscript.text, depth + 1),
            TextSuperscript superscript => RenderRichText(superscript.text, depth + 1),
            TextUrl url => RenderRichText(url.text, depth + 1),
            TextEmail email => RenderRichText(email.text, depth + 1),
            TextPhone phone => RenderRichText(phone.text, depth + 1),
            TextMention mention => RenderRichText(mention.text, depth + 1),
            TextAnchor anchor => anchor.name ?? string.Empty,
            TextMath math => math.source ?? string.Empty,
            TextCustomEmoji emoji => emoji.alt ?? string.Empty,
            _ => string.Empty,
        };
    }

    private sealed class HtmlTag(string name, int offset, Func<int, int, MessageEntity> create)
    {
        public string Name { get; } = name;
        public int Offset { get; } = offset;
        public Func<int, int, MessageEntity> Create { get; } = create;
    }
}
