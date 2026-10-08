using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Models;

/// <summary>
/// The alias-name rule, in one place for the bot commands, the create-alias card and the lookups.
/// </summary>
public static partial class AliasNames
{
    public const string Rule =
        "Use 2-50 characters: lowercase letters, digits, hyphens. Must start and end with a letter or digit.";

    // The rule itself, shared by both anchored forms below so they can't drift apart.
    private const string Body = @"[a-z0-9][a-z0-9\-]{0,48}[a-z0-9]";

    /// <summary>
    /// The rule for an Adaptive Card <c>Input.Text</c> <c>regex</c>, which the Teams client
    /// evaluates as JavaScript: there <c>$</c> without the multiline flag means end of input, and
    /// <c>\z</c> doesn't exist.
    /// </summary>
    public const string ClientPattern = "^" + Body + "$";

    // \z, not $: in .NET, $ also matches before a final newline, which let "ops\n" through, and a
    // control character in a Table Storage key then failed the write.
    [GeneratedRegex("^" + Body + @"\z")]
    private static partial Regex Pattern();

    /// <summary>Whether <paramref name="name"/> (already lowercased) is a valid alias name.</summary>
    public static bool IsValid(string? name) => name != null && Pattern().IsMatch(name);
}
