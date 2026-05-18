using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class LogSanitizerTests
{
    [Fact]
    public void Sanitize_Null_ReturnsEmptyString()
    {
        Assert.Equal("", LogSanitizer.Sanitize(null));
    }

    [Fact]
    public void Sanitize_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal("", LogSanitizer.Sanitize(""));
    }

    [Fact]
    public void Sanitize_CleanString_ReturnsSameString()
    {
        const string input = "hello world 123 !@#$%";
        Assert.Equal(input, LogSanitizer.Sanitize(input));
    }

    [Fact]
    public void Sanitize_NewlineCharacters_ReplacedWithUnderscore()
    {
        Assert.Equal("line1_line2", LogSanitizer.Sanitize("line1\nline2"));
        Assert.Equal("line1_line2", LogSanitizer.Sanitize("line1\rline2"));
        Assert.Equal("line1__line2", LogSanitizer.Sanitize("line1\r\nline2"));
    }

    [Fact]
    public void Sanitize_TabCharacter_ReplacedWithUnderscore()
    {
        Assert.Equal("col1_col2", LogSanitizer.Sanitize("col1\tcol2"));
    }

    [Fact]
    public void Sanitize_NullByte_ReplacedWithUnderscore()
    {
        Assert.Equal("before_after", LogSanitizer.Sanitize("before\0after"));
    }

    [Fact]
    public void Sanitize_UnicodeSeparators_ReplacedWithUnderscore()
    {
        // U+2028 LINE SEPARATOR and U+2029 PARAGRAPH SEPARATOR
        Assert.Equal("a_b", LogSanitizer.Sanitize("a\u2028b"));
        Assert.Equal("a_b", LogSanitizer.Sanitize("a\u2029b"));
    }

    [Fact]
    public void Sanitize_MixedContent_OnlyUnsafeReplaced()
    {
        Assert.Equal("user_input_with spaces & symbols!", LogSanitizer.Sanitize("user\ninput\rwith spaces & symbols!"));
    }

    [Fact]
    public void Sanitize_PreservesLength()
    {
        const string input = "ab\ncd\ref";
        var result = LogSanitizer.Sanitize(input);
        Assert.Equal(input.Length, result.Length);
    }

    [Fact]
    public void Sanitize_UnicodeSeparators_CombinedWithOtherControls_AllReplaced()
    {
        Assert.Equal("a___b", LogSanitizer.Sanitize("a\n\u2028\rb"));
        Assert.Equal("___", LogSanitizer.Sanitize("\u2028\u2029\t"));
    }

    [Fact]
    public void Sanitize_ConsecutiveUnsafeCharacters_AllReplaced()
    {
        Assert.Equal("a__b", LogSanitizer.Sanitize("a\n\nb"));
        Assert.Equal("a__b", LogSanitizer.Sanitize("a\r\rb"));
        Assert.Equal("a__b", LogSanitizer.Sanitize("a\0\0b"));
        Assert.Equal("__", LogSanitizer.Sanitize("\n\n"));
    }

    [Fact]
    public void Sanitize_LongCleanString_PassesThroughUnchanged()
    {
        // Guards the no-allocation fast path: a long string with no unsafe
        // characters should be returned without per-character copying.
        var input = string.Concat(Enumerable.Repeat("hello world 123 !@#$% ", 60));
        Assert.True(input.Length >= 1000);
        Assert.Equal(input, LogSanitizer.Sanitize(input));
    }
}
