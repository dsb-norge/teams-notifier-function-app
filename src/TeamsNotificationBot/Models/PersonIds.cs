using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Models;

/// <summary>
/// How callers name a person: an Entra object ID (a GUID) or a UPN. Used by mentions and by
/// direct messages, which find the person in a team roster by either.
/// </summary>
public static partial class PersonIds
{
    public const int MaxLength = 256;

    [GeneratedRegex(@"^[^@\s<>]+@[^@\s<>]+\z")]
    private static partial Regex UpnPattern();

    public static bool IsValid(string? id) =>
        id is { Length: > 0 and <= MaxLength } && (Guid.TryParse(id, out _) || UpnPattern().IsMatch(id));

    /// <summary>
    /// The object ID in the form stored as a personal conversation's row key (lowercase, hyphenated),
    /// or null when <paramref name="id"/> is a UPN.
    /// </summary>
    public static string? ObjectId(string id) => Guid.TryParse(id, out var guid) ? guid.ToString("D") : null;
}
