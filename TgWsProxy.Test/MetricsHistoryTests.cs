using TgWsProxy.Domain;
using TgWsProxy.Infrastructure.Metrics;

namespace TgWsProxy.Test;

public class MetricsHistoryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StatsSnapshot Snapshot(long active = 0, long up = 0, long down = 0, long errors = 0) => new()
    {
        ConnectionsActive = active,
        BytesUp = up,
        BytesDown = down,
        WsErrors = errors,
    };

    [Fact]
    public void Query_Returns_Requested_Window_With_No_Samples()
    {
        var history = new MetricsHistory();

        var result = history.Query(TimeSpan.FromMinutes(15), TimeSpan.FromSeconds(5), Origin);

        // Ряд пуст (сэмплер ещё не писал), но запрошенное окно возвращается: по нему
        // клиент строит сетку графика (F-3.3).
        Assert.Equal(5, result.StepSeconds);
        Assert.Equal(900, result.WindowSeconds);
        Assert.Empty(result.Points);
    }

    [Fact]
    public void Query_Returns_Points_In_Chronological_Order()
    {
        var history = new MetricsHistory();
        for (var i = 0; i < 6; i++)
        {
            history.Record(Origin.AddSeconds(i * 5), Snapshot(active: i));
        }

        var result = history.Query(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5), Origin.AddSeconds(30));

        Assert.Equal(6, result.Points.Count);
        for (var i = 1; i < result.Points.Count; i++)
        {
            Assert.True(result.Points[i].Timestamp > result.Points[i - 1].Timestamp);
        }
    }

    [Fact]
    public void Query_Computes_Rates_From_Counter_Deltas()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot(up: 1000, down: 2000));
        history.Record(Origin.AddSeconds(5), Snapshot(up: 1500, down: 3000));

        var result = history.Query(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), Origin.AddSeconds(10));

        var withRates = result.Points.Where(p => p.BytesUpPerSecond > 0).ToList();
        Assert.Single(withRates);
        Assert.Equal(100, withRates[0].BytesUpPerSecond, 3);
        Assert.Equal(200, withRates[0].BytesDownPerSecond, 3);
    }

    [Fact]
    public void Query_First_Point_Has_Zero_Rate()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot(up: 5000, down: 5000));

        var result = history.Query(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), Origin.AddSeconds(5));

        var point = Assert.Single(result.Points);
        Assert.Equal(0, point.BytesUpPerSecond);
        Assert.Equal(0, point.BytesDownPerSecond);
        Assert.Equal(0, point.Errors);
    }

    [Fact]
    public void Query_Reports_Error_Delta_And_Clamps_Negative()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot(errors: 10));
        history.Record(Origin.AddSeconds(5), Snapshot(errors: 13));
        history.Record(Origin.AddSeconds(10), Snapshot(errors: 2)); // счётчик сброшен рестартом

        var result = history.Query(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(5), Origin.AddSeconds(15));

        var errors = result.Points.Select(p => p.Errors).ToList();
        Assert.Equal(3, errors[1]);
        Assert.Equal(0, errors[2]);
        Assert.All(errors, e => Assert.True(e >= 0));
    }

    [Fact]
    public void Query_Holds_Last_State_When_Sample_Missing()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot(active: 7));
        // Следующий сэмпл через 15 секунд: интервалы 5..10 и 10..15 пусты.

        var result = history.Query(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(5), Origin.AddSeconds(20));

        Assert.Equal(4, result.Points.Count);
        Assert.Equal(7, result.Points[0].ConnectionsActive);
        Assert.Equal(7, result.Points[1].ConnectionsActive);
        Assert.Equal(7, result.Points[2].ConnectionsActive);
        Assert.All(result.Points.Skip(1), p => Assert.Equal(0, p.BytesUpPerSecond));
    }

    [Fact]
    public void Record_Evicts_Oldest_When_Capacity_Exceeded()
    {
        var history = new MetricsHistory();
        var total = MetricsHistory.Capacity + 10;
        for (var i = 0; i < total; i++)
        {
            history.Record(Origin.AddSeconds(i * MetricsHistory.DefaultStepSeconds), Snapshot(active: i));
        }

        var result = history.Query(TimeSpan.FromHours(1), TimeSpan.FromSeconds(5), Origin.AddSeconds(total * 5));

        // История ограничена Capacity точками и вытесняет самые старые.
        Assert.True(result.Points.Count <= MetricsHistory.Capacity);
        Assert.Equal(total - MetricsHistory.Capacity, result.Points[0].ConnectionsActive);
    }

    [Fact]
    public void Query_Clamps_Window_To_Step_When_Window_Smaller()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot());

        var result = history.Query(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10), Origin.AddSeconds(5));

        Assert.Equal(10, result.StepSeconds);
        Assert.Equal(10, result.WindowSeconds);
    }

    [Fact]
    public async Task Concurrent_Record_And_Query_Are_Safe()
    {
        var history = new MetricsHistory();
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                history.Record(Origin.AddMilliseconds(i), Snapshot(active: i, up: i * 10));
            }
        });
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
            {
                var dto = history.Query(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(5), Origin.AddSeconds(5));
                Assert.NotNull(dto.Points);
            }
        });

        await Task.WhenAll(writer, reader);
    }

    [Fact]
    public void Query_Produces_Strictly_Increasing_Timestamps_Without_Duplicates()
    {
        var history = new MetricsHistory();
        // Редкие сэмплы: между ними есть пустые интервалы, которые «тянут» последнее состояние.
        history.Record(Origin, Snapshot(active: 1));
        history.Record(Origin.AddSeconds(20), Snapshot(active: 2));

        var result = history.Query(TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(5), Origin.AddSeconds(40));

        Assert.Equal(8, result.Points.Count);
        var timestamps = result.Points.Select(p => p.Timestamp).ToList();
        Assert.Equal(timestamps.Count, timestamps.Distinct().Count());
        for (var i = 1; i < timestamps.Count; i++)
        {
            Assert.True(timestamps[i] > timestamps[i - 1], $"точка {i} не возрастает");
        }
        Assert.Equal(Origin.AddSeconds(40), timestamps[^1]);
    }

    [Fact]
    public void Query_Aligns_Window_And_Step_To_Grid()
    {
        var history = new MetricsHistory();
        history.Record(Origin, Snapshot());

        // Окно 17 с при шаге 5 с выравнивается до 15 с (три интервала).
        var result = history.Query(TimeSpan.FromSeconds(17), TimeSpan.FromSeconds(5), Origin.AddSeconds(15));

        Assert.Equal(5, result.StepSeconds);
        Assert.Equal(15, result.WindowSeconds);
        Assert.Equal(3, result.Points.Count);
    }
}
