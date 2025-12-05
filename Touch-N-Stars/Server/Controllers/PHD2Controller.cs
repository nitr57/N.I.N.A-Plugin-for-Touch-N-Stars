using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using TouchNStars.PHD2;
using TouchNStars.Server.Models;
using TouchNStars.Server.Services;
using TouchNStars.Utility;

namespace TouchNStars.Server.Controllers;

/// <summary>
/// API Controller for PHD2 guiding integration
/// </summary>
public class PHD2Controller : WebApiController
{
    private static PHD2Service phd2Service;
    private static PHD2ImageService phd2ImageService;
    private static readonly object phd2InitLock = new object();

    private static void EnsurePHD2ServicesInitialized()
    {
        if (phd2Service == null || phd2ImageService == null)
        {
            lock (phd2InitLock)
            {
                if (phd2Service == null)
                {
                    try
                    {
                        Logger.Debug("Initializing PHD2Service on demand");
                        phd2Service = new PHD2Service();
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to initialize PHD2Service: {ex}");
                        throw;
                    }
                }

                if (phd2ImageService == null && phd2Service != null)
                {
                    try
                    {
                        Logger.Debug("Initializing PHD2ImageService on demand");
                        phd2ImageService = new PHD2ImageService(phd2Service);
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Failed to initialize PHD2ImageService: {ex}");
                        throw;
                    }
                }
            }
        }
    }

    public static void CleanupPHD2Service() {
        phd2ImageService?.Dispose();
        phd2Service?.Dispose();
    }

    /// <summary>
    /// GET /api/phd2/status - Get PHD2 connection and guiding status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/status")]
    public async Task<ApiResponse> GetPHD2Status() {
        try {
            EnsurePHD2ServicesInitialized();
            var status = await phd2Service.GetStatusAsync();
            return new ApiResponse {
                Success = true,
                Response = status,
                StatusCode = 200,
                Type = "PHD2Status"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/connect - Connect to PHD2
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/connect")]
    public async Task<ApiResponse> ConnectPHD2() {
        try {
            EnsurePHD2ServicesInitialized();
            string hostname = "localhost";
            uint instance = 1;

            try {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null) {
                    if (requestData.ContainsKey("hostname") && requestData["hostname"] != null) {
                        hostname = requestData["hostname"].ToString();
                    }
                    if (requestData.ContainsKey("instance") && requestData["instance"] != null) {
                        if (uint.TryParse(requestData["instance"].ToString(), out uint parsedInstance)) {
                            instance = parsedInstance;
                        }
                    }
                }
            } catch {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.ConnectAsync(hostname, instance);

            return new ApiResponse {
                Success = result,
                Response = new { Connected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Connection"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/disconnect - Disconnect from PHD2
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/disconnect")]
    public async Task<ApiResponse> DisconnectPHD2() {
        try {
            EnsurePHD2ServicesInitialized();
            await phd2Service.DisconnectAsync();

            return new ApiResponse {
                Success = true,
                Response = new { Connected = false },
                StatusCode = 200,
                Type = "PHD2Connection"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/inject-profile - Inject a PHD2 profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/inject-profile")]
    public async Task<ApiResponse> InjectPHD2Profile() {
        try {
            EnsurePHD2ServicesInitialized();
            
            // Get PHD2 path from active profile settings
            var phd2Path = TouchNStars.Mediators?.Profile?.ActiveProfile?.GuiderSettings?.PHD2Path;
            if (string.IsNullOrEmpty(phd2Path)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "PHD2 path not configured",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.InjectProfilesAsync(phd2Path);

            return new ApiResponse {
                Success = result,
                Response = new { Injected = result },
                StatusCode = result ? 200 : 400,
                Type = "PHD2ProfileInjection"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/profiles - Get equipment profiles
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/profiles")]
    public async Task<ApiResponse> GetPHD2Profiles() {
        try {
            EnsurePHD2ServicesInitialized();
            var profiles = await phd2Service.GetEquipmentProfilesAsync();

            return new ApiResponse {
                Success = true,
                Response = profiles,
                StatusCode = 200,
                Type = "PHD2Profiles"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/connect-equipment - Connect equipment with profile
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/connect-equipment")]
    public async Task<ApiResponse> ConnectPHD2Equipment() {
        try {
            EnsurePHD2ServicesInitialized();
            string profileName = null;

            try {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null && requestData.ContainsKey("profileName") && requestData["profileName"] != null) {
                    profileName = requestData["profileName"].ToString();
                }
            } catch {
                // Handle parsing errors
            }

            if (string.IsNullOrEmpty(profileName)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "Profile name is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool result = await phd2Service.ConnectEquipmentAsync(profileName);

            return new ApiResponse {
                Success = result,
                Response = new { EquipmentConnected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Equipment"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/disconnect-equipment - Disconnect equipment
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/disconnect-equipment")]
    public async Task<ApiResponse> DisconnectPHD2Equipment() {
        try {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.DisconnectEquipmentAsync();

            return new ApiResponse {
                Success = result,
                Response = new { EquipmentDisconnected = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Equipment"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/start-guiding - Start guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/start-guiding")]
    public async Task<ApiResponse> StartPHD2Guiding() {
        try {
            EnsurePHD2ServicesInitialized();
            double settlePixels = 2.0;
            double settleTime = 10.0;
            double settleTimeout = 100.0;

            try {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null) {
                    if (requestData.ContainsKey("settlePixels") && requestData["settlePixels"] != null) {
                        if (double.TryParse(requestData["settlePixels"].ToString(), out double parsedSettlePixels)) {
                            settlePixels = parsedSettlePixels;
                        }
                    }
                    if (requestData.ContainsKey("settleTime") && requestData["settleTime"] != null) {
                        if (double.TryParse(requestData["settleTime"].ToString(), out double parsedSettleTime)) {
                            settleTime = parsedSettleTime;
                        }
                    }
                    if (requestData.ContainsKey("settleTimeout") && requestData["settleTimeout"] != null) {
                        if (double.TryParse(requestData["settleTimeout"].ToString(), out double parsedSettleTimeout)) {
                            settleTimeout = parsedSettleTimeout;
                        }
                    }
                }
            } catch {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.StartGuidingAsync(settlePixels, settleTime, settleTimeout);

            return new ApiResponse {
                Success = result,
                Response = new { GuidingStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Guiding"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/stop-guiding - Stop guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/stop-guiding")]
    public async Task<ApiResponse> StopPHD2Guiding() {
        try {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.StopGuidingAsync();

            return new ApiResponse {
                Success = result,
                Response = new { GuidingStopped = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Guiding"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/dither - Dither guiding star
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/dither")]
    public async Task<ApiResponse> DitherPHD2() {
        try {
            EnsurePHD2ServicesInitialized();
            double ditherPixels = 3.0;
            double settlePixels = 2.0;
            double settleTime = 10.0;
            double settleTimeout = 100.0;

            try {
                var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
                if (requestData != null) {
                    if (requestData.ContainsKey("ditherPixels") && requestData["ditherPixels"] != null) {
                        if (double.TryParse(requestData["ditherPixels"].ToString(), out double parsedDitherPixels)) {
                            ditherPixels = parsedDitherPixels;
                        }
                    }
                    if (requestData.ContainsKey("settlePixels") && requestData["settlePixels"] != null) {
                        if (double.TryParse(requestData["settlePixels"].ToString(), out double parsedSettlePixels)) {
                            settlePixels = parsedSettlePixels;
                        }
                    }
                    if (requestData.ContainsKey("settleTime") && requestData["settleTime"] != null) {
                        if (double.TryParse(requestData["settleTime"].ToString(), out double parsedSettleTime)) {
                            settleTime = parsedSettleTime;
                        }
                    }
                    if (requestData.ContainsKey("settleTimeout") && requestData["settleTimeout"] != null) {
                        if (double.TryParse(requestData["settleTimeout"].ToString(), out double parsedSettleTimeout)) {
                            settleTimeout = parsedSettleTimeout;
                        }
                    }
                }
            } catch {
                // Use defaults if parsing fails
            }

            bool result = await phd2Service.DitherAsync(ditherPixels, settlePixels, settleTime, settleTimeout);

            return new ApiResponse {
                Success = result,
                Response = new { DitherStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Dither"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/pause - Pause guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/pause")]
    public async Task<ApiResponse> PausePHD2() {
        try {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.PauseGuidingAsync();

            return new ApiResponse {
                Success = result,
                Response = new { Paused = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Pause"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/unpause - Resume guiding
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/unpause")]
    public async Task<ApiResponse> UnpausePHD2() {
        try {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.UnpauseGuidingAsync();

            return new ApiResponse {
                Success = result,
                Response = new { Unpaused = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Pause"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/start-looping - Start looping exposures
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/start-looping")]
    public async Task<ApiResponse> StartPHD2Looping() {
        try {
            EnsurePHD2ServicesInitialized();
            bool result = await phd2Service.StartLoopingAsync();

            return new ApiResponse {
                Success = result,
                Response = new { LoopingStarted = result, Error = phd2Service.LastError },
                StatusCode = result ? 200 : 400,
                Type = "PHD2Looping"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/settling - Get settling status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/settling")]
    public async Task<ApiResponse> GetPHD2Settling() {
        try {
            EnsurePHD2ServicesInitialized();
            var settling = await phd2Service.CheckSettlingAsync();

            return new ApiResponse {
                Success = true,
                Response = settling,
                StatusCode = 200,
                Type = "PHD2Settling"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/pixel-scale - Get pixel scale
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/pixel-scale")]
    public async Task<ApiResponse> GetPHD2PixelScale() {
        try {
            EnsurePHD2ServicesInitialized();
            var pixelScale = await phd2Service.GetPixelScaleAsync();

            return new ApiResponse {
                Success = true,
                Response = new { PixelScale = pixelScale },
                StatusCode = 200,
                Type = "PHD2PixelScale"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/all-info - Get comprehensive PHD2 information
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/all-info")]
    public async Task<ApiResponse> GetAllPHD2Info() {
        try {
            EnsurePHD2ServicesInitialized();
            // Get all available PHD2 information in parallel
            var statusTask = phd2Service.GetStatusAsync();
            var profilesTask = phd2Service.GetEquipmentProfilesAsync();
            var settlingTask = phd2Service.CheckSettlingAsync();
            var pixelScaleTask = phd2Service.GetPixelScaleAsync();

            await Task.WhenAll(statusTask, profilesTask, settlingTask, pixelScaleTask);

            var status = await statusTask;
            var profiles = await profilesTask;
            var settling = await settlingTask;
            var pixelScale = await pixelScaleTask;

            // Try to get star image info if available
            object starImageInfo = null;
            try
            {
                if (phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping"))
                {
                    var starImage = await phd2Service.GetStarImageAsync(15); // Get minimal size star image for info
                    if (starImage != null)
                    {
                        starImageInfo = new {
                            Available = true,
                            Frame = starImage.Frame,
                            Width = starImage.Width,
                            Height = starImage.Height,
                            StarPosition = new {
                                X = starImage.StarPosX,
                                Y = starImage.StarPosY
                            },
                            StarInfo = status?.CurrentStar != null ? new {
                                SNR = status.CurrentStar.SNR,
                                HFD = status.CurrentStar.HFD,
                                StarMass = status.CurrentStar.StarMass,
                                LastUpdate = status.CurrentStar.LastUpdate,
                                TimeSinceUpdate = DateTime.Now - status.CurrentStar.LastUpdate
                            } : null
                        };
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"Could not get star image info: {ex.Message}");
                starImageInfo = new {
                    Available = false,
                    Error = ex.Message
                };
            }

            if (starImageInfo == null)
            {
                starImageInfo = new {
                    Available = false,
                    Reason = "Not guiding or looping"
                };
            }

            var allInfo = new {
                Connection = new {
                    IsConnected = phd2Service.IsConnected,
                    LastError = phd2Service.LastError
                },
                Status = status,
                EquipmentProfiles = profiles,
                Settling = settling,
                PixelScale = pixelScale,
                StarImage = starImageInfo,
                Capabilities = new {
                    CanGuide = phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping" || status?.AppState == "Stopped"),
                    CanDither = phd2Service.IsConnected && status?.AppState == "Guiding",
                    CanPause = phd2Service.IsConnected && status?.AppState == "Guiding",
                    CanLoop = phd2Service.IsConnected && status?.AppState == "Stopped",
                    CanGetStarImage = phd2Service.IsConnected && (status?.AppState == "Guiding" || status?.AppState == "Looping")
                },
                GuideStats = status?.Stats != null ? new {
                    RmsTotal = status.Stats.RmsTotal,
                    RmsRA = status.Stats.RmsRA,
                    RmsDec = status.Stats.RmsDec,
                    PeakRA = status.Stats.PeakRA,
                    PeakDec = status.Stats.PeakDec,
                    AvgDistance = status.AvgDist
                } : null,
                StarLostInfo = status?.LastStarLost != null ? new {
                    Frame = status.LastStarLost.Frame,
                    Time = status.LastStarLost.Time,
                    StarMass = status.LastStarLost.StarMass,
                    SNR = status.LastStarLost.SNR,
                    AvgDist = status.LastStarLost.AvgDist,
                    ErrorCode = status.LastStarLost.ErrorCode,
                    Status = status.LastStarLost.Status,
                    Timestamp = status.LastStarLost.Timestamp,
                    TimeSinceLost = DateTime.Now - status.LastStarLost.Timestamp
                } : null,
                ServerInfo = new {
                    PHD2Version = status?.Version,
                    PHD2Subversion = status?.PHDSubver,
                    AppState = status?.AppState,
                    IsGuiding = status?.IsGuiding ?? false,
                    IsSettling = status?.IsSettling ?? false
                }
            };

            return new ApiResponse {
                Success = true,
                Response = allInfo,
                StatusCode = 200,
                Type = "PHD2AllInfo"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    // PHD2 Parameter Control Endpoints

    /// <summary>
    /// POST /api/phd2/set-exposure - Set exposure time
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-exposure")]
    public async Task<ApiResponse> SetPHD2Exposure() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("exposureMs") || requestData["exposureMs"] == null) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "exposureMs parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!int.TryParse(requestData["exposureMs"].ToString(), out int exposureMs)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "exposureMs must be a valid integer",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetExposureAsync(exposureMs);

            return new ApiResponse {
                Success = true,
                Response = new { ExposureSet = exposureMs },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-exposure - Get exposure time
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-exposure")]
    public async Task<ApiResponse> GetPHD2Exposure() {
        try {
            EnsurePHD2ServicesInitialized();
            var exposure = await phd2Service.GetExposureAsync();

            return new ApiResponse {
                Success = true,
                Response = new { Exposure = exposure },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-dec-guide-mode - Set declination guide mode
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-dec-guide-mode")]
    public async Task<ApiResponse> SetPHD2DecGuideMode() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("mode") || requestData["mode"] == null) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "mode parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string mode = requestData["mode"].ToString();
            await phd2Service.SetDecGuideModeAsync(mode);

            return new ApiResponse {
                Success = true,
                Response = new { DecGuideModeSet = mode },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-dec-guide-mode - Get declination guide mode
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-dec-guide-mode")]
    public async Task<ApiResponse> GetPHD2DecGuideMode() {
        try {
            EnsurePHD2ServicesInitialized();
            var mode = await phd2Service.GetDecGuideModeAsync();

            return new ApiResponse {
                Success = true,
                Response = new { DecGuideMode = mode },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-guide-output-enabled - Enable/disable guide output
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-guide-output-enabled")]
    public async Task<ApiResponse> SetPHD2GuideOutputEnabled() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || requestData["enabled"] == null) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetGuideOutputEnabledAsync(enabled);

            return new ApiResponse {
                Success = true,
                Response = new { GuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-guide-output-enabled - Get guide output status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-guide-output-enabled")]
    public async Task<ApiResponse> GetPHD2GuideOutputEnabled() {
        try {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetGuideOutputEnabledAsync();

            return new ApiResponse {
                Success = true,
                Response = new { GuideOutputEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-lock-position - Set lock position
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-position")]
    public async Task<ApiResponse> SetPHD2LockPosition() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("x") || !requestData.ContainsKey("y")) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "x and y parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["x"].ToString(), out double x) ||
                !double.TryParse(requestData["y"].ToString(), out double y)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "x and y must be valid numbers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            bool exact = true;
            if (requestData.ContainsKey("exact") && requestData["exact"] != null) {
                bool.TryParse(requestData["exact"].ToString(), out exact);
            }

            await phd2Service.SetLockPositionAsync(x, y, exact);

            return new ApiResponse {
                Success = true,
                Response = new { LockPositionSet = new { X = x, Y = y, Exact = exact } },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-lock-position - Get lock position
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-position")]
    public async Task<ApiResponse> GetPHD2LockPosition() {
        try {
            EnsurePHD2ServicesInitialized();
            var position = await phd2Service.GetLockPositionAsync();

            if (position == null || position.Length < 2) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "Keine Daten vorhanden - PHD2 ist derzeit nicht am guidenden oder es wurde keine Lock-Position festgelegt",
                    StatusCode = 400,
                    Type = "NoDataAvailable"
                };
            }

            return new ApiResponse {
                Success = true,
                Response = new { LockPosition = new { X = position[0], Y = position[1] } },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (InvalidOperationException ex) when (ex.Message.Contains("PHD2 not connected")) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = "PHD2 ist nicht erreichbar",
                StatusCode = 500,
                Type = "PHD2NotConnected"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = "Keine Daten vorhanden - " + ex.Message,
                StatusCode = 400,
                Type = "NoDataAvailable"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/find-star - Auto-select a star
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/find-star")]
    public async Task<ApiResponse> FindPHD2Star() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            int[] roi = null;

            // Parse optional ROI parameter
            if (requestData != null && requestData.ContainsKey("roi") && requestData["roi"] != null) {
                try {
                    var roiData = requestData["roi"];
                    if (roiData is Newtonsoft.Json.Linq.JArray roiArray && roiArray.Count == 4) {
                        roi = new int[4];
                        for (int i = 0; i < 4; i++) {
                            roi[i] = (int)roiArray[i];
                        }
                    } else if (roiData is System.Collections.Generic.List<object> roiList && roiList.Count == 4) {
                        roi = new int[4];
                        for (int i = 0; i < 4; i++) {
                            roi[i] = Convert.ToInt32(roiList[i]);
                        }
                    } else {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse {
                            Success = false,
                            Error = "roi must be an array of 4 integers: [x, y, width, height]",
                            StatusCode = 400,
                            Type = "Error"
                        };
                    }
                } catch {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse {
                        Success = false,
                        Error = "roi must be an array of 4 integers: [x, y, width, height]",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }
            }

            var position = await phd2Service.FindStarAsync(roi);

            return new ApiResponse {
                Success = true,
                Response = new {
                    StarPosition = new { X = position[0], Y = position[1] },
                    ROI = roi != null ? new { X = roi[0], Y = roi[1], Width = roi[2], Height = roi[3] } : null
                },
                StatusCode = 200,
                Type = "PHD2StarSelection"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-lock-shift-enabled - Enable/disable lock shift
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-shift-enabled")]
    public async Task<ApiResponse> SetPHD2LockShiftEnabled() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || requestData["enabled"] == null) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled parameter is required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled must be a valid boolean",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetLockShiftEnabledAsync(enabled);

            return new ApiResponse {
                Success = true,
                Response = new { LockShiftEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-lock-shift-enabled - Get lock shift status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-shift-enabled")]
    public async Task<ApiResponse> GetPHD2LockShiftEnabled() {
        try {
            EnsurePHD2ServicesInitialized();
            var enabled = await phd2Service.GetLockShiftEnabledAsync();

            return new ApiResponse {
                Success = true,
                Response = new { LockShiftEnabled = enabled },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-lock-shift-params - Set lock shift parameters
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-lock-shift-params")]
    public async Task<ApiResponse> SetPHD2LockShiftParams() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("xRate") || !requestData.ContainsKey("yRate")) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "xRate and yRate parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!double.TryParse(requestData["xRate"].ToString(), out double xRate) ||
                !double.TryParse(requestData["yRate"].ToString(), out double yRate)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "xRate and yRate must be valid numbers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string units = "arcsec/hr";
            string axes = "RA/Dec";
            if (requestData.ContainsKey("units") && requestData["units"] != null) {
                units = requestData["units"].ToString();
            }
            if (requestData.ContainsKey("axes") && requestData["axes"] != null) {
                axes = requestData["axes"].ToString();
            }

            await phd2Service.SetLockShiftParamsAsync(xRate, yRate, units, axes);

            return new ApiResponse {
                Success = true,
                Response = new { LockShiftParamsSet = new { XRate = xRate, YRate = yRate, Units = units, Axes = axes } },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-lock-shift-params - Get lock shift parameters
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-lock-shift-params")]
    public async Task<ApiResponse> GetPHD2LockShiftParams() {
        try {
            EnsurePHD2ServicesInitialized();
            var parameters = await phd2Service.GetLockShiftParamsAsync();

            return new ApiResponse {
                Success = true,
                Response = new { LockShiftParams = parameters },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-algo-param - Set algorithm parameter
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-algo-param")]
    public async Task<ApiResponse> SetPHD2AlgoParam() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("axis") || !requestData.ContainsKey("name") || !requestData.ContainsKey("value")) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "axis, name, and value parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            string axis = requestData["axis"].ToString();
            string name = requestData["name"].ToString();

            if (!double.TryParse(requestData["value"].ToString(), out double value)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "value must be a valid number",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetAlgoParamAsync(axis, name, value);

            return new ApiResponse {
                Success = true,
                Response = new { Message = $"Algorithm parameter set: {axis}.{name} = {value}" },
                StatusCode = 200,
                Type = "PHD2AlgoParamSet"
            };
        } catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis")) {
            // Expected behavior for invalid axis parameter
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2InvalidAxis"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-algo-param-names - Get algorithm parameter names
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-algo-param-names")]
    public async Task<ApiResponse> GetPHD2AlgoParamNames([QueryField(true)] string axis) {
        try {
            EnsurePHD2ServicesInitialized();
            var paramNames = await phd2Service.GetAlgoParamNamesAsync(axis);

            return new ApiResponse {
                Success = true,
                Response = new { Axis = axis, ParameterNames = paramNames },
                StatusCode = 200,
                Type = "PHD2AlgoParamNames"
            };
        } catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis")) {
            // Expected behavior for invalid axis parameter
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2InvalidAxis"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-algo-param - Get algorithm parameter value
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-algo-param")]
    public async Task<ApiResponse> GetPHD2AlgoParam([QueryField(true)] string axis, [QueryField(true)] string name) {
        try {
            EnsurePHD2ServicesInitialized();
            var value = await phd2Service.GetAlgoParamAsync(axis, name);

            return new ApiResponse {
                Success = true,
                Response = new { Axis = axis, Name = name, Value = value },
                StatusCode = 200,
                Type = "PHD2AlgoParam"
            };
        } catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis") || ex.Message.Contains("could not get param")) {
            // Expected behavior for invalid axis or parameter names
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = ex.Message.Contains("Invalid axis") ? "PHD2InvalidAxis" : "PHD2ParamNotFound"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/set-variable-delay-settings - Set variable delay settings
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/set-variable-delay-settings")]
    public async Task<ApiResponse> SetPHD2VariableDelaySettings() {
        try {
            EnsurePHD2ServicesInitialized();
            var requestData = await HttpContext.GetRequestDataAsync<Dictionary<string, object>>();
            if (requestData == null || !requestData.ContainsKey("enabled") || !requestData.ContainsKey("shortDelaySeconds") || !requestData.ContainsKey("longDelaySeconds")) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled, shortDelaySeconds, and longDelaySeconds parameters are required",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            if (!bool.TryParse(requestData["enabled"].ToString(), out bool enabled) ||
                !int.TryParse(requestData["shortDelaySeconds"].ToString(), out int shortDelaySeconds) ||
                !int.TryParse(requestData["longDelaySeconds"].ToString(), out int longDelaySeconds)) {
                HttpContext.Response.StatusCode = 400;
                return new ApiResponse {
                    Success = false,
                    Error = "enabled must be boolean, shortDelaySeconds and longDelaySeconds must be integers",
                    StatusCode = 400,
                    Type = "Error"
                };
            }

            await phd2Service.SetVariableDelaySettingsAsync(enabled, shortDelaySeconds, longDelaySeconds);

            return new ApiResponse {
                Success = true,
                Response = new { Message = $"Variable delay settings updated: enabled={enabled}, short={shortDelaySeconds}s, long={longDelaySeconds}s" },
                StatusCode = 200,
                Type = "PHD2VariableDelaySet"
            };
        } catch (PHD2Exception ex) when (ex.Message.Contains("method not found")) {
            // Expected behavior for unsupported PHD2 versions
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2MethodNotFound"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-variable-delay-settings - Get variable delay settings
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-variable-delay-settings")]
    public async Task<ApiResponse> GetPHD2VariableDelaySettings() {
        try {
            EnsurePHD2ServicesInitialized();
            var settings = await phd2Service.GetVariableDelaySettingsAsync();

            return new ApiResponse {
                Success = true,
                Response = settings,
                StatusCode = 200,
                Type = "PHD2VariableDelay"
            };
        } catch (PHD2Exception ex) when (ex.Message.Contains("method not found")) {
            // Expected behavior for unsupported PHD2 versions
            HttpContext.Response.StatusCode = 400;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 400,
                Type = "PHD2MethodNotFound"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-connected - Get connection status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-connected")]
    public async Task<ApiResponse> GetPHD2Connected() {
        try {
            EnsurePHD2ServicesInitialized();
            var connected = await phd2Service.GetConnectedAsync();

            return new ApiResponse {
                Success = true,
                Response = new { Connected = connected },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-paused - Get paused status
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-paused")]
    public async Task<ApiResponse> GetPHD2Paused() {
        try {
            EnsurePHD2ServicesInitialized();
            var paused = await phd2Service.GetPausedAsync();

            return new ApiResponse {
                Success = true,
                Response = new { Paused = paused },
                StatusCode = 200,
                Type = "PHD2Parameter"
            };
        } catch (Exception ex) {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-current-equipment - Get current equipment
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-current-equipment")]
    public async Task<ApiResponse> GetPHD2CurrentEquipment()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Check if PHD2 is connected first
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var equipment = await phd2Service.GetCurrentEquipmentAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { CurrentEquipment = equipment },
                StatusCode = 200,
                Type = "PHD2CurrentEquipment"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting current equipment: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/get-profile - Get profile
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/get-profile")]
    public async Task<ApiResponse> GetPHD2Profile()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // Check if PHD2 is connected first
            if (!phd2Service.IsConnected)
            {
                return new ApiResponse
                {
                    Success = false,
                    Error = "PHD2 is not connected",
                    StatusCode = 400,
                    Type = "PHD2NotConnected"
                };
            }

            var profile = await phd2Service.GetProfileAsync();

            return new ApiResponse
            {
                Success = true,
                Response = new { Profile = profile },
                StatusCode = 200,
                Type = "PHD2Profile"
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting profile: {ex}");
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    // PHD2 Image API Endpoints

    /// <summary>
    /// GET /api/phd2/current-image - Get current PHD2 image
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/current-image")]
    public async Task GetPHD2CurrentImage()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            var imageBytes = await phd2ImageService.GetCurrentImageBytesAsync();

            if (imageBytes == null)
            {
                HttpContext.Response.StatusCode = 404;
                HttpContext.Response.ContentType = "application/json";
                var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
                {
                    Success = false,
                    Error = phd2ImageService.LastError ?? "No current PHD2 image available",
                    StatusCode = 404,
                    Type = "PHD2ImageNotFound"
                });
                Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
                return;
            }

            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("Pragma", "no-cache");
            HttpContext.Response.Headers.Add("Expires", "0");
            HttpContext.Response.Headers.Add("Last-Modified", phd2ImageService.LastImageTime.ToString("R"));

            Response.OutputStream.Write(imageBytes, 0, imageBytes.Length);
        }
        catch (Exception ex)
        {
            Logger.Error($"Error serving PHD2 image: {ex}");
            HttpContext.Response.StatusCode = 500;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
    }

    /// <summary>
    /// GET /api/phd2/image-info - Get PHD2 image info
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/image-info")]
    public async Task<ApiResponse> GetPHD2ImageInfo()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            return new ApiResponse
            {
                Success = true,
                Response = new
                {
                    HasCurrentImage = phd2ImageService.HasCurrentImage,
                    LastImageTime = phd2ImageService.LastImageTime,
                    LastError = phd2ImageService.LastError,
                    TimeSinceLastImage = phd2ImageService.HasCurrentImage ?
                        DateTime.Now - phd2ImageService.LastImageTime : (TimeSpan?)null
                },
                StatusCode = 200,
                Type = "PHD2ImageInfo"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// POST /api/phd2/refresh-image - Refresh PHD2 image
    /// </summary>
    [Route(HttpVerbs.Post, "/phd2/refresh-image")]
    public async Task<ApiResponse> RefreshPHD2Image()
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            bool success = await phd2ImageService.RefreshImageAsync();

            return new ApiResponse
            {
                Success = success,
                Response = new
                {
                    ImageRefreshed = success,
                    LastImageTime = phd2ImageService.LastImageTime,
                    Error = phd2ImageService.LastError
                },
                StatusCode = success ? 200 : 400,
                Type = "PHD2ImageRefresh"
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            HttpContext.Response.StatusCode = 500;
            return new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            };
        }
    }

    /// <summary>
    /// GET /api/phd2/star-image - Get PHD2 star image
    /// </summary>
    [Route(HttpVerbs.Get, "/phd2/star-image")]
    public async Task GetPHD2StarImage([QueryField] int size)
    {
        try
        {
            EnsurePHD2ServicesInitialized();
            // PHD2 requires size >= 15, default to 15 if not specified or too small
            int requestedSize = size > 0 ? Math.Max(15, size) : 15;

            // Get star image data from PHD2
            var starImageData = await phd2Service.GetStarImageAsync(requestedSize);

            if (starImageData == null)
            {
                HttpContext.Response.StatusCode = 404;
                HttpContext.Response.ContentType = "application/json";
                var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
                {
                    Success = false,
                    Error = phd2Service.LastError ?? "No PHD2 star image available",
                    StatusCode = 404,
                    Type = "PHD2StarImageNotFound"
                });
                Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
                return;
            }

            // Convert base64 pixel data to JPG - no additional scaling needed as PHD2 already provides correct size
            byte[] jpgBytes = ImageConverter.ConvertBase64StarImageToJpg(
                starImageData.Pixels,
                starImageData.Width,
                starImageData.Height,
                null // Don't scale further as PHD2 already provided the requested size
            );

            // Set response headers
            HttpContext.Response.ContentType = "image/jpeg";
            HttpContext.Response.StatusCode = 200;
            HttpContext.Response.Headers.Add("Cache-Control", "no-cache, no-store, must-revalidate");
            HttpContext.Response.Headers.Add("Pragma", "no-cache");
            HttpContext.Response.Headers.Add("Expires", "0");
            HttpContext.Response.Headers.Add("X-Star-Position", $"{starImageData.StarPosX},{starImageData.StarPosY}");
            HttpContext.Response.Headers.Add("X-Image-Size", $"{starImageData.Width}x{starImageData.Height}");
            HttpContext.Response.Headers.Add("X-Frame", starImageData.Frame.ToString());
            HttpContext.Response.Headers.Add("X-Requested-Size", requestedSize.ToString());

            // Write JPG data to response
            Response.OutputStream.Write(jpgBytes, 0, jpgBytes.Length);
        }
        catch (PHD2Exception ex) when (ex.Message == "no star selected")
        {
            Logger.Debug($"PHD2 star image not available: {ex.Message}");
            HttpContext.Response.StatusCode = 404;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 404,
                Type = "PHD2StarNotSelected"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
        catch (Exception ex)
        {
            Logger.Error($"Error serving PHD2 star image: {ex}");
            HttpContext.Response.StatusCode = 500;
            HttpContext.Response.ContentType = "application/json";
            var errorResponse = System.Text.Json.JsonSerializer.Serialize(new ApiResponse
            {
                Success = false,
                Error = ex.Message,
                StatusCode = 500,
                Type = "Error"
            });
            Response.OutputStream.Write(System.Text.Encoding.UTF8.GetBytes(errorResponse));
        }
    }
}
