# HANDOVER — RTSP/RTP knobs, "toggles + Advanced" UI ✅ CODE-COMPLETE

**STATUS (2026-06-05): DONE — all tasks 1–8 complete.** Both the C# layers AND the web layer are applied, and
all 8 verification gates passed (build 0/0, grep-clean, els↔id + gather↔snapshot parity, old-file round-trip
reasoned, probesize sentinel, toggle behaviour; bonus `node --check web/app.js` OK). The last Config.cs
doc-comment that still named the dropped booleans was reworded, so the tree is fully grep-clean.
**Only Phase-6 field tests remain** (real camera rig + GPU — see "Out of scope" below). The OLD→NEW edit
blocks below are kept for the historical record; everything in them is already applied.

**Project.** `C:\Users\gregor\Downloads\Dev\mpv` — MpvGrid, .NET 9 WinForms 2×2 camera wall on libmpv
(pinned `libmpv-2.dll`, client API 2.5 / mpv 0.41.0). Namespace `MpvGrid`, interop ns `MpvGrid.Mpv`.
**No git** (locked) — the verification gates ARE the safety net. Build: `dotnet build MpvGrid.csproj`.

---

## ⚠️ Critical re-anchoring finding (why you must not use the old execution doc verbatim)

`HANDOVER-rtsp-rtp-execution.md` is **materially STALE**. It assumes four "dead knobs" (`rtsp-http-port`,
`rtp-max-src`, `clock-jitter`, `clock-synchro`) still exist — they were **deleted** in the 2026-06-05 cleanup.
Current source has **none** of them, and `SettingsSnapshot` now also carries the **Backoff fields** the doc
never mentioned. All edits in THIS doc are already re-anchored to the real current code — trust this doc, not
the old one.

---

## What this feature is

5 new stream-handling knobs, each mapped to a REAL dll-verified mpv/ffmpeg option, wired through the 5-layer
contract (Config → SettingsSnapshot → FeedController apply → RenderApp snapshot/apply → web). Plus a UI the
user explicitly designed: **"toggles + Advanced"** (chosen over raw-numbers and over latency-presets).

| # | Option | Config field (default · clamp) | Wire key | Scheme |
|---|---|---|---|---|
| 1 | `network-timeout` | `int NetworkTimeoutSec` (60 · 0..3600; 0=no timeout) | `networkTimeoutSec` | all |
| 2 | `demuxer-lavf-analyzeduration` | `int DemuxAnalyzeDurationMs` (0 · 0..60000; 0=default) | `demuxAnalyzeDurationMs` | all |
| 3 | `demuxer-lavf-probesize` | `int DemuxProbesizeBytes` (0 · 0..100000000; **0→emit 5000000**) | `demuxProbesizeBytes` | all |
| 4 | `rtsp-transport` | `string RtspTransport` ("lavf" · {lavf,udp,tcp,http,udp_multicast}) | `rtspTransport` | rtsp only |
| 5 | `reorder_queue_size` via `demuxer-lavf-o` | `int ReorderQueueSize` (0 · 0..10000; 0=don't emit) | `reorderQueueSize` | rtsp+rtp |

**Verified facts (do NOT re-derive):** `probesize=0` is REJECTED by libmpv (rc -6) → emit `5000000` for the
0/default case, never `0`. `network-timeout=0` and `analyzeduration=0` ARE valid. `rtsp-transport` accepts all
5 values. `reorder_queue_size=N` accepted as an ffmpeg AVOption passthrough. Wire JSON is
`PropertyNameCaseInsensitive`, so JS camelCase ↔ C# PascalCase matches automatically.

**RtspOverTcp + RtspHttpTunnel are DROPPED entirely** (no migration; old streams.json keys are ignored on load,
feed defaults to lavf — user accepted the silent revert; lavf auto-falls-back UDP→TCP so it's low-risk).

---

## UI design — "toggles + Advanced" (user-approved)

The two probe knobs and the jitter knob are **geeky numbers**, so the dashboard hides them behind friendly
toggles; the raw numbers live under an `<details>` "Advanced" expander. **The numbers are the single source of
truth** (`gatherSettings` reads the numbers, NOT the toggles). A toggle is pure sugar:
- Flip **Faster startup** ON → write preset into the two raw fields; OFF → write 0/0.
- Flip **Smooth choppy video** ON → write preset into the jitter field; OFF → write 0.
- Editing a raw Advanced field re-derives the toggle's checked state (`value>0`).
- On latch (render), toggle.checked is derived from the snapshot values.

**Locked UI decisions (user said "get started" = accept recommendations):**
- **Probe size shown in KB** in the UI (C# field stays bytes; app.js multiplies ×1000 on the wire, ÷1000 on display).
- **Toggle presets (starting values — FLAG as the #1 Phase-6 field-test item, esp. jitter):**
  `PRESET_ANALYZE_MS = 500`, `PRESET_PROBE_KB = 500`, `PRESET_JITTER_PKTS = 50`.
- **Transport dropdown labels:** `Auto (recommended)` / `UDP` / `TCP` / `HTTP (firewall tunnel)` /
  `UDP multicast (rare)`.
- **Panel placement** (minimal churn to the existing settings panel):
  - `network-timeout` → settings-grid #1, **right after `net-cache`** (the two "network" knobs together).
  - **Faster startup** + **Smooth choppy video** toggles → `settings-toggles`, **before** `skip-loop`
    (replacing the removed rtsp-tcp/rtsp-http switches' slot). Order: faster, smooth, skip-loop, hw-decode.
  - Transport section main grid: `rtsp-transport` select + `rtp-timeout` (kept visible).
  - **Advanced `<details>`** after that grid: `analyze-duration` (Startup wait, ms), `probesize`
    (Startup data, KB), `reorder-queue` (Jitter buffer, pkts), **and `rtsp-frame-buffer` moved into it**
    (raw bytes — geekiest; main surface stays clean).

---

## ✅ DONE (tasks 1–5) — do not redo

- **Config.cs** — removed `RtspOverTcp` + `RtspHttpTunnel`; added `NetworkTimeoutSec=60`,
  `DemuxAnalyzeDurationMs=0`, `DemuxProbesizeBytes=0` (engine section), `RtspTransport="lavf"`,
  `ReorderQueueSize=0` (transport section); reworded section comments; added all clamps + RtspTransport
  validation in `Normalize()`.
- **ControlProtocol.cs** — `SettingsSnapshot`: removed the 2 bools; added `NetworkTimeoutSec`,
  `DemuxAnalyzeDurationMs`, `DemuxProbesizeBytes`, `string RtspTransport="lavf"`, `ReorderQueueSize`
  (Backoff fields untouched).
- **FeedController.cs** — `ApplyStreamOptions`: added `network-timeout`, `demuxer-lavf-analyzeduration`
  (ms→s; 0→"0"), `demuxer-lavf-probesize` (0→"5000000"); replaced the rtsp if/elif/else with a single
  `if (isRtsp) client.SetProperty("rtsp-transport", _config.RtspTransport)`; added `reorder_queue_size` to the
  `lavf` list gated `(isRtsp || isRtp)`. `using System.Globalization;` already present.
- **RenderApp.cs** — `BuildSnapshot` mirrors the 5 fields (dropped the 2 bools); `ApplySettings`
  `perMediaChanged` predicate + copy-into-Config block updated to the 5 fields. (XML-doc already accurate.)

---

## ⏳ REMAINING — tasks 6–8 (web layer + verify)

### TASK 5 — `RenderApp.cs` (3 edits) — ✅ DONE this session (kept for reference; skip)

**5A. `BuildSnapshot` → `snap.Settings`.**
OLD:
```csharp
        snap.Settings = new SettingsSnapshot
        {
            RtspOverTcp = _config.RtspOverTcp,
            NetworkCachingMs = _config.NetworkCachingMs,
            HwDecode = _config.HwDecode,
            SkipLoopFilter = _config.SkipLoopFilter,
            ExtraMpvArgs = new List<string>(_config.ExtraMpvArgs),
            RtspHttpTunnel = _config.RtspHttpTunnel,
            RtspFrameBufferSizeBytes = _config.RtspFrameBufferSizeBytes,
            RtpTimeoutSec = _config.RtpTimeoutSec,
            BackoffStartMs = _config.BackoffStartMs,
```
NEW:
```csharp
        snap.Settings = new SettingsSnapshot
        {
            NetworkCachingMs = _config.NetworkCachingMs,
            NetworkTimeoutSec = _config.NetworkTimeoutSec,
            HwDecode = _config.HwDecode,
            SkipLoopFilter = _config.SkipLoopFilter,
            ExtraMpvArgs = new List<string>(_config.ExtraMpvArgs),
            DemuxAnalyzeDurationMs = _config.DemuxAnalyzeDurationMs,
            DemuxProbesizeBytes = _config.DemuxProbesizeBytes,
            RtspTransport = _config.RtspTransport,
            RtspFrameBufferSizeBytes = _config.RtspFrameBufferSizeBytes,
            RtpTimeoutSec = _config.RtpTimeoutSec,
            ReorderQueueSize = _config.ReorderQueueSize,
            BackoffStartMs = _config.BackoffStartMs,
```
(The `BackoffMaxMs … StallTimeoutMs` lines after `BackoffStartMs` are unchanged.)

**5B. `ApplySettings` → `perMediaChanged` predicate.**
OLD:
```csharp
        bool perMediaChanged =
            s.NetworkCachingMs != _config.NetworkCachingMs ||
            s.RtspOverTcp != _config.RtspOverTcp ||
            s.SkipLoopFilter != _config.SkipLoopFilter ||
            s.RtspHttpTunnel != _config.RtspHttpTunnel ||
            s.RtspFrameBufferSizeBytes != _config.RtspFrameBufferSizeBytes ||
            s.RtpTimeoutSec != _config.RtpTimeoutSec;
```
NEW:
```csharp
        bool perMediaChanged =
            s.NetworkCachingMs != _config.NetworkCachingMs ||
            s.NetworkTimeoutSec != _config.NetworkTimeoutSec ||
            s.SkipLoopFilter != _config.SkipLoopFilter ||
            s.DemuxAnalyzeDurationMs != _config.DemuxAnalyzeDurationMs ||
            s.DemuxProbesizeBytes != _config.DemuxProbesizeBytes ||
            s.RtspTransport != _config.RtspTransport ||
            s.RtspFrameBufferSizeBytes != _config.RtspFrameBufferSizeBytes ||
            s.RtpTimeoutSec != _config.RtpTimeoutSec ||
            s.ReorderQueueSize != _config.ReorderQueueSize;
```

**5C. `ApplySettings` → copy-into-Config block.**
OLD:
```csharp
        // Copy every field into the shared config (the timings apply with no reconnect at all).
        _config.NetworkCachingMs = s.NetworkCachingMs;
        _config.RtspOverTcp = s.RtspOverTcp;
        _config.SkipLoopFilter = s.SkipLoopFilter;
        _config.HwDecode = s.HwDecode;
        _config.ExtraMpvArgs = new List<string>(s.ExtraMpvArgs);
        _config.RtspHttpTunnel = s.RtspHttpTunnel;
        _config.RtspFrameBufferSizeBytes = s.RtspFrameBufferSizeBytes;
        _config.RtpTimeoutSec = s.RtpTimeoutSec;
        _config.BackoffStartMs = s.BackoffStartMs;
```
NEW:
```csharp
        // Copy every field into the shared config (the timings apply with no reconnect at all).
        _config.NetworkCachingMs = s.NetworkCachingMs;
        _config.NetworkTimeoutSec = s.NetworkTimeoutSec;
        _config.SkipLoopFilter = s.SkipLoopFilter;
        _config.HwDecode = s.HwDecode;
        _config.ExtraMpvArgs = new List<string>(s.ExtraMpvArgs);
        _config.DemuxAnalyzeDurationMs = s.DemuxAnalyzeDurationMs;
        _config.DemuxProbesizeBytes = s.DemuxProbesizeBytes;
        _config.RtspTransport = s.RtspTransport;
        _config.RtspFrameBufferSizeBytes = s.RtspFrameBufferSizeBytes;
        _config.RtpTimeoutSec = s.RtpTimeoutSec;
        _config.ReorderQueueSize = s.ReorderQueueSize;
        _config.BackoffStartMs = s.BackoffStartMs;
```
(The `BackoffMaxMs … StallTimeoutMs` copies after `BackoffStartMs` are unchanged.)
> NOTE: the `ApplySettings` XML-doc already says "cache-secs, rtsp-transport, skiploopfilter" — already
> accurate, no comment edit needed.

---

### TASK 6 — `web/index.html` (3 edits)

**6A. settings-grid #1 — add `network-timeout` right after `net-cache`.** Insert a new `.setting` between
the net-cache `</div>` and the `stall` `.setting`.
OLD (boundary):
```html
            <input type="number" id="net-cache" min="0" max="60000" step="50"
                   title="Network caching buffer (milliseconds).&#10;How much incoming video mpv buffers before showing it on screen.&#10;Higher = smoother playback on a flaky/busy network, but more delay behind real time.&#10;Lower = closer to live, but more likely to stutter if the network hiccups.&#10;Default: 10000 (10 seconds) — a deep buffer that rides out a poor link.  Range: 0–60000 ms." />
          </div>
          <div class="setting">
            <label for="stall" title="Stall (freeze) timeout (milliseconds).
```
NEW:
```html
            <input type="number" id="net-cache" min="0" max="60000" step="50"
                   title="Network caching buffer (milliseconds).&#10;How much incoming video mpv buffers before showing it on screen.&#10;Higher = smoother playback on a flaky/busy network, but more delay behind real time.&#10;Lower = closer to live, but more likely to stutter if the network hiccups.&#10;Default: 10000 (10 seconds) — a deep buffer that rides out a poor link.  Range: 0–60000 ms." />
          </div>
          <div class="setting">
            <label for="network-timeout" title="Network timeout (seconds).&#10;How long the engine waits for a stream to start sending data before giving up on the connection — covers a camera that's unplugged or unreachable, where connecting would otherwise hang. The stall timeout only covers a stream that was already playing; this covers one that never starts.&#10;Lower = notices a dead camera sooner; higher = more patient on a slow link.&#10;Set to 0 to never time out. Applies to every feed.&#10;Default: 60.  Range: 0–3600 s.">Net timeout <span class="unit">s</span></label>
            <input type="number" id="network-timeout" min="0" max="3600" step="1"
                   title="Network timeout (seconds).&#10;How long the engine waits for a stream to start sending data before giving up on the connection — covers a camera that's unplugged or unreachable, where connecting would otherwise hang. The stall timeout only covers a stream that was already playing; this covers one that never starts.&#10;Lower = notices a dead camera sooner; higher = more patient on a slow link.&#10;Set to 0 to never time out. Applies to every feed.&#10;Default: 60.  Range: 0–3600 s." />
          </div>
          <div class="setting">
            <label for="stall" title="Stall (freeze) timeout (milliseconds).
```

**6B. `settings-toggles` — delete `rtsp-tcp` + `rtsp-http`; add `faster-startup` + `smooth-video` before `skip-loop`.**
OLD:
```html
        <div class="settings-toggles">
          <label class="switch" title="Receive RTSP camera streams over TCP instead of UDP.&#10;OFF (default) = UDP, matching the engine default: it negotiates UDP and auto-falls back to TCP on its own, which is what lets most cameras connect unattended.&#10;ON = force interleaved TCP — steadier on a lossy/congested link, but a UDP-only camera may then fail to connect at all. Turn ON only for a source that needs it (e.g. behind a NAT/firewall that blocks UDP).&#10;Only affects 'rtsp://' sources.&#10;Default: OFF (UDP).">
            <input type="checkbox" id="rtsp-tcp" />
            <span class="switch-track"><span class="switch-thumb"></span></span>
            <span class="switch-label">RTSP over TCP</span>
          </label>
          <label class="switch" title="Tunnel RTSP (and its video) inside HTTP.&#10;Use this when a firewall only allows ordinary web (HTTP) traffic out and blocks the normal camera ports. Implies TCP, so it overrides 'RTSP over TCP' when on.&#10;Slightly more overhead than plain TCP — leave OFF unless a stream won't connect any other way. Only affects 'rtsp://' sources.&#10;Default: OFF.">
            <input type="checkbox" id="rtsp-http" />
            <span class="switch-track"><span class="switch-thumb"></span></span>
            <span class="switch-label">RTSP over HTTP (tunnel)</span>
          </label>
          <label class="switch" title="Skip the H.264 de-blocking 'loop filter' while decoding.&#10;Lowers CPU/GPU load per stream — handy when running several feeds at once or on a weaker PC — at the cost of slightly softer, more 'blocky' video.&#10;Affects picture quality only; it does not change reliability.&#10;Default: OFF.">
```
NEW:
```html
        <div class="settings-toggles">
          <label class="switch" title="Faster startup.&#10;Make cameras appear on screen quicker by letting the engine spend less time inspecting each stream before it plays.&#10;ON = lower connect latency; very occasionally an unusual stream is misread.&#10;Fine-tune the exact values under 'Advanced' (Startup wait / Startup data) — turning this off resets them.&#10;Applies to every feed.&#10;Default: OFF.">
            <input type="checkbox" id="faster-startup" />
            <span class="switch-track"><span class="switch-thumb"></span></span>
            <span class="switch-label">Faster startup</span>
          </label>
          <label class="switch" title="Smooth choppy video.&#10;Buffer and re-order incoming video packets to iron out stutter on a network that delivers them out of order or in bursts — at the cost of a little extra delay.&#10;Set the exact buffer depth under 'Advanced' (Jitter buffer); turning this off resets it.&#10;Affects 'rtsp://' and 'rtp://' feeds.&#10;Default: OFF.">
            <input type="checkbox" id="smooth-video" />
            <span class="switch-track"><span class="switch-thumb"></span></span>
            <span class="switch-label">Smooth choppy video</span>
          </label>
          <label class="switch" title="Skip the H.264 de-blocking 'loop filter' while decoding.&#10;Lowers CPU/GPU load per stream — handy when running several feeds at once or on a weaker PC — at the cost of slightly softer, more 'blocky' video.&#10;Affects picture quality only; it does not change reliability.&#10;Default: OFF.">
```

**6C. Transport section — replace the whole block.** Adds `rtsp-transport` select; keeps `rtp-timeout`;
moves `rtsp-frame-buffer` into a new Advanced `<details>` alongside the 3 new raw fields.
OLD:
```html
        <div class="settings-sub">RTP / RTSP transport</div>
        <div class="settings-grid">
          <div class="setting">
            <label for="rtsp-frame-buffer" title="RTSP receive frame buffer (bytes).&#10;How big a single video frame the engine will accept from an RTSP camera. High-resolution / high-bitrate cameras can send frames bigger than the default, which shows up as torn or dropped pictures — raising this fixes that, at the cost of a little memory per stream.&#10;Only affects 'rtsp://' sources.&#10;Default: 0 (use the engine default).  Range: 0–10000000.">RTSP frame buf <span class="unit">B</span></label>
            <input type="number" id="rtsp-frame-buffer" min="0" max="10000000" step="10000"
                   title="RTSP receive frame buffer (bytes).&#10;How big a single video frame the engine will accept from an RTSP camera. High-resolution / high-bitrate cameras can send frames bigger than the default, which shows up as torn or dropped pictures — raising this fixes that, at the cost of a little memory per stream.&#10;Only affects 'rtsp://' sources.&#10;Default: 0 (use the engine default).  Range: 0–10000000." />
          </div>
          <div class="setting">
            <label for="rtp-timeout" title="RTP receive timeout (seconds).&#10;How long the engine waits with no incoming RTP packets before deciding the source is dead and giving up (the app then reconnects it). Lower = notices a dropped sender sooner; higher = more patient on a bursty link.&#10;Set to -1 to never time out. Only affects 'rtp://' sources.&#10;Default: 5.  Range: -1–3600.">RTP timeout <span class="unit">s</span></label>
            <input type="number" id="rtp-timeout" min="-1" max="3600" step="1"
                   title="RTP receive timeout (seconds).&#10;How long the engine waits with no incoming RTP packets before deciding the source is dead and giving up (the app then reconnects it). Lower = notices a dropped sender sooner; higher = more patient on a bursty link.&#10;Set to -1 to never time out. Only affects 'rtp://' sources.&#10;Default: 5.  Range: -1–3600." />
          </div>
        </div>
```
NEW:
```html
        <div class="settings-sub">RTP / RTSP transport</div>
        <div class="settings-grid">
          <div class="setting">
            <label for="rtsp-transport" title="RTSP transport.&#10;How the engine carries an RTSP camera's video.&#10;Auto (recommended) = let ffmpeg negotiate — UDP with an automatic TCP fallback — the default that lets most cameras connect unattended.&#10;UDP = force plain UDP (lowest latency, but lossy on a congested link).&#10;TCP = force interleaved TCP (steadier on a lossy link; a UDP-only camera may then fail to connect).&#10;HTTP = tunnel RTSP inside HTTP (gets through firewalls that only allow web traffic).&#10;UDP multicast = receive a multicast RTP session (rare).&#10;Only affects 'rtsp://' sources.&#10;Default: Auto.">RTSP transport</label>
            <select id="rtsp-transport"
                    title="RTSP transport.&#10;How the engine carries an RTSP camera's video.&#10;Auto (recommended) = let ffmpeg negotiate — UDP with an automatic TCP fallback — the default that lets most cameras connect unattended.&#10;UDP = force plain UDP (lowest latency, but lossy on a congested link).&#10;TCP = force interleaved TCP (steadier on a lossy link; a UDP-only camera may then fail to connect).&#10;HTTP = tunnel RTSP inside HTTP (gets through firewalls that only allow web traffic).&#10;UDP multicast = receive a multicast RTP session (rare).&#10;Only affects 'rtsp://' sources.&#10;Default: Auto.">
              <option value="lavf">Auto (recommended)</option>
              <option value="udp">UDP</option>
              <option value="tcp">TCP</option>
              <option value="http">HTTP (firewall tunnel)</option>
              <option value="udp_multicast">UDP multicast (rare)</option>
            </select>
          </div>
          <div class="setting">
            <label for="rtp-timeout" title="RTP receive timeout (seconds).&#10;How long the engine waits with no incoming RTP packets before deciding the source is dead and giving up (the app then reconnects it). Lower = notices a dropped sender sooner; higher = more patient on a bursty link.&#10;Set to -1 to never time out. Only affects 'rtp://' sources.&#10;Default: 5.  Range: -1–3600.">RTP timeout <span class="unit">s</span></label>
            <input type="number" id="rtp-timeout" min="-1" max="3600" step="1"
                   title="RTP receive timeout (seconds).&#10;How long the engine waits with no incoming RTP packets before deciding the source is dead and giving up (the app then reconnects it). Lower = notices a dropped sender sooner; higher = more patient on a bursty link.&#10;Set to -1 to never time out. Only affects 'rtp://' sources.&#10;Default: 5.  Range: -1–3600." />
          </div>
        </div>

        <details class="settings-advanced">
          <summary>Advanced</summary>
          <div class="settings-grid">
            <div class="setting">
              <label for="analyze-duration" title="Startup wait — analyze duration (milliseconds).&#10;How long the engine inspects an incoming stream before it starts playing, to work out its format. Lowering it makes a stream appear faster; too low and an unusual stream may be misdetected.&#10;Leave at 0 to use the engine default. Driven by the 'Faster startup' toggle. Applies to every feed.&#10;Default: 0 (engine default).  Range: 0–60000 ms.">Startup wait <span class="unit">ms</span></label>
              <input type="number" id="analyze-duration" min="0" max="60000" step="100"
                     title="Startup wait — analyze duration (milliseconds).&#10;How long the engine inspects an incoming stream before it starts playing, to work out its format. Lowering it makes a stream appear faster; too low and an unusual stream may be misdetected.&#10;Leave at 0 to use the engine default. Driven by the 'Faster startup' toggle. Applies to every feed.&#10;Default: 0 (engine default).  Range: 0–60000 ms." />
            </div>
            <div class="setting">
              <label for="probesize" title="Startup data — probe size (kilobytes).&#10;How much of an incoming stream the engine reads before it starts playing, to work out its format. Lowering it shaves connect latency; too low and an unusual stream may be misdetected.&#10;Leave at 0 to use the engine default (about 5000 KB). Driven by the 'Faster startup' toggle. Applies to every feed.&#10;Default: 0 (engine default).  Range: 0–100000 KB.">Startup data <span class="unit">KB</span></label>
              <input type="number" id="probesize" min="0" max="100000" step="100"
                     title="Startup data — probe size (kilobytes).&#10;How much of an incoming stream the engine reads before it starts playing, to work out its format. Lowering it shaves connect latency; too low and an unusual stream may be misdetected.&#10;Leave at 0 to use the engine default (about 5000 KB). Driven by the 'Faster startup' toggle. Applies to every feed.&#10;Default: 0 (engine default).  Range: 0–100000 KB." />
            </div>
            <div class="setting">
              <label for="reorder-queue" title="Jitter buffer — reorder queue (packets).&#10;How many out-of-order packets the engine holds and re-sorts before decoding. A bigger buffer smooths a network that delivers packets out of order or in bursts (fewer glitches), at the cost of a little extra latency.&#10;Leave at 0 to use the engine default. Driven by the 'Smooth choppy video' toggle. Affects 'rtsp://' and 'rtp://' sources.&#10;Default: 0 (engine default).  Range: 0–10000.">Jitter buffer <span class="unit">pkts</span></label>
              <input type="number" id="reorder-queue" min="0" max="10000" step="10"
                     title="Jitter buffer — reorder queue (packets).&#10;How many out-of-order packets the engine holds and re-sorts before decoding. A bigger buffer smooths a network that delivers packets out of order or in bursts (fewer glitches), at the cost of a little extra latency.&#10;Leave at 0 to use the engine default. Driven by the 'Smooth choppy video' toggle. Affects 'rtsp://' and 'rtp://' sources.&#10;Default: 0 (engine default).  Range: 0–10000." />
            </div>
            <div class="setting">
              <label for="rtsp-frame-buffer" title="RTSP receive frame buffer (bytes).&#10;How big a single video frame the engine will accept from an RTSP camera. High-resolution / high-bitrate cameras can send frames bigger than the default, which shows up as torn or dropped pictures — raising this fixes that, at the cost of a little memory per stream.&#10;Only affects 'rtsp://' sources.&#10;Default: 0 (use the engine default).  Range: 0–10000000.">RTSP frame buf <span class="unit">B</span></label>
              <input type="number" id="rtsp-frame-buffer" min="0" max="10000000" step="10000"
                     title="RTSP receive frame buffer (bytes).&#10;How big a single video frame the engine will accept from an RTSP camera. High-resolution / high-bitrate cameras can send frames bigger than the default, which shows up as torn or dropped pictures — raising this fixes that, at the cost of a little memory per stream.&#10;Only affects 'rtsp://' sources.&#10;Default: 0 (use the engine default).  Range: 0–10000000." />
            </div>
          </div>
        </details>
```

---

### TASK 6b — `web/style.css` (add Advanced-expander styling)

Append after the `.settings-args` / `textarea` rules (end of the "mpv settings panel" section). Functional
without it, but this makes the `<details>` match the `.settings-sub` typographic style:
```css
/* Advanced (collapsible) sub-block inside the settings fieldset. */
.settings-advanced { margin-top: 16px; }
.settings-advanced > summary {
  cursor: pointer;
  list-style: none;
  display: flex;
  align-items: center;
  gap: 7px;
  padding: 4px 0;
  font-size: 12px;
  font-weight: 600;
  letter-spacing: 1px;
  text-transform: uppercase;
  color: var(--t-lo);
}
.settings-advanced > summary::-webkit-details-marker { display: none; }
.settings-advanced > summary::before {
  content: '▸';
  font-size: 11px;
  transition: transform 0.12s ease;
}
.settings-advanced[open] > summary::before { transform: rotate(90deg); }
.settings-advanced > summary:hover { color: var(--t-md); }
.settings-advanced > .settings-grid { margin-top: 12px; }
```

---

### TASK 7 — `web/app.js` (4 edits + preset consts)

**7A. `els` map — remove `rtspTcp`/`rtspHttp`, add the new ids.**
OLD:
```javascript
    netCache: document.getElementById('net-cache'),
    stall: document.getElementById('stall'),
    backoffStart: document.getElementById('backoff-start'),
    backoffMax: document.getElementById('backoff-max'),
    backoffReset: document.getElementById('backoff-reset'),
    backoffFactor: document.getElementById('backoff-factor'),
    rtspTcp: document.getElementById('rtsp-tcp'),
    rtspHttp: document.getElementById('rtsp-http'),
    rtspFrameBuffer: document.getElementById('rtsp-frame-buffer'),
    rtpTimeout: document.getElementById('rtp-timeout'),
    skipLoop: document.getElementById('skip-loop'),
    hwDecode: document.getElementById('hw-decode'),
    extraArgs: document.getElementById('extra-args'),
```
NEW:
```javascript
    netCache: document.getElementById('net-cache'),
    networkTimeout: document.getElementById('network-timeout'),
    stall: document.getElementById('stall'),
    backoffStart: document.getElementById('backoff-start'),
    backoffMax: document.getElementById('backoff-max'),
    backoffReset: document.getElementById('backoff-reset'),
    backoffFactor: document.getElementById('backoff-factor'),
    rtspTransport: document.getElementById('rtsp-transport'),
    rtpTimeout: document.getElementById('rtp-timeout'),
    // "Faster startup" / "Smooth choppy video" are friendly toggles over the raw Advanced numbers below;
    // the numbers are the source of truth (gatherSettings reads them), the toggles just preset/clear them.
    fasterStartup: document.getElementById('faster-startup'),
    smoothVideo: document.getElementById('smooth-video'),
    analyzeDuration: document.getElementById('analyze-duration'),
    probesize: document.getElementById('probesize'),
    reorderQueue: document.getElementById('reorder-queue'),
    rtspFrameBuffer: document.getElementById('rtsp-frame-buffer'),
    skipLoop: document.getElementById('skip-loop'),
    hwDecode: document.getElementById('hw-decode'),
    extraArgs: document.getElementById('extra-args'),
```

**7B. Add preset consts.** Put right after `const token = …;` near the top of the IIFE:
```javascript
  // "Faster startup" / "Smooth choppy video" toggle presets — values written into the raw Advanced fields
  // when a toggle is switched ON (OFF writes 0 = engine default). Sensible starting points; tune the exact
  // numbers under Advanced. PRESET_JITTER_PKTS especially is a Phase-6 field-test value.
  const PRESET_ANALYZE_MS = 500;   // demuxer-lavf-analyzeduration
  const PRESET_PROBE_KB = 500;     // demuxer-lavf-probesize (shown in KB; ×1000 → bytes on the wire)
  const PRESET_JITTER_PKTS = 50;   // reorder_queue_size
```

**7C. `wireControls()` — add toggle↔number sync.** Add after the `els.saveSettings…` listener block (before
the power-controls section):
```javascript
    // "Faster startup" / "Smooth choppy video": flipping a toggle fills in (ON) or clears (OFF) the raw
    // Advanced fields it represents; editing those fields keeps the toggle in sync. The fields are what get
    // sent (gatherSettings), so there's a single source of truth and Apply/Save need no toggle awareness.
    els.fasterStartup.addEventListener('change', () => {
      els.analyzeDuration.value = els.fasterStartup.checked ? String(PRESET_ANALYZE_MS) : '0';
      els.probesize.value = els.fasterStartup.checked ? String(PRESET_PROBE_KB) : '0';
    });
    els.smoothVideo.addEventListener('change', () => {
      els.reorderQueue.value = els.smoothVideo.checked ? String(PRESET_JITTER_PKTS) : '0';
    });
    const syncFasterStartup = () => {
      els.fasterStartup.checked = intVal(els.analyzeDuration) > 0 || intVal(els.probesize) > 0;
    };
    els.analyzeDuration.addEventListener('input', syncFasterStartup);
    els.probesize.addEventListener('input', syncFasterStartup);
    els.reorderQueue.addEventListener('input', () => {
      els.smoothVideo.checked = intVal(els.reorderQueue) > 0;
    });
```

**7D. `gatherSettings()`.**
OLD:
```javascript
      backoffFactor: floatVal(els.backoffFactor),
      rtspOverTcp: els.rtspTcp.checked,
      skipLoopFilter: els.skipLoop.checked,
      hwDecode: els.hwDecode.checked,
      extraMpvArgs: els.extraArgs.value.split(/\s+/).filter(Boolean),
      // RTP / RTSP transport (camelCase = SettingsSnapshot fields; server clamps to range)
      rtspHttpTunnel: els.rtspHttp.checked,
      rtspFrameBufferSizeBytes: intVal(els.rtspFrameBuffer),
      rtpTimeoutSec: intVal(els.rtpTimeout),
    };
```
NEW:
```javascript
      backoffFactor: floatVal(els.backoffFactor),
      networkTimeoutSec: intVal(els.networkTimeout),
      skipLoopFilter: els.skipLoop.checked,
      hwDecode: els.hwDecode.checked,
      extraMpvArgs: els.extraArgs.value.split(/\s+/).filter(Boolean),
      // Low-latency connect (the "Faster startup" toggle drives these two raw fields; KB → bytes on the wire)
      demuxAnalyzeDurationMs: intVal(els.analyzeDuration),
      demuxProbesizeBytes: intVal(els.probesize) * 1000,
      // RTP / RTSP transport (camelCase = SettingsSnapshot fields; server clamps to range)
      rtspTransport: els.rtspTransport.value,
      rtspFrameBufferSizeBytes: intVal(els.rtspFrameBuffer),
      rtpTimeoutSec: intVal(els.rtpTimeout),
      reorderQueueSize: intVal(els.reorderQueue),   // "Smooth choppy video" toggle drives this raw field
    };
```

**7E. `renderSettings()` latch.**
OLD:
```javascript
    const s = f.settings || {};
    setNum(els.netCache, s.networkCachingMs);
    setNum(els.stall, s.stallTimeoutMs);
    setNum(els.backoffStart, s.backoffStartMs);
    setNum(els.backoffMax, s.backoffMaxMs);
    setNum(els.backoffReset, s.backoffResetMs);
    setNum(els.backoffFactor, s.backoffFactor);
    setCheck(els.rtspTcp, s.rtspOverTcp);
    setCheck(els.rtspHttp, s.rtspHttpTunnel);
    setNum(els.rtspFrameBuffer, s.rtspFrameBufferSizeBytes);
    setNum(els.rtpTimeout, s.rtpTimeoutSec);
    setCheck(els.skipLoop, s.skipLoopFilter);
    setCheck(els.hwDecode, s.hwDecode);
    setText(els.extraArgs, (s.extraMpvArgs || []).join('\n'));
    settingsLatched = true;
```
NEW:
```javascript
    const s = f.settings || {};
    setNum(els.netCache, s.networkCachingMs);
    setNum(els.networkTimeout, s.networkTimeoutSec);
    setNum(els.stall, s.stallTimeoutMs);
    setNum(els.backoffStart, s.backoffStartMs);
    setNum(els.backoffMax, s.backoffMaxMs);
    setNum(els.backoffReset, s.backoffResetMs);
    setNum(els.backoffFactor, s.backoffFactor);
    setText(els.rtspTransport, s.rtspTransport || 'lavf');   // <select>: .value matches an <option>
    setNum(els.rtspFrameBuffer, s.rtspFrameBufferSizeBytes);
    setNum(els.rtpTimeout, s.rtpTimeoutSec);
    // Low-latency connect: probesize bytes → KB for display; derive the two friendly toggles from the values.
    setNum(els.analyzeDuration, s.demuxAnalyzeDurationMs);
    setNum(els.probesize, s.demuxProbesizeBytes != null ? Math.round(s.demuxProbesizeBytes / 1000) : null);
    setNum(els.reorderQueue, s.reorderQueueSize);
    setCheck(els.fasterStartup, (s.demuxAnalyzeDurationMs > 0) || (s.demuxProbesizeBytes > 0));
    setCheck(els.smoothVideo, s.reorderQueueSize > 0);
    setCheck(els.skipLoop, s.skipLoopFilter);
    setCheck(els.hwDecode, s.hwDecode);
    setText(els.extraArgs, (s.extraMpvArgs || []).join('\n'));
    settingsLatched = true;
```
> `setText` writes `el.value` (guarded by `activeElement`) — correct for a `<select>`; `|| 'lavf'` guards a
> missing field. No per-input change listener needed beyond 7C (settings are batch-read by Apply/Save).

---

### TASK 8 — Verification gates (run before declaring done)

1. **Build:** `dotnet build MpvGrid.csproj` → **0 warnings / 0 errors.**
2. **No dangling old identifiers** (grep compiled tree; exclude `reference\`, `obj`, `bin`, `HANDOVER*.md`):
   `RtspOverTcp|RtspHttpTunnel|rtspOverTcp|rtspHttpTunnel|rtspTcp|rtspHttp|'rtsp-tcp'|"rtsp-http"` → **zero**
   in `.cs`/`.js`/`.html`. (`bin\...\streams.json` may still carry old keys — that's the ignore-unknown test, gate 6.)
3. **No VLC residue regression:** zero `VlcGrid|vlcgrid|VLCGRID_|ExtraVlcArgs` and zero `VLC|libVLC|LibVLC`
   (same exclusions).
4. **els↔id parity:** every `document.getElementById('…')` in `app.js` resolves to an `id` in `index.html`
   (gone: `rtsp-tcp`, `rtsp-http`; added: `network-timeout`, `rtsp-transport`, `faster-startup`,
   `smooth-video`, `analyze-duration`, `probesize`, `reorder-queue`). And every new id has an `els` entry.
5. **gather↔snapshot parity:** every key `gatherSettings()` emits maps to a `SettingsSnapshot` field, and
   every `SettingsSnapshot` field is set in `BuildSnapshot` AND handled in `ApplySettings` (predicate + copy).
   New: `networkTimeoutSec`, `demuxAnalyzeDurationMs`, `demuxProbesizeBytes`, `rtspTransport`,
   `reorderQueueSize`. Removed: `rtspOverTcp`, `rtspHttpTunnel`.
6. **Old-file round-trip (reason, no rig):** a `streams.json` with `"RtspOverTcp": true` / `"RtspHttpTunnel": true`
   and no `RtspTransport` key loads cleanly (unknown keys ignored; `RtspTransport` stays `"lavf"`). A file with
   `"RtspTransport":"udp"` loads as `udp`; `"garbage"`/blank → `Normalize()` → `"lavf"`.
7. **Probesize sentinel:** confirm `ApplyStreamOptions` emits `demuxer-lavf-probesize` = `"5000000"` when
   `DemuxProbesizeBytes == 0` (NEVER `"0"` — libmpv rejects it, rc -6). (Already implemented; just re-read it.)
8. **Toggle behaviour sanity (read the code, no rig):** Faster startup ON → analyze=500, probe=500(KB)=500000B
   on the wire; OFF → 0/0. Smooth ON → jitter=50; OFF → 0. Editing an Advanced field re-derives its toggle.
9. **Optional:** re-run the headless dll option-harness (source in `HANDOVER-rtsp-rtp-ui-knobs.md` §"Throwaway
   dll-verification harness") to re-confirm spellings.

---

## Out of scope — flag for Phase 6 (needs the real camera rig + GPU)

Runtime *behaviour* (not the wiring) of: the toggle presets (esp. `PRESET_JITTER_PKTS=50` — tune on real RTP),
`rtsp-transport` switching on real cameras, the connect-latency effect of analyze/probesize, and that
`network-timeout` actually abandons an unplugged-camera connect in ~60 s. Also confirm the build's
`network-timeout` default really is 60 (the "behavior-neutral" claim depends on it; cheap check via the harness
or `mpv_get_property` before set).

## Trail
- This doc supersedes `HANDOVER-rtsp-rtp-execution.md` for the web layer (that doc is stale re: dead knobs and
  predates the toggles+Advanced UI decision). C# edits there were correct in intent and are now DONE.
- Plan brief: `HANDOVER-rtsp-rtp-ui-knobs.md`. Memory: `rtsp-rtp-knobs`, `mpv-migration`,
  `grid-count-feature` (grid-count is sequenced AFTER this).
- Task list (TaskList): #1–8 ALL completed (2026-06-05). C# layers + web layer applied; all 8 verify gates
  passed. No code work remains — only Phase-6 field tests on the real camera rig.
