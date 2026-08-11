# MetricsPusher

A Windows tray application whose only job is to push hardware metrics to a display panel
on the local subnet: one JSON UDP datagram per second, fire-and-forget. There is a tray
icon and a menu containing nothing but `Exit`. No installer, no service, no autostart.

The metrics engine was extracted verbatim from `R:\Yupix\systray-app` (YupixTrayApp
v5.12.1). **The wire contract is unchanged from that app** — see `push_metrics.md`, which
is authoritative for anything on the wire.

## Commands

```powershell
dotnet build --warnaserror     # must be clean - StyleCop + Roslynator are enforced
dotnet test                    # 227 tests
dotnet test --filter "FullyQualifiedName~GpuDisplayPushServiceTests"

# Portable single-file exe (~150-160 MB, no prerequisites on the target machine)
dotnet publish MetricsPusher.csproj -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "publish"
```

Always publish `--self-contained` and use forward slashes in `-o`. A published exe near
3 MB means the publish silently fell back to framework-dependent.

Logs: `%LOCALAPPDATA%\MetricsPusher\logs\app.log` (10 MB, rotates to `.1`–`.3`).

## Layout

| Path | What it is |
|---|---|
| `Program.cs` | Elevation refusal, single-instance mutex, exception safety net |
| `TrayApplicationContext.cs` | The whole UI: icon, Exit item, and when the push loop starts |
| `Services/GpuDisplayPushService.cs` | Wire DTO, display discovery, the 1 Hz send loop |
| `Services/GpuMonitorService.cs` | GPU sensors: NVML primary, NVAPI fallback |
| `Services/NvmlService.cs` | `nvml.dll` P/Invoke layer |
| `Services/SystemMetricsService.cs` | CPU (PDH), RAM, disk, Windows version, AV/firewall/reboot |
| `Services/SampledMetric.cs` | Per-metric read cadences for the NVAPI fallback |
| `Services/LocalNetworkService.cs` | Local IPv4 selection, feeds address derivation |
| `push_metrics.md` | Authoritative UDP wire protocol. Update it in the same change as any wire-visible change |

## Constraints

- **x64 only.** NVAPI and NVML are 64-bit; `PlatformTarget` is pinned.
- **Never runs elevated.** `app.manifest` declares `asInvoker`; `Program.Main` refuses to
  run when `IsInRole(Administrator)` is true, checked *before* the single-instance mutex
  so an elevated launch leaves nothing behind.
- **One instance per session.** `Local\` mutex, not `Global\` — RDP and fast user
  switching each get their own tray icon.

## Invariants worth knowing before you change anything

- **`MetricsCacheTtlMs` (950) must stay below the 1 s send cadence.** It and
  `VramIntervalMs` / `LostSweepsBeforeDrop` move together — the comment above
  `LostSweepsBeforeDrop` in `GpuMonitorService.cs` spells out the false-drop window that
  opens if one moves alone.
- **Adding a wire field means raising `MaxDatagramBytes` and re-pinning the worst-case
  test in the same change.** The worst case (522) *equals* the ceiling by design; there
  is no slack. Only a total approaching 1024 reopens the receiver contract.
- **`NvmlService` is deliberately not thread-safe.** Every member must be called under
  `GpuMonitorService._lock`.
- **`BuildPayload` is the single mapping.** `BuildPayloadJson` (what tests pin) and
  `BuildPayloadUtf8` (what is sent) are two projections of it, pinned byte-identical by a
  test. Never duplicate the mapping into one of them.
- **The push loop starts once per session**, gated by an `Interlocked` exchange. It starts
  when a GPU is detected — either by `GpuMonitorService.Initialize` (which waits up to
  30 s) or by the 5 s poll in `InitializeAndStartPushAsync` that covers a probe outlasting
  that wait. Do not remove that poll: with no menu timer, nothing else notices a late GPU.

## Working here

Run `dotnet build --warnaserror` and `dotnet test` before considering work done; both must
pass cleanly. `.editorconfig` mandates CRLF line endings.

The services under `Services/` are a verbatim extraction carrying hard-won behavior
(handle-loss strike counting, latched legacy-API fallbacks, backend splits). Do not
"clean them up" — changes there risk changing what goes on the wire.
