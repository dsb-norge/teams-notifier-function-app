using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class HelpTextBuilderTests
{
    [Fact]
    public void CommandHelp_ConfigureWebhook_ExplainsFieldsAndAllEvents()
    {
        var help = HelpTextBuilder.CommandHelp("configure-webhook");

        Assert.NotNull(help);
        Assert.Contains("description", help!);
        Assert.Contains("account", help);
        Assert.Contains("events", help);
        // Every event type is enumerated (sourced from UpdownEventTypes, so it can't drift).
        foreach (var ev in UpdownEventTypes.All)
            Assert.Contains(ev, help);
        // Default set is called out (all except performance_drop).
        Assert.Contains("performance_drop", help);
    }

    [Fact]
    public void CommandHelp_CreateWebhook_MentionsRequiredAccountAndDescription()
    {
        var help = HelpTextBuilder.CommandHelp("create-webhook");

        Assert.NotNull(help);
        Assert.Contains("account", help!);
        Assert.Contains("description", help);
        Assert.Contains("required", help);
    }

    // Every command the bot routes must have per-command help (F5). Guards against adding a command
    // and forgetting its help entry.
    [Theory]
    [InlineData("set-alias")]
    [InlineData("create-alias")]
    [InlineData("remove-alias")]
    [InlineData("list-aliases")]
    [InlineData("create-webhook")]
    [InlineData("configure-webhook")]
    [InlineData("list-webhooks")]
    [InlineData("show-webhook")]
    [InlineData("remove-webhook")]
    [InlineData("rotate-webhook")]
    [InlineData("show-ip-allow-list")]
    [InlineData("update-ip-allow-list")]
    [InlineData("queue-status")]
    [InlineData("queue-peek")]
    [InlineData("queue-retry")]
    [InlineData("queue-retry-all")]
    [InlineData("checkin")]
    [InlineData("delete-post")]
    [InlineData("setup-guide")]
    [InlineData("help")]
    public void CommandHelp_KnownCommands_HaveDetailedHelp(string command)
    {
        Assert.False(string.IsNullOrWhiteSpace(HelpTextBuilder.CommandHelp(command)));
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("")]
    [InlineData("not-a-command")]
    public void CommandHelp_Unknown_ReturnsNull(string command)
    {
        Assert.Null(HelpTextBuilder.CommandHelp(command));
    }
}
