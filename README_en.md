# N.I.N.A Plugin for Touch-N-Stars
This plugin integrates [Touch'N'Stars](https://github.com/Touch-N-Stars/Touch-N-Stars) with the astrophotography software **NINA** (Nighttime Imaging 'N' Astronomy).

### 🚀 **Current Status: Beta Version**  
The plugin is currently in development and may contain bugs.

### 🔧 **Installation**
To use the plugin, copy the contents of the ZIP file to NINA's plugin directory.
This is usually located at: %LOCALAPPDATA%/NINA/Plugins/3.0.0/Touch-N-Stars
If the folder doesn't exist, please create it first.

The release package includes the offline `celestia-atlas-data` tree. Landscapes
created by Touch'N'Stars are stored outside the replaceable plugin directory,
normally below `~/.local/share/NINA/Touch-N-Stars/celestia-atlas-data/landscapes`
on Linux. Existing generated landscapes are migrated automatically.

### 🧩 **Important Notes** 
- The **Advanced API** plugin is required in the latest version.
  The API port must be set to 1888 and V2 must be enabled.
  Additionally, "Use Access-Control-Allow-Origin Header" must be enabled.
- Version 2.2.2.0 or newer is required for Three Point Polar Alignment.

### Linux discovery and system metrics

Linux advertises `_touchnstars._tcp` using the system Avahi responder through
`avahi-publish-service --no-fail` (packages `avahi-daemon` and `avahi-utils`).
It preserves the configured instance name, port and TXT records, reconnects when
Avahi restarts, and withdraws the service when the plugin stops. The plugin never
starts its own UDP 5353 responder on Linux. If the client is missing, discovery
logs an error; the HTTP server remains available by IP. Install `avahi-utils`
when upgrading an older PINS image before using this plugin.

`GET /api/metrics` includes `Swap` on Linux with `TotalBytes`, `UsedBytes`,
`AvailableBytes` and `UsedPercent`, sampled alongside RAM from `/proc/meminfo`.
This reports all active swap, including zram and disk swap, rather than the
configured backing-file size. Disabled swap reports zero capacity and usage;
unavailable swap counters and other operating systems report null.

Run the isolated OS-boundary tests with
`dotnet test Touch-N-Stars.System.Tests/Touch-N-Stars.System.Tests.csproj`.
These compile the actual metrics and advertisement sources without deploying
the plugin or requiring the NINA host. A full plugin build requires .NET 10
and the Linux NINA project layout.

Live Pi validation and the opt-in Avahi integration test are documented in
[Linux discovery and swap validation](docs/linux-avahi-swap-validation.md).
