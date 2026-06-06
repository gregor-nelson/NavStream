using System.Drawing;
using System.Reflection;

namespace NavStream;

/// <summary>
/// The application icon (a 2x2 "video wall" matching the 4-feed grid), loaded once from the
/// <c>app.ico</c> embedded resource. Used for the tray <see cref="System.Windows.Forms.NotifyIcon"/>
/// and the app windows. Embedded (not <c>ExtractAssociatedIcon</c>) so it works under single-file
/// publish, where the exe is extracted to a temp path the icon API can't reliably read.
/// </summary>
internal static class AppIcon
{
    private static readonly Icon? _icon = Load();

    /// <summary>The app icon, or <c>null</c> if the resource was somehow unavailable (callers fall
    /// back to the Windows default).</summary>
    public static Icon? Default => _icon;

    private static Icon? Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // RootNamespace ("NavStream") + file name -> "NavStream.app.ico".
            using var stream = asm.GetManifestResourceStream("NavStream.app.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch
        {
            return null;
        }
    }
}
