using System.Threading;
using System.Windows.Forms;

namespace MpvGrid;

/// <summary>
/// The tray control dashboard process (--dashboard, D-DASH-1/4). A separate process from the render
/// grid so it survives a "restart display": the <see cref="ControlClient"/> simply reconnects to the
/// new render's pipe. Single-instance (a mutex) so the supervisor never ends up with two trays.
/// </summary>
internal static class DashboardHost
{
    public static int Run(string[] args)
    {
        var config = Config.Load();
        Logger.Init(config.ResolveLogPath(), "DASH");

        CrashGuards.SuppressWerDialog();

        using var mutex = new Mutex(false, Constants.DashboardMutexName);
        bool owns;
        try { owns = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owns = true; }

        if (!owns)
        {
            Logger.Log("Dashboard: another dashboard instance is already running — exiting.");
            return 0;
        }

        Logger.Log("Dashboard: starting tray control panel.");

        try
        {
            ApplicationConfiguration.Initialize();

            var client = new ControlClient();
            using var tray = new TrayContext(client, config.DashboardHttpPort);
            client.Start();

            Application.Run(tray);
        }
        catch (Exception ex)
        {
            Logger.Log($"Dashboard: FATAL: {ex}");
            return Constants.ExitCrash;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }

        Logger.Log("Dashboard: exited.");
        return 0;
    }
}
