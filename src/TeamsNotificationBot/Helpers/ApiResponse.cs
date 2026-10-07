using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace TeamsNotificationBot.Helpers;

public static class ApiResponse
{
    public static IActionResult Problem(
        int status,
        string title,
        string detail,
        string instance,
        string? correlationId = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = instance,
            Type = $"https://httpstatuses.io/{status}"
        };

        if (correlationId != null)
            problem.Extensions["correlationId"] = correlationId;

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" }
        };
    }

    /// <summary>
    /// 404 for an alias that exists but whose conversation the bot no longer has, so a message to
    /// it could never be delivered. The detail tells it apart from an unknown alias.
    /// </summary>
    public static IActionResult ConversationGone(string alias, string instance, string? correlationId) =>
        Problem(404, "Not Found",
            $"Alias '{alias}' exists, but the bot no longer has the conversation it points to " +
            "(the bot may have been removed from that team or chat). Run set-alias in the " +
            "conversation the alias should post to.",
            instance, correlationId);

    public static async Task WriteProblemAsync(
        HttpResponse response,
        int status,
        string title,
        string detail,
        string instance,
        string? correlationId = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = instance,
            Type = $"https://httpstatuses.io/{status}"
        };

        if (correlationId != null)
            problem.Extensions["correlationId"] = correlationId;

        response.StatusCode = status;
        response.ContentType = "application/problem+json";
        await response.WriteAsJsonAsync(problem);
    }
}
