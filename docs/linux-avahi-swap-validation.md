# Linux discovery and swap validation

Validated on the user's ARM64 Raspberry Pi running Raspberry Pi OS Trixie,
Avahi 0.8-16 and the installed PINS/NINA .NET 10 runtime on 2026-10-11.

## Issue #115

The core behavior in [issue #115](https://github.com/Touch-N-Stars/N.I.N.A-Plugin-for-Touch-N-Stars/issues/115)
was reproduced with the originally installed plugin:

- NINA and Avahi both held UDP 5353 sockets.
- Restarting Avahi while NINA was running logged a second-stack warning and
  `Host name conflict`, then renamed the advertised hostname to `<host>-2.local`.
- Stopping NINA and restarting Avahi restored `<host>.local` without a conflict.
- Reinstalling the original DLL reproduced the conflict again; restoring the
  fixed DLL restored the original hostname.

The exact DNS failure differed from the report. The original name resolved to
a different IP, rather than timing out. A packet capture showed the router
answering with both a stale address and the Pi's current address. Therefore this
test confirms the plugin-dependent hostname conflict and wrong-host resolution,
but does not establish that every DNS failure on this LAN originates in the
plugin. Router DNS/cache state is a separate factor.

## Fixed plugin

The Linux publication path now calls `avahi-publish-service --no-fail`, preserving
the instance identity, configured port and TXT properties, including `txtvers=1`.
It leaves host/address records and UDP 5353 ownership to Avahi.

The final build passed:

- Three consecutive Avahi restarts: zero hostname conflicts; correct service,
  LAN address and port after each restart.
- Daemon absence: HTTP metrics remained available and publication reconnected
  after Avahi returned.
- Delayed daemon startup: NINA started successfully while Avahi was unavailable,
  then advertised successfully after Avahi started.
- NINA stop: the service disappeared; restarting NINA restored it.
- Only Avahi held UDP 5353 sockets.
- Both `_touchnstars._tcp` and `_pinsdaemon._tcp` resolved on the LAN.
- HTTP access by the restored `.local` hostname succeeded from the Windows client.

For the daemon-absence tests, Avahi's service and socket were temporarily masked
at runtime to prevent D-Bus activation from immediately restarting the daemon.
Both masks were removed and the original enabled services were restored.

## Swap and regression checks

- `/api/metrics` swap capacity/free/used counters matched `/proc/meminfo`.
  The tested host had 8,154,775,552 bytes of active disk swap and nonzero usage.
- The desktop and 390 x 844 browser layouts displayed the live swap card.
- Manual refresh and automatic polling updated the card.
- Browser-only response substitutions verified disabled swap and older backends
  with no `Swap` field. Live responses were restored afterward.
- pinsdaemon health returned HTTP 200; unauthenticated swap access returned 401.
- All 70 existing plugin tests and 10 system-boundary tests passed on the Pi.
- All 166 pinsdaemon tests passed on the Pi.
- All 852 frontend tests passed on Windows; targeted lint, formatting and the
  production frontend build passed.
- The Debian package built on the Pi with `avahi-utils` in its dependency list
  and the existing Avahi service file present. Shell syntax checks passed.

The full plugin was compiled from the current `origin/develop` sources against
the Pi's installed NINA and plugin dependency assemblies using SDK 10.0.401.
The existing test suite was run in a temporary .NET 10 project against that DLL;
its checked-in project still targets .NET 8 Windows. Build/test adaptation files
and logs remain in the Pi's validation directory. Compiler warnings were in
existing code outside this change.

Windows checkout line endings were normalized in the temporary Linux package
staging source. Missing TestClient dependencies were installed into a temporary
test-only Python path, leaving the daemon's production virtualenv unchanged.

The fixed plugin and frontend remain installed on the Pi, with the original DLL
and frontend entry point backed up. NINA, pinsdaemon and Avahi are running.
No imaging, guiding, slewing or hardware-connection session was exercised.

## Repeat the system-boundary tests

```sh
dotnet test Touch-N-Stars.System.Tests/Touch-N-Stars.System.Tests.csproj
```

On a Linux test host with Avahi and avahi-utils installed, opt in to publishing
a temporary test service to verify port/TXT updates and withdrawal:

```sh
RUN_AVAHI_INTEGRATION=1 dotnet test Touch-N-Stars.System.Tests/Touch-N-Stars.System.Tests.csproj
```
