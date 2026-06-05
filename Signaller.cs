using System.Threading;

namespace MpvGrid;

/// <summary>
/// Named-event signalling to the supervisor, factored out so the tray host (and the HTTP bridge) can
/// drive the grid stop/start/shutdown/full-stop transitions without a WinForms window. The supervisor
/// <em>owns</em> (creates) these events; this class only
/// <see cref="EventWaitHandle.OpenExisting(string)"/>s them, lazily and best-effort — when the app was
/// launched standalone (no supervisor) the events don't exist and every method simply returns false.
///
/// Thread-safe: the lazy handle opens are guarded by a lock so concurrent calls from the HTTP accept
/// threads and the view-frame timer can't race. Nothing here ever throws to its caller.
/// </summary>
internal sealed class Signaller : IDisposable
{
    private readonly object _gate = new();
    private EventWaitHandle? _gridStop;        // pulse: ask the supervisor to stop the grid
    private EventWaitHandle? _gridStart;       // pulse: ask the supervisor to resume the grid
    private EventWaitHandle? _gridStopped;     // read-only: set by the supervisor while the grid is stopped
    private EventWaitHandle? _shutdownDisplays; // pulse: stop the grid + force-kill all render processes
    private EventWaitHandle? _fullStop;        // pulse: tear the whole app (render + dashboard) down
    private bool _disposed;

    /// <summary>Pulse the supervisor's grid start- or stop-feed event. Returns false when there's no
    /// supervisor to signal (launched standalone).</summary>
    public bool SignalGrid(bool start)
    {
        ref EventWaitHandle? handle = ref (start ? ref _gridStart : ref _gridStop);
        return Pulse(ref handle, start ? Constants.GridStartEventName : Constants.GridStopEventName);
    }

    /// <summary>Pulse the supervisor's shutdown-displays event. False when there's no supervisor.</summary>
    public bool SignalShutdownDisplays() =>
        Pulse(ref _shutdownDisplays, Constants.ShutdownDisplaysEventName);

    /// <summary>Pulse the supervisor's full-stop event (tear the whole app down). False when standalone.</summary>
    public bool SignalFullStop() =>
        Pulse(ref _fullStop, Constants.FullStopEventName);

    /// <summary>Read the supervisor's manual-reset "grid stopped" flag (the dashboard can't learn this from
    /// a snapshot — the render pipe is down while stopped). False when there's no supervisor.</summary>
    public bool ReadGridStopped()
    {
        var h = Resolve(ref _gridStopped, Constants.GridStoppedStateEventName);
        if (h is null) return false;
        try { return h.WaitOne(0); } catch { return false; }
    }

    private bool Pulse(ref EventWaitHandle? slot, string name)
    {
        var h = Resolve(ref slot, name);
        if (h is null) return false;
        try { h.Set(); return true; } catch { return false; }
    }

    /// <summary>Lazily open a named event handle (re-trying on each call, since the supervisor may come up
    /// after us). Returns null when the event doesn't exist yet.</summary>
    private EventWaitHandle? Resolve(ref EventWaitHandle? slot, string name)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (slot is not null) return slot;
            try { slot = EventWaitHandle.OpenExisting(name); }
            catch { slot = null; }
            return slot;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            try { _gridStop?.Dispose(); } catch { }
            try { _gridStart?.Dispose(); } catch { }
            try { _gridStopped?.Dispose(); } catch { }
            try { _shutdownDisplays?.Dispose(); } catch { }
            try { _fullStop?.Dispose(); } catch { }
        }
    }
}
