# PawnIO CPU Temperature, CPU Power and NVMe Temperature — Implementation Plan

> **Status: approved, not yet implemented.** No production code exists for any of this yet;
> this document is the deliverable, and §8 is the checklist for the implementation session.
> Work happens on branch `feat/cpu-nvme-temperature`.
>
> Every factual claim is cited. Claims marked *measured* were verified on the dev box
> (ZENBOOK, Intel Core Ultra 7 155H, Windows 11) on 2026-08-11; claims marked **spike required**
> are explicitly unverified and are step 1 of §8.

---

## Context

MetricsPusher reports CPU name and CPU load but no CPU temperature. GPU temperature is on the
wire (`temp`); the CPU side has no equivalent because Windows exposes no unprivileged API for
die temperature — it lives in model-specific registers (Intel) and the AMD data-fabric SMN
address space, both of which need ring 0.

The classic answer, WinRing0, is unusable: Microsoft Defender began flagging it as
`Trojan:Win32/Vigorf.A` on 2025-09-04 and the driver is deprecated for exposing raw ring-0
primitives to userspace ([FanControl driver history](https://deepwiki.com/Rem0o/FanControl.Releases/5.4-driver-evolution-and-anti-virus-issues)).
The replacement is **PawnIO** — a signed kernel driver that executes *signed Pawn bytecode
modules*, so userspace gets narrow per-module IOCTLs instead of arbitrary `rdmsr`.
LibreHardwareMonitor swapped to it in [PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857),
merged 2025-09-16, shipped in v0.9.6.

**Intended outcome:** a `CpuTemperatureProvider` abstraction inside `Services/` that yields die
temperature when PawnIO is present and a degraded ACPI thermal-zone reading otherwise, at a cost
of one kernel round trip per second and zero steady-state allocations. The value is **not** put
on the UDP wire in this change — that is a separate commit with its own `push_metrics.md` work.

### Decisions taken up front

| Question | Decision |
|---|---|
| Elevation | **`requireAdministrator`.** `Program.Main`'s elevation refusal is deleted. |
| Integration | **Direct PawnIO IOCTL.** No LibreHardwareMonitorLib dependency. |
| Wire field | **Not yet.** Providers only; datagram untouched. |
| Prerequisite | **Bundle `PawnIO_setup.exe`**, prompt and install on first run when absent. |
| NVMe SSD temperature | **In scope** (§3.4a). Needs no driver and no elevation. Two-tier `IOCTL_STORAGE_QUERY_PROPERTY` probe; **no** vendor pass-through paths (Samsung, Intel RST). |
| CPU package power | **In scope** (§3.4b). RAPL energy delta, both vendors. Power *limit* is Intel-only. |
| Sequencing | **One combined commit** for CPU temp + CPU power + NVMe. |
| Branch | **`feat/cpu-nvme-temperature`**; `main` stays shippable. |

**Explicitly out of scope**, so this is not re-litigated later: Super I/O sensors via `LpcIO.p`
(motherboard temperatures, chassis fan RPM, board voltages) — judged overkill for the value;
per-core temperatures, per-core clocks and core voltage (the per-core IOCTL + affinity cost this
design exists to avoid); thermal-throttling flag decoding; effective CPU clock; Intel PSys
platform power; DIMM temperature via SMBus; PCH temperature; AMD SMCA error counters.

The elevation decision is the one with blast radius. It is stated plainly here so the follow-up
session does not have to re-derive it: MetricsPusher's documented "never runs elevated"
constraint is being **retired**, deliberately, because PawnIO's device ACL admits only SYSTEM
and elevated Administrators (§1.3). The costs — a UAC prompt on every launch, an admin token
held for the session by a process that pushes cleartext UDP, and standard-user accounts losing
the ability to launch the app at all — were presented and accepted.

---

## 1. Architecture decision record

### 1.1 How PawnIO works

PawnIO is a WDM kernel driver embedding a modified 64-bit Pawn abstract machine
([namazso/PawnIO](https://github.com/namazso/PawnIO)). Userspace does not get `rdmsr`; it gets
whatever IOCTLs a loaded *module* chooses to expose. Modules are Pawn source compiled to `.amx`
bytecode and **RSA-signed**; the driver refuses unsigned bytecode unless built with
`PAWNIO_UNRESTRICTED` ([architecture overview](https://deepwiki.com/namazso/PawnIO)).

Three layers: the driver (device creation, IRP dispatch), the AMX VM (Harvard-model, signature
verification), and native functions the VM calls (`msr_read`, `pci_config_read_dword`, …).

**The security property that matters here is module-level allow-listing.** From the module
sources:

- [`IntelMSR.p`](https://github.com/namazso/PawnIO.Modules/blob/master/IntelMSR.p) —
  `ioctl_read_msr` checks `is_allowed_msr_read(msr)` against a ~33-entry list and returns
  `STATUS_ACCESS_DENIED` otherwise. The list contains exactly the three registers this feature
  needs: `MSR_IA32_THERM_STATUS` (`0x19C`), `MSR_IA32_PACKAGE_THERM_STATUS` (`0x1B1`),
  `MSR_IA32_TEMPERATURE_TARGET` (`0x1A2`). `main()` gates on x64 + `CpuVendor_Intel`.
- [`AMDFamily17.p`](https://github.com/namazso/PawnIO.Modules/blob/master/AMDFamily17.p) —
  same allow-list pattern for MSRs, but `ioctl_read_smn` takes **any** offset with no
  allow-list (it only verifies the root complex is vendor `0x1022`). `main()` gates on x64 +
  `CpuVendor_AMD` + `family >= 0x17 && family <= 0x1A`, i.e. **Zen 1 through Zen 5**.

Other shipped modules — `RyzenSMU`, `LpcIO`, `SmbusI801`, `IntelPCHThermal`, `DellSMM`,
`Nvidia`, `AMDFamily0F/10`, `ZhaoxinMSR`, `ARMMSR` — are irrelevant here. **CPU temperature
needs `IntelMSR` and `AMDFamily17` only.**

### 1.2 Chosen stack: direct PawnIO IOCTL

Three candidates were evaluated.

**(A) LibreHardwareMonitorLib 0.9.6 — rejected.** Correct and well-maintained, but three
disqualifying costs for this codebase:

1. *Dependency weight.* The package pulls `HidSharp`, `System.Management`, `System.IO.Ports`,
   `DiskInfoToolkit` and `RAMSPDToolkit-NDD`
   ([NuGet listing](https://www.nuget.org/packages/LibreHardwareMonitorLib/)). This repo pins
   its whole transitive closure in `packages.lock.json` with `NuGetAuditMode=all`; that goes
   from 3 direct packages to a dozen-plus, and `System.Management` reintroduces WMI, which
   `SystemMetricsService`'s design comment explicitly avoids.
2. *Per-update cost.* `IntelCpu.Update()` calls `System.Threading.Thread.Sleep(1)` **once per
   core** inside its core-clock loop (`LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs:639`),
   plus a `ThreadAffinity.Set` and an IOCTL per core. On a 16-core part that is ~16 ms of
   blocking and 30+ syscalls per `Update()`. There is no public switch to disable clock
   sensors — `Computer.IsCpuEnabled` is all-or-nothing. Construction is worse: `GenericCpu`
   busy-spins up to 5 × 25 ms estimating TSC frequency, and `CpuGroup` probes `CpuId.Get` over
   192 thread slots per processor group.
3. *Licensing.* LHM is MPL-2.0. Bundling it into a single-file exe is permitted but carries
   source-availability obligations that this repo does not currently have to meet.

**(B) LHM's `PawnIo`/`IntelMsr`/`AmdFamily17` types only — rejected.** These are `public` and
would supply the IOCTL plumbing and the embedded signed modules for free, but referencing the
package drags in every transitive dependency regardless of which types are used. Cost (1)
stands undiminished for a gain of roughly 150 lines.

**(C) Direct IOCTL — chosen.** The entire surface needed is:

- `CreateFileW(@"\\?\GLOBALROOT\Device\PawnIO", …)`
- `DeviceIoControl(h, IOCTL_PIO_LOAD_BINARY, <module bytes>, …)`
- `DeviceIoControl(h, IOCTL_PIO_EXECUTE_FN, <32-byte fn name || long[] in>, long[] out, …)`

Both imports are from `kernel32`, a KnownDLL, so **no new entry in
`SystemLibraryResolver.GuardedLibraries`** is required (per the rule in `CLAUDE.md`), and
`[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]` in `Program.cs:9`
already satisfies `CA5392`, which is an error here. `PawnIOLib.dll` is bypassed entirely — one
fewer native library to pin.

This also matches the codebase's established idiom exactly: `SystemMetricsService` is
hand-rolled PDH/kernel32 P/Invoke with no WMI, and `NvmlService` is a hand-rolled P/Invoke
layer under a service that owns the locking.

**Licence note:** PawnIO is GPL-2.0-or-later *with an explicit exception* for "independent
modules that communicate with PawnIO solely through the device IO control interface"
([README](https://github.com/namazso/PawnIO/blob/master/README.md)). Direct IOCTL is precisely
that case, so MetricsPusher stays unencumbered. The two `.bin` modules are LGPL-2.1-or-later
and are redistributed unmodified — ship `COPYING` alongside them, as LHM does.

### 1.3 Why elevation is unavoidable on the chosen path

`PawnIO.inf.in` sets the device's DACL:

```ini
[@PAWNIO_NAME@_Device.NT.HW]
AddReg = Custom_Security

[Custom_Security]
HKR,,Security,,"D:P(A;;GA;;;SY)(A;;GA;;;BA)"
```

([source](https://github.com/namazso/PawnIO/blob/master/PawnIO/PawnIO.inf.in)) — protected DACL,
GENERIC_ALL for `SY` (SYSTEM) and `BA` (Builtin Administrators), **nothing else**. A
non-elevated process gets `ERROR_ACCESS_DENIED` from `CreateFile`.

The failure is silent, which matters for diagnostics: LHM's own wrapper returns a `PawnIo`
instance with a null handle, and every subsequent `Execute` returns a zero-filled array rather
than throwing (`LibreHardwareMonitorLib/PawnIo/PawnIo.cs:75-90,120`). **Our implementation must
distinguish "driver absent", "access denied", and "module rejected this CPU" explicitly** — see
§3.4.

PawnIO 2.2.0 added an opt-in to expose the device to non-administrators ("although not
recommended", [release notes](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0)).
That path was considered and **rejected**: it widens a kernel device ACL machine-wide and
permanently for every local process, and `ioctl_read_smn` has no offset allow-list, so on AMD it
would hand any local program a read window into the SoC fabric. Elevating one app is the
narrower exposure.

---

## 2. Dependency manifest

### 2.1 NuGet

**None added.** `MetricsPusher.csproj` keeps `NvAPIWrapper.Net`, `Roslynator.Analyzers`,
`StyleCop.Analyzers`. `packages.lock.json` must be byte-identical after this change — verify
after both a plain `dotnet build` and a `dotnet publish`, per the existing csproj comment.

### 2.2 Binary assets committed to the repo

| Asset | Source | Version | Licence |
|---|---|---|---|
| `Resources/PawnIo/IntelMSR.bin` | [`PawnIO.Modules` releases](https://github.com/namazso/PawnIO.Modules/releases) → `release_0_2_10.zip` | 0.2.10 (2026-07-27) | LGPL-2.1-or-later |
| `Resources/PawnIo/AMDFamily17.bin` | same archive | 0.2.10 | LGPL-2.1-or-later |
| `Resources/PawnIo/COPYING` | same archive | — | LGPL-2.1 text |
| `Resources/PawnIO_setup.exe` | [`PawnIO.Setup` releases](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) | 2.2.0 (2026-03-15) | GPL-2.0+ |

All four go in as `<EmbeddedResource>`. 0.2.10 is the same module release LHM master embeds
(`LibreHardwareMonitorLib/Resources/PawnIo/README`), which is the best available signal that
these blobs are current and known-good.

Record each asset's SHA-256 in `README.md` next to its upstream URL. The published exe's own
SHA-256 changes whenever these blobs change — consistent with the existing reproducibility note
in `CLAUDE.md`.

### 2.3 PawnIO runtime facts

| Property | Value | Source |
|---|---|---|
| Device path | `\\?\GLOBALROOT\Device\PawnIO` | `PawnIo.cs:67` |
| Device type | `41394` (`0xA1B2`) | `pawnio_um.h` |
| `IOCTL_PIO_LOAD_BINARY` | `0xA1B22084` = `(41394<<16) \| (0x821<<2)` | `pawnio_um.h` |
| `IOCTL_PIO_EXECUTE_FN` | `0xA1B22104` = `(41394<<16) \| (0x841<<2)` | `pawnio_um.h` |
| `IOCTL_PIO_VERSION` | `0xA1B22184` = `(41394<<16) \| (0x861<<2)` | `pawnio_um.h` |
| Service name | `PawnIO` — kernel driver, `StartType=3` (demand start) | `PawnIO.inf.in` |
| Device class | `SoftwareDevice`, root-enumerated `Root\PawnIO`, `PnpLockdown=1` | `PawnIO.inf.in` |
| Minimum OS | `10.0...17763` → **Windows 10 1809 (RS5)** | `PawnIO.inf.in` |
| Presence key | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` → `DisplayVersion` | `PawnIo.cs:25-41` |

**Execute wire format** (`PawnIo.cs:98-121`): input buffer is a 32-byte ASCII function name
(NUL-padded, name truncated to 31 chars) followed by the `long[]` inputs; output is `long[]`.
`METHOD_BUFFERED`, so the kernel copies — no pinning required for correctness, only for
allocation avoidance.

### 2.4 Installer command line

```powershell
# Bundled path (what the app runs; already elevated, so no extra UAC prompt)
PawnIO_setup.exe -install -silent

# Documented manual alternatives for the README
winget install --id namazso.PawnIO --exact --silent --accept-package-agreements --accept-source-agreements
```

Confirmed flags: `-install`, `-silent`, `-unrestricted` (loads unsigned modules — **never use**).
Exit codes are DOS errors as of 2.2.0; silent mode returns `ERROR_SUCCESS_REBOOT_REQUIRED`
(3010) when a restart is needed. 2.2.0 upgrades in place from 2.1.0 without an uninstall.

> **Spike required (checklist step 1).** The full flag list is not published. Run
> `PawnIO_setup.exe -?` and record the real output in the README. Also determine empirically
> whether a clean install returns 3010 on Windows 11 — the reboot path drives §3.5's UX.

### 2.5 Runtime detection logic

Three independent probes, in order, because they fail differently:

1. **Installed?** `Version.TryParse` on the `DisplayVersion` value above, checking both the
   native view and `RegistryView.Registry64` (LHM checks both — `PawnIo.cs:33-40`). Cheap,
   registry-only, no device handle. Drives the install prompt.
2. **Openable?** `CreateFileW` on the device path. `ERROR_FILE_NOT_FOUND` → driver not started;
   `ERROR_ACCESS_DENIED` → not elevated (should be impossible post-change, so log it loudly as
   a manifest regression).
3. **Module accepted?** `IOCTL_PIO_LOAD_BINARY` returns failure when the module's `main()`
   returns `STATUS_NOT_SUPPORTED` — i.e. wrong vendor, wrong family, or 32-bit. This is the
   normal negative result on an AMD family 0x10–0x16 part and must not be logged as an error.

---

## 3. Component design

### 3.1 File layout

| New file | Responsibility |
|---|---|
| `Services/PawnIoDevice.cs` | P/Invoke layer: open device, load a module, execute a function. One instance per loaded module. `IDisposable`. Deliberately **not** thread-safe — same contract as `NvmlService`. |
| `Services/CpuTemperatureService.cs` | Owns provider selection, the lock, the cache, and edge-triggered logging. The only type the rest of the app talks to. |
| `Services/CpuTemperatureProviders.cs` | `ICpuTemperatureProvider` + the three implementations. |
| `Services/PawnIoInstaller.cs` | Presence detection, the one-time prompt, extracting and running the bundled setup. |
| `Resources/PawnIo/*` | The four embedded assets from §2.2. |

Mirrors the existing split: `NvmlService` (P/Invoke) under `GpuMonitorService` (locking,
cadence, caching, logging).

### 3.2 Provider interface

```csharp
internal interface ICpuTemperatureProvider : IDisposable
{
    /// <summary>Source of the reading, for logging and a future wire field.</summary>
    CpuTemperatureSource Source { get; }

    /// <summary>Reads once. False = unavailable this call (never throws).</summary>
    bool TryRead(out float celsius);
}

internal enum CpuTemperatureSource { None, IntelPackageMsr, AmdTctlSmn, AcpiThermalZone }
```

`Source` is carried from day one even though nothing consumes it yet: an ACPI zone reading and a
die reading are different physical quantities, and a future `cpuTemp` wire field must not blur
them (see §5's note on the follow-up commit).

### 3.3 Primary providers

**`IntelMsrTemperatureProvider`** — loads `IntelMSR.bin`.

- *Init, once:* read `IA32_TEMPERATURE_TARGET` (`0x1A2`); `tjMax = (eax >> 16) & 0xFF`. Fall
  back to `100 °C` if the read fails, which is what LHM does
  (`IntelCpu.cs:544-556`). Clamp to a sane 60–130 band and reject the reading otherwise.
- *Per poll:* read `IA32_PACKAGE_THERM_STATUS` (`0x1B1`). Bit 31 must be set (reading valid);
  `deltaT = (eax & 0x007F0000) >> 16`; **`celsius = tjMax − deltaT`**.
- *Fallback within the provider:* if `0x1B1` is unsupported (pre-Nehalem), read
  `IA32_THERM_STATUS` (`0x19C`) with affinity pinned to core 0 and use the same decode.

Per-core temperatures are deliberately **not** read. The package register is a single MSR read
with no thread-affinity juggling, and the app reports one number. This is where most of the
saving over LHM comes from.

*Per-generation quirks handled:* modern parts (Nehalem onward, including Meteor Lake / Arrow
Lake / Lunar Lake / Panther Lake) all derive TjMax from `0x1A2`; LHM's large `switch` exists only
to hardcode TjMax for pre-Nehalem models (`IntelCpu.cs:85-260`), which this app does not need to
support — those CPUs predate Windows 10 1809, PawnIO's floor. On hybrid parts, P-cores and
E-cores may report different `0x1A2` values; the package register pairs with the package target,
so reading `0x1A2` unpinned is correct for the package reading.

**`AmdSmnTemperatureProvider`** — loads `AMDFamily17.bin`. Covers family `0x17`–`0x1A`
(Zen 1–Zen 5); the module's `main()` rejects anything else, so no C#-side family table is needed.

- *Per poll:* `ioctl_read_smn(0x00059800)` — `THM_TCON_CUR_TMP` on the data fabric.
- *Decode* (`Amd17Cpu.cs:273-293`): `raw = (value >> 21) * 125` → milli-°C, so
  `tctl = raw / 1000f`. If `(value & 0x80000) != 0` (`RANGE_SEL`) **or**
  `(value & 0x30000) == 0x30000` (`TJ_SEL`), subtract 49 °C.
- *Tdie:* Tctl minus a per-SKU offset — `−20` for 1600X/1700X/1800X, `−27` for Threadripper
  19xx/29xx, `−10` for 2700X, otherwise `0`. Zen 2 and later have no offset, so Tctl **is** Tdie.
  Reuse `SystemMetricsService`'s already-cached CPU name for the match rather than re-reading the
  registry.
- *Report Tdie*, since that is the physical die temperature; Tctl on the affected first-gen SKUs
  is a deliberately inflated fan-control number.

**Per-CCD temperatures are not read.** `0x59954` (most parts) / `0x59B08` (models `0x61`
Raphael and `0x44` Granite Ridge), stride 4, up to 8 CCDs — 8 extra IOCTLs per poll for a
per-die breakdown this app has no field for. The design note belongs in the code comment so a
future change knows where to look, but the reads stay out.

**PCI bus mutex.** `ioctl_read_smn` writes an index to PCI config `0x60` then reads data from
`0x64` on device `0:0.0` — a shared index/data pair. The module's own doc comment says to hold
`\BaseNamedObjects\Access_PCI` first. Take `Global\Access_PCI` with a 10 ms timeout around each
SMN read, matching LHM (`Mutexes.cs`, `Amd17Cpu.cs:186`). Creating a `Global\` object needs
`SeCreateGlobalPrivilege`, which the now-elevated process holds — one of the few things
elevation makes *easier*. Create it with a World-FullControl DACL so unelevated tools
(HWiNFO, an unelevated LHM) can still interoperate. If the wait times out, skip the tick.

### 3.4 Fallback provider — ACPI thermal zone via **PDH**, not WMI

The brief proposed `MSAcpi_ThermalZoneTemperature` (`root\WMI`) or
`Win32_PerfFormattedData_Counters_ThermalZoneInformation`. Measured on the dev machine
(ZENBOOK, Intel Core Ultra 7 155H, **non-elevated** token):

| Route | Result |
|---|---|
| `root\WMI` → `MSAcpi_ThermalZoneTemperature` | **Access denied.** Also documented as returning `Unsupported` in VMs and on many desktops. |
| `root\CIMV2` → `Win32_PerfFormattedData_...ThermalZoneInformation` | Worked. `\_TZ.THRM`, `HighPrecisionTemperature = 3432` |
| **PDH** → `\Thermal Zone Information(\_TZ.THRM)\High Precision Temperature` | Worked. `3342` → **61.05 °C**, and it tracked live between samples |

**Use PDH.** It reads the same counter set the WMI perf class projects, but through
`pdh.dll` — which `SystemMetricsService` already P/Invokes and which is already in
`SystemLibraryResolver.GuardedLibraries`. No `System.Management`, no WMI provider host
spin-up, no new dependency, and it honours the "without WMI" design note on
`SystemMetricsService`. The elevation change would now make the `root\WMI` class readable, but
that is not a reason to take on a NuGet package and a WMI round trip for the same number.

- Counter path: `\Thermal Zone Information(*)\High Precision Temperature`, added with
  `PdhAddEnglishCounterW` so it is language-independent — the same call `SystemMetricsService`
  uses for `% Processor Utility`.
- Units are **deci-Kelvin**: `celsius = value / 10.0 − 273.15`.
- Wildcard instance: enumerate and take the **maximum** across zones, or `\_TZ.THRM` when it is
  the only one. Validate into 0–125 °C and drop anything outside.

**Documented reliability limits** (these belong in the README, not just the code):

- It is an **ACPI thermal zone**, i.e. a board/platform sensor the firmware chose to expose —
  *not* the CPU die. Expect it to read low and lag under load.
- Many desktops expose **no** `\_TZ` object at all; the counter set is then empty and the
  provider reports nothing. That is the expected outcome, not a fault.
- VMs generally expose nothing.
- Some firmware reports a **constant** — a plausible-looking value that never moves. There is
  no reliable programmatic way to distinguish that from a genuinely stable idle temperature;
  the mitigation is documentation, and the `Source` discriminator so a consumer knows what it
  is looking at.

### 3.4a NVMe SSD temperature — no driver required

Added after the CPU work was scoped. **This does not go through PawnIO** and is independent of
every decision above, including elevation.

Windows exposes storage-device temperature directly through `IOCTL_STORAGE_QUERY_PROPERTY`
([Working with NVMe drives](https://learn.microsoft.com/windows/win32/fileio/working-with-nvme-devices#temperature-queries),
Windows 10 / Server 2016 and later).

**Why no elevation is needed** — this is the load-bearing detail, and it is documented rather
than folklore. The "caller must have administrative privileges" rule applies to *direct access
(DASD read/write)* handles. [`CreateFile`'s Physical Disks and Volumes
section](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)
carves out the exemption:

> The *dwDesiredAccess* parameter **can be zero, allowing the application to query device
> attributes without accessing a device**. … It can also be used for reading statistics without
> requiring higher-level data read/write permission.

`IOCTL_STORAGE_QUERY_PROPERTY` is declared `FILE_ANY_ACCESS`, so the I/O manager demands no
read/write right on the handle. Open with
`CreateFileW(path, dwDesiredAccess: 0, FILE_SHARE_READ | FILE_SHARE_WRITE, OPEN_EXISTING)`.
Measured here: the convenient WMI equivalent (`Get-StorageReliabilityCounter`) *is*
access-denied unelevated, so the zero access mask is the entire trick.

**Two-tier probe, decided once at init, on the same handle.** Some drivers implement one path
and not the other.

1. **`StorageDeviceTemperatureProperty` (= 52)** → [`STORAGE_TEMPERATURE_DATA_DESCRIPTOR`](https://learn.microsoft.com/en-us/windows/win32/api/WinIoctl/ns-winioctl-storage_temperature_data_descriptor)
   holding [`STORAGE_TEMPERATURE_INFO`](https://learn.microsoft.com/en-us/windows/win32/api/WinIoctl/ns-winioctl-storage_temperature_info)
   entries whose `Temperature` is a **signed value already in °C**. Protocol-agnostic — SATA
   devices can answer too. Preferred: no decoding.
2. **`StorageDeviceProtocolSpecificProperty` (= 50)** with `ProtocolTypeNvme`,
   `NVMeDataTypeLogPage`, `ProtocolDataRequestValue = 0x02` (SMART / Health Information) →
   composite temperature at **log bytes 1–2, little-endian Kelvin**. `°C = K − 273.15`.
3. Otherwise latch to unavailable and log one line.

**Three specification traps** that will silently produce garbage if missed:

- `STORAGE_PROPERTY_ID` is **not sequential** — it jumps to 48 partway through the enum. Take
  50 and 52 from the header, do not count entries.
- `ProtocolDataLength` must be **≥ 512** for log-page requests; the docs state this twice.
- `ProtocolDataOffset` is relative to the start of the embedded
  `STORAGE_PROTOCOL_SPECIFIC_DATA`, **not** to the start of the output buffer.

**Validation: reuse `Constants.IsValidTemperature`** (`Constants.cs:60`, 0–150 °C — the same
band the wire contract applies to GPU `temp`). It already rejects NaN/∞ and, usefully here, the
never-reported `0 K` case, which decodes to −273 °C. Do not write a second validator; the CPU
providers should use it too.

**Which disk.** The app already reports the **system** volume (`diskFree`/`diskTotal` derive
from `Environment.SystemDirectory`'s root in `SystemMetricsService.OpenSystemDrive`). Keep them
consistent: resolve `\\.\C:` → `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS` → `DiskNumber` →
`\\.\PhysicalDriveN`, rather than blindly probing `PhysicalDrive0`. Cache the resolved path for
the session; only the temperature query repeats.

**Cost:** one `DeviceIoControl` per poll on a handle opened once, into a preallocated buffer —
the same budget as the CPU providers (§4). Validate into 0–125 °C; drop `Temperature` values of
`0` or `SHRT_MIN`, which some controllers use for "not reported".

**Provider shape:** a fourth, *independent* provider — not part of the CPU chain:

```csharp
internal enum CpuTemperatureSource { None, IntelPackageMsr, AmdTctlSmn, AcpiThermalZone }
// separate type, separate lifetime:
internal sealed class NvmeTemperatureProvider : IDisposable { bool TryRead(out float celsius); }
```

It needs neither PawnIO nor elevation, so it *could* ship on its own — but per the sequencing
decision it lands in **one combined commit** with the CPU work. Consequence to accept knowingly:
this low-risk sensor is then gated behind every PawnIO unknown (undocumented installer flags,
the Defender heuristic test, the elevation regression). If Phase 0's spikes go badly, splitting
the commit is the escape hatch — the code is already separate, only the commit boundary moves.

*Known limits:* RAID/HBA controllers and some USB-bridged drives return
`ERROR_INVALID_FUNCTION`; VMs generally report nothing; a drive behind Intel RST in RAID mode
may not answer. All are ordinary negatives — latch and fall silent, same discipline as §3.5.

### 3.4b CPU package power (RAPL)

Same device handle and same loaded module as the temperature providers — **one extra
`DeviceIoControl` per second**, no new module, no new dependency.

Both vendors expose a free-running **energy accumulator**, not a wattage. Power is the delta
over elapsed time, exactly as LHM computes it (`Amd17Cpu.cs:255-271`):

```
watts = (Δenergy_raw × energyUnit) / Δseconds
```

| | Units register (read once) | Energy register (per tick) | Limit |
|---|---|---|---|
| Intel | `MSR_RAPL_POWER_UNIT` `0x606` — ESU in bits 12:8, `energyUnit = 1 / 2^ESU` J (ESU 14 → 61 µJ) | `MSR_PKG_ENERGY_STATUS` `0x611`, bits 31:0 | `MSR_PKG_POWER_INFO` `0x614` bits 14:0 = TDP; `MSR_PKG_POWER_LIMIT` `0x610` bits 14:0 = PL1 |
| AMD | `MSR_PWR_UNIT` `0xC0010299` — ESU in bits 12:8 (ESU 16 → 15.3 µJ) | `MSR_PKG_ENERGY_STAT` `0xC001029B`, bits 31:0 | **none** |

Three things that will produce wrong numbers if missed:

- **The accumulator is 32-bit and wraps.** At ~250 W with 15.3 µJ units it wraps roughly every
  **263 seconds**, so this is routine, not an edge case. Use unsigned delta arithmetic —
  `pwr = last <= now ? now - last : (0xFFFFFFFF - last) + now`. A single wrap between two 1 Hz
  samples is impossible at any real power level, so no ambiguity arises.
- **The first tick cannot produce a value.** Two samples are required. The codebase already has
  this shape — `SystemMetricsService.PrimeCpuCounter` exists so the PDH rate counter is primed
  before the loop starts. Prime the energy counter in the same place, for the same reason.
- **Measure Δt with `Stopwatch.GetTimestamp()`**, not the nominal 1 s tick. `PeriodicTimer` drifts,
  and dividing by an assumed interval turns jitter directly into power error.

**The power *limit* is Intel-only.** `AMDFamily17.p`'s allow-list contains `MSR_PWR_UNIT`,
`MSR_CORE_ENERGY_STAT` and `MSR_PKG_ENERGY_STAT` — no package power-limit register. On AMD the
limit lives behind the SMU (`RyzenSMU.p`), which is out of scope. This mirrors a pattern the wire
contract already documents: `watts` and `limitW` are structurally absent on the GPU's NVAPI
fallback (`push_metrics.md` §5). Treat an absent CPU limit the same way — a structural absence,
not a failure, and never rendered as an error.

*Validation:* reject non-positive or absurd wattage (> 1000 W) and any `Δt` outside 0.5–2 s,
which mirrors the guard `GenericCpu.Update` uses on its TSC window.

### 3.5 Selection, health and degradation

`CpuTemperatureService` owns the state machine, mirroring `SystemMetricsService.PdhState`:

```
NotInitialized → probe once →
    Intel CPU + PawnIO device opens + IntelMSR.bin loads  → IntelPackageMsr
    AMD   CPU + PawnIO device opens + AMDFamily17.bin loads → AmdTctlSmn
    otherwise, PDH thermal zone counter creates            → AcpiThermalZone
    otherwise                                              → Failed (never retried)
```

- **Vendor detection** comes from the CPU name `SystemMetricsService` already caches
  (`_cpuName`), not a new CPUID path — the module's `main()` is the authoritative gate anyway,
  so a name-based guess only picks which module to *try* first. Try the other one if the first
  is rejected.
- **Structural failures latch.** No PawnIO, no thermal zone, module rejected → `Failed`, and
  the poll becomes a single field read forever after. Same discipline as
  `SystemMetricsService._wscUnavailable` and `PdhState.Failed`.
- **Transient failures self-heal.** A failed IOCTL or an invalid-bit-31 reading returns `null`
  for that tick and is retried next tick.
- **Logging is edge-triggered**, one line per failure streak, matching
  `SystemMetricsService.NoCpuValueThisTick`. `LoggingService` also collapses consecutive
  identical lines, but per `CLAUDE.md` that safety net is not a substitute for edge-triggering
  at the call site.
- **No new thread and no new timer.** The read happens on the existing 1 Hz push tick in
  `GpuDisplayPushService.RunAsync`, alongside `SystemMetricsService.GetSystemMetrics()`.

### 3.6 Installer flow (`PawnIoInstaller`)

On startup, after the single-instance mutex and before the push loop:

1. Registry probe (§2.5 step 1). Present → done.
2. Absent → check a one-time marker at
   `HKCU\Software\MetricsPusher\PawnIoInstallDeclined`. Set → done, never ask again.
3. Otherwise show a `MessageBox` (Yes/No) explaining what PawnIO is, that it is a signed
   third-party kernel driver, and that declining only costs CPU die temperature.
4. **No** → write the marker, continue with the fallback provider.
5. **Yes** → extract the embedded `PawnIO_setup.exe` to
   `%LOCALAPPDATA%\MetricsPusher\` and run `-install -silent`, waiting with a timeout.
   The process is already elevated, so no second UAC prompt.
   - exit `0` → re-probe and proceed.
   - exit `3010` (`ERROR_SUCCESS_REBOOT_REQUIRED`) → tell the user a restart is needed, use the
     fallback for this session, do **not** set the declined marker.
   - anything else → log the code, use the fallback, set the marker so it is not retried every
     launch.
6. Delete the extracted file afterwards.

Extraction target is a per-user path the elevated process can write; do not extract next to the
exe, which may be read-only or on removable media.

### 3.7 Changes to existing files

| File | Change |
|---|---|
| `app.manifest` | `requestedExecutionLevel` `asInvoker` → `requireAdministrator`. Rewrite the comment above it — it currently describes the passive half of a no-admin rule that no longer exists. |
| `Program.cs` | Delete `IsElevated()`, `IsUacDisabled()`, `IsUacDisabledValue()`, `UacPolicyKey`, and the refusal block with its two `MessageBox` strings. Add the `PawnIoInstaller` call after the mutex. |
| `MetricsPusher.Tests/ProgramTests.cs` | Delete the three `IsUacDisabledValue_*` tests (they test a method that ceases to exist). |
| `Services/SystemMetricsService.cs` | Add `float? CpuTemperature`, `int? CpuPowerWatts`, `int? CpuPowerLimitWatts` and `float? NvmeTemperature` to `SystemMetrics`, populated from the new services. **Deliberately not mapped in `BuildPayload`** — one comment covering all four, so nobody "fixes" the omission before the wire commit. |
| `MetricsPusher.csproj` | Four `<EmbeddedResource>` entries. No `PackageReference` changes. |
| `CLAUDE.md` | Rewrite the "Never runs elevated" constraint; add PawnIO to Layout and Constraints. |
| `README.md` | PawnIO prerequisite, what elevation now means for the user, asset SHA-256s, fallback limits. |
| `push_metrics.md` | **No change.** No wire-visible behaviour changes in this commit. |

---

## 4. Resource-impact budget

Targets, per 1 Hz poll:

| Metric | Target | How it is met |
|---|---|---|
| Kernel round trips | **3** | Two on the PawnIO handle — temperature (`0x1B1` Intel / SMN `0x59800` AMD) and package energy (`0x611` / `0xC001029B`) — plus one on the storage handle for NVMe. TjMax, RAPL units, the power limit and the CPU name are all read once at init. No per-core loop anywhere. |
| Thread-affinity changes | **0** | Package MSR and SMN are both package/root-complex scope. Only the pre-Nehalem `0x19C` fallback needs pinning, and that path is unreachable on supported OSes. |
| `Thread.Sleep` | **0** | The specific thing rejected from LHM's `IntelCpu.Update()`. |
| New threads / timers | **0** | Reads on the existing `PeriodicTimer` tick. |
| Steady-state allocations | **0 bytes** | Preallocate the 32+8-byte input and 8-byte output buffers per provider in the constructor and reuse; `stackalloc` inside `TryRead` where it fits. This is where LHM's `Execute` is wasteful — it allocates four arrays per call — and where the AMD path's `.Where().ToArray()/.Max()/.Average()` LINQ per update is avoided by not reading CCDs at all. |
| Mutex traffic | 1 wait+release, AMD only | 10 ms timeout; skip the tick on timeout. |

The zero-allocation target matters more here than it would elsewhere: the csproj sets
`ConcurrentGarbageCollection=false` and `ServerGarbageCollection=false`, so allocation directly
buys foreground GC pauses in a process whose whole design point is invisibility.

**One-time cost** (startup, off the push loop): registry probe, two `CreateFile` calls, one
`IOCTL_PIO_LOAD_BINARY` with a few-KB blob, and the init reads (`0x1A2`, RAPL units, power
limit, NVMe tier selection). Plus one primed energy sample, alongside the existing
`PrimeCpuCounter`. Well under the 30 s GPU-probe window the push loop already waits on.

### Verifying 0.0% CPU

1. **Baseline first.** Measure the current build for 10 minutes, then the new build under the
   same conditions. The delta is the number that matters; the absolute is dominated by the
   existing GPU/PDH work.
   ```powershell
   Get-Counter '\Process(MetricsPusher)\% Processor Time' -SampleInterval 5 -MaxSamples 120 |
     ForEach-Object { $_.CounterSamples[0].CookedValue }
   ```
2. **Allocation rate** — the sharper instrument, since a 1 Hz workload hides in CPU% noise:
   ```powershell
   dotnet-counters monitor --process-id <pid> --counters System.Runtime
   ```
   Watch `alloc-rate` and `gen-0-gc-count`. **Acceptance: `gen-0-gc-count` must not increase
   over a 10-minute idle run relative to baseline.**
3. **Syscall count** — a WPR/`xperf` trace, or Process Monitor filtered to `DeviceIoControl` on
   the PawnIO device, confirming exactly one per second.
4. Repeat both on an AMD box, where the mutex and SMN path differ.

---

## 5. Deployment plan

- **Prerequisite:** PawnIO 2.2.0, bundled and installed on first run (§3.6) — **for CPU
  temperature only**. NVMe temperature needs no driver, no prerequisite and no elevation, so it
  works on a stock machine the moment the app runs. No change to the app's own "no installer, no
  service, no autostart" shape — MetricsPusher itself is still a copy-and-run exe.
- **Elevation:** `requireAdministrator`. The user sees a UAC prompt on every launch. Say this
  plainly in the README's install section, next to the existing "where to install it and why" —
  and say why, because an app that previously *refused* admin now demanding it is exactly the
  kind of change that reads as a compromise if unexplained.
- **Standard-user machines are now unsupported.** A user without admin credentials cannot start
  the app at all. This is a hard regression and belongs in the README and the release notes.
- **Code signing:** MetricsPusher stays unsigned; reproducible builds remain the integrity
  story. `PawnIO.sys` is signed by its author — that signature is what lets it load, and we do
  not touch it.
- **Enterprise:** PawnIO is a legitimately signed, non-blocklisted driver, but it *is* a
  scriptable ring-0 driver. Environments running WDAC in enforcement will need it in policy.
  Note that FACEIT anti-cheat blocks PawnIO's signer outright
  ([FACEIT issue](https://github.com/namazso/PawnIO.Setup/issues/1)), so the two cannot coexist.
- **Follow-up commit (not this change):** adding a `cpuTemp` wire field. It is a key addition,
  so protocol `v` stays `1` — but per `CLAUDE.md` it requires raising `MaxDatagramBytes`,
  re-pinning the worst-case test in the same commit, and updating `push_metrics.md` §3.1
  (introduction history), §3.3 (budget table), §4 (field reference), §5 (absence semantics),
  §6 (cadence/staleness), §8.3 (typed model), §8.4 (display semantics) and §9 (conformance
  checklist). The `CpuTemperatureSource` discriminator should inform how §5 documents absence,
  since an ACPI-zone reading and a die reading are not interchangeable.

---

## 6. Testing matrix

**Unit (xUnit, in `MetricsPusher.Tests`) — pure decode functions, no hardware:**

| Case | Expectation |
|---|---|
| Intel: `0x1A2 = 0x00640000` | `tjMax == 100` |
| Intel: `0x1B1` with bit 31 clear | reading rejected |
| Intel: `tjMax=100`, `deltaT=0x1E` | `70 °C` |
| AMD: `raw` with `RANGE_SEL` set | `−49 °C` applied |
| AMD: `TJ_SEL == 0x30000` | `−49 °C` applied |
| AMD: `1800X` name | Tdie = Tctl − 20 |
| AMD: `9950X` name | Tdie == Tctl |
| PDH: `3342` deci-Kelvin | `61.05 °C` (±0.01) |
| Any provider: value outside 0–150 | rejected by `Constants.IsValidTemperature` |
| NVMe: signed-°C descriptor | decoded; `0 K` / never-reported rejected |
| NVMe: log page, Kelvin LE at bytes 1–2 | `°C = K − 273.15` |
| RAPL: ESU 14 → energy unit | `1/2^14` J = 61.035 µJ |
| RAPL: `Δraw = 16384`, `Δt = 1.0 s`, ESU 14 | `1.0 W` |
| RAPL: **counter wrap** `last = 0xFFFFFF00`, `now = 0x100` | delta = `0x200`, not a huge negative |
| RAPL: `Δt` outside 0.5–2 s | rejected, no value that tick |
| RAPL: first sample | returns null (priming), second returns a value |

**Integration / manual:**

| Platform | What to check |
|---|---|
| Intel Meteor Lake (dev box, Core Ultra 7 155H) | Package temp within ~2 °C of HWiNFO64's "CPU Package"; also exercises the PDH fallback, which is confirmed working here |
| Intel 12th–14th gen (hybrid P/E) | TjMax read unpinned still yields a sane package temp |
| Intel 8th–10th gen | Baseline non-hybrid path |
| AMD Zen 3 (5000) | Tctl == Tdie, no offset |
| AMD Zen 4 (7000, model `0x61`) | Tctl path; confirm the `+20 °C` X-series behaviour is *inside* the reported Tctl and needs no extra handling |
| AMD Zen 5 (9000, model `0x44`, family `0x1A`) | Module loads — family gate is `<= 0x1A` |
| AMD Zen 1 1800X, if reachable | The `−20` Tdie offset branch |
| AMD family 0x10–0x16 | Module returns `STATUS_NOT_SUPPORTED`; app falls back cleanly and logs it as *expected*, not an error |
| VM (Hyper-V) | No PawnIO device or module rejects; no thermal zone; provider `Failed`, one log line, app otherwise normal |
| PawnIO absent | Install prompt appears once; declining writes the marker and never re-prompts |
| PawnIO present, service stopped | `CreateFile` → `ERROR_FILE_NOT_FOUND`; graceful fallback |
| Defender default | No detection on `PawnIO.sys` or on MetricsPusher extracting/running the embedded setup — **test this explicitly**, extract-and-execute is a heuristic trigger |
| Defender + HVCI / memory integrity on | Driver still loads |
| WDAC enforcement | Expected block; verify the app degrades rather than hanging |
| **NVMe on in-box `stornvme.sys`** (dev box: Micron MTFDKBA1T0QFM) | Tier-1 property answers; °C tracks a sustained-write burst; **works unelevated** |
| **NVMe, elevated** | Same value as unelevated — proves the access *mask*, not the token, is what matters |
| NVMe on Samsung `secnvme.sys` | Expected tier-1 and tier-2 failure → latch, one log line, no error spam |
| Intel RST / VMD RAID mode | Expected not-supported → latch cleanly (explicitly out of scope, §7 R11) |
| SATA SSD as system disk | Tier-1 may answer; if not, latch — do not attempt ATA SMART |
| Multi-disk box | The volume→`PhysicalDriveN` resolution picks the **system** disk, matching `diskFree`/`diskTotal` |
| **CPU package power, Intel** | Watts within ~10 % of HWiNFO64's "CPU Package Power" at idle **and** under a sustained all-core load; limit matches the board's PL1 |
| **CPU package power, AMD** | Watts track HWiNFO64; **limit absent** and logged as structural, not as a failure |
| **Power under sustained load ≥ 5 min** | Survives at least one 32-bit accumulator wrap without a spike or a negative — the wrap is routine at ~263 s under load, so this test must run long enough to hit one |
| **Power across a sleep/resume cycle** | No absurd value on the first tick after resume (large `Δt` is rejected) |

---

## 7. Risk register

| # | Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|---|
| R1 | **Elevation regression.** Standard users cannot launch the app; a UAC prompt every launch causes users to abandon it. | High | High | Accepted by decision. Document prominently. If it bites, the escape hatch is PawnIO's non-admin device exposure (§1.3), which reverts `app.manifest` alone. |
| R2 | **Extract-and-run trips AV heuristics.** Writing an embedded exe to disk and executing it elevated is a classic dropper shape. | Medium | High | Test against Defender before release (§6). Fallback: stop embedding and ship `PawnIO_setup.exe` beside the app, or document winget as the only path. |
| R3 | **Module signing key changes.** Modules are RSA-signed against a key baked into the driver; a key rotation makes the pinned 0.2.10 blobs unloadable on a newer PawnIO. | Low | High | `IOCTL_PIO_LOAD_BINARY` failure is already a first-class state → fallback provider. Check `PawnIO.Modules` releases at each app release; refreshing means replacing two blobs. |
| R4 | **PawnIO reclassified by Defender**, as WinRing0 was. It is a scriptable ring-0 driver, an inherently attention-drawing category. | Low | High | Nothing prevents it. Detection is already graceful. Monitor the upstream repos; the whole feature is behind one provider and can be disabled without touching the wire. |
| R5 | **New CPU family outside `0x17`–`0x1A`** (Zen 6) rejected by the pinned module. | Medium | Medium | Expected and handled — module rejection is a normal negative. Refresh the blobs when upstream adds support. |
| R6 | **AMD SMN read races** another monitoring tool on the PCI index/data pair, yielding a garbage temperature. | Medium | Low | `Global\Access_PCI` with World-FullControl DACL (§3.3), plus the 0–125 °C validator. |
| R7 | **FACEIT anti-cheat blocks PawnIO's signer**, so the app and FACEIT cannot coexist. | Low | Medium | Upstream issue, no workaround. Document it. |
| R8 | **Thermal-zone fallback reports a plausible but wrong number** (board sensor, or a constant), and a consumer treats it as die temperature. | Medium | Medium | `CpuTemperatureSource` carried from day one; §3.4's limits in the README; do not put it on the wire until §5's follow-up decides how to signal provenance. |
| R9 | **`packages.lock.json` churn** — the existing publish/restore hazard. | Low | Low | No `PackageReference` changes, so it should be inert. Verify byte-identical after build *and* publish, as `CLAUDE.md` requires. |
| R10 | **Bundled installer goes stale**, drifting behind the module blobs it must be compatible with. | Medium | Low | Pin and record all versions in one README table; treat "refresh PawnIO assets" as a single atomic maintenance task. |
| R11 | **NVMe temperature is driver-dependent, not universal.** Vendor NVMe drivers (Samsung `secnvme.sys`), Intel RST/VMD RAID mode, USB bridges and hardware RAID return not-supported. | Medium | Low | Accepted by scope decision: two documented tiers, then latch. Vendor `IOCTL_SCSI_MINIPORT` paths are explicitly **out of scope** — undocumented formats, per-vendor maintenance, GPL-source-derived. Documented as a known limit, not a bug. |
| R12 | **Combining CPU and NVMe in one commit** blocks a low-risk, no-driver sensor behind every PawnIO unknown. | Medium | Low | Accepted by sequencing decision. The two providers are separate types with no shared state, so splitting the commit stays available if Phase 0 goes badly. |

---

## 8. Implementation checklist

Ordered for execution. Each step ends buildable and testable.

**Phase 0 — branch and spikes**
0. **`git switch -c feat/cpu-nvme-temperature`** off `main` (clean tree at `2a11e5e`). All work
   below happens there; `main` stays shippable. The elevation change alone justifies this — it
   is a user-visible regression for standard-user machines and must be easy to abandon.
   Spikes are throwaway console projects **outside** the repo (use the scratchpad), so nothing
   half-finished is ever committed.
1. Run `PawnIO_setup.exe -?`; record the real flag list. Install 2.2.0 on the dev box; note
   whether it returns `3010`. Confirm the `PawnIO` service and the `Uninstall\PawnIO` key appear
   as §2.3 predicts.
2. Download `release_0_2_10.zip`; record SHA-256 for `IntelMSR.bin`, `AMDFamily17.bin`,
   `PawnIO_setup.exe`. Verify the setup exe's Authenticode signature.
3. Throwaway console spike: open the device, load `IntelMSR.bin`, read `0x1A2` and `0x1B1`,
   print the decoded temperature. Cross-check against HWiNFO64. **This validates §2.3's IOCTL
   codes and wire format before any production code exists.**
3a. NVMe spike (**independent — run this first, it needs neither PawnIO nor elevation**):
   `IOCTL_STORAGE_QUERY_PROPERTY` / `StorageDeviceTemperatureProperty` against the system disk,
   from a **non-elevated** process with `dwDesiredAccess = 0`. Confirm the returned °C is sane
   and tracks a disk-load burst. Repeat elevated to prove the access mask, not the token, is
   what matters.

**Phase 1 — elevation**
4. `app.manifest` → `requireAdministrator`; rewrite the surrounding comment.
5. Strip the refusal from `Program.cs` (`IsElevated`, `IsUacDisabled`, `IsUacDisabledValue`,
   `UacPolicyKey`, the block and its messages).
6. Delete the three `IsUacDisabledValue_*` tests from `ProgramTests.cs`.
7. `dotnet build --warnaserror` && `dotnet test` — green, test count drops by 3.
8. Launch and confirm: one UAC prompt, tray icon appears, datagrams still flow.

**Phase 2 — PawnIO plumbing**
9. Add the four embedded resources and their `<EmbeddedResource>` entries. Confirm
   `packages.lock.json` is unchanged after both build and publish.
10. `Services/PawnIoDevice.cs`: `CreateFileW`/`DeviceIoControl` P/Invokes, `LoadModule`,
    `Execute` with preallocated reusable buffers, `IDisposable`. Distinguish the three failure
    modes from §2.5.
11. Unit-test the name-marshalling (32-byte NUL-padded, truncation at 31 chars) and buffer
    layout against hand-built byte arrays — no device needed.

**Phase 2a — NVMe (code-independent of Phases 1–2; same commit)**
9a. `Services/NvmeTemperatureService.cs`: resolve the system volume's `PhysicalDriveN` via
    `IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS`, open with `dwDesiredAccess = 0`, then the two-tier
    probe — `StorageDeviceTemperatureProperty` (52), else the NVMe `0x02` log page (50). Decide
    the path once at init, reuse the buffer, validate with `Constants.IsValidTemperature`, latch
    structural failures.
9b. Unit-test both decodes against hand-built byte arrays — signed-°C descriptor, and
    little-endian Kelvin at log bytes 1–2 including the `0 K` rejection. Add
    `float? NvmeTemperature` to `SystemMetrics` (unmapped in `BuildPayload`, same comment as §3.7).

**Phase 3 — providers**
12. `ICpuTemperatureProvider` + `CpuTemperatureSource`.
13. `IntelMsrTemperatureProvider`, with the decode split into `internal static` pure functions
    so it is unit-testable without hardware.
14. `AmdSmnTemperatureProvider`, likewise, including the Tdie offset table and the
    `Global\Access_PCI` wait.
15. `ThermalZonePdhProvider`, reusing `SystemMetricsService`'s PDH idiom
    (`PdhAddEnglishCounterW`, the `Priming`/`Ready`/`Failed` state discipline, deci-Kelvin
    conversion).
15a. **`CpuPackagePowerProvider`** (§3.4b) on the *same* `PawnIoDevice` handle: read RAPL units
    and the Intel-only limit at init, then one energy read per tick. Unsigned wrap arithmetic,
    `Stopwatch.GetTimestamp()` for `Δt`, prime the first sample next to the existing
    `PrimeCpuCounter` call in `GpuDisplayPushService.RunAsync`. Keep the delta maths in an
    `internal static` pure function so the wrap case is unit-testable.
16. Write every unit test in §6's table. All must pass with no PawnIO installed.

**Phase 4 — service and wiring**
17. `CpuTemperatureService`: probe order, latching, edge-triggered logging, `float?` accessor.
18. Add `float? CpuTemperature` to `SystemMetrics`; populate it in `GetSystemMetrics()`; add the
    comment explaining why it is *not* in `BuildPayload`.
19. Log the selected source once at startup, then the temperature at `Debug` every ~60 s so the
    log is verifiable without a wire field.
20. `dotnet build --warnaserror` && `dotnet test`.

**Phase 5 — installer**
21. `PawnIoInstaller`: registry probe, declined marker, prompt, extract, run, exit-code handling
    (0 / 3010 / other), cleanup.
22. Call it from `Program.Main` after the mutex.
23. Test all four paths on a machine with PawnIO uninstalled: accept, decline, decline-then-relaunch,
    reboot-required.

**Phase 6 — verification and docs**
24. Run §4's profiling: baseline vs. new, CPU% and `gen-0-gc-count`. **Gate: gen-0 count must
    not increase over 10 minutes idle.**
25. Work §6's platform matrix as far as available hardware allows; record what was *not* tested.
26. Update `CLAUDE.md` — rewrite the "Never runs elevated" constraint, add PawnIO to Layout and
    Constraints, note the module-blob refresh task.
27. Update `README.md` — PawnIO prerequisite, elevation and its consequences (including the
    standard-user regression), asset versions and SHA-256s, fallback reliability limits.
28. Final `dotnet build --warnaserror` && `dotnet test`; confirm `packages.lock.json` unchanged;
    publish self-contained and confirm the exe is ~130 MB, not ~3 MB.
29. Commit on `feat/cpu-nvme-temperature` (CRLF per `.editorconfig`). Leave the merge decision to
    the user — the elevation regression is the kind of change that deserves a look at the real
    diff before it reaches `main`. If Phase 0 killed the PawnIO half, split: land the NVMe
    provider alone and drop the manifest, installer and module changes entirely.

---

## 9. Verification summary

Work is done when all of the following hold, **on branch `feat/cpu-nvme-temperature`** with
`main` untouched:

- `dotnet build --warnaserror` and `dotnet test` pass; test count = 275 − 3 (removed UAC tests)
  + the new decode tests.
- On the dev box, NVMe temperature is reported and matches CrystalDiskInfo within ~2 °C —
  **and reads the same value elevated and unelevated**, proving the `dwDesiredAccess = 0`
  premise rather than assuming it.
- `packages.lock.json` is byte-identical after a plain build **and** after a publish.
- `dotnet publish -c Release -r win-x64 --self-contained` yields a ~130 MB exe.
- On the Intel dev box with PawnIO installed: log shows `IntelPackageMsr`, and the reported
  temperature tracks HWiNFO64's CPU Package within ~2 °C.
- CPU package power tracks HWiNFO64 within ~10 % at idle and under sustained all-core load, and
  survives at least one 32-bit accumulator wrap (a ≥ 5-minute loaded run) with no spike.
- With PawnIO uninstalled: log shows `AcpiThermalZone` and a value near the
  `\Thermal Zone Information(\_TZ.THRM)\High Precision Temperature` counter.
- In a VM: log shows one line, provider `Failed`, and the app otherwise behaves exactly as
  before.
- `gen-0-gc-count` over a 10-minute idle run is unchanged from the pre-change baseline.
- No datagram change: the wire-contract tests pass untouched and `push_metrics.md` needs no edit.

---

## Sources

- [pawnio.eu](https://pawnio.eu/) · [namazso/PawnIO](https://github.com/namazso/PawnIO) ·
  [PawnIO.inf.in](https://github.com/namazso/PawnIO/blob/master/PawnIO/PawnIO.inf.in) ·
  [pawnio_um.h](https://github.com/namazso/PawnIO/blob/master/PawnIO/include/pawnio_um.h) ·
  [architecture](https://deepwiki.com/namazso/PawnIO)
- [namazso/PawnIO.Modules](https://github.com/namazso/PawnIO.Modules) ·
  [IntelMSR.p](https://github.com/namazso/PawnIO.Modules/blob/master/IntelMSR.p) ·
  [AMDFamily17.p](https://github.com/namazso/PawnIO.Modules/blob/master/AMDFamily17.p) ·
  [releases](https://github.com/namazso/PawnIO.Modules/releases)
- [namazso/PawnIO.Setup releases](https://github.com/namazso/PawnIO.Setup/releases) ·
  [2.2.0 notes](https://github.com/namazso/PawnIO.Setup/releases/tag/2.2.0) ·
  [FACEIT block](https://github.com/namazso/PawnIO.Setup/issues/1)
- [LHM PR #1857](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/1857) ·
  [PawnIo.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/PawnIo/PawnIo.cs) ·
  [IntelCpu.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Cpu/IntelCpu.cs) ·
  [Amd17Cpu.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs) ·
  [Mutexes.cs](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/master/LibreHardwareMonitorLib/Hardware/Mutexes.cs) ·
  [NuGet](https://www.nuget.org/packages/LibreHardwareMonitorLib/) ·
  [discussion #2149](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/discussions/2149)
- [FanControl driver/AV history](https://deepwiki.com/Rem0o/FanControl.Releases/5.4-driver-evolution-and-anti-virus-issues) ·
  [Replacing WinRing0 with PawnIO](https://poorlydocumented.com/2025/09/replacing-winring0-in-fan-control-with-pawnio/)
- [AMD Tctl offset table (Linux k10temp)](https://github.com/torvalds/linux/blob/master/drivers/hwmon/k10temp.c) ·
  [Win32_PerfFormattedData_Counters_ThermalZoneInformation](https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-perf)
- Register semantics (`IA32_THERM_STATUS` 0x19C, `IA32_PACKAGE_THERM_STATUS` 0x1B1,
  `IA32_TEMPERATURE_TARGET` 0x1A2): Intel SDM Vol. 4. AMD `THM_TCON_CUR_TMP` at SMN `0x59800`:
  AMD PPR for family 17h/19h/1Ah.
- Local measurements (2026-08-11, ZENBOOK, Intel Core Ultra 7 155H, non-elevated): PDH thermal
  zone `3342` deci-K → 61.05 °C; `root\WMI MSAcpi_ThermalZoneTemperature` → access denied.
