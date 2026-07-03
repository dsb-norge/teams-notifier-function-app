using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class WebhookCommandParserTests
{
    [Fact]
    public void ParseCreate_ValidAccountAndDescription()
    {
        var (args, error) = WebhookCommandParser.ParseCreate(" account ops@dsb.no description Prod uptime + SSL");

        Assert.Null(error);
        Assert.NotNull(args);
        Assert.Equal("updown", args!.Source);
        Assert.Equal("ops@dsb.no", args.Account);
        Assert.Equal("Prod uptime + SSL", args.Description);
    }

    [Fact]
    public void ParseCreate_OptionalLeadingUpdownToken()
    {
        var (args, error) = WebhookCommandParser.ParseCreate("updown account a@b.no description hello world");

        Assert.Null(error);
        Assert.Equal("a@b.no", args!.Account);
        Assert.Equal("hello world", args.Description);
    }

    [Fact]
    public void ParseCreate_AccountMayContainSlashAndEmail()
    {
        var (args, _) = WebhookCommandParser.ParseCreate(
            "account prod-monitoring / ops@dsb.no description SSL + uptime");

        Assert.Equal("prod-monitoring / ops@dsb.no", args!.Account);
        Assert.Equal("SSL + uptime", args.Description);
    }

    [Fact]
    public void ParseCreate_AccountIsNonGreedy_StopsAtFirstDescriptionKeyword()
    {
        var (args, _) = WebhookCommandParser.ParseCreate("account A description B description C");

        Assert.Equal("A", args!.Account);
        Assert.Equal("B description C", args.Description);
    }

    [Theory]
    [InlineData("")]                         // nothing
    [InlineData("   ")]                       // whitespace only
    [InlineData("account ops@dsb.no")]        // missing description
    [InlineData("account   description x")]   // empty account value
    public void ParseCreate_InvalidReturnsUsageError(string input)
    {
        var (args, error) = WebhookCommandParser.ParseCreate(input);

        Assert.Null(args);
        Assert.Equal(WebhookCommandParser.UsageError, error);
    }

    [Fact]
    public void ParseCreate_UnsupportedSource_ReturnsSourceError()
    {
        var (args, error) = WebhookCommandParser.ParseCreate("slack account a description b");

        Assert.Null(args);
        Assert.Equal(WebhookCommandParser.UnsupportedSourceError, error);
    }
}
