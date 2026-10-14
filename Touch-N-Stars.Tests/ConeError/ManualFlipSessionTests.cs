using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using Xunit;

namespace TouchNStars.Tests.ConeError;

/// <summary>The daytime check rig: 50° N, 10° E, the tube on a landmark 2° above the southern horizon.</summary>
public sealed class ManualRig : IDisposable
{
    public static readonly DateTime Evening = new(2026, 10, 14, 19, 0, 0, DateTimeKind.Utc);

    public ManualRig(DateTime? start = null, bool tracking = true, bool pierSideSupported = true, double tiltArcmin = 4.0)
    {
        Directory = Path.Combine(Path.GetTempPath(), "cone-error-tests-" + Guid.NewGuid().ToString("N"));
        Store = new ConeErrorStore(Directory);
        Clock = new FakeClock(start ?? Evening);
        Sun = new FakeSun(10.0);
        World = new ConeForwardModel { LatitudeDegrees = 50.0, TiltAwayFromSaddleArcmin = tiltArcmin, AxisOffsetMm = 250.0 };
        PierSide = new FakePierSide("West") { Supported = pierSideSupported };
        Mount = new FakeMount(Clock, 50.0, 10.0, PierSideLabel.East, 0.02, -38.0, tracking) { PierSide = PierSide };
        Session = new ManualFlipSession(
            Mount,
            PierSide,
            Clock,
            Sun,
            Store,
            new ManualFlipOptions
            {
                Limits = new LimitSet { MinAltitudeDegrees = 0.0, PinsPastMeridianDegrees = 2.5, SunExclusionDegrees = 30.0 },
                LatitudeDegrees = 50.0,
                DovetailMm = 200.0,
                MountKey = "test|Fake mount",
                Profile = new ProfileFacts("test", "Test", 50.0, 10.0, null, 10.0, 0.0, 5.0, 400.0, "ASTAP", null)
            },
            backgroundLoop: false);
    }

    public string Directory { get; }
    public ConeErrorStore Store { get; }
    public FakeClock Clock { get; }
    public FakeSun Sun { get; }
    public ConeForwardModel World { get; }
    public FakePierSide PierSide { get; }
    public FakeMount Mount { get; }
    public ManualFlipSession Session { get; }

    public async Task MoveTo(PierSideLabel side)
    {
        ManualMovePlan plan = Session.PlanMove(side);
        Assert.True(plan.Ok, plan.Refusal ?? LimitValidator.Describe(plan.Check.Violations));
        await Session.MoveAsync(plan.PlanId).WaitAsync(TimeSpan.FromSeconds(10));
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class ManualFlipSessionTests
{
    [Fact]
    public async Task TrackingIsOffDuringTheModeAndRestoredOnExit()
    {
        using var rig = new ManualRig(tracking: true);

        rig.Session.Begin();
        Assert.False(rig.Mount.Tracking);
        Assert.True(rig.Store.LoadJournal().Tracking);

        await rig.Session.EndAsync();

        Assert.True(rig.Mount.Tracking);
        Assert.Equal("West", rig.PierSide.Setting);
        Assert.Null(rig.Store.LoadJournal());
        Assert.Equal(SessionPhase.Complete, rig.Session.GetStatus().Phase);
    }

    [Fact]
    public async Task TrackingOffBeforeTheModeStaysOff()
    {
        using var rig = new ManualRig(tracking: false);

        rig.Session.Begin();
        await rig.Session.EndAsync();

        Assert.False(rig.Mount.Tracking);
        Assert.Equal(new[] { false }, rig.Mount.TrackingCalls);
    }

    [Fact]
    public async Task AbortStopsTheMountAndRestoresTracking()
    {
        using var rig = new ManualRig(tracking: true);
        rig.Session.Begin();
        rig.Session.Capture();

        await rig.Session.AbortAsync();

        Assert.True(rig.Mount.Tracking);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.Equal(SessionPhase.Aborted, rig.Session.GetStatus().Phase);
        Assert.Throws<ConeErrorException>(() => rig.Session.Capture());
    }

    [Fact]
    public async Task AbortDuringAMoveRestoresTracking()
    {
        using var rig = new ManualRig(tracking: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnSlew = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        rig.Session.Begin();
        rig.Session.Capture();
        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);

        Task move = rig.Session.MoveAsync(plan.PlanId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Session.AbortAsync();
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(rig.Mount.Tracking);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.Equal("West", rig.PierSide.Setting);
    }

    [Fact]
    public async Task AMoveThatFailsStillRestoresTrackingOnExit()
    {
        using var rig = new ManualRig(tracking: true);
        rig.Mount.SlewException = new InvalidOperationException("goto refused: below horizon");
        rig.Session.Begin();
        rig.Session.Capture();
        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);

        await Assert.ThrowsAsync<ConeErrorException>(() => rig.Session.MoveAsync(plan.PlanId));
        Assert.Equal("MoveFailed", rig.Session.GetStatus().Error.Code);
        await rig.Session.EndAsync();

        Assert.True(rig.Mount.Tracking);
    }

    [Fact]
    public async Task MoveLandsOnTheOtherSideAtTheCapturedHourAngleWithTrackingOff()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        ManualFlipStatus captured = rig.Session.Capture();
        Assert.True(captured.EastCheck.Ok);
        Assert.True(captured.WestCheck.Ok);

        await rig.MoveTo(PierSideLabel.West);

        Assert.Equal(PierSideLabel.West, rig.Mount.Side);
        Assert.False(rig.Mount.Tracking);
        Assert.InRange(Spherical.SeparationDegrees(rig.Mount.HourAngle, rig.Mount.Declination, 0.02, -38.0) * 60.0, 0.0, ManualFlipSession.ArrivalToleranceArcmin);
        // the flip tracks for 90 s on the way, so a short correction follows
        Assert.True(rig.Mount.Slews.Count >= 2);
        Assert.Equal(SessionPhase.Ready, rig.Session.GetStatus().Phase);
    }

    [Fact]
    public async Task MovesBackAndForthAsOftenAsWanted()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();

        await rig.MoveTo(PierSideLabel.West);
        await rig.MoveTo(PierSideLabel.East);
        await rig.MoveTo(PierSideLabel.West);

        Assert.Equal(PierSideLabel.West, rig.Mount.Side);
    }

    [Fact]
    public void TheCurrentSideCannotBePlanned()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();

        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.East);

        Assert.False(plan.Ok);
        Assert.Contains("already", plan.Refusal);
    }

    [Fact]
    public void PlanningNeedsACapturedPosition()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();

        Assert.Equal("NotCaptured", Assert.Throws<ConeErrorException>(() => rig.Session.PlanMove(PierSideLabel.West)).Code);
    }

    [Fact]
    public async Task AMountThatCannotForceThePierSideIsExplainedAndNeverMoved()
    {
        using var rig = new ManualRig(pierSideSupported: false);
        rig.Session.Begin();
        rig.Session.Capture();

        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);

        Assert.False(plan.Ok);
        Assert.Contains("cannot be told", plan.Refusal);
        Assert.Equal("PlanRefused", (await Assert.ThrowsAsync<ConeErrorException>(() => rig.Session.MoveAsync(plan.PlanId))).Code);
        Assert.Empty(rig.Mount.Slews);
    }

    [Fact]
    public void ALandmarkReachableOnOnlyOneSideIsRefused()
    {
        using var rig = new ManualRig();
        rig.Mount.Place(0.5, -38.0);
        rig.Session.Begin();

        ManualFlipStatus captured = rig.Session.Capture();
        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);

        Assert.True(captured.EastCheck.Ok);
        Assert.False(captured.WestCheck.Ok);
        Assert.False(plan.Ok);
        Assert.Contains(plan.Check.Violations, v => v.Code == LimitViolationCode.PastMeridianLimit);
        Assert.Empty(rig.Mount.Slews);
    }

    [Fact]
    public async Task UnknownAndExpiredPlansAreRefused()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();

        Assert.Equal("PlanUnknown", (await Assert.ThrowsAsync<ConeErrorException>(() => rig.Session.MoveAsync("nope"))).Code);

        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);
        rig.Clock.Advance(TimeSpan.FromSeconds(61));
        Assert.Equal("PlanExpired", (await Assert.ThrowsAsync<ConeErrorException>(() => rig.Session.MoveAsync(plan.PlanId))).Code);
        Assert.Empty(rig.Mount.Slews);
    }

    [Fact]
    public void SunNearTheFlipPathRefusesAndTellsWhenItWillBeAllowed()
    {
        using var rig = new ManualRig(start: new DateTime(2026, 6, 21, 15, 0, 0, DateTimeKind.Utc));
        rig.Session.Begin();
        rig.Session.Capture();

        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);

        Assert.False(plan.Ok);
        Assert.Contains(plan.Check.Violations, v => v.Code == LimitViolationCode.SunTooCloseOnPath);
        Assert.NotNull(plan.NextAllowedUtc);
        Assert.InRange(plan.NextAllowedUtc.Value, new DateTime(2026, 6, 21, 18, 0, 0, DateTimeKind.Utc), new DateTime(2026, 6, 21, 20, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task APlanThatBecameUnsafeIsRefusedAtExecution()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();
        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);
        Assert.True(plan.Ok);

        rig.Sun.Fixed = new HaDec(0.3, -30.0);

        Assert.Equal("PlanRefused", (await Assert.ThrowsAsync<ConeErrorException>(() => rig.Session.MoveAsync(plan.PlanId))).Code);
        Assert.Empty(rig.Mount.Slews);
    }

    [Fact]
    public async Task DeadmanStopsAMoveWhenTheAppStopsPolling()
    {
        using var rig = new ManualRig();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnSlew = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        rig.Session.Begin();
        rig.Session.Capture();
        ManualMovePlan plan = rig.Session.PlanMove(PierSideLabel.West);
        Task move = rig.Session.MoveAsync(plan.PlanId);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        rig.Clock.Advance(TimeSpan.FromSeconds(10));
        rig.Session.CheckBackground();
        Assert.Equal(0, rig.Mount.StopCount);

        rig.Clock.Advance(TimeSpan.FromSeconds(15));
        rig.Session.CheckBackground();
        await move.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(rig.Mount.StopCount >= 1);
        Assert.Equal("ConnectionLost", rig.Session.GetStatus().Error.Code);
        Assert.False(rig.Mount.Tracking);
    }

    [Fact]
    public void HeartbeatsKeepTheDeadmanQuiet()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();

        for (int i = 0; i < 10; i++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            rig.Session.Heartbeat();
            rig.Session.CheckBackground();
        }

        Assert.Equal(0, rig.Mount.StopCount);
        Assert.Null(rig.Session.GetStatus().Error);
    }

    [Fact]
    public void SunApproachingAStaticPointingAlertsButNeverMovesTheMount()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();

        // 33° away: a warning; then 20° away: critical
        rig.Sun.Fixed = new HaDec(0.02 + 33.0 / 15.0 / Math.Cos(38.0 * Math.PI / 180.0), -38.0);
        rig.Session.CheckBackground();
        SunWatchStatus approaching = rig.Session.GetStatus().Sun;

        rig.Sun.Fixed = new HaDec(0.02, -18.0);
        rig.Session.CheckBackground();
        SunWatchStatus near = rig.Session.GetStatus().Sun;

        Assert.True(approaching.Warning);
        Assert.False(approaching.Critical);
        Assert.True(near.Critical);
        Assert.Empty(rig.Mount.Slews);
        Assert.Equal(0, rig.Mount.StopCount);
    }

    [Fact]
    public async Task IdleTimeoutEndsTheModeAndRestoresTracking()
    {
        using var rig = new ManualRig(tracking: true);
        rig.Session.Begin();

        rig.Clock.Advance(TimeSpan.FromMinutes(31));
        rig.Session.CheckBackground();
        await Task.Delay(50);

        Assert.True(rig.Session.Finished);
        Assert.True(rig.Mount.Tracking);
    }

    [Theory]
    [InlineData(4.0)]
    [InlineData(-6.0)]
    public async Task ReCentredLandmarkGivesTheDaytimeEstimateWithTheParallaxCorrection(double tilt)
    {
        using var rig = new ManualRig(tiltArcmin: tilt);
        Vector3 landmark = ConeForwardModel.Direction(0.02, -38.0);
        const double distance = 10000.0;
        (double haEast, double decEast) = rig.World.CentreOnLandmark(PierSideLabel.East, landmark, distance);
        (double haWest, double decWest) = rig.World.CentreOnLandmark(PierSideLabel.West, landmark, distance);
        rig.Mount.Place(haEast, decEast);
        rig.Session.Begin();
        rig.Session.Capture();

        await rig.MoveTo(PierSideLabel.West);
        rig.Mount.Place(haWest, decWest);
        ManualEstimate estimate = rig.Session.RecenterDone(250.0, distance);

        Assert.Null(estimate.Refusal);
        Assert.Equal(-tilt, estimate.ConeArcmin!.Value, 0.02);
        Assert.Equal(rig.World.ExpectedShimEnd().ToString(), estimate.ShimEnd);
        Assert.Equal("manual", estimate.Result.Mode);
        Assert.Single(rig.Store.LoadResults());
    }

    [Fact]
    public async Task ALandmarkTooCloseIsRefusedForTheEstimate()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();
        await rig.MoveTo(PierSideLabel.West);

        ManualEstimate estimate = rig.Session.RecenterDone(250.0, 1000.0);

        Assert.NotNull(estimate.Refusal);
        Assert.Contains("too close", estimate.Refusal);
        Assert.Null(estimate.ConeArcmin);
        Assert.Empty(rig.Store.LoadResults());
    }

    [Fact]
    public async Task TheEstimateNeedsTheParallaxInputs()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();
        await rig.MoveTo(PierSideLabel.West);

        ManualEstimate estimate = rig.Session.RecenterDone(null, null);

        Assert.NotNull(estimate.Refusal);
        Assert.Null(estimate.ConeArcmin);
    }

    [Fact]
    public void TheEstimateNeedsBothSides()
    {
        using var rig = new ManualRig();
        rig.Session.Begin();
        rig.Session.Capture();

        Assert.Equal("NeedBothSides", Assert.Throws<ConeErrorException>(() => rig.Session.RecenterDone(250.0, 10000.0)).Code);
    }
}
