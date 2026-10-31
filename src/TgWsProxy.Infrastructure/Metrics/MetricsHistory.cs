
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.ReadModels;
using TgWsProxy.Domain;

namespace TgWsProxy.Infrastructure.Metrics;

/// <summary>
/// Кольцевой буфер сэмплов счётчиков для графиков. Ограничен по числу точек
/// (умолчание: час истории при шаге 5 с), старые записи вытесняются.
/// Потокобезопасен: запись идёт из фонового сэмплера, чтение — из обработчиков запросов.
/// </summary>
public sealed class MetricsHistory : ITimeseriesBufferConfigurator, ITimeseriesReader
{
    /// <summary>Шаг сэмплирования по умолчанию (совпадает с шагом по умолчанию для API).</summary>
    public const int DefaultStepSeconds = 5;

    /// <summary>Окно по умолчанию для запроса временного ряда — 15 минут.</summary>
    public const int DefaultWindowSeconds = 900;

    /// <summary>Глубина истории в секундах: один час независимо от шага.</summary>
    public const int CapacitySeconds = 3600;

    /// <summary>Максимальная глубина истории — один час при шаге по умолчанию.</summary>
    public const int Capacity = CapacitySeconds / DefaultStepSeconds;

    private readonly object sync = new();
    private Sample[] samples;
    private int capacityPoints;
    private int stepSeconds;
    private int windowSeconds;
    private int count;
    private int head;

    /// <summary>Создаёт буфер с умолчаниями (шаг 5 с, окно 15 мин).</summary>
    public MetricsHistory()
        : this(DefaultStepSeconds, DefaultWindowSeconds)
    {
    }

    /// <summary>Создаёт буфер по конфигурации (<c>timeseriesStepSeconds</c>/<c>timeseriesWindowSeconds</c>).</summary>
    public MetricsHistory(Config config)
        : this(config.TimeseriesStepSeconds, config.TimeseriesWindowSeconds)
    {
    }

    /// <summary>Создаёт буфер с явными шагом сэмплирования и окном по умолчанию.</summary>
    /// <param name="stepSeconds">Шаг сэмплирования в секундах.</param>
    /// <param name="windowSeconds">Окно по умолчанию для запросов в секундах.</param>
    public MetricsHistory(int stepSeconds, int windowSeconds)
    {
        this.stepSeconds = Math.Max(1, stepSeconds);
        this.windowSeconds = Math.Max(this.stepSeconds, windowSeconds);
        capacityPoints = Math.Max(2, CapacitySeconds / this.stepSeconds);
        samples = new Sample[capacityPoints];
    }

    /// <summary>Шаг сэмплирования в секундах.</summary>
    public int StepSeconds
    {
        get
        {
            lock (sync)
            {
                return stepSeconds;
            }
        }
    }

    /// <summary>Окно по умолчанию для запросов временного ряда в секундах.</summary>
    public int WindowSeconds
    {
        get
        {
            lock (sync)
            {
                return windowSeconds;
            }
        }
    }

    /// <summary>Максимальное число хранимых точек.</summary>
    public int CapacityPoints
    {
        get
        {
            lock (sync)
            {
                return capacityPoints;
            }
        }
    }

    /// <summary>
    /// Пересоздаёт буфер под новые параметры (ключи <c>timeseries*</c> категории «горячо»).
    /// История сбрасывается: сэмплы, снятые с прежним шагом, нельзя пересчитать под новый.
    /// </summary>
    public void Reconfigure(int stepSeconds, int windowSeconds)
    {
        var newStep = Math.Max(1, stepSeconds);
        var newWindow = Math.Max(newStep, windowSeconds);
        var newCapacity = Math.Max(2, CapacitySeconds / newStep);

        lock (sync)
        {
            this.stepSeconds = newStep;
            this.windowSeconds = newWindow;
            capacityPoints = newCapacity;
            samples = new Sample[newCapacity];
            count = 0;
            head = 0;
        }
    }

    /// <summary>Добавляет сэмпл; при переполнении вытесняет самый старый.</summary>
    public void Record(DateTimeOffset timestamp, StatsSnapshot snapshot)
    {
        lock (sync)
        {
            samples[head] = new Sample(
                timestamp,
                snapshot.ConnectionsActive,
                snapshot.BytesUp,
                snapshot.BytesDown,
                snapshot.WsErrors);
            head = (head + 1) % capacityPoints;
            if (count < capacityPoints)
            {
                count++;
            }
        }
    }

    /// <summary>
    /// Строит временной ряд за последние <paramref name="window"/> с шагом <paramref name="step"/>.
    /// Ряд — равномерная сетка: метка точки равна концу её интервала, поэтому значения строго
    /// возрастают и не дублируются. Значение точки — последний сэмпл внутри интервала; скорости
    /// считаются по разнице счётчиков с предыдущей точкой, поэтому на первой точке скорость равна нулю.
    /// </summary>
    public TimeseriesDto Query(TimeSpan window, TimeSpan step, DateTimeOffset now)
    {
        var stepSeconds = Math.Max(1, (int)Math.Round(step.TotalSeconds));
        // Окно выравнивается по шагу, чтобы ряд был равномерной сеткой без «хвоста».
        var bucketCount = Math.Max(1, (int)Math.Round(window.TotalSeconds) / stepSeconds);
        var windowSeconds = bucketCount * stepSeconds;

        Sample[] snapshot;
        lock (sync)
        {
            snapshot = Ordered();
        }

        var points = new List<TimeseriesPoint>(bucketCount);
        if (snapshot.Length == 0)
        {
            // Буфер пуст (сэмплер ещё не записал данные), но запрошенное окно всё равно
            // возвращается в ответе: клиент строит сетку графика по этим значениям.
            return new TimeseriesDto(stepSeconds, windowSeconds, points);
        }

        var from = now - TimeSpan.FromSeconds(windowSeconds);

        // Раскладываем сэмплы по корзинам; снимок упорядочен, поэтому в корзине остаётся самый свежий.
        // Сэмплы на границе now относятся к последней корзине, чтобы свежие данные не терялись.
        var buckets = new Sample?[bucketCount];
        foreach (var sample in snapshot)
        {
            if (sample.Timestamp < from)
            {
                continue;
            }

            var index = (int)((sample.Timestamp - from).TotalSeconds / stepSeconds);
            if (index >= bucketCount)
            {
                index = bucketCount - 1;
            }

            buckets[index] = sample;
        }

        Sample? previous = null;
        for (var i = 0; i < bucketCount; i++)
        {
            // Метка точки — конец интервала: сетка строго возрастает и не даёт дублей.
            var timestamp = from + TimeSpan.FromSeconds((i + 1) * stepSeconds);

            if (buckets[i] is { } point)
            {
                double upRate = 0;
                double downRate = 0;
                long errors = 0;

                if (previous is { } prev)
                {
                    var elapsed = (point.Timestamp - prev.Timestamp).TotalSeconds;
                    if (elapsed > 0)
                    {
                        upRate = (point.BytesUp - prev.BytesUp) / elapsed;
                        downRate = (point.BytesDown - prev.BytesDown) / elapsed;
                    }
                    errors = Math.Max(0, point.WsErrors - prev.WsErrors);
                }

                points.Add(new TimeseriesPoint(
                    timestamp,
                    point.ConnectionsActive,
                    Math.Max(0, upRate),
                    Math.Max(0, downRate),
                    errors));

                previous = point;
            }
            else if (previous is { } held)
            {
                // Пропуск сэмпла: тянем последнее известное состояние, скорость — ноль.
                points.Add(new TimeseriesPoint(timestamp, held.ConnectionsActive, 0, 0, 0));
            }
        }

        return new TimeseriesDto(stepSeconds, windowSeconds, points);
    }

    /// <summary>Копия сэмплов в хронологическом порядке (от старых к новым).</summary>
    private Sample[] Ordered()
    {
        if (count == 0)
        {
            return [];
        }

        var result = new Sample[count];
        var start = count == CapacityPoints ? head : 0;
        for (var i = 0; i < count; i++)
        {
            result[i] = samples[(start + i) % CapacityPoints];
        }
        return result;
    }

    private readonly record struct Sample(
        DateTimeOffset Timestamp,
        long ConnectionsActive,
        long BytesUp,
        long BytesDown,
        long WsErrors);
}
