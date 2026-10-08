using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class AliasNamesTests
{
    [Theory]
    [InlineData("ops")]
    [InlineData("ab")]
    [InlineData("ops-alerts")]
    [InlineData("team-1-deploy")]
    public void ValidNames_Pass(string name) => Assert.True(AliasNames.IsValid(name));

    [Fact]
    public void FiftyCharacters_Pass_FiftyOneDoNot()
    {
        Assert.True(AliasNames.IsValid(new string('a', 50)));
        Assert.False(AliasNames.IsValid(new string('a', 51)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("-ops")]
    [InlineData("ops-")]
    [InlineData("Ops")]
    [InlineData("ops_alerts")]
    [InlineData("ops alerts")]
    [InlineData("ops\n")]   // .NET's $ matches before a final newline; the rule must not
    [InlineData("ops\r\n")]
    public void InvalidNames_Fail(string? name) => Assert.False(AliasNames.IsValid(name));
}
