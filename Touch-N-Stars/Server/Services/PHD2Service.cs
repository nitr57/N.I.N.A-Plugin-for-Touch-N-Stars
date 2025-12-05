using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.PHD2;

namespace TouchNStars.Server.Services
{
    public class PHD2Service : IDisposable
    {
        private PHD2Client client;
        private readonly object lockObject = new object();
        private string lastError;
        private Task<bool> currentConnectionTask = null;

        private async Task WaitForConnectionIfNeeded()
        {
            Task<bool> connectionTask = null;
            lock (lockObject)
            {
                if (currentConnectionTask != null && !currentConnectionTask.IsCompleted)
                {
                    connectionTask = currentConnectionTask;
                }
            }

            if (connectionTask != null)
            {
                Logger.Debug("Waiting for ongoing PHD2 connection");
                await connectionTask;
            }
        }

        public async Task WaitForConnectionAsync()
        {
            await WaitForConnectionIfNeeded();
        }

        public bool IsConnected => client?.IsConnected ?? false;
        public string LastError => lastError;

        public PHD2Service()
        {
            client = new PHD2Client();
        }

        public async Task<bool> ConnectAsync(string hostname = "localhost", uint instance = 1)
        {
            lock (lockObject)
            {
                if (client?.IsConnected == true)
                {
                    Logger.Debug("PHD2 already connected, skipping connection attempt");
                    return true;
                }

                if (currentConnectionTask != null && !currentConnectionTask.IsCompleted)
                {
                    Logger.Debug("PHD2 connection already in progress, waiting for existing attempt");
                }
                else
                {
                    currentConnectionTask = ConnectInternalAsync(hostname, instance);
                }
            }

            return await currentConnectionTask;
        }

        private async Task<bool> ConnectInternalAsync(string hostname, uint instance)
        {
            return await Task.Run(() =>
            {
                const int maxRetries = 10;
                int attemptCount = 0;

                while (attemptCount < maxRetries)
                {
                    attemptCount++;

                    try
                    {
                        lock (lockObject)
                        {
                            Logger.Info($"PHD2 connection attempt {attemptCount}/{maxRetries} to {hostname}, instance {instance}");
                            
                            // Defensive disconnect of existing connection
                            if (client != null && client.IsConnected)
                            {
                                try
                                {
                                    Logger.Debug("Gracefully disconnecting existing PHD2 client");
                                    client.Disconnect();
                                }
                                catch (Exception disconnectEx)
                                {
                                    Logger.Warning($"Error during graceful disconnect: {disconnectEx.Message}");
                                }
                            }

                            ushort port = (ushort)(4400 + instance - 1);

                            Logger.Debug($"Creating PHD2 client for {hostname}:{port}");
                            client = new PHD2Client(hostname, instance);
                            
                            Logger.Debug("Attempting PHD2 client connection");
                            client.Connect();
                            
                            if (client.IsConnected)
                            {
                                Logger.Info($"PHD2 connection successful on attempt {attemptCount}");
                                lastError = null;
                                return true;
                            }
                            else
                            {
                                throw new InvalidOperationException("Client reports not connected after Connect() call");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string errorMsg = $"PHD2 connection attempt {attemptCount} failed: {ex.Message}";
                        Logger.Warning(errorMsg);
                        
                        if (attemptCount < maxRetries)
                        {
                            Logger.Info($"Retrying PHD2 connection in {attemptCount * 1000}ms...");
                            System.Threading.Thread.Sleep(attemptCount * 1000); // Progressive backoff
                        }
                        else
                        {
                            lastError = $"Failed to connect to PHD2 at {hostname}:{4400 + instance - 1} after {maxRetries} attempts: {ex.Message}";
                            Logger.Error($"All PHD2 connection attempts failed: {ex}");
                        }
                    }
                }

                lock (lockObject)
                {
                    currentConnectionTask = null;
                }
                return false;
            });
        }

        public async Task DisconnectAsync()
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        client?.Disconnect();
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Error disconnecting from PHD2: {ex}");
                }
            });
        }

        public async Task<bool> InjectProfilesAsync(string fileName, CancellationToken ct = default) {
            // If we are connected, we cannot inject profiles
            if (IsConnected) {
                return false;
            }

            // If PHD2 instance is running, we cannot inject profiles
            try
            {
                // Check if PHD2 is already started with the expected instance number
                var names = new[] { "phd2", "phd2.bin" };
                var p = Process.GetProcesses().Where(p => names.Contains(p.ProcessName, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (p.Length > 0)
                {
                    NINA.Core.Utility.Logger.Error($"phd2 already running");
                    return false;
                }

                if (!File.Exists(fileName))
                {
                    NINA.Core.Utility.Logger.Error($"phd2 not found at {fileName}");
                    throw new FileNotFoundException();
                }

                var process = new Process
                {
                    StartInfo = {
                        FileName = fileName,
                        Arguments = $"-l={Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "phd2", "profiles.phd")}"
                    }
                };

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                process?.Start();
                await process.WaitForExitAsync(linkedCts.Token);

                return process.ExitCode == 0;
            }
            catch(Exception ex)
            {
                NINA.Core.Utility.Logger.Error($"phd2 error {ex.Message}");
                return false;
            }
        }

        public async Task<bool> StartGuidingAsync(double settlePixels = 2.0, double settleTime = 10.0, double settleTimeout = 100.0)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Guide(settlePixels, settleTime, settleTimeout);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to start guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> StopGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.StopCapture();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to stop guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> DitherAsync(double ditherPixels = 3.0, double settlePixels = 2.0, double settleTime = 10.0, double settleTimeout = 100.0)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Dither(ditherPixels, settlePixels, settleTime, settleTimeout);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to dither: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> PauseGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Pause();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to pause guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> UnpauseGuidingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Unpause();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to unpause guiding: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> StartLoopingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.Loop();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to start looping: {ex}");
                    return false;
                }
            });
        }

        public async Task<System.Collections.Generic.List<string>> GetEquipmentProfilesAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return new System.Collections.Generic.List<string>();
                        }

                        var profiles = client.GetEquipmentProfiles();
                        lastError = null;
                        return profiles;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get equipment profiles: {ex}");
                    return new System.Collections.Generic.List<string>();
                }
            });
        }

        public async Task<bool> ConnectEquipmentAsync(string profileName)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.ConnectEquipment(profileName);
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to connect equipment: {ex}");
                    return false;
                }
            });
        }

        public async Task<bool> DisconnectEquipmentAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return false;
                        }

                        client.DisconnectEquipment();
                        lastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to disconnect equipment: {ex}");
                    return false;
                }
            });
        }

        public async Task<PHD2Status> GetStatusAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            return new PHD2Status
                            {
                                IsConnected = false,
                                AppState = "Disconnected"
                            };
                        }

                        var status = client.GetStatus();
                        lastError = null;
                        return status;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get PHD2 status: {ex}");
                    return new PHD2Status
                    {
                        IsConnected = false,
                        AppState = "Error"
                    };
                }
            });
        }

        public async Task<SettleProgress> CheckSettlingAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            lastError = "PHD2 is not connected";
                            return null;
                        }

                        if (!client.IsSettling())
                        {
                            return new SettleProgress { Done = true };
                        }

                        var progress = client.CheckSettling();
                        lastError = null;
                        return progress;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to check settling: {ex}");
                    return null;
                }
            });
        }

        public async Task<double> GetPixelScaleAsync()
        {
            await WaitForConnectionIfNeeded();

            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetPixelScale();
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Pixel scale not available"))
                {
                    // This is expected when no camera is connected or calibration hasn't been done
                    // Log at Debug level to avoid spam in normal operation
                    Logger.Debug($"Pixel scale not available: {ex.Message}");
                    return 0.0;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get pixel scale: {ex}");
                    return 0.0;
                }
            });
        }

        // PHD2 "set_" methods
        public async Task SetExposureAsync(int exposureMs)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetExposure(exposureMs);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set exposure: {ex}");
                    throw;
                }
            });
        }

        public async Task SetDecGuideModeAsync(string mode)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetDecGuideMode(mode);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set Dec guide mode: {ex}");
                    throw;
                }
            });
        }

        public async Task SetGuideOutputEnabledAsync(bool enabled)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetGuideOutputEnabled(enabled);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set guide output enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockPositionAsync(double x, double y, bool exact = true)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockPosition(x, y, exact);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock position: {ex}");
                    throw;
                }
            });
        }

        /// <summary>
        /// Auto-select a star using PHD2's find_star method
        /// </summary>
        /// <param name="roi">Optional region of interest [x, y, width, height]. If null, uses full frame.</param>
        /// <returns>The lock position coordinates [x, y] of the selected star</returns>
        public async Task<double[]> FindStarAsync(int[] roi = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var result = client.FindStar(roi);
                        lastError = null;
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to find star: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockShiftEnabledAsync(bool enabled)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockShiftEnabled(enabled);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock shift enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task SetLockShiftParamsAsync(double xRate, double yRate, string units = "arcsec/hr", string axes = "RA/Dec")
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetLockShiftParams(xRate, yRate, units, axes);
                        lastError = null;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set lock shift params: {ex}");
                    throw;
                }
            });
        }

        public async Task SetAlgoParamAsync(string axis, string name, double value)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        // Log the exact value being sent
                        Logger.Info($"Setting PHD2 algorithm parameter {axis}.{name} = {value:F10} (raw: {value})");
                        
                        // Round to a reasonable precision to avoid floating-point precision issues
                        // PHD2 typically works with values to 2-3 decimal places
                        double roundedValue = Math.Round(value, 3);
                        
                        // Add a tiny offset to all values to avoid PHD2's internal floating-point precision issues
                        // This ensures we don't hit any problematic decimal representations
                        // Use 0.001 offset which will survive the 3-decimal rounding
                        double adjustedValue = roundedValue + 0.001;
                        
                        Logger.Info($"Adjusted value from {roundedValue:F4} to {adjustedValue:F4} to avoid PHD2 floating-point issues");
                        
                        roundedValue = adjustedValue;

                        client.SetAlgoParam(axis, name, roundedValue);
                        
                        // Read back the value to verify what PHD2 actually received
                        try
                        {
                            var actualValue = client.GetAlgoParam(axis, name);
                            Logger.Info($"PHD2 confirmed parameter {axis}.{name} = {actualValue:F10} (expected: {roundedValue:F10})");
                            
                            if (Math.Abs(actualValue - roundedValue) > 0.001)
                            {
                                Logger.Warning($"Value mismatch! Sent: {roundedValue:F10}, Got back: {actualValue:F10}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Could not read back parameter value: {ex.Message}");
                        }
                        
                        lastError = null;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
                {
                    // This is expected behavior for invalid axis names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 invalid axis: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set algorithm parameter: {ex}");
                    throw;
                }
            });
        }

        public async Task SetVariableDelaySettingsAsync(bool enabled, int shortDelaySeconds, int longDelaySeconds)
        {
            await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        client.SetVariableDelaySettings(enabled, shortDelaySeconds, longDelaySeconds);
                        lastError = null;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
                {
                    // This is expected behavior for unsupported PHD2 versions
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 method not supported: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to set variable delay settings: {ex}");
                    throw;
                }
            });
        }

        // PHD2 "get_" methods
        public async Task<int> GetExposureAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetExposure();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get exposure: {ex}");
                    throw;
                }
            });
        }

        public async Task<string> GetDecGuideModeAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetDecGuideMode();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get Dec guide mode: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetGuideOutputEnabledAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetGuideOutputEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get guide output enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task<double[]> GetLockPositionAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockPosition();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock position: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetLockShiftEnabledAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockShiftEnabled();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock shift enabled: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetLockShiftParamsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetLockShiftParams();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get lock shift params: {ex}");
                    throw;
                }
            });
        }

        public async Task<string[]> GetAlgoParamNamesAsync(string axis)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAlgoParamNames(axis);
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis"))
                {
                    // This is expected behavior for invalid axis names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 invalid axis: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get algorithm parameter names: {ex}");
                    throw;
                }
            });
        }

        public async Task<double> GetAlgoParamAsync(string axis, string name)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetAlgoParam(axis, name);
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("Invalid axis") || ex.Message.Contains("could not get param"))
                {
                    // This is expected behavior for invalid axis or parameter names
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 parameter error: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get algorithm parameter: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetVariableDelaySettingsAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetVariableDelaySettings();
                    }
                }
                catch (PHD2Exception ex) when (ex.Message.Contains("method not found"))
                {
                    // This is expected behavior for older PHD2 versions
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 method not supported: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get variable delay settings: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetConnectedAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetConnected();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get connected status: {ex}");
                    throw;
                }
            });
        }

        public async Task<bool> GetPausedAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetPaused();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get paused status: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetCurrentEquipmentAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetCurrentEquipment();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get current equipment: {ex}");
                    throw;
                }
            });
        }

        public async Task<object> GetProfileAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        return client.GetProfile();
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get profile: {ex}");
                    throw;
                }
            });
        }

        /// <summary>
        /// Save the current image from PHD2 to a FITS file
        /// </summary>
        /// <returns>The full path to the saved FITS image file</returns>
        public async Task<string> SaveImageAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var filename = client.SaveImage();
                        lastError = null;
                        Logger.Debug($"PHD2 image saved to: {filename}");
                        return filename;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    
                    // "no image available" is expected when PHD2 hasn't captured an image yet
                    if (ex.Message.Contains("no image available"))
                    {
                        Logger.Debug($"PHD2 image not available yet: {ex.Message}");
                    }
                    else
                    {
                        Logger.Error($"Failed to save PHD2 image: {ex}");
                    }
                    throw;
                }
            });
        }

        /// <summary>
        /// Get the star image from PHD2 as base64 encoded data
        /// </summary>
        /// <param name="size">Optional size parameter for the image</param>
        /// <returns>Star image data including dimensions, star position, and base64 encoded pixels</returns>
        public async Task<StarImageData> GetStarImageAsync(int? size = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    lock (lockObject)
                    {
                        if (client == null || !client.IsConnected)
                        {
                            throw new InvalidOperationException("PHD2 not connected");
                        }

                        var starImage = client.GetStarImage(size);
                        lastError = null;
                        Logger.Debug($"PHD2 star image retrieved: {starImage.Width}x{starImage.Height}, star at ({starImage.StarPosX}, {starImage.StarPosY})");
                        return starImage;
                    }
                }
                catch (PHD2Exception ex) when (ex.Message == "no star selected")
                {
                    lastError = ex.Message;
                    Logger.Debug($"PHD2 star image not available: {ex.Message}");
                    throw;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    Logger.Error($"Failed to get PHD2 star image: {ex}");
                    throw;
                }
            });
        }

        private bool disposed = false;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    try
                    {
                        lock (lockObject)
                        {
                            Logger.Debug("Disposing PHD2Service");
                            
                            // Gracefully disconnect first
                            if (client != null && client.IsConnected)
                            {
                                try
                                {
                                    Logger.Debug("Disconnecting PHD2 client during dispose");
                                    client.Disconnect();
                                }
                                catch (Exception disconnectEx)
                                {
                                    Logger.Warning($"Error during PHD2 disconnect in dispose: {disconnectEx.Message}");
                                }
                            }

                            // Dispose the client
                            client?.Dispose();
                            client = null;
                            
                            Logger.Debug("PHD2Service disposed successfully");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error disposing PHD2Service: {ex}");
                    }
                }
                disposed = true;
            }
        }

        ~PHD2Service()
        {
            Dispose(false);
        }
    }
}
