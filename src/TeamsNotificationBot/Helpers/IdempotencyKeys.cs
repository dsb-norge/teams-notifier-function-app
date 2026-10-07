using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using TeamsNotificationBot.Services;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// The API's <c>Idempotency-Key</c> header: validation, scoping and the responses it produces.
/// </summary>
public static class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxLength = 256;

    /// <summary>
    /// Reads the header. Returns false with <paramref name="error"/> when it is present but
    /// invalid; otherwise true, with <paramref name="key"/> null when it is absent. An empty or
    /// whitespace header counts as absent.
    /// </summary>
    public static bool TryRead(HttpRequest request, out string? key, out string? error)
    {
        key = null;
        error = null;

        var value = request.Headers[HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(value))
            return true;

        if (value.Length > MaxLength)
        {
            error = $"{HeaderName} is {value.Length} characters; at most {MaxLength} are allowed.";
            return false;
        }

        foreach (var c in value)
        {
            if (c < 0x20 || c > 0x7E)
            {
                error = $"{HeaderName} may contain only printable ASCII characters.";
                return false;
            }
        }

        key = value;
        return true;
    }

    /// <summary>
    /// The record key for a caller's key: the SHA-256 of the calling principal, the target and the
    /// key. So the same key from two callers, or for two targets, never collides, and any printable
    /// key is a valid Table Storage row key (which rejects '/', '\', '#' and '?').
    /// </summary>
    /// <param name="targetParts">
    /// The target, as its separate fields (the alias, or every field of a <c>/v1/send</c> target).
    /// The parts are hashed as a JSON array, so no value can shift a boundary between fields:
    /// joined with a separator, ("a|b", "c") and ("a", "b|c") would hash alike.
    /// </param>
    public static string Scope(string? principalId, string key, params string?[] targetParts) =>
        Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes<string?[]>([principalId, key, .. targetParts])));

    /// <summary>The original response of a completed request, replayed for a duplicate.</summary>
    public static IActionResult Replay(IdempotencyResult result) =>
        new ObjectResult(JsonSerializer.Deserialize<object>(result.ResponseBody)) { StatusCode = result.StatusCode };

    /// <summary>409 for a duplicate that arrives while the original request still holds the key.</summary>
    public static IActionResult InProgress(string instance, string? correlationId) =>
        ApiResponse.Problem(409, "Conflict",
            $"A request with this {HeaderName} is still being processed. Retry after it has completed.",
            instance, correlationId);
}
