using System.Diagnostics;
using TouchNStars.Server;
using Xunit;

public class AvahiIntegrationTests
{
    // Opt in only on a test host: this registers a temporary service on the LAN.
    private sealed class AvahiFactAttribute : FactAttribute
    {
        public AvahiFactAttribute()
        {
            if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("RUN_AVAHI_INTEGRATION") != "1")
                Skip = "Requires Linux, running Avahi, avahi-utils and RUN_AVAHI_INTEGRATION=1.";
        }
    }

    [AvahiFact]
    public async Task UpdatesAndWithdrawsServiceThroughSystemAvahi()
    {
        var name = $"tns-validation-{Guid.NewGuid():N}";
        var publisher = new AvahiServicePublisher();
        try
        {
            publisher.StartOrUpdate(name, "_tns-validation._tcp.", 5051,
                new Dictionary<string, string> { ["instanceName"] = "Test rig" });
            await Task.Delay(2000);
            var first = Browse();
            Assert.Contains($";{name};", first);
            Assert.Contains(";5051;", first);
            Assert.Contains("instanceName=Test rig", first);

            publisher.StartOrUpdate(name, "_tns-validation._tcp.", 5052,
                new Dictionary<string, string> { ["instanceName"] = "Updated rig" });
            await Task.Delay(2000);
            var updated = Browse();
            Assert.Contains(";5052;", updated);
            Assert.DoesNotContain(";5051;", updated);
            Assert.Contains("instanceName=Updated rig", updated);
        }
        finally
        {
            publisher.Stop();
        }
        await Task.Delay(2000);
        Assert.DoesNotContain($";{name};", Browse());
    }

    private static string Browse()
    {
        using var process = Process.Start(new ProcessStartInfo("/usr/bin/avahi-browse")
        {
            ArgumentList = { "-rtp", "_tns-validation._tcp" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill();
            throw new TimeoutException("Avahi service browse timed out.");
        }
        Assert.Equal(0, process.ExitCode);
        return output.GetAwaiter().GetResult();
    }
}
