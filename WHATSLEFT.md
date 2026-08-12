# What's left

Status of the `feat/cpu-nvme-temperature` work, as of commit `b6776cd`.

The implementation of `docs/pawnio-cpu-temp-plan.md` is **code-complete and merged to the
branch**: 447 tests pass, `dotnet build --warnaserror` is clean, `packages.lock.json` is
byte-identical after both a build and a publish, and the published exe is 132 MB
(self-contained). No wire-visible behaviour changed — the worst-case datagram test still
pins exactly 522 bytes and `push_metrics.md` needed no edit.

What remains is **validation on hardware this machine does not have**, a handful of code
paths that could not be exercised here, and the deliberately deferred wire commit.

---

## 1. End-to-end run on a machine with an NVIDIA GPU

Nothing below is a known defect — it is work that could not be done on the dev box, which
has Intel Arc graphics and no `nvml.dll`/`nvapi64.dll`.

This matters because of a coupling that is by design (plan §3.5) and is commented at the
construction site in `GpuDisplayPushService.RunAsync`: **the new sensors only initialize
once a GPU is detected and a display has answered discovery.** They fill fields in the
datagram, and there is no datagram without a GPU. On a machine with neither, none of this
code ever runs.

- [ ] Launch the app. Confirm exactly one UAC prompt, the tray icon appears, and datagrams
      still flow. (Plan checklist step 8, never run.)
- [ ] Check `%LOCALAPPDATA%\MetricsPusher\logs\app.log` for the one-shot line naming the
      selected source, then the `Debug` sensor line that follows roughly once a minute.
- [ ] Cross-check CPU package temperature against HWiNFO64 (target: within ~2 °C).
- [ ] Cross-check CPU package power against HWiNFO64 at idle **and** under sustained
      all-core load (target: within ~10 %), and confirm the limit matches the board's PL1.
- [ ] **Run HWiNFO64 at the same time**, not just before or after. This is the only way to
      exercise the shared-open fix — see §5 below for why that is the interesting case.
- [ ] Cross-check NVMe temperature against CrystalDiskInfo (target: within ~2 °C).

## 2. Sustained-load and resume behaviour

- [ ] **A loaded run of at least 5 minutes.** The 32-bit RAPL energy accumulator wraps
      roughly every 263 s at ~250 W, so this is routine rather than an edge case. Confirm no
      spike and no negative wattage across at least one wrap. The wrap arithmetic is
      unit-tested, but has never met a real accumulator.
- [ ] **A sleep/resume cycle.** Confirm no absurd value on the first tick after resume — a
      large Δt should be rejected by the 0.5–2 s guard rather than producing a huge number.

## 3. AMD bring-up — treat as unproven

**The entire AMD leg has never touched silicon.** No AMD hardware was available. The decode
maths are pure functions tested against the published layouts and the `AMDFamily17.p` source
at tag 0.2.10, and the module's own `main()` gates on vendor and family — but first contact
should be treated as bring-up, not as a regression test.

Unexercised: `AMDFamily17.bin` loading at all, `ioctl_read_smn`, the `Global\Access_PCI`
mutex including its World-FullControl DACL, and the Tctl → Tdie decode.

- [ ] Zen 3 (5000): confirm Tctl == Tdie, no offset applied.
- [ ] Zen 4 (7000, model `0x61`) and Zen 5 (9000, model `0x44`, family `0x1A`): confirm the
      module loads — the family gate is `<= 0x1A`.
- [ ] A first-gen part (1600X/1700X/1800X/2700X/Threadripper 19xx/29xx) if one is reachable,
      to exercise the Tdie offset branch. This is the only branch where the offset is
      non-zero, and it is the one most likely to be wrong.
- [ ] An AMD family `0x10`–`0x16` part: the module must return `STATUS_NOT_SUPPORTED` and
      the app must fall back cleanly, logging it as **expected** rather than as an error.
- [ ] Confirm the package power limit is **absent** on AMD and is logged as structural, not
      as a failure. This mirrors how `watts`/`limitW` are absent on the GPU's NVAPI
      fallback (`push_metrics.md` §5).

## 4. Installer paths that could not be exercised

PawnIO 2.2.0 was already installed on the dev box, so `PawnIoInstaller` could only ever
reach `AlreadyInstalled`. Every other path is covered by injected-delegate unit tests only.

The important gap: **the silent-install exit code was never observed.** The driver was
installed interactively, so we never saw what `-install -silent` returns. That branch is
written to the 2.2.0 release-notes contract, and the code says so in a comment rather than
implying it was measured. A wrong guess costs the reboot notice, not correctness — anything
unrecognised falls into the default arm and degrades safely.

**The reboot question is answered, though:** a clean install of 2.2.0 on Windows 11 (build
26200) did **not** require a restart — the driver was live and the device openable in the
same session. So `3010` is an edge case rather than the expected first-install result, which
de-risks the UX considerably. What is still unknown is only what the *silent* path returns
numerically.

Needs a machine with PawnIO **not** installed:

- [ ] Accept the prompt → exit code observed, driver installed, re-probe succeeds.
- [ ] Decline → marker written at `HKCU\Software\MetricsPusher\PawnIoInstallDeclined`,
      fallback provider used.
- [ ] Decline, then relaunch → **no second prompt**.
- [ ] The `3010` path, if it can be provoked at all — likely hard, since a clean install on
      Windows 11 was observed not to need a restart.
- [ ] Confirm the extracted installer is deleted afterwards.
- [ ] **Windows Defender**: confirm no detection when the app extracts and runs the embedded
      setup elevated. This is plan risk R2 — writing an embedded exe to disk and executing
      it elevated is a classic dropper shape, and it has not been tested against Defender.

## 5. Two invariants that are easy to regress

Both are documented in `CLAUDE.md`, but they are the kind of thing a well-meaning change
would undo, and neither failure is visible in the test suite.

- **The PawnIO device is opened `FILE_SHARE_READ | FILE_SHARE_WRITE` on purpose.**
  LibreHardwareMonitor and FanControl are clients of the same device. An exclusive open
  (`dwShareMode = 0`) reads like hardening but would either fail with
  `ERROR_SHARING_VIOLATION` or lock those tools out — and the visible symptom would be *CPU
  temperature silently degrading to the ACPI thermal zone whenever the user has FanControl
  open*. A field-only failure. This was caught in review, not by a test.
- **`PawnIoDevice.TryExecute` passes exact byte counts, never buffer capacity.**
  `IntelMSR`'s `ioctl_read_msr` size-checks `in_size`/`out_size` to exactly one int64 each
  and rejects oversized requests *before* consulting its MSR allow-list — so passing
  capacity fails every read while looking exactly like "this module does not support this
  CPU", i.e. a wrong diagnosis of a caller-side bug. Pinned by a test asserting 40 bytes in,
  8 out.

## 6. Profiling gate (plan §4)

Skipped by decision — the app is not run on this machine. Do this on the NVIDIA box, since
it needs the push loop actually ticking.

- [ ] Baseline the **pre-change** build for 10 minutes, then the new build under the same
      conditions. The delta is the number that matters; the absolute is dominated by the
      existing GPU/PDH work.
      ```powershell
      Get-Counter '\Process(MetricsPusher)\% Processor Time' -SampleInterval 5 -MaxSamples 120 |
        ForEach-Object { $_.CounterSamples[0].CookedValue }
      ```
- [ ] **Acceptance gate: `gen-0-gc-count` must not increase over a 10-minute idle run**
      relative to baseline. This is the sharper instrument — a 1 Hz workload hides in CPU%
      noise, and the csproj disables both concurrent and server GC, so allocation buys
      foreground pauses directly.
      ```powershell
      dotnet-counters monitor --process-id <pid> --counters System.Runtime
      ```
- [ ] Optionally confirm exactly one `DeviceIoControl` per second on the PawnIO device with
      Process Monitor or a WPR trace.

## 7. The wire commit — deliberately not done here

Putting any of the four new values on the wire is a **separate commit**, by plan decision.
The values are collected and logged but not transmitted; `SystemMetrics` carries
`CpuTemperature`, `CpuPowerWatts`, `CpuPowerLimitWatts` and `NvmeTemperature`, all
deliberately unmapped in `BuildPayload` with a banner comment saying why.

When that commit happens it must, **in the same change**:

1. Raise `GpuDisplayPushService.MaxDatagramBytes` — the worst case currently **equals** the
   522-byte ceiling, so there is no slack to spend.
2. Re-pin the worst-case datagram test.
3. Update `push_metrics.md` §§3.1, 3.3, 4, 5, 6, 8.3, 8.4 and 9.

Protocol `v` stays `1` — adding a key is not a breaking change, since consumers ignore
unknown keys. Only a total approaching 1024 bytes reopens the receiver contract.

**Decide provenance first.** `CpuTemperatureSource` distinguishes a die reading
(`IntelPackageMsr`/`AmdTctlSmn`) from an ACPI board sensor (`AcpiThermalZone`). These are
not the same physical quantity — the zone reads low and lags under load, and some firmware
reports a constant that never moves. §5 of the protocol document has to say which absence
and provenance semantics apply before either value can ship. This is plan risk R8.

## 8. Maintenance

- **Refreshing the PawnIO assets is one atomic task** (plan R10). The two `.bin` modules,
  `COPYING` and `PawnIO_setup.exe` move together, and the README SHA-256 table is re-recorded
  in the same change. Check `PawnIO.Modules` releases at each app release.
- **Zen 6** will fall outside the pinned module's `0x17`–`0x1A` family gate and will be
  rejected until the blobs are refreshed. That is handled — module rejection is a normal
  negative — but it is why the refresh task exists.
- Embedding these blobs changes the published exe's own SHA-256, consistent with the
  existing reproducible-build note in `CLAUDE.md`.

## 9. Known limits that are not bugs

Documented in `README.md`; listed here so they are not re-investigated as defects.

- The ACPI thermal-zone fallback is a **board sensor, not the CPU die**. Many desktops expose
  no `\_TZ` object at all and the provider then reports nothing. VMs generally expose
  nothing. Some firmware reports a plausible-looking constant, and there is no reliable
  programmatic way to tell that from a genuinely stable idle temperature.
- NVMe temperature is **driver-dependent**. Vendor drivers (Samsung `secnvme.sys`), Intel
  RST/VMD RAID mode, USB bridges and hardware RAID may return not-supported; the app latches
  and falls silent after one log line. Vendor `IOCTL_SCSI_MINIPORT` pass-through paths are
  explicitly out of scope.
- Because temperature validation reuses the shared 0–150 °C band, a genuinely **sub-zero**
  drive (cold boot in an unheated room) reports nothing rather than a negative.
- **FACEIT anti-cheat blocks PawnIO's signer outright**, so the two cannot coexist. Upstream
  issue, no workaround.
- WDAC enforcement environments need PawnIO in policy. It is legitimately signed and
  non-blocklisted, but it *is* a scriptable ring-0 driver.
- Loading the Intel module hands an elevated caller the ability to **write**
  `MSR_PKG_POWER_LIMIT` — it is on the module's six-entry write allow-list. This app only
  ever reads it, but the capability is real and is stated in the README.

## 10. Errors found in the plan, already fixed in code

`docs/pawnio-cpu-temp-plan.md` is kept as the approved design record and was **not**
rewritten, so it still contains these. `docs/pawnio-phase0-findings.md` records what was
actually measured.

| Plan says | Reality |
|---|---|
| §3.4b: `0x614`/`0x610` bits 14:0 = watts | They are in **power units**; divide by `2^PSU` (bits 3:0 of `0x606`), a different field from the ESU. Uncorrected: 224 W reported on a 28 W part, passing the plan's own `< 1000 W` guard. |
| §3.4a: an 8-byte `STORAGE_PROPERTY_QUERY` | It is **12 bytes**; an 8-byte input is rejected with `ERROR_BAD_LENGTH`, making tier 1 look unsupported everywhere. |
| §3.4b: wrap = `(0xFFFFFFFF - last) + now` | Yields `0x1FF` where §6's own test vector demands `0x200`. Plain `unchecked(now - last)` is the true modular distance. |
| §2.3: `IOCTL_PIO_VERSION` = `0xA1B22184` | Returns `ERROR_INVALID_PARAMETER` (87). Unusable; module-load success is the liveness signal. |
| §9: test count = 275 − 3 | The three removed methods are a `[Fact]` and two 3-case `[Theory]`s = **7 cases**. |
| §3.3: TjMax example of 100 | Measured **110** on Meteor Lake. Inside the 60–130 clamp, so the clamp is right, but 100 is not typical. |
