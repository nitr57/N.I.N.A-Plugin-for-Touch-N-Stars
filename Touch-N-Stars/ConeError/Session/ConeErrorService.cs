using System;
using System.Collections.Generic;
using System.Linq;
using NINA.Core.Utility;
using TouchNStars.ConeError.Domain;

namespace TouchNStars.ConeError.Session;

public sealed class AutoPlanRequest
{
    public double? DovetailMm { get; set; }
    public int? Repetitions { get; set; }
    public double? Declination { get; set; }
    public double? ExposureSeconds { get; set; }

    /// <summary>The phone's clock, for the server-side clock check.</summary>
    public DateTime? ClientTimeUtc { get; set; }
}

public sealed class AutoPlanResponse
{
    public string PlanId { get; init; }
    public DateTime ExpiresUtc { get; init; }

    /// <summary>Seconds the plan is valid from its creation, so that clients need not compare clocks.</summary>
    public double ExpiresInSeconds { get; init; }

    public AutoPlan Plan { get; init; }
    public PairTiming Timing { get; init; }
    public double ExposureSeconds { get; init; }
    public double DovetailMm { get; init; }
    public int Repetitions { get; init; }
    public bool AskPierLabels { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class TimeCheck
{
    public DateTime SystemUtc { get; init; }
    public DateTime? ClientUtc { get; init; }
    public double? SkewSeconds { get; init; }
    public bool LooksUnset { get; init; }
    public double? MountLstHours { get; init; }
    public double PiLstHours { get; init; }

    /// <summary>Mount sidereal time minus the Pi's (from its clock and the profile longitude), minutes.</summary>
    public double? LstSkewMinutes { get; init; }

    /// <summary>The Sun's altitude now at the profile site, degrees.</summary>
    public double SunAltitudeDegrees { get; init; }
}

public sealed class Preconditions
{
    public MountSnapshot Mount { get; init; }
    public ProfileFacts Profile { get; init; }
    public ObservatoryState Observatory { get; init; }
    public TimeCheck Time { get; init; }
    public IReadOnlyList<string> Blockers { get; init; }
    public IReadOnlyList<string> Warnings { get; init; }
    public bool Ready => Blockers.Count == 0;
}

/// <summary>
/// Entry point of the cone error wizard for the API: preconditions, plans, the one running
/// measurement, the persisted settings and results.
/// </summary>
public sealed class ConeErrorService
{
    public static readonly TimeSpan AutoPlanLifetime = TimeSpan.FromSeconds(60);

    /// <summary>Clock differences that warn and that block, seconds.</summary>
    public const double TimeSkewWarnSeconds = 120.0;
    public const double TimeSkewBlockSeconds = 600.0;

    /// <summary>A mount sidereal time this far off the Pi's means the mount's clock or site is wrong, minutes.</summary>
    public const double LstSkewWarnMinutes = 1.0;
    public const double LstSkewBlockMinutes = 8.0;

    /// <summary>The measurement runs at night: the Sun must be below this altitude (end of civil twilight), degrees.</summary>
    public const double MaxSunAltitudeDegrees = -6.0;

    /// <summary>Planning assumptions on top of the profile settle time, seconds.</summary>
    public const double SlewSeconds = 120.0;
    public const double FlipSeconds = 90.0;
    public const double ExtraSettleSeconds = 2.0;
    public const double SolveSeconds = 30.0;

    private static ConeErrorService instance;

    private readonly Func<IConeEnvironment> environment;
    private readonly object sync = new();
    private readonly Dictionary<string, (AutoPlanRequest Request, AutoPlanResponse Response)> autoPlans = new();
    private AutoMeasurementSession session;

    public ConeErrorService(Func<IConeEnvironment> environment, ConeErrorStore store)
    {
        this.environment = environment;
        Store = store;
    }

    /// <summary>The service the API uses, bound to NINA through <see cref="Adapters.NinaConeEnvironment"/>.</summary>
    public static ConeErrorService Instance
    {
        get
        {
            lock (typeof(ConeErrorService))
            {
                return instance ??= new ConeErrorService(
                    () => new Adapters.NinaConeEnvironment(TouchNStars.Mediators),
                    new ConeErrorStore(ConeErrorStore.DefaultDirectory));
            }
        }
    }

    public ConeErrorStore Store { get; }

    public ConeErrorSettings GetSettings() => Store.LoadSettings();

    /// <summary>Applies only the fields a client sent (JSON object) on top of the stored settings.</summary>
    public ConeErrorSettings PatchSettings(string json)
    {
        ConeErrorSettings current = Store.LoadSettings();
        // a copy: PopulateObject would append a sent list to the stored one in place
        List<string> verified = current.VerifiedPierLabels.ToList();
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                Newtonsoft.Json.JsonConvert.PopulateObject(json, current);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new ConeErrorException("BadRequest", $"The settings are not valid: {ex.Message}");
            }
        }

        current.VerifiedPierLabels = verified;
        return Store.SaveSettings(current);
    }

    public IReadOnlyList<ConeResultRecord> Results() => Store.LoadResults().OrderByDescending(r => r.TimestampUtc).ToList();

    private bool IsRunning() => session != null && !session.Finished;

    #region Preconditions

    public Preconditions GetPreconditions(DateTime? clientUtc)
    {
        IConeEnvironment env = environment();
        MountSnapshot mount = SafeSnapshot(env.Mount);
        ProfileFacts profile = env.Observatory.Profile();
        ObservatoryState state = env.Observatory.State();
        TimeCheck time = CheckTime(env.Clock.UtcNow, clientUtc, mount, profile);

        var blockers = new List<string>();
        var warnings = new List<string>();

        if (mount == null || !mount.Connected)
        {
            blockers.Add("Connect the mount.");
        }
        else if (mount.AtPark)
        {
            blockers.Add("Unpark the mount.");
        }

        if (!state.CameraConnected)
        {
            blockers.Add("Connect the camera.");
        }
        else if (state.CameraExposing)
        {
            blockers.Add("The camera is exposing; stop the exposure or loop first.");
        }

        if (profile.SolverProblem != null)
        {
            blockers.Add(profile.SolverProblem);
        }

        if (!(profile.FocalLength > 0.0))
        {
            blockers.Add("Set the telescope focal length in the profile; the plate solver needs it.");
        }

        if (profile.Latitude == 0.0 && profile.Longitude == 0.0)
        {
            blockers.Add("Set the site location in the profile (latitude and longitude are 0).");
        }

        if (state.SequenceRunning)
        {
            blockers.Add("A sequence is running; stop it first.");
        }

        if (state.InternalGuiderGuiding)
        {
            blockers.Add("The internal guider is guiding; stop guiding first.");
        }

        if (time.LooksUnset)
        {
            blockers.Add($"The Pi's clock looks unset ({time.SystemUtc:yyyy-MM-dd HH:mm} UTC). Set the time first.");
        }
        else if (time.SkewSeconds is { } skew && Math.Abs(skew) > TimeSkewBlockSeconds)
        {
            blockers.Add($"The Pi's clock is {Math.Abs(skew) / 60.0:F0} minutes off this device's clock. Set the time first.");
        }
        else if (time.SkewSeconds is { } small && Math.Abs(small) > TimeSkewWarnSeconds)
        {
            warnings.Add($"The Pi's clock is {Math.Abs(small) / 60.0:F1} minutes off this device's clock.");
        }

        if (time.LstSkewMinutes is { } lstSkew && Math.Abs(lstSkew) > LstSkewBlockMinutes)
        {
            blockers.Add($"The mount's sidereal time is {Math.Abs(lstSkew):F0} minutes off the Pi's: the mount's time or site is wrong.");
        }
        else if (time.LstSkewMinutes is { } lstSmall && Math.Abs(lstSmall) > LstSkewWarnMinutes)
        {
            warnings.Add($"The mount's sidereal time is {Math.Abs(lstSmall):F1} minutes off the Pi's; check the mount's time and site.");
        }

        if (time.SunAltitudeDegrees > MaxSunAltitudeDegrees)
        {
            blockers.Add($"The Sun is {time.SunAltitudeDegrees:F0}° high. The measurement points near the meridian and needs a dark sky; start it after the end of civil twilight.");
        }

        warnings.Add("Stop PHD2 guiding if it runs; it cannot be detected here.");

        return new Preconditions
        {
            Mount = mount,
            Profile = profile,
            Observatory = state,
            Time = time,
            Blockers = blockers,
            Warnings = warnings
        };
    }

    public static TimeCheck CheckTime(DateTime systemUtc, DateTime? clientUtc, MountSnapshot mount, ProfileFacts profile)
    {
        double piLst = SunEphemeris.LocalSiderealTimeHours(systemUtc, profile.Longitude);
        (double sunHa, double sunDec) = SunEphemeris.HourAngleDeclination(systemUtc, profile.Longitude);
        return new TimeCheck
        {
            SystemUtc = systemUtc,
            ClientUtc = clientUtc,
            SkewSeconds = clientUtc.HasValue ? (systemUtc - clientUtc.Value).TotalSeconds : null,
            LooksUnset = systemUtc.Year < 2025,
            MountLstHours = mount?.LocalSiderealTimeHours,
            PiLstHours = piLst,
            LstSkewMinutes = mount is { Connected: true } ? Spherical.WrapHours12(mount.LocalSiderealTimeHours - piLst) * 60.0 : null,
            SunAltitudeDegrees = Spherical.AltAz(sunHa, sunDec, profile.Latitude).Altitude
        };
    }

    private static MountSnapshot SafeSnapshot(IConeMount mount)
    {
        try
        {
            return mount.Snapshot();
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: reading the mount failed: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Measurement

    private static string MountKey(ProfileFacts profile, MountSnapshot mount) => $"{profile.ProfileId}|{mount?.Name}";

    public AutoPlanResponse PlanAuto(AutoPlanRequest request)
    {
        Preconditions pre = GetPreconditions(request.ClientTimeUtc);
        if (!pre.Ready)
        {
            throw new ConeErrorException("PreconditionsNotMet", string.Join(" ", pre.Blockers));
        }

        if (request.DovetailMm is not > 0)
        {
            throw new ConeErrorException("DovetailRequired", "Enter the distance between the two mounting points on the dovetail (mm).");
        }

        AutoPlanResponse response = BuildAutoPlan(environment(), request, Store.LoadSettings(), pre);
        lock (sync)
        {
            autoPlans.Clear();
            autoPlans[response.PlanId] = (request, response);
        }

        return response;
    }

    private static AutoPlanResponse BuildAutoPlan(IConeEnvironment env, AutoPlanRequest request, ConeErrorSettings settings, Preconditions pre)
    {
        ProfileFacts profile = pre.Profile;
        double exposure = request.ExposureSeconds is > 0 and <= 120 ? request.ExposureSeconds.Value : settings.ExposureSeconds ?? profile.PlateSolveExposureSeconds;
        var timing = new PairTiming(
            SlewSeconds + profile.SettleSeconds,
            FlipSeconds + profile.SettleSeconds,
            ExtraSettleSeconds,
            exposure,
            SolveSeconds);
        DateTime now = env.Clock.UtcNow;
        int repetitions = request.Repetitions ?? settings.Repetitions;

        AutoPlan plan = TargetPlanner.Plan(new PlannerInput
        {
            NowUtc = now,
            LocalSiderealTimeHours = pre.Mount.LocalSiderealTimeHours,
            LatitudeDegrees = profile.Latitude,
            Repetitions = repetitions,
            Declination = request.Declination,
            Timing = timing,
            MinAltitudeDegrees = settings.MinAltitudeDegrees,
            HorizonAltitudeAt = profile.HorizonAltitudeAt,
            FlipAfterMinutes = profile.MinutesAfterMeridian,
            MaxMinutesAfterMeridian = profile.MaxMinutesAfterMeridian
        });

        return new AutoPlanResponse
        {
            PlanId = Guid.NewGuid().ToString("N"),
            ExpiresUtc = now + AutoPlanLifetime,
            ExpiresInSeconds = AutoPlanLifetime.TotalSeconds,
            Plan = plan,
            Timing = timing,
            ExposureSeconds = exposure,
            DovetailMm = request.DovetailMm ?? 0.0,
            Repetitions = repetitions,
            AskPierLabels = !settings.VerifiedPierLabels.Contains(MountKey(profile, pre.Mount)),
            Warnings = pre.Warnings
        };
    }

    /// <summary>Starts a confirmed plan; the user must confirm that no pointing model is active in the mount.</summary>
    public SessionStatus StartAuto(string planId, bool noPointingModel)
    {
        IConeEnvironment env = environment();
        (AutoPlanRequest Request, AutoPlanResponse Response) planned;
        lock (sync)
        {
            if (planId == null || !autoPlans.TryGetValue(planId, out planned))
            {
                throw new ConeErrorException("PlanUnknown", "This plan is unknown or was replaced; plan again.");
            }

            autoPlans.Remove(planId);
            if (env.Clock.UtcNow > planned.Response.ExpiresUtc)
            {
                throw new ConeErrorException("PlanExpired", "The confirmation expired; plan again.");
            }

            if (IsRunning())
            {
                throw new ConeErrorException("Busy", "A measurement is already running.");
            }
        }

        if (!noPointingModel)
        {
            throw new ConeErrorException("PointingModelNotConfirmed", "Confirm that no pointing model is active in the mount; it would hide the cone error.");
        }

        Preconditions pre = GetPreconditions(null);
        if (!pre.Ready)
        {
            throw new ConeErrorException("PreconditionsNotMet", string.Join(" ", pre.Blockers));
        }

        ConeErrorSettings settings = Store.LoadSettings();
        AutoPlanResponse plan = planned.Response;
        string key = MountKey(pre.Profile, pre.Mount);
        var run = new AutoMeasurementSession(
            env.Mount,
            env.Imager,
            env.Clock,
            Store,
            new AutoRunOptions
            {
                Repetitions = plan.Repetitions,
                DeclinationDegrees = plan.Plan.DeclinationDegrees,
                ExposureSeconds = plan.ExposureSeconds,
                DovetailMm = plan.DovetailMm,
                Timing = plan.Timing,
                LatitudeDegrees = pre.Profile.Latitude,
                MinAltitudeDegrees = settings.MinAltitudeDegrees,
                FlipAfterMinutes = plan.Plan.FlipAfterMinutes,
                AskPierLabels = !settings.VerifiedPierLabels.Contains(key),
                MountKey = key,
                Profile = pre.Profile
            });
        run.PierLabelsConfirmed += () => RememberPierLabels(key);

        lock (sync)
        {
            if (IsRunning())
            {
                throw new ConeErrorException("Busy", "A measurement is already running.");
            }

            session = run;
        }

        run.Start();
        return run.GetStatus();
    }

    private void RememberPierLabels(string key)
    {
        ConeErrorSettings settings = Store.LoadSettings();
        if (!settings.VerifiedPierLabels.Contains(key))
        {
            settings.VerifiedPierLabels.Add(key);
            Store.SaveSettings(settings);
        }
    }

    public bool Answer(int id, bool yes)
    {
        AutoMeasurementSession run;
        lock (sync)
        {
            run = session;
        }

        return run != null && run.Answer(id, yes);
    }

    public SessionStatus Status()
    {
        AutoMeasurementSession run;
        lock (sync)
        {
            run = session;
        }

        return run?.GetStatus() ?? new SessionStatus();
    }

    /// <summary>Stops the mount at once and ends the measurement; with nothing running it still stops the mount.</summary>
    public SessionStatus Abort()
    {
        AutoMeasurementSession run;
        lock (sync)
        {
            run = session;
        }

        if (run != null && !run.Finished)
        {
            run.Abort();
        }
        else
        {
            try
            {
                environment().Mount.Stop();
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: stop failed: {ex.Message}");
            }
        }

        return Status();
    }

    /// <summary>Plugin teardown: stop a running measurement.</summary>
    public void Shutdown()
    {
        try
        {
            AutoMeasurementSession run;
            lock (sync)
            {
                run = session;
            }

            run?.Abort();
            run?.Completion.Wait(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: shutdown failed: {ex.Message}");
        }
    }

    #endregion
}
