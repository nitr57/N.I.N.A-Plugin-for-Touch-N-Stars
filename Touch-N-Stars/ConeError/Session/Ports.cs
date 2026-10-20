using System;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

/// <summary>
/// The mount as the sessions see it: right ascension and declination of date (JNow), the mount's
/// own sidereal time, so that hour angles are in the mount's frame.
/// </summary>
public sealed record MountSnapshot(
    bool Connected,
    bool AtPark,
    bool Tracking,
    bool Slewing,
    PierSideLabel? Side,
    double LocalSiderealTimeHours,
    double RightAscensionHours,
    double DeclinationDegrees,
    double SiteLatitude,
    double SiteLongitude,
    string Name,
    DateTime TimestampUtc,
    bool AtHome = false)
{
    public double HourAngleHours => Spherical.HourAngle(LocalSiderealTimeHours, RightAscensionHours);
}

/// <summary>The mount operations the sessions use. There is deliberately no sync.</summary>
public interface IConeMount
{
    MountSnapshot Snapshot();

    /// <summary>Slews to RA/Dec of date and waits for the end of the slew; throws with the driver's reason on a refusal.</summary>
    Task SlewAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token);

    /// <summary>Stops any motion now (abort slew); must return immediately.</summary>
    void Stop();

    /// <summary>Turns tracking on or off; true when tracking is now as requested.</summary>
    bool SetTracking(bool enabled);
}

/// <summary>Forces the pier side of the next goto through the mount's "preferred pier side".</summary>
public interface IPierSideControl
{
    bool Supported { get; }

    /// <summary>What is used, e.g. "OnStepX preferred pier side (:SX96)".</summary>
    string Description { get; }

    /// <summary>Why the pier side cannot be forced, when not <see cref="Supported"/>.</summary>
    string UnsupportedReason { get; }

    /// <summary>
    /// Reads and remembers the current preferred pier side (once per session), so that it can be
    /// written to the restore journal before anything is changed.
    /// </summary>
    string ReadOriginal();

    /// <summary>Makes the next goto land on <paramref name="side"/>.</summary>
    Task PrepareAsync(PierSideLabel side, CancellationToken token);

    /// <summary>Restores the setting found before the first <see cref="PrepareAsync"/>; no-op if nothing was changed.</summary>
    Task RestoreAsync();

    /// <summary>Sets a value read from the restore journal after a restart (a setting only, no motion).</summary>
    Task RestoreToAsync(string setting);
}

/// <summary>For mounts whose pier side cannot be forced: every change is refused.</summary>
public sealed class UnsupportedPierSideControl : IPierSideControl
{
    public UnsupportedPierSideControl(string reason)
    {
        UnsupportedReason = reason;
    }

    public bool Supported => false;

    public string Description => "none";

    public string UnsupportedReason { get; }

    public string ReadOriginal() => null;

    public Task PrepareAsync(PierSideLabel side, CancellationToken token)
        => throw new ConeErrorException("PierSideUnsupported", UnsupportedReason);

    public Task RestoreAsync() => Task.CompletedTask;

    public Task RestoreToAsync(string setting) => Task.CompletedTask;
}

/// <summary>One capture and solve; the solved position is of date (JNow), like the mount's.</summary>
public sealed record SolveOutcome(bool Success, double RightAscensionHours, double DeclinationDegrees, DateTime MidExposureUtc, string Error)
{
    public static SolveOutcome Failed(string error) => new(false, double.NaN, double.NaN, DateTime.MinValue, error);
}

public interface IConeImager
{
    /// <summary>Captures an image and plate-solves it near the hint, without syncing or moving the mount.</summary>
    Task<SolveOutcome> CaptureAndSolveAsync(double exposureSeconds, double hintRaHours, double hintDecDegrees, CancellationToken token);

    void AbortExposure();
}

public enum PointingModelState
{
    None,
    Active,
    Unknown
}

public sealed record PointingModelInfo(PointingModelState State, double? ConeTermArcsec, string Detail);

/// <summary>Mount-reported limits; null where the mount does not report them.</summary>
public sealed record MountLimits(
    double? HorizonDegrees,
    double? OverheadDegrees,
    double? PastMeridianEastDegrees,
    double? PastMeridianWestDegrees,
    string Source)
{
    public static MountLimits None { get; } = new(null, null, null, null, null);
}

/// <summary>What the connected mount driver allows: pier side control, limits and the pointing model state.</summary>
public sealed record MountDriverInfo(string Kind, IPierSideControl PierSide, MountLimits Limits, PointingModelInfo PointingModel);

public interface IMountDriverInspector
{
    MountDriverInfo Inspect();
}

/// <summary>The profile values the sessions need.</summary>
public sealed record ProfileFacts(
    string ProfileId,
    string ProfileName,
    double Latitude,
    double Longitude,
    [property: Newtonsoft.Json.JsonIgnore] Func<double, double> HorizonAltitudeAt,
    double MaxMinutesAfterMeridian,
    double SettleSeconds,
    double PlateSolveExposureSeconds,
    double FocalLength,
    string SolverDescription,
    string SolverProblem);

/// <summary>The rest of the observatory state the preconditions look at.</summary>
public sealed record ObservatoryState(
    bool CameraConnected,
    bool CameraExposing,
    bool SequenceRunning,
    bool InternalGuiderGuiding);

public interface IObservatory
{
    ProfileFacts Profile();

    ObservatoryState State();
}

/// <summary>Everything a session needs, resolved for the connected equipment.</summary>
public interface IConeEnvironment
{
    IConeMount Mount { get; }
    IConeImager Imager { get; }
    IMountDriverInspector Driver { get; }
    IObservatory Observatory { get; }
    IClock Clock { get; }
    ISunProvider Sun { get; }
}

public interface IClock
{
    DateTime UtcNow { get; }

    Task Delay(TimeSpan delay, CancellationToken token);
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken token) => Task.Delay(delay, token);
}

/// <summary>The Sun as (hour angle, declination), in the hour-angle frame of the Pi's clock and the profile longitude.</summary>
public interface ISunProvider
{
    HaDec SunAt(DateTime utc);
}

public sealed class MeeusSunProvider : ISunProvider
{
    private readonly Func<double> longitude;

    public MeeusSunProvider(Func<double> longitude)
    {
        this.longitude = longitude;
    }

    public HaDec SunAt(DateTime utc)
    {
        (double ha, double dec) = SunEphemeris.HourAngleDeclination(utc, longitude());
        return new HaDec(ha, dec);
    }
}

/// <summary>A refusal or failure with a stable code for the UI.</summary>
public sealed class ConeErrorException : Exception
{
    public ConeErrorException(string code, string message) : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
