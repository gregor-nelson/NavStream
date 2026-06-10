using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// Orchestrates the render process: builds one grid window + health overlay per configured display,
/// ONE shared engine over the concatenation of every form's views (forms in display order ⇒ global
/// feed order), and wires the hotkey callbacks identically on every form (global indices, so any
/// focused form can drive any feed). Engine creation is deferred until the LAST form is shown so the
/// native handles exist before mpv binds to them. Closing any form closes them all (symmetric
/// lifetime) and the process exits 0 → the supervisor relaunches.
///
/// Also hosts the control IPC server (D-DASH-1): it publishes per-feed telemetry + current config to
/// the separate dashboard process and executes the operator commands the dashboard sends back, each
/// marshalled onto the WinForms UI thread.
/// </summary>
internal sealed class RenderApp
{
    private readonly Config _config;
    private readonly List<GridForm> _forms = new();
    private readonly List<OverlayForm> _overlays = new();
    private readonly List<(int Offset, int Count)> _slices = new();   // per display: slice of the flat feed list
    private ApplicationContext? _ctx;
    private int _shownCount;
    private bool _closing;
    private Engine? _engine;
    private ControlServer? _server;

    public RenderApp(Config config) => _config = config;

    public void Run()
    {
        var partition = _config.GetDisplayPartition();

        // Normalize rule 6: a fixed last layout may not have capacity for every stream — those render nowhere.
        int assigned = partition.Sum(p => p.Count);
        if (assigned < _config.Streams.Count)
            Logger.Log($"Render: {_config.Streams.Count - assigned} stream(s) unassigned — the wall has {assigned} cell(s); they will not render.");

        foreach (var (display, offset, count) in partition)
        {
            var form = new GridForm(_config, display, _forms.Count);
            form.Shown += OnAnyFormShown;
            form.FormClosed += (_, _) => OnAnyFormClosed();
            _forms.Add(form);
            _slices.Add((offset, count));
        }

        // No MainForm — every form has the same say over the process lifetime (OnAnyFormClosed fans out).
        _ctx = new ApplicationContext();
        foreach (var form in _forms) form.Show();
        Application.Run(_ctx);
    }

    /// <summary>Any form closing tears the whole render process down (Esc anywhere, restartDisplay,
    /// applyRoster, mutex displacement): close the rest, exit the message loop → Run returns → exit 0 →
    /// supervisor relaunches. Guarded so the fan-out closes don't re-enter.</summary>
    private void OnAnyFormClosed()
    {
        if (_closing) return;
        _closing = true;
        Teardown();
        foreach (var form in _forms)
        {
            try { if (!form.IsDisposed) form.Close(); } catch { /* already gone */ }
        }
        _ctx?.ExitThread();
    }

    private void OnAnyFormShown(object? sender, EventArgs e)
    {
        // Init only after the LAST form is shown, so every view's native handle is realized. All Show()
        // calls precede Application.Run, so this lands within the first message pumps. The _closing check
        // is defense-in-depth against a teardown (e.g. mutex displacement) landing between Shown events.
        if (_closing || ++_shownCount < _forms.Count || _engine is not null) return;
        InitEngine();
    }

    private void InitEngine()
    {
        try
        {
            // ONE engine over all forms' views — forms are built in display order, so the concatenation
            // is the flat global feed order (FeedController.Index/CellNumber stay continuous).
            var allViews = _forms.SelectMany(f => f.Views).ToList();
            _engine = new Engine(_config, allViews);

            var displays = _config.Displays!;
            for (int d = 0; d < _forms.Count; d++)
            {
                var form = _forms[d];
                var (offset, count) = _slices[d];
                var spec = LayoutSpec.Parse(displays[d].Layout);

                var overlay = new OverlayForm(form, form.Bounds, _config.AlwaysOnTop, spec);
                overlay.SetFeeds(_engine.Feeds.Skip(offset).Take(count).ToList());
                overlay.Reposition(form.Bounds);
                _overlays.Add(overlay);

                // Wire hotkeys — identical on every form: D1–D9 carry global indices into the shared
                // engine, so any focused form can reconnect any feed on any display.
                form.ForceReconnect = i =>
                {
                    if (i >= 0 && i < _engine.Feeds.Count) _engine.Feeds[i].ForceReconnect();
                };
                form.ToggleOverlay = () =>
                {
                    // Keep Config (the dashboard's source of truth for overlay state) in sync with the
                    // H hotkey, then nudge the dashboard so its checkbox tracks the grid.
                    SetOverlay(!_config.OverlayEnabled);
                };
                form.OnLayoutChanged = () => overlay.Reposition(form.Bounds);
            }

            // Repaint the owning overlay whenever any feed's state/stats change (marshalled to the UI
            // thread), and push a fresh telemetry frame to the dashboard.
            foreach (var feed in _engine.Feeds)
                feed.Changed += OnFeedChanged;

            foreach (var overlay in _overlays)
                overlay.SetLogoVisible(_config.LogoEnabled);
            ApplyLogoParams();   // push persisted opacity/brightness/size/position into the overlays
            ApplyBadgeParams();  // push persisted badge opacity/size/position into the overlays
            foreach (var overlay in _overlays)
            {
                overlay.Visible = _config.OverlayEnabled;
                overlay.Show();
            }

            _engine.Start();

            // Start the control IPC server now that engine + overlays exist.
            _server = new ControlServer(BuildSnapshot, HandleCommand);
            _server.Start();

            Logger.Log($"Render: engine + {_overlays.Count} overlay(s) + control server initialized ({_forms.Count} display(s)).");
        }
        catch (Exception ex)
        {
            Logger.Log($"Render: FATAL during init: {ex}");
            throw; // -> global unhandled handler -> exit non-zero -> supervisor restarts
        }
    }

    /// <summary>Ask the render windows to close from another thread (mutex displacement / supervisor stop).
    /// Closing one fans out to the rest via OnAnyFormClosed.</summary>
    public void RequestExit()
    {
        var f = _forms.FirstOrDefault();
        if (f is null || !f.IsHandleCreated) return;
        Logger.Log("Render: stop signalled by another instance — closing.");
        try { f.BeginInvoke(() => { try { f.Close(); } catch { } }); }
        catch { /* form already gone */ }
    }

    /// <summary>The overlay that paints a given global feed index, via the display slices.</summary>
    private OverlayForm? OverlayFor(int feedIndex)
    {
        for (int d = 0; d < _overlays.Count && d < _slices.Count; d++)
        {
            if (feedIndex >= _slices[d].Offset && feedIndex < _slices[d].Offset + _slices[d].Count)
                return _overlays[d];
        }
        return null;
    }

    private void OnFeedChanged(FeedController feed)
    {
        // Route the repaint to the owning overlay only — no need to rebuild every monitor-sized bitmap
        // on each stat tick of a single feed.
        var overlay = OverlayFor(feed.Index);
        if (overlay is not null && overlay.IsHandleCreated)
        {
            try { overlay.BeginInvoke(() => overlay.RenderNow()); }
            catch { /* form closing */ }
        }
        // Telemetry push is thread-safe and independent of the UI thread.
        _server?.PushNow();
    }

    // ---- Control IPC: telemetry out, commands in (D-DASH-1) ----

    /// <summary>Build a telemetry frame from current engine/overlay/config state. Called on the
    /// control-server background thread; reads are simple field/property reads (benign cross-thread).</summary>
    private ControlSnapshot BuildSnapshot()
    {
        var snap = new ControlSnapshot
        {
            ConfigPath = _config.SourcePath,
            RenderPid = Environment.ProcessId,
        };

        var engine = _engine;
        if (engine is not null)
        {
            foreach (var f in engine.Feeds)
            {
                snap.Feeds.Add(new FeedSnapshot
                {
                    Index = f.Index,
                    Cell = f.CellNumber,
                    Name = f.Name,
                    NamesOnly = f.NamesOnly,
                    Url = f.Url,
                    State = f.State.ToString(),
                    Health = f.Health.ToString(),
                    BitrateKbps = f.InputBitrateKbps,
                    LostPictures = f.LostPictures,
                    DecodedVideo = f.DecodedVideo,
                    Status = f.StatusText(),
                });
            }
        }

        int rendered = _slices.Sum(s => s.Count);   // cells actually on the wall (Σ slice counts)

        snap.Visual = new VisualSnapshot
        {
            OverlayEnabled = _config.OverlayEnabled,   // kept in sync on every toggle (avoids cross-thread control reads)
            LogoEnabled = _config.LogoEnabled,
            LogoOpacityPct = _config.LogoOpacityPct,
            LogoBrightnessPct = _config.LogoBrightnessPct,
            LogoSizePct = _config.LogoSizePct,
            LogoPosition = _config.LogoPosition,
            BadgeOpacityPct = _config.BadgeOpacityPct,
            BadgeSizePct = _config.BadgeSizePct,
            BadgePosition = _config.BadgePosition,
            Monitor = _config.Monitor,   // legacy mirror of Displays[0].Monitor (old clients keep working)
            MonitorCount = Screen.AllScreens.Length,
            Borderless = _config.Borderless,
            AlwaysOnTop = _config.AlwaysOnTop,
            // Copy (not alias) so telemetry never shares the live config's display list.
            Displays = (_config.Displays ?? new List<DisplayConfig>())
                .Select(d => new DisplayConfig { Monitor = d.Monitor, Layout = d.Layout, Cells = d.Cells })
                .ToList(),
            // Normalize rule 6 residual: streams beyond the wall's cell count have no feed (and no feed
            // card), so the dashboard can only warn about them via this count.
            UnassignedStreams = Math.Max(0, _config.Streams.Count - rendered),
        };

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
            BackoffMaxMs = _config.BackoffMaxMs,
            BackoffFactor = _config.BackoffFactor,
            BackoffResetMs = _config.BackoffResetMs,
            StallTimeoutMs = _config.StallTimeoutMs,
        };

        // Copy (not alias) so telemetry never shares the live config's pool list.
        snap.InactivePool = _config.InactivePool
            .Select(p => new InactiveStream { Url = p.Url, Name = p.Name, NamesOnly = p.NamesOnly })
            .ToList();

        // Rule-6 residual streams (active in config, no cell on the wall): the roster editor seeds its
        // draft from feeds + this list — without it an applyRoster would silently drop them from config.
        for (int i = rendered; i < _config.Streams.Count; i++)
        {
            snap.UnassignedActive.Add(new InactiveStream
            {
                Url = _config.Streams[i],
                Name = i < _config.Names.Count ? _config.Names[i] : "",
                NamesOnly = i < _config.OverlayNamesOnly.Count && _config.OverlayNamesOnly[i],
            });
        }

        return snap;
    }

    /// <summary>A command arrived from the dashboard (background thread). Marshal to the UI thread
    /// (all forms share it — any realized handle will do).</summary>
    private void HandleCommand(ControlCommand cmd)
    {
        var f = _forms.FirstOrDefault();
        if (f is null || !f.IsHandleCreated) return;
        try { f.BeginInvoke(() => DispatchCommand(cmd)); }
        catch { /* form closing */ }
    }

    private void DispatchCommand(ControlCommand cmd)
    {
        switch (cmd.Name)
        {
            case ControlCommands.Reconnect:
                if (_engine is not null && cmd.Index >= 0 && cmd.Index < _engine.Feeds.Count)
                    _engine.Feeds[cmd.Index].ForceReconnect();
                break;

            case ControlCommands.RestartDisplay:
                // Same proven path as Esc: close a window (fans out to all) → exit 0 → supervisor relaunches.
                Logger.Log("Control: restart display — closing render (supervisor will relaunch).");
                _forms.FirstOrDefault()?.Close();
                break;

            case ControlCommands.SetOverlay:
                SetOverlay(cmd.BoolValue);
                break;

            case ControlCommands.SetLogo:
                SetLogo(cmd.BoolValue);
                break;

            // ---- Brand-watermark appearance (live; persisted on Save) ----
            case ControlCommands.SetLogoOpacity:
                _config.LogoOpacityPct = Math.Clamp(cmd.IntValue, 0, 80);
                ApplyLogoParams();
                _server?.PushNow();
                break;

            case ControlCommands.SetLogoBrightness:
                _config.LogoBrightnessPct = Math.Clamp(cmd.IntValue, 0, 100);
                ApplyLogoParams();
                _server?.PushNow();
                break;

            case ControlCommands.SetLogoSize:
                _config.LogoSizePct = Math.Clamp(cmd.IntValue, 5, 30);
                ApplyLogoParams();
                _server?.PushNow();
                break;

            case ControlCommands.SetLogoPosition:
                _config.LogoPosition = Math.Clamp(cmd.IntValue, 0, 3);
                ApplyLogoParams();
                _server?.PushNow();
                break;

            // ---- Health-overlay badge appearance (live; persisted on Save) ----
            case ControlCommands.SetBadgeOpacity:
                _config.BadgeOpacityPct = Math.Clamp(cmd.IntValue, 30, 100);
                ApplyBadgeParams();
                _server?.PushNow();
                break;

            case ControlCommands.SetBadgeSize:
                _config.BadgeSizePct = Math.Clamp(cmd.IntValue, 60, 160);
                ApplyBadgeParams();
                _server?.PushNow();
                break;

            case ControlCommands.SetBadgePosition:
                _config.BadgePosition = Math.Clamp(cmd.IntValue, 0, 3);
                ApplyBadgeParams();
                _server?.PushNow();
                break;

            case ControlCommands.FullStop:
                Logger.Log("Control: full stop requested from dashboard — exiting (supervisor stops too).");
                Environment.Exit(Constants.ExitStop);
                break;

            // ---- Phase B: window / visual params (live) ----
            case ControlCommands.SetMonitor:
                // Index = display (old clients send no index ⇒ 0 ⇒ display 0, the legacy behavior). A stale
                // client can target a display that no longer exists — drop the command rather than clamp it,
                // or it would silently move a DIFFERENT display's window.
                if (cmd.Index >= 0 && cmd.Index < _forms.Count)
                {
                    _forms[cmd.Index].SetMonitor(cmd.IntValue);
                    var displays = _config.Displays!;
                    _config.Monitor = displays[0].Monitor;   // keep the legacy key mirrored
                    // Duplicates are allowed + logged: rejecting would deadlock a two-monitor swap, and the
                    // unplug fallback (TargetScreenBounds → screen 0) can force a duplicate anyway.
                    if (displays.GroupBy(x => x.Monitor).Any(g => g.Count() > 1))
                        Logger.Log("Control: multiple displays now share a monitor (allowed; they will stack).");
                }
                else
                    Logger.Log($"Control: setMonitor for display {cmd.Index} ignored — wall has {_forms.Count} display(s) (stale client?).");
                _server?.PushNow();
                break;

            case ControlCommands.SetLayout:
                // Live re-tile only — capacity-gated by GridForm (a partition change goes through the
                // save+restart path instead). Push either way so the UI re-syncs. Same stale-index
                // rejection as setMonitor: clamping would re-tile the wrong display.
                if (cmd.Index >= 0 && cmd.Index < _forms.Count)
                {
                    var spec = LayoutSpec.Parse(cmd.StringValue);
                    if (_forms[cmd.Index].SetLayout(spec) && cmd.Index < _overlays.Count)
                        _overlays[cmd.Index].SetLayout(spec);
                }
                else
                    Logger.Log($"Control: setLayout for display {cmd.Index} ignored — wall has {_forms.Count} display(s) (stale client?).");
                _server?.PushNow();
                break;

            case ControlCommands.ApplyDisplays:
                if (cmd.Displays is not null) ApplyDisplays(cmd.Displays);
                break;

            case ControlCommands.SetBorderless:
                foreach (var form in _forms) form.SetBorderless(cmd.BoolValue);
                _server?.PushNow();
                break;

            case ControlCommands.SetAlwaysOnTop:
                foreach (var form in _forms) form.SetAlwaysOnTop(cmd.BoolValue);
                _server?.PushNow();
                break;

            // ---- Overlay: per-feed vessel name + "name only" mode (live, display-only) ----
            case ControlCommands.SetName:
                if (_engine is not null && cmd.Index >= 0 && cmd.Index < _engine.Feeds.Count)
                {
                    string name = (cmd.StringValue ?? string.Empty).Trim();
                    _engine.Feeds[cmd.Index].SetName(name);
                    SetListItem(_config.Names, cmd.Index, name);   // mirror so Save persists it
                    OverlayFor(cmd.Index)?.RenderNow();
                    _server?.PushNow();
                }
                break;

            case ControlCommands.SetOverlayNamesOnly:
                if (_engine is not null && cmd.Index >= 0 && cmd.Index < _engine.Feeds.Count)
                {
                    _engine.Feeds[cmd.Index].SetNamesOnly(cmd.BoolValue);
                    SetListItem(_config.OverlayNamesOnly, cmd.Index, cmd.BoolValue);   // mirror so Save persists it
                    OverlayFor(cmd.Index)?.RenderNow();
                    _server?.PushNow();
                }
                break;

            // ---- Phase C: engine-setting knobs + Apply/Save ----
            case ControlCommands.ApplySettings:
                if (cmd.Settings is not null) ApplySettings(cmd.Settings);
                break;

            case ControlCommands.SetStreams:
                if (cmd.Streams is not null) ApplyStreams(cmd.Streams);
                break;

            case ControlCommands.Save:
                // Save = persist whatever is currently live. If the dashboard attached the latest UI
                // source URLs / settings, apply them first so "what you see" is exactly what gets written.
                if (cmd.Streams is not null) ApplyStreams(cmd.Streams);
                if (cmd.Settings is not null) ApplySettings(cmd.Settings);
                SaveConfig();
                break;

            case ControlCommands.ApplyRoster:
                ApplyRoster(cmd);
                break;

            default:
                Logger.Log($"Control: unknown command '{cmd.Name}'.");
                break;
        }
    }

    /// <summary>Apply overlay visibility live (all displays), keep Config in sync, and refresh the dashboard. UI thread.</summary>
    private void SetOverlay(bool on)
    {
        _config.OverlayEnabled = on;
        foreach (var overlay in _overlays) overlay.Visible = on;
        _server?.PushNow();
    }

    /// <summary>Apply brand-watermark visibility live (all displays), keep Config in sync, and refresh the dashboard. UI thread.</summary>
    private void SetLogo(bool on)
    {
        _config.LogoEnabled = on;
        foreach (var overlay in _overlays) overlay.SetLogoVisible(on);
        _server?.PushNow();
    }

    /// <summary>Push the four persisted watermark-appearance knobs from <see cref="Config"/> into the overlay,
    /// mapping each dashboard percent/enum to the overlay's render units (alpha &amp; width 0..1, brightness a
    /// 0..0.5 RGB lift, corner 0..3). Each overlay setter no-ops if its value is unchanged, so calling this
    /// after a single-knob command only repaints once. UI thread.</summary>
    private void ApplyLogoParams()
    {
        foreach (var overlay in _overlays)
        {
            overlay.SetLogoOpacity(_config.LogoOpacityPct / 100f);
            overlay.SetLogoBrightness(_config.LogoBrightnessPct / 100f * 0.5f);
            overlay.SetLogoSize(_config.LogoSizePct / 100f);
            overlay.SetLogoPosition(_config.LogoPosition);
        }
    }

    /// <summary>Push the three persisted health-overlay badge knobs from <see cref="Config"/> into the overlay,
    /// mapping each dashboard percent/enum to the overlay's render units (opacity &amp; size are /100 fractions,
    /// corner 0..3). Each overlay setter no-ops if unchanged, so calling this after a single-knob command only
    /// repaints once. UI thread.</summary>
    private void ApplyBadgeParams()
    {
        foreach (var overlay in _overlays)
        {
            overlay.SetBadgeOpacity(_config.BadgeOpacityPct / 100f);
            overlay.SetBadgeSize(_config.BadgeSizePct / 100f);
            overlay.SetBadgePosition(_config.BadgePosition);
        }
    }

    /// <summary>Set a position-aligned config list element by index, padding the list with defaults up to
    /// <paramref name="index"/> first. Keeps <see cref="Config.Names"/> / <see cref="Config.OverlayNamesOnly"/>
    /// aligned with the feeds even when earlier slots were never edited, so a later Save writes the right slot.</summary>
    private static void SetListItem<T>(List<T> list, int index, T value)
    {
        if (index < 0) return;
        while (list.Count <= index) list.Add(default!);
        list[index] = value;
    }

    /// <summary>Apply a settings frame from the dashboard into the shared <see cref="Config"/> and make it
    /// take effect live, by category (D-DASH-2):
    ///  • per-stream options (cache-secs, rtsp-transport, skiploopfilter) → force-reconnect all feeds;
    ///  • HwDecode → live per-feed reset + reconnect (a single reconnect also picks up the per-media options);
    ///  • backoff / stall timings → read live from Config by the stats/reconnect code, no reconnect needed;
    ///  • ExtraMpvArgs → core args set once at engine construction → only effective after Save + Restart
    ///    Display (the relaunched process reloads streams.json). We copy them in and log; the dashboard's UI
    ///    says as much. UI thread.</summary>
    private void ApplySettings(SettingsSnapshot s)
    {
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
        bool hwChanged = s.HwDecode != _config.HwDecode;
        bool extraArgsChanged = !_config.ExtraMpvArgs.SequenceEqual(s.ExtraMpvArgs);

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
        _config.BackoffMaxMs = s.BackoffMaxMs;
        _config.BackoffFactor = s.BackoffFactor;
        _config.BackoffResetMs = s.BackoffResetMs;
        _config.StallTimeoutMs = s.StallTimeoutMs;

        _config.Normalize(); // clamp any cross-field oddities (e.g. max < start) before feeds re-read it

        // One reconnect is enough: SetHwDecodeAll force-reconnects, and ApplyStreamOptions re-reads the per-stream
        // options from Config on that reconnect — so when HW changed we skip the separate ForceReconnectAll.
        if (hwChanged) _engine?.SetHwDecodeAll(_config.HwDecode);
        else if (perMediaChanged) _engine?.ForceReconnectAll();

        if (extraArgsChanged)
            Logger.Log("Control: ExtraMpvArgs changed — core args, effective only after Save + Restart Display.");

        Logger.Log($"Control: settings applied (perMedia={perMediaChanged}, hw={hwChanged}, extraArgs={extraArgsChanged}).");
        _server?.PushNow();
    }

    /// <summary>Apply per-cell source URLs from the dashboard's Sources group and swap each *changed* feed
    /// live. The list is positional and fixed to the existing cells: a blank entry leaves that feed as-is
    /// (so the operator can't accidentally blank a cell, and we never re-pack the list the way
    /// <see cref="Config.Normalize"/> would — that would misalign cells with feeds). Only feeds whose URL
    /// actually differs reconnect, so editing one cell never blinks the others. <see cref="Config.Streams"/>
    /// is mirrored by position (same pattern as Names/OverlayNamesOnly) so a later Save persists it. UI thread.</summary>
    private void ApplyStreams(List<string> urls)
    {
        var engine = _engine;
        if (engine is null) return;

        int changed = 0;
        for (int i = 0; i < engine.Feeds.Count && i < urls.Count; i++)
        {
            string url = (urls[i] ?? string.Empty).Trim();
            if (url.Length == 0) continue;                                       // blank = leave this feed as-is
            if (string.Equals(url, engine.Feeds[i].Url, StringComparison.Ordinal)) continue;

            SetListItem(_config.Streams, i, url);   // keep Config.Streams aligned so Save writes the right slot
            engine.SetStreamUrl(i, url);            // live swap: only this cell rebuilds Media + reconnects
            changed++;
        }

        if (changed > 0) Logger.Log($"Control: {changed} stream source(s) changed — reconnecting those feed(s).");
        _server?.PushNow();
    }

    /// <summary>Apply a full roster from the dashboard: overwrite Config wholesale (active arrays + pool),
    /// persist, then restart the display so a fresh process re-reads streams.json (atomic at the process
    /// boundary). Never a positional merge — see HANDOVER-dynamic-grid-tier1-execution.md §PRECEDENCE. UI thread.</summary>
    private void ApplyRoster(ControlCommand cmd)
    {
        var streams   = cmd.Streams      ?? new List<string>();
        var names     = cmd.Names        ?? new List<string>();
        var namesOnly = cmd.NamesOnly    ?? new List<bool>();
        var pool      = cmd.InactivePool ?? new List<InactiveStream>();

        // Zip + drop any active entry with a blank URL, carrying its aligned name/namesOnly with it, so the
        // three parallel lists stay index-aligned (Config.Normalize filters blank Streams but does NOT drop the
        // aligned Names/OverlayNamesOnly — doing the filter here avoids that shift).
        var act = new List<(string url, string name, bool no)>();
        for (int i = 0; i < streams.Count; i++)
        {
            string url = (streams[i] ?? string.Empty).Trim();
            if (url.Length == 0) continue;
            string nm  = i < names.Count ? (names[i] ?? string.Empty).Trim() : string.Empty;
            bool   no  = i < namesOnly.Count && namesOnly[i];
            act.Add((url, nm, no));
        }

        // Keep the old lists so a failed save can roll the in-memory config back — otherwise the snapshot,
        // a later plain Save, and the forms' DisplayConfig references would describe a roster that never
        // made it to disk while the live wall still runs the old one.
        var old = (_config.Streams, _config.Names, _config.OverlayNamesOnly, _config.InactivePool, _config.Displays);

        _config.Streams          = act.Select(a => a.url).ToList();
        _config.Names            = act.Select(a => a.name).ToList();
        _config.OverlayNamesOnly = act.Select(a => a.no).ToList();
        _config.InactivePool     = pool;          // Normalize() (inside Save) trims/filters/caps the pool
        if (cmd.Displays is not null) _config.Displays = cmd.Displays;   // partition rides along; Normalize reconciles Cells vs the new roster size
        _config.Normalize();

        try { _config.Save(); }
        catch (Exception ex)
        {
            (_config.Streams, _config.Names, _config.OverlayNamesOnly, _config.InactivePool, _config.Displays) = old;
            _config.Normalize();   // idempotent — restores the canonical pre-apply state
            Logger.Log($"Control: applyRoster save FAILED, NOT restarting (config rolled back): {ex.Message}");
            _server?.PushNow();
            return;   // keep the live grid on the old roster
        }

        Logger.Log($"Control: roster applied ({_config.Streams.Count} active, {_config.InactivePool.Count} pooled, {_config.Displays!.Count} display(s)) — restarting display.");
        _forms.FirstOrDefault()?.Close();   // fans out to all forms → exit 0 → supervisor relaunches → fresh Config.Load()
    }

    /// <summary>Apply a wholesale display partition from the dashboard: overwrite, Normalize (which
    /// reconciles Cells vs the stream count), persist, then restart so the fresh process rebuilds the
    /// forms — same save-fail-⇒-no-restart guard as <see cref="ApplyRoster"/>. UI thread.</summary>
    private void ApplyDisplays(List<DisplayConfig> displays)
    {
        var old = _config.Displays;   // for rollback — same reasoning as ApplyRoster
        _config.Displays = displays;
        _config.Normalize();

        try { _config.Save(); }
        catch (Exception ex)
        {
            _config.Displays = old;
            _config.Normalize();   // idempotent — restores the canonical pre-apply state
            Logger.Log($"Control: applyDisplays save FAILED, NOT restarting (config rolled back): {ex.Message}");
            _server?.PushNow();
            return;   // keep the live wall on the old partition
        }

        Logger.Log($"Control: display partition applied ({_config.Displays!.Count} display(s)) — restarting display.");
        _forms.FirstOrDefault()?.Close();   // fans out to all forms → exit 0 → supervisor relaunches
    }

    /// <summary>Persist the current live config to streams.json (D-DASH-3 Save). UI thread.</summary>
    private void SaveConfig()
    {
        try
        {
            _config.Save();
            Logger.Log($"Control: config saved to {_config.SourcePath}.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Control: config save FAILED: {ex.Message}");
        }
        _server?.PushNow();
    }

    private void Teardown()
    {
        try { _server?.Dispose(); } catch { }
        _server = null;
        try { _engine?.Dispose(); } catch { }
        _engine = null;
        foreach (var overlay in _overlays)
        {
            try { overlay.Dispose(); } catch { }
        }
        _overlays.Clear();
        Logger.Log("Render: torn down.");
    }
}
