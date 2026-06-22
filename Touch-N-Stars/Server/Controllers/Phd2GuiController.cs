using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for the native PHD2 GUI embed (xpra HTML5 session). See
/// <see cref="Phd2GuiService"/> for the lifecycle details. Linux only.
/// </summary>
public class Phd2GuiController : WebApiController
{
    private static readonly Phd2GuiService service = new Phd2GuiService();

    /// <summary>
    /// GET /api/phd2-gui/status - Report xpra availability and whether a session is up.
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2-gui/status")]
    public ApiResponse Status()
    {
        try
        {
            bool available = service.IsAvailable();
            int port = service.LastPort;
            bool running = available && service.IsRunning(port);

            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    Available = available,
                    Running = running,
                    Port = port,
                    Display = service.LastDisplay
                },
                StatusCode = 200,
                Type = "PHD2GuiStatus"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2-gui/start - Launch (or reuse) the PHD2 xpra session.
    /// Body (all optional): { port, display, phd2Command, extraArgs }
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2-gui/start")]
    public async Task<ApiResponse> Start()
    {
        try
        {
            int port = 0;
            int display = 0;
            string phd2Command = null;
            string extraArgs = null;

            try
            {
                var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (body != null)
                {
                    if (body.TryGetValue("port", out var p) && int.TryParse(p?.ToString(), out var pi)) port = pi;
                    if (body.TryGetValue("display", out var d) && int.TryParse(d?.ToString(), out var di)) display = di;
                    if (body.TryGetValue("phd2Command", out var c)) phd2Command = c?.ToString();
                    if (body.TryGetValue("extraArgs", out var e)) extraArgs = e?.ToString();
                }
            }
            catch
            {
                // Use defaults if the body is missing or unparseable.
            }

            var result = service.Start(port, display, phd2Command, extraArgs);

            return new ApiResponse
            {
                Success = result.Success,
                Response = new
                {
                    Running = result.Success,
                    Port = result.Port,
                    Display = result.Display,
                    AlreadyRunning = result.AlreadyRunning
                },
                Error = result.Error,
                StatusCode = result.Success ? 200 : 400,
                Type = "PHD2GuiStart"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    /// <summary>
    /// POST /api/phd2-gui/stop - Stop the PHD2 xpra session.
    /// Body (optional): { display }
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2-gui/stop")]
    public async Task<ApiResponse> Stop()
    {
        try
        {
            int display = 0;
            try
            {
                var body = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (body != null && body.TryGetValue("display", out var d) && int.TryParse(d?.ToString(), out var di))
                {
                    display = di;
                }
            }
            catch
            {
                // Default to the last started display.
            }

            bool ok = service.Stop(display);
            return new ApiResponse
            {
                Success = ok,
                Response = new { Stopped = ok },
                StatusCode = ok ? 200 : 400,
                Type = "PHD2GuiStop"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }
}
