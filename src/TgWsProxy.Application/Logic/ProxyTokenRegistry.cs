using TgWsProxy.Application.Abstractions;
using TgWsProxy.Domain;

namespace TgWsProxy.Application.Logic;

/// <summary>
/// Снимок токенов доступа прокси (F-8.1, F-8.4). Реестр держит разобранные секреты и порядок
/// перебора для рукопожатия, а единственным источником правды остаётся <see cref="Config"/>:
/// панель и API меняют список токенов в конфигурации, после чего вызывают <see cref="Reload"/>.
///
/// Список конфигурации читается по индексу, а не перечислителем: во время применения новой
/// конфигурации (§ 2.8) коллекция заменяется целиком, и перечисление упало бы с
/// <c>InvalidOperationException</c> прямо в горячем пути рукопожатия. Неконсистентный снимок
/// живёт до ближайшего <see cref="Reload"/>, который вызывается сразу после применения.
/// </summary>
public sealed class ProxyTokenRegistry(Config config) : IProxyTokenRegistry
{
    private readonly object _sync = new();

    /// <summary>Разобранные токены; <c>volatile</c> — читаются без блокировки.</summary>
    private volatile ProxyTokenEntry[] _all = [];

    /// <summary>Включённые токены в порядке перебора для рукопожатия.</summary>
    private volatile ProxyTokenEntry[] _order = [];

    private volatile bool _dirty = true;
    private volatile bool _orderDirty = true;

    /// <inheritdoc />
    public IReadOnlyList<ProxyTokenEntry> HandshakeOrder
    {
        get
        {
            EnsureSnapshot();
            return _order;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProxyTokenEntry> All
    {
        get
        {
            EnsureSnapshot();
            return _all;
        }
    }

    /// <inheritdoc />
    public int EnabledCount
    {
        get
        {
            EnsureSnapshot();
            var count = 0;
            var entries = _all;
            for (var i = 0; i < entries.Length; i++)
            {
                if (entries[i].Enabled)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <inheritdoc />
    public ProxyTokenEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        EnsureSnapshot();
        var entries = _all;
        for (var i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Id, id, StringComparison.Ordinal))
            {
                return entries[i];
            }
        }

        return null;
    }

    /// <inheritdoc />
    public void Touch(string tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Снимок: отметка нужна, чтобы следующее рукопожатие началось с «свежего» токена.
        EnsureSnapshot();
        var entries = _all;
        for (var i = 0; i < entries.Length; i++)
        {
            if (string.Equals(entries[i].Id, tokenId, StringComparison.Ordinal))
            {
                entries[i].Touch(now);
                _orderDirty = true;
                break;
            }
        }

        // Конфигурация: оттуда значение попадает в GET /api/tokens и в файл при следующей записи.
        lock (_sync)
        {
            for (var i = 0; i < config.Tokens.Count; i++)
            {
                if (string.Equals(config.Tokens[i].Id, tokenId, StringComparison.Ordinal))
                {
                    config.Tokens[i].LastUsedAt = now;
                    break;
                }
            }
        }
    }

    /// <inheritdoc />
    public void Reload()
    {
        _dirty = true;
        _orderDirty = true;
    }

    /// <summary>Лениво перестраивает снимок и порядок перебора под блокировкой.</summary>
    private void EnsureSnapshot()
    {
        if (!_dirty && !_orderDirty)
        {
            return;
        }

        lock (_sync)
        {
            if (_dirty)
            {
                _all = BuildEntries();
                _dirty = false;
            }

            if (_orderDirty)
            {
                _order = BuildOrder(_all);
                _orderDirty = false;
            }
        }
    }

    private ProxyTokenEntry[] BuildEntries()
    {
        var count = config.Tokens.Count;
        var entries = new ProxyTokenEntry[count];
        for (var i = 0; i < count; i++)
        {
            var token = config.Tokens[i];
            entries[i] = new ProxyTokenEntry(
                token.Id ?? "",
                string.IsNullOrWhiteSpace(token.Name) ? token.Id ?? "" : token.Name,
                token.Secret ?? "",
                ParseSecret(token.Secret),
                token.Enabled,
                token.CreatedAt ?? DateTimeOffset.MinValue,
                token.LastUsedAt);
        }

        return entries;
    }

    /// <summary>
    /// Порядок перебора (F-8.4): последний успешно использованный токен — первым, затем по
    /// времени создания. Свежий токен первым означает, что активное устройство находит свой
    /// секрет с первой попытки, а не после перебора всех остальных.
    /// </summary>
    private static ProxyTokenEntry[] BuildOrder(ProxyTokenEntry[] entries)
    {
        return entries
            .Where(static entry => entry.Enabled)
            .OrderByDescending(static entry => entry.LastUsedAt ?? DateTimeOffset.MinValue)
            .ThenBy(static entry => entry.CreatedAt)
            .ToArray();
    }

    /// <summary>
    /// Разбирает hex-секрет. Конфигурация проверена <c>ConfigValidator</c>, поэтому пустой
    /// результат возможен только для правленого вручную файла: такой токен просто никогда
    /// не совпадёт ни с одним рукопожатием.
    /// </summary>
    private static byte[] ParseSecret(string? secretHex)
    {
        if (string.IsNullOrWhiteSpace(secretHex))
        {
            return [];
        }

        try
        {
            return Convert.FromHexString(secretHex.Trim());
        }
        catch (FormatException)
        {
            return [];
        }
    }
}
