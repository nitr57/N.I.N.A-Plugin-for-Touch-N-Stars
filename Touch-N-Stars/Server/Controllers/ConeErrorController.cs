using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// REST API of the cone error wizard under /api/cone-error (see docs/cone-error/design.md in pins).
/// Responses are { success, response } or { success: false, error, code } with camelCase names,
/// enums as strings and NaN as null. The app polls GET /status every 2 s; POST /abort stops the
/// mount at once.
/// </summary>
public class ConeErrorController : WebApiController
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static readonly JsonSerializerSettings Json = CreateJson();

    public ConeErrorController() : this(ConeErrorService.Instance)
    {
    }

    /// <summary>For tests: a controller bound to its own service.</summary>
    public ConeErrorController(ConeErrorService service)
    {
        Service = service;
    }

    private ConeErrorService Service { get; }

    private static JsonSerializerSettings CreateJson()
    {
        var settings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Include,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        };
        settings.Converters.Add(new InternalGuiderJson.NonFiniteDoubleConverter());
        settings.Converters.Add(new StringEnumConverter());
        return settings;
    }

    /// <summary>GET /api/cone-error/preconditions?clientTimeUtc=ISO-8601 - what blocks or warns, per mode.</summary>
    [Route(HttpVerbs.Get, "/cone-error/preconditions")]
    public Task GetPreconditions() => Run(() =>
    {
        DateTime? client = null;
        string raw = Request.QueryString["clientTimeUtc"];
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime parsed))
        {
            client = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
        }

        return Task.FromResult<object>(Service.GetPreconditions(client));
    });

    [Route(HttpVerbs.Get, "/cone-error/settings")]
    public Task GetSettings() => Run(() => Task.FromResult<object>(Service.GetSettings()));

    [Route(HttpVerbs.Put, "/cone-error/settings")]
    public Task PutSettings() => Run(async () => Service.SaveSettings(await Body<ConeErrorSettings>() ?? new ConeErrorSettings()));

    /// <summary>GET /api/cone-error/status - the running or last session; always 200 so it can be polled.</summary>
    [Route(HttpVerbs.Get, "/cone-error/status")]
    public Task GetStatus() => Run(() => Task.FromResult<object>(Service.Status()));

    /// <summary>POST /api/cone-error/abort - stops the mount and the exposure now and ends the session.</summary>
    [Route(HttpVerbs.Post, "/cone-error/abort")]
    public Task Abort() => Run(() => Task.FromResult<object>(Service.Abort()));

    [Route(HttpVerbs.Get, "/cone-error/results")]
    public Task GetResults() => Run(() => Task.FromResult<object>(Service.Results()));

    /// <summary>POST /api/cone-error/restore - applies a leftover restore journal (settings only, never a move).</summary>
    [Route(HttpVerbs.Post, "/cone-error/restore")]
    public Task Restore() => Run(async () => await Service.RestoreAsync());

    /// <summary>POST /api/cone-error/auto/plan {dovetailMm, repetitions, declination?, exposureSeconds?} - the slews to confirm.</summary>
    [Route(HttpVerbs.Post, "/cone-error/auto/plan")]
    public Task PlanAuto() => Run(async () => Service.PlanAuto(await Body<AutoPlanRequest>() ?? new AutoPlanRequest()));

    /// <summary>POST /api/cone-error/auto/start {planId, acknowledgePointingModel} - the mount moves.</summary>
    [Route(HttpVerbs.Post, "/cone-error/auto/start")]
    public Task StartAuto() => Run(async () =>
    {
        JObject body = await BodyObject();
        return Service.StartAuto((string)body["planId"], (bool?)body["acknowledgePointingModel"] ?? false);
    });

    /// <summary>POST /api/cone-error/answer {id, yes} - answers the session's question (the pier side check).</summary>
    [Route(HttpVerbs.Post, "/cone-error/answer")]
    public Task Answer() => Run(async () =>
    {
        JObject body = await BodyObject();
        if (!Service.Answer((int?)body["id"] ?? -1, (bool?)body["yes"] ?? false))
        {
            throw new ConeErrorException("NoQuestion", "There is no such question.");
        }

        return Service.Status();
    });

    [Route(HttpVerbs.Post, "/cone-error/manual/begin")]
    public Task BeginManual() => Run(() => Task.FromResult<object>(Service.BeginManual()));

    [Route(HttpVerbs.Post, "/cone-error/manual/capture")]
    public Task Capture() => Run(() => Task.FromResult<object>(Service.Capture()));

    /// <summary>
    /// POST /api/cone-error/manual/plan-move {side: "East"|"West", overrideSunPath} - the checks of the move to
    /// confirm; overrideSunPath overrides a refusal for the Sun near the path alone, for this one move.
    /// </summary>
    [Route(HttpVerbs.Post, "/cone-error/manual/plan-move")]
    public Task PlanMove() => Run(async () =>
    {
        JObject body = await BodyObject();
        if (!Enum.TryParse((string)body["side"], ignoreCase: true, out PierSideLabel side))
        {
            throw new ConeErrorException("BadRequest", "side must be East or West.");
        }

        return Service.PlanManualMove(side, (bool?)body["overrideSunPath"] ?? false);
    });

    /// <summary>POST /api/cone-error/manual/move {planId} - starts a confirmed move; progress through /status.</summary>
    [Route(HttpVerbs.Post, "/cone-error/manual/move")]
    public Task Move() => Run(async () =>
    {
        JObject body = await BodyObject();
        return Service.StartManualMove((string)body["planId"]);
    });

    /// <summary>POST /api/cone-error/manual/stop - stops a running move; the daytime check stays active.</summary>
    [Route(HttpVerbs.Post, "/cone-error/manual/stop")]
    public Task StopMove() => Run(() => Task.FromResult<object>(Service.StopManualMove()));

    /// <summary>POST /api/cone-error/manual/recenter-done {axisOffsetMm, landmarkDistanceMeters} - the daytime estimate.</summary>
    [Route(HttpVerbs.Post, "/cone-error/manual/recenter-done")]
    public Task RecenterDone() => Run(async () =>
    {
        JObject body = await BodyObject();
        return Service.RecenterDone((double?)body["axisOffsetMm"], (double?)body["landmarkDistanceMeters"]);
    });

    /// <summary>POST /api/cone-error/manual/end - ends the daytime check and restores tracking.</summary>
    [Route(HttpVerbs.Post, "/cone-error/manual/end")]
    public Task EndManual() => Run(async () => await Service.EndManualAsync());

    private async Task<T> Body<T>() where T : class
    {
        string text = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<T>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new ConeErrorException("BadRequest", $"The request body is not valid: {ex.Message}");
        }
    }

    private async Task<JObject> BodyObject()
    {
        string text = await HttpContext.GetRequestBodyAsStringAsync();
        if (string.IsNullOrWhiteSpace(text))
        {
            return new JObject();
        }

        try
        {
            return JObject.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ConeErrorException("BadRequest", $"The request body is not valid JSON: {ex.Message}");
        }
    }

    private async Task Run(Func<Task<object>> action)
    {
        try
        {
            object response = await action();
            await SendJson(200, new { success = true, response });
        }
        catch (ConeErrorException ex)
        {
            int status = ex.Code is "BadRequest" or "DovetailRequired" ? 400 : 409;
            await SendJson(status, new { success = false, error = ex.Message, code = ex.Code });
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: {Request.Url.AbsolutePath} failed: {ex}");
            await SendJson(500, new { success = false, error = ex.Message, code = "Error" });
        }
    }

    private Task SendJson(int statusCode, object data)
    {
        HttpContext.Response.StatusCode = statusCode;
        HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
        return HttpContext.SendStringAsync(JsonConvert.SerializeObject(data, Json), "application/json", Utf8NoBom);
    }
}
