using System.Text.Json;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class NotificationRequestTests
{
    private static NotificationRequest Deserialize(string json) =>
        JsonSerializer.Deserialize<NotificationRequest>(json)!;

    [Fact]
    public void TextFormatWithStringMessageIsValid()
    {
        var request = Deserialize("""{"message": "Hello", "format": "text"}""");

        Assert.True(request.IsValid(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void TextFormatWithObjectMessageIsInvalid()
    {
        var request = Deserialize("""{"message": {"key": "value"}, "format": "text"}""");

        Assert.False(request.IsValid(out var error));
        Assert.Contains("string", error);
    }

    [Fact]
    public void AdaptiveCardFormatWithObjectMessageIsValid()
    {
        var request = Deserialize("""{"message": {"type": "AdaptiveCard"}, "format": "adaptive-card"}""");

        Assert.True(request.IsValid(out var error));
        Assert.Null(error);
    }

    [Fact]
    public void UnsupportedFormatIsInvalid()
    {
        var request = Deserialize("""{"message": "Hello", "format": "html"}""");

        Assert.False(request.IsValid(out var error));
        Assert.Contains("Unsupported format", error);
    }

    [Fact]
    public void MissingMessageIsInvalid()
    {
        var request = Deserialize("""{"format": "text"}""");

        Assert.False(request.IsValid(out var error));
        Assert.Contains("required", error);
    }

    [Fact]
    public void DefaultFormatIsText()
    {
        var request = Deserialize("""{"message": "Hello"}""");

        Assert.Equal("text", request.Format);
        Assert.True(request.IsValid(out _));
    }

    [Fact]
    public void AdaptiveCardFormatWithStringMessageIsInvalid()
    {
        var request = Deserialize("""{"message": "Hello", "format": "adaptive-card"}""");

        Assert.False(request.IsValid(out var error));
        Assert.Contains("JSON object", error);
    }
}
