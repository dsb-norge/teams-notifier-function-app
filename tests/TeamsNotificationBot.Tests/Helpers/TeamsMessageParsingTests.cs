using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class TeamsMessageParsingTests
{
    [Theory]
    // The exact shape observed in App Insights (self-closing, double quotes) + the command text.
    [InlineData("<quoted messageId=\"1782992989940\"/>\ndelete-post", "1782992989940")]
    [InlineData("<quoted messageId=\"1782992989940\"></quoted>delete-post", "1782992989940")]
    [InlineData("<quoted messageId='456'>...</quoted> delete-post", "456")]
    [InlineData("<quoted messageId=789>", "789")]
    // Extra attributes before/after messageId.
    [InlineData("<quoted itemid=\"x\" messageId=\"111\" author=\"bot\">", "111")]
    // First quoted reference wins.
    [InlineData("<quoted messageId=\"111\"></quoted><quoted messageId=\"222\"></quoted>", "111")]
    public void ExtractQuotedMessageId_ParsesTeamsQuoteReference(string text, string expected)
    {
        Assert.Equal(expected, TeamsMessageParsing.ExtractQuotedMessageId(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("delete-post")]                              // plain command, no quote
    [InlineData("<quoted author=\"bot\">no id here</quoted>")] // quoted tag without messageId
    public void ExtractQuotedMessageId_ReturnsNull_WhenNoQuotedReference(string? text)
    {
        Assert.Null(TeamsMessageParsing.ExtractQuotedMessageId(text));
    }
}
