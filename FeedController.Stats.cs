using System.Globalization;

namespace MpvGrid;

/// <summary>
/// Step 6 — mpv property sampling, stall detection feed, and health diagnosis (spec §6, §7).
/// A 1s timer polls the feed's <see cref="MpvClient"/> properties (the §5 stall detector cadence). The
/// overlay repaints on the <see cref="Changed"/> event, throttled to RefreshSeconds unless state/health flips.
/// </summary>
internal sealed partial class FeedController
{
    private System.Threading.Timer? _statsTimer;

    // Latest sampled values. Written on the stats-timer thread; read unlocked on the render UI thread
    // (overlay) and the control-server pusher thread (BuildSnapshot). Deliberate, documented reliance on
    // 64-bit aligned reads/writes being atomic on x64 (our only target) — values are re-published every
    // second, so any torn read self-corrects on the next tick. Add Interlocked/volatile before any 32-bit port.
    public FeedHealth Health { get; private set; } = FeedHealth.Connecting;
    public double InputBitrateKbps { get; private set; }
    public long LostPictures { get; private set; }
    public long DemuxCorrupted { get; private set; }      // retired (no mpv analog) — held at 0, still published
    public long DemuxDiscontinuity { get; private set; }  // retired (no mpv analog) — held at 0, still published
    public long DecodedVideo { get; private set; }        // retired (no mpv analog) — held at 0, still published

    // Stall + progress tracking.
    private bool _haveBitrate;                 // false until the first readable raw-input-rate post-load (gates Warn)
    private double _lastCacheTime = double.MinValue;       // previous demuxer-cache-time (sec); sentinel = no reading
    private DateTime _lastProgressUtc = DateTime.MinValue;
    private long _prevLostPictures = -1;
    private DateTime _lastOverlayRepaintUtc = DateTime.MinValue;

    // Reload gate: Start()/RestartPlayback() set this on every loadfile so the next Sample() resets the
    // bitrate/stall state (the previous build detected a reload via the demux counter restarting; mpv has no
    // such reset, so the (re)load path signals it explicitly). Only this bool crosses threads (naturally
    // atomic); the gate fields it triggers are all written on the stats thread.
    private volatile bool _reloaded;
    private void MarkReloaded() => _reloaded = true;

    private const int SampleIntervalMs = 1000;

    private void StartStatsTimer()
    {
        // One-shot, re-armed at the end of each tick (see OnStatsTick). A periodic timer would re-enter if a
        // native call ever blocked past the interval (e.g. mid-reconnect/network hang), and two Samples would
        // race the unsynchronized delta fields — so we guarantee a single Sample in flight at any time.
        _statsTimer = new System.Threading.Timer(OnStatsTick, null, SampleIntervalMs, Timeout.Infinite);
    }

    private void OnStatsTick(object? _)
    {
        try { Sample(); }
        catch { /* never let monitoring crash a feed */ }
        finally
        {
            try { _statsTimer?.Change(SampleIntervalMs, Timeout.Infinite); }
            catch (ObjectDisposedException) { /* stopped during this tick */ }
        }
    }

    private void StopStatsTimer()
    {
        var t = _statsTimer;
        _statsTimer = null;
        if (t is null) return;
        try
        {
            // Synchronous dispose: block until any in-flight Sample() returns before Dispose() goes on to
            // Stop()/Dispose() the mpv handle. Stops a threadpool Sample from polling a freed handle (an
            // uncatchable AccessViolationException → render crash). Bounded so shutdown can't hang on a
            // stuck native call.
            using var done = new ManualResetEvent(false);
            if (t.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch { /* best effort */ }
    }

    private void Sample()
    {
        if (_disposed) return;                       // bail before touching the handle (teardown in progress)
        var client = _client;
        if (client is null) return;
        FeedState state = State;

        // Consume the reload gate (set by Start()/RestartPlayback() on every loadfile). Reset the bitrate
        // and stall state so a transient 0 during reconnect can't trip Warn and the first post-load sample
        // is never counted as a stall. Done here so every gate-field write stays on the stats thread.
        if (_reloaded)
        {
            _reloaded = false;
            _haveBitrate = false;
            InputBitrateKbps = 0;
            _lastProgressUtc = DateTime.MinValue;
            _lastCacheTime = double.MinValue;
        }

        bool flatForTimeout = false;

        // ---- bitrate: network ingress rate, the 1:1 analog of the old DemuxReadBytes-delta hack ----
        // demuxer-cache-state/raw-input-rate is bytes/s read straight off the socket — purpose-built, with
        // no int32-wrap or counter-reset math. ×8/1000 => kbit/s. For the rtsp:// (live555) and rtp:// feeds
        // this app plays there is no separate access module, but mpv's demuxer-cache-state still reports the
        // ingress rate. Fall back to video-bitrate (bits/s) when the sub-key is unreadable on this build.
        // (D4 #1 risk: verify the exact spelling on the pinned 0.41.0/API-2.5 dll against a real RTSP feed.)
        bool haveRawRate = client.TryGetLong("demuxer-cache-state/raw-input-rate", out long rawInputRate);
        if (haveRawRate && rawInputRate >= 0)
        {
            InputBitrateKbps = rawInputRate * 8.0 / 1000.0;
            _haveBitrate = true;                     // a real reading exists post-load — let Warn arm
        }
        else if (client.TryGetDouble("video-bitrate", out double videoBitrate) && videoBitrate > 0)
        {
            InputBitrateKbps = videoBitrate / 1000.0;
            _haveBitrate = true;
        }

        // ---- lost pictures: output + decoder frame drops drive the "decode overload => Warn" path ----
        long dropped = 0;
        if (client.TryGetLong("frame-drop-count", out long frameDrops) && frameDrops > 0) dropped += frameDrops;
        if (client.TryGetLong("decoder-frame-drop-count", out long decDrops) && decDrops > 0) dropped += decDrops;
        LostPictures = dropped;

        // DecodedVideo / DemuxCorrupted / DemuxDiscontinuity have no clean mpv analog — retired but kept
        // published (display-only; the dashboard tolerates 0). They stay at their default 0.

        // Stall detection (§5): demuxer-cache-time (sec) is the progress clock — the analog of "demux bytes
        // advanced". We deliberately do NOT fall back to playback time — a live feed's output clock keeps
        // advancing while the network playout buffer drains (NetworkCachingMs is 10s by default, longer than
        // the 8s stall window) and can free-run on a genuinely dead stream, masking the outage. A reconnect
        // resets the cache, which shows up as a JUMP (any change counts as progress), so a feed that just
        // came back is never torn down on its first sample. If cache-time is unreadable on this build, fall
        // back to positive ingress as the progress signal.
        if (state == FeedState.Playing)
        {
            var nowUtc = DateTime.UtcNow;
            if (_lastProgressUtc == DateTime.MinValue) _lastProgressUtc = nowUtc;

            bool progressed;
            if (client.TryGetDouble("demuxer-cache-time", out double cacheTime))
            {
                progressed = _lastCacheTime == double.MinValue || cacheTime != _lastCacheTime;
                _lastCacheTime = cacheTime;
            }
            else
            {
                // No cache-time on this build: a positive ingress rate is progress. If even raw-input-rate
                // is unreadable we have no signal, so assume progress — missing telemetry must never
                // fabricate a stall (the whole point of the #1 field-test risk).
                progressed = !haveRawRate || rawInputRate > 0;
            }

            if (progressed) _lastProgressUtc = nowUtc;
            flatForTimeout = (nowUtc - _lastProgressUtc).TotalMilliseconds >= _config.StallTimeoutMs;
        }
        else
        {
            _lastProgressUtc = DateTime.MinValue;
            _lastCacheTime = double.MinValue;
        }

        // Diagnose health (§6): distinguish network dropout from local decode overload.
        bool lostRising = _prevLostPictures >= 0 && LostPictures > _prevLostPictures;
        _prevLostPictures = LostPictures;
        FeedHealth health = DiagnoseHealth(state, InputBitrateKbps, _haveBitrate, lostRising);

        bool flipped = health != Health || _lastHealthState != state;
        Health = health;
        _lastHealthState = state;

        // Stall detection drives reconnect (§5). Done after health update so the overlay shows Stalled.
        if (flatForTimeout) CheckStall(true);

        ResetBackoffIfHealthy();

        // Repaint overlay on flip, or at most every RefreshSeconds.
        var now = DateTime.UtcNow;
        if (flipped || (now - _lastOverlayRepaintUtc).TotalSeconds >= _config.RefreshSeconds)
        {
            _lastOverlayRepaintUtc = now;
            Changed?.Invoke(this);
        }
    }

    private FeedState _lastHealthState = FeedState.Connecting;

    private static FeedHealth DiagnoseHealth(FeedState state, double bitrateKbps, bool haveBitrate, bool lostRising)
    {
        switch (state)
        {
            case FeedState.Reconnecting:
                return FeedHealth.Down;        // red
            case FeedState.Connecting:
                return FeedHealth.Connecting;  // neutral/gray
            case FeedState.Stalled:
                return FeedHealth.Warn;        // yellow
            case FeedState.Playing:
            default:
                // Until the first real bitrate delta lands (feed just reached Playing / just reconnected),
                // don't let a transient 0 kbps read flip the badge yellow — it IS playing, so hold green
                // until we actually know the rate. A genuine dropout keeps _haveBitrate true and trips below.
                if (haveBitrate && bitrateKbps < 1) return FeedHealth.Warn;  // playing but nothing arriving
                if (lostRising) return FeedHealth.Warn;        // bitrate holds but lost-pics climbing = decode overload
                return FeedHealth.Healthy;                     // green
        }
    }

    /// <summary>Short status line for the overlay corner badge.</summary>
    public string StatusText()
    {
        string s = State switch
        {
            FeedState.Playing => "LIVE",
            FeedState.Connecting => "CONNECTING",
            FeedState.Stalled => "STALLED",
            FeedState.Reconnecting => "RECONNECTING",
            _ => State.ToString().ToUpperInvariant(),
        };
        if (State == FeedState.Playing)
            s += " " + InputBitrateKbps.ToString("0", CultureInfo.InvariantCulture) + " kbps";
        if (LostPictures > 0) s += $"  lost:{LostPictures}";
        return s;
    }
}
