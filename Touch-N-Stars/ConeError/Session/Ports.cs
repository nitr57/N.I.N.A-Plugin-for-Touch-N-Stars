using System;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

/// <summary>
/// The mount as the session sees it: right ascension and declination of date (JNow) and the
/// mount's own sidereal time, so that hour angles are in the mount's frame.
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
    string Name,
    DateTime TimestampUtc)
{
    public double HourAngleHours => Spherical.HourAngle(LocalSiderealTimeHours, RightAscensionHours);
}

/// <summary>The mount operations the session uses. There is deliberately no sync.</summary>
public interface IConeMount
{
    MountSnapshot Snapshot();

    /// <summary>Slews to RA/Dec of date and waits for the end of the slew; throws with the reason on a refusal.</summary>
    Task SlewAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token);

    /// <summary>
    /// NINA's meridian flip to RA/Dec of date: the driver moves the mount to the pier side the target
    /// is expected on (ASCOM/Alpaca set SideOfPier, INDI and OnStepX do their own); throws on a failure.
    /// </summary>
    Task FlipAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token);

    /// <summary>Stops any motion now (abort slew); must return immediately.</summary>
    void Stop();

    /// <summary>Turns tracking on or off; true when tracking is now as requested.</summary>
    bool SetTracking(bool enabled);
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

/// <summary>The profile values the session needs.</summary>
public sealed record ProfileFacts(
    string ProfileId,
    string ProfileName,
    double Latitude,
    double Longitude,
    [property: Newtonsoft.Json.JsonIgnore] Func<double, double> HorizonAltitudeAt,
    double MinutesAfterMeridian,
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

/// <summary>Everything the session needs, resolved for the connected equipment.</summary>
public interface IConeEnvironment
{
    IConeMount Mount { get; }
    IConeImager Imager { get; }
    IObservatory Observatory { get; }
    IClock Clock { get; }
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
