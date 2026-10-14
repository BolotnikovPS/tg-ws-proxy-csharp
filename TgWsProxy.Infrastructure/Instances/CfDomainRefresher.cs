#nullable enable

using Microsoft.Extensions.Logging;
using System.Net.Http;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Instances;

/// <summary>
/// Обновляет список базовых доменов CF proxy (порт upstream
/// <c>start_cfproxy_domain_refresh</c>): сразу подставляет список по умолчанию,
/// затем раз в час пытается забрать свежий список из репозитория upstream.
/// </summary>
internal sealed class CfDomainRefresher(
    Config config,
    ICfDomainBalancer balancer,
    ILogger<CfDomainRefresher> logger) : ICfDomainRefresher
{
    /// <summary>URL со списком закодированных доменов (CFPROXY_DOMAINS_URL).</summary>
    private const string DomainsUrl =
        "https://raw.githubusercontent.com/Flowseal/tg-ws-proxy/main/.github/cfproxy-domains.txt";

    /// <summary>Период обновления списка доменов (секунды).</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    /// <summary>Минимальное число валидных доменов, чтобы принять новый список.</summary>
    private const int MinValidDomains = 3;

    private CancellationTokenSource? lifetime;

    /// <summary>
    /// Подставляет стартовый список доменов и запускает фоновое обновление раз в час.
    /// </summary>
    /// <param name="cancellationToken">Токен остановки прокси.</param>
    public void Start(CancellationToken cancellationToken)
    {
        var initial = config.CfProxyDomains.Count > 0 ? config.CfProxyDomains : CfDomains.DefaultDomains;
        balancer.UpdateDomains(initial);
        logger.LogInformation(
            "CF proxy domain pool initialized ({Count} domains, source={Source})",
            initial.Count,
            config.CfProxyDomains.Count > 0 ? "user" : "builtin");

        if (config.CfProxyDomains.Count > 0)
        {
            // Пользователь задал домены вручную — автообновление не нужно.
            return;
        }

        lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        BackgroundTaskRunner.RunDetachedSafe(
            RefreshLoopAsync,
            logger,
            "[cf-domains] refresh",
            lifetime.Token);
    }

    /// <summary>
    /// Останавливает фоновое обновление списка доменов.
    /// </summary>
    public void Stop()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(RefreshInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Забирает список доменов и обновляет балансировщик, если данных достаточно.
    /// </summary>
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var fetched = await FetchAsync(cancellationToken).ConfigureAwait(false);
        var pool = Normalize(fetched);

        if (pool.Count >= MinValidDomains)
        {
            balancer.UpdateDomains(pool);
            logger.LogInformation("CF proxy domain pool updated from GitHub ({Count} domains)", pool.Count);
            return;
        }

        if (fetched.Count > 0)
        {
            logger.LogWarning(
                "Ignoring fetched CF proxy domains due to low-quality payload (total={Total}, valid={Valid}, required>={Required}); keeping current domain pool",
                fetched.Count, pool.Count, MinValidDomains);
        }
        else
        {
            logger.LogWarning("CF proxy domain refresh failed or empty response; keeping current domain pool");
        }
    }

    /// <summary>
    /// Скачивает файл со списком доменов; при любой ошибке возвращает пустой список.
    /// </summary>
    private async Task<List<string>> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var handler = new HttpClientHandler();
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("tg-ws-proxy");

            // Кэш-пробивающий параметр, как в upstream (случайный суффикс).
            var url = $"{DomainsUrl}?{Random.Shared.Next(0x1000000):x6}";
            var text = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var result = new List<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                result.Add(CfDomains.Decode(line));
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return [];
        }
        catch (Exception ex)
        {
            logger.LogWarning("Failed to fetch CF proxy domain list: {Error}", ex.Message);
            return [];
        }
    }

    /// <summary>
    /// Приводит список доменов к нижнему регистру, отбрасывает невалидные и дубликаты.
    /// </summary>
    private static List<string> Normalize(IEnumerable<string> domains)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();
        foreach (var domain in domains)
        {
            var item = domain.Trim().ToLowerInvariant();
            if (!IsValidDomain(item) || !seen.Add(item))
            {
                continue;
            }

            normalized.Add(item);
        }

        return normalized;
    }

    /// <summary>
    /// Проверяет домен по тем же правилам, что и upstream <c>_is_valid_domain</c>.
    /// </summary>
    private static bool IsValidDomain(string domain)
    {
        if (domain.Length == 0 || domain.Length > 253 || domain.StartsWith('.') || domain.EndsWith('.'))
        {
            return false;
        }

        var labels = domain.Split('.');
        if (labels.Length < 2)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '-')
                {
                    return false;
                }
            }
        }

        var tld = labels[^1];
        return tld.Length >= 2 && tld.Any(char.IsLetter);
    }
}
