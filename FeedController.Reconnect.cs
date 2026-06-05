using MpvGrid.Mpv;

namespace MpvGrid;

/// <summary>
/// Step 5 — the independent per-feed reconnect state machine (spec §5, D4).
/// Triggers into Reconnecting: EncounteredError, EndReached/Stopped (a live feed shouldn't end),
/// and the stall detector (§6 stats timer calls <see cref="CheckStall"/>). Reconnect runs on a
/// background thread; backoff is exponential 2→4→8→16→30s, reset after a sustained healthy spell,
/// and retries forever at the cap.
/// </summary>
internal sealed partial class FeedController
{
    private readonly object _gate = new();
    private FeedState _state = FeedState.Connecting;
    private bool _disposed;
    private bool _intentionalStop;          // suppress the Stopped event we cause during reconnect
    private bool _reconnectInFlight;
    private int _currentBackoffMs;
    private DateTime _playingSinceUtc = DateTime.MinValue;
    private CancellationTokenSource _cts = new();

    /// <summary>Raised on any state/stat change so the overlay can repaint (UI-thread marshalled by subscriber).</summary>
    public event Action<FeedController>? Changed;

    public FeedState State { get { lock (_gate) return _state; } }

    private void WireEvents()
    {
        if (_client is null) return;
        _currentBackoffMs = _config.BackoffStartMs;

        // The previous build's 4 separate events (Playing/EncounteredError/EndReached/Stopped) collapse to mpv's single
        // END_FILE + a reason enum, plus FILE_LOADED for "playing" (§3.3). One subscription dispatches them.
        _client.Event += OnMpvEvent;
    }

    /// <summary>Dispatch a single mpv pump event onto the §3.3 reconnect map. Runs on mpv's <c>mpv-pump</c>
    /// thread (the analog of the previous build's native event thread): bodies stay non-blocking and never call back into
    /// mpv in a way that could deadlock — the actual restart is offloaded to a Task by <see cref="ScheduleReconnect"/>.
    /// The four handler bodies are preserved verbatim from the old per-event subscriptions.</summary>
    private void OnMpvEvent(MpvEvent e)
    {
        switch (e.Id)
        {
            // FILE_LOADED == the old Playing event (file open + output starting; matches that closely enough —
            // PLAYBACK_RESTART is the alternate trigger if the badge ever flips to LIVE before pixels appear).
            case MpvEventId.FileLoaded:
                lock (_gate)
                {
                    _playingSinceUtc = DateTime.UtcNow;
                    _reconnectInFlight = false;
                    // Clear the intentional-stop gate: the prior file's END_FILE(STOP) arrives before this
                    // FILE_LOADED (events are in order), so by now it has done its job — keep the flag from
                    // lingering after an idle reconnect that emitted no STOP.
                    _intentionalStop = false;
                }
                SetState(FeedState.Playing);
                Logger.Log($"Feed {CellNumber}: Playing.");
                break;

            case MpvEventId.EndFile:
                switch (e.EndReason)
                {
                    case MpvEndReason.Error:   // old EncounteredError
                        Logger.Log($"Feed {CellNumber}: EncounteredError.");
                        // Heuristic HW-surface-alloc fallback: if HW decode was on, drop to software for the
                        // retry (and stay there). Keeps a flaky GPU path from black-holing one panel forever.
                        if (_useHwDecode)
                        {
                            _useHwDecode = false;
                            Logger.Log($"Feed {CellNumber}: falling back to software decode after error.");
                        }
                        ScheduleReconnect("error");
                        break;

                    case MpvEndReason.Eof:     // old EndReached
                        Logger.Log($"Feed {CellNumber}: EndReached (live feed ended) — reconnecting.");
                        ScheduleReconnect("end");
                        break;

                    case MpvEndReason.Stop:    // old Stopped
                        lock (_gate)
                        {
                            if (_intentionalStop) { _intentionalStop = false; return; }
                        }
                        Logger.Log($"Feed {CellNumber}: Stopped unexpectedly — reconnecting.");
                        ScheduleReconnect("stopped");
                        break;

                    // Quit/Redirect: we never quit a feed handle and a redirect is benign — ignore both.
                }
                break;
        }
    }

    private void SetState(FeedState newState)
    {
        bool changed;
        lock (_gate)
        {
            changed = _state != newState;
            _state = newState;
        }
        if (changed) Changed?.Invoke(this);
    }

    /// <summary>Force a reconnect now (hotkeys 1–4). Resets nothing else; respects in-flight guard.</summary>
    public void ForceReconnect()
    {
        Logger.Log($"Feed {CellNumber}: manual force-reconnect.");
        ScheduleReconnect("manual", immediate: true);
    }

    /// <summary>Called by the §6 stats timer: if Playing but throughput is flat past the timeout, reconnect.</summary>
    private void CheckStall(bool throughputFlatForTimeout)
    {
        if (State != FeedState.Playing) return;
        if (throughputFlatForTimeout)
        {
            Logger.Log($"Feed {CellNumber}: stall detected (no throughput for {_config.StallTimeoutMs}ms) — reconnecting.");
            SetState(FeedState.Stalled);
            ScheduleReconnect("stall");
        }
    }

    private void ScheduleReconnect(string reason, bool immediate = false)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_reconnectInFlight) return;     // already handling a reconnect for this feed
            _reconnectInFlight = true;
            _state = FeedState.Reconnecting;
        }
        Changed?.Invoke(this);

        int delay;
        lock (_gate)
        {
            delay = immediate ? 0 : _currentBackoffMs;
            // Advance backoff for the *next* attempt (exponential, capped). Reset happens in
            // ResetBackoffIfHealthy() after a sustained Playing spell.
            long next = (long)Math.Round(_currentBackoffMs * _config.BackoffFactor);
            _currentBackoffMs = (int)Math.Min(next, _config.BackoffMaxMs);
        }

        var token = _cts.Token;
        Logger.Log($"Feed {CellNumber}: reconnect ({reason}) in {delay}ms.");

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0) await Task.Delay(delay, token);
                if (token.IsCancellationRequested) return;
                RestartPlayback();
            }
            catch (OperationCanceledException) { /* shutting down */ }
            catch (Exception ex)
            {
                Logger.Log($"Feed {CellNumber}: reconnect attempt threw: {ex.Message}");
                lock (_gate) { _reconnectInFlight = false; }
            }
        }, token);
    }

    /// <summary>Stop the current playback and start fresh. Runs on a background thread (never the UI thread).</summary>
    private void RestartPlayback()
    {
        var client = _client;
        if (client is null) return;
        if (_cts.IsCancellationRequested) return;

        SetState(FeedState.Connecting);
        // loadfile … replace both tears down the prior file and loads the new one in one command (no separate
        // Stop()). Set _intentionalStop BEFORE the load so the END_FILE(STOP) the replace emits for the prior
        // file is swallowed by OnMpvEvent. (None is emitted on an idle reconnect after EOF/error — a stuck
        // _intentionalStop=true is benign and is cleared on the next FILE_LOADED.) MarkReloaded() signals the
        // stats thread to reset the bitrate/stall gate, since mpv has no demux-counter reset to detect a reload.
        lock (_gate) { _intentionalStop = true; }
        try
        {
            MarkReloaded();
            ApplyStreamOptions(client);
            client.Load(_url);
            Logger.Log($"Feed {CellNumber}: reconnect play issued (hw={_useHwDecode}).");
        }
        catch (Exception ex)
        {
            Logger.Log($"Feed {CellNumber}: reconnect play failed: {ex.Message}");
            // Leave _reconnectInFlight set briefly; the next error/end event or stall will re-trigger.
            lock (_gate) { _reconnectInFlight = false; }
        }
        // Note: _reconnectInFlight is cleared by the FILE_LOADED event when the feed actually recovers.
    }

    /// <summary>Reset backoff to the start value after the feed has been healthy long enough (§5).</summary>
    private void ResetBackoffIfHealthy()
    {
        lock (_gate)
        {
            if (_state != FeedState.Playing) return;
            if (_playingSinceUtc == DateTime.MinValue) return;
            if ((DateTime.UtcNow - _playingSinceUtc).TotalMilliseconds >= _config.BackoffResetMs
                && _currentBackoffMs != _config.BackoffStartMs)
            {
                _currentBackoffMs = _config.BackoffStartMs;
                Logger.Log($"Feed {CellNumber}: healthy >{_config.BackoffResetMs}ms — backoff reset.");
            }
        }
    }

    private void CancelReconnects()
    {
        lock (_gate) { _disposed = true; }
        try { _cts.Cancel(); } catch { }
    }
}
