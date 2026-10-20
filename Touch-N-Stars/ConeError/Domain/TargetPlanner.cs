using System;
using System.Collections.Generic;

namespace TouchNStars.ConeError.Domain;

/// <summary>Timing assumptions of a measurement pair, in seconds.</summary>
public sealed record PairTiming(double SlewSeconds, double FlipSeconds, double SettleSeconds, double ExposureSeconds, double SolveSeconds)
{
    /// <summary>Time of one side's sample after arrival: settle, exposure, download and solve.</summary>
    public double SampleSeconds => SettleSeconds + ExposureSeconds + SolveSeconds;
}

/// <summary>Input of <see cref="TargetPlanner.Plan"/>.</summary>
public sealed class PlannerInput
{
    public DateTime NowUtc { get; init; }

    /// <summary>The mount's local sidereal time at <see cref="NowUtc"/>, hours.</summary>
    public double LocalSiderealTimeHours { get; init; }

    public double LatitudeDegrees { get; init; }

    public int Repetitions { get; init; }

    /// <summary>Requested declination, or null for a suggestion near 0°.</summary>
    public double? Declination { get; init; }

    public PairTiming Timing { get; init; }

    public double MinAltitudeDegrees { get; init; }

    /// <summary>The profile's custom horizon: altitude (degrees) at an azimuth (degrees), or null.</summary>
    public Func<double, double> HorizonAltitudeAt { get; init; }

    /// <summary>When the flip is made: the profile's minutes after the meridian.</summary>
    public double FlipAfterMinutes { get; init; }

    /// <summary>How far past the meridian the mount may track before it must flip: the profile's maximum.</summary>
    public double MaxMinutesAfterMeridian { get; init; }
}

/// <summary>One planned pair: the same coordinates on the west side before the meridian and on the east side after it.</summary>
public sealed class PlannedPair
{
    public int Pair { get; init; }
    public double RightAscensionHours { get; init; }
    public double DeclinationDegrees { get; init; }
    public DateTime WestExposureUtc { get; init; }
    public double WestHourAngle { get; init; }
    public DateTime FlipUtc { get; init; }
    public DateTime EastExposureUtc { get; init; }
    public double EastHourAngle { get; init; }
    public double Altitude { get; init; }
    public double Azimuth { get; init; }
}

public sealed class AutoPlan
{
    public double DeclinationDegrees { get; init; }
    public bool DeclinationSuggested { get; init; }
    public double FlipAfterMinutes { get; init; }
    public IReadOnlyList<PlannedPair> Pairs { get; init; } = Array.Empty<PlannedPair>();
    public double EstimatedSeconds { get; init; }
    public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

    public bool Ok => Problems.Count == 0;
}

/// <summary>
/// Plans the measurement the way a sequence does a meridian flip: each pair is a target near the
/// meridian at a declination near 0° (at most 30°), sampled on the west pier side just before the
/// meridian, tracked until it is the profile's "minutes after meridian" past it, flipped with NINA's
/// meridian flip and sampled again on the east side. Both samples see the same coordinates; nothing
/// forces a pier side, so it works with any mount NINA can flip.
/// </summary>
public static class TargetPlanner
{
    public const double MaxAbsDeclinationDegrees = 30.0;
    public const int MinRepetitions = 2;
    public const int MaxRepetitions = 10;

    /// <summary>The west sample is taken this long before the target reaches the meridian, minutes of time.</summary>
    public const double WestSampleMinutesBeforeMeridian = 0.5;

    /// <summary>The flip waits at least this long past the meridian, minutes of time, so that every driver sees the target past it.</summary>
    public const double MinFlipAfterMinutes = 1.0;

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

        double flipAfter = Math.Max(input.FlipAfterMinutes, MinFlipAfterMinutes);
        if (flipAfter > input.MaxMinutesAfterMeridian)
        {
            return Refuse(
                $"The flip is made {flipAfter:F0} min after the meridian, but the profile lets the mount track only {input.MaxMinutesAfterMeridian:F0} min past it. Set 'minutes after meridian' to at most the maximum.");
        }

        if (input.Declination.HasValue)
        {
            if (Math.Abs(input.Declination.Value) > MaxAbsDeclinationDegrees)
            {
                return Refuse($"The declination must be within ±{MaxAbsDeclinationDegrees:F0}° so that the axis non-perpendicularity (tan δ) stays small.");
            }

            return PlanAt(input, input.Declination.Value, suggested: false, flipAfter);
        }

        AutoPlan last = null;
        foreach (double dec in CandidateDeclinations(input.LatitudeDegrees))
        {
            double meridianAltitude = 90.0 - Math.Abs(input.LatitudeDegrees - dec);
            if (meridianAltitude < input.MinAltitudeDegrees + SuggestionAltitudeMarginDegrees && Math.Abs(dec) < MaxAbsDeclinationDegrees)
            {
                continue;
            }

            last = PlanAt(input, dec, suggested: true, flipAfter);
            if (last.Ok)
            {
                return last;
            }
        }

        return last ?? Refuse("No declination between 0° and 30° is high enough above the horizon at the meridian.");
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

    private static AutoPlan PlanAt(PlannerInput input, double declination, bool suggested, double flipAfter)
    {
        PairTiming timing = input.Timing;
        var pairs = new List<PlannedPair>();
        var problems = new List<string>();
        double elapsed = 0.0;

        // both samples are within minutes of the meridian, where the target culminates
        (double altitude, double azimuth) = Spherical.AltAz(0.0, declination, input.LatitudeDegrees);

        for (int pair = 0; pair < input.Repetitions; pair++)
        {
            // after the first pair the mount is on the east side and has to come back west
            double slew = pair == 0 ? timing.SlewSeconds : timing.FlipSeconds;
            Times t = PairTimes(timing, slew, flipAfter);
            double westMid = elapsed + t.WestMid;
            double ra = Spherical.WrapHours24(input.LocalSiderealTimeHours + Sidereal(westMid) + WestSampleMinutesBeforeMeridian / 60.0);
            double westHa = -WestSampleMinutesBeforeMeridian / 60.0;
            double eastHa = Spherical.HourAngle(input.LocalSiderealTimeHours + Sidereal(elapsed + t.EastMid), ra);

            pairs.Add(new PlannedPair
            {
                Pair = pair,
                RightAscensionHours = ra,
                DeclinationDegrees = declination,
                WestExposureUtc = input.NowUtc.AddSeconds(westMid),
                WestHourAngle = westHa,
                FlipUtc = input.NowUtc.AddSeconds(elapsed + t.Flip),
                EastExposureUtc = input.NowUtc.AddSeconds(elapsed + t.EastMid),
                EastHourAngle = eastHa,
                Altitude = altitude,
                Azimuth = azimuth
            });

            elapsed += t.End;
        }

        double minimum = input.MinAltitudeDegrees;
        string source = "the minimum altitude";
        if (input.HorizonAltitudeAt != null)
        {
            double horizon = input.HorizonAltitudeAt(azimuth);
            if (!double.IsNaN(horizon) && horizon > minimum)
            {
                minimum = horizon;
                source = "the profile's custom horizon";
            }
        }

        if (altitude < minimum)
        {
            problems.Add($"At declination {declination:F0}° the target culminates at {altitude:F1}°, below {source} of {minimum:F1}°.");
        }

        return new AutoPlan
        {
            DeclinationDegrees = declination,
            DeclinationSuggested = suggested,
            FlipAfterMinutes = flipAfter,
            Pairs = pairs,
            EstimatedSeconds = elapsed,
            Problems = problems
        };
    }

    /// <summary>Seconds from the start of a pair: west exposure midpoint, flip, east exposure midpoint, end.</summary>
    public readonly record struct Times(double WestMid, double Flip, double EastMid, double End);

    public static Times PairTimes(PairTiming timing, double slewSeconds, double flipAfterMinutes)
    {
        double westMid = slewSeconds + timing.SettleSeconds + timing.ExposureSeconds / 2.0;
        double westDone = westMid + timing.ExposureSeconds / 2.0 + timing.SolveSeconds;
        // the target reaches the flip time this long after the west midpoint (sidereal minutes → seconds)
        double untilFlip = (WestSampleMinutesBeforeMeridian + flipAfterMinutes) * 60.0 / Spherical.SiderealPerSolar;
        double flip = Math.Max(westDone, westMid + untilFlip);
        double eastMid = flip + timing.FlipSeconds + timing.SettleSeconds + timing.ExposureSeconds / 2.0;
        double end = eastMid + timing.ExposureSeconds / 2.0 + timing.SolveSeconds;
        return new Times(westMid, flip, eastMid, end);
    }

    /// <summary>Sidereal hours elapsed in a number of solar seconds.</summary>
    public static double Sidereal(double solarSeconds) => solarSeconds / 3600.0 * Spherical.SiderealPerSolar;

    private static AutoPlan Refuse(string reason) => new() { Problems = new[] { reason } };
}
