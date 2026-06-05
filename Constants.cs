namespace MpvGrid;

/// <summary>Shared names/codes used across the supervisor and render processes (spec §1, §3, §8).</summary>
internal static class Constants
{
    // Exit-code IPC between render and supervisor:
    //   0  -> normal exit, supervisor relaunches ("Esc/Alt+F4 = quit→restart", §3)
    //   1  -> crash (unhandled exception handler), supervisor relaunches (§8)
    //   42 -> deliberate full stop (Ctrl+Shift+Q), supervisor exits too (§3)
    public const int ExitRestart = 0;
    public const int ExitCrash = 1;
    public const int ExitStop = 42;

    // Named mutex so two render instances never fight for the monitor (§1).
    public const string RenderMutexName = @"Global\MpvGrid.Render.Mutex.v1";

    // Auto-reset event the supervisor sets to ask the current render instance to quit-and-stop;
    // also used by a new render instance to signal a stale one to exit (§1).
    public const string RenderStopEventName = @"Global\MpvGrid.Render.StopEvent.v1";

    // ---- Control dashboard (separate-process design, D-DASH-1) ----

    // Launch arg that runs the single artifact as the tray control dashboard.
    public const string DashboardFlag = "--dashboard";

    // Named pipe the render process serves and the dashboard process connects to. Machine-local,
    // same-user ACL. A fixed name so the dashboard re-finds the server after a grid restart.
    public const string ControlPipeName = "MpvGrid.Control.v1";

    // Single-instance mutex so the supervisor never ends up with two tray dashboards.
    public const string DashboardMutexName = @"Global\MpvGrid.Dashboard.Mutex.v1";

    // Manual-reset event the dashboard's "Exit Application" sets to ask the supervisor to tear the
    // whole app down (render + dashboard + itself). The supervisor waits on it on a background thread.
    public const string FullStopEventName = @"Global\MpvGrid.FullStop.Event.v1";

    // Stream-feed stop/resume (dashboard "Live video feed" toggle). Unlike a full stop, this only
    // stops the video grid (render) and keeps it stopped — the supervisor + dashboard stay alive so
    // the operator can relaunch the feed later. The render is the control-pipe server, so when it's
    // stopped the dashboard can't reach it; these supervisor-owned named events carry the toggle:
    //   • GridStop/GridStart  — auto-reset pulses the dashboard sets to request each transition.
    //   • GridStoppedState     — manual-reset flag the supervisor holds set while the grid is stopped,
    //                            so the dashboard can render the toggle/banner (even after a respawn).
    public const string GridStopEventName = @"Global\MpvGrid.Grid.StopEvent.v1";
    public const string GridStartEventName = @"Global\MpvGrid.Grid.StartEvent.v1";
    public const string GridStoppedStateEventName = @"Global\MpvGrid.Grid.StoppedState.v1";

    // Shutdown Displays (dashboard button): a harder counterpart to the grid-stop toggle. The supervisor
    // stops + holds the grid (like a feed stop, so it won't relaunch), then force-kills every MpvGrid
    // render process on the machine — its tracked child plus any orphans a dead supervisor left playing
    // (D5) — while keeping itself + the dashboard alive so the operator can relaunch later. Auto-reset
    // pulse the dashboard sets; the supervisor waits on it on a background thread.
    public const string ShutdownDisplaysEventName = @"Global\MpvGrid.ShutdownDisplays.Event.v1";
}
