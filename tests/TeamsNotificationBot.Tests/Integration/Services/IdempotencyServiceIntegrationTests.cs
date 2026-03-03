using TeamsNotificationBot.Services;
using TeamsNotificationBot.Tests.Integration.Fixtures;
using Xunit;

namespace TeamsNotificationBot.Tests.Integration.Services;

[Collection("Azurite")]
public class IdempotencyServiceIntegrationTests
{
    private readonly IdempotencyService _service;

    public IdempotencyServiceIntegrationTests(AzuriteFixture azurite)
    {
        var tableClient = azurite.CreateTableClient("idempotency");
        _service = new IdempotencyService(tableClient);
    }

    [Fact]
    public async Task SetAndGet_RoundTrips_StatusCodeAndBody()
    {
        var body = """{"id":"msg-1","status":"accepted"}""";

        await _service.SetAsync("notify", "key-1", 202, body);
        var result = await _service.GetAsync("notify", "key-1");

        Assert.NotNull(result);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal(body, result.ResponseBody);
    }

    [Fact]
    public async Task Get_NotFound_ReturnsNull()
    {
        var result = await _service.GetAsync("nonexistent-scope", "nonexistent-key");

        Assert.Null(result);
    }

    [Fact]
    public async Task Set_Upsert_Overwrites()
    {
        await _service.SetAsync("notify", "overwrite-key", 202, "first");

        await _service.SetAsync("notify", "overwrite-key", 500, "error occurred");
        var result = await _service.GetAsync("notify", "overwrite-key");

        Assert.NotNull(result);
        Assert.Equal(500, result.StatusCode);
        Assert.Equal("error occurred", result.ResponseBody);
    }
}
