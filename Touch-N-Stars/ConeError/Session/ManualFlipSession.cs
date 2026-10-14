using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

public sealed class ManualFlipOptions
{
    public LimitSet Limits { get; init; }
    public double LatitudeDegrees { get; init; }
    public bool StrictPath { get; init; }
    public double? DovetailMm { get; init; }
    public string MountKey { get; init; }
    public ProfileFacts Profile { get; init; }
}

/// <summary>A planned move of the daytime check, valid for <see cref="ManualFlipSession.PlanLifetime"/>.</summary>
public sealed class ManualMovePlan
{
    public string PlanId { get; init; }
    public DateTime ExpiresUtc { get; init; }
    public string FromSide { get; init; }
    public string ToSide { get; init; }
    public double HourAngle { get; init; }
    public double Declination { get; init; }
    public EndpointCheck Check { get; init; }

    /// <summary>When the move is refused for the Sun: the first time it would be allowed within 12 h.</summary>
    public DateTime? NextAllowedUtc { get; init; }

    public string Refusal { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool Ok => Refusal == null && Check != null && Check.Ok;
}

/// <summary>The daytime estimate from the re-centred landmark.</summary>
public sealed class ManualEstimate
{
    public double MeasuredArcmin { get; init; }
    public double? ParallaxArcmin { get; init; }
    public double? ConeArcmin { get; init; }
    public double? ShimMm { get; init; }
    public string ShimEnd { get; init; }
    public string Refusal { get; init; }
    public ConeResultRecord Result { get; init; }
    public ConeResultRecord Previous { get; init; }
}

/// <summary>
/// The daytime check: tracking off, positions in mount terms (hour angle and declination, fixed to the
/// ground), moves between the same pointing on the east and west pier side. Every move is planned,
/// checked against the limits and the Sun (endpoint and path), confirmed by the user and re-checked
/// right before it starts. Ending or aborting the mode restores tracking and the preferred pier side.
/// </summary>
public sealed class ManualFlipSession
{
    public static readonly TimeSpan PlanLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MoveBudget = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Deadman = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);

    /// <summary>The landmark must be far enough that its parallax stays below this, arcmin.</summary>
    public const double MaxParallaxArcmin = 0.2;

    /// <summary>A move ends within this of the captured position (arcmin on the sky), else it is corrected.</summary>
    public const double ArrivalToleranceArcmin = 1.0;

    public const int MaxCorrections = 2;

    public const string CollisionWarning =
        "Stay at the mount: on the counterweight-up side a low tube can hit the tripod or the pier.";

    public const string CapsWarning = "Cap the finder and the guide scope; never look through the eyepiece while the mount moves.";

    private readonly IConeMount mount;
    private readonly IPierSideControl pierSide;
    private readonly IClock clock;
    private readonly ISunProvider sun;
    private readonly ConeErrorStore store;
    private readonly ManualFlipOptions options;
    private readonly SafetyWatchdog watchdog;
    private readonly bool backgroundLoop;
    private readonly CancellationTokenSource cts = new();
    private readonly object sync = new();
    private readonly object trackingLock = new();
    private readonly Dictionary<string, ManualMovePlan> plans = new();

    private SessionPhase phase = SessionPhase.Preparing;
    private string message = "Starting the daytime check";
    private SessionError error;
    private bool trackingWasOn;
    private bool begun;
    private bool ended;
    private bool restorePending;
    private (double Ha, double Dec, PierSideLabel Side, double Alt, double Az)? captured;
    private EndpointCheck eastCheck;
    private EndpointCheck westCheck;
    private double? centredEast;
    private double? centredWest;
    private string lastMove;
    private CancellationTokenSource moveCts;
    private DateTime lastSeen;
    private DateTime lastActivity;
    private ConeResultRecord result;
    private ConeResultRecord previous;
    private Task loop = Task.CompletedTask;

    public ManualFlipSession(
        IConeMount mount,
        IPierSideControl pierSide,
        IClock clock,
        ISunProvider sun,
        ConeErrorStore store,
        ManualFlipOptions options,
        bool backgroundLoop = true)
    {
        this.mount = mount;
        this.pierSide = pierSide;
        this.clock = clock;
        this.sun = sun;
        this.store = store;
        this.options = options;
        this.backgroundLoop = backgroundLoop;
        watchdog = new SafetyWatchdog(mount, sun, clock, () => options.Limits.SunExclusionDegrees);
        watchdog.SunTooClose += OnSunTooClose;
        StartedUtc = clock.UtcNow;
        lastSeen = lastActivity = StartedUtc;
    }

    public DateTime StartedUtc { get; }

    internal SafetyWatchdog Watchdog => watchdog;

    public bool Finished
    {
        get
        {
            lock (sync)
            {
                return ended;
            }
        }
    }

    /// <summary>Records tracking and the preferred pier side, then turns tracking off for the whole mode.</summary>
    public void Begin()
    {
        MountSnapshot snap = mount.Snapshot();
        if (snap == null || !snap.Connected)
        {
            throw new ConeErrorException("MountNotConnected", "Connect the mount first.");
        }

        if (snap.AtPark)
        {
            throw new ConeErrorException("MountParked", "Unpark the mount first.");
        }

        string original = pierSide.Supported ? pierSide.ReadOriginal() : null;
        store.SaveJournal(new RestoreJournal
        {
            CreatedUtc = clock.UtcNow,
            MountName = options.MountKey,
            Mode = "manual",
            PreferredPierSide = original,
            Tracking = snap.Tracking
        });

        lock (sync)
        {
            trackingWasOn = snap.Tracking;
            begun = true;
        }

        if (snap.Tracking && !mount.SetTracking(false))
        {
            Logger.Warning("ConeError: the mount refused to turn tracking off");
        }

        SetPhase(SessionPhase.Ready, "Aim at a distant landmark, then capture the position.");
        if (backgroundLoop)
        {
            loop = Task.Run(() => LoopAsync(cts.Token));
        }
    }

    /// <summary>Called on every status poll: the client is still there.</summary>
    public void Heartbeat()
    {
        lock (sync)
        {
            lastSeen = clock.UtcNow;
        }
    }

    private void Touch()
    {
        lock (sync)
        {
            lastSeen = lastActivity = clock.UtcNow;
        }
    }

    public ManualFlipStatus Capture()
    {
        EnsureActive();
        Touch();
        MountSnapshot snap = mount.Snapshot();
        if (snap.Side == null)
        {
            throw new ConeErrorException("PierSideUnknown", "The mount does not report its pier side.");
        }

        if (snap.Slewing)
        {
            throw new ConeErrorException("MountMoving", "Wait until the mount stands still.");
        }

        (double alt, double az) = Spherical.AltAz(snap.HourAngleHours, snap.DeclinationDegrees, options.LatitudeDegrees);
        HaDec[] suns = Suns();
        (EndpointCheck east, EndpointCheck west) = LimitValidator.ValidateBothSides(
            options.Limits, snap.HourAngleHours, snap.DeclinationDegrees, options.LatitudeDegrees, suns);

        lock (sync)
        {
            captured = (snap.HourAngleHours, snap.DeclinationDegrees, snap.Side.Value, alt, az);
            eastCheck = east;
            westCheck = west;
            centredEast = snap.Side == PierSideLabel.East ? snap.HourAngleHours : null;
            centredWest = snap.Side == PierSideLabel.West ? snap.HourAngleHours : null;
            result = null;
            previous = null;
            phase = SessionPhase.Ready;
            message = "Position captured.";
        }

        return GetManualStatus();
    }

    public ManualMovePlan PlanMove(PierSideLabel target)
    {
        EnsureActive();
        Touch();
        (double Ha, double Dec, PierSideLabel Side, double Alt, double Az)? c;
        lock (sync)
        {
            c = captured;
            if (phase == SessionPhase.Moving)
            {
                throw new ConeErrorException("Busy", "A move is running.");
            }
        }

        if (c == null)
        {
            throw new ConeErrorException("NotCaptured", "Capture the position first.");
        }

        MountSnapshot snap = mount.Snapshot();
        if (!pierSide.Supported)
        {
            return Store(new ManualMovePlan
            {
                PlanId = NewId(),
                ToSide = target.ToString(),
                Refusal = pierSide.UnsupportedReason
            });
        }

        if (snap.Side == target)
        {
            return Store(new ManualMovePlan
            {
                PlanId = NewId(),
                FromSide = snap.Side?.ToString(),
                ToSide = target.ToString(),
                Refusal = $"The mount is already on the {target.ToString().ToLowerInvariant()} pier side."
            });
        }

        DateTime now = clock.UtcNow;
        EndpointCheck check = CheckMove(snap, target, c.Value.Ha, c.Value.Dec, now);
        DateTime? next = null;
        if (!check.Ok && check.Violations.All(v => v.Code is LimitViolationCode.SunTooClose or LimitViolationCode.SunTooCloseOnPath))
        {
            next = SunWindow.NextAllowed(
                t => CheckMove(snap, target, c.Value.Ha, c.Value.Dec, t).Ok,
                now,
                TimeSpan.FromHours(12),
                TimeSpan.FromMinutes(5),
                TimeSpan.Zero);
        }

        var warnings = new List<string> { CapsWarning };
        if (check.PastMeridianDegrees > 0.0)
        {
            warnings.Add(CollisionWarning);
        }

        return Store(new ManualMovePlan
        {
            PlanId = NewId(),
            ExpiresUtc = now + PlanLifetime,
            FromSide = snap.Side?.ToString(),
            ToSide = target.ToString(),
            HourAngle = c.Value.Ha,
            Declination = c.Value.Dec,
            Check = check,
            NextAllowedUtc = next,
            Warnings = warnings
        });
    }

    private EndpointCheck CheckMove(MountSnapshot from, PierSideLabel target, double ha, double dec, DateTime at)
    {
        HaDec[] suns = { sun.SunAt(at), sun.SunAt(at + MoveBudget) };
        return LimitValidator.ValidateMove(
            options.Limits,
            from.Side ?? ConeMath.Other(target),
            new HaDec(from.HourAngleHours, from.DeclinationDegrees),
            target,
            new HaDec(ha, dec),
            options.LatitudeDegrees,
            suns,
            options.StrictPath);
    }

    private ManualMovePlan Store(ManualMovePlan plan)
    {
        lock (sync)
        {
            plans.Clear();
            plans[plan.PlanId] = plan;
        }

        return plan;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Executes a confirmed plan after checking it again with the current position, time and Sun.</summary>
    public async Task MoveAsync(string planId)
    {
        EnsureActive();
        Touch();
        ManualMovePlan plan;
        (double Ha, double Dec, PierSideLabel Side, double Alt, double Az) c;
        CancellationTokenSource move;
        lock (sync)
        {
            if (planId == null || !plans.TryGetValue(planId, out plan))
            {
                throw new ConeErrorException("PlanUnknown", "This move was not planned or was replaced; plan it again.");
            }

            plans.Remove(planId);
            if (!plan.Ok)
            {
                throw new ConeErrorException("PlanRefused", plan.Refusal ?? LimitValidator.Describe(plan.Check.Violations));
            }

            if (clock.UtcNow > plan.ExpiresUtc)
            {
                throw new ConeErrorException("PlanExpired", "The confirmation expired; plan the move again.");
            }

            if (phase == SessionPhase.Moving || captured == null)
            {
                throw new ConeErrorException("Busy", "A move is running.");
            }

            c = captured.Value;
            moveCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            move = moveCts;
            phase = SessionPhase.Moving;
            message = $"Moving to the {plan.ToSide.ToLowerInvariant()} pier side";
        }

        PierSideLabel target = Enum.Parse<PierSideLabel>(plan.ToSide);
        try
        {
            MountSnapshot snap = mount.Snapshot();
            EndpointCheck recheck = CheckMove(snap, target, c.Ha, c.Dec, clock.UtcNow);
            if (!recheck.Ok)
            {
                throw new ConeErrorException("PlanRefused", "The move is no longer allowed: " + LimitValidator.Describe(recheck.Violations));
            }

            await pierSide.PrepareAsync(target, move.Token);
            await SlewToCapturedAsync(c.Ha, c.Dec, move.Token);

            MountSnapshot arrived = mount.Snapshot();
            if (arrived.Side != target)
            {
                throw new ConeErrorException(
                    "PierSideRefused",
                    $"The mount was asked for the {target.ToString().ToLowerInvariant()} pier side but reports {arrived.Side?.ToString().ToLowerInvariant() ?? "an unknown side"}; no further moves.");
            }

            for (int i = 0; i < MaxCorrections && !Arrived(arrived, c.Ha, c.Dec); i++)
            {
                EndpointCheck small = CheckMove(arrived, target, c.Ha, c.Dec, clock.UtcNow);
                if (!small.Ok)
                {
                    throw new ConeErrorException("PlanRefused", "The correction is not allowed: " + LimitValidator.Describe(small.Violations));
                }

                await SlewToCapturedAsync(c.Ha, c.Dec, move.Token);
                arrived = mount.Snapshot();
            }

            lock (sync)
            {
                phase = SessionPhase.Ready;
                lastMove = $"Arrived on the {target.ToString().ToLowerInvariant()} pier side, {ArrivalErrorArcmin(arrived, c.Ha, c.Dec):F1}′ from the captured position.";
                message = lastMove + " Compare the landmark; re-centre it with the jog pad for an estimate.";
                error = null;
            }
        }
        catch (OperationCanceledException)
        {
            SafeStop();
            lock (sync)
            {
                phase = ended ? phase : SessionPhase.Ready;
                lastMove = "The move was stopped.";
                message = error?.Message ?? lastMove;
            }
        }
        catch (ConeErrorException ex)
        {
            SafeStop();
            SetError(ex.Code, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: daytime move failed: {ex}");
            SafeStop();
            SetError("MoveFailed", ex.Message);
            throw new ConeErrorException("MoveFailed", ex.Message);
        }
        finally
        {
            KeepTrackingOff();
            lock (sync)
            {
                if (moveCts == move)
                {
                    moveCts = null;
                }
            }

            move.Dispose();
        }
    }

    private async Task SlewToCapturedAsync(double ha, double dec, CancellationToken token)
    {
        MountSnapshot snap = mount.Snapshot();
        double ra = Spherical.RightAscension(snap.LocalSiderealTimeHours, ha);
        watchdog.BeginOwnMotion();
        try
        {
            await mount.SlewAsync(ra, dec, token);
        }
        finally
        {
            watchdog.EndOwnMotion();
            KeepTrackingOff();
        }
    }

    /// <summary>
    /// Every driver turns tracking on for a goto; the daytime check keeps it off. Skipped once the mode
    /// has ended, so that a cancelled move cannot undo the restored tracking.
    /// </summary>
    private void KeepTrackingOff()
    {
        try
        {
            lock (trackingLock)
            {
                if (Finished)
                {
                    return;
                }

                MountSnapshot snap = mount.Snapshot();
                if (snap != null && snap.Tracking)
                {
                    mount.SetTracking(false);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: turning tracking off failed: {ex.Message}");
        }
    }

    private static double ArrivalErrorArcmin(MountSnapshot at, double ha, double dec)
        => Spherical.SeparationDegrees(at.HourAngleHours, at.DeclinationDegrees, ha, dec) * 60.0;

    private static bool Arrived(MountSnapshot at, double ha, double dec) => ArrivalErrorArcmin(at, ha, dec) <= ArrivalToleranceArcmin;

    /// <summary>
    /// The landmark is centred again on the current side: the daytime estimate
    /// c = ½ · (HA_W − HA_E) · cos δ + parallax, refused without the parallax inputs or above 0.2′.
    /// </summary>
    public ManualEstimate RecenterDone(double? axisOffsetMm, double? landmarkDistanceMeters)
    {
        EnsureActive();
        Touch();
        MountSnapshot snap = mount.Snapshot();
        double? east;
        double? west;
        double dec;
        lock (sync)
        {
            if (captured == null)
            {
                throw new ConeErrorException("NotCaptured", "Capture the position first.");
            }

            if (snap.Side == PierSideLabel.East)
            {
                centredEast = snap.HourAngleHours;
            }
            else if (snap.Side == PierSideLabel.West)
            {
                centredWest = snap.HourAngleHours;
            }

            east = centredEast;
            west = centredWest;
            dec = captured.Value.Dec;
        }

        if (east == null || west == null)
        {
            throw new ConeErrorException("NeedBothSides", "Centre the landmark on both pier sides first.");
        }

        double measured = ConeMath.ManualConeArcmin(west.Value, east.Value, dec);
        if (axisOffsetMm is not > 0 || landmarkDistanceMeters is not > 0)
        {
            return new ManualEstimate
            {
                MeasuredArcmin = measured,
                Refusal = "Enter the distance from the RA axis to the tube centre and the landmark distance; the parallax of a near landmark looks exactly like cone error."
            };
        }

        double parallax = ConeMath.ParallaxArcmin(axisOffsetMm.Value, landmarkDistanceMeters.Value);
        if (parallax > MaxParallaxArcmin)
        {
            double minDistance = axisOffsetMm.Value / 1000.0 / Math.Tan(Spherical.DegToRad(MaxParallaxArcmin / 60.0));
            return new ManualEstimate
            {
                MeasuredArcmin = measured,
                ParallaxArcmin = parallax,
                Refusal = $"The landmark is too close: its parallax of {parallax:F2}′ is above {MaxParallaxArcmin}′. Use a landmark at least {minDistance / 1000.0:F1} km away."
            };
        }

        double cone = measured + parallax;
        ShimEnd end = ConeMath.ShimEndFor(cone);
        var record = new ConeResultRecord
        {
            Id = NewId(),
            Mode = "manual",
            TimestampUtc = clock.UtcNow,
            ProfileId = options.Profile?.ProfileId,
            ProfileName = options.Profile?.ProfileName,
            MountName = options.MountKey,
            DeclinationDegrees = dec,
            DovetailMm = options.DovetailMm,
            ConeArcmin = cone,
            PairsUsed = 1,
            PairsTotal = 1,
            Quality = "DaytimeEstimate",
            Significant = true,
            ShimMm = options.DovetailMm.HasValue ? ConeMath.ShimThicknessMm(cone, options.DovetailMm.Value) : null,
            ShimEnd = end.ToString(),
            ParallaxArcmin = parallax
        };

        ConeResultRecord before = null;
        try
        {
            before = store.PreviousResult(record);
            store.AddResult(record);
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: saving the daytime estimate failed: {ex.Message}");
        }

        lock (sync)
        {
            result = record;
            previous = before;
        }

        return new ManualEstimate
        {
            MeasuredArcmin = measured,
            ParallaxArcmin = parallax,
            ConeArcmin = cone,
            ShimMm = record.ShimMm,
            ShimEnd = record.ShimEnd,
            Result = record,
            Previous = before
        };
    }

    /// <summary>Stops a running move now; the mode stays active.</summary>
    public void StopMove()
    {
        SafeStop();
        lock (sync)
        {
            moveCts?.Cancel();
        }
    }

    /// <summary>Stops the mount and ends the mode (restores tracking and the preferred pier side).</summary>
    public Task AbortAsync()
    {
        SafeStop();
        SetError("Aborted", "Aborted by the user.");
        return EndAsync(SessionPhase.Aborted);
    }

    public Task EndAsync() => EndAsync(SessionPhase.Complete);

    private async Task EndAsync(SessionPhase final)
    {
        bool wasOn;
        lock (sync)
        {
            if (ended)
            {
                return;
            }

            ended = true;
            moveCts?.Cancel();
            wasOn = trackingWasOn;
        }

        cts.Cancel();
        bool ok = true;
        if (begun)
        {
            try
            {
                lock (trackingLock)
                {
                    if (!mount.SetTracking(wasOn))
                    {
                        ok = false;
                        Logger.Warning("ConeError: the mount refused to restore tracking");
                    }
                }
            }
            catch (Exception ex)
            {
                ok = false;
                Logger.Error($"ConeError: restoring tracking failed: {ex.Message}");
            }

            try
            {
                await pierSide.RestoreAsync();
            }
            catch (Exception ex)
            {
                ok = false;
                Logger.Error($"ConeError: restoring the preferred pier side failed: {ex.Message}");
            }

            if (ok)
            {
                store.ClearJournal();
            }
        }

        lock (sync)
        {
            restorePending = !ok;
            phase = final;
            message = final == SessionPhase.Aborted ? "Aborted; tracking restored." : "Daytime check ended; tracking restored.";
        }

        try
        {
            await loop;
        }
        catch (Exception)
        {
            // the loop ends with the session
        }
    }

    private void SafeStop()
    {
        try
        {
            mount.Stop();
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: stopping the mount failed: {ex.Message}");
        }
    }

    private void OnSunTooClose(double separation)
    {
        CancellationTokenSource move;
        lock (sync)
        {
            move = moveCts;
            if (move != null)
            {
                error = new SessionError { Code = "SunTooClose", Message = $"The telescope came within {separation:F1}° of the Sun; the move was stopped. Cover the telescope." };
            }
        }

        // the watchdog has already stopped the mount; it never moves it away by itself
        move?.Cancel();
    }

    /// <summary>The 1 s background step: watchdog, deadman during moves, idle timeout.</summary>
    public void CheckBackground()
    {
        watchdog.Check();
        DateTime now = clock.UtcNow;
        bool moving;
        bool idle;
        lock (sync)
        {
            moving = phase == SessionPhase.Moving;
            idle = !moving && now - lastActivity > IdleTimeout && now - lastSeen > IdleTimeout;
        }

        if (moving && now - LastSeen > Deadman)
        {
            SafeStop();
            SetError("ConnectionLost", "The app stopped answering during a move, so the mount was stopped.");
            lock (sync)
            {
                moveCts?.Cancel();
            }
        }

        if (idle)
        {
            _ = EndAsync();
        }
    }

    private DateTime LastSeen
    {
        get
        {
            lock (sync)
            {
                return lastSeen;
            }
        }
    }

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                CheckBackground();
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: daytime check background step failed: {ex.Message}");
            }

            try
            {
                await clock.Delay(SafetyWatchdog.Interval, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private HaDec[] Suns()
    {
        DateTime now = clock.UtcNow;
        return new[] { sun.SunAt(now), sun.SunAt(now + MoveBudget) };
    }

    private void EnsureActive()
    {
        lock (sync)
        {
            if (ended)
            {
                throw new ConeErrorException("NotActive", "The daytime check has ended.");
            }
        }
    }

    private void SetPhase(SessionPhase p, string text)
    {
        lock (sync)
        {
            phase = p;
            message = text;
        }
    }

    private void SetError(string code, string text)
    {
        lock (sync)
        {
            error = new SessionError { Code = code, Message = text };
            message = text;
        }
    }

    private ManualFlipStatus GetManualStatus()
    {
        MountSnapshot snap = null;
        try
        {
            snap = mount.Snapshot();
        }
        catch (Exception)
        {
            // status without the live side
        }

        lock (sync)
        {
            return new ManualFlipStatus
            {
                CapturedHourAngle = captured?.Ha,
                CapturedDeclination = captured?.Dec,
                CapturedSide = captured?.Side.ToString(),
                CapturedAltitude = captured?.Alt,
                CapturedAzimuth = captured?.Az,
                CurrentSide = snap?.Side?.ToString(),
                TrackingWasOn = trackingWasOn,
                EastCheck = eastCheck,
                WestCheck = westCheck,
                CentredHourAngleEast = centredEast,
                CentredHourAngleWest = centredWest,
                LastMove = lastMove
            };
        }
    }

    public SessionStatus GetStatus()
    {
        ManualFlipStatus manual = GetManualStatus();
        lock (sync)
        {
            return new SessionStatus
            {
                Mode = "manual",
                Phase = phase,
                Message = message,
                StartedUtc = StartedUtc,
                Active = !ended,
                CurrentSide = manual.CurrentSide,
                Manual = manual,
                Sun = watchdog.Sun,
                Error = error,
                Result = result,
                Previous = previous,
                RestorePending = restorePending
            };
        }
    }
}
