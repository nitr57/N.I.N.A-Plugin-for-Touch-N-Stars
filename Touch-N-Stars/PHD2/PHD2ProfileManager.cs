using NINA.Core.Utility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace TouchNStars.PHD2
{
    /// <summary>
    /// Manages reading and writing PHD2 profile configuration files (.phd format)
    /// 
    /// PHD2 profile files use a key=value format with the structure:
    /// PHD Config 1
    /// /path/to/setting	scope	type	value
    /// 
    /// Example:
    /// /profile/2/camera/binning	1	1
    /// /profile/2/scope/CalibrationDistance	1	25
    /// </summary>
    public class PHD2ProfileManager
    {
        private static readonly object _fileLock = new();
        private readonly string _profileFilePath;

        /// <summary>
        /// Initializes a new PHD2 profile manager for the specified file path
        /// </summary>
        /// <param name="profileFilePath">Full path to the .phd profile file</param>
        public PHD2ProfileManager(string profileFilePath)
        {
            _profileFilePath = profileFilePath ?? throw new ArgumentNullException(nameof(profileFilePath));
        }

        /// <summary>
        /// Reads all settings from the PHD2 profile file
        /// </summary>
        /// <returns>Dictionary of settings with keys and values</returns>
        public async Task<Dictionary<string, string>> ReadProfileAsync()
        {
            if (!File.Exists(_profileFilePath))
            {
                Logger.Warning($"Profile file not found: {_profileFilePath}");
                return new Dictionary<string, string>();
            }

            try
            {
                lock (_fileLock)
                {
                    var settings = new Dictionary<string, string>();
                    var lines = File.ReadAllLines(_profileFilePath);

                    foreach (var line in lines)
                    {
                        // Skip empty lines and header
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("PHD Config"))
                            continue;

                        // Parse the line: /path/to/setting<TAB>scope<TAB>type<TAB>value
                        var parts = line.Split('\t');
                        if (parts.Length >= 4)
                        {
                            var key = parts[0];
                            var value = parts[3]; // The value is the 4th column
                            settings[key] = value;
                        }
                    }

                    return settings;
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error reading PHD2 profile: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Gets a specific setting from the PHD2 profile
        /// </summary>
        /// <param name="settingPath">The full path to the setting (e.g., /profile/2/camera/binning)</param>
        /// <returns>The setting value, or null if not found</returns>
        public async Task<string> GetSettingAsync(string settingPath)
        {
            if (string.IsNullOrEmpty(settingPath))
                throw new ArgumentNullException(nameof(settingPath));

            var settings = await ReadProfileAsync();
            return settings.ContainsKey(settingPath) ? settings[settingPath] : null;
        }

        /// <summary>
        /// Sets a setting in the PHD2 profile file
        /// </summary>
        /// <param name="settingPath">The full path to the setting (e.g., /profile/2/camera/binning)</param>
        /// <param name="value">The new value for the setting</param>
        /// <param name="scope">The scope identifier (typically 1 for local settings)</param>
        public async Task SetSettingAsync(string settingPath, string value, int scope = 1)
        {
            if (string.IsNullOrEmpty(settingPath))
                throw new ArgumentNullException(nameof(settingPath));

            if (value == null)
                throw new ArgumentNullException(nameof(value));

            try
            {
                lock (_fileLock)
                {
                    var lines = new List<string>();
                    var headerFound = false;
                    var settingFound = false;
                    var allLines = File.Exists(_profileFilePath) ? File.ReadAllLines(_profileFilePath).ToList() : new List<string>();

                    foreach (var line in allLines)
                    {
                        // Preserve header
                        if (line.StartsWith("PHD Config"))
                        {
                            lines.Add(line);
                            headerFound = true;
                            continue;
                        }

                        // Skip empty lines
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            lines.Add(line);
                            continue;
                        }

                        // Check if this is the setting we're looking for
                        var parts = line.Split('\t');
                        if (parts.Length >= 4 && parts[0] == settingPath)
                        {
                            // Update the existing setting
                            lines.Add($"{settingPath}\t{scope}\t1\t{value}");
                            settingFound = true;
                        }
                        else
                        {
                            lines.Add(line);
                        }
                    }

                    // If header not found, add it
                    if (!headerFound)
                    {
                        lines.Insert(0, "PHD Config 1");
                    }

                    // If setting not found, add it at the end (before any empty lines at end)
                    if (!settingFound)
                    {
                        lines.Add($"{settingPath}\t{scope}\t1\t{value}");
                    }

                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(_profileFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write back to file
                    File.WriteAllLines(_profileFilePath, lines);
                    Logger.Debug($"Updated PHD2 profile setting: {settingPath} = {value}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error writing PHD2 profile setting: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Gets all settings for a specific profile section
        /// </summary>
        /// <param name="sectionPath">The path prefix (e.g., /profile/2/camera)</param>
        /// <returns>Dictionary of settings matching the section</returns>
        public async Task<Dictionary<string, string>> GetSectionAsync(string sectionPath)
        {
            if (string.IsNullOrEmpty(sectionPath))
                throw new ArgumentNullException(nameof(sectionPath));

            var allSettings = await ReadProfileAsync();
            var sectionSettings = new Dictionary<string, string>();

            foreach (var kvp in allSettings)
            {
                if (kvp.Key.StartsWith(sectionPath))
                {
                    sectionSettings[kvp.Key] = kvp.Value;
                }
            }

            return sectionSettings;
        }

        /// <summary>
        /// Deletes a setting from the PHD2 profile
        /// </summary>
        /// <param name="settingPath">The full path to the setting</param>
        public async Task DeleteSettingAsync(string settingPath)
        {
            if (string.IsNullOrEmpty(settingPath))
                throw new ArgumentNullException(nameof(settingPath));

            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(_profileFilePath))
                        return;

                    var lines = File.ReadAllLines(_profileFilePath).ToList();
                    var updatedLines = lines.Where(line =>
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("PHD Config"))
                            return true;

                        var parts = line.Split('\t');
                        return parts.Length < 4 || parts[0] != settingPath;
                    }).ToList();

                    File.WriteAllLines(_profileFilePath, updatedLines);
                    Logger.Debug($"Deleted PHD2 profile setting: {settingPath}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deleting PHD2 profile setting: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Gets the current profile number from the PHD2 profile file
        /// </summary>
        /// <returns>The profile number (typically 1 or 2)</returns>
        public async Task<int> GetCurrentProfileAsync()
        {
            var settings = await ReadProfileAsync();
            if (settings.ContainsKey("/currentProfile"))
            {
                if (int.TryParse(settings["/currentProfile"], out var profileNum))
                {
                    return profileNum;
                }
            }
            return 1; // Default to profile 1
        }

        /// <summary>
        /// Exports profile settings to a JSON-compatible dictionary
        /// </summary>
        /// <returns>Dictionary suitable for JSON serialization</returns>
        public async Task<Dictionary<string, object>> ExportProfileAsync()
        {
            var settings = await ReadProfileAsync();
            var exported = new Dictionary<string, object>();

            foreach (var kvp in settings)
            {
                // Try to convert numeric values
                if (double.TryParse(kvp.Value, out var doubleVal))
                {
                    exported[kvp.Key] = doubleVal;
                }
                else if (kvp.Value == "0" || kvp.Value == "1")
                {
                    exported[kvp.Key] = kvp.Value == "1";
                }
                else
                {
                    exported[kvp.Key] = kvp.Value;
                }
            }

            return exported;
        }

        /// <summary>
        /// Creates a new profile with the specified number and optional initial settings
        /// </summary>
        /// <param name="profileNumber">The profile number to create (e.g., 2, 3, 4)</param>
        /// <param name="profileName">Display name for the profile (optional)</param>
        /// <param name="templateSettings">Initial settings for the profile (optional)</param>
        public async Task CreateProfileAsync(int profileNumber, string profileName = null, Dictionary<string, string> templateSettings = null)
        {
            if (profileNumber < 1)
                throw new ArgumentException("Profile number must be >= 1", nameof(profileNumber));

            try
            {
                lock (_fileLock)
                {
                    var lines = new List<string>();
                    
                    // Read existing content
                    if (File.Exists(_profileFilePath))
                    {
                        lines.AddRange(File.ReadAllLines(_profileFilePath));
                    }
                    else
                    {
                        // Add header if creating new file
                        lines.Add("PHD Config 1");
                    }

                    // Add profile name if provided
                    if (!string.IsNullOrEmpty(profileName))
                    {
                        lines.Add($"/profile/{profileNumber}/name\t1\t1\t{profileName}");
                    }

                    // Add template settings or basic default settings for the profile
                    if (templateSettings != null && templateSettings.Count > 0)
                    {
                        foreach (var kvp in templateSettings)
                        {
                            // Only add settings that start with the profile number path
                            if (kvp.Key.Contains($"/profile/{profileNumber}/"))
                            {
                                lines.Add($"{kvp.Key}\t1\t1\t{kvp.Value}");
                            }
                        }
                    }
                    else
                    {
                        // Add minimal default settings for new profile
                        AddDefaultProfileSettings(lines, profileNumber);
                    }

                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(_profileFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write back to file
                    File.WriteAllLines(_profileFilePath, lines);
                    Logger.Debug($"Created new PHD2 profile: {profileNumber} (name: {profileName ?? "default"})");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error creating PHD2 profile: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Duplicates an existing profile with a new profile number
        /// </summary>
        /// <param name="sourceProfileNumber">The profile number to copy from</param>
        /// <param name="targetProfileNumber">The new profile number to create</param>
        /// <param name="newProfileName">Optional new name for the profile</param>
        public async Task DuplicateProfileAsync(int sourceProfileNumber, int targetProfileNumber, string newProfileName = null)
        {
            if (sourceProfileNumber == targetProfileNumber)
                throw new ArgumentException("Source and target profile numbers must be different");

            if (targetProfileNumber < 1)
                throw new ArgumentException("Profile number must be >= 1", nameof(targetProfileNumber));

            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(_profileFilePath))
                        throw new FileNotFoundException($"Profile file not found: {_profileFilePath}");

                    var lines = File.ReadAllLines(_profileFilePath).ToList();
                    var newLines = new List<string>();
                    var sourceSettings = new Dictionary<string, string>();

                    // First pass: collect header and source profile settings
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("PHD Config"))
                        {
                            newLines.Add(line);
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(line))
                        {
                            newLines.Add(line);
                            continue;
                        }

                        var parts = line.Split('\t');
                        if (parts.Length >= 4 && parts[0].Contains($"/profile/{sourceProfileNumber}/"))
                        {
                            // Store source settings
                            sourceSettings[parts[0]] = line;
                        }
                        else
                        {
                            // Keep other settings
                            newLines.Add(line);
                        }
                    }

                    // Second pass: add target profile settings based on source
                    foreach (var kvp in sourceSettings)
                    {
                        var newKey = kvp.Key.Replace($"/profile/{sourceProfileNumber}/", $"/profile/{targetProfileNumber}/");
                        
                        // Override profile name if provided
                        if (newKey.EndsWith("/name") && !string.IsNullOrEmpty(newProfileName))
                        {
                            newLines.Add($"{newKey}\t1\t1\t{newProfileName}");
                        }
                        else
                        {
                            var parts = kvp.Value.Split('\t');
                            newLines.Add($"{newKey}\t{parts[1]}\t{parts[2]}\t{parts[3]}");
                        }
                    }

                    // Ensure directory exists
                    var directory = Path.GetDirectoryName(_profileFilePath);
                    if (!Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    // Write back to file
                    File.WriteAllLines(_profileFilePath, newLines);
                    Logger.Debug($"Duplicated PHD2 profile {sourceProfileNumber} to {targetProfileNumber}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error duplicating PHD2 profile: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Deletes all settings associated with a profile number
        /// </summary>
        /// <param name="profileNumber">The profile number to delete</param>
        public async Task DeleteProfileAsync(int profileNumber)
        {
            if (profileNumber < 1)
                throw new ArgumentException("Profile number must be >= 1", nameof(profileNumber));

            try
            {
                lock (_fileLock)
                {
                    if (!File.Exists(_profileFilePath))
                        return;

                    var lines = File.ReadAllLines(_profileFilePath).ToList();
                    var updatedLines = lines.Where(line =>
                    {
                        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("PHD Config"))
                            return true;

                        var parts = line.Split('\t');
                        return parts.Length < 1 || !parts[0].Contains($"/profile/{profileNumber}/");
                    }).ToList();

                    File.WriteAllLines(_profileFilePath, updatedLines);
                    Logger.Debug($"Deleted PHD2 profile: {profileNumber}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deleting PHD2 profile: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Gets a list of all profile numbers available in the profile file
        /// </summary>
        /// <returns>List of profile numbers</returns>
        public async Task<List<int>> ListProfilesAsync()
        {
            try
            {
                var settings = await ReadProfileAsync();
                var profileNumbers = new HashSet<int>();

                foreach (var key in settings.Keys)
                {
                    // Extract profile number from paths like /profile/2/...
                    var match = System.Text.RegularExpressions.Regex.Match(key, @"/profile/(\d+)/");
                    if (match.Success && int.TryParse(match.Groups[1].Value, out var profileNum))
                    {
                        profileNumbers.Add(profileNum);
                    }
                }

                return profileNumbers.OrderBy(x => x).ToList();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing PHD2 profiles: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Gets profile information including name and basic settings
        /// </summary>
        /// <param name="profileNumber">The profile number to query</param>
        /// <returns>Profile info dictionary</returns>
        public async Task<Dictionary<string, object>> GetProfileInfoAsync(int profileNumber)
        {
            try
            {
                var section = await GetSectionAsync($"/profile/{profileNumber}");
                var info = new Dictionary<string, object>
                {
                    { "profileNumber", profileNumber },
                    { "settingCount", section.Count },
                    { "settings", section }
                };

                // Try to get profile name
                var nameKey = $"/profile/{profileNumber}/name";
                if (section.ContainsKey(nameKey))
                {
                    info["name"] = section[nameKey];
                }

                return info;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting PHD2 profile info: {ex}");
                throw;
            }
        }

        /// <summary>
        /// Adds default settings for a new profile
        /// </summary>
        private void AddDefaultProfileSettings(List<string> lines, int profileNumber)
        {
            // Add minimal core settings for a new profile
            var defaultSettings = new[]
            {
                $"/profile/{profileNumber}/name\t1\t1\tProfile {profileNumber}",
                $"/profile/{profileNumber}/camera/binning\t1\t1\t1",
                $"/profile/{profileNumber}/camera/pixelsize\t1\t1\t1.5",
                $"/profile/{profileNumber}/camera/TimeoutMs\t1\t1\t15000",
                $"/profile/{profileNumber}/guider/StarMinSNR\t1\t1\t6",
                $"/profile/{profileNumber}/guider/StarMaxHFD\t1\t1\t10",
                $"/profile/{profileNumber}/guider/FastRecenter\t1\t1\t1",
                $"/profile/{profileNumber}/scope/CalibrationDistance\t1\t1\t25",
                $"/profile/{profileNumber}/scope/MaxRaDuration\t1\t1\t2500",
                $"/profile/{profileNumber}/scope/MaxDecDuration\t1\t1\t2500",
                $"/profile/{profileNumber}/ExposureDurationMs\t1\t1\t1000"
            };

            lines.AddRange(defaultSettings);
        }
    }
}
