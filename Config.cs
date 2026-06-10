using System.Text.Json;
using System.Text.Json.Serialization;

namespace NavStream;

/// <summary>
/// Runtime configuration (spec §7). Loaded from <c>streams.json</c> next to the exe.
/// Every property has a code default, so a missing or partial file still runs.
/// Unknown / legacy keys (e.g. an earlier build's HttpBasePort) are ignored on load.
/// </summary>
internal sealed class Config
{
    // ---- streams & window (D6) ----
    public List<string> Streams { get; set; } = new()
    {
        "rtsp://192.169.60.12/h264",
        "rtsp://192.169.65.12/h264",
        "rtsp://192.169.67.12/h264",
        "rtp://@:8002",
    };

    /// <summary>
    /// Optional per-stream display labels (vessel names) drawn on the overlay, aligned by position with
    /// <see cref="Streams"/> (Names[i] labels Streams[i]). A blank or missing entry falls back to just the
    /// cell number for that feed. This is the operator-controllable overlay text (e.g. "Skandi Skansen").
    /// </summary>
    public List<string> Names { get; set; } = new();

    /// <summary>
    /// Optional per-feed "name only" overlay mode, aligned by position with <see cref="Streams"/>
    /// (OverlayNamesOnly[i] applies to Streams[i]). When true that feed's badge collapses to just the
    /// vessel name + a colored health dot — hiding the cell number, bitrate, lost-frame counts and status
    /// word. A missing entry falls back to <c>false</c> (the default detailed badge).
    /// </summary>
    public List<bool> OverlayNamesOnly { get; set; } = new();

    /// <summary>Defined-but-inactive streams parked by the roster editor (dynamic-grid Tier 1). The engine
    /// never reads this pool; it is not positionally aligned with the active Streams/Names/OverlayNamesOnly
    /// lists. Round-trips in streams.json. A missing key loads as an empty list (back-compat).</summary>
    public List<InactiveStream> InactivePool { get; set; } = new();
    public int Monitor { get; set; } = 0;          // Screen.AllScreens index; mirrors Displays[0].Monitor (legacy key)
    public bool Borderless { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = false;

    /// <summary>Multi-monitor partition: how the flat <see cref="Streams"/> list is split across displays,
    /// in order (display 0 takes the first Cells feeds, display 1 the next, …). Cell numbering is continuous
    /// across displays. Loaded as null from a legacy file — <see cref="Normalize"/> synthesizes one display
    /// from the legacy <see cref="Monitor"/> key and always leaves this non-empty.</summary>
    public List<DisplayConfig>? Displays { get; set; }

    // ---- engine / per-stream options (D7, §4) ----
    /// <summary>Receiver buffer (mpv <c>cache-secs</c>, given here in ms) — how much stream the engine prefetches
    /// before playing. Default 10000 (10 s): a deep buffer that rides out packet loss/jitter on a poor link (the
    /// equivalent of mpv's <c>--cache-secs=10</c>), so a stream plays on launch without hand-tuning. It
    /// costs ~10 s of latency behind real time — lower it for a low-latency wall on a clean network.</summary>
    public int NetworkCachingMs { get; set; } = 10000;
    /// <summary>Connect/read timeout (mpv <c>network-timeout</c>, seconds) before the engine abandons a connection
    /// that never starts delivering data — covers the initial-connect hang (e.g. an unplugged camera) that the
    /// playing-state stall detector can't see. 0 = no timeout. Default 60 (mpv's own default). Every feed.</summary>
    public int NetworkTimeoutSec { get; set; } = 60;
    public bool HwDecode { get; set; } = false;    // d3d11va — enable only after 4-decode GPU test
    public bool SkipLoopFilter { get; set; } = false;
    public List<string> ExtraMpvArgs { get; set; } = new();

    /// <summary>Low-latency connect — analyzeduration (mpv <c>demuxer-lavf-analyzeduration</c>, ms): how long
    /// ffmpeg inspects a stream before it starts playing. 0 = engine default. Lower trims connect latency; too
    /// low can misdetect an unusual stream. Every feed. Clamp 0..60000. (Dashboard "Faster startup" toggle.)</summary>
    public int DemuxAnalyzeDurationMs { get; set; } = 0;
    /// <summary>Low-latency connect — probesize (mpv <c>demuxer-lavf-probesize</c>, bytes): how much ffmpeg reads
    /// before it starts playing. 0 = engine default (emitted as 5000000, mpv 0.41's default — probesize=0 is
    /// rejected by libmpv). Every feed. Clamp 0..100000000. (Dashboard "Faster startup" toggle.)</summary>
    public int DemuxProbesizeBytes { get; set; } = 0;

    // ---- RTP / RTSP transport (per-stream options, live-applied on reconnect; §4) ----
    // Defaults reproduce the prior behaviour: lavf transport (ffmpeg negotiates UDP with a TCP fallback) and the
    // demuxer's own buffer/timeout/reorder defaults (the values ApplyStreamOptions leaves unset).
    /// <summary>RTSP transport mode (mpv <c>rtsp-transport</c>): one of <c>lavf</c> (default — ffmpeg negotiates
    /// UDP with an automatic TCP fallback, the unattended-connect default), <c>udp</c>, <c>tcp</c>, <c>http</c>
    /// (tunnel through an HTTP-only firewall), or <c>udp_multicast</c>. RTSP feeds only. Replaces the former
    /// two RTSP-transport booleans. <see cref="Normalize"/> guarantees a valid value (blank or
    /// unrecognised → <c>lavf</c>).</summary>
    public string RtspTransport { get; set; } = "lavf";
    /// <summary>RTSP receive frame buffer (ffmpeg <c>buffer_size</c> via <c>demuxer-lavf-o</c>, bytes). 0 = engine
    /// default (100000). Raise for high-bitrate cameras that overflow the default. RTSP feeds only.</summary>
    public int RtspFrameBufferSizeBytes { get; set; } = 0;
    /// <summary>RTP/RTCP receive timeout (ffmpeg <c>timeout</c> via <c>demuxer-lavf-o</c>, seconds) before the
    /// engine declares the source dead. -1 = never time out. RTP feeds only.</summary>
    public int RtpTimeoutSec { get; set; } = 5;
    /// <summary>RTP jitter buffer depth (ffmpeg <c>reorder_queue_size</c> via <c>demuxer-lavf-o</c>, packets).
    /// 0 = engine default (not emitted). Holds &amp; re-sorts out-of-order RTP packets before decode — raise to
    /// smooth a network that delivers packets out of order, at a little extra latency. RTSP + RTP feeds only.
    /// (Dashboard "Smooth choppy video" toggle.)</summary>
    public int ReorderQueueSize { get; set; } = 0;

    // ---- reconnect / backoff (D4, §5) ----
    public int BackoffStartMs { get; set; } = 2000;
    public int BackoffMaxMs { get; set; } = 30000;
    public double BackoffFactor { get; set; } = 2;
    public int BackoffResetMs { get; set; } = 15000;
    public int StallTimeoutMs { get; set; } = 8000;

    // ---- monitoring (D8, §6) ----
    public bool OverlayEnabled { get; set; } = true;
    public bool LogoEnabled { get; set; } = true;   // InterMoor brand watermark; dashboard-toggleable
    // Brand-watermark appearance (dashboard "Logo settings"; live + persisted). Defaults reproduce the prior
    // hardcoded look: 40% alpha, brightness 50% (→ 0.25 RGB lift), 15% of cell width, bottom-left corner.
    public int LogoOpacityPct { get; set; } = 40;     // 0..80  → alpha = pct/100
    public int LogoBrightnessPct { get; set; } = 50;  // 0..100 → RGB lift = pct/100 * 0.5
    public int LogoSizePct { get; set; } = 15;        // 5..30  → width fraction = pct/100
    public int LogoPosition { get; set; } = 0;        // 0=bottom-left, 1=bottom-right, 2=top-right, 3=top-left
    // Health-overlay badge appearance (dashboard "Overlay settings"; live + persisted). Defaults reproduce the
    // prior hardcoded look: pill fill α196 (= 77% opacity), 100% size, top-left corner.
    public int BadgeOpacityPct { get; set; } = 77;    // 30..100 → surface alpha = (pct/100) scales PillFill α196 in proportion
    public int BadgeSizePct { get; set; } = 100;      // 60..160 → master scale ×= pct/100
    public int BadgePosition { get; set; } = 0;       // 0=top-left, 1=top-right, 2=bottom-right, 3=bottom-left
    public int RefreshSeconds { get; set; } = 2;
    public string LogPath { get; set; } = "navstream.log";

    // ---- supervisor (D1, §1) ----
    public int SupervisorMinHealthyMs { get; set; } = 10000;
    public int SupervisorBackoffMaxMs { get; set; } = 15000;

    // ---- control dashboard (browser bridge, D-DASH-1) ----
    /// <summary>Fixed TCP port the dashboard's HTTP/SSE bridge binds for LAN access. The control panel is
    /// reached at <c>http://&lt;this-pc-ip&gt;:PORT</c> from any device on the local network, so this is a
    /// pinned, type-able port (no scan) — pick one that's free on the unit.</summary>
    public int DashboardHttpPort { get; set; } = 8080;

    [JsonIgnore]
    public string SourcePath { get; private set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // Unknown / legacy keys are ignored by default (no UnmappedMemberHandling set).
    };

    /// <summary>Absolute path to streams.json beside the executable.</summary>
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "streams.json");

    /// <summary>
    /// Loads config from streams.json beside the exe. Never throws on a bad/partial/missing
    /// file — falls back to code defaults (with the real stream list) so the grid always runs.
    /// </summary>
    public static Config Load()
    {
        string path = DefaultPath;
        Config config = new();
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                config = JsonSerializer.Deserialize<Config>(json, JsonOpts) ?? new Config();
            }
        }
        catch
        {
            // Malformed JSON, locked file, etc. -> defaults. Logged by the caller.
            config = new Config();
        }

        config.SourcePath = File.Exists(path) ? path : "(defaults)";
        config.Normalize();
        return config;
    }

    /// <summary>
    /// Persist the current config to <see cref="SourcePath"/> (or <see cref="DefaultPath"/> when the app
    /// is running on code defaults). Writes indented JSON; <see cref="SourcePath"/> is <c>[JsonIgnore]</c>
    /// so it never round-trips. After a successful save the live <see cref="SourcePath"/> points at the
    /// file just written, so the dashboard's snapshot reflects the real path. May throw on I/O errors —
    /// the caller (RenderApp) catches and logs.
    /// </summary>
    public void Save()
    {
        string path = (string.IsNullOrEmpty(SourcePath) || SourcePath == "(defaults)")
            ? DefaultPath
            : SourcePath;

        Normalize(); // never persist an out-of-range hand-edit that slipped in via the dashboard
        string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        SourcePath = path;
    }

    /// <summary>Clamp/sanitize values so a hand-edited file (or a dashboard apply) can't put the app in a
    /// broken state. Idempotent; safe to call after a live settings change as well as on load.</summary>
    public void Normalize()
    {
        Streams = (Streams ?? new List<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Take(16)
            .ToList();

        // Names align with Streams by position, so keep blanks (don't filter) — only trim and cap at 16.
        Names = (Names ?? new List<string>())
            .Select(s => (s ?? string.Empty).Trim())
            .Take(16)
            .ToList();

        // OverlayNamesOnly aligns with Streams by position too — keep entries, just cap at 16.
        OverlayNamesOnly = (OverlayNamesOnly ?? new List<bool>())
            .Take(16)
            .ToList();

        // InactivePool has no positional alignment requirement — drop blank-URL entries, trim, soft-cap.
        InactivePool = (InactivePool ?? new List<InactiveStream>())
            .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Url))
            .Select(p => new InactiveStream { Url = p.Url.Trim(), Name = (p.Name ?? "").Trim(), NamesOnly = p.NamesOnly })
            .Take(64)   // soft cap to bound streams.json; arbitrary, generous
            .ToList();

        ExtraMpvArgs ??= new List<string>();

        if (Monitor < 0) Monitor = 0;
        NormalizeDisplays();
        if (NetworkCachingMs < 0) NetworkCachingMs = 0;
        if (NetworkTimeoutSec < 0) NetworkTimeoutSec = 0;
        if (NetworkTimeoutSec > 3600) NetworkTimeoutSec = 3600;
        if (DemuxAnalyzeDurationMs < 0) DemuxAnalyzeDurationMs = 0;
        if (DemuxAnalyzeDurationMs > 60000) DemuxAnalyzeDurationMs = 60000;
        if (DemuxProbesizeBytes < 0) DemuxProbesizeBytes = 0;
        if (DemuxProbesizeBytes > 100_000_000) DemuxProbesizeBytes = 100_000_000;

        // RTP / RTSP transport: keep hand-edits / dashboard values in the engine's accepted ranges.
        RtspTransport = (RtspTransport ?? "").Trim().ToLowerInvariant();
        if (RtspTransport is not ("lavf" or "udp" or "tcp" or "http" or "udp_multicast"))
            RtspTransport = "lavf";
        if (RtspFrameBufferSizeBytes < 0) RtspFrameBufferSizeBytes = 0;
        if (RtspFrameBufferSizeBytes > 10_000_000) RtspFrameBufferSizeBytes = 10_000_000;
        if (RtpTimeoutSec < -1) RtpTimeoutSec = -1;          // -1 = never time out
        if (RtpTimeoutSec > 3600) RtpTimeoutSec = 3600;
        if (ReorderQueueSize < 0) ReorderQueueSize = 0;
        if (ReorderQueueSize > 10000) ReorderQueueSize = 10000;

        if (BackoffStartMs < 100) BackoffStartMs = 100;
        if (BackoffMaxMs < BackoffStartMs) BackoffMaxMs = BackoffStartMs;
        if (BackoffFactor < 1.0) BackoffFactor = 1.0;
        if (BackoffResetMs < 1000) BackoffResetMs = 1000;
        if (StallTimeoutMs < 1000) StallTimeoutMs = 1000;

        // Brand-watermark appearance — keep dashboard/hand-edits in the ranges the render + sliders expect.
        LogoOpacityPct = Math.Clamp(LogoOpacityPct, 0, 80);
        LogoBrightnessPct = Math.Clamp(LogoBrightnessPct, 0, 100);
        LogoSizePct = Math.Clamp(LogoSizePct, 5, 30);
        if (LogoPosition < 0 || LogoPosition > 3) LogoPosition = 0;

        // Health-overlay badge appearance — keep dashboard/hand-edits in the ranges the render + sliders expect.
        BadgeOpacityPct = Math.Clamp(BadgeOpacityPct, 30, 100);
        BadgeSizePct = Math.Clamp(BadgeSizePct, 60, 160);
        if (BadgePosition < 0 || BadgePosition > 3) BadgePosition = 0;

        if (RefreshSeconds < 1) RefreshSeconds = 1;
        if (string.IsNullOrWhiteSpace(LogPath)) LogPath = "navstream.log";

        if (SupervisorMinHealthyMs < 0) SupervisorMinHealthyMs = 0;
        if (SupervisorBackoffMaxMs < 1000) SupervisorBackoffMaxMs = 1000;

        // Keep the dashboard port in the usable, non-privileged range.
        if (DashboardHttpPort < 1024 || DashboardHttpPort > 65000) DashboardHttpPort = 8080;
    }

    /// <summary>Normalize the multi-monitor partition (runs inside <see cref="Normalize"/>, after Streams is
    /// final). Guarantees: Displays is non-empty (legacy file → one synthesized display), every entry is
    /// canonical (Monitor ≥ 0, Layout a valid LayoutSpec string, Cells within capacity), and Σ Cells never
    /// exceeds the active stream count (shrink from the tail). Σ Cells below the count grows the LAST display
    /// up to its capacity; any residual streams stay unassigned (unrendered — logged by RenderApp at startup)
    /// rather than deleting stream entries to fit a layout. Mirrors the legacy Monitor key to Displays[0].</summary>
    private void NormalizeDisplays()
    {
        var displays = (Displays ?? new List<DisplayConfig>())
            .Where(d => d is not null)
            .Take(8)   // display cap
            .ToList();

        foreach (var d in displays)
        {
            // Out-of-range vs real screens is a runtime concern (TargetScreenBounds falls back to screen 0).
            d.Monitor = Math.Max(0, d.Monitor);
            var spec = LayoutSpec.Parse(d.Layout);
            d.Layout = spec.ToString();   // canonicalize; invalid → "auto"
            d.Cells = Math.Clamp(d.Cells, 1, spec.Capacity);
        }

        int n = Math.Clamp(Streams.Count, 1, 16);

        if (displays.Count == 0)
            displays.Add(new DisplayConfig { Monitor = Monitor, Layout = "auto", Cells = n });

        // Σ Cells > N → shrink from the tail, dropping emptied trailing displays (≥1 display survives: n ≥ 1).
        int sum = displays.Sum(d => d.Cells);
        while (sum > n)
        {
            var last = displays[^1];
            int trim = Math.Min(last.Cells, sum - n);
            last.Cells -= trim;
            sum -= trim;
            if (last.Cells == 0 && displays.Count > 1) displays.RemoveAt(displays.Count - 1);
            else if (last.Cells == 0) { last.Cells = 1; break; }
        }

        // Σ Cells < N → grow the LAST display up to its capacity; residual streams stay unassigned.
        if (sum < n)
        {
            var last = displays[^1];
            int cap = LayoutSpec.Parse(last.Layout).Capacity;
            last.Cells = Math.Min(cap, last.Cells + (n - sum));
        }

        Displays = displays;
        Monitor = displays[0].Monitor;   // keep the legacy key mirrored for old configs/clients
    }

    /// <summary>The display partition as (display, flat-offset, count) slices over <see cref="Streams"/>,
    /// running-sum offsets — so RenderApp and the snapshot slice the flat feed list identically.
    /// Call after <see cref="Normalize"/> (Displays is then non-empty and within bounds).</summary>
    public IReadOnlyList<(DisplayConfig Display, int Offset, int Count)> GetDisplayPartition()
    {
        var parts = new List<(DisplayConfig, int, int)>();
        int offset = 0;
        foreach (var d in Displays ?? new List<DisplayConfig>())
        {
            parts.Add((d, offset, d.Cells));
            offset += d.Cells;
        }
        return parts;
    }

    /// <summary>Resolve LogPath against the exe directory when it is relative.</summary>
    public string ResolveLogPath()
    {
        return Path.IsPathRooted(LogPath)
            ? LogPath
            : Path.Combine(AppContext.BaseDirectory, LogPath);
    }
}

/// <summary>One display of the multi-monitor wall: which monitor it renders on, its layout preset
/// ("auto" or "CxR" — see <see cref="LayoutSpec"/>), and how many of the flat active streams it takes
/// (in list order, continuous cell numbering). Dual config/wire DTO like <see cref="InactiveStream"/> —
/// round-trips in streams.json and over both IPC hops.</summary>
internal sealed class DisplayConfig
{
    public int Monitor { get; set; }
    public string Layout { get; set; } = "auto";
    public int Cells { get; set; } = 4;
}

/// <summary>A defined-but-inactive stream parked in the roster pool. Not positionally aligned with anything;
/// the engine never reads it. Round-trips in streams.json and over both IPC hops.</summary>
internal sealed class InactiveStream
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public bool NamesOnly { get; set; }
}
