using System.Text.Json;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class SetupGuideCardBuilderTests
{
    [Fact]
    public void Build_ReturnsValidAdaptiveCard()
    {
        var cardJson = SetupGuideCardBuilder.Build("test-app-id", "func-test.azurewebsites.net");

        var doc = JsonDocument.Parse(cardJson);
        Assert.Equal("AdaptiveCard", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.4", doc.RootElement.GetProperty("version").GetString());
        Assert.True(doc.RootElement.GetProperty("body").GetArrayLength() > 0);
    }

    [Fact]
    public void Build_IncludesApiAppIdInAudience()
    {
        var cardJson = SetupGuideCardBuilder.Build("my-api-app-id", "func-test.azurewebsites.net");

        Assert.Contains("api://my-api-app-id", cardJson);
        Assert.Contains("api://my-api-app-id/.default", cardJson);
    }

    [Fact]
    public void Build_IncludesHostnameInEndpoints()
    {
        var cardJson = SetupGuideCardBuilder.Build("app-id", "func-mybot.azurewebsites.net");

        Assert.Contains("func-mybot.azurewebsites.net/api/v1/notify/", cardJson);
        Assert.Contains("func-mybot.azurewebsites.net/api/v1/alert/", cardJson);
    }

    [Fact]
    public void Build_IncludesRequiredRole()
    {
        var cardJson = SetupGuideCardBuilder.Build("app-id", "host");

        Assert.Contains("Notifications.Send", cardJson);
    }

    [Fact]
    public void Build_IncludesCurlExample()
    {
        var cardJson = SetupGuideCardBuilder.Build("app-id", "host");

        Assert.Contains("az account get-access-token", cardJson);
        Assert.Contains("curl", cardJson);
        Assert.Contains("Authorization: Bearer", cardJson);
    }

    [Fact]
    public void Build_IncludesAadAuthSection()
    {
        var cardJson = SetupGuideCardBuilder.Build("app-id", "host");

        Assert.Contains("AAD Resource URI", cardJson);
        Assert.Contains("Common Schema", cardJson);
    }
}
