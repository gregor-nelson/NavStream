'use strict';

// MpvGrid browser control — Phase 3 (final): live renderer + the full operator control set.
// The server (ViewFrameBuilder) still does all the merge/staleness/banner logic and pushes a flat
// ViewFrame over SSE; this script paints it AND sends operator actions back via POST /command. It holds
// no business logic — every command is one documented verb the C# dispatcher (TrayContext.OnCommand)
// already routes to the render pipe or the supervisor. Phase 3 adds the mpv-settings panel (Apply/Save),
// the "Live video feed" master toggle, and the Shutdown Displays / Exit Application power controls.

(function () {
  const token = new URLSearchParams(location.search).get('token') || '';

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
    // mpv settings panel (the whole <fieldset> disables together when the grid is down)
    settingsFields: document.getElementById('settings-fields'),
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
      if (!confirm('Restart the whole 4-feed display now? (the grid blinks ~1 s)')) return;
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
      if (!confirm('Shut down all displays now? This disconnects every stream and force-kills all MpvGrid '
        + 'render processes (including any orphaned ones). The grid goes dark; this dashboard stays open '
        + "— switch 'Live video feed' back on to relaunch.")) return;
      if (!(await send({ name: 'shutdownDisplays' })))
        feedback(els.cmdFeedback, "Can't reach the supervisor to shut down displays.", 'warn');
    });

    els.exit.addEventListener('click', async () => {
      if (!confirm('Exit MpvGrid completely? This stops every stream and closes the whole application '
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
      rtspOverTcp: els.rtspTcp.checked,
      skipLoopFilter: els.skipLoop.checked,
      hwDecode: els.hwDecode.checked,
      extraMpvArgs: els.extraArgs.value.split(/\s+/).filter(Boolean),
      // RTP / RTSP transport (camelCase = SettingsSnapshot fields; server clamps to range)
      rtspHttpTunnel: els.rtspHttp.checked,
      rtspFrameBufferSizeBytes: intVal(els.rtspFrameBuffer),
      rtpTimeoutSec: intVal(els.rtpTimeout),
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
    // Banner (big tri-state line) — colour driven entirely by the server's kind.
    els.banner.textContent = f.banner.text;
    els.banner.className = 'banner ' + f.banner.kind;

    // Status (dim second line).
    els.status.textContent = f.status.text || '';
    els.status.className = 'status ' + (f.status.kind === 'none' ? '' : f.status.kind);

    // A dropped connection re-arms the name + settings latches so the next live snapshot repopulates them.
    if (!f.connected && connected) { namesLatched = false; settingsLatched = false; }
    connected = f.connected;
    feedStopped = !!f.feedStopped;

    renderHeader(f);
    renderDisplay(f);
    renderSettings(f);
    renderPower(f);
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
    // the 4 offline placeholders, in which case f.settings is a zeroed default, not the real config.
    const hasLive = (f.feeds || []).some((x) => x.health && x.health !== 'Offline');
    if (!f.connected || settingsLatched || !hasLive) return;

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
    const editable = isConn && !offline;
    r.name.disabled = !editable;
    r.namesOnly.disabled = !editable;
    r.reconnect.disabled = !editable;
    r.url.disabled = !isConn;
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

  wireControls();
  connect();
})();
