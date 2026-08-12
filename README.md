# MetricsPusher

MetricsPusher is a lightweight Windows tray application that sends hardware and system
metrics to a display panel on the local network once per second over UDP. It has no
installer, service, or autostart component; run the executable and use the tray icon to
exit it. It does require administrator rights, and it can offer to install one third-party
kernel driver on first run — both are explained below.

## Requirements

- 64-bit Windows
- **Administrator rights** — the app shows a UAC prompt every time it starts, see below
- An NVIDIA GPU for GPU metrics
- A display receiver on the supported local network
- Optional: the PawnIO kernel driver, which is needed for CPU die temperature and package
  power

## Administrator rights

**MetricsPusher requires administrator rights and prompts for them on every launch.**

The reason is CPU die temperature. Windows exposes no unprivileged way to read it — the
value lives in model-specific registers on Intel and in the data-fabric address space on
AMD, both of which need kernel code. MetricsPusher reads them through the PawnIO driver,
whose device is protected by an access-control list that admits only SYSTEM and the local
Administrators group. Without an administrator token the device cannot be opened at all: the
open fails with "access denied" before any temperature is read. There is no configuration
that changes this and no partial version of it.

**Earlier versions of MetricsPusher refused to run elevated.** That rule has been reversed
deliberately, and it is the one change here with a real cost:

- **Standard-user accounts can no longer launch MetricsPusher at all.** If you do not have
  administrator credentials on the machine, this version will not start. That is a
  regression from v1.0.0, not an oversight.
- A UAC prompt appears on every launch. There is no "remember this" for it.

Everything else the app reports — GPU metrics, CPU load, RAM, disk space, NVMe drive
temperature — works without any of this. A Windows manifest is all-or-nothing, though, so
the elevation the CPU sensor needs applies to the whole process.

## CPU die temperature and the PawnIO driver

PawnIO is a third-party, digitally signed kernel driver that executes signed bytecode
modules, so a program gets a narrow, per-module set of operations rather than raw kernel
access. It is what LibreHardwareMonitor and FanControl moved to after WinRing0 was
deprecated and flagged by antivirus. MetricsPusher talks to it directly and bundles the two
modules it needs; no other PawnIO module is loaded.

**PawnIO is only needed for CPU die temperature.** NVMe drive temperature needs no driver,
no prerequisite and no elevation, and works on a stock machine.

On first run, if PawnIO is not installed, MetricsPusher asks once whether to install the
bundled 2.2.0 setup. Nothing is downloaded — the installer is inside the executable.
Declining is remembered per user and is never asked again; the only thing it costs is CPU
die temperature, and the app falls back to an ACPI thermal-zone reading where the firmware
provides one (see the limits below).

To install it yourself instead, without the prompt:

```powershell
winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements
```

**The driver is cleanly reversible.** It can be removed from Windows Settings > Installed
apps, or with the bundled installer:

```powershell
PawnIO_setup.exe -uninstall
```

The installer's complete flag list is `-install`, `-uninstall`, `-unrestricted`,
`-debuginfo` and `-silent`. It must itself be run elevated, and in silent mode it reports
nothing on screen even on failure.

**Never use `-unrestricted`.** That flag installs the edition of the driver that loads
*unsigned* modules, which discards the signature check that makes PawnIO safer than what it
replaced. MetricsPusher runs the bundled setup with `-install -silent` only.

### What the bundled PawnIO files are

| Asset | Version | SHA-256 |
|---|---|---|
| `Resources/PawnIo/IntelMSR.bin` | 0.2.10 | `D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F` |
| `Resources/PawnIo/AMDFamily17.bin` | 0.2.10 | `DAE74615761B78BDF064DFB3E136252DDCC6FC727D88F14738D0E5800D427A91` |
| `Resources/PawnIo/COPYING` | — | `1E7E6BAE5A5BDE32F1AE5A7C37A082D1AB03CF89354F7F936AC40BE9E39A6531` |
| `Resources/PawnIO_setup.exe` | 2.2.0 | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` |

The two modules come from `release_0_2_10.zip` at
<https://github.com/namazso/PawnIO.Modules/releases>, redistributed unmodified; they are
LGPL-2.1-or-later, which is why `COPYING` ships alongside them. The installer comes from
<https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0>. Its Authenticode signature is
Valid, signed `E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU`.

PawnIO itself is GPL-2.0-or-later with an explicit exception for independent modules that
communicate with it solely through the device IO-control interface. MetricsPusher does
exactly that — direct IOCTL, no library linked — so it stays unencumbered by that licence.

These files are embedded in the executable, so changing any of them changes the published
executable's own SHA-256. That is consistent with the reproducibility note below: a hash is
only meaningful against the exact tag it was taken from.

### Enterprise and compatibility

PawnIO is legitimately signed and is not on Microsoft's driver blocklist, but it is a
scriptable ring-0 driver, and it should be treated as one:

- Environments running **WDAC in enforcement** need it allowed in policy. Without that, the
  driver does not load and MetricsPusher degrades to the fallback reading.
- **FACEIT anti-cheat blocks PawnIO's signer outright**, so the two cannot coexist on the
  same machine. There is no workaround; if you run FACEIT, decline the install.
- Loading the Intel module gives this elevated process the ability to *write* the package
  power limit as well as read it. MetricsPusher only ever reads, but the capability is part
  of what installing the driver grants.

## What the temperature readings actually measure

**CPU, with PawnIO installed:** the die temperature — the Intel package thermal register, or
the AMD Tdie value derived from Tctl. This is the number HWiNFO64 and similar tools report as
CPU package temperature.

**CPU, without PawnIO:** an **ACPI thermal zone** read through performance counters. This is
a board or platform sensor that the firmware chose to expose, *not* the CPU die, and its
limits are worth knowing before you trust it:

- Expect it to read low and to lag behind the die under load.
- Many desktops expose no `\_TZ` object at all. The app then reports no CPU temperature.
  That is the expected outcome on such machines, not a fault.
- Virtual machines generally expose nothing.
- Some firmware reports a **constant** — a plausible-looking value that never moves. There is
  no reliable way to distinguish that from a genuinely stable idle temperature, which is why
  it is documented here rather than detected in code.

**NVMe drive temperature** is read from the system disk through a standard Windows storage
query. It needs no driver and no elevation, but it is not universal:

- Vendor NVMe drivers (Samsung's `secnvme.sys`), Intel RST / VMD RAID mode, USB-bridged
  drives and hardware RAID controllers may report the query as not supported. When that
  happens the app latches, logs one line and stays silent — it does not retry every second.
- Vendor-specific pass-through paths (`IOCTL_SCSI_MINIPORT`) are explicitly out of scope, so
  a drive that only answers those reports nothing.
- Temperature validation uses the same 0–150 °C band the wire contract applies to GPU
  temperature. A genuinely sub-zero drive — a cold boot in an unheated room — therefore
  reports nothing rather than a negative number. That is deliberate.

The UDP payload publishes die/package CPU temperature as `cpuTemp`, package draw as
`cpuWatts`, the Intel-only package limit as `cpuLimitW`, and system-disk temperature as
`nvmeTemp`. The ACPI board-zone fallback is still logged locally but is deliberately omitted
from `cpuTemp`, so that key never changes physical meaning by machine.

Since v1.0.1 it also describes the network adapter the datagram itself leaves by — the
same interface the display address is derived from: `netName` (driver make/model,
trademark marks stripped), `netType` (0 Ethernet / 1 Wi-Fi / 2 other), `netLink`
(negotiated Mbps) and `netRx`/`netTx` (throughput in kbit/s, measured over each 1 s
interval). The whole set costs one `GetIfEntry2` call per tick into a reused buffer —
no adapter enumeration, no new timer. See
[push_metrics.md](push_metrics.md) for exact ranges and absence semantics.

## What has and has not been tested

The CPU sensors were verified on Intel Core Ultra 7 155H and AMD Ryzen 9 9950X machines
running Windows 11. Intel validation confirmed opening the PawnIO
device, loading the Intel module, reading TjMax (110 °C on that part), decoding the package
temperature, deriving 10.05 W package power from the RAPL energy accumulator, the correctly
scaled 28 W TDP and 64 W PL1 limits, the driver refusing a register outside the module's
allow-list, and NVMe drive temperature working without elevation. Zen 5 validation confirmed
the family `0x1A`/model `0x44` module gate, SMN Tdie with zero offset, AMD RAPL package power,
structural absence of a package limit, and a 5½-minute 100%-CPU run holding 200 W cleanly
across the expected 32-bit accumulator-wrap interval.

Not verified, and stated plainly rather than implied:

- Other AMD generations remain unverified: Zen 3/4 and the first-generation non-zero Tdie
  offset branch, plus the expected-not-supported fallback on family `0x10`–`0x16`.
- **The silent install exit code.** PawnIO was installed interactively during development,
  so the `-install -silent` exit code was never observed. A clean 2.2.0 install on Windows
  11 did **not** ask for a restart, so the reboot-required path is not one a normal first
  install is expected to take — but the code still handles it, untested.
- Sleep/resume behavior has not been exercised on hardware.

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

This matters more than it used to. The app now runs elevated, so whatever sits at that path
is what an administrator token gets handed to on the next launch. Anyone who can write to
the folder can substitute the executable and inherit that prompt.

## Security summary

- **Nothing listens.** The UDP socket is send-only; the app never reads a datagram, and
  there is no config file, command-line input, IPC, or auto-update. No network input is
  parsed anywhere.
- **It runs elevated, and only because it must.** `app.manifest` declares
  `requireAdministrator` so the PawnIO device can be opened at all; see
  [Administrator rights](#administrator-rights) above for why there is no unprivileged
  alternative, and what it costs. Nothing else in the app needs the token.
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
