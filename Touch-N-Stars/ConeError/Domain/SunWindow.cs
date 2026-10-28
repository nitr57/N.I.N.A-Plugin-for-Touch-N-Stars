using System;

namespace TouchNStars.ConeError.Domain;

/// <summary>Finds when a move refused for the Sun becomes allowed, so the UI can say when to come back.</summary>
public static class SunWindow
{
    /// <summary>
    /// The first instant after <paramref name="fromUtc"/>, searched in steps up to <paramref name="horizon"/>,
    /// at which <paramref name="allowedAt"/> holds for the instant and for the following
    /// <paramref name="holdFor"/> (the move's time budget); null if there is none in the horizon.
    /// </summary>
    public static DateTime? NextAllowed(Func<DateTime, bool> allowedAt, DateTime fromUtc, TimeSpan horizon, TimeSpan step, TimeSpan holdFor)
    {
        if (allowedAt == null)
        {
            throw new ArgumentNullException(nameof(allowedAt));
        }

        if (step <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(step));
        }

        for (DateTime t = fromUtc; t <= fromUtc + horizon; t += step)
        {
            if (allowedAt(t) && allowedAt(t + holdFor))
            {
                return t;
            }
        }

        return null;
    }
}
