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
