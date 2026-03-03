using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Helpers;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class HealthFunctionTests
{
    private readonly HealthFunction _function = new();

    [Fact]
    public void Returns200WithStatusAndVersion()
    {
        var req = HttpRequestHelper.CreateGetRequest();

        var result = _function.Run(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(AppInfo.Version, doc.RootElement.GetProperty("version").GetString());
        Assert.True(doc.RootElement.TryGetProperty("timestamp", out _));
    }
}
