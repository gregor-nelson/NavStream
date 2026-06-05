using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MpvGrid;

/// <summary>
/// Dark "control-room" visual palette for the operator UI, keyed to the InterMoor app icon
/// (tools/make-icon.ps1): a near-black neutral panel (#181A1F) with the brand blue / green / amber /
/// red accents from the 2x2 video-wall tiles. Pure-black ground, cool-neutral surfaces, a #409CFF
/// brand-blue accent (matching the overlay's feed badges and status LEDs), white text, and the
/// Barlow Condensed display face. These are the shared design tokens: the render overlay paints with
/// them and the browser control UI (web\style.css) mirrors them so both read as the same product.
///
/// Barlow Condensed is bundled as embedded .ttf resources (fonts\*.ttf) and loaded at startup, so nothing
/// needs to be installed and it survives the single-file publish. We register the bytes with BOTH font
/// engines: a process-wide <see cref="PrivateFontCollection"/> (GDI+, used by Graphics.DrawString) AND
/// GDI via AddFontMemResourceEx. The GDI registration is essential — <see cref="System.Windows.Forms.TextRenderer"/>
/// renders through GDI, which keeps a separate font table from GDI+ and would otherwise substitute Arial
/// for a face it only knows in GDI+.
/// </summary>
internal static class Theme
{
    // ---- neutral "control-room" surfaces (keyed to the InterMoor app-icon panel #181A1F) ----
    public static readonly Color Bg          = Hex("#000000"); // app ground (matches the overlay backdrop)
    public static readonly Color Surface     = Hex("#0E1116"); // panels
    public static readonly Color Surface2    = Hex("#161A21"); // inputs (the icon-panel tone)
    public static readonly Color Surface3    = Hex("#1F242E"); // raised
    public static readonly Color Outline     = Hex("#2E3139"); // outline
    public static readonly Color OutlineSoft = Hex("#1F2229"); // outline-soft
    public static readonly Color BezelLight  = Hex("#3A3D45"); // bezel-l
    public static readonly Color BezelDark   = Hex("#22252D"); // bezel-d

    // ---- InterMoor brand accent (blue, from the icon's blue tile + the overlay feed badges) ----
    public static readonly Color Accent      = Hex("#409CFF"); // primary accent / CTA / captions
    public static readonly Color AccentHi    = Hex("#5DAEFF"); // hover (lifted)
    public static readonly Color AccentDim   = Hex("#0070B8"); // pressed (overlay wordmark blue)

    // ---- status palette (shared with the overlay LEDs and the icon's accent tiles) ----
    public static readonly Color Green       = Hex("#26C778"); // healthy  (icon green tile)
    public static readonly Color Amber       = Hex("#F5A923"); // warn / stale (icon orange tile)
    public static readonly Color Red         = Hex("#E5484D"); // down / error (icon red tile)
    public static readonly Color Blue        = Hex("#409CFF"); // info (== Accent)
    public static readonly Color Gray        = Hex("#6A6E72"); // idle / unknown

    // Text colours: the design uses translucent white, but GDI text rendering is opaque, so these are
    // pre-blended against the black ground (0.92 / 0.60 / 0.38 white on #000).
    public static readonly Color TextHi  = Color.FromArgb(235, 235, 235); // --t-hi 0.92
    public static readonly Color TextMid = Color.FromArgb(153, 153, 153); // --t-md 0.60
    public static readonly Color TextLo  = Color.FromArgb(97, 97, 97);    // --t-lo 0.38

    // ---- fonts (Barlow Condensed, bundled) ----
    private static readonly PrivateFontCollection _collection = new();
    private static readonly Dictionary<string, FontFamily> _families = new(StringComparer.OrdinalIgnoreCase);

    static Theme() => LoadFonts();

    private static void LoadFonts()
    {
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            var data = new byte[s.Length];
            s.ReadExactly(data);
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                _collection.AddMemoryFont(handle.AddrOfPinnedObject(), data.Length); // feed GDI+ (DrawString)
                // Feed GDI too, so the by-name lookups TextRenderer/WinForms controls do at paint time
                // resolve the face instead of substituting Arial. Both engines copy the bytes, so the
                // pinned buffer can be freed immediately; the fonts stay private to this process and are
                // released automatically at exit (no RemoveFontMemResourceEx needed).
                uint installed = 0;
                AddFontMemResourceEx(handle.AddrOfPinnedObject(), (uint)data.Length, IntPtr.Zero, ref installed);
            }
            finally { handle.Free(); }
        }
        foreach (var fam in _collection.Families) _families[fam.Name] = fam;
    }

    /// <summary>Register an in-memory font with GDI for the current process (no install, not enumerable).
    /// Needed so GDI text rendering — every WinForms control and <see cref="System.Windows.Forms.TextRenderer"/> —
    /// can find the Barlow faces by name; the GDI+ <see cref="PrivateFontCollection"/> alone is invisible to GDI.</summary>
    [DllImport("gdi32.dll")]
    private static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

    /// <summary>Resolve a font by Barlow-Condensed weight, falling back gracefully if the bundle is missing.</summary>
    private static Font Make(string[] prefer, float size, FontStyle style)
    {
        foreach (var n in prefer)
            if (_families.TryGetValue(n, out var fam)) return new Font(fam, size, style, GraphicsUnit.Point);
        // Bundle not loaded (shouldn't happen) — try an installed copy, then a generic sans.
        foreach (var n in prefer)
            try { return new Font(n, size, style, GraphicsUnit.Point); } catch { /* not installed */ }
        return new Font("Segoe UI", size, style, GraphicsUnit.Point);
    }

    // Barlow Condensed is narrow, so sizes run a touch larger than the Segoe UI equivalents.
    public static Font Light(float size)    => Make(new[] { "Barlow Condensed Light", "Barlow Condensed" }, size, FontStyle.Regular);
    public static Font Regular(float size)  => Make(new[] { "Barlow Condensed" }, size, FontStyle.Regular);
    public static Font Medium(float size)   => Make(new[] { "Barlow Condensed Medium", "Barlow Condensed" }, size, FontStyle.Regular);
    public static Font SemiBold(float size) => Make(new[] { "Barlow Condensed SemiBold", "Barlow Condensed Medium", "Barlow Condensed" }, size, FontStyle.Regular);

    private static Color Hex(string hex)
    {
        var h = hex.TrimStart('#');
        return Color.FromArgb(
            Convert.ToInt32(h.Substring(0, 2), 16),
            Convert.ToInt32(h.Substring(2, 2), 16),
            Convert.ToInt32(h.Substring(4, 2), 16));
    }
}
