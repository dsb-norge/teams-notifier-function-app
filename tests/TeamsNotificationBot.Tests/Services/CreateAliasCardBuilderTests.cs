using System.Text.Json;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class CreateAliasCardBuilderTests
{
    [Fact]
    public void Build_ReturnsValidAdaptiveCard()
    {
        var cardJson = CreateAliasCardBuilder.Build();

        var doc = JsonDocument.Parse(cardJson);
        Assert.Equal("AdaptiveCard", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.4", doc.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Build_WithSuggestedAlias_PreFillsInput()
    {
        var cardJson = CreateAliasCardBuilder.Build("devops-alerts");

        Assert.Contains("devops-alerts", cardJson);
    }

    [Fact]
    public void Build_WithoutSuggestion_HasEmptyValue()
    {
        var cardJson = CreateAliasCardBuilder.Build();

        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        // Find the aliasName Input.Text (index 3)
        var aliasInput = body[3];
        Assert.Equal("Input.Text", aliasInput.GetProperty("type").GetString());
        Assert.Equal("aliasName", aliasInput.GetProperty("id").GetString());
        Assert.Equal("", aliasInput.GetProperty("value").GetString());
    }

    [Fact]
    public void Build_HasSubmitAction()
    {
        var cardJson = CreateAliasCardBuilder.Build();

        var doc = JsonDocument.Parse(cardJson);
        var actions = doc.RootElement.GetProperty("actions");
        Assert.Equal(1, actions.GetArrayLength());
        Assert.Equal("Action.Submit", actions[0].GetProperty("type").GetString());

        var data = actions[0].GetProperty("data");
        Assert.Equal("createAlias", data.GetProperty("action").GetString());
    }

    [Theory]
    [InlineData("channel", "DevOps Alerts", null, "devops-alerts")]
    [InlineData("channel", "General", null, "general")]
    [InlineData("channel", "My_Channel Name!", null, "my-channel-name")]
    [InlineData("personal", null, "Jane Smith", "jane")]
    [InlineData("groupChat", null, null, null)]
    [InlineData("channel", null, null, null)]
    [InlineData("channel", "", null, null)]
    [InlineData("personal", null, "X", null)] // too short
    [InlineData("channel", "DevOps--Alerts", null, "devops-alerts")] // double hyphens collapse
    [InlineData("channel", "---", null, null)] // only hyphens → null
    [InlineData("channel", "2024-Q1", null, "2024-q1")] // digits preserved
    [InlineData("personal", null, "Ö-Test", "test")] // non-ASCII stripped → "test" which is >= 2
    [InlineData("channel", "  Spaced  ", null, "spaced")] // trim whitespace
    public void DeriveAlias_VariousContexts(
        string conversationType, string? channelName, string? userName, string? expected)
    {
        var result = CreateAliasCardBuilder.DeriveAlias(conversationType, channelName, userName);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Build_WithSuggestedDescription_PreFillsDescription()
    {
        var cardJson = CreateAliasCardBuilder.Build("test", "My channel alerts");
        Assert.Contains("My channel alerts", cardJson);
    }

    [Fact]
    public void Build_RegexPattern_IsPresentInCard()
    {
        var cardJson = CreateAliasCardBuilder.Build();
        var doc = JsonDocument.Parse(cardJson);
        var body = doc.RootElement.GetProperty("body");
        // aliasName input is at index 3
        var aliasInput = body[3];
        var regex = aliasInput.GetProperty("regex").GetString();
        Assert.NotNull(regex);
        Assert.Contains("[a-z0-9]", regex);
    }
}
