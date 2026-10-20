namespace TgWsProxy.Infrastructure.WebProxy;

/// <summary>
/// Транспортный выход relay-сессии: отправляет один сериализованный кадр (заголовок + payload)
/// как единое сообщение нижележащего транспорта (WebSocket). Реализация обязана быть безопасной
/// для конкурентных вызовов — мультиплексор пишет кадры из многих потоков одновременно.
/// Переданный буфер действителен только до завершения <see cref="SendAsync"/>.
/// </summary>
public interface IRelayFrameSink
{
    /// <summary>Отправляет целиком сериализованный relay-кадр.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> frameBytes, CancellationToken ct);
}

/// <summary>
/// Делегат отправки кадра с сериализацией заголовка. Передаётся потокам мультиплексора;
/// внутренний — сериализацию и пул буферов держит <see cref="RelayMultiplexer"/>.
/// </summary>
internal delegate ValueTask RelayFrameSender(
    RelayFrameType type,
    int streamId,
    ReadOnlyMemory<byte> payload,
    CancellationToken ct);
