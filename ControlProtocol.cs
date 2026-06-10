using System.Text.Json;

namespace NavStream;

/// <summary>
/// Wire types for the control dashboard IPC (D-DASH-1: separate process over a named pipe).
/// The render process is the server; the dashboard process is the client. Two message types,
/// one per direction, serialized as one-line JSON each (newline-delimited framing):
///   • <see cref="ControlSnapshot"/>  render → dashboard  (telemetry + current config)
///   • <see cref="ControlCommand"/>   dashboard → render  (operator actions)
/// Both sides are the same assembly, so the DTOs are shared directly — no schema duplication.
/// </summary>
internal static class ControlJson
{
    public static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Per-feed telemetry row (mirrors <see cref="FeedController"/> live state, §6).</summary>
internal sealed class FeedSnapshot
{
    public int Index { get; set; }
    public int Cell { get; set; }
    public string Name { get; set; } = "";   // operator-set vessel name (overlay label); "" => unnamed
    public bool NamesOnly { get; set; }       // per-feed "name only" overlay mode (name + health dot, no detail)
    public string Url { get; set; } = "";
    public string State { get; set; } = "";   // FeedState
    public string Health { get; set; } = "";   // FeedHealth
    public double BitrateKbps { get; set; }
    public long LostPictures { get; set; }
    public long DecodedVideo { get; set; }
    public string Status { get; set; } = "";   // FeedController.StatusText()
}

/// <summary>Window/visual params the dashboard can read and (later phases) change.</summary>
internal sealed class VisualSnapshot
{
    public bool OverlayEnabled { get; set; }
    public bool LogoEnabled { get; set; }
    // Brand-watermark appearance, mirrored back so the dashboard "Logo settings" controls self-populate.
    public int LogoOpacityPct { get; set; }
    public int LogoBrightnessPct { get; set; }
    public int LogoSizePct { get; set; }
    public int LogoPosition { get; set; }
    // Health-overlay badge appearance, mirrored back so the dashboard "Overlay settings" controls self-populate.
    public int BadgeOpacityPct { get; set; }
    public int BadgeSizePct { get; set; }
    public int BadgePosition { get; set; }
    // Legacy single-display fields, kept populated (= Displays[0].Monitor) so old clients keep working.
    public int Monitor { get; set; }
    public int MonitorCount { get; set; }
    public bool Borderless { get; set; }
    public bool AlwaysOnTop { get; set; }
    /// <summary>The multi-monitor partition (DisplayConfig doubles as the wire DTO, like InactiveStream).</summary>
    public List<DisplayConfig> Displays { get; set; } = new();
    /// <summary>Configured streams with no cell on the wall (Normalize rule 6 residual: Σ Cells maxed out
    /// below the stream count). They have no engine feed — and so no feed card — which is exactly why the
    /// dashboard needs this count to warn the operator.</summary>
    public int UnassignedStreams { get; set; }
}

/// <summary>The engine knobs from <see cref="Config"/> (§7) the dashboard exposes (Phase C).</summary>
internal sealed class SettingsSnapshot
{
    public int NetworkCachingMs { get; set; }
    public int NetworkTimeoutSec { get; set; }
    public bool HwDecode { get; set; }
    public bool SkipLoopFilter { get; set; }
    public List<string> ExtraMpvArgs { get; set; } = new();
    public int DemuxAnalyzeDurationMs { get; set; }
    public int DemuxProbesizeBytes { get; set; }
    // RTP / RTSP transport (mirror Config; per-stream, live on reconnect)
    public string RtspTransport { get; set; } = "lavf";
    public int RtspFrameBufferSizeBytes { get; set; }
    public int RtpTimeoutSec { get; set; }
    public int ReorderQueueSize { get; set; }
    public int BackoffStartMs { get; set; }
    public int BackoffMaxMs { get; set; }
    public double BackoffFactor { get; set; }
    public int BackoffResetMs { get; set; }
    public int StallTimeoutMs { get; set; }
}

/// <summary>One full telemetry frame pushed render → dashboard (~1/s and on every state flip).</summary>
internal sealed class ControlSnapshot
{
    public List<FeedSnapshot> Feeds { get; set; } = new();
    public VisualSnapshot Visual { get; set; } = new();
    public SettingsSnapshot Settings { get; set; } = new();
    public string ConfigPath { get; set; } = "";
    public int RenderPid { get; set; }
    public List<InactiveStream> InactivePool { get; set; } = new(); // parked pool, for the dashboard roster editor

    /// <summary>Active streams with no cell on the wall (Normalize rule-6 residual; same count as
    /// <see cref="VisualSnapshot.UnassignedStreams"/>). They have no engine feed, so they ride here as
    /// url/name/namesOnly triplets (<see cref="InactiveStream"/> doubles as the triplet DTO) — the roster
    /// editor must seed its draft with them or an applyRoster would silently delete them from config.</summary>
    public List<InactiveStream> UnassignedActive { get; set; } = new();
}

/// <summary>Command names (dashboard → render). Kept as constants so both ends agree.</summary>
internal static class ControlCommands
{
    public const string Reconnect = "reconnect";            // Index = feed (0-based)
    public const string RestartDisplay = "restartDisplay";  // render exits 0 → supervisor relaunches
    public const string SetOverlay = "setOverlay";          // BoolValue
    public const string SetLogo = "setLogo";                // BoolValue (brand watermark, live)
    public const string SetLogoOpacity = "setLogoOpacity";       // IntValue 0..80   (watermark alpha %, live)
    public const string SetLogoBrightness = "setLogoBrightness"; // IntValue 0..100  (watermark RGB lift %, live)
    public const string SetLogoSize = "setLogoSize";             // IntValue 5..30   (watermark width % of cell, live)
    public const string SetLogoPosition = "setLogoPosition";     // IntValue 0..3    (watermark corner, live)
    public const string SetBadgeOpacity = "setBadgeOpacity";     // IntValue 30..100 (badge surface opacity %, live)
    public const string SetBadgeSize = "setBadgeSize";           // IntValue 60..160 (badge size % of base scale, live)
    public const string SetBadgePosition = "setBadgePosition";   // IntValue 0..3    (badge corner, live)
    public const string SetMonitor = "setMonitor";          // Index = display (default 0 ⇒ old clients keep working), IntValue = monitor
    public const string SetLayout = "setLayout";            // Index = display, StringValue = layout ("auto"/"CxR"); live, capacity-gated
    public const string ApplyDisplays = "applyDisplays";    // Displays (wholesale partition → Normalize + Save + restart)
    public const string SetBorderless = "setBorderless";    // BoolValue (Phase B)
    public const string SetAlwaysOnTop = "setAlwaysOnTop";  // BoolValue (Phase B)
    public const string SetName = "setName";                // Index + StringValue (vessel name, live)
    public const string SetOverlayNamesOnly = "setOverlayNamesOnly"; // Index + BoolValue ("name only" mode, live)
    public const string ApplySettings = "applySettings";    // Settings  (Phase C, session-only)
    public const string SetStreams = "setStreams";          // Streams (per-cell source URLs, live swap)
    public const string Save = "save";                      // persist current Config to streams.json (Phase C)
    public const string ApplyRoster = "applyRoster";        // wholesale roster overwrite + Save + restart (dynamic-grid Tier 1)
    public const string FullStop = "fullStop";              // tear the whole app down
}

/// <summary>One operator action dashboard → render. Optional fields carry the payload per command.</summary>
internal sealed class ControlCommand
{
    public string Name { get; set; } = "";
    public int Index { get; set; }
    public bool BoolValue { get; set; }
    public int IntValue { get; set; }
    public string? StringValue { get; set; }   // free-text payload (e.g. a vessel name for SetName)
    public List<string>? Streams { get; set; } // per-cell source URLs (SetStreams / Save), positional
    public List<string>? Names { get; set; }                 // active vessel names, aligned with Streams (applyRoster)
    public List<bool>? NamesOnly { get; set; }               // active "name only" flags, aligned with Streams (applyRoster)
    public List<InactiveStream>? InactivePool { get; set; }  // the parked pool, wholesale (applyRoster)
    public List<DisplayConfig>? Displays { get; set; }       // the display partition, wholesale (applyDisplays / applyRoster)
    public SettingsSnapshot? Settings { get; set; }
    public VisualSnapshot? Visual { get; set; }
}
