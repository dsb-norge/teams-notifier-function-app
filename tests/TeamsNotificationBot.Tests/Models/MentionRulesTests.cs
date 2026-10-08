using System.Text.Json;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class MentionRulesTests
{
    private const string Upn = "jane.doe@example.com";
    private const string ObjectId = "0b5f8a8e-1c1e-4f43-9a37-2f6b1b0c9d11";

    private static MessageMention Person(string key, string id = Upn, string? name = "Jane Doe") =>
        new() { Key = key, Id = id, Name = name };

    private static MessageMention Tag(string key, string tag = "MjQzMmI1N2Itd==", string? name = "On call") =>
        new() { Key = key, Tag = tag, Name = name };

    private static JsonElement Text(string text) => JsonSerializer.SerializeToElement(text);

    private static JsonElement Card(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string? ValidateText(string text, params MessageMention?[] mentions) =>
        MentionRules.Validate(mentions, "text", Text(text));

    [Fact]
    public void NoMentions_IsValid_WhateverTheMessageSays()
    {
        // Without 'mentions' the message is forwarded unchanged, as before mentions existed.
        Assert.Null(MentionRules.Validate(null, "text", Text("<at>anything</at> <at>")));
        Assert.Null(MentionRules.Validate([], "text", Text("<at>anything</at>")));
    }

    [Fact]
    public void PersonAndTag_EachPlaced_AreValid()
    {
        Assert.Null(ValidateText("Apply failed. <at>merger</at>, <at>oncall</at>: please look.",
            Person("merger"), Tag("oncall")));
    }

    [Fact]
    public void ObjectId_IsAcceptedAsAPersonId()
    {
        Assert.Null(ValidateText("<at>a</at>", Person("a", ObjectId)));
        Assert.Null(ValidateText("<at>a</at>", Person("a", ObjectId.ToUpperInvariant())));
    }

    [Fact]
    public void AKeyPlacedTwice_IsValid()
    {
        Assert.Null(ValidateText("<at>a</at> and again <at>a</at>", Person("a")));
    }

    [Fact]
    public void PersonName_IsOptional()
    {
        Assert.Null(ValidateText("<at>a</at>", Person("a", name: null)));
    }

    [Theory]
    [InlineData("<at>b</at>", "doesn't name a key")]
    [InlineData("no placement at all", "never placed")]
    [InlineData("<at>a</at> <at>", "isn't a mention placement")]
    [InlineData("<at>a</at> </at>", "isn't a mention placement")]
    [InlineData("<at>a</at> <AT>a</AT>", "isn't a mention placement")]
    [InlineData("<at>a</at> <at id=\"x\">a</at>", "isn't a mention placement")]
    [InlineData("<at><at>a</at></at>", "isn't a mention placement")]
    [InlineData("<at> a</at>", "doesn't name a key")]
    public void Placements_MustMatchTheDeclaredKeysExactly(string text, string expected)
    {
        var error = ValidateText(text, Person("a"));

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void HtmlEscapedAtTags_AreJustText()
    {
        // Matching runs on the raw text: entities aren't decoded first.
        Assert.Null(ValidateText("<at>a</at> writes &lt;at&gt;x&lt;/at&gt;", Person("a")));
    }

    [Fact]
    public void AnAttributeLikeWordStartingWithAt_IsNotAnAtTag()
    {
        Assert.Null(ValidateText("<at>a</at> <attachment>", Person("a")));
    }

    [Fact]
    public void EveryKeyMustBePlaced()
    {
        var error = ValidateText("<at>a</at>", Person("a"), Person("b"));

        Assert.Contains("'b' is never placed", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("a<b")]
    [InlineData("key\n")]
    public void InvalidKey_IsRejected(string key)
    {
        Assert.Contains("key is invalid", ValidateText("text", Person(key)));
    }

    [Fact]
    public void KeyLongerThan64_IsRejected()
    {
        var key = new string('k', MentionRules.MaxKeyLength + 1);
        Assert.Contains("key is invalid", ValidateText($"<at>{key}</at>", Person(key)));
    }

    [Fact]
    public void DuplicateKey_IsRejected()
    {
        Assert.Contains("more than once", ValidateText("<at>a</at>", Person("a"), Tag("a")));
    }

    [Fact]
    public void NullEntry_IsRejected()
    {
        Assert.Contains("mentions[0] must be an object", ValidateText("x", [null]));
    }

    [Fact]
    public void IdAndTag_AreExclusive_AndOneIsRequired()
    {
        Assert.Contains("exactly one of", ValidateText("<at>a</at>",
            new MessageMention { Key = "a", Id = Upn, Tag = "dGFn", Name = "x" }));
        Assert.Contains("exactly one of", ValidateText("<at>a</at>",
            new MessageMention { Key = "a", Name = "x" }));
    }

    [Theory]
    [InlineData("jane")]
    [InlineData("jane@")]
    [InlineData("@example.com")]
    [InlineData("jane doe@example.com")]
    [InlineData("<jane>@example.com")]
    [InlineData("29:1abc")]
    public void PersonId_MustBeAnObjectIdOrAUpn(string id)
    {
        Assert.Contains("must be an Entra object ID", ValidateText("<at>a</at>", Person("a", id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Jane <Doe>")]
    [InlineData("Jane\nDoe")]
    public void InvalidName_IsRejected(string name)
    {
        Assert.Contains("invalid name", ValidateText("<at>a</at>", Person("a", name: name)));
    }

    [Fact]
    public void TagName_IsRequired()
    {
        Assert.Contains("'name' is required for a tag", ValidateText("<at>t</at>", Tag("t", name: null)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("tab\tid")]
    public void InvalidTagId_IsRejected(string tag)
    {
        Assert.Contains("'tag' must be a tag ID", ValidateText("<at>t</at>", Tag("t", tag)));
    }

    [Fact]
    public void AtMost20People()
    {
        var people = Enumerable.Range(0, MentionRules.MaxPeople + 1).Select(i => Person($"p{i}")).ToArray();
        var text = string.Concat(people.Select(p => $"<at>{p.Key}</at>"));

        Assert.Null(ValidateText(text[..^"<at>p20</at>".Length], people[..^1]));
        Assert.Contains("at most 20", ValidateText(text, people));
    }

    [Fact]
    public void AtMost10Tags()
    {
        var tags = Enumerable.Range(0, MentionRules.MaxTags + 1).Select(i => Tag($"t{i}")).ToArray();
        var text = string.Concat(tags.Select(t => $"<at>{t.Key}</at>"));

        Assert.Null(ValidateText(text[..^"<at>t10</at>".Length], tags[..^1]));
        Assert.Contains("at most 10", ValidateText(text, tags));
    }

    [Fact]
    public void AtMost30Placements_ARepeatCountingEachTime()
    {
        var thirty = string.Concat(Enumerable.Repeat("<at>a</at>", MentionRules.MaxPlacements));

        Assert.Null(ValidateText(thirty, Person("a")));
        Assert.Contains("more than 30 mention placements", ValidateText(thirty + "<at>a</at>", Person("a")));
    }

    [Fact]
    public void Card_PlacementsAreCountedAcrossItsStrings()
    {
        var item = """{"type":"TextBlock","text":"<at>a</at><at>a</at><at>a</at>"}""";
        var card = Card($$"""{"type":"AdaptiveCard","body":[{{string.Join(",", Enumerable.Repeat(item, 11))}}]}""");

        Assert.Contains("more than 30", MentionRules.Validate([Person("a")], "adaptive-card", card));
    }

    // --- Cards ---

    [Fact]
    public void Card_PlacementsAreFoundInAnyStringValue()
    {
        var card = Card("""
            {"type":"AdaptiveCard","version":"1.5","body":[
              {"type":"TextBlock","text":"Owner: <at>a</at>"},
              {"type":"FactSet","facts":[{"title":"On call","value":"<at>t</at>"}]}]}
            """);

        Assert.Null(MentionRules.Validate([Person("a"), Tag("t")], "adaptive-card", card));
    }

    [Fact]
    public void Card_UnplacedKey_IsRejected()
    {
        var card = Card("""{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}]}""");

        Assert.Contains("'t' is never placed",
            MentionRules.Validate([Person("a"), Tag("t")], "adaptive-card", card));
    }

    [Fact]
    public void Card_WithItsOwnMentionEntities_IsRejected_WhenItUsesMentions()
    {
        var card = Card("""
            {"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}],
             "msteams":{"entities":[{"type":"mention","text":"<at>a</at>","mentioned":{"id":"x","name":"x"}}]}}
            """);

        Assert.Contains("can't also carry mention entities",
            MentionRules.Validate([Person("a")], "adaptive-card", card));
    }

    [Fact]
    public void Card_WithItsOwnMentionEntities_IsForwardedUnchecked_WithoutMentions()
    {
        var card = Card("""
            {"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>X</at>"}],
             "msteams":{"entities":[{"type":"mention","text":"<at>X</at>","mentioned":{"id":"x","name":"X"}}]}}
            """);

        Assert.Null(MentionRules.Validate(null, "adaptive-card", card));
    }

    [Fact]
    public void Card_WithOtherMsteamsSettings_IsValid()
    {
        var card = Card("""{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}],"msteams":{"width":"Full"}}""");

        Assert.Null(MentionRules.Validate([Person("a")], "adaptive-card", card));
    }

    [Fact]
    public void Card_WithOtherEntityTypes_IsValid()
    {
        var card = Card("""
            {"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}],
             "msteams":{"entities":[{"type":"clientInfo"}, "not an object"]}}
            """);

        Assert.Null(MentionRules.Validate([Person("a")], "adaptive-card", card));
    }

    [Theory]
    [InlineData("""{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}],"msteams":"full"}""", "'msteams' must be an object")]
    [InlineData("""{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>a</at>"}],"msteams":{"entities":{}}}""", "'msteams.entities' must be an array")]
    public void Card_MsteamsOfTheWrongShape_IsRejected(string json, string expected)
    {
        Assert.Contains(expected, MentionRules.Validate([Person("a")], "adaptive-card", Card(json)));
    }

    // --- Targets ---

    [Fact]
    public void CheckTarget_PeopleAndTags_InAChannel_AreFine()
    {
        Assert.Null(MentionRules.CheckTarget([Person("a"), Tag("t")], "channel"));
    }

    [Fact]
    public void CheckTarget_People_InAGroupChat_AreFine_ButTagsAreNot()
    {
        Assert.Null(MentionRules.CheckTarget([Person("a")], "groupChat"));
        Assert.Contains("only be used in a channel", MentionRules.CheckTarget([Person("a"), Tag("t")], "groupChat"));
    }

    [Fact]
    public void CheckTarget_NoMentions_InAPersonalChat()
    {
        Assert.Contains("personal chat", MentionRules.CheckTarget([Person("a")], "personal"));
        Assert.Null(MentionRules.CheckTarget(null, "personal"));
        Assert.Null(MentionRules.CheckTarget([], "personal"));
    }
}
