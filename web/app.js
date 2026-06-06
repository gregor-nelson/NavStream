'use strict';

// NavStream browser control — Phase 3 (final): live renderer + the full operator control set.
// The server (ViewFrameBuilder) still does all the merge/staleness/banner logic and pushes a flat
// ViewFrame over SSE; this script paints it AND sends operator actions back via POST /command. It holds
// no business logic — every command is one documented verb the C# dispatcher (TrayContext.OnCommand)
// already routes to the render pipe or the supervisor. Phase 3 adds the mpv-settings panel (Apply/Save),
// the "Live video feed" master toggle, and the Shutdown Displays / Exit Application power controls.

(function () {
  const token = new URLSearchParams(location.search).get('token') || '';

  // "Faster startup" / "Smooth choppy video" toggle presets — values written into the raw Advanced fields
  // when a toggle is switched ON (OFF writes 0 = engine default). Sensible starting points; tune the exact
  // numbers under Advanced. PRESET_JITTER_PKTS especially is a Phase-6 field-test value.
  const PRESET_ANALYZE_MS = 500;   // demuxer-lavf-analyzeduration
  const PRESET_PROBE_KB = 500;     // demuxer-lavf-probesize (shown in KB; ×1000 → bytes on the wire)
  const PRESET_JITTER_PKTS = 50;   // reorder_queue_size

  const els = {
    banner: document.getElementById('banner'),
    status: document.getElementById('status'),
    feeds: document.getElementById('feeds'),
    linkDot: document.getElementById('link-dot'),
    linkText: document.getElementById('link-text'),
    // header telemetry chips (read-only — pure output, painted by renderHeader)
    hdrPid: document.getElementById('hdr-pid'),
    chipStreams: document.getElementById('chip-streams'),
    streamsVal: document.getElementById('streams-val'),
    tallyHealthy: document.getElementById('tally-healthy'),
    tallyWarn: document.getElementById('tally-warn'),
    tallyDown: document.getElementById('tally-down'),
    chipBitrate: document.getElementById('chip-bitrate'),
    bitrateVal: document.getElementById('bitrate-val'),
    chipFresh: document.getElementById('chip-fresh'),
    freshVal: document.getElementById('fresh-val'),
    chipMonitor: document.getElementById('chip-monitor'),
    monitorVal: document.getElementById('monitor-val'),
    chipDrops: document.getElementById('chip-drops'),
    dropsVal: document.getElementById('drops-val'),
    // display panel
    overlay: document.getElementById('overlay'),
    borderless: document.getElementById('borderless'),
    alwaysOnTop: document.getElementById('always-on-top'),
    monitor: document.getElementById('monitor'),
    // streams panel
    save: document.getElementById('save'),
    streamsFeedback: document.getElementById('streams-feedback'),
    // roster (layout) editor
    rosterUrl: document.getElementById('roster-url'),
    rosterName: document.getElementById('roster-name'),
    rosterAdd: document.getElementById('roster-add'),
    rosterActive: document.getElementById('roster-active'),
    rosterPool: document.getElementById('roster-pool'),
    rosterActiveCount: document.getElementById('roster-active-count'),
    rosterPoolCount: document.getElementById('roster-pool-count'),
    rosterApply: document.getElementById('roster-apply'),
    rosterDiscard: document.getElementById('roster-discard'),
    rosterFeedback: document.getElementById('roster-feedback'),
    rosterCaution: document.getElementById('roster-caution'),
    rosterDirtyNote: document.getElementById('roster-dirty-note'),
    // mpv settings panel (the whole <fieldset> disables together when the grid is down)
    settingsFields: document.getElementById('settings-fields'),
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
    applySettings: document.getElementById('apply-settings'),
    saveSettings: document.getElementById('save-settings'),
    settingsFeedback: document.getElementById('settings-feedback'),
    // command bar (feed toggle / shutdown / exit stay enabled even while the grid is down; restart is
    // gated on connected — see renderPower). One shared feedback line for the whole strip.
    feedToggle: document.getElementById('feed-toggle'),
    restart: document.getElementById('restart'),
    shutdown: document.getElementById('shutdown'),
    exit: document.getElementById('exit'),
    cmdFeedback: document.getElementById('cmd-feedback'),
  };

  const rows = [];             // cached feed-card DOM, reused across frames to avoid flicker
  let connected = false;       // last frame's grid-connected state
  let feedStopped = false;     // last frame's supervisor "grid stopped" flag (drives the feed toggle)
  let namesLatched = false;    // names/names-only populated once per connection (seam: don't clobber edits)
  let settingsLatched = false; // mpv settings populated once per connection (same seam as names)
  let monitorCount = -1;       // current monitor-select option count (rebuild only when it changes)
  let rosterLatched = false;   // roster draft seeded from a live frame at least once this connection
  let lastFrame = null;        // last rendered frame, so Discard can re-seed without waiting for the next tick
  const roster = { active: [], pool: [], dirty: false };  // membership draft (active arrays + parked pool)

  // ---- command channel (POST /command) ----

  // Returns the channel's success: false = the grid/supervisor couldn't be reached (show "can't reach").
  async function send(cmd) {
    try {
      const res = await fetch('/command?token=' + encodeURIComponent(token), {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(cmd),
      });
      if (!res.ok) return false;
      const j = await res.json();
      return !!j.ok;
    } catch {
      return false;
    }
  }

  // A transient status line (auto-clears after 4 s), mirroring the old form's ShowFeedback.
  function feedback(el, text, kind) {
    el.textContent = text;
    el.className = 'feedback' + (kind ? ' ' + kind : '');
    clearTimeout(el._clearTimer);
    el._clearTimer = setTimeout(() => { el.textContent = ''; el.className = 'feedback'; }, 4000);
  }

  // ---- display-panel + streams-panel controls (wired once) ----

  function wireControls() {
    // Setting an input's .checked/.value programmatically does NOT fire 'change' in the DOM, so mirroring
    // a frame into these controls never echoes a command back — no suppression flag needed (unlike WinForms).
    els.overlay.addEventListener('change', () => send({ name: 'setOverlay', boolValue: els.overlay.checked }));
    els.borderless.addEventListener('change', () => send({ name: 'setBorderless', boolValue: els.borderless.checked }));
    els.alwaysOnTop.addEventListener('change', () => send({ name: 'setAlwaysOnTop', boolValue: els.alwaysOnTop.checked }));

    els.monitor.addEventListener('change', () => {
      const i = els.monitor.selectedIndex;
      if (i >= 0) send({ name: 'setMonitor', intValue: i });
    });

    els.restart.addEventListener('click', async () => {
      if (!confirm('Restart the whole display now? (the grid blinks ~1 s)')) return;
      if (!(await send({ name: 'restartDisplay' }))) feedback(els.cmdFeedback, "Couldn't reach the grid.", 'warn');
    });

    // Names + modes are already applied live; a plain Save (no settings payload) persists the current live
    // config to streams.json (the C# dispatch keys off whether a settings payload is present).
    els.save.addEventListener('click', async () => {
      // Attach the live source boxes so Save writes exactly what's shown (names/modes are already applied
      // live; the render's Save handler applies any changed URLs first, then persists the whole config).
      const ok = await send({ name: 'save', streams: gatherStreams() });
      feedback(els.streamsFeedback, ok ? 'Saved to Config' : "Couldn't reach the grid.", ok ? 'ok' : 'warn');
    });

    // ---- mpv settings: Apply (session) / Save (persist, with settings payload) ----
    els.applySettings.addEventListener('click', async () => {
      const ok = await send({ name: 'applySettings', settings: gatherSettings() });
      feedback(els.settingsFeedback, ok ? 'Applied (session)' : "Couldn't reach the grid.", ok ? 'ok' : 'warn');
    });
    els.saveSettings.addEventListener('click', async () => {
      const ok = await send({ name: 'save', settings: gatherSettings() });
      feedback(els.settingsFeedback, ok ? 'Saved to Config' : "Couldn't reach the grid.", ok ? 'ok' : 'warn');
    });

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

    // ---- power controls (feed toggle / shutdown / exit) — these stay enabled even while the grid is down ----

    // Master feed switch: true = start the grid, false = stop it. Confirm on STOP only. We never optimistically
    // flip the box ourselves — its checked state is driven from frame.feedStopped every tick (renderPower), so
    // the supervisor's truth wins. On a cancelled stop we just restore it to match the last known feedStopped.
    els.feedToggle.addEventListener('change', async () => {
      if (els.feedToggle.checked) {
        if (!(await send({ name: 'feedToggle', boolValue: true })))
          feedback(els.cmdFeedback, "Can't reach the supervisor to start the feed.", 'warn');
        return;
      }
      if (!confirm('Fully stop and close the video feed? The grid goes dark until you switch it back on. '
        + 'This dashboard stays open.')) {
        els.feedToggle.checked = !feedStopped;   // user cancelled — restore to the live state
        return;
      }
      if (!(await send({ name: 'feedToggle', boolValue: false })))
        feedback(els.cmdFeedback, "Can't reach the supervisor to stop the feed.", 'warn');
    });

    els.shutdown.addEventListener('click', async () => {
      if (!confirm('Shut down all displays now? This disconnects every stream and force-kills all NavStream '
        + 'render processes (including any orphaned ones). The grid goes dark; this dashboard stays open '
        + "— switch 'Live video feed' back on to relaunch.")) return;
      if (!(await send({ name: 'shutdownDisplays' })))
        feedback(els.cmdFeedback, "Can't reach the supervisor to shut down displays.", 'warn');
    });

    els.exit.addEventListener('click', async () => {
      if (!confirm('Exit NavStream completely? This stops every stream and closes the whole application '
        + '(render + this dashboard).')) return;
      if (!(await send({ name: 'exitApplication' })))
        feedback(els.cmdFeedback, "Couldn't reach the app to exit.", 'warn');
    });
  }

  // Build the settings payload from the inputs (camelCase field names = SettingsSnapshot). Numerics are
  // coerced to numbers; extra args split on any whitespace/newlines with empties dropped (mirrors ParseArgs).
  function gatherSettings() {
    return {
      networkCachingMs: intVal(els.netCache),
      stallTimeoutMs: intVal(els.stall),
      backoffStartMs: intVal(els.backoffStart),
      backoffMaxMs: intVal(els.backoffMax),
      backoffResetMs: intVal(els.backoffReset),
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
  }

  function intVal(el) { const n = Math.round(Number(el.value)); return Number.isFinite(n) ? n : 0; }
  function floatVal(el) { const n = Number(el.value); return Number.isFinite(n) ? n : 0; }

  // ---- SSE connection to the dashboard process ----

  function connect() {
    const src = new EventSource('/events?token=' + encodeURIComponent(token));

    src.onmessage = (e) => {
      setLink(true);
      let frame;
      try { frame = JSON.parse(e.data); } catch { return; }
      render(frame);
    };

    src.onerror = () => {
      // The dashboard process is normally always-up; an error means the bridge went away.
      // EventSource auto-reconnects, so just reflect the dropped link.
      setLink(false);
    };
  }

  function setLink(up) {
    els.linkDot.className = 'dot ' + (up ? 'dot-green' : 'dot-red');
    els.linkText.textContent = up ? 'control link active' : 'control link lost — retrying…';
  }

  // ---- render ----

  function render(f) {
    lastFrame = f;
    // Banner (big tri-state line) — colour driven entirely by the server's kind.
    els.banner.textContent = f.banner.text;
    els.banner.className = 'banner ' + f.banner.kind;

    // Status (dim second line).
    els.status.textContent = f.status.text || '';
    els.status.className = 'status ' + (f.status.kind === 'none' ? '' : f.status.kind);

    // A dropped connection re-arms the name + settings latches so the next live snapshot repopulates them.
    if (!f.connected && connected) { namesLatched = false; settingsLatched = false; rosterLatched = false; }
    connected = f.connected;
    feedStopped = !!f.feedStopped;

    renderHeader(f);
    renderDisplay(f);
    renderSettings(f);
    renderPower(f);
    renderRoster(f);
    renderFeeds(f.feeds || [], f.connected);
  }

  // ---- header telemetry chips (pure output; banner + status are set above in render) ----

  // Paints the read-only chip cluster from the frame. Every live metric shows '—' (dim) when the grid is
  // down — only presentational formatting happens here (bitrate sum, MON n, PID label); no business logic.
  function renderHeader(f) {
    const conn = !!f.connected;

    // Subtitle: Render PID (the banner text itself is server-built and set verbatim in render()).
    els.hdrPid.textContent = conn && f.renderPid > 0 ? 'RENDER PID ' + f.renderPid : '—';

    // Streams: healthy/total + weak/down count badges, whole chip tinted by the worst health present.
    const t = f.tally || {};
    if (conn) {
      els.streamsVal.textContent = (t.healthy || 0) + '/' + (t.total || 0);
      setTally(els.tallyHealthy, t.healthy, true);
      setTally(els.tallyWarn, t.warn, (t.warn || 0) > 0);
      setTally(els.tallyDown, t.down, (t.down || 0) > 0);
      chipColor(els.chipStreams, (t.down || 0) > 0 ? 'red' : ((t.warn || 0) > 0 ? 'amber' : 'green'));
    } else {
      els.streamsVal.textContent = '—';
      setTally(els.tallyHealthy, 0, false);
      setTally(els.tallyWarn, 0, false);
      setTally(els.tallyDown, 0, false);
      chipColor(els.chipStreams, 'off');
    }

    // Bitrate: sum of the per-feed kbps (client-side; <1000 → kbps, else Mbps to 1 dp).
    if (conn) {
      const kbps = (f.feeds || []).reduce((s, x) => s + (x.bitrateKbps || 0), 0);
      els.bitrateVal.textContent = fmtBitrate(kbps);
      chipColor(els.chipBitrate, '');
    } else {
      els.bitrateVal.textContent = '—';
      chipColor(els.chipBitrate, 'off');
    }

    // Freshness: telemetry frame age; stale → amber + ⚠ (the warning text also rides the status sub-line).
    if (conn) {
      els.freshVal.textContent = (f.stale ? '⚠ ' : '') + (Number(f.ageSec) || 0).toFixed(1) + 's';
      chipColor(els.chipFresh, f.stale ? 'amber' : '');
    } else {
      els.freshVal.textContent = '—';
      chipColor(els.chipFresh, 'off');
    }

    // Monitor index the grid is on.
    const v = f.visual || {};
    if (conn) {
      els.monitorVal.textContent = 'MON ' + (v.monitor != null ? v.monitor : 0);
      chipColor(els.chipMonitor, '');
    } else {
      els.monitorVal.textContent = '—';
      chipColor(els.chipMonitor, 'off');
    }

    // Drops: total lost pictures across feeds (amber once any frames have been dropped).
    if (conn) {
      const drops = (f.feeds || []).reduce((s, x) => s + (x.lostPictures || 0), 0);
      els.dropsVal.textContent = String(drops);
      chipColor(els.chipDrops, drops > 0 ? 'amber' : '');
    } else {
      els.dropsVal.textContent = '—';
      chipColor(els.chipDrops, 'off');
    }
  }

  function fmtBitrate(kbps) {
    if (!(kbps > 0)) return '0 kbps';
    return kbps < 1000 ? Math.round(kbps) + ' kbps' : (kbps / 1000).toFixed(1) + ' Mbps';
  }

  // A tally badge shows its count and hides when not applicable (the dot colour is fixed in the markup).
  function setTally(el, n, show) {
    el.textContent = String(n || 0);
    el.style.display = show ? '' : 'none';
  }

  // Apply one of the chip colour states ('green'|'amber'|'red'|'off'), or clear to the neutral default.
  function chipColor(el, state) {
    el.classList.remove('chip--green', 'chip--amber', 'chip--red', 'chip--off');
    if (state) el.classList.add('chip--' + state);
  }

  function renderSettings(f) {
    // Whole panel (a <fieldset>) is meaningless while the grid is down — one flag disables every control.
    els.settingsFields.disabled = !f.connected;

    // Latch the mpv settings ONCE per connection (seam: don't clobber the operator's in-progress edits),
    // and only off a frame that actually carries live feed data — a freshly-connected frame can still hold
    // offline placeholders, in which case f.settings is a zeroed default, not the real config.
    const hasLive = (f.feeds || []).some((x) => x.health && x.health !== 'Offline');
    if (!f.connected || settingsLatched || !hasLive) return;

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
  }

  // Latch writers: skip a field the operator is actively editing (belt-and-suspenders — the latch runs at
  // most once per connection anyway). Programmatic .value/.checked never fire 'change', so no echo.
  function setNum(el, v) { if (el !== document.activeElement && v != null) el.value = String(v); }
  function setText(el, v) { if (el !== document.activeElement) el.value = v; }
  function setCheck(el, v) { if (el !== document.activeElement) el.checked = !!v; }

  function renderPower(f) {
    // Mirror the supervisor's truth every tick (NOT optimistic): checked = feed is running = !feedStopped.
    // Programmatic .checked doesn't fire 'change', so this can't loop back into a feedToggle command.
    els.feedToggle.checked = !f.feedStopped;
    // Restart rides the render pipe, so it's only meaningful while the grid is connected. The other cmdbar
    // controls — feed toggle / shutdown / exit — are deliberately never disabled: a stopped or down grid
    // must still be relaunchable, and shutdown/exit go via the supervisor, not the (possibly dead) render pipe.
    els.restart.disabled = !f.connected;
  }

  function renderDisplay(f) {
    const v = f.visual || {};

    // Mirror the grid-owned toggles every frame (the H hotkey can flip overlay grid-side). Programmatic
    // .checked never fires 'change', so this can't loop back into a command.
    els.overlay.checked = !!v.overlayEnabled;
    els.borderless.checked = !!v.borderless;
    els.alwaysOnTop.checked = !!v.alwaysOnTop;

    // Monitor selector: rebuild the option list only when the count changes, then reflect the active one
    // (but never while the operator has the dropdown open).
    const count = Math.max(1, v.monitorCount || 0);
    if (count !== monitorCount) {
      monitorCount = count;
      els.monitor.innerHTML = '';
      for (let i = 0; i < count; i++) {
        const opt = document.createElement('option');
        opt.value = String(i);
        opt.textContent = 'Monitor ' + i;
        els.monitor.appendChild(opt);
      }
    }
    if (document.activeElement !== els.monitor && v.monitor >= 0 && v.monitor < els.monitor.options.length) {
      els.monitor.selectedIndex = v.monitor;
    }

    // These controls are meaningless while the grid is down. The cmdbar controls gate themselves in
    // renderPower (restart on connected; feed toggle / shutdown / exit never disabled), so they are NOT in
    // this line; the mpv-settings panel gates itself in renderSettings.
    const on = !!f.connected;
    els.overlay.disabled = els.borderless.disabled = els.alwaysOnTop.disabled =
      els.monitor.disabled = els.save.disabled = !on;
  }

  function renderFeeds(feeds, isConn) {
    // Grow/shrink the cached card list to match the frame.
    while (rows.length < feeds.length) rows.push(makeRow());
    while (rows.length > feeds.length) els.feeds.removeChild(rows.pop().root);

    // Latch the operator-set name + names-only ONCE per connection — only off a frame that actually carries
    // live feed data (a connected frame can briefly still hold offline placeholders before the first snapshot).
    const latchNow = isConn && !namesLatched && feeds.some((f) => f.health && f.health !== 'Offline');

    for (let i = 0; i < feeds.length; i++) updateRow(rows[i], feeds[i], isConn, latchNow);

    if (latchNow) namesLatched = true;
  }

  function makeRow() {
    const root = document.createElement('div');
    root.className = 'feed';

    const swatch = document.createElement('div');
    swatch.className = 'feed-swatch';

    const body = document.createElement('div');
    body.className = 'feed-body';

    // Title row: health dot + "STREAM n · STATE" + a right-aligned Reconnect button.
    const titleRow = document.createElement('div');
    titleRow.className = 'feed-title-row';
    const dot = document.createElement('span'); dot.className = 'feed-dot';
    const title = document.createElement('span'); title.className = 'feed-title';
    const reconnect = document.createElement('button');
    reconnect.type = 'button';
    reconnect.className = 'btn btn-sm feed-reconnect';
    reconnect.textContent = 'Reconnect';
    titleRow.append(dot, title, reconnect);

    // Name row: editable vessel name (overlay label) + a "name only" switch.
    const nameRow = document.createElement('div');
    nameRow.className = 'feed-name-row';
    const name = document.createElement('input');
    name.type = 'text';
    name.className = 'feed-name-input';
    name.placeholder = 'Vessel name';
    name.maxLength = 64;
    const namesOnlyWrap = document.createElement('label');
    namesOnlyWrap.className = 'switch switch-sm';
    namesOnlyWrap.title = 'Name only: show just the vessel name + a health dot — hides stream #, IP & status.';
    const namesOnly = document.createElement('input');
    namesOnly.type = 'checkbox';
    const track = document.createElement('span'); track.className = 'switch-track';
    const thumb = document.createElement('span'); thumb.className = 'switch-thumb';
    track.append(thumb);
    namesOnlyWrap.append(namesOnly, track);
    nameRow.append(name, namesOnlyWrap);

    // Source row: editable full source URL (live per-cell swap via setStreams). Replaces the old read-only
    // protocol + IP display — the whole URL (scheme, host/IP, port, path) now lives in this one box.
    const sourceRow = document.createElement('div');
    sourceRow.className = 'feed-source-row';
    const srcLabel = document.createElement('span');
    srcLabel.className = 'feed-source-label';
    srcLabel.textContent = 'Source';
    const url = document.createElement('input');
    url.type = 'text';
    url.className = 'feed-url-input';
    url.placeholder = 'rtsp://host/path';
    url.spellcheck = false;
    url.maxLength = 512;
    url.title = 'Stream source URL (e.g. rtsp://192.168.1.10/h264). Edit and press Enter to swap this feed '
      + 'live; use "Save to Config" to keep it.';
    sourceRow.append(srcLabel, url);

    const stats = document.createElement('div'); stats.className = 'feed-stats';

    body.append(titleRow, nameRow, sourceRow, stats);
    root.append(swatch, body);
    els.feeds.appendChild(root);

    const r = { root, title, name, namesOnly, reconnect, url, stats, feedIndex: 0 };

    // ---- per-feed command wiring (reads r.feedIndex, refreshed each frame) ----
    reconnect.addEventListener('click', () => send({ name: 'reconnect', index: r.feedIndex }));
    // Commit the name on blur-after-edit ('change') and on Enter (which blurs → fires 'change' if dirty).
    name.addEventListener('change', () => send({ name: 'setName', index: r.feedIndex, stringValue: r.name.value }));
    name.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); r.name.blur(); } });
    namesOnly.addEventListener('change', () =>
      send({ name: 'setOverlayNamesOnly', index: r.feedIndex, boolValue: r.namesOnly.checked }));
    // Source URL: commit on blur-after-edit ('change') and on Enter. Live-swaps just this cell.
    url.addEventListener('change', () => commitUrl(r));
    url.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); r.url.blur(); } });

    return r;
  }

  function updateRow(r, f, isConn, latchNow) {
    r.feedIndex = typeof f.index === 'number' ? f.index : 0;

    const offline = !f.health || f.health === 'Offline';
    const healthClass = healthOf(f.health);
    r.root.className = 'feed' + (healthClass ? ' health-' + healthClass : '') + (offline ? ' offline' : '');

    const state = f.state === 'Playing' ? 'LIVE' : (f.state || '—').toUpperCase();
    r.title.textContent = 'STREAM ' + f.cell + ' · ' + state;

    // Name + names-only: written only on the once-per-connection latch frame, and never over a field the
    // operator is actively editing (seam #1).
    if (latchNow && document.activeElement !== r.name) r.name.value = f.name || '';
    if (latchNow && document.activeElement !== r.namesOnly) r.namesOnly.checked = !!f.namesOnly;

    // Source URL: latched once per connection like the name, and never over an in-progress edit. The full
    // URL now lives in the editable box, so there's no separate protocol/IP display to paint.
    if (latchNow && document.activeElement !== r.url) r.url.value = f.url || '';

    r.stats.textContent = offline
      ? '—'
      : Math.round(f.bitrateKbps) + ' kbps   ·   lost ' + f.lostPictures + '   ·   dec ' + f.decodedVideo;

    // Full URL + status as a hover tooltip (mirrors the form's per-row tooltip).
    const tip = (f.url ? f.url : '') + (f.status ? '\n' + f.status : '');
    r.root.title = tip.trim();

    // Name / names-only / reconnect only make sense on a feed that's actually up. The Source box is the
    // deliberate exception: editing it is how the operator FIXES a down feed (e.g. a wrong IP), so it stays
    // editable whenever the grid is connected — independent of this feed's own health.
    // §PRECEDENCE: while the roster draft is dirty the live active set must stay stable, so the per-cell
    // name / names-only / reconnect / source edits are paused (the Source box's usual down-feed exception is
    // overridden while dirty). r.isConn / r.offline are cached so applyDirtyGate() can re-gate without a frame.
    r.isConn = isConn;
    r.offline = offline;
    const editable = isConn && !offline && !roster.dirty;
    r.name.disabled = !editable;
    r.namesOnly.disabled = !editable;
    r.reconnect.disabled = !editable;
    r.url.disabled = !isConn || roster.dirty;
  }

  // ---- presentation helpers (pure formatting of data already in the frame) ----

  function healthOf(h) {
    switch (h) {
      case 'Healthy': return 'healthy';
      case 'Warn': return 'warn';
      case 'Down': return 'down';
      default: return ''; // Connecting / Offline / unknown -> neutral
    }
  }

  // Commit ONE cell's edited source URL and live-swap it. setStreams is positional and the backend treats a
  // blank entry as "leave that feed as-is", so we send blanks for every earlier cell and the new URL at this
  // index (the list need only reach feedIndex). A blank input is a no-op — the backend refuses to blank a cell.
  function commitUrl(r) {
    const v = r.url.value.trim();
    if (!v) return;
    const streams = [];
    for (let i = 0; i <= r.feedIndex; i++) streams.push(i === r.feedIndex ? v : '');
    send({ name: 'setStreams', streams }).then((ok) =>
      feedback(els.streamsFeedback, ok ? 'Source updated — reconnecting that stream' : "Couldn't reach the grid.",
        ok ? 'ok' : 'warn'));
  }

  // Snapshot every cell's current source box for a Save (positional, aligned with the feeds). Blank/disabled
  // boxes go as '' = "leave as-is", and any cell already matching its live URL is a no-op on the backend, so
  // this is safe to send wholesale — it just guarantees Save persists exactly what's shown.
  function gatherStreams() {
    return rows.map((r) => (r.url && !r.url.disabled ? r.url.value.trim() : ''));
  }

  // ---- roster (layout) editor: a membership draft over the live active set + the parked pool ----
  // The draft mirrors live telemetry while clean; the first edit latches it dirty (stops re-seeding) and,
  // per §PRECEDENCE, pauses the live per-cell edit controls so the active set can't shift under the draft.
  // "Apply layout & restart" sends the whole desired roster as `applyRoster` (wholesale overwrite + restart).

  let lastRosterSig = '';   // skips redundant repaints (the draft re-seeds every clean frame at 1 Hz)

  function renderRoster(f) {
    const hasLive = (f.feeds || []).some((x) => x.health && x.health !== 'Offline');
    // Seed/refresh the draft from live telemetry while the operator hasn't started editing.
    if (f.connected && hasLive && !roster.dirty) {
      roster.active = (f.feeds || []).map((x) => ({ url: x.url || '', name: x.name || '', namesOnly: !!x.namesOnly }));
      roster.pool = (f.inactivePool || []).map((p) => ({ url: p.url || '', name: p.name || '', namesOnly: !!p.namesOnly }));
      rosterLatched = true;
    }
    paintRoster();
  }

  // Repaint both lists + the add/apply/guard states. Gated on a signature so the 1 Hz clean re-seed doesn't
  // thrash the DOM when nothing actually changed.
  function paintRoster() {
    const enabled = connected && rosterLatched;
    const sig = JSON.stringify({ a: roster.active, p: roster.pool, d: roster.dirty, e: enabled });
    if (sig === lastRosterSig) return;
    lastRosterSig = sig;

    const n = roster.active.length;
    els.rosterActiveCount.textContent = String(n);
    els.rosterPoolCount.textContent = String(roster.pool.length);

    paintList(els.rosterActive, roster.active, true, enabled);
    paintList(els.rosterPool, roster.pool, false, enabled);

    els.rosterAdd.disabled = !enabled || n >= 16;
    els.rosterUrl.disabled = !enabled;
    els.rosterName.disabled = !enabled;
    // Apply only when there's something to commit and at least one active stream; Discard whenever dirty.
    els.rosterApply.disabled = !connected || !roster.dirty || n === 0;
    els.rosterDiscard.disabled = !roster.dirty;

    // Risk caution (libmpv Phase-6 rig test pending): high-count walls aren't validated on this hardware yet.
    if (n > 4) {
      els.rosterCaution.hidden = false;
      els.rosterCaution.textContent = '⚠ ' + n + ' active streams — walls above 4 aren’t validated on '
        + 'this hardware yet (heavier CPU/GPU load). Confirm on the rig before relying on it.';
    } else {
      els.rosterCaution.hidden = true;
    }
    els.rosterDirtyNote.hidden = !roster.dirty;
  }

  function paintList(container, items, isActive, enabled) {
    container.textContent = '';
    if (items.length === 0) {
      const empty = document.createElement('div');
      empty.className = 'roster-empty';
      empty.textContent = isActive ? 'No active streams.' : 'No parked streams.';
      container.appendChild(empty);
      return;
    }
    items.forEach((item, i) => container.appendChild(makeRosterRow(item, i, isActive, enabled)));
  }

  function makeRosterRow(item, i, isActive, enabled) {
    const row = document.createElement('div');
    row.className = 'roster-row';

    if (isActive) {
      const cell = document.createElement('span');
      cell.className = 'roster-cell';
      cell.textContent = String(i + 1);
      row.appendChild(cell);
    }

    const label = document.createElement('span');
    label.className = 'roster-label';
    const nameTxt = document.createElement('span');
    nameTxt.className = 'roster-name-txt' + (item.name ? '' : ' unnamed');
    nameTxt.textContent = item.name || 'Unnamed';
    const urlTxt = document.createElement('span');
    urlTxt.className = 'roster-url-txt';
    urlTxt.textContent = item.url;
    label.append(nameTxt, urlTxt);
    label.title = (item.name ? item.name + '\n' : '') + item.url;
    row.appendChild(label);

    if (isActive) {
      row.append(
        btn('Deactivate', enabled && roster.active.length > 1, () => rosterDeactivate(i)),
        btn('Remove', enabled && roster.active.length > 1, () => rosterRemove(i)),
      );
    } else {
      row.append(
        btn('Activate', enabled && roster.active.length < 16, () => rosterActivate(i)),
        btn('Delete', enabled, () => rosterDelete(i)),
      );
    }
    return row;
  }

  function btn(text, on, onClick) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'btn btn-sm';
    b.textContent = text;
    b.disabled = !on;
    b.addEventListener('click', onClick);
    return b;
  }

  // Mutations — each marks the draft dirty (stops re-seeding + pauses live edits) and repaints.
  function rosterDirty() {
    roster.dirty = true;
    lastRosterSig = '';   // force the next paint through
    paintRoster();
    applyDirtyGate();
  }
  function rosterAddStream() {
    const url = els.rosterUrl.value.trim();
    if (!url) { feedback(els.rosterFeedback, 'Enter a source URL to add.', 'warn'); return; }
    if (roster.active.length >= 16) { feedback(els.rosterFeedback, 'Wall is full (16 active).', 'warn'); return; }
    roster.active.push({ url, name: els.rosterName.value.trim(), namesOnly: false });
    els.rosterUrl.value = '';
    els.rosterName.value = '';
    rosterDirty();
  }
  function rosterRemove(i) { roster.active.splice(i, 1); rosterDirty(); }
  function rosterDeactivate(i) {
    const [it] = roster.active.splice(i, 1);
    if (it) roster.pool.push(it);
    rosterDirty();
  }
  function rosterActivate(i) {
    if (roster.active.length >= 16) { feedback(els.rosterFeedback, 'Wall is full (16 active).', 'warn'); return; }
    const [it] = roster.pool.splice(i, 1);
    if (it) roster.active.push(it);
    rosterDirty();
  }
  function rosterDelete(i) { roster.pool.splice(i, 1); rosterDirty(); }

  // §PRECEDENCE: while the draft is dirty, pause the per-cell edit controls in the feed cards until
  // Apply or Discard. Re-gates immediately (no need to wait for the next frame) using cached row state.
  function applyDirtyGate() {
    for (const r of rows) {
      const editable = r.isConn && !r.offline && !roster.dirty;
      r.name.disabled = !editable;
      r.namesOnly.disabled = !editable;
      r.reconnect.disabled = !editable;
      r.url.disabled = !r.isConn || roster.dirty;
    }
    els.rosterDirtyNote.hidden = !roster.dirty;
  }

  function wireRoster() {
    els.rosterAdd.addEventListener('click', rosterAddStream);
    els.rosterUrl.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); rosterAddStream(); } });
    els.rosterName.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); rosterAddStream(); } });

    els.rosterApply.addEventListener('click', async () => {
      if (roster.active.length === 0) return;
      const ok = await send({
        name: 'applyRoster',
        streams: roster.active.map((a) => a.url),
        names: roster.active.map((a) => a.name),
        namesOnly: roster.active.map((a) => a.namesOnly),
        inactivePool: roster.pool,
      });
      if (!ok) { feedback(els.rosterFeedback, "Couldn't reach the grid.", 'warn'); return; }
      feedback(els.rosterFeedback, 'Applying — the wall blinks ~1 s while it restarts.', 'ok');
      // The restart drops SSE; on reconnect the (now clean) draft re-seeds from the new live roster.
      roster.dirty = false;
      lastRosterSig = '';
      applyDirtyGate();
      paintRoster();
    });

    els.rosterDiscard.addEventListener('click', () => {
      roster.dirty = false;
      lastRosterSig = '';
      if (lastFrame) renderRoster(lastFrame);   // re-seed from the latest frame (clean ⇒ mirrors live)
      else paintRoster();
      applyDirtyGate();
      feedback(els.rosterFeedback, 'Discarded — roster mirrors the live grid.', 'ok');
    });
  }

  // ---- custom tooltips ----------------------------------------------------
  // Replaces the browser's native title= bubbles with a single floating panel styled in the app's theme
  // (Barlow Condensed, control-room surfaces — see .tooltip in style.css). Works by delegation: the first
  // time an element carrying a `title` is hovered/focused, its text is moved into `data-tip` and the
  // `title` removed (so the OS bubble never fires), then rendered into the shared panel. A live-updated
  // title (e.g. the per-feed rows) wins again on its next hover, so dynamic tooltips keep working.
  function wireTooltips() {
    const tip = document.createElement('div');
    tip.className = 'tooltip';
    tip.setAttribute('role', 'tooltip');
    const arrow = document.createElement('span'); arrow.className = 'tooltip-arrow';
    const inner = document.createElement('div'); inner.className = 'tooltip-inner';
    tip.append(arrow, inner);
    tip.style.display = 'none';
    document.body.appendChild(tip);

    let current = null;   // element the tooltip is currently describing
    let hideTimer = 0;

    // title wins (covers live-updated titles); else the stashed data-tip. Adopting once kills the OS bubble.
    function textFor(el) {
      if (el.hasAttribute('title')) {
        const t = el.getAttribute('title');
        el.setAttribute('data-tip', t);
        el.removeAttribute('title');
        return t;
      }
      return el.getAttribute('data-tip') || '';
    }

    // Lay the help text out: first line → heading, trailing "Default: …" line → meta footer, rest → body.
    function build(text) {
      inner.textContent = '';
      const lines = text.split('\n').map((s) => s.trim()).filter(Boolean);
      if (!lines.length) return;

      let meta = '';
      if (lines.length > 1 && /^Default:/i.test(lines[lines.length - 1])) meta = lines.pop();

      if (lines.length > 1) {
        const h = document.createElement('div'); h.className = 'tooltip-title';
        h.textContent = lines.shift();
        inner.appendChild(h);
      }
      for (const line of lines) {
        const p = document.createElement('div'); p.className = 'tooltip-body';
        p.textContent = line;
        inner.appendChild(p);
      }
      if (meta) {
        const m = document.createElement('div'); m.className = 'tooltip-meta';
        m.textContent = meta;
        inner.appendChild(m);
      }
    }

    function position(el) {
      const r = el.getBoundingClientRect();
      const gap = 8, edge = 6;
      const tw = tip.offsetWidth, th = tip.offsetHeight;   // reading offset* also flushes layout before we fade in

      const above = r.top >= th + gap + edge;              // prefer above; flip below if it wouldn't fit
      const top = above ? r.top - th - gap : r.bottom + gap;

      let left = r.left + r.width / 2 - tw / 2;             // centre on the target, then clamp to the viewport
      left = Math.max(edge, Math.min(left, window.innerWidth - tw - edge));

      tip.style.left = Math.round(left) + 'px';
      tip.style.top = Math.round(top) + 'px';
      tip.classList.toggle('above', above);
      tip.classList.toggle('below', !above);

      // Caret tracks the target centre even when the box is clamped (10px wide → offset by half).
      const cx = r.left + r.width / 2 - left;
      arrow.style.left = (Math.max(12, Math.min(cx, tw - 12)) - 5) + 'px';
    }

    function show(el) {
      const text = textFor(el);
      if (!text) return;
      current = el;
      clearTimeout(hideTimer);
      build(text);
      tip.style.display = 'block';
      position(el);              // forces a layout flush while opacity is still 0, so 'show' can transition in
      tip.classList.add('show');
    }

    function hide() {
      current = null;
      tip.classList.remove('show');
      hideTimer = setTimeout(() => { tip.style.display = 'none'; }, 140);
    }

    // Nearest ancestor that carries tooltip text.
    function tipTarget(node) {
      for (let el = node; el && el !== document.body; el = el.parentElement) {
        if (el.nodeType === 1 && (el.hasAttribute('title') || el.hasAttribute('data-tip'))) return el;
      }
      return null;
    }

    document.addEventListener('pointerover', (e) => {
      const el = tipTarget(e.target);
      if (el && el !== current) show(el);
    });
    document.addEventListener('pointerout', (e) => {
      if (!current) return;
      if (e.relatedTarget && current.contains(e.relatedTarget)) return;   // still inside the same target
      if (e.relatedTarget && tipTarget(e.relatedTarget)) return;          // straight onto another → let pointerover swap
      hide();
    });
    // Keyboard parity.
    document.addEventListener('focusin', (e) => { const el = tipTarget(e.target); if (el) show(el); });
    document.addEventListener('focusout', () => { if (current) hide(); });
    // A fixed panel detaches from its target on scroll/resize, and shouldn't linger over a just-clicked control.
    window.addEventListener('scroll', () => { if (current) hide(); }, true);
    window.addEventListener('resize', () => { if (current) hide(); });
    document.addEventListener('click', () => { if (current) hide(); });
  }

  wireControls();
  wireRoster();
  wireTooltips();
  connect();
})();
