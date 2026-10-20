using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class AutoMeasurementSessionTests
{
    private static void AssertComplete(SessionStatus status)
        => Assert.True(status.Phase == SessionPhase.Complete, $"{status.Phase}: {status.Error?.Code} {status.Error?.Message}");

    [Theory]
    [InlineData(5.0)]
    [InlineData(-5.0)]
    [InlineData(1.2)]
    public async Task FullRunRecoversTheInjectedConeAndTheEndToShim(double tilt)
    {
        using var rig = new AutoRig(tiltArcmin: tilt);

        SessionStatus status = await AutoRig.Run(rig.Session());

        AssertComplete(status);
        Assert.Equal(-tilt, status.Result.ConeArcmin, 0.05);
        Assert.Equal(rig.World.ExpectedShimEnd().ToString(), status.Result.ShimEnd);
        Assert.Equal(rig.World.ExpectedShimMm(200.0), status.Result.ShimMm!.Value, 0.005);
        Assert.Equal(3, status.Result.PairsUsed);
    }

    [Fact]
    public async Task EachPairIsAGotoToTheWestSideAndAMeridianFlipToTheEastSide()
    {
        using var rig = new AutoRig();

        await AutoRig.Run(rig.Session(repetitions: 2));

        // the goto of pair 2 comes from the east side: a normal mount goes back west by itself
        Assert.Equal(
            new[] { ("slew", PierSideLabel.West), ("flip", PierSideLabel.East), ("slew", PierSideLabel.West), ("flip", PierSideLabel.East) },
            rig.Mount.Moves.Select(m => (m.Kind, m.Side)));
    }

    [Fact]
    public async Task TheFlipWaitsUntilTheTargetIsPastTheMeridianByTheProfileMinutes()
    {
        using var rig = new AutoRig();

        await AutoRig.Run(rig.Session(repetitions: 2, flipAfterMinutes: 5.0));

        Assert.All(rig.Mount.Moves.Where(m => m.Kind == "flip"), m => Assert.True(m.HourAngle >= 5.0 / 60.0 - 1e-9));
        Assert.All(rig.Mount.Moves.Where(m => m.Kind == "slew"), m => Assert.InRange(m.HourAngle, -0.05, 0.0));
    }

    [Fact]
    public async Task AMountThatStaysOnItsSideAtTheGotoIsFlippedWest()
    {
        using var rig = new AutoRig(tiltArcmin: 4.0);
        rig.Mount.GotoSide = GotoSideChoice.StayOnSide;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        AssertComplete(status);
        Assert.Equal(-4.0, status.Result.ConeArcmin, 0.05);
        Assert.Contains(rig.Mount.Moves, m => m.Kind == "flip" && m.Side == PierSideLabel.West);
    }

    [Fact]
    public async Task AMountThatCannotFlipStopsWithoutFurtherMoves()
    {
        using var rig = new AutoRig();
        rig.Mount.CannotFlip = true;

        SessionStatus status = await AutoRig.Run(rig.Session());

        Assert.Equal(SessionPhase.Failed, status.Phase);
        Assert.Equal("PierSideNotReached", status.Error.Code);
        Assert.Equal(2, rig.Mount.Moves.Count);
        Assert.Null(status.Result);
    }

    [Fact]
    public async Task AMountThatNeverReachesTheWestSideStops()
    {
        using var rig = new AutoRig();
        rig.Mount.GotoSide = GotoSideChoice.StayOnSide;
        rig.Mount.CannotFlip = true;

        SessionStatus status = await AutoRig.Run(rig.Session());

        Assert.Equal("PierSideNotReached", status.Error.Code);
        Assert.Contains("west", status.Error.Message);
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

        AssertComplete(status);
        Assert.Contains(status.Pairs, p => p.Problem != null && p.Problem.Contains("plate solve failed"));
        Assert.Equal(3, status.Result.PairsUsed);
        Assert.Equal(-3.0, status.Result.ConeArcmin, 0.05);
    }

    [Fact]
    public async Task ASolveThatWorksOnTheSecondTryIsUsed()
    {
        using var rig = new AutoRig();
        rig.Imager.Fail = (call, _) => call == 0;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        AssertComplete(status);
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
    }

    [Fact]
    public async Task AMountThatMovesDuringTheExposureLosesThatSample()
    {
        using var rig = new AutoRig();
        rig.Imager.DuringExposure = call =>
        {
            if (call is 0 or 1)
            {
                rig.Mount.Place(rig.Mount.HourAngle + 0.01, rig.Mount.Declination);
            }
        };

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 3));

        AssertComplete(status);
        Assert.Contains(status.Pairs, p => p.Problem != null && p.Problem.Contains("moved"));
    }

    [Fact]
    public async Task AbortWhileWaitingForTheMeridianStopsTheMount()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Clock.Hold = () => session.GetStatus().Phase == SessionPhase.WaitingForMeridian;

        session.Start();
        await WaitFor(session, s => s.Phase == SessionPhase.WaitingForMeridian);
        Assert.NotNull(session.GetStatus().SecondsUntilFlip);
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        SessionStatus status = session.GetStatus();

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("Aborted", status.Error.Code);
        Assert.True(rig.Mount.StopCount >= 1);
        Assert.DoesNotContain(rig.Mount.Moves, m => m.Kind == "flip");
    }

    [Fact]
    public async Task AbortMidFlipStopsTheMountBeforeAnythingIsCancelled()
    {
        using var rig = new AutoRig();
        bool stoppedBeforeCancel = false;
        var flip = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.Mount.OnMove = async (kind, _, token) =>
        {
            if (kind == "flip")
            {
                using CancellationTokenRegistration registration = token.Register(() => stoppedBeforeCancel = rig.Mount.StopCount > 0);
                flip.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            }
        };
        AutoMeasurementSession session = rig.Session();

        session.Start();
        await flip.Task.WaitAsync(TimeSpan.FromSeconds(10));
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionPhase.Aborted, session.GetStatus().Phase);
        Assert.True(stoppedBeforeCancel);
        Assert.True(rig.Imager.AbortCount >= 1);
    }

    [Fact]
    public async Task AGotoThatFailsBecauseOfTheAbortIsReportedAsAborted()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session();
        rig.Mount.OnMove = (kind, _, _) =>
        {
            if (kind == "flip")
            {
                session.Abort();
                throw new ConeErrorException("FlipFailed", "The meridian flip failed: aborted");
            }

            return Task.CompletedTask;
        };

        SessionStatus status = await AutoRig.Run(session);

        Assert.Equal(SessionPhase.Aborted, status.Phase);
        Assert.Equal("Aborted", status.Error.Code);
    }

    [Fact]
    public async Task TrackingOffBeforeTheRunIsOffAgainAfterIt()
    {
        using var rig = new AutoRig(tracking: false);

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 2));

        AssertComplete(status);
        Assert.False(rig.Mount.Tracking);
        Assert.Equal(new[] { false }, rig.Mount.TrackingCalls);
    }

    [Fact]
    public async Task TrackingOnBeforeTheRunIsLeftOn()
    {
        using var rig = new AutoRig(tracking: true);

        await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.True(rig.Mount.Tracking);
        Assert.Empty(rig.Mount.TrackingCalls);
    }

    [Fact]
    public async Task AnAbortedRunAlsoPutsTrackingBack()
    {
        using var rig = new AutoRig(tracking: false);
        AutoMeasurementSession session = rig.Session();
        rig.Clock.Hold = () => session.GetStatus().Phase == SessionPhase.WaitingForMeridian;

        session.Start();
        await WaitFor(session, s => s.Phase == SessionPhase.WaitingForMeridian);
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(rig.Mount.Tracking);
    }

    [Fact]
    public async Task ATargetBelowTheMinimumAltitudeIsNeverSlewedTo()
    {
        using var rig = new AutoRig();

        SessionStatus status = await AutoRig.Run(rig.Session(minAltitude: 60.0));

        Assert.Equal("BelowMinimumAltitude", status.Error.Code);
        Assert.Empty(rig.Mount.Moves);
    }

    [Fact]
    public async Task TheCounterweightQuestionIsAskedOnceOnTheWestSide()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(repetitions: 2, askPierLabels: true);
        bool confirmed = false;
        session.PierLabelsConfirmed += () => confirmed = true;

        session.Start();
        SessionQuestion question = (await WaitFor(session, s => s.Question != null)).Question;
        Assert.Equal("West", session.GetStatus().CurrentSide);
        Assert.Contains("EAST side of the mount", question.Text);
        Assert.False(session.Answer(question.Id + 1, yes: true));
        Assert.True(session.Answer(question.Id, yes: true));
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(confirmed);
        AssertComplete(session.GetStatus());
    }

    [Fact]
    public async Task NoToTheCounterweightQuestionStopsTheRun()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(askPierLabels: true);

        session.Start();
        SessionQuestion question = (await WaitFor(session, s => s.Question != null)).Question;
        session.Answer(question.Id, yes: false);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("PierLabelsInverted", session.GetStatus().Error.Code);
        Assert.Single(rig.Mount.Moves);
    }

    [Fact]
    public async Task AbortWhileTheQuestionIsOpenIsAnAbortNotInvertedLabels()
    {
        using var rig = new AutoRig();
        AutoMeasurementSession session = rig.Session(askPierLabels: true);

        session.Start();
        await WaitFor(session, s => s.Question != null);
        session.Abort();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SessionPhase.Aborted, session.GetStatus().Phase);
        Assert.Equal("Aborted", session.GetStatus().Error.Code);
    }

    [Fact]
    public async Task ResultIsStoredAndTheNextRunComparesWithIt()
    {
        using var rig = new AutoRig(tiltArcmin: 5.0);
        SessionStatus first = await AutoRig.Run(rig.Session(repetitions: 2));

        SessionStatus second = await AutoRig.Run(rig.Session(repetitions: 2));

        Assert.NotNull(second.Previous);
        Assert.Equal(first.Result.Id, second.Previous.Id);
        Assert.Equal(2, rig.Store.LoadResults().Count);
    }

    [Fact]
    public async Task NoisyPairsWithAnOutlierStillGiveTheRightEnd()
    {
        using var rig = new AutoRig(tiltArcmin: -2.0);
        var random = new Random(7);
        rig.Imager.NoiseArcmin = call => call == 5 ? 25.0 : (random.NextDouble() - 0.5) * 0.4;

        SessionStatus status = await AutoRig.Run(rig.Session(repetitions: 6));

        AssertComplete(status);
        Assert.Contains(status.Result.Pairs, p => p.Rejected);
        Assert.Equal(2.0, status.Result.ConeArcmin, 0.3);
        Assert.Equal(ShimEnd.Front.ToString(), status.Result.ShimEnd);
    }

    private static async Task<SessionStatus> WaitFor(AutoMeasurementSession session, Func<SessionStatus, bool> condition)
    {
        for (int i = 0; i < 1000; i++)
        {
            SessionStatus status = session.GetStatus();
            if (condition(status))
            {
                return status;
            }

            await Task.Delay(5);
        }

        throw new TimeoutException("condition not reached");
    }
}
