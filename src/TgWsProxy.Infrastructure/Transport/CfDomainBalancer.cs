using TgWsProxy.Application.Abstractions;

namespace TgWsProxy.Infrastructure.Transport;

/// <summary>
/// Реализация распределения CF proxy доменов по DC (порт upstream <c>balancer.py</c>).
/// </summary>
public sealed class CfDomainBalancer : ICfDomainBalancer
{
    private static readonly int[] KnownDcs = [1, 2, 3, 4, 5, 203];

    private readonly Lock sync = new();
    private List<string> domains = [];
    private Dictionary<int, string> dcToDomain = [];

    /// <inheritdoc />
    public IReadOnlyList<string> Domains
    {
        get
        {
            lock (sync)
            {
                return [.. domains];
            }
        }
    }

    /// <inheritdoc />
    public void UpdateDomains(IReadOnlyList<string> newDomains)
    {
        lock (sync)
        {
            if (SameMultiset(domains, newDomains))
            {
                return;
            }

            domains = [.. newDomains];
            if (domains.Count == 0)
            {
                dcToDomain = [];
                return;
            }

            dcToDomain = KnownDcs.ToDictionary(dc => dc, _ => domains[Random.Shared.Next(domains.Count)]);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> DomainsForDc(int dc)
    {
        lock (sync)
        {
            var result = new List<string>(domains.Count);
            dcToDomain.TryGetValue(dc, out var current);
            if (!string.IsNullOrEmpty(current))
            {
                result.Add(current);
            }

            var shuffled = domains.ToArray();
            Random.Shared.Shuffle(shuffled);
            foreach (var domain in shuffled)
            {
                if (!string.Equals(domain, current, StringComparison.Ordinal))
                {
                    result.Add(domain);
                }
            }

            return result;
        }
    }

    /// <inheritdoc />
    public bool UpdateDomainForDc(int dc, string domain)
    {
        lock (sync)
        {
            if (dcToDomain.TryGetValue(dc, out var existing) && string.Equals(existing, domain, StringComparison.Ordinal))
            {
                return false;
            }

            dcToDomain[dc] = domain;
            return true;
        }
    }

    /// <summary>
    /// Сравнивает два списка как мультимножества (аналог <c>Counter</c> в upstream).
    /// </summary>
    private static bool SameMultiset(List<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in left)
        {
            counts[item] = counts.GetValueOrDefault(item) + 1;
        }

        foreach (var item in right)
        {
            if (!counts.TryGetValue(item, out var count) || count == 0)
            {
                return false;
            }

            counts[item] = count - 1;
        }

        return true;
    }
}
