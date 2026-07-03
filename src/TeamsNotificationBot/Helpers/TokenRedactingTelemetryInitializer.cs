using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Redacts the secret <c>{token}</c> segment of the updown ingress URL from telemetry.
///
/// The ingest URL is a capability URL — the token is a bearer secret — so it must never reach
/// App Insights. This rewrites <c>/api/v1/ingest/updown/&lt;token&gt;</c> →
/// <c>/api/v1/ingest/updown/***</c> across the telemetry types the worker emits:
///  - <see cref="RequestTelemetry"/> — <c>Url</c> and <c>Name</c>.
///  - <see cref="TraceTelemetry"/> — <c>Message</c>. The ASP.NET Core hosting pipeline logs
///    "Request starting/finished HTTP/1.1 POST &lt;full-url&gt;" at Information level, embedding the
///    token in the message string; the earlier request-only path missed these.
///  - any <see cref="ISupportProperties"/> custom properties (e.g. the hosting scope's
///    <c>RequestPath</c>) that carry the URL.
///
/// NB: the HTTP <c>requests</c> telemetry for an isolated-worker HTTP trigger is emitted by the
/// Functions host process, not the worker, so a worker-registered initializer cannot reach it.
/// That residual is tracked in docs/feat-updown-io-webhook/refinements.md (F9).
/// </summary>
public class TokenRedactingTelemetryInitializer : ITelemetryInitializer
{
    private const string IngestPathFragment = "/api/v1/ingest/updown/";

    private static readonly Regex IngestToken = new(
        @"(/api/v1/ingest/updown/)[^/?#\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Initialize(ITelemetry telemetry)
    {
        switch (telemetry)
        {
            case RequestTelemetry request:
                if (request.Url is { } url)
                {
                    var redacted = IngestToken.Replace(url.ToString(), "$1***");
                    if (Uri.TryCreate(redacted, UriKind.Absolute, out var newUri))
                        request.Url = newUri;
                }

                if (!string.IsNullOrEmpty(request.Name))
                    request.Name = IngestToken.Replace(request.Name, "$1***");
                break;

            case TraceTelemetry trace:
                if (!string.IsNullOrEmpty(trace.Message))
                    trace.Message = IngestToken.Replace(trace.Message, "$1***");
                break;
        }

        // Custom properties (e.g. ASP.NET Core hosting's RequestPath) can carry the raw URL too.
        if (telemetry is ISupportProperties withProps)
        {
            foreach (var key in withProps.Properties.Keys.ToList())
            {
                var value = withProps.Properties[key];
                if (!string.IsNullOrEmpty(value)
                    && value.Contains(IngestPathFragment, StringComparison.OrdinalIgnoreCase))
                {
                    withProps.Properties[key] = IngestToken.Replace(value, "$1***");
                }
            }
        }
    }
}
