using System;
using System.Collections.Generic;

namespace TouchNStars.Server.Models;

public class SystemMetrics
{
    public double? CpuUsagePercent { get; set; }
    public MemoryMetrics Memory { get; set; }
    public List<DiskMetrics> Disks { get; set; } = new();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
}

public class MemoryMetrics
{
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double UsedPercent { get; set; }
}

public class DiskMetrics
{
    public string Name { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long UsedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public double UsedPercent { get; set; }
}
