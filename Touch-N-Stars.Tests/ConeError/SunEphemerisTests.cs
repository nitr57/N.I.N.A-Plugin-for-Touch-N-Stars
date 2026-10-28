using TouchNStars.ConeError.Domain;
using Xunit;

namespace TouchNStars.Tests.ConeError;

/// <summary>Reference values from J. Meeus, Astronomical Algorithms, 2nd ed., examples 12.a, 12.b and 25.a.</summary>
public class SunEphemerisTests
{
    [Fact]
    public void JulianDateOfTheMeeusExample()
    {
        Assert.Equal(2448908.5, SunEphemeris.JulianDate(new DateTime(1992, 10, 13, 0, 0, 0, DateTimeKind.Utc)), 1e-6);
    }

    [Fact]
    public void Example25a_SunOn1992October13()
    {
        // α = 13h13m31.4s = 198.38083°, δ = −7°47′06″ = −7.78507°.
        SunPosition sun = SunEphemeris.Position(2448908.5);

        Assert.Equal(198.38083, sun.RightAscensionHours * 15.0, 0.01);
        Assert.Equal(-7.78507, sun.DeclinationDegrees, 0.01);
    }

    [Fact]
    public void Example12a_MeanSiderealTimeAt0hUt()
    {
        // 1987 April 10, 0h UT: θ0 = 13h10m46.3668s.
        double gmst = SunEphemeris.GreenwichMeanSiderealTimeHours(new DateTime(1987, 4, 10, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(13.0 + 10.0 / 60.0 + 46.3668 / 3600.0, gmst, 1e-6);
    }

    [Fact]
    public void Example12b_MeanSiderealTimeAtAnyInstant()
    {
        // 1987 April 10, 19h21m00s UT: θ0 = 8h34m57.0896s.
        double gmst = SunEphemeris.GreenwichMeanSiderealTimeHours(new DateTime(1987, 4, 10, 19, 21, 0, DateTimeKind.Utc));

        Assert.Equal(8.0 + 34.0 / 60.0 + 57.0896 / 3600.0, gmst, 1e-5);
    }

    [Fact]
    public void JuneSolstice2026()
    {
        SunPosition sun = SunEphemeris.Position(new DateTime(2026, 6, 21, 8, 24, 0, DateTimeKind.Utc));

        Assert.Equal(23.436, sun.DeclinationDegrees, 0.05);
        Assert.Equal(6.0, sun.RightAscensionHours, 0.01);
    }

    [Fact]
    public void MarchEquinox2026()
    {
        SunPosition sun = SunEphemeris.Position(new DateTime(2026, 3, 20, 14, 46, 0, DateTimeKind.Utc));

        Assert.Equal(0.0, sun.DeclinationDegrees, 0.05);
    }

    [Theory]
    [InlineData(11.0)]
    [InlineData(-71.0)]
    [InlineData(150.0)]
    public void SunIsNearTheMeridianAtLocalApparentNoon(double longitude)
    {
        // Local noon is near 12:00 − longitude/15 UT; the equation of time is at most about 16 minutes.
        DateTime noon = new DateTime(2026, 4, 15, 12, 0, 0, DateTimeKind.Utc).AddHours(-longitude / 15.0);

        (double ha, _) = SunEphemeris.HourAngleDeclination(noon, longitude);

        Assert.InRange(ha, -0.3, 0.3);
    }
}
