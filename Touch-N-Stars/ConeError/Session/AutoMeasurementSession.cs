using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

/// <summary>What the user confirmed for an automated run.</summary>
public sealed class AutoRunOptions
{
    public int Repetitions { get; init; }
    public double DeclinationDegrees { get; init; }
    public double ExposureSeconds { get; init; }
    public double? DovetailMm { get; init; }
    public PairTiming Timing { get; init; }
    public LimitSet Limits { get; init; }
    public double LatitudeDegrees { get; init; }

    /// <summary>The confirmed envelope: the largest |hour angle| of the plan, degrees.</summary>
    public double MaxAbsHourAngleDegrees { get; init; }

    /// <summary>Ask once whether the counterweight is where the reported pier side says.</summary>
    public bool AskPierLabels { get; init; }

    /// <summary>Measure every pair west first, east second (the plan's <see cref="AutoPlan.NormalSidesOnly"/>).</summary>
    public bool NormalSidesOnly { get; init; }

    public string MountKey { get; init; }
    public ProfileFacts Profile { get; init; }
}

/// <summary>
/// The automated measurement: for each repetition a target near the meridian, measured on both pier
/// sides (the side order alternates), ΔRA = RA_solved − RA_mount per side, the cone per pair, then the
/// statistics and the shim advice. Never syncs. Before every slew the move is re-validated against the
/// confirmed envelope, the limits and the Sun; any deviation fails the pair or the run instead of
/// widening the plan. Abort stops the mount at once; the preferred pier side is restored in any case.
/// </summary>
public sealed class AutoMeasurementSession
{
    /// <summary>Slack on the confirmed |hour angle| envelope for timing differences, degrees.</summary>
    public const double EnvelopeSlackDegrees = 1.0;

    /// <summary>The mount may drift this much during an exposure before the sample is discarded.</summary>
    public const double MaxMotionDuringExposureArcsec = 30.0;

    /// <summary>Offsets larger than this point at a sync, epoch or site problem, not at cone error.</summary>
    public const double MaxPlausibleOffsetDegrees = 2.0;

    public static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(10);

    private readonly IConeMount mount;
    private readonly IPierSideControl pierSide;
    private readonly IConeImager imager;
    private readonly IClock clock;
    private readonly ISunProvider sun;
    private readonly ConeErrorStore store;
    private readonly AutoRunOptions options;
    private readonly SafetyWatchdog watchdog;
    private readonly bool watchdogLoop;
    private readonly CancellationTokenSource cts = new();
    private readonly object sync = new();
    private readonly List<ConePairRecord> pairs = new();

    private SessionPhase phase = SessionPhase.Preparing;
    private string message = "Preparing";
    private SessionError error;
    private ConeResultRecord result;
    private ConeResultRecord previous;
    private PierSideLabel? currentSide;
    private int pairIndex;
    private SessionQuestion question;
    private TaskCompletionSource<bool> answer;
    private int questionIds;
    private bool restorePending;
    private bool pierLabelsChecked;
    private bool completed;
    private string abortCode;
    private string abortMessage;

    public AutoMeasurementSession(
        IConeMount mount,
        IPierSideControl pierSide,
        IConeImager imager,
        IClock clock,
        ISunProvider sun,
        ConeErrorStore store,
        AutoRunOptions options,
        bool watchdogLoop = true)
    {
        this.watchdogLoop = watchdogLoop;
        this.mount = mount;
        this.pierSide = pierSide;
        this.imager = imager;
        this.clock = clock;
        this.sun = sun;
        this.store = store;
        this.options = options;
        watchdog = new SafetyWatchdog(mount, sun, clock, () => options.Limits.SunExclusionDegrees, () => options.LatitudeDegrees, () => options.Limits.SunSetAltitudeDegrees);
        watchdog.SunTooClose += sep => AbortWith("SunTooClose", $"The telescope came within {sep:F1}° of the Sun; the measurement was stopped.");
        watchdog.UnexpectedMotion += reason => AbortWith("UnexpectedMotion", reason + " The measurement was stopped; restart it without syncing or moving the mount.");
        StartedUtc = clock.UtcNow;
    }

    public DateTime StartedUtc { get; }

    /// <summary>The watchdog, so that tests can run its checks without the 1 s loop.</summary>
    internal SafetyWatchdog Watchdog => watchdog;

    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the user confirmed the pier labels, so that the service can remember the mount.</summary>
    public event Action PierLabelsConfirmed;

    public bool Finished
    {
        get
        {
            lock (sync)
            {
                return completed;
            }
        }
    }

    public void Start()
    {
        Completion = Task.Run(RunAsync);
    }

    /// <summary>Stops the mount and the exposure now and cancels the run; restore happens on the run's way out.</summary>
    public void Abort() => AbortWith("Aborted", "Aborted by the user.");

    private void AbortWith(string code, string reason)
    {
        lock (sync)
        {
            if (phase is SessionPhase.Complete or SessionPhase.Failed or SessionPhase.Aborted)
            {
                return;
            }

            abortCode ??= code;
            abortMessage ??= reason;
        }

        // stop the mount first; then cancel, before the question is answered, so that nothing that
        // wakes up reads a live token
        SafeStop();
        try
        {
            cts.Cancel();
        }
        catch (AggregateException ex)
        {
            Logger.Warning($"ConeError: a cancellation callback failed: {ex.GetBaseException().Message}");
        }

        lock (sync)
        {
            answer?.TrySetResult(false);
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

        try
        {
            imager.AbortExposure();
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: aborting the exposure failed: {ex.Message}");
        }
    }

    public bool Answer(int id, bool yes)
    {
        lock (sync)
        {
            if (question == null || question.Id != id || answer == null)
            {
                return false;
            }

            answer.TrySetResult(yes);
            return true;
        }
    }

    public SessionStatus GetStatus()
    {
        lock (sync)
        {
            return new SessionStatus
            {
                Mode = "auto",
                Phase = phase,
                Message = message,
                StartedUtc = StartedUtc,
                Active = !completed,
                PairIndex = pairIndex,
                Repetitions = options.Repetitions,
                CurrentSide = currentSide?.ToString(),
                DeclinationDegrees = options.DeclinationDegrees,
                Pairs = pairs.Select(Copy).ToList(),
                Question = question,
                Sun = watchdog.Sun,
                Error = error,
                Result = result,
                Previous = previous,
                RestorePending = restorePending
            };
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

    private async Task RunAsync()
    {
        CancellationToken token = cts.Token;
        Task watch = watchdogLoop ? watchdog.RunAsync(token) : Task.CompletedTask;
        bool changedPierSide = false;
        try
        {
            string original = pierSide.ReadOriginal();
            store.SaveJournal(new RestoreJournal
            {
                CreatedUtc = clock.UtcNow,
                MountName = options.MountKey,
                Mode = "auto",
                PreferredPierSide = original
            });
            changedPierSide = true;

            MountSnapshot start = mount.Snapshot();
            // At home OnStepX ignores the preferred pier side and takes the side from the sign of the
            // hour angle (Goto.cpp setTarget); the first target is east of the meridian, so West.
            currentSide = start == null ? PierSideLabel.East : start.AtHome ? PierSideLabel.West : start.Side ?? PierSideLabel.East;
            for (int p = 0; p < options.Repetitions; p++)
            {
                token.ThrowIfCancellationRequested();
                lock (sync)
                {
                    pairIndex = p;
                }

                await MeasurePairAsync(p, token);
            }

            Finish();
        }
        catch (Exception) when (Volatile.Read(ref abortCode) != null)
        {
            // an abort also makes the goto or the exposure it interrupted fail; report the abort
            SafeStop();
            Fail(SessionPhase.Aborted, abortCode, abortMessage);
        }
        catch (ConeErrorException ex)
        {
            SafeStop();
            Fail(SessionPhase.Failed, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: measurement failed: {ex}");
            SafeStop();
            Fail(SessionPhase.Failed, "Error", ex.Message);
        }
        finally
        {
            cts.Cancel();
            await IgnoreAsync(watch);
            if (changedPierSide)
            {
                await RestoreAsync();
            }

            lock (sync)
            {
                completed = true;
            }
        }
    }

    private async Task RestoreAsync()
    {
        try
        {
            await pierSide.RestoreAsync();
            store.ClearJournal();
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: restoring the preferred pier side failed: {ex.Message}");
            lock (sync)
            {
                restorePending = true;
            }
        }
    }

    private static async Task IgnoreAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // the watchdog ends with the session
        }
    }

    private async Task MeasurePairAsync(int p, CancellationToken token)
    {
        PairTiming timing = options.Timing;
        PierSideLabel first = options.NormalSidesOnly ? PierSideLabel.West : currentSide ?? PierSideLabel.East;
        PierSideLabel second = ConeMath.Other(first);

        MountSnapshot snap = mount.Snapshot();
        double slew = p == 0
            ? timing.SlewSeconds
            : first != currentSide ? timing.FlipSeconds : Math.Min(timing.SlewSeconds, 30.0);
        double centre = slew + timing.SettleSeconds + timing.ExposureSeconds / 2.0 + timing.BetweenSidesSeconds / 2.0;
        double ra = Spherical.WrapHours24(snap.LocalSiderealTimeHours + TargetPlanner.Sidereal(centre));
        double dec = options.DeclinationDegrees;

        var record = new ConePairRecord { Index = p, RightAscensionHours = ra, DeclinationDegrees = dec };
        lock (sync)
        {
            pairs.Add(record);
        }

        SideSample east = null;
        SideSample west = null;
        foreach (PierSideLabel side in new[] { first, second })
        {
            double expectedIn = (side == first ? slew : timing.FlipSeconds) + timing.SettleSeconds + timing.ExposureSeconds / 2.0;
            token.ThrowIfCancellationRequested();
            SideSample sample = await MeasureSideAsync(p, side, ra, dec, expectedIn, canArriveAtOnce: side == first, token);
            if (!sample.Ok)
            {
                lock (sync)
                {
                    record.Problem = $"{side} side: {sample.Problem}";
                }

                return;
            }

            if (side == PierSideLabel.East)
            {
                east = sample;
            }
            else
            {
                west = sample;
            }
        }

        double cone = ConeMath.PairConeArcmin(west.DeltaRaHours, east.DeltaRaHours, dec);
        lock (sync)
        {
            record.DeltaRaEastArcmin = east.DeltaRaHours * ConeMath.ArcminPerHourOfRa * Math.Cos(Spherical.DegToRad(dec));
            record.DeltaRaWestArcmin = west.DeltaRaHours * ConeMath.ArcminPerHourOfRa * Math.Cos(Spherical.DegToRad(dec));
            record.DeltaDecWestMinusEastArcmin = (west.DeltaDecDegrees - east.DeltaDecDegrees) * 60.0;
            record.ConeArcmin = cone;
        }
    }

    private sealed record SideSample(bool Ok, double DeltaRaHours, double DeltaDecDegrees, string Problem)
    {
        public static SideSample Failed(string problem) => new(false, double.NaN, double.NaN, problem);
    }

    /// <param name="canArriveAtOnce">A goto that keeps the pier side can arrive at once: check the limits at the command too.</param>
    private async Task<SideSample> MeasureSideAsync(int p, PierSideLabel side, double ra, double dec, double expectedInSeconds, bool canArriveAtOnce, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        DateTime now = clock.UtcNow;
        MountSnapshot snap = mount.Snapshot();
        double lst = snap.LocalSiderealTimeHours + TargetPlanner.Sidereal(expectedInSeconds);
        double ha = Spherical.HourAngle(lst, ra);

        if (Math.Abs(ha * 15.0) > options.MaxAbsHourAngleDegrees + EnvelopeSlackDegrees)
        {
            return SideSample.Failed($"the target would be {Math.Abs(ha * 15.0):F1}° from the meridian, outside the confirmed plan; pair skipped.");
        }

        PierSideLabel fromSide = snap.Side ?? side;
        HaDec[] suns = { sun.SunAt(now), sun.SunAt(now.AddSeconds(expectedInSeconds + options.Timing.SampleSeconds)) };
        EndpointCheck check = LimitValidator.ValidateMove(
            options.Limits,
            fromSide,
            new HaDec(snap.HourAngleHours, snap.DeclinationDegrees),
            side,
            new HaDec(ha, dec),
            options.LatitudeDegrees,
            suns,
            strictPath: false);
        if (!check.Ok)
        {
            if (check.Violations.Any(v => v.Code is LimitViolationCode.SunTooClose or LimitViolationCode.SunTooCloseOnPath))
            {
                throw new ConeErrorException("SunTooClose", LimitValidator.Describe(check.Violations));
            }

            return SideSample.Failed(LimitValidator.Describe(check.Violations) + " Pair skipped.");
        }

        // the limits over the whole sample, as in the plan: from the goto (when it can be quick) to the end of the solve
        IReadOnlyList<LimitViolation> window = TargetPlanner.WindowViolations(
            options.Limits,
            side,
            ra,
            dec,
            options.LatitudeDegrees,
            snap.LocalSiderealTimeHours,
            canArriveAtOnce ? 0.0 : expectedInSeconds,
            expectedInSeconds + options.Timing.ExposureSeconds / 2.0 + options.Timing.SolveSeconds,
            suns[0]);
        if (window.Count > 0)
        {
            return SideSample.Failed(LimitValidator.Describe(window) + " Pair skipped.");
        }

        SetPhase(SessionPhase.Slewing, $"Pair {p + 1}: {(side == currentSide ? "slewing" : "flipping")} to the {side.ToString().ToLowerInvariant()} pier side");
        await pierSide.PrepareAsync(side, token);
        token.ThrowIfCancellationRequested();
        watchdog.BeginOwnMotion();
        try
        {
            await mount.SlewAsync(ra, dec, token);
        }
        finally
        {
            watchdog.EndOwnMotion();
        }

        token.ThrowIfCancellationRequested();
        MountSnapshot arrived = mount.Snapshot();
        RequireTracking(arrived, side);
        if (arrived.Side != side)
        {
            throw new ConeErrorException(
                "PierSideRefused",
                $"The mount was asked for the {side.ToString().ToLowerInvariant()} pier side but reports {arrived.Side?.ToString().ToLowerInvariant() ?? "an unknown side"}. It cannot be forced to the other side here, so the measurement stopped without further moves.");
        }

        lock (sync)
        {
            currentSide = side;
        }

        if (options.AskPierLabels && !pierLabelsChecked && side == PierSideLabel.East)
        {
            await AskPierLabelsAsync(token);
            pierLabelsChecked = true;
        }

        SetPhase(SessionPhase.Settling, $"Pair {p + 1}, {side.ToString().ToLowerInvariant()} side: settling");
        await clock.Delay(TimeSpan.FromSeconds(options.Timing.SettleSeconds), token);

        string problem = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            SetPhase(SessionPhase.Exposing, $"Pair {p + 1}, {side.ToString().ToLowerInvariant()} side: exposing and solving{(attempt > 0 ? " (second try)" : "")}");
            MountSnapshot before = mount.Snapshot();
            RequireTracking(before, side);
            SolveOutcome solved = await imager.CaptureAndSolveAsync(options.ExposureSeconds, ra, dec, token);
            MountSnapshot after = mount.Snapshot();
            token.ThrowIfCancellationRequested();

            if (!solved.Success)
            {
                problem = $"the plate solve failed ({solved.Error})";
                continue;
            }

            double moved = Spherical.SeparationDegrees(before.RightAscensionHours, before.DeclinationDegrees, after.RightAscensionHours, after.DeclinationDegrees) * 3600.0;
            if (moved > MaxMotionDuringExposureArcsec || before.Side != after.Side)
            {
                problem = $"the mount moved {moved:F0}″ during the exposure";
                continue;
            }

            double mountRa = Spherical.WrapHours24(before.RightAscensionHours + Spherical.WrapHours12(after.RightAscensionHours - before.RightAscensionHours) / 2.0);
            double mountDec = (before.DeclinationDegrees + after.DeclinationDegrees) / 2.0;
            double dRa = ConeMath.DeltaRaHours(solved.RightAscensionHours, mountRa);
            double dDec = solved.DeclinationDegrees - mountDec;
            double onSky = Math.Abs(dRa * 15.0 * Math.Cos(Spherical.DegToRad(dec)));
            if (onSky > MaxPlausibleOffsetDegrees || Math.Abs(dDec) > MaxPlausibleOffsetDegrees)
            {
                return SideSample.Failed(
                    $"the solved position is {Math.Max(onSky, Math.Abs(dDec)):F1}° from the mount position; that is not cone error but a sync, epoch, time or site problem.");
            }

            return new SideSample(true, dRa, dDec, null);
        }

        return SideSample.Failed(problem);
    }

    /// <summary>
    /// A mount that stops tracking (OnStepX does at its meridian limit, and reports the goto as done)
    /// drifts through every exposure; stop with the reason instead of failing sample after sample.
    /// </summary>
    private static void RequireTracking(MountSnapshot snapshot, PierSideLabel side)
    {
        if (snapshot is { Connected: true, Tracking: false })
        {
            throw new ConeErrorException(
                "TrackingStopped",
                $"The mount stopped tracking on the {side.ToString().ToLowerInvariant()} pier side, most likely at its meridian limit. The measurement stopped; check the mount's limits.");
        }
    }

    private async Task AskPierLabelsAsync(CancellationToken token)
    {
        TaskCompletionSource<bool> pending;
        lock (sync)
        {
            question = new SessionQuestion
            {
                Id = ++questionIds,
                Kind = "pierLabels",
                Text = "The mount reports the EAST pier side and points near the meridian. Is the counterweight bar on the WEST side of the mount right now (the tube on the east side)?"
            };
            answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            pending = answer;
            phase = SessionPhase.WaitingForAnswer;
            message = "Waiting for the pier side check";
        }

        Task timeout = clock.Delay(QuestionTimeout, token);
        Task finished = await Task.WhenAny(pending.Task, timeout);
        bool yes = finished == pending.Task && pending.Task.Result;
        lock (sync)
        {
            question = null;
            answer = null;
        }

        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref abortCode) != null)
        {
            throw new OperationCanceledException(token);
        }

        if (finished != pending.Task)
        {
            throw new ConeErrorException("QuestionTimeout", "No answer to the pier side check; the measurement stopped.");
        }

        if (!yes)
        {
            throw new ConeErrorException(
                "PierLabelsInverted",
                "The mount reports its pier side the other way round. With these labels the shim advice would point at the wrong end, so the measurement stopped. Check the driver's pier side setting.");
        }

        PierLabelsConfirmed?.Invoke();
    }

    private void Finish()
    {
        List<ConePairRecord> valid;
        lock (sync)
        {
            valid = pairs.Where(r => r.ConeArcmin.HasValue).ToList();
        }

        int required = Math.Max(2, (int)Math.Ceiling(options.Repetitions / 2.0));
        if (valid.Count < required)
        {
            Fail(
                SessionPhase.Failed,
                "TooFewPairs",
                $"Only {valid.Count} of {options.Repetitions} pairs could be measured; at least {required} are needed. " + string.Join(" ", pairs.Where(r => r.Problem != null).Select(r => $"Pair {r.Index + 1}: {r.Problem}")));
            return;
        }

        ConeSummary summary = ConeStatistics.Summarize(valid.Select(r => r.ConeArcmin.Value).ToList());
        lock (sync)
        {
            foreach (int i in summary.RejectedIndices)
            {
                valid[i].Rejected = true;
            }
        }

        var record = new ConeResultRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Mode = "auto",
            TimestampUtc = clock.UtcNow,
            ProfileId = options.Profile?.ProfileId,
            ProfileName = options.Profile?.ProfileName,
            MountName = options.MountKey,
            DeclinationDegrees = options.DeclinationDegrees,
            DovetailMm = options.DovetailMm,
            ConeArcmin = summary.Mean,
            StdDevArcmin = Finite(summary.StdDev),
            StdErrorArcmin = Finite(summary.StdError),
            MedianArcmin = Finite(summary.Median),
            PairsUsed = summary.Used,
            PairsTotal = options.Repetitions,
            Quality = summary.Quality.ToString(),
            Significant = summary.Significant,
            ShimMm = options.DovetailMm.HasValue ? ConeMath.ShimThicknessMm(summary.Mean, options.DovetailMm.Value) : null,
            ShimEnd = (summary.Significant ? ConeMath.ShimEndFor(summary.Mean) : ShimEnd.None).ToString(),
            Pairs = pairs.Select(Copy).ToList()
        };

        ConeResultRecord before = null;
        try
        {
            before = store.PreviousResult(record);
            store.AddResult(record);
        }
        catch (Exception ex)
        {
            Logger.Error($"ConeError: saving the result failed: {ex.Message}");
        }

        lock (sync)
        {
            result = record;
            previous = before;
            phase = SessionPhase.Complete;
            message = "Measurement complete";
        }
    }

    private static double? Finite(double value) => double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    private void Fail(SessionPhase p, string code, string text)
    {
        lock (sync)
        {
            phase = p;
            message = text;
            error = new SessionError { Code = code, Message = text };
            question = null;
        }
    }

    private static ConePairRecord Copy(ConePairRecord r) => new()
    {
        Index = r.Index,
        RightAscensionHours = r.RightAscensionHours,
        DeclinationDegrees = r.DeclinationDegrees,
        DeltaRaEastArcmin = r.DeltaRaEastArcmin,
        DeltaRaWestArcmin = r.DeltaRaWestArcmin,
        DeltaDecWestMinusEastArcmin = r.DeltaDecWestMinusEastArcmin,
        ConeArcmin = r.ConeArcmin,
        Rejected = r.Rejected,
        Problem = r.Problem
    };
}
