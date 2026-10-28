using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Input;
using NINA.Profile.Interfaces;

namespace TouchNStars.Server.Services;

/// <summary>
/// The Livestack plugin's calibration master library (Bias, Dark and multi-session Flat) for Touch-N-Stars.
///
/// The libraries live in Livestack's CalibrationVM, which loads them from the profile once and stores its own copy on
/// every change, so masters are added and removed through that object - by reflection, like ninaAPI's LivestackBridge
/// - and never by writing the profile. FITS files are read with Livestack's own reader, so a flat's mean, which
/// Livestack divides by, is the value its desktop wizard would compute.
/// </summary>
public static class LivestackCalibrationService
{
    private const string LivestackAssemblyName = "nina.plugin.livestack";
    private const string MediatorTypeName = "NINA.Plugin.Livestack.LivestackMediator";
    private const string FrameMetaTypeName = "NINA.Plugin.Livestack.Image.CalibrationFrameMeta";
    private const string FrameTypeTypeName = "NINA.Plugin.Livestack.Image.CalibrationFrameType";
    private const string ReaderTypeName = "NINA.Plugin.Livestack.CFitsioFITSReader";
    private const string StackBagTypeName = "NINA.Plugin.Livestack.Image.LiveStackBag";

    /// <summary>Livestack's CalibrationFrameType names, used as the API's type values.</summary>
    public static readonly string[] Types = { "BIAS", "DARK", "FLAT" };

    // The desktop wizard only reads these (CalibrationVM.AddCalibrationFrame)
    private static readonly string[] SupportedExtensions = { ".fits", ".fit" };

    // A flat is read in full for its mean (4 bytes per pixel and more): one at a time, or two large flats added from
    // two devices could exhaust a Pi's memory. Library changes themselves go through LivestackSettingsService.WriteLock.
    private static readonly SemaphoreSlim FlatReadGate = new(1, 1);

    public sealed class CalibrationException : Exception
    {
        public CalibrationException(int statusCode, string code, string field, string message) : base(message)
        {
            StatusCode = statusCode;
            Code = code;
            Field = field;
        }

        public int StatusCode { get; }

        public string Code { get; }

        public string Field { get; }
    }

    public sealed class MasterInfo
    {
        public string Type { get; init; }

        public string Path { get; init; }

        public string FileName { get; init; }

        public bool FileExists { get; init; }

        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>-1: any gain.</summary>
        public int Gain { get; init; }

        /// <summary>-1: any offset.</summary>
        public int Offset { get; init; }

        public double ExposureTime { get; init; }

        public string Filter { get; init; }
    }

    public sealed class AddRequest
    {
        public string Path { get; init; }

        public string Type { get; init; }

        public int Gain { get; init; } = -1;

        public int Offset { get; init; } = -1;

        public double ExposureTime { get; init; }

        public string Filter { get; init; }
    }

    /// <summary>
    /// Identifies a library entry. Desktop Livestack allows the same file twice with different values, so besides the
    /// path every given value has to match too.
    /// </summary>
    public sealed class MasterMatch
    {
        public string Path { get; init; }

        public int? Gain { get; init; }

        public int? Offset { get; init; }

        public double? ExposureTime { get; init; }

        public string Filter { get; init; }
    }

    public static object Describe()
    {
        var vm = ResolveCalibrationVM(out _);

        return new
        {
            Bias = ReadLibrary(vm, "BIAS"),
            Dark = ReadLibrary(vm, "DARK"),
            Flat = ReadLibrary(vm, "FLAT"),
            IsStacking = LivestackSettingsService.IsStacking(),
        };
    }

    /// <summary>What the FITS header says about a master, as a proposal for <see cref="Add"/>. Reads no pixels.</summary>
    public static MasterInfo Inspect(string path)
    {
        string fullPath = CheckFile(path);
        ResolveCalibrationVM(out var assembly);

        using var reader = OpenReader(assembly, fullPath);
        var meta = ReadHeader(reader).ExtractMetaData();

        return new MasterInfo
        {
            Type = GuessType(meta.Image.ImageType),
            Path = fullPath,
            FileName = System.IO.Path.GetFileName(fullPath),
            FileExists = true,
            Width = GetInt(reader, "Width"),
            Height = GetInt(reader, "Height"),
            Gain = meta.Camera.Gain,
            Offset = meta.Camera.Offset,
            ExposureTime = double.IsNaN(meta.Image.ExposureTime) ? 0 : meta.Image.ExposureTime,
            Filter = meta.FilterWheel.Filter ?? string.Empty,
        };
    }

    /// <summary>
    /// Adds a master the way the desktop wizard does: size and, for a flat, the mean come from the file, the rest from
    /// the request (the user may have corrected the header's values), an empty filter becomes Livestack's
    /// "No_filter". Only flats use the mean (Livestack divides by it and drops flats without one), so bias and dark
    /// pixels are not read - that takes seconds and hundreds of MB for a large master on a Pi.
    /// </summary>
    public static object Add(AddRequest request, IProfileService profileService)
    {
        string type = CheckType(request.Type);
        string fullPath = CheckFile(request.Path);

        if (request.Gain < -1)
        {
            throw new CalibrationException(400, "out-of-range", "Gain", "Gain must be -1 (any) or higher");
        }

        if (request.Offset < -1)
        {
            throw new CalibrationException(400, "out-of-range", "Offset", "Offset must be -1 (any) or higher");
        }

        if (!double.IsFinite(request.ExposureTime) || request.ExposureTime < 0)
        {
            throw new CalibrationException(400, "out-of-range", "ExposureTime", "ExposureTime must be 0 or more seconds");
        }

        var vm = ResolveCalibrationVM(out var assembly);
        var profile = profileService.ActiveProfile;
        CheckNotStacking();
        CheckNotInLibrary(vm, type, fullPath);

        int width;
        int height;
        float mean = float.NaN;
        using (var reader = OpenReader(assembly, fullPath))
        {
            width = GetInt(reader, "Width");
            height = GetInt(reader, "Height");
            if (type == "FLAT")
            {
                FlatReadGate.Wait();
                try
                {
                    mean = ReadMean(reader);
                }
                finally
                {
                    FlatReadGate.Release();
                }
            }
        }

        if (type == "FLAT" && (!float.IsFinite(mean) || mean <= 0))
        {
            throw new CalibrationException(400, "flat-mean-invalid", "Path", $"The flat's mean pixel value is {mean}; Livestack can't divide by it");
        }

        // Livestack matches the filter name exactly, so it is stored as given
        string filter = string.IsNullOrWhiteSpace(request.Filter)
            ? assembly.GetType(StackBagTypeName)?.GetField("NOFILTER", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "No_filter"
            : request.Filter;

        var frameTypeType = assembly.GetType(FrameTypeTypeName);
        var frameMetaType = assembly.GetType(FrameMetaTypeName);
        var addMethod = vm.GetType().GetMethod("AddCalibrationFrame", BindingFlags.Public | BindingFlags.Instance);
        if (frameTypeType == null || frameMetaType == null || addMethod == null)
        {
            throw Incompatible("CalibrationFrameMeta or CalibrationVM.AddCalibrationFrame is missing");
        }

        var frame = Create(frameMetaType,
            Enum.Parse(frameTypeType, type),
            fullPath,
            request.Gain,
            request.Offset,
            request.ExposureTime,
            filter,
            width,
            height,
            mean);

        lock (LivestackSettingsService.WriteLock)
        {
            // Again: reading the file took a while, a stack may have started, the same master been added or the
            // profile been switched (Livestack would put the master into the new profile's library) meanwhile
            if (!ReferenceEquals(profileService.ActiveProfile, profile))
            {
                throw new CalibrationException(409, "profile-changed", null, "The profile was switched while the master was read");
            }

            CheckNotStacking();
            CheckNotInLibrary(vm, type, fullPath);
            Invoke(() => addMethod.Invoke(vm, new[] { frame }));
        }

        Logger.Info($"Livestack {type} master added: {fullPath} ({width}x{height}, gain {request.Gain}, offset {request.Offset}, {request.ExposureTime}s, filter {filter})");
        return Describe();
    }

    /// <summary>Takes a master out of the library; the file stays on disk.</summary>
    public static object Remove(string type, MasterMatch match)
    {
        type = CheckType(type);
        var vm = ResolveCalibrationVM(out _);

        string commandName = $"Delete{char.ToUpperInvariant(type[0])}{type[1..].ToLowerInvariant()}MasterCommand";
        if (vm.GetType().GetProperty(commandName)?.GetValue(vm) is not ICommand command)
        {
            throw Incompatible($"CalibrationVM.{commandName} is missing");
        }

        lock (LivestackSettingsService.WriteLock)
        {
            CheckNotStacking();

            var master = FindMaster(vm, type, match)
                ?? throw new CalibrationException(404, "not-in-library", "Path", $"{match.Path} is not in the {type} library");

            try
            {
                command.Execute(master);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                throw new CalibrationException(500, "livestack-error", null, ex.Message);
            }
        }

        Logger.Info($"Livestack {type} master removed from the library: {match.Path}");
        return Describe();
    }

    private static object ResolveCalibrationVM(out Assembly assembly)
    {
        assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, LivestackAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (assembly == null)
        {
            throw new CalibrationException(503, "livestack-unavailable", null, "The Livestack plugin is not loaded");
        }

        var property = assembly.GetType(MediatorTypeName)?.GetProperty("CalibrationVM", BindingFlags.Public | BindingFlags.Static);
        if (property == null)
        {
            throw Incompatible("LivestackMediator.CalibrationVM is missing");
        }

        // The library object exists as soon as Livestack's manifest is constructed, even when N.I.N.A. then refuses it
        if (!LivestackSettingsService.IsLivestackLoaded())
        {
            throw new CalibrationException(503, "livestack-unavailable", null, "The Livestack plugin is not loaded (refused by N.I.N.A. or still starting)");
        }

        return property.GetValue(null)
            ?? throw new CalibrationException(503, "livestack-unavailable", null, "The Livestack calibration library is not initialized yet");
    }

    private static List<MasterInfo> ReadLibrary(object vm, string type)
    {
        return LibraryItems(vm, type).Select(item => new MasterInfo
        {
            Type = type,
            Path = Get<string>(item, "Path"),
            FileName = System.IO.Path.GetFileName(Get<string>(item, "Path") ?? string.Empty),
            FileExists = File.Exists(Get<string>(item, "Path")),
            Width = Get<int>(item, "Width"),
            Height = Get<int>(item, "Height"),
            Gain = Get<int>(item, "Gain"),
            Offset = Get<int>(item, "Offset"),
            ExposureTime = Get<double>(item, "ExposureTime"),
            Filter = Get<string>(item, "Filter"),
        }).ToList();
    }

    private static List<object> LibraryItems(object vm, string type)
    {
        string propertyName = $"{char.ToUpperInvariant(type[0])}{type[1..].ToLowerInvariant()}Library";
        if (vm.GetType().GetProperty(propertyName)?.GetValue(vm) is not IEnumerable library)
        {
            throw Incompatible($"CalibrationVM.{propertyName} is missing");
        }

        // A snapshot; Livestack may change the collection while it is copied
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return library.Cast<object>().ToList();
            }
            catch (InvalidOperationException) when (attempt < 3)
            {
            }
        }
    }

    private static object FindMaster(object vm, string type, string path)
    {
        return FindMaster(vm, type, new MasterMatch { Path = path });
    }

    private static object FindMaster(object vm, string type, MasterMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.Path))
        {
            return null;
        }

        string wanted = System.IO.Path.GetFullPath(match.Path.Trim());
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return LibraryItems(vm, type).FirstOrDefault(item =>
        {
            string itemPath = Get<string>(item, "Path");
            return itemPath != null
                && string.Equals(System.IO.Path.GetFullPath(itemPath), wanted, comparison)
                && (match.Gain == null || Get<int>(item, "Gain") == match.Gain)
                && (match.Offset == null || Get<int>(item, "Offset") == match.Offset)
                && (match.ExposureTime == null || Get<double>(item, "ExposureTime") == match.ExposureTime)
                && (match.Filter == null || Get<string>(item, "Filter") == match.Filter);
        });
    }

    private static string CheckType(string type)
    {
        string normalized = type?.Trim().ToUpperInvariant();
        if (!Types.Contains(normalized))
        {
            throw new CalibrationException(400, "invalid-type", "Type", $"Type must be one of {string.Join(", ", Types)}");
        }

        return normalized;
    }

    private static string CheckFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path.Trim()))
        {
            throw new CalibrationException(400, "path-not-absolute", "Path", "The master must be given by its absolute path on this computer");
        }

        string fullPath = System.IO.Path.GetFullPath(path.Trim());
        if (!File.Exists(fullPath))
        {
            throw new CalibrationException(400, "file-not-found", "Path", $"The file {fullPath} does not exist");
        }

        if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(fullPath), StringComparer.OrdinalIgnoreCase))
        {
            throw new CalibrationException(400, "unsupported-format", "Path", "Only .fits and .fit masters are supported");
        }

        return fullPath;
    }

    private static void CheckNotInLibrary(object vm, string type, string fullPath)
    {
        if (FindMaster(vm, type, fullPath) != null)
        {
            throw new CalibrationException(409, "already-in-library", "Path", $"{fullPath} is already in the {type} library");
        }
    }

    private static void CheckNotStacking()
    {
        if (LivestackSettingsService.IsStacking() == true)
        {
            throw new CalibrationException(409, "livestack-running", null, "The calibration library can't be changed while live stacking runs");
        }
    }

    /// <summary>The header's IMAGETYP as a type proposal; the user confirms or corrects it.</summary>
    private static string GuessType(string imageType)
    {
        string value = imageType?.Trim().Trim('\'').Trim().ToLowerInvariant() ?? string.Empty;

        // N.I.N.A. writes DARKFLAT for dark flats: neither a dark for lights nor a flat
        if (value.Contains("dark") && value.Contains("flat"))
        {
            return null;
        }

        if (value.Contains("bias"))
        {
            return "BIAS";
        }

        if (value.Contains("dark"))
        {
            return "DARK";
        }

        return value.Contains("flat") ? "FLAT" : null;
    }

    private static IDisposable OpenReader(Assembly assembly, string fullPath)
    {
        var readerType = assembly.GetType(ReaderTypeName) ?? throw Incompatible("CFitsioFITSReader is missing");
        try
        {
            return (IDisposable)Activator.CreateInstance(readerType, fullPath);
        }
        catch (TargetInvocationException ex)
        {
            throw Unreadable(fullPath, ex.InnerException ?? ex);
        }
        catch (MissingMethodException)
        {
            throw Incompatible("CFitsioFITSReader(string) is missing");
        }
    }

    private static FITSHeader ReadHeader(IDisposable reader)
    {
        var method = reader.GetType().GetMethod("ReadHeader", Type.EmptyTypes) ?? throw Incompatible("CFitsioFITSReader.ReadHeader is missing");
        return (FITSHeader)InvokeReader(reader, method);
    }

    /// <summary>The mean of all pixels as Livestack's reader returns them (CalibrationVM: ReadAllPixelsAsFloat().Mean()).</summary>
    private static float ReadMean(IDisposable reader)
    {
        var method = reader.GetType().GetMethod("ReadAllPixelsAsFloat", Type.EmptyTypes)
            ?? throw Incompatible("CFitsioFITSReader.ReadAllPixelsAsFloat is missing");
        var pixels = (float[])InvokeReader(reader, method);
        if (pixels.Length == 0)
        {
            return float.NaN;
        }

        double sum = 0;
        foreach (float pixel in pixels)
        {
            sum += pixel;
        }

        return (float)(sum / pixels.Length);
    }

    private static object InvokeReader(IDisposable reader, MethodInfo method)
    {
        try
        {
            return method.Invoke(reader, null);
        }
        catch (TargetInvocationException ex)
        {
            throw Unreadable(Get<string>(reader, "FilePath"), ex.InnerException ?? ex);
        }
    }

    private static object Create(Type type, params object[] arguments)
    {
        try
        {
            return Activator.CreateInstance(type, arguments);
        }
        catch (MissingMethodException)
        {
            throw Incompatible($"{type.Name} has no constructor taking ({string.Join(", ", arguments.Select(a => a?.GetType().Name))})");
        }
        catch (TargetInvocationException ex)
        {
            Logger.Error(ex.InnerException ?? ex);
            throw new CalibrationException(500, "livestack-error", null, (ex.InnerException ?? ex).Message);
        }
    }

    private static object Invoke(Func<object> action)
    {
        try
        {
            return action();
        }
        catch (TargetInvocationException ex)
        {
            Logger.Error(ex.InnerException ?? ex);
            throw new CalibrationException(500, "livestack-error", null, (ex.InnerException ?? ex).Message);
        }
    }

    private static int GetInt(object target, string property)
    {
        return Get<int>(target, property);
    }

    private static T Get<T>(object target, string property)
    {
        return target.GetType().GetProperty(property)?.GetValue(target) is T value ? value : default;
    }

    private static CalibrationException Unreadable(string path, Exception ex)
    {
        Logger.Warning($"Livestack could not read the master {path}: {ex.Message}");
        return new CalibrationException(400, "file-unreadable", "Path", $"The file can't be read as a FITS master: {ex.Message}");
    }

    private static CalibrationException Incompatible(string detail)
    {
        return new CalibrationException(503, "livestack-incompatible", null, $"The installed Livestack version is not supported: {detail}");
    }
}
