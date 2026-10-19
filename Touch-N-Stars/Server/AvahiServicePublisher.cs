using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using NINA.Core.Utility;

namespace TouchNStars.Server;

// Registers with the system responder over D-Bus via Avahi's unprivileged client.
// Never publishes host records or opens an mDNS socket itself.
internal sealed class AvahiServicePublisher
{
    private Process publisher;
    private string[] currentArguments;

    internal static ProcessStartInfo BuildStartInfo(string name, string type, int port,
        IReadOnlyDictionary<string, string> properties)
    {
        var info = new ProcessStartInfo("/usr/bin/avahi-publish-service")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Wait for Avahi at boot and reconnect after daemon restarts.
        info.ArgumentList.Add("--no-fail");
        info.ArgumentList.Add("--");
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(type.TrimEnd('.'));
        info.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
        // ServiceProfile supplied this DNS-SD TXT version on the previous path.
        if (properties == null || !properties.ContainsKey("txtvers"))
            info.ArgumentList.Add("txtvers=1");
        if (properties != null)
        {
            foreach (var pair in properties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                    info.ArgumentList.Add($"{pair.Key}={pair.Value}");
            }
        }
        return info;
    }

    public void StartOrUpdate(string name, string type, int port,
        IReadOnlyDictionary<string, string> properties)
    {
        var info = BuildStartInfo(name, type, port, properties);
        var arguments = info.ArgumentList.ToArray();
        if (publisher != null && !publisher.HasExited && arguments.SequenceEqual(currentArguments))
            return;

        Stop();
        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Logger.Debug($"Avahi publisher: {e.Data}");
        };
        process.Exited += OnPublisherExited;
        try
        {
            process.Start();
            publisher = process;
            currentArguments = arguments;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch
        {
            Stop();
            process.Dispose();
            Logger.Warning("Linux discovery requires avahi-daemon and avahi-utils. No in-process mDNS responder will be started.");
            throw;
        }
    }

    private static void OnPublisherExited(object sender, EventArgs e)
    {
        Logger.Warning("Avahi service publisher exited; discovery will retry on the next advertisement update.");
    }

    public void Stop()
    {
        var process = publisher;
        publisher = null;
        currentArguments = null;
        if (process == null) return;
        try
        {
            process.Exited -= OnPublisherExited;
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(2000);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to stop Avahi publisher: {ex.Message}");
        }
        finally
        {
            process.Dispose();
        }
    }
}
