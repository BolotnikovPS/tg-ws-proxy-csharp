#nullable enable

using Microsoft.Extensions.Logging.Abstractions;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;

namespace TgWsProxy.Test;

/// <summary>
/// Тесты применения конфигурации из Web API (F-2.9…F-2.13): три категории ключей
/// (горячо / слушатель / только старт), транзакционность и маскирование секретов.
/// </summary>
public sealed class ConfigUpdateServiceTests : IDisposable
{
    private const string SecretHex = "f43b08aef31a50b7b92c17f07bb66b11";

    private readonly string _directory;
    private readonly Config _config;
    private readonly ConnectionLimiter _connectionLimiter;
    private readonly PeerRateLimiter _peerRateLimiter;
    private readonly PeerAccessControl _accessControl;
    private readonly StubListenerController _listener = new();
    private readonly StubTimeseriesBuffer _timeseries = new();
    private readonly ProxyTokenRegistry _tokens;
    private readonly ConfigUpdateService _service;

    public ConfigUpdateServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "tgws-config-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _config = new Config { WebApiToken = "initial-token" };
        _config.Tokens.Add(new ProxyToken
        {
            Id = "default",
            Name = "default",
            Secret = SecretHex,
            Enabled = true,
        });

        ConfigDefaults.Apply(_config);

        _connectionLimiter = new ConnectionLimiter(_config);
        _peerRateLimiter = new PeerRateLimiter(_config);
        _accessControl = new PeerAccessControl(_config);
        _tokens = new ProxyTokenRegistry(_config);

        var runtime = new ConfigRuntime(Path.Combine(_directory, "config.json"), isExplicit: false);
        _service = new ConfigUpdateService(
            _config,
            runtime,
            _connectionLimiter,
            _peerRateLimiter,
            _accessControl,
            _listener,
            _timeseries,
            _tokens,
            NullLogger<ConfigUpdateService>.Instance);
    }

    /// <summary>
    /// Регрессия: ответ GET /api/config, отправленный обратно без изменений, не должен
    /// порождать ни одного «изменённого» ключа. Ранее <see cref="Config.LogPath"/> оставался
    /// <c>null</c>, а форма присылала <c>""</c>, из-за чего панель показывала
    /// «нужен рестарт: logPath» при каждом сохранении.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MaskedRoundTrip_ReportsNoChanges()
    {
        var result = await _service.ApplyAsync(_service.GetMasked(), CancellationToken.None);

        Assert.True(result.Saved);
        Assert.Empty(result.HotReloaded);
        Assert.False(result.ListenerRestarted);
        Assert.Equal(0, result.DroppedSessions);
        Assert.False(_listener.WasCalled);
    }

    /// <summary>
    /// «Горячие» ключи применяются к работающему процессу сразу, слушатель не перезапускается (F-2.11).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_HotKey_AppliesWithoutRestart()
    {
        var result = await _service.ApplyAsync(
            new ConfigDto { MaxConnections = 250 },
            CancellationToken.None);

        Assert.False(result.ListenerRestarted);
        Assert.Equal(new[] { "maxConnections" }, result.HotReloaded);
        Assert.Equal(250, _connectionLimiter.Limit);
        Assert.Equal(250, _config.MaxConnections);
    }

    /// <summary>
    /// Смена параметров временного ряда — «горячий» ключ: буфер пересоздаётся без перезапуска (F-2.11).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_TimeseriesKey_ReconfiguresBuffer()
    {
        var result = await _service.ApplyAsync(
            new ConfigDto { TimeseriesStepSeconds = 10, TimeseriesWindowSeconds = 600 },
            CancellationToken.None);

        Assert.False(result.ListenerRestarted);
        Assert.Contains("timeseriesStepSeconds", result.HotReloaded);
        Assert.Contains("timeseriesWindowSeconds", result.HotReloaded);
        Assert.Equal((10, 600), _timeseries.LastReconfigure);
        Assert.Equal(10, _config.TimeseriesStepSeconds);
        Assert.Equal(600, _config.TimeseriesWindowSeconds);
    }

    /// <summary>
    /// Ключ категории «слушатель» сохраняется на диск и передаётся контроллеру перезапуска (F-2.11, F-7.1).
    /// Значения в живой конфиг переносит контроллер — и только после успешной привязки (F-7.3).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ListenerKey_RestartsListenerAndPersists()
    {
        _listener.DroppedSessions = 3;

        var result = await _service.ApplyAsync(new ConfigDto { WsPoolSize = 8 }, CancellationToken.None);

        Assert.True(result.ListenerRestarted);
        Assert.Equal(3, result.DroppedSessions);
        Assert.Empty(result.HotReloaded);
        Assert.True(_listener.WasCalled);
        Assert.Equal(8, _listener.LastCandidate?.WsPoolSize);

        // Живой процесс по-прежнему на прежнем значении: перенос делает контроллер после bind.
        Assert.Equal(4, _config.WsPoolSize);

        var saved = ConfigFileLoader.Load(_service.Path, isExplicit: true);
        Assert.Equal(8, saved.WsPoolSize);
    }

    /// <summary>
    /// Неудачная перепривязка откатывает файл: диск не должен опережать работающий процесс (F-2.13).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ListenerRebindFailure_RollsBackFile()
    {
        ConfigFileLoader.Save(_service.Path, _config);
        _listener.Failure = "port 19999 is already in use";

        await Assert.ThrowsAsync<ListenerRebindException>(() =>
            _service.ApplyAsync(new ConfigDto { WsPoolSize = 8 }, CancellationToken.None));

        var restored = ConfigFileLoader.Load(_service.Path, isExplicit: true);
        Assert.Equal(4, restored.WsPoolSize);
        Assert.Equal(4, _config.WsPoolSize);
    }

    /// <summary>
    /// Запрос во время уже идущего перезапуска получает отказ, и файл тоже возвращается
    /// к прежнему содержимому (F-7.6).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ConcurrentRestart_RollsBackFileAndRethrows()
    {
        ConfigFileLoader.Save(_service.Path, _config);
        _listener.InProgress = true;

        await Assert.ThrowsAsync<ListenerRestartInProgressException>(() =>
            _service.ApplyAsync(new ConfigDto { WsPoolSize = 8 }, CancellationToken.None));

        var restored = ConfigFileLoader.Load(_service.Path, isExplicit: true);
        Assert.Equal(4, restored.WsPoolSize);
    }

    /// <summary>
    /// Привязка слушателя и уровень журнала правятся только файлом: панель показывает их как
    /// справку, а API отклоняет изменение с 409 (F-2.12, F-5.16).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_ListenerBindingAndVerbose_AreStartupOnly()
    {
        var exception = await Assert.ThrowsAsync<StartupOnlyKeysException>(() =>
            _service.ApplyAsync(
                new ConfigDto { Port = 19999, Host = "0.0.0.0", Verbose = true },
                CancellationToken.None));

        Assert.Equal(
            new[] { "host", "port", "verbose" },
            exception.StartupOnly.OrderBy(key => key));
        Assert.False(_listener.WasCalled);
        Assert.False(File.Exists(_service.Path));
        Assert.Equal(1443, _config.Port);
        Assert.Equal("127.0.0.1", _config.Host);
        Assert.False(_config.Verbose);
    }

    /// <summary>
    /// Ключи «только старт» отклоняются до записи и перечисляются в ответе (F-2.12).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_StartupOnlyKey_IsRejectedWithKeyList()
    {
        var exception = await Assert.ThrowsAsync<StartupOnlyKeysException>(() =>
            _service.ApplyAsync(
                new ConfigDto { MetricsPort = 9999, MetricsHost = "0.0.0.0" },
                CancellationToken.None));

        Assert.Equal(new[] { "metricsHost", "metricsPort" }, exception.StartupOnly.OrderBy(key => key));
        Assert.False(File.Exists(_service.Path));
        Assert.Equal(8081, _config.MetricsPort);
    }

    /// <summary>
    /// Маскированное значение из формы не должно затирать настоящий секрет в файле (F-2.10, F-8.8).
    /// </summary>
    [Fact]
    public async Task ApplyAsync_MaskedSecret_DoesNotOverwriteStoredSecret()
    {
        var patch = _service.GetMasked();
        Assert.Equal("f43b…6b11", Assert.Single(patch.Tokens!).Secret);
        Assert.Equal("init…oken", patch.WebApiToken);

        await _service.ApplyAsync(patch, CancellationToken.None);

        var saved = ConfigFileLoader.Load(_service.Path, isExplicit: true);
        Assert.Equal(SecretHex, Assert.Single(saved.Tokens).Secret);
        Assert.Equal("initial-token", saved.WebApiToken);
    }

    /// <summary>
    /// Невалидное значение отклоняется до записи: файл и живая конфигурация остаются прежними.
    /// </summary>
    [Fact]
    public async Task ApplyAsync_InvalidValue_ThrowsAndKeepsFileUntouched()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyAsync(new ConfigDto { Port = 70000 }, CancellationToken.None));

        Assert.Equal(1443, _config.Port);
        Assert.False(File.Exists(_service.Path));
    }

    /// <summary>Сохранение возвращает замечания, которые панель показывает пользователю (F-5.18).</summary>
    [Fact]
    public async Task ApplyAsync_DisabledPerIpStats_ProducesWarning()
    {
        var result = await _service.ApplyAsync(
            new ConfigDto { EnablePerIpStats = false },
            CancellationToken.None);

        Assert.Contains(result.Warnings, warning => warning.Contains("enablePerIpStats", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Временный каталог уберёт ОС — на результат теста это не влияет.
        }
    }

    /// <summary>Заглушка перезапуска слушателя: запоминает кандидата и умеет «падать».</summary>
    private sealed class StubListenerController : IListenerController
    {
        public bool WasCalled { get; private set; }

        public Config? LastCandidate { get; private set; }

        public int DroppedSessions { get; set; }

        public string? Failure { get; set; }

        public bool InProgress { get; set; }

        public ListenerState State { get; private set; } = ListenerState.Listening;

        public int RestartCount { get; private set; }

        public DateTimeOffset? LastRestartAt { get; private set; }

        public Task<ListenerRestartResult> RestartAsync(Config candidate, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastCandidate = candidate;

            if (InProgress)
            {
                throw new ListenerRestartInProgressException();
            }

            if (Failure is not null)
            {
                return Task.FromResult(ListenerRestartResult.Failed(Failure));
            }

            State = ListenerState.Restarting;
            RestartCount++;
            LastRestartAt = DateTimeOffset.UtcNow;
            State = ListenerState.Listening;
            return Task.FromResult(ListenerRestartResult.Ok(DroppedSessions));
        }
    }

    /// <summary>Заглушка горячей перенастройки буфера временного ряда.</summary>
    private sealed class StubTimeseriesBuffer : ITimeseriesBufferConfigurator
    {
        public int StepSeconds { get; private set; } = 5;

        public int WindowSeconds { get; private set; } = 900;

        public (int Step, int Window)? LastReconfigure { get; private set; }

        public void Reconfigure(int stepSeconds, int windowSeconds)
        {
            StepSeconds = stepSeconds;
            WindowSeconds = windowSeconds;
            LastReconfigure = (stepSeconds, windowSeconds);
        }
    }
}
