using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MpvGrid;

/// <summary>
/// Crash-safety plumbing (spec §8). Suppresses the Windows Error Reporting dialog (so an
/// unattended box never hangs on a crash) and routes unhandled exceptions to the log before
/// exiting non-zero, so the supervisor restarts the render app cleanly.
/// </summary>
internal static class CrashGuards
{
    [Flags]
    private enum ErrorModes : uint
    {
        SEM_FAILCRITICALERRORS = 0x0001,
        SEM_NOGPFAULTERRORBOX = 0x0002,
        SEM_NOOPENFILEERRORBOX = 0x8000,
    }

    [DllImport("kernel32.dll")]
    private static extern ErrorModes SetErrorMode(ErrorModes mode);

    /// <summary>Stop Windows from popping a crash/critical-error dialog (applies to native faults too).</summary>
    public static void SuppressWerDialog()
    {
        try
        {
            SetErrorMode(ErrorModes.SEM_FAILCRITICALERRORS |
                         ErrorModes.SEM_NOGPFAULTERRORBOX |
                         ErrorModes.SEM_NOOPENFILEERRORBOX);
        }
        catch { /* best effort */ }
    }

    /// <summary>Install managed unhandled-exception handlers for the render app (call before Application.Run).</summary>
    public static void InstallRenderHandlers()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        Application.ThreadException += (_, e) =>
        {
            Logger.Log($"Render: UNHANDLED UI exception: {e.Exception}");
            Environment.Exit(Constants.ExitCrash);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Log($"Render: UNHANDLED domain exception: {e.ExceptionObject}");
            Environment.Exit(Constants.ExitCrash);
        };
    }
}
