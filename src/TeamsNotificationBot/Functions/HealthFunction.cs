using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using TeamsNotificationBot.Helpers;

namespace TeamsNotificationBot.Functions;

public class HealthFunction
{
    [Function("Health")]
    public IActionResult Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequest req)
    {
        return new OkObjectResult(new
        {
            status = "ok",
            version = AppInfo.Version,
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        });
    }
}
