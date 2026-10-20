using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.INDI;
using NINA.INDI.Devices;
using NINA.INDI.Protocol;
using TouchNStars.ConeError.Domain;
using TouchNStars.ConeError.Session;

namespace TouchNStars.ConeError.Adapters;

/// <summary>
/// Works out what the connected mount driver allows. A pier side can only be forced through a
/// "preferred pier side" setting: pins' native OnStepX driver (:SX96) and the INDI OnStep and
/// Proxisky drivers ("Preferred Pier Side"). NINA's ITelescope.SideOfPier is read-only and the ASCOM
/// and INDI telescope classes are internal, so every other mount is refused with a reason.
/// </summary>
public sealed class NinaMountDriverInspector : IMountDriverInspector
{
    public const string IndiPreferredPierSide = "Preferred Pier Side";
    public const string IndiMinutesPastMeridian = "Minutes Past Meridian";
    public const string IndiElevationLimit = "Slew elevation Limit";

    private readonly ITelescopeMediator telescope;

    public NinaMountDriverInspector(ITelescopeMediator telescope)
    {
        this.telescope = telescope;
    }

    public MountDriverInfo Inspect()
    {
        var info = telescope.GetInfo();
        if (info == null || !info.Connected)
        {
            return new MountDriverInfo("none", new UnsupportedPierSideControl("The mount is not connected."), MountLimits.None, new PointingModelInfo(PointingModelState.Unknown, null, "mount not connected"));
        }

        if (telescope.GetDevice() is OnStepXTelescope { Connected: true } onStepX)
        {
            return new MountDriverInfo("OnStepX", new OnStepXPierSideControl(onStepX), OnStepXLimits(onStepX), OnStepModelProbe.Probe(telescope));
        }

        INDITelescope indi = INDIClient.Instance.GetRegisteredDevice<INDITelescope>();
        if (indi != null)
        {
            INDIDeviceSnapshot snapshot = INDIClient.Instance.GetDeviceSnapshots(indi.DeviceName).FirstOrDefault();
            INDIPropertySnapshot preferred = snapshot?.Properties.FirstOrDefault(p => p.Name == IndiPreferredPierSide);
            if (preferred != null)
            {
                return new MountDriverInfo(
                    "INDI OnStep",
                    new IndiOnStepPierSideControl(indi.DeviceName, () => telescope.SendCommandString(":GX96#", true)),
                    IndiOnStepLimits(snapshot),
                    OnStepModelProbe.Probe(telescope));
            }

            return new MountDriverInfo(
                "INDI",
                new UnsupportedPierSideControl($"The INDI driver of {indi.DeviceName} ({snapshot?.DriverExec}) cannot be told which pier side to use. Supported are the native OnStepX driver and the INDI OnStep and Proxisky drivers."),
                MountLimits.None,
                new PointingModelInfo(PointingModelState.Unknown, null, "not readable for this driver"));
        }

        return new MountDriverInfo(
            "other",
            new UnsupportedPierSideControl($"The driver of {info.Name} cannot be told which pier side to use. Supported are the native OnStepX driver and the INDI OnStep and Proxisky drivers."),
            MountLimits.None,
            new PointingModelInfo(PointingModelState.Unknown, null, "not readable for this driver"));
    }

    private static MountLimits OnStepXLimits(OnStepXTelescope onStepX)
    {
        try
        {
            OnStepXMountSettings s = onStepX.GetMountSettings();
            // OnStepX applies the east limit on PIER_SIDE_EAST when h < -limit (Guide.cpp), which is the
            // past-meridian angle of NINA's pierEast; the west limit likewise.
            bool any = s.HorizonLimit.HasValue || s.OverheadLimit.HasValue || s.MeridianLimitEast.HasValue || s.MeridianLimitWest.HasValue;
            return new MountLimits(s.HorizonLimit, s.OverheadLimit, s.MeridianLimitEast, s.MeridianLimitWest, any ? "OnStepX" : null);
        }
        catch (Exception ex)
        {
            Logger.Warning($"ConeError: reading the OnStepX limits failed: {ex.Message}");
            return MountLimits.None;
        }
    }

    /// <summary>
    /// "Minutes Past Meridian" holds :GXE9#/:GXEA# (minutes of time, 4 per degree); "Slew elevation
    /// Limit" :Gh#/:Go#. lx200_OnStep refreshes both from the mount with apply() and leaves their state
    /// Idle (lx200_OnStep.cpp ReadScopeStatus), so Idle and Ok values count; Busy (a write in progress)
    /// and Alert do not. The source names the mount only when at least one value was read.
    /// </summary>
    public static MountLimits IndiOnStepLimits(INDIDeviceSnapshot snapshot)
    {
        INDIPropertySnapshot meridian = snapshot?.Properties.FirstOrDefault(p => p.Name == IndiMinutesPastMeridian);
        INDIPropertySnapshot elevation = snapshot?.Properties.FirstOrDefault(p => p.Name == IndiElevationLimit);
        double? east = Number(meridian, "East") / 4.0;
        double? west = Number(meridian, "West") / 4.0;
        double? min = Number(elevation, "minAlt");
        double? max = Number(elevation, "maxAlt");
        bool any = east.HasValue || west.HasValue || min.HasValue || max.HasValue;
        return new MountLimits(min, max, east, west, any ? "INDI OnStep" : null);
    }

    /// <summary>A number element of a property whose values are settled (state Idle or Ok); null otherwise.</summary>
    private static double? Number(INDIPropertySnapshot property, string element)
    {
        if (property == null
            || !(string.Equals(property.State, "Ok", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.State, "Idle", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        object value = property.Elements.FirstOrDefault(e => e.Name == element)?.Value;
        return value switch
        {
            double d when !double.IsNaN(d) => d,
            float f => f,
            int i => i,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => null
        };
    }
}

/// <summary>The native OnStepX driver: preferred pier side by :SX96, read by :GX96#.</summary>
public sealed class OnStepXPierSideControl : IPierSideControl
{
    private readonly OnStepXTelescope mount;
    private string original;
    private bool changed;

    public OnStepXPierSideControl(OnStepXTelescope mount)
    {
        this.mount = mount;
    }

    public bool Supported => true;

    public string Description => "OnStepX preferred pier side (:SX96)";

    public string UnsupportedReason => null;

    public string OriginalSetting => original;

    public string ReadOriginal()
    {
        if (original == null)
        {
            original = mount.GetMountSettings().PreferredPierSide ?? "Best";
        }

        return original;
    }

    public Task PrepareAsync(PierSideLabel side, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ReadOriginal();
        // set before sending: a change that is sent but not acknowledged must still be restored
        changed = true;
        Set(side == PierSideLabel.East ? "East" : "West");
        return Task.CompletedTask;
    }

    public Task RestoreAsync()
    {
        if (changed && original != null)
        {
            Set(original);
            changed = false;
        }

        return Task.CompletedTask;
    }

    public Task RestoreToAsync(string setting)
    {
        Set(setting);
        return Task.CompletedTask;
    }

    private void Set(string setting)
    {
        string letter = setting?.Trim().ToUpperInvariant() switch
        {
            "EAST" or "E" => "E",
            "WEST" or "W" => "W",
            _ => "B"
        };
        if (!mount.SendCommandBool($":SX96,{letter}#", true))
        {
            throw new ConeErrorException("PierSideRefused", $"OnStepX refused the preferred pier side {setting}.");
        }
    }
}

/// <summary>
/// The INDI OnStep and Proxisky drivers: the "Preferred Pier Side" switch (1 = West, 2 = East, 3 = Best).
/// <para>
/// The stock lx200_OnStep counts only a '0' reply as success, but OnStepX answers '1' when it accepts
/// :SX96 (ProcessCmds.cpp). So on success the driver resets the switch and leaves it Busy, and the
/// real value comes back with its next status poll, which reads :GX96# and sets the element On with
/// state Ok. INDIClient.SetProperty also changes the local copy at once. A change therefore counts as
/// confirmed only once the driver has sent the property again (a newer timestamp) with state Ok and
/// the requested element On.
/// </para>
/// </summary>
public sealed class IndiOnStepPierSideControl : IPierSideControl
{
    private static readonly TimeSpan Confirmation = TimeSpan.FromSeconds(10);
    private readonly string device;
    private readonly Func<string> rawPreferredQuery;
    private string original;
    private bool changed;

    /// <param name="rawPreferredQuery">Sends :GX96# directly (INDI over TCP only), for an ambiguous switch; may throw.</param>
    public IndiOnStepPierSideControl(string device, Func<string> rawPreferredQuery = null)
    {
        this.device = device;
        this.rawPreferredQuery = rawPreferredQuery;
    }

    public bool Supported => true;

    public string Description => "INDI OnStep \"Preferred Pier Side\"";

    public string UnsupportedReason => null;

    public string OriginalSetting => original;

    private INDIPropertySnapshot Property()
        => INDIClient.Instance.GetDeviceSnapshots(device).FirstOrDefault()?.Properties.FirstOrDefault(p => p.Name == NinaMountDriverInspector.IndiPreferredPierSide);

    public string ReadOriginal()
    {
        if (original == null)
        {
            original = Interpret(Property(), rawPreferredQuery);
        }

        return original;
    }

    /// <summary>
    /// The setting the switch shows. The driver's poll sets the element it read On without clearing
    /// the others, so after a change from elsewhere two can be On; then :GX96# decides if it can be
    /// sent, else "Best" (the OnStepX default).
    /// </summary>
    public static string Interpret(INDIPropertySnapshot property, Func<string> rawPreferredQuery = null)
    {
        List<string> on = property?.Elements.Where(e => e.Value is true).Select(e => e.Name).ToList() ?? new List<string>();
        if (on.Count == 1)
        {
            return Name(on[0]);
        }

        if (on.Count > 1 && rawPreferredQuery != null)
        {
            try
            {
                string reply = rawPreferredQuery()?.Trim().TrimEnd('#').ToUpperInvariant();
                if (reply is "E" or "W" or "B")
                {
                    return reply switch { "E" => "East", "W" => "West", _ => "Best" };
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"ConeError: :GX96# not available: {ex.Message}");
            }
        }

        if (on.Count > 1)
        {
            Logger.Warning($"ConeError: the preferred pier side switch shows {string.Join(", ", on.Select(Name))}; taking Best as the original");
        }

        return "Best";
    }

    private static string Name(string element) => element switch
    {
        "1" => "West",
        "2" => "East",
        _ => "Best"
    };

    public async Task PrepareAsync(PierSideLabel side, CancellationToken token)
    {
        ReadOriginal();
        // set before sending: a change that is sent but not confirmed must still be restored
        changed = true;
        await SetAsync(side == PierSideLabel.East ? "East" : "West", token);
    }

    public async Task RestoreAsync()
    {
        if (changed && original != null)
        {
            await SetAsync(original, CancellationToken.None);
            changed = false;
        }
    }

    public Task RestoreToAsync(string setting) => SetAsync(setting, CancellationToken.None);

    private async Task SetAsync(string setting, CancellationToken token)
    {
        string element = setting?.Trim().ToUpperInvariant() switch
        {
            "WEST" or "W" => "1",
            "EAST" or "E" => "2",
            _ => "3"
        };
        string before = Property()?.Timestamp;
        var elements = new Dictionary<string, object> { ["1"] = element == "1", ["2"] = element == "2", ["3"] = element == "3" };
        if (!INDIClient.Instance.SetProperty(device, NinaMountDriverInspector.IndiPreferredPierSide, elements, out string error))
        {
            throw new ConeErrorException("PierSideRefused", $"The INDI driver refused the preferred pier side {setting}: {error}");
        }

        DateTime until = DateTime.UtcNow + Confirmation;
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(200, token);
            if (IsConfirmed(Property(), before, element))
            {
                return;
            }

            if (string.Equals(Property()?.State, "Alert", StringComparison.OrdinalIgnoreCase))
            {
                throw new ConeErrorException("PierSideRefused", $"The mount refused the preferred pier side {setting}.");
            }
        }

        throw new ConeErrorException("PierSideRefused", $"The INDI driver did not confirm the preferred pier side {setting}.");
    }

    /// <summary>The driver has sent the property since <paramref name="timestampBefore"/>, with state Ok and the element On.</summary>
    public static bool IsConfirmed(INDIPropertySnapshot property, string timestampBefore, string element)
        => property != null
            && !string.Equals(property.Timestamp, timestampBefore, StringComparison.Ordinal)
            && string.Equals(property.State, "Ok", StringComparison.OrdinalIgnoreCase)
            && property.Elements.FirstOrDefault(e => e.Name == element)?.Value is true;
}

/// <summary>
/// OnStep(X) pointing model terms :GX00#..:GX08# in arcsec (:GX04# = doCor, the cone term). Readable
/// on the native driver and on INDI over TCP; elsewhere the state is unknown.
/// </summary>
public static class OnStepModelProbe
{
    public static PointingModelInfo Probe(ITelescopeMediator telescope)
    {
        try
        {
            var terms = new long[9];
            for (int i = 0; i < terms.Length; i++)
            {
                string reply = telescope.SendCommandString($":GX0{i}#", true)?.Trim().TrimEnd('#');
                if (!long.TryParse(reply, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out terms[i]))
                {
                    return new PointingModelInfo(PointingModelState.Unknown, null, $"the reply to :GX0{i}# was not readable");
                }
            }

            return Classify(terms);
        }
        catch (Exception ex)
        {
            return new PointingModelInfo(PointingModelState.Unknown, null, $"not readable ({ex.Message})");
        }
    }

    /// <summary>Any non-zero term means an active model; the cone term is :GX04#.</summary>
    public static PointingModelInfo Classify(IReadOnlyList<long> terms)
    {
        if (terms.All(t => t == 0))
        {
            return new PointingModelInfo(PointingModelState.None, 0, "all OnStep model terms are 0");
        }

        return new PointingModelInfo(PointingModelState.Active, terms[4], $"OnStep model terms set, cone term {terms[4]}″");
    }
}
