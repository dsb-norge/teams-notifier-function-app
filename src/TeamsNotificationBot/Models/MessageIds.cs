using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Models;

/// <summary>
/// The messageIds the API hands out: a route prefix and 32 hex digits
/// (<c>msg-…</c>, <c>send-…</c>, <c>alert-…</c>, <c>checkin-…</c>, <c>updown-…</c>).
/// </summary>
public static partial class MessageIds
{
    // \z, not $: in .NET, $ also matches before a final newline.
    [GeneratedRegex(@"^[a-z]{1,16}-[0-9a-f]{32}\z")]
    private static partial Regex Pattern();

    /// <summary>
    /// Whether <paramref name="value"/> has the shape of a messageId. Checked before using a
    /// caller's value as a Table Storage key.
    /// </summary>
    public static bool IsValid(string? value) => value != null && Pattern().IsMatch(value);
}
