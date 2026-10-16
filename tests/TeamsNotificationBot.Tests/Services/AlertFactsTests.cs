using System.Text.Json;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class AlertFactsTests
{
    private static List<(string Title, string Value)> Flatten(string json, Func<string, bool>? skip = null) =>
        AlertFacts.Flatten(JsonDocument.Parse(json).RootElement, skip)
            .Select(f => (f.Title, f.Value)).ToList();

    [Fact]
    public void NestedObjects_GetDottedPaths()
    {
        var facts = Flatten("""{ "status": "Failed", "properties": { "statusCode": "Conflict", "entity": { "name": "vm-1" } } }""");

        Assert.Equal([("status", "Failed"), ("properties.statusCode", "Conflict"), ("properties.entity.name", "vm-1")], facts);
    }

    [Fact]
    public void JsonInAString_IsUnpacked()
    {
        // An activity log event's statusMessage: the error as escaped JSON.
        var facts = Flatten("""
            { "properties": { "statusMessage": "{\"status\":\"Failed\",\"error\":{\"code\":\"E1\",\"message\":\"2 errors\"}}" } }
            """);

        Assert.Equal([
            ("properties.statusMessage.status", "Failed"),
            ("properties.statusMessage.error.code", "E1"),
            ("properties.statusMessage.error.message", "2 errors")], facts);
    }

    [Fact]
    public void JsonInAString_IsUnpackedThreeLevelsDeep_AndNoFurther()
    {
        // The outer object is the payload itself; a, b and c hold escaped JSON, and so does d.
        var fourth = JsonSerializer.Serialize(new { e = "too deep" });
        var third = JsonSerializer.Serialize(new { d = fourth });
        var second = JsonSerializer.Serialize(new { c = third });
        var first = JsonSerializer.Serialize(new { b = second });

        var facts = Flatten(JsonSerializer.Serialize(new { a = first }));

        Assert.Equal([("a.b.c.d", fourth)], facts);
    }

    [Theory]
    [InlineData("{not json}")]
    [InlineData("[1, 2")]
    [InlineData("{")]
    [InlineData("[link](https://example.com)")]
    public void TextThatOnlyLooksLikeJson_StaysText(string text)
    {
        var facts = Flatten(JsonSerializer.Serialize(new { message = text }));

        Assert.Equal([("message", text)], facts);
    }

    [Fact]
    public void NameValueObjects_BecomeOneFactPerName()
    {
        var facts = Flatten("""{ "dimensions": [ { "name": "Computer", "value": "vm-1" }, { "name": "ResourceId", "value": "abc" } ] }""");

        Assert.Equal([("dimensions.Computer", "vm-1"), ("dimensions.ResourceId", "abc")], facts);
    }

    [Fact]
    public void ObjectsThatAreNotNameValuePairs_AreIndexed()
    {
        // A third property, or a name that isn't a string, makes it an ordinary object.
        var facts = Flatten("""{ "rows": [ { "name": "a", "value": 1, "extra": true }, { "name": 7, "value": 2 } ] }""");

        Assert.Equal([
            ("rows[0].name", "a"), ("rows[0].value", "1"), ("rows[0].extra", "true"),
            ("rows[1].name", "7"), ("rows[1].value", "2")], facts);
    }

    [Fact]
    public void ScalarLists_BecomeOneFact()
    {
        var facts = Flatten("""{ "items": [ "vm-1", 2, true, "", "vm-3" ] }""");

        Assert.Equal([("items", "vm-1, 2, true, vm-3")], facts);
    }

    [Fact]
    public void ListsWithOneItem_HaveNoIndex()
    {
        var facts = Flatten("""{ "allOf": [ { "metricName": "Percentage CPU", "metricValue": 31.1 } ] }""");

        Assert.Equal([("allOf.metricName", "Percentage CPU"), ("allOf.metricValue", "31.1")], facts);
    }

    [Fact]
    public void ListsOfLists_AreWalked()
    {
        // A simple log alert's event rows: a list of lists of name/value pairs.
        var facts = Flatten("""{ "event": [ [ { "name": "type", "value": "NullReferenceException" } ] ] }""");

        Assert.Equal([("event.type", "NullReferenceException")], facts);
    }

    [Fact]
    public void MultipleObjects_AreIndexed()
    {
        var facts = Flatten("""{ "details": [ { "message": "first" }, { "message": "second" } ] }""");

        Assert.Equal([("details[0].message", "first"), ("details[1].message", "second")], facts);
    }

    [Fact]
    public void NullsAndEmpties_AreLeftOut()
    {
        var facts = Flatten("""
            { "a": null, "b": "", "c": "  ", "d": [], "e": {}, "f": "{}", "g": [null], "h": "[]", "i": "kept" }
            """);

        Assert.Equal([("i", "kept")], facts);
    }

    [Fact]
    public void NumbersAndBooleans_KeepTheirText()
    {
        var facts = Flatten("""{ "metricValue": 3.0, "threshold": -1e3, "isHIR": false }""");

        Assert.Equal([("metricValue", "3.0"), ("threshold", "-1e3"), ("isHIR", "false")], facts);
    }

    [Fact]
    public void Skip_LeavesOutNamesAtAnyDepth_AlsoInsideUnpackedJson()
    {
        var facts = Flatten("""
            { "claims": "{\"upn\":\"someone@example.com\"}", "properties": { "secret": 1, "kept": "{\"secret\":2,\"x\":3}" } }
            """,
            skip: name => name is "claims" or "secret");

        Assert.Equal([("properties.kept.x", "3")], facts);
    }

    [Fact]
    public void JsonInAListItem_IsUnpacked()
    {
        var encoded = JsonSerializer.Serialize(new { status = "Failed" });
        var facts = Flatten(JsonSerializer.Serialize(new { one = new[] { encoded }, two = new[] { encoded, "plain" } }));

        Assert.Equal([("one.status", "Failed"), ("two[0].status", "Failed"), ("two[1]", "plain")], facts);
    }

    [Fact]
    public void Skip_AppliesToNameValueNames_AndToJsonInListItems()
    {
        // A simple log alert's event row with a column named like a hidden field, and a list
        // item holding escaped JSON with one inside.
        var facts = Flatten("""
            {
              "event": [ [ { "name": "claims", "value": "{\"ipaddr\":\"192.0.2.10\"}" }, { "name": "type", "value": "E" } ] ],
              "details": [ "{\"claims\":\"x\",\"kept\":1}" ]
            }
            """,
            skip: name => name == "claims");

        Assert.Equal([("event.type", "E"), ("details.kept", "1")], facts);
    }

    [Fact]
    public void WithoutSkip_NameValueNamesAreAllKept()
    {
        var facts = Flatten("""{ "rows": [ { "name": "claims", "value": "the author's" } ] }""");

        Assert.Equal([("rows.claims", "the author's")], facts);
    }

    [Fact]
    public void NoSection_GivesNoFacts()
    {
        Assert.Empty(AlertFacts.Flatten(null));
        Assert.Empty(AlertFacts.Flatten(JsonDocument.Parse("null").RootElement));
    }

    [Fact]
    public void ScalarSection_IsTitledValue()
    {
        Assert.Equal([("value", "just text")], Flatten("\"just text\""));
    }
}
