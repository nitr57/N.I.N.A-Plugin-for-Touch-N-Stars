using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

public class LimitValidatorTests
{
    private const double Latitude = 50.0;

    /// <summary>The Sun far below, so that only the limit under test can refuse.</summary>
    private static readonly HaDec[] NightSun = { new(12.0, -20.0) };

    private static LimitSet Pins(double minAltitude = 20.0, double pastMeridian = 2.5) => new()
    {
        MinAltitudeDegrees = minAltitude,
        PinsPastMeridianDegrees = pastMeridian,
        SunExclusionDegrees = 30.0
    };

    [Fact]
    public void MeridianTargetAtDecZeroPassesOnBothSides()
    {
        (EndpointCheck east, EndpointCheck west) = LimitValidator.ValidateBothSides(Pins(), 0.0, 0.0, Latitude, NightSun);

        Assert.True(east.Ok);
        Assert.True(west.Ok);
        Assert.Equal(40.0, east.Altitude, 0.01);
    }

    [Fact]
    public void BelowThePinsMinimumAltitudeIsRefused()
    {
        EndpointCheck check = LimitValidator.ValidateEndpoint(Pins(minAltitude: 45.0), PierSideLabel.East, 0.0, 0.0, Latitude, NightSun);

        LimitViolation violation = Assert.Single(check.Violations);
        Assert.Equal(LimitViolationCode.BelowHorizon, violation.Code);
        Assert.Equal("PINS minimum altitude", violation.Source);
    }

    [Fact]
    public void TheCustomHorizonIsUsedWhenItIsHigher()
    {
        var limits = new LimitSet
        {
            MinAltitudeDegrees = 10.0,
            PinsPastMeridianDegrees = 2.5,
            HorizonAltitudeAt = az => az > 170.0 && az < 190.0 ? 42.0 : 5.0
        };

        EndpointCheck check = LimitValidator.ValidateEndpoint(limits, PierSideLabel.East, 0.0, 0.0, Latitude, NightSun);

        Assert.Equal("PINS custom horizon", Assert.Single(check.Violations).Source);
        Assert.Equal(42.0, check.MinAltitudeDegrees);
    }

    [Theory]
    [InlineData(25.0, 30.0, "INDI OnStep horizon limit")]
    [InlineData(35.0, 30.0, "PINS minimum altitude")]
    public void StricterOfMountAndPinsMinimumAltitudeApplies(double pinsMin, double mountHorizon, string expectedSource)
    {
        var limits = new LimitSet
        {
            MinAltitudeDegrees = pinsMin,
            PinsPastMeridianDegrees = 2.5,
            MountHorizonDegrees = mountHorizon,
            MountSource = "INDI OnStep"
        };

        (double altitude, string source) = limits.EffectiveMinAltitude(180.0);

        Assert.Equal(Math.Max(pinsMin, mountHorizon), altitude);
        Assert.Equal(expectedSource, source);
    }

    [Fact]
    public void AboveTheMountOverheadLimitIsRefused()
    {
        var limits = new LimitSet { MinAltitudeDegrees = 0.0, PinsPastMeridianDegrees = 2.5, MountOverheadDegrees = 80.0, MountSource = "OnStepX" };

        EndpointCheck check = LimitValidator.ValidateEndpoint(limits, PierSideLabel.East, 0.0, 45.0, Latitude, NightSun);

        Assert.Equal(LimitViolationCode.AboveOverhead, Assert.Single(check.Violations).Code);
    }

    [Theory]
    [InlineData(PierSideLabel.East, -0.2)]
    [InlineData(PierSideLabel.West, 0.2)]
    public void PastTheMeridianLimitIsRefusedOnTheCounterweightUpSide(PierSideLabel side, double ha)
    {
        // 0.2 h = 3° past the meridian; the PINS limit (10 min after the meridian) is 2.5°.
        EndpointCheck check = LimitValidator.ValidateEndpoint(Pins(), side, ha, 0.0, Latitude, NightSun);

        LimitViolation violation = Assert.Single(check.Violations);
        Assert.Equal(LimitViolationCode.PastMeridianLimit, violation.Code);
        Assert.Equal(3.0, violation.Value, 1e-9);
        Assert.Equal(2.5, violation.Limit);
    }

    [Theory]
    [InlineData(PierSideLabel.East, 0.2)]
    [InlineData(PierSideLabel.West, -0.2)]
    public void TheCounterweightDownSideHasNoPastMeridianLimit(PierSideLabel side, double ha)
    {
        Assert.True(LimitValidator.ValidateEndpoint(Pins(), side, ha, 0.0, Latitude, NightSun).Ok);
    }

    [Fact]
    public void MountMeridianLimitStricterThanPinsWins()
    {
        var limits = new LimitSet
        {
            MinAltitudeDegrees = 20.0,
            PinsPastMeridianDegrees = 5.0,
            MountPastMeridianEastDegrees = 1.0,
            MountPastMeridianWestDegrees = 10.0,
            MountSource = "OnStepX"
        };

        EndpointCheck east = LimitValidator.ValidateEndpoint(limits, PierSideLabel.East, -0.1, 0.0, Latitude, NightSun);
        EndpointCheck west = LimitValidator.ValidateEndpoint(limits, PierSideLabel.West, 0.1, 0.0, Latitude, NightSun);

        Assert.Equal("OnStepX", Assert.Single(east.Violations).Source);
        Assert.Equal(1.0, east.PastMeridianLimitDegrees);
        Assert.True(west.Ok);
        Assert.Equal(5.0, west.PastMeridianLimitDegrees);
    }

    [Fact]
    public void PinsLimitStricterThanMountWins()
    {
        var limits = new LimitSet { MinAltitudeDegrees = 20.0, PinsPastMeridianDegrees = 1.0, MountPastMeridianWestDegrees = 15.0, MountSource = "OnStepX" };

        EndpointCheck west = LimitValidator.ValidateEndpoint(limits, PierSideLabel.West, 0.1, 0.0, Latitude, NightSun);

        Assert.Equal("PINS", Assert.Single(west.Violations).Source);
    }

    [Fact]
    public void UnknownMountLimitsFallBackToPins()
    {
        LimitSet limits = Pins();

        Assert.Equal(2.5, limits.EffectivePastMeridianDegrees(PierSideLabel.East));
        Assert.Equal("PINS", limits.PastMeridianSource(PierSideLabel.West));
        Assert.Equal(("PINS minimum altitude", 20.0), (limits.EffectiveMinAltitude(90.0).Source, limits.EffectiveMinAltitude(90.0).Altitude));
    }

    [Fact]
    public void TargetReachableOnOnlyOneSideIsReportedAsSuch()
    {
        // 0.5 h west of the meridian: fine on pierEast, 7.5° past the meridian on pierWest.
        (EndpointCheck east, EndpointCheck west) = LimitValidator.ValidateBothSides(Pins(), 0.5, 0.0, Latitude, NightSun);

        Assert.True(east.Ok);
        Assert.False(west.Ok);
        Assert.Equal(LimitViolationCode.PastMeridianLimit, Assert.Single(west.Violations).Code);
    }

    [Fact]
    public void SunWithinTheRadiusAtTheEndpointIsRefused()
    {
        HaDec[] sun = { new(0.5, 5.0) };

        EndpointCheck check = LimitValidator.ValidateEndpoint(Pins(minAltitude: 0.0), PierSideLabel.East, 0.0, 0.0, Latitude, sun);

        Assert.Equal(LimitViolationCode.SunTooClose, Assert.Single(check.Violations).Code);
        Assert.InRange(check.SunSeparationDegrees, 8.5, 9.5);
    }

    [Fact]
    public void TheClosestOfSeveralSunPositionsCounts()
    {
        // Now 60° away, at the end of the move's time budget 27° away.
        HaDec[] suns = { new(4.0, 0.0), new(1.8, 0.0) };

        EndpointCheck check = LimitValidator.ValidateEndpoint(Pins(minAltitude: 0.0), PierSideLabel.East, 0.0, 0.0, Latitude, suns);

        Assert.Equal(LimitViolationCode.SunTooClose, Assert.Single(check.Violations).Code);
        Assert.Equal(27.0, check.SunSeparationDegrees, 1e-6);
    }

    [Fact]
    public void RequiresASunPosition()
    {
        Assert.Throws<ArgumentException>(() => LimitValidator.ValidateEndpoint(Pins(), PierSideLabel.East, 0.0, 0.0, Latitude, Array.Empty<HaDec>()));
    }
}
