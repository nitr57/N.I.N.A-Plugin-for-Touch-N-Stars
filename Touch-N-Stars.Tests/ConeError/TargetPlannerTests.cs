using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class TargetPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc);
    private static readonly PairTiming Timing = new(SlewSeconds: 120, FlipSeconds: 90, SettleSeconds: 5, ExposureSeconds: 5, SolveSeconds: 20);
    private const double Lst = 22.5;

    private static PlannerInput Input(
        double latitude = 50.0,
        int repetitions = 5,
        double? dec = null,
        double minAltitude = 20.0,
        double flipAfter = 5.0,
        double maxAfter = 10.0,
        Func<double, double>? horizon = null) => new()
    {
        NowUtc = Now,
        LocalSiderealTimeHours = Lst,
        LatitudeDegrees = latitude,
        Repetitions = repetitions,
        Declination = dec,
        Timing = Timing,
        MinAltitudeDegrees = minAltitude,
        HorizonAltitudeAt = horizon,
        FlipAfterMinutes = flipAfter,
        MaxMinutesAfterMeridian = maxAfter
    };

    private static double HourAngleAt(DateTime utc, double ra) => Spherical.HourAngle(Lst + TargetPlanner.Sidereal((utc - Now).TotalSeconds), ra);

    [Fact]
    public void SuggestsDeclinationZeroAtMidNorthernLatitudes()
    {
        AutoPlan plan = TargetPlanner.Plan(Input());

        Assert.True(plan.Ok, string.Join(" ", plan.Problems));
        Assert.Equal(0.0, plan.DeclinationDegrees);
        Assert.True(plan.DeclinationSuggested);
        Assert.Equal(5, plan.Pairs.Count);
        Assert.Equal(40.0, plan.Pairs[0].Altitude, 1e-6);
    }

    [Fact]
    public void MovesTowardThePoleWhenTheEquatorIsTooLow()
    {
        // At 65° N the equator culminates at 25°; with a 30° minimum (plus margin) the suggestion moves north.
        AutoPlan plan = TargetPlanner.Plan(Input(latitude: 65.0, minAltitude: 30.0));

        Assert.True(plan.Ok);
        Assert.Equal(10.0, plan.DeclinationDegrees);
    }

    [Fact]
    public void MovesTowardTheSouthPoleInTheSouthernHemisphere()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(latitude: -65.0, minAltitude: 30.0));

        Assert.True(plan.Ok);
        Assert.Equal(-10.0, plan.DeclinationDegrees);
    }

    [Fact]
    public void RefusesWhenEvenThirtyDegreesIsBelowTheMinimum()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(latitude: 85.0, minAltitude: 40.0));

        Assert.False(plan.Ok);
        Assert.Contains("below the minimum altitude", plan.Problems[0]);
    }

    [Fact]
    public void RefusesARequestedDeclinationBeyondThirtyDegrees()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(dec: 35.0));

        Assert.False(plan.Ok);
        Assert.Contains("±30°", plan.Problems[0]);
    }

    [Fact]
    public void ARequestedDeclinationIsUsedAsIs()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(dec: 12.0));

        Assert.True(plan.Ok);
        Assert.Equal(12.0, plan.DeclinationDegrees);
        Assert.False(plan.DeclinationSuggested);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    public void RefusesRepetitionsOutOfRange(int repetitions)
    {
        Assert.False(TargetPlanner.Plan(Input(repetitions: repetitions)).Ok);
    }

    [Fact]
    public void RefusesWhenTheFlipComesAfterTheProfileMaximum()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(flipAfter: 15.0, maxAfter: 10.0));

        Assert.False(plan.Ok);
        Assert.Contains("minutes after meridian", plan.Problems[0]);
    }

    [Fact]
    public void AFlipRightAtTheMeridianIsMovedOneMinutePastIt()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(flipAfter: 0.0));

        Assert.True(plan.Ok);
        Assert.Equal(TargetPlanner.MinFlipAfterMinutes, plan.FlipAfterMinutes);
    }

    [Fact]
    public void TheWestSampleIsJustBeforeTheMeridianAndTheEastSampleAfterTheFlipTime()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(flipAfter: 5.0));

        Assert.All(plan.Pairs, p =>
        {
            Assert.Equal(-TargetPlanner.WestSampleMinutesBeforeMeridian / 60.0, HourAngleAt(p.WestExposureUtc, p.RightAscensionHours), 1e-9);
            Assert.Equal(5.0 / 60.0, HourAngleAt(p.FlipUtc, p.RightAscensionHours), 1e-9);
            Assert.Equal(p.EastHourAngle, HourAngleAt(p.EastExposureUtc, p.RightAscensionHours), 1e-9);
            Assert.True(p.EastHourAngle > 5.0 / 60.0);
            Assert.True(p.FlipUtc < p.EastExposureUtc);
        });
    }

    [Fact]
    public void PairsFollowEachOtherInTime()
    {
        AutoPlan plan = TargetPlanner.Plan(Input());

        for (int i = 1; i < plan.Pairs.Count; i++)
        {
            Assert.True(plan.Pairs[i].WestExposureUtc > plan.Pairs[i - 1].EastExposureUtc);
            Assert.True(plan.Pairs[i].RightAscensionHours != plan.Pairs[i - 1].RightAscensionHours);
        }
    }

    [Fact]
    public void ACustomHorizonAboveTheTargetRefusesThePlan()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(dec: 0.0, horizon: az => az > 170.0 && az < 190.0 ? 45.0 : 0.0));

        Assert.False(plan.Ok);
        Assert.Contains("custom horizon", plan.Problems[0]);
    }

    [Fact]
    public void EstimatedDurationCoversAllPairs()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(repetitions: 3));

        double first = TargetPlanner.PairTimes(Timing, Timing.SlewSeconds, 5.0).End;
        double next = TargetPlanner.PairTimes(Timing, Timing.FlipSeconds, 5.0).End;
        Assert.Equal(first + 2 * next, plan.EstimatedSeconds, 1e-6);
    }

    [Fact]
    public void ASlowSolveDelaysTheFlipInsteadOfFlippingBeforeTheWestSampleIsDone()
    {
        var slow = Timing with { SolveSeconds = 600 };

        TargetPlanner.Times t = TargetPlanner.PairTimes(slow, slow.SlewSeconds, 1.0);

        Assert.Equal(t.WestMid + slow.ExposureSeconds / 2.0 + slow.SolveSeconds, t.Flip, 1e-9);
    }
}
