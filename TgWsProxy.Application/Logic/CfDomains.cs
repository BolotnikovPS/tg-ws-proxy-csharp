#nullable enable

using System.Text;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Работа с обфусцированным списком CF proxy доменов (порт upstream <c>config.py</c>).
/// </summary>
public static class CfDomains
{
    /// <summary>Суффикс, подставляемый при декодировании домена.</summary>
    private static readonly string Suffix = ".co.uk";

    /// <summary>
    /// Закодированный список доменов по умолчанию (в upstream — <c>_CFPROXY_ENC</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> Encoded =
    [
        "virkgj.com",
        "vmmzovy.com",
        "mkuosckvso.com",
        "zaewayzmplad.com",
        "twdmbzcm.com",
        "awzwsldi.com",
        "clngqrflngqin.com",
        "tjacxbqtj.com",
        "bxaxtxmrw.com",
        "dmohrsgmohcrwb.com",
        "vwbmtmoi.com",
        "khgrre.com",
        "ulihssf.com",
        "tmhqsdqmfpmk.com",
        "xwuwoqbm.com",
        "orgcnunpj.com",
        "zhkuldz.com",
        "zypoljnslxa.com",
        "efabnxaowuzs.com",
        "zaftuzsftqdq.com"
    ];

    /// <summary>
    /// Домены по умолчанию в декодированном виде (первый — <c>pclead.co.uk</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDomains = [.. Encoded.Select(Decode)];

    /// <summary>
    /// Декодирует обфусцированный домен (аналог upstream <c>_dd()</c>).
    /// </summary>
    /// <param name="encoded">Закодированное значение.</param>
    /// <returns>Декодированный домен.</returns>
    public static string Decode(string encoded)
    {
        if (!encoded.EndsWith(".com", StringComparison.Ordinal))
        {
            return encoded;
        }

        var body = encoded[..^4];
        var shift = body.Count(char.IsLetter);
        var sb = new StringBuilder(body.Length + Suffix.Length);
        foreach (var c in body)
        {
            if (char.IsLetter(c))
            {
                var lower = c > '`';
                var baseChar = lower ? 'a' : 'A';
                sb.Append((char)(((c - baseChar - shift) % 26 + 26) % 26 + baseChar));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append(Suffix).ToString();
    }
}
