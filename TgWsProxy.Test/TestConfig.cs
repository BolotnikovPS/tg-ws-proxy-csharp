#nullable enable

using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Фабрика конфигурации для тестов. С редакции 3 конфиг обязан содержать хотя бы один
/// включённый токен доступа (F-8.2), поэтому «пустой» <see cref="Config"/> больше не проходит
/// валидацию — тестам нужен предсказуемый токен.
/// </summary>
internal static class TestConfig
{
    /// <summary>Секрет тестового токена (32 hex-символа).</summary>
    internal const string TokenSecret = "00112233445566778899aabbccddeeff";

    /// <summary>
    /// Создаёт конфиг с одним включённым токеном и применяет к нему правки теста.
    /// </summary>
    internal static Config Create(Action<Config>? configure = null)
    {
        var config = new Config();
        config.Tokens.Add(new ProxyToken
        {
            Id = "default",
            Name = "default",
            Secret = TokenSecret,
            Enabled = true,
        });

        configure?.Invoke(config);
        return config;
    }
}
