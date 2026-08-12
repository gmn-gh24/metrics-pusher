# What's left — network metrics (v1.0.1), GPU-less operation (v1.0.2)

Validation the dev box cannot do. The dev box is a desktop on wired 5GbE Realtek
(`netType` 0, `netLink` 5000), always awake, single physical NIC, **with an RTX 3090 Ti** —
so the following paths shipped tested by unit fixtures and reasoning, not by hardware.

## Outstanding — GPU-less operation (v1.0.2)

Removing the NVIDIA gate added a branch this box cannot reach: it always finds a GPU. The
unit test pins the payload shape (every `gpu*` key absent, everything else present), but no
datagram from a GPU-less machine has been observed live.

- **A machine with no NVIDIA GPU.** The whole point of the change. Expected: the loop starts,
  discovery runs as usual, and every datagram carries `cpu*`/`nvme*`/`net*`/`ram*`/`disk*`/OS
  fields with no `gpu*` key all session. Worth watching specifically that the **CPU and NVMe
  sensors initialize at all** there — before v1.0.2 they were never constructed on such a
  machine, so PawnIO module loading and the NVMe IOCTL path have only ever run on boxes that
  also had a GPU. `Program.cs` already prompted for the PawnIO install on those machines and
  then never used it; that install is live for the first time as of v1.0.2.
- **How the display renders a permanently GPU-less sender.** Protocol-wise this is just §5
  absence, and the reference consumer's rule is "absent ⇒ keep last-good / render unknown".
  Untested is what the panel actually looks like when the GPU region never populates.
- **The startup race on a machine that does have a GPU.** The probe no longer gates the loop,
  so the first datagram can precede it and ship without `gpu*` keys, with the keys appearing
  a tick or two later. Needs a capture against a display that answers the first discovery
  ping; expected to be invisible to a consumer that keeps last-good, but never observed.
- **Mid-session GPU loss.** §7.1 previously claimed a sender goes fully silent when GPU
  monitoring drops; the suppression guard stopped doing that once the CPU/NVMe/network fields
  joined the live set, and §7.1 now says so. Confirm with a driver restart: expected is the
  `gpu*` keys dropping out (two datagrams) while everything else keeps arriving at 1 Hz.

## Outstanding — network metrics

- **A Wi-Fi adapter.** `netType` = 1 has never been produced by a real read (only by the
  mapping test), and `netLink` has only ever been observed static — a Wi-Fi link that
  renegotiates its rate mid-session is the one real-world case exercising the per-tick
  re-read of `ReceiveLinkSpeed`.
- **Adapter disable/re-enable mid-session.** The counter-reset path: octet counters go
  backwards, that tick must re-baseline (both rates absent for one datagram) and the next
  tick must report normally. Also worth watching: whether `GetIfEntry2` for the cached
  interface index starts failing while disabled (expected: edge-triggered Debug line,
  recovery line on re-enable) and whether the same index returns on re-enable.
- **Sleep/resume.** The first tick after wake spans the sleep; the 0.5–2 s interval
  rejection must drop it (rates absent for one datagram) rather than report a fabricated
  multi-hour average. Mirrors the RAPL `cpuWatts` guard, which shares the same window.
- **A VPN / Hyper-V / WSL adapter winning the gateway selection.** The inherited §1.1
  hazard: the network fields would then describe the virtual adapter (its name, its
  often-synthetic link speed, its traffic), which is also where the datagram goes. Expected
  and documented, but never observed live from this build.
- **A 10GbE-or-faster machine.** The upper end of `netLink` (>5000) and multi-Gbit
  sustained `netRx` values come only from unit fixtures; a saturated fast link has not
  been observed live.
- **A machine with no default gateway at startup.** The probe should latch all five
  fields absent for the session (one Debug line). Only the "no adapter at all" branch of
  that has test coverage; a gateway that appears *after* startup stays unwatched by
  design (the adapter is resolved once, like the frozen discovery endpoint).

## Deliberately skipped, not pending

- **Throughput cross-checks against third-party monitors** (HWiNFO64 etc.) — the user
  declined those tools; validation was done against Task Manager's own per-adapter
  figures and a known file transfer instead.
- **Wi-Fi RSSI / signal quality** — rejected in design: it would need `wlanapi.dll`, a
  session WLAN handle, and would be structurally absent on every wired machine.
- **Per-adapter multi-NIC reporting** — out of scope on purpose; the protocol describes
  the one adapter the push leaves by.
