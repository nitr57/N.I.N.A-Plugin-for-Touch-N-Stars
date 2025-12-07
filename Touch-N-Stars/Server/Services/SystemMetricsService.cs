using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using NINA.Core.Utility;
using TouchNStars.Server.Models;

namespace TouchNStars.Server.Services;

public class SystemMetricsService
{
    private const int CpuSampleDelayMilliseconds = 200;

    public async Task<SystemMetrics> GetMetricsAsync()
    {
        var metrics = new SystemMetrics
        {
            CpuUsagePercent = await GetCpuUsageAsync().ConfigureAwait(false),
            Memory = GetMemoryMetrics(),
            Disks = GetDiskMetrics(),
            TimestampUtc = DateTime.UtcNow
        };

        return metrics;
    }

    private static async Task<double?> GetCpuUsageAsync()
    {
        var first = CaptureCpuSample();
        if (first == null)
        {
            return null;
        }

        await Task.Delay(CpuSampleDelayMilliseconds).ConfigureAwait(false);

        var second = CaptureCpuSample();
        if (second == null)
        {
            return null;
        }

        ulong idleDelta = second.Value.Idle - first.Value.Idle;
        ulong totalDelta = second.Value.Total - first.Value.Total;

        if (totalDelta == 0 || idleDelta > totalDelta)
        {
            return null;
        }

        double usage = (double)(totalDelta - idleDelta) / totalDelta * 100.0;
        return Math.Clamp(usage, 0, 100);
    }

    private static CpuSample? CaptureCpuSample()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
                {
                    return null;
                }

                ulong idleTicks = ToUInt64(idle);
                ulong kernelTicks = ToUInt64(kernel);
                ulong userTicks = ToUInt64(user);
                ulong total = kernelTicks + userTicks;

                return new CpuSample(idleTicks, total);
            }

            if (OperatingSystem.IsLinux())
            {
                string line = File.ReadLines("/proc/stat").FirstOrDefault();
                if (string.IsNullOrEmpty(line) || !line.StartsWith("cpu", StringComparison.Ordinal))
                {
                    return null;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5)
                {
                    return null;
                }

                ulong idle = ParseUlong(parts, 4);
                ulong iowait = ParseUlong(parts, 5);
                ulong total = 0;

                for (int i = 1; i < parts.Length; i++)
                {
                    total += ParseUlong(parts, i);
                }

                ulong idleAll = idle + iowait;
                return new CpuSample(idleAll, total);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"System metrics CPU sample failed: {ex.Message}");
        }

        return null;
    }

    private static MemoryMetrics GetMemoryMetrics()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                MEMORYSTATUSEX status = new MEMORYSTATUSEX
                {
                    dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX))
                };

                if (GlobalMemoryStatusEx(ref status))
                {
                    long total = unchecked((long)status.ullTotalPhys);
                    long available = unchecked((long)status.ullAvailPhys);
                    return BuildMemoryMetrics(total, available);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                Dictionary<string, long> values = File
                    .ReadLines("/proc/meminfo")
                    .Select(ParseMemInfoLine)
                    .Where(kv => kv.HasValue)
                    .Select(kv => kv.Value)
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

                if (values.TryGetValue("MemTotal", out long total))
                {
                    long available = values.TryGetValue("MemAvailable", out long memAvailable)
                        ? memAvailable
                        : (values.TryGetValue("MemFree", out long memFree) ? memFree : 0)
                          + (values.TryGetValue("Buffers", out long buffers) ? buffers : 0)
                          + (values.TryGetValue("Cached", out long cached) ? cached : 0);

                    return BuildMemoryMetrics(total, available);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"System metrics memory sample failed: {ex.Message}");
        }

        return null;
    }

    private static MemoryMetrics BuildMemoryMetrics(long total, long available)
    {
        total = Math.Max(total, 0);
        available = Math.Clamp(available, 0, total);
        long used = total - available;
        double usedPercent = total == 0 ? 0 : (double)used / total * 100.0;

        return new MemoryMetrics
        {
            TotalBytes = total,
            AvailableBytes = available,
            UsedBytes = used,
            UsedPercent = Math.Clamp(usedPercent, 0, 100)
        };
    }

    private static List<DiskMetrics> GetDiskMetrics()
    {
        List<DiskMetrics> disks = new();

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                long total = drive.TotalSize;
                long available = drive.AvailableFreeSpace;
                long used = total - available;
                double usedPercent = total == 0 ? 0 : (double)used / total * 100.0;

                disks.Add(new DiskMetrics
                {
                    Name = drive.Name,
                    TotalBytes = total,
                    AvailableBytes = available,
                    UsedBytes = used,
                    UsedPercent = Math.Clamp(usedPercent, 0, 100)
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"System metrics disk sample failed: {ex.Message}");
        }

        return disks;
    }

    private static ulong ParseUlong(string[] parts, int index)
    {
        if (index >= parts.Length)
        {
            return 0;
        }

        return ulong.TryParse(parts[index], out ulong value) ? value : 0;
    }

    private static KeyValuePair<string, long>? ParseMemInfoLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string[] segments = line.Split(':', 2, StringSplitOptions.TrimEntries);
        if (segments.Length != 2)
        {
            return null;
        }

        string key = segments[0];
        string valuePart = segments[1];
        string numericToken = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (numericToken == null)
        {
            return null;
        }

        if (!long.TryParse(numericToken, out long valueKb))
        {
            return null;
        }

        long valueBytes = valueKb * 1024;
        return new KeyValuePair<string, long>(key, valueBytes);
    }

    private static ulong ToUInt64(FILETIME time)
    {
        return ((ulong)time.dwHighDateTime << 32) | time.dwLowDateTime;
    }

    private readonly record struct CpuSample(ulong Idle, ulong Total);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
