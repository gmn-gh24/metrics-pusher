# MetricsPusher

A Windows tray application whose only job is to push hardware metrics to a display panel
on the local subnet: one JSON UDP datagram per second, fire-and-forget. There is a tray
icon and a menu containing nothing but `Exit`. No installer, no service, no autostart.

The metrics engine was extracted verbatim from `R:\Yupix\systray-app` (YupixTrayApp
v5.12.1). **The wire contract is unchanged from that app** — see `push_metrics.md`, which
is authoritative for anything on the wire.

## Commands

```powershell
dotnet build --warnaserror     # must be clean - StyleCop + Roslynator + CA5392 are enforced
dotnet test                    # 447 tests
dotnet test --filter "FullyQualifiedName~GpuDisplayPushServiceTests"

# Portable single-file exe (~130 MB, no .NET prerequisite on the target machine)
dotnet publish MetricsPusher.csproj -c Release -r win-x64 --self-contained -o "publish"
```

Always publish `--self-contained` and use forward slashes in `-o`. A published exe near
3 MB means the publish silently fell back to framework-dependent.

Release publishes are **reproducible** — `ContinuousIntegrationBuild` is on for Release only
(it rewrites source paths, which would break local debugging). The same commit built from
any directory yields a byte-identical exe, which is the only integrity check an unsigned
binary has. The exe embeds its commit hash, so every commit changes it: a published SHA-256
is only meaningful against the exact tag it was taken from, and cannot live in this repo.

`PublishSingleFile` / `IncludeNativeLibrariesForSelfExtract` now live in the csproj rather
than the command line, so restore resolves one package set: passed only on the command
line, they added `Microsoft.NET.ILLink.Tasks` to `packages.lock.json` on every publish and
dropped it on every plain restore. Both are publish-time only - `dotnet build` is
unaffected. If you change either, re-check that the lock file is byte-identical after a
build and after a publish.

Logs: `%LOCALAPPDATA%\MetricsPusher\logs\app.log` (10 MB, rotates to `.1`–`.3`).

## Layout

| Path | What it is |
|---|---|
| `Program.cs` | Native-library pinning, single-instance mutex, the PawnIO first-run prompt, exception safety net |
| `TrayApplicationContext.cs` | The whole UI: icon, Exit item, and when the push loop starts |
| `Services/GpuDisplayPushService.cs` | Wire DTO, display discovery, the 1 Hz send loop |
| `Services/GpuMonitorService.cs` | GPU sensors: NVML primary, NVAPI fallback |
| `Services/NvmlService.cs` | `nvml.dll` P/Invoke layer |
| `Services/SystemMetricsService.cs` | CPU (PDH), RAM, disk, Windows version, AV/firewall/reboot |
| `Services/SampledMetric.cs` | Per-metric read cadences for the NVAPI fallback |
| `Services/LocalNetworkService.cs` | Local IPv4 selection, feeds address derivation |
| `Services/SystemLibraryResolver.cs` | Pins every P/Invoked native library to absolute System32 |
| `Services/PawnIoDevice.cs` | PawnIO IOCTL layer: open the device, load a signed module, execute a function. Not thread-safe, same contract as `NvmlService` |
| `Services/CpuTemperatureService.cs` | Provider selection, latching, caching, edge-triggered logging. The only CPU-sensor type the rest of the app talks to |
| `Services/CpuTemperatureProviders.cs` | `ICpuTemperatureProvider`: Intel package MSR, AMD Tctl/Tdie over SMN, and the ACPI thermal-zone fallback via PDH |
| `Services/CpuPackagePowerProvider.cs` | RAPL energy accumulator → watts, plus the Intel-only package power limit |
| `Services/NvmeTemperatureService.cs` | System-disk temperature via `IOCTL_STORAGE_QUERY_PROPERTY`. No driver, no elevation, no PawnIO |
| `Services/PawnIoInstaller.cs` | Presence probe, the one-time consent prompt, extract-and-run the bundled setup |
| `Resources/PawnIo/` | The two embedded signed modules (`IntelMSR.bin`, `AMDFamily17.bin`) and their `COPYING`; `Resources/PawnIO_setup.exe` is the bundled 2.2.0 installer |
| `push_metrics.md` | Authoritative UDP wire protocol. Update it in the same change as any wire-visible change |
| `README.md` | User-facing: requirements, publish, **where to install it and why** |

## Constraints

- **x64 only.** NVAPI and NVML are 64-bit; `PlatformTarget` is pinned.
- **Always runs elevated** — the reverse of the rule this app shipped with in v1.0.0.
  `app.manifest` declares `requireAdministrator`, and `Program.Main`'s refusal (`IsElevated`,
  `IsUacDisabled`, `IsUacDisabledValue`, `UacPolicyKey`) is deleted, not disabled. The reason
  is a DACL, not a preference: PawnIO's device carries `D:P(A;;GA;;;SY)(A;;GA;;;BA)` —
  protected, GENERIC_ALL for SYSTEM and Builtin Administrators, nothing for anyone else — so
  CPU die temperature is unreachable without an admin token. Measured, not inferred: a
  non-elevated `CreateFileW` on `\\?\GLOBALROOT\Device\PawnIO` returns Win32 5
  (`ERROR_ACCESS_DENIED`) on a machine where the driver is installed and running. The costs
  were accepted, not overlooked — a UAC prompt on every launch, and standard-user accounts
  can no longer launch the app at all. The escape hatch, if it ever bites, is PawnIO 2.2.0's
  opt-in non-admin device exposure, which would revert `app.manifest` alone; it was rejected
  here because it widens a kernel device ACL machine-wide for every local process.
- **One instance per session.** `Local\` mutex, not `Global\` — RDP and fast user
  switching each get their own tray icon. That deliberately allows two same-user instances,
  so anything touching a shared path must tolerate a second writer (`LoggingService` opens
  the log `FileShare.ReadWrite` for exactly this reason).
- **Every native library loads from System32, by absolute path.** `SystemLibraryResolver`
  pins `nvml` / `pdh` / `wscapi` / `nvapi64`, and `CA5392` is an **error** so a new
  `DllImport` cannot reintroduce a searched load. Adding a P/Invoke means adding its
  library to `GuardedLibraries` unless it is a KnownDLL.
- **.NET 10 (LTS).** Every publish is self-contained, so the runtime ships inside the exe
  and gets no Windows Update servicing — the only patch path for a runtime CVE is
  rebuilding this project. net8.0 went out of support on 10 Nov 2026.

## Invariants worth knowing before you change anything

- **`MetricsCacheTtlMs` (950) must stay below the 1 s send cadence.** It and
  `VramIntervalMs` / `LostSweepsBeforeDrop` move together — the comment above
  `LostSweepsBeforeDrop` in `GpuMonitorService.cs` spells out the false-drop window that
  opens if one moves alone.
- **Three version numbers, independent — never derive one from another.** The protocol
  version (`ProtocolVersion`, the wire `v`) moves *only* on a breaking schema change: a key
  removed, renamed, retyped, or re-meaning'd. Adding a key is not breaking — consumers
  ignore unknown keys — so it does not bump `v`. The app's release version
  (`MetricsPusher.csproj`) moves on any release and says nothing about wire compatibility.
  `push_metrics.md`'s document version moves on any edit to that file. v1.0.0 of this app
  speaks the same protocol `1` the originating tray app's v5.12.0 spoke. Spelled out in
  `push_metrics.md` §3.
- **Adding a wire field means raising `MaxDatagramBytes` and re-pinning the worst-case
  test in the same change.** The worst case (522) *equals* the ceiling by design; there
  is no slack. Only a total approaching 1024 reopens the receiver contract.
- **The four new `SystemMetrics` fields are deliberately unmapped.** `CpuTemperature`,
  `CpuPowerWatts`, `CpuPowerLimitWatts` and `NvmeTemperature` are populated on every tick and
  read by nothing but a Debug log line. Putting any one of them on the wire means raising
  `MaxDatagramBytes`, re-pinning the worst-case datagram test and updating `push_metrics.md`
  — all in the same change. The comment above them in `SystemMetricsService.cs` says the same
  thing at the call site; do not "fix" the omission ahead of that change.
- **`NvmlService` is deliberately not thread-safe.** Every member must be called under
  `GpuMonitorService._lock`.
- **The PawnIO device is opened `FILE_SHARE_READ | FILE_SHARE_WRITE` on purpose.** Do not
  harden it to 0. LibreHardwareMonitor and FanControl are clients of the same machine-wide
  device, and an exclusive open would either fail for us with `ERROR_SHARING_VIOLATION` or
  lock them out. The visible symptom would be CPU temperature silently degrading to the ACPI
  fallback whenever one of them happens to be running — a field-only failure no test catches.
- **`PawnIoDevice.TryExecute` must pass EXACT byte counts, never buffer capacity.** The
  buffers are preallocated and reused, so their capacity and the length of any one call are
  different numbers. `IntelMSR`'s `ioctl_read_msr` is declared `DEFINE_IOCTL_SIZED(…, 1, 1)`
  and size-checks `in_size`/`out_size` to exactly one int64 each, rejecting an oversized
  request *before* the MSR allow-list is ever consulted — so passing capacity fails every
  read while looking exactly like "this module does not support this CPU".
- **RAPL power registers use PSU; energy registers use ESU. They are different fields.**
  `MSR_PKG_POWER_INFO` (`0x614`, TDP) and `MSR_PKG_POWER_LIMIT` (`0x610`, PL1) are in POWER
  units and must be divided by `2^PSU`, PSU being bits 3:0 of `MSR_RAPL_POWER_UNIT`
  (`0x606`); the energy accumulator uses ESU, bits 12:8 of the same register. On the dev box
  ESU is 14 and PSU is 3, so skipping the division reports 224 W TDP and 512 W PL1 on a 28 W
  part — values that pass the "positive and under 1000 W" sanity guard and ship silently.
- **`BuildPayload` is the single mapping.** `BuildPayloadJson` (what tests pin) and
  `BuildPayloadUtf8` (what is sent) are two projections of it, pinned byte-identical by a
  test. Never duplicate the mapping into one of them.
- **A display address is only derived on a private network.** `DeriveDisplayAddress` returns
  null outside RFC 1918 / CGNAT / link-local. The push is cleartext, unauthenticated, and
  aimed at a *derived* address, so on a public IPv4 it would stream this machine's
  antivirus/firewall/reboot posture to a stranger. Widening `IsPrivateIPv4` re-opens that.
- **`LoggingService` collapses consecutive identical lines.** A handful of per-tick catch
  blocks (the NVAPI sensor reads, RAM, free disk) are not edge-triggered and would repeat
  at the 1 Hz sweep rate forever on a persistently broken sensor. The collapse applies the
  codebase's "one line per failure streak" rule to every call site at once — do not remove
  it in favour of trusting each call site to remember.
- **The push loop starts once per session**, gated by an `Interlocked` exchange. It starts
  when a GPU is detected — either by `GpuMonitorService.Initialize` (which waits up to
  30 s) or by the 5 s poll in `InitializeAndStartPushAsync` that covers a probe outlasting
  that wait. Do not remove that poll: with no menu timer, nothing else notices a late GPU.
- **The CPU and disk sensors live on the push loop, so no GPU means no sensors.** They are
  constructed and initialized inside `RunAsync`, which only runs once an NVIDIA GPU is found
  *and* a display has answered discovery; on a machine with neither, they never initialize.
  That is the design — they exist to fill fields in this datagram, and there is no datagram
  without a GPU — not a bug to be routed around with a second timer.
- **Refreshing the PawnIO assets is one atomic task.** `Resources/PawnIo/IntelMSR.bin`,
  `Resources/PawnIo/AMDFamily17.bin` and `Resources/PawnIo/COPYING` come from one module
  release and move together with `Resources/PawnIO_setup.exe`; the SHA-256 table in
  `README.md` is re-recorded in the same change. Partial refreshes are how the bundled
  installer drifts out of compatibility with the modules it has to load.

## Working here

Run `dotnet build --warnaserror` and `dotnet test` before considering work done; both must
pass cleanly. `.editorconfig` mandates CRLF line endings.

The services under `Services/` are a verbatim extraction carrying hard-won behavior
(handle-loss strike counting, latched legacy-API fallbacks, backend splits). Do not
"clean them up" — changes there risk changing what goes on the wire.
