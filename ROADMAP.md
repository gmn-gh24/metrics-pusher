# Roadmap

Candidate work for MetricsPusher, in rough priority order. Nothing here is committed —
this is the thinking that should happen before any of it is started. Anything that touches
the wire is measured against `push_metrics.md`, which stays authoritative.

Current state: sender v1.0.2, protocol `v: 1`, worst-case datagram 732 bytes against a
1024-byte receiver floor (292 bytes of slack).

---

## 1. Intel and AMD GPU support

**Verdict: yes, and the protocol does not have to change at all.** The hard part is not
the wire format or the metrics engine — it is one unresolved question about where vendor
DLLs live on disk. That question should be answered before any code is written.

### Why the wire is already ready

Every `gpu*` key is optional, and `push_metrics.md` §5 already defines a missing key as
"unknown / unavailable", never zero. It also already has the exact concept this needs:
**per-backend structural absence** — `watts` and `limitW` are permanently absent on
NVAPI-fallback senders, and that is documented as expected rather than as a fault. An Arc
or Radeon sender is therefore schema-identical to an NVIDIA one with a different subset of
keys present. `gpu` is free text, so the board name carries the vendor without a new field.

This is the same reason removing the NVIDIA gate in v1.0.2 needed no `v` bump. Adding
vendors is additive in the same sense: **no key is removed, renamed, retyped or
re-meaning'd, so protocol `v` stays `1`** and existing consumers need no change.

### The blocker to resolve first

`SystemLibraryResolver` pins every P/Invoked native library to an absolute `System32` path,
and `CA5392` is an **error** precisely so a new `DllImport` cannot reintroduce a searched
load. NVML and NVAPI qualify because NVIDIA installs them into `System32`.

**Before choosing any vendor SDK, check where its DLL actually lands on a clean driver
install** — `amdadlx64.dll` / `atiadlxx.dll` for AMD, `igcl_api.dll` or `ze_loader.dll` for
Intel. If one of them ships only inside the driver store rather than `System32`, supporting
it means deliberately widening the pinning rule, and that is a security decision about
load-order hijacking, not a mechanical one. Do not discover this halfway through an
implementation. This single check decides whether §1 is a weekend or a redesign.

### Suggested shape

Phase 1 is worth doing even if the vendor SDKs turn out to be awkward, because it needs no
new native dependency at all:

- **Vendor-neutral floor.** `IDXGIAdapter3::QueryVideoMemoryInfo` gives `vramUsed` /
  `vramTotal` for any adapter, and PDH's `GPU Engine` / `GPU Adapter Memory` counters give
  utilization. `pdh` is *already* pinned in `SystemLibraryResolver` and already used by
  `SystemMetricsService`, so this costs nothing new and covers integrated graphics too.
  Realistic output: `gpu`, `load`, `vramUsed`, `vramTotal` on essentially every machine.
- **Vendor backends for the rest.** Temperature, fan, power and clocks need the vendor
  library: ADLX for AMD (legacy ADL as a fallback for older drivers), IGCL or Level Zero
  Sysman for Intel.

Structurally, follow the pattern the codebase already has. `CpuTemperatureProviders.cs`
defines `ICpuTemperatureProvider` with Intel-MSR, AMD-SMN and ACPI implementations, and
`CpuTemperatureService` owns selection, latching and caching. GPU support should mirror it:
an `IGpuBackend` (initialize / try-read / dispose) with NVML, NVAPI, ADLX, IGCL and
DXGI+PDH implementations, while `GpuMonitorService` keeps what it already does well — the
`SampledMetric` cadence registry, the handle-loss strike counting, the 950 ms snapshot
cache. Today the NVML-primary/NVAPI-fallback choice is hardcoded; that is the only part
that has to become pluggable.

### Design questions that need answers, not defaults

- **Which GPU on a hybrid machine?** Intel iGPU plus NVIDIA dGPU is the common laptop
  configuration. §7.2 currently reports "the first GPU", which on such a machine would
  likely report the iGPU and quietly hide the one the user cares about. A documented
  preference order (discrete over integrated) is needed before multi-vendor ships.
- **Do not let `power` drift in meaning.** §4 already warns that NVML reports *board*
  power while the NVAPI fallback prefers the *chip* domain. AMD and Intel expose different
  domains again. The rule that keeps `cpuTemp` die-only applies here: if a vendor cannot
  produce the same physical quantity, omit the key rather than reuse it.
- **Budget.** 292 bytes of slack. A `gpuVendor` key is probably unnecessary — the board
  name already carries it — and would spend slack for nothing.
- **Testing.** The dev box is NVIDIA-only, so neither new backend can be validated here.
  The `IGpuBackend` seam is what makes them unit-testable at all; field validation goes to
  `WHATSLEFT.md` the way the GPU-less path did.

---

## 2. Recommended, in priority order

### 2.1 Re-discovery when the display's address changes

**This is the most likely real-world failure in the current design.** Discovery freezes the
endpoint for the session, and exhausting the 10 attempts disables the push until the app is
restarted. So a display that takes a new DHCP lease, or that comes online eleven minutes
after the PC did, is invisible until someone restarts the tray app — and because the push is
fire-and-forget, nothing surfaces the problem. The user experiences "it just stopped
working" with a healthy-looking tray icon.

Worth fixing with a slow re-probe (re-arm discovery after N consecutive sends with no
reply, or re-derive the address if the local IPv4 changes). Note the interaction with
`LocalNetworkService`: the adapter is deliberately resolved once, and the display address
derives from it, so "the local IP changed" and "the display moved" are the same event here.

### 2.2 A conformance test tying `push_metrics.md` to the DTO

`push_metrics.md` is authoritative for the wire, but nothing mechanically checks that the
code still matches it. That gap is not hypothetical: §7.1 asserted that a mid-session GPU
loss silences the sender entirely, which stopped being true when the CPU, NVMe and network
fields joined the live-metric set — the doc described a behavior the guard no longer had,
and only a manual read caught it.

A test that parses the §4 field table and asserts key names, wire order and nullability
against `GpuDisplayPayload` would turn that class of drift into a build failure. It also
directly protects the "adding a field means raising `MaxDatagramBytes` in the same change"
invariant.

### 2.3 Reproducible-build verification in CI

`CLAUDE.md` leans on byte-identical rebuilds as *the* integrity check for an unsigned
binary, but nothing verifies the property holds. A job that builds the same commit twice
from two different directories and diffs the exe would make it a check rather than a claim.
Pairs naturally with publishing a per-tag SHA-256 somewhere outside this repo, which is
where it has to live anyway since the exe embeds its own commit hash.

### 2.4 Explicit sleep/resume handling

`cpuWatts` (RAPL) and `netRx`/`netTx` both rely on the 0.5–2 s interval-acceptance band to
discard the tick that spans a sleep. That works, but it is an inference. A
`SystemEvents.PowerModeChanged` hook that explicitly invalidates the rate baselines on
resume is more direct, would remove a recurring `WHATSLEFT.md` entry, and gets more
valuable with every rate-derived field added.

### 2.5 Tray diagnostics

The menu holds only `Exit`, so diagnosing a missing field means finding and reading
`%LOCALAPPDATA%\MetricsPusher\logs\app.log`. A "Copy diagnostics" item that puts the
current provenance on the clipboard — which GPU backend won, which CPU temperature
provider, the NVMe tier, the selected adapter, the discovered display endpoint — would make
remote support tractable. Cheap, and it does not touch the wire.

### 2.6 Battery fields for laptops

A natural additive extension that fits the existing OS-posture theme (`av`, `fw`, `reboot`,
`win`): percentage and AC/charging state from `GetSystemPowerStatus`. No new native
dependency, and the budget has room. Additive, so protocol `v` stays `1` — but it still
means raising `MaxDatagramBytes` and re-pinning the worst-case test in the same change.

---

## Explicitly not planned

- **Shrinking the ~131 MB exe.** Self-contained publishing is a deliberate constraint
  (`CLAUDE.md`: the runtime ships inside the exe and the only patch path for a runtime CVE
  is rebuilding). Trimming fights the P/Invoke surface, and NativeAOT is not realistic with
  WinForms. The size is the cost of "no .NET prerequisite on the target machine".
- **Multi-GPU reporting.** §7.2 reports the first GPU on purpose. Reporting several would
  change the datagram from a flat record into a nested one, which is a protocol `v` bump
  for a case this app does not serve.
- **Encryption or authentication on the wire.** §10 rests on a trusted subnet, and the
  private-IPv4 restriction in `DeriveDisplayAddress` is what keeps that premise honest.
  Adding crypto to a 1 Hz fire-and-forget datagram aimed at an ESP32 would cost more than
  it buys; the existing answer is "do not send it off a private network at all".
