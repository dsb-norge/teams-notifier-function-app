namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Sanitizes user-provided values before logging to prevent log forging (CWE-117).
/// Replaces control characters (newlines, tabs, etc.) with underscores.
/// </summary>
public static class LogSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? "";

        // Fast path: check if sanitization is needed
        var needsSanitization = false;
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                needsSanitization = true;
                break;
            }
        }

        if (!needsSanitization)
            return value;

        return string.Create(value.Length, value, static (span, src) =>
        {
            for (var i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? '_' : src[i];
        });
    }
}
