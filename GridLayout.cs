using System.Drawing;

namespace MpvGrid;

/// <summary>
/// Exact 2×2 quadrant math (spec §3). Deliberately NOT using TableLayoutPanel — its
/// padding/cell borders leave hairline gaps. The right/bottom panels absorb the odd pixel so
/// the four rectangles tile the client area with zero gaps and zero overflow.
/// Pure and side-effect-free so it can be unit-checked without a window.
/// </summary>
internal static class GridLayout
{
    /// <summary>
    /// Returns exactly 4 rectangles (TL, TR, BL, BR order) tiling a w×h client area.
    /// Guarantees: TL.W+TR.W == w, TL.H+BL.H == h, no gaps, no overflow.
    /// </summary>
    public static Rectangle[] Quadrants(int width, int height)
    {
        if (width < 0) width = 0;
        if (height < 0) height = 0;

        int leftW = width / 2;
        int rightW = width - leftW;   // absorbs the odd horizontal pixel
        int topH = height / 2;
        int botH = height - topH;     // absorbs the odd vertical pixel

        return new[]
        {
            new Rectangle(0,     0,    leftW,  topH),  // top-left
            new Rectangle(leftW, 0,    rightW, topH),  // top-right
            new Rectangle(0,     topH, leftW,  botH),  // bottom-left
            new Rectangle(leftW, topH, rightW, botH),  // bottom-right
        };
    }
}
