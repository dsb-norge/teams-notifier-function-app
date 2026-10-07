using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Logging.Abstractions;
using TeamsNotificationBot.Models;
using TeamsNotificationBot.Services;
using Xunit;

namespace TeamsNotificationBot.Tests.Services;

public class DeliveryEventsTests
{
    private static QueueMessage AliasMessage(Dictionary<string, string>? metadata = null) => new()
    {
        MessageId = "msg-1",
        Alias = "ops",
        Format = "text",
        Message = "hello",
        Source = "notify",
        PrincipalId = "00000000-0000-0000-0000-000000000001",
        Metadata = metadata,
        EnqueuedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void Build_CarriesCallerTargetAndMetadata()
    {
        var evt = DeliveryEvents.Build(DeliveryEvents.QueuedEventName,
            AliasMessage(new() { ["run"] = "42", ["environment"] = "dev" }), null);

        Assert.Equal("NotificationQueued", evt.Name);
        Assert.Equal("msg-1", evt.Properties["MessageId"]);
        Assert.Equal("notify", evt.Properties["Source"]);
        Assert.Equal("00000000-0000-0000-0000-000000000001", evt.Properties["PrincipalId"]);
        Assert.Equal("ops", evt.Properties["Alias"]);
        Assert.Equal("42", evt.Properties["meta.run"]);
        Assert.Equal("dev", evt.Properties["meta.environment"]);
    }

    [Fact]
    public void Build_DirectTarget_CarriesTargetIds()
    {
        var message = new QueueMessage
        {
            MessageId = "send-1",
            Source = "send",
            Target = new MessageTarget { Type = "channel", TeamId = "team-1", ChannelId = "19:abc@thread.tacv2" }
        };

        var evt = DeliveryEvents.Build(DeliveryEvents.QueuedEventName, message, null);

        Assert.Equal("channel", evt.Properties["TargetType"]);
        Assert.Equal("team-1", evt.Properties["TeamId"]);
        Assert.Equal("19:abc@thread.tacv2", evt.Properties["ChannelId"]);
        Assert.False(evt.Properties.ContainsKey("UserId"));
        Assert.False(evt.Properties.ContainsKey("Alias"));
    }

    [Fact]
    public void Build_PinsSamplingPercentageTo100()
    {
        var evt = DeliveryEvents.Build(DeliveryEvents.DeliveredEventName, AliasMessage(), null);

        Assert.Equal(100, ((ISupportSampling)evt).SamplingPercentage);
    }

    [Fact]
    public void Build_SanitizesCallerControlledValues()
    {
        var evt = DeliveryEvents.Build(DeliveryEvents.QueuedEventName,
            AliasMessage(new() { ["note"] = "line1\nline2" }), null);

        Assert.Equal("line1_line2", evt.Properties["meta.note"]);
    }

    [Fact]
    public void DeliveryFailed_TruncatesLongErrors()
    {
        var channel = new CapturingChannel();
        var events = new DeliveryEvents(new TelemetryClient(NewConfiguration(channel)), NullLogger<DeliveryEvents>.Instance);

        events.DeliveryFailed(AliasMessage(), 3, "InvalidOperationException", new string('x', 5000));

        var evt = Assert.IsType<EventTelemetry>(Assert.Single(channel.Items));
        Assert.Equal("NotificationDeliveryFailed", evt.Name);
        Assert.Equal("3", evt.Properties["DequeueCount"]);
        Assert.Equal("InvalidOperationException", evt.Properties["ErrorType"]);
        Assert.Equal(1024, evt.Properties["Error"].Length);
    }

    /// <summary>
    /// The point of pinning the sampling percentage: run the events through a real SDK pipeline
    /// that samples at 1 % and check that none of them is dropped, while ordinary events are.
    /// </summary>
    [Fact]
    public void Events_SurviveSdkSampling_WhileOrdinaryEventsDoNot()
    {
        var channel = new CapturingChannel();
        var configuration = NewConfiguration(channel);
        configuration.DefaultTelemetrySink.TelemetryProcessorChainBuilder.UseSampling(1);
        configuration.DefaultTelemetrySink.TelemetryProcessorChainBuilder.Build();
        var client = new TelemetryClient(configuration);
        var events = new DeliveryEvents(client, NullLogger<DeliveryEvents>.Instance);

        for (var i = 0; i < 200; i++)
        {
            events.Queued(AliasMessage());
            client.TrackEvent(new EventTelemetry("Ordinary") { Context = { Operation = { Id = $"op-{i}" } } });
        }

        Assert.Equal(200, channel.Items.OfType<EventTelemetry>().Count(e => e.Name == "NotificationQueued"));
        Assert.True(channel.Items.OfType<EventTelemetry>().Count(e => e.Name == "Ordinary") < 50);
    }

    [Fact]
    public void ATelemetryFailure_NeverReachesTheCaller()
    {
        var events = new DeliveryEvents(
            new TelemetryClient(NewConfiguration(new ThrowingChannel())), NullLogger<DeliveryEvents>.Instance);

        events.Queued(AliasMessage());
        events.Delivered(AliasMessage(), "team-1", "19:c@thread.tacv2", 1);
        events.DeliveryFailed(AliasMessage(), 1, "Exception", "boom");
    }

    private static TelemetryConfiguration NewConfiguration(ITelemetryChannel channel) => new()
    {
        TelemetryChannel = channel,
        ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000"
    };

    private sealed class ThrowingChannel : ITelemetryChannel
    {
        public bool? DeveloperMode { get; set; }
        public string EndpointAddress { get; set; } = "";
        public void Send(ITelemetry item) => throw new InvalidOperationException("channel down");
        public void Flush() { }
        public void Dispose() { }
    }

    private sealed class CapturingChannel : ITelemetryChannel
    {
        public List<ITelemetry> Items { get; } = [];
        public bool? DeveloperMode { get; set; }
        public string EndpointAddress { get; set; } = "";
        public void Send(ITelemetry item) => Items.Add(item);
        public void Flush() { }
        public void Dispose() { }
    }
}
