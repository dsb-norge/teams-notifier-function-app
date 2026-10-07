using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class MessageIdsTests
{
    [Theory]
    [InlineData("msg-0123456789abcdef0123456789abcdef")]
    [InlineData("send-0123456789abcdef0123456789abcdef")]
    [InlineData("updown-0123456789abcdef0123456789abcdef")]
    public void IssuedIds_AreValid(string id) => Assert.True(MessageIds.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("msg-0123")]
    [InlineData("msg-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("msg-0123456789abcdef0123456789abcdef/x")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("msg-0123456789abcdef0123456789abcdef\n")]
    public void AnythingElse_IsNot(string? id) => Assert.False(MessageIds.IsValid(id));
}
