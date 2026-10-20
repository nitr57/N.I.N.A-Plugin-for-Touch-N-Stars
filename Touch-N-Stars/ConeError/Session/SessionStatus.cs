using System;
using System.Collections.Generic;

namespace TouchNStars.ConeError.Session;

public enum SessionPhase
{
    Idle,
    Preparing,
    Slewing,
    WaitingForAnswer,
    Settling,
    Exposing,
    WaitingForMeridian,
    Flipping,
    Complete,
    Failed,
    Aborted
}

/// <summary>A question the running session waits for (the one-time pier label check).</summary>
public sealed class SessionQuestion
{
    public int Id { get; init; }
    public string Kind { get; init; }
    public string Text { get; init; }
}

public sealed class SessionError
{
    public string Code { get; init; }
    public string Message { get; init; }
}

public sealed class SessionStatus
{
    /// <summary>"none" or "auto".</summary>
    public string Mode { get; init; } = "none";

    public SessionPhase Phase { get; init; } = SessionPhase.Idle;
    public string Message { get; init; }
    public DateTime? StartedUtc { get; init; }
    public bool Active { get; init; }

    public int PairIndex { get; init; }
    public int Repetitions { get; init; }
    public string CurrentSide { get; init; }
    public double? DeclinationDegrees { get; init; }

    /// <summary>While waiting for the target to cross the meridian: seconds until the flip.</summary>
    public double? SecondsUntilFlip { get; init; }

    public IReadOnlyList<ConePairRecord> Pairs { get; init; } = Array.Empty<ConePairRecord>();
    public SessionQuestion Question { get; init; }
    public SessionError Error { get; init; }
    public ConeResultRecord Result { get; init; }
    public ConeResultRecord Previous { get; init; }
}
