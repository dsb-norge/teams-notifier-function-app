using System.Text.Json;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class AdaptiveCardValidatorTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    [Fact]
    public void T7_RejectsActionOpenUrl()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [{ "type": "TextBlock", "text": "Hello" }],
            "actions": [{ "type": "Action.OpenUrl", "title": "Click", "url": "https://evil.com" }]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("Action.OpenUrl", error);
    }

    [Fact]
    public void T8_AcceptsValidCard()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [
                { "type": "TextBlock", "text": "Hello World", "weight": "Bolder" },
                { "type": "TextBlock", "text": "This is a notification." }
            ]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void RejectsMissingType()
    {
        var card = Parse("""
        {
            "version": "1.4",
            "body": [{ "type": "TextBlock", "text": "Hello" }]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("type", error);
    }

    [Fact]
    public void RejectsMissingVersion()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "body": [{ "type": "TextBlock", "text": "Hello" }]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("version", error);
    }

    [Fact]
    public void RejectsMissingBody()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4"
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("body", error);
    }

    [Fact]
    public void RejectsExternalImageUrl()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [
                { "type": "Image", "url": "https://example.com/image.png" }
            ]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("External image", error);
    }

    [Fact]
    public void AcceptsDataUriImage()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [
                { "type": "Image", "url": "data:image/png;base64,iVBOR..." }
            ]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.True(isValid);
        Assert.Null(error);
    }

    [Fact]
    public void RejectsActionSubmit()
    {
        var card = Parse("""
        {
            "type": "AdaptiveCard",
            "version": "1.4",
            "body": [{ "type": "TextBlock", "text": "Hello" }],
            "actions": [{ "type": "Action.Submit", "title": "Submit" }]
        }
        """);

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("Action.Submit", error);
    }

    [Fact]
    public void RejectsNonObjectCard()
    {
        var card = Parse("\"not an object\"");

        var (isValid, error) = AdaptiveCardValidator.Validate(card);

        Assert.False(isValid);
        Assert.Contains("JSON object", error);
    }
}
