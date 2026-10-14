using System;
using System.Collections.Generic;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

public enum SessionPhase
{
    Idle,
    Preparing,
    Slewing,
    WaitingForAnswer,
    Settling,
    Exposing,
    Ready,
    Moving,
    Complete,
    Failed,
    Aborted
}

/// <summary>A question the running session waits for (e.g. the one-time pier label check).</summary>
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

/// <summary>The Sun relative to the current pointing, from the watchdog.</summary>
public sealed class SunWatchStatus
{
    public double SeparationDegrees { get; init; }
    public double RadiusDegrees { get; init; }

    /// <summary>Within radius + 5°: the Sun is getting close.</summary>
    public bool Warning { get; init; }

    /// <summary>Within the radius: cover the telescope.</summary>
    public bool Critical { get; init; }
}

/// <summary>The captured position and the moves of the daytime check.</summary>
public sealed class ManualFlipStatus
{
    public double? CapturedHourAngle { get; init; }
    public double? CapturedDeclination { get; init; }
    public string CapturedSide { get; init; }
    public double? CapturedAltitude { get; init; }
    public double? CapturedAzimuth { get; init; }
    public string CurrentSide { get; init; }
    public bool TrackingWasOn { get; init; }
    public EndpointCheck EastCheck { get; init; }
    public EndpointCheck WestCheck { get; init; }
    public double? CentredHourAngleEast { get; init; }
    public double? CentredHourAngleWest { get; init; }
    public string LastMove { get; init; }
    public bool PierLabelsVerified { get; init; }
}

public sealed class SessionStatus
{
    /// <summary>"none", "auto" or "manual".</summary>
    public string Mode { get; init; } = "none";

    public SessionPhase Phase { get; init; } = SessionPhase.Idle;
    public string Message { get; init; }
    public DateTime? StartedUtc { get; init; }
    public bool Active { get; init; }

    public int PairIndex { get; init; }
    public int Repetitions { get; init; }
    public string CurrentSide { get; init; }
    public double? DeclinationDegrees { get; init; }
    public IReadOnlyList<ConePairRecord> Pairs { get; init; } = Array.Empty<ConePairRecord>();
    public SessionQuestion Question { get; init; }

    public ManualFlipStatus Manual { get; init; }

    public SunWatchStatus Sun { get; init; }
    public SessionError Error { get; init; }
    public ConeResultRecord Result { get; init; }
    public ConeResultRecord Previous { get; init; }
    public bool RestorePending { get; init; }
}
