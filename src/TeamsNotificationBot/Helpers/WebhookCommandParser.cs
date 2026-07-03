using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Pure parsing for webhook bot commands, kept out of the handler so it is unit-testable.
/// </summary>
public static class WebhookCommandParser
{
    /// <summary>Parsed <c>create-webhook</c> arguments.</summary>
    public record CreateArgs(string Source, string Account, string Description);

    public const string UsageError =
        "Usage: **create-webhook** `account <updown account> description <description>`\n\n" +
        "Both **account** and **description** are required — they help humans track which updown " +
        "account a webhook belongs to and what it's for. Example:\n\n" +
        "`create-webhook account ops@dsb.no description Prod uptime + SSL`";

    public const string UnsupportedSourceError =
        "Only **updown** webhooks are supported.\n\n" + UsageError;

    // account/description are free text (the account label may contain '@' or '/'); account must
    // precede description, and account text may not contain the literal " description " keyword.
    private static readonly Regex CreatePattern = new(
        @"^account\s+(.+?)\s+description\s+(.+)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Parses the text that follows the <c>create-webhook</c> command word (original casing —
    /// account emails/labels and descriptions are preserved). Grammar:
    /// <c>[updown] account &lt;account&gt; description &lt;description&gt;</c>. Returns the parsed
    /// args, or an error message to show the operator.
    /// </summary>
    public static (CreateArgs? args, string? error) ParseCreate(string? argsText)
    {
        var rest = (argsText ?? string.Empty).Trim();
        if (rest.Length == 0)
            return (null, UsageError);

        // Optional leading "updown" source token. Any other leading token that isn't the "account"
        // keyword is an (unsupported) source — report that specifically.
        if (StartsWithWord(rest, "updown"))
        {
            rest = rest["updown".Length..].Trim();
        }
        else if (!StartsWithWord(rest, "account"))
        {
            return (null, UnsupportedSourceError);
        }

        var match = CreatePattern.Match(rest);
        if (!match.Success)
            return (null, UsageError);

        var account = match.Groups[1].Value.Trim();
        var description = match.Groups[2].Value.Trim();
        if (account.Length == 0 || description.Length == 0)
            return (null, UsageError);

        return (new CreateArgs("updown", account, description), null);
    }

    private static bool StartsWithWord(string s, string word) =>
        s.Length >= word.Length
        && s[..word.Length].Equals(word, StringComparison.OrdinalIgnoreCase)
        && (s.Length == word.Length || char.IsWhiteSpace(s[word.Length]));
}
