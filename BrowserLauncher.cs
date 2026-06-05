using System.Diagnostics;

namespace MpvGrid;

/// <summary>
/// Opens the browser-based control UI as an ordinary web page in the operator's default browser — a
/// normal tab, deliberately NOT a chromeless Edge "app mode" (<c>--app=…</c>) window. The dashboard
/// process stays a headless tray icon; the UI is just a page served on loopback that the operator can
/// pull up (and close) like any other web page. The shell routes the tokenized URL to whatever browser
/// is registered as default.
///
/// Always detached: the spawned browser is fully independent of this process, so closing the tab — or the
/// whole browser — never touches the tray, and the operator can reopen it any number of times from the
/// tray menu (or by navigating to the URL directly).
/// </summary>
internal static class BrowserLauncher
{
    /// <summary>Open the control UI at <paramref name="baseUrl"/> (e.g. <c>http://localhost:17653/</c>)
    /// carrying the per-launch <paramref name="token"/>, in the system default browser. Best-effort: logs
    /// and returns on any failure (the tray icon stays usable regardless).</summary>
    public static void Launch(string baseUrl, string token)
    {
        string url = $"{baseUrl}?token={token}";

        try
        {
            // UseShellExecute hands the URL to the registered default browser, which opens it as a
            // normal tab. No --app=, so there's no separate chromeless window to wrap the dashboard.
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Logger.Log($"Dashboard: opened control UI in the default browser at {baseUrl}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Dashboard: could not open the control UI in a browser: {ex.Message}");
        }
    }
}
