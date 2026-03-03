using Azure;
using Azure.Data.Tables;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class AliasServiceTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly AliasService _service;

    public AliasServiceTests()
    {
        _service = new AliasService(_tableClient.Object);
    }

    [Fact]
    public async Task GetAliasAsync_Found_ReturnsEntity()
    {
        var entity = new AliasEntity
        {
            PartitionKey = "alias",
            RowKey = "devops-test",
            TargetType = "channel",
            TeamId = "team-1",
            ChannelId = "channel-1"
        };

        _tableClient.Setup(t => t.GetEntityAsync<AliasEntity>("alias", "devops-test",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        var result = await _service.GetAliasAsync("devops-test");

        Assert.NotNull(result);
        Assert.Equal("channel", result.TargetType);
        Assert.Equal("team-1", result.TeamId);
        Assert.Equal("channel-1", result.ChannelId);
    }

    [Fact]
    public async Task GetAliasAsync_NotFound_ReturnsNull()
    {
        _tableClient.Setup(t => t.GetEntityAsync<AliasEntity>("alias", "nonexistent",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        var result = await _service.GetAliasAsync("nonexistent");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAliasAsync_LowercasesName()
    {
        var entity = new AliasEntity { PartitionKey = "alias", RowKey = "myalias" };

        _tableClient.Setup(t => t.GetEntityAsync<AliasEntity>("alias", "myalias",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        await _service.GetAliasAsync("MyAlias");

        _tableClient.Verify(t => t.GetEntityAsync<AliasEntity>("alias", "myalias",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SetAliasAsync_UpsertsCorrectedEntity()
    {
        var entity = new AliasEntity { TargetType = "channel", TeamId = "t1", ChannelId = "c1" };

        _tableClient.Setup(t => t.UpsertEntityAsync(It.IsAny<AliasEntity>(),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        var result = await _service.SetAliasAsync("MyAlias", entity);

        Assert.Equal("alias", result.PartitionKey);
        Assert.Equal("myalias", result.RowKey);

        _tableClient.Verify(t => t.UpsertEntityAsync(
            It.Is<AliasEntity>(e => e.PartitionKey == "alias" && e.RowKey == "myalias"),
            It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveAliasAsync_Found_ReturnsTrue()
    {
        var entity = new AliasEntity { PartitionKey = "alias", RowKey = "myalias" };

        _tableClient.Setup(t => t.GetEntityAsync<AliasEntity>("alias", "myalias",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        _tableClient.Setup(t => t.DeleteEntityAsync("alias", "myalias",
            It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        var result = await _service.RemoveAliasAsync("MyAlias");

        Assert.True(result);
    }

    [Fact]
    public async Task RemoveAliasAsync_NotFound_ReturnsFalse()
    {
        _tableClient.Setup(t => t.GetEntityAsync<AliasEntity>("alias", "nonexistent",
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        var result = await _service.RemoveAliasAsync("nonexistent");

        Assert.False(result);

        // Verify DeleteEntityAsync was never called since entity doesn't exist
        _tableClient.Verify(t => t.DeleteEntityAsync("alias", "nonexistent",
            It.IsAny<ETag>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
