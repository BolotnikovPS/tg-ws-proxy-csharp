using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Rich-сообщения (Bot API 10.1+) и разбор HTML-атрибутов.
/// <c>sendRichMessage</c> отправляет содержимое через <c>messages.sendMessage</c> с флагом
/// <c>rich_message</c>: текстового тела у такого сообщения нет, содержимое лежит в дереве
/// page-блоков. Апдейт должен отдавать хотя бы <c>text</c>, иначе клиент видит пустое сообщение.
/// </summary>
public class BotGatewayRichMessageTests
{
    private static (string Text, MtProtoTL.MessageEntity[] Entities) ParseHtml(string html) =>
        BotGateway.ResolveTextAndEntities(new Dictionary<string, object> { ["parse_mode"] = "HTML" }, html);

    [Fact]
    public void Html_LinkAttribute_DoesNotCaptureClosingQuote()
    {
        var (text, entities) = ParseHtml("<a href=\"https://example.org\">link</a>");

        Assert.Equal("link", text);
        var link = Assert.IsType<MtProtoTL.MessageEntityTextUrl>(Assert.Single(entities));
        Assert.Equal("https://example.org", link.url);
    }

    [Fact]
    public void Html_LinkAttribute_SingleQuotesAreSupported()
    {
        var (_, entities) = ParseHtml("<a href='https://example.org/path'>link</a>");

        var link = Assert.IsType<MtProtoTL.MessageEntityTextUrl>(Assert.Single(entities));
        Assert.Equal("https://example.org/path", link.url);
    }

    [Fact]
    public void Html_PreLanguageAttribute_IsRead()
    {
        var (text, entities) = ParseHtml("<pre language=\"csharp\">var x = 1;</pre>");

        Assert.Equal("var x = 1;", text);
        var pre = Assert.IsType<MtProtoTL.MessageEntityPre>(Assert.Single(entities));
        Assert.Equal("csharp", pre.language);
    }

    [Fact]
    public void RenderRichBlocks_FlattensHeadingsParagraphsAndLists()
    {
        var blocks = new MtProtoTL.PageBlock[]
        {
            new MtProtoTL.PageBlockTitle { text = new MtProtoTL.TextPlain { text = "Заголовок" } },
            new MtProtoTL.PageBlockParagraph
            {
                text = new MtProtoTL.TextConcat
                {
                    texts =
                    [
                        new MtProtoTL.TextPlain { text = "жирный " },
                        new MtProtoTL.TextBold { text = new MtProtoTL.TextPlain { text = "фрагмент" } },
                    ],
                },
            },
            new MtProtoTL.PageBlockList
            {
                items =
                [
                    new MtProtoTL.PageListItemText { text = new MtProtoTL.TextPlain { text = "раз" } },
                    new MtProtoTL.PageListItemText { text = new MtProtoTL.TextPlain { text = "два" } },
                ],
            },
        };

        Assert.Equal("Заголовок\nжирный фрагмент\n- раз\n- два", BotGateway.RenderRichBlocks(blocks));
    }

    [Fact]
    public void RenderRichBlocks_OrderedListNumbersFromStart()
    {
        var blocks = new MtProtoTL.PageBlock[]
        {
            new MtProtoTL.PageBlockOrderedList
            {
                flags = MtProtoTL.PageBlockOrderedList.Flags.has_start,
                start = 3,
                items =
                [
                    new MtProtoTL.PageListOrderedItemText { text = new MtProtoTL.TextPlain { text = "три" } },
                    new MtProtoTL.PageListOrderedItemText { text = new MtProtoTL.TextPlain { text = "четыре" } },
                ],
            },
        };

        Assert.Equal("3. три\n4. четыре", BotGateway.RenderRichBlocks(blocks));
    }

    [Fact]
    public void RenderRichBlocks_EmptyOrNull_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, BotGateway.RenderRichBlocks(null!));
        Assert.Equal(string.Empty, BotGateway.RenderRichBlocks([]));
        Assert.Equal(string.Empty, BotGateway.RenderRichBlocks([new MtProtoTL.PageBlockUnsupported()]));
    }

    [Fact]
    public void BuildMessage_RendersRichMessageBlocksAsText()
    {
        var msg = new MtProtoTL.Message
        {
            id = 42,
            peer_id = new MtProtoTL.PeerUser { user_id = 20 },
            date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            rich_message = new MtProtoTL.RichMessage
            {
                blocks =
                [
                    new MtProtoTL.PageBlockParagraph { text = new MtProtoTL.TextPlain { text = "rich body" } },
                ],
            },
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal("rich body", built["text"]);
    }

    [Fact]
    public void BuildMessage_RichMessage_DoesNotOverridePlainText()
    {
        var msg = new MtProtoTL.Message
        {
            id = 43,
            peer_id = new MtProtoTL.PeerUser { user_id = 20 },
            date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            message = "обычный текст",
            rich_message = new MtProtoTL.RichMessage
            {
                blocks =
                [
                    new MtProtoTL.PageBlockParagraph { text = new MtProtoTL.TextPlain { text = "rich body" } },
                ],
            },
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.Equal("обычный текст", built["text"]);
    }

    [Fact]
    public void BuildMessage_EmptyRichMessage_OmitsText()
    {
        var msg = new MtProtoTL.Message
        {
            id = 44,
            peer_id = new MtProtoTL.PeerUser { user_id = 20 },
            date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            rich_message = new MtProtoTL.RichMessage { blocks = [] },
        };

        var built = BotGateway.BuildMessage(msg, null!, null);

        Assert.False(built.ContainsKey("text"));
    }

    [Fact]
    public void RenderRichBlocks_RendersHeadingTypes()
    {
        var blocks = new MtProtoTL.PageBlock[]
        {
            new MtProtoTL.PageBlockHeading1 { text = new MtProtoTL.TextPlain { text = "H1" } },
            new MtProtoTL.PageBlockHeading2 { text = new MtProtoTL.TextPlain { text = "H2" } },
            new MtProtoTL.PageBlockHeading3 { text = new MtProtoTL.TextPlain { text = "H3" } },
            new MtProtoTL.PageBlockHeading6 { text = new MtProtoTL.TextPlain { text = "H6" } },
            new MtProtoTL.PageBlockKicker { text = new MtProtoTL.TextPlain { text = "kicker" } },
        };

        Assert.Equal("H1\nH2\nH3\nH6\nkicker", BotGateway.RenderRichBlocks(blocks));
    }

    [Fact]
    public void RenderRichBlocks_RendersTableRowsAndCover()
    {
        var blocks = new MtProtoTL.PageBlock[]
        {
            new MtProtoTL.PageBlockTable
            {
                title = new MtProtoTL.TextPlain { text = "Итоги" },
                rows =
                [
                    new MtProtoTL.PageTableRow
                    {
                        cells =
                        [
                            new MtProtoTL.PageTableCell { text = new MtProtoTL.TextPlain { text = "a" } },
                            new MtProtoTL.PageTableCell { text = new MtProtoTL.TextPlain { text = "b" } },
                        ],
                    },
                ],
            },
            new MtProtoTL.PageBlockCover
            {
                cover = new MtProtoTL.PageBlockParagraph { text = new MtProtoTL.TextPlain { text = "cover body" } },
            },
        };

        Assert.Equal("Итоги\na | b\ncover body", BotGateway.RenderRichBlocks(blocks));
    }
}
