using System.Drawing;
using System.Windows.Forms;

namespace NavStream;

/// <summary>
/// A minimal child control whose native window handle (<see cref="Control.Handle"/>) is the embed target
/// for one mpv handle — the per-feed <see cref="Mpv.MpvClient"/> sets it as mpv's <c>wid</c> before
/// <c>mpv_initialize</c>, exactly where the old <c>VideoView</c> sat (the surface swap, Phase 2).
/// mpv paints the video straight into this HWND via its <c>gpu</c> video output, so the control draws
/// nothing managed itself; the black background fills the cell until the first frame arrives.
/// </summary>
internal sealed class MpvHost : Control
{
    public MpvHost()
    {
        BackColor = Color.Black;
        TabStop = false;
    }
}
