using System.Diagnostics;
using System.Threading;

namespace NavStream;

/// <summary>
/// Supervisor mode (spec §1, D1/D5). Default behavior when the artifact is double-clicked: spawn
/// <c>NavStream.exe --render</c>, wait for it to exit, relaunch. A render that exits within
/// SupervisorMinHealthyMs is treated as a crash-loop and relaunched with exponential backoff
/// (1→2→4→8→15s cap). Never gives up. A deliberate stop (render exits with code 42) stops the
/// supervisor too. Crucially does NOT use a kill-on-close Job Object: if the supervisor itself is
/// killed, the render child keeps playing (D5).
///
/// Also owns the tray control dashboard process (D-DASH-1): it spawns <c>NavStream.exe --dashboard</c>
/// once and keeps it alive across render restarts on a background thread, and tears it down on a full
/// stop (grid Ctrl+Shift+Q, or the dashboard's own "Exit Application" via the full-stop event).
/// </summary>
internal static class Supervisor
{
    private static volatile bool _stopping;
    private static volatile Process? _renderChild;
    private static volatile Process? _dashboardChild;

    // Stream-feed stop/resume: when the operator stops the feed, the grid is torn down and held
    // stopped (no relaunch) until they start it again, without exiting the supervisor or dashboard.
    private static volatile bool _paused;
    private static readonly ManualResetEventSlim _resumeGate = new(true);   // open = grid may run

    public static int Run(string[] args)
    {
        var config = Config.Load();
        Logger.Init(config.ResolveLogPath(), "SUP");

        string exePath = Environment.ProcessPath
                         ?? Process.GetCurrentProcess().MainModule?.FileName
                         ?? "NavStream.exe";

        Logger.Log($"Supervisor: starting. exe={exePath}, minHealthyMs={config.SupervisorMinHealthyMs}, backoffMaxMs={config.SupervisorBackoffMaxMs}");

        CrashGuards.SuppressWerDialog();

        // Full-stop event: created here so the dashboard process can open & set it to ask for a
        // whole-app teardown even while the render grid is down (manual-reset; we exit on it).
        var fullStop = new EventWaitHandle(false, EventResetMode.ManualReset, Constants.FullStopEventName);
        StartFullStopWaiter(fullStop, exePath, args);

        // Stream-feed stop/resume: the dashboard's "Live video feed" toggle pulses these to stop/start
        // the grid; the manual-reset "stopped" event lets it render the toggle while the render is down.
        // Created before the dashboard is spawned so it can always OpenExisting them.
        var gridStop = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.GridStopEventName);
        var gridStart = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.GridStartEventName);
        var gridStopped = new EventWaitHandle(false, EventResetMode.ManualReset, Constants.GridStoppedStateEventName);
        StartGridStopWaiter(gridStop, gridStopped);
        StartGridStartWaiter(gridStart, gridStopped);

        // Shutdown Displays: a harder counterpart to the stop toggle. Holds the grid stopped (like a feed
        // stop) and force-kills every NavStream render process on the machine, orphans included. Created
        // before the dashboard is spawned so it can always OpenExisting it.
        var shutdownDisplays = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.ShutdownDisplaysEventName);
        StartShutdownDisplaysWaiter(shutdownDisplays, gridStopped);

        // Tray dashboard: spawned once and kept alive independently of the render restart loop.
        StartDashboardKeeper(exePath, args);

        const int backoffStartMs = 1000;
        int backoffMs = backoffStartMs;

        while (!_stopping)
        {
            // Hold here while the operator has stopped the feed; the grid-start waiter opens the gate.
            _resumeGate.Wait();
            if (_stopping) break;

            var sw = Stopwatch.StartNew();
            Process? child;
            try
            {
                child = StartRender(exePath, args);
            }
            catch (Exception ex)
            {
                Logger.Log($"Supervisor: failed to spawn render: {ex.Message}; retrying in {backoffMs}ms.");
                Thread.Sleep(backoffMs);
                backoffMs = NextBackoff(backoffMs, config.SupervisorBackoffMaxMs);
                continue;
            }

            if (child is null)
            {
                Logger.Log($"Supervisor: spawn returned null; retrying in {backoffMs}ms.");
                Thread.Sleep(backoffMs);
                backoffMs = NextBackoff(backoffMs, config.SupervisorBackoffMaxMs);
                continue;
            }

            _renderChild = child;
            Logger.Log($"Supervisor: spawned render PID {child.Id}.");
            child.WaitForExit();
            int code = child.ExitCode;
            sw.Stop();
            _renderChild = null;
            Logger.Log($"Supervisor: render PID {child.Id} exited code={code} after {sw.ElapsedMilliseconds}ms.");

            if (_stopping) break;

            if (code == Constants.ExitStop)
            {
                Logger.Log("Supervisor: render requested full stop (Ctrl+Shift+Q). Stopping dashboard + supervisor.");
                _stopping = true;
                var dash = _dashboardChild; _dashboardChild = null;
                KillChild(dash, "dashboard");
                return 0;
            }

            // Feed stopped by the operator: the render we just stopped should NOT relaunch. Loop back
            // to the gate (above) and hold until a start is requested — skip the crash-loop backoff.
            if (_paused)
            {
                Logger.Log("Supervisor: feed stopped by operator — holding (grid will not relaunch until resumed).");
                continue;
            }

            // Crash-loop detection: a render that dies almost immediately gets backed off so a
            // persistently-broken build can't spin the CPU. A healthy long run resets backoff.
            if (sw.ElapsedMilliseconds < config.SupervisorMinHealthyMs)
            {
                Logger.Log($"Supervisor: render exited within {config.SupervisorMinHealthyMs}ms (crash-loop) — backing off {backoffMs}ms.");
                Thread.Sleep(backoffMs);
                backoffMs = NextBackoff(backoffMs, config.SupervisorBackoffMaxMs);
            }
            else
            {
                backoffMs = backoffStartMs; // healthy run -> reset, relaunch immediately
            }
        }

        var dashChild = _dashboardChild; _dashboardChild = null;
        KillChild(dashChild, "dashboard");
        return 0;
    }

    private static Process? StartRender(string exePath, string[] args)
        => StartChild(exePath, args, Program.RenderFlag);

    /// <summary>Keep the tray dashboard process alive: spawn it, wait, respawn if it dies. Background.</summary>
    private static void StartDashboardKeeper(string exePath, string[] args)
    {
        var t = new Thread(() =>
        {
            int backoff = 1000;
            while (!_stopping)
            {
                Process? dash;
                try { dash = StartChild(exePath, args, Constants.DashboardFlag); }
                catch (Exception ex)
                {
                    Logger.Log($"Supervisor: failed to spawn dashboard: {ex.Message}; retrying in {backoff}ms.");
                    Thread.Sleep(backoff);
                    backoff = Math.Min(backoff * 2, 15000);
                    continue;
                }

                if (dash is null) { Thread.Sleep(backoff); backoff = Math.Min(backoff * 2, 15000); continue; }

                _dashboardChild = dash;
                Logger.Log($"Supervisor: spawned dashboard PID {dash.Id}.");
                try { dash.WaitForExit(); } catch { }
                _dashboardChild = null;
                if (_stopping) break;

                Logger.Log("Supervisor: dashboard exited — respawning.");
                Thread.Sleep(750); // small guard against a tight respawn loop
                backoff = 1000;
            }
        }) { IsBackground = true, Name = "dashboard-keeper" };
        t.Start();
    }

    /// <summary>Wait for the full-stop event, then tear down render + dashboard and exit the process.</summary>
    private static void StartFullStopWaiter(EventWaitHandle fullStop, string exePath, string[] args)
    {
        var t = new Thread(() =>
        {
            try { fullStop.WaitOne(); } catch { return; }
            _stopping = true;
            _resumeGate.Set();   // release the main loop if it's parked on a stopped feed
            Logger.Log("Supervisor: full-stop event signalled — tearing down render + dashboard.");

            // Give the render grid a brief chance to exit gracefully (engine cleanup) before killing.
            try
            {
                using var stopRender = EventWaitHandle.OpenExisting(Constants.RenderStopEventName);
                stopRender.Set();
            }
            catch { /* no render up */ }

            var rc = _renderChild; _renderChild = null;
            try { rc?.WaitForExit(2000); } catch { }
            KillChild(rc, "render");

            var dc = _dashboardChild; _dashboardChild = null;
            KillChild(dc, "dashboard");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "fullstop-waiter" };
        t.Start();
    }

    /// <summary>Wait for the dashboard's stop-feed pulse, then stop the grid and hold it stopped: close
    /// the gate so the main loop won't relaunch, mark the shared "stopped" state for the dashboard, and
    /// ask the current render to exit (it exits 0, but the main loop sees <see cref="_paused"/> and holds).</summary>
    private static void StartGridStopWaiter(EventWaitHandle stopRequest, EventWaitHandle stoppedState)
    {
        var t = new Thread(() =>
        {
            while (!_stopping)
            {
                try { stopRequest.WaitOne(); } catch { return; }
                if (_stopping) break;

                _paused = true;
                _resumeGate.Reset();
                try { stoppedState.Set(); } catch { }   // dashboard reads this to show "feed stopped"
                Logger.Log("Supervisor: stop-feed requested — stopping the video grid (held stopped).");

                // Ask the render to close gracefully (engine cleanup); the main loop won't relaunch it.
                try
                {
                    using var stopRender = EventWaitHandle.OpenExisting(Constants.RenderStopEventName);
                    stopRender.Set();
                }
                catch { /* no render up */ }

                var rc = _renderChild;
                try { rc?.WaitForExit(2000); } catch { }
                KillChild(rc, "render (feed stopped)");
            }
        }) { IsBackground = true, Name = "grid-stop-waiter" };
        t.Start();
    }

    /// <summary>Wait for the dashboard's start-feed pulse, then resume the grid: clear the shared
    /// "stopped" state and open the gate so the main loop spawns a fresh render.</summary>
    private static void StartGridStartWaiter(EventWaitHandle startRequest, EventWaitHandle stoppedState)
    {
        var t = new Thread(() =>
        {
            while (!_stopping)
            {
                try { startRequest.WaitOne(); } catch { return; }
                if (_stopping) break;

                Logger.Log("Supervisor: start-feed requested — resuming the video grid.");
                _paused = false;
                try { stoppedState.Reset(); } catch { }
                _resumeGate.Set();
            }
        }) { IsBackground = true, Name = "grid-start-waiter" };
        t.Start();
    }

    /// <summary>Wait for the dashboard's "Shutdown Displays" pulse: stop and hold the grid like a feed stop
    /// (so the main loop won't relaunch), then force-kill every NavStream render process on the machine —
    /// the current child plus any orphans a dead supervisor left playing (D5). Unlike the graceful stop
    /// toggle this skips the engine-cleanup wait: the operator asked for a hard kill of every render process.
    /// The supervisor + dashboard stay alive so the feed can be relaunched later.</summary>
    private static void StartShutdownDisplaysWaiter(EventWaitHandle request, EventWaitHandle stoppedState)
    {
        var t = new Thread(() =>
        {
            while (!_stopping)
            {
                try { request.WaitOne(); } catch { return; }
                if (_stopping) break;

                _paused = true;
                _resumeGate.Reset();
                try { stoppedState.Set(); } catch { }   // dashboard reads this to show "feed stopped"
                Logger.Log("Supervisor: shutdown-displays requested — holding grid stopped + sweeping render processes.");

                // The sweep kills our tracked child too (by image name), so just drop the handle.
                _renderChild = null;
                SweepRenderProcesses();
            }
        }) { IsBackground = true, Name = "shutdown-displays-waiter" };
        t.Start();
    }

    /// <summary>Force-kill every NavStream render process on the machine: the current render child plus any
    /// orphaned renders a previously-killed supervisor left playing (D5). Identifies them by image name
    /// minus the two known-good PIDs (this supervisor + the tray dashboard), so it needs no command-line
    /// probing and never touches the control panel. Best-effort per process.</summary>
    private static void SweepRenderProcesses()
    {
        int selfPid = Environment.ProcessId;
        int dashPid = _dashboardChild?.Id ?? -1;
        string name = Process.GetCurrentProcess().ProcessName;   // image base name (no .exe), e.g. "NavStream"

        Process[] procs;
        try { procs = Process.GetProcessesByName(name); }
        catch (Exception ex) { Logger.Log($"Supervisor: render sweep failed to enumerate: {ex.Message}"); return; }

        int killed = 0;
        foreach (var p in procs)
        {
            try
            {
                if (p.Id == selfPid || p.Id == dashPid) continue;   // keep the supervisor + the dashboard
                p.Kill(entireProcessTree: true);
                killed++;
                Logger.Log($"Supervisor: shutdown-displays killed render PID {p.Id}.");
            }
            catch (Exception ex) { Logger.Log($"Supervisor: shutdown-displays kill PID {p.Id} failed: {ex.Message}"); }
            finally { try { p.Dispose(); } catch { } }
        }
        Logger.Log($"Supervisor: shutdown-displays sweep complete — {killed} render process(es) killed.");
    }

    private static Process? StartChild(string exePath, string[] args, string modeFlag)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(modeFlag);
        // Forward any extra args, but never our own mode flags (so a render child never inherits
        // --dashboard, and vice versa).
        foreach (var a in args)
        {
            if (string.Equals(a, Program.RenderFlag, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a, Constants.DashboardFlag, StringComparison.OrdinalIgnoreCase)) continue;
            psi.ArgumentList.Add(a);
        }

        // Deliberately NOT placed in a kill-on-close Job Object: if the supervisor dies, the
        // render child must keep playing (D5).
        return Process.Start(psi);
    }

    private static void KillChild(Process? p, string label)
    {
        if (p is null) return;
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                Logger.Log($"Supervisor: killed {label} PID {p.Id}.");
            }
        }
        catch (Exception ex) { Logger.Log($"Supervisor: kill {label} failed: {ex.Message}"); }
    }

    private static int NextBackoff(int current, int capMs)
    {
        long next = (long)current * 2;
        return (int)Math.Min(next, capMs);
    }
}
