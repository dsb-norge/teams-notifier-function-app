namespace TeamsNotificationBot.Tests;

/// <summary>
/// Canonical updown.io webhook payloads (verbatim from https://updown.io/api#webhooks,
/// timestamps normalized), shared by parsing and card-builder tests. Each is the array
/// wrapper updown always sends. See docs/feat-updown-io-webhook/manual-verification.md.
/// </summary>
public static class UpdownPayloads
{
    public const string CheckDown = """
    [{
      "event": "check.down",
      "time": "2026-07-01T10:48:48Z",
      "description": "DOWN: https://updown.io/ since 10:38:48 (UTC), reason: 418 I'm a teapot",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https", "alias": null,
        "uptime": 100.0, "down": true, "down_since": "2026-07-01T10:43:48Z", "up_since": null,
        "error": "418 I'm a teapot", "last_status": 418 },
      "downtime": { "id": "6a44f090706306086d4e09bc",
        "details_url": "https://updown.io/downtimes/6a44f090706306086d4e09bc",
        "error": "418 I'm a teapot", "started_at": "2026-07-01T10:38:48Z",
        "ended_at": null, "duration": null, "partial": null }
    }]
    """;

    public const string CheckUp = """
    [{
      "event": "check.up",
      "time": "2026-07-01T10:48:48Z",
      "description": "UP: https://updown.io/ since 10:48:33 (UTC), after being down for 10 minutes",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https", "alias": "prod-site",
        "down": false, "up_since": "2026-06-01T10:48:48Z", "last_status": 200 },
      "downtime": { "id": "6a44f090706306086d4e09be",
        "details_url": "https://updown.io/downtimes/6a44f090706306086d4e09be",
        "error": "418 I'm a teapot", "started_at": "2026-07-01T10:38:48Z",
        "ended_at": "2026-07-01T10:48:33Z", "duration": 585, "partial": null }
    }]
    """;

    public const string SslInvalid = """
    [{
      "event": "check.ssl_invalid",
      "time": "2026-07-01T10:48:48Z",
      "description": "The SSL certificate served by updown.io is not valid (error code 20: unable to get local issuer certificate)",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "ssl": { "cert": { "subject": "updown.io", "issuer": "Let's Encrypt Authority X3 (Let's Encrypt)",
        "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
        "error": "error code 20: unable to get local issuer certificate" }
    }]
    """;

    public const string SslValid = """
    [{
      "event": "check.ssl_valid",
      "time": "2026-07-01T10:48:48Z",
      "description": "The SSL certificate served by updown.io is now valid",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "ssl": { "cert": { "subject": "updown.io", "issuer": "Let's Encrypt Authority X3 (Let's Encrypt)",
        "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" } }
    }]
    """;

    public const string SslExpiration = """
    [{
      "event": "check.ssl_expiration",
      "time": "2026-07-01T10:48:48Z",
      "description": "The SSL certificate served by updown.io will expire in 7 days",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "ssl": { "cert": { "subject": "updown.io", "issuer": "Let's Encrypt Authority X3 (Let's Encrypt)",
        "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
        "days_before_expiration": 7 }
    }]
    """;

    public const string SslRenewed = """
    [{
      "event": "check.ssl_renewed",
      "time": "2026-07-01T10:48:48Z",
      "description": "The SSL certificate served by updown.io was renewed",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "ssl": {
        "new_cert": { "subject": "updown.io", "issuer": "Let's Encrypt Authority X3 (Let's Encrypt)",
          "from": "2018-09-08T21:00:18Z", "to": "2019-03-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" },
        "old_cert": { "subject": "updown.io", "issuer": "Let's Encrypt Authority X3 (Let's Encrypt)",
          "from": "2018-09-08T21:00:18Z", "to": "2018-12-07T21:00:18Z", "algorithm": "SHA-256 with RSA encryption" } }
    }]
    """;

    public const string PerformanceDrop = """
    [{
      "event": "check.performance_drop",
      "time": "2026-07-01T10:48:48Z",
      "description": "Apdex of https://updown.io/ dropped 47%",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "apdex_dropped": "47%",
      "last_metrics": { "2023-03-12T07:00:00Z": { "apdex": 0.51 } }
    }]
    """;

    /// <summary>A future/unknown event type the code must accept and skip.</summary>
    public const string UnknownEvent = """
    [{ "event": "check.some_future_thing", "time": "2026-07-01T10:48:48Z",
       "check": { "token": "xyz0", "url": "https://updown.io" } }]
    """;

    /// <summary>All optional structures absent — must parse without throwing.</summary>
    public const string NullsEverywhere = """
    [{ "event": "check.down", "time": null, "description": null, "check": null }]
    """;

    public const string EmptyArray = "[]";

    public const string NotAnArray = """{ "event": "check.down" }""";

    public const string Malformed = "{not json";

    /// <summary>A downtime link on a non-updown.io host — must NOT be linkified.</summary>
    public const string CheckUpEvilLink = """
    [{
      "event": "check.up",
      "time": "2026-07-01T10:48:48Z",
      "description": "UP again",
      "check": { "token": "xyz0", "url": "https://updown.io", "type": "https" },
      "downtime": { "details_url": "https://evil.example.com/phish", "duration": 60,
        "started_at": "2026-07-01T10:38:48Z", "ended_at": "2026-07-01T10:39:48Z" }
    }]
    """;
}
