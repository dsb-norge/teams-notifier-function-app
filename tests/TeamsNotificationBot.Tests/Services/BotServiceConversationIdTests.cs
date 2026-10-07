using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class BotServiceConversationIdTests
{
    [Theory]
    [InlineData("19:abc@thread.tacv2", "19:abc@thread.tacv2")]
    [InlineData("19:abc@thread.tacv2;messageid=1712345678901", "19:abc@thread.tacv2")]
    [InlineData("19:abc@thread.tacv2;MessageId=1712345678901", "19:abc@thread.tacv2")]
    public void BaseConversationId_DropsTheThreadSuffix(string id, string expected) =>
        Assert.Equal(expected, BotService.BaseConversationId(id));
}
