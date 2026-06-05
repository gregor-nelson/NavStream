using System.Globalization;
using MpvGrid.Mpv;

namespace MpvGrid;

internal enum FeedState { Connecting, Playing, Stalled, Reconnecting }

/// <summary>Operator-facing health derived from state + stats (spec §6).</summary>
internal enum FeedHealth { Connecting, Healthy, Warn, Down }

/// <summary>
/// One feed = one mpv handle (<see cref="MpvClient"/>) embedded into one <see cref="MpvHost"/> surface,
/// with its own independent reconnect state machine (spec §5) and mpv-property sampling (§6). Nothing here
/// touches the other feeds. Step 4 wires playback + per-stream options; steps 5–6 add reconnect/backoff
/// and monitoring.
/// </summary>
internal sealed partial class FeedController : IDisposable
{
    private readonly int _index;          // 0-based (cell TL,TR,BL,BR)
    private string _url;                  // source URL; mutable via SetUrl (dashboard live source-swap)
    private string _name;                 // operator-set overlay label (vessel name); "" => fall back to cell number
    private bool _namesOnly;              // "name only" overlay mode: just the vessel name + a health dot
    private readonly IReadOnlyList<(string name, string value)> _coreOptions;  // engine-wide options (§3.1)
    private readonly MpvHost _view;       // host control; its .Handle is mpv's wid embed target
    private readonly Config _config;

    private MpvClient? _client;

    // Per-feed HW-decode choice: starts from config, may fall back to software on a decode error (§4)
    // without disturbing the other feeds.
    private bool _useHwDecode;

    public int Index => _index;
    public string Url => _url;
    public string Name => _name;
    public bool NamesOnly => _namesOnly;
    public int CellNumber => _index + 1;

    public FeedController(int index, string url, string name, bool namesOnly,
        IReadOnlyList<(string name, string value)> coreOptions, MpvHost view, Config config)
    {
        _index = index;
        _url = url;
        _name = name ?? string.Empty;
        _namesOnly = namesOnly;
        _coreOptions = coreOptions;
        _view = view;
        _config = config;
        _useHwDecode = config.HwDecode;
    }

    /// <summary>Create this feed's mpv handle, embedding its video output into the host control's HWND.
    /// (<see cref="MpvHost.Handle"/> is realized by now — RenderApp defers engine creation to Form.Shown,
    /// by which point the child controls are parented and the window is visible.)</summary>
    public void Attach()
    {
        _client = new MpvClient(_view.Handle, _coreOptions);
        WireEvents();           // step-5 partial: reconnect triggers (off the mpv pump)
        StartStatsTimer();      // step-6 partial: mpv-property sampling + stall detection
    }

    /// <summary>Apply every per-feed stream option to the handle right before a <c>loadfile</c> (§3.2, D7).
    /// With one handle per feed (D3) there is no <c>Media</c> object — these are handle properties mpv
    /// re-reads on each <c>loadfile</c>, preserving the previous build's "every option fresh on each reconnect"
    /// live-apply semantics. RTSP-only and RTP-only knobs are gated by URL scheme so a feed never gets
    /// options for a demuxer it isn't using.</summary>
    private void ApplyStreamOptions(MpvClient client)
    {
        bool isRtsp = _url.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
        bool isRtp = _url.StartsWith("rtp://", StringComparison.OrdinalIgnoreCase);

        // Receiver buffer (every scheme). The UI value is in milliseconds; mpv buffers in SECONDS,
        // so convert. cache-secs is the read-ahead target; demuxer-readahead-secs bounds the demuxer queue.
        // cache-pause=no so a brief live underrun rides through instead of freezing the wall. (Field-tune
        // the latency-vs-resilience balance on the real network — Phase 5.)
        string cacheSecs = (Math.Max(0, _config.NetworkCachingMs) / 1000.0).ToString(CultureInfo.InvariantCulture);
        client.SetProperty("cache", "yes");
        client.SetProperty("cache-secs", cacheSecs);
        client.SetProperty("demuxer-readahead-secs", cacheSecs);
        client.SetProperty("cache-pause", "no");

        // Connect/read timeout (every scheme): covers the initial-connect hang the stall detector can't — the
        // stall detector only runs once a feed reaches Playing. 0 = no timeout. Set explicitly every load so a
        // live source swap can't inherit a stale value from a previous URL on the handle.
        client.SetProperty("network-timeout",
            _config.NetworkTimeoutSec.ToString(CultureInfo.InvariantCulture));

        // Low-latency connect (every scheme): cap how long / how much ffmpeg probes before it starts playing.
        // These are mpv properties that persist on the handle, so set a DEFINITE value every load (never the
        // "emit only when > 0" trick) — a live source swap must not inherit a previous URL's probe limits.
        // analyzeduration: UI ms → seconds; 0 → "0" = mpv default. probesize: 0 → "5000000" (mpv 0.41 default;
        // probesize=0 is rejected by libmpv, verified rc -6).
        string analyze = _config.DemuxAnalyzeDurationMs > 0
            ? (_config.DemuxAnalyzeDurationMs / 1000.0).ToString(CultureInfo.InvariantCulture)
            : "0";
        client.SetProperty("demuxer-lavf-analyzeduration", analyze);
        client.SetProperty("demuxer-lavf-probesize",
            _config.DemuxProbesizeBytes > 0
                ? _config.DemuxProbesizeBytes.ToString(CultureInfo.InvariantCulture)
                : "5000000");

        // ---- RTSP transport (rtsp:// only) ----
        // Single selector (lavf/udp/tcp/http/udp_multicast); Normalize guarantees a valid value, so set it
        // unconditionally for rtsp feeds. Set explicitly every time so a live source swap can't leave a stale
        // transport from a previous URL on the handle. "lavf" = mpv's default (ffmpeg negotiates UDP with its
        // own TCP fallback) — the previous build's unattended-connect behaviour.
        if (isRtsp)
            client.SetProperty("rtsp-transport", _config.RtspTransport);

        // ---- ffmpeg demuxer AVOptions (demuxer-lavf-o) — composed once from the per-scheme knobs ----
        // RtspFrameBufferSizeBytes → ffmpeg buffer_size; RtpTimeoutSec → ffmpeg timeout (MICROSECONDS);
        // ReorderQueueSize → ffmpeg RTP jitter-buffer depth (rtsp+rtp). Always set the property (empty string
        // clears any stale value from a previous source on a live swap).
        var lavf = new List<string>();
        if (isRtsp && _config.RtspFrameBufferSizeBytes > 0)
            lavf.Add($"buffer_size={_config.RtspFrameBufferSizeBytes}");
        if (isRtp && _config.RtpTimeoutSec >= 0)
            lavf.Add($"timeout={(long)_config.RtpTimeoutSec * 1_000_000}");
        if ((isRtsp || isRtp) && _config.ReorderQueueSize > 0)
            lavf.Add($"reorder_queue_size={_config.ReorderQueueSize}");
        client.SetProperty("demuxer-lavf-o", string.Join(",", lavf));

        // HW decode: d3d11va-copy is the robust 4×-decode bet during bring-up (D9). OFF => force software so
        // the GPU 4-decode question stays parked until confirmed on real hardware.
        client.SetProperty("hwdec", _useHwDecode ? "d3d11va-copy" : "no");

        // Skip the in-loop deblocking filter to shed decode load when asked; "default" restores it.
        client.SetProperty("vd-lavc-skiploopfilter", _config.SkipLoopFilter ? "all" : "default");
    }

    /// <summary>(Re)start playback: apply fresh stream options, then loadfile. Safe to call repeatedly.</summary>
    public void Start()
    {
        var client = _client;
        if (client is null) return;
        SetState(FeedState.Connecting);
        // Reset the bitrate/stall gate on every (re)load so a transient 0 during reconnect can't trip Warn
        // and the first post-load sample is never counted as a stall (§3.4). RestartPlayback (reconnect) does
        // the same. The previous build detected the reset inside Sample() via the demux counter restarting;
        // mpv has no such counter, so signal it explicitly here (consumed on the next stats tick).
        MarkReloaded();
        ApplyStreamOptions(client);
        client.Load(_url);
        Logger.Log($"Feed {CellNumber}: play {_url} (hw={_useHwDecode}).");
    }

    /// <summary>Live-change hardware decode for this feed (dashboard Phase C). Updates the per-feed flag
    /// (<see cref="ApplyStreamOptions"/> reads it on every reconnect), sets the <c>hwdec</c> property live,
    /// then force-reconnects so the change takes effect cleanly. A later decode error still trips the
    /// software-fallback path in the END_FILE(error) handler.</summary>
    public void SetHwDecode(bool on)
    {
        _useHwDecode = on;
        try { _client?.SetProperty("hwdec", on ? "d3d11va-copy" : "no"); } catch { /* mid-restart */ }
        Logger.Log($"Feed {CellNumber}: HW decode set to {on} — reconnecting.");
        ScheduleReconnect("hwdecode", immediate: true);
    }

    /// <summary>Live-change this feed's source URL (dashboard Sources group). Updates the field that
    /// <see cref="ApplyStreamOptions"/>/<see cref="Start"/> read on every reconnect, then force-reconnects so
    /// the new source takes effect immediately for this feed alone. No-op when the URL is unchanged so editing
    /// one cell never blinks a feed that didn't change. <paramref name="url"/> is trimmed by the caller.</summary>
    public void SetUrl(string url)
    {
        if (string.Equals(_url, url, StringComparison.Ordinal)) return;
        _url = url;
        Logger.Log($"Feed {CellNumber}: source set to {_url} — reconnecting.");
        ScheduleReconnect("source-change", immediate: true);
    }

    /// <summary>Live-change this feed's overlay vessel name (dashboard Overlay group). Display-only: the
    /// overlay reads <see cref="Name"/> fresh on every repaint, so no media reconnect is needed.</summary>
    public void SetName(string name) => _name = (name ?? string.Empty).Trim();

    /// <summary>Live-toggle this feed's "name only" overlay mode (dashboard Overlay group). Display-only:
    /// the overlay reads <see cref="NamesOnly"/> fresh on every repaint, so no media reconnect is needed.</summary>
    public void SetNamesOnly(bool on) => _namesOnly = on;

    public void Dispose()
    {
        CancelReconnects(); // step-5 partial
        StopStatsTimer();   // step-6 partial
        var c = _client;
        _client = null;
        if (c is null) return;
        // MpvClient.Dispose is race-free and bounded (quit → wakeup → join the pump → terminate_destroy);
        // Stop() first mirrors the old _view.MediaPlayer=null; p.Stop() ordering. _disposed is already set
        // by CancelReconnects so any END_FILE the teardown emits is swallowed by ScheduleReconnect's guard.
        try { c.Stop(); } catch { /* best effort */ }
        try { c.Dispose(); } catch { /* best effort */ }
    }
}
