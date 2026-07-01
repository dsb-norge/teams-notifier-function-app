using System.Text.RegularExpressions;
using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;

namespace TeamsNotificationBot.Helpers;

/// <summary>
/// Redacts the secret <c>{token}</c> segment of the updown ingress URL from request telemetry.
///
/// App Insights records request URLs by default, which would otherwise leak the capability-URL
/// secret into telemetry. This rewrites <c>/api/v1/ingest/updown/&lt;token&gt;</c> →
/// <c>/api/v1/ingest/updown/***</c> on both the telemetry Url and Name.
/// </summary>
public class TokenRedactingTelemetryInitializer : ITelemetryInitializer
{
    private static readonly Regex IngestToken = new(
        @"(/api/v1/ingest/updown/)[^/?#\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public void Initialize(ITelemetry telemetry)
    {
        if (telemetry is not RequestTelemetry request)
            return;

        if (request.Url is { } url)
        {
            var redacted = IngestToken.Replace(url.ToString(), "$1***");
            if (Uri.TryCreate(redacted, UriKind.Absolute, out var newUri))
                request.Url = newUri;
        }

        if (!string.IsNullOrEmpty(request.Name))
            request.Name = IngestToken.Replace(request.Name, "$1***");
    }
}
