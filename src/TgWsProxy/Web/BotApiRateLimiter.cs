using System;
using TgWsProxy.Domain;

namespace TgWsProxy.Web;

/// <summary>
/// По-ботовый in-memory ограничитель Bot API (fixed window 1 секунда, ключ — <c>botId</c> из токена).
/// При превышении <c>botApi.requestsPerSecondPerToken</c> запрос отклоняется с 429 до разбора тела.
/// <c>0</c> или меньше — ограничение выключено. Счётчики сбрасываются целиком на границе секунды,
/// поэтому память ограничена числом активных ботов за последнюю секунду.
/// </summary>
internal sealed class BotApiRateLimiter
{
    private readonly Func<double> _clock;
    private readonly Dictionary<long, int> _counts = [];
    private readonly Lock _sync = new();
    private double _windowStart;

    public BotApiRateLimiter()
        : this(static () => Environment.TickCount64 / 1000.0)
    {
    }

    /// <summary>Создаёт ограничитель с явными часами (для тестов).</summary>
    /// <param name="clock">Источник монотонного времени в секундах.</param>
    internal BotApiRateLimiter(Func<double> clock)
    {
        _clock = clock;
        _windowStart = Math.Floor(clock());
    }

    /// <summary>
    /// Проверяет, можно ли пропустить ещё один запрос бота <paramref name="botId"/> в текущую секунду.
    /// Возвращает <c>false</c>, если лимит за текущее окно исчерпан.
    /// </summary>
    /// <param name="botId">Числовой идентификатор бота (число до «:» в bot token).</param>
    /// <param name="limitPerSecond">Максимум запросов в секунду; &lt;= 0 — ограничение выключено.</param>
    public bool TryAcquire(long botId, int limitPerSecond)
    {
        if (limitPerSecond <= 0)
        {
            return true;
        }

        var second = Math.Floor(_clock());

        lock (_sync)
        {
            if (second != _windowStart)
            {
                _windowStart = second;
                _counts.Clear();
            }

            if (_counts.TryGetValue(botId, out var count) && count >= limitPerSecond)
            {
                return false;
            }

            _counts[botId] = count + 1;
            return true;
        }
    }
}