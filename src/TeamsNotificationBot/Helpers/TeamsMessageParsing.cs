using System.Text.RegularExpressions;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Small pure parsers for Teams message payloads, kept out of the bot handler so they are
/// unit-testable without a turn context.
/// </summary>
public static class TeamsMessageParsing
{
    // Teams embeds a quoted-message reference in the message text when a user *quotes* a message
    // (as opposed to an inline reply), e.g. <quoted messageId="1782992989940"></quoted> or a
    // self-closing <quoted messageId="1782992989940"/>. A quote does NOT set Activity.ReplyToId.
    private static readonly Regex QuotedMessageId = new(
        @"<quoted\b[^>]*\bmessageId\s*=\s*[""']?(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Extracts the target message id from a Teams "quoted" reference in the message text, or null
    /// when there is none. Used by delete-post so quoting an (older) card and sending the command
    /// targets that card — the natural gesture that previously did nothing (refinements.md F7).
    /// </summary>
    public static string? ExtractQuotedMessageId(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;

        var match = QuotedMessageId.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }
}
