using System.Text.Json;
using TeamsNotificationBot.Models;
using Xunit;

namespace TeamsNotificationBot.Tests.Models;

public class MetadataRulesTests
{
    [Fact]
    public void Absent_IsValid() => Assert.Null(MetadataRules.Validate(null));

    [Fact]
    public void TypicalCallerMetadata_IsValid()
    {
        Assert.Null(MetadataRules.Validate(new Dictionary<string, string>
        {
            ["repository"] = "example-org/example-repo",
            ["run_id"] = "1234567890",
            ["environment"] = "dev",
            ["trace.id"] = "a-b-c"
        }));
    }

    [Fact]
    public void TenEntries_IsValid_ElevenIsNot()
    {
        var ten = Enumerable.Range(0, 10).ToDictionary(i => $"k{i}", i => "v");
        Assert.Null(MetadataRules.Validate(ten));

        ten["k10"] = "v";
        Assert.Contains("at most 10", MetadataRules.Validate(ten));
    }

    [Theory]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("slash/key")]
    [InlineData("æøå")]
    [InlineData("run\n")]
    [InlineData("run\r\n")]
    public void InvalidKey_IsRejected(string key)
    {
        var error = MetadataRules.Validate(new Dictionary<string, string> { [key] = "v" });

        Assert.NotNull(error);
        Assert.Contains("metadata key", error);
    }

    [Fact]
    public void SixtyFourValidCharactersFollowedByANewline_IsRejected()
    {
        // .NET's $ matches before a final newline; the pattern must not.
        Assert.NotNull(MetadataRules.Validate(new Dictionary<string, string> { [new string('k', 64) + "\n"] = "v" }));
    }

    [Fact]
    public void KeyLength_64IsValid_65IsNot()
    {
        Assert.Null(MetadataRules.Validate(new Dictionary<string, string> { [new string('k', 64)] = "v" }));
        Assert.NotNull(MetadataRules.Validate(new Dictionary<string, string> { [new string('k', 65)] = "v" }));
    }

    [Fact]
    public void ValueLength_256IsValid_257IsNot()
    {
        Assert.Null(MetadataRules.Validate(new Dictionary<string, string> { ["k"] = new string('v', 256) }));
        Assert.Contains("at most 256", MetadataRules.Validate(new Dictionary<string, string> { ["k"] = new string('v', 257) }));
    }

    [Fact]
    public void JsonNullValue_IsRejected()
    {
        var request = JsonSerializer.Deserialize<NotificationRequest>(
            """{"message": "hi", "metadata": {"run": null}}""")!;

        Assert.False(request.IsValid(out var error));
        Assert.Contains("must be a string", error);
    }

    [Fact]
    public void NotificationRequest_AppliesTheRules()
    {
        var request = JsonSerializer.Deserialize<NotificationRequest>(
            """{"message": "hi", "metadata": {"bad key": "v"}}""")!;

        Assert.False(request.IsValid(out var error));
        Assert.Contains("metadata key", error);
    }
}
