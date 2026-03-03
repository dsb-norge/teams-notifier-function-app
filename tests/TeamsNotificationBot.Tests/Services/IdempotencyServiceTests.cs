using Azure;
using Azure.Data.Tables;
using Moq;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class IdempotencyServiceTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly IdempotencyService _service;

    public IdempotencyServiceTests()
    {
        _service = new IdempotencyService(_tableClient.Object);
    }

    [Fact]
    public async Task GetAsync_ExistingEntry_ReturnsResult()
    {
        var entity = new TableEntity("notify", "key-1")
        {
            ["StatusCode"] = 202,
            ["ResponseBody"] = """{"status":"queued"}"""
        };
        _tableClient
            .Setup(t => t.GetEntityAsync<TableEntity>("notify", "key-1", null, default))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        var result = await _service.GetAsync("notify", "key-1");

        Assert.NotNull(result);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("""{"status":"queued"}""", result.ResponseBody);
    }

    [Fact]
    public async Task GetAsync_MissingEntry_ReturnsNull()
    {
        _tableClient
            .Setup(t => t.GetEntityAsync<TableEntity>("notify", "missing", null, default))
            .ThrowsAsync(new RequestFailedException(404, "Not Found"));

        var result = await _service.GetAsync("notify", "missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task SetAsync_UpsertsCalled()
    {
        await _service.SetAsync("notify", "key-1", 202, """{"status":"queued"}""");

        _tableClient.Verify(t => t.UpsertEntityAsync(
            It.Is<TableEntity>(e =>
                e.PartitionKey == "notify" &&
                e.RowKey == "key-1" &&
                (int)e["StatusCode"] == 202 &&
                (string)e["ResponseBody"] == """{"status":"queued"}"""),
            It.IsAny<TableUpdateMode>(),
            default), Times.Once);
    }
}
