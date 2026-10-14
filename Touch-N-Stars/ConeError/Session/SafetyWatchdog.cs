using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

/// <summary>
/// Polls the mount once a second while a session runs:
/// <list type="bullet">
/// <item>the Sun relative to the current pointing: a warning within radius + 5°; within the radius
/// any running move is stopped and the session is told (it does not move the mount by itself);</item>
/// <item>a position jump while neither the session nor the mount is slewing (a sync or a move from
/// elsewhere), which would corrupt the measurement.</item>
/// </list>
/// <see cref="Check"/> is the testable step; <see cref="RunAsync"/> calls it in a loop.
/// </summary>
public sealed class SafetyWatchdog
{
    public const double WarningMarginDegrees = 5.0;

    /// <summary>A jump larger than this between two polls while still counts as a move from elsewhere.</summary>
    public const double JumpArcmin = 1.0;

    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IConeMount mount;
    private readonly ISunProvider sun;
    private readonly IClock clock;
    private readonly Func<double> radius;
    private MountSnapshot last;
    private int ownMotion;

    public SafetyWatchdog(IConeMount mount, ISunProvider sun, IClock clock, Func<double> radiusDegrees)
    {
        this.mount = mount;
        this.sun = sun;
        this.clock = clock;
        radius = radiusDegrees;
    }

    /// <summary>The Sun came within the radius of the current pointing.</summary>
    public event Action<double> SunTooClose;

    /// <summary>The mount moved while it should have stood still.</summary>
    public event Action<string> UnexpectedMotion;

    public SunWatchStatus Sun { get; private set; }

    /// <summary>
    /// Whether the Sun within the radius stops a running move. Only an explicitly overridden daytime
    /// move turns it off, so that the tube is not stopped mid-swing pointing near the Sun.
    /// </summary>
    public bool StopOnSun { get; set; } = true;

    /// <summary>Marks a move the session started itself; position changes until <see cref="EndOwnMotion"/> are expected.</summary>
    public void BeginOwnMotion() => Interlocked.Increment(ref ownMotion);

    public void EndOwnMotion()
    {
        Interlocked.Decrement(ref ownMotion);
        last = null;
    }

    public async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: watchdog check failed: {ex.Message}");
            }

            try
            {
                await clock.Delay(Interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Check()
    {
        MountSnapshot now = mount.Snapshot();
        if (now == null || !now.Connected)
        {
            last = null;
            return;
        }

        HaDec sunNow = sun.SunAt(clock.UtcNow);
        double separation = Spherical.SeparationDegrees(now.HourAngleHours, now.DeclinationDegrees, sunNow.HourAngle, sunNow.Declination);
        double r = radius();
        Sun = new SunWatchStatus
        {
            SeparationDegrees = separation,
            RadiusDegrees = r,
            Warning = separation < r + WarningMarginDegrees,
            Critical = separation < r
        };

        if (separation < r)
        {
            if (StopOnSun && (now.Slewing || Volatile.Read(ref ownMotion) > 0))
            {
                mount.Stop();
            }

            SunTooClose?.Invoke(separation);
        }

        if (Volatile.Read(ref ownMotion) > 0 || now.Slewing)
        {
            last = null;
            return;
        }

        if (last != null && last.Tracking == now.Tracking)
        {
            double jump = now.Tracking
                ? Spherical.SeparationDegrees(last.RightAscensionHours, last.DeclinationDegrees, now.RightAscensionHours, now.DeclinationDegrees)
                : Spherical.SeparationDegrees(last.HourAngleHours, last.DeclinationDegrees, now.HourAngleHours, now.DeclinationDegrees);
            if (jump * 60.0 > JumpArcmin || last.Side != now.Side)
            {
                UnexpectedMotion?.Invoke($"The mount position changed by {jump * 60.0:F1}′ while it should have stood still (a sync or a move from elsewhere).");
            }
        }

        last = now;
    }
}
