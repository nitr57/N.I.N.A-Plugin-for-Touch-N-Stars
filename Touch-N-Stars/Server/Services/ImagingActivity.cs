using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using TouchNStars.Server.Controllers;

namespace TouchNStars.Server.Services;

/// <summary>
/// Whether the sequence or another job holds the equipment, for TNS jobs and commands that would otherwise take the
/// camera, focuser, filter wheel or mount over mid-way. On the desktop the user starts them deliberately; from TNS
/// they are one tap away while something else runs.
/// </summary>
public static class ImagingActivity
{
    /// <summary>True while the advanced sequencer is running.</summary>
    public static bool IsSequenceRunning()
    {
        try
        {
            return TouchNStars.Mediators?.Sequence?.IsAdvancedSequenceRunning() == true;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read the sequencer state: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Why a TNS job or command that uses <paramref name="devices"/> must not start now, or null: the end-of-night
    /// shutdown runs, the advanced sequence runs (also while it waits for its start), or another job that uses one of
    /// these devices runs (the activities the end-of-night shutdown stops, the Aberration Inspector, the star detection
    /// optimizer's live sweep, a 10micron model build). <paramref name="self"/> names the caller's own activities, which
    /// do not count. Never throws; a state that can't be read counts as not busy. Two starts in the same instant can
    /// both pass.
    /// </summary>
    public static string BusyReason(RigDevices devices, params string[] self)
    {
        try
        {
            return FindBusyReason(devices, self);
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read whether the equipment is in use: {ex}");
            return null;
        }
    }

    private static string FindBusyReason(RigDevices devices, string[] self)
    {
        bool Mine(string name) => self.Contains(name);

        if (!Mine(ActivityNames.EndOfNight) && Read(() => SecureObservatoryService.Instance.GetStatus().State == SecureRunState.Running))
        {
            return "The end-of-night shutdown is running (it stops everything and parks the mount). Wait until it has finished.";
        }

        if (IsSequenceRunning())
        {
            return "A sequence is running (also while it waits for its start). It uses the equipment: stop it first.";
        }

        foreach (var activity in RunningActivities())
        {
            if (Mine(activity.Name) || (DevicesOf(activity.Name) & devices) == 0)
            {
                continue;
            }

            // Without HocusFocus's own flag the autofocus state is TNS's afRun, which can stay set after an autofocus
            // that NINA, the sequence or MCP ended: not trusted for a refusal.
            if (activity.Name == ActivityNames.Autofocus && FilterOffsetController.IsHocusFocusAutoFocusInProgress() != true)
            {
                continue;
            }

            return $"Another job uses the same equipment ({activity.Name}). Let it finish or stop it first.";
        }

        if ((devices & (RigDevices.Camera | RigDevices.Focuser)) != 0)
        {
            if (!Mine(ActivityNames.InspectorAnalysis) && Read(StarDetectionOptimizerSession.InspectorCaptureInProgress))
            {
                return "An Aberration Inspector analysis is running. It takes exposures and moves the focuser: let it finish or cancel it first.";
            }

            if (!Mine(ActivityNames.OptimizerSweep) && Read(() => StarDetectionOptimizerSession.IsDrivingFocuser))
            {
                return "The star detection optimizer is running a live sweep. It takes exposures and moves the focuser: let it finish or cancel it first.";
            }
        }

        if ((devices & (RigDevices.Mount | RigDevices.Camera)) != 0 && !Mine(ActivityNames.TenMicronBuild) && TenMicronController.IsModelBuildRunning())
        {
            return "A 10micron model build is running. It slews the mount and takes exposures: let it finish or cancel it first.";
        }

        return null;
    }

    private static readonly Dictionary<string, RigDevices> ActivityDevices = new()
    {
        [ActivityNames.FilterOffset] = RigDevices.Camera | RigDevices.Focuser | RigDevices.FilterWheel,
        [ActivityNames.Autofocus] = RigDevices.Camera | RigDevices.Focuser | RigDevices.FilterWheel,
        [ActivityNames.Flats] = RigDevices.Camera | RigDevices.FilterWheel | RigDevices.FlatPanel,
        [ActivityNames.ConeError] = RigDevices.Mount | RigDevices.Camera,
        [ActivityNames.GuidingStart] = RigDevices.Mount | RigDevices.GuideCamera,
        [ActivityNames.DarkLibrary] = RigDevices.GuideCamera,
        [ActivityNames.GuidingCoach] = RigDevices.Mount | RigDevices.GuideCamera,
    };

    /// <summary>An activity this table does not know counts as using everything.</summary>
    private static RigDevices DevicesOf(string activity)
        => ActivityDevices.TryGetValue(activity, out var devices) ? devices : RigDevices.All;

    // The end-of-night shutdown's own list (one place for both), read through its rig, which holds no state.
    private static IReadOnlyList<RigActivity> RunningActivities()
    {
        try
        {
            return new MediatorSecureObservatoryRig().RunningActivities();
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read the running TNS activities: {ex.Message}");
            return Array.Empty<RigActivity>();
        }
    }

    private static bool Read(Func<bool> state)
    {
        try
        {
            return state();
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not read whether a job is running: {ex.Message}");
            return false;
        }
    }
}

/// <summary>The devices a TNS job or command uses, for <see cref="ImagingActivity.BusyReason"/>.</summary>
[Flags]
public enum RigDevices
{
    None = 0,
    Camera = 1,
    Focuser = 2,
    FilterWheel = 4,
    Mount = 8,
    GuideCamera = 16,
    FlatPanel = 32,
    All = Camera | Focuser | FilterWheel | Mount | GuideCamera | FlatPanel,
}

/// <summary>
/// Names of the jobs <see cref="ImagingActivity.BusyReason"/> knows. The first seven must stay equal to the names in
/// the end-of-night shutdown's activity list (MediatorSecureObservatoryRig.RunningActivities): a job finds itself in
/// that list by name, and a name this table does not know counts as using every device.
/// </summary>
public static class ActivityNames
{
    public const string FilterOffset = "filter offset calibration";
    public const string Autofocus = "autofocus";
    public const string Flats = "flats";
    public const string ConeError = "cone error measurement";
    public const string GuidingStart = "guiding start";
    public const string DarkLibrary = "guider dark library";
    public const string GuidingCoach = "guiding coach";
    public const string InspectorAnalysis = "Aberration Inspector analysis";
    public const string OptimizerSweep = "star detection optimizer sweep";
    public const string TenMicronBuild = "10micron model build";
    public const string EndOfNight = "end-of-night shutdown";
}
