using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;

namespace TouchNStars.Tests.ConeError;

/// <summary>
/// A virtual clock: short delays advance it and yield; delays of 5 minutes and more (timeouts) wait
/// for cancellation, and so does every delay while <see cref="Hold"/> says so.
/// </summary>
public sealed class FakeClock : IClock
{
    private readonly object sync = new();
    private DateTime now;

    public FakeClock(DateTime start)
    {
        now = start;
    }

    public DateTime UtcNow
    {
        get
        {
            lock (sync)
            {
                return now;
            }
        }
    }

    /// <summary>While this returns true, delays wait for cancellation (to catch a phase in a test).</summary>
    public Func<bool>? Hold { get; set; }

    public void Advance(TimeSpan by)
    {
        lock (sync)
        {
            now += by;
        }
    }

    public async Task Delay(TimeSpan delay, CancellationToken token)
    {
        if (delay >= TimeSpan.FromMinutes(5) || Hold?.Invoke() == true)
        {
            await Task.Delay(Timeout.Infinite, token);
        }

        token.ThrowIfCancellationRequested();
        Advance(delay);
        await Task.Yield();
    }
}

/// <summary>How the fake mount picks the pier side of a goto.</summary>
public enum GotoSideChoice
{
    /// <summary>The counterweight-down side for the target's hour angle, like most mounts.</summary>
    Normal,

    /// <summary>Stays on the current side while the target is reachable there.</summary>
    StayOnSide
}

/// <summary>
/// A mount that behaves like NINA's drivers: a goto lands on the side <see cref="GotoSide"/> picks,
/// a meridian flip moves to the side the target is expected on (NINA's ExpectedPierSide: HA 0..12 h
/// east, else west) and does nothing when the mount is already there. RA/Dec are what it believes.
/// </summary>
public sealed class FakeMount : IConeMount
{
    private readonly object sync = new();
    private readonly FakeClock clock;
    private readonly double longitude;
    private double ra;
    private double ha;
    private double dec;

    public FakeMount(FakeClock clock, double longitude, PierSideLabel side, double hourAngle, double declination, bool tracking)
    {
        this.clock = clock;
        this.longitude = longitude;
        Side = side;
        Tracking = tracking;
        dec = declination;
        ha = hourAngle;
        ra = Spherical.RightAscension(Lst, hourAngle);
    }

    public PierSideLabel Side { get; private set; }
    public bool Tracking { get; private set; }
    public bool Slewing { get; private set; }
    public bool Connected { get; set; } = true;
    public GotoSideChoice GotoSide { get; set; } = GotoSideChoice.Normal;

    /// <summary>The mount cannot change its pier side with a meridian flip (it slews, but stays).</summary>
    public bool CannotFlip { get; set; }

    /// <summary>Called when a slew or flip starts, before it completes; can block on the token.</summary>
    public Func<string, PierSideLabel, CancellationToken, Task>? OnMove { get; set; }

    public Exception? SlewException { get; set; }

    public List<(string Kind, double Ra, double Dec, double HourAngle, PierSideLabel Side)> Moves { get; } = new();
    public List<bool> TrackingCalls { get; } = new();
    public int StopCount { get; private set; }

    public double Lst => SunEphemeris.LocalSiderealTimeHours(clock.UtcNow, longitude);

    public double HourAngle
    {
        get
        {
            lock (sync)
            {
                return Tracking ? Spherical.HourAngle(Lst, ra) : ha;
            }
        }
    }

    public double Declination
    {
        get
        {
            lock (sync)
            {
                return dec;
            }
        }
    }

    /// <summary>Puts the mount somewhere without a goto (e.g. a move from elsewhere).</summary>
    public void Place(double hourAngle, double declination)
    {
        lock (sync)
        {
            ha = hourAngle;
            dec = declination;
            ra = Spherical.RightAscension(Lst, hourAngle);
        }
    }

    public MountSnapshot Snapshot()
    {
        lock (sync)
        {
            double lst = Lst;
            double currentRa = Tracking ? ra : Spherical.RightAscension(lst, ha);
            return new MountSnapshot(Connected, false, Tracking, Slewing, Side, lst, currentRa, dec, "Fake mount", clock.UtcNow);
        }
    }

    private static PierSideLabel Expected(double hourAngle) => Spherical.WrapHours12(hourAngle) > 0.0 ? PierSideLabel.East : PierSideLabel.West;

    public Task SlewAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token)
    {
        double targetHa = Spherical.HourAngle(Lst, rightAscensionHours);
        PierSideLabel target = GotoSide == GotoSideChoice.StayOnSide ? Side : Expected(targetHa);
        return MoveAsync("slew", rightAscensionHours, declinationDegrees, target, 60.0, token);
    }

    public Task FlipAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token)
    {
        double targetHa = Spherical.HourAngle(Lst, rightAscensionHours);
        PierSideLabel expected = Expected(targetHa);
        PierSideLabel target = CannotFlip ? Side : expected;
        return MoveAsync("flip", rightAscensionHours, declinationDegrees, target, target == Side ? 1.0 : 90.0, token);
    }

    private async Task MoveAsync(string kind, double rightAscension, double declination, PierSideLabel target, double seconds, CancellationToken token)
    {
        lock (sync)
        {
            Moves.Add((kind, rightAscension, declination, Spherical.HourAngle(Lst, rightAscension), target));
            Slewing = true;
            Tracking = true;
        }

        try
        {
            if (OnMove != null)
            {
                await OnMove(kind, target, token);
            }

            if (SlewException != null)
            {
                throw SlewException;
            }

            await clock.Delay(TimeSpan.FromSeconds(seconds), token);
            lock (sync)
            {
                ra = rightAscension;
                dec = declination;
                ha = Spherical.HourAngle(Lst, ra);
                Side = target;
            }
        }
        finally
        {
            lock (sync)
            {
                Slewing = false;
            }
        }
    }

    public void Stop()
    {
        lock (sync)
        {
            StopCount++;
            Slewing = false;
        }
    }

    public bool SetTracking(bool enabled)
    {
        lock (sync)
        {
            TrackingCalls.Add(enabled);
            if (Tracking && !enabled)
            {
                ha = Spherical.HourAngle(Lst, ra);
            }
            else if (!Tracking && enabled)
            {
                ra = Spherical.RightAscension(Lst, ha);
            }

            Tracking = enabled;
            return true;
        }
    }
}

/// <summary>Solves with the forward model: the true pointing of the mount's current state.</summary>
public sealed class FakeImager : IConeImager
{
    private readonly FakeMount mount;
    private readonly FakeClock clock;
    private readonly ConeForwardModel world;
    private int calls;

    public FakeImager(FakeMount mount, FakeClock clock, ConeForwardModel world)
    {
        this.mount = mount;
        this.clock = clock;
        this.world = world;
    }

    /// <summary>(call index, side) → whether this solve fails.</summary>
    public Func<int, PierSideLabel, bool> Fail { get; set; } = (_, _) => false;

    /// <summary>Called during the exposure, e.g. to move the mount behind the session's back.</summary>
    public Action<int>? DuringExposure { get; set; }

    /// <summary>Extra solved RA noise per call, arcmin on the sky.</summary>
    public Func<int, double> NoiseArcmin { get; set; } = _ => 0.0;

    public int AbortCount { get; private set; }

    public async Task<SolveOutcome> CaptureAndSolveAsync(double exposureSeconds, double hintRaHours, double hintDecDegrees, CancellationToken token)
    {
        int call = calls++;
        DateTime mid = clock.UtcNow.AddSeconds(exposureSeconds / 2.0);
        MountSnapshot snap = mount.Snapshot();
        await clock.Delay(TimeSpan.FromSeconds(exposureSeconds / 2.0), token);
        DuringExposure?.Invoke(call);
        await clock.Delay(TimeSpan.FromSeconds(exposureSeconds / 2.0 + 20.0), token);

        PierSideLabel side = snap.Side ?? throw new InvalidOperationException("no pier side");
        if (Fail(call, side))
        {
            return SolveOutcome.Failed("no solution");
        }

        (double trueHa, double trueDec) = ConeForwardModel.Angles(world.TruePointing(side, snap.HourAngleHours, snap.DeclinationDegrees).Optical);
        double noise = NoiseArcmin(call) / 60.0 / 15.0 / Math.Cos(trueDec * Math.PI / 180.0);
        double solvedRa = Spherical.WrapHours24(snap.LocalSiderealTimeHours - trueHa + noise);
        return new SolveOutcome(true, solvedRa, trueDec, mid, null);
    }

    public void AbortExposure() => AbortCount++;
}

/// <summary>The test rig: a mount at 50° N, 10° E, at night, with a known tube tilt.</summary>
public sealed class AutoRig : IDisposable
{
    public static readonly DateTime Night = new(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc);

    public static readonly PairTiming Timing = new(SlewSeconds: 120, FlipSeconds: 90, SettleSeconds: 2, ExposureSeconds: 5, SolveSeconds: 30);

    public AutoRig(double tiltArcmin = 5.0, bool tracking = true)
    {
        Directory = Path.Combine(Path.GetTempPath(), "cone-error-tests-" + Guid.NewGuid().ToString("N"));
        Store = new ConeErrorStore(Directory);
        Clock = new FakeClock(Night);
        World = new ConeForwardModel { LatitudeDegrees = 50.0, TiltAwayFromSaddleArcmin = tiltArcmin };
        Mount = new FakeMount(Clock, 10.0, PierSideLabel.East, 1.0, 20.0, tracking);
        Imager = new FakeImager(Mount, Clock, World);
    }

    public string Directory { get; }
    public ConeErrorStore Store { get; }
    public FakeClock Clock { get; }
    public ConeForwardModel World { get; }
    public FakeMount Mount { get; }
    public FakeImager Imager { get; }

    public static ProfileFacts Profile => new("test", "Test", 50.0, 10.0, null, 5.0, 10.0, 0.0, 5.0, 400.0, "ASTAP", null);

    public AutoMeasurementSession Session(int repetitions = 3, bool askPierLabels = false, double flipAfterMinutes = 5.0, double minAltitude = 20.0) => new(
        Mount,
        Imager,
        Clock,
        Store,
        new AutoRunOptions
        {
            Repetitions = repetitions,
            DeclinationDegrees = 0.0,
            ExposureSeconds = 5.0,
            DovetailMm = 200.0,
            Timing = Timing,
            LatitudeDegrees = 50.0,
            MinAltitudeDegrees = minAltitude,
            FlipAfterMinutes = flipAfterMinutes,
            AskPierLabels = askPierLabels,
            MountKey = "test|Fake mount",
            Profile = Profile
        });

    public static async Task<SessionStatus> Run(AutoMeasurementSession session)
    {
        session.Start();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        return session.GetStatus();
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
