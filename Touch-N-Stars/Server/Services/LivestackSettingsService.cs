using NINA.Core.Utility;
using NINA.Profile;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services;

/// <summary>
/// The Livestack plugin's options page for Touch-N-Stars. Reads and writes the plugin's own profile settings - same
/// plugin GUID, keys and value types as its options page, so desktop N.I.N.A. sees the same values - validates them
/// against the plugin's ranges and the host filesystem, and on Linux gives every profile a working directory outside
/// /tmp.
///
/// The Livestack plugin is not modified. It reads its settings from the profile at the moment it needs them, so a
/// value written here applies from its next use: the next frame, or the next stack tab for the Default* values.
/// </summary>
public static class LivestackSettingsService
{
    public static readonly Guid LivestackPluginId = Guid.Parse("10bc1716-54af-425e-b307-c0ca1ce10600");

    public const long MinFreeBytes = 1L << 30;
    public const long LowFreeBytes = 5L << 30;

    private const string LivestackAssemblyName = "nina.plugin.livestack";
    private const string LivestackMediatorTypeName = "NINA.Plugin.Livestack.LivestackMediator";
    private const string DefaultFolderName = "LiveStack";

    private static readonly TimeSpan SeedWaitForLivestack = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SaveHandlerWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan SaveHandlerFallbackDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SeedGateTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly SemaphoreSlim SeedGate = new(1, 1);

    // One probe per folder at a time: a hung network mount then blocks one thread, not one per request
    private static readonly Dictionary<string, Task<DirectoryStatus>> RunningProbes = new();

    private static IProfileService attachedProfileService;
    private static EventHandler profileChangedHandler;

    /// <summary>
    /// Every write of TNS to Livestack's settings - this service and the calibration library - goes through this
    /// lock. N.I.N.A. keeps plugin settings in plain dictionaries, and Livestack stores an empty library when
    /// serialising it fails, so concurrent writes from several devices must not overlap.
    /// </summary>
    internal static readonly object WriteLock = new();

    /// <summary>The Livestack plugin's setting names (Livestack.cs), used as keys in the profile and in the API.</summary>
    public static class Keys
    {
        public const string WorkingDirectory = nameof(WorkingDirectory);
        public const string UseBiasForLights = nameof(UseBiasForLights);
        public const string HotpixelRemoval = nameof(HotpixelRemoval);
        public const string SaveCalibratedFlats = nameof(SaveCalibratedFlats);
        public const string SaveCalibratedLights = nameof(SaveCalibratedLights);
        public const string SaveStackedLights = nameof(SaveStackedLights);
        public const string DefaultStretchAmount = nameof(DefaultStretchAmount);
        public const string DefaultBlackClipping = nameof(DefaultBlackClipping);
        public const string DefaultEnableBackgroundExtraction = nameof(DefaultEnableBackgroundExtraction);
        public const string DefaultBackgroundExtractionAmount = nameof(DefaultBackgroundExtractionAmount);
        public const string DefaultEnableGreenDeNoise = nameof(DefaultEnableGreenDeNoise);
        public const string DefaultGreenDeNoiseAmount = nameof(DefaultGreenDeNoiseAmount);
        public const string DefaultDownsample = nameof(DefaultDownsample);
    }

    // The defaults of the plugin's getters
    private static readonly Dictionary<string, bool> BoolDefaults = new()
    {
        [Keys.UseBiasForLights] = true,
        [Keys.HotpixelRemoval] = true,
        [Keys.SaveCalibratedFlats] = false,
        [Keys.SaveCalibratedLights] = false,
        [Keys.SaveStackedLights] = false,
        [Keys.DefaultEnableBackgroundExtraction] = false,
        [Keys.DefaultEnableGreenDeNoise] = false,
    };

    // The ranges of the plugin's options page (Options.xaml steppers); its setters only clamp two of them
    private static readonly Dictionary<string, NumberLimit> NumberLimits = new()
    {
        [Keys.DefaultStretchAmount] = new NumberLimit(0, 1, 0.01),
        [Keys.DefaultBlackClipping] = new NumberLimit(-10, 0, 0.1),
        [Keys.DefaultBackgroundExtractionAmount] = new NumberLimit(0, 1, 0.1),
        [Keys.DefaultGreenDeNoiseAmount] = new NumberLimit(0, 1, 0.1),
        [Keys.DefaultDownsample] = new NumberLimit(1, 4, 1),
    };

    /// <summary>
    /// Changing these while a stack runs would split one session's output between two folders or mix differently
    /// calibrated frames in one stack.
    /// </summary>
    public static readonly string[] LockedWhileStacking =
    {
        Keys.WorkingDirectory,
        Keys.UseBiasForLights,
        Keys.HotpixelRemoval,
    };

    public sealed class NumberLimit
    {
        public NumberLimit(double min, double max, double step)
        {
            Min = min;
            Max = max;
            Step = step;
        }

        public double Min { get; }

        public double Max { get; }

        public double Step { get; }
    }

    public sealed class DirectoryStatus
    {
        public bool Exists { get; init; }

        public bool Writable { get; init; }

        public long? FreeBytes { get; init; }

        public string FileSystem { get; init; }

        /// <summary>tmpfs/ramfs: whatever Livestack writes there is held in memory.</summary>
        public bool IsRamDisk { get; init; }

        /// <summary>null, or one of the path-* error codes of the API.</summary>
        public string Problem { get; init; }
    }

    public sealed class SettingError
    {
        public SettingError(string field, string code, string message)
        {
            Field = field;
            Code = code;
            Message = message;
        }

        public string Field { get; }

        public string Code { get; }

        public string Message { get; }
    }

    public sealed class UpdateResult
    {
        public int StatusCode { get; set; } = 200;

        public List<SettingError> Errors { get; } = new();
    }

    /// <summary>
    /// Gives the active profile, and every profile switched to later, a Livestack working directory outside /tmp
    /// while it has none of its own, or one that is no path on this computer (a profile copied from Windows).
    /// Livestack's default is the temp path, which is a RAM disk on current Debian and is emptied at boot in any case.
    /// Linux only, and only once N.I.N.A. has loaded Livestack. Runs in the background, off plugin loading and off
    /// the profile switch.
    /// </summary>
    public static void Attach(IProfileService profileService)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        Detach();
        attachedProfileService = profileService;
        profileChangedHandler = (_, _) => ScheduleSeed(profileService);
        profileService.ProfileChanged += profileChangedHandler;
        ScheduleSeed(profileService);
    }

    public static void Detach()
    {
        if (attachedProfileService != null && profileChangedHandler != null)
        {
            attachedProfileService.ProfileChanged -= profileChangedHandler;
        }

        attachedProfileService = null;
        profileChangedHandler = null;
    }

    private static void ScheduleSeed(IProfileService profileService)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Livestack may load after this plugin; without it, or when N.I.N.A. refused to load it, give up
                var deadline = DateTime.UtcNow + SeedWaitForLivestack;
                while (!IsLivestackLoaded())
                {
                    if (DateTime.UtcNow > deadline)
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                await WaitUntilProfileChangesAreSaved(profileService);

                if (!await SeedGate.WaitAsync(SeedGateTimeout))
                {
                    Logger.Warning("Livestack working directory not checked: an earlier check still hangs (unreachable network mount?)");
                    return;
                }

                try
                {
                    SeedWorkingDirectory(profileService);
                }
                finally
                {
                    SeedGate.Release();
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not set up the Livestack working directory: {ex}");
            }
        });
    }

    /// <summary>
    /// ProfileService raises ProfileChanged while it switches, before it listens to the new profile's changes again
    /// (ProfileService.SelectProfile: the handlers run first, RegisterChangedEventHandlers last). A value written
    /// before that would never be saved, so this waits until ProfileService is subscribed to the active profile.
    /// </summary>
    private static async Task WaitUntilProfileChangesAreSaved(IProfileService profileService)
    {
        var deadline = DateTime.UtcNow + SaveHandlerWait;
        while (DateTime.UtcNow < deadline)
        {
            bool? subscribed = IsProfileServiceSubscribed(profileService);
            if (subscribed == true)
            {
                return;
            }

            if (subscribed == null)
            {
                // Can't be told (N.I.N.A. changed): the switch is short, give it a generous margin
                await Task.Delay(SaveHandlerFallbackDelay);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <summary>
    /// Whether ProfileService's save handler is among the active profile's PropertyChanged subscribers, read from
    /// the event's backing field (declared on a base class of Profile); null when that can't be read.
    /// </summary>
    private static bool? IsProfileServiceSubscribed(IProfileService profileService)
    {
        try
        {
            var profile = profileService.ActiveProfile;
            FieldInfo field = null;
            for (var type = profile?.GetType(); type != null && field == null; type = type.BaseType)
            {
                field = type.GetField("PropertyChanged", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            }

            if (field == null)
            {
                return null;
            }

            var handlers = field.GetValue(profile) as Delegate;
            return handlers?.GetInvocationList().Any(d => ReferenceEquals(d.Target, profileService)) ?? false;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not check the profile's save handler: {ex.Message}");
            return null;
        }
    }

    public static object Describe(IProfileService profileService)
    {
        var accessor = new PluginOptionsAccessor(profileService, LivestackPluginId);
        var profile = profileService.ActiveProfile;
        string workingDirectory = GetWorkingDirectory(accessor);

        bool stored = profile.PluginSettings.TryGetValue(LivestackPluginId, Keys.WorkingDirectory, out string _);
        bool isDefault = !stored || DefaultWorkingDirectoryCandidates(profile).Any(c => SamePath(c, workingDirectory));

        return new
        {
            WorkingDirectory = workingDirectory,
            WorkingDirectoryIsDefault = isDefault,
            WorkingDirectoryStatus = ProbeResponsive(workingDirectory),
            UseBiasForLights = GetBool(accessor, Keys.UseBiasForLights),
            HotpixelRemoval = GetBool(accessor, Keys.HotpixelRemoval),
            SaveCalibratedFlats = GetBool(accessor, Keys.SaveCalibratedFlats),
            SaveCalibratedLights = GetBool(accessor, Keys.SaveCalibratedLights),
            SaveStackedLights = GetBool(accessor, Keys.SaveStackedLights),
            DefaultStretchAmount = GetDouble(profileService, accessor, Keys.DefaultStretchAmount),
            DefaultBlackClipping = GetDouble(profileService, accessor, Keys.DefaultBlackClipping),
            DefaultEnableBackgroundExtraction = GetBool(accessor, Keys.DefaultEnableBackgroundExtraction),
            DefaultBackgroundExtractionAmount = GetDouble(profileService, accessor, Keys.DefaultBackgroundExtractionAmount),
            DefaultEnableGreenDeNoise = GetBool(accessor, Keys.DefaultEnableGreenDeNoise),
            DefaultGreenDeNoiseAmount = GetDouble(profileService, accessor, Keys.DefaultGreenDeNoiseAmount),
            DefaultDownsample = accessor.GetValueInt32(Keys.DefaultDownsample, 1),
            IsStacking = IsStacking(),
            LivestackLoaded = IsLivestackLoaded(),
            // Keys with a value of their own in the profile; the app stores the others even when unchanged
            StoredKeys = StoredKeys(profile),
            LockedWhileStacking,
            Limits = new
            {
                DefaultStretchAmount = NumberLimits[Keys.DefaultStretchAmount],
                DefaultBlackClipping = NumberLimits[Keys.DefaultBlackClipping],
                DefaultBackgroundExtractionAmount = NumberLimits[Keys.DefaultBackgroundExtractionAmount],
                DefaultGreenDeNoiseAmount = NumberLimits[Keys.DefaultGreenDeNoiseAmount],
                DefaultDownsample = NumberLimits[Keys.DefaultDownsample],
                MinFreeBytes,
                LowFreeBytes,
            },
        };
    }

    /// <summary>
    /// Validates every setting in <paramref name="body"/> and writes them only when all of them pass. Values are
    /// stored with the type the plugin reads them with; the profile ignores a value of any other type. A value is also
    /// stored when it equals the effective default but nothing is stored yet, so an explicit choice stays one: the
    /// working directory is not seeded over it, and the stretch defaults no longer follow the image settings.
    /// </summary>
    public static UpdateResult Update(IProfileService profileService, IDictionary<string, object> body)
    {
        var result = new UpdateResult();

        if (body == null)
        {
            return Reject(result, 400, null, "invalid-body", "Request body is required");
        }

        Dictionary<string, object> values;
        try
        {
            values = new Dictionary<string, object>(body, StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return Reject(result, 400, null, "invalid-body", "The request body names a setting more than once");
        }

        var settings = profileService.ActiveProfile.PluginSettings;
        var accessor = new PluginOptionsAccessor(profileService, LivestackPluginId);
        var writes = new List<Action>();
        var changedLocked = new List<string>();
        bool anyKnown = false;

        foreach (var (key, fallback) in BoolDefaults)
        {
            if (!values.TryGetValue(key, out object raw))
            {
                continue;
            }

            anyKnown = true;

            if (!TryReadBool(raw, out bool value))
            {
                result.Errors.Add(new SettingError(key, "invalid-type", $"{key} must be true or false"));
                continue;
            }

            bool changed = value != accessor.GetValueBoolean(key, fallback);
            if (changed || !settings.TryGetValue(LivestackPluginId, key, out bool _))
            {
                writes.Add(() => accessor.SetValueBoolean(key, value));
            }

            if (changed && LockedWhileStacking.Contains(key))
            {
                changedLocked.Add(key);
            }
        }

        foreach (var (key, limit) in NumberLimits)
        {
            if (!values.TryGetValue(key, out object raw))
            {
                continue;
            }

            anyKnown = true;

            if (!TryReadNumber(raw, out double value))
            {
                result.Errors.Add(new SettingError(key, "invalid-type", $"{key} must be a number"));
                continue;
            }

            if (!double.IsFinite(value) || value < limit.Min || value > limit.Max)
            {
                result.Errors.Add(new SettingError(key, "out-of-range",
                    string.Create(CultureInfo.InvariantCulture, $"{key} must be between {limit.Min} and {limit.Max}")));
                continue;
            }

            if (key == Keys.DefaultDownsample)
            {
                if (value != Math.Floor(value))
                {
                    result.Errors.Add(new SettingError(key, "not-an-integer", $"{key} must be a whole number"));
                    continue;
                }

                int downsample = (int)value;
                if (downsample != accessor.GetValueInt32(key, 1) || !settings.TryGetValue(LivestackPluginId, key, out int _))
                {
                    writes.Add(() => accessor.SetValueInt32(key, downsample));
                }
                continue;
            }

            if (value != GetDouble(profileService, accessor, key) || !settings.TryGetValue(LivestackPluginId, key, out double _))
            {
                writes.Add(() => accessor.SetValueDouble(key, value));
            }
        }

        if (values.TryGetValue(Keys.WorkingDirectory, out object rawPath))
        {
            anyKnown = true;

            if (rawPath is not string text)
            {
                result.Errors.Add(new SettingError(Keys.WorkingDirectory, "invalid-type", $"{Keys.WorkingDirectory} must be text"));
            }
            else
            {
                string path = text.Trim();
                var status = ProbeResponsive(path);

                if (status.Problem != null)
                {
                    result.Errors.Add(new SettingError(Keys.WorkingDirectory, status.Problem, DescribeProblem(status.Problem, path)));
                }
                else
                {
                    string fullPath = Path.GetFullPath(path);
                    bool changed = !SamePath(fullPath, GetWorkingDirectory(accessor));
                    if (changed || !settings.TryGetValue(LivestackPluginId, Keys.WorkingDirectory, out string _))
                    {
                        writes.Add(() => accessor.SetValueString(Keys.WorkingDirectory, fullPath));
                    }

                    if (changed)
                    {
                        changedLocked.Add(Keys.WorkingDirectory);
                    }
                }
            }
        }

        if (!anyKnown)
        {
            return Reject(result, 400, null, "invalid-body", "The request body contains no Livestack setting");
        }

        if (result.Errors.Count > 0)
        {
            return Reject(result, 400);
        }

        lock (WriteLock)
        {
            if (changedLocked.Count > 0 && IsStacking() == true)
            {
                foreach (string key in changedLocked)
                {
                    result.Errors.Add(new SettingError(key, "livestack-running", $"{key} can't be changed while live stacking runs"));
                }
                return Reject(result, 409);
            }

            foreach (var write in writes)
            {
                write();
            }
        }

        return result;
    }

    /// <summary>
    /// true while Livestack's Start Live Stack command runs, false when it doesn't or its dockable doesn't exist,
    /// null when Livestack isn't loaded or no longer has that shape.
    /// </summary>
    public static bool? IsStacking()
    {
        var assembly = FindLivestackAssembly();
        if (assembly == null)
        {
            return null;
        }

        try
        {
            var mediator = assembly.GetType(LivestackMediatorTypeName);
            var dockableProperty = mediator?.GetProperty("LiveStackDockable", BindingFlags.Public | BindingFlags.Static);
            if (dockableProperty == null)
            {
                return null;
            }

            var dockable = dockableProperty.GetValue(null);
            if (dockable == null)
            {
                return false;
            }

            var command = dockable.GetType().GetProperty("StartLiveStackCommand")?.GetValue(dockable);
            return command?.GetType().GetProperty("IsRunning")?.GetValue(command) is bool running ? running : null;
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not determine whether Livestack is stacking: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// <see cref="Probe"/> with a time limit: a folder on an unreachable network mount answers "not responding"
    /// after a few seconds, and a probe still hanging there is reused instead of starting another one.
    /// </summary>
    private static DirectoryStatus ProbeResponsive(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Probe(path);
        }

        Task<DirectoryStatus> probe;
        lock (RunningProbes)
        {
            if (!RunningProbes.TryGetValue(path, out probe) || probe.IsCompleted)
            {
                probe = Task.Run(() => Probe(path));
                RunningProbes[path] = probe;
            }
        }

        return probe.Wait(ProbeTimeout) ? probe.Result : new DirectoryStatus { Problem = "path-not-responding" };
    }

    /// <summary>Whether <paramref name="path"/> can be Livestack's working directory, and how much room it has.</summary>
    public static DirectoryStatus Probe(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return new DirectoryStatus { Problem = "path-not-absolute" };
        }

        string fullPath = Path.GetFullPath(path);
        var (freeBytes, fileSystem) = ReadFileSystem(fullPath);
        bool isRamDisk = IsRamFileSystem(fileSystem);

        if (!Directory.Exists(fullPath))
        {
            return new DirectoryStatus { FreeBytes = freeBytes, FileSystem = fileSystem, IsRamDisk = isRamDisk, Problem = "path-not-found" };
        }

        bool writable = CanWrite(fullPath);
        string problem = !writable
            ? "path-not-writable"
            : freeBytes < MinFreeBytes ? "path-insufficient-space" : null;

        return new DirectoryStatus
        {
            Exists = true,
            Writable = writable,
            FreeBytes = freeBytes,
            FileSystem = fileSystem,
            IsRamDisk = isRamDisk,
            Problem = problem,
        };
    }

    /// <summary>
    /// Free space and type of the file system <paramref name="fullPath"/> is on - for a path that doesn't exist yet,
    /// the one its nearest existing parent is on. Symlinks are followed, so a link into another mount (a USB disk,
    /// /tmp) reports that mount.
    /// </summary>
    private static (long? FreeBytes, string FileSystem) ReadFileSystem(string fullPath)
    {
        string existing = fullPath;
        while (existing != null && !Directory.Exists(existing))
        {
            existing = Path.GetDirectoryName(existing);
        }

        if (existing == null)
        {
            return (null, null);
        }

        string realPath = ResolveRealPath(existing);
        var drive = FindDrive(realPath);

        try
        {
            return (drive?.AvailableFreeSpace, drive?.DriveFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug($"Could not read the free space of {realPath}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// tmpfs/ramfs, and overlay: on a Pi that is Raspberry Pi OS's "Overlay File System", whose writes live in RAM
    /// and are gone after a reboot.
    /// </summary>
    private static bool IsRamFileSystem(string fileSystem)
    {
        return string.Equals(fileSystem, "tmpfs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileSystem, "ramfs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileSystem, "overlay", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The path with every symlink along it replaced by its final target - also links inside a link's target, which
    /// a further pass resolves. Bounded, so a link loop can't hang it.
    /// </summary>
    private static string ResolveRealPath(string fullPath)
    {
        string current = fullPath;
        for (int pass = 0; pass < 40; pass++)
        {
            string resolved = ResolveLinksOnce(current);
            if (resolved == current)
            {
                break;
            }

            current = resolved;
        }

        return current;
    }

    private static string ResolveLinksOnce(string fullPath)
    {
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        string current = root;

        foreach (string part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var target = new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true);
                if (target != null)
                {
                    current = target.FullName;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                break;
            }
        }

        return current;
    }

    /// <summary>
    /// N.I.N.A. loaded Livestack: its dockable exists. The manifest alone is no proof - PluginLoader constructs it
    /// (which sets LivestackMediator.Plugin and CalibrationVM) before its compatibility checks, and composes the
    /// dockables only when they pass.
    /// </summary>
    internal static bool IsLivestackLoaded()
    {
        var assembly = FindLivestackAssembly();
        if (assembly == null)
        {
            return false;
        }

        try
        {
            var property = assembly.GetType(LivestackMediatorTypeName)?.GetProperty("LiveStackDockable", BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null) != null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"Could not check whether Livestack is loaded: {ex.Message}");
            return false;
        }
    }

    private static string[] StoredKeys(IProfile profile)
    {
        var settings = profile.PluginSettings;
        var stored = new List<string>();

        foreach (string key in BoolDefaults.Keys)
        {
            if (settings.TryGetValue(LivestackPluginId, key, out bool _))
            {
                stored.Add(key);
            }
        }

        foreach (string key in NumberLimits.Keys)
        {
            bool isStored = key == Keys.DefaultDownsample
                ? settings.TryGetValue(LivestackPluginId, key, out int _)
                : settings.TryGetValue(LivestackPluginId, key, out double _);
            if (isStored)
            {
                stored.Add(key);
            }
        }

        if (settings.TryGetValue(LivestackPluginId, Keys.WorkingDirectory, out string _))
        {
            stored.Add(Keys.WorkingDirectory);
        }

        return stored.ToArray();
    }

    /// <summary>A working directory of the profile's own that is a path on this computer.</summary>
    private static bool HasLocalWorkingDirectory(IProfile profile, out string stored)
    {
        return profile.PluginSettings.TryGetValue(LivestackPluginId, Keys.WorkingDirectory, out stored)
            && !string.IsNullOrWhiteSpace(stored)
            && Path.IsPathFullyQualified(stored);
    }

    private static Assembly FindLivestackAssembly()
    {
        return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(IsLivestackAssembly);
    }

    private static bool IsLivestackAssembly(Assembly assembly)
    {
        return string.Equals(assembly.GetName().Name, LivestackAssemblyName, StringComparison.OrdinalIgnoreCase);
    }

    private static void SeedWorkingDirectory(IProfileService profileService)
    {
        try
        {
            var profile = profileService.ActiveProfile;
            if (profile == null)
            {
                return;
            }

            if (HasLocalWorkingDirectory(profile, out string stored))
            {
                return;
            }

            if (stored != null)
            {
                // Livestack would write into a relative folder under the process's working directory
                Logger.Warning($"Livestack working directory \"{stored}\" of profile \"{profile.Name}\" is no path on this computer and is replaced");
            }

            foreach (string candidate in DefaultWorkingDirectoryCandidates(profile))
            {
                // Checked before creating it, so no empty folder is left behind in RAM
                if (IsRamFileSystem(ReadFileSystem(candidate).FileSystem))
                {
                    continue;
                }

                try
                {
                    Directory.CreateDirectory(candidate);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    Logger.Warning($"Could not create the Livestack working directory {candidate}: {ex.Message}");
                    continue;
                }

                // Too little space is accepted: the app warns about it, and RAM would be worse
                var status = Probe(candidate);
                if (status.IsRamDisk || status.Problem is "path-not-absolute" or "path-not-found" or "path-not-writable")
                {
                    continue;
                }

                lock (WriteLock)
                {
                    // Checking the folder took a while: the user may have chosen one meanwhile, or switched profiles
                    if (!ReferenceEquals(profileService.ActiveProfile, profile) || HasLocalWorkingDirectory(profile, out _))
                    {
                        return;
                    }

                    profile.PluginSettings.SetValue(LivestackPluginId, Keys.WorkingDirectory, candidate);
                }

                Logger.Info($"Livestack working directory of profile \"{profile.Name}\" set to {candidate} instead of {stored ?? Path.GetTempPath()}");
                return;
            }

            Logger.Warning($"No Livestack working directory outside RAM could be set up; Livestack keeps using {Path.GetTempPath()}");
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not set up the Livestack working directory: {ex}");
        }
    }

    /// <summary>A LiveStack folder next to the profile's images, then in N.I.N.A.'s default image folder.</summary>
    private static IEnumerable<string> DefaultWorkingDirectoryCandidates(IProfile profile)
    {
        var folders = new[]
        {
            profile.ImageFileSettings?.FilePath,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "N.I.N.A"),
        };

        return folders
            .Where(f => !string.IsNullOrWhiteSpace(f) && Path.IsPathFullyQualified(f))
            .Select(f => Path.Combine(Path.GetFullPath(f), DefaultFolderName));
    }

    private static string GetWorkingDirectory(PluginOptionsAccessor accessor)
    {
        return accessor.GetValueString(Keys.WorkingDirectory, Path.GetTempPath());
    }

    private static bool GetBool(PluginOptionsAccessor accessor, string key)
    {
        return accessor.GetValueBoolean(key, BoolDefaults[key]);
    }

    private static double GetDouble(IProfileService profileService, PluginOptionsAccessor accessor, string key)
    {
        double fallback = key switch
        {
            Keys.DefaultStretchAmount => profileService.ActiveProfile.ImageSettings.AutoStretchFactor,
            Keys.DefaultBlackClipping => profileService.ActiveProfile.ImageSettings.BlackClipping,
            _ => 0.7,
        };
        return accessor.GetValueDouble(key, fallback);
    }

    internal static bool TryReadBool(object raw, out bool value)
    {
        switch (raw)
        {
            case bool b:
                value = b;
                return true;

            case string s:
                return bool.TryParse(s, out value);

            default:
                value = false;
                return false;
        }
    }

    internal static bool TryReadNumber(object raw, out double value)
    {
        switch (raw)
        {
            case string s:
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

            case bool or null:
                value = 0;
                return false;

            case IConvertible convertible:
                try
                {
                    value = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    value = 0;
                    return false;
                }

            default:
                value = 0;
                return false;
        }
    }

    private static string DescribeProblem(string problem, string path)
    {
        return problem switch
        {
            "path-not-absolute" => "The working directory must be an absolute path on this computer",
            "path-not-found" => $"The folder {path} does not exist",
            "path-not-writable" => $"The folder {path} is not writable",
            "path-insufficient-space" => $"Less than {MinFreeBytes >> 30} GiB free at {path}",
            "path-not-responding" => $"The folder {path} does not respond (unreachable network mount?)",
            _ => problem,
        };
    }

    private static UpdateResult Reject(UpdateResult result, int statusCode, string field = null, string code = null, string message = null)
    {
        if (code != null)
        {
            result.Errors.Add(new SettingError(field, code, message));
        }

        result.StatusCode = statusCode;
        return result;
    }

    /// <summary>The mounted drive whose mount point is the longest prefix of <paramref name="fullPath"/>.</summary>
    private static DriveInfo FindDrive(string fullPath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        DriveInfo best = null;
        int bestLength = -1;

        foreach (var drive in DriveInfo.GetDrives())
        {
            string root;
            try
            {
                root = drive.RootDirectory.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            bool contains = string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), comparison)
                || fullPath.StartsWith(prefix, comparison);

            if (contains && root.Length > bestLength)
            {
                best = drive;
                bestLength = root.Length;
            }
        }

        return best;
    }

    private static bool CanWrite(string directory)
    {
        string probe = Path.Combine(directory, $".tns-write-test-{Guid.NewGuid():N}");
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static bool SamePath(string a, string b)
    {
        if (a == null || b == null)
        {
            return false;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            comparison);
    }
}
