using System.Diagnostics;

namespace MpvGrid;

/// <summary>
/// Opens the browser-based control UI as an ordinary web page in the operator's default browser — a
/// normal tab, deliberately NOT a chromeless Edge "app mode" (<c>--app=…</c>) window. The dashboard
/// process stays a headless tray icon; the UI is just a page served over HTTP that the operator can
/// pull up (and close) like any other web page. The URL is opened in whatever browser is registered as
/// default (via explorer.exe, so it runs at the user's normal privilege — see <see cref="Launch"/>).
///
/// Always detached: the spawned browser is fully independent of this process, so closing the tab — or the
/// whole browser — never touches the tray, and the operator can reopen it any number of times from the
/// tray menu (or by navigating to the URL directly).
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>Open the control UI at <paramref name="url"/> (e.g. <c>http://localhost:8080/</c>) in the
    /// system default browser. Best-effort: logs and returns on any failure (the tray icon stays usable
    /// regardless).
    ///
    /// We launch through <c>explorer.exe</c> rather than ShellExecute-ing the URL directly: this process runs
    /// elevated (app.manifest), and a direct shell-open would start the browser elevated too — which Chrome
    /// refuses to run as, and which wouldn't share the operator's normal (medium-IL) browser session. Handing
    /// the URL to explorer (already running at the user's level) opens it in an ordinary, non-elevated tab.</summary>
    public static void Launch(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", url) { UseShellExecute = false });
            Logger.Log($"Dashboard: opened control UI in the default browser at {url}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Dashboard: could not open the control UI in a browser: {ex.Message}");
        }
    }
}
