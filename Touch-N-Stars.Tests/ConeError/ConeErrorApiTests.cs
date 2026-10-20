using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using Newtonsoft.Json.Linq;
using TouchNStars.ConeError.Session;
using TouchNStars.Server.Controllers;
using Xunit;

namespace TouchNStars.Tests.ConeError;

internal sealed class FakeObservatory : IObservatory
{
    public ObservatoryState State { get; set; } = new(true, false, false, false);
    public string? SolverProblem { get; set; }

    public ProfileFacts Profile() => AutoRig.Profile with { SolverProblem = SolverProblem! };

    ObservatoryState IObservatory.State() => State;
}

internal sealed class FakeEnvironment : IConeEnvironment
{
    public FakeEnvironment(FakeMount mount, FakeImager imager, FakeClock clock)
    {
        Mount = mount;
        Imager = imager;
        Clock = clock;
    }

    public IConeMount Mount { get; }
    public IConeImager Imager { get; }
    public FakeObservatory FakeObservatory { get; } = new();
    public IObservatory Observatory => FakeObservatory;
    public IClock Clock { get; }
}

/// <summary>The cone error controller on a real EmbedIO server over the forward-model rig.</summary>
internal sealed class ConeErrorApiHost : IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly WebServer server;
    private readonly HttpClient client;

    public ConeErrorApiHost(AutoRig rig)
    {
        Rig = rig;
        Environment = new FakeEnvironment(rig.Mount, rig.Imager, rig.Clock);
        Service = new ConeErrorService(() => Environment, rig.Store);
        int port = FreePort();
        server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new ConeErrorController(Service)));
        _ = server.RunAsync(lifetime.Token);
        BaseAddress = new Uri($"http://127.0.0.1:{port}/api/cone-error/");
        client = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(20) };
    }

    public Uri BaseAddress { get; }

    public AutoRig Rig { get; }
    public FakeEnvironment Environment { get; }
    public ConeErrorService Service { get; }

    public Task<(int Status, JObject Json)> Get(string path) => Send(HttpMethod.Get, path, null);

    public Task<(int Status, JObject Json)> Post(string path, object? body = null) => Send(HttpMethod.Post, path, body == null ? null : JObject.FromObject(body).ToString());

    public async Task<JObject> WaitForPhase(params string[] phases)
    {
        for (int i = 0; i < 400; i++)
        {
            (_, JObject json) = await Get("status");
            if (phases.Contains((string)json["response"]!["phase"]!))
            {
                return (JObject)json["response"]!;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("phase not reached: " + string.Join(", ", phases));
    }

    private async Task<(int Status, JObject Json)> Send(HttpMethod method, string path, string? body)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            try
            {
                using HttpResponseMessage response = await client.SendAsync(request);
                return ((int)response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));
            }
            catch (HttpRequestException) when (attempt < 30)
            {
                await Task.Delay(100);
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        Service.Shutdown();
        lifetime.Cancel();
        client.Dispose();
        server.Dispose();
    }
}

public class ConeErrorApiTests
{
    private const string MountKey = "test|Fake mount";

    private static async Task<string> Plan(ConeErrorApiHost host, int repetitions = 3)
    {
        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0, repetitions });
        Assert.Equal(200, status);
        Assert.True((bool)json["response"]!["plan"]!["ok"]!, json.ToString());
        return (string)json["response"]!["planId"]!;
    }

    private static Task<(int Status, JObject Json)> Start(ConeErrorApiHost host, string planId)
        => host.Post("auto/start", new { planId, noPointingModel = true });

    private static Task<(int Status, JObject Json)> Preconditions(ConeErrorApiHost host, DateTime clientUtc)
        => host.Get("preconditions?clientTimeUtc=" + Uri.EscapeDataString(clientUtc.ToString("o")));

    [Fact]
    public async Task PlanStartPollAndResultEndToEnd()
    {
        using var rig = new AutoRig(tiltArcmin: 6.0);
        using var host = new ConeErrorApiHost(rig);

        (int preStatus, JObject pre) = await Preconditions(host, rig.Clock.UtcNow);
        Assert.Equal(200, preStatus);
        Assert.True((bool)pre["response"]!["ready"]!, pre.ToString());
        Assert.Equal(0.0, (double)pre["response"]!["time"]!["skewSeconds"]!, 1.0);

        string planId = await Plan(host);
        (int startStatus, _) = await Start(host, planId);
        Assert.Equal(200, startStatus);

        // the mount is not verified yet: the one-time counterweight check comes on the first west arrival
        JObject waiting = await host.WaitForPhase("WaitingForAnswer");
        Assert.Equal("pierLabels", (string)waiting["question"]!["kind"]!);
        Assert.Equal("West", (string)waiting["currentSide"]!);
        (int answer, _) = await host.Post("answer", new { id = (int)waiting["question"]!["id"]!, yes = true });
        Assert.Equal(200, answer);

        JObject done = await host.WaitForPhase("Complete", "Failed", "Aborted");
        Assert.Equal("Complete", (string)done["phase"]!);
        Assert.Equal(-6.0, (double)done["result"]!["coneArcmin"]!, 0.05);
        Assert.Equal("Rear", (string)done["result"]!["shimEnd"]!);
        Assert.Equal(rig.World.ExpectedShimMm(200.0), (double)done["result"]!["shimMm"]!, 0.005);

        (_, JObject results) = await host.Get("results");
        Assert.Single((JArray)results["response"]!);
        Assert.Contains(MountKey, rig.Store.LoadSettings().VerifiedPierLabels);
    }

    [Fact]
    public async Task ThePlanUsesTheProfileFlipTimeAndAsksForUnverifiedMounts()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (_, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0, repetitions = 4 });
        JToken response = json["response"]!;

        Assert.Equal(5.0, (double)response["plan"]!["flipAfterMinutes"]!);
        Assert.Equal(4, ((JArray)response["plan"]!["pairs"]!).Count);
        Assert.True((bool)response["askPierLabels"]!);
        Assert.Equal(60.0, (double)response["expiresInSeconds"]!);
    }

    [Fact]
    public async Task AbortOverHttpStopsTheMountMidFlip()
    {
        using var rig = new AutoRig();
        var flip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnMove = async (kind, _, token) =>
        {
            if (kind == "flip")
            {
                flip.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        rig.Store.SaveSettings(new ConeErrorSettings { VerifiedPierLabels = { MountKey } });
        using var host = new ConeErrorApiHost(rig);
        await Start(host, await Plan(host));
        await flip.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (int status, _) = await host.Post("abort");
        JObject done = await host.WaitForPhase("Aborted");

        Assert.Equal(200, status);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.Equal("Aborted", (string)done["error"]!["code"]!);
    }

    [Fact]
    public async Task APlanCanBeStartedOnlyOnce()
    {
        using var rig = new AutoRig();
        rig.Mount.OnMove = (_, _, token) => Task.Delay(Timeout.Infinite, token);
        using var host = new ConeErrorApiHost(rig);
        string planId = await Plan(host);

        (int first, _) = await Start(host, planId);
        (int second, JObject json) = await Start(host, planId);

        Assert.Equal(200, first);
        Assert.Equal(409, second);
        Assert.Equal("PlanUnknown", (string)json["code"]!);
    }

    [Fact]
    public async Task OnlyOneMeasurementRunsAtATime()
    {
        using var rig = new AutoRig();
        rig.Mount.OnMove = (_, _, token) => Task.Delay(Timeout.Infinite, token);
        using var host = new ConeErrorApiHost(rig);
        await Start(host, await Plan(host));

        (int second, JObject json) = await Start(host, await Plan(host));

        Assert.Equal(409, second);
        Assert.Equal("Busy", (string)json["code"]!);
    }

    [Fact]
    public async Task AnExpiredPlanIsRefused()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        string planId = await Plan(host);
        rig.Clock.Advance(TimeSpan.FromSeconds(61));

        (int status, JObject json) = await Start(host, planId);

        Assert.Equal(409, status);
        Assert.Equal("PlanExpired", (string)json["code"]!);
        Assert.Empty(rig.Mount.Moves);
    }

    [Fact]
    public async Task TheStartNeedsTheNoPointingModelConfirmation()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (int refused, JObject json) = await host.Post("auto/start", new { planId = await Plan(host) });
        (int accepted, _) = await Start(host, await Plan(host));

        Assert.Equal(409, refused);
        Assert.Equal("PointingModelNotConfirmed", (string)json["code"]!);
        Assert.Equal(200, accepted);
    }

    [Fact]
    public async Task PlanningNeedsTheDovetailLength()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (int status, JObject json) = await host.Post("auto/plan", new { repetitions = 3 });

        Assert.Equal(400, status);
        Assert.Equal("DovetailRequired", (string)json["code"]!);
    }

    [Fact]
    public async Task ARunningSequenceBlocks()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        host.Environment.FakeObservatory.State = new ObservatoryState(true, false, true, false);

        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0 });

        Assert.Equal(409, status);
        Assert.Equal("PreconditionsNotMet", (string)json["code"]!);
        Assert.Contains("sequence", (string)json["error"]!);
    }

    [Fact]
    public async Task ADisconnectedMountBlocks()
    {
        using var rig = new AutoRig();
        rig.Mount.Connected = false;
        using var host = new ConeErrorApiHost(rig);

        (_, JObject pre) = await Preconditions(host, rig.Clock.UtcNow);

        Assert.False((bool)pre["response"]!["ready"]!);
        Assert.Contains("Connect the mount", pre["response"]!["blockers"]!.ToString());
    }

    [Fact]
    public async Task AWrongPiClockBlocks()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (_, JObject pre) = await Preconditions(host, rig.Clock.UtcNow.AddHours(3));

        Assert.False((bool)pre["response"]!["ready"]!);
        Assert.Contains("minutes off", pre["response"]!["blockers"]!.ToString());
    }

    [Fact]
    public async Task PlanningChecksThePhoneClockOnTheServer()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0, clientTimeUtc = rig.Clock.UtcNow.AddHours(1).ToString("o") });

        Assert.Equal(409, status);
        Assert.Contains("minutes off", (string)json["error"]!);
    }

    [Fact]
    public async Task DaylightBlocks()
    {
        using var rig = new AutoRig();
        rig.Clock.Advance(TimeSpan.FromHours(14));
        using var host = new ConeErrorApiHost(rig);

        (_, JObject pre) = await Preconditions(host, rig.Clock.UtcNow);

        Assert.False((bool)pre["response"]!["ready"]!);
        Assert.True((double)pre["response"]!["time"]!["sunAltitudeDegrees"]! > 0.0);
        Assert.Contains("The Sun is", pre["response"]!["blockers"]!.ToString());
    }

    [Fact]
    public async Task SettingsUpdatesOnlyChangeTheFieldsSent()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        rig.Store.SaveSettings(new ConeErrorSettings { MinAltitudeDegrees = 30.0, DovetailMm = 180.0, VerifiedPierLabels = { "k" } });

        (int status, JObject json) = await Send(host, HttpMethod.Put, "settings", new { repetitions = 7 });

        Assert.Equal(200, status);
        Assert.Equal(30.0, (double)json["response"]!["minAltitudeDegrees"]!);
        Assert.Equal(7, (int)json["response"]!["repetitions"]!);
        Assert.Equal(180.0, rig.Store.LoadSettings().DovetailMm);
        Assert.Contains("k", rig.Store.LoadSettings().VerifiedPierLabels);
    }

    [Fact]
    public async Task SettingsUpdatesCannotAddVerifiedMounts()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        rig.Store.SaveSettings(new ConeErrorSettings { VerifiedPierLabels = { "k" } });

        await Send(host, HttpMethod.Put, "settings", new { verifiedPierLabels = new[] { MountKey } });

        Assert.Equal(new[] { "k" }, rig.Store.LoadSettings().VerifiedPierLabels);
    }

    [Fact]
    public async Task ASettingsUpdateWithAWrongTypeIsABadRequest()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (int status, JObject json) = await Send(host, HttpMethod.Put, "settings", new { repetitions = "many" });

        Assert.Equal(400, status);
        Assert.Equal("BadRequest", (string)json["code"]!);
    }

    [Fact]
    public async Task AbortStopsTheMountAlsoAfterAMeasurementHasFinished()
    {
        using var rig = new AutoRig();
        rig.Store.SaveSettings(new ConeErrorSettings { VerifiedPierLabels = { MountKey } });
        using var host = new ConeErrorApiHost(rig);
        await Start(host, await Plan(host, repetitions: 2));
        await host.WaitForPhase("Complete");
        int before = rig.Mount.StopCount;

        await host.Post("abort");

        Assert.Equal(before + 1, rig.Mount.StopCount);
    }

    [Fact]
    public async Task StatusIsAlwaysPollable()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (int status, JObject json) = await host.Get("status");

        Assert.Equal(200, status);
        Assert.Equal("none", (string)json["response"]!["mode"]!);
        Assert.Equal("Idle", (string)json["response"]!["phase"]!);
    }

    private static async Task<(int Status, JObject Json)> Send(ConeErrorApiHost host, HttpMethod method, string path, object body)
    {
        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        using var request = new HttpRequestMessage(method, path) { Content = new StringContent(JObject.FromObject(body).ToString(), Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await client.SendAsync(request);
        return ((int)response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));
    }
}
