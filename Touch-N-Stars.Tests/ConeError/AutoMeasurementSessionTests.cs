using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class AutoMeasurementSessionTests
{
    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    [InlineData(1.2)]
    public async Task FullRunRecoversTheInjectedConeAndTheEndToShim(double tilt)
    {
        using var rig = new AutoRig(tiltArcmin: tilt);

        SessionStatus status = await AutoRig.Run(rig.Session());

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.NotNull(status.Result);
        Assert.Equal(-tilt, status.Result.ConeArcmin, 0.05);
        Assert.Equal(rig.World.ExpectedShimEnd().ToString(), status.Result.ShimEnd);
        Assert.Equal(rig.World.ExpectedShimMm(200.0), status.Result.ShimMm!.Value, 0.005);
        Assert.Equal(4, status.Result.PairsUsed);
        Assert.Equal("Good", status.Result.Quality);
    }

    [Fact]
    public async Task AlternatesTheSidesAndRestoresThePreferredPierSide()
    {
        using var rig = new AutoRig();

        await AutoRig.Run(rig.Session(repetitions: 3));

        // E W | W E | E W: one slew per pair on the side the mount is on, one flip
        Assert.Equal(
            new[] { PierSideLabel.East, PierSideLabel.West, PierSideLabel.West, PierSideLabel.East, PierSideLabel.East, PierSideLabel.West },
            rig.Mount.Slews.Select(s => s.Side));
        Assert.Equal("Best", rig.PierSide.Setting);
        Assert.Null(rig.Store.LoadJournal());
    }

    [Fact]
    public async Task EachPairIsMeasuredSymmetricallyAboutTheMeridian()
    {
        using var rig = new AutoRig();

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.All(rig.Mount.Slews, s => Assert.InRange(Math.Abs(Spherical.HourAngle(rig.Mount.Lst, s.Ra)), 0.0, 0.2));
        Assert.Equal(2, status.Pairs.Count(p => p.ConeArcmin.HasValue));
    }

    [Fact]
    public async Task ResultIsStoredAndTheNextRunComparesWithIt()
    {
        using var rig = new AutoRig(tiltArcmin: 5.0);
        SessionStatus first = await AutoRig.Run(rig.Session());

        SessionStatus second = await AutoRig.Run(rig.Session());

        Assert.NotNull(second.Previous);
        Assert.Equal(first.Result.Id, second.Previous.Id);
        Assert.Equal(2, rig.Store.LoadResults().Count);
    }

    [Theory]
    [InlineData(PierSideLabel.East)]
    [InlineData(PierSideLabel.West)]
    public async Task SolveFailureOnOneSideDropsThatPairOnly(PierSideLabel failingSide)
    {
        using var rig = new AutoRig(tiltArcmin: 3.0);
        int failuresLeft = 2;
        // both attempts of the first sample on that side fail
        rig.Imager.Fail = (_, side) => side == failingSide && failuresLeft-- > 0;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 4));

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.Contains(status.Pairs, p => p.Problem != null && p.Problem.Contains("plate solve failed"));
        Assert.True(status.Result.PairsUsed >= 3);
        Assert.Equal(-3.0, status.Result.ConeArcmin, 0.05);
    }

    [Fact]
    public async Task ASolveThatWorksOnTheSecondTryIsUsed()
    {
        using var rig = new AutoRig();
        rig.Imager.Fail = (call, _) => call == 0;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.Equal(2, status.Result.PairsUsed);
    }

    [Fact]
    public async Task FailsWithTooFewPairsWhenNothingSolves()
    {
        using var rig = new AutoRig();
        rig.Imager.Fail = (_, _) => true;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 3));

        Assert.Equal(SessionPhase.Failed, status.Phase);
        Assert.Equal("TooFewPairs", status.Error.Code);
        Assert.Null(status.Result);
        Assert.Equal("Best", rig.PierSide.Setting);
    }

    [Fact]
    public async Task AbortMidFlipStopsTheMountAndRestores()
    {
        using var rig = new AutoRig();
        var flipStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnSlew = async (side, token) =>
        {
            if (side == PierSideLabel.West)
            {
                flipStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        AutoMeasurementSession session = rig.Session();
        bool trackingBefore = rig.Mount.Tracking;

        session.Start();
        await flipStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        SessionStatus status = session.GetStatus();

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("Aborted", status.Error.Code);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.True(rig.Imager.AbortCount >= 1);
        Assert.Equal("Best", rig.PierSide.Setting);
        Assert.Null(rig.Store.LoadJournal());
        Assert.Equal(trackingBefore, rig.Mount.Tracking);
        Assert.Empty(rig.Mount.TrackingCalls);
        Assert.Null(status.Result);
    }

    [Fact]
    public async Task MountThatIgnoresThePierSideStopsWithoutFurtherMoves()
    {
        using var rig = new AutoRig();
        rig.Mount.IgnorePierSide = true;

        SessionStatus status = await AutoRig.Run(rig.Session());

        Assert.Equal(SessionPhase.Failed, status.Phase);
        Assert.Equal("PierSideRefused", status.Error.Code);
        // the first slew lands on the east side it is on anyway, the flip does not: nothing after that
        Assert.Equal(2, rig.Mount.Slews.Count);
        Assert.Equal("Best", rig.PierSide.Setting);
    }

    [Fact]
    public async Task TargetOutsideTheLimitsIsNeverSlewedTo()
    {
        using var rig = new AutoRig(pastMeridian: 0.05);

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.Equal(SessionPhase.Failed, status.Phase);
        Assert.Equal("TooFewPairs", status.Error.Code);
        Assert.Empty(rig.Mount.Slews);
        Assert.All(status.Pairs, p => Assert.Contains("past the meridian", p.Problem));
    }

    [Fact]
    public async Task TargetBelowTheMinimumAltitudeIsNeverSlewedTo()
    {
        using var rig = new AutoRig();
        rig.Limits = new LimitSet { MinAltitudeDegrees = 60.0, PinsPastMeridianDegrees = 2.5, SunExclusionDegrees = 30.0 };

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.Empty(rig.Mount.Slews);
        Assert.Equal(SessionPhase.Failed, status.Phase);
    }

    [Fact]
    public async Task SunNearTheTargetStopsTheRunBeforeAnyMove()
    {
        using var rig = new AutoRig();
        rig.Sun.Fixed = new HaDec(0.3, 5.0);

        SessionStatus status = await AutoRig.Run(rig.Session());

        Assert.Equal(SessionPhase.Failed, status.Phase);
        Assert.Equal("SunTooClose", status.Error.Code);
        Assert.Empty(rig.Mount.Slews);
    }

    [Fact]
    public async Task UserSaysTheCounterweightIsWrongStopsTheRun()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(askPierLabels: true);

        session.Start();
        SessionQuestion question = await WaitForQuestion(session);
        Assert.Equal("pierLabels", question.Kind);
        Assert.True(session.Answer(question.Id, yes: false));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        SessionStatus status = session.GetStatus();
        Assert.Equal("PierLabelsInverted", status.Error.Code);
        Assert.Single(rig.Mount.Slews);
        Assert.Equal("Best", rig.PierSide.Setting);
    }

    [Fact]
    public async Task ConfirmedPierLabelsContinueTheRun()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(repetitions: 2, askPierLabels: true);
        bool confirmed = false;
        session.PierLabelsConfirmed += () => confirmed = true;

        session.Start();
        SessionQuestion question = await WaitForQuestion(session);
        Assert.False(session.Answer(question.Id + 1, yes: true));
        Assert.True(session.Answer(question.Id, yes: true));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(confirmed);
        Assert.Equal(SessionPhase.Complete, session.GetStatus().Phase);
    }

    [Fact]
    public async Task WatchdogStopsTheRunWhenTheSunComesTooClose()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Imager.DuringExposure = call =>
        {
            if (call == 1)
            {
                MountSnapshot snap = rig.Mount.Snapshot();
                rig.Sun.Fixed = new HaDec(snap.HourAngleHours + 0.5, snap.DeclinationDegrees);
                session.Watchdog.Check();
            }
        };

        SessionStatus status = await AutoRig.Run(session);

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("SunTooClose", status.Error.Code);
        Assert.True(rig.Mount.StopCount >= 1);
    }

    [Fact]
    public async Task WatchdogStopsTheRunWhenTheMountIsSyncedOrMovedFromElsewhere()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Imager.DuringExposure = call =>
        {
            if (call == 2)
            {
                session.Watchdog.Check();
                rig.Mount.Place(rig.Mount.HourAngle + 0.01, rig.Mount.Declination);
                session.Watchdog.Check();
            }
        };

        SessionStatus status = await AutoRig.Run(session);

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("UnexpectedMotion", status.Error.Code);
    }

    [Fact]
    public async Task NoisyPairsWithAnOutlierStillGiveTheRightEnd()
    {
        using var rig = new AutoRig(tiltArcmin: -2.0);
        var random = new Random(7);
        rig.Imager.NoiseArcmin = call => call == 5 ? 25.0 : (random.NextDouble() - 0.5) * 0.4;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 6));

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.Contains(status.Result.Pairs, p => p.Rejected);
        Assert.Equal(2.0, status.Result.ConeArcmin, 0.3);
        Assert.Equal(ShimEnd.Front.ToString(), status.Result.ShimEnd);
    }

    [Fact]
    public async Task FailedRestoreLeavesTheJournalForLater()
    {
        using var rig = new AutoRig();
        rig.PierSide.FailRestore = true;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.True(status.RestorePending);
        Assert.NotNull(rig.Store.LoadJournal());
        Assert.Equal("Best", rig.Store.LoadJournal().PreferredPierSide);
    }

    [Fact]
    public async Task AbortWhileTheCounterweightQuestionIsOpenIsAnAbortNotInvertedLabels()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(askPierLabels: true);

        session.Start();
        await WaitForQuestion(session);
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        SessionStatus status = session.GetStatus();
        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("Aborted", status.Error.Code);
    }

    [Fact]
    public async Task AGotoThatFailsBecauseOfTheAbortIsReportedAsAborted()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Mount.OnSlew = (side, _) =>
        {
            if (side == PierSideLabel.West)
            {
                session.Abort();
                throw new ConeErrorException("SlewFailed", "The mount refused the slew: aborted");
            }

            return Task.CompletedTask;
        };

        SessionStatus status = await AutoRig.Run(session);

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("Aborted", status.Error.Code);
    }

    [Fact]
    public async Task ARunStartedAtHomeBeginsOnTheSideTheFirmwarePicks()
    {
        using var rig = new AutoRig();
        rig.Mount.AtHome = true;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.Equal(PierSideLabel.West, rig.Mount.Slews[0].Side);
    }

    [Fact]
    public async Task NormalSidesOnlyMeasuresEveryPairWestThenEastWithoutPastMeridianSamples()
    {
        using var rig = new AutoRig(tiltArcmin: 3.0, pastMeridian: 0.0);

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 3, normalSidesOnly: true));

        Assert.Equal(SessionPhase.Complete, status.Phase);
        Assert.Equal(-3.0, status.Result.ConeArcmin, 0.05);
        Assert.Equal(
            new[] { PierSideLabel.West, PierSideLabel.East, PierSideLabel.West, PierSideLabel.East, PierSideLabel.West, PierSideLabel.East },
            rig.Mount.Slews.Select(x => x.Side));
    }

    [Fact]
    public async Task AGotoFromElsewhereStopsTheRun()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Imager.DuringExposure = call =>
        {
            if (call == 1)
            {
                rig.Clock.Advance(TimeSpan.FromSeconds(10));
                rig.Mount.ExternalSlew = true;
                session.Watchdog.Check();
                rig.Mount.ExternalSlew = false;
            }
        };

        SessionStatus status = await AutoRig.Run(session);

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("UnexpectedMotion", status.Error.Code);
    }

    [Fact]
    public async Task TheSessionCountsAsActiveUntilItsRestoreHasRun()
    {
        using var rig = new AutoRig();
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRestore = new SlowRestorePierSide(rig.PierSide, restoring, release.Task);
        rig.Mount.PierSide = rig.PierSide;
        var session = new AutoMeasurementSession(
            rig.Mount, slowRestore, rig.Imager, rig.Clock, rig.Sun, rig.Store,
            new AutoRunOptions
            {
                Repetitions = 2, DeclinationDegrees = 0.0, ExposureSeconds = 5.0, DovetailMm = 200.0, Timing = AutoRig.Timing,
                Limits = rig.Limits, LatitudeDegrees = 50.0, MaxAbsHourAngleDegrees = 2.5, MountKey = "k"
            },
            watchdogLoop: false);

        session.Start();
        await restoring.Task.WaitAsync(TimeSpan.FromSeconds(10));
        bool finishedDuringRestore = session.Finished;
        bool activeDuringRestore = session.GetStatus().Active;
        release.SetResult();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(finishedDuringRestore);
        Assert.True(activeDuringRestore);
        Assert.True(session.Finished);
    }

    private sealed class SlowRestorePierSide : IPierSideControl
    {
        private readonly FakePierSide inner;
        private readonly TaskCompletionSource restoring;
        private readonly Task release;

        public SlowRestorePierSide(FakePierSide inner, TaskCompletionSource restoring, Task release)
        {
            this.inner = inner;
            this.restoring = restoring;
            this.release = release;
        }

        public bool Supported => true;
        public string Description => "slow";
        public string UnsupportedReason => null!;
        public string OriginalSetting => inner.OriginalSetting!;
        public string ReadOriginal() => inner.ReadOriginal();
        public Task PrepareAsync(PierSideLabel side, CancellationToken token) => inner.PrepareAsync(side, token);

        public async Task RestoreAsync()
        {
            restoring.TrySetResult();
            await release;
            await inner.RestoreAsync();
        }

        public Task RestoreToAsync(string setting) => inner.RestoreToAsync(setting);
    }

    private static async Task<SessionQuestion> WaitForQuestion(AutoMeasurementSession session)
    {
        for (int i = 0; i < 500; i++)
        {
            SessionQuestion question = session.GetStatus().Question;
            if (question != null)
            {
                return question;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("no question");
    }
}
