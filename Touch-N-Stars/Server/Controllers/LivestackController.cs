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
/// The Livestack plugin's options and its calibration master library for Touch-N-Stars. The options are stored in the
/// profile exactly as the plugin's options page stores them (LivestackSettingsService); the masters go through the
/// plugin's own library object (LivestackCalibrationService). Field names are the plugin's names. Errors answer
/// { Success: false, Error, Response: { Errors: [{ Field, Code, Message }] } }.
/// </summary>
public class LivestackController : WebApiController
{
    /// <summary>
    /// GET /api/livestack/settings - The effective settings, the working directory's status (exists, writable, free
    /// space, RAM disk), whether a stack runs, the settings locked while it does, and the allowed ranges.
    /// </summary>
    [Route(HttpVerbs.Get, "/livestack/settings")]
    public async Task<ApiResponse> GetSettings()
    {
        try
        {
            // The folder probe touches the file system, which can block on a dead network mount
            return Ok(await Task.Run(() => LivestackSettingsService.Describe(TouchNStars.Mediators.Profile)), "LivestackSettings");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return Error(ex.Message, 500, null);
        }
    }

    /// <summary>
    /// PUT /api/livestack/settings - Body: any subset of the settings, e.g. { "SaveStackedLights": true,
    /// "DefaultDownsample": 2 }. All values are checked before any is stored: 400 when one is invalid, 409 (code
    /// livestack-running) when a locked setting would change while a stack runs. Success answers like GET.
    /// </summary>
    [Route(HttpVerbs.Put, "/livestack/settings")]
    public async Task<ApiResponse> UpdateSettings()
    {
        var body = await ReadBody();

        try
        {
            var (result, settings) = await Task.Run(() =>
            {
                var update = LivestackSettingsService.Update(TouchNStars.Mediators.Profile, body);
                return (update, update.Errors.Count > 0 ? null : LivestackSettingsService.Describe(TouchNStars.Mediators.Profile));
            });
            if (result.Errors.Count > 0)
            {
                return Error(result.Errors[0].Message, result.StatusCode, new { result.Errors });
            }

            return Ok(settings, "LivestackSettings");
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return Error(ex.Message, 500, null);
        }
    }

    /// <summary>
    /// GET /api/livestack/calibration - The Bias, Dark and Flat libraries ({ Type, Path, FileName, FileExists, Width,
    /// Height, Gain, Offset, ExposureTime, Filter }; -1 gain/offset = any) and whether a stack runs.
    /// </summary>
    [Route(HttpVerbs.Get, "/livestack/calibration")]
    public Task<ApiResponse> GetCalibration()
    {
        return RunCalibration(LivestackCalibrationService.Describe);
    }

    /// <summary>
    /// GET /api/livestack/calibration/inspect?path=... - The master's FITS header as a proposal for adding it: Type
    /// (from IMAGETYP, null when unknown), size, gain, offset, exposure time, filter. Reads no pixel data.
    /// </summary>
    [Route(HttpVerbs.Get, "/livestack/calibration/inspect")]
    public Task<ApiResponse> InspectMaster()
    {
        string path = HttpContext.Request.QueryString["path"];
        return RunCalibration(() => LivestackCalibrationService.Inspect(path));
    }

    /// <summary>
    /// POST /api/livestack/calibration - Body { "Path", "Type": "BIAS|DARK|FLAT", "Gain", "Offset", "ExposureTime",
    /// "Filter" } adds a master; size and mean are read from the file, which takes a few seconds for a large master.
    /// 409 when it is already in that library or a stack runs. Success answers like GET /livestack/calibration.
    /// </summary>
    [Route(HttpVerbs.Post, "/livestack/calibration")]
    public async Task<ApiResponse> AddMaster()
    {
        var rawBody = await ReadBody();
        if (rawBody == null)
        {
            return CalibrationError(new LivestackCalibrationService.CalibrationException(400, "invalid-body", null, "Request body is required"));
        }

        LivestackCalibrationService.AddRequest request;
        try
        {
            Dictionary<string, object> body;
            try
            {
                body = new Dictionary<string, object>(rawBody, StringComparer.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                throw new LivestackCalibrationService.CalibrationException(400, "invalid-body", null, "The request body names a field more than once");
            }

            request = new LivestackCalibrationService.AddRequest
            {
                Path = ReadString(body, "Path"),
                Type = ReadString(body, "Type"),
                Gain = ReadInt(body, "Gain", -1),
                Offset = ReadInt(body, "Offset", -1),
                ExposureTime = ReadNumber(body, "ExposureTime", 0),
                Filter = ReadString(body, "Filter"),
            };
        }
        catch (LivestackCalibrationService.CalibrationException ex)
        {
            return CalibrationError(ex);
        }

        return await RunCalibration(() => LivestackCalibrationService.Add(request, TouchNStars.Mediators.Profile));
    }

    /// <summary>
    /// DELETE /api/livestack/calibration?type=BIAS|DARK|FLAT&amp;path=...[&amp;gain=&amp;offset=&amp;exposureTime=&amp;filter=]
    /// - Takes a master out of its library; the file stays on disk. The optional values pick the right entry when the
    /// same file is in the library twice. 404 when it is not in that library, 409 while a stack runs.
    /// </summary>
    [Route(HttpVerbs.Delete, "/livestack/calibration")]
    public async Task<ApiResponse> RemoveMaster()
    {
        var query = HttpContext.Request.QueryString;
        LivestackCalibrationService.MasterMatch match;
        try
        {
            match = new LivestackCalibrationService.MasterMatch
            {
                Path = query["path"],
                Gain = ReadOptionalInt(query["gain"], "gain"),
                Offset = ReadOptionalInt(query["offset"], "offset"),
                ExposureTime = ReadOptionalNumber(query["exposureTime"], "exposureTime"),
                Filter = query["filter"],
            };
        }
        catch (LivestackCalibrationService.CalibrationException ex)
        {
            return CalibrationError(ex);
        }

        string type = query["type"];
        return await RunCalibration(() => LivestackCalibrationService.Remove(type, match));
    }

    private async Task<ApiResponse> RunCalibration(Func<object> action)
    {
        try
        {
            // FITS reads and the flat mean take a while on a Pi
            return Ok(await Task.Run(action), "LivestackCalibration");
        }
        catch (LivestackCalibrationService.CalibrationException ex)
        {
            return CalibrationError(ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return Error(ex.Message, 500, null);
        }
    }

    private ApiResponse CalibrationError(LivestackCalibrationService.CalibrationException ex)
    {
        var errors = new[] { new LivestackSettingsService.SettingError(ex.Field, ex.Code, ex.Message) };
        return Error(ex.Message, ex.StatusCode, new { Errors = errors });
    }

    private async Task<Dictionary<string, object>> ReadBody()
    {
        try
        {
            return await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
        }
        catch (Exception ex)
        {
            Logger.Debug($"Unreadable Livestack request body: {ex.Message}");
            return null;
        }
    }

    private static string ReadString(Dictionary<string, object> body, string key)
    {
        if (!body.TryGetValue(key, out object raw) || raw == null)
        {
            return null;
        }

        return raw as string
            ?? throw new LivestackCalibrationService.CalibrationException(400, "invalid-type", key, $"{key} must be text");
    }

    private static double ReadNumber(Dictionary<string, object> body, string key, double fallback)
    {
        if (!body.TryGetValue(key, out object raw) || raw == null)
        {
            return fallback;
        }

        return LivestackSettingsService.TryReadNumber(raw, out double value)
            ? value
            : throw new LivestackCalibrationService.CalibrationException(400, "invalid-type", key, $"{key} must be a number");
    }

    private static int ReadInt(Dictionary<string, object> body, string key, int fallback)
    {
        return ToInt(ReadNumber(body, key, fallback), key);
    }

    private static int ToInt(double value, string key)
    {
        if (value != Math.Floor(value))
        {
            throw new LivestackCalibrationService.CalibrationException(400, "not-an-integer", key, $"{key} must be a whole number");
        }

        if (value < int.MinValue || value > int.MaxValue)
        {
            throw new LivestackCalibrationService.CalibrationException(400, "out-of-range", key, $"{key} is out of range");
        }

        return (int)value;
    }

    private static double? ReadOptionalNumber(string raw, string key)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return LivestackSettingsService.TryReadNumber(raw, out double value)
            ? value
            : throw new LivestackCalibrationService.CalibrationException(400, "invalid-type", key, $"{key} must be a number");
    }

    private static int? ReadOptionalInt(string raw, string key)
    {
        double? value = ReadOptionalNumber(raw, key);
        return value == null ? null : ToInt(value.Value, key);
    }

    private ApiResponse Ok(object response, string type)
    {
        HttpContext.Response.StatusCode = 200;
        return new ApiResponse { Success = true, Response = response, StatusCode = 200, Type = type };
    }

    private ApiResponse Error(string error, int statusCode, object response)
    {
        HttpContext.Response.StatusCode = statusCode;
        return new ApiResponse { Success = false, Error = error, Response = response, StatusCode = statusCode, Type = "Error" };
    }
}
