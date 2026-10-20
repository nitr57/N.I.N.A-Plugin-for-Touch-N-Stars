using System.Net.Http;
using System.Reflection;
using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using NINA.Plugin.Interfaces;
using Newtonsoft.Json.Linq;
using TouchNStars.Server.Controllers;
using Xunit;

namespace TouchNStars.Tests;

public class LuckyCaptureFocusApiTests {
    [Theory]
    [InlineData("GET", "focus/status", "focus/status")]
    [InlineData("GET", "focus/metrics", "focus/metrics")]
    [InlineData("GET", "focus/settings", "focus/settings")]
    [InlineData("PUT", "focus/settings", "focus/settings/set")]
    [InlineData("POST", "focus/arm", "focus/arm")]
    [InlineData("POST", "focus/heartbeat", "focus/heartbeat")]
    [InlineData("POST", "focus/move-relative", "focus/move-relative")]
    [InlineData("POST", "focus/move-absolute", "focus/move-absolute")]
    [InlineData("POST", "focus/stop", "focus/stop")]
    [InlineData("POST", "focus/autofocus/start", "focus/autofocus/start")]
    [InlineData("GET", "focus/autofocus/status", "focus/autofocus/status")]
    [InlineData("POST", "focus/autofocus/cancel", "focus/autofocus/cancel")]
    [InlineData("GET", "simulation/capabilities", "simulation/capabilities")]
    [InlineData("GET", "simulation/sources", "simulation/sources")]
    [InlineData("GET", "simulation/settings", "simulation/settings")]
    [InlineData("PUT", "simulation/settings", "simulation/settings/set")]
    [InlineData("POST", "simulation/select", "simulation/select")]
    [InlineData("GET", "simulation/status", "simulation/status")]
    [InlineData("POST", "simulation/replay/control", "simulation/replay/control")]
    public async Task ForwardsThroughBrokerWithoutWaitingForADeviceJob(string verb, string path, string operation) {
        var broker = new ReplyBroker();
        var property = typeof(TouchNStars).GetProperty(nameof(TouchNStars.Mediators))!;
        var previous = property.GetValue(null);
        var arguments = typeof(Mediators).GetConstructors().Single().GetParameters()
            .Select(p => p.ParameterType == typeof(IMessageBroker) ? (object)broker : null).ToArray();
        property.SetValue(null, Activator.CreateInstance(typeof(Mediators), arguments));
        using var stop = new CancellationTokenSource();
        var port = InternalGuiderApiHost.FreePort();
        using var server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new LuckyCaptureController()));
        var running = server.RunAsync(stop.Token);
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/v1/luckycapture/"), Timeout = TimeSpan.FromSeconds(3) };
        try {
            HttpResponseMessage? response = null;
            for (var i = 0; i < 30; i++) {
                using var request = new HttpRequestMessage(new HttpMethod(verb), path);
                if (verb != "GET") request.Content = new StringContent("{\"steps\":10}", Encoding.UTF8, "application/json");
                try { response = await client.SendAsync(request); break; }
                catch (HttpRequestException) when (i < 29) { await Task.Delay(20); }
            }
            using (response) {
                Assert.Equal(202, (int)response!.StatusCode);
                Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
                var body = JObject.Parse(await response.Content.ReadAsStringAsync());
                Assert.Equal(operation, body.Value<string>("operation"));
                if (verb != "GET") Assert.Equal(10, JObject.Parse(body.Value<string>("payload")!).Value<int>("steps"));
            }
        } finally { stop.Cancel(); property.SetValue(null, previous); }
    }

    private sealed class ReplyBroker : IMessageBroker {
        public Task Publish(IMessage message) => Publish(message, default);
        public Task Publish(IMessage message, CancellationToken token) {
            Assert.Equal("PINS.LuckyCapture.Request.v1", message.Topic);
            var content = (IDictionary<string, object>)message.Content;
            var reply = (TaskCompletionSource<(int, string, byte[])>)content["reply"];
            var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { operation = content["operation"], payload = content["payload"] });
            reply.TrySetResult((202, "application/json", body));
            return Task.CompletedTask;
        }
        public void Subscribe(string topic, ISubscriber subscriber) { }
        public void Unsubscribe(string topic, ISubscriber subscriber) { }
    }
}
