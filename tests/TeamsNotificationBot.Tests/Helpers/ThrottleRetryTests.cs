using System.Net;
using TeamsNotificationBot.Helpers;
using Xunit;

namespace TeamsNotificationBot.Tests.Helpers;

public class ThrottleRetryTests
{
    // Records the delays requested instead of actually sleeping, so tests are instant.
    private sealed class FakeDelay
    {
        public List<TimeSpan> Waited { get; } = [];
        public Func<TimeSpan, CancellationToken, Task> Func => (d, _) => { Waited.Add(d); return Task.CompletedTask; };
    }

    private static readonly string ThrottleMessage =
        "ReplyToActivity operation returned an invalid status code '(429) TooManyRequests' - RemoteError: Too many requests., Throttled";

    [Fact]
    public void IsThrottling_DetectsObservedBotFrameworkMessage()
    {
        Assert.True(ThrottleRetry.IsThrottling(new Exception(ThrottleMessage), out _));
    }

    [Fact]
    public void IsThrottling_DetectsHttpRequestException429()
    {
        var ex = new HttpRequestException("boom", null, HttpStatusCode.TooManyRequests);
        Assert.True(ThrottleRetry.IsThrottling(ex, out _));
    }

    [Fact]
    public void IsThrottling_DetectsThrottleInInnerException()
    {
        var ex = new InvalidOperationException("wrapper", new Exception(ThrottleMessage));
        Assert.True(ThrottleRetry.IsThrottling(ex, out _));
    }

    [Theory]
    [InlineData("No conversation reference found")]
    [InlineData("500 Internal Server Error")]
    [InlineData("The request timed out")]
    public void IsThrottling_IgnoresNonThrottleExceptions(string message)
    {
        Assert.False(ThrottleRetry.IsThrottling(new Exception(message), out _));
    }

    [Fact]
    public void TryGetThrottleDelay_UsesCappedExponentialBackoff()
    {
        var cap = TimeSpan.FromSeconds(20);
        Assert.True(ThrottleRetry.TryGetThrottleDelay(new Exception(ThrottleMessage), attempt: 1, cap, out var d1));
        Assert.True(ThrottleRetry.TryGetThrottleDelay(new Exception(ThrottleMessage), attempt: 2, cap, out var d2));
        Assert.True(ThrottleRetry.TryGetThrottleDelay(new Exception(ThrottleMessage), attempt: 3, cap, out var d3));
        Assert.Equal(TimeSpan.FromSeconds(1), d1);
        Assert.Equal(TimeSpan.FromSeconds(2), d2);
        Assert.Equal(TimeSpan.FromSeconds(4), d3);
    }

    [Fact]
    public void TryGetThrottleDelay_ClampsToCap()
    {
        var cap = TimeSpan.FromSeconds(3);
        Assert.True(ThrottleRetry.TryGetThrottleDelay(new Exception(ThrottleMessage), attempt: 10, cap, out var d));
        Assert.Equal(cap, d);
    }

    [Fact]
    public void TryGetThrottleDelay_FalseForNonThrottle()
    {
        Assert.False(ThrottleRetry.TryGetThrottleDelay(new Exception("nope"), attempt: 1, TimeSpan.FromSeconds(5), out _));
    }

    [Fact]
    public async Task ExecuteAsync_RetriesThenSucceeds_OnTransientThrottle()
    {
        var delay = new FakeDelay();
        var attempts = 0;

        await ThrottleRetry.ExecuteAsync(() =>
        {
            attempts++;
            if (attempts < 3) throw new Exception(ThrottleMessage);
            return Task.CompletedTask;
        }, delay: delay.Func);

        Assert.Equal(3, attempts);              // failed twice, succeeded on the third
        Assert.Equal(2, delay.Waited.Count);    // backed off before each retry
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delay.Waited);
    }

    [Fact]
    public async Task ExecuteAsync_Rethrows_AfterMaxAttempts_OnPersistentThrottle()
    {
        var delay = new FakeDelay();
        var attempts = 0;

        await Assert.ThrowsAsync<Exception>(() => ThrottleRetry.ExecuteAsync(() =>
        {
            attempts++;
            throw new Exception(ThrottleMessage);
        }, maxAttempts: 4, delay: delay.Func));

        Assert.Equal(4, attempts);              // tried maxAttempts times
        Assert.Equal(3, delay.Waited.Count);    // backed off between the 4 attempts
    }

    [Fact]
    public async Task ExecuteAsync_RethrowsImmediately_OnNonThrottle()
    {
        var delay = new FakeDelay();
        var attempts = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => ThrottleRetry.ExecuteAsync(() =>
        {
            attempts++;
            throw new InvalidOperationException("not a throttle");
        }, delay: delay.Func));

        Assert.Equal(1, attempts);              // no retry for non-throttle
        Assert.Empty(delay.Waited);
    }
}
