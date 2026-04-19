using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Pr3.ConfigAndSecurity.Domain;
using Xunit;

namespace Pr3.ConfigAndSecurity.Tests;

public sealed class IntegrationSecurityTests
{
    [Fact]
    public async Task Доверенный_источник_получает_разрешающий_заголовок()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/items");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values));
        Assert.Contains("http://localhost:5173", values);
    }

    [Fact]
    public async Task Недоверенный_источник_не_получает_разрешающий_заголовок()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/items");
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.local");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Ограничитель_частоты_возвращает_429()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 2, writeLimit: 1);
        var client = factory.CreateClient();

        async Task<HttpStatusCode> Call()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/items");
            request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");
            var resp = await client.SendAsync(request);
            return resp.StatusCode;
        }

        var a = await Call();
        var b = await Call();
        var c = await Call();
        var d = await Call();
        var e = await Call();

        Assert.Equal(HttpStatusCode.OK, a);
        Assert.Equal(HttpStatusCode.OK, b);
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Equal(HttpStatusCode.OK, d);
        Assert.Equal((HttpStatusCode)429, e);
    }

    [Fact]
    public async Task Защитные_заголовки_присутствуют()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/items");

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.True(response.Headers.Contains("Cache-Control"));
    }

    [Fact]
    public async Task Учебный_режим_возвращает_подробное_сообщение_ошибки()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/items/by-id/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorResponse>(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("bad_request", error!.Code);
        Assert.Contains("Элемент не найден", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Боевой_режим_скрывает_детали_ошибки()
    {
        var factory = CreateFactory(mode: "Боевой", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/items/by-id/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorResponse>(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("bad_request", error!.Code);
        Assert.Equal("Ошибка обработки запроса", error.Message);
        Assert.DoesNotContain("Элемент не найден", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Режим_влияет_на_строгость_ограничителя_частоты()
    {
        var trainingFactory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 1, writeLimit: 1);
        var trainingClient = trainingFactory.CreateClient();

        var t1 = await trainingClient.GetAsync("/api/items");
        var t2 = await trainingClient.GetAsync("/api/items");
        var t3 = await trainingClient.GetAsync("/api/items");

        Assert.Equal(HttpStatusCode.OK, t1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, t2.StatusCode);
        Assert.Equal((HttpStatusCode)429, t3.StatusCode);

        var productionFactory = CreateFactory(mode: "Боевой", trustedOrigin: "http://localhost:5173", readLimit: 1, writeLimit: 1);
        var productionClient = productionFactory.CreateClient();

        var p1 = await productionClient.GetAsync("/api/items");
        var p2 = await productionClient.GetAsync("/api/items");

        Assert.Equal(HttpStatusCode.OK, p1.StatusCode);
        Assert.Equal((HttpStatusCode)429, p2.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(string mode, string trustedOrigin, int readLimit, int writeLimit)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.Sources.Clear();

                    var settings = new Dictionary<string, string?>
                    {
                        ["App:Mode"] = mode,
                        ["App:TrustedOrigins:0"] = trustedOrigin,
                        ["App:RateLimits:ReadPerMinute"] = readLimit.ToString(),
                        ["App:RateLimits:WritePerMinute"] = writeLimit.ToString()
                    };

                    cfg.AddInMemoryCollection(settings);
                });
            });
    }
}
