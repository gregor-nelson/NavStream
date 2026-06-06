using System.Drawing;
using System.Reflection;

namespace NavStream;

/// <summary>
/// The InterMoor corporate logo, loaded once from the embedded <c>assets\logo.png</c> resource and painted
/// as a subtle bottom-left watermark by <see cref="OverlayForm"/>. The source artwork's white background was
/// pre-stripped to straight (un-premultiplied) alpha at build-prep time, so it composites cleanly over live
/// video. Embedded (not loaded from disk) so it ships inside the single-file exe, matching <see cref="AppIcon"/>.
/// </summary>
internal static class LogoImage
{
    private static readonly Image? _image = Load();

    /// <summary>The brand logo, or <c>null</c> if the resource was unavailable (the watermark is then skipped).</summary>
    public static Image? Default => _image;

    private static Image? Load()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            // LogicalName set in the .csproj -> "NavStream.logo.png".
            using var stream = asm.GetManifestResourceStream("NavStream.logo.png");
            if (stream is null) return null;
            // Image.FromStream keeps the stream alive for the image's lifetime; copy into a standalone
            // Bitmap so the stream can be disposed immediately and the pixels stay owned by us.
            using var loaded = Image.FromStream(stream);
            return new Bitmap(loaded);
        }
        catch
        {
            return null;
        }
    }
}
