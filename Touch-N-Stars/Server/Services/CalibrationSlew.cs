using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;

namespace TouchNStars.Server.Services;

/// <summary>Side of the meridian to calibrate on. East and West are sides of the sky, as in PHD2's Calibration Assistant.</summary>
public enum CalibrationSlewSide
{
    /// <summary>The side the mount is on now: no meridian flip.</summary>
    Stay,
    /// <summary>Point east of the meridian (negative hour angle).</summary>
    East,
    /// <summary>Point west of the meridian (positive hour angle).</summary>
    West,
}

/// <summary>
/// The slew to a guider calibration position before a calibration: like pins' "Slew To Guider Calibration Position"
/// and PHD2's Calibration Assistant, 5 degrees from the meridian on the celestial equator, approached from 1 degree south so that
/// the Dec backlash is taken up. Unlike the sequence item, Stay keeps the mount's side of pier (a mount tracking past
/// the meridian does not flip) and a slew that fails throws, so that no calibration runs where the mount stopped.
/// </summary>
public static class CalibrationSlew
{
    public const double HourAngleOffsetDegrees = 5.0;
    public const double TargetDeclination = 0.0;
    private const double BacklashApproachDegrees = 1.0;

    /// <summary>The lowest altitude for the calibration position without a higher custom horizon, as the sequence item.</summary>
    public const double MinAltitudeDegrees = 10.0;

    /// <summary>The guider start and the Guiding Coach start themselves; what else holds the mount or the guide camera counts.</summary>
    private static readonly string[] OwnActivities = { ActivityNames.GuidingStart, ActivityNames.GuidingCoach };

    /// <summary>Parses stay, east or west (any case).</summary>
    public static bool TryParseSide(string value, out CalibrationSlewSide side)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "stay": side = CalibrationSlewSide.Stay; return true;
            case "east": side = CalibrationSlewSide.East; return true;
            case "west": side = CalibrationSlewSide.West; return true;
            default: side = CalibrationSlewSide.Stay; return false;
        }
    }

    /// <summary>Why the mount cannot slew to the calibration position now, or null when it can.</summary>
    public static string Blocker(TelescopeInfo info)
    {
        if (info == null || !info.Connected) return "Connect the mount to slew to the calibration position.";
        if (info.AtPark) return "The mount is parked. Unpark it to slew to the calibration position.";
        return null;
    }

    /// <summary>True when the calibration position on <paramref name="side"/> is east of the meridian.</summary>
    public static bool PointsEast(CalibrationSlewSide side, TelescopeInfo info)
    {
        switch (side)
        {
            case CalibrationSlewSide.East: return true;
            case CalibrationSlewSide.West: return false;
        }

        // Counterweight down, a telescope west of the pier points east and one east of the pier points west
        // (see PierSide). Going there keeps the side of pier, also when the mount tracked past the meridian.
        if (info.SideOfPier == PierSide.pierWest) return true;
        if (info.SideOfPier == PierSide.pierEast) return false;

        // Side of pier unknown: the side of the sky the telescope points at now.
        return HourAngleHours(info.SiderealTime, info.RightAscension) <= 0.0;
    }

    /// <summary>Hour angle in hours, -12..12.</summary>
    public static double HourAngleHours(double siderealTimeHours, double rightAscensionHours)
        => AstroUtil.EuclidianModulus(siderealTimeHours - rightAscensionHours + 12.0, 24.0) - 12.0;

    /// <summary>The calibration position (JNow) at the given sidereal time.</summary>
    public static Coordinates Target(double siderealTimeHours, bool pointEast, double declination = TargetDeclination)
    {
        // Hour angle = LST - RA: east of the meridian the RA is ahead of the sidereal time.
        double offsetHours = HourAngleOffsetDegrees / 15.0;
        double ra = AstroUtil.EuclidianModulus(siderealTimeHours + (pointEast ? offsetHours : -offsetHours), 24.0);
        return new Coordinates(ra, declination, Epoch.JNOW, Coordinates.RAType.Hours);
    }

    /// <summary>
    /// Why the slew to the calibration position on <paramref name="side"/> must not start now, besides <see cref="Blocker"/>:
    /// the mount moves (a slew, park or home started elsewhere), another job holds the mount or the guide camera (the
    /// sequence, a cone error measurement, the end-of-night shutdown, ...), or the position or its backlash approach point
    /// 1 degree below is lower than <see cref="MinAltitudeDegrees"/> or the profile's custom horizon at its azimuth (the
    /// rule of pins' "Slew To Guider Calibration Position"; at a fixed hour angle the altitude does not depend on the time).
    /// Null when it may start; an unknown site skips the altitude check.
    /// </summary>
    public static string StartProblem(TelescopeInfo info, CalibrationSlewSide side)
    {
        if (info == null || !info.Connected)
        {
            return null;
        }

        if (info.Slewing)
        {
            return "The mount is moving (a slew, park or home started elsewhere). Wait until it has stopped.";
        }

        string busy = ImagingActivity.BusyReason(RigDevices.Mount | RigDevices.GuideCamera, OwnActivities);
        if (busy != null)
        {
            return busy;
        }

        double latitude = info.SiteLatitude;
        if (!double.IsFinite(latitude))
        {
            return null;
        }

        bool east = PointsEast(side, info);

        // East of the meridian the hour angle is negative.
        double hourAngle = east ? -HourAngleOffsetDegrees : HourAngleOffsetDegrees;
        foreach (double declination in new[] { TargetDeclination, TargetDeclination - BacklashApproachDegrees })
        {
            double altitude = AstroUtil.GetAltitude(hourAngle, latitude, declination);
            double limit = MinAltitudeDegrees;
            double horizonAltitude = HorizonAltitude(AstroUtil.GetAzimuth(hourAngle, altitude, latitude, declination));
            if (double.IsFinite(horizonAltitude))
            {
                limit = Math.Max(limit, horizonAltitude);
            }

            if (!(altitude >= limit))
            {
                return $"The calibration position {(east ? "east" : "west")} of the meridian at Dec {declination:F1} deg is {altitude:F0} deg high, below the limit of {limit:F0} deg (the profile's horizon or {MinAltitudeDegrees:F0} deg).";
            }
        }

        return null;
    }

    /// <summary>The profile's custom horizon at <paramref name="azimuth"/>, or NaN without one or when it can't be read.</summary>
    private static double HorizonAltitude(double azimuth)
    {
        try
        {
            return ProfileHorizon()?.GetAltitude(azimuth) ?? double.NaN;
        }
        catch (Exception ex)
        {
            Logger.Warning($"InternalGuider: reading the profile's horizon failed: {ex.Message}");
            return double.NaN;
        }
    }

    // Its own method: the profile types load only when it runs (inside the catch above), not with the caller.
    private static CustomHorizon ProfileHorizon() => TouchNStars.Mediators?.Profile?.ActiveProfile?.AstrometrySettings?.Horizon;

    /// <summary>Slews to the calibration position on <paramref name="side"/>; throws when the mount cannot or did not get there.</summary>
    /// <param name="notStarted">What did not start because of a failed slew, for the error message.</param>
    public static async Task SlewAsync(ITelescopeMediator telescope, CalibrationSlewSide side, CancellationToken ct,
        string notStarted = "no calibration was started")
    {
        TelescopeInfo info = telescope?.GetInfo();
        string blocker = Blocker(info);
        if (blocker != null) throw new InvalidOperationException(blocker);

        // Checked when the request came and again now: the slew can start a while later (the guider stopped first).
        string problem = StartProblem(info, side);
        if (problem != null) throw new InvalidOperationException(problem);

        bool pointEast = PointsEast(side, info);
        Coordinates approach = Target(info.SiderealTime, pointEast, TargetDeclination - BacklashApproachDegrees);
        Coordinates target = Target(info.SiderealTime, pointEast);
        Logger.Info($"InternalGuider: slewing to the calibration position, side {side} (pier {info.SideOfPier}) -> point {(pointEast ? "east" : "west")}, RA {target.RA:F4}h Dec {target.Dec:F1}deg");

        try
        {
            foreach (Coordinates coordinates in new[] { approach, target })
            {
                if (!await telescope.SlewToCoordinatesAsync(coordinates, ct).ConfigureAwait(false))
                {
                    throw new InvalidOperationException($"The mount did not reach the calibration position, so {notStarted}. See the N.I.N.A. log for the reason.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            try { telescope.StopSlew(); } catch (Exception ex) { Logger.Warning($"InternalGuider: stopping the calibration slew failed: {ex.Message}"); }
            throw;
        }
    }
}
