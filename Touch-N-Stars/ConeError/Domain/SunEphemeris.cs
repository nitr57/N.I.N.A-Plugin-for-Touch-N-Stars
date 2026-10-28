using System;

namespace TouchNStars.ConeError.Domain;

/// <summary>Apparent geocentric equatorial position of the Sun of date (JNow).</summary>
public readonly record struct SunPosition(double RightAscensionHours, double DeclinationDegrees);

/// <summary>
/// Sun position and sidereal time for the sun safety checks, after J. Meeus, Astronomical
/// Algorithms, 2nd ed. (1998): chapter 25 "Solar Coordinates", low-accuracy method (about 0.01°),
/// and chapter 12 "Sidereal Time", formula 12.4.
/// <para>
/// Pure managed code on purpose: the safety check must never depend on a native library that might
/// be missing and silently return a Sun at (0, 0). The accuracy is far better than the 15°+
/// exclusion radius needs; the difference between UT and TD (about 70 s) and the Sun's 8.8″
/// parallax are neglected.
/// </para>
/// </summary>
public static class SunEphemeris
{
    private const double J2000 = 2451545.0;

    /// <summary>Julian date of a UTC instant.</summary>
    public static double JulianDate(DateTime utc)
    {
        DateTime u = utc.Kind == DateTimeKind.Local ? utc.ToUniversalTime() : utc;
        // OLE automation date 0 is 1899-12-30 00:00 = JD 2415018.5.
        return u.ToOADate() + 2415018.5;
    }

    /// <summary>Apparent right ascension and declination of the Sun (Meeus 25.2 to 25.8).</summary>
    public static SunPosition Position(DateTime utc) => Position(JulianDate(utc));

    public static SunPosition Position(double julianDate)
    {
        double t = (julianDate - J2000) / 36525.0;

        double l0 = 280.46646 + 36000.76983 * t + 0.0003032 * t * t;
        double m = 357.52911 + 35999.05029 * t - 0.0001537 * t * t;
        double mRad = Spherical.DegToRad(m);

        double center = (1.914602 - 0.004817 * t - 0.000014 * t * t) * Math.Sin(mRad)
            + (0.019993 - 0.000101 * t) * Math.Sin(2.0 * mRad)
            + 0.000289 * Math.Sin(3.0 * mRad);

        double trueLongitude = l0 + center;
        double omega = Spherical.DegToRad(125.04 - 1934.136 * t);
        double apparentLongitude = Spherical.DegToRad(trueLongitude - 0.00569 - 0.00478 * Math.Sin(omega));

        double meanObliquity = 23.0 + 26.0 / 60.0 + 21.448 / 3600.0
            - (46.8150 * t + 0.00059 * t * t - 0.001813 * t * t * t) / 3600.0;
        double obliquity = Spherical.DegToRad(meanObliquity + 0.00256 * Math.Cos(omega));

        double ra = Math.Atan2(Math.Cos(obliquity) * Math.Sin(apparentLongitude), Math.Cos(apparentLongitude));
        double dec = Math.Asin(Math.Sin(obliquity) * Math.Sin(apparentLongitude));

        return new SunPosition(Spherical.WrapHours24(Spherical.RadToDeg(ra) / 15.0), Spherical.RadToDeg(dec));
    }

    /// <summary>Mean sidereal time at Greenwich in hours (Meeus 12.4).</summary>
    public static double GreenwichMeanSiderealTimeHours(DateTime utc)
    {
        double jd = JulianDate(utc);
        double t = (jd - J2000) / 36525.0;
        double theta = 280.46061837 + 360.98564736629 * (jd - J2000) + 0.000387933 * t * t - t * t * t / 38710000.0;
        return Spherical.WrapDegrees360(theta) / 15.0;
    }

    /// <summary>Local mean sidereal time in hours; longitude in degrees, east positive.</summary>
    public static double LocalSiderealTimeHours(DateTime utc, double longitudeDegrees)
        => Spherical.WrapHours24(GreenwichMeanSiderealTimeHours(utc) + longitudeDegrees / 15.0);

    /// <summary>The Sun's hour angle (hours) and declination (degrees) for an observer at a longitude.</summary>
    public static (double HourAngle, double Declination) HourAngleDeclination(DateTime utc, double longitudeDegrees)
    {
        SunPosition sun = Position(utc);
        double lst = LocalSiderealTimeHours(utc, longitudeDegrees);
        return (Spherical.HourAngle(lst, sun.RightAscensionHours), sun.DeclinationDegrees);
    }
}
