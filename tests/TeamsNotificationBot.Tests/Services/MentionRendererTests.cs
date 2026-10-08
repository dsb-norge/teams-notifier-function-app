using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.Core.Models;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class MentionRendererTests
{
    private const string Upn = "jane.doe@example.com";

    private static readonly ChannelAccount Jane = new() { Id = "29:jane", Name = "Jane Doe (Ops)", AadObjectId = "oid-jane" };

    private static ChannelAccount? InRoster(string id) => id == Upn ? Jane : null;

    private static readonly MessageMention Merger = new() { Key = "merger", Id = Upn, Name = "Jane" };
    private static readonly MessageMention Stranger = new() { Key = "stranger", Id = "stranger@example.com", Name = "Sam" };
    private static readonly MessageMention OnCall = new() { Key = "oncall", Tag = "dGFnLWlk", Name = "On call" };

    [Fact]
    public void Person_InTheRoster_IsMentionedUnderTheRostersName()
    {
        var rendered = MentionRenderer.Render("text", "Look, <at>merger</at>.", [Merger], InRoster, tagsAllowed: true);

        // The roster's name, not the caller's: the name shown is the person pinged.
        Assert.Equal("Look, <at>Jane Doe (Ops)</at>.", rendered.Message);
        var entity = Assert.Single(rendered.Entities);
        Assert.Equal("<at>Jane Doe (Ops)</at>", entity.Text);
        Assert.Equal("29:jane", entity.Mentioned.Id);
        Assert.Equal("Jane Doe (Ops)", entity.Mentioned.Name);
        Assert.Empty(rendered.Unresolved);
        Assert.False(rendered.HasTagMentions);
    }

    [Fact]
    public void Person_NotInTheRoster_IsPlainText_AndReported()
    {
        var rendered = MentionRenderer.Render("text", "<at>merger</at> and <at>stranger</at>",
            [Merger, Stranger], InRoster, tagsAllowed: true);

        Assert.Equal("<at>Jane Doe (Ops)</at> and Sam", rendered.Message);
        Assert.Single(rendered.Entities);
        Assert.Equal(["stranger@example.com"], rendered.Unresolved);
    }

    [Fact]
    public void Person_NotInTheRoster_WithoutAName_ShowsTheId()
    {
        var mention = new MessageMention { Key = "s", Id = "stranger@example.com" };

        var rendered = MentionRenderer.Render("text", "Hi <at>s</at>", [mention], InRoster, tagsAllowed: true);

        Assert.Equal("Hi stranger@example.com", rendered.Message);
    }

    [Fact]
    public void Tag_IsMentionedAsGiven_WithTheTagType()
    {
        var rendered = MentionRenderer.Render("text", "<at>oncall</at>!", [OnCall], InRoster, tagsAllowed: true);

        Assert.Equal("<at>On call</at>!", rendered.Message);
        var entity = Assert.Single(rendered.Entities);
        Assert.Equal("dGFnLWlk", entity.Mentioned.Id);
        Assert.Equal("tag", entity.Mentioned.Properties["type"].GetString());
        Assert.True(rendered.HasTagMentions);
        Assert.Empty(rendered.Unresolved);
    }

    [Fact]
    public void Tag_WhereTagsArentAllowed_IsPlainText_AndReported()
    {
        var rendered = MentionRenderer.Render("text", "<at>oncall</at> <at>merger</at>",
            [OnCall, Merger], InRoster, tagsAllowed: false);

        Assert.Equal("On call <at>Jane Doe (Ops)</at>", rendered.Message);
        Assert.Equal("29:jane", Assert.Single(rendered.Entities).Mentioned.Id);
        Assert.Equal(["dGFnLWlk"], rendered.Unresolved);
        Assert.False(rendered.HasTagMentions);
    }

    // Teams pairs <at> tags with entities by position: one entity per placement, in text order.

    [Fact]
    public void AKeyPlacedTwice_GetsAnEntityPerPlacement()
    {
        var rendered = MentionRenderer.Render("text", "<at>merger</at>, <at>merger</at>", [Merger], InRoster, true);

        Assert.Equal("<at>Jane Doe (Ops)</at>, <at>Jane Doe (Ops)</at>", rendered.Message);
        Assert.Equal(2, rendered.Entities.Count);
        Assert.All(rendered.Entities, e => Assert.Equal("29:jane", e.Mentioned.Id));
    }

    [Fact]
    public void Entities_FollowThePlacements_NotTheDeclarationOrder()
    {
        var rendered = MentionRenderer.Render("text", "<at>oncall</at> then <at>stranger</at> then <at>merger</at>",
            [Merger, Stranger, OnCall], InRoster, true);

        Assert.Equal("<at>On call</at> then Sam then <at>Jane Doe (Ops)</at>", rendered.Message);
        Assert.Equal(["dGFnLWlk", "29:jane"], rendered.Entities.Select(e => e.Mentioned.Id));
    }

    [Fact]
    public void RosterName_WithAngleBrackets_CantBreakTheTag()
    {
        var odd = new ChannelAccount { Id = "29:odd", Name = "Jane <Ops>\n" };

        var rendered = MentionRenderer.Render("text", "<at>merger</at>", [Merger], _ => odd, true);

        Assert.Equal("<at>Jane Ops</at>", rendered.Message);
    }

    [Fact]
    public void RosterMember_WithoutAName_IsPlainText()
    {
        var nameless = new ChannelAccount { Id = "29:x", Name = "" };

        var rendered = MentionRenderer.Render("text", "<at>merger</at>", [Merger], _ => nameless, true);

        Assert.Equal("Jane", rendered.Message);
        Assert.Equal([Upn], rendered.Unresolved);
    }

    [Fact]
    public void Unresolved_ListsEachIdOnce()
    {
        var again = new MessageMention { Key = "again", Id = "stranger@example.com" };

        var rendered = MentionRenderer.Render("text", "<at>stranger</at> <at>again</at>", [Stranger, again], InRoster, true);

        Assert.Equal(["stranger@example.com"], rendered.Unresolved);
    }

    [Fact]
    public void TextEntities_SerializeAsTeamsExpects()
    {
        var rendered = MentionRenderer.Render("text", "<at>oncall</at>", [OnCall], InRoster, true);

        var json = Microsoft.Agents.Core.Serialization.ProtocolJsonSerializer.ToJson(Assert.Single(rendered.Entities));
        var entity = JsonNode.Parse(json)!;
        Assert.Equal("mention", (string?)entity["type"]);
        Assert.Equal("<at>On call</at>", (string?)entity["text"]);
        Assert.Equal("tag", (string?)entity["mentioned"]!["type"]);
    }

    // --- Cards ---

    private const string CardJson = """
        {"type":"AdaptiveCard","version":"1.5","body":[
          {"type":"TextBlock","text":"Owner: <at>merger</at>"},
          {"type":"FactSet","facts":[{"title":"Escalate","value":"<at>oncall</at> or <at>stranger</at>"}]}],
         "msteams":{"width":"Full"}}
        """;

    [Fact]
    public void Card_PlacementsAreReplaced_AndTheEntitiesGoIntoTheCard()
    {
        var rendered = MentionRenderer.Render("adaptive-card", CardJson, [Merger, OnCall, Stranger], InRoster, true);

        var card = JsonNode.Parse(rendered.Message)!;
        Assert.Equal("Owner: <at>Jane Doe (Ops)</at>", (string?)card["body"]![0]!["text"]);
        Assert.Equal("<at>On call</at> or Sam", (string?)card["body"]![1]!["facts"]![0]!["value"]);
        Assert.Equal("Full", (string?)card["msteams"]!["width"]);

        var entities = card["msteams"]!["entities"]!.AsArray();
        Assert.Equal(2, entities.Count);
        Assert.Equal("mention", (string?)entities[0]!["type"]);
        Assert.Equal("<at>Jane Doe (Ops)</at>", (string?)entities[0]!["text"]);
        Assert.Equal("29:jane", (string?)entities[0]!["mentioned"]!["id"]);
        Assert.Null(entities[0]!["mentioned"]!["type"]);
        Assert.Equal("tag", (string?)entities[1]!["mentioned"]!["type"]);

        // On a card the entities travel inside it, not on the activity.
        Assert.Empty(rendered.Entities);
        Assert.Equal(["stranger@example.com"], rendered.Unresolved);
    }

    [Fact]
    public void Card_PlacementsInAnArrayOfStrings_AreReplacedToo()
    {
        const string card = """{"type":"AdaptiveCard","body":[],"lines":["<at>merger</at>", 1, ["<at>stranger</at>"]]}""";

        var rendered = MentionRenderer.Render("adaptive-card", card, [Merger, Stranger], InRoster, true);

        var lines = JsonNode.Parse(rendered.Message)!["lines"]!.AsArray();
        Assert.Equal("<at>Jane Doe (Ops)</at>", (string?)lines[0]);
        Assert.Equal(1, (int)lines[1]!);
        Assert.Equal("Sam", (string?)lines[2]![0]);
    }

    [Fact]
    public void Card_Entities_FollowThePlacementsInDocumentOrder()
    {
        const string card = """
            {"type":"AdaptiveCard","body":[
              {"type":"TextBlock","text":"<at>oncall</at>"},
              {"type":"Container","items":[{"type":"TextBlock","text":"<at>merger</at> and <at>oncall</at>"}]},
              {"type":"TextBlock","text":"<at>merger</at>"}]}
            """;

        var rendered = MentionRenderer.Render("adaptive-card", card, [Merger, OnCall], InRoster, true);

        var ids = JsonNode.Parse(rendered.Message)!["msteams"]!["entities"]!.AsArray()
            .Select(e => (string?)e!["mentioned"]!["id"]);
        Assert.Equal(["dGFnLWlk", "29:jane", "dGFnLWlk", "29:jane"], ids);
    }

    [Fact]
    public void Card_WithoutMsteams_GetsOne()
    {
        const string card = """{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>merger</at>"}]}""";

        var rendered = MentionRenderer.Render("adaptive-card", card, [Merger], InRoster, true);

        Assert.Single(JsonNode.Parse(rendered.Message)!["msteams"]!["entities"]!.AsArray());
    }

    [Fact]
    public void Card_NobodyResolved_GetsNoEntities()
    {
        const string card = """{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>stranger</at>"}]}""";

        var rendered = MentionRenderer.Render("adaptive-card", card, [Stranger], InRoster, true);

        var node = JsonNode.Parse(rendered.Message)!;
        Assert.Equal("Sam", (string?)node["body"]![0]!["text"]);
        Assert.Null(node["msteams"]);
    }

    [Fact]
    public void Card_ARosterNameIsEscapedAsJson()
    {
        var quoted = new ChannelAccount { Id = "29:q", Name = "Jane \"JD\" Doe" };
        const string card = """{"type":"AdaptiveCard","body":[{"type":"TextBlock","text":"<at>merger</at>"}]}""";

        var rendered = MentionRenderer.Render("adaptive-card", card, [Merger], _ => quoted, true);

        using var doc = JsonDocument.Parse(rendered.Message);
        Assert.Equal("<at>Jane \"JD\" Doe</at>", doc.RootElement.GetProperty("body")[0].GetProperty("text").GetString());
    }
}
