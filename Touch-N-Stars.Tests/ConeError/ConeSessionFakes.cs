using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;

namespace TouchNStars.Tests.ConeError;

/// <summary>A virtual clock: short delays advance it and yield; delays of 5 minutes and more (timeouts) wait for cancellation.</summary>
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

    public void Advance(TimeSpan by)
    {
        lock (sync)
        {
            now += by;
        }
    }

    public async Task Delay(TimeSpan delay, CancellationToken token)
    {
        if (delay >= TimeSpan.FromMinutes(5))
        {
            await Task.Delay(Timeout.Infinite, token);
        }

        token.ThrowIfCancellationRequested();
        Advance(delay);
        await Task.Yield();
    }
}

public sealed class FakeSun : ISunProvider
{
    private readonly double longitude;

    public FakeSun(double longitude)
    {
        this.longitude = longitude;
    }

    /// <summary>Overrides the ephemeris, e.g. to put the Sun next to the telescope.</summary>
    public HaDec? Fixed { get; set; }

    public HaDec SunAt(DateTime utc)
    {
        if (Fixed is { } f)
        {
            return f;
        }

        (double ha, double dec) = SunEphemeris.HourAngleDeclination(utc, longitude);
        return new HaDec(ha, dec);
    }
}

/// <summary>
/// A mount whose goto lands on the side the pier side control prefers (unless it refuses), tracks or
/// stands still, and whose RA/Dec are what it believes (its own frame).
/// </summary>
public sealed class FakeMount : IConeMount
{
    private readonly object sync = new();
    private readonly FakeClock clock;
    private readonly double longitude;
    private double ra;
    private double ha;
    private double dec;

    public FakeMount(FakeClock clock, double latitude, double longitude, PierSideLabel side, double hourAngle, double declination, bool tracking)
    {
        this.clock = clock;
        Latitude = latitude;
        this.longitude = longitude;
        Side = side;
        Tracking = tracking;
        dec = declination;
        ha = hourAngle;
        ra = Spherical.RightAscension(Lst, hourAngle);
    }

    public double Latitude { get; }
    public PierSideLabel Side { get; private set; }
    public bool Tracking { get; private set; }
    public bool Slewing { get; private set; }
    public FakePierSide? PierSide { get; set; }

    /// <summary>The mount ignores the preferred pier side and stays where it is.</summary>
    public bool IgnorePierSide { get; set; }

    /// <summary>At home OnStepX ignores the preferred side and takes it from the sign of the hour angle (Goto.cpp).</summary>
    public bool AtHome { get; set; }

    /// <summary>NINA's settle time after a goto, during which the driver keeps tracking on.</summary>
    public double SettleSeconds { get; set; }

    /// <summary>A goto started by something else (another view or client) is running.</summary>
    public bool ExternalSlew { get; set; }

    public bool Connected { get; set; } = true;

    /// <summary>The mount keeps tracking when asked to stop.</summary>
    public bool RefuseTrackingOff { get; set; }


    /// <summary>Degrees per second of a goto; the goto tracks meanwhile, like real drivers do.</summary>
    public double SlewSpeed { get; set; } = 2.0;

    /// <summary>Called when a slew starts, before it completes; can block on the token.</summary>
    public Func<PierSideLabel, CancellationToken, Task>? OnSlew { get; set; }

    public Exception? SlewException { get; set; }

    public List<(double Ra, double Dec, PierSideLabel Side)> Slews { get; } = new();
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

    /// <summary>Puts the mount somewhere without a goto, e.g. the user re-centring or a sync.</summary>
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
            return new MountSnapshot(Connected, false, Tracking, Slewing || ExternalSlew, Side, lst, currentRa, dec, Latitude, longitude, "Fake mount", clock.UtcNow, AtHome);
        }
    }

    public async Task SlewAsync(double rightAscensionHours, double declinationDegrees, CancellationToken token)
    {
        PierSideLabel target = IgnorePierSide || PierSide == null ? Side : PierSide.Preferred ?? Side;
        if (AtHome)
        {
            target = Spherical.HourAngle(Lst, rightAscensionHours) < 0.0 ? PierSideLabel.West : PierSideLabel.East;
        }
        lock (sync)
        {
            Slews.Add((rightAscensionHours, declinationDegrees, target));
            Slewing = true;
            Tracking = true;
        }

        try
        {
            if (OnSlew != null)
            {
                await OnSlew(target, token);
            }

            if (SlewException != null)
            {
                throw SlewException;
            }

            double fromHa = HourAngle;
            double distance = target != Side ? 180.0 : Spherical.SeparationDegrees(fromHa, Declination, Spherical.HourAngle(Lst, rightAscensionHours), declinationDegrees);
            await clock.Delay(TimeSpan.FromSeconds(distance / SlewSpeed), token);
            lock (sync)
            {
                // the goto aimed at the RA of the command and tracks it on arrival
                ra = rightAscensionHours;
                dec = declinationDegrees;
                ha = Spherical.HourAngle(Lst, ra);
                Side = target;
                AtHome = false;
            }

            // NINA waits the settle time with tracking still on
            await clock.Delay(TimeSpan.FromSeconds(SettleSeconds), token);
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
            if (!enabled && RefuseTrackingOff)
            {
                return false;
            }

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

public sealed class FakePierSide : IPierSideControl
{
    public FakePierSide(string original = "Best")
    {
        Setting = original;
        OriginalValue = original;
    }

    public string Setting { get; private set; }
    public string OriginalValue { get; }
    public bool Supported { get; init; } = true;
    public string Description => "fake";
    public string UnsupportedReason => Supported ? null! : "This mount cannot be told which pier side to use.";
    public string? OriginalSetting { get; private set; }
    public int Prepares { get; private set; }
    public bool FailRestore { get; set; }

    /// <summary>Reading the original setting fails (e.g. a serial error).</summary>
    public bool FailRead { get; set; }

    /// <summary>Called when the original setting is read (the start of a session).</summary>
    public Action? OnRead { get; set; }

    public PierSideLabel? Preferred => Setting switch
    {
        "East" => PierSideLabel.East,
        "West" => PierSideLabel.West,
        _ => null
    };

    public string ReadOriginal()
    {
        OnRead?.Invoke();
        return FailRead ? throw new InvalidOperationException("serial read failed") : OriginalSetting ??= Setting;
    }

    public Task PrepareAsync(PierSideLabel side, CancellationToken token)
    {
        if (!Supported)
        {
            throw new ConeErrorException("PierSideUnsupported", UnsupportedReason);
        }

        ReadOriginal();
        Setting = side.ToString();
        Prepares++;
        return Task.CompletedTask;
    }

    public Task RestoreAsync()
    {
        if (FailRestore)
        {
            throw new InvalidOperationException("restore failed");
        }

        if (OriginalSetting != null)
        {
            Setting = OriginalSetting;
        }

        return Task.CompletedTask;
    }

    public Task RestoreToAsync(string setting)
    {
        Setting = setting;
        return Task.CompletedTask;
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
    public int Calls => calls;

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

/// <summary>The automated test rig: a mount at 50° N, 10° E, at night, with a known tube tilt.</summary>
public sealed class AutoRig : IDisposable
{
    public static readonly DateTime Night = new(2026, 10, 14, 21, 0, 0, DateTimeKind.Utc);

    public AutoRig(double tiltArcmin = 5.0, PierSideLabel startSide = PierSideLabel.East, double pastMeridian = 2.5)
    {
        Directory = Path.Combine(Path.GetTempPath(), "cone-error-tests-" + Guid.NewGuid().ToString("N"));
        Store = new ConeErrorStore(Directory);
        Clock = new FakeClock(Night);
        Sun = new FakeSun(10.0);
        World = new ConeForwardModel { LatitudeDegrees = 50.0, TiltAwayFromSaddleArcmin = tiltArcmin };
        PierSide = new FakePierSide();
        Mount = new FakeMount(Clock, 50.0, 10.0, startSide, 1.0, 20.0, tracking: true) { PierSide = PierSide };
        Imager = new FakeImager(Mount, Clock, World);
        Limits = new LimitSet { MinAltitudeDegrees = 20.0, PinsPastMeridianDegrees = pastMeridian, SunExclusionDegrees = 30.0 };
    }

    public string Directory { get; }
    public ConeErrorStore Store { get; }
    public FakeClock Clock { get; }
    public FakeSun Sun { get; }
    public ConeForwardModel World { get; }
    public FakePierSide PierSide { get; }
    public FakeMount Mount { get; }
    public FakeImager Imager { get; }
    public LimitSet Limits { get; set; }

    public static readonly PairTiming Timing = new(SlewSeconds: 120, FlipSeconds: 90, SettleSeconds: 2, ExposureSeconds: 5, SolveSeconds: 30);

    public AutoMeasurementSession Session(int repetitions = 4, bool askPierLabels = false, double maxAbsHa = 2.5, bool normalSidesOnly = false) => new(
        Mount,
        PierSide,
        Imager,
        Clock,
        Sun,
        Store,
        new AutoRunOptions
        {
            Repetitions = repetitions,
            DeclinationDegrees = 0.0,
            ExposureSeconds = 5.0,
            DovetailMm = 200.0,
            Timing = Timing,
            Limits = Limits,
            LatitudeDegrees = 50.0,
            MaxAbsHourAngleDegrees = maxAbsHa,
            AskPierLabels = askPierLabels,
            NormalSidesOnly = normalSidesOnly,
            MountKey = "test|Fake mount",
            Profile = new ProfileFacts("test", "Test", 50.0, 10.0, null, 10.0, 0.0, 5.0, 400.0, "ASTAP", null)
        },
        watchdogLoop: false);

    public static async Task<SessionStatus> Run(AutoMeasurementSession session)
    {
        session.Start();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(20));
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
