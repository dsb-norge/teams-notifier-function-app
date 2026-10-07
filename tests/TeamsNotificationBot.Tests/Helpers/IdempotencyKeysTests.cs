using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class IdempotencyKeysTests
{
    private static (bool ok, string? key, string? error) Read(string? header)
    {
        var req = HttpRequestHelper.CreatePostRequest(
            body: "{}", headers: header == null ? null : new() { [IdempotencyKeys.HeaderName] = header });
        var ok = IdempotencyKeys.TryRead(req, out var key, out var error);
        return (ok, key, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentOrBlank_IsValid_AndMeansNoKey(string? header)
    {
        var (ok, key, error) = Read(header);

        Assert.True(ok);
        Assert.Null(key);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("deploy-run-42-notification")]
    [InlineData("example-org/example-repo#42?attempt=1")] // characters Table Storage rejects in a row key
    [InlineData("3f2a9c8b1e0d4f6a7b5c9e8d1f0a2b3c4d5e6f708192a3b4c5d6e7f8091a2b3c")]
    public void PrintableAscii_IsValid(string header)
    {
        var (ok, key, _) = Read(header);

        Assert.True(ok);
        Assert.Equal(header, key);
    }

    [Fact]
    public void Exactly256Characters_IsValid_257IsNot()
    {
        Assert.True(Read(new string('k', 256)).ok);

        var (ok, _, error) = Read(new string('k', 257));
        Assert.False(ok);
        Assert.Contains("at most 256", error);
    }

    [Theory]
    [InlineData("tab\there")]
    [InlineData("nøkkel")]
    public void NonPrintableOrNonAscii_IsRejected(string header)
    {
        var (ok, _, error) = Read(header);

        Assert.False(ok);
        Assert.Contains("printable ASCII", error);
    }

    [Fact]
    public void Scope_IsLowercaseSha256Hex()
    {
        Assert.Matches("^[0-9a-f]{64}$", IdempotencyKeys.Scope("principal", "key", "ops"));
    }

    [Fact]
    public void Scope_DiffersByCallerTargetAndKey()
    {
        var baseline = IdempotencyKeys.Scope("principal-a", "key", "ops");

        Assert.Equal(baseline, IdempotencyKeys.Scope("principal-a", "key", "ops"));
        Assert.NotEqual(baseline, IdempotencyKeys.Scope("principal-b", "key", "ops"));
        Assert.NotEqual(baseline, IdempotencyKeys.Scope("principal-a", "key", "other"));
        Assert.NotEqual(baseline, IdempotencyKeys.Scope("principal-a", "key2", "ops"));
    }

    [Theory]
    [InlineData("a|b", "c", "a", "b|c")]
    [InlineData("a\nb", "c", "a", "b\nc")]
    [InlineData("a\"", "b", "a", "\"b")]
    public void Scope_NoValueCanShiftAFieldBoundary(string a1, string b1, string a2, string b2)
    {
        Assert.NotEqual(IdempotencyKeys.Scope("p", "k", a1, b1), IdempotencyKeys.Scope("p", "k", a2, b2));
    }

    [Fact]
    public void Scope_AbsentAndEmptyTargetFields_AreDifferent()
    {
        Assert.NotEqual(IdempotencyKeys.Scope("p", "k", "personal", null), IdempotencyKeys.Scope("p", "k", "personal", ""));
    }
}
