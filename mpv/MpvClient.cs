using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace NavStream.Mpv;

/// <summary>Event ids surfaced to the engine (subset of mpv_event_id — see <see cref="MpvNative"/>).</summary>
internal enum MpvEventId
{
    None = MpvNative.MPV_EVENT_NONE,
    Shutdown = MpvNative.MPV_EVENT_SHUTDOWN,
    EndFile = MpvNative.MPV_EVENT_END_FILE,
    FileLoaded = MpvNative.MPV_EVENT_FILE_LOADED,
    PlaybackRestart = MpvNative.MPV_EVENT_PLAYBACK_RESTART,
    PropertyChange = MpvNative.MPV_EVENT_PROPERTY_CHANGE,
}

/// <summary>Why an END_FILE fired (mpv_end_file_reason). Drives the §3.3 reconnect mapping.</summary>
internal enum MpvEndReason
{
    Eof = MpvNative.MPV_END_FILE_REASON_EOF,
    Stop = MpvNative.MPV_END_FILE_REASON_STOP,
    Quit = MpvNative.MPV_END_FILE_REASON_QUIT,
    Error = MpvNative.MPV_END_FILE_REASON_ERROR,
    Redirect = MpvNative.MPV_END_FILE_REASON_REDIRECT,
    Unknown = -1,
}

/// <summary>A pump event marshalled out of <see cref="MpvClient"/>'s background thread. Carries only what
/// the reconnect/telemetry layers need; property values are polled via <c>GetProperty*</c>, not read off
/// the event union, so we never depend on per-version struct layout.</summary>
internal readonly struct MpvEvent
{
    public MpvEventId Id { get; }
    public int Error { get; }
    public MpvEndReason EndReason { get; }   // valid when Id == EndFile
    public string? PropertyName { get; }     // valid when Id == PropertyChange

    private MpvEvent(MpvEventId id, int error, MpvEndReason endReason, string? propertyName)
    {
        Id = id;
        Error = error;
        EndReason = endReason;
        PropertyName = propertyName;
    }

    public static MpvEvent Simple(MpvEventId id, int error) => new(id, error, MpvEndReason.Unknown, null);
    public static MpvEvent End(MpvEndReason reason, int error) => new(MpvEventId.EndFile, error, reason, null);
    public static MpvEvent Property(string? name) => new(MpvEventId.PropertyChange, 0, MpvEndReason.Unknown, name);
}

/// <summary>
/// Managed wrapper over one mpv handle (D3 — one handle per feed). Thin: create → set <c>wid</c> + core
/// options → initialize → run a dedicated pump thread that marshals events out via <see cref="Event"/>.
/// Knows nothing about WinForms or <see cref="FeedController"/>; the engine wires it in Phases 3–5.
///
/// Threading: <see cref="Event"/> is raised from the pump thread, exactly like the previous engine's events fired off
/// a native thread — subscribers marshal to the UI thread themselves (the existing handlers already do).
/// Never call back into mpv from inside an <see cref="Event"/> handler on the pump thread.
/// </summary>
internal sealed class MpvClient : IDisposable
{
    private readonly IntPtr _ctx;
    private readonly Thread _pump;
    private volatile bool _disposed;

    /// <summary>Raised once per mpv event from the pump thread. Handlers must not throw or block on mpv.</summary>
    public event Action<MpvEvent>? Event;

    /// <summary>libmpv client API version (e.g. 0x20005 == 2.5). Available without a live handle.</summary>
    public static uint ClientApiVersion => MpvNative.mpv_client_api_version();

    /// <param name="hwnd">The child control HWND to embed mpv's video output into (set as <c>wid</c> before
    /// <c>mpv_initialize</c>). Pass <see cref="IntPtr.Zero"/> for a headless handle (no embed) — used by the
    /// Phase 1 lifecycle check and any future off-screen use.</param>
    /// <param name="coreOptions">Engine-wide options (§3.1), set before initialize. Includes <c>vo</c>.</param>
    public MpvClient(IntPtr hwnd, IReadOnlyList<(string name, string value)> coreOptions)
    {
        _ctx = MpvNative.mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() returned NULL.");

        try
        {
            // wid embeds mpv into the host HWND and MUST be set before initialize (the per-handle
            // equivalent of the old videoView.MediaPlayer = player). Decimal HWND per mpv's wid contract.
            if (hwnd != IntPtr.Zero)
                Check(MpvNative.mpv_set_option_string(_ctx, "wid",
                    hwnd.ToInt64().ToString(CultureInfo.InvariantCulture)), "set wid");

            foreach (var (name, value) in coreOptions)
                Check(MpvNative.mpv_set_option_string(_ctx, name, value), $"set option {name}={value}");

            Check(MpvNative.mpv_initialize(_ctx), "mpv_initialize");

            // Keep mpv's own log spew off our stderr/console path.
            MpvNative.mpv_request_log_messages(_ctx, "no");
        }
        catch
        {
            // Initialization failed after create — don't leak the handle.
            MpvNative.mpv_terminate_destroy(_ctx);
            throw;
        }

        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "mpv-pump" };
        _pump.Start();
    }

    // ---- playback verbs ----

    /// <summary>loadfile &lt;url&gt; replace — reloads in place (emits END_FILE(STOP) for the previous file).</summary>
    public void Load(string url) => Command("loadfile", url, "replace");

    /// <summary>stop — ends playback, leaving the (idle) handle alive for the next Load.</summary>
    public void Stop() => Command("stop");

    /// <summary>Run an mpv command as a NULL-terminated argv (no quoting/escaping concerns vs the string form).</summary>
    public int Command(params string[] args)
    {
        var ptrs = new IntPtr[args.Length + 1];   // + trailing NULL terminator
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Utf8ToHGlobal(args[i]);
            ptrs[args.Length] = IntPtr.Zero;
            return MpvNative.mpv_command(_ctx, ptrs);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != IntPtr.Zero) Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    // ---- options / properties ----

    public int SetOption(string name, string value) => MpvNative.mpv_set_option_string(_ctx, name, value);
    public int SetProperty(string name, string value) => MpvNative.mpv_set_property_string(_ctx, name, value);

    public string? GetPropertyString(string name)
    {
        IntPtr p = MpvNative.mpv_get_property_string(_ctx, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { MpvNative.mpv_free(p); }
    }

    public bool TryGetDouble(string name, out double value) =>
        MpvNative.mpv_get_property_double(_ctx, name, MpvNative.MPV_FORMAT_DOUBLE, out value) == 0;

    public bool TryGetLong(string name, out long value) =>
        MpvNative.mpv_get_property_long(_ctx, name, MpvNative.MPV_FORMAT_INT64, out value) == 0;

    public bool TryGetFlag(string name, out bool value)
    {
        int rc = MpvNative.mpv_get_property_flag(_ctx, name, MpvNative.MPV_FORMAT_FLAG, out int v);
        value = v != 0;
        return rc == 0;
    }

    /// <summary>Subscribe to PROPERTY_CHANGE events for <paramref name="name"/> (value polled, so format is
    /// advisory — pass NONE to get a bare change notification).</summary>
    public int ObserveProperty(string name, int format = MpvNative.MPV_FORMAT_NONE, ulong userdata = 0) =>
        MpvNative.mpv_observe_property(_ctx, userdata, name, format);

    // ---- the pump ----

    private void PumpLoop()
    {
        while (true)
        {
            IntPtr p = MpvNative.mpv_wait_event(_ctx, -1.0);   // blocks until an event (or mpv_wakeup)
            if (_disposed) break;                              // teardown in progress: stop touching _ctx
            if (p == IntPtr.Zero) continue;

            var native = Marshal.PtrToStructure<MpvNative.mpv_event>(p);
            if (native.event_id == MpvNative.MPV_EVENT_SHUTDOWN) break;

            MpvEvent managed = Translate(native);
            var handler = Event;
            if (handler is null) continue;
            try { handler(managed); }
            catch { /* a handler must never kill the pump (and so the feed) */ }
        }
    }

    private static MpvEvent Translate(in MpvNative.mpv_event native)
    {
        switch (native.event_id)
        {
            case MpvNative.MPV_EVENT_END_FILE:
                // mpv_event_end_file's first field is the reason int; read just that (later fields shift
                // across mpv versions, so we deliberately don't marshal the whole struct).
                int reason = native.data != IntPtr.Zero ? Marshal.ReadInt32(native.data) : (int)MpvEndReason.Unknown;
                return MpvEvent.End((MpvEndReason)reason, native.error);

            case MpvNative.MPV_EVENT_PROPERTY_CHANGE:
                string? propName = null;
                if (native.data != IntPtr.Zero)
                {
                    var prop = Marshal.PtrToStructure<MpvNative.mpv_event_property>(native.data);
                    propName = Marshal.PtrToStringUTF8(prop.name);
                }
                return MpvEvent.Property(propName);

            default:
                return MpvEvent.Simple((MpvEventId)native.event_id, native.error);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Race-free teardown: ask the core to quit (the pump gets SHUTDOWN while _ctx is still valid),
        // wake the pump in case the SHUTDOWN is slow, JOIN it so no thread is inside wait_event, and only
        // THEN free the handle. Mirrors StopStatsTimer's bounded synchronous dispose (2s cap).
        try { MpvNative.mpv_command_string(_ctx, "quit"); } catch { /* best effort */ }
        try { MpvNative.mpv_wakeup(_ctx); } catch { /* best effort */ }
        try { _pump.Join(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        try { MpvNative.mpv_terminate_destroy(_ctx); } catch { /* best effort */ }
    }

    // ---- helpers ----

    private static void Check(int rc, string what)
    {
        if (rc < 0)
            throw new InvalidOperationException($"mpv {what} failed: {MpvNative.ErrorString(rc)} ({rc}).");
    }

    private static IntPtr Utf8ToHGlobal(string s)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(s);
        IntPtr p = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, p, bytes.Length);
        Marshal.WriteByte(p, bytes.Length, 0);   // NUL terminator
        return p;
    }
}
