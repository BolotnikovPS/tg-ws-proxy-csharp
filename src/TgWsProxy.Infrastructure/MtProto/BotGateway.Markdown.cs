#nullable disable
using System.Globalization;
using System.Text;
using TgWsProxy.Infrastructure.MtProto.TL;
using static TgWsProxy.Domain.AppConstants.BotApi;

namespace TgWsProxy.Infrastructure.MtProto;

/// <summary>parse_mode=Markdown / MarkdownV2: собственный парсер.</summary>
public sealed partial class BotGateway
{

    /// <summary>
    /// Микро-парсер Markdown (Bot API <c>parse_mode</c>) в текст + MessageEntity[]. Базовый набор
    /// (<c>parse_mode = "Markdown"</c>): <c>*bold*</c>, <c>_italic_</c>, <c>`code`</c>,
    /// <c>```pre```</c> (с языком в первой строке), <c>[текст](url)</c>, а также
    /// <c>[текст](tg://user?id=N)</c> → упоминание по id. В режиме <c>MarkdownV2</c> дополнительно
    /// <c>__underline__</c>, <c>~strike~</c>, <c>||spoiler||</c> и цитата строкой, начинающейся с <c>&gt;</c>.
    /// Экранирование — <c>\</c> перед символом.
    /// Парсер намеренно снисходительный: незакрытая разметка остаётся обычным текстом (Bot API на таком
    /// входе вернул бы <c>400</c>, шлюз же не должен ломать отправку), а удвоенные маркеры
    /// (<c>**bold**</c>, <c>~~strike~~</c>) трактуются как разметка. Смещения — в UTF-16 code units.
    /// </summary>
    private static (string Text, MessageEntity[] Entities) ParseMarkdown(string markdown, bool v2)
    {
        var sb = new StringBuilder(markdown.Length);
        var entities = new List<MessageEntity>();
        ParseMarkdownInto(markdown, v2, sb, entities, 0);
        return (sb.ToString(), entities.Count == 0 ? null : [.. entities]);
    }

    /// <summary>
    /// Разбирает фрагмент Markdown, дописывая обычный текст в <paramref name="sb"/> и найденные сущности —
    /// в <paramref name="entities"/> (смещения сущностей — относительно начала <paramref name="sb"/>).
    /// </summary>
    private static void ParseMarkdownInto(
        string text, bool v2, StringBuilder sb, List<MessageEntity> entities, int depth)
    {
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];

            // Экранирование: '\' отдаёт следующий символ как обычный текст.
            if (c == '\\' && i + 1 < text.Length)
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                i = AppendMarkdownCode(text, i, sb, entities);
                continue;
            }

            if (c == '[' && TryAppendMarkdownLink(text, i, v2, sb, entities, depth, out var afterLink))
            {
                i = afterLink;
                continue;
            }

            // MarkdownV2: цитата — строка, начинающаяся с '>'.
            if (c == '>' && v2 && (i == 0 || text[i - 1] == '\n'))
            {
                i = AppendMarkdownQuote(text, i, sb, entities, depth);
                continue;
            }

            if (IsMarkdownMarker(c, v2) && depth < MaxMarkdownDepth)
            {
                // Удвоенные маркеры (__underline__, ~~strike~~) — только MarkdownV2; в legacy-режиме
                // каждый символ считается отдельным маркером, как в Bot API.
                var run = v2 ? Math.Min(CountRun(text, i, c), 2) : 1;
                var close = run >= MinMarkdownMarkerRun(c) ? FindClosingMarker(text, i + run, c, run) : -1;
                if (close > i + run)
                {
                    var start = sb.Length;
                    var nested = entities.Count;
                    ParseMarkdownInto(text[(i + run)..close], v2, sb, entities, depth + 1);
                    ShiftEntities(entities, nested, start);
                    entities.Add(CreateMarkdownEntity(c, run, start, sb.Length - start));
                    i = close + run;
                    continue;
                }

                // Незакрытая разметка — обычный текст.
                sb.Append(text, i, run);
                i += run;
                continue;
            }

            sb.Append(c);
            i++;
        }
    }

    /// <summary>Обрабатывает <c>`code`</c> и <c>```pre```</c>; возвращает индекс после закрывающего забора.</summary>
    private static int AppendMarkdownCode(string text, int start, StringBuilder sb, List<MessageEntity> entities)
    {
        var fence = CountRun(text, start, '`');

        if (fence >= 3)
        {
            var afterFence = start + fence;
            var language = "";
            var contentStart = afterFence;
            var newline = text.IndexOf('\n', afterFence);
            if (newline >= 0 && text.AsSpan(afterFence, newline - afterFence).IndexOf('`') < 0)
            {
                language = text[afterFence..newline].Trim();
                contentStart = newline + 1;
            }

            var pre = FindClosingMarker(text, contentStart, '`', 3);
            if (pre < 0)
            {
                sb.Append(text, start, fence);
                return afterFence;
            }

            var content = text[contentStart..pre];
            if (content.EndsWith('\n'))
            {
                content = content[..^1];
            }

            var offset = sb.Length;
            sb.Append(content);
            entities.Add(new MessageEntityPre { offset = offset, length = content.Length, language = language });
            return pre + CountRun(text, pre, '`');
        }

        var close = FindClosingMarker(text, start + fence, '`', fence);
        if (close < 0)
        {
            sb.Append(text, start, fence);
            return start + fence;
        }

        var code = text[(start + fence)..close];
        var codeOffset = sb.Length;
        sb.Append(code);
        entities.Add(new MessageEntityCode { offset = codeOffset, length = code.Length });
        return close + CountRun(text, close, '`');
    }

    /// <summary>Пытается разобрать <c>[текст](url)</c>; при неудаче ничего не пишет и возвращает false.</summary>
    private static bool TryAppendMarkdownLink(
        string text, int start, bool v2, StringBuilder sb, List<MessageEntity> entities, int depth, out int next)
    {
        next = start;

        var closeBracket = -1;
        for (var i = start + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == ']')
            {
                closeBracket = i;
                break;
            }

            if (text[i] == '[')
            {
                return false; // вложенные ссылки не поддерживаются
            }
        }

        if (closeBracket < 0 || closeBracket + 1 >= text.Length || text[closeBracket + 1] != '(')
        {
            return false;
        }

        var closeParen = -1;
        var depthParen = 0;
        for (var i = closeBracket + 1; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '(')
            {
                depthParen++;
            }
            else if (text[i] == ')')
            {
                depthParen--;
                if (depthParen == 0)
                {
                    closeParen = i;
                    break;
                }
            }
        }

        if (closeParen < 0)
        {
            return false;
        }

        var label = text[(start + 1)..closeBracket];
        var url = text[(closeBracket + 2)..closeParen];
        if (label.Length == 0 || url.Length == 0)
        {
            return false;
        }

        var labelStart = sb.Length;
        var nested = entities.Count;
        ParseMarkdownInto(label, v2, sb, entities, depth + 1);
        ShiftEntities(entities, nested, labelStart);
        entities.Add(CreateMarkdownLinkEntity(url, labelStart, sb.Length - labelStart));
        next = closeParen + 1;
        return true;
    }

    /// <summary>MarkdownV2-цитата: <c>&gt;</c> в начале строки; возвращает индекс конца строки.</summary>
    private static int AppendMarkdownQuote(
        string text, int start, StringBuilder sb, List<MessageEntity> entities, int depth)
    {
        var contentStart = start + 1;
        if (contentStart < text.Length && text[contentStart] == ' ')
        {
            contentStart++;
        }

        var lineEnd = text.IndexOf('\n', contentStart);
        if (lineEnd < 0)
        {
            lineEnd = text.Length;
        }

        var quoteStart = sb.Length;
        var nested = entities.Count;
        ParseMarkdownInto(text[contentStart..lineEnd], v2: true, sb, entities, depth + 1);
        ShiftEntities(entities, nested, quoteStart);
        var length = sb.Length - quoteStart;
        if (length > 0)
        {
            entities.Add(new MessageEntityBlockquote { offset = quoteStart, length = length });
        }

        return lineEnd;
    }

    private static bool IsMarkdownMarker(char c, bool v2) => c switch
    {
        '*' or '_' => true,
        '~' or '|' => v2,
        _ => false,
    };

    /// <summary>Минимальная длина серии маркера: <c>||spoiler||</c> не бывает одиночным символом.</summary>
    private static int MinMarkdownMarkerRun(char c) => c == '|' ? 2 : 1;

    private static MessageEntity CreateMarkdownEntity(char marker, int run, int offset, int length) => marker switch
    {
        '*' => new MessageEntityBold { offset = offset, length = length },
        '_' => run >= 2
            ? new MessageEntityUnderline { offset = offset, length = length }
            : new MessageEntityItalic { offset = offset, length = length },
        '~' => new MessageEntityStrike { offset = offset, length = length },
        '|' when run >= 2 => new MessageEntitySpoiler { offset = offset, length = length },
        _ => null,
    };

    /// <summary><c>[текст](url)</c>: <c>tg://user?id=N</c> превращается в упоминание по id, остальное — text_url.</summary>
    private static MessageEntity CreateMarkdownLinkEntity(string url, int offset, int length)
    {
        if (url.StartsWith(MentionPrefix, StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(url[MentionPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return new InputMessageEntityMentionName
            {
                offset = offset,
                length = length,
                user_id = new InputUser { user_id = userId, access_hash = 0 },
            };
        }

        return new MessageEntityTextUrl { offset = offset, length = length, url = url };
    }

    /// <summary>Длина серии одинаковых символов, начиная с <paramref name="start"/>.</summary>
    private static int CountRun(string text, int start, char c)
    {
        var i = start;
        while (i < text.Length && text[i] == c)
        {
            i++;
        }

        return i - start;
    }

    /// <summary>Ищет начало серии из не менее чем <paramref name="run"/> символов <paramref name="c"/>.</summary>
    private static int FindClosingMarker(string text, int from, char c, int run)
    {
        var i = from;
        while (i < text.Length)
        {
            if (text[i] != c)
            {
                i++;
                continue;
            }

            var length = CountRun(text, i, c);
            if (length >= run)
            {
                return i;
            }

            i += length;
        }

        return -1;
    }

    /// <summary>Сдвигает смещения сущностей, добавленных при разборе вложенного фрагмента.</summary>
    private static void ShiftEntities(List<MessageEntity> entities, int from, int offset)
    {
        if (offset == 0)
        {
            return;
        }

        for (var i = from; i < entities.Count; i++)
        {
            entities[i].offset += offset;
        }
    }
}
