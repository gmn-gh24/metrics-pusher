# MetricsPusher — UDP Metrics Protocol Reference

**Protocol version: `1` &nbsp;|&nbsp; Sender version: MetricsPusher v1.0.1 &nbsp;|&nbsp; Status: authoritative**

This document fully specifies the UDP metrics datagram pushed by MetricsPusher
(Windows). The feed is **consumer-agnostic**: any device or
application capable of receiving UDP datagrams and parsing JSON can consume it — an
embedded display, a desktop dashboard, a monitoring/logging service. The reference
consumer is an ESP32 LCD display, referenced below only where its constraints shaped
the contract. This document contains everything needed to **build a brand-new
consumer from scratch**, or to **update or verify an existing one**, without reading
the sender's source code.

Where this document and the sender code disagree, the code and its pinned tests win —
the authoritative sources are listed in [§12](#12-authoritative-sources-in-this-repo).

---

## 1. Transport

| Property | Value |
|---|---|
| Protocol | UDP (IPv4, unicast) |
| Destination port | **4210** |
| Source port | Ephemeral (do not rely on it) |
| Cadence | **1 datagram per second** (see [§7](#7-send-conditions-and-suppression-rules) for when ticks are skipped) |
| Reliability | Fire-and-forget. **No ACK, no retransmit, no sequence numbers, no ordering guarantees.** |
| Datagram size | ≤ **732 bytes** by contract (raised from 591 for the network fields — see §3.3). Bounded by per-string truncation plus plausible numeric widths and **pinned by a worst-case unit test**; the sender also checks each serialized length and logs an edge-triggered warning if an overrun somehow escapes that fixture. Receivers MUST buffer ≥ **1024 bytes** (≥ 512 sufficed for senders < v5.12.0) and never assume a fixed size. |
| Fragmentation | Never — one metrics report is always exactly one datagram, well under any MTU. |
| Encryption / auth | **None.** Cleartext JSON on the local subnet (see [§10](#10-security-model)). |

### 1.1 How the sender finds the consumer

There is no broadcast, mDNS, or configuration. The sender **derives** the destination:

1. Takes its own local IPv4 address — specifically, the **first** operationally-up,
   non-loopback interface with an IPv4 gateway, in OS enumeration order. (Deployment
   hazard: a VPN, Hyper-V, or WSL adapter that has a gateway can win, sending metrics
   to the wrong network's `.99`.)
2. **Requires that address to be on a private network** — RFC 1918 (`10/8`, `172.16/12`,
   `192.168/16`), RFC 6598 CGNAT (`100.64/10`), or RFC 3927 link-local (`169.254/16`).
   Anything else derives nothing and the attempt is skipped. Since v1.0.0: the payload is
   cleartext and unauthenticated ([§10](#10-security-model)) and its destination is
   *derived* rather than configured, so a PC holding a routable public IPv4 would
   otherwise have pushed its host name, hardware and security posture to an unrelated
   internet host once per second. That is outside the trusted-subnet premise §10 rests on,
   not an instance of it.
3. Replaces the **last octet with `99`** (e.g. PC at `192.168.1.42` → target
   `192.168.1.99`). This implicitly assumes a **/24 subnet**; on wider prefixes the
   derived address may land on a different logical segment.
4. ICMP-pings that address with a **1000 ms per-ping timeout**: attempt *n* fires at
   (n−1) × 60 s, 10 attempts total (≈ 9-minute window). The first successful ping
   **freezes** the endpoint `<derived-ip>:4210` for the sender's whole session.
5. An attempt where the PC currently holds `.99` itself, is not on a private network, or
   has no IPv4 gateway yet, is skipped but still **consumes** one of the 10 attempts; the
   loop keeps going and can succeed later (e.g. after DHCP settles). Only exhausting all 10
   attempts disables the feature until the tray app restarts. The endpoint is never
   re-derived mid-session (a PC changing subnets keeps pushing to the old, now-wrong
   address).

**Consumer obligations that follow from this:**
- The consumer device MUST hold the `.99` host address on its subnet (static IP or DHCP reservation).
- The consumer MUST answer ICMP echo (ping) **within 1 second** — discovery depends on it.
- The consumer MUST listen on UDP port 4210.
- The consumer SHOULD be online within ~9 minutes of the PC's tray app starting, or it
  will receive nothing until the tray app restarts.

### 1.2 Multiple senders

Every PC on the subnet running the tray app pushes to the same `.99` address. A consumer
may therefore receive interleaved datagrams from several machines. Disambiguate by the
`host` field (preferred) or the datagram's source IP.

### 1.3 Send-failure behavior (why isolated gaps happen)

If a send fails (`SocketException`, or a disposed socket), the sender disposes its socket and lazily recreates
it on the next tick; the failing tick's datagram is dropped with no retry. Consumers see
this as an isolated 1–2 s gap. ICMP port-unreachable feedback is suppressed on the
sender's socket, so a powered-off consumer costs the sender nothing.

---

## 2. Datagram format

- **Payload:** a single UTF-8 JSON object. No envelope, no length prefix, no trailing
  newline, no NUL terminator.
- **Wire bytes are pure ASCII.** The sender's JSON encoder escapes every non-ASCII and
  HTML-sensitive character as `\uXXXX` (e.g. `é` → `\u00E9`, `<` → `\u003C`). After
  standard JSON unescaping, string values are ordinary UTF-16/UTF-8 text.
- **Numbers** use invariant formatting: `.` decimal separator, no thousands separators,
  no exponent notation for the ranges involved. Integers have no decimal point. The one
  float-typed field (`temp`) is integral in practice (see §4.1).
- **Null handling:** the sender **omits** null fields entirely. A key is either present
  with a non-null value or absent. `null` never appears on the wire. One exception on
  values: a string field can legitimately arrive as `""` (see §4.1 truncation edge cases).
- **Key order** is stable in practice (pinned by sender tests, in the order of the
  table in §4) but consumers MUST parse as a JSON object and MUST NOT rely on order.
- **Unknown keys MUST be ignored.** This is the forward-compatibility contract: new
  fields are added without bumping `v` (see §3).

### 2.1 Example — full datagram (captured from real hardware on MetricsPusher v1.0.1, an idle desktop; every field is as the sender produced it — nothing is spliced)

```json
{"v":1,"gpu":"NVIDIA GeForce RTX 3090 Ti","host":"G-MONSTER","temp":38,"load":2,"vramUsed":2557,"vramTotal":24564,"fan":0,"power":5,"watts":26,"limitW":477,"clock":210,"vramClock":405,"cpu":"AMD Ryzen 9 9950X","cpuLoad":7,"cpuTemp":69.875,"cpuWatts":60,"nvmeTemp":46,"ramUsed":19015,"ramTotal":48696,"diskFree":181,"diskTotal":837,"netName":"Realtek PCIe 5GbE Family Controller","netType":0,"netLink":5000,"netRx":23,"netTx":20,"win":"11 23H2","av":0,"reboot":0,"fw":1,"up":193784}
```
(481 bytes)

This is the current schema, on the NVML backend, from an AMD Ryzen 9 9950X + RTX 3090 Ti
at idle. `cpuTemp`, `cpuWatts` and `nvmeTemp` are all present; **`cpuLimitW` is absent
because the sender is AMD**, whose bundled PawnIO module exposes package energy but no
package power-limit register — a structural absence, not a failed read (§4, §5). An
Intel sender is the only one that carries that key, which is why no single real capture
can show every current field at once.

The sensor values also show the formatting §4.1 warns about: `cpuTemp` is
`69.875` because AMD Tdie decodes in 0.125 °C steps, while `nvmeTemp` is a whole `46`
from the tier-1 storage-temperature property — **do not infer the provider from decimal
formatting**, and parse all three temperature keys as float/double.

All five network fields are present and show an idle wired desktop: `netName` is the
driver's description of the one adapter the sender pushes through (§1.1 selection; the
marks-stripping in §4 had nothing to strip from this particular driver string),
`netType:0` says wired Ethernet without parsing that name, `netLink:5000` is the
negotiated 5GbE rate in Mbps, and `netRx:23`/`netTx:20` kbit/s are one second of real
background chatter — effectively zero against a 5,000,000 kbit/s link. `netRx` and
`netTx` share `netLink`'s unit family a factor of 1000 apart, so
`netRx ÷ (netLink × 1000)` is receive utilisation directly.

This is a second-pass capture for a reason that belongs to the **capture harness, not to
the wire**: the harness calls the payload builder directly and so has no pre-loop phase,
where the real sender primes the PDH baseline, the RAPL energy accumulator, the network
throughput baseline and the first OS-health refresh before its timer starts. The harness
therefore does that priming as an explicit first pass and publishes the second. A real
sender's **first** datagram normally
already carries `cpuLoad` but **not** `cpuWatts` — the first energy sample only
establishes a baseline, so watts appear from the second tick — while `netRx`/`netTx`
normally **are** present from the first tick, because the adapter probe at loop
initialization doubles as their baseline (§11); `av`/`reboot`/`fw`
are best-effort against a background refresh. See §4 and §5 for the actual
first-datagram semantics.

`power`, `watts` and `limitW` are three views of **one** milliwatt pair: 26 W against
this board's 477 W enforced limit is the 5 % on the same line
(`round(26 × 100 ÷ 477) = 5`). Since v5.12.0 the
denominator is on the wire too, so `round(watts × 100 ÷ limitW)` reproduces `power`
within **±1 count** at a desktop-class limit like this one (±2 or worse on low-limit
boards — §4) — a cross-check, not a second quantity to draw (§8.4). Note that the
limit is the board's **enforced** limit, not its nameplate TDP, and that it is
board-specific: do not hardcode a denominator. `cpuWatts` has **no such companion here**
and must be read as an absolute figure: 60 W is package draw with no limit beside it on
this AMD box. This capture is at idle — hence
`"fan":0` (the board parks its fans below its temperature floor), `"clock":210` and
`"vramClock":405`; under sustained load the same board reaches ~1800/10501 MHz with
`temp` near 48 and `power` in the double digits.

### 2.2 Example — degraded datagram (several sources unavailable this tick)

```json
{"v":1,"gpu":"NVIDIA GeForce RTX 4070","host":"DESKTOP-A1B2C3","temp":71,"load":99,"cpu":"AMD Ryzen 9 7950X3D","ramUsed":9216,"ramTotal":32768,"win":"10 22H2","reboot":1,"up":86452}
```
Missing keys mean *unknown/unavailable*, **not zero** (see §5).

### 2.3 Example — sparse parser test vector

A real current (v5.8.1+) sender essentially always includes the session-cached fields
(`gpu`/`cpu`/`win`/`ramTotal`/`diskTotal`…) alongside any live metric, so a datagram
this sparse will not occur in practice — but a consumer MUST still parse it correctly
(`v` plus at least one live metric is the only hard guarantee, and **older senders send
fewer fields** — see §3.2):

```json
{"v":1,"host":"DESKTOP-A1B2C3","cpuLoad":42,"up":120}
```

---

## 3. Protocol versioning

- `"v"` is present in **every** datagram. Current and only published value: `1`.
- `v` is bumped **only on breaking changes** (a field removed, renamed, retyped, or
  re-unit-ed). Adding fields is NOT a version bump — consumers tolerate unknown keys.
- A consumer receiving `v` greater than what it supports SHOULD ignore the datagram
  (or display a "protocol too new" state) rather than misinterpret fields.

**The protocol carries its own version, independent of everything else.** Three numbers are
in play and none may be derived from another. Only the first is on the wire:

| Number | Where it lives | When it moves |
|---|---|---|
| **Protocol version** — currently `1` | The `v` field of every datagram; `GpuDisplayPushService.ProtocolVersion` | Only on a breaking schema change, per the bullets above |
| **Sender version** — currently `1.0.1` | `MetricsPusher.csproj` and the app's release tag. **Never transmitted** | On any app release, wire-affecting or not |
| **Document version** — currently `1.19` | The footer of this file | On any edit to this document, including corrections that changed no behaviour |

So **a sender version change tells a consumer nothing about compatibility**, in either
direction. MetricsPusher v1.0.0 speaks the same protocol `1` the originating tray app's
v5.12.0 spoke, despite the two release numbers looking nothing alike; a consumer written
against either works unchanged with the other. The protocol is *expected* to outlive many
sender releases without moving — it has been `1` for every sender in §3.1. Branch on `v`,
never on a sender release number, which you cannot see anyway.

### 3.1 Field introduction history

| Sender version | Fields added / changed |
|---|---|
| v5.5.0 | Feature debut: `v`, `gpu`, `temp`, `load`, `vramUsed`, `vramTotal`, `fan` (informal ≤ 256-byte budget, documentation-only) |
| v5.6.0 | `host` |
| v5.7.0 | `cpu`, `cpuLoad`, `ramUsed`, `ramTotal`, `diskFree`, `diskTotal`; documented budget raised to 384 |
| v5.7.1 | **First enforced contract:** identity truncation introduced (63 chars) and the budget became a test-pinned constant at 448 |
| v5.8.0 | `power`, `clock`, `win`, `av`, `reboot`, `up`; budget 448 → **496**; string caps became **JSON-encoded-byte** caps |
| v5.8.1 | `fw`; worst-case datagram 470 → 477 bytes (budget 496 unchanged) |
| v5.10.0 | `vramClock` and `watts`; worst-case datagram 477 → 495 → **508** bytes and the **budget ceiling raised 496 → 508** (see §3.3). GPU metrics moved to NVML with NVAPI as fallback: no existing field changed shape, but on the NVML path **every** GPU field is now re-read every second (see §6), and `watts` exists only on that path (see §5) |
| v5.12.0 | `limitW` — the enforced power limit the `power` percentage divides by, published from the sender's existing acquire-time cache (NVML backend only, like `watts`). Worst-case datagram 508 → **522**, **budget ceiling raised 508 → 522**; **receiver floor renegotiated ≥ 512 → ≥ 1024** (see §3.3) |
| v1.0.1 (CPU/NVMe) | `cpuTemp`, `cpuWatts`, `cpuLimitW`, `nvmeTemp`; worst-case datagram and ceiling 522 → **591**. `cpuTemp` is deliberately die/package-only: an ACPI motherboard thermal-zone fallback is not serialized under that key. Protocol `v` remains `1` because all four keys are additive. |
| v1.0.1 (network) | `netName`, `netType`, `netLink`, `netRx`, `netTx` — identity, media type, negotiated link speed and rx/tx throughput of the **primary adapter**, the same one the sender derives the display address from (§1.1); worst-case datagram and ceiling 591 → **732**. `netName` is the driver's description with trademark marks stripped; `cpu` gains `(C)` in its own strip list in the same change. Protocol `v` remains `1` because all five keys are additive. Both extensions ship together as MetricsPusher v1.0.1; the two rows record the two budget raises separately. |

### 3.2 Older senders in the field

On a mixed fleet a consumer can meet pre-5.8.0 senders. Consequences:

- **< v5.6.0:** no `host` — disambiguate by source IP only.
- **< v5.7.0:** GPU fields only (no CPU/RAM/disk).
- **< v5.7.1:** **no string truncation at all** — `gpu`/`cpu`/`host` may exceed 63
  bytes. Consumers MUST bound their own string copies (`strlcpy` semantics) rather
  than trusting the caps in §4.
- **< v5.8.0:** none of `power`/`clock`/`win`/`av`/`reboot`/`up`.
- **< v5.8.1:** no `fw`.
- **< v5.10.0:** no `vramClock`, no `watts`; GPU fields are cadence-tiered (§6), so `vram*`/`fan`/`power` can be up to ~3.95 s old. Datagrams are ≤ 496 bytes there.
- **< v5.12.0:** no `limitW`. Datagrams are ≤ 508 bytes there.
- **MetricsPusher v1.0.0:** none of the CPU/NVMe fields (`cpuTemp`, `cpuWatts`,
  `cpuLimitW`, `nvmeTemp`) and none of the network fields (`netName`, `netType`,
  `netLink`, `netRx`, `netTx`) — both sets debut together in v1.0.1. Datagrams are
  ≤ 522 bytes there.

All are `v:1`; version cannot distinguish sender age — presence of fields can.

### 3.3 Datagram budget history (and why the next field is not free)

| Sender | Ceiling | Measured worst case | Slack to ceiling | Slack to the receiver floor (≥ 512 through v5.11.x; ≥ 1024 from v5.12.0) |
|---|---:|---:|---:|---:|
| v5.7.1 | 448 | 386 | 62 | — |
| v5.8.0 | 496 | 470 | 26 | 16 |
| v5.8.1 | 496 | 477 | 19 | 16 |
| v5.10.0 (`vramClock`) | 496 | 495 | 1 | 16 |
| v5.10.0 (`watts`) | 508 | 508 | 0 | 4 |
| **v5.12.0 (`limitW`)** | **522** | **522** | **0** | **502** |
| **v1.0.1 (CPU/NVMe)** | **591** | **591** | **0** | **433** |
| **v1.0.1 (network)** | **732** | **732** | **0** | **292** |

The 496 → 508 raise was a deliberate renegotiation, taken with the consequence stated
up front: the ceiling then **equalled** the worst case, and only **4 bytes** separated it
from the ≥ 512-byte receive buffer this contract required of every consumer. v5.12.0
paid that consequence — see "Consequence for future senders" below.

One deliberate cushion sits inside the pinned worst case, and it is worth knowing before
anyone tries to spend it. The worst-case fixture uses `105.75` — a **6-byte fractional**
temperature — even though both driver stacks report **whole** degrees today (NVML returns
an integer, NVAPI's sensors are integral). So the pinned 732 already carries a GPU temperature
roughly **3 bytes wider than any datagram this sender can actually produce**. That gap is
load-bearing at 732/732: it is what keeps real traffic clear of the ceiling, and a future
field must not treat it as free space — the 292 bytes of headroom to the receiver floor
are the room for growth, this ~3-byte cushion is not.

The residual exposure is narrow but real: `temp` is validated only against 0–150, so a
backend that ever reported *fractional* degrees could format wider than 6 bytes — the
validator-widest value, `149.12344`, is **9 bytes**, 3 more than the fixture. That would
put real datagrams over the ceiling and force the budget to be **recomputed**, not merely
re-measured, since there is nothing left between the worst case and the ceiling to absorb it.

**Consequence for future senders:** v5.12.0 **consumed** the consequence v5.10.0 warned
about, in the order that warning prescribed. The reference consumer's receive buffers
were raised **496 → 1024 bytes first**, and only then was this document's receiver floor
renegotiated **≥ 512 → ≥ 1024** and the sender's ceiling raised 508 → 522. The ceiling
again **equals** the measured worst case. The CPU/NVMe extension then raised that exact
ceiling 522 → 591, and the network extension 591 → 732, leaving 292 bytes of slack to the
floor. The practical rule remains: the
**next** field is a sender-side change again — raise `MaxDatagramBytes`, re-pin the
worst-case test and extend this table in one commit — and the receiver contract only
re-enters negotiation when the total approaches **1024**. Consumers that already buffered
1024 bytes (common and long recommended here) needed no change at all.

---

## 4. Field reference

Types below are the JSON types on the wire. "Cadence" says how often the underlying
value is re-read on the sender; every present field is re-sent in every datagram.
**A slower cadence never means a missing key** — between reads the sender re-sends the
last value it read (see [§6](#6-freshness-staleness-and-loss) for the resulting
staleness bounds).
**GPU cadences are backend-dependent** (v5.10.0+): on the NVML backend — the primary,
and what any current NVIDIA driver gives — every GPU field is read **every second**.
The values in parentheses are the NVAPI **fallback** cadences, used only where NVML will
not initialize or will not hand out a device-0 handle. Nothing about the key set or the
value shapes changes with the backend, except `watts` and `limitW`, which the fallback
cannot produce at all.
**Validated?** = the sender range-checks and drops implausible values; unvalidated
fields pass through whatever the OS/driver reports, so consumers should clamp before
using values in layout math.

| Key | JSON type | Units / domain | Expected range | Validated? | Cadence | Meaning & source |
|---|---|---|---|---|---|---|
| `v` | int | — | `1` | — | constant | Protocol version. Always present. |
| `gpu` | string | text, ≤ 63 encoded bytes | — | truncated | 1 s *(fallback: session)* | NVIDIA GPU marketing name (NVML `nvmlDeviceGetName`, NVAPI `FullName` on the fallback), e.g. `"NVIDIA GeForce RTX 3090 Ti"`. A static fact either way — the cadence only says what the sender pays for: re-read every sweep on NVML (the call is ~0.025 ms), read once and latched for the session on the fallback (re-read there only after the driver handle is lost and reacquired). |
| `host` | string | text, ≤ 63 encoded bytes | — | truncated | session | Windows computer name (`Environment.MachineName`, typically upper-case, ≤ 15 chars in practice). Use to disambiguate multiple senders. |
| `temp` | **float** | °C | 0 – 150 | **yes** (0–150, NaN/∞ rejected) | 1 s | GPU core temperature. Integral in practice (see §4.1) — both backends report whole degrees. Hotspot is NOT available. |
| `load` | int | % | 0 – 100 | no | 1 s | GPU utilization, as reported by the driver. |
| `vramUsed` | int (long) | **MiB** (1024²) | ≥ 0 | no | 1 s *(fallback: 2 s)* | Dedicated VRAM in use. One driver call produces `vramUsed` and `vramTotal` together, so the pair is always self-consistent. **The two backends define "in use" slightly differently:** NVML reports the driver's own `used` figure (which counts memory the driver reserves for itself), while the NVAPI fallback computes `total − currentAvailableDedicated`. A sender that falls back mid-session can therefore show a small step in this field — that is a backend change, not a workload change. |
| `vramTotal` | int (long) | **MiB** | > 0 | no | 1 s *(fallback: 2 s)* | Total dedicated VRAM — a constant of the board. Both backends report the same figure on the hardware this was validated against (`24564` on an RTX 3090 Ti, NVML and NVAPI alike). |
| `fan` | int | % | 0 – 100 | no | 1 s *(fallback: 3 s)* | GPU cooler level, as reported (first fan of the board). `0` is common and legitimate (semi-passive fans stopped). Absent on boards whose driver does not expose fan speed at all. |
| `power` | int | **% of TDP** | 0 – 200 | **yes** (0–200; NaN/∞ rejected on the fallback, unrepresentable on NVML) | 1 s *(fallback: 3 s)* | GPU power draw as percent of its power limit. Can exceed 100 transiently (boost). **Semantic note (v5.10.0+):** on the NVML backend this is **board** power (`nvmlDeviceGetPowerUsage` ÷ `nvmlDeviceGetEnforcedPowerLimit`, both milliwatts, rounded); the NVAPI fallback prefers the **chip** power domain when the driver exposes it, so the same board can read a few percent apart between backends. Same field, same units, slightly wider domain — do not treat a backend change as a workload change. The raw draw this percentage is computed from is on the wire too, as `watts` (next row) — one reading, two fields. |
| `watts` | int | **W** (whole watts) | 0 – 1999 | **yes** (x < 2000) | 1 s | GPU **board** power draw in whole watts (`nvmlDeviceGetPowerUsage`, milliwatts rounded to the nearest watt). **NVML backend only — permanently absent on NVAPI-fallback machines**, which have no watts query at all (§5). Same reading as `power`: when both are present, `watts ÷ enforced-limit = power`, so they never disagree (a statement about the sender's raw **milliwatt** pair, which is what it divides — the tolerance on reconstructing `power` from the *rounded wire values* is the `limitW` row's). Present without `power` when the enforced-limit query failed (watts needs no denominator), and — the mirror case — `power` can survive while `watts` is dropped, since the two are validated independently: a draw at or above 2000 W fails the watts cap while its ratio to a large limit can still be ≤ 200 %. Since v5.12.0 the enforced limit itself is on the wire as `limitW` (next row). Absent on senders < v5.10.0. |
| `limitW` | int | **W** (whole watts) | 1 – 1999 | **yes** (0 < x < 2000) | session (acquire-time) | GPU **enforced power limit** (`nvmlDeviceGetEnforcedPowerLimit`, milliwatts rounded to nearest watt) — the denominator the `power` percentage divides by. **NVML backend only — permanently absent on NVAPI-fallback machines**, like `watts` (§5). Read **once per handle acquisition**, re-read only after a handle drop/re-acquire, so it is effectively constant for the sender's session (it moves only when the user drags the board's power slider). Validated **independently** of `power`/`watts`; unlike `watts` the cap **excludes zero** (a zero limit is a broken read, not a board state). When all three are present, `round(watts × 100 ÷ limitW)` reproduces `power` within **±1 count at desktop-class enforced limits (≳ 100 W)** — the fleet's case. **Not a universal bound:** both watt fields are rounded to whole watts *before* a consumer divides them, while the sender divides the raw milliwatt pair, and the `watts` rounding alone is worth up to `50 ÷ limitW` percentage points; on very low-limit boards that double rounding can add a second count (20499 mW drawn against a 35 W limit: sender `power = 59`, wire reconstruction `round(20 × 100 ÷ 35) = 57`). Allow **±2** there — and on very small or **fractional-watt** limits treat the cross-check as advisory rather than as any bound at all: the limit's own rounding is then a percent-scale denominator error that grows with load (a 34.5 W limit rounds to `34`, a 2 % error, so a 39500 mW draw gives sender `114` against a reconstruction of `118`). Absent on senders < v5.12.0. |
| `clock` | int | MHz | 1 – 9999 | **yes** (0 < x < 10000) | 1 s | GPU core (graphics) clock. Idle values like `210` are normal. |
| `vramClock` | int | MHz | 1 – 19999 | **yes** (0 < x < 20000) | 1 s | GPU memory (VRAM) clock. **Its own, wider cap than `clock`:** memory clocks are reported on a different scale — GDDR6X reads ~`10501` under load and ~`405` at idle on an RTX 3090 Ti, so a 5-digit value is normal and is not an error. Absent on senders < v5.10.0. |
| `cpu` | string | text, ≤ 63 encoded bytes | — | truncated | session | CPU name from the registry with marketing noise stripped: `(R)`/`(C)`/`(TM)`, `@ x.xGHz`, `N-Core Processor`, `with Radeon Graphics`, the standalone word `CPU`; whitespace runs collapsed. If nothing survives cleaning, the key is absent. |
| `cpuLoad` | int | % | 0 – 100 | **yes** (clamped 0–100) | 1 s | Total CPU utilization from PDH counter `% Processor Utility` — matches Windows Task Manager's CPU %. Normally present from the **first** datagram (the sender primes the counter's baseline before the loop starts). |
| `cpuTemp` | **float** | °C | 0 – 150 | **yes** (0–150, NaN/∞ rejected) | 1 s | CPU **die/package** temperature: Intel package thermal-status MSR or AMD Tdie decoded from Tctl via PawnIO. This key has one physical meaning on every machine. The degraded ACPI thermal-zone provider is a motherboard sensor, not the die, and is deliberately **never serialized** as `cpuTemp`; its local reading remains diagnostic-only (§5). Intel readings are integral; AMD readings have 0.125 °C resolution. |
| `cpuWatts` | int | **W** (whole watts) | 0 – 1000 | **yes** before rounding (0 < x ≤ 1000) | 1 s | CPU package power from the Intel/AMD RAPL package-energy accumulator, averaged over the measured interval and rounded to whole watts. A tiny positive draw can round to `0`; that is a real rounded measurement. The first sample establishes a baseline and is absent; intervals outside 0.5–2 s are rejected, including the first tick after a long sleep, then the next normal interval self-heals. |
| `cpuLimitW` | int | **W** (whole watts) | 1 – 1000 | **yes** before and after rounding (0 < x ≤ 1000) | session | Intel package power limit: PL1 from `MSR_PKG_POWER_LIMIT`, falling back to rated TDP from `MSR_PKG_POWER_INFO` if PL1 cannot be read, rounded to whole watts. A rounded zero is dropped. **Structurally absent on AMD**, whose bundled PawnIO module exposes package energy but no package power-limit register. Ambient: it does not trigger a datagram by itself (§7). |
| `nvmeTemp` | **float** | °C | 0 – 150 | **yes** (0–150, NaN/∞ rejected) | 1 s | Temperature of the physical disk backing the Windows system volume. Primary source is `StorageDeviceTemperatureProperty` (whole °C); fallback is the NVMe health-log composite temperature (Kelvin converted to °C, normally ending in `.85`). Driver-dependent and absent for unsupported vendor/RAID/USB paths (§5). |
| `ramUsed` | int (long) | **MiB** | ≥ 0 | no | 1 s | Physical RAM in use (total − available). |
| `ramTotal` | int (long) | **MiB** | > 0 | no | 1 s | Total physical RAM (constant for the machine, still re-read per tick). |
| `diskFree` | int (long) | **GiB** (1024³) | ≥ 0 | no | 1 s | Free space on the Windows system volume (usually `C:`). |
| `diskTotal` | int (long) | **GiB** | > 0 | no | session | Total capacity of the system volume (read once, cached). |
| `netName` | string | text, ≤ 63 encoded bytes | — | truncated | session | Driver description (make/model) of the **primary network adapter** — the same adapter the sender derives the display address from (§1.1): the first operationally-up interface holding both an IPv4 gateway and an IPv4 address, in OS enumeration order. The §1.1 deployment hazard is inherited deliberately: if a VPN/Hyper-V/WSL adapter wins the selection, all five `net*` fields describe *that* adapter — which is also the one the datagram leaves by. Trademark marks (`(R)`, `(C)`, `(TM)`) are stripped before sending, e.g. `"Intel Ethernet Controller I225-V"`; whitespace runs collapsed. Deliberately the driver's description and **not** the user-renameable alias (`"Ethernet 2"`), which is not hardware identity. Resolved and read once per session. |
| `netType` | int | enum | `0`, `1`, `2` | mapped | session | Primary adapter media type: `0` = Ethernet (IANA ifType 6), `1` = Wi-Fi (ifType 71), `2` = anything else. The machine-readable companion to `netName` — pick an icon from this rather than sniffing the name string. Cached at the same session probe as `netName`. |
| `netLink` | int | **Mbps** | 1 – 400000 | **yes** (0 < x ≤ 400000) | 1 s | Negotiated **receive** link speed of the primary adapter (`MIB_IF_ROW2.ReceiveLinkSpeed`, bits/s, reduced to whole Mbps). Re-read every tick — a static fact in practice, but a mid-session renegotiation (Wi-Fi rate change, cable re-plug at a different speed) self-heals within a second. Both of the driver's "no answer" encodings — `0` (common on disconnected and virtual adapters) and the all-ones unknown sentinel — are dropped, as is anything above 400 Gbps; absent while the medium is disconnected. |
| `netRx` | int (long) | **kbit/s** | 0 – 100000000 | **yes** (0 ≤ x ≤ 100000000) | 1 s | Receive throughput of the primary adapter: the delta of the interface's cumulative received-octets counter over the **measured** interval between two reads, × 8 ÷ 1000, rounded to whole kbit/s. `0` is a real reading (an idle adapter), not an absence. Divide by 1000 for Mbit/s — the same unit family as `netLink`, so `netRx ÷ (netLink × 1000)` is link utilisation. Intervals outside 0.5–2 s are rejected (notably the first tick after resume) and a counter that went backwards (adapter disable/re-enable, driver restart) re-baselines instead of producing a rate; both self-heal on the next tick. |
| `netTx` | int (long) | **kbit/s** | 0 – 100000000 | **yes** (0 ≤ x ≤ 100000000) | 1 s | Transmit mirror of `netRx`, from the sent-octets counter of the same read. The two directions are computed and validated **independently**, so one can be absent while the other survives. |
| `win` | string | text, ≤ 16 encoded bytes | — | truncated | session | Windows version: `"<major> <feature-release>"`. `major` ∈ `"10"`, `"11"`, `"Srv"` (any Windows Server SKU). Fallback when the feature-release string is unavailable: `"<major> <build>"`, e.g. `"11 26100"`. Examples: `"11 23H2"`, `"10 22H2"`, `"Srv 21H2"`. |
| `av` | int | tri-state | `0`, `1`, `2` | mapped | ≈ 60 s, best-effort | Antivirus health from Windows Security Center: `0` = good (green), `1` = not monitored / snoozed (yellow), `2` = poor (red). **Also `2` when the Security Center service itself is not running** — an at-risk state, render it red. Absent = unknown (see §5). |
| `reboot` | int | boolean | `0`, `1` | — | ≈ 60 s, best-effort | OS reboot pending: `1` if Windows servicing (CBS `RebootPending`) or Windows Update (`RebootRequired`) requires a restart. **`PendingFileRenameOperations` is deliberately ignored** (chronic false positives), so this can read `0` where other tools say a reboot is pending. `0` is a **positive** "no reboot needed". Absent = unknown. |
| `fw` | int | boolean | `0`, `1` | mapped | ≈ 60 s, best-effort | Windows Firewall status, Windows Security Center's aggregate verdict: `1` = enabled/OK (green), `0` = disabled or at-risk (any profile Windows considers unprotected). **Also `0` when the Security Center service itself is not running** — an at-risk state, render as off. Not a per-profile report — Domain/Private/Public are collapsed by Windows itself. Absent = unknown (see §5). |
| `up` | int (long) | seconds | ≥ 0 (10-digit worst case) | — | 1 s | System uptime in whole seconds, **including time asleep** (matches Task Manager). Resets to ~0 on reboot. Derive boot time as `now − up` if the consumer has wall-clock time (NTP); the sender deliberately does not transmit an absolute boot timestamp. |

### 4.1 Value formatting details

- `temp` is float-typed on the sender but both driver stacks report **whole degrees**,
  so on real hardware it is integral (`40`). Sender tests pin fractional formatting
  (`62.5`) as legal, and the serializer uses shortest-round-trip float formatting —
  parse as double and do **not** assume a bound on fractional digits.
- `cpuTemp` and `nvmeTemp` are also JSON floats. Intel CPU and tier-1 storage
  readings are integral, AMD Tdie uses 0.125 °C steps, and the NVMe health-log
  fallback converts whole Kelvin to Celsius. Parse all three temperature keys as
  float/double; do not infer the provider from decimal formatting.
- `clock` and `vramClock` are **different domains with different caps** (10000 and
  20000, both exclusive). A consumer sizing a field or a bar from `clock`'s 4 digits
  will clip `vramClock`, which legitimately uses 5.
- All `MiB`/`GiB` figures are binary (1024-based), already rounded down to integers.
- String caps (63 / 16) are measured in **JSON-encoded bytes**, not characters
  (senders ≥ v5.7.1 for identities; the encoded-byte semantics and the `win` cap are
  v5.8.0+; `netName` shares the 63-byte identity cap and its semantics; see §3.2 for
  older senders). Since the wire form is ASCII (escaped), an
  encoded cap of 63 also guarantees the *decoded* UTF-8 form fits a 64-byte buffer
  including a NUL. Consumers copying decoded strings can use 64-byte
  (`gpu`/`host`/`cpu`/`netName`) and 17-byte (`win`) destinations — but per §3.2, bound
  the copy regardless.
- **Truncation edge cases (v5.8.0+):** truncation never splits a UTF-16 surrogate
  pair; a value whose every character is expensive to encode can be truncated to the
  **empty string** (key present, value `""` — render as unknown); a value the encoder
  rejects as invalid UTF-16 is dropped entirely (key absent).

---

## 5. Absence semantics (critical)

**A missing key always means "unknown / unavailable right now" — never zero, never false.**

Three failure lifetimes exist on the sender:
- **Per-tick (self-healing):** the value may return on any later datagram.
- **Session-cached:** the value was read once; if that one read failed, the key is
  absent for the sender's entire session.
- **Permanent / structural:** the sender's current backend or the board simply cannot
  produce that value, so it never appears — `watts` and `limitW` on every NVAPI-fallback
  sender, and `fan` on boards whose driver does not expose a fan. Not a failure and not
  worth rendering as one; see the table's own rows.

| Field | Lifetime | Why it can be absent |
|---|---|---|
| `temp`, `load`, `clock`, `vramClock` | per-tick | The individual driver query failed this snapshot (each fails independently), or the value failed plausibility validation. |
| `vramUsed`/`vramTotal`, `fan`, `power`/`watts` | per-tick on NVML; per **cadence** (2 s / 3 s / 3 s) on the NVAPI fallback | Same causes. On the fallback a failed read is retried at the field's own cadence, not every second, so the key stays absent until the next scheduled read succeeds. |
| `fan` (additionally) | permanent | Boards whose driver does not expose fan speed answer "not supported" on every read — then absent for the whole session, silently (it is expected hardware behavior, not a fault). |
| `power` (additionally) | session | The enforced power limit — the percentage's denominator — is read once per handle acquisition on the NVML backend. If that read fails, `power` is absent until the handle is re-acquired **while `watts` keeps arriving**: the draw needs no denominator. Since v5.12.0 the limit itself disappears alongside `power` in exactly this case: `limitW` is that same cached read, so the two go together while `watts` keeps arriving alone. |
| `watts` | **permanent on the NVAPI fallback** | Unlike every other absence in this table, this one is structural rather than a failure: NVAPI exposes no watts query, so a sender that fell back never sends the key at all. On the NVML backend it is per-tick like the rest. A consumer must therefore not treat a missing `watts` as a fault — check `power` for whether GPU power is being reported at all. |
| `limitW` | **permanent on the NVAPI fallback**; session-cached on NVML | Structural on the fallback, like `watts`: the acquire-time limit cache is only ever populated through NVML, so a sender that fell back never sends the key. On the NVML backend it is read **once per handle acquisition** — a failed read leaves it absent until the handle is dropped and re-acquired (the same read `power` depends on, so both are gone together). It can also be dropped **alone** by its own validation (`0 < x < 2000`, zero excluded) while `power` and `watts` survive, since the three are validated independently. |
| `gpu` | per-tick on NVML; session, self-healing on the fallback | The name read failed. On NVML it is simply re-read next sweep. On the fallback it is retried every sweep until the first success, then latched for the session. |
| all GPU fields together | typically **two datagrams** | The driver handle was lost (driver restart, GPU reset). The sender requires **two consecutive** all-null sweeps before believing it. On the NVML backend every field is read every sweep, so both datagrams simply carry no GPU fields at all. On the NVAPI fallback the first one degrades *partially* — the per-sweep fields (`temp`, `load`, `clock`, `vramClock`) and any slower field that happened to be due are gone, while slower fields that were not due still carry their cached values — and the second drops the whole GPU set. Either way the handle is then released and its cached values discarded with it. The sweep after that normally re-acquires straight away (NVML first), and because the caches were cleared the full set returns at once. It lasts longer only when the handle is lost within ~5 s of having been acquired (re-acquisition waits out that window), or while re-acquisition itself keeps failing — ~5 s per attempt. |
| `cpu` | session | Registry read failed, or nothing survived name cleaning. |
| `cpuLoad` | per-tick *or* permanent | Transient: a PDH sample failed this tick (self-heals). Permanent: PDH is structurally unusable on that machine (counter can't be created) — then absent forever. Baseline is primed before the loop, so it is normally present from the first datagram. |
| `cpuTemp` | per-tick *or* permanent/structural | Transient: the selected PawnIO die provider failed this read or rejected an implausible value. Permanent/structural: PawnIO is unavailable, neither signed module supports the CPU, or the app selected the ACPI thermal-zone fallback. **An ACPI reading is intentionally omitted even when locally available**, because it is a motherboard zone with different placement and lag; absence therefore means “no CPU die/package reading,” not “no temperature-like sensor exists.” |
| `cpuWatts` | per-tick *or* permanent/structural | The first energy sample only establishes a baseline. Later transient absences mean the accumulator read failed, the elapsed interval fell outside 0.5–2 s (notably first tick after resume), or the result failed the 0–1000 W plausibility band; it retries next tick. Permanently absent when PawnIO/module/RAPL initialization is unavailable. |
| `cpuLimitW` | session on Intel; **permanent/structural on AMD** | Intel reads PL1 once during provider initialization and falls back to the same one-shot TDP register; if neither answers, it remains absent for the session. The bundled AMD module exposes energy but no package-limit register, so absence on AMD is expected, not a fault. |
| `nvmeTemp` | per-tick after a successful probe; otherwise session/permanent | The sender probes the system disk once. If neither the storage-temperature property nor the NVMe health log is supported, it latches unavailable for the session (common with vendor NVMe drivers, Intel RST/VMD, USB bridges and RAID). On a supported path, individual read failures or out-of-range values omit the key until a later tick succeeds. |
| `ramUsed`/`ramTotal`, `diskFree` | per-tick | The OS call failed (rare). |
| `diskTotal` | session | One-shot read failed (rare). |
| all `net*` fields together | session | The one-time adapter probe failed: no operationally-up interface held both an IPv4 gateway and an IPv4 address when the push loop started (the sender resolves the adapter once and never re-picks it), or the interface-row read failed at that probe. Latched for the session. |
| `netName` | session | The probe succeeded but the driver's description field was empty (then absent all session while the other four report), or — the §4.1 truncation edge — a pathological name truncated to `""` (key present, empty value). |
| `netType` | session | Practically never absent alone: it is cached by the same probe as `netName` and every ifType maps (unrecognized → `2`). |
| `netLink` | per-tick | The medium is disconnected, the driver reports no speed (`0` — common on virtual adapters — or the unknown sentinel), the value exceeded 400 Gbps, or this tick's interface read failed. |
| `netRx`, `netTx` | per-tick | This tick's interface read failed, the medium is disconnected, the measured interval fell outside 0.5–2 s (notably the first tick after resume), the octet counter went backwards (adapter disable/re-enable or driver restart — that tick re-baselines), or the rate failed the 100 Gbit/s cap. Each direction fails independently; both self-heal on the next tick. |
| `win` | session | Version registry key unreadable, or `CurrentBuild` was non-numeric. |
| `av` | ≈ 60 s cache | No Security Center on the SKU (e.g. Windows Server — then absent forever), the WSC call failed, WSC returned an unrecognized health value, or the first background refresh hasn't completed yet (it is queued when the display is *discovered*, not at app start). |
| `reboot` | ≈ 60 s cache | Registry probe threw (rare), or first refresh not yet completed. |
| `fw` | ≈ 60 s cache | Same causes as `av`: no Security Center on the SKU (e.g. Windows Server — then absent forever), the WSC call failed, WSC returned an unrecognized health value, or the first background refresh hasn't completed yet. |
| `host` | session | Practically never absent. |
| `up`, `v` | — | Never absent in a sent datagram. |

Consumer rendering rule of thumb: render absent fields as `--`/blank/"unknown", and
distinguish that from real zeros (`fan:0`, `reboot:0`, `load:0` are meaningful values).

---

## 6. Freshness, staleness and loss

- Expected inter-datagram gap is 1 s. Single losses are normal for UDP (including the
  sender's own drop-and-recreate on send failure, §1.3); do not alarm on one missed tick.
- **Recommended staleness policy:** keep last-good values; if no datagram arrives for
  **5–10 s**, mark the display stale (grey-out / "no signal"). The sender goes quiet
  legitimately when: the PC sleeps or shuts down, the tray app exits, or NVIDIA GPU
  monitoring becomes unavailable mid-session (§7.1).
- Datagrams are self-contained state snapshots. There is nothing to accumulate; always
  replace, never sum. Later datagram wins (use arrival order; there are no sequence numbers).
- Intra-datagram skew: **fields in one datagram are not all of the same age.** The
  CPU/RAM/disk/uptime fields are read on the tick that sends them. The GPU fields come
  from a shared snapshot (< 0.95 s old, matching the table below — the freshness check is
  a strict `<`) whose slower-cadence entries are themselves
  re-served from the previous read, giving these worst-case ages at the moment the
  datagram leaves the PC:

  | Field(s) | Read cadence | Worst-case age on the wire |
  |---|---|---|
  | `cpuLoad`, `cpuTemp`, `cpuWatts`, `nvmeTemp`, `netLink`, `netRx`, `netTx`, `ramUsed`, `ramTotal`, `diskFree`, `up` | every tick | ~0 (read on the sending tick; the two rates describe the ~1 s interval that just ended) |
  | `cpuLimitW` | once during Intel provider initialization | the sender's whole uptime; never present on AMD |
  | **all GPU fields** (`gpu`, `temp`, `load`, `vram*`, `fan`, `power`, `watts`, `clock`, `vramClock`) — **NVML backend** | every sweep | < **0.95 s** (snapshot TTL) |
  | `limitW` | once per handle acquisition | the handle's lifetime — typically the sender's whole session |
  | `av`, `fw`, `reboot` | ≈ 60 s, best-effort | over a minute (a refresh is skipped while a previous one is still blocked) |
  | `host`, `cpu`, `win`, `diskTotal`, `netName`, `netType` | session | the sender's whole uptime (static facts; the two network identities are cached at the one-time adapter probe) |

  On the **NVAPI fallback** the GPU rows split by cadence tier instead:

  | Field(s) | Read cadence | Worst-case age on the wire |
  |---|---|---|
  | `temp`, `load`, `clock`, `vramClock` | every sweep | < **0.95 s** (snapshot TTL) |
  | `vramUsed`, `vramTotal` | 2 s | < **2.95 s** (2 s cadence + 0.95 s snapshot TTL) |
  | `fan`, `power` (no `watts` on this backend) | 3 s | < **3.95 s** (3 s cadence + 0.95 s snapshot TTL) |
  | `limitW` | — | n/a — never present on this backend (structural absence, §5) |
  | `gpu` | session | the sender's whole uptime (a static fact, latched after the first successful read — see §4 and §5) |

  These are ceilings, not typical values: in the common case the sending tick is also
  the sweep, so the GPU fields are at most one cadence period old and usually fresher.
  Do not treat cross-field consistency as an invariant — on the fallback, `load` can
  already show a new workload while `power` still reports the previous one. If the GPU
  Monitor window is open on the PC, every GPU field collapses to the "every sweep" row
  on that backend too.

---

## 7. Send conditions and suppression rules

A datagram is sent on a 1 s tick **only if at least one "live" metric is non-null**:

- **Live metrics** (any one present ⇒ datagram sent): `temp`, `load`, `vramUsed`,
  `vramTotal`, `fan`, `power`, `watts`, `clock`, `vramClock`, `cpuLoad`, `cpuTemp`,
  `cpuWatts`, `nvmeTemp`, `netRx`, `netTx`, `ramUsed`, `ramTotal`, `diskFree`, `diskTotal`.
- **Ambient/identity fields** (never trigger a send by themselves; they ride along
  whenever a live metric makes the datagram worth sending): `gpu`, `host`, `cpu`,
  `win`, `av`, `reboot`, `fw`, `up`, `limitW`, `cpuLimitW`, `netName`, `netType`, and
  `netLink`. The two limit fields
  are ambient for the same reason the OS-health fields are: each is acquire-time state,
  practically always present for the whole session on its supporting platform, so counting one
  as live would make this guard dead code there and let a names-and-limit payload blank
  the display's last-good screen when every real sensor fails. The three network
  identity/capacity fields follow the same logic — `netName`/`netType` are session-cached
  facts and `netLink` rides every healthy read — while the two throughput fields are
  real per-tick measurements and count as live (`netRx:0` from an idle adapter included:
  zero is a reading, not an absence).

Consequence for consumers: **every received datagram contains at least one live
metric.** Note this suppression is a defensive guard that in practice essentially never
fires — `ramTotal` and `diskTotal` count as "live" despite being static machine facts,
so total metric blackout is close to impossible; expect silence to come from the causes
in §6/§7.1 instead.

### 7.1 The NVIDIA gate

The push loop starts only after NVIDIA GPU monitoring reports available — usually at
tray-app startup, but possibly minutes later if the startup probe outlives its 30 s
startup wait (a menu interaction can trigger the late start). **Since v5.10.0 that probe
asks both stacks:** NVAPI first, and NVML only if NVAPI declines. A board only NVML can
read (a TCC-mode compute card, some vGPU adapters) therefore reports where a pre-v5.10.0
sender was silent; nothing changes for a machine where NVAPI works, which is asked first
and answers first. Additionally there is a **per-tick** gate: if GPU monitoring becomes
unavailable mid-session, an already-running sender goes silent (no datagrams at all — not
even CPU/RAM/disk/OS fields, although they are still readable). Machines where **neither**
stack can read an NVIDIA GPU never send anything. Consumers must not assume every PC on
the subnet reports.

### 7.2 Multi-GPU machines

Only the **first** physical GPU is reported — NVML device index 0, or the first of the
NVAPI enumeration on the fallback. A multi-GPU workstation still emits a single
`gpu`/`temp`/`load`/`vram*`/`fan`/`power`/`clock`/`vramClock` set; the other GPUs are
silently invisible to this protocol. (The two stacks order devices independently, so a
sender that falls back to NVAPI on a multi-GPU box could in principle report a
different board — the `gpu` name is what tells a consumer which one it is looking at.)

---

## 8. Building a consumer — implementation guide

### 8.1 Minimal correct receive loop (pseudocode)

```text
sock = udp_listen(port=4210, recv_buffer >= 1024 bytes)
state = {}                      # last-good values per host
loop:
    data, src = sock.recv()
    doc = json_parse(data)      # tolerate unknown keys; reject malformed silently
    if doc is invalid or doc["v"] missing: continue
    if doc["v"] > 1: continue   # protocol too new for this consumer
    host = doc.get("host", str(src.ip))
    state[host].update_present_fields(doc)   # absent key => leave last-good, mark unknown-age
    state[host].last_seen = now()
render loop (independent):
    for host: if now() - last_seen > 8s: render stale
              else: render fields (absent => "--", 0 => "0", "" => "--")
```

### 8.2 Reference embedded consumer: ESP32 / ArduinoJson

- RX buffer: ≥ **1024 bytes** (`char buf[1024]`; datagram ≤ 732 by contract — 292 bytes of slack; see §3.3). The reference ESP32's buffers were raised 496 → 1024 as part of the v5.12.0 renegotiation.
- `JsonDocument` capacity: 1024 bytes is comfortably sufficient for the ≤ 33 keys of this size
  (ArduinoJson v6: `StaticJsonDocument<1024>`; v7 sizes dynamically).
- ArduinoJson unescapes `\uXXXX` sequences automatically; decoded strings from v5.8.0+
  senders fit `char[64]` (`gpu`/`host`/`cpu`/`netName`) and `char[17]` (`win`) — but
  always copy with `strlcpy` anyway, because pre-v5.7.1 senders truncate nothing (§3.2).
- Check presence before reading: `doc["power"].is<int>()` (v7) /
  `doc.containsKey("power")` (v6) — never default absent numerics to 0 for display.
- WiFi power-save modes can delay/clump UDP delivery; if per-second smoothness matters,
  disable modem sleep (`WiFi.setSleep(false)`).

### 8.3 Typed model (any language)

```text
struct Metrics {
  int      v;            // required, == 1
  string?  gpu, host, cpu, win;
  float?   temp;                       // °C
  int?     load, fan, power, clock;    // %, %, %TDP, MHz
  int?     watts;                      // W, whole watts (NVML-backend senders only)
  int?     limitW;                     // W, enforced power limit (NVML-backend senders only)
  int?     vramClock;                  // MHz (up to 5 digits - wider than clock)
  long?    vramUsedMiB, vramTotalMiB;
  int?     cpuLoad;                    // %
  float?   cpuTemp;                    // °C, die/package only (never ACPI zone)
  int?     cpuWatts;                   // W, whole package power
  int?     cpuLimitW;                  // W, Intel package limit; absent on AMD
  float?   nvmeTemp;                   // °C, system NVMe disk
  long?    ramUsedMiB, ramTotalMiB;
  long?    diskFreeGiB, diskTotalGiB;
  string?  netName;                    // primary adapter make/model, marks stripped
  int?     netType;                    // 0 = Ethernet, 1 = Wi-Fi, 2 = other
  int?     netLink;                    // Mbps, negotiated link speed
  long?    netRx, netTx;               // kbit/s over the last ~1 s interval
  int?     av;                         // 0|1|2
  int?     reboot;                     // 0|1
  int?     fw;                         // 0|1
  long?    upSeconds;
}
```

### 8.4 Suggested display semantics (matches the sender ecosystem's conventions)

| Signal | Convention |
|---|---|
| `temp` color | green `< 80`, orange `80–89`, red `≥ 90` (°C) |
| `av` color | `0` green, `1` yellow, `2` red, absent grey |
| `reboot` | `1` → show a restart-pending badge; `0` → all clear; absent → unknown |
| `fw` | `1` → all clear (green); `0` → "firewall off" warning (red); absent → unknown (grey) |
| `power` | can be shown as a bar to 100%, allowing overshoot to 200; clamp anything beyond |
| `watts` | absolute draw, pairs naturally with the `power` bar (`65 W · 14%`). Absent on NVAPI-fallback senders - render the row from `power` alone rather than blanking it |
| `limitW` | the enforced limit — scales an absolute-watts gauge exactly (`65 / 477 W`) instead of guessing a full-scale value. It is the **same** quantity `power` already encodes (`round(watts × 100 ÷ limitW)` reproduces `power` within ±1 count at desktop-class limits, ≳ 100 W; ±2 or worse on low-limit boards, where rounding both watt fields before dividing costs further counts — §4), so **draw ONE power bar, not two**; use `limitW` for the axis/label and fall back to `power`'s 0–100(–200) scale when it is absent (NVAPI-fallback senders, or a failed acquire-time read) |
| `clock`/`vramClock` | MHz; typically shown side by side. Size the field for 5 digits — `vramClock` uses them |
| `load`/`fan`/`cpuLoad` | clamp to 0–100 before drawing bars (`load`/`fan` are not sender-validated) |
| `cpuTemp` | CPU die/package temperature; use CPU-specific thresholds rather than assuming the GPU `temp` colors fit every processor. Absent on ACPI-fallback machines by design — do not relabel another board sensor as CPU temperature |
| `cpuWatts`/`cpuLimitW` | one CPU package-power gauge: show `cpuWatts / cpuLimitW W` when the Intel limit exists, otherwise show absolute `cpuWatts` alone. A missing `cpuLimitW` is normal on AMD |
| `nvmeTemp` | system-disk temperature in °C; absent is common on unsupported storage-driver paths, so hide/grey the row rather than alarming |
| `netName`/`netType` | adapter identity row: pick the icon from `netType` (`0` wired, `1` Wi-Fi, `2` generic), never by parsing `netName`. Size the name field like the other identities (64-byte decoded buffer) |
| `netLink` | link capacity in Mbps; size for 6 digits (`400000` is legal). Natural axis for a throughput gauge: `netRx ÷ (netLink × 1000)` is utilisation |
| `netRx`/`netTx` | kbit/s; divide by 1000 for Mbit/s — the same unit family as `netLink`. `0` is a real idle reading and renders as `0`; **absent** (baseline tick, first tick after resume, adapter reset) keeps last-good/`--` instead. Do not alarm on a single absent tick |
| `up` | format as `Nd HHh` / `HH:MM`; derive boot time as `now − up` only if NTP-synced |
| VRAM/RAM | `used/total` in GB with one decimal: `value / 1024` |

---

## 9. Verifying an existing consumer — conformance checklist

A conforming consumer:

1. ☐ Listens on UDP 4210, answers ICMP ping within 1 s, holds the `.99` address.
2. ☐ Buffers ≥ **1024 bytes** per datagram (≥ 512 sufficed for senders < v5.12.0); never assumes a fixed size.
3. ☐ Parses as a JSON **object**; does not depend on key order.
4. ☐ **Ignores unknown keys** without error (forward compatibility).
5. ☐ Checks `v == 1`; ignores datagrams with higher `v` gracefully.
6. ☐ Treats absent keys as *unknown*, distinct from `0` (esp. `fan`, `reboot`, `fw`, `load`);
   treats an empty-string value like an absent key for display.
7. ☐ Handles `\uXXXX` escapes in strings (any standard JSON parser does).
8. ☐ Bound-checks every string copy regardless of the documented caps (pre-v5.7.1
   senders truncate nothing); ≥ 64-byte (`gpu`/`host`/`cpu`/`netName`) and ≥ 17-byte
   (`win`) buffers suffice for v5.8.0+ senders.
9. ☐ Parses `temp`, `cpuTemp` and `nvmeTemp` as float/double with no assumed digit
   bound; all other numerics as integers (use 64-bit for `up`, `ram*`, `vram*`,
   `disk*`, `netRx`/`netTx` to be safe).
10. ☐ Clamps unvalidated numerics (`load`, `fan`, `vramUsed`/`vramTotal`, `ram*`,
    `disk*`) before using them in layout math, and sizes clock fields for the
    5 digits `vramClock` can carry.
11. ☐ Tolerates `limitW` absent while `watts` is present (the acquire-time limit read
    failed, or the sender is on the NVAPI fallback) **and** the mirror case, `limitW`
    present while `watts` and `power` are absent. Reconstructs `power` from
    `round(watts × 100 ÷ limitW)` only as a **cross-check**, expecting agreement within
    ±1 count at desktop-class enforced limits (≳ 100 W) and **±2 on low-limit boards** —
    and treating the cross-check as **advisory rather than a hard assertion** on very
    small or fractional-watt limits, where rounding both watt fields before dividing
    drifts further still (§4). Never a second gauge beside the one `power` already drives.
12. ☐ Implements a 5–10 s staleness timeout with last-good retention (no blanking on a
    single lost packet).
13. ☐ Treats missing `cpuTemp` as “no die/package reading” (including a deliberate
    ACPI-fallback omission), and missing `cpuLimitW` as normal on AMD.
14. ☐ Renders `av` per the tri-state table, including red for `2`.
15. ☐ Tolerates interleaved datagrams from multiple hosts (keys off `host`, falling
    back to source IP for pre-v5.6.0 senders).
16. ☐ Never replies to the sender — the protocol is strictly one-way. (The sender's
    socket does hold an ephemeral UDP port, but the app never reads from it; anything
    sent there is discarded.)

**Test vectors:** feed the consumer §2.1 (full), §2.2 (degraded), §2.3 (sparse), plus:
- Malformed: truncated JSON, empty datagram, non-JSON bytes → must be ignored, no crash.
- Unknown-key probe: `{"v":1,"cpuLoad":5,"up":9,"futureField":123}` → parses, ignores `futureField`.
- Escape probe: `{"v":1,"cpuLoad":5,"host":"CAF\u00C9-PC","up":9}` → host decodes and renders as `CAFÉ-PC` (or the device's best fallback glyph).
- Empty-string probe: `{"v":1,"cpuLoad":5,"gpu":"","up":9}` → renders gpu as unknown, no crash.
- Version probe: `{"v":2,"cpuLoad":5}` → ignored/flagged, not misrendered.
- Legacy probe: `{"v":1,"gpu":"NVIDIA GeForce RTX 3060","temp":55,"load":10,"vramUsed":2048,"vramTotal":12288,"fan":30}` (a v5.5.0-shaped datagram, no host) → renders GPU data, keys off source IP.
- Oversize-string probe: a datagram with a 100-byte `gpu` value (legal from pre-v5.7.1 senders) → string safely bounded, no overflow.
- Boundary sizes: a **732-byte** datagram (current worst case, which is also the ceiling) must parse; the ≥ 1024-byte buffer floor leaves 292 bytes of slack, so a consumer sized to the floor has ample room for its own framing overhead — but must still not assume a fixed size.
- Net-fields probe: `{"v":1,"netRx":0,"up":9}` → renders rx as a real 0 (idle), not as unknown; `{"v":1,"netName":"X","netType":0,"netLink":2500,"cpuLoad":5,"up":9}` → adapter row renders with no throughput values (last-good/`--`).

---

## 10. Security model

- **Cleartext, unauthenticated, unsigned.** Anyone on the subnet can sniff the metrics
  (including security posture: `av`, `fw`, `reboot`, `win`) or forge datagrams to the
  consumer. This is an accepted trade-off for a trusted office LAN display.
- **The trade-off is bounded to networks it can be made about.** The sender derives no
  destination at all unless its own address is RFC 1918, CGNAT, or link-local
  ([§1.1](#11-how-the-sender-finds-the-consumer)), so a PC on a routable public address
  sends nothing rather than streaming its posture to a stranger. This bounds *who can be
  reached*, not *what is protected*: on the local subnet the guarantees above are unchanged.
- Consumers MUST therefore treat every field as untrusted input: bound-check string
  copies, range-check numerics before using them in math (e.g. bar widths), and never
  execute/interpret payload content.
- The sender never reads from its socket and never binds a well-known port (its
  ephemeral send port exists but inbound data on it is simply discarded). There is no
  application-level path to reach the PC through this protocol.
- If forgery ever matters, mitigation belongs at the network layer (VLAN) or a future
  breaking protocol revision (`v: 2`) — do not bolt ad-hoc auth onto `v: 1`.

---

## 11. Sender-side behavior summary (for agents modifying the sender)

- The push loop is driven by a single 1 Hz `PeriodicTimer`; discovery briefly uses its
  own 60 s `PeriodicTimer` before the loop starts. (The tray app has unrelated UI
  timers — menu refresh, cooldowns — that never touch this protocol.) Slow data
  (av/fw/reboot) refreshes every 60th tick via a queued background task gated against
  pile-up; `host`/`cpu`/`win`/`diskTotal` are read once per session; GPU metrics come
  from a shared snapshot cache (950 ms TTL) that dedupes the push loop, the GPU
  Monitor window and the tray menu into one sweep per second.
- CPU die temperature, CPU package power and system-disk temperature are read on that
  same 1 Hz tick; `cpuLimitW` is cached at Intel provider initialization. `cpuTemp` is
  mapped only when `CpuTemperatureSource` is `IntelPackageMsr` or `AmdTctlSmn` — the
  ACPI board-zone fallback remains diagnostic-only so wire provenance cannot drift.
- The network fields ride the same tick for one `GetIfEntry2` call into a preallocated,
  reused buffer — no adapter enumeration, no allocation, no second timer. The adapter is
  resolved **once**, at push-loop initialization, as the same interface the display
  address is derived from (§1.1), and that probe read caches `netName`/`netType` and
  doubles as the throughput baseline, so the first datagram normally already carries
  `netRx`/`netTx`. The rate window is the RAPL discipline reused: measured Stopwatch
  intervals, 0.5–2 s acceptance band, always-advancing baseline, counter-decrease =
  reset. The managed `NetworkInterface` enumeration stays where it always was — on the
  once-per-discovery-attempt path, never per tick.
- **Two GPU backends, latched, never mixed per field** (v5.10.0+). The sweep acquires
  **NVML** (`Services/NvmlService.cs`, `nvml.dll`) first and falls back to **NVAPI**
  only if NVML will not initialize or will not hand out a device-0 handle; the choice is latched until the handle is dropped. Each
  metric's read routes on that latch, and a null NVML reading stays null for that tick
  and is retried on the next one rather than falling through to the other stack, so one
  datagram never blends two views of the board. The same two-strike handle-loss rule
  and the same 5 s rate-limited re-acquire cover both, and a re-acquire always re-tries
  NVML first — a driver restart invalidates both stacks, and NVML re-init is the cheap
  probe. `power` is the one field whose *meaning* shifts slightly with the backend
  (board vs chip domain — see §4).
- **Cadence tiers apply to the NVAPI fallback only** (`Services/SampledMetric.cs`):
  name once per session, `temp`/`load`/`clock`/`vramClock` every sweep, `vram*` every
  2 s, `fan`/`power` every 3 s; between reads the sweep re-serves the cached value, so
  the wire is unchanged either way. The tiers are driven by measured cost: the NVAPI
  power-topology read alone was 13.15 ms of a 16.4 ms sweep (80 %). NVML's reads cost
  ~0.02–0.3 ms each, leaving nothing to amortize, so on that backend **every** field is
  read on every sweep and the staleness table above collapses to one row. (The full
  measurement, the per-operation attribution table and the levers left unused were
  recorded in the originating project's `trayapp_perf.md`, which is not part of this
  repo.) A new GPU field still costs one
  read method plus one registry line — never a timer or a second per-tick call.
  (v5.12.0's `limitW` is the **recorded exception, not a precedent**: it needed **no**
  read at all, because the enforced limit was already cached at acquire time as the
  `power` percentage's denominator. That exemption is available only to data the sweep
  machinery already holds; anything that needs a driver read still goes through the
  registry.)
  A consumer that needs every field live can suspend the fallback's tiers via
  `GpuMonitorService.SetHighFidelity`, after which every field is read every sweep.
  Nothing in MetricsPusher currently holds high fidelity — the push loop is the only
  consumer — so the tiers above are always in effect on the fallback backend.
- Handle-loss detection works identically on both backends, on the reads that actually **executed** in a sweep: a sweep
  counts as lost when at least one read ran and every one that ran returned nothing.
  Cached values are deliberately not evidence of a live handle. **Two consecutive** lost
  sweeps are required before the driver handle is dropped — one is a suspicion, two is a
  verdict. A single-strike rule would drop a live handle on hardware whose per-sweep
  sensors legitimately report nothing (a vGPU whose utilization domain reports
  `IsPresent:false`; a probe that validated a GPU other than the one swept) every time
  only those sensors were due; the slower cadences answer on the very next sweep and
  clear the count, as does any healthy sweep. (That rescue case cannot arise on the
  NVML backend at all, where every read executes on every sweep — it is the fallback's
  cadence tiers that make an all-null sweep ambiguous in the first place.) On the
  second strike the handle is released, its cached values are discarded and a fresh
  acquire runs — §5 describes the degradation a consumer sees. Acquisition is
  rate-limited to once per 5 s, but the back-off is stamped at every *attempt*, the
  successful one included — so a handle that has lived longer than 5 s is re-acquired on
  the very next sweep, and the rate limit only bites when losses or acquisition failures
  come faster than 5 s apart. It does **not** imply a 5 s GPU blackout (see §5).
- Zero-impact is a hard project constraint: new fields must ride existing per-tick
  reads or slow caches. Adding any polling loop, timer, or per-tick syscall is a
  design regression — see `CLAUDE.md` and the v5.8.0 plan history.
- The 732-byte budget is enforced by a **worst-case unit test**
  (`BuildPayloadJson_ShouldFitDatagramBudget_WhenEveryFieldIsAtItsWorstCase`), which asserts
  the exact byte count AND the constant, so the two cannot drift apart. Since v5.10.0 the
  push loop also **checks every datagram at runtime** (`NoteOversizeDatagram`, one integer
  comparison per tick): an oversize datagram is still **sent** — truncating it would emit
  invalid JSON and dropping it would blank the display — but it logs one edge-triggered
  warning per oversize streak, so an overrun that escaped the unit test is findable in the
  field instead of silently truncating at a consumer's buffer.
  The worst case still **equals** the ceiling (732/732, §3.3) — but the receiver floor was
  renegotiated to ≥ 1024 in v5.12.0, so 292 bytes now sit between them. The next field is
  therefore a **sender-side change again**: raise the constant, re-pin the worst-case test
  and extend §3.3 in one commit; only a total approaching 1024 reopens the receiver contract.
- Only `temp`, `cpuTemp` and `nvmeTemp` (via `Constants.IsValidTemperature`), `power`, `watts`
  (< 2000 W), `limitW` (0 < x < 2000 — zero excluded, unlike `watts`), `clock` and
  `vramClock`, `cpuWatts` (0 < x ≤ 1000 before whole-watt rounding), `cpuLimitW`
  (the same band plus zero excluded after rounding), `netLink` (0 < x ≤ 400000 Mbps,
  with the driver's zero and all-ones "no answer" encodings dropped) and
  `netRx`/`netTx` (0 ≤ x ≤ 100000000 kbit/s, validated independently per direction)
  are range-validated before sending, and `cpuLoad`
  is clamped to 0–100 (guarding the int cast; PDH already caps at 100); `av`/`fw` are
  mapped enumerations. Everything else is passed through as read. Note the validators
  **drop** an implausible value (key absent) rather than clamping it — including a
  `power` percentage above 200, which is checked before rounding. `power`, `watts` and
  `limitW` are validated independently, so any one can be dropped while the others
  survive (§4).
- Field-order, null-omission, truncation, suppression, and formatting are all pinned by
  `MetricsPusher.Tests/GpuDisplayPushServiceTests.cs` — change behavior there first
  (red), then in the code (green).
- When adding a field: add it to the DTO (`GpuDisplayPayload`) and to the mapping in the
  private `BuildPayload`, which is the single source both `BuildPayloadJson` (tests) and
  `BuildPayloadUtf8` (the send path) project from — a test pins their bytes identical, so
  the mapping must never be duplicated into one of them. Then decide live-vs-ambient for
  the suppression guard, extend the exact-string / null-omission / worst-case tests, and
  append to §3.1 and §4 here. A GPU-side field must also be produced on **both**
  backends (or be explicitly documented as absent on the fallback) and pick a
  `SampledMetric` cadence tier for the fallback. Do **not** bump `v` for additive
  fields. **Budget check:** the current worst case is 732 of a 732 ceiling — no slack
  there, so any new field means raising `MaxDatagramBytes` and re-pinning the worst-case
  test in the same commit — but 292 bytes remain to the ≥ 1024-byte receiver-buffer
  floor, so that raise is a sender-side change and does not reopen the receiver
  contract until the total approaches 1024 (§3.3).

---

## 12. Authoritative sources in this repo

| File | What it defines |
|---|---|
| `Services/GpuDisplayPushService.cs` | Wire DTO (`GpuDisplayPayload`), field order, serializer options, suppression guard, truncation (encoded-byte caps, surrogate-safe), budget constants, discovery, push loop, send-failure recovery. The mapping lives in the private `BuildPayload`; `BuildPayloadJson` (string, what the tests pin) and `BuildPayloadUtf8` (the UTF-8 bytes actually sent) are two projections of it, pinned byte-identical by a test |
| `Services/GpuMonitorService.cs` | GPU metrics for the primary GPU only: the NVML-first / NVAPI-fallback backend latch and its acquire/drop machinery, the acquire-time enforced-limit cache and its `limitW` publication, the power-percent derivation, power ≤ 200 %, watts < 2000 W, limitW 0 < x < 2000 (zero excluded) and clock < 10000 / vramClock < 20000 MHz validation, 950 ms snapshot cache, executed-reads handle-loss detection, the per-metric cadence registry and the high-fidelity override |
| `Services/NvmlService.cs` | The NVML interop layer itself: entry points, struct layouts, unit conversion at the boundary (bytes → MiB, raw milliwatts kept raw), per-getter null-on-failure, and the one-per-session edge-triggered diagnostic (never consumed by NOT_SUPPORTED) |
| `Services/SampledMetric.cs` | The cadence primitive itself: session / every-sweep / interval sampling, latch-on-success for session reads, and the executed-read flags the handle-loss rule counts |
| `Services/SystemMetricsService.cs` | CPU/RAM/disk/OS-health collection: PDH counter (primed baseline, latched structural failure), `GlobalMemoryStatusEx`, `DriveInfo`, Windows-version formatting (incl. `Srv` detection), WSC antivirus and firewall mapping (incl. S_FALSE→POOR), reboot-pending detection, background refresh with pile-up gate |
| `Services/CpuTemperatureService.cs`, `Services/CpuTemperatureProviders.cs`, `Services/CpuPackagePowerProvider.cs` | CPU source selection and provenance, Intel package/AMD Tdie decoding, RAPL package draw and Intel-only package limit, including validation and absence behavior |
| `Services/NvmeTemperatureService.cs` | System-volume-to-disk resolution, the two Windows storage temperature query tiers, validation and session latching |
| `Services/NetworkThroughputService.cs` | The `net*` fields: one-time adapter resolution and identity caching (description with marks stripped, media-type mapping), the MIB_IF_ROW2 layout and its offset self-check, per-tick GetIfEntry2 read, link-speed validation, and the throughput rate window (measured intervals, 0.5–2 s band, counter-reset re-baseline, 100 Gbit/s cap) |
| `Services/LocalNetworkService.cs` | The one adapter-selection walk (first up interface with an IPv4 gateway and address): feeds both display-address derivation and the network sensor's interface index, so the two cannot disagree |
| `Constants.cs` | UDP port (4210), display host octet (99), discovery attempts/interval/ping timeout, temperature validation bounds (0–150 °C) |
| `MetricsPusher.Tests/GpuDisplayPushServiceTests.cs` | The pinned wire contract: exact JSON, omission, truncation, budget worst-case |
| `MetricsPusher.Tests/SystemMetricsServiceTests.cs` | Windows-version, AV-mapping, firewall-mapping and reboot-detection semantics |
| `TrayApplicationContext.cs` | When the push service starts (GPU detected at startup or later) |

---

*Document version: 1.19 (2026-08-12, sender MetricsPusher v1.0.1). v1.19: **five additive network fields, protocol `v` remains `1`; sender released as v1.0.1.** Added `netName`, `netType`, `netLink`, `netRx` and `netTx` — identity, media type, negotiated link speed and rx/tx throughput of the **primary adapter**, defined as the same interface the sender already derives the display address from (§1.1, deployment hazard inherited deliberately). `netRx`/`netTx` are **live** for the §7 suppression guard (a wire-visible classification; zero is a real idle reading), while `netName`/`netType`/`netLink` are **ambient** for the same reason the limit fields are. Rate semantics reuse the RAPL discipline `cpuWatts` established — measured interval, 0.5–2 s acceptance band, always-advancing baseline — plus a counter-decrease-is-reset rule, but unlike `cpuWatts` the rates are normally present from the **first** datagram, because the adapter probe doubles as their baseline (§2.1, §11). `netName` shares the 63-encoded-byte identity cap and has trademark marks (`(R)`, `(C)`, `(TM)`) stripped; the `cpu` field's own strip list gains `(C)` in the same change — a value-level cleanup on the rare machines whose ProcessorNameString carries it, not a retype. Worst case and ceiling 591 → **732** under the unchanged ≥ 1024-byte receiver floor (292 bytes of slack); §2.1 recaptured on real hardware (481 bytes, all five network fields present, `round(26 × 100 ÷ 477) = 5` re-derived). Updated §§1–9, §11, §12. The sender version becomes **1.0.1**, releasing the CPU/NVMe fields (v1.17) and these network fields together; §3.1/§3.3 rows renamed from "MetricsPusher next" to v1.0.1 accordingly. v1.18: **§2.1 recaptured on the current schema; no wire change.** The full-datagram example was still the v5.12.0 capture and therefore carried none of the four new keys. It is now a real 384-byte capture from the live NVML backend on an idle Ryzen 9 9950X + RTX 3090 Ti, carrying `cpuTemp":64.875` (AMD Tdie's 0.125 °C step), `"cpuWatts":43` and `"nvmeTemp":48` (whole °C from the tier-1 storage property) — nothing spliced. `cpuLimitW` is **absent by design** there because the machine is AMD, so §2.1 now states that no single real capture can show all four new fields at once, and that the decimal formatting of the three temperature keys says nothing about which provider produced them (§4.1). The harness note now also records that a real sender's first datagram carries `cpuLoad` but not `cpuWatts` (the first RAPL sample is only a baseline), and the `power`/`watts`/`limitW` paragraph is re-derived against the new numbers (28 W of 477 W = 6 %) with the reminder that `cpuWatts` has no denominator beside it on AMD. §3's document-version row, stale at `1.16` since v1.17, now tracks this footer. v1.17: **four additive fields, protocol `v` remains `1`.** Added die/package-only `cpuTemp`, RAPL `cpuWatts`, Intel-only `cpuLimitW`, and system-disk `nvmeTemp`; the ACPI board-zone fallback is deliberately omitted from `cpuTemp`. The exact worst case and sender ceiling move 522 → **591** under the unchanged ≥ 1024-byte receiver floor. Updated §§3–9, §11 and §12. v1.16: **first tagged release; two behavioural changes, no payload change.** The key set, the value shapes, the 522-byte budget and protocol `v: 1` are all untouched, and a consumer needs no change. **§3 now states the rule the changelog below had only ever implied:** the protocol version (`v`), the sender's release version, and this document's version are three independent numbers, none derivable from another, and only `v` is on the wire or says anything about compatibility. Two sender behaviours are newly recorded. (1) **§1.1 / §10: a destination is only derived on a private network** — RFC 1918, RFC 6598 CGNAT, or RFC 3927 link-local. This does not alter any datagram; it bounds *whether* one is sent, and only on a PC holding a non-private IPv4, where the trusted-subnet premise §10 rests on does not hold in the first place. Note that a LAN numbered outside those ranges (squat space such as `25/8`) now sends nothing where it previously sent. (2) The sender now loads `nvml.dll` **only** from an absolute `%WINDIR%\System32` path and will not search elsewhere for it, which closes a DLL search-order hijack. On a machine where NVML previously resolved from somewhere else — an old pre-R450 driver with `NVSMI\` on `PATH` — the sender falls back to NVAPI, which makes `watts` and `limitW` **structurally absent** exactly as §4 and §5 already describe for that backend; the absence is not new, but this is a new way to reach it. v1.15: **provenance, no wire change.** The sender was extracted from the originating multi-purpose tray app (v5.12.1) into MetricsPusher, a standalone tray app whose only job is this feed; the metrics and push code came across verbatim apart from namespaces, so **every byte of the contract below is unchanged** and protocol `v` stays `1`. Renamed `trayapp_metrics.md` → `push_metrics.md`. §11 and §12 re-pointed at the new file layout (`NetworkDiagService.cs` → `LocalNetworkService.cs`, `MetricsPusher.Tests/` → `MetricsPusher.Tests/`); the perf investigation log (`trayapp_perf.md`) stayed behind in the originating project and §11 now says so instead of linking it. High fidelity (§11) is no longer held by anything — the GPU Monitor window did not come across — so the NVAPI fallback's cadence tiers are now always in effect. Everything below this line describes the sender's history before the extraction. v1.14: accuracy pass, no wire change — the ±1 reconstruction bound stated in §2.1/§4/§8.4/§9 is now scoped to desktop-class enforced limits (≳ 100 W; on low-limit boards the double rounding of the two watt fields can drift a second count, so the cross-check is ±2 there — 20499 mW against a 35 W limit gives sender `59` against a wire reconstruction of `57`), and §4's `watts` row now says explicitly that its "never disagree" claim is about the sender's raw milliwatt pair, not the rounded wire values. **±2 is itself not a floor:** on very small or **fractional-watt** limits the limit's own rounding becomes a percent-scale denominator error that grows with load (a 34.5 W limit rounds to `34`, so a 39500 mW draw gives sender `114` against a reconstruction of `118`), so §4 and §9 tell a consumer to treat the cross-check as **advisory** rather than a bounded assertion there. No test changes: the pinned fixtures and the live capture are all desktop-class. v1.13: the new `limitW` field — the GPU's enforced power limit, the denominator `power` was already being divided by — joins the wire for **zero** new driver reads: the value was already cached at acquire time, so §11 records it as the one **exception** to the "a new GPU field costs one read method plus one registry line" rule (available only to data the sweep machinery already holds). §2.1 is a real v5.12.0 capture taken from the live NVML backend on an idle RTX 3090 Ti (338 bytes, `"limitW":477` immediately after `"watts":23`, `round(23 × 100 ÷ 477) = 5 = power`) — unlike the v5.10.0 example nothing in it is spliced, so it is a genuine second-tick datagram rather than a first-tick one. Worst-case datagram 508 → **522** and the ceiling raised with it (still equal, no slack there), but the **receiver floor was renegotiated ≥ 512 → ≥ 1024** after the reference ESP32's buffers went 496 → 1024, leaving 502 bytes of headroom — so the next field is a sender-side change again. `limitW` is **ambient** for the suppression guard (acquire-time state, always present on NVML) and validated **independently** of `power`/`watts` with a cap that **excludes zero**; it is **structurally absent on the NVAPI fallback**, like `watts`. Sections updated: §1–§9, §11, §12. Protocol `v` is NOT bumped: the field is additive and consumers ignore unknown keys. v1.12: two § corrections from the final re-review. §3.3's temperature note had it backwards: the pinned 508 is measured with a **6-byte fractional** `105.75`, so it already carries ~3 bytes more temperature than any real integral reading — that cushion is the load-bearing part, and the residual exposure is only a backend formatting wider than 6 bytes (validator-widest `149.12344` = 9), which would force a recomputation. §6: v1.11 removed `gpu` from the session row without re-homing it, leaving the only wire data key with no worst-case age; it is now in the NVML "all GPU fields" row and has its own session row in the fallback table, consistent with §4 and §5. No wire-visible change. v1.11: accuracy pass from the dedicated wire-contract audit before the v5.10.0 release build. §1.3 send failures also cover a disposed socket; §3.3 records that the 508 worst case assumes whole-degree temperatures (fractional would add ~3 bytes and force a recomputation, since nothing is left to absorb it); §4 gives `gpu` the same NVML/fallback cadence split the other rows have, notes that `watts` is the one thing the fallback cannot produce, and documents the validation asymmetry (a draw ≥ 2000 W is dropped while its ratio can still pass, and vice versa); §5 now names **three** failure lifetimes instead of two — per-tick, session-cached and permanent/structural (`watts` on fallback senders, `fan` on boards without an exposed fan) — which is what its own table always described; §6 harmonizes the snapshot-age prose with the strict `<` the freshness check uses; §8.2 corrects the key count 21 → 23; §11 adds `watts` to the validated-field list, states that NVML "will not initialize or hand out a device-0 handle" rather than "will not load", and records the **new runtime oversize-datagram guard** (edge-triggered warning, datagram still sent); §12 adds the watts cap. No wire-visible change — key set, value shapes and the 508 budget are untouched, so no protocol `v` bump. v1.10: correction — §4's `power` row still ended with "Watts are still not on the wire", contradicting the `watts` row directly beneath it in the same table; the clause was removed in the parallel `trayapp_perf.md` row but missed here. It now points at `watts` as the raw reading the percentage is derived from. No wire-visible change. v1.9: the new `watts` field (GPU board power draw in whole watts, own exclusive 2000 cap) publishes the raw reading the `power` percentage was already derived from — one NVML read, two fields, so they can never describe different instants; §2.1 recaptured, §3.1/§3.2 extended, **new §3.3** records the budget history and states plainly that the 496 → 508 renegotiation spent the margin (worst case now EQUALS the ceiling, 4 bytes under the ≥ 512-byte receiver floor, so the next field needs the reference consumer's buffer raised first), §4 gains the `watts` row, §5 documents its structural absence on NVAPI-fallback senders (unlike every other absence, which is transient) and that `watts` survives a failed enforced-limit read that suppresses `power`, §6/§7/§8.2/§8.3/§8.4/§9/§11 updated. Protocol `v` is NOT bumped: the field is additive and consumers ignore unknown keys. v1.8: accuracy pass after the v5.10.0 branch review — §4's `vramTotal` note claimed a backend difference that neither capture supports (both report `24564`); the real difference is in `vramUsed`, where NVML reports the driver's `used` and NVAPI computes `total − currentAvailableDedicated`, so the note moved to that row. §7.1: the startup availability probe now asks NVML when NVAPI declines, so machines only NVML can read (TCC/vGPU) send where they previously did not. No wire-visible change — the key set, the value shapes and the budget are untouched, so no protocol `v` bump. v1.7: the sender's GPU metrics now come from NVML with NVAPI as fallback, and the new `vramClock` (GPU memory clock, MHz, own 20000 cap) rides that change — §2.1 recaptured, §3.1/§3.2 extended, §4 gains the `vramClock` row plus backend-dependent cadences and the board-vs-chip `power` semantic note, §4.1 warns that the two clock fields have different digit widths, §5 splits the per-cadence absences by backend and documents the NOT_SUPPORTED fan and the session-cached power-limit denominator, §6 collapses to one GPU row on NVML with the fallback tiers kept as a second table, §7/§8.3/§8.4/§9 updated for the new field, §11 describes the backend model and the 495/496 budget, §12 adds `NvmlService.cs`. Protocol `v` is NOT bumped: the field is additive and consumers ignore unknown keys. v1.6: correction — §11's handle-loss bullet still stated the superseded one-strike rule, contradicting §5 and the sender code; it now describes the two-strike behavior (and why it is two) in §5's terms. §11 also links the perf investigation log (`trayapp_perf.md`) behind the cadence tiers. No wire-visible change. v1.5: accuracy pass after the v5.9.0 branch review — handle loss now takes two consecutive all-null sweeps, so §5's GPU-blackout row describes the two-step degradation it actually produces; §11 points at the shared private `BuildPayload` (not `BuildPayloadJson`) and notes that a new GPU field must pick a cadence tier; §12 covers `BuildPayloadUtf8`; sender version and the 13.15/16.4 ms share (80 %, was 78 %) corrected. v1.4: GPU reads are now cadence-tiered (`gpu` session, `temp`/`load`/`clock` every sweep, `vram*` 2 s, `fan`/`power` 3 s) and the snapshot TTL is 950 ms, not 500 — no field added, removed or retyped, so no protocol `v` bump; §4 cadences, §5 absence lifetimes, §6 staleness bounds (new per-field worst-case table), §11 sender behavior and §12 sources updated accordingly. v1.3: accuracy pass after a doc-vs-code verification — `fw` added to the §6 refresh-cadence and §10 security-posture lists, §11 validation summary now covers the `cpuLoad` clamp and `av`/`fw` mapping, §2.3 sender reference updated. v1.2: added the `fw` (Windows Firewall status) field; worst-case datagram 470 → 477 bytes. v1.1: 21 accuracy corrections and 8 additions after an independent doc-vs-code verification pass. Update this file in the same commit as any wire-visible change.*
