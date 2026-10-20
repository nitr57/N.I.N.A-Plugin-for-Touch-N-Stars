using System;
using System.Collections.Generic;

namespace TouchNStars.ConeError.Domain;

/// <summary>
/// Mechanical axis angles of a German equatorial mount, in degrees. <see cref="Ra"/> turns the Dec axis
/// about the polar axis; <see cref="Dec"/> turns the tube about the Dec axis. On pierEast a pointing
/// (h, δ) has the axes (15·h, δ). On pierWest the tube is through the visible pole: (15·h + 180,
/// 180 − δ) in the northern hemisphere, (15·h + 180, −180 − δ) in the southern one, so that a flip
/// swings over the pole the mount actually points at (as OnStepX's Transform::mountToInstrument does).
/// </summary>
public readonly record struct AxisAngles(double Ra, double Dec);

/// <summary>A sky position along a move, as hour angle (hours) and declination (degrees).</summary>
public readonly record struct HaDec(double HourAngle, double Declination);

/// <summary>
/// The ideal (error-free) kinematics of a German equatorial mount in the frame
/// X = (HA 0, Dec 0), Y = east, Z = north celestial pole. The Dec axis unit vector (from the mount
/// toward the tube) is u = (sin A, cos A, 0) and the optical axis (cos B cos A, −cos B sin A, sin B).
/// With these, pierEast (u = (sin h, cos h, 0)) is the counterweight-down side for hour angles
/// 0..12 h in both hemispheres, which is NINA's <c>MeridianFlip.ExpectedPierSide</c>.
/// </summary>
public static class GemGeometry
{
    public static AxisAngles ToAxes(PierSideLabel side, double hourAngleHours, double declinationDegrees, double latitudeDegrees)
    {
        double a = Spherical.WrapHours12(hourAngleHours) * 15.0;
        if (side == PierSideLabel.East)
        {
            return new AxisAngles(a, declinationDegrees);
        }

        return latitudeDegrees >= 0.0
            ? new AxisAngles(a + 180.0, 180.0 - declinationDegrees)
            : new AxisAngles(a + 180.0, -180.0 - declinationDegrees);
    }

    /// <summary>The home (park) position: tube at the visible pole, counterweight down.</summary>
    public static AxisAngles Home(double latitudeDegrees) => new(90.0, latitudeDegrees >= 0.0 ? 90.0 : -90.0);

    /// <summary>Whether axis angles are the beyond-the-pole form (pierWest).</summary>
    public static bool BeyondPole(AxisAngles axes) => Math.Abs(Spherical.WrapDegrees180(axes.Dec)) > 90.0;

    /// <summary>Unit vector of the ideal optical axis for axis angles.</summary>
    public static Vector3 OpticalAxis(AxisAngles axes)
    {
        double a = Spherical.DegToRad(axes.Ra);
        double b = Spherical.DegToRad(axes.Dec);
        return new Vector3(Math.Cos(b) * Math.Cos(a), -Math.Cos(b) * Math.Sin(a), Math.Sin(b));
    }

    /// <summary>Unit vector of the Dec axis, pointing from the mount toward the tube.</summary>
    public static Vector3 DecAxis(AxisAngles axes)
    {
        double a = Spherical.DegToRad(axes.Ra);
        return new Vector3(Math.Sin(a), Math.Cos(a), 0.0);
    }

    public static HaDec Pointing(AxisAngles axes)
    {
        (double ha, double dec) = Spherical.FromVector(OpticalAxis(axes));
        return new HaDec(ha, dec);
    }
}

/// <summary>
/// Sky positions along the possible paths of a move between two axis states, for the sun check of
/// a move. Mounts do not report their path, so several plausible ones are checked and the move is
/// refused if any of them comes too close to the Sun:
/// <list type="bullet">
/// <item>both axes in proportion (straight in axis space);</item>
/// <item>both axes at the same angular rate, the shorter one stopping first;</item>
/// <item>for RA travel of 135° or more (a flip), both of the above also the other way round the RA axis;</item>
/// <item>for a pier side change, the same via the home position, which OnStepX visits by default
/// (MFLIP_SKIP_HOME OFF, Goto.cpp), and via its horizon-avoiding waypoints (Goto::waypoint): 60° or
/// 120° of the RA axis below 10° altitude more than 6 h from the meridian, else 45° or 135° below
/// 20° altitude at latitudes under 45°;</item>
/// <item>with <c>strict</c>, also RA first then Dec, and Dec first then RA.</item>
/// </list>
/// </summary>
public static class FlipPathSampler
{
    public const double DefaultStepDegrees = 0.5;

    /// <summary>
    /// RA travel from which the long way round the RA axis is also considered: a flip (about 180°),
    /// whose direction the mount does not tell. A shorter leg, such as the 90° to the home position,
    /// takes the short way.
    /// </summary>
    public const double BothDirectionsFromDegrees = 135.0;

    public static IReadOnlyList<HaDec> Sample(
        AxisAngles from,
        AxisAngles to,
        bool strict,
        double latitudeDegrees,
        double startAltitudeDegrees = 90.0,
        double stepDegrees = DefaultStepDegrees)
    {
        if (stepDegrees <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(stepDegrees));
        }

        var points = new List<HaDec>();
        AddLeg(points, from, to, strict, stepDegrees);

        if (GemGeometry.BeyondPole(from) != GemGeometry.BeyondPole(to))
        {
            AxisAngles home = GemGeometry.Home(latitudeDegrees);
            AddLeg(points, from, home, strict, stepDegrees);
            AddLeg(points, home, to, strict, stepDegrees);

            var avoidRa = new List<double>();
            if (startAltitudeDegrees < 10.0 && Math.Abs(GemGeometry.Pointing(from).HourAngle) > 6.0)
            {
                avoidRa.AddRange(new[] { 60.0, 120.0 });
            }
            else if (startAltitudeDegrees < 20.0 && Math.Abs(latitudeDegrees) < 45.0)
            {
                avoidRa.AddRange(new[] { 45.0, 135.0 });
            }

            foreach (double ra in avoidRa)
            {
                var avoid = new AxisAngles(ra, home.Dec);
                AddLeg(points, from, avoid, strict, stepDegrees);
                AddLeg(points, avoid, home, strict, stepDegrees);
            }
        }

        return points;
    }

    private static void AddLeg(List<HaDec> points, AxisAngles from, AxisAngles to, bool strict, double stepDegrees)
    {
        double dRa = Spherical.WrapDegrees180(to.Ra - from.Ra);
        double dDec = to.Dec - from.Dec;

        var raTravels = new List<double> { dRa };
        if (Math.Abs(dRa) >= BothDirectionsFromDegrees)
        {
            raTravels.Add(dRa > 0.0 ? dRa - 360.0 : dRa + 360.0);
        }

        foreach (double ra in raTravels)
        {
            AddProportional(points, from, ra, dDec, stepDegrees);
            AddEqualRate(points, from, ra, dDec, stepDegrees);
            if (strict)
            {
                AddRaFirst(points, from, ra, dDec, stepDegrees);
                AddDecFirst(points, from, ra, dDec, stepDegrees);
            }
        }
    }

    private static int Steps(double travel, double stepDegrees) => Math.Max(1, (int)Math.Ceiling(Math.Abs(travel) / stepDegrees));

    private static void AddProportional(List<HaDec> points, AxisAngles from, double dRa, double dDec, double step)
    {
        int n = Steps(Math.Max(Math.Abs(dRa), Math.Abs(dDec)), step);
        for (int i = 0; i <= n; i++)
        {
            double s = (double)i / n;
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra + s * dRa, from.Dec + s * dDec)));
        }
    }

    private static void AddEqualRate(List<HaDec> points, AxisAngles from, double dRa, double dDec, double step)
    {
        double longest = Math.Max(Math.Abs(dRa), Math.Abs(dDec));
        int n = Steps(longest, step);
        for (int i = 0; i <= n; i++)
        {
            double travelled = longest * i / n;
            double ra = Math.Sign(dRa) * Math.Min(travelled, Math.Abs(dRa));
            double dec = Math.Sign(dDec) * Math.Min(travelled, Math.Abs(dDec));
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra + ra, from.Dec + dec)));
        }
    }

    private static void AddRaFirst(List<HaDec> points, AxisAngles from, double dRa, double dDec, double step)
    {
        int nRa = Steps(dRa, step);
        for (int i = 0; i <= nRa; i++)
        {
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra + dRa * i / nRa, from.Dec)));
        }

        int nDec = Steps(dDec, step);
        for (int i = 0; i <= nDec; i++)
        {
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra + dRa, from.Dec + dDec * i / nDec)));
        }
    }

    private static void AddDecFirst(List<HaDec> points, AxisAngles from, double dRa, double dDec, double step)
    {
        int nDec = Steps(dDec, step);
        for (int i = 0; i <= nDec; i++)
        {
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra, from.Dec + dDec * i / nDec)));
        }

        int nRa = Steps(dRa, step);
        for (int i = 0; i <= nRa; i++)
        {
            points.Add(GemGeometry.Pointing(new AxisAngles(from.Ra + dRa * i / nRa, from.Dec + dDec)));
        }
    }
}
