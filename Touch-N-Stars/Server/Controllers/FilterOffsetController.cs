using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TouchNStars.Server.Infrastructure;
using TouchNStars.Server.Models;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// Controller for the Filter Offset Calculator (DarksCustoms plugin) web interface.
/// Replicates the logic of FilterOffsetCalculator.Execute() without requiring WPF dialogs.
/// 
/// Workflow:
///   POST /api/filter-offset/start   → starts background calculation
///   GET  /api/filter-offset/status  → poll for progress
///   GET  /api/filter-offset/stop    → cancel
///   GET  /api/filter-offset/filters → list profile filters
///   GET  /api/filter-offset/result  → fetch pending old/new offsets (state == PendingResult)
///   POST /api/filter-offset/apply   → write the chosen offsets to the profile
///   GET  /api/filter-offset/discard → restore old values and go back to Idle
/// </summary>
public class FilterOffsetController : WebApiController
{
    // ── Shared state ─────────────────────────────────────────────────────────
    // All of this is static and reachable from every HTTP entry point at once (several browser tabs,
    // a stale bundle, a script). Guards the state-machine transitions in start/apply/discard; the
    // background calculation itself runs outside it, so a running calculation never blocks polling.
    private static readonly object _stateLock = new object();

    private static Task _offsetTask;
    private static CancellationTokenSource _cts;

    // "Idle" | "Running" | "PendingResult" | "Error"
    private static string _state = "Idle";
    private static int _currentLoop;
    private static int _totalLoops;
    private static int _currentFilterIndex;
    private static int _totalFilters;
    private static string _currentFilterName = "";
    private static string _errorMessage = "";

    // Saved values for discard
    private static bool _oldUseOffsets;
    private static int? _oldDefaultFilterPosition;
    private static List<(int Position, string Name, int FocusOffset)> _oldOffsets = new();

    // Computed result waiting for user accept/discard
    private static FilterOffsetResult _result;

    // ── GET /api/filter-offset/filters ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/filters")]
    public ApiResponse GetFilters()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null)
            {
                HttpContext.Response.StatusCode = 503;
                return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
            }

            var filters = profile.FilterWheelSettings.FilterWheelFilters
                .Select((f, idx) => new
                {
                    index = idx,
                    position = (int)f.Position,
                    name = f.Name,
                    focusOffset = f.FocusOffset,
                    autoFocusFilter = f.AutoFocusFilter,
                    autoFocusExposureTime = f.AutoFocusExposureTime,
                })
                .ToList();

            return new ApiResponse
            {
                Success = true,
                StatusCode = 200,
                Type = "FilterList",
                Response = new
                {
                    Filters = filters,
                    UseFilterWheelOffsets = profile.FocuserSettings.UseFilterWheelOffsets,
                    DefaultAutofocusExposureTime = profile.FocuserSettings.AutoFocusExposureTime,
                }
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── POST /api/filter-offset/start ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/start")]
    public async Task<ApiResponse> StartCalculation()
    {
        if (_offsetTask != null && !_offsetTask.IsCompleted)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "Calculation already running", StatusCode = 409, Type = "Error" };
        }

        // The task completes as soon as a result is ready, so IsCompleted alone does not mean the
        // controller is free. Restarting while a result is pending would capture the offsets this
        // run zeroed in the profile as the "old" values, making the user's real offsets
        // unrecoverable — /discard would then restore zeros. Force accept or discard first.
        if (_state == "PendingResult")
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse
            {
                Success = false,
                Error = "A calculated result is still pending — accept or discard it before starting a new calculation",
                StatusCode = 409,
                Type = "Error"
            };
        }

        FilterOffsetStartRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetStartRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        if (payload?.FilterPositions == null || payload.FilterPositions.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No filters specified", StatusCode = 400, Type = "Error" };
        }

        if (payload.Loops < 1)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "Loops must be >= 1", StatusCode = 400, Type = "Error" };
        }

        var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
        if (profile == null)
        {
            HttpContext.Response.StatusCode = 503;
            return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
        }

        var selectedFilters = profile.FilterWheelSettings.FilterWheelFilters
            .Where(f => payload.FilterPositions.Contains((int)f.Position))
            .OrderBy(f => f.Position)
            .ToList();

        if (selectedFilters.Count == 0)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = "No matching filters found in profile", StatusCode = 400, Type = "Error" };
        }

        CancellationToken token;
        lock (_stateLock)
        {
            // Re-check under the lock: the guards above ran before the request body was read, so two
            // near-simultaneous starts could both have passed them. Go by _state rather than the
            // task handle — _state flips to Running here, while _offsetTask is only assigned after
            // the lock is released and would still look completed to a racing second start.
            if (_state == "Running" || _state == "PendingResult")
            {
                HttpContext.Response.StatusCode = 409;
                return new ApiResponse { Success = false, Error = "Calculation already running", StatusCode = 409, Type = "Error" };
            }

            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            token = _cts.Token;

            _state = "Running";
            _currentLoop = 0;
            _totalLoops = payload.Loops;
            _currentFilterIndex = 0;
            _totalFilters = selectedFilters.Count;
            _currentFilterName = "";
            _errorMessage = "";
            _result = null;
        }

        // Deliberately NOT Task.Run(..., token): with a token, a cancel that lands before the
        // threadpool picks the delegate up completes the task as Canceled without ever running it,
        // so none of the recovery below happens and _state stays "Running" forever — which the
        // restart guard then reads as "busy" for the rest of the process's life. The delegate
        // observes cancellation through RunCalculation's own ThrowIfCancellationRequested instead.
        _offsetTask = Task.Run(async () =>
        {
            try
            {
                await RunCalculation(selectedFilters, payload.Loops, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Logger.Info("FilterOffset: calculation cancelled");
                lock (_stateLock)
                {
                    RestoreOldValues();
                    _state = "Idle";
                }
            }
            catch (Exception ex)
            {
                // Includes the TaskCanceledException HttpClient raises on its own timeout — that is
                // a failure, not a user cancellation, and must not be reported as one.
                Logger.Error($"FilterOffset: calculation failed: {ex}");
                lock (_stateLock)
                {
                    RestoreOldValues();
                    _state = "Error";
                    _errorMessage = ex.Message;
                }
            }
        });

        return new ApiResponse { Success = true, Response = "Filter offset calculation started", StatusCode = 200, Type = "Success" };
    }

    // ── GET /api/filter-offset/status ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/status")]
    public ApiResponse GetStatus()
    {
        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = new
            {
                State = _state,
                CurrentLoop = _currentLoop,
                TotalLoops = _totalLoops,
                CurrentFilterIndex = _currentFilterIndex,
                TotalFilters = _totalFilters,
                CurrentFilterName = _currentFilterName,
                Error = _errorMessage,
            }
        };
    }

    // ── GET /api/filter-offset/stop ──────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/stop")]
    public ApiResponse StopCalculation()
    {
        try
        {
            // Under the lock so this can't land between StartCalculation disposing the old source
            // and installing the new one — cancelling a disposed source throws, and cancelling the
            // brand new one would abort a run that has not begun.
            lock (_stateLock)
            {
                _cts?.Cancel();
            }
            return new ApiResponse { Success = true, Response = "Stop requested", StatusCode = 200, Type = "Success" };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/result ────────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/result")]
    public ApiResponse GetResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            HttpContext.Response.StatusCode = 404;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 404, Type = "Error" };
        }

        return new ApiResponse
        {
            Success = true,
            StatusCode = 200,
            Type = "Success",
            Response = _result
        };
    }

    // ── POST /api/filter-offset/apply ────────────────────────────────────────
    [Route(HttpVerbs.Post, "/filter-offset/apply")]
    public async Task<ApiResponse> ApplyResult()
    {
        if (_state != "PendingResult" || _result == null)
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        FilterOffsetApplyRequest payload;
        try
        {
            using var reader = new StreamReader(HttpContext.Request.InputStream);
            var body = await reader.ReadToEndAsync();
            payload = JsonSerializer.Deserialize<FilterOffsetApplyRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse { Success = false, Error = $"Invalid request body: {ex.Message}", StatusCode = 400, Type = "Error" };
        }

        // Everything below mutates the shared state machine and the profile, so it runs under the
        // same lock as start/discard: two clients hitting Accept, or Accept racing Discard, must not
        // both get past the pending check and write the profile twice. Re-check inside the lock —
        // the check above happened before the request body was read.
        try
        {
            lock (_stateLock)
            {
                if (_state != "PendingResult" || _result == null)
                {
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
                }

                var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
                if (profile == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Profile not available", StatusCode = 503, Type = "Error" };
                }

                // NewOffsets are already relative to the base filter (see ComputeOffsets). Shifting the
                // whole set by a constant never changes the spacing between the measured filters, which
                // is all the focuser move between two calibrated filters depends on — but it does change
                // how the measured block lines up with any filter that was left out of the calibration,
                // since those still carry offsets on the old zero point. So always anchor the set on one
                // measured filter's PREVIOUS offset: that filter's moves to and from the uncalibrated
                // ones stay exactly as they were, and the recalibration only redistributes the rest.
                //
                // Any measured filter works as the anchor. Prefer the one the user is selecting as the
                // new AutoFocus filter, since keeping that one where it was is the least surprising
                // outcome, but fall back to the base filter whenever that selection is not part of
                // this calibration — including "None". Picking an uncalibrated filter as the AutoFocus
                // filter stays allowed; it just does not get to be the anchor.
                var newOffsets = _result.NewOffsets
                    .Select(o => (o.Position, o.Name, o.FocusOffset))
                    .ToList();

                if (newOffsets.Count > 0)
                {
                    int anchorIndex = payload?.NewDefaultFilterPosition is int afPosition
                        ? newOffsets.FindIndex(o => o.Position == afPosition)
                        : -1;
                    if (anchorIndex < 0) anchorIndex = 0;

                    var (anchorPosition, _, measured) = newOffsets[anchorIndex];
                    int anchor = _result.OldOffsets?.FirstOrDefault(o => o.Position == anchorPosition)?.FocusOffset ?? 0;
                    if (measured != anchor)
                    {
                        newOffsets = newOffsets
                            .Select(o => (o.Position, o.Name, o.FocusOffset - measured + anchor))
                            .ToList();
                    }

                    Logger.Info($"FilterOffset: anchoring offsets on filter position {anchorPosition} at its " +
                                $"previous offset {anchor} (measured {measured})");
                }

                // Write offsets to profile
                profile.FocuserSettings.UseFilterWheelOffsets = true;
                foreach (var (Position, Name, FocusOffset) in newOffsets)
                {
                    var f = profile.FilterWheelSettings.FilterWheelFilters
                        .FirstOrDefault(x => x.Position == Position);
                    if (f != null)
                        f.FocusOffset = FocusOffset;
                }

                // Set new AutoFocus filter
                foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                    f.AutoFocusFilter = f.Position == (payload?.NewDefaultFilterPosition ?? -1);

                // Profile.Save() swallows its own IO exceptions and returns normally, so a successful
                // return here does not mean anything reached disk — don't claim it did. The in-memory
                // profile is authoritative either way, and ProfileService's save timer retries.
                profile.Save();

                _result = null;
                _state = "Idle";

                return new ApiResponse { Success = true, Response = "Offsets applied", StatusCode = 200, Type = "Success" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── GET /api/filter-offset/discard ───────────────────────────────────────
    [Route(HttpVerbs.Get, "/filter-offset/discard")]
    public ApiResponse DiscardResult()
    {
        if (_state != "PendingResult")
        {
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
        }

        try
        {
            lock (_stateLock)
            {
                // Re-check under the lock: a concurrent /apply may already have consumed the result,
                // and restoring the old values on top of a completed apply would undo it.
                if (_state != "PendingResult")
                {
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse { Success = false, Error = "No result pending", StatusCode = 409, Type = "Error" };
                }

                RestoreOldValues();
                _result = null;
                _state = "Idle";
                return new ApiResponse { Success = true, Response = "Discarded; old values restored", StatusCode = 200, Type = "Success" };
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
        }
    }

    // ── Calculation ───────────────────────────────────────────────────────────

    private async Task RunCalculation(List<FilterInfo> selectedFilters, int loops, CancellationToken token)
    {
        var profile = TouchNStars.Mediators.Profile.ActiveProfile;

        // 1. Save old state
        _oldUseOffsets = profile.FocuserSettings.UseFilterWheelOffsets;
        _oldDefaultFilterPosition = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.AutoFocusFilter)?.Position;
        _oldOffsets = selectedFilters
            .Select(f => ((int)f.Position, f.Name, f.FocusOffset))
            .ToList();

        // 2. Setup: disable offsets & autofocus-filter flag, reset offsets to 0
        profile.FocuserSettings.UseFilterWheelOffsets = false;
        foreach (var f in selectedFilters)
        {
            f.AutoFocusFilter = false;
            f.FocusOffset = 0;
        }

        // position → list of AF settled positions, one per loop
        var calculatedPositions = new Dictionary<int, List<int>>();
        foreach (var f in selectedFilters)
            calculatedPositions[(int)f.Position] = new List<int>();

        var apiUrl = await CoreUtility.GetApiUrl();
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        // 3. Run loops × filters. The status field tracks the counter rather than being the counter,
        // so it never reports loops + 1 after the last iteration increments past the bound.
        for (int loop = 1; loop <= loops; loop++)
        {
            _currentLoop = loop;
            _currentFilterIndex = 0;

            foreach (var filter in selectedFilters)
            {
                token.ThrowIfCancellationRequested();

                _currentFilterIndex++;
                _currentFilterName = filter.Name;

                Logger.Info($"FilterOffset: loop {_currentLoop}/{loops} — switching to filter '{filter.Name}' (position {filter.Position})");

                // Re-apply suppression immediately before each filter's AF attempts.
                // Something resets UseFilterWheelOffsets or AutoFocusFilter between iterations;
                // keeping both false prevents HocusFocus's SetAutofocusFilter from switching
                // the filter wheel away from the intended target during the AF run.
                profile.FocuserSettings.UseFilterWheelOffsets = false;
                foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                    f.AutoFocusFilter = false;

                int position = await MeasureFilterFocusPositionAsync(filter, apiUrl, client, token);
                Logger.Info($"FilterOffset: filter '{filter.Name}' settled at position {position}");

                calculatedPositions[(int)filter.Position].Add(position);
            }
        }

        // 4. Compute new offsets using the same algorithm as FilterOffsetCalculator.Execute()
        var newOffsets = ComputeOffsets(selectedFilters, calculatedPositions, profile);

        // 5. Store result and move to PendingResult state. This is the only transition INTO the state
        // the HTTP handlers wait on, so it publishes under the same lock they read under — otherwise
        // the handlers hold a lock that the writer ignores, and the freshly built result has no
        // release barrier pairing with their acquire (which matters on the ARM targets).
        //
        // Keeping the existing AutoFocus filter is always a valid choice, including when it was not
        // one of the calibrated filters, so it is suggested unchanged — the apply step then anchors
        // on the base filter instead.
        lock (_stateLock)
        {
            _result = new FilterOffsetResult
            {
                OldOffsets = _oldOffsets
                    .Select(o => new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset })
                    .ToList(),
                NewOffsets = newOffsets
                    .Select(o => new FilterOffsetEntry { Position = o.Position, Name = o.Name, FocusOffset = o.FocusOffset })
                    .ToList(),
                OldDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
                SuggestedDefaultFilterPosition = _oldDefaultFilterPosition.HasValue ? (int?)_oldDefaultFilterPosition.Value : null,
            };

            _state = "PendingResult";
        }
    }

    // Runs AF for one filter and returns its settled focuser position, guaranteeing the wheel was
    // actually on the requested filter for the whole run — not just at the moment ChangeFilter was
    // first called.
    //
    // The race this guards against: DataContainer.afRun (mirroring HocusFocus's "AF completed" broadcast)
    // clears BEFORE HocusFocus's own post-run cleanup restores the filter that was active when THAT run
    // started. Reacting to that early signal and switching to the next filter lets the still-in-flight
    // cleanup silently move the wheel back afterward — right as the next AF trigger fires. That gets
    // rejected ("Another AutoFocus is already in progress"), and once the retry succeeds, the wheel is
    // sitting on the reverted (wrong) filter.
    //
    // The primary fix is WaitForHocusFocusCleanupAsync below: it doesn't let this method return — and so
    // doesn't let the caller switch to the next filter — until HocusFocus's OWN in-progress guard
    // (HocusFocusVM.AutoFocusInProgress) has cleared, which only happens after that filter-restore cleanup
    // has actually finished. Re-asserting ChangeFilter before every trigger attempt (including retries),
    // and verifying the actually-mounted filter right after completion, are kept as defense in depth for
    // anything else (a manual AF from another client, etc.) that might still move the wheel mid-run.
    private static async Task<int> MeasureFilterFocusPositionAsync(FilterInfo filter, string apiUrl, HttpClient client, CancellationToken token)
    {
        const int maxVerificationAttempts = 3;

        for (var verificationAttempt = 1; ; verificationAttempt++)
        {
            token.ThrowIfCancellationRequested();

            await StartAutofocusWithRetryAsync(apiUrl, client, filter, token);

            // Wait until the AF file watcher (BackgroundWorker) signals completion
            await WaitForAutofocusAsync(token);

            if (DataContainer.afError)
                throw new Exception($"AutoFocus failed for filter '{filter.Name}'");

            // The just-completed run's own filter-restore cleanup hasn't fired yet at this point (it only
            // runs after the completion signal we just waited on), so the wheel still reflects whatever
            // filter the run actually measured through.
            var actualFilter = TouchNStars.Mediators.FilterWheel.GetInfo()?.SelectedFilter;
            if (actualFilter != null && actualFilter.Position != filter.Position)
            {
                if (verificationAttempt >= maxVerificationAttempts)
                    throw new Exception($"FilterOffset: AutoFocus for filter '{filter.Name}' kept measuring through filter '{actualFilter.Name}' instead after {verificationAttempt} attempts");

                Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' actually ran through filter '{actualFilter.Name}' (race with previous run's cleanup) — discarding result and re-measuring (attempt {verificationAttempt})");
                await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);
                continue;
            }

            int position = await GetFocuserPositionAsync(apiUrl, client, token);

            // Don't return — and so don't let the caller switch to the next filter — until HocusFocus has
            // actually finished tearing this run down (filter restored, guard released). This is what
            // prevents the race from happening in the first place, rather than just detecting it above.
            await WaitForHocusFocusCleanupAsync(token);

            return position;
        }
    }

    // Reflects into HocusFocus's own in-progress guard (HocusFocusVM.Current.AutoFocusInProgress), which —
    // unlike DataContainer.afRun — only clears in the finally block of HocusFocusVM.StartAutoFocus, i.e.
    // after "await autoFocusEngine.Run(...)" has fully returned, including AutoFocusEngine.RunImpl's own
    // outer finally (PerformPostAutoFocusActions' filter restore, then ReleaseAutoFocusInProgress). That
    // makes it the one externally-observable signal that means "safe to switch to the next filter now".
    // Returns null (and doesn't block) if HocusFocus isn't loaded or the property can't be read, so this
    // degrades gracefully rather than hanging the calibration on an unrelated AF backend.
    private static bool? IsHocusFocusAutoFocusInProgress()
    {
        try
        {
            var hocusFocusVMType = Type.GetType("NINA.Joko.Plugins.HocusFocus.AutoFocus.HocusFocusVM, NINA.Joko.Plugins.HocusFocus");
            var currentVM = hocusFocusVMType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return currentVM?.GetType().GetProperty("AutoFocusInProgress")?.GetValue(currentVM) as bool?;
        }
        catch (Exception ex)
        {
            Logger.Warning($"FilterOffset: could not read HocusFocus AutoFocusInProgress via reflection: {ex.Message}");
            return null;
        }
    }

    private static async Task WaitForHocusFocusCleanupAsync(CancellationToken token)
    {
        // PerformPostAutoFocusActions' individual steps (filter restore, temp-comp restore, guiding
        // restart) each allow up to 1 minute, so give this generous headroom before giving up.
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (IsHocusFocusAutoFocusInProgress() != true) return; // false (torn down) or null (unavailable) — either way, don't block

            await Task.Delay(250, token);
        }

        Logger.Warning("FilterOffset: HocusFocus AutoFocus cleanup did not clear within 90s — proceeding anyway");
    }

    private static async Task StartAutofocusWithRetryAsync(string apiUrl, HttpClient client, FilterInfo filter, CancellationToken token)
    {
        // The previous run's post-AF cleanup steps each time out after 1 minute (filter restore,
        // temp-comp restore, guiding restart), so allow up to 3 minutes of re-triggering before
        // giving up with a clear error instead of hanging.
        var deadline = DateTime.UtcNow.AddMinutes(3);

        for (var attempt = 1; ; attempt++)
        {
            token.ThrowIfCancellationRequested();

            // Re-assert the target filter right before every trigger attempt, including retries — a
            // previous run's delayed cleanup can revert the wheel between attempts (see
            // MeasureFilterFocusPositionAsync), and this is the last write before AF captures "current
            // filter" as the run's imaging filter.
            await TouchNStars.Mediators.FilterWheel.ChangeFilter(filter, token);

            // Reset AF tracking state and start AF (ninaAPI call is async: returns "started" immediately)
            lock (DataContainer.lockObj)
            {
                DataContainer.afRun = true;
                DataContainer.afError = false;
                DataContainer.afErrorText = string.Empty;
                DataContainer.newAfGraph = false;
                DataContainer.afStartConfirmed = false;
            }

            await client.GetAsync($"{apiUrl}/equipment/focuser/auto-focus", token);

            if (await WaitForAutofocusStartAsync(token)) return;

            if (DateTime.UtcNow >= deadline)
                throw new Exception($"AutoFocus did not start for filter '{filter.Name}' after {attempt} attempts — a previous AutoFocus run may be stuck (see NINA log)");

            Logger.Warning($"FilterOffset: AutoFocus for filter '{filter.Name}' did not start (attempt {attempt}, previous run likely still finishing) — retrying");
            await Task.Delay(5000, token);
        }
    }

    private static async Task<bool> WaitForAutofocusStartAsync(CancellationToken token)
    {
        // AutoFocusRunStarting is broadcast right after the AF run claims its in-progress guard,
        // well before any exposures, so a healthy start confirms within a few seconds.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            lock (DataContainer.lockObj)
            {
                if (DataContainer.afStartConfirmed) return true;
            }

            await Task.Delay(500, token);
        }
        return false;
    }

    private static async Task WaitForAutofocusAsync(CancellationToken token)
    {
        // Give NINA a moment to write its AF file / log before we start polling
        await Task.Delay(2000, token);

        var deadline = DateTime.UtcNow.AddMinutes(20);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            bool stillRunning;
            lock (DataContainer.lockObj)
                stillRunning = DataContainer.afRun;

            if (!stillRunning) return;

            await Task.Delay(1000, token);
        }

        throw new TimeoutException("AutoFocus did not complete within 20 minutes");
    }

    // Never returns a sentinel on failure. A position of 0 is indistinguishable from a real reading,
    // and every offset in the result is a difference against these numbers — one unreadable position
    // silently turns the whole calibration into garbage (a failed read for the base filter alone
    // would push every other filter's offset up to a near-absolute focuser position). Failing the
    // run instead costs the user a repeat, which is the cheaper of the two outcomes by far.
    private static async Task<int> GetFocuserPositionAsync(string apiUrl, HttpClient client, CancellationToken token)
    {
        const int maxAttempts = 3;
        string lastError = "unknown error";

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();

            try
            {
                var resp = await client.GetAsync($"{apiUrl}/equipment/focuser/info", token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(token);
                    using var doc = JsonDocument.Parse(json);

                    if (doc.RootElement.TryGetProperty("Response", out var response) &&
                        response.TryGetProperty("Position", out var pos))
                        return pos.GetInt32();

                    lastError = "response contained no focuser position";
                }
                else
                {
                    lastError = $"HTTP {(int)resp.StatusCode}";
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            Logger.Warning($"FilterOffset: could not read focuser position ({lastError}) — attempt {attempt}/{maxAttempts}");
            if (attempt < maxAttempts) await Task.Delay(1000, token);
        }

        throw new Exception($"Could not read the focuser position after {maxAttempts} attempts: {lastError}");
    }

    // Smallest deviation-from-median (in focuser steps) that may ever be treated as an outlier.
    // Scaled from the AutoFocus step size so it tracks the setup rather than assuming one focuser:
    // a run that lands within half an AF sampling step of the others is normal AF scatter, not a
    // bad measurement. The absolute floor only guards against a nonsensically small step size.
    private static double OutlierToleranceSteps(NINA.Profile.Interfaces.IProfile profile)
        => Math.Max(5.0, profile.FocuserSettings.AutoFocusStepSize / 2.0);

    private static double Median(IReadOnlyList<double> sorted)
        => sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2.0;

    /// <summary>
    /// Robustly aggregates one filter's per-loop AF positions into a single focus position.
    ///
    /// A plain mean lets a single bad loop drag the result by a fraction of its error, which is the
    /// opposite of what running multiple loops is for. Instead: take the median, drop samples that
    /// are both statistically extreme (&gt; 3 scaled MADs) and beyond the tolerance floor, then mean
    /// what survives — the median resists the outlier, the mean of the survivors keeps the precision.
    ///
    /// Note this can only reject numerically outlying samples. A loop that measured through the
    /// wrong filter still passes if that filter happens to be near-parfocal with the intended one.
    ///
    /// Survivors keep their zero-based loop index, because dropping a loop from the middle of the
    /// series leaves a gap that the temperature-drift estimate has to account for.
    ///
    /// Rejections are reported through <paramref name="reportRejections"/> rather than straight to
    /// Logger: touching NINA's Logger initialises it, which creates a log file and prunes the real
    /// user log directory — not something a unit test of this arithmetic should do.
    /// </summary>
    internal static List<(int Loop, int Position)> RejectOutliers(
        string filterName,
        List<int> positions,
        double tolerance,
        Action<string> reportRejections = null)
    {
        var all = positions.Select((p, i) => (Loop: i, Position: p)).ToList();
        if (positions.Count < 3) return all;

        var sorted = positions.Select(p => (double)p).OrderBy(p => p).ToList();
        double median = Median(sorted);

        var deviations = positions.Select(p => Math.Abs(p - median)).OrderBy(d => d).ToList();
        // 1.4826 rescales the MAD to be comparable to a standard deviation for normal data.
        double cutoff = Math.Max(tolerance, 3.0 * 1.4826 * Median(deviations));

        var accepted = new List<(int Loop, int Position)>();
        var rejected = new List<string>();
        for (int i = 0; i < positions.Count; i++)
        {
            if (Math.Abs(positions[i] - median) > cutoff)
                rejected.Add($"loop {i + 1}: {positions[i]} ({positions[i] - median:+0.#;-0.#;0} from median)");
            else
                accepted.Add((i, positions[i]));
        }

        if (rejected.Count > 0)
        {
            reportRejections?.Invoke(
                $"FilterOffset: discarded {rejected.Count} outlying measurement(s) for filter '{filterName}' " +
                $"(median {median:0.#}, cutoff ±{cutoff:0.#} steps): {string.Join("; ", rejected)}. " +
                "A loop this far off usually means the AutoFocus ran through a different filter than intended " +
                "— check the filter wheel for slot slippage.");
        }

        // Defensive only: the median is at or between the samples, so at least one always survives
        // the cutoff. Kept so a future change to the cutoff can't start returning an empty set and
        // silently produce an offset out of no measurements at all.
        return accepted.Count > 0 ? accepted : all;
    }

    /// <summary>
    /// Mean per-loop focus drift of one filter's surviving samples, positive when the focus position
    /// moves down over the run.
    ///
    /// The divisor is the loop span, not the sample count: the samples are one per loop, but outlier
    /// rejection can punch a hole in the middle of the series, and a survivor pair that straddles a
    /// discarded loop is still separated by every loop that sat between them.
    /// </summary>
    internal static double EstimatePerLoopDrift(IReadOnlyList<(int Loop, int Position)> samples)
    {
        if (samples.Count < 2) return 0.0;

        int loopSpan = samples[^1].Loop - samples[0].Loop;
        return loopSpan > 0
            ? (samples[0].Position - samples[^1].Position) / (double)loopSpan
            : 0.0;
    }

    /// <summary>
    /// One filter's surviving samples, averaged and drift-corrected back to the start of the run so
    /// that filters measured at different times are directly comparable.
    ///
    /// Two corrections, both in units of loops:
    ///  * <paramref name="withinLoopRatio"/> is where in a loop this filter is measured — the later
    ///    its turn, the more the focus has already moved since that loop began.
    ///  * the mean loop index is which loops the samples actually came from. Outlier rejection runs
    ///    per filter, so one filter can lose an early loop that its neighbours kept, which drags its
    ///    mean later in time by an amount no within-loop term can see. With complete sample sets
    ///    this term is the same constant for every filter and cancels out of the offsets entirely,
    ///    which is why it was invisible before rejection existed.
    /// </summary>
    internal static double EstimateFocusAtRunStart(
        IReadOnlyList<(int Loop, int Position)> samples,
        double driftPerLoop,
        double withinLoopRatio)
        => samples.Average(p => p.Position)
           + ((samples.Average(p => p.Loop) + withinLoopRatio) * driftPerLoop);

    /// <summary>
    /// Aggregates the per-loop AF results into focus offsets.
    ///
    /// Based on the offset math from FilterOffsetCalculator.Execute(), with two deliberate changes:
    ///  * per-filter aggregation is a median with outlier rejection rather than a plain mean, so one
    ///    bad loop out of several no longer corrupts every filter's result;
    ///  * the values returned are offsets relative to the base filter — the lowest-position filter in
    ///    the selection, which is also the one measured first in every loop — not absolute focuser
    ///    positions. FocusOffset is a relative quantity everywhere else in NINA: it is displayed as
    ///    one, compared against the existing offsets in OldOffsets, and pushed to hardware verbatim
    ///    by some wheels (e.g. OasisFilterWheel.StoreFocusOffsets). The apply step can still
    ///    re-reference these onto whichever filter the user picks as the new AutoFocus filter.
    /// </summary>
    private static List<(int Position, string Name, int FocusOffset)> ComputeOffsets(
        List<FilterInfo> selectedFilters,
        Dictionary<int, List<int>> calculatedPositions,
        NINA.Profile.Interfaces.IProfile profile)
    {
        double tolerance = OutlierToleranceSteps(profile);

        // Outlier-filtered samples per filter position, each tagged with the loop it came from.
        var accepted = new Dictionary<int, List<(int Loop, int Position)>>();
        foreach (var f in selectedFilters)
        {
            int pos = (int)f.Position;
            accepted[pos] = RejectOutliers(f.Name, calculatedPositions[pos], tolerance, msg => Logger.Warning(msg));
        }

        // Temperature drift is estimated from the first (base) filter, across surviving samples only
        // so a rejected loop can't masquerade as thermal movement.
        double temperatureDrift = selectedFilters.Count > 0
            ? EstimatePerLoopDrift(accepted[(int)selectedFilters[0].Position])
            : 0.0;

        double defaultAfTime = profile.FocuserSettings.AutoFocusExposureTime;
        double defaultFilterAfTime = new FilterInfo().AutoFocusExposureTime;

        // Total exposure time across selected filters (used for ratio weighting). Kept as a double:
        // truncating it skews every within-loop ratio, and sub-second exposures would truncate the
        // whole sum to 0 and silently fall back to the guard below.
        double totalTime = selectedFilters.Sum(f =>
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == f.Position);
            return pf?.AutoFocusExposureTime == defaultFilterAfTime ? defaultAfTime : (pf?.AutoFocusExposureTime ?? defaultAfTime);
        });
        if (totalTime <= 0.0) totalTime = 1.0;

        // Drift-corrected absolute focus position per filter, in selection order.
        var absolutePositions = new List<(int Position, string Name, double? Focus)>();
        double totalRatio = 0.0;

        foreach (var filter in selectedFilters)
        {
            var pf = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == filter.Position);
            double filterTime = pf?.AutoFocusExposureTime == defaultFilterAfTime
                ? defaultAfTime
                : (pf?.AutoFocusExposureTime ?? defaultAfTime);

            var positions = accepted[(int)filter.Position];
            double? focus = positions.Count > 0
                ? EstimateFocusAtRunStart(positions, temperatureDrift, totalRatio)
                : null;

            absolutePositions.Add(((int)filter.Position, filter.Name, focus));
            totalRatio += filterTime / totalTime;
        }

        // Convert to offsets relative to the base filter.
        double? baseFocus = absolutePositions.Count > 0 ? absolutePositions[0].Focus : null;
        if (baseFocus == null)
        {
            Logger.Warning("FilterOffset: base filter has no usable measurement — reporting all offsets as 0.");
            return absolutePositions.Select(p => (p.Position, p.Name, 0)).ToList();
        }

        Logger.Info($"FilterOffset: computing offsets relative to base filter '{absolutePositions[0].Name}' " +
                    $"at position {baseFocus.Value:0.#} (temperature drift {temperatureDrift:+0.##;-0.##;0} steps/loop)");

        var result = new List<(int Position, string Name, int FocusOffset)>();
        foreach (var (Position, Name, Focus) in absolutePositions)
        {
            int focusOffset = Focus.HasValue
                ? (int)Math.Round(Focus.Value - baseFocus.Value, MidpointRounding.AwayFromZero)
                : 0;

            if (!Focus.HasValue)
                Logger.Warning($"FilterOffset: no usable measurement for filter '{Name}' — offset set to 0.");

            result.Add((Position, Name, focusOffset));
        }

        return result;
    }

    private static void RestoreOldValues()
    {
        try
        {
            var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
            if (profile == null) return;

            profile.FocuserSettings.UseFilterWheelOffsets = _oldUseOffsets;

            foreach (var (Position, Name, FocusOffset) in _oldOffsets)
            {
                var f = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(x => x.Position == Position);
                if (f != null) f.FocusOffset = FocusOffset;
            }

            foreach (var f in profile.FilterWheelSettings.FilterWheelFilters)
                f.AutoFocusFilter = f.Position == (_oldDefaultFilterPosition ?? -1);
        }
        catch (Exception ex)
        {
            Logger.Error($"FilterOffset: failed to restore old values: {ex.Message}");
        }
    }
}

// ── Request / response models ─────────────────────────────────────────────────

public class FilterOffsetStartRequest
{
    public int Loops { get; set; } = 3;
    public List<int> FilterPositions { get; set; }
}

public class FilterOffsetApplyRequest
{
    /// <summary>
    /// Filter that becomes the AutoFocus filter, and the filter the new offsets are anchored on.
    /// Null leaves no AutoFocus filter set and anchors on the base filter instead.
    /// </summary>
    public int? NewDefaultFilterPosition { get; set; }
}

public class FilterOffsetEntry
{
    public int Position { get; set; }
    public string Name { get; set; }
    public int FocusOffset { get; set; }
}

public class FilterOffsetResult
{
    public List<FilterOffsetEntry> OldOffsets { get; set; }
    public List<FilterOffsetEntry> NewOffsets { get; set; }
    public int? OldDefaultFilterPosition { get; set; }
    public int? SuggestedDefaultFilterPosition { get; set; }
}
