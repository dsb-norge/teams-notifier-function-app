using System.Net;
using Microsoft.Extensions.Logging;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Bounded retry for outbound Teams / Bot Framework sends that get throttled (HTTP 429). Bot
/// Framework throttles a bot's outbound sends under burst (e.g. many updown events at once); the
/// send surfaces as an exception (`ReplyToActivity … '(429) TooManyRequests' … Throttled`). Without
/// this, a 429 propagates to the queue trigger and burns the queue's limited dequeue budget
/// (30 s × maxDequeueCount) — a sustained throttle could poison a card. This smooths short bursts by
/// retrying the whole send operation with **capped exponential backoff**, and rethrows on a persistent
/// throttle so the queue can still retry later. See refinements.md F11.
///
/// NOTE: the current exception surface does not expose a parsed <c>Retry-After</c>, so backoff is
/// used (not the exact header). The retry logs the exception type; honoring an exact <c>Retry-After</c>
/// is a future enhancement if a deployed build shows the SDK exposes one.
///
/// Retries the whole operation (which builds a fresh request each attempt), not an HttpRequestMessage,
/// so there is no "request already sent" reuse problem.
/// </summary>
public static class ThrottleRetry
{
    public static async Task ExecuteAsync(
        Func<Task> operation,
        int maxAttempts = 4,
        TimeSpan? maxDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var cap = maxDelay ?? TimeSpan.FromSeconds(20);
        var wait = delay ?? ((d, ct) => Task.Delay(d, ct));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation();
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts && TryGetThrottleDelay(ex, attempt, cap, out var d))
            {
                // Log the exception type so a deployed build reveals the SDK's actual 429 shape (and
                // whether a Retry-After is available to honor precisely).
                logger?.LogWarning(ex,
                    "Outbound send throttled (429). Attempt {Attempt}/{Max}; backing off {DelaySeconds:n1}s. ExceptionType={Type}",
                    attempt, maxAttempts, d.TotalSeconds, ex.GetType().FullName);
                await wait(d, cancellationToken);
            }
        }
    }

    /// <summary>True (with the delay to wait) if <paramref name="ex"/> is a 429 throttle.</summary>
    internal static bool TryGetThrottleDelay(Exception ex, int attempt, TimeSpan cap, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!IsThrottling(ex, out var retryAfter))
            return false;

        if (retryAfter is { } ra)
        {
            delay = ra < TimeSpan.Zero ? TimeSpan.Zero : (ra > cap ? cap : ra);
            return true;
        }

        // Capped exponential backoff (1s, 2s, 4s, …). Clamp the SECONDS to the cap before building the
        // TimeSpan — Math.Pow overflows to +Infinity for large attempt counts, and
        // TimeSpan.FromSeconds(Infinity) throws, which would turn a throttle into a hard failure.
        var backoffSeconds = Math.Min(Math.Pow(2, attempt - 1), cap.TotalSeconds);
        delay = TimeSpan.FromSeconds(backoffSeconds);
        return true;
    }

    /// <summary>
    /// Detects an HTTP 429 anywhere in the exception chain. Uses the strongly-typed
    /// <see cref="HttpRequestException.StatusCode"/> when available, and falls back to the message
    /// text — the M365 Agents / Bot Framework connector surfaces throttling as an
    /// "…'(429) TooManyRequests'… Throttled" message. <paramref name="retryAfter"/> is populated only
    /// if a delay can be extracted (none of the current exception types expose it, so it stays null
    /// and backoff applies).
    /// </summary>
    internal static bool IsThrottling(Exception ex, out TimeSpan? retryAfter)
    {
        retryAfter = null;
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
                return true;

            var message = e.Message;
            if (message is not null
                && (message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("(429)", StringComparison.Ordinal)))
                return true;
        }

        return false;
    }
}
