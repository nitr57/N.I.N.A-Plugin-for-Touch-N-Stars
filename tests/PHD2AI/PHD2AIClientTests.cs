using System.Net;
using System.Net.Sockets;
using Newtonsoft.Json.Linq;
using TouchNStars.PHD2;
using Xunit;

public class PHD2AIClientTests
{
    [Fact]
    public async Task GraphHistoryRetainsNativeAIEventsAndRejectsOtherPHD2Instances()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint instance = (uint)(((IPEndPoint)listener.LocalEndpoint).Port - 4399);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            var request = JObject.Parse(await reader.ReadLineAsync());
            await writer.WriteLineAsync("{\"Event\":\"GuideStep\",\"Frame\":900,\"AvgDist\":0.2,\"RADistanceRaw\":0.2,\"DECDistanceRaw\":0.1,\"AIMode\":\"active\",\"AIRAContribution\":0.03,\"RADuration\":40}");
            await writer.WriteLineAsync("{\"Event\":\"GuidingDithered\"}");
            await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = 0 }.ToString(Newtonsoft.Json.Formatting.None));
            await reader.ReadLineAsync(); // Keep the socket open until the client disconnects.
        });
        using var client = new PHD2Client("127.0.0.1", instance);
        client.Connect();
        client.GetAIStatus();
        var snapshot = client.GetAIGuideSteps("localhost", instance);
        Assert.True(snapshot["Supported"].Value<bool>());
        var steps = (JArray)snapshot["Steps"];
        Assert.Equal(2, steps.Count);
        Assert.Equal(900, steps[0]["Frame"].Value<int>());
        Assert.Equal(0.03, steps[0]["AIRAContribution"].Value<double>());
        Assert.Equal("active", steps[0]["AIMode"].Value<string>());
        Assert.True(steps[1]["Dither"].Value<bool>());
        steps[0]["AIRAContribution"] = 999;
        Assert.Equal(0.03, client.GetAIGuideSteps("localhost", instance)["Steps"][0]["AIRAContribution"].Value<double>());
        Assert.False(client.GetAIGuideSteps("localhost", instance + 1)["Supported"].Value<bool>());
        Assert.False(client.GetAIGuideSteps("other-host", instance)["Supported"].Value<bool>());
        client.ClearAIGuideSteps();
        Assert.Empty((JArray)client.GetAIGuideSteps("localhost", instance)["Steps"]);
        client.Disconnect();
        await server;
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 5005)]
    public async Task GraphCacheIsBoundedAndOlderPHD2EventsDoNotClaimAISupport(bool ai, int count)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint instance = (uint)(((IPEndPoint)listener.LocalEndpoint).Port - 4399);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            var request = JObject.Parse(await reader.ReadLineAsync());
            for (int i = 0; i < count; i++)
            {
                var evt = new JObject { ["Event"] = "GuideStep", ["Frame"] = i, ["AvgDist"] = 0.1 };
                if (ai) evt["AIMode"] = "shadow";
                await writer.WriteLineAsync(evt.ToString(Newtonsoft.Json.Formatting.None));
            }
            await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = 0 }.ToString(Newtonsoft.Json.Formatting.None));
            await reader.ReadLineAsync();
        });
        using var client = new PHD2Client("127.0.0.1", instance);
        client.Connect();
        client.GetAIStatus();
        var data = client.GetAIGuideSteps("localhost", instance, 5000);
        Assert.Equal(ai, data["Supported"].Value<bool>());
        Assert.Equal(ai ? 5000 : 0, ((JArray)data["Steps"]).Count);
        if (ai) Assert.Equal(5, data["Steps"][0]["Frame"].Value<int>());
        client.Disconnect();
        Assert.False(client.GetAIGuideSteps("localhost", instance)["Supported"].Value<bool>());
        await server;
    }

    [Fact]
    public void HttpBoundaryKeepsObjectsArraysAndScalarValues()
    {
        var data = JObject.Parse("{\"mode\":\"disabled\",\"model_loaded\":false,\"confidence\":0.63,\"warnings\":[\"mount\"],\"training\":{\"frames\":42,\"path\":null}}");
        var plain = (Dictionary<string, object>)TouchNStars.Server.Models.PHD2AIResponseData.ToPlain(data);
        Assert.Equal("disabled", plain["mode"]);
        Assert.Equal(false, plain["model_loaded"]);
        Assert.Equal(0.63, plain["confidence"]);
        Assert.Equal("mount", ((object[])plain["warnings"])[0]);
        var training = (Dictionary<string, object>)plain["training"];
        Assert.Equal(42L, training["frames"]);
        Assert.Null(training["path"]);
        Assert.Equal(0L, TouchNStars.Server.Models.PHD2AIResponseData.ToPlain(new JValue(0L)));
    }

    [Theory]
    [InlineData("ai_set_prediction_gain", "{\"gain\":2}")]
    [InlineData("ai_set_prediction_gain", "{\"gain\":\"0.1\"}")]
    [InlineData("ai_set_prediction_gain", "{}")]
    [InlineData("ai_set_mode", "{\"mode\":\"enabled\"}")]
    [InlineData("ai_set_mode", "{}")]
    [InlineData("ai_select_model", "{\"path\":\"\"}")]
    [InlineData("ai_set_storage_directory", "{\"path\":\"\"}")]
    [InlineData("ai_set_storage_directory", "{}")]
    [InlineData("ai_train_model", "{}")]
    [InlineData("ai_start_training", "{\"duration_sec\":60,\"period_sec\":120}")]
    [InlineData("ai_start_training", "{\"period_sec\":10}")]
    [InlineData("ai_start_training", "{\"duration_sec\":\"720\"}")]
    [InlineData("ai_get_status", "{\"arbitrary\":true}")]
    [InlineData("slew", "{}")]
    public void InvalidInputFailsBeforeAnyNetworkCall(string method, string json)
    {
        using var client = new PHD2Client();
        Assert.Throws<ArgumentException>(() => client.CallAI(method, JObject.Parse(json)));
    }

    [Theory]
    [InlineData("ai_start_training", "{\"duration_sec\":720,\"period_sec\":120}")]
    [InlineData("ai_train_model", "{\"recording_path\":\"/home/pi/baseline.csv\",\"period_sec\":0}")]
    [InlineData("ai_set_mode", "{\"mode\":\"shadow\"}")]
    [InlineData("ai_set_prediction_gain", "{\"gain\":0.1}")]
    [InlineData("ai_select_model", "{\"path\":\"/home/pi/model.json\"}")]
    [InlineData("ai_set_storage_directory", "{\"path\":\"/home/pi/Documents/PHD2\"}")]
    [InlineData("ai_cancel_training", "{}")]
    public async Task RequestsPreserveParametersAndMatchResponsesAmongEvents(string method, string json)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint instance = (uint)(((IPEndPoint)listener.LocalEndpoint).Port - 4399);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            var request = JObject.Parse(await reader.ReadLineAsync());
            Assert.Equal(method, request["method"].Value<string>());
            var parameters = JObject.Parse(json);
            Assert.True(JToken.DeepEquals(parameters.HasValues ? parameters : null, request["params"]));
            await writer.WriteLineAsync("{\"Event\":\"AIGuideStatus\",\"Mode\":\"disabled\"}");
            await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"],
                ["result"] = new JObject { ["state"] = "complete" } }.ToString(Newtonsoft.Json.Formatting.None));
        });
        using var client = new PHD2Client("127.0.0.1", instance);
        client.Connect();
        var result = client.CallAI(method, JObject.Parse(json));
        Assert.Equal("complete", result["state"].Value<string>());
        await server;
    }

    [Fact]
    public async Task ServerRejectionIsNotReportedAsSuccess()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint instance = (uint)(((IPEndPoint)listener.LocalEndpoint).Port - 4399);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            var request = JObject.Parse(await reader.ReadLineAsync());
            await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"],
                ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not found" } }
                .ToString(Newtonsoft.Json.Formatting.None));
        });
        using var client = new PHD2Client("127.0.0.1", instance);
        client.Connect();
        var error = Assert.Throws<PHD2Exception>(() => client.GetAIStatus());
        Assert.Contains("Method not found", error.Message);
        await server;
    }

    [Fact]
    public async Task ExposureEventsDoNotLeaveCaptureReportedAsStopped()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        uint instance = (uint)(((IPEndPoint)listener.LocalEndpoint).Port - 4399);
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true };
            foreach (var evt in new[] { "LoopingExposures", "LoopingExposuresStopped" })
            {
                var request = JObject.Parse(await reader.ReadLineAsync());
                await writer.WriteLineAsync(new JObject { ["Event"] = evt }.ToString(Newtonsoft.Json.Formatting.None));
                await writer.WriteLineAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = request["id"], ["result"] = 0 }
                    .ToString(Newtonsoft.Json.Formatting.None));
            }
        });
        using var client = new PHD2Client("127.0.0.1", instance);
        client.Connect();
        client.GetAIStatus();
        Assert.Equal("Looping", client.AppState);
        client.GetAIStatus();
        Assert.Equal("Stopped", client.AppState);
        await server;
    }
}
