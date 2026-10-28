using System;

namespace TouchNStars.ConeError.Domain;

/// <summary>
/// Angle helpers for the cone error measurement. Hour angles and right ascensions are in hours,
/// declinations, altitudes and separations in degrees. No NINA types and no native code, so that
/// everything here runs in unit tests.
/// </summary>
public static class Spherical
{
    public const double SiderealPerSolar = 1.00273790935;

    public static double DegToRad(double degrees) => degrees * Math.PI / 180.0;

    public static double RadToDeg(double radians) => radians * 180.0 / Math.PI;

    /// <summary>Wraps hours into (-12, 12].</summary>
    public static double WrapHours12(double hours)
    {
        double wrapped = hours % 24.0;
        if (wrapped <= -12.0)
        {
            wrapped += 24.0;
        }
        else if (wrapped > 12.0)
        {
            wrapped -= 24.0;
        }

        return wrapped;
    }

    /// <summary>Wraps hours into [0, 24).</summary>
    public static double WrapHours24(double hours)
    {
        double wrapped = hours % 24.0;
        return wrapped < 0.0 ? wrapped + 24.0 : wrapped;
    }

    /// <summary>Wraps degrees into (-180, 180].</summary>
    public static double WrapDegrees180(double degrees)
    {
        double wrapped = degrees % 360.0;
        if (wrapped <= -180.0)
        {
            wrapped += 360.0;
        }
        else if (wrapped > 180.0)
        {
            wrapped -= 360.0;
        }

        return wrapped;
    }

    /// <summary>Wraps degrees into [0, 360).</summary>
    public static double WrapDegrees360(double degrees)
    {
        double wrapped = degrees % 360.0;
        return wrapped < 0.0 ? wrapped + 360.0 : wrapped;
    }

    /// <summary>Hour angle in (-12, 12] hours, positive west of the meridian.</summary>
    public static double HourAngle(double localSiderealTimeHours, double rightAscensionHours)
        => WrapHours12(localSiderealTimeHours - rightAscensionHours);

    /// <summary>Right ascension in [0, 24) hours for an hour angle at a local sidereal time.</summary>
    public static double RightAscension(double localSiderealTimeHours, double hourAngleHours)
        => WrapHours24(localSiderealTimeHours - hourAngleHours);

    /// <summary>
    /// Great-circle separation in degrees of two positions given as (longitude-like angle in hours,
    /// latitude-like angle in degrees). Works for RA/Dec and for HA/Dec pairs alike, as long as both
    /// positions use the same frame. Uses the Vincenty form, stable at all separations.
    /// </summary>
    public static double SeparationDegrees(double lon1Hours, double lat1Degrees, double lon2Hours, double lat2Degrees)
    {
        double dLon = DegToRad((lon2Hours - lon1Hours) * 15.0);
        double phi1 = DegToRad(lat1Degrees);
        double phi2 = DegToRad(lat2Degrees);

        double a = Math.Cos(phi2) * Math.Sin(dLon);
        double b = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLon);
        double c = Math.Sin(phi1) * Math.Sin(phi2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Cos(dLon);

        return RadToDeg(Math.Atan2(Math.Sqrt(a * a + b * b), c));
    }

    /// <summary>
    /// Altitude and azimuth (degrees, azimuth from north through east) of an hour angle and
    /// declination at a latitude. Geometric, without refraction.
    /// </summary>
    public static (double Altitude, double Azimuth) AltAz(double hourAngleHours, double declinationDegrees, double latitudeDegrees)
    {
        double h = DegToRad(hourAngleHours * 15.0);
        double dec = DegToRad(declinationDegrees);
        double lat = DegToRad(latitudeDegrees);

        double sinAlt = Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(h);
        double altitude = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0));

        double y = -Math.Cos(dec) * Math.Sin(h);
        double x = Math.Sin(dec) * Math.Cos(lat) - Math.Cos(dec) * Math.Cos(h) * Math.Sin(lat);
        double azimuth = WrapDegrees360(RadToDeg(Math.Atan2(y, x)));

        return (RadToDeg(altitude), azimuth);
    }

    /// <summary>Unit vector of (hour angle, declination) in the frame X = (HA 0, Dec 0), Y = east, Z = celestial pole.</summary>
    public static Vector3 ToVector(double hourAngleHours, double declinationDegrees)
    {
        double h = DegToRad(hourAngleHours * 15.0);
        double dec = DegToRad(declinationDegrees);
        return new Vector3(Math.Cos(dec) * Math.Cos(h), -Math.Cos(dec) * Math.Sin(h), Math.Sin(dec));
    }

    /// <summary>Hour angle (hours, (-12, 12]) and declination (degrees) of a vector in the frame of <see cref="ToVector"/>.</summary>
    public static (double HourAngle, double Declination) FromVector(Vector3 v)
    {
        Vector3 n = v.Normalized();
        double dec = Math.Asin(Math.Clamp(n.Z, -1.0, 1.0));
        double h = Math.Atan2(-n.Y, n.X);
        return (WrapHours12(RadToDeg(h) / 15.0), RadToDeg(dec));
    }
}

/// <summary>A small immutable 3-vector for the mount geometry.</summary>
public readonly record struct Vector3(double X, double Y, double Z)
{
    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static Vector3 operator *(double s, Vector3 v) => new(s * v.X, s * v.Y, s * v.Z);

    public double Dot(Vector3 other) => X * other.X + Y * other.Y + Z * other.Z;

    public Vector3 Cross(Vector3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public double Length => Math.Sqrt(Dot(this));

    public Vector3 Normalized()
    {
        double length = Length;
        return length == 0.0 ? this : (1.0 / length) * this;
    }
}
