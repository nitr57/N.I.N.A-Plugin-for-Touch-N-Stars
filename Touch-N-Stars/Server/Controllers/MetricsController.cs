using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

public class MetricsController : WebApiController
{
    private static readonly SystemMetricsService MetricsService = new();

    [Route(HttpVerbs.Get, "/metrics")]
    public Task<SystemMetrics> GetMetrics()
    {
        return MetricsService.GetMetricsAsync();
    }
}
