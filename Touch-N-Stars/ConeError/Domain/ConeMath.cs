using System;

namespace TouchNStars.ConeError.Domain;

/// <summary>Pier side as NINA/ASCOM report it (<c>PierSide.pierEast</c> / <c>pierWest</c>).</summary>
public enum PierSideLabel
{
    East,
    West
}

/// <summary>Which mounting point of the tube on the dovetail gets the shim.</summary>
public enum ShimEnd
{
    None,
    /// <summary>The objective (light entry) end.</summary>
    Front,
    /// <summary>The focuser / camera end.</summary>
    Rear
}

/// <summary>
/// The cone error formulas and the shim advice. See docs/cone-error/design.md, "Sign convention".
/// <para>
/// Sign convention (the single source of truth in code):
/// ΔRA = RA_solved − RA_mount (same epoch, wrapped to ±12 h), and
/// c = ½ · (ΔRA_pierWest − ΔRA_pierEast) · cos δ, with NINA/ASCOM pier labels
/// (pierEast = the counterweight-down side for targets west of the meridian).
/// The daytime check uses c = ½ · (HA_pierWest − HA_pierEast) · cos δ with the mount-reported hour
/// angles at which the landmark is centred, which is the same quantity.
/// c &gt; 0: the objective end tilts toward the dovetail/saddle, so the FRONT mounting point is shimmed;
/// c &lt; 0: the REAR mounting point is shimmed; the shim thickness is t = L · tan|c|.
/// </para>
/// </summary>
public static class ConeMath
{
    public const double ArcminPerHourOfRa = 15.0 * 60.0;

    /// <summary>The pointing offset of one sample: RA_solved − RA_mount in hours, wrapped to (-12, 12].</summary>
    public static double DeltaRaHours(double solvedRaHours, double mountRaHours)
        => Spherical.WrapHours12(solvedRaHours - mountRaHours);

    /// <summary>Cone error of one east/west pair in arcminutes: ½ · (ΔRA_W − ΔRA_E) · cos δ.</summary>
    public static double PairConeArcmin(double deltaRaWestHours, double deltaRaEastHours, double declinationDegrees)
        => 0.5 * Spherical.WrapHours12(deltaRaWestHours - deltaRaEastHours) * ArcminPerHourOfRa
            * Math.Cos(Spherical.DegToRad(declinationDegrees));

    /// <summary>
    /// Daytime estimate in arcminutes from the mount-reported hour angles with the landmark centred on
    /// each side: ½ · (HA_W − HA_E) · cos δ, not yet corrected for parallax (<see cref="ParallaxArcmin"/>).
    /// </summary>
    public static double ManualConeArcmin(double hourAngleWestHours, double hourAngleEastHours, double declinationDegrees)
        => 0.5 * Spherical.WrapHours12(hourAngleWestHours - hourAngleEastHours) * ArcminPerHourOfRa
            * Math.Cos(Spherical.DegToRad(declinationDegrees));

    /// <summary>
    /// Parallax of a landmark at a finite distance in arcminutes. The tube's optical axis sits
    /// <paramref name="axisOffsetMm"/> from the RA axis along the Dec axis, on opposite sides of the
    /// RA axis on the two pier sides, which biases the daytime estimate by exactly this amount:
    /// c_measured = c − parallax, so c = c_measured + parallax.
    /// </summary>
    public static double ParallaxArcmin(double axisOffsetMm, double landmarkDistanceMeters)
    {
        if (landmarkDistanceMeters <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(landmarkDistanceMeters), "The landmark distance must be positive.");
        }

        return Spherical.RadToDeg(Math.Atan(axisOffsetMm / 1000.0 / landmarkDistanceMeters)) * 60.0;
    }

    /// <summary>Shim thickness in mm for a cone error and the distance L between the two mounting points: t = L · tan|c|.</summary>
    public static double ShimThicknessMm(double coneArcmin, double mountingPointDistanceMm)
        => mountingPointDistanceMm * Math.Tan(Spherical.DegToRad(Math.Abs(coneArcmin) / 60.0));

    /// <summary>Which end to shim for a signed cone error (see the sign convention above).</summary>
    public static ShimEnd ShimEndFor(double coneArcmin)
    {
        if (coneArcmin > 0.0)
        {
            return ShimEnd.Front;
        }

        return coneArcmin < 0.0 ? ShimEnd.Rear : ShimEnd.None;
    }

    /// <summary>
    /// Past-meridian angle in degrees of a pointing on a pier side: positive when the mount is past
    /// the meridian on that side (counterweight up), negative before it. On pierEast the normal range
    /// is west of the meridian (HA &gt; 0), so pointing east of it (HA &lt; 0) is past the meridian.
    /// </summary>
    public static double PastMeridianDegrees(PierSideLabel side, double hourAngleHours)
    {
        double ha = Spherical.WrapHours12(hourAngleHours) * 15.0;
        return side == PierSideLabel.East ? -ha : ha;
    }

    public static PierSideLabel Other(PierSideLabel side) => side == PierSideLabel.East ? PierSideLabel.West : PierSideLabel.East;
}
