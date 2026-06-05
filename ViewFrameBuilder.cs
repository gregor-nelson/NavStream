using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MpvGrid;

/// <summary>
/// Server-side view model for the browser dashboard. Owns all the merge/staleness/banner logic (snapshot
/// merge, per-tick staleness, connection banner) so the browser stays a dumb renderer: it just paints
/// whatever <see cref="ViewFrame"/> it's handed.
///
/// It subscribes to <see cref="ControlClient"/> (events fire on the client's background thread — there is
/// no UI thread to marshal onto, so a lock guards the merged state) and folds in the supervisor's
/// "grid stopped" flag via <see cref="Signaller"/>. A 1 Hz <see cref="System.Threading.Timer"/> publishes
/// a fresh frame (which doubles as the SSE keep-alive + dead-client probe); state changes publish
/// immediately on top of that. Every publish goes through <see cref="_publishGate"/>, so frames are
/// serialized one-at-a-time with a monotonic <see cref="ViewFrame.Seq"/> — the single-writer model the
/// <see cref="SseHub"/> relies on.
/// </summary>
internal sealed class ViewFrameBuilder : IDisposable
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(3);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly ControlClient _client;
    private readonly Signaller _signaller;
    private readonly SseHub _sse;

    private readonly object _gate = new();          // guards the merged snapshot state below
    private ControlSnapshot? _last;
    private bool _connected;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;
    private int _total, _healthy, _warn, _down;

    private readonly object _publishGate = new();   // serializes frame build + broadcast (single writer)
    private long _seq;

    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public ViewFrameBuilder(ControlClient client, Signaller signaller, SseHub sse)
    {
        _client = client;
        _signaller = signaller;
        _sse = sse;

        _client.SnapshotReceived += OnSnapshot;
        _client.ConnectionChanged += OnConnection;

        // 1 Hz heartbeat: refreshes staleness, re-reads the grid-stopped flag, and keeps SSE clients alive.
        _timer = new System.Threading.Timer(_ => SafePublish(), null, 1000, 1000);
    }

    private void OnSnapshot(ControlSnapshot snap)
    {
        lock (_gate)
        {
            _last = snap;
            _lastSnapshotUtc = DateTime.UtcNow;
            Tally(snap);
        }
        SafePublish();
    }

    private void OnConnection(bool ok)
    {
        lock (_gate)
        {
            _connected = ok;
            if (!ok)
            {
                _last = null;
                _lastSnapshotUtc = DateTime.MinValue;
                _total = _healthy = _warn = _down = 0;
            }
        }
        SafePublish();
    }

    /// <summary>Bucket per-feed health into the header tally (mirrors the form's TallyStreamHealth).</summary>
    private void Tally(ControlSnapshot snap)
    {
        _total = snap.Feeds.Count;
        _healthy = _warn = _down = 0;
        foreach (var f in snap.Feeds)
        {
            switch (f.Health)
            {
                case "Healthy": _healthy++; break;
                case "Warn": _warn++; break;
                case "Down": _down++; break;
            }
        }
    }

    private void SafePublish()
    {
        try
        {
            lock (_publishGate)
            {
                if (_disposed) return;
                var frame = BuildFrame();
                _sse.Broadcast(Encode(frame));
            }
        }
        catch { /* telemetry must never crash the dashboard */ }
    }

    private ViewFrame BuildFrame()
    {
        bool connected; ControlSnapshot? last; DateTime lastUtc;
        int total, healthy, warn, down;
        lock (_gate)
        {
            connected = _connected; last = _last; lastUtc = _lastSnapshotUtc;
            total = _total; healthy = _healthy; warn = _warn; down = _down;
        }

        bool feedStopped = _signaller.ReadGridStopped();

        double ageSec = 0; bool stale = false;
        if (connected && lastUtc != DateTime.MinValue)
        {
            var age = DateTime.UtcNow - lastUtc;
            ageSec = age.TotalSeconds;
            stale = age > StaleAfter;
        }

        int renderPid = connected && last is not null ? last.RenderPid : 0;
        int connecting = Math.Max(0, total - healthy - warn - down);

        return new ViewFrame
        {
            Seq = ++_seq,
            Connected = connected,
            FeedStopped = feedStopped,
            RenderPid = renderPid,
            AgeSec = Math.Round(ageSec, 1),
            Stale = stale,
            Banner = BuildBanner(connected, feedStopped),
            Status = BuildStatus(connected, lastUtc, ageSec, stale, total, healthy, warn, down, connecting),
            Tally = new Tally { Total = total, Healthy = healthy, Warn = warn, Down = down, Connecting = connecting },
            Feeds = BuildFeeds(connected, last),
            Visual = connected && last is not null ? last.Visual : new VisualSnapshot(),
            Settings = connected && last is not null ? last.Settings : new SettingsSnapshot(),
        };
    }

    /// <summary>The big tri-state header line — lifted verbatim from the form's UpdateConnBanner/ApplySnapshot.</summary>
    private static Banner BuildBanner(bool connected, bool feedStopped)
    {
        if (connected)
            return new Banner
            {
                Kind = "connected",
                Text = "● CONNECTED",
            };
        if (feedStopped)
            return new Banner { Kind = "feedStopped", Text = "STREAM STOPPED" };
        return new Banner { Kind = "down", Text = "● GRID IS DOWN — RECONNECTING…" };
    }

    /// <summary>The dim second line — stream summary + telemetry freshness (form's OnTick staleness block).</summary>
    private static StatusLine BuildStatus(bool connected, DateTime lastUtc, double ageSec, bool stale,
        int total, int healthy, int warn, int down, int connecting)
    {
        if (!connected || lastUtc == DateTime.MinValue)
            return new StatusLine { Kind = "none", Text = "" };

        if (stale)
            return new StatusLine
            {
                Kind = "warn",
                Text = string.Format(CultureInfo.InvariantCulture, "⚠ No telemetry for {0:0}s — link may be stalling.", ageSec),
            };

        bool needsAttention = warn > 0 || down > 0;
        return new StatusLine
        {
            Kind = needsAttention ? "warn" : "ok",
            Text = string.Format(CultureInfo.InvariantCulture, "{0}  ·  updated {1:0.0}s ago",
                Summary(total, healthy, warn, down, connecting), ageSec),
        };
    }

    /// <summary>One-line health breakdown — only the categories needing attention (form's StreamSummary).</summary>
    private static string Summary(int total, int healthy, int warn, int down, int connecting)
    {
        if (total == 0) return "No streams configured";
        var parts = new List<string>();
        if (warn > 0) parts.Add($"{warn} weak");
        if (down > 0) parts.Add($"{down} down");
        if (connecting > 0) parts.Add($"{connecting} connecting");
        return parts.Count == 0 ? "All streams healthy" : string.Join("  ·  ", parts);
    }

    /// <summary>Live feed rows when connected; four offline placeholders when down so the table keeps its
    /// shape (the form kept four rows and greyed them out via SetOffline).</summary>
    private static List<FeedSnapshot> BuildFeeds(bool connected, ControlSnapshot? last)
    {
        if (connected && last is not null && last.Feeds.Count > 0)
            return last.Feeds;

        var placeholders = new List<FeedSnapshot>(4);
        for (int i = 0; i < 4; i++)
            placeholders.Add(new FeedSnapshot { Index = i, Cell = i + 1, State = "—", Health = "Offline" });
        return placeholders;
    }

    /// <summary>Frame the JSON as one SSE event: <c>id: {seq}\ndata: {json}\n\n</c> (UTF-8).</summary>
    private static byte[] Encode(ViewFrame frame)
    {
        string json = JsonSerializer.Serialize(frame, Json);
        return Encoding.UTF8.GetBytes($"id: {frame.Seq}\ndata: {json}\n\n");
    }

    public void Dispose()
    {
        lock (_publishGate) _disposed = true;
        try { _client.SnapshotReceived -= OnSnapshot; } catch { }
        try { _client.ConnectionChanged -= OnConnection; } catch { }
        try { _timer.Dispose(); } catch { }
    }
}

// ---- wire DTOs (browser-facing; serialized camelCase) ----

/// <summary>One flat frame the browser renders directly. All merge/staleness/banner decisions are already
/// baked in server-side; the JS does no business logic.</summary>
internal sealed class ViewFrame
{
    public long Seq { get; set; }
    public bool Connected { get; set; }
    public bool FeedStopped { get; set; }
    public int RenderPid { get; set; }
    public double AgeSec { get; set; }
    public bool Stale { get; set; }
    public Banner Banner { get; set; } = new();
    public StatusLine Status { get; set; } = new();
    public Tally Tally { get; set; } = new();
    public List<FeedSnapshot> Feeds { get; set; } = new();
    public VisualSnapshot Visual { get; set; } = new();
    public SettingsSnapshot Settings { get; set; } = new();
}

/// <summary>The big header line. <see cref="Kind"/> drives the colour ("connected"/"feedStopped"/"down").</summary>
internal sealed class Banner
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>The dim second line. <see cref="Kind"/> ∈ "ok" | "warn" | "none".</summary>
internal sealed class StatusLine
{
    public string Kind { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>At-a-glance per-health counts for the header tally.</summary>
internal sealed class Tally
{
    public int Total { get; set; }
    public int Healthy { get; set; }
    public int Warn { get; set; }
    public int Down { get; set; }
    public int Connecting { get; set; }
}
