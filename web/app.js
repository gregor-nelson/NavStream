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
    logo: document.getElementById('logo'),
    borderless: document.getElementById('borderless'),
    alwaysOnTop: document.getElementById('always-on-top'),
    unassignedNote: document.getElementById('unassigned-note'),
    // logo settings popdown (range sliders + corner select)
    logoAdvanced: document.getElementById('logo-advanced'),
    logoOpacity: document.getElementById('logo-opacity'),
    logoOpacityVal: document.getElementById('logo-opacity-val'),
    logoBrightness: document.getElementById('logo-brightness'),
    logoBrightnessVal: document.getElementById('logo-brightness-val'),
    logoSize: document.getElementById('logo-size'),
    logoSizeVal: document.getElementById('logo-size-val'),
    logoPosition: document.getElementById('logo-position'),
    // overlay (health badge) settings popdown (range sliders + corner select)
    overlayAdvanced: document.getElementById('overlay-advanced'),
    badgeOpacity: document.getElementById('badge-opacity'),
    badgeOpacityVal: document.getElementById('badge-opacity-val'),
    badgeSize: document.getElementById('badge-size'),
    badgeSizeVal: document.getElementById('badge-size-val'),
    badgePosition: document.getElementById('badge-position'),
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
    rosterDisplays: document.getElementById('roster-displays'),
    rosterCellsNote: document.getElementById('roster-cells-note'),
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
  let rosterLatched = false;   // roster draft seeded from a live frame at least once this connection
  let rosterApplyPending = false; // applyRoster sent; cleared by the restart's SSE drop (success) or a timeout (failure)
  let rosterApplyTimer = 0;
  let lastFrame = null;        // last rendered frame, so Discard can re-seed without waiting for the next tick
  const roster = { active: [], pool: [], displays: [], dirty: false };  // membership + partition draft

  // Layout presets the Wall Layout partition editor offers — the single home for display/grid control.
  // The full list is always available: the Apply+restart path reconciles cells vs capacity via Normalize.
  // A valid non-preset layout from a hand-edited config (any "CxR") gets its own option when mirrored.
  const LAYOUTS = [['auto', 'Auto'], ['1x1', '1×1'], ['2x1', '2×1'], ['1x2', '1×2'], ['2x2', '2×2'], ['3x2', '3×2']];

  // A frame carries authoritative live data when any feed is non-Offline — OR when the connected render
  // reports ZERO feeds: offline placeholders are always ≥1 row, so an empty list can only be a genuinely
  // empty wall (0 configured streams), and the editors must still latch/enable so the first stream can be
  // added from the dashboard. One predicate for all three latch seams (settings, names, roster).
  function hasLiveData(feeds, connected) {
    feeds = feeds || [];
    return !!connected && (feeds.length === 0 || feeds.some((x) => x.health && x.health !== 'Offline'));
  }

  // Monitor options 0..optCount-1 for a display select; optCount can exceed the live monitorCount when a
  // saved config references a monitor that isn't attached right now — those entries are labelled
  // "(unplugged)" so the select can still represent the config truth.
  function fillMonitorOptions(select, optCount, monitorCount) {
    for (let m = 0; m < optCount; m++) {
      const opt = document.createElement('option');
      opt.value = String(m);
      opt.textContent = 'Monitor ' + m + (m >= monitorCount ? ' (unplugged)' : '');
      select.appendChild(opt);
    }
  }

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
    els.logo.addEventListener('change', () => send({ name: 'setLogo', boolValue: els.logo.checked }));
    els.borderless.addEventListener('change', () => send({ name: 'setBorderless', boolValue: els.borderless.checked }));
    els.alwaysOnTop.addEventListener('change', () => send({ name: 'setAlwaysOnTop', boolValue: els.alwaysOnTop.checked }));

    // Logo settings: each slider repaints the overlay live. Update the % readout instantly, but throttle the
    // command (leading + trailing) so a fast drag doesn't flood the pipe — the trailing call sends the final
    // value. Position fires on 'change' (one value per pick). Values persist only on Save (with the streams).
    const sendOpacity = throttle((v) => send({ name: 'setLogoOpacity', intValue: v }), 60);
    const sendBrightness = throttle((v) => send({ name: 'setLogoBrightness', intValue: v }), 60);
    const sendSize = throttle((v) => send({ name: 'setLogoSize', intValue: v }), 60);
    els.logoOpacity.addEventListener('input', () => {
      const v = intVal(els.logoOpacity); els.logoOpacityVal.textContent = v + '%'; sendOpacity(v);
    });
    els.logoBrightness.addEventListener('input', () => {
      const v = intVal(els.logoBrightness); els.logoBrightnessVal.textContent = v + '%'; sendBrightness(v);
    });
    els.logoSize.addEventListener('input', () => {
      const v = intVal(els.logoSize); els.logoSizeVal.textContent = v + '%'; sendSize(v);
    });
    els.logoPosition.addEventListener('change', () =>
      send({ name: 'setLogoPosition', intValue: Number(els.logoPosition.value) || 0 }));

    // Overlay (health badge) settings: same live + throttled pattern as the logo sliders. Opacity/Size repaint
    // the badges live; Position fires on 'change'. Values persist only on Save (with the streams).
    const sendBadgeOpacity = throttle((v) => send({ name: 'setBadgeOpacity', intValue: v }), 60);
    const sendBadgeSize = throttle((v) => send({ name: 'setBadgeSize', intValue: v }), 60);
    els.badgeOpacity.addEventListener('input', () => {
      const v = intVal(els.badgeOpacity); els.badgeOpacityVal.textContent = v + '%'; sendBadgeOpacity(v);
    });
    els.badgeSize.addEventListener('input', () => {
      const v = intVal(els.badgeSize); els.badgeSizeVal.textContent = v + '%'; sendBadgeSize(v);
    });
    els.badgePosition.addEventListener('change', () =>
      send({ name: 'setBadgePosition', intValue: Number(els.badgePosition.value) || 0 }));

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

  // Leading + trailing throttle: fires immediately, then at most once per `ms`, always delivering the final
  // call's args. Used so a slider drag repaints live without flooding the command pipe.
  function throttle(fn, ms) {
    let last = -Infinity, timer = 0, saved = null;
    const run = (args) => { last = Date.now(); fn.apply(null, args); };
    return function () {
      saved = arguments;
      const wait = ms - (Date.now() - last);
      if (wait <= 0) { if (timer) { clearTimeout(timer); timer = 0; } run(saved); }
      else if (!timer) { timer = setTimeout(() => { timer = 0; run(saved); }, wait); }
    };
  }

  // Mirror a snapshot value into a range slider + its % readout, never while the operator is dragging it
  // (activeElement guard). Programmatic .value never fires 'input', so this can't echo a command back.
  function setRange(el, valEl, v, suffix) {
    if (el !== document.activeElement && v != null) {
      el.value = String(v);
      valEl.textContent = v + (suffix || '');
    }
  }

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
    if (!f.connected && connected) {
      namesLatched = false; settingsLatched = false; rosterLatched = false;
      // The restart blink after a roster Apply: the render only restarts after a SUCCESSFUL config save,
      // so the drop is the "accepted + saved" signal — only now is it safe to mark the draft clean (it
      // re-seeds from the new live roster on reconnect).
      if (rosterApplyPending) {
        rosterApplyPending = false;
        clearTimeout(rosterApplyTimer);
        roster.dirty = false;
        lastRosterSig = '';
        applyDirtyGate();
      }
    }
    connected = f.connected;
    feedStopped = !!f.feedStopped;

    renderHeader(f);
    renderDisplay(f);
    renderSettings(f);
    renderPower(f);
    renderRoster(f);
    renderFeeds(f.feeds || [], f.connected, (f.visual || {}).displays || []);
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

    // Monitor index(es) the wall renders on — one per display ("MON 0+1"), legacy fallback to v.monitor.
    const v = f.visual || {};
    if (conn) {
      const ds = v.displays || [];
      els.monitorVal.textContent = 'MON ' + (ds.length
        ? ds.map((d) => d.monitor).join('+')
        : (v.monitor != null ? v.monitor : 0));
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
    if (!f.connected || settingsLatched || !hasLiveData(f.feeds, f.connected)) return;

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
    els.logo.checked = !!v.logoEnabled;
    els.borderless.checked = !!v.borderless;
    els.alwaysOnTop.checked = !!v.alwaysOnTop;

    // Logo settings: mirror the live appearance knobs (guarded against clobbering an in-progress drag/pick).
    setRange(els.logoOpacity, els.logoOpacityVal, v.logoOpacityPct, '%');
    setRange(els.logoBrightness, els.logoBrightnessVal, v.logoBrightnessPct, '%');
    setRange(els.logoSize, els.logoSizeVal, v.logoSizePct, '%');
    if (document.activeElement !== els.logoPosition && v.logoPosition != null)
      els.logoPosition.value = String(v.logoPosition);

    // Overlay settings: mirror the live badge appearance knobs (guarded against clobbering an in-progress drag/pick).
    setRange(els.badgeOpacity, els.badgeOpacityVal, v.badgeOpacityPct, '%');
    setRange(els.badgeSize, els.badgeSizeVal, v.badgeSizePct, '%');
    if (document.activeElement !== els.badgePosition && v.badgePosition != null)
      els.badgePosition.value = String(v.badgePosition);

    // Normalize rule-6 residual: configured streams beyond the wall's cell count have no feed (and no feed
    // card), so this note — sitting under the Wall Layout partition editor — is the operator's only signal
    // that something isn't rendering.
    const unassigned = (f.connected && v.unassignedStreams) || 0;
    els.unassignedNote.hidden = unassigned <= 0;
    if (unassigned > 0)
      els.unassignedNote.textContent = '⚠ ' + unassigned + ' configured stream' + (unassigned === 1 ? ' has' : 's have')
        + ' no cell on the wall and ' + (unassigned === 1 ? 'is' : 'are') + ' not rendering — grow the '
        + 'Displays split above and Apply.';

    // These controls are meaningless while the grid is down. The cmdbar controls gate themselves in
    // renderPower (restart on connected; feed toggle / shutdown / exit never disabled), so they are NOT in
    // this line; the mpv-settings panel gates itself in renderSettings.
    const on = !!f.connected;
    els.overlay.disabled = els.borderless.disabled = els.alwaysOnTop.disabled = els.save.disabled = !on;

    // Logo sliders only make sense when the grid is up AND the watermark is on — disable + grey otherwise.
    const logoOn = on && !!v.logoEnabled;
    els.logoOpacity.disabled = els.logoBrightness.disabled =
      els.logoSize.disabled = els.logoPosition.disabled = !logoOn;
    els.logoAdvanced.classList.toggle('is-disabled', !logoOn);

    // Badge controls only make sense when the grid is up AND the overlay badges are on — disable + grey otherwise.
    const badgeOn = on && !!v.overlayEnabled;
    els.badgeOpacity.disabled = els.badgeSize.disabled = els.badgePosition.disabled = !badgeOn;
    els.overlayAdvanced.classList.toggle('is-disabled', !badgeOn);
  }

  function renderFeeds(feeds, isConn, displays) {
    // Grow/shrink the cached card list to match the frame.
    while (rows.length < feeds.length) rows.push(makeRow());
    while (rows.length > feeds.length) els.feeds.removeChild(rows.pop().root);

    updateFeedSeparators(displays || []);

    // Latch the operator-set name + names-only ONCE per connection — only off a frame that actually carries
    // live feed data (a connected frame can briefly still hold offline placeholders before the first snapshot).
    const latchNow = isConn && !namesLatched && hasLiveData(feeds, isConn);

    for (let i = 0; i < feeds.length; i++) updateRow(rows[i], feeds[i], isConn, latchNow);

    if (latchNow) namesLatched = true;
  }

  // Multi-display walls get a thin separator label ("DISPLAY n · MON m") above each display's first feed
  // card, derived from visual.displays. Signature-keyed so the 1 Hz frames don't thrash the DOM; the rows
  // array itself stays untouched (cards keep flat global indices).
  let feedSepSig = '';
  const feedSeps = [];
  function updateFeedSeparators(displays) {
    const sig = displays.map((d) => (d.cells || 0) + '@' + (d.monitor || 0)).join() + ':' + rows.length;
    if (sig === feedSepSig) return;
    feedSepSig = sig;
    for (const s of feedSeps) s.remove();
    feedSeps.length = 0;
    if (displays.length < 2) return;
    let offset = 0;
    displays.forEach((d, i) => {
      if (offset < rows.length) {
        const sep = document.createElement('div');
        sep.className = 'feed-display-sep';
        sep.textContent = 'DISPLAY ' + i + ' · MON ' + (d.monitor || 0);
        els.feeds.insertBefore(sep, rows[offset].root);
        feedSeps.push(sep);
      }
      offset += d.cells || 0;
    });
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

    // Name row: editable vessel name (overlay label) + two compact labeled per-stream overlay toggles.
    // "Name" = name-only badge mode; "Logo" = brand watermark on this cell. Helper builds identical switches
    // (label + visually-hidden checkbox + track) — the checkbox must stay immediately before the track for the
    // `input:checked + .switch-track` CSS to flip it.
    const nameRow = document.createElement('div');
    nameRow.className = 'feed-name-row';
    const name = document.createElement('input');
    name.type = 'text';
    name.className = 'feed-name-input';
    name.placeholder = 'Vessel name';
    name.maxLength = 64;

    function makeToggle(labelText, titleText, on) {
      const wrap = document.createElement('label');
      wrap.className = 'switch switch-sm feed-toggle';
      wrap.title = titleText;
      const lbl = document.createElement('span');
      lbl.className = 'switch-label feed-toggle-label';
      lbl.textContent = labelText;
      const input = document.createElement('input');
      input.type = 'checkbox';
      input.checked = !!on;
      const track = document.createElement('span'); track.className = 'switch-track';
      const thumb = document.createElement('span'); thumb.className = 'switch-thumb';
      track.append(thumb);
      wrap.append(lbl, input, track);
      return { wrap, input };
    }

    const nameOnlyToggle = makeToggle('Name',
      'Name only: show just the vessel name + a health dot — hides stream #, IP & status.', false);
    const namesOnly = nameOnlyToggle.input;
    const logoToggle = makeToggle('Logo',
      'Logo: show the brand watermark on this stream’s cell. The global Brand logo switch must also be on.', true);
    const logo = logoToggle.input;
    nameRow.append(name, nameOnlyToggle.wrap, logoToggle.wrap);

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

    const r = { root, title, name, namesOnly, logo, reconnect, url, stats, feedIndex: 0 };

    // ---- per-feed command wiring (reads r.feedIndex, refreshed each frame) ----
    reconnect.addEventListener('click', () => send({ name: 'reconnect', index: r.feedIndex }));
    // Commit the name on blur-after-edit ('change') and on Enter (which blurs → fires 'change' if dirty).
    name.addEventListener('change', () => send({ name: 'setName', index: r.feedIndex, stringValue: r.name.value }));
    name.addEventListener('keydown', (e) => { if (e.key === 'Enter') { e.preventDefault(); r.name.blur(); } });
    namesOnly.addEventListener('change', () =>
      send({ name: 'setOverlayNamesOnly', index: r.feedIndex, boolValue: r.namesOnly.checked }));
    // Per-stream brand watermark. The backend command (setOverlayLogo) lands with the per-stream-logo
    // feature; until then the render process logs it as unknown and ignores it (harmless, toggle is visual).
    logo.addEventListener('change', () =>
      send({ name: 'setOverlayLogo', index: r.feedIndex, boolValue: r.logo.checked }));
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
    r.logo.disabled = !editable;
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
    // Seed/refresh the draft from live telemetry while the operator hasn't started editing. The active
    // draft = rendered feeds PLUS the rule-6 residual streams (active in config but without a cell — they
    // have no feed card, and applyRoster overwrites Config wholesale, so omitting them here would
    // permanently delete them from streams.json on the next Apply).
    if (f.connected && hasLiveData(f.feeds, f.connected) && !roster.dirty) {
      roster.active = (f.feeds || [])
        .map((x) => ({ url: x.url || '', name: x.name || '', namesOnly: !!x.namesOnly }))
        .concat((f.unassignedActive || []).map((p) => ({ url: p.url || '', name: p.name || '', namesOnly: !!p.namesOnly })));
      roster.pool = (f.inactivePool || []).map((p) => ({ url: p.url || '', name: p.name || '', namesOnly: !!p.namesOnly }));
      const ds = ((f.visual || {}).displays || []);
      roster.displays = ds.length
        ? ds.map((d) => ({ monitor: d.monitor || 0, layout: d.layout || 'auto', cells: d.cells || 1 }))
        : [{ monitor: ((f.visual || {}).monitor) || 0, layout: 'auto', cells: roster.active.length || 1 }];
      rosterLatched = true;
    }
    paintRoster();
  }

  // Repaint both lists + the add/apply/guard states. Gated on a signature so the 1 Hz clean re-seed doesn't
  // thrash the DOM when nothing actually changed.
  function paintRoster() {
    const enabled = connected && rosterLatched;
    // monitorCount is in the signature so a monitor hotplug/unplug refreshes the partition editor's
    // monitor dropdowns even when the draft itself hasn't changed.
    const monitorCount = Math.max(1, (((lastFrame || {}).visual || {}).monitorCount) || 0);
    const sig = JSON.stringify({ a: roster.active, p: roster.pool, ds: roster.displays, d: roster.dirty, e: enabled, m: monitorCount });
    if (sig === lastRosterSig) return;
    lastRosterSig = sig;

    const n = roster.active.length;
    els.rosterActiveCount.textContent = String(n);
    els.rosterPoolCount.textContent = String(roster.pool.length);

    paintList(els.rosterActive, roster.active, true, enabled);
    paintList(els.rosterPool, roster.pool, false, enabled);
    paintRosterDisplays(enabled, monitorCount);

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

  // The display-partition editor: monitor / layout / cells per display, add/remove display (1..8). This is
  // the ONLY place the dashboard edits the partition (the old live per-display selects competed with it and
  // could be silently reverted by a stale draft). It offers the FULL layout list and any cells value — the
  // restart path lets Normalize reconcile (clamp cells to capacity, shrink/grow against the active count),
  // and the Σ readout below warns about a mismatch without blocking Apply. Rows are rebuilt only on a structural change
  // (display count / monitor option range) and value-mirrored in place otherwise, so committing the cells
  // input (Enter / spinner / blur) never tears down the control under the operator's focus.
  let rosterDispSig = '';   // structural signature; '' forces a rebuild
  let rosterDispRows = [];  // cached row DOM {root, monitor, layout, cells, remove}
  let rosterAddBtn = null;

  function paintRosterDisplays(enabled, monitorCount) {
    const optCounts = roster.displays.map((d) => Math.max(monitorCount, (d.monitor || 0) + 1));
    const struct = roster.displays.length + ':' + monitorCount + ':' + optCounts.join();
    if (struct !== rosterDispSig) {
      rosterDispSig = struct;
      els.rosterDisplays.textContent = '';
      rosterDispRows = roster.displays.map((d, i) => makeRosterDisplayRow(i, optCounts[i], monitorCount));
      rosterAddBtn = btn('Add display', true, () => {
        roster.displays.push({ monitor: 0, layout: 'auto', cells: 1 });
        rosterDirty();
      });
      els.rosterDisplays.appendChild(rosterAddBtn);
    }

    // Mirror draft values + gate states in place (never over the control the operator is using).
    roster.displays.forEach((d, i) => {
      const row = rosterDispRows[i];
      if (!row) return;
      if (document.activeElement !== row.monitor) row.monitor.value = String(d.monitor);
      if (document.activeElement !== row.layout) {
        const val = String(d.layout || 'auto');
        // A valid non-preset layout (hand-edited config, e.g. "4x2" — the C# parser accepts any CxR) gets
        // its own option rather than masquerading as Auto: the select must show exactly what Apply sends.
        if (![...row.layout.options].some((o) => o.value === val)) {
          const opt = document.createElement('option');
          opt.value = val;
          opt.textContent = val;
          row.layout.appendChild(opt);
        }
        row.layout.value = val;
      }
      if (document.activeElement !== row.cells) row.cells.value = String(d.cells);
      row.monitor.disabled = row.layout.disabled = row.cells.disabled = !enabled;
      row.remove.disabled = !(enabled && roster.displays.length > 1);
    });
    rosterAddBtn.disabled = !(enabled && roster.displays.length < 8);

    // Σ cells readout: warn on a mismatch with the active count, but never block — Normalize reconciles
    // (shrinks from the tail / grows the last display; residual streams stay unrendered and are logged).
    const total = roster.displays.reduce((s, d) => s + (d.cells || 0), 0);
    const n = roster.active.length;
    if (total !== n) {
      els.rosterCellsNote.hidden = false;
      els.rosterCellsNote.textContent = '⚠ Displays take ' + total + ' cell' + (total === 1 ? '' : 's')
        + ' but ' + n + ' stream' + (n === 1 ? ' is' : 's are') + ' active — Apply will auto-reconcile '
        + (total > n ? '(the last display shrinks).' : '(the last display grows up to its layout; leftover streams won\'t render).');
    } else {
      els.rosterCellsNote.hidden = true;
    }
  }

  // One roster partition row. Handlers read/write roster.displays[i] at event time — rows are positional
  // and any add/remove changes the structural signature, which rebuilds the whole strip (fresh indices).
  function makeRosterDisplayRow(i, optCount, monitorCount) {
    const row = document.createElement('div');
    row.className = 'roster-display-row';

    const label = document.createElement('span');
    label.className = 'display-row-label';
    label.textContent = 'Display ' + i;

    const monitor = document.createElement('select');
    monitor.title = 'Which monitor this display\'s grid renders on (lands on Apply & restart). An "unplugged" '
      + 'entry is a monitor the saved config references that isn\'t connected right now — the grid falls '
      + 'back to monitor 0 until it returns.';
    fillMonitorOptions(monitor, optCount, monitorCount);
    monitor.addEventListener('change', () => {
      roster.displays[i].monitor = Number(monitor.value) || 0;
      rosterDirty();
    });

    const layout = document.createElement('select');
    layout.title = 'Layout preset for this display (lands on Apply & restart). Auto tiles near-square to its '
      + 'cell count; a fixed layout keeps its frame, leaving spare cells black.';
    for (const [val, text] of LAYOUTS) {
      const opt = document.createElement('option');
      opt.value = val;
      opt.textContent = text;
      layout.appendChild(opt);
    }
    layout.addEventListener('change', () => {
      roster.displays[i].layout = layout.value;
      rosterDirty();
    });

    const cells = document.createElement('input');
    cells.type = 'number';
    cells.className = 'roster-cells-input';
    cells.min = '1';
    cells.max = '16';
    cells.step = '1';
    cells.title = 'How many active streams this display takes (in roster order).';
    cells.addEventListener('change', () => {
      roster.displays[i].cells = Math.max(1, Math.min(16, Math.round(Number(cells.value)) || 1));
      cells.value = String(roster.displays[i].cells);   // show the clamp even while the input keeps focus
      rosterDirty();
    });

    const remove = btn('Remove', true, () => { roster.displays.splice(i, 1); rosterDirty(); });
    row.append(label, monitor, layout, cells, remove);
    els.rosterDisplays.appendChild(row);
    return { root: row, monitor, layout, cells, remove };
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
      r.logo.disabled = !editable;
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
        displays: roster.displays.map((d) => ({
          monitor: Number(d.monitor) || 0,
          layout: d.layout || 'auto',
          cells: Math.max(1, Number(d.cells) || 1),
        })),
      });
      if (!ok) { feedback(els.rosterFeedback, "Couldn't reach the grid.", 'warn'); return; }
      feedback(els.rosterFeedback, 'Applying — the wall blinks ~1 s while it restarts.', 'ok');
      // DON'T mark the draft clean yet: the render only restarts after a successful config save (a failed
      // save keeps the old wall and just logs). The restart's SSE drop is the success signal — handled in
      // render(), which then clears the draft so it re-seeds from the new roster on reconnect. If the drop
      // never comes, the save failed and the draft must survive instead of silently re-seeding away.
      rosterApplyPending = true;
      clearTimeout(rosterApplyTimer);
      rosterApplyTimer = setTimeout(() => {
        if (!rosterApplyPending) return;
        rosterApplyPending = false;
        feedback(els.rosterFeedback,
          "The wall didn't restart — the config save likely failed (check the log). Your changes are kept.", 'warn');
      }, 6000);
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
