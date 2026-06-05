using System.Threading;
using System.Windows.Forms;

namespace MpvGrid;

internal static class Program
{
    // Launch arg that switches the single artifact into the embedded render app.
    public const string RenderFlag = "--render";
    // Launch arg that switches the single artifact into the tray control dashboard (D-DASH-1/4).
    public const string DashboardFlag = Constants.DashboardFlag;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => string.Equals(a, RenderFlag, StringComparison.OrdinalIgnoreCase)))
            return RenderHost.Run(args);

        if (args.Any(a => string.Equals(a, DashboardFlag, StringComparison.OrdinalIgnoreCase)))
            return DashboardHost.Run(args);

        // Default (double-click) = supervisor.
        return Supervisor.Run(args);
    }
}

/// <summary>The embedded render app (--render): borderless 2×2 grid window with crash guards
/// and a startup mutex so two grids never fight for the monitor (spec §1, §8).</summary>
internal static class RenderHost
{
    public static int Run(string[] args)
    {
        var config = Config.Load();
        Logger.Init(config.ResolveLogPath(), "REN");

        CrashGuards.SuppressWerDialog();

        // Startup mutex: only one render instance owns the monitor at a time (§1).
        using var mutex = new Mutex(false, Constants.RenderMutexName);
        using var stopEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Constants.RenderStopEventName);

        if (!AcquireOwnership(mutex, stopEvent))
        {
            Logger.Log("Render: could not acquire the render mutex; another grid is up. Exiting (stop).");
            return Constants.ExitStop; // don't have the supervisor spin relaunching into a conflict
        }

        // Clear any lingering signal before we start listening, so we don't self-trigger.
        try { stopEvent.Reset(); } catch { }

        Logger.Log($"Render: starting. config={config.SourcePath}, streams={config.Streams.Count}, monitor={config.Monitor}");

        try
        {
            ApplicationConfiguration.Initialize();
            CrashGuards.InstallRenderHandlers();

            var app = new RenderApp(config);

            // Waiter: a future render instance (or the supervisor) sets the stop event to ask us to
            // exit so it can take over the monitor. Background thread dies with the process.
            var waiter = new Thread(() =>
            {
                try { stopEvent.WaitOne(); app.RequestExit(); } catch { }
            }) { IsBackground = true, Name = "render-stop-waiter" };
            waiter.Start();

            app.Run();
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }

        Logger.Log("Render: window closed, exiting (restart).");
        return Constants.ExitRestart;
    }

    /// <summary>
    /// Acquire the render mutex. If a stale render holds it, signal it to exit and wait for release.
    /// </summary>
    private static bool AcquireOwnership(Mutex mutex, EventWaitHandle stopEvent)
    {
        bool got;
        try { got = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { got = true; } // prior owner died; we inherit cleanly

        if (got) return true;

        Logger.Log("Render: another instance holds the mutex — signalling it to exit.");
        try { stopEvent.Set(); } catch { }

        try { got = mutex.WaitOne(5000); }
        catch (AbandonedMutexException) { got = true; }

        return got;
    }
}
