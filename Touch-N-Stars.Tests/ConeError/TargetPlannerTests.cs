using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class TargetPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 14, 22, 0, 0, DateTimeKind.Utc);
    private static readonly PairTiming Timing = new(SlewSeconds: 120, FlipSeconds: 90, SettleSeconds: 5, ExposureSeconds: 5, SolveSeconds: 20);

    private static PlannerInput Input(
        double latitude = 50.0,
        int repetitions = 5,
        double? dec = null,
        PierSideLabel? side = PierSideLabel.East,
        double minAltitude = 20.0,
        double pastMeridian = 2.5) => new()
    {
        NowUtc = Now,
        LocalSiderealTimeHours = 22.5,
        LatitudeDegrees = latitude,
        CurrentSide = side,
        Repetitions = repetitions,
        Declination = dec,
        Timing = Timing,
        Limits = new LimitSet { MinAltitudeDegrees = minAltitude, PinsPastMeridianDegrees = pastMeridian, SunExclusionDegrees = 30.0 },
        SunAt = _ => new HaDec(12.0, -10.0)
    };

    [Fact]
    public void SuggestsDeclinationZeroAtMidNorthernLatitudes()
    {
        AutoPlan plan = TargetPlanner.Plan(Input());

        Assert.True(plan.Ok, plan.Refusal ?? LimitValidator.Describe(plan.Violations));
        Assert.Equal(0.0, plan.DeclinationDegrees);
        Assert.True(plan.DeclinationSuggested);
        Assert.Equal(10, plan.Steps.Count);
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
        AutoPlan plan = TargetPlanner.Plan(Input(latitude: 80.0, minAltitude: 45.0));

        Assert.False(plan.Ok);
        Assert.Contains(plan.Violations, v => v.Code == LimitViolationCode.BelowHorizon);
    }

    [Fact]
    public void RefusesARequestedDeclinationBeyondThirtyDegrees()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(dec: 35.0));

        Assert.False(plan.Ok);
        Assert.NotNull(plan.Refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void RefusesRepetitionsOutOfRange(int repetitions)
    {
        Assert.NotNull(TargetPlanner.Plan(Input(repetitions: repetitions)).Refusal);
    }

    [Fact]
    public void ASinglePairIsPlanned()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(repetitions: 1));

        Assert.True(plan.Ok, plan.Refusal ?? LimitValidator.Describe(plan.Violations));
        Assert.Equal(2, plan.Steps.Count);
        Assert.NotEqual(plan.Steps[0].Side, plan.Steps[1].Side);
    }

    [Fact]
    public void EachPairIsSymmetricAboutTheMeridianAndAlternatesTheSideOrder()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(side: PierSideLabel.West));

        for (int pair = 0; pair < 5; pair++)
        {
            PlannedStep first = plan.Steps[2 * pair];
            PlannedStep second = plan.Steps[2 * pair + 1];

            Assert.Equal(first.RightAscensionHours, second.RightAscensionHours);
            Assert.Equal(-first.Check.HourAngle, second.Check.HourAngle, 1e-9);
            Assert.NotEqual(first.Side, second.Side);
            Assert.Equal("slew", first.Kind);
            Assert.Equal("flip", second.Kind);
            Assert.Equal(pair % 2 == 0 ? PierSideLabel.West : PierSideLabel.East, first.Side);
        }
    }

    [Fact]
    public void EveryPlannedSampleIsWithinTheMeridianLimitOnItsSide()
    {
        AutoPlan plan = TargetPlanner.Plan(Input());

        Assert.All(plan.Steps, s => Assert.True(s.Check.PastMeridianDegrees <= 2.5));
        Assert.True(plan.MaxAbsHourAngleDegrees < 2.5);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.0)]
    public void WithASmallMeridianLimitEveryPairIsMeasuredOnTheNormalSides(double pastMeridian)
    {
        // A mount that allows (almost) nothing past the meridian cannot alternate the side order;
        // West first (HA < 0), East second (HA > 0) keeps both samples counterweight-down.
        AutoPlan plan = TargetPlanner.Plan(Input(pastMeridian: pastMeridian, side: PierSideLabel.East));

        Assert.True(plan.Ok, plan.Refusal ?? LimitValidator.Describe(plan.Violations));
        Assert.True(plan.NormalSidesOnly);
        Assert.All(plan.Steps, s => Assert.True(s.Check.PastMeridianDegrees <= 0.0));
        Assert.All(plan.Steps.Where((_, i) => i % 2 == 0), s => Assert.Equal(PierSideLabel.West, s.Side));
        Assert.Equal("flip", plan.Steps[0].Kind);
    }

    [Fact]
    public void AnEastSampleThatIsPastTheLimitAtTheGotoFallsBackToTheNormalSides()
    {
        // the 2026-10-20 UMi run: 1° east limit, 20 s settle; the counterweight-up East sample is 0.34°
        // past the meridian at the planned exposure but 1.0° past it when the goto arrives at once
        var timing = new PairTiming(SlewSeconds: 140, FlipSeconds: 110, SettleSeconds: 20, ExposureSeconds: 2, SolveSeconds: 30);
        PlannerInput input = Input(side: PierSideLabel.East);
        input = new PlannerInput
        {
            NowUtc = input.NowUtc,
            LocalSiderealTimeHours = input.LocalSiderealTimeHours,
            LatitudeDegrees = input.LatitudeDegrees,
            CurrentSide = PierSideLabel.East,
            Repetitions = 3,
            Timing = timing,
            Limits = new LimitSet { MinAltitudeDegrees = 20.0, PinsPastMeridianDegrees = 2.5, MountPastMeridianEastDegrees = 1.0, MountPastMeridianWestDegrees = 2.5, SunExclusionDegrees = 30.0 },
            SunAt = input.SunAt
        };

        AutoPlan plan = TargetPlanner.Plan(input);

        Assert.True(plan.Ok, plan.Refusal ?? LimitValidator.Describe(plan.Violations));
        Assert.True(plan.NormalSidesOnly);
        Assert.All(plan.Steps, s => Assert.Empty(s.WindowViolations));
        Assert.All(plan.Steps, s => Assert.True(s.Check.PastMeridianDegrees <= 0.0));
    }

    [Fact]
    public void ARoomyMeridianLimitKeepsTheAlternatingOrder()
    {
        Assert.False(TargetPlanner.Plan(Input(pastMeridian: 2.5)).NormalSidesOnly);
    }

    [Fact]
    public void SunNearTheTargetRefusesThePlan()
    {
        PlannerInput input = Input();
        input = new PlannerInput
        {
            NowUtc = input.NowUtc,
            LocalSiderealTimeHours = input.LocalSiderealTimeHours,
            LatitudeDegrees = input.LatitudeDegrees,
            CurrentSide = input.CurrentSide,
            Repetitions = input.Repetitions,
            Declination = 0.0,
            Timing = input.Timing,
            Limits = input.Limits,
            SunAt = _ => new HaDec(0.5, 5.0)
        };

        AutoPlan plan = TargetPlanner.Plan(input);

        Assert.Contains(plan.Violations, v => v.Code == LimitViolationCode.SunTooClose);
    }

    [Fact]
    public void EstimatedDurationCoversAllPairs()
    {
        AutoPlan plan = TargetPlanner.Plan(Input(repetitions: 3));

        // First pair with the full slew, the next two with a short one.
        double expected = (120 + 90 + 60) + 2 * (30 + 90 + 60);
        Assert.Equal(expected, plan.EstimatedSeconds, 1e-9);
    }
}
