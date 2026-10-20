using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

/// <summary>What the user confirmed for a run.</summary>
public sealed class AutoRunOptions
{
    public int Repetitions { get; init; }
    public double DeclinationDegrees { get; init; }
    public double ExposureSeconds { get; init; }
    public double? DovetailMm { get; init; }
    public PairTiming Timing { get; init; }
    public double LatitudeDegrees { get; init; }
    public double MinAltitudeDegrees { get; init; }

    /// <summary>The flip is made when the target is this far past the meridian, minutes of time.</summary>
    public double FlipAfterMinutes { get; init; }

    /// <summary>Ask once whether the counterweight is where the reported pier side says.</summary>
    public bool AskPierLabels { get; init; }

    public string MountKey { get; init; }
    public ProfileFacts Profile { get; init; }
}

/// <summary>
/// The measurement, done the way a sequence does a meridian flip. For each repetition a target just
/// east of the meridian is sampled on the west pier side, tracked until it is past the meridian,
/// flipped with NINA's meridian flip (each driver does that its own way) and sampled again on the
/// east side. ΔRA = RA_solved − RA_mount per side, the cone per pair, then the statistics and the
/// shim advice. Never syncs and never forces a pier side; the reported side is checked after every
/// move. Abort stops the mount at once; tracking is put back as it was.
/// </summary>
public sealed class AutoMeasurementSession
{
    /// <summary>The mount may drift this much during an exposure before the sample is discarded.</summary>
    public const double MaxMotionDuringExposureArcsec = 30.0;

    /// <summary>Offsets larger than this point at a sync, epoch or site problem, not at cone error.</summary>
    public const double MaxPlausibleOffsetDegrees = 2.0;

    public static readonly TimeSpan QuestionTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The wait for the meridian is done in steps of at most this, so that the status stays current.</summary>
    public static readonly TimeSpan WaitStep = TimeSpan.FromSeconds(2);

    private readonly IConeMount mount;
    private readonly IConeImager imager;
    private readonly IClock clock;
    private readonly ConeErrorStore store;
    private readonly AutoRunOptions options;
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
    private double? secondsUntilFlip;
    private SessionQuestion question;
    private TaskCompletionSource<bool> answer;
    private int questionIds;
    private bool pierLabelsChecked;
    private bool completed;
    private string abortCode;
    private string abortMessage;

    public AutoMeasurementSession(IConeMount mount, IConeImager imager, IClock clock, ConeErrorStore store, AutoRunOptions options)
    {
        this.mount = mount;
        this.imager = imager;
        this.clock = clock;
        this.store = store;
        this.options = options;
        StartedUtc = clock.UtcNow;
    }

    public DateTime StartedUtc { get; }

    public Task Completion { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the user confirmed the pier labels, so that the service can remember the mount.</summary>
    public event Action PierLabelsConfirmed;

    /// <summary>True once the run has ended and tracking was put back.</summary>
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

    /// <summary>Stops the mount and the exposure now and ends the run.</summary>
    public void Abort() => AbortWith("Aborted", "Aborted by the user.");

    private void AbortWith(string code, string reason)
    {
        lock (sync)
        {
            if (completed)
            {
                return;
            }

            abortCode ??= code;
            abortMessage ??= reason;
        }

        // stop first; cancel before the question is answered, so that nothing that wakes up reads a live token
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
                SecondsUntilFlip = secondsUntilFlip,
                Pairs = pairs.Select(Copy).ToList(),
                Question = question,
                Error = error,
                Result = result,
                Previous = previous
            };
        }
    }

    private void SetPhase(SessionPhase p, string text)
    {
        lock (sync)
        {
            phase = p;
            message = text;
            secondsUntilFlip = null;
        }
    }

    private async Task RunAsync()
    {
        CancellationToken token = cts.Token;
        bool? trackingBefore = null;
        try
        {
            MountSnapshot start = mount.Snapshot();
            trackingBefore = start?.Tracking;
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
            RestoreTracking(trackingBefore);
            lock (sync)
            {
                completed = true;
            }
        }
    }

    /// <summary>Every goto and flip turns tracking on; put it back off if it was off before the run.</summary>
    private void RestoreTracking(bool? before)
    {
        if (before != false)
        {
            return;
        }

        try
        {
            if (!mount.SetTracking(false))
            {
                Logger.Warning("ConeError: the mount did not turn tracking off again after the measurement");
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: turning tracking off after the measurement failed: {ex.Message}");
        }
    }

    private async Task MeasurePairAsync(int p, CancellationToken token)
    {
        PairTiming timing = options.Timing;
        double dec = options.DeclinationDegrees;

        // a target that reaches the west sample time just before the meridian
        MountSnapshot snap = mount.Snapshot();
        double slew = p == 0 ? timing.SlewSeconds : timing.FlipSeconds;
        double westMidIn = slew + timing.SettleSeconds + timing.ExposureSeconds / 2.0;
        double ra = Spherical.WrapHours24(
            snap.LocalSiderealTimeHours + TargetPlanner.Sidereal(westMidIn) + TargetPlanner.WestSampleMinutesBeforeMeridian / 60.0);

        var record = new ConePairRecord { Index = p, RightAscensionHours = ra, DeclinationDegrees = dec };
        lock (sync)
        {
            pairs.Add(record);
        }

        (double altitude, _) = Spherical.AltAz(0.0, dec, options.LatitudeDegrees);
        if (altitude < options.MinAltitudeDegrees)
        {
            throw new ConeErrorException("BelowMinimumAltitude", $"The target culminates at {altitude:F1}°, below the minimum altitude of {options.MinAltitudeDegrees:F0}°.");
        }

        // west side: the normal side for a target east of the meridian
        SetPhase(SessionPhase.Slewing, $"Pair {p + 1}: slewing to the target east of the meridian");
        await mount.SlewAsync(ra, dec, token);
        if (Side() != PierSideLabel.West)
        {
            SetPhase(SessionPhase.Flipping, $"Pair {p + 1}: flipping to the west pier side");
            await mount.FlipAsync(ra, dec, token);
        }

        RequireSide(PierSideLabel.West, "The mount does not go to the west pier side for a target just east of the meridian, where the counterweight is down. Check its preferred pier side setting.");

        if (options.AskPierLabels && !pierLabelsChecked)
        {
            await AskPierLabelsAsync(token);
            pierLabelsChecked = true;
        }

        SideSample west = await SampleAsync(p, PierSideLabel.West, ra, dec, token);
        if (!west.Ok)
        {
            lock (sync)
            {
                record.Problem = $"West side: {west.Problem}";
            }

            return;
        }

        // keep tracking until the target is past the meridian, then flip as a sequence would
        await WaitForFlipTimeAsync(p, ra, token);
        SetPhase(SessionPhase.Flipping, $"Pair {p + 1}: meridian flip to the east pier side");
        await mount.FlipAsync(ra, dec, token);
        RequireSide(PierSideLabel.East, "The mount did not flip to the east pier side. It needs to support NINA's meridian flip.");

        SideSample east = await SampleAsync(p, PierSideLabel.East, ra, dec, token);
        if (!east.Ok)
        {
            lock (sync)
            {
                record.Problem = $"East side: {east.Problem}";
            }

            return;
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

    private PierSideLabel? Side()
    {
        PierSideLabel? side = mount.Snapshot()?.Side;
        lock (sync)
        {
            currentSide = side;
        }

        return side;
    }

    private void RequireSide(PierSideLabel side, string problem)
    {
        PierSideLabel? reported = Side();
        if (reported != side)
        {
            throw new ConeErrorException(
                "PierSideNotReached",
                $"{problem} (The mount reports {reported?.ToString().ToLowerInvariant() ?? "an unknown side"}.)");
        }
    }

    private async Task WaitForFlipTimeAsync(int p, double ra, CancellationToken token)
    {
        double flipHourAngle = options.FlipAfterMinutes / 60.0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            double ha = Spherical.HourAngle(mount.Snapshot().LocalSiderealTimeHours, ra);
            double remaining = (flipHourAngle - ha) * 3600.0 / Spherical.SiderealPerSolar;
            if (remaining <= 0.0)
            {
                return;
            }

            lock (sync)
            {
                phase = SessionPhase.WaitingForMeridian;
                message = $"Pair {p + 1}: waiting for the target to cross the meridian";
                secondsUntilFlip = remaining;
            }

            await clock.Delay(TimeSpan.FromSeconds(Math.Min(remaining, WaitStep.TotalSeconds)), token);
        }
    }

    private sealed record SideSample(bool Ok, double DeltaRaHours, double DeltaDecDegrees, string Problem)
    {
        public static SideSample Failed(string problem) => new(false, double.NaN, double.NaN, problem);
    }

    private async Task<SideSample> SampleAsync(int p, PierSideLabel side, double ra, double dec, CancellationToken token)
    {
        string where = $"Pair {p + 1}, {side.ToString().ToLowerInvariant()} side";
        SetPhase(SessionPhase.Settling, $"{where}: settling");
        await clock.Delay(TimeSpan.FromSeconds(options.Timing.SettleSeconds), token);

        string problem = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            SetPhase(SessionPhase.Exposing, $"{where}: exposing and solving{(attempt > 0 ? " (second try)" : "")}");
            MountSnapshot before = mount.Snapshot();
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

    private async Task AskPierLabelsAsync(CancellationToken token)
    {
        TaskCompletionSource<bool> pending;
        lock (sync)
        {
            question = new SessionQuestion
            {
                Id = ++questionIds,
                Kind = "pierLabels",
                Text = "The mount reports the WEST pier side and points just east of the meridian. Is the counterweight bar on the EAST side of the mount right now (the tube on the west side)?"
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
            secondsUntilFlip = null;
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
            secondsUntilFlip = null;
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
