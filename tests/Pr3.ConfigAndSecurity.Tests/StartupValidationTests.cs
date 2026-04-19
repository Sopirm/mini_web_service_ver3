using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Pr3.ConfigAndSecurity.Tests;

public sealed class StartupValidationTests
{
    [Fact]
    public void Некорректная_конфигурация_останавливает_запуск()
    {
        var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.Sources.Clear();

                    var settings = new Dictionary<string, string?>
                    {
                        ["App:Mode"] = "Учебный",
                        ["App:RateLimits:ReadPerMinute"] = "0",
                        ["App:RateLimits:WritePerMinute"] = "0"
                    };

                    cfg.AddInMemoryCollection(settings);
                });
            });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = FlattenMessages(ex);

        Assert.Contains("Некорректные настройки приложения", message, StringComparison.OrdinalIgnoreCase);
    }

    private static string FlattenMessages(Exception ex)
    {
        var parts = new List<string>();
        Exception? current = ex;

        while (current is not null)
        {
            parts.Add(current.Message);
            current = current.InnerException;
        }

        return string.Join(" | ", parts);
    }
}
