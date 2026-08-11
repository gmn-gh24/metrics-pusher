# MetricsPusher

MetricsPusher is a lightweight Windows tray application that sends hardware and system
metrics to a display panel on the local network once per second over UDP. It has no
installer, service, or autostart component; run the executable and use the tray icon to
exit it.

## Requirements

- 64-bit Windows
- An NVIDIA GPU for GPU metrics
- A display receiver on the supported local network

## Is .NET required?

**No separate .NET installation is required when using the published MetricsPusher
executable.** The release build is self-contained and bundles the required .NET 10
runtime into the single `win-x64` executable.

Use the executable from the `publish` output or a packaged release. Do not distribute an
executable copied directly from `bin\Release`: ordinary build output may be
framework-dependent and require the .NET 10 Windows Desktop Runtime to be installed on
the target computer.

## Build and publish

Build and test the project:

```powershell
dotnet build --warnaserror
dotnet test
```

Create the standalone executable that includes .NET:

```powershell
dotnet publish MetricsPusher.csproj -c Release -r win-x64 --self-contained -o "publish"
```

`PublishSingleFile` and `IncludeNativeLibrariesForSelfExtract` live in the project file, so
they no longer need to be passed on the command line — that also keeps `packages.lock.json`
identical between a plain build and a publish.

The distributable executable is written to `publish\MetricsPusher.exe` and is about 130 MB.
Because the .NET runtime is bundled, the file is substantially larger than a
framework-dependent build; anything near 3 MB means the publish silently fell back to
framework-dependent and will not run on a machine without the runtime.

## Where to install it

**Put the executable somewhere only administrators can write, such as
`%ProgramFiles%\MetricsPusher\`.**

This is the one deployment choice that carries a security consequence. MetricsPusher loads
`nvml.dll`, `nvapi64.dll`, `pdh.dll` and `wscapi.dll` from the operating system; the app
pins all four to an absolute `%WINDIR%\System32` path and refuses to search anywhere else
(see `Services/SystemLibraryResolver.cs`), so a DLL planted beside the executable is not
loaded. Running from a user-writable folder such as `Downloads` is therefore not dangerous
in itself — but a writable folder still lets anyone replace the executable, and nothing in
a portable build can detect that. An admin-only directory closes that.

## Security summary

- **Nothing listens.** The UDP socket is send-only; the app never reads a datagram, and
  there is no config file, command-line input, IPC, or auto-update. No network input is
  parsed anywhere.
- **It never runs elevated.** `app.manifest` declares `asInvoker` and the app refuses to
  start if its token carries the Administrators group.
- **The metrics are cleartext and unauthenticated,** by design — see
  [push_metrics.md](push_metrics.md) §10. Anyone on the subnet can read them, and they
  include this machine's antivirus, firewall and pending-reboot state.
- **A destination is only derived on a private network** (RFC 1918, CGNAT, or link-local).
  On a public IP address the push is disabled rather than sent to a stranger.
- Releases are not code-signed. If you distribute the executable, expect a SmartScreen
  "unknown publisher" prompt, and verify the file you run is the one you built.

## Logs

Application logs are stored at:

```text
%LOCALAPPDATA%\MetricsPusher\logs\app.log
```

For the UDP payload and wire protocol, see [push_metrics.md](push_metrics.md).
