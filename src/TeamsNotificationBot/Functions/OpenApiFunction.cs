using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;

namespace TeamsNotificationBot.Functions;

public class OpenApiFunction
{
    [Function("OpenApi")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v1/openapi.yaml")] HttpRequest req)
    {
        var filePath = Path.Combine(AppContext.BaseDirectory, "openapi.yaml");

        if (!File.Exists(filePath))
        {
            return new NotFoundResult();
        }

        var content = File.ReadAllText(filePath);
        return new ContentResult
        {
            Content = content,
            ContentType = "application/yaml",
            StatusCode = StatusCodes.Status200OK
        };
    }
}
