using System.Security.Cryptography;
using System.Text;

namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Проверка авторизации bridge-канала WEB proxy по параметру <c>?bridge=&lt;capability&gt;</c>.
///
/// <c>capability = base64url(HMAC-SHA256(key = весь секрет (вкл. dd-байт), message = context))</c> без
/// <c>=</c>-паддинга. Два контекста (точные строки, <c>\n</c> — перевод строки):
/// <list type="bullet">
/// <item><c>v1</c>: <c>tdesktop-web-proxy-bridge-v1\n&lt;host&gt;</c> — корневой путь;</item>
/// <item><c>v2</c>: <c>tdesktop-web-proxy-bridge-v2\n&lt;host&gt;\n&lt;basePath&gt;</c> — с <c>basePath</c>.</item>
/// </list>
/// </summary>
public static class BridgeCapabilityVerifier
{
    /// <summary>Префикс контекста v1 (без <c>basePath</c>).</summary>
    public const string ContextV1Prefix = "tdesktop-web-proxy-bridge-v1\n";

    /// <summary>Префикс контекста v2 (с <c>basePath</c>).</summary>
    public const string ContextV2Prefix = "tdesktop-web-proxy-bridge-v2\n";

    /// <summary>Максимальная длина <c>basePath</c> после нормализации.</summary>
    public const int MaxBasePathLength = 128;

    /// <summary>Длина capability в байтах (HMAC-SHA256).</summary>
    public const int CapabilityByteLength = 32;

    /// <summary>
    /// Нормализует basePath к голому слагу без ведущего/хвостового <c>/</c>: <c>""</c> для корня,
    /// иначе <c>"v1"</c>. Один источник истины и для маршрутов bridge, и для контекста HMAC v2.
    /// </summary>
    public static string NormalizeBasePath(string? basePath)
        => basePath?.Trim().Trim('/') ?? "";

    /// <summary>
    /// Вычисляет capability для секрета и адреса bridge. Используется в тестах и при генерации
    /// ссылок; сервер в горячем пути применяет <see cref="Verify"/>.
    /// </summary>
    /// <param name="secret">Секрет прокси целиком (16 байт, или 17 с dd-префиксом).</param>
    /// <param name="host">Домен bridge-сайта без пути.</param>
    /// <param name="basePath">Нормализованный путь (пусто — контекст v1).</param>
    public static string ComputeCapability(ReadOnlySpan<byte> secret, string host, string basePath)
        => Base64Url.Encode(Hmac(secret, host, basePath));

    /// <summary>
    /// Проверяет capability для секрета. Сравнение — постоянного времени
    /// (<see cref="CryptographicOperations.FixedTimeEquals"/>, F-W.9).
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> secret, string host, string basePath, ReadOnlySpan<char> capability)
    {
        if (capability.IsEmpty)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[CapabilityByteLength];
        if (!Base64Url.TryDecode(capability, decoded, out var written) || written != CapabilityByteLength)
        {
            return false;
        }

        var expected = Hmac(secret, host, basePath);
        return CryptographicOperations.FixedTimeEquals(expected, decoded[..written]);
    }

    /// <summary>HMAC-SHA256(secret, context) для указанного адреса bridge.</summary>
    private static byte[] Hmac(ReadOnlySpan<byte> secret, string host, string basePath)
    {
        var hasBasePath = !string.IsNullOrEmpty(basePath);
        var context = hasBasePath
            ? $"{ContextV2Prefix}{host}\n{basePath}"
            : $"{ContextV1Prefix}{host}";
        return HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(context));
    }

    /// <summary>base64url без <c>=</c>-паддинга (RFC 4648 §5), зависимость только от BCL.</summary>
    public static class Base64Url
    {
        public static string Encode(ReadOnlySpan<byte> data)
        {
            var base64 = Convert.ToBase64String(data);
            Span<char> buffer = base64.Length <= 256 ? stackalloc char[base64.Length] : new char[base64.Length];
            var written = 0;
            for (var i = 0; i < base64.Length; i++)
            {
                var c = base64[i];
                if (c == '=')
                {
                    break;
                }

                buffer[written++] = c switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => c,
                };
            }

            return new string(buffer[..written]);
        }

        public static bool TryDecode(ReadOnlySpan<char> input, Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            if (input.IsEmpty)
            {
                return false;
            }

            var rem = input.Length % 4;
            if (rem == 1)
            {
                return false;
            }

            var padding = rem == 0 ? 0 : 4 - rem;
            var base64Length = input.Length + padding;
            char[]? rented = null;
            Span<char> base64 = base64Length <= 512
                ? stackalloc char[base64Length]
                : (rented = System.Buffers.ArrayPool<char>.Shared.Rent(base64Length)).AsSpan(0, base64Length);

            try
            {
                for (var i = 0; i < input.Length; i++)
                {
                    base64[i] = input[i] switch
                    {
                        '-' => '+',
                        '_' => '/',
                        var c => c,
                    };
                }

                for (var i = input.Length; i < base64Length; i++)
                {
                    base64[i] = '=';
                }

                return Convert.TryFromBase64Chars(base64, destination, out bytesWritten);
            }
            finally
            {
                if (rented is not null)
                {
                    System.Buffers.ArrayPool<char>.Shared.Return(rented);
                }
            }
        }
    }
}
