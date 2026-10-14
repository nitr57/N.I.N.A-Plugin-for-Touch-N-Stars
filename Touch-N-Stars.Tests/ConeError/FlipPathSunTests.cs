using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

/// <summary>
/// The sun check of a pier flip: the endpoints and the possible paths between them. The scenario is
/// the daytime check at 50° N: a landmark 2° above the southern horizon (HA 0, δ −38°).
/// </summary>
public class FlipPathSunTests
{
    private const double Latitude = 50.0;
    private const double Longitude = 10.0;
    private static readonly HaDec Landmark = new(0.0, -38.0);

    private static LimitSet DaytimeLimits() => new()
    {
        MinAltitudeDegrees = 0.0,
        PinsPastMeridianDegrees = 2.5,
        SunExclusionDegrees = 30.0
    };

    private static EndpointCheck Flip(HaDec[] suns, bool strict = false)
        => LimitValidator.ValidateMove(DaytimeLimits(), PierSideLabel.East, Landmark, PierSideLabel.West, Landmark, Latitude, suns, strict);

    [Fact]
    public void FlipPathGoesOverThePoleAndEndsOnTheSamePointing()
    {
        IReadOnlyList<HaDec> path = FlipPathSampler.Sample(
            GemGeometry.ToAxes(PierSideLabel.East, 0.0, -38.0, Latitude),
            GemGeometry.ToAxes(PierSideLabel.West, 0.0, -38.0, Latitude),
            strict: false,
            Latitude);

        Assert.Contains(path, p => p.Declination > 89.0);
        Assert.Equal(0.0, path[^1].HourAngle, 1e-9);
        Assert.Equal(-38.0, path[^1].Declination, 1e-9);
        Assert.Equal(-38.0, path[0].Declination, 1e-9);
    }

    [Fact]
    public void BothSidesOfTheSamePointingHaveTheSameSkyPosition()
    {
        HaDec east = GemGeometry.Pointing(GemGeometry.ToAxes(PierSideLabel.East, 1.3, 12.0, Latitude));
        HaDec west = GemGeometry.Pointing(GemGeometry.ToAxes(PierSideLabel.West, 1.3, 12.0, Latitude));
        HaDec southWest = GemGeometry.Pointing(GemGeometry.ToAxes(PierSideLabel.West, 1.3, 12.0, -35.0));
        Assert.Equal(east.HourAngle, southWest.HourAngle, 1e-9);
        Assert.Equal(east.Declination, southWest.Declination, 1e-9);

        Assert.Equal(east.HourAngle, west.HourAngle, 1e-9);
        Assert.Equal(east.Declination, west.Declination, 1e-9);
    }

    [Fact]
    public void SunAtTheLandmarkIsRefusedAtTheEndpoint()
    {
        EndpointCheck check = Flip(new[] { new HaDec(0.4, -30.0) });

        Assert.Contains(check.Violations, v => v.Code == LimitViolationCode.SunTooClose);
    }

    [Theory]
    [InlineData(3.0, 20.0)]
    [InlineData(-3.0, 20.0)]
    public void SunNearThePathButFarFromTheLandmarkIsRefused(double sunHa, double sunDec)
    {
        // Afternoon (or morning) sun in May: 70° from the landmark, but the flip swings the tube past it.
        EndpointCheck check = Flip(new[] { new HaDec(sunHa, sunDec) });

        Assert.True(check.SunSeparationDegrees > 60.0);
        LimitViolation violation = Assert.Single(check.Violations);
        Assert.Equal(LimitViolationCode.SunTooCloseOnPath, violation.Code);
        Assert.True(check.PathSunSeparationDegrees < 30.0);
    }

    [Fact]
    public void SunFarFromLandmarkAndPathIsAllowed()
    {
        EndpointCheck check = Flip(new[] { new HaDec(12.0, -20.0) });

        Assert.True(check.Ok);
        Assert.True(check.PathSunSeparationDegrees > 30.0);
    }

    [Fact]
    public void StrictPathsAlsoRefuseASunOnlyTheRaFirstSwingWouldPass()
    {
        // A target on the celestial equator; a spring-morning Sun 10° up in the east, 75° away.
        // Only turning the RA axis first sweeps the tube along the equator past it.
        var target = new HaDec(0.0, 0.0);
        HaDec[] sun = { new(-5.0, 0.0) };

        EndpointCheck normal = LimitValidator.ValidateMove(DaytimeLimits(), PierSideLabel.East, target, PierSideLabel.West, target, Latitude, sun, strictPath: false);
        EndpointCheck strict = LimitValidator.ValidateMove(DaytimeLimits(), PierSideLabel.East, target, PierSideLabel.West, target, Latitude, sun, strictPath: true);

        Assert.True(normal.Ok, LimitValidator.Describe(normal.Violations));
        Assert.Contains(strict.Violations, v => v.Code == LimitViolationCode.SunTooCloseOnPath);
    }

    [Fact]
    public void ASunBelowTheHorizonRefusesNothing()
    {
        // 8° below the horizon, geometrically next to the swing
        HaDec[] sun = { new(5.0, -23.0) };

        EndpointCheck check = LimitValidator.ValidateMove(
            DaytimeLimits(), PierSideLabel.East, new HaDec(0.0, 0.0), PierSideLabel.West, new HaDec(0.0, 0.0), Latitude, sun, strictPath: true);

        Assert.True(check.Ok);
        Assert.True(check.SunBelowHorizon);
    }

    [Fact]
    public void ShortMovesOnlyCheckTheShortWayRound()
    {
        IReadOnlyList<HaDec> path = FlipPathSampler.Sample(new AxisAngles(10.0, 20.0), new AxisAngles(12.0, 21.0), strict: false, Latitude);

        Assert.All(path, p => Assert.InRange(p.HourAngle * 15.0, 9.9, 12.1));
    }

    [Fact]
    public void WinterNoonSunAboveTheSouthernLandmarkIsRefused()
    {
        // 2026-12-15 near local noon at 10° E: the Sun is about 17° above the southern horizon.
        DateTime noon = new(2026, 12, 15, 11, 15, 0, DateTimeKind.Utc);

        EndpointCheck check = Flip(new[] { Sun(noon) });

        Assert.Contains(check.Violations, v => v.Code == LimitViolationCode.SunTooClose);
    }

    [Fact]
    public void SummerSunsetIsAllowed()
    {
        // 2026-06-21 19:00 UT at 10° E: the Sun is about 3° high in the north-west, 33° from the closest path point.
        DateTime sunset = new(2026, 6, 21, 19, 0, 0, DateTimeKind.Utc);

        EndpointCheck check = Flip(new[] { Sun(sunset), Sun(sunset.AddMinutes(10)) });

        Assert.True(check.Ok, LimitValidator.Describe(check.Violations));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(12)]
    [InlineData(15)]
    [InlineData(18)]
    public void SummerDaytimeFlipsAreRefusedBecauseTheTubeSwingsAcrossTheSky(int hourUt)
    {
        // The flip swings the tube from the southern horizon over the pole through the eastern or
        // western sky; with a 30° radius that passes too close to a Sun that is more than a few
        // degrees above the horizon, although the landmark itself is 60° to 110° away from it.
        DateTime when = new(2026, 6, 21, hourUt, 0, 0, DateTimeKind.Utc);

        EndpointCheck check = Flip(new[] { Sun(when) });

        Assert.True(check.SunSeparationDegrees > 60.0);
        Assert.Contains(check.Violations, v => v.Code == LimitViolationCode.SunTooCloseOnPath);
    }

    [Fact]
    public void WinterMorningSunNearTheFlipPathIsRefused()
    {
        // 2026-12-15 09:00 UT: the Sun is about 33° from the landmark, but the flip passes much closer.
        DateTime morning = new(2026, 12, 15, 9, 0, 0, DateTimeKind.Utc);

        EndpointCheck check = Flip(new[] { Sun(morning) });

        Assert.False(check.Ok);
        Assert.True(check.PathSunSeparationDegrees < check.SunSeparationDegrees);
    }

    [Fact]
    public void SouthernFlipSwingsOverTheSouthPole()
    {
        // 35° S, landmark 2° above the northern horizon on the meridian
        IReadOnlyList<HaDec> path = FlipPathSampler.Sample(
            GemGeometry.ToAxes(PierSideLabel.East, 0.0, 53.0, -35.0),
            GemGeometry.ToAxes(PierSideLabel.West, 0.0, 53.0, -35.0),
            strict: false,
            -35.0);

        Assert.Contains(path, p => p.Declination < -89.0);
        Assert.DoesNotContain(path, p => p.Declination > 53.5);
    }

    [Fact]
    public void SouthernSunNearTheRealSwingIsRefused()
    {
        // A mid-morning Sun 60° from the landmark: sampling over the north pole kept 48° from it,
        // the real swing over the south pole passes about 2° from it.
        var landmark = new HaDec(0.0, 53.0);
        EndpointCheck check = LimitValidator.ValidateMove(
            DaytimeLimits(), PierSideLabel.East, landmark, PierSideLabel.West, landmark, -35.0, new[] { new HaDec(-3.0, 5.0) }, strictPath: false);

        Assert.True(check.SunSeparationDegrees > 55.0);
        Assert.Contains(check.Violations, v => v.Code == LimitViolationCode.SunTooCloseOnPath);
        Assert.True(check.PathSunSeparationDegrees < 5.0);
    }

    [Fact]
    public void OnStepXsRouteViaHomeIsSampled()
    {
        // OnStepX flips via the home position by default (MFLIP_SKIP_HOME OFF). Its second leg at
        // equal rates points at about (−3 h, 45°) where the direct paths are at 26° or 83°.
        AxisAngles from = GemGeometry.ToAxes(PierSideLabel.East, 0.0, -38.0, Latitude);
        AxisAngles to = GemGeometry.ToAxes(PierSideLabel.West, 0.0, -38.0, Latitude);
        var viaHomePoint = new HaDec(-3.0, 45.0);

        IReadOnlyList<HaDec> path = FlipPathSampler.Sample(from, to, strict: false, Latitude);
        double nearest = path.Min(p => Spherical.SeparationDegrees(p.HourAngle, p.Declination, viaHomePoint.HourAngle, viaHomePoint.Declination));

        Assert.True(nearest < 1.0);
        Assert.Contains(path, p => p.Declination > 89.5);
    }

    [Fact]
    public void LowLandmarksAtLowLatitudesAlsoCheckTheHorizonAvoidingWaypoint()
    {
        IReadOnlyList<HaDec> plain = FlipPathSampler.Sample(
            GemGeometry.ToAxes(PierSideLabel.East, 0.0, -70.0, 20.0),
            GemGeometry.ToAxes(PierSideLabel.West, 0.0, -70.0, 20.0),
            strict: false,
            20.0,
            startAltitudeDegrees: 60.0);
        IReadOnlyList<HaDec> low = FlipPathSampler.Sample(
            GemGeometry.ToAxes(PierSideLabel.East, 0.0, -70.0, 20.0),
            GemGeometry.ToAxes(PierSideLabel.West, 0.0, -70.0, 20.0),
            strict: false,
            20.0,
            startAltitudeDegrees: 0.0);

        Assert.True(low.Count > plain.Count);
    }

    private static HaDec Sun(DateTime utc)
    {
        (double ha, double dec) = SunEphemeris.HourAngleDeclination(utc, Longitude);
        return new HaDec(ha, dec);
    }
}

public class SunWindowTests
{
    [Fact]
    public void FindsTheFirstAllowedInstantThatHoldsForTheMove()
    {
        DateTime start = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime opens = start.AddMinutes(37);

        DateTime? next = SunWindow.NextAllowed(t => t >= opens, start, TimeSpan.FromHours(12), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.Equal(start.AddMinutes(40), next);
    }

    [Fact]
    public void ReturnsNullWhenNothingIsAllowedWithinTheHorizon()
    {
        DateTime start = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(SunWindow.NextAllowed(_ => false, start, TimeSpan.FromHours(1), TimeSpan.FromMinutes(5), TimeSpan.Zero));
    }

    [Fact]
    public void AnInstantWhoseMoveWouldRunIntoTheSunIsSkipped()
    {
        DateTime start = new(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        DateTime closes = start.AddMinutes(8);
        DateTime reopens = start.AddMinutes(30);

        DateTime? next = SunWindow.NextAllowed(t => t < closes || t >= reopens, start, TimeSpan.FromHours(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));

        Assert.Equal(reopens, next);
    }
}
