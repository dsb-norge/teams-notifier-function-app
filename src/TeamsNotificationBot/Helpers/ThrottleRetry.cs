using System.Net;
using Microsoft.Extensions.Logging;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Bounded retry for outbound Teams / Bot Framework sends that get throttled (HTTP 429). Bot
/// Framework throttles a bot's outbound sends under burst (e.g. many updown events at once); the
/// send surfaces as an exception (`ReplyToActivity … '(429) TooManyRequests' … Throttled`). Without
/// this, a 429 propagates to the queue trigger and burns the queue's limited dequeue budget
/// (30 s × maxDequeueCount) — a sustained throttle could poison a card. This smooths short bursts by
/// retrying the whole send operation with capped backoff (respecting a Retry-After when the
/// exception exposes one), and rethrows on a persistent throttle so the queue can still retry later.
/// See refinements.md F11.
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

        // Honor Retry-After when present; otherwise capped exponential backoff (1s, 2s, 4s, …).
        var d = retryAfter ?? TimeSpan.FromSeconds(Math.Pow(2, attempt - 1));
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        delay = d > cap ? cap : d;
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
