using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using TouchNStars.Server.Services;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Endpoints for the HocusFocus Star Detection Optimization Wizard, driven headlessly through
/// <see cref="StarDetectionOptimizerSession"/>. One session at a time: the client starts it, polls its state,
/// sets inputs, runs commands, answers the wizard's confirmations, and ends it.
/// </summary>
public class HocusFocusOptimizerController : WebApiController
{
    [Route(HttpVerbs.Get, "/hocusfocus/optimizer")]
    public object GetState()
    {
        try
        {
            var session = StarDetectionOptimizerSession.Current;
            if (session == null)
            {
                return new Dictionary<string, object>
                {
                    { "Success", true },
                    { "Active", false },
                    // "accepted" / "closed" / "timedOut" after a session ends, so a client polling past it learns the outcome.
                    { "LastOutcome", StarDetectionOptimizerSession.LastOutcome },
                };
            }
            return session.GetState();
        }
        catch (Exception ex)
        {
            return Fail(500, "Failed to read optimizer state", ex);
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/start")]
    public object StartSession()
    {
        try
        {
            return StarDetectionOptimizerSession.Start().GetState();
        }
        catch (Exception ex)
        {
            return Fail(503, "Failed to start the optimizer", ex);
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/end")]
    public object EndSession()
    {
        try
        {
            StarDetectionOptimizerSession.End();
            return new Dictionary<string, object> { { "Success", true } };
        }
        catch (Exception ex)
        {
            return Fail(500, "Failed to end the optimizer", ex);
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/property/{name}")]
    public async Task<object> SetProperty(string name)
    {
        try
        {
            var session = RequireSession();
            var value = await ReadValueAsync();
            session.SetProperty(name, value);
            return session.GetState();
        }
        catch (Exception ex)
        {
            return Fail(400, $"Failed to set {name}", ex);
        }
    }

    /// <summary>Body: { "value": "&lt;run folder&gt;/&lt;attempt folder&gt;" } as listed by /hocusfocus/list-af, or null to clear.</summary>
    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/source/{index}")]
    public async Task<object> SetSource(int index)
    {
        try
        {
            var session = RequireSession();
            var value = await ReadValueAsync();
            session.SetSourcePath(index, value.ValueKind == JsonValueKind.String ? value.GetString() : null);
            return session.GetState();
        }
        catch (Exception ex)
        {
            return Fail(400, $"Failed to set source {index}", ex);
        }
    }

    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/command/{name}")]
    public object RunCommand(string name)
    {
        try
        {
            var session = RequireSession();
            var refusal = session.Execute(name);
            if (refusal != null)
            {
                return Fail(409, refusal, null);
            }
            return session.GetState();
        }
        catch (Exception ex)
        {
            return Fail(400, $"Failed to run {name}", ex);
        }
    }

    /// <summary>Body: { "id": &lt;confirmation id&gt;, "value": true|false }.</summary>
    [Route(HttpVerbs.Post, "/hocusfocus/optimizer/confirm")]
    public async Task<object> AnswerConfirmation()
    {
        try
        {
            var session = RequireSession();
            using var doc = JsonDocument.Parse(await HttpContext.GetRequestBodyAsStringAsync());
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetInt32();
            var agreed = root.GetProperty("value").GetBoolean();
            if (!session.Answer(id, agreed))
            {
                return Fail(409, "That confirmation is no longer waiting for an answer", null);
            }
            return session.GetState();
        }
        catch (Exception ex)
        {
            return Fail(400, "Failed to answer the confirmation", ex);
        }
    }

    private static StarDetectionOptimizerSession RequireSession() =>
        StarDetectionOptimizerSession.Current ?? throw new InvalidOperationException("No optimizer session is running");

    private async Task<JsonElement> ReadValueAsync()
    {
        var json = await HttpContext.GetRequestBodyAsStringAsync();
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        return doc.RootElement.TryGetProperty("value", out var value) ? value.Clone() : default;
    }

    private Dictionary<string, object> Fail(int status, string message, Exception ex)
    {
        var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
        if (inner != null)
        {
            Logger.Error($"[Optimizer] {message}: {inner}");
        }
        HttpContext.Response.StatusCode = status;
        return new Dictionary<string, object>
        {
            { "Success", false },
            { "Error", inner == null ? message : $"{message}: {inner.Message}" },
        };
    }
}
