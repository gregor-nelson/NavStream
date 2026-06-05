using System.Text.Json;

namespace MpvGrid;

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
    public int Monitor { get; set; }
    public int MonitorCount { get; set; }
    public bool Borderless { get; set; }
    public bool AlwaysOnTop { get; set; }
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
}

/// <summary>Command names (dashboard → render). Kept as constants so both ends agree.</summary>
internal static class ControlCommands
{
    public const string Reconnect = "reconnect";            // Index = feed (0-based)
    public const string RestartDisplay = "restartDisplay";  // render exits 0 → supervisor relaunches
    public const string SetOverlay = "setOverlay";          // BoolValue
    public const string SetMonitor = "setMonitor";          // IntValue  (Phase B)
    public const string SetBorderless = "setBorderless";    // BoolValue (Phase B)
    public const string SetAlwaysOnTop = "setAlwaysOnTop";  // BoolValue (Phase B)
    public const string SetName = "setName";                // Index + StringValue (vessel name, live)
    public const string SetOverlayNamesOnly = "setOverlayNamesOnly"; // Index + BoolValue ("name only" mode, live)
    public const string ApplySettings = "applySettings";    // Settings  (Phase C, session-only)
    public const string SetStreams = "setStreams";          // Streams (per-cell source URLs, live swap)
    public const string Save = "save";                      // persist current Config to streams.json (Phase C)
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
    public SettingsSnapshot? Settings { get; set; }
    public VisualSnapshot? Visual { get; set; }
}
