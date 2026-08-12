# PawnIO Phase 0 — Asset Acquisition and Spike Findings

> Companion to `pawnio-cpu-temp-plan.md`. Covers checklist Phase 0 steps 1–3 and Phase 2
> step 9. Measured on the dev box (ZENBOOK, Intel Core Ultra 7 155H, Windows 11
> 10.0.26200) on 2026-08-11.
>
> Everything below is either **measured** on that machine or **read verbatim** from the
> named upstream source. Items that could not be measured are marked **UNVERIFIED** and are
> collected in §7 — they are not softened, because production code is being written against
> them.

---

## 1. Assets committed to the repo

All four downloaded through a scratch directory and copied into the repo unmodified. SHA-256
is `Get-FileHash -Algorithm SHA256`, and each hash was re-taken **after** the copy into the
repo, so these are the hashes of the committed bytes.

| Repo path | Upstream URL | Version | Bytes | SHA-256 | Licence |
|---|---|---|---|---|---|
| `Resources/PawnIo/IntelMSR.bin` | `https://github.com/namazso/PawnIO.Modules/releases/download/0.2.10/release_0_2_10.zip` | 0.2.10 (published 2026-07-27) | 5 324 | `D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F` | LGPL-2.1-or-later |
| `Resources/PawnIo/AMDFamily17.bin` | same archive | 0.2.10 | 10 652 | `DAE74615761B78BDF064DFB3E136252DDCC6FC727D88F14738D0E5800D427A91` | LGPL-2.1-or-later |
| `Resources/PawnIo/COPYING` | same archive | — | 27 032 | `1E7E6BAE5A5BDE32F1AE5A7C37A082D1AB03CF89354F7F936AC40BE9E39A6531` | LGPL-2.1 text |
| `Resources/PawnIO_setup.exe` | `https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe` | 2.2.0 (published 2026-03-15) | 3 410 960 | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` | GPL-2.0+ |

The containing archive, for anyone re-deriving the module hashes:

| File | SHA-256 |
|---|---|
| `release_0_2_10.zip` | `971C7C974C538B62AC020E0442FA99D0423417BFB496DFE9A4A43CCC0ABC0E63` |

**Archive layout — as the plan assumed.** `release_0_2_10.zip` is **flat**: 23 `.bin` modules
and a single `COPYING` at the archive root, no subdirectories. The three files we want are
picked out by name with no path handling. The full contents are `AMDFamily0F`, `AMDFamily10`,
`AMDFamily17`, `AMDReset`, `ARMMSR`, `DellSMM`, `Echo`, `IntelMCHBAR`, `IntelMSR`,
`IntelOOBMSM`, `IntelPCHThermal`, `IsaBridgeEC`, `LpcACPIEC`, `LpcCrOSEC`, `LpcIO`, `Nvidia`,
`RyzenSMU`, `SmbusI801`, `SmbusIntelSkylakeIMC`, `SmbusNCT6793`, `SmbusPIIX4`, `ZhaoxinMSR`
(all `.bin`) plus `COPYING`. `COPYING` opens with "GNU LESSER GENERAL PUBLIC LICENSE /
Version 2.1, February 1999", confirming §2.2's licence column.

`PawnIO.Modules` 0.2.10 is the newest release; there is no 0.2.11 or later, so the plan's
pinned version is current as of this date.

### 1.1 Authenticode — `PawnIO_setup.exe`

Verbatim from `Get-AuthenticodeSignature`:

```
Status          : Valid
StatusMessage   : Signature verified.
SignatureType   : Authenticode
IsOSBinary      : False
SignerSubject   : E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU
SignerIssuer    : CN=GLOBALTRUST 2015 CODESIGNING 1, OU=GLOBALTRUST Certification Service,
                  O=e-commerce monitoring GmbH, L=Wien, S=Wien, C=AT
SignerThumb     : F380DCC9F706E2756A5047B832FFE719E1BC35F5
SignerNotBefore : 08/02/2024 04:02:18
SignerNotAfter  : 08/05/2027 06:02:18
TimeStamperSubj : CN=Microsoft Public RSA Time Stamping Authority, OU=nShield TSS
                  ESN:7A1A-05E0-D947, OU=Microsoft Ireland Operations Limited,
                  O=Microsoft Corporation, L=Redmond, S=Washington, C=US
```

**Valid, and not a blocker.** The signing certificate expires 2027-08-05; the binary is
countersigned by a Microsoft timestamp authority, so the signature stays valid past that
date. Worth a calendar note against risk R10 ("bundled installer goes stale") — a refresh of
the bundled setup is due before the certificate expires if upstream re-issues.

Version resource: `FileVersion 2.2.0.0`, `ProductVersion 2.2.0.0`, `CompanyName namazso`,
`FileDescription PawnIO Setup`.

**Two spellings of the same binary.** The published release asset is `PawnIO_setup.exe`
(underscore); the binary's own internal name is `PawnIOSetup.exe` (no underscore, as it
appears in its own usage text, §2). The README table and the embedded-resource name use the
**asset** spelling, because that is what is downloaded and committed. Anyone matching on the
internal name — a WDAC rule, an AV exclusion, a process-name check — will see the other.

---

## 2. Installer command line

`-?` is **not** a valid flag. It is rejected, and printing the usage block is a side effect
of that rejection. The tool answers in a **GUI message box**, not on stdout — a redirected
run captures nothing. Captured verbatim:

```
Unknown argument: -?

Usage: PawnIOSetup.exe [-install] [-uninstall] [-unrestricted] [-debuginfo] [-silent]
  -install       Install PawnIO
  -uninstall     Uninstall PawnIO
  -unrestricted  Install unrestricted edition
  -debuginfo     Install debug info
  -silent        Run in silent mode (no UI, even on error)
```

Those five flags are the complete list.

**Differences from plan §2.4.** The plan's "confirmed flags" line named `-install`,
`-silent` and `-unrestricted`. All three are real, but the list was **incomplete**:

- **`-uninstall`** — undocumented in the plan. This is the most useful find here: the driver
  install is cleanly reversible from the same bundled binary we already ship. Plan §5's
  deployment notes never say how a user backs the driver out; the README's
  elevation/prerequisite section should, and it can point at the bundled exe rather than at
  Add/Remove Programs.
- **`-debuginfo`** — undocumented in the plan. Installs debug symbols. No use here.

`-unrestricted` remains **never to be used**: it installs the edition that loads unsigned
bytecode, which discards the module-signing property §1.1 of the plan leans on.

**`-silent` means the exit code is the only channel.** "No UI, even on error" — in silent
mode there is no message box, so a failed install is invisible except through the process
exit code. That makes exit-code handling in §3.6 load-bearing rather than defensive.

### 2.1 The setup binary itself requires elevation

Its embedded manifest declares:

```xml
<requestedExecutionLevel level="requireAdministrator" uiAccess="false"/>
```

A non-elevated `Start-Process` fails outright with "The requested operation requires
elevation" — it cannot even print its own usage. This is consistent with plan §3.6, which
runs it from an already-elevated MetricsPusher and notes "no second UAC prompt". It is
recorded here because it closes a door: there is no unelevated probe of the installer, so
`PawnIoInstaller` cannot, for example, run it with a query flag to decide anything before
committing to the install.

---

## 3. Installed state — measured

PawnIO 2.2.0 was installed **interactively by the user**, not by this agent, and not in
silent mode. The state below was read back after that install.

| Property | Plan §2.3 predicted | Measured | Verdict |
|---|---|---|---|
| Service name | `PawnIO` | `PawnIO` | **Confirmed** |
| Service kind | kernel driver | `Win32_SystemDriver`, `PathName = C:\WINDOWS\system32\DriverStore\FileRepository\pawnio.inf_amd64_a72a2f969b8b7496\PawnIO.sys` | **Confirmed** |
| Start type | `StartType=3` (demand start) | `StartMode = Manual` | **Confirmed** — `Manual` is the friendly spelling of demand-start 3 |
| Running now | not predicted | `State = Running` | Device is live |
| Device class | `SoftwareDevice`, root-enumerated `Root\PawnIO` | PnP device present, `Status = OK`, `Class = SoftwareDevice`, `FriendlyName = PawnIO`, `InstanceId = ROOT\PAWNIO\0000` | **Confirmed** |
| Presence key | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` → `DisplayVersion` | present; `DisplayName = PawnIO`, `DisplayVersion = 2.2.0.0`, `InstallLocation = C:\Program Files\PawnIO` | **Confirmed**, with the two caveats below |

**Caveat 1 — `DisplayVersion` is four-part.** The measured value is the string `"2.2.0.0"`,
not `"2.2.0"`. Plan §2.5 step 1 specifies `Version.TryParse` on this value, which parses
`"2.2.0.0"` correctly, so the plan's approach is sound as written. But any implementation
tempted to string-compare against `"2.2.0"`, or to use `StartsWith`/equality against a
two- or three-part literal, gets a **false negative** and would re-prompt to install an
already-installed driver on every launch. Use `Version.TryParse` and compare `Version`
objects, exactly as specified.

**Caveat 2 — the key exists only in the native 64-bit view.**
`HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO` is **absent**
on this machine. The plan notes LibreHardwareMonitor checks both views
(`PawnIo.cs:33-40`), and checking both remains the right defensive choice — but only one
view answers here, so **a bug in the second lookup would not be caught on this dev box**.
Anyone testing the probe should force the WOW6432Node branch deliberately rather than trust
a green run.

**Partly measured — the reboot question is answered, the exit code is not.**

*Measured:* a clean install of PawnIO 2.2.0 on this Windows 11 box (build 26200) did **not
require a restart**. The driver was live and the device openable in the same session, which
the IOCTL spike in §5 then proved by talking to it. Plan §2.4 asked whether a clean install
returns `3010`; on the evidence, a normal first install has no reason to, because no restart
is pending. That materially de-risks §3.6's UX — the reboot path is an edge case, not the
common one.

*Still UNVERIFIED:* the numeric exit code from `-install -silent`. The install was performed
interactively through the GUI, so no exit code was ever observed. Consequence: §3.6's
three-way handling (`0` / `3010` / other) is written against the **documented** contract from
the 2.2.0 release notes ("in silent mode `ERROR_SUCCESS_REBOOT_REQUIRED` is appropriately
returned if a restart is needed"), not against an observation. Do not paper over this: we
know a restart was not needed, we do not know what the silent path *returns*. The mitigation
is that anything unrecognised falls into the default arm and degrades safely, so a wrong
guess costs the reboot notice rather than correctness.

---

## 4. Module allow-list — read from source at tag 0.2.10

`IntelMSR.p` was fetched at **tag `0.2.10`**, not `master`, so it matches the committed
`IntelMSR.bin` byte-for-byte in provenance.

**The execute function name is `ioctl_read_msr`** — confirmed, not guessed. Its contract, from
the source's own doc comment:

```
/// @param in [0] = MSR
/// @param in_size Must be 1
/// @param out [0] = Value read
/// @param out_size Must be 1
/// @return An NTSTATUS
DEFINE_IOCTL_SIZED(ioctl_read_msr, 1, 1)
```

The **sized** declaration matters for the production implementation: `in_size` must be
exactly 1 and `out_size` exactly 1. A caller that pads the input with extra `long` slots, or
asks for a two-element output, will be rejected by the dispatcher before the allow-list is
ever consulted.

A second function, `ioctl_write_msr` (`in_size` 2, `out_size` 0), also exists. **We never
call it**; it is noted only so nobody is surprised that the module has a write path.

`main()` gates on `get_arch() != ARCH_X64` and `get_cpu_vendor() != CpuVendor_Intel`, each
returning `STATUS_NOT_SUPPORTED` — confirming plan §1.1 and §2.5 step 3.

### 4.1 Read allow-list — all 33 entries

The plan asserted only `0x19C`, `0x1B1` and `0x1A2` explicitly and described the list as
"~33-entry". **The count is exactly 33**, and — this is the finding that matters for §3.4b —
**every register the RAPL path needs is on it**:

| MSR | Name | Needed by |
|---|---|---|
| `0x02A` | `MSR_EBL_CR_POWERON` | — |
| `0x0CE` | `MSR_PLATFORM_INFO` | — |
| `0x0E7` | `MSR_IA32_MPERF` | — |
| `0x0E8` | `MSR_IA32_APERF` | — |
| `0x150` | `MSR_OC_MAILBOX` | — |
| `0x198` | `MSR_IA32_PERF_STATUS` | — |
| `0x19C` | `MSR_IA32_THERM_STATUS` | **§3.3 pre-Nehalem fallback** |
| `0x1A2` | `MSR_IA32_TEMPERATURE_TARGET` | **§3.3 tjMax** |
| `0x1A4` | `MSR_MISC_FEATURE_CONTROL` | — |
| `0x1AD` | `MSR_TURBO_RATIO_LIMIT` | — |
| `0x1B1` | `MSR_IA32_PACKAGE_THERM_STATUS` | **§3.3 package temperature** |
| `0x601` | `MSR_VR_CURRENT_CONFIG` | — |
| `0x606` | `MSR_RAPL_POWER_UNIT` | **§3.4b energy unit** |
| `0x607` | `MSR_VR_MAILBOX_INTERFACE` | — |
| `0x608` | `MSR_VR_MAILBOX_DATA` | — |
| `0x610` | `MSR_PKG_POWER_LIMIT` | **§3.4b PL1** |
| `0x611` | `MSR_PKG_ENERGY_STATUS` | **§3.4b energy accumulator** |
| `0x613` | `MSR_PKG_PERF_STATUS` | — |
| `0x614` | `MSR_PKG_POWER_INFO` | **§3.4b TDP** |
| `0x618` | `MSR_DRAM_POWER_LIMIT` | — |
| `0x619` | `MSR_DRAM_ENERGY_STATUS` | — |
| `0x61B` | `MSR_DRAM_PERF_STATUS` | — |
| `0x61C` | `MSR_DRAM_POWER_INFO` | — |
| `0x620` | `MSR_UNC_PERF_GLOBAL_CTRL` | — |
| `0x621` | `MSR_UNC_PERF_GLOBAL_STATUS` | — |
| `0x638` | `MSR_PP0_POWER_LIMIT` | — |
| `0x639` | `MSR_PP0_ENERGY_STATUS` | — |
| `0x63A` | `MSR_PP0_POLICY` | — |
| `0x63B` | `MSR_PP0_PERF_STATUS` | — |
| `0x640` | `MSR_PP1_POWER_LIMIT` | — |
| `0x641` | `MSR_PP1_ENERGY_STATUS` | — |
| `0x642` | `MSR_PP1_POLICY` | — |
| `0x64D` | `MSR_PLATFORM_ENERGY_STATUS` | — |

**Verdict for §3.4b: the Intel RAPL path is admissible.** `0x606`, `0x611`, `0x614` and
`0x610` are all readable through `ioctl_read_msr`. The plan's Intel power design needs no
second module and no change. (The *decoded values* are still unverified — see §5.)

There is also a six-entry **write** allow-list (`0x601`, `0x610`, `0x150`, `0x607`, `0x608`,
`0x1A4`). Note that `MSR_PKG_POWER_LIMIT` `0x610` is writable. We only ever read it, but it
is worth knowing that this module, loaded, hands an elevated caller the ability to change the
package power limit — a point for the README's honesty about what the driver enables.

---

## 5. IOCTL spike — **VERIFIED ON HARDWARE**

Ran elevated, exit code 0, on the dev box with PawnIO 2.2.0 installed and the service
running. Raw output:

```
Elevated: True   OS: 10.0.26200.0
CreateFileW on \\?\GLOBALROOT\Device\PawnIO ... OK
IOCTL_PIO_VERSION (0xA1B22184) ... FAILED, Win32 error 87 (ERROR_INVALID_PARAMETER)
IOCTL_PIO_LOAD_BINARY (0xA1B22084) with IntelMSR.bin (5324 bytes) ... OK, bytesReturned=0
MSR 0x1A2 = 0x00000000086E0000 -> tjMax = 110
MSR 0x1B1 = 0x000000008822080A -> bit31 valid, deltaT 34 -> 76.0 C
MSR 0x1B1 = 0x000000008830080A -> bit31 valid, deltaT 48 -> 62.0 C
MSR 0x1B1 = 0x00000000882E080A -> bit31 valid, deltaT 46 -> 64.0 C
MSR 0x19C = 0x000000008831280A -> deltaT 49 -> 61 C (unpinned core, informational)
MSR 0x606 = 0x00000000000A0E03 -> ESU 14 -> energyUnit 61.035 uJ; PSU (bits 3:0) = 3
MSR 0x611 sample1 = 0xB23D09D6, sample2 = 0xB23F946A, delta 166548, dt 1.0116 s -> 10.05 W
MSR 0x614 = 0x00120000000000E0 -> bits14:0 = 224 -> TDP = 224 / 2^3 = 28.00 W
MSR 0x610 = 0x0042820000DD8200 -> bits14:0 = 512 -> PL1 = 512 / 2^3 = 64.00 W
MSR 0x1B0 (not on allow-list) -> refused, Win32 error 5 ERROR_ACCESS_DENIED
```

### 5.1 What this confirms

**Plan §2.3 is correct where the feature depends on it.**

| Item | Verdict |
|---|---|
| Device path `\\?\GLOBALROOT\Device\PawnIO` | **VERIFIED** |
| `IOCTL_PIO_LOAD_BINARY` = `0xA1B22084` | **VERIFIED** — accepts `IntelMSR.bin` unmodified, returns `bytesReturned = 0` |
| `IOCTL_PIO_EXECUTE_FN` = `0xA1B22104` | **VERIFIED** |
| Execute wire format: 32-byte NUL-padded ASCII name ‖ `long[]` in, `long[]` out | **VERIFIED** |
| Function name `ioctl_read_msr` | **VERIFIED** |
| `0x1A2` decode `tjMax = (v >> 16) & 0xFF` | **VERIFIED** → 110 |
| `0x1B1` decode: bit 31 valid, `deltaT = (v & 0x007F0000) >> 16`, `celsius = tjMax − deltaT` | **VERIFIED** |
| `IOCTL_PIO_VERSION` = `0xA1B22184` | **WRONG or mis-specified** — see §5.2 |

The three `0x1B1` samples (76.0, 62.0, 64.0 °C) were taken 300 ms apart on a machine that was
mid-build, and they move — this is a live sensor, not a latched constant. The unpinned `0x19C`
core reading (61 °C) sits just below the package figure, which is the expected relationship.

**Module-level allow-listing genuinely enforces.** The negative control — MSR `0x1B0`, which
is *not* in §4.1's list — was refused with `ERROR_ACCESS_DENIED`. Plan §1.1 rests its entire
security argument on this property ("userspace gets narrow per-module IOCTLs instead of
arbitrary `rdmsr`"). It is now **demonstrated, not asserted**.

**Elevation is genuinely required.** The same binary run non-elevated fails at `CreateFileW`
with **Win32 error 5, `ERROR_ACCESS_DENIED`** — confirming plan §1.3's DACL analysis
(`D:P(A;;GA;;;SY)(A;;GA;;;BA)`) by measurement. It also confirms §2.5's failure taxonomy is
right to separate "access denied" (error 5) from "driver not started" (error 2): error 5 is
what an unelevated caller sees on a machine where the driver *is* installed and running.

### 5.2 Plan defect — `IOCTL_PIO_VERSION` does not work

`0xA1B22184` returns **`ERROR_INVALID_PARAMETER` (87)**, not a version. Either the control
code or its expected buffer size is wrong in plan §2.3's table. The spike passed a zero-length
input and an 8-byte output; a different output size may be required.

**Impact: none on the feature** — nothing in the design calls it. But §2.3's table currently
lists it beside two codes that *are* verified, which makes it look equally trustworthy. That
row should be marked **UNVERIFIED/WRONG** rather than left implying confirmation, so nobody
later builds a "is the driver alive?" probe on it. Use `IOCTL_PIO_LOAD_BINARY` succeeding as
the liveness signal instead; that is verified and the code has to do it anyway.

### 5.3 The correction that would have shipped a bug — RAPL power units

**Plan §3.4b's table is incomplete in a way that produces numbers wrong by a factor of 8 on
this machine.** It says:

> `MSR_PKG_POWER_INFO` `0x614` bits 14:0 = TDP; `MSR_PKG_POWER_LIMIT` `0x610` bits 14:0 = PL1

Those raw fields are **not watts**. They are in RAPL *power* units and must be divided by
`2^PSU`, where **PSU is bits 3:0 of `MSR_RAPL_POWER_UNIT` (`0x606`)** — a different field from
the ESU (bits 12:8) the plan already documents for the *energy* registers. Measured here:

```
0x606 = 0x000A0E03
    PSU = bits 3:0  = 3   -> power unit  = 1 / 2^3  = 0.125 W
    ESU = bits 12:8 = 14  -> energy unit = 1 / 2^14 = 61.035 uJ
0x614 bits14:0 = 224 -> TDP = 224 / 2^3 = 28.00 W
0x610 bits14:0 = 512 -> PL1 = 512 / 2^3 = 64.00 W
```

28 W is exactly the Core Ultra 7 155H's rated base power, and 64 W is a plausible PL1 for this
chassis. **Taking the raw fields as watts would have reported 224 W TDP and 512 W PL1 on a
28 W laptop part** — values that would pass any "is it positive and under 1000 W" guard the
plan specifies, and would therefore have shipped silently. `CpuPackagePowerProvider` (§15a)
must read PSU at init alongside ESU and apply it to both limit registers.

The **energy** path needs no correction: ESU 14 → 61.035 µJ, and the measured delta over
1.0116 s gives **10.05 W** package power at light idle, which is sane for this part.

### 5.4 Note for §3.3 — tjMax is not 100 here

Measured **tjMax = 110** on this Meteor Lake part. Plan §3.3 uses 100 °C as its fallback
constant (correctly, mirroring LHM) but the surrounding prose leaves the impression that 100
is typical. It is not universal. 110 falls inside the plan's specified 60–130 sanity band, so
**the clamp is correctly specified** — but a test or comment that treats 100 as the expected
value would be misleading. The band is doing real work.

### 5.5 The spike is left in the scratchpad

`agentA-spike\RUNME.txt` has the exact command line and notes that it needs elevation and
writes to `spike-output.txt` beside the exe (the elevated console is a separate window). It
is worth keeping for the AMD leg of §6's platform matrix, where `AMDFamily17.bin`,
`ioctl_read_smn` and the `Global\Access_PCI` mutex are all still unexercised.

---

## 6. csproj wiring and manifest resource names

Four `<EmbeddedResource>` entries were added to the existing `ItemGroup` that already holds
`Resources\trayicon.ico`, with a comment in the file's established register.

**Build:** `dotnet build MetricsPusher.csproj --artifacts-path <agentA> --warnaserror` —
**succeeded, 0 warnings, 0 errors.**

> A full-solution `dotnet build` fails at the time of writing with 93 errors, **all** in
> `MetricsPusher.Tests` (`PawnIoDeviceTests.cs` referencing a `PawnIoDevice` type that does
> not exist yet, and `NvmeTemperatureServiceTests.cs`). That is concurrent work in progress
> by another agent, in files outside this scope, and is unrelated to the resource entries.
> `MetricsPusher.csproj` alone builds clean.

**`packages.lock.json`:** `git status --short packages.lock.json
MetricsPusher.Tests/packages.lock.json` is **EMPTY** after the build. Both lock files are
byte-identical, as plan §2.1 and risk R9 require. (The publish-side half of that check is
still owed — plan §2.1 asks for verification after a `dotnet publish` as well, and that has
not been run here.)

**Manifest resource names — verified, not guessed.** Read out of the built
`MetricsPusher.dll` via `System.Reflection.Metadata.PEReader`, in declaration order:

```
MetricsPusher.Resources.trayicon.ico
MetricsPusher.Resources.PawnIo.IntelMSR.bin
MetricsPusher.Resources.PawnIo.AMDFamily17.bin
MetricsPusher.Resources.PawnIo.COPYING
MetricsPusher.Resources.PawnIO_setup.exe
```

These are the exact strings to pass to `Assembly.GetManifestResourceStream`. Three details
worth having in writing, since each is a plausible place to guess wrong:

- The directory is `PawnIo` — **lowercase `o`** — because that is the on-disk folder name.
  The file `PawnIO_setup.exe` keeps its **uppercase `IO`**. The two differ within the same
  resource set; that is intentional and matches the plan's own §3.1 path table.
- `COPYING` has **no extension**, and the name is not mangled or given one.
- The underscore in `PawnIO_setup.exe` survives verbatim. MSBuild's resource-name mangling
  does not apply to plain `EmbeddedResource` items, only to culture-suffixed and `.resx`
  items.

The built `MetricsPusher.dll` is 3 955 712 bytes, up from a pre-change size dominated by the
3 410 960-byte setup exe — independent confirmation that the blobs really are embedded
rather than silently dropped.

---

## 7. Where measurement differs from the plan

Ordered by how much it should change someone's behaviour.

1. **§3.4b omits the RAPL *power* unit divisor — the one defect here that would have shipped
   a wrong number.** `0x614` and `0x610` bits 14:0 must be divided by `2^PSU`, PSU being bits
   3:0 of `0x606`. Measured PSU = 3, so raw 224 → **28.00 W** TDP and raw 512 → **64.00 W**
   PL1; taken raw they would have been reported as 224 W and 512 W on a 28 W laptop part, and
   would have passed the plan's own sanity guards. See §5.3. **Fix `CpuPackagePowerProvider`
   before it is written, not after.**
2. **§2.3's `IOCTL_PIO_VERSION` = `0xA1B22184` is wrong or mis-specified** — it returns
   `ERROR_INVALID_PARAMETER` (87). Unused by the feature, so documentation-only, but that
   table row must not keep sitting next to two verified codes looking equally confirmed.
   See §5.2.
3. **§2.4's flag list was incomplete.** `-uninstall` and `-debuginfo` exist and were not
   listed. `-uninstall` is a genuine improvement to the deployment story and belongs in the
   README: the driver is reversible from the same bundled binary.
4. **`-?` is not a valid flag**, and help is a **GUI message box**, not stdout. The plan
   assumed `PawnIO_setup.exe -?` would yield capturable console output. It does not. Any
   exit code observed from a `-?` invocation is a usage error and says nothing about the
   installer's normal exit codes.
5. **The silent-install exit code and the 3010 question remain UNVERIFIED.** Plan §2.4
   flagged this; it is still open, because the user installed interactively. §3.6's handling
   is written against the release notes, not an observation. **This is now the only
   significant unverified item in Phase 0.**
6. **`DisplayVersion` is `"2.2.0.0"`, four-part.** Fine for `Version.TryParse` as specified;
   fatal for any string comparison against `"2.2.0"`.
7. **The uninstall key is only in the native 64-bit view here**, not WOW6432Node. Checking
   both is still correct, but this machine cannot catch a bug in the second lookup.
8. **The allow-list is wider than the plan documented, and that is what makes §3.4b
   implementable as specified.** Plan §1.1 asserted only `0x19C` / `0x1B1` / `0x1A2`. The
   0.2.10 list also admits `0x606`, `0x611`, `0x614` and `0x610` — verified from source
   *and* by successful reads (§5) — so the **entire** Intel RAPL path runs through
   `IntelMSR.bin` with no second module and no design change.
9. **tjMax measured 110, not 100** (§5.4). Inside the plan's 60–130 band, so the clamp is
   right, but 100 is not a typical value to write tests or comments around.
10. **`ioctl_read_msr` is size-checked** (`in_size` exactly 1, `out_size` exactly 1). The plan
    describes the buffer layout but not the strictness. A padded input array will be rejected.
11. **The installer binary itself is `requireAdministrator`.** Consistent with §3.6, but it
    removes any possibility of an unelevated pre-flight check against the installer.
12. **Two spellings**: asset `PawnIO_setup.exe`, internal name `PawnIOSetup.exe`.
13. **`MSR_PKG_POWER_LIMIT` `0x610` is on the module's *write* allow-list.** We never write
    it, but loading `IntelMSR.bin` does grant an elevated caller the ability to change the
    package power limit. Relevant to how honestly the README describes what the driver
    enables (plan §5's enterprise/deployment notes).
14. Minor, no action: the plan's "~33-entry list" is **exactly 33**; the archive layout is
    flat as assumed; 0.2.10 and 2.2.0 are still the newest releases of their respective
    repos; the published dates in §2.2 (2026-07-27, 2026-03-15) both match upstream.

---

## 8. Still owed

- The silent-install exit code and the reboot-required verdict (§3) — the only significant
  unverified item left from Phase 0.
- The **AMD** leg: `AMDFamily17.bin`, `ioctl_read_smn(0x00059800)`, the Tctl/Tdie decode and
  the `Global\Access_PCI` mutex are all still unexercised. No AMD hardware was available.
- `packages.lock.json` verification after a `dotnet publish`, not just after a build (§6).
- Recording these SHA-256s in `README.md` next to their upstream URLs, per plan §2.2.
- Cross-checking the measured 76/62/64 °C package readings and 10.05 W against HWiNFO64,
  per plan §6's integration matrix.
