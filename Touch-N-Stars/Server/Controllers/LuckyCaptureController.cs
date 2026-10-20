using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Plugin.Interfaces;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace TouchNStars.Server.Controllers;

/// <summary>Optional integration through PINS's public message broker, without reflection.</summary>
public class LuckyCaptureController : WebApiController {
    [Route(HttpVerbs.Get, "/v1/luckycapture/health")]
    public Task Health() => Forward("health");
    [Route(HttpVerbs.Get, "/v1/luckycapture/camera")]
    public Task Camera() => Forward("camera");
    [Route(HttpVerbs.Get, "/v1/luckycapture/capabilities")]
    public Task Capabilities() => Forward("capabilities");
    [Route(HttpVerbs.Get, "/v1/luckycapture/controls")]
    public Task Controls() => Forward("controls");
    [Route(HttpVerbs.Post, "/v1/luckycapture/diagnostics/frame")]
    public Task Frame() => Post("diagnostics/frame");
    [Route(HttpVerbs.Get, "/v1/luckycapture/status")]
    public Task Status() => Forward("status");
    [Route(HttpVerbs.Get, "/v1/luckycapture/statistics")]
    public Task Statistics() => Forward("statistics");
    [Route(HttpVerbs.Get, "/v1/luckycapture/settings")]
    public Task Settings() => Forward("settings");
    [Route(HttpVerbs.Put, "/v1/luckycapture/settings")]
    public Task Configure() => Post("settings/set");
    [Route(HttpVerbs.Get, "/v1/luckycapture/preview")]
    public Task Preview() => Forward("preview");
    [Route(HttpVerbs.Get, "/v1/luckycapture/histogram")]
    public Task Histogram() => Forward("histogram");
    [Route(HttpVerbs.Post, "/v1/luckycapture/preview/start")]
    public Task StartPreview() => Post("preview/start");
    [Route(HttpVerbs.Post, "/v1/luckycapture/preview/stop")]
    public Task StopPreview() => Post("preview/stop");
    [Route(HttpVerbs.Post, "/v1/luckycapture/recording/start")]
    public Task StartRecording() => Post("recording/start");
    [Route(HttpVerbs.Post, "/v1/luckycapture/recording/stop")]
    public Task StopRecording() => Post("recording/stop");
    [Route(HttpVerbs.Get, "/v1/luckycapture/recording")]
    public Task Recording() => Forward("recording");
    [Route(HttpVerbs.Get, "/v1/luckycapture/roi")]
    public Task Roi() => Forward("roi");
    [Route(HttpVerbs.Put, "/v1/luckycapture/roi")]
    public Task SetRoi() => Post("roi/set");
    [Route(HttpVerbs.Delete, "/v1/luckycapture/roi")]
    public Task ResetRoi() => Forward("roi/reset");
    [Route(HttpVerbs.Put, "/v1/luckycapture/controls/{id}")]
    public Task SetControl(string id) => Post("controls/set/" + id);
    [Route(HttpVerbs.Post, "/v1/luckycapture/snapshot")]
    public Task Snapshot() => Post("snapshot");
    [Route(HttpVerbs.Get, "/v1/luckycapture/focus/status")]
    public Task FocusStatus() => Forward("focus/status");

    [Route(HttpVerbs.Get, "/v1/luckycapture/focus/metrics")]
    public Task FocusMetrics() => Forward("focus/metrics");

    [Route(HttpVerbs.Get, "/v1/luckycapture/focus/settings")]
    public Task FocusSettings() => Forward("focus/settings");

    [Route(HttpVerbs.Put, "/v1/luckycapture/focus/settings")]
    public Task SetFocusSettings() => Post("focus/settings/set");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/arm")]
    public Task ArmFocuser() => Post("focus/arm");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/heartbeat")]
    public Task FocusHeartbeat() => Post("focus/heartbeat");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/move-relative")]
    public Task FocusRelative() => Post("focus/move-relative");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/move-absolute")]
    public Task FocusAbsolute() => Post("focus/move-absolute");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/stop")]
    public Task StopFocus() => Post("focus/stop");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/autofocus/start")]
    public Task StartPlanetaryFocus() => Post("focus/autofocus/start");

    [Route(HttpVerbs.Get, "/v1/luckycapture/focus/autofocus/status")]
    public Task PlanetaryFocusStatus() => Forward("focus/autofocus/status");

    [Route(HttpVerbs.Post, "/v1/luckycapture/focus/autofocus/cancel")]
    public Task CancelPlanetaryFocus() => Post("focus/autofocus/cancel");

    [Route(HttpVerbs.Get, "/v1/luckycapture/simulation/capabilities")]
    public Task SimulationCapabilities() => Forward("simulation/capabilities");

    [Route(HttpVerbs.Get, "/v1/luckycapture/simulation/sources")]
    public Task SimulationSources() => Forward("simulation/sources");

    [Route(HttpVerbs.Get, "/v1/luckycapture/simulation/settings")]
    public Task SimulationSettings() => Forward("simulation/settings");

    [Route(HttpVerbs.Put, "/v1/luckycapture/simulation/settings")]
    public Task SetSimulationSettings() => Post("simulation/settings/set");

    [Route(HttpVerbs.Post, "/v1/luckycapture/simulation/select")]
    public Task SelectSimulation() => Post("simulation/select");

    [Route(HttpVerbs.Get, "/v1/luckycapture/simulation/status")]
    public Task SimulationStatus() => Forward("simulation/status");

    [Route(HttpVerbs.Post, "/v1/luckycapture/simulation/replay/control")]
    public Task ReplayControl() => Post("simulation/replay/control");

    [Route(HttpVerbs.Get, "/v1/luckycapture/events")]
    public async Task Events() {
        HttpContext.Response.ContentType = "text/event-stream";
        HttpContext.Response.Headers["Cache-Control"] = "no-store";
        long revision = -1;
        while (!HttpContext.CancellationToken.IsCancellationRequested) {
            try {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.CancellationToken);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                var result = await Request("events/wait", JsonConvert.SerializeObject(new { revision }), heartbeat.Token);
                var json = Encoding.UTF8.GetString(result.Body);
                if (result.Status != 200 && result.Status != 408) break;
                if (result.Status == 200) revision = Newtonsoft.Json.Linq.JObject.Parse(json).Value<long>("revision");
                var bytes = Encoding.UTF8.GetBytes(result.Status == 408 ? ": heartbeat\n\n" : "data: " + json + "\n\n");
                await HttpContext.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, HttpContext.CancellationToken);
                await HttpContext.Response.OutputStream.FlushAsync(HttpContext.CancellationToken);
            } catch (OperationCanceledException) { break; }
              catch (IOException) { break; }
        }
    }

    private async Task Post(string operation) {
        try { await Forward(operation, await Body()); }
        catch (InvalidDataException) { await SendError(400, "INVALID_INPUT", "Request body exceeds 64 KiB"); }
    }

    private async Task<string> Body() {
        using var reader = new StreamReader(HttpContext.Request.InputStream, Encoding.UTF8, true, 4096, true);
        var body = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), HttpContext.CancellationToken)) > 0) {
            if (body.Length + count > 65536) throw new InvalidDataException();
            body.Append(buffer, 0, count);
        }
        return body.Length == 0 ? "{}" : body.ToString();
    }

    private async Task Forward(string operation, string payload = "{}") {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(operation == "diagnostics/frame" ? 120 : 15));
        try {
            var result = await Request(operation, payload, timeout.Token);
            HttpContext.Response.StatusCode = result.Status;
            HttpContext.Response.ContentType = result.ContentType;
            HttpContext.Response.Headers["Cache-Control"] = "no-store";
            HttpContext.Response.ContentLength64 = result.Body.Length;
            await HttpContext.Response.OutputStream.WriteAsync(result.Body, 0, result.Body.Length, HttpContext.CancellationToken);
        } catch (OperationCanceledException) {
            if (!HttpContext.CancellationToken.IsCancellationRequested) await SendError(408, "CANCELLED", "LuckyCapture request timed out");
        }
    }

    private static async Task<(int Status, string ContentType, byte[] Body)> Request(string operation, string payload, CancellationToken token) {
        var reply = new TaskCompletionSource<(int Status, string ContentType, byte[] Body)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await TouchNStars.Mediators.MessageBroker.Publish(new RequestMessage(operation, payload, token, reply), token);
        if (reply.Task.IsCompleted) return await reply.Task;
        return (503, "application/json", Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new {
            code = "PLUGIN_UNAVAILABLE", message = "PINS LuckyCapture is not installed or loaded"
        })));
    }

    private Task SendError(int status, string code, string message) {
        HttpContext.Response.StatusCode = status;
        return HttpContext.SendStringAsync(JsonConvert.SerializeObject(new { code, message }), "application/json", Encoding.UTF8);
    }

    private sealed class RequestMessage : IMessage {
        public RequestMessage(string operation, string payload, CancellationToken cancellation,
            TaskCompletionSource<(int, string, byte[])> reply) {
            Content = new Dictionary<string, object> { ["operation"] = operation, ["payload"] = payload,
                ["cancellation"] = cancellation, ["reply"] = reply };
        }
        public Guid SenderId => Guid.Parse("2ccac7ce-8162-4d94-b84b-7b2a645bf42d");
        public string Sender => "Touch-N-Stars";
        public DateTimeOffset SentAt { get; } = DateTimeOffset.UtcNow;
        public Guid MessageId { get; } = Guid.NewGuid();
        public DateTimeOffset? Expiration => SentAt.AddMinutes(2);
        public Guid? CorrelationId => MessageId;
        public int Version => 1;
        public IDictionary<string, object> CustomHeaders { get; } = new Dictionary<string, object>();
        public string Topic => "PINS.LuckyCapture.Request.v1";
        public object Content { get; }
    }
}
