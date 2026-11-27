using Microsoft.Extensions.Logging;
using TgWsProxy.Application.Abstractions;
using static TgWsProxy.Domain.AppConstants.CfWebSocket;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Стратегия подключения к CF-доменам по WebSocket — порт upstream <c>proxy/cf_websocket.py</c>
/// (v1.12.0, PR #1633 «CF worker pool: включить cooldown упавших доменов»).
/// </summary>
/// <remarks>
/// До v1.12.0 перебор CF-доменов и переключение TLS/no-TLS были размазаны по
/// <c>bridge.py</c> и <c>pool.py</c>, а охлаждение упавших доменов либо отсутствовало,
/// либо было закомментировано как TODO. Здесь собрана единая политика:
/// <list type="bullet">
/// <item>пара «домен, режим TLS» после <see cref="TimeoutException"/> уходит в охлаждение
/// на <see cref="TimeoutCooldownSeconds"/> секунд — к ней не возвращаются, пока она
/// не остынет;</item>
/// <item>каждый таймаут переключает режим TLS (TLS ↔ plain) и берёт следующий, более
/// щедрый лимит из <see cref="ConnectTimeoutSeconds"/>;</item>
/// <item>успешные подключения накапливают статистику TLS/plain, и «предпочтительный»
/// режим для следующих попыток — тот, что чаще срабатывал (<see cref="PrefersTls"/>).</item>
/// </list>
/// Охлаждение H2-полос читается отсюда же (<see cref="TlsInCooldown"/>) — так H2-пул
/// не открывает канал к домену, который только что таймаутил по WebSocket.
/// </remarks>
internal sealed class CfConnectionStrategy(
    IRawWebSocketFactory wsFactory,
    ILogger<CfConnectionStrategy> logger) : ICfConnectionStrategy
{
    private readonly Lock sync = new();

    /// <summary>
    /// Моменты (монотонные секунды), до которых пара «домен, TLS» считается остывающей.
    /// Значение <see cref="double.PositiveInfinity"/> означает «попытка в полёте»: пока
    /// она идёт, эту пару не должен взять никто другой (upstream <c>math.inf</c>).
    /// </summary>
    private readonly Dictionary<(string Name, bool Tls), double> timeoutUntil = [];

    /// <summary>Число успешных подключений по TLS и без него (upstream <c>CfConStats</c>).</summary>
    private int tlsSuccesses;

    private int noTlsSuccesses;

    /// <summary>
    /// Текущее монотонное время в секундах (аналог <c>time.monotonic()</c>). Тот же источник,
    /// что и <see cref="CfH2Lane.CurrentTime"/>, поэтому значения сопоставимы.
    /// </summary>
    internal static double Now() => Environment.TickCount64 / 1000.0;

    /// <summary>
    /// Сбрасывает охлаждения и статистику. Вызывается при старте сервера
    /// (upstream <c>reset_cf_timeouts()</c> из <c>tg_ws_proxy._run</c>).
    /// </summary>
    public void Reset()
    {
        lock (sync)
        {
            timeoutUntil.Clear();
            tlsSuccesses = 0;
            noTlsSuccesses = 0;
        }
    }

    /// <summary>
    /// Предпочитаемый режим: TLS, если он срабатывал не реже, чем plain
    /// (upstream <c>cf_prefers_tls()</c>).
    /// </summary>
    public bool PrefersTls()
    {
        lock (sync)
        {
            return tlsSuccesses >= noTlsSuccesses;
        }
    }

    /// <summary>
    /// Признак того, что TLS-вариант домена в охлаждении (upstream <c>cf_tls_in_cooldown</c>).
    /// Нужен H2-пулу: он пропускает домен целиком, если его TLS только что таймаутил.
    /// </summary>
    public bool TlsInCooldown(string name)
    {
        lock (sync)
        {
            return timeoutUntil.TryGetValue((name, true), out var until)
                   && Now() < until;
        }
    }

    /// <summary>
    /// Перебирает домены-кандидаты, пока не откроет соединение, с охлаждением упавших
    /// пар «домен, TLS» и переключением TLS после таймаута.
    /// </summary>
    /// <param name="candidates">
    /// Имена кандидатов в порядке перебора. Для CF proxy это базовые домены (из них
    /// <paramref name="hostFor"/> строит <c>kws{dc}.{domain}</c>), для CF worker — сами
    /// домены воркеров.
    /// </param>
    /// <param name="hostFor">Преобразование имени кандидата в хост подключения.</param>
    /// <param name="label">Метка для логов (скоуп сессии).</param>
    /// <param name="secure"><see langword="false"/> — режим <c>--no-secure</c>, TLS не пробуется.</param>
    /// <param name="path">Путь WS-эндпоинта (для воркера — с query <c>dst</c>/<c>dc</c>).</param>
    /// <param name="timeout">Верхняя граница таймаута попытки (урезается лестницей).</param>
    /// <param name="cancellationToken">Проверяется между попытками: прервать идущий дозвон нельзя.</param>
    /// <returns>Открытое соединение и имя сработавшего кандидата, либо <see langword="null"/>.</returns>
    public async Task<(IRawWebSocket Ws, string Name)?> ConnectAsync(
        IReadOnlyList<string> candidates,
        Func<string, string> hostFor,
        string label,
        bool secure,
        string path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var domains = new List<string>(candidates);
        var retrySingleDomain = secure && domains.Count == 1;
        if (retrySingleDomain)
        {
            // Одиночный домен пробуется дважды: если первый заход пропущен из-за
            // охлаждения, у второго есть шанс уйти в другом режиме TLS.
            domains.Add(domains[0]);
        }

        var timeouts = 0;
        var tlsFirst = PrefersTls();

        for (var index = 0; index < domains.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = domains[index];
            var useTls = secure && (tlsFirst != (timeouts % 2 != 0));
            if (retrySingleDomain && index == 1 && timeouts == 0)
            {
                // Первый заход был пропущен из-за охлаждения — инвертируем режим.
                useTls = !tlsFirst;
            }

            var key = (name, useTls);
            var hasRetryAt = false;
            var retryAt = 0.0;
            lock (sync)
            {
                if (timeoutUntil.TryGetValue(key, out var until))
                {
                    if (Now() < until)
                    {
                        continue;
                    }

                    hasRetryAt = true;
                    retryAt = until;

                    // Помечаем попытку «в полёте»: параллельный вызов не возьмёт эту пару.
                    timeoutUntil[key] = double.PositiveInfinity;
                }
            }

            var host = hostFor(name);
            IRawWebSocket? ws = null;
            var timedOut = false;
            try
            {
                var attemptTimeout = TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, ConnectTimeoutSeconds[timeouts]));
                ws = await wsFactory
                    .Connect(host, host, path, label, attemptTimeout, secure: useTls)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                timedOut = true;
                PutInCooldown(key, hasRetryAt, host, useTls, label, ex);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    "[{Label}] CF WebSocket failed (host={Host} tls={Tls}): {Error}",
                    label, host, useTls, ex.Message);
            }
            finally
            {
                // Снимаем маркер «в полёте», возвращая прежнее время охлаждения.
                lock (sync)
                {
                    if (hasRetryAt
                        && timeoutUntil.TryGetValue(key, out var current)
                        && double.IsPositiveInfinity(current))
                    {
                        timeoutUntil[key] = retryAt;
                    }
                }
            }

            if (timedOut)
            {
                timeouts++;
                if (timeouts >= ConnectTimeoutSeconds.Length)
                {
                    break;
                }

                continue;
            }

            lock (sync)
            {
                timeoutUntil.Remove(key);
            }

            if (ws is not null)
            {
                lock (sync)
                {
                    if (useTls)
                    {
                        tlsSuccesses++;
                    }
                    else
                    {
                        noTlsSuccesses++;
                    }
                }

                return (ws, name);
            }

            if (retrySingleDomain)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>
    /// Переводит пару «домен, TLS» в охлаждение после таймаута и попутно чистит
    /// давно истёкшие записи (upstream делает то же в обработчике <c>TimeoutError</c>).
    /// </summary>
    private void PutInCooldown((string Name, bool Tls) key, bool hasRetryAt, string host, bool useTls, string label, Exception ex)
    {
        var now = Now();
        lock (sync)
        {
            if (!hasRetryAt && timeoutUntil.ContainsKey(key))
            {
                return;
            }

            foreach (var stale in timeoutUntil
                         .Where(pair => pair.Value + TimeoutCooldownSeconds <= now)
                         .Select(pair => pair.Key)
                         .ToList())
            {
                timeoutUntil.Remove(stale);
            }

            timeoutUntil[key] = now + TimeoutCooldownSeconds;
        }

        logger.LogWarning(
            "[{Label}] CF WebSocket timed out (host={Host} tls={Tls}): {Error}; retry in {Cooldown:F0}s",
            label, host, useTls, ex.Message, TimeoutCooldownSeconds);
    }
}
