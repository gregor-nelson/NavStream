using System.Drawing;
using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// The tray host for the control dashboard (D-DASH-1/4) — the <see cref="ApplicationContext"/> that is
/// the <see cref="Application.Run(ApplicationContext)"/> target for the dashboard process.
/// It owns the <see cref="NotifyIcon"/> + tray menu and wires the headless plumbing together:
/// <see cref="ControlClient"/> (named pipe to the render) and <see cref="Signaller"/> (named events to the
/// supervisor) feed a <see cref="ViewFrameBuilder"/>, which broadcasts to browser tabs over the
/// <see cref="SseHub"/> behind the <see cref="HttpControlBridge"/>; the UI itself is opened as an ordinary
/// browser tab by the <see cref="BrowserLauncher"/>.
///
/// If no loopback port binds the bridge runs headless — the tray + its menu (which drive the supervisor's
/// named events directly) still work, and a balloon tip says web control is unavailable. We never exit on
/// that failure: the supervisor would just respawn us into the same condition.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly ControlClient _client;
    private readonly Signaller _signaller;
    private readonly SseHub _sse;
    private readonly ViewFrameBuilder _builder;
    private readonly HttpControlBridge _bridge;
    private readonly NotifyIcon _tray;

    public TrayContext(ControlClient client, int httpPort)
    {
        _client = client;
        _signaller = new Signaller();
        _sse = new SseHub();
        _builder = new ViewFrameBuilder(client, _signaller, _sse);
        _bridge = new HttpControlBridge(_sse, OnCommand, httpPort);

        _tray = new NotifyIcon
        {
            Icon = AppIcon.Default ?? SystemIcons.Application,
            Text = "NavStream control dashboard",
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenDashboard();
        _tray.ContextMenuStrip = BuildTrayMenu();

        bool bound = _bridge.Start();
        if (bound)
        {
            // Open the dashboard web page in the default browser on launch. (Set NAVSTREAM_NO_BROWSER=1
            // to suppress the auto-open — used by automated tests / headless runs; the tray stays live.)
            if (Environment.GetEnvironmentVariable("NAVSTREAM_NO_BROWSER") != "1") OpenDashboard();
        }
        else
        {
            _tray.ShowBalloonTip(5000, "NavStream",
                "Web control panel unavailable (no local port). Tray menu still works.", ToolTipIcon.Warning);
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => OpenDashboard());
        menu.Items.Add("Restart Display", null, (_, _) =>
            _client.Send(new ControlCommand { Name = ControlCommands.RestartDisplay }));
        menu.Items.Add("Shutdown Displays", null, (_, _) => ShutdownDisplaysFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit Application", null, (_, _) => ExitApplication());
        return menu;
    }

    /// <summary>Open (or re-open) the browser control UI. Balloon-tips if the bridge is headless.</summary>
    private void OpenDashboard()
    {
        if (_bridge.IsRunning && _bridge.BaseUrl is { } url)
            BrowserLauncher.Launch(url);
        else
            _tray.ShowBalloonTip(4000, "NavStream", "Web control panel unavailable.", ToolTipIcon.Warning);
    }

    /// <summary>Tray "Shutdown Displays": confirm, then ask the supervisor to hard-stop + force-kill every
    /// render process while keeping this tray alive (the browser banner reflects it on the next tick).</summary>
    private void ShutdownDisplaysFromTray()
    {
        if (MessageBox.Show(
                "Shut down all displays now? This disconnects every stream and force-kills all NavStream "
                + "render processes (including any orphaned ones). The grid goes dark; this dashboard "
                + "stays open — switch 'Live video feed' back on to relaunch.",
                "NavStream — Shutdown Displays",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        if (!_signaller.SignalShutdownDisplays())
            _tray.ShowBalloonTip(4000, "NavStream", "Can't reach the supervisor to shut down displays.", ToolTipIcon.Warning);
    }

    /// <summary>
    /// Route a browser command. The supervisor-event verbs (driven by named events, not the render pipe)
    /// are handled here; everything else is a pipe <see cref="ControlCommands"/> verb forwarded verbatim to
    /// the render. Returns the channel's success so the browser can show "can't reach grid". Runs on an HTTP
    /// accept thread — every dependency it touches is thread-safe.
    /// </summary>
    private bool OnCommand(ControlCommand cmd)
    {
        switch (cmd.Name)
        {
            case "feedToggle":          // BoolValue: true = start the grid, false = stop it
                return _signaller.SignalGrid(cmd.BoolValue);
            case "shutdownDisplays":
                return _signaller.SignalShutdownDisplays();
            case "exitApplication":
                BeginInvokeOnTray(ExitApplication);   // marshal the teardown onto the UI thread
                return true;
            default:
                return _client.Send(cmd);
        }
    }

    /// <summary>Tear the whole app down: ask the render (pipe) + the supervisor (full-stop event), then end
    /// our message loop. Best-effort on both channels so it works even mid grid-restart. Lifted from the
    /// old form's ExitApplication.</summary>
    private void ExitApplication()
    {
        Logger.Log("Dashboard: Exit Application — signalling full stop.");
        _client.Send(new ControlCommand { Name = ControlCommands.FullStop }); // works if render is up
        _signaller.SignalFullStop();                                          // supervisor tears the rest down

        _tray.Visible = false;
        ExitThread();
    }

    /// <summary>Marshal an action onto the tray's UI thread (commands arrive on HTTP accept threads).</summary>
    private void BeginInvokeOnTray(Action action)
    {
        var strip = _tray.ContextMenuStrip;
        if (strip is not null && strip.IsHandleCreated)
        {
            try { strip.BeginInvoke(action); return; } catch { }
        }
        action();   // no UI handle yet — run inline (still safe; these calls don't touch UI controls)
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _bridge.Dispose(); } catch { }
            try { _builder.Dispose(); } catch { }
            try { _sse.Dispose(); } catch { }
            try { _signaller.Dispose(); } catch { }
            try { _tray.Visible = false; _tray.Dispose(); } catch { }
            try { _client.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}
