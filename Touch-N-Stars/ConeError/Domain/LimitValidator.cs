using System;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.ConeError.Domain;

/// <summary>
/// The limits a move is checked against. Mount-reported values are null when the mount (driver)
/// does not report them; the stricter of the mount and PINS value applies where both exist.
/// </summary>
public sealed class LimitSet
{
    /// <summary>PINS minimum altitude of the measurement (plugin setting), degrees.</summary>
    public double MinAltitudeDegrees { get; init; }

    /// <summary>The profile's custom horizon: altitude (degrees) at an azimuth (degrees), or null when none is set.</summary>
    public Func<double, double> HorizonAltitudeAt { get; init; }

    /// <summary>PINS past-meridian limit, degrees: the profile's MaxMinutesAfterMeridian · 0.25°.</summary>
    public double PinsPastMeridianDegrees { get; init; }

    public double? MountHorizonDegrees { get; init; }

    public double? MountOverheadDegrees { get; init; }

    /// <summary>How far past the meridian the mount may point on pierEast, degrees.</summary>
    public double? MountPastMeridianEastDegrees { get; init; }

    /// <summary>How far past the meridian the mount may point on pierWest, degrees.</summary>
    public double? MountPastMeridianWestDegrees { get; init; }

    public double SunExclusionDegrees { get; init; } = 30.0;

    /// <summary>Where the mount values come from, e.g. "OnStepX", "INDI OnStep", or null when the mount reports none.</summary>
    public string MountSource { get; init; }

    public double EffectivePastMeridianDegrees(PierSideLabel side)
    {
        double? mount = side == PierSideLabel.East ? MountPastMeridianEastDegrees : MountPastMeridianWestDegrees;
        return mount.HasValue ? Math.Min(mount.Value, PinsPastMeridianDegrees) : PinsPastMeridianDegrees;
    }

    public string PastMeridianSource(PierSideLabel side)
    {
        double? mount = side == PierSideLabel.East ? MountPastMeridianEastDegrees : MountPastMeridianWestDegrees;
        return mount.HasValue && mount.Value < PinsPastMeridianDegrees ? MountSource ?? "mount" : "PINS";
    }

    /// <summary>The lowest allowed altitude at an azimuth and which limit sets it.</summary>
    public (double Altitude, string Source) EffectiveMinAltitude(double azimuthDegrees)
    {
        double altitude = MinAltitudeDegrees;
        string source = "PINS minimum altitude";

        if (HorizonAltitudeAt != null)
        {
            double horizon = HorizonAltitudeAt(azimuthDegrees);
            if (!double.IsNaN(horizon) && horizon > altitude)
            {
                altitude = horizon;
                source = "PINS custom horizon";
            }
        }

        if (MountHorizonDegrees.HasValue && MountHorizonDegrees.Value > altitude)
        {
            altitude = MountHorizonDegrees.Value;
            source = (MountSource ?? "mount") + " horizon limit";
        }

        return (altitude, source);
    }
}

public enum LimitViolationCode
{
    BelowHorizon,
    AboveOverhead,
    PastMeridianLimit,
    SunTooClose,
    SunTooCloseOnPath
}

/// <summary>A refused check with the values behind it, for the UI.</summary>
public sealed record LimitViolation(LimitViolationCode Code, string Message, double Value, double Limit, string Source);

/// <summary>The checks of one move endpoint on one pier side.</summary>
public sealed class EndpointCheck
{
    public PierSideLabel Side { get; init; }
    public double HourAngle { get; init; }
    public double Declination { get; init; }
    public double Altitude { get; init; }
    public double Azimuth { get; init; }
    public double PastMeridianDegrees { get; init; }
    public double PastMeridianLimitDegrees { get; init; }
    public double MinAltitudeDegrees { get; init; }

    /// <summary>Smallest separation from the Sun at this endpoint over all checked times.</summary>
    public double SunSeparationDegrees { get; init; }

    /// <summary>Smallest separation from the Sun along the move to this endpoint, or NaN when no path was checked.</summary>
    public double PathSunSeparationDegrees { get; init; } = double.NaN;

    public IReadOnlyList<LimitViolation> Violations { get; init; } = Array.Empty<LimitViolation>();

    public bool Ok => Violations.Count == 0;
}

/// <summary>
/// Validates move endpoints and paths against the limits and the sun exclusion radius. The Sun is
/// given as one or more (hour angle, declination) positions, e.g. now and at the end of the move's
/// time budget, in the same hour-angle frame as the pointing.
/// </summary>
public static class LimitValidator
{
    public static EndpointCheck ValidateEndpoint(
        LimitSet limits,
        PierSideLabel side,
        double hourAngleHours,
        double declinationDegrees,
        double latitudeDegrees,
        IReadOnlyList<HaDec> suns)
    {
        return Validate(limits, side, hourAngleHours, declinationDegrees, latitudeDegrees, suns, null);
    }

    /// <summary>Validates the endpoint of a move and every sample of its possible paths.</summary>
    public static EndpointCheck ValidateMove(
        LimitSet limits,
        PierSideLabel fromSide,
        HaDec from,
        PierSideLabel toSide,
        HaDec to,
        double latitudeDegrees,
        IReadOnlyList<HaDec> suns,
        bool strictPath)
    {
        IReadOnlyList<HaDec> path = FlipPathSampler.Sample(
            GemGeometry.ToAxes(fromSide, from.HourAngle, from.Declination),
            GemGeometry.ToAxes(toSide, to.HourAngle, to.Declination),
            strictPath);

        return Validate(limits, toSide, to.HourAngle, to.Declination, latitudeDegrees, suns, path);
    }

    private static EndpointCheck Validate(
        LimitSet limits,
        PierSideLabel side,
        double hourAngleHours,
        double declinationDegrees,
        double latitudeDegrees,
        IReadOnlyList<HaDec> suns,
        IReadOnlyList<HaDec> path)
    {
        if (limits == null)
        {
            throw new ArgumentNullException(nameof(limits));
        }

        if (suns == null || suns.Count == 0)
        {
            throw new ArgumentException("At least one sun position is required.", nameof(suns));
        }

        var violations = new List<LimitViolation>();
        (double altitude, double azimuth) = Spherical.AltAz(hourAngleHours, declinationDegrees, latitudeDegrees);

        (double minAltitude, string minSource) = limits.EffectiveMinAltitude(azimuth);
        if (altitude < minAltitude)
        {
            violations.Add(new LimitViolation(
                LimitViolationCode.BelowHorizon,
                $"Altitude {altitude:F1}° is below the {minSource} of {minAltitude:F1}°.",
                altitude,
                minAltitude,
                minSource));
        }

        if (limits.MountOverheadDegrees.HasValue && altitude > limits.MountOverheadDegrees.Value)
        {
            violations.Add(new LimitViolation(
                LimitViolationCode.AboveOverhead,
                $"Altitude {altitude:F1}° is above the {limits.MountSource ?? "mount"} overhead limit of {limits.MountOverheadDegrees.Value:F1}°.",
                altitude,
                limits.MountOverheadDegrees.Value,
                limits.MountSource ?? "mount"));
        }

        double past = ConeMath.PastMeridianDegrees(side, hourAngleHours);
        double pastLimit = limits.EffectivePastMeridianDegrees(side);
        if (past > pastLimit)
        {
            string source = limits.PastMeridianSource(side);
            violations.Add(new LimitViolation(
                LimitViolationCode.PastMeridianLimit,
                $"On the {side.ToString().ToLowerInvariant()} pier side this position is {past:F1}° past the meridian; the {source} limit is {pastLimit:F1}°.",
                past,
                pastLimit,
                source));
        }

        double endpointSun = MinSunSeparation(new[] { new HaDec(hourAngleHours, declinationDegrees) }, suns);
        if (endpointSun < limits.SunExclusionDegrees)
        {
            violations.Add(new LimitViolation(
                LimitViolationCode.SunTooClose,
                $"The position is {endpointSun:F1}° from the Sun; the exclusion radius is {limits.SunExclusionDegrees:F0}°.",
                endpointSun,
                limits.SunExclusionDegrees,
                "PINS sun exclusion"));
        }

        double pathSun = double.NaN;
        if (path != null)
        {
            pathSun = MinSunSeparation(path, suns);
            if (pathSun < limits.SunExclusionDegrees && endpointSun >= limits.SunExclusionDegrees)
            {
                violations.Add(new LimitViolation(
                    LimitViolationCode.SunTooCloseOnPath,
                    $"The move may pass {pathSun:F1}° from the Sun on its way; the exclusion radius is {limits.SunExclusionDegrees:F0}°.",
                    pathSun,
                    limits.SunExclusionDegrees,
                    "PINS sun exclusion"));
            }
        }

        return new EndpointCheck
        {
            Side = side,
            HourAngle = Spherical.WrapHours12(hourAngleHours),
            Declination = declinationDegrees,
            Altitude = altitude,
            Azimuth = azimuth,
            PastMeridianDegrees = past,
            PastMeridianLimitDegrees = pastLimit,
            MinAltitudeDegrees = minAltitude,
            SunSeparationDegrees = endpointSun,
            PathSunSeparationDegrees = pathSun,
            Violations = violations
        };
    }

    public static double MinSunSeparation(IReadOnlyList<HaDec> points, IReadOnlyList<HaDec> suns)
    {
        double min = double.PositiveInfinity;
        foreach (HaDec sun in suns)
        {
            foreach (HaDec p in points)
            {
                min = Math.Min(min, Spherical.SeparationDegrees(p.HourAngle, p.Declination, sun.HourAngle, sun.Declination));
            }
        }

        return min;
    }

    /// <summary>Whether the same pointing is allowed on both pier sides, with the checks of each.</summary>
    public static (EndpointCheck East, EndpointCheck West) ValidateBothSides(
        LimitSet limits,
        double hourAngleHours,
        double declinationDegrees,
        double latitudeDegrees,
        IReadOnlyList<HaDec> suns)
    {
        return (
            ValidateEndpoint(limits, PierSideLabel.East, hourAngleHours, declinationDegrees, latitudeDegrees, suns),
            ValidateEndpoint(limits, PierSideLabel.West, hourAngleHours, declinationDegrees, latitudeDegrees, suns));
    }

    public static string Describe(IEnumerable<LimitViolation> violations) => string.Join(" ", violations.Select(v => v.Message));
}
