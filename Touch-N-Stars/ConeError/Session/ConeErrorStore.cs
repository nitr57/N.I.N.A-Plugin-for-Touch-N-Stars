using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using NINA.Core.Utility;

namespace TouchNStars.ConeError.Session;

/// <summary>Settings of the cone error wizard (TnsCache/cone-error/settings.json).</summary>
public sealed class ConeErrorSettings
{
    /// <summary>Minimum altitude of the measurement.</summary>
    public double MinAltitudeDegrees { get; set; } = 20.0;

    public double? DovetailMm { get; set; }

    public int Repetitions { get; set; } = 3;

    public double? ExposureSeconds { get; set; }

    /// <summary>Mounts whose pier side labels the user has confirmed against the counterweight.</summary>
    public List<string> VerifiedPierLabels { get; set; } = new();

    /// <summary>Testing in daylight (e.g. with simulators): the Sun check only warns instead of blocking.</summary>
    public bool IgnoreSunCheck { get; set; }

    /// <summary>Clamps values a client may have sent out of range.</summary>
    public ConeErrorSettings Sanitized()
    {
        return new ConeErrorSettings
        {
            MinAltitudeDegrees = Math.Clamp(double.IsNaN(MinAltitudeDegrees) ? 20.0 : MinAltitudeDegrees, 0.0, 80.0),
            DovetailMm = DovetailMm is > 0 and < 2000 ? DovetailMm : null,
            Repetitions = Math.Clamp(Repetitions, 2, 10),
            ExposureSeconds = ExposureSeconds is > 0 and <= 120 ? ExposureSeconds : null,
            VerifiedPierLabels = (VerifiedPierLabels ?? new List<string>()).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList(),
            IgnoreSunCheck = IgnoreSunCheck
        };
    }
}

/// <summary>One east/west pair of the automated measurement.</summary>
public sealed class ConePairRecord
{
    public int Index { get; set; }
    public double RightAscensionHours { get; set; }
    public double DeclinationDegrees { get; set; }
    public double? DeltaRaEastArcmin { get; set; }
    public double? DeltaRaWestArcmin { get; set; }

    /// <summary>ΔDec(W) − ΔDec(E): a Dec index error shows here, not in the cone.</summary>
    public double? DeltaDecWestMinusEastArcmin { get; set; }

    public double? ConeArcmin { get; set; }
    public bool Rejected { get; set; }
    public string Problem { get; set; }
}

/// <summary>A finished measurement, kept for the before/after comparison (TnsCache/cone-error/results.json).</summary>
public sealed class ConeResultRecord
{
    public string Id { get; set; }

    /// <summary>"auto": plate solves on both pier sides.</summary>
    public string Mode { get; set; }

    public DateTime TimestampUtc { get; set; }
    public string ProfileId { get; set; }
    public string ProfileName { get; set; }
    public string MountName { get; set; }
    public double DeclinationDegrees { get; set; }
    public double? DovetailMm { get; set; }
    public double ConeArcmin { get; set; }
    public double? StdDevArcmin { get; set; }
    public double? StdErrorArcmin { get; set; }
    public double? MedianArcmin { get; set; }
    public int PairsUsed { get; set; }
    public int PairsTotal { get; set; }
    public string Quality { get; set; }
    public bool Significant { get; set; }
    public double? ShimMm { get; set; }

    /// <summary>"Front", "Rear" or "None".</summary>
    public string ShimEnd { get; set; }

    public List<ConePairRecord> Pairs { get; set; } = new();
}

/// <summary>JSON files of the wizard, in one directory, each written under a lock.</summary>
public sealed class ConeErrorStore
{
    public const int MaxResults = 50;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly object fileLock = new();

    public ConeErrorStore(string directory)
    {
        Directory = directory;
    }

    public string Directory { get; }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA", "TnsCache", "cone-error");

    private string SettingsPath => Path.Combine(Directory, "settings.json");
    private string ResultsPath => Path.Combine(Directory, "results.json");

    public ConeErrorSettings LoadSettings() => (Read<ConeErrorSettings>(SettingsPath) ?? new ConeErrorSettings()).Sanitized();

    public ConeErrorSettings SaveSettings(ConeErrorSettings settings)
    {
        ConeErrorSettings clean = settings.Sanitized();
        Write(SettingsPath, clean);
        return clean;
    }

    public List<ConeResultRecord> LoadResults() => Read<List<ConeResultRecord>>(ResultsPath) ?? new List<ConeResultRecord>();

    public void AddResult(ConeResultRecord result)
    {
        lock (fileLock)
        {
            List<ConeResultRecord> results = LoadResults();
            results.Add(result);
            Write(ResultsPath, results.OrderByDescending(r => r.TimestampUtc).Take(MaxResults).OrderBy(r => r.TimestampUtc).ToList());
        }
    }

    /// <summary>The latest earlier result of the same mode for the same profile and mount, for before/after.</summary>
    public ConeResultRecord PreviousResult(ConeResultRecord current)
    {
        return LoadResults()
            .Where(r => r.Id != current.Id && r.Mode == current.Mode && r.ProfileId == current.ProfileId && r.MountName == current.MountName && r.TimestampUtc < current.TimestampUtc)
            .OrderByDescending(r => r.TimestampUtc)
            .FirstOrDefault();
    }

    private T Read<T>(string path) where T : class
    {
        lock (fileLock)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception ex)
            {
                Logger.Warning($"ConeError: could not read {path}: {ex.Message}");
                return null;
            }
        }
    }

    private void Write<T>(string path, T value)
    {
        lock (fileLock)
        {
            System.IO.Directory.CreateDirectory(Directory);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, path, overwrite: true);
        }
    }
}
