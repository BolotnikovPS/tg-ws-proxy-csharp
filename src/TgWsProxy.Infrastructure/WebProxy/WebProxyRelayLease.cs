namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Аренда жизненного цикла одной relay-сессии WEB proxy: фабрика мостов для потоков и
/// <see cref="LifetimeToken"/>, который отменяется при отзыве токена/перезапуске слушателя
/// (F-8.7). <see cref="Dispose"/> закрывает счётчики, привязку (IP, токен) и запись реестра;
/// идемпотентен.
/// </summary>
public sealed class WebProxyRelayLease : IDisposable
{
    private Action? _close;
    private int _disposed;

    internal WebProxyRelayLease(
        Func<RelayStream, CancellationToken, Task> startBridge,
        CancellationToken lifetimeToken,
        Action close)
    {
        StartBridge = startBridge;
        LifetimeToken = lifetimeToken;
        _close = close;
    }

    /// <summary>Фабрика моста одного relay-потока (вызывается мультиплексором по кадру <c>Open</c>).</summary>
    public Func<RelayStream, CancellationToken, Task> StartBridge { get; }

    /// <summary>Токен, отменяемый при внешнем разрыве сессии (реестр живых сессий).</summary>
    public CancellationToken LifetimeToken { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _close?.Invoke();
            _close = null;
        }
    }
}
