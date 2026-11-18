using TgWsProxy.Infrastructure.MtProto;
using MtProtoTL = TgWsProxy.Infrastructure.MtProto.TL;

namespace TgWsProxy.Test;

/// <summary>
/// Разбор <c>parse_mode</c> (HTML / Markdown / MarkdownV2) в текст + MessageEntity[].
/// </summary>
public class BotGatewayMarkdownTests
{
    private static (string Text, MtProtoTL.MessageEntity[] Entities) Parse(string text, string parseMode) =>
        BotGateway.ResolveTextAndEntities(new Dictionary<string, object> { ["parse_mode"] = parseMode }, text);

    [Fact]
    public void MarkdownV2_ParsesBold()
    {
        var (text, entities) = Parse("*bold*", "MarkdownV2");

        Assert.Equal("bold", text);
        var bold = Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
        Assert.Equal(0, bold.offset);
        Assert.Equal(4, bold.length);
    }

    [Fact]
    public void MarkdownV2_ParsesItalicUnderlineStrikeSpoiler()
    {
        var (text, entities) = Parse("_i_ __u__ ~s~ ||sp||", "MarkdownV2");

        Assert.Equal("i u s sp", text);
        Assert.Equal(
            ["Italic:0:1", "Underline:2:1", "Strike:4:1", "Spoiler:6:2"],
            entities.Select(Describe));
    }

    [Fact]
    public void MarkdownV2_DoesNotParseInsideCode()
    {
        var (text, entities) = Parse("`*x*`", "MarkdownV2");

        Assert.Equal("*x*", text);
        var code = Assert.IsType<MtProtoTL.MessageEntityCode>(Assert.Single(entities));
        Assert.Equal(0, code.offset);
        Assert.Equal(3, code.length);
    }

    [Fact]
    public void MarkdownV2_ParsesPreWithLanguage()
    {
        var (text, entities) = Parse("```python\nprint(1)\n```", "MarkdownV2");

        Assert.Equal("print(1)", text);
        var pre = Assert.IsType<MtProtoTL.MessageEntityPre>(Assert.Single(entities));
        Assert.Equal("python", pre.language);
        Assert.Equal(0, pre.offset);
        Assert.Equal(8, pre.length);
    }

    [Fact]
    public void MarkdownV2_ParsesInlineCodeDoesNotParseEscapes()
    {
        // Внутри кода экранирование не действует: бэкслеш остаётся символом.
        var (text, entities) = Parse("`a\\nb`", "MarkdownV2");

        Assert.Equal("a\\nb", text);
        Assert.IsType<MtProtoTL.MessageEntityCode>(Assert.Single(entities));
    }

    [Fact]
    public void MarkdownV2_ParsesTextUrlWithNestedParentheses()
    {
        var (text, entities) = Parse("[site](https://example.com/a(b))", "MarkdownV2");

        Assert.Equal("site", text);
        var link = Assert.IsType<MtProtoTL.MessageEntityTextUrl>(Assert.Single(entities));
        Assert.Equal("https://example.com/a(b)", link.url);
        Assert.Equal(0, link.offset);
        Assert.Equal(4, link.length);
    }

    [Fact]
    public void MarkdownV2_MapsTgUserLinkToMentionById()
    {
        var (text, entities) = Parse("[Ann](tg://user?id=42)", "MarkdownV2");

        Assert.Equal("Ann", text);
        var mention = Assert.IsType<MtProtoTL.InputMessageEntityMentionName>(Assert.Single(entities));
        var mentionUser = Assert.IsType<MtProtoTL.InputUser>(mention.user_id);
        Assert.Equal(42, mentionUser.user_id);
        Assert.Equal(0, mentionUser.access_hash);
        Assert.Equal(0, mention.offset);
        Assert.Equal(3, mention.length);
    }

    [Fact]
    public void MarkdownV2_ParsesBlockquote()
    {
        var (text, entities) = Parse("> quote\nplain", "MarkdownV2");

        Assert.Equal("quote\nplain", text);
        var quote = Assert.IsType<MtProtoTL.MessageEntityBlockquote>(Assert.Single(entities));
        Assert.Equal(0, quote.offset);
        Assert.Equal(5, quote.length);
    }

    [Fact]
    public void MarkdownV2_EscapedMarkersStayLiteral()
    {
        var (text, entities) = Parse("\\*not bold\\*", "MarkdownV2");

        Assert.Equal("*not bold*", text);
        Assert.Null(entities);
    }

    [Fact]
    public void MarkdownV2_UnclosedMarkerStaysLiteral()
    {
        var (text, entities) = Parse("*bold", "MarkdownV2");

        Assert.Equal("*bold", text);
        Assert.Null(entities);
    }

    [Fact]
    public void MarkdownV2_OffsetsAreUtf16CodeUnits()
    {
        // Суррогатная пара (эмодзи) занимает два code unit, поэтому смещение — 2.
        var (text, entities) = Parse("😀*bold*", "MarkdownV2");

        Assert.Equal("😀bold", text);
        var bold = Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
        Assert.Equal(2, bold.offset);
        Assert.Equal(4, bold.length);
    }

    [Fact]
    public void MarkdownV2_ParsesNestedMarkers()
    {
        var (text, entities) = Parse("_a *b* c_", "MarkdownV2");

        Assert.Equal("a b c", text);
        Assert.Equal(["Bold:2:1", "Italic:0:5"], entities.Select(Describe));
    }

    [Fact]
    public void MarkdownV2_MapsBoldOnlyInsideValidPair()
    {
        var (text, entities) = Parse("a *b* c", "MarkdownV2");

        Assert.Equal("a b c", text);
        var bold = Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
        Assert.Equal(2, bold.offset);
        Assert.Equal(1, bold.length);
    }

    [Fact]
    public void LegacyMarkdown_KeepsTildeAndPipeLiteral()
    {
        var (text, entities) = Parse("~x~ ||y||", "Markdown");

        Assert.Equal("~x~ ||y||", text);
        Assert.Null(entities);
    }

    [Fact]
    public void LegacyMarkdown_ParsesBoldItalicCode()
    {
        var (text, entities) = Parse("*b* _i_ `c`", "Markdown");

        Assert.Equal("b i c", text);
        Assert.Equal(["Bold:0:1", "Italic:2:1", "Code:4:1"], entities.Select(Describe));
    }

    [Fact]
    public void LegacyMarkdown_TreatsUnderscorePairAsSeparateMarkers()
    {
        // У legacy-Markdown нет underline: "__u__" — это два маркера italic, посередине «u».
        var (text, entities) = Parse("__u__", "Markdown");

        Assert.Equal("_u_", text);
        var italic = Assert.IsType<MtProtoTL.MessageEntityItalic>(Assert.Single(entities));
        Assert.Equal(1, italic.offset);
        Assert.Equal(1, italic.length);
    }

    [Fact]
    public void LegacyMarkdown_DoubleAsteriskKeepsStrayMarker()
    {
        var (text, entities) = Parse("**b**", "Markdown");

        Assert.Equal("*b*", text);
        var bold = Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
        Assert.Equal(1, bold.offset);
        Assert.Equal(1, bold.length);
    }

    [Fact]
    public void LegacyMarkdown_ParsesPreAndLink()
    {
        var (text, entities) = Parse("```\ncode\n``` [s](https://e.x)", "Markdown");

        Assert.Equal("code s", text);
        Assert.Equal(["Pre:0:4", "TextUrl:5:1"], entities.Select(Describe));
    }

    [Fact]
    public void ParseMode_EntitiesArrayHasPriorityOverMarkdown()
    {
        var args = new Dictionary<string, object>
        {
            ["parse_mode"] = "MarkdownV2",
            ["entities"] = "[{\"type\":\"bold\",\"offset\":0,\"length\":2}]",
        };

        var (text, entities) = BotGateway.ResolveTextAndEntities(args, "*keep* markers");

        // Текст не переразбирается: entities передан явно.
        Assert.Equal("*keep* markers", text);
        var bold = Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
        Assert.Equal(0, bold.offset);
        Assert.Equal(2, bold.length);
    }

    [Fact]
    public void ParseMode_HtmlIsStillParsed()
    {
        var (text, entities) = Parse("<b>x</b>", "HTML");

        Assert.Equal("x", text);
        Assert.IsType<MtProtoTL.MessageEntityBold>(Assert.Single(entities));
    }

    [Fact]
    public void ParseMode_EmptyParseModeKeepsTextAsIs()
    {
        var (text, entities) = Parse("*x*", "");

        Assert.Equal("*x*", text);
        Assert.Null(entities);
    }

    [Theory]
    [InlineData("MarkdownV3")]
    [InlineData("markdown")]
    [InlineData("HTML5")]
    public void ParseMode_UnknownModeThrows400(string parseMode)
    {
        var exception = Assert.Throws<BotGatewayException>(() => Parse("x", parseMode));

        Assert.Equal(400, exception.ErrorCode);
        Assert.Contains("unknown parse_mode", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Краткое описание сущности для сравнения списков: тип, offset, length.</summary>
    private static string Describe(MtProtoTL.MessageEntity entity) => entity switch
    {
        MtProtoTL.MessageEntityBold => $"Bold:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityItalic => $"Italic:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityUnderline => $"Underline:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityStrike => $"Strike:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntitySpoiler => $"Spoiler:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityCode => $"Code:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityPre => $"Pre:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityTextUrl => $"TextUrl:{entity.offset}:{entity.length}",
        MtProtoTL.MessageEntityBlockquote => $"Blockquote:{entity.offset}:{entity.length}",
        MtProtoTL.InputMessageEntityMentionName => $"MentionName:{entity.offset}:{entity.length}",
        _ => $"{entity.GetType().Name}:{entity.offset}:{entity.length}",
    };
}
