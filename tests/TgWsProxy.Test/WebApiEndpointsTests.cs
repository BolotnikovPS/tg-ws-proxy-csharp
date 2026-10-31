using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using TgWsProxy.Application;
using TgWsProxy.Application.Abstractions;
using TgWsProxy.Application.Logic;
using TgWsProxy.Application.StartConfig;
using TgWsProxy.Domain;
using TgWsProxy.Domain.Abstractions;
using TgWsProxy.Infrastructure.Metrics;
using TgWsProxy.Infrastructure.Session;
using TgWsProxy.Infrastructure.WebProxy;
using TgWsProxy.Web;

namespace TgWsProxy.Test;

/// <summary>
/// Проверки служебного HTTP-слоя: формирование адреса привязки и контракт <c>/healthz</c>
/// (F-3.4: 200 при живом листенере, 503 при мёртвом).
/// </summary>
public sealed class WebApiEndpointsTests
{
    [Theory]
    [InlineData("127.0.0.1", 8081, "http://127.0.0.1:8081")]
    [InlineData("localhost", 8081, "http://localhost:8081")]
    [InlineData("0.0.0.0", 8081, "http://*:8081")]
    [InlineData("*", 8081, "http://*:8081")]
    [InlineData("+", 8081, "http://*:8081")]
    [InlineData("", 8081, "http://*:8081")]
    [InlineData("::", 8081, "http://[::]:8081")]
    [InlineData("[::]", 8081, "http://[::]:8081")]
    [InlineData("::1", 8081, "http://[::1]:8081")]
    [InlineData("127.0.0.1", 0, "http://127.0.0.1:0")]
    public void BuildListenUrl_FormatsHostAndPort(string host, int port, string expected)
    {
        Assert.Equal(expected, WebApiEndpoints.BuildListenUrl(host, port));
    }

    [Fact]
    public async Task Healthz_Returns_200_When_Server_Listening()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.True(json.RootElement.GetProperty("listening").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("version").GetString()));
        Assert.True(json.RootElement.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Healthz_Returns_503_When_Server_Not_Listening()
    {
        await using var app = await StartAppAsync(listening: false);

        var response = await app.Client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", json.RootElement.GetProperty("status").GetString());
        Assert.False(json.RootElement.GetProperty("listening").GetBoolean());
    }

    [Fact]
    public async Task ApiMetrics_Returns_Snapshot_With_Metadata()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/api/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(dto.GetProperty("version").GetString()));
        Assert.True(dto.GetProperty("uptimeSeconds").GetInt64() >= 0);
        // camelCase-политика обязательна: страница инфографики читает именно эти имена.
        Assert.True(dto.TryGetProperty("connectionsActive", out _));
        Assert.True(dto.TryGetProperty("bytesUp", out _));
        Assert.True(dto.TryGetProperty("h2Requests", out _));
        Assert.True(dto.TryGetProperty("h2Enabled", out _));
    }

    [Fact]
    public async Task ApiInfo_Describes_Effective_Configuration()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/api/info");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("listening").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(dto.GetProperty("version").GetString()));
        Assert.True(dto.GetProperty("startedAt").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
        // Config по умолчанию: пул DC из пяти записей (DC1..DC5), маскировка выключена.
        Assert.Equal(5, dto.GetProperty("dcCount").GetInt32());
        Assert.False(dto.GetProperty("maskingConfigured").GetBoolean());
        // F-7.8: порт слушателя, состояние и статистика перезапусков видны через /api/info.
        Assert.Equal(1443, dto.GetProperty("listenerPort").GetInt32());
        Assert.Equal("listening", dto.GetProperty("listenerState").GetString());
        Assert.Equal(2, dto.GetProperty("listenerRestarts").GetInt32());
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            dto.GetProperty("lastListenerRestart").GetDateTimeOffset());
    }

    [Fact]
    public async Task Metrics_Returns_Prometheus_Text()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/metrics");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("# TYPE tgwsproxy_connections_active gauge", body, StringComparison.Ordinal);
        Assert.Contains("tgwsproxy_uptime_seconds ", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Timeseries_Returns_Requested_Window_And_Step()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/api/metrics/timeseries?window=120&step=5");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, dto.GetProperty("stepSeconds").GetInt32());
        Assert.Equal(120, dto.GetProperty("windowSeconds").GetInt32());
        // Буфер пуст, пока сэмплер не записал данные — ряд корректно пустой.
        Assert.Equal(JsonValueKind.Array, dto.GetProperty("points").ValueKind);
    }

    [Fact]
    public async Task Timeseries_Clamps_Out_Of_Range_Parameters()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.GetAsync("/api/metrics/timeseries?window=999999&step=0");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, dto.GetProperty("stepSeconds").GetInt32());
        // Окно ограничено часом и выровнено по шагу в 1 с.
        Assert.Equal(3600, dto.GetProperty("windowSeconds").GetInt32());
    }

    // F-8.8 / N-8: список токенов никогда не отдаёт секрет в открытом виде.
    [Fact]
    public async Task ApiTokens_Lists_Tokens_With_Masked_Secrets()
    {
        var config = TestConfig.Create(c => c.Tokens.Add(new ProxyToken
        {
            Id = "second",
            Name = "second",
            Secret = "ffffffffffffffffffffffffffffffff",
            Enabled = false,
        }));

        await using var app = await StartAppAsync(listening: true, config);

        var response = await app.Client.GetAsync("/api/tokens");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, dto.ValueKind);
        Assert.Equal(2, dto.GetArrayLength());

        var first = dto[0];
        Assert.Equal("default", first.GetProperty("name").GetString());
        Assert.True(first.GetProperty("enabled").GetBoolean());
        // Маска вида «abcd…ef01»: значение не совпадает с настоящим секретом.
        Assert.Equal("0011…eeff", first.GetProperty("secret").GetString());
        Assert.True(first.TryGetProperty("activeSessions", out _));
        Assert.True(first.TryGetProperty("createdAt", out _));
        Assert.False(dto[1].GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(TestConfig.TokenSecret, dto.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiTokens_Create_Returns_Secret_Once_Without_Caching()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.PostAsJsonAsync(
            "/api/tokens",
            new { name = "phone", enabled = true });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = created.GetProperty("secret").GetString();
        Assert.NotNull(secret);
        Assert.Equal(32, secret!.Length);
        Assert.Equal(16, Convert.FromHexString(secret).Length);
        Assert.False(string.IsNullOrWhiteSpace(created.GetProperty("id").GetString()));

        // Последующий GET обязан маскировать тот же секрет (N-8).
        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens");
        var stored = list.EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "phone");
        Assert.NotEqual(secret, stored.GetProperty("secret").GetString());
        Assert.DoesNotContain(secret, list.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApiTokens_Create_Rejects_Duplicate_Name()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());

        var response = await app.Client.PostAsJsonAsync(
            "/api/tokens",
            new { name = "DEFAULT" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(
            "application/problem+json",
            response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        // Расширение повторяет запрошенное имя: клиент видит, какой именно name конфликтует.
        Assert.Equal("DEFAULT", problem.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiTokens_Update_Changes_Name_And_Disables()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());

        // Одноимённый второй токен нужен, чтобы отключение не оставило список без enabled.
        var created = await app.Client.PostAsJsonAsync("/api/tokens", new { name = "tablet" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        var response = await app.Client.PutAsJsonAsync(
            $"/api/tokens/{id}",
            new { name = "tablet-2", enabled = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("tablet-2", dto.GetProperty("token").GetProperty("name").GetString());
        Assert.False(dto.GetProperty("token").GetProperty("enabled").GetBoolean());
        Assert.Equal(0, dto.GetProperty("closedSessions").GetInt32());

        // Маскированный секрет означает «не менять» (F-2.10, F-8.8).
        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens");
        var stored = list.EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == id);
        Assert.Equal("tablet-2", stored.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiTokens_Update_Unknown_Id_Returns_404()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.PutAsJsonAsync(
            "/api/tokens/missing",
            new { name = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ApiTokens_Delete_Rejects_Last_Enabled_Token()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());

        var response = await app.Client.DeleteAsync("/api/tokens/default");

        // F-8.2: хотя бы один включённый токен обязателен, поэтому валидатор отвечает 400.
        // Удаление единственного токена оставляет пустой список — срабатывает проверка «at least one».
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("at least one token", problem.GetProperty("detail").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApiTokens_Delete_Removes_Token()
    {
        var config = TestConfig.Create(c => c.Tokens.Add(new ProxyToken
        {
            Id = "spare",
            Name = "spare",
            Secret = "ffffffffffffffffffffffffffffffff",
        }));

        await using var app = await StartAppAsync(listening: true, config);

        var response = await app.Client.DeleteAsync("/api/tokens/spare");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dto.GetProperty("deleted").GetBoolean());

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens");
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("default", list[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task ApiTokens_Rotate_Returns_New_Secret_Without_Caching()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());

        var response = await app.Client.PostAsync("/api/tokens/default/rotate", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        var secret = dto.GetProperty("secret").GetString();
        Assert.NotNull(secret);
        Assert.NotEqual(TestConfig.TokenSecret, secret);
        Assert.Equal(32, secret!.Length);

        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens");
        Assert.DoesNotContain(secret, list.GetRawText(), StringComparison.Ordinal);
    }

    // F-8.8 (исключение): ссылка для Telegram строится только через отдельный эндпоинт,
    // который отдаёт полный секрет и потому запрещён к кэшированию.
    [Fact]
    public async Task ApiTokens_Link_Returns_TelegramLink_Without_Caching()
    {
        await using var app = await StartAppAsync(
            listening: true,
            TestConfig.Create(c => c.MtProto.PublicHost = "proxy.example.com"));

        var response = await app.Client.GetAsync("/api/tokens/default/link");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("default", dto.GetProperty("tokenId").GetString());
        Assert.Equal("default", dto.GetProperty("name").GetString());
        Assert.Equal(TestConfig.TokenSecret, dto.GetProperty("secret").GetString());
        Assert.Equal(
            $"https://t.me/proxy?server=proxy.example.com&port=1443&secret=dd{TestConfig.TokenSecret}",
            dto.GetProperty("link").GetString());
    }

    // Без внешнего адреса ссылку не строим, но секрет отдаём (F-8.8).
    [Fact]
    public async Task ApiTokens_Link_Omits_Link_When_PublicHost_Missing()
    {
        var config = TestConfig.Create();

        await using var app = await StartAppAsync(listening: true, config);

        var dto = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens/default/link");

        Assert.Equal(JsonValueKind.Null, dto.GetProperty("link").ValueKind);
        Assert.Equal(TestConfig.TokenSecret, dto.GetProperty("secret").GetString());
    }

    // Ссылка строится по внешнему адресу publicHost.
    [Fact]
    public async Task ApiTokens_Link_Uses_PublicHost()
    {
        var config = TestConfig.Create(c => c.MtProto.PublicHost = "proxy.example.com");

        await using var app = await StartAppAsync(listening: true, config);

        var dto = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens/default/link");

        Assert.Equal(
            $"https://t.me/proxy?server=proxy.example.com&port=1443&secret=dd{TestConfig.TokenSecret}",
            dto.GetProperty("link").GetString());
    }

    // Порт из publicHost перекрывает порт слушателя — так публикуется нестандартный порт хоста.
    [Fact]
    public async Task ApiTokens_Link_Honours_Port_From_PublicHost()
    {
        var config = TestConfig.Create(c => c.MtProto.PublicHost = "proxy.example.com:2443");

        await using var app = await StartAppAsync(listening: true, config);

        var dto = await app.Client.GetFromJsonAsync<JsonElement>("/api/tokens/default/link");

        Assert.Equal(
            $"https://t.me/proxy?server=proxy.example.com&port=2443&secret=dd{TestConfig.TokenSecret}",
            dto.GetProperty("link").GetString());
    }

    [Fact]
    public async Task ApiTokens_Link_Unknown_Id_Returns_404()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());

        var response = await app.Client.GetAsync("/api/tokens/missing/link");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // F-9.4: разрез per-IP статистики по паре, адресу и токену плюс фильтр по токену.
    [Fact]
    public async Task ApiPerIp_Supports_Group_And_Token_Filters()
    {
        await using var app = await StartAppAsync(listening: true, TestConfig.Create());
        var peerStats = app.App.Services.GetRequiredService<IPeerStatsRegistry>();

        var first = IPAddress.Parse("203.0.113.7");
        var second = IPAddress.Parse("203.0.113.8");

        // Первая пара: сессия с токеном и трафиком в обе стороны.
        peerStats.ConnectionOpened(first);
        peerStats.BindToken(first, "default");
        peerStats.AddBytesUp(first, "default", 100);
        peerStats.AddBytesDown(first, "default", 40);

        // Вторая пара: ещё одна сессия с тем же токеном плюс отказ по ACL (без токена, F-9.3).
        peerStats.ConnectionOpened(second);
        peerStats.BindToken(second, "default");
        peerStats.AddBytesUp(second, "default", 10);
        peerStats.RecordBlocked(second);

        var pairs = await app.Client.GetFromJsonAsync<JsonElement>("/api/per-ip");
        Assert.Equal(3, pairs.GetProperty("total").GetInt32());
        var top = pairs.GetProperty("items")[0];
        Assert.Equal("203.0.113.7", top.GetProperty("ip").GetString());
        Assert.Equal("default", top.GetProperty("tokenId").GetString());
        Assert.Equal("default", top.GetProperty("tokenName").GetString());
        Assert.Equal(100, top.GetProperty("bytesUp").GetInt64());
        Assert.Equal(40, top.GetProperty("bytesDown").GetInt64());
        Assert.Equal(1, top.GetProperty("activeConnections").GetInt32());

        // Разрез по адресу: токен схлопывается, суммы складываются.
        var byIp = await app.Client.GetFromJsonAsync<JsonElement>("/api/per-ip?group=ip");
        Assert.Equal(2, byIp.GetProperty("total").GetInt32());
        var heavy = byIp.GetProperty("items")[0];
        Assert.Equal("203.0.113.7", heavy.GetProperty("ip").GetString());
        Assert.Equal(JsonValueKind.Null, heavy.GetProperty("tokenId").ValueKind);
        Assert.Equal(JsonValueKind.Null, heavy.GetProperty("tokenName").ValueKind);
        // Отклонённое соединение видно в разрезе по адресу.
        var rejected = byIp.GetProperty("items")[1];
        Assert.Equal("203.0.113.8", rejected.GetProperty("ip").GetString());
        Assert.Equal(1, rejected.GetProperty("rejected").GetInt32());

        // Разрез по токену: адрес пуст, байты суммируются по всем адресам токена.
        var byToken = await app.Client.GetFromJsonAsync<JsonElement>("/api/per-ip?group=token");
        Assert.Equal(2, byToken.GetProperty("total").GetInt32());
        var tokenRow = byToken.GetProperty("items")[0];
        Assert.Equal("default", tokenRow.GetProperty("tokenId").GetString());
        Assert.Equal("default", tokenRow.GetProperty("tokenName").GetString());
        Assert.Equal("", tokenRow.GetProperty("ip").GetString());
        Assert.Equal(110, tokenRow.GetProperty("bytesUp").GetInt64());

        // Фильтр ?token= сужает выборку до пар с этим токеном (отказ без токена отсекается).
        var filtered = await app.Client.GetFromJsonAsync<JsonElement>("/api/per-ip?token=default");
        Assert.Equal(2, filtered.GetProperty("total").GetInt32());

        // Неизвестный разрез трактуется как pair.
        var unknown = await app.Client.GetFromJsonAsync<JsonElement>("/api/per-ip?group=whatever");
        Assert.Equal(3, unknown.GetProperty("total").GetInt32());
    }

    // F-9.5: агрегаты по токену целиком — по одному элементу на каждый токен конфигурации.
    [Fact]
    public async Task ApiMetricsByToken_Aggregates_Traffic_Per_Token()
    {
        var config = TestConfig.Create(c => c.Tokens.Add(new ProxyToken
        {
            Id = "spare",
            Name = "spare",
            Secret = "ffffffffffffffffffffffffffffffff",
            Enabled = true,
        }));

        await using var app = await StartAppAsync(listening: true, config);
        var peerStats = app.App.Services.GetRequiredService<IPeerStatsRegistry>();

        var first = IPAddress.Parse("203.0.113.7");
        var second = IPAddress.Parse("203.0.113.8");
        peerStats.ConnectionOpened(first);
        peerStats.BindToken(first, "default");
        peerStats.AddBytesUp(first, "default", 100);
        peerStats.ConnectionOpened(second);
        peerStats.BindToken(second, "spare");
        peerStats.AddBytesDown(second, "spare", 500);

        var response = await app.Client.GetAsync("/api/metrics/by-token");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, items.GetArrayLength());
        // Сортировка по суммарному трафику: токен spare получает 500 байт вниз.
        Assert.Equal("spare", items[0].GetProperty("tokenName").GetString());
        Assert.Equal(500, items[0].GetProperty("bytesDown").GetInt64());
        Assert.Equal(0, items[0].GetProperty("bytesUp").GetInt64());
        Assert.Equal("default", items[1].GetProperty("tokenName").GetString());
        Assert.Equal(100, items[1].GetProperty("bytesUp").GetInt64());
        // F-8.8 / N-8: секреты не попадают в агрегаты.
        Assert.DoesNotContain(TestConfig.TokenSecret, items.GetRawText(), StringComparison.Ordinal);
    }

    // F-9.6 / N-12: per-IP серии помечаются именем токена (не секретом) и режутся лимитом.
    [Fact]
    public async Task Metrics_Exports_Peer_Series_With_Token_Label_And_Top_Limit()
    {
        await using var app = await StartAppAsync(
            listening: true,
            TestConfig.Create(c => c.PrometheusTopPeers = 1));
        var peerStats = app.App.Services.GetRequiredService<IPeerStatsRegistry>();

        var heavy = IPAddress.Parse("203.0.113.7");
        var light = IPAddress.Parse("203.0.113.8");
        // Байты учитываются только для уже открытых пар: горячий путь не создаёт записи.
        peerStats.ConnectionOpened(heavy);
        peerStats.BindToken(heavy, "default");
        peerStats.AddBytesUp(heavy, "default", 900);
        peerStats.ConnectionOpened(light);
        peerStats.BindToken(light, "default");
        peerStats.AddBytesUp(light, "default", 10);

        var body = await app.Client.GetStringAsync("/metrics");

        Assert.Contains(
            "tgws_peer_bytes_up_total{ip=\"203.0.113.7\",token=\"default\"} 900",
            body,
            StringComparison.Ordinal);
        // Лимит prometheusTopPeers=1 оставляет только самую «тяжёлую» пару.
        Assert.DoesNotContain("203.0.113.8", body, StringComparison.Ordinal);
        Assert.DoesNotContain(TestConfig.TokenSecret, body, StringComparison.Ordinal);
    }

    // F-9.x: POST /api/dc-check возвращает матрицу «DC × маршрут» (4 маршрута, 6 DC).
    [Fact]
    public async Task DcCheck_Returns_Route_Matrix()
    {
        await using var app = await StartAppAsync(listening: true);

        var response = await app.Client.PostAsync("/api/dc-check", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var report = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(6, report.GetProperty("dcs").GetArrayLength());
        var routes = report.GetProperty("routes");
        Assert.Equal(4, routes.GetArrayLength());
        Assert.Equal("directWs", routes[0].GetProperty("route").GetString());
        Assert.Equal("Прямой WebSocket", routes[0].GetProperty("label").GetString());
        Assert.True(routes[0].GetProperty("results")[0].GetProperty("ok").GetBoolean());
        Assert.Equal(42, routes[0].GetProperty("results")[0].GetProperty("latencyMs").GetInt32());
        // Маршрут «Прямой TCP» возвращает фактический IP, проверенный хостом (для переноса в «DC → IP»).
        Assert.Equal("directTcp", routes[1].GetProperty("route").GetString());
        Assert.Equal("149.154.167.99", routes[1].GetProperty("results")[1].GetProperty("ip").GetString());
    }

    /// <summary>
    /// Поднимает приложение на свободном порту (порт 0) с подставным <see cref="IProxyServer"/>
    /// и клиентом, привязанным к фактическому адресу привязки.
    /// </summary>
    private static async Task<TestApp> StartAppAsync(bool listening, Config? config = null)
    {
        var cfg = config ?? new Config();
        // Хост применяет умолчания (список DC) до запуска — тест повторяет это поведение.
        ConfigDefaults.Apply(cfg);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(cfg);
        // Полный набор прикладных сервисов: minimal API выводит источник параметра из
        // регистрации, поэтому незарегистрированный интерфейс был бы принят за тело запроса.
        builder.Services.AddProxyApplication();
        // Реестр живых сессий живёт в Infrastructure (F-7.4/F-8.11): токен-эндпоинты
        // вводят IActiveSessionRegistry, а незарегистрированный интерфейс minimal API
        // принял бы за тело запроса и уронил бы построение всей таблицы маршрутов.
        builder.Services.AddSingleton<IActiveSessionRegistry, ActiveSessionRegistry>();
        // Ссылки WEB proxy для /api/tokens/{id}/link (F-8.8): в тестах используется
        // настоящий адаптер — маркер секрета и capability проверяются отдельно
        // в WebProxyLinkBuilderTests.
        builder.Services.AddSingleton<IWebProxyLinks, WebProxyLinks>();
        // Проверка достижимости DC — сетевой ввод-вывод: в тестах подменяется заглушкой,
        // иначе незарегистрированный интерфейс minimal API принял бы за тело запроса.
        builder.Services.AddSingleton<IDcReachabilityChecker>(new StubDcChecker());
        builder.Services.AddSingleton<IProxyServer>(new StubProxyServer(listening));
        builder.Services.AddSingleton<IProxyStats>(new ProxyStats());
        builder.Services.AddSingleton<MetricsHistory>();
        // Тот же буфер виден обработчику /api/metrics/timeseries через прикладной порт (F-3.3).
        builder.Services.AddSingleton<ITimeseriesReader>(sp =>
            sp.GetRequiredService<MetricsHistory>());
        // timeseries* — горячие ключи (F-2.11): сервис обновления получает буфер через абстракцию.
        builder.Services.AddSingleton<ITimeseriesBufferConfigurator>(sp =>
            sp.GetRequiredService<MetricsHistory>());
        builder.Services.AddSingleton<IListenerController>(new StubListenerController(listening));
        builder.Services.AddSingleton(new ConfigRuntime(
            Path.Combine(Path.GetTempPath(), "tgws-proxy-test", "config.json"), isExplicit: false));
        builder.Services.AddSingleton<ConfigUpdateService>();
        // Тот же набор, что в Program.cs: /metrics и /healthz/ready требуют реестр
        // метрик и health checks ещё на этапе построения маршрутов (F-3.1/F-3.4).
        builder.Services.AddProxyObservability(cfg);

        var app = builder.Build();
        WebApiEndpoints.MapWebApiEndpoints(app);
        await app.StartAsync();

        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        var address = Assert.Single(addresses ?? []);
        return new TestApp(app, new HttpClient { BaseAddress = new Uri(address) });
    }

    private sealed record TestApp(WebApplication App, HttpClient Client) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    /// <summary>
    /// Заглушка проверки достижимости DC: возвращает фиксированную матрицу без сетевых вызовов.
    /// </summary>
    private sealed class StubDcChecker : IDcReachabilityChecker
    {
        public Task<DcCheckReport> CheckAsync(CancellationToken ct = default)
        {
            var dcs = new[] { 1, 2, 3, 4, 5, 203 };
            var ok = new DcResult(1, true, 42, null, "ok: 149.154.175.50", null);
            var okTcp = new DcResult(2, true, 5, null, null, "149.154.167.99");
            var fail = new DcResult(2, false, null, "timeout", "проверено 1 адрес(ов)", null);
            var routes = new DcRouteCheck[]
            {
                new("directWs", "Прямой WebSocket", new[] { ok, fail, ok, ok, ok, ok }, null),
                new("directTcp", "Прямой TCP", new[] { fail, okTcp, ok, ok, ok, ok }, null),
                new("cfProxy", "Cloudflare-фронтинг", new[] { ok, ok, ok, ok, ok, ok }, "использован встроенный пул (сэмпл из 3)"),
                new("cfWorker", "Cloudflare Worker", new[] { ok, ok, ok, ok, ok, ok }, null),
            };
            return Task.FromResult(new DcCheckReport(DateTimeOffset.UtcNow, dcs, routes));
        }
    }

    /// <summary><see cref="IProxyServer"/> с управляемым признаком живости.</summary>
    private sealed class StubProxyServer(bool listening) : IProxyServer
    {
        public bool IsListening { get; } = listening;

        public Task Run(CancellationToken cancellationToken) => Task.CompletedTask;

        // Тесты конечных точек перепривязку не запрашивают: она покрыта ProxyHostedServiceTests.
        public Task RebindAsync(
            IPEndPoint endpoint,
            Func<CancellationToken, Task>? betweenStopAndBind,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("RebindAsync не используется в тестах Web API.");
    }

    /// <summary>
    /// Заглушка контроллера слушателя: тесты конечных точек читают состояние для
    /// <c>GET /api/info</c> (F-7.8), сам перезапуск покрыт ProxyHostedServiceTests и ConfigUpdateServiceTests.
    /// </summary>
    private sealed class StubListenerController(bool listening) : IListenerController
    {
        /// <summary>
        /// Состояние повторяет признак слушателя: /healthz выводит статус из
        /// <see cref="IListenerController.State"/> (F-3.9).
        /// </summary>
        public ListenerState State => listening ? ListenerState.Listening : ListenerState.Unavailable;

        public int RestartCount => 2;

        public DateTimeOffset? LastRestartAt { get; } =
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        public Task<ListenerRestartResult> RestartAsync(Config candidate, CancellationToken cancellationToken) =>
            throw new NotSupportedException("RestartAsync не используется в тестах Web API.");
    }
}
