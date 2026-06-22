using NINA.Core.Utility;
using System;
using System.Diagnostics;
using System.Net.Sockets;

namespace TouchNStars.Server.Services;

/// <summary>
/// Manages a native PHD2 GUI session streamed to the browser via xpra's HTML5 client.
///
/// The native PHD2 dialogs (Guiding Assistant, Brain, calibration Wizard) are not
/// exposed over the PHD2 JSON-RPC surface that the rest of Touch-N-Stars drives, so
/// this service launches PHD2 inside a headless xpra session and lets the frontend
/// embed xpra's HTML5 client in an iframe.
///
/// Linux only. The service manages the xpra process lifecycle and binds xpra's HTML5
/// server to a TCP port the browser connects to directly. (A future hardening step is
/// to bind xpra to loopback and reverse-proxy it through the TNS web server.)
/// </summary>
public class Phd2GuiService
{
    private const int DefaultPort = 14500;
    private const int DefaultDisplay = 100;
    private const string DefaultPhd2Command = "phd2";

    private static readonly object stateLock = new object();
    private bool? xpraAvailable;

    // Remember what we last started so status/stop can default to it.
    private int lastPort = DefaultPort;
    private int lastDisplay = DefaultDisplay;

    public int LastPort { get { lock (stateLock) { return lastPort; } } }
    public int LastDisplay { get { lock (stateLock) { return lastDisplay; } } }

    /// <summary>
    /// True when running on Linux and the xpra binary is on PATH. Cached after first probe.
    /// </summary>
    public bool IsAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        lock (stateLock)
        {
            if (xpraAvailable.HasValue)
            {
                return xpraAvailable.Value;
            }
        }

        bool available = false;
        try
        {
            var result = RunBash("command -v xpra", 5000);
            available = result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut);
        }
        catch (Exception ex)
        {
            Logger.Warning($"PHD2 GUI: xpra availability probe failed: {ex.Message}");
        }

        lock (stateLock)
        {
            xpraAvailable = available;
        }
        return available;
    }

    /// <summary>
    /// True when something is listening on the xpra HTML5 port (i.e. the session is up).
    /// </summary>
    public bool IsRunning(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.BeginConnect("127.0.0.1", port, null, null);
            bool ok = connect.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(750));
            if (ok)
            {
                client.EndConnect(connect);
            }
            return ok && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Start a PHD2 xpra session. Idempotent: if the port is already serving, returns
    /// without launching a second session.
    /// </summary>
    public StartResult Start(int port, int display, string phd2Command, string extraArgs)
    {
        if (!OperatingSystem.IsLinux())
        {
            return StartResult.Fail("The PHD2 native GUI embed is only supported on Linux.");
        }
        if (!IsAvailable())
        {
            return StartResult.Fail("xpra is not installed on the host. Install xpra to use the native PHD2 GUI.");
        }

        if (port <= 0 || port > 65535) port = DefaultPort;
        if (display <= 0) display = DefaultDisplay;
        if (string.IsNullOrWhiteSpace(phd2Command)) phd2Command = DefaultPhd2Command;
        extraArgs ??= string.Empty;

        lock (stateLock)
        {
            lastPort = port;
            lastDisplay = display;
        }

        if (IsRunning(port))
        {
            Logger.Info($"PHD2 GUI: xpra session already serving on port {port}");
            return StartResult.Ok(port, display, alreadyRunning: true);
        }

        // On a standard xpra install the dummy Xorg backend is used automatically; PHD2
        // (wxWidgets) renders incorrectly under Xvfb, so a distro needing an explicit
        // backend should inject "--xvfb=Xorg ..." via extraArgs.
        var startCommand =
            $"xpra start :{display} " +
            $"--start={ShellQuote(phd2Command)} " +
            $"--html=on " +
            $"--bind-tcp=0.0.0.0:{port} " +
            $"--daemon=yes --systemd-run=no --no-pulseaudio " +
            extraArgs;

        Logger.Info($"PHD2 GUI: starting xpra session => {startCommand.Trim()}");
        var result = RunBash(startCommand, 20000);

        if (!string.IsNullOrWhiteSpace(result.StdOut)) Logger.Info($"PHD2 GUI: xpra stdout => {result.StdOut.Trim()}");
        if (!string.IsNullOrWhiteSpace(result.StdErr)) Logger.Info($"PHD2 GUI: xpra stderr => {result.StdErr.Trim()}");

        if (result.ExitCode != 0)
        {
            var msg = string.IsNullOrWhiteSpace(result.StdErr)
                ? $"xpra start exited with code {result.ExitCode}"
                : result.StdErr.Trim();
            return StartResult.Fail(msg);
        }

        // xpra daemonizes before its HTML5 server accepts connections; wait for the
        // port so the frontend doesn't embed the iframe too early.
        if (!WaitForPort(port, 10000))
        {
            return StartResult.Fail($"xpra session started but port {port} did not come up in time.");
        }

        return StartResult.Ok(port, display, alreadyRunning: false);
    }

    /// <summary>
    /// Stop the PHD2 xpra session on the given display.
    /// </summary>
    public bool Stop(int display)
    {
        if (!OperatingSystem.IsLinux() || !IsAvailable())
        {
            return false;
        }
        if (display <= 0) display = LastDisplay;

        Logger.Info($"PHD2 GUI: stopping xpra session :{display}");
        var result = RunBash($"xpra stop :{display}", 15000);
        if (result.ExitCode != 0)
        {
            Logger.Warning($"PHD2 GUI: xpra stop :{display} exited with {result.ExitCode}: {result.StdErr?.Trim()}");
        }
        return result.ExitCode == 0;
    }

    private bool WaitForPort(int port, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (IsRunning(port))
            {
                return true;
            }
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    private static string ShellQuote(string value)
    {
        // Single-quote and escape embedded single quotes for safe bash -c usage.
        return "'" + value.Replace("'", "'\\''") + "'";
    }

    private static ProcessResult RunBash(string command, int timeoutMs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/bash",
            Arguments = $"-c \"{command.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(true); } catch { }
            return new ProcessResult { ExitCode = -1, StdOut = stdout, StdErr = "Command timed out." };
        }
        return new ProcessResult { ExitCode = process.ExitCode, StdOut = stdout, StdErr = stderr };
    }

    private struct ProcessResult
    {
        public int ExitCode;
        public string StdOut;
        public string StdErr;
    }

    public struct StartResult
    {
        public bool Success;
        public string Error;
        public int Port;
        public int Display;
        public bool AlreadyRunning;

        public static StartResult Ok(int port, int display, bool alreadyRunning) =>
            new StartResult { Success = true, Port = port, Display = display, AlreadyRunning = alreadyRunning };

        public static StartResult Fail(string error) =>
            new StartResult { Success = false, Error = error };
    }
}
