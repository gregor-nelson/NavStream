using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// Orchestrates the render process: builds the grid window, the engine (4 feeds), and the health
/// overlay, and wires the hotkey callbacks. Engine creation is deferred until the form is shown so
/// the VideoView native handles exist before MediaPlayers bind to them.
///
/// Also hosts the control IPC server (D-DASH-1): it publishes per-feed telemetry + current config to
/// the separate dashboard process and executes the operator commands the dashboard sends back, each
/// marshalled onto the WinForms UI thread.
/// </summary>
internal sealed class RenderApp
{
    private readonly Config _config;
    private GridForm? _form;
    private Engine? _engine;
    private OverlayForm? _overlay;
    private ControlServer? _server;

    public RenderApp(Config config) => _config = config;

    public void Run()
    {
        _form = new GridForm(_config);
        _form.Shown += OnFormShown;
        _form.FormClosed += (_, _) => Teardown();

        Application.Run(_form);
    }

    private void OnFormShown(object? sender, EventArgs e)
    {
        if (_form is null || _engine is not null) return; // init once

        try
        {
            _engine = new Engine(_config, _form.Views);

            _overlay = new OverlayForm(_form, _form.Bounds, _config.AlwaysOnTop);
            _overlay.SetFeeds(_engine.Feeds);
            _overlay.Reposition(_form.Bounds);

            // Wire hotkeys.
            _form.ForceReconnect = i =>
            {
                if (i >= 0 && i < _engine.Feeds.Count) _engine.Feeds[i].ForceReconnect();
            };
            _form.ToggleOverlay = () =>
            {
                // Keep Config (the dashboard's source of truth for overlay state) in sync with the
                // H hotkey, then nudge the dashboard so its checkbox tracks the grid.
                bool on = !(_overlay?.Visible ?? false);
                SetOverlay(on);
            };
            _form.OnLayoutChanged = () =>
            {
                if (_form is not null) _overlay?.Reposition(_form.Bounds);
            };

            // Repaint overlay whenever any feed's state/stats change (marshalled to the UI thread),
            // and push a fresh telemetry frame to the dashboard.
            foreach (var feed in _engine.Feeds)
                feed.Changed += OnFeedChanged;

            _overlay.Visible = _config.OverlayEnabled;
            _overlay.Show();

            _engine.Start();

            // Start the control IPC server now that engine + overlay exist.
            _server = new ControlServer(BuildSnapshot, HandleCommand);
            _server.Start();

            Logger.Log("Render: engine + overlay + control server initialized.");
        }
        catch (Exception ex)
        {
            Logger.Log($"Render: FATAL during init: {ex}");
            throw; // -> global unhandled handler -> exit non-zero -> supervisor restarts
        }
    }

    /// <summary>Ask the render window to close from another thread (mutex displacement / supervisor stop).</summary>
    public void RequestExit()
    {
        var f = _form;
        if (f is null || !f.IsHandleCreated) return;
        Logger.Log("Render: stop signalled by another instance — closing.");
        try { f.BeginInvoke(() => { try { f.Close(); } catch { } }); }
        catch { /* form already gone */ }
    }

    private void OnFeedChanged(FeedController feed)
    {
        var overlay = _overlay;
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

        snap.Visual = new VisualSnapshot
        {
            OverlayEnabled = _config.OverlayEnabled,   // kept in sync on every toggle (avoids cross-thread control reads)
            Monitor = _config.Monitor,
            MonitorCount = Screen.AllScreens.Length,
            Borderless = _config.Borderless,
            AlwaysOnTop = _config.AlwaysOnTop,
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

        return snap;
    }

    /// <summary>A command arrived from the dashboard (background thread). Marshal to the UI thread.</summary>
    private void HandleCommand(ControlCommand cmd)
    {
        var f = _form;
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
                // Same proven path as Esc: close the window → process exits 0 → supervisor relaunches.
                Logger.Log("Control: restart display — closing render (supervisor will relaunch).");
                _form?.Close();
                break;

            case ControlCommands.SetOverlay:
                SetOverlay(cmd.BoolValue);
                break;

            case ControlCommands.FullStop:
                Logger.Log("Control: full stop requested from dashboard — exiting (supervisor stops too).");
                Environment.Exit(Constants.ExitStop);
                break;

            // ---- Phase B: window / visual params (live) ----
            case ControlCommands.SetMonitor:
                _form?.SetMonitor(cmd.IntValue);
                _server?.PushNow();
                break;

            case ControlCommands.SetBorderless:
                _form?.SetBorderless(cmd.BoolValue);
                _server?.PushNow();
                break;

            case ControlCommands.SetAlwaysOnTop:
                _form?.SetAlwaysOnTop(cmd.BoolValue);
                _server?.PushNow();
                break;

            // ---- Overlay: per-feed vessel name + "name only" mode (live, display-only) ----
            case ControlCommands.SetName:
                if (_engine is not null && cmd.Index >= 0 && cmd.Index < _engine.Feeds.Count)
                {
                    string name = (cmd.StringValue ?? string.Empty).Trim();
                    _engine.Feeds[cmd.Index].SetName(name);
                    SetListItem(_config.Names, cmd.Index, name);   // mirror so Save persists it
                    _overlay?.RenderNow();
                    _server?.PushNow();
                }
                break;

            case ControlCommands.SetOverlayNamesOnly:
                if (_engine is not null && cmd.Index >= 0 && cmd.Index < _engine.Feeds.Count)
                {
                    _engine.Feeds[cmd.Index].SetNamesOnly(cmd.BoolValue);
                    SetListItem(_config.OverlayNamesOnly, cmd.Index, cmd.BoolValue);   // mirror so Save persists it
                    _overlay?.RenderNow();
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

    /// <summary>Apply overlay visibility live, keep Config in sync, and refresh the dashboard. UI thread.</summary>
    private void SetOverlay(bool on)
    {
        _config.OverlayEnabled = on;
        if (_overlay is not null) _overlay.Visible = on;
        _server?.PushNow();
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

        _config.Streams          = act.Select(a => a.url).ToList();
        _config.Names            = act.Select(a => a.name).ToList();
        _config.OverlayNamesOnly = act.Select(a => a.no).ToList();
        _config.InactivePool     = pool;          // Normalize() (inside Save) trims/filters/caps the pool
        _config.Normalize();

        try { _config.Save(); }
        catch (Exception ex)
        {
            Logger.Log($"Control: applyRoster save FAILED, NOT restarting: {ex.Message}");
            _server?.PushNow();
            return;   // keep the live grid on the old roster
        }

        Logger.Log($"Control: roster applied ({_config.Streams.Count} active, {_config.InactivePool.Count} pooled) — restarting display.");
        _form?.Close();   // exit 0 → supervisor relaunches → fresh Config.Load() reads the new roster
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
        try { _overlay?.Dispose(); } catch { }
        _overlay = null;
        Logger.Log("Render: torn down.");
    }
}
