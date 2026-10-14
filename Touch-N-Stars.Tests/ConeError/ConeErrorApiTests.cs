using System.Net;
using System.Net.Sockets;
using System.Text;
using EmbedIO;
using EmbedIO.WebApi;
using Newtonsoft.Json.Linq;
using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using TouchNStars.Server.Controllers;
using Xunit;

namespace TouchNStars.Tests.ConeError;

internal sealed class FakeDriverInspector : IMountDriverInspector
{
    public IPierSideControl PierSide { get; set; } = new FakePierSide();
    public MountLimits Limits { get; set; } = MountLimits.None;
    public PointingModelInfo Model { get; set; } = new(PointingModelState.None, 0, "all OnStep model terms are 0");

    public MountDriverInfo Inspect() => new("fake", PierSide, Limits, Model);
}

internal sealed class FakeObservatory : IObservatory
{
    public ObservatoryState State { get; set; } = new(true, false, false, false);
    public string? SolverProblem { get; set; }

    public ProfileFacts Profile() => new("test", "Test", 50.0, 10.0, null, 10.0, 0.0, 5.0, 400.0, "ASTAP", SolverProblem!);

    ObservatoryState IObservatory.State() => State;
}

internal sealed class FakeEnvironment : IConeEnvironment
{
    public FakeEnvironment(FakeMount mount, FakeImager imager, FakeClock clock, FakeSun sun, FakeDriverInspector driver)
    {
        Mount = mount;
        Imager = imager;
        Clock = clock;
        Sun = sun;
        Driver = driver;
    }

    public IConeMount Mount { get; }
    public IConeImager Imager { get; }
    public IMountDriverInspector Driver { get; }
    public FakeObservatory FakeObservatory { get; } = new();
    public IObservatory Observatory => FakeObservatory;
    public IClock Clock { get; }
    public ISunProvider Sun { get; }
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
        Driver = new FakeDriverInspector { PierSide = rig.PierSide };
        Environment = new FakeEnvironment(rig.Mount, rig.Imager, rig.Clock, rig.Sun, Driver);
        Service = new ConeErrorService(() => Environment, rig.Store, backgroundLoops: false);
        int port = FreePort();
        server = new WebServer(o => o.WithUrlPrefix($"http://127.0.0.1:{port}/").WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m.WithController(() => new ConeErrorController(Service)));
        _ = server.RunAsync(lifetime.Token);
        client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/api/cone-error/"), Timeout = TimeSpan.FromSeconds(20) };
    }

    public AutoRig Rig { get; }
    public FakeDriverInspector Driver { get; }
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
    private static async Task<string> Plan(ConeErrorApiHost host, int repetitions = 3)
    {
        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0, repetitions });
        Assert.Equal(200, status);
        Assert.True((bool)json["response"]!["plan"]!["ok"]!, json.ToString());
        return (string)json["response"]!["planId"]!;
    }

    [Fact]
    public async Task PlanStartPollAndResultEndToEnd()
    {
        using var rig = new AutoRig(tiltArcmin: 6.0);
        using var host = new ConeErrorApiHost(rig);

        (int preStatus, JObject pre) = await host.Get("preconditions?clientTimeUtc=" + Uri.EscapeDataString(rig.Clock.UtcNow.ToString("o")));
        Assert.Equal(200, preStatus);
        Assert.True((bool)pre["response"]!["autoReady"]!, pre.ToString());
        Assert.Equal(0.0, (double)pre["response"]!["time"]!["skewSeconds"]!, 1.0);

        string planId = await Plan(host);
        (int startStatus, _) = await host.Post("auto/start", new { planId });
        Assert.Equal(200, startStatus);

        // the mount is not verified yet: the one-time counterweight check comes after the first east arrival
        JObject waiting = await host.WaitForPhase("WaitingForAnswer");
        Assert.Equal("pierLabels", (string)waiting["question"]!["kind"]!);
        (int answer, _) = await host.Post("answer", new { id = (int)waiting["question"]!["id"]!, yes = true });
        Assert.Equal(200, answer);

        JObject done = await host.WaitForPhase("Complete", "Failed", "Aborted");
        Assert.Equal("Complete", (string)done["phase"]!);
        Assert.Equal(-6.0, (double)done["result"]!["coneArcmin"]!, 0.05);
        Assert.Equal("Rear", (string)done["result"]!["shimEnd"]!);
        Assert.Equal(rig.World.ExpectedShimMm(200.0), (double)done["result"]!["shimMm"]!, 0.005);

        (_, JObject results) = await host.Get("results");
        Assert.Single((JArray)results["response"]!);
        Assert.Contains("test|Fake mount", rig.Store.LoadSettings().VerifiedPierLabels);
    }

    [Fact]
    public async Task AbortOverHttpStopsTheMountImmediately()
    {
        using var rig = new AutoRig();
        var flip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnSlew = async (side, token) =>
        {
            if (side == PierSideLabel.West)
            {
                flip.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        rig.Store.SaveSettings(new ConeErrorSettings { VerifiedPierLabels = { "test|Fake mount" } });
        using var host = new ConeErrorApiHost(rig);
        await host.Post("auto/start", new { planId = await Plan(host) });
        await flip.Task.WaitAsync(TimeSpan.FromSeconds(10));

        (int status, _) = await host.Post("abort");
        JObject done = await host.WaitForPhase("Aborted");

        Assert.Equal(200, status);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.Equal("Aborted", (string)done["error"]!["code"]!);
        Assert.Equal("Best", rig.PierSide.Setting);
    }

    [Fact]
    public async Task APlanCanBeStartedOnlyOnce()
    {
        using var rig = new AutoRig();
        rig.Mount.OnSlew = (_, token) => Task.Delay(Timeout.Infinite, token);
        using var host = new ConeErrorApiHost(rig);
        string planId = await Plan(host);

        (int first, _) = await host.Post("auto/start", new { planId });
        (int second, JObject json) = await host.Post("auto/start", new { planId });

        Assert.Equal(200, first);
        Assert.Equal(409, second);
        Assert.Equal("PlanUnknown", (string)json["code"]!);
    }

    [Fact]
    public async Task AnExpiredPlanIsRefused()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        string planId = await Plan(host);
        rig.Clock.Advance(TimeSpan.FromSeconds(61));

        (int status, JObject json) = await host.Post("auto/start", new { planId });

        Assert.Equal(409, status);
        Assert.Equal("PlanExpired", (string)json["code"]!);
        Assert.Empty(rig.Mount.Slews);
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
    public async Task AMountThatCannotForceThePierSideBlocksWithItsReason()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        host.Driver.PierSide = new FakePierSide { Supported = false };

        (_, JObject pre) = await host.Get("preconditions");
        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0 });

        Assert.False((bool)pre["response"]!["autoReady"]!);
        Assert.Contains("cannot be told", pre["response"]!["autoBlockers"]!.ToString());
        Assert.Equal(409, status);
        Assert.Equal("PreconditionsNotMet", (string)json["code"]!);
    }

    [Fact]
    public async Task AnActivePointingModelMustBeAcknowledged()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        host.Driver.Model = new PointingModelInfo(PointingModelState.Active, 185, "cone term 185″");

        (int refused, JObject json) = await host.Post("auto/start", new { planId = await Plan(host) });
        (int accepted, _) = await host.Post("auto/start", new { planId = await Plan(host), acknowledgePointingModel = true });

        Assert.Equal(409, refused);
        Assert.Equal("PointingModelNotAcknowledged", (string)json["code"]!);
        Assert.Equal(200, accepted);
    }

    [Fact]
    public async Task ARunningSequenceBlocks()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);
        host.Environment.FakeObservatory.State = new ObservatoryState(true, false, true, false);

        (int status, JObject json) = await host.Post("auto/plan", new { dovetailMm = 200.0 });

        Assert.Equal(409, status);
        Assert.Contains("sequence", (string)json["error"]!);
    }

    [Fact]
    public async Task AnUnsetPiClockBlocks()
    {
        using var rig = new AutoRig();
        using var host = new ConeErrorApiHost(rig);

        (_, JObject pre) = await host.Get("preconditions?clientTimeUtc=" + Uri.EscapeDataString(rig.Clock.UtcNow.AddHours(3).ToString("o")));

        Assert.False((bool)pre["response"]!["manualReady"]!);
        Assert.Contains("minutes off", pre["response"]!["manualBlockers"]!.ToString());
    }

    [Fact]
    public async Task DaytimeCheckOverHttpMovesAndRestoresTracking()
    {
        using var rig = new AutoRig();
        rig.Mount.Place(0.02, -38.0);
        using var host = new ConeErrorApiHost(rig);
        host.Environment.FakeObservatory.State = new ObservatoryState(false, false, false, false);
        rig.Store.SaveSettings(new ConeErrorSettings { ManualMinAltitudeDegrees = 0.0 });

        (int begin, JObject begun) = await host.Post("manual/begin");
        Assert.Equal(200, begin);
        Assert.False(rig.Mount.Tracking);
        await host.Post("manual/capture");
        (int planStatus, JObject plan) = await host.Post("manual/plan-move", new { side = "West" });
        Assert.Equal(200, planStatus);
        Assert.True((bool)plan["response"]!["ok"]!, plan.ToString());

        (int move, _) = await host.Post("manual/move", new { planId = (string)plan["response"]!["planId"]! });
        Assert.Equal(200, move);
        JObject ready = await host.WaitForPhase("Ready");
        for (int i = 0; i < 100 && rig.Mount.Side != PierSideLabel.West; i++)
        {
            await Task.Delay(20);
        }

        (int end, _) = await host.Post("manual/end");

        Assert.Equal("manual", (string)begun["response"]!["mode"]!);
        Assert.Equal(PierSideLabel.West, rig.Mount.Side);
        Assert.Equal(200, end);
        Assert.True(rig.Mount.Tracking);
    }

    [Fact]
    public async Task RestoreAppliesALeftoverJournalWithoutMoving()
    {
        using var rig = new AutoRig();
        rig.Store.SaveJournal(new RestoreJournal { Mode = "auto", PreferredPierSide = "West" });
        using var host = new ConeErrorApiHost(rig);

        (_, JObject pre) = await host.Get("preconditions");
        Assert.True((bool)pre["response"]!["restorePending"]!);

        (int status, _) = await host.Post("restore");

        Assert.Equal(200, status);
        Assert.Equal("West", rig.PierSide.Setting);
        Assert.Null(rig.Store.LoadJournal());
        Assert.Empty(rig.Mount.Slews);
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
}
