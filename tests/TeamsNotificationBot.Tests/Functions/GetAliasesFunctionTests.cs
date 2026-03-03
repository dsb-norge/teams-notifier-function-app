using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TeamsNotificationBot.Functions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Functions;

public class GetAliasesFunctionTests : IDisposable
{
    private readonly Mock<IAliasService> _aliasService = new();
    private readonly GetAliasesFunction _function;

    public GetAliasesFunctionTests()
    {
        _function = new GetAliasesFunction(
            _aliasService.Object,
            NullLogger<GetAliasesFunction>.Instance);

        Environment.SetEnvironmentVariable("DEBUG_MODE", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DEBUG_MODE", null);
    }

    [Fact]
    public async Task DebugEnabled_Returns200WithAliases()
    {
        Environment.SetEnvironmentVariable("DEBUG_MODE", "true");
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>
        {
            new()
            {
                RowKey = "devops-test",
                TargetType = "channel",
                TeamId = "team-1",
                ChannelId = "channel-1",
                Description = "Test",
                CreatedByName = "User",
                CreatedAt = DateTimeOffset.UtcNow
            }
        });

        var req = HttpRequestHelper.CreateGetRequest();

        var result = await _function.Run(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);

        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var aliases = doc.RootElement.GetProperty("aliases");
        Assert.Equal(1, aliases.GetArrayLength());
        Assert.Equal("devops-test", aliases[0].GetProperty("alias").GetString());
    }

    [Fact]
    public async Task DebugDisabled_Returns403()
    {
        // DEBUG_MODE not set (default = disabled)
        var req = HttpRequestHelper.CreateGetRequest();

        var result = await _function.Run(req);

        var objectResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, objectResult.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(objectResult.Value);
        Assert.Equal("Forbidden", problem.Title);
    }

    [Fact]
    public async Task DebugEnabled_EmptyAliases_Returns200WithEmptyArray()
    {
        Environment.SetEnvironmentVariable("DEBUG_MODE", "true");
        _aliasService.Setup(s => s.GetAllAliasesAsync()).ReturnsAsync(new List<AliasEntity>());

        var req = HttpRequestHelper.CreateGetRequest();

        var result = await _function.Run(req);

        var okResult = Assert.IsType<OkObjectResult>(result);
        Assert.Equal(200, okResult.StatusCode);

        var json = JsonSerializer.Serialize(okResult.Value);
        var doc = JsonDocument.Parse(json);
        var aliases = doc.RootElement.GetProperty("aliases");
        Assert.Equal(0, aliases.GetArrayLength());
    }
}
