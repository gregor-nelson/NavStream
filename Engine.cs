using MpvGrid.Mpv;

namespace MpvGrid;

/// <summary>
/// Owns the 4 per-feed controllers (spec §4). Unlike the previous build there is no shared instance to
/// own: with libmpv each <see cref="FeedController"/> creates its own mpv handle (D3 — one handle per
/// feed). The engine just builds the engine-wide core options once (§3.1) and hands them, plus each
/// <see cref="MpvHost"/> surface, to the controllers. Each controller runs its own independent reconnect
/// state machine (§5) so one feed failing never touches the other three.
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly Config _config;
    private readonly List<FeedController> _feeds = new();

    public IReadOnlyList<FeedController> Feeds => _feeds;

    public Engine(Config config, IReadOnlyList<MpvHost> views)
    {
        _config = config;

        // Build the engine-wide options once and share the list across all 4 handles. Reading the client
        // API version also forces the libmpv dll to load via the resolver here — an early, clear failure
        // point if the pinned dll is missing (the analog of the old engine's one-time global init).
        var coreOptions = BuildCoreOptions(config);
        uint api = MpvClient.ClientApiVersion;
        Logger.Log($"Engine: libmpv client API {api >> 16}.{api & 0xFFFF} initialized.");

        int count = Math.Min(views.Count, _config.Streams.Count);
        for (int i = 0; i < count; i++)
        {
            string name = i < _config.Names.Count ? _config.Names[i] : string.Empty;
            bool namesOnly = i < _config.OverlayNamesOnly.Count && _config.OverlayNamesOnly[i];
            var feed = new FeedController(i, _config.Streams[i], name, namesOnly, coreOptions, views[i], _config);
            _feeds.Add(feed);
        }
    }

    /// <summary>Engine-wide mpv options — the per-handle analog of the old engine's core args (§3.1). Set on
    /// every feed's handle before <c>mpv_initialize</c>. Returned as a shared, immutable (name,value) list.</summary>
    private static IReadOnlyList<(string name, string value)> BuildCoreOptions(Config config)
    {
        var opts = new List<(string, string)>
        {
            // 4 simultaneous camera audio tracks would be cacophony and contend for the audio device. Mute
            // at the engine level (deliberate default — video-only wall; the previous build muted audio too).
            // ao=null detaches the audio output entirely so no device is ever opened.
            ("audio", "no"),
            ("ao", "null"),
            // No OSD title overlay; libmpv shows none by default anyway.
            ("osd-level", "0"),
            // Keep mpv's own log spew off the console path. The API-level half is
            // mpv_request_log_messages("no") in the MpvClient ctor.
            ("msg-level", "all=no"),
            ("terminal", "no"),

            // Embedded video output. wid (the host HWND) is set per-feed by MpvClient before initialize.
            // gpu is the always-available embedded VO; gpu-next is a Phase-6 field-test upgrade if present.
            ("vo", "gpu"),

            // Don't let mpv eat keyboard/mouse from the embedded HWND — hotkeys are handled by
            // GridForm.ProcessCmdKey (the old EnableKeyInput=false / EnableMouseInput=false equivalent).
            ("input-default-bindings", "no"),
            ("input-vo-keyboard", "no"),
            ("input-cursor", "no"),

            // The handle stays alive between loadfiles and never auto-closes on EOF — we drive reconnect
            // ourselves (the §5 state machine). idle=yes keeps the core running with no file loaded.
            ("keep-open", "no"),
            ("idle", "yes"),
            ("force-window", "no"),

            // Deterministic embed: ignore any user config files / scripts on the host machine.
            ("config", "no"),
            ("load-scripts", "no"),
        };

        // Field tuning: ExtraMpvArgs map verbatim onto mpv options. Each entry is "key=value" (a leading
        // "--" is tolerated); a bare token becomes "token=yes". (Renamed ExtraMpvArgs in Phase 7.)
        foreach (var raw in config.ExtraMpvArgs)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string entry = raw.Trim();
            if (entry.StartsWith("--", StringComparison.Ordinal)) entry = entry.Substring(2);
            int eq = entry.IndexOf('=');
            if (eq < 0) opts.Add((entry, "yes"));
            else opts.Add((entry.Substring(0, eq), entry.Substring(eq + 1)));
        }

        return opts;
    }

    /// <summary>Create each feed's mpv handle (embedding it into its host HWND) and start every feed playing.</summary>
    public void Start()
    {
        foreach (var feed in _feeds)
        {
            feed.Attach();
            feed.Start();
        }
        Logger.Log($"Engine: started {_feeds.Count} feed(s).");
    }

    /// <summary>Force every feed to re-apply its stream options and reconnect — the live-apply path for the
    /// per-feed stream properties (cache-secs, rtsp-transport, skiploopfilter) after the dashboard mutates
    /// Config.</summary>
    public void ForceReconnectAll()
    {
        foreach (var feed in _feeds) feed.ForceReconnect();
    }

    /// <summary>Change hardware-decode on every feed live (each sets <c>hwdec</c> and reconnects). Used by
    /// the dashboard's HwDecode knob — avoids a full display restart.</summary>
    public void SetHwDecodeAll(bool on)
    {
        foreach (var feed in _feeds) feed.SetHwDecode(on);
    }

    /// <summary>Live-change a single feed's source URL (dashboard Sources group). Only the targeted cell
    /// re-applies options and reconnects; the other feeds are untouched. Out-of-range indices are ignored.</summary>
    public void SetStreamUrl(int index, string url)
    {
        if (index < 0 || index >= _feeds.Count) return;
        _feeds[index].SetUrl(url);
    }

    public void Dispose()
    {
        foreach (var feed in _feeds)
        {
            try { feed.Dispose(); } catch { /* best effort */ }
        }
        _feeds.Clear();
    }
}
