using System;
using System.Collections.Generic;
using System.Linq;

namespace TouchNStars.ConeError.Domain;

public enum ConeQuality
{
    /// <summary>Fewer than two usable values.</summary>
    Insufficient,
    Good,
    Fair,
    Poor
}

/// <summary>Summary of the per-pair cone errors (arcminutes) of one measurement.</summary>
public sealed record ConeSummary(
    int Count,
    int Used,
    double Mean,
    double StdDev,
    double StdError,
    double Median,
    IReadOnlyList<int> RejectedIndices,
    ConeQuality Quality,
    bool Significant);

/// <summary>
/// Statistics of the per-pair cone errors: mean, sample standard deviation, standard error of the
/// mean and median, with outliers rejected by the modified z-score of B. Iglewicz and D. Hoaglin,
/// "How to Detect and Handle Outliers" (ASQC, 1993): M = 0.6745 · |x − median| / MAD, rejected when
/// M &gt; 3.5, i.e. further than 3.5 robust sigmas (1.4826 · MAD) from the median.
/// </summary>
public static class ConeStatistics
{
    /// <summary>MAD rejection needs this many values; with fewer the median is not robust enough.</summary>
    public const int MinCountForRejection = 4;

    /// <summary>Values further than this many robust sigmas (1.4826 · MAD) from the median are rejected (Iglewicz and Hoaglin).</summary>
    public const double RejectionSigmas = 3.5;

    /// <summary>Floor of the robust sigma in arcminutes, so that near-identical values are not rejected for noise.</summary>
    public const double MinRobustSigmaArcmin = 0.1;

    public const double GoodStdErrorArcmin = 0.5;
    public const double GoodStdDevArcmin = 1.0;
    public const double FairStdErrorArcmin = 1.5;

    public static ConeSummary Summarize(IReadOnlyList<double> values)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values));
        }

        var rejected = new List<int>();
        if (values.Count >= MinCountForRejection)
        {
            double median = Median(values);
            double mad = Median(values.Select(v => Math.Abs(v - median)).ToList());
            double sigma = Math.Max(1.4826 * mad, MinRobustSigmaArcmin);
            for (int i = 0; i < values.Count; i++)
            {
                if (Math.Abs(values[i] - median) > RejectionSigmas * sigma)
                {
                    rejected.Add(i);
                }
            }
        }

        List<double> used = values.Where((_, i) => !rejected.Contains(i)).ToList();
        if (used.Count == 0)
        {
            return new ConeSummary(values.Count, 0, double.NaN, double.NaN, double.NaN, double.NaN, rejected, ConeQuality.Insufficient, false);
        }

        double mean = used.Average();
        double medianUsed = Median(used);
        if (used.Count < 2)
        {
            return new ConeSummary(values.Count, used.Count, mean, double.NaN, double.NaN, medianUsed, rejected, ConeQuality.Insufficient, false);
        }

        double stdDev = Math.Sqrt(used.Sum(v => (v - mean) * (v - mean)) / (used.Count - 1));
        double stdError = stdDev / Math.Sqrt(used.Count);

        return new ConeSummary(
            values.Count,
            used.Count,
            mean,
            stdDev,
            stdError,
            medianUsed,
            rejected,
            Rate(stdDev, stdError),
            IsSignificant(mean, stdError));
    }

    /// <summary>Good: SE ≤ 0.5′ and SD ≤ 1′; fair: SE ≤ 1.5′; poor otherwise.</summary>
    public static ConeQuality Rate(double stdDev, double stdError)
    {
        if (double.IsNaN(stdDev) || double.IsNaN(stdError))
        {
            return ConeQuality.Insufficient;
        }

        if (stdError <= GoodStdErrorArcmin && stdDev <= GoodStdDevArcmin)
        {
            return ConeQuality.Good;
        }

        return stdError <= FairStdErrorArcmin ? ConeQuality.Fair : ConeQuality.Poor;
    }

    /// <summary>The mean differs from zero by at least two standard errors.</summary>
    public static bool IsSignificant(double mean, double stdError)
    {
        if (double.IsNaN(mean) || double.IsNaN(stdError))
        {
            return false;
        }

        return stdError == 0.0 ? mean != 0.0 : Math.Abs(mean) >= 2.0 * stdError;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return double.NaN;
        }

        double[] sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : 0.5 * (sorted[mid - 1] + sorted[mid]);
    }
}
