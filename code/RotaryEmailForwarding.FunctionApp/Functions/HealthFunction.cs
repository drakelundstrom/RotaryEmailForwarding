using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;

namespace RotaryEmailForwarding.FunctionApp.Functions;

public sealed class HealthFunction(IConfiguration configuration)
{
    [Function("Health")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData request)
    {
        var response = request.CreateResponse(HttpStatusCode.OK);

        await response.WriteAsJsonAsync(new
        {
            status = "Healthy",
            application = "RotaryEmailForwarding",
            environment = configuration["appEnvironment"] ?? "local",
            utcNow = DateTimeOffset.UtcNow
        });

        return response;
    }
}
