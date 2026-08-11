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

## Building from a fresh clone

The repository is self-contained: clone it and the two commands above are all you need. No
generated file is committed except `packages.lock.json`, which is a build *input* — it pins
the whole transitive dependency graph so every machine restores identical packages.
`dotnet restore --locked-mode` verifies the lock file matches the project files and fails
rather than silently updating them.

The only prerequisite is the **.NET 10 SDK**. Build output (`bin/`, `obj/`, `publish/`) is
git-ignored and never committed.

## Verifying a download

Release builds are **reproducible**: the same commit built on any machine, from any
directory, with the same SDK produces a **byte-identical** `MetricsPusher.exe`. This is what
`ContinuousIntegrationBuild` in the project file buys — without it the compiler embeds
absolute source paths and two machines produce two different binaries.

Because MetricsPusher is not code-signed, this is the only way to confirm a
`MetricsPusher.exe` you were given really is built from this source:

```powershell
git clone https://github.com/gmn-gh24/metrics-pusher.git
cd metrics-pusher
git checkout v1.0.0
dotnet publish MetricsPusher.csproj -c Release -r win-x64 --self-contained -o "publish"
Get-FileHash publish\MetricsPusher.exe -Algorithm SHA256
```

Compare that hash against the one published with the release. They must match exactly.

Note that the executable embeds its own commit hash, so **each commit produces a different
binary** — always check out the exact tag before comparing, and use the hash published
alongside that release rather than one taken from any other build.

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
