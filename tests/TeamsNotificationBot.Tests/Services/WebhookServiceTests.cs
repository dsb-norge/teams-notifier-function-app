using Azure;
using Azure.Data.Tables;
using Moq;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class WebhookServiceTests
{
    private readonly Mock<TableClient> _tableClient = new();
    private readonly WebhookService _service;

    public WebhookServiceTests()
    {
        _service = new WebhookService(_tableClient.Object);
    }

    [Fact]
    public void Sha256Hex_IsDeterministic_And64HexChars()
    {
        var a = WebhookService.Sha256Hex("token-abc");
        var b = WebhookService.Sha256Hex("token-abc");
        Assert.Equal(a, b);
        Assert.Equal(64, a.Length);
        Assert.Matches("^[0-9a-f]{64}$", a);
        Assert.NotEqual(a, WebhookService.Sha256Hex("token-abd"));
    }

    [Fact]
    public void GenerateToken_IsUrlSafe_HighEntropy_AndUnique()
    {
        var t1 = WebhookService.GenerateToken();
        var t2 = WebhookService.GenerateToken();
        Assert.NotEqual(t1, t2);
        Assert.True(t1.Length >= 40);
        Assert.Matches("^[A-Za-z0-9_-]+$", t1); // base64url, no padding
    }

    [Fact]
    public void GenerateId_Is8Hex()
    {
        Assert.Matches("^[0-9a-f]{8}$", WebhookService.GenerateId());
    }

    [Fact]
    public async Task ResolveByTokenAsync_HashesTokenForPointRead()
    {
        var token = "supersecret";
        var hash = WebhookService.Sha256Hex(token);
        var entity = new WebhookTokenEntity { PartitionKey = "webhook", RowKey = hash, Id = "abc12345" };

        _tableClient.Setup(t => t.GetEntityAsync<WebhookTokenEntity>("webhook", hash,
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(entity, Mock.Of<Response>()));

        var result = await _service.ResolveByTokenAsync(token);

        Assert.NotNull(result);
        Assert.Equal("abc12345", result.Id);
        _tableClient.Verify(t => t.GetEntityAsync<WebhookTokenEntity>("webhook", hash,
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveByTokenAsync_NotFound_ReturnsNull()
    {
        _tableClient.Setup(t => t.GetEntityAsync<WebhookTokenEntity>("webhook", It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "Not found"));

        Assert.Null(await _service.ResolveByTokenAsync("whatever"));
    }

    [Fact]
    public async Task ResolveByTokenAsync_EmptyToken_ReturnsNullWithoutQuery()
    {
        Assert.Null(await _service.ResolveByTokenAsync(""));
        _tableClient.Verify(t => t.GetEntityAsync<WebhookTokenEntity>(It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void EnabledEvents_EmptyString_FallsBackToDefaults()
    {
        var entity = new WebhookTokenEntity { EnabledEvents = "" };
        Assert.Equal(UpdownEventTypes.DefaultEnabled, entity.GetEnabledEvents());
        Assert.True(entity.IsEventEnabled(UpdownEventTypes.Down));
        Assert.False(entity.IsEventEnabled(UpdownEventTypes.PerformanceDrop)); // excluded by default
    }

    [Fact]
    public void EnabledEvents_ExplicitList_IsRespected()
    {
        var entity = new WebhookTokenEntity { EnabledEvents = "check.ssl_expiration, check.ssl_renewed" };
        Assert.True(entity.IsEventEnabled(UpdownEventTypes.SslExpiration));
        Assert.True(entity.IsEventEnabled(UpdownEventTypes.SslRenewed));
        Assert.False(entity.IsEventEnabled(UpdownEventTypes.Down));
        Assert.False(entity.IsEventEnabled(UpdownEventTypes.Up));
    }
}
