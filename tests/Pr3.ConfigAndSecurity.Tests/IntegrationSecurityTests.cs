using System.Net;
using System.Net.Http.Json;
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

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auto-parts");
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

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/auto-parts");
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
            var request = new HttpRequestMessage(HttpMethod.Get, "/api/auto-parts");
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

        var response = await client.GetAsync("/api/auto-parts");

        Assert.True(response.Headers.Contains("X-Content-Type-Options"));
        Assert.True(response.Headers.Contains("X-Frame-Options"));
        Assert.True(response.Headers.Contains("Cache-Control"));
    }

    [Fact]
    public async Task Учебный_режим_возвращает_подробное_сообщение_ошибки()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auto-parts/by-id/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorResponse>(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("bad_request", error!.Code);
        Assert.Contains("Автозапчасть не найдена", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Боевой_режим_скрывает_детали_ошибки()
    {
        var factory = CreateFactory(mode: "Боевой", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/auto-parts/by-id/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorResponse>(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("bad_request", error!.Code);
        Assert.Equal("Ошибка обработки запроса", error.Message);
        Assert.DoesNotContain("Автозапчасть не найдена", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Режим_влияет_на_строгость_ограничителя_частоты()
    {
        var trainingFactory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 1, writeLimit: 1);
        var trainingClient = trainingFactory.CreateClient();

        var t1 = await trainingClient.GetAsync("/api/auto-parts");
        var t2 = await trainingClient.GetAsync("/api/auto-parts");
        var t3 = await trainingClient.GetAsync("/api/auto-parts");

        Assert.Equal(HttpStatusCode.OK, t1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, t2.StatusCode);
        Assert.Equal((HttpStatusCode)429, t3.StatusCode);

        var productionFactory = CreateFactory(mode: "Боевой", trustedOrigin: "http://localhost:5173", readLimit: 1, writeLimit: 1);
        var productionClient = productionFactory.CreateClient();

        var p1 = await productionClient.GetAsync("/api/auto-parts");
        var p2 = await productionClient.GetAsync("/api/auto-parts");

        Assert.Equal(HttpStatusCode.OK, p1.StatusCode);
        Assert.Equal((HttpStatusCode)429, p2.StatusCode);
    }

    [Fact]
    public async Task Можно_создать_и_удалить_автозапчасть_через_api()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var createRequest = new CreateItemRequest("Тормозной диск", 3490.50m);
        var createResponse = await client.PostAsJsonAsync("/api/auto-parts", createRequest);
        var created = await createResponse.Content.ReadFromJsonAsync<Item>();

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        Assert.NotNull(created);
        Assert.Equal("Тормозной диск", created!.Name);
        Assert.Equal(3490.50m, created.Price);
        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal($"/api/auto-parts/by-id/{created.Id}", createResponse.Headers.Location?.OriginalString);

        var getBeforeDelete = await client.GetAsync($"/api/auto-parts/by-id/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getBeforeDelete.StatusCode);

        var deleteResponse = await client.DeleteAsync($"/api/auto-parts/by-id/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var getAfterDelete = await client.GetAsync($"/api/auto-parts/by-id/{created.Id}");
        Assert.Equal(HttpStatusCode.BadRequest, getAfterDelete.StatusCode);
    }

    [Fact]
    public async Task Удаление_несуществующей_детали_возвращает_ошибку()
    {
        var factory = CreateFactory(mode: "Учебный", trustedOrigin: "http://localhost:5173", readLimit: 100, writeLimit: 100);
        var client = factory.CreateClient();

        var response = await client.DeleteAsync($"/api/auto-parts/by-id/{Guid.NewGuid()}");
        var body = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ErrorResponse>(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(error);
        Assert.Equal("bad_request", error!.Code);
        Assert.Contains("Автозапчасть не найдена", error.Message, StringComparison.OrdinalIgnoreCase);
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
