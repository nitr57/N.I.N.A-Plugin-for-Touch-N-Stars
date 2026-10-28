using TouchNStars.ConeError.Domain;

namespace TouchNStars.Tests.ConeError;

/// <summary>
/// Test-only physical model of a German equatorial mount with a tilted tube. It deliberately
/// shares no code with the estimator (ConeMath) or with GemGeometry: the pier label of a mechanical
/// state is derived from the counterweight position (ASCOM: pierEast is the normal, counterweight-down
/// state for targets west of the meridian), and the expected shim end from the heights of the two
/// mounting points above the saddle. A sign error in the estimator therefore fails the tests.
/// <para>
/// Frame: X = (HA 0, Dec 0), Y = east, Z = celestial pole. Positions are what the mount believes
/// (its own frame); the polar misalignment rotates the mount frame against the sky.
/// </para>
/// </summary>
public sealed class ConeForwardModel
{
    public double LatitudeDegrees { get; init; } = 50.0;

    /// <summary>Physical tube tilt in arcmin: positive = the objective end tilts AWAY from the saddle.</summary>
    public double TiltAwayFromSaddleArcmin { get; init; }

    /// <summary>Non-perpendicularity of the Dec axis to the polar axis, arcmin.</summary>
    public double NonPerpendicularityArcmin { get; init; }

    /// <summary>Polar misalignment, arcmin: rotations of the mount frame about X and about Y.</summary>
    public double PolarAboutXArcmin { get; init; }

    public double PolarAboutYArcmin { get; init; }

    /// <summary>Encoder index errors (a sync offset), arcmin.</summary>
    public double IndexRaArcmin { get; init; }

    public double IndexDecArcmin { get; init; }

    /// <summary>A driver that reports the pier side the wrong way round.</summary>
    public bool InvertedLabels { get; init; }

    /// <summary>Distance of the optical axis from the RA axis along the Dec axis, mm (landmark parallax).</summary>
    public double AxisOffsetMm { get; init; }

    private static double Rad(double degrees) => degrees * Math.PI / 180.0;

    private static double ArcminRad(double arcmin) => Rad(arcmin / 60.0);

    public static Vector3 Direction(double hourAngleHours, double declinationDegrees)
    {
        double h = Rad(hourAngleHours * 15.0);
        double d = Rad(declinationDegrees);
        return new Vector3(Math.Cos(d) * Math.Cos(h), -Math.Cos(d) * Math.Sin(h), Math.Sin(d));
    }

    public static (double HourAngle, double Declination) Angles(Vector3 v)
    {
        Vector3 n = v.Normalized();
        double dec = Math.Asin(Math.Clamp(n.Z, -1.0, 1.0)) * 180.0 / Math.PI;
        double ha = Math.Atan2(-n.Y, n.X) * 180.0 / Math.PI / 15.0;
        return (ha, dec);
    }

    /// <summary>Rodrigues rotation of v about a unit axis k by an angle.</summary>
    public static Vector3 Rotate(Vector3 v, Vector3 k, double angleRad)
    {
        Vector3 axis = k.Normalized();
        double c = Math.Cos(angleRad);
        double s = Math.Sin(angleRad);
        return c * v + s * axis.Cross(v) + (1.0 - c) * axis.Dot(v) * axis;
    }

    private Vector3 Zenith
    {
        get
        {
            double phi = Rad(LatitudeDegrees);
            return new Vector3(Math.Cos(phi), 0.0, Math.Sin(phi));
        }
    }

    /// <summary>
    /// The pier label (as NINA reads it from the driver) of a mechanical state with Dec axis u while
    /// pointing at hour angle h: pierEast is counterweight-down for h in (0, 12 h) and counterweight-up
    /// for h in (-12, 0); pierWest the other way round.
    /// </summary>
    private PierSideLabel PhysicalLabel(Vector3 decAxis, double hourAngleHours)
    {
        bool counterweightDown = decAxis.Dot(Zenith) > 0.0;
        double h = ((hourAngleHours % 24.0) + 24.0) % 24.0;
        bool westOfMeridian = h > 0.0 && h < 12.0;
        PierSideLabel label = counterweightDown == westOfMeridian ? PierSideLabel.East : PierSideLabel.West;
        return InvertedLabels ? ConeMath.Other(label) : label;
    }

    /// <summary>The Dec axis the firmware uses for a target on the side the driver labels <paramref name="side"/>.</summary>
    private Vector3 DecAxisFor(PierSideLabel side, double hourAngleHours)
    {
        double h = Rad(hourAngleHours * 15.0);
        var candidate = new Vector3(Math.Sin(h), Math.Cos(h), 0.0);
        return PhysicalLabel(candidate, hourAngleHours) == side ? candidate : -1.0 * candidate;
    }

    /// <summary>The true sky direction of the optical axis and the Dec axis when the mount points (in its own frame) at a target.</summary>
    public (Vector3 Optical, Vector3 DecAxis) TruePointing(PierSideLabel side, double hourAngleHours, double declinationDegrees)
    {
        Vector3 ideal = Direction(hourAngleHours, declinationDegrees);
        Vector3 u = DecAxisFor(side, hourAngleHours);
        var z = new Vector3(0.0, 0.0, 1.0);

        // Index errors: Dec encoder about the Dec axis, RA encoder about the polar axis.
        Vector3 e1 = Rotate(ideal, u, ArcminRad(IndexDecArcmin));
        e1 = Rotate(e1, z, ArcminRad(IndexRaArcmin));
        u = Rotate(u, z, ArcminRad(IndexRaArcmin));

        // Non-perpendicularity: the Dec axis assembly tilted toward the pole.
        Vector3 w = z.Cross(u);
        if (w.Length > 1e-12 && NonPerpendicularityArcmin != 0.0)
        {
            e1 = Rotate(e1, w, ArcminRad(NonPerpendicularityArcmin));
            u = Rotate(u, w, ArcminRad(NonPerpendicularityArcmin));
        }

        double c = ArcminRad(TiltAwayFromSaddleArcmin);
        Vector3 optical = (Math.Cos(c) * e1 + Math.Sin(c) * u).Normalized();

        return (ToSky(optical), ToSky(u));
    }

    private Vector3 ToSky(Vector3 v)
    {
        Vector3 r = Rotate(v, new Vector3(1.0, 0.0, 0.0), ArcminRad(PolarAboutXArcmin));
        return Rotate(r, new Vector3(0.0, 1.0, 0.0), ArcminRad(PolarAboutYArcmin));
    }

    private Vector3 FromSky(Vector3 v)
    {
        Vector3 r = Rotate(v, new Vector3(0.0, 1.0, 0.0), -ArcminRad(PolarAboutYArcmin));
        return Rotate(r, new Vector3(1.0, 0.0, 0.0), -ArcminRad(PolarAboutXArcmin));
    }

    /// <summary>
    /// One plate-solve sample: the mount slews to (ha, dec) on a side; returns RA_solved − RA_mount in
    /// hours (same sidereal time for both), what the session computes from a real solve.
    /// </summary>
    public double DeltaRaHours(PierSideLabel side, double hourAngleHours, double declinationDegrees)
    {
        (double solvedHa, _) = Angles(TruePointing(side, hourAngleHours, declinationDegrees).Optical);
        double dHa = solvedHa - hourAngleHours;
        dHa = ((dHa + 12.0) % 24.0 + 24.0) % 24.0 - 12.0;
        return -dHa;
    }

    /// <summary>
    /// The mount-reported (ha, dec) at which a landmark at a distance is centred on a side. The
    /// landmark direction is given in the sky frame as seen from the RA axis.
    /// </summary>
    public (double HourAngle, double Declination) CentreOnLandmark(PierSideLabel side, Vector3 landmarkDirection, double landmarkDistanceMeters)
    {
        Vector3 landmark = landmarkDistanceMeters * landmarkDirection.Normalized();
        Vector3 command = FromSky(landmarkDirection.Normalized());

        for (int i = 0; i < 200; i++)
        {
            (double ha, double dec) = Angles(command);
            (Vector3 optical, Vector3 u) = TruePointing(side, ha, dec);
            Vector3 aperture = (AxisOffsetMm / 1000.0) * u;
            Vector3 wanted = (landmark - aperture).Normalized();
            Vector3 error = wanted - optical;
            if (error.Length < 1e-13)
            {
                break;
            }

            command = (command + FromSky(error)).Normalized();
        }

        return Angles(command);
    }

    /// <summary>
    /// The end that needs the shim, from the body-frame heights of the two mounting points above the
    /// saddle plane (the plane perpendicular to the Dec axis): the lower end is lifted.
    /// </summary>
    public ShimEnd ExpectedShimEnd()
    {
        double c = ArcminRad(TiltAwayFromSaddleArcmin);
        double frontHeight = 0.5 * Math.Sin(c);
        double rearHeight = -0.5 * Math.Sin(c);
        if (frontHeight == rearHeight)
        {
            return ShimEnd.None;
        }

        return frontHeight < rearHeight ? ShimEnd.Front : ShimEnd.Rear;
    }

    /// <summary>Expected shim thickness in mm for a mounting-point distance: L · tan|tilt|.</summary>
    public double ExpectedShimMm(double mountingPointDistanceMm) => mountingPointDistanceMm * Math.Tan(Math.Abs(ArcminRad(TiltAwayFromSaddleArcmin)));
}
