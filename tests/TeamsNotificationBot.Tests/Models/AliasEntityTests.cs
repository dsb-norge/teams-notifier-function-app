using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class AliasEntityTests
{
    [Fact]
    public void ConversationKey_Channel_IsTeamAndChannel() =>
        Assert.Equal(("team-1", "19:c@thread.tacv2"),
            new AliasEntity { TargetType = "channel", TeamId = "team-1", ChannelId = "19:c@thread.tacv2" }.ConversationKey());

    [Fact]
    public void ConversationKey_Personal_IsUserPartition() =>
        Assert.Equal(("user", "oid-1"), new AliasEntity { TargetType = "personal", UserId = "oid-1" }.ConversationKey());

    [Fact]
    public void ConversationKey_GroupChat_IsChatPartition() =>
        Assert.Equal(("chat", "19:chat@thread.v2"), new AliasEntity { TargetType = "groupChat", ChatId = "19:chat@thread.v2" }.ConversationKey());

    [Theory]
    [InlineData("channel", "team-1", null, null, null)]
    [InlineData("channel", null, "19:c@thread.tacv2", null, null)]
    [InlineData("personal", null, null, "", null)]
    [InlineData("groupChat", null, null, null, null)]
    [InlineData("meeting", "team-1", "19:c@thread.tacv2", "oid-1", "19:chat@thread.v2")]
    public void ConversationKey_Malformed_IsNull(string type, string? teamId, string? channelId, string? userId, string? chatId) =>
        Assert.Null(new AliasEntity
        {
            TargetType = type, TeamId = teamId, ChannelId = channelId, UserId = userId, ChatId = chatId
        }.ConversationKey());
}
