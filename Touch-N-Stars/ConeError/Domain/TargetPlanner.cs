using System;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.ConeError.Domain;

/// <summary>Timing assumptions of a measurement pair, in seconds.</summary>
public sealed record PairTiming(double SlewSeconds, double FlipSeconds, double SettleSeconds, double ExposureSeconds, double SolveSeconds)
{
    /// <summary>Time of one side's sample after arrival: settle, exposure, download and solve.</summary>
    public double SampleSeconds => SettleSeconds + ExposureSeconds + SolveSeconds;

    /// <summary>Time between the exposure midpoints of the two sides of a pair.</summary>
    public double BetweenSidesSeconds => SampleSeconds + FlipSeconds;

    public double PairSeconds => SlewSeconds + FlipSeconds + 2.0 * SampleSeconds;
}

/// <summary>Input of <see cref="TargetPlanner.Plan"/>.</summary>
public sealed class PlannerInput
{
    public DateTime NowUtc { get; init; }

    /// <summary>The mount's local sidereal time at <see cref="NowUtc"/>, hours.</summary>
    public double LocalSiderealTimeHours { get; init; }

    public double LatitudeDegrees { get; init; }

    /// <summary>The side the mount is on now; the first pair starts there to save a flip. Null: start east.</summary>
    public PierSideLabel? CurrentSide { get; init; }

    public int Repetitions { get; init; }

    /// <summary>Requested declination, or null for a suggestion near 0°.</summary>
    public double? Declination { get; init; }

    public PairTiming Timing { get; init; }

    public LimitSet Limits { get; init; }

    /// <summary>The Sun's (hour angle, declination) at a UTC instant, in the frame of the mount's hour angles.</summary>
    public Func<DateTime, HaDec> SunAt { get; init; }
}

/// <summary>
/// One planned sample: the side, the coordinates and the checks at the expected exposure time. Its
/// violations also cover the limits over the sample's whole window (see <see cref="TargetPlanner"/>).
/// </summary>
public sealed class PlannedStep
{
    public int Pair { get; init; }
    public PierSideLabel Side { get; init; }

    /// <summary>"slew" for the first side of a pair, "flip" for the second.</summary>
    public string Kind { get; init; }

    public double RightAscensionHours { get; init; }
    public double DeclinationDegrees { get; init; }
    public DateTime ExpectedExposureUtc { get; init; }
    public EndpointCheck Check { get; init; }

    /// <summary>Limit violations at the ends of the sample's window, beyond those of <see cref="Check"/>.</summary>
    public IReadOnlyList<LimitViolation> WindowViolations { get; init; } = Array.Empty<LimitViolation>();
}

public sealed class AutoPlan
{
    public double DeclinationDegrees { get; init; }
    public bool DeclinationSuggested { get; init; }
    public IReadOnlyList<PlannedStep> Steps { get; init; } = Array.Empty<PlannedStep>();

    /// <summary>The largest |hour angle| any sample is planned at, degrees; the confirmed envelope.</summary>
    public double MaxAbsHourAngleDegrees { get; init; }

    /// <summary>
    /// Every pair is measured west side first, east side second, so that no sample is ever past the
    /// meridian (counterweight up). Used when the meridian limits are too small for the alternating
    /// order, at the cost of one more flip per pair.
    /// </summary>
    public bool NormalSidesOnly { get; init; }

    public double EstimatedSeconds { get; init; }
    public IReadOnlyList<LimitViolation> Violations { get; init; } = Array.Empty<LimitViolation>();
    public string Refusal { get; init; }

    public bool Ok => Refusal == null && Violations.Count == 0;
}

/// <summary>
/// Plans the automated measurement: a target near the meridian at a declination near 0° (at most
/// 30°), measured on both pier sides per repetition. Each pair gets its own target, placed so that
/// its two exposures sit symmetrically around the meridian (residual polar misalignment then cancels
/// in the difference), and consecutive pairs alternate the side order so that each pair after the
/// first needs one flip and one short slew. When the past-meridian limit of either side is smaller
/// than half a pair's hour-angle window, the alternating order is impossible (one sample of every other
/// pair would be counterweight-up on each side); then every pair is measured West first (HA &lt; 0) and
/// East second (HA &gt; 0), both counterweight-down.
/// <para>
/// The limits are checked over each sample's window, not only at the planned exposure: a goto that
/// keeps the pier side can arrive at once (a 7 s goto where 2 min were planned), so the first side of
/// a pair is also checked at the goto command, and every sample at the end of its solve. East of the
/// meridian on the east side, the counterweight is up most at the goto; OnStepX then stops tracking
/// at its meridian limit (seen on a UMi with a 1° east limit, 2026-10-20).
/// </para>
/// </summary>
public static class TargetPlanner
{
    public const double MaxAbsDeclinationDegrees = 30.0;
    public const int MinRepetitions = 2;
    public const int MaxRepetitions = 10;

    /// <summary>Extra altitude kept above the minimum when the declination is suggested.</summary>
    public const double SuggestionAltitudeMarginDegrees = 5.0;

    public static AutoPlan Plan(PlannerInput input)
    {
        if (input == null)
        {
            throw new ArgumentNullException(nameof(input));
        }

        if (input.Repetitions < MinRepetitions || input.Repetitions > MaxRepetitions)
        {
            return Refuse($"The number of repetitions must be between {MinRepetitions} and {MaxRepetitions}.");
        }

        if (input.Declination.HasValue)
        {
            if (Math.Abs(input.Declination.Value) > MaxAbsDeclinationDegrees)
            {
                return Refuse($"The declination must be within ±{MaxAbsDeclinationDegrees:F0}° so that the axis non-perpendicularity (tan δ) stays small.");
            }

            return PlanAt(input, input.Declination.Value, suggested: false);
        }

        AutoPlan first = null;
        foreach (double dec in CandidateDeclinations(input.LatitudeDegrees))
        {
            double meridianAltitude = 90.0 - Math.Abs(input.LatitudeDegrees - dec);
            if (meridianAltitude < input.Limits.MinAltitudeDegrees + SuggestionAltitudeMarginDegrees && Math.Abs(dec) < MaxAbsDeclinationDegrees)
            {
                continue;
            }

            AutoPlan plan = PlanAt(input, dec, suggested: true);
            first ??= plan;
            if (plan.Ok)
            {
                return plan;
            }
        }

        return first ?? Refuse("No declination between 0° and 30° is high enough above the horizon at the meridian.");
    }

    /// <summary>0°, then 1° steps toward the visible pole up to 30°.</summary>
    public static IEnumerable<double> CandidateDeclinations(double latitudeDegrees)
    {
        double direction = latitudeDegrees >= 0.0 ? 1.0 : -1.0;
        for (int i = 0; i <= (int)MaxAbsDeclinationDegrees; i++)
        {
            yield return direction * i;
        }
    }

    /// <summary>The alternating order when it stays within the meridian limits over every sample's window, else the normal sides.</summary>
    private static AutoPlan PlanAt(PlannerInput input, double declination, bool suggested)
    {
        AutoPlan alternating = PlanAt(input, declination, suggested, normalSidesOnly: false);
        return alternating.Violations.Any(v => v.Code == LimitViolationCode.PastMeridianLimit)
            ? PlanAt(input, declination, suggested, normalSidesOnly: true)
            : alternating;
    }

    private static AutoPlan PlanAt(PlannerInput input, double declination, bool suggested, bool normalSidesOnly)
    {
        PairTiming timing = input.Timing;
        var steps = new List<PlannedStep>();
        var violations = new List<LimitViolation>();
        double halfWindow = Sidereal(timing.BetweenSidesSeconds / 2.0) * 15.0;
        PierSideLabel firstSide = normalSidesOnly ? PierSideLabel.West : input.CurrentSide ?? PierSideLabel.East;
        PierSideLabel? previous = input.CurrentSide;
        double maxAbsHa = 0.0;
        double elapsed = 0.0;

        for (int pair = 0; pair < input.Repetitions; pair++)
        {
            double slew = pair == 0
                ? timing.SlewSeconds
                : normalSidesOnly ? timing.FlipSeconds : Math.Min(timing.SlewSeconds, 30.0);
            double firstMid = elapsed + slew + timing.SettleSeconds + timing.ExposureSeconds / 2.0;
            double secondMid = firstMid + timing.BetweenSidesSeconds;
            double centre = (firstMid + secondMid) / 2.0;

            double raAtCentre = Spherical.WrapHours24(input.LocalSiderealTimeHours + Sidereal(centre));
            PierSideLabel secondSide = ConeMath.Other(firstSide);

            double sampleRest = timing.ExposureSeconds / 2.0 + timing.SolveSeconds;
            steps.Add(Step(input, pair, firstSide, previous == null || previous == firstSide ? "slew" : "flip", raAtCentre, declination, firstMid, elapsed, firstMid + sampleRest));
            steps.Add(Step(input, pair, secondSide, "flip", raAtCentre, declination, secondMid, secondMid, secondMid + sampleRest));

            maxAbsHa = Math.Max(maxAbsHa, halfWindow);
            elapsed += slew + timing.FlipSeconds + 2.0 * timing.SampleSeconds;
            previous = secondSide;
            firstSide = normalSidesOnly ? PierSideLabel.West : secondSide;
        }

        foreach (PlannedStep step in steps)
        {
            violations.AddRange(step.Check.Violations);
            violations.AddRange(step.WindowViolations);
        }

        return new AutoPlan
        {
            DeclinationDegrees = declination,
            DeclinationSuggested = suggested,
            Steps = steps,
            MaxAbsHourAngleDegrees = maxAbsHa,
            NormalSidesOnly = normalSidesOnly,
            EstimatedSeconds = elapsed,
            Violations = violations.GroupBy(v => (v.Code, v.Source)).Select(g => g.First()).ToList()
        };
    }

    /// <param name="secondsFromNow">The planned exposure midpoint.</param>
    /// <param name="earliestSeconds">The earliest the mount can be there: the goto command for a goto that keeps the side.</param>
    /// <param name="latestSeconds">The end of the sample's solve.</param>
    private static PlannedStep Step(
        PlannerInput input,
        int pair,
        PierSideLabel side,
        string kind,
        double ra,
        double dec,
        double secondsFromNow,
        double earliestSeconds,
        double latestSeconds)
    {
        double lst = input.LocalSiderealTimeHours + Sidereal(secondsFromNow);
        double ha = Spherical.HourAngle(lst, ra);
        DateTime when = input.NowUtc.AddSeconds(secondsFromNow);
        HaDec sun = input.SunAt(when);
        EndpointCheck check = LimitValidator.ValidateEndpoint(input.Limits, side, ha, dec, input.LatitudeDegrees, new[] { sun });
        IReadOnlyList<LimitViolation> window = WindowViolations(input.Limits, side, ra, dec, input.LatitudeDegrees, input.LocalSiderealTimeHours, earliestSeconds, latestSeconds, sun);

        return new PlannedStep
        {
            Pair = pair,
            Side = side,
            Kind = kind,
            RightAscensionHours = ra,
            DeclinationDegrees = dec,
            ExpectedExposureUtc = when,
            Check = check,
            WindowViolations = window
        };
    }

    /// <summary>
    /// The mount and altitude limits at both ends of a time window for a fixed target (the Sun is checked
    /// at the exposure). The past-meridian angle changes monotonically with time, so the ends are the worst.
    /// </summary>
    public static IReadOnlyList<LimitViolation> WindowViolations(
        LimitSet limits,
        PierSideLabel side,
        double ra,
        double dec,
        double latitude,
        double lstNow,
        double earliestSeconds,
        double latestSeconds,
        HaDec sun)
    {
        var found = new List<LimitViolation>();
        foreach (double seconds in new[] { earliestSeconds, latestSeconds })
        {
            double ha = Spherical.HourAngle(lstNow + Sidereal(seconds), ra);
            EndpointCheck end = LimitValidator.ValidateEndpoint(limits, side, ha, dec, latitude, new[] { sun });
            found.AddRange(end.Violations.Where(v => v.Code is not (LimitViolationCode.SunTooClose or LimitViolationCode.SunTooCloseOnPath)));
        }

        return found.GroupBy(v => v.Code).Select(g => g.OrderByDescending(v => Math.Abs(v.Value - v.Limit)).First()).ToList();
    }

    /// <summary>Sidereal hours elapsed in a number of solar seconds.</summary>
    public static double Sidereal(double solarSeconds) => solarSeconds / 3600.0 * Spherical.SiderealPerSolar;

    private static AutoPlan Refuse(string reason) => new() { Refusal = reason };
}
