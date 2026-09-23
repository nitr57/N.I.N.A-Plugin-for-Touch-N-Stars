using NINA.Core.Utility;
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TouchNStars.Server.Services;

/// <summary>
/// One HocusFocus Star Detection Optimization Wizard, driven headlessly for TNS. HocusFocus builds the wizard VM
/// (HocusFocusPlugin.CreateStarDetectionOptimizer); this class owns it, relays the wizard's confirmations to the
/// TNS UI and exposes a whitelisted slice of its state, inputs and commands. Everything goes through reflection,
/// like the rest of the HocusFocus integration, so TNS has no compile-time dependency on the plugin.
/// </summary>
public sealed class StarDetectionOptimizerSession : IDisposable
{
    private const string PluginTypeName = "NINA.Joko.Plugins.HocusFocus.HocusFocusPlugin, NINA.Joko.Plugins.HocusFocus";

    // How long one of the wizard's confirmations waits for an answer before counting as "no". Every confirmation
    // guards an action (starting a live sweep, moving the focuser, re-running the search), so an unanswered one
    // must never proceed on its own.
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromMinutes(10);

    // A session nobody has looked at for this long, and that is not working, is ended so its loaded frames do
    // not stay in memory. Counted from the later of the last client request and the last moment the wizard was
    // busy, so a run left going in a hidden window keeps its results for this long after it finishes.
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan IdleCheckInterval = TimeSpan.FromMinutes(1);

    // Inputs TNS may set, by the wizard's property name. Everything else is read-only from TNS.
    private static readonly HashSet<string> SettableProperties = new(StringComparer.Ordinal)
    {
        "SourceMode",
        "RunCount",
        "OptimizeMode",
        "StartFromCurrentSettings",
        "FocusRecoverySteps",
        "DefocusAwareDonutDetection",
        "GpuAccelerationEnabled",
        "OptimizeForAberrationInspection",
        "TargetFilterName",
        "LiveExposureSeconds",
        "SaveFolderPath",
        "SweepDetectionBinning",
        "ApplyRecommendedStepSize",
        "RecaptureExposureSeconds",
        "SelectedVariant",
    };

    // Commands TNS may run. Left out: the Browse commands (desktop folder dialogs; TNS sets the paths directly),
    // Review/BackToSummary (the per-star labelling canvas) and ImportRunSettings (desktop file dialog).
    private static readonly string[] AllowedCommands =
    {
        "StartCommand",
        "CancelCommand",
        "AcceptCommand",
        "OptimizeAgainAtRecommendedBinningCommand",
        "CaptureNewSweepCommand",
        "BackCommand",
        "CloseCommand",
        "ReOptimizeCommand",
        "ContinueOptimizationCommand",
    };

    private static readonly object sessionLock = new();
    private static StarDetectionOptimizerSession current;

    // What the last session ended with, so a client polling after Accept/Close still learns the outcome.
    private static string lastOutcome;

    private readonly object vm;
    private readonly Type vmType;
    private readonly Type pluginType;
    private readonly object stateLock = new();
    private readonly EventHandler requestCloseHandler;
    private PendingConfirmation pendingConfirmation;
    private int nextConfirmationId;
    private string lastCommandError;
    private bool acceptRequested;
    private bool disposed;
    private DateTime lastActivityUtc = DateTime.UtcNow;
    private readonly Timer idleTimer;

    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    private sealed class PendingConfirmation
    {
        public PendingConfirmation(int id, string title, string message)
        {
            Id = id;
            Title = title;
            Message = message;
        }

        public int Id { get; }
        public string Title { get; }
        public string Message { get; }
        public TaskCompletionSource<bool> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private StarDetectionOptimizerSession(Type pluginType, Func<Func<string, string, bool>, object> create)
    {
        this.pluginType = pluginType;
        vm = create(Confirm) ?? throw new InvalidOperationException("HocusFocus did not create the optimization wizard");
        vmType = vm.GetType();

        // Accept and Close both end the wizard by raising RequestClose; the desktop closes its window there.
        requestCloseHandler = (s, e) => EndCurrent(this, acceptRequested ? "accepted" : "closed");
        vmType.GetEvent("RequestClose")?.AddEventHandler(vm, requestCloseHandler);

        idleTimer = new Timer(_ => CheckIdle(), null, IdleCheckInterval, IdleCheckInterval);
    }

    // ---- Idle timeout and focuser ownership ----------------------------------------------------------------

    private void Touch()
    {
        lock (stateLock)
        {
            lastActivityUtc = DateTime.UtcNow;
        }
    }

    // Working: a command running, a confirmation waiting, or the wizard itself busy (capturing, loading, searching).
    private bool IsWorking()
    {
        lock (stateLock)
        {
            if (pendingConfirmation != null)
            {
                return true;
            }
        }
        if (Get(vm, "IsBusy") as bool? == true)
        {
            return true;
        }
        return AllowedCommands.Any(name => Get(Get(vm, name), "IsRunning") as bool? == true);
    }

    private void CheckIdle()
    {
        try
        {
            if (IsWorking())
            {
                Touch();
                return;
            }
            DateTime last;
            lock (stateLock)
            {
                if (disposed)
                {
                    return;
                }
                last = lastActivityUtc;
            }
            if (DateTime.UtcNow - last > IdleTimeout)
            {
                Logger.Info($"[Optimizer] Ending the optimization wizard after {IdleTimeout.TotalMinutes:0} minutes without activity");
                EndCurrent(this, "timedOut");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"[Optimizer] Idle check failed: {ex}");
        }
    }

    /// <summary>
    /// True while the optimizer is running a live sweep, which moves the focuser and takes exposures. The
    /// Aberration Inspector refuses to start its own run meanwhile; the desktop wizard is modal, so there the two
    /// could never overlap.
    /// </summary>
    public static bool IsDrivingFocuser
    {
        get
        {
            var session = Current;
            return session != null && session.IsWorking() && Get(session.vm, "IsLive") as bool? == true;
        }
    }

    // Any Aberration Inspector analysis in flight (live run, exposure analysis or saved-run replay). Read from its
    // private task field: its command availability is also false when a device is simply disconnected.
    private bool InspectorAnalysisRunning()
    {
        var inspectorVM = pluginType.GetProperty("InspectorVM", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var analyzeTask = inspectorVM?.GetType()
            .GetField("analyzeTask", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(inspectorVM) as Task;
        return analyzeTask != null && !analyzeTask.IsCompleted;
    }

    /// <summary>The running session, or null.</summary>
    public static StarDetectionOptimizerSession Current
    {
        get
        {
            lock (sessionLock)
            {
                return current;
            }
        }
    }

    public static string LastOutcome
    {
        get
        {
            lock (sessionLock)
            {
                return lastOutcome;
            }
        }
    }

    /// <summary>Starts a new wizard session, ending any previous one.</summary>
    public static StarDetectionOptimizerSession Start()
    {
        var pluginType = Type.GetType(PluginTypeName)
            ?? throw new InvalidOperationException("HocusFocus plugin not loaded");
        var factory = pluginType.GetProperty("CreateStarDetectionOptimizer", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Delegate
            ?? throw new InvalidOperationException("This HocusFocus build cannot run the optimizer headless (CreateStarDetectionOptimizer missing)");

        lock (sessionLock)
        {
            current?.DisposeCore();
            current = null;
            lastOutcome = null;
            current = new StarDetectionOptimizerSession(pluginType, confirm => factory.DynamicInvoke(confirm));
            return current;
        }
    }

    /// <summary>Ends the running session, cancelling anything in flight.</summary>
    public static void End()
    {
        var session = Current;
        if (session != null)
        {
            EndCurrent(session, "closed");
        }
    }

    private static void EndCurrent(StarDetectionOptimizerSession session, string outcome)
    {
        lock (sessionLock)
        {
            if (current != session)
            {
                return;
            }
            current = null;
            lastOutcome = outcome;
        }
        // Disposing inside RequestClose would tear the VM down under its own command; let that return first.
        Task.Run(session.DisposeCore);
    }

    // ---- Confirmations -------------------------------------------------------------------------------------

    // Called by the wizard on the thread running the command. Blocks that thread (never a request thread: TNS
    // runs commands on the thread pool) until the TNS user answers, the timeout passes, or the session ends.
    private bool Confirm(string title, string message)
    {
        var pending = new PendingConfirmation(Interlocked.Increment(ref nextConfirmationId), title, message);
        lock (stateLock)
        {
            if (disposed)
            {
                return false;
            }
            pendingConfirmation = pending;
        }
        try
        {
            return pending.Answer.Task.Wait(ConfirmationTimeout) && pending.Answer.Task.Result;
        }
        finally
        {
            lock (stateLock)
            {
                if (pendingConfirmation == pending)
                {
                    pendingConfirmation = null;
                }
            }
        }
    }

    public bool Answer(int id, bool agreed)
    {
        Touch();
        PendingConfirmation pending;
        lock (stateLock)
        {
            pending = pendingConfirmation;
        }
        return pending != null && pending.Id == id && pending.Answer.TrySetResult(agreed);
    }

    // ---- Inputs --------------------------------------------------------------------------------------------

    public void SetProperty(string name, JsonElement value)
    {
        Touch();
        if (!SettableProperties.Contains(name))
        {
            throw new ArgumentException($"'{name}' cannot be set");
        }
        var property = vmType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite)
        {
            throw new ArgumentException($"'{name}' is not settable on this HocusFocus build");
        }
        var converted = Convert(value, property.PropertyType);
        property.SetValue(vm, converted);

        // The desktop's Browse button also persists the live-sweep folder to the AutoFocus options, so the
        // choice sticks across launches; do the same.
        if (name == "SaveFolderPath" && converted is string folder && !string.IsNullOrWhiteSpace(folder))
        {
            var autoFocusOptions = pluginType.GetProperty("AutoFocusOptions", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            autoFocusOptions?.GetType().GetProperty("SavePath")?.SetValue(autoFocusOptions, folder);
        }
    }

    /// <summary>
    /// Points replay source <paramref name="index"/> at a saved auto-focus attempt, given as the path relative to
    /// the AutoFocus save folder that /hocusfocus/list-af returns (e.g. "2026-09-20_22-14-03/attempt01").
    /// </summary>
    public void SetSourcePath(int index, string relativeAttemptPath)
    {
        Touch();
        var sources = Get(vm, "SourcePaths") as IList
            ?? throw new InvalidOperationException("SourcePaths not available");
        if (index < 0 || index >= sources.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), $"Source {index} does not exist (run count {sources.Count})");
        }
        if (string.IsNullOrWhiteSpace(relativeAttemptPath))
        {
            sources[index] = null;
            return;
        }

        var savePath = GetAutoFocusSavePath()
            ?? throw new InvalidOperationException("No AutoFocus save path is configured");
        var root = Path.GetFullPath(savePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relativeAttemptPath));
        // Only folders under the AutoFocus save path: that is all list-af offers, and it keeps a client from
        // pointing the loader anywhere else on the disk.
        if (!full.StartsWith(root, StringComparison.Ordinal) || !Directory.Exists(full))
        {
            throw new ArgumentException($"'{relativeAttemptPath}' is not a saved auto-focus folder");
        }
        sources[index] = full;
    }

    private string GetAutoFocusSavePath()
    {
        var autoFocusOptions = pluginType.GetProperty("AutoFocusOptions", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        var savePath = Get(autoFocusOptions, "SavePath") as string;
        return string.IsNullOrWhiteSpace(savePath) ? null : savePath;
    }

    private static object Convert(JsonElement value, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (underlying == typeof(string))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
        }
        if (underlying == typeof(bool))
        {
            return value.ValueKind == JsonValueKind.String ? bool.Parse(value.GetString()) : value.GetBoolean();
        }
        if (underlying.IsEnum)
        {
            return Enum.Parse(underlying, value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString(), ignoreCase: true);
        }
        var number = value.ValueKind == JsonValueKind.String
            ? double.Parse(value.GetString(), CultureInfo.InvariantCulture)
            : value.GetDouble();
        if (underlying == typeof(int))
        {
            return (int)Math.Round(number);
        }
        if (underlying == typeof(double))
        {
            return number;
        }
        return System.Convert.ChangeType(number, underlying, CultureInfo.InvariantCulture);
    }

    // ---- Commands ------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs one of the wizard's commands on the thread pool and returns at once; progress and results show up in
    /// <see cref="GetState"/>. Returns null when it started, otherwise why it could not.
    /// </summary>
    public string Execute(string commandName)
    {
        Touch();
        if (!AllowedCommands.Contains(commandName))
        {
            throw new ArgumentException($"'{commandName}' cannot be run");
        }
        var command = Get(vm, commandName);
        if (command == null || !CanExecute(command))
        {
            return $"{commandName} is not available right now";
        }
        // Both of these take exposures and move the focuser: a live sweep, and a new sweep from the summary.
        var movesFocuser = commandName == "CaptureNewSweepCommand"
            || (commandName == "StartCommand" && Get(vm, "IsLive") as bool? == true);
        if (movesFocuser && ImagingActivity.IsSequenceRunning())
        {
            return ImagingActivity.SequenceRunningMessage;
        }
        if (movesFocuser && InspectorAnalysisRunning())
        {
            return "An Aberration Inspector analysis is running. Let it finish or cancel it first: both would drive the focuser.";
        }
        if (commandName == "AcceptCommand")
        {
            acceptRequested = true;
        }
        lock (stateLock)
        {
            lastCommandError = null;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var executeAsync = command.GetType().GetMethod("ExecuteAsync", new[] { typeof(object) });
                if (executeAsync != null)
                {
                    await ((Task)executeAsync.Invoke(command, new object[] { null })).ConfigureAwait(false);
                }
                else
                {
                    command.GetType().GetMethod("Execute", new[] { typeof(object) })?.Invoke(command, new object[] { null });
                }
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Logger.Error($"[Optimizer] {commandName} failed: {inner}");
                lock (stateLock)
                {
                    lastCommandError = inner.Message;
                }
            }
        });
        return null;
    }

    private static bool CanExecute(object command)
    {
        try
        {
            var method = command.GetType().GetMethod("CanExecute", new[] { typeof(object) });
            return method == null || (bool)method.Invoke(command, new object[] { null });
        }
        catch
        {
            return false;
        }
    }

    // ---- State ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A snapshot for the client: every scalar the wizard exposes (the desktop view's own texts included), plus the
    /// few lists and rows the TNS pages render, the command states, and any confirmation waiting for an answer.
    /// </summary>
    public Dictionary<string, object> GetState()
    {
        Touch();
        var state = new Dictionary<string, object>
        {
            { "Success", true },
            { "Active", true },
            { "SessionId", SessionId },
            { "Values", ReadScalars(vm) },
            { "SourcePaths", ReadSourcePaths() },
            { "AvailableFilterNames", ReadStrings(Get(vm, "AvailableFilterNames")) },
            { "ChangedParameters", ReadRows(Get(vm, "ChangedParametersDisplay")) },
            { "RecommendationRows", ReadRows(Get(vm, "RecommendationRows")) },
            { "StarsPerFrame", ReadRows(Get(vm, "StarsPerFrame")) },
            { "SelectedCurve", ReadCurve(Get(vm, "SelectedCurve")) },
            { "Summary", Get(vm, "Summary") is object summary ? ReadScalars(summary) : null },
            { "Commands", AllowedCommands.ToDictionary(name => name, name => (object)CommandState(Get(vm, name))) },
            { "EnumOptions", EnumOptions() },
            { "AutoFocusSavePath", GetAutoFocusSavePath() },
            // A live sweep is refused while a sequence runs; lets the client say so before the tap.
            { "SequenceRunning", ImagingActivity.IsSequenceRunning() },
        };
        lock (stateLock)
        {
            state["PendingConfirmation"] = pendingConfirmation == null
                ? null
                : new Dictionary<string, object>
                {
                    { "Id", pendingConfirmation.Id },
                    { "Title", pendingConfirmation.Title },
                    { "Message", pendingConfirmation.Message },
                };
            state["LastCommandError"] = lastCommandError;
        }
        return state;
    }

    private static Dictionary<string, object> CommandState(object command)
    {
        if (command == null)
        {
            return new Dictionary<string, object> { { "Available", false } };
        }
        var isRunning = Get(command, "IsRunning") as bool? ?? false;
        return new Dictionary<string, object>
        {
            { "Available", true },
            { "CanExecute", CanExecute(command) },
            { "IsRunning", isRunning },
        };
    }

    // SourcePaths as the client knows them: relative to the AutoFocus save folder when they sit inside it.
    private List<string> ReadSourcePaths()
    {
        var savePath = GetAutoFocusSavePath();
        var root = savePath == null ? null : Path.GetFullPath(savePath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return ReadStrings(Get(vm, "SourcePaths"))
            .Select(path => path != null && root != null && path.StartsWith(root, StringComparison.Ordinal)
                ? path.Substring(root.Length)
                : path)
            .ToList();
    }

    // The summary's focus curve for the selected variant: the measured points (core sweep and focus-recovery
    // points apart, as the desktop draws them), the fitted hyperbola sampled across their range, and its minimum.
    private static Dictionary<string, object> ReadCurve(object curve)
    {
        if (curve == null)
        {
            return null;
        }
        var result = ReadScalars(curve);
        var points = ReadRows(Get(curve, "Points"));
        result["Points"] = points;
        result["CorePoints"] = ReadRows(Get(curve, "CorePoints"));
        result["RecoveryPoints"] = ReadRows(Get(curve, "RecoveryPoints"));
        result["Minimum"] = ReadScalars(Get(curve, "Minimum"));

        var fitCurve = new List<Dictionary<string, object>>();
        var xs = points.Select(point => point.TryGetValue("X", out var x) && x is double d ? d : double.NaN)
            .Where(double.IsFinite)
            .ToList();
        if (Get(curve, "Fitting") is Func<double, double> fitting && xs.Count > 1)
        {
            const int samples = 60;
            double min = xs.Min(), max = xs.Max();
            for (var i = 0; i <= samples; i++)
            {
                var x = min + (max - min) * i / samples;
                try
                {
                    var y = fitting(x);
                    if (double.IsFinite(y))
                    {
                        fitCurve.Add(new Dictionary<string, object> { { "X", x }, { "Y", y } });
                    }
                }
                catch
                {
                    // Outside the fit's domain; leave the gap.
                }
            }
        }
        result["FitCurve"] = fitCurve;
        return result;
    }

    private static List<string> ReadStrings(object value) =>
        value is IEnumerable items ? items.Cast<object>().Select(item => item?.ToString()).ToList() : new List<string>();

    private static List<Dictionary<string, object>> ReadRows(object value) =>
        value is IEnumerable rows && value is not string
            ? rows.Cast<object>().ToList().Select(ReadScalars).ToList()
            : new List<Dictionary<string, object>>();

    // Every readable scalar property (string, bool, number, enum, and lists of numbers), skipping any getter that
    // throws: a few of the wizard's display texts only make sense on certain steps.
    private static Dictionary<string, object> ReadScalars(object obj)
    {
        var values = new Dictionary<string, object>();
        if (obj == null)
        {
            return values;
        }
        foreach (var property in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0 || !property.CanRead)
            {
                continue;
            }
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            try
            {
                if (IsScalar(type))
                {
                    values[property.Name] = Scalar(property.GetValue(obj));
                }
                else if (typeof(IEnumerable<double>).IsAssignableFrom(property.PropertyType))
                {
                    values[property.Name] = ((IEnumerable<double>)property.GetValue(obj))?.Select(d => Scalar(d)).ToList();
                }
            }
            catch
            {
                // Not meaningful on the current step.
            }
        }
        return values;
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal);

    private static object Scalar(object value) => value switch
    {
        double d when !double.IsFinite(d) => null,
        float f when !float.IsFinite(f) => null,
        Enum e => e.ToString(),
        _ => value,
    };

    // Choices for the enum inputs, with the wizard's own display text ([Description]) where it has one.
    private Dictionary<string, object> EnumOptions()
    {
        var options = new Dictionary<string, object>();
        foreach (var name in SettableProperties)
        {
            var type = vmType.GetProperty(name)?.PropertyType;
            type = type == null ? null : Nullable.GetUnderlyingType(type) ?? type;
            if (type == null || !type.IsEnum)
            {
                continue;
            }
            options[name] = Enum.GetNames(type)
                .Select(value => new Dictionary<string, object>
                {
                    { "Value", value },
                    { "Label", type.GetField(value)?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? value },
                })
                .ToList();
        }
        return options;
    }

    private static object Get(object obj, string name) =>
        obj?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);

    // ---- Teardown ------------------------------------------------------------------------------------------

    private void DisposeCore()
    {
        PendingConfirmation pending;
        lock (stateLock)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            pending = pendingConfirmation;
        }
        idleTimer?.Dispose();
        // Release a command blocked on a confirmation first, as "no", so it unwinds instead of waiting out the timeout.
        pending?.Answer.TrySetResult(false);
        try
        {
            vmType.GetEvent("RequestClose")?.RemoveEventHandler(vm, requestCloseHandler);
            // Cancel anything in flight before disposing, as closing the desktop window does.
            var cancel = Get(vm, "CancelCommand");
            if (cancel != null && CanExecute(cancel))
            {
                cancel.GetType().GetMethod("Execute", new[] { typeof(object) })?.Invoke(cancel, new object[] { null });
            }
            (vm as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Error($"[Optimizer] Error disposing the optimization wizard: {ex}");
        }
    }

    public void Dispose() => EndCurrent(this, "closed");
}
