using System.Drawing;

namespace NavStream;

/// <summary>
/// Exact N-up grid math (spec §3 + dynamic-grid Tier 1). Deliberately NOT using TableLayoutPanel — its
/// padding/cell borders leave hairline gaps. Boundary arithmetic (integer division of the row/column
/// boundaries, not per-cell sizes) makes adjacent cells share an exact edge and the last cell reach
/// exactly width/height, so the rectangles tile the client area with zero gaps and zero overflow.
/// Pure and side-effect-free so it can be unit-checked without a window.
/// </summary>
internal static class GridLayout
{
    /// <summary>
    /// Returns exactly <paramref name="count"/> rectangles (clamped to [1,16]) tiling a w×h client area,
    /// row-major (left→right, top→bottom). Near-square layout: cols=⌈√n⌉, rows=⌈n/cols⌉; the first rows-1
    /// rows hold <c>cols</c> cells each and the last row holds the remainder stretched edge-to-edge.
    /// Guarantees: exactly <paramref name="count"/> rects, full coverage (Σ area == width*height), no
    /// overlap, all within [0,width]×[0,height]. Tile(4,w,h) reproduces <see cref="Quadrants"/> bit-for-bit.
    /// </summary>
    public static Rectangle[] Tile(int count, int width, int height)
    {
        if (count < 1) count = 1;
        if (count > 16) count = 16;
        if (width < 0) width = 0;
        if (height < 0) height = 0;

        int cols = (int)Math.Ceiling(Math.Sqrt(count));
        int rows = (int)Math.Ceiling((double)count / cols);

        var rects = new Rectangle[count];
        int idx = 0;
        for (int r = 0; r < rows; r++)
        {
            // Last row holds the remainder, stretched edge-to-edge; earlier rows are full width.
            int cellsInRow = (r < rows - 1) ? cols : count - (rows - 1) * cols;

            // Integer division of the boundaries (not the spans) absorbs the odd pixel exactly.
            int y0 = r * height / rows;
            int y1 = (r + 1) * height / rows;

            for (int c = 0; c < cellsInRow; c++)
            {
                int x0 = c * width / cellsInRow;
                int x1 = (c + 1) * width / cellsInRow;
                rects[idx++] = new Rectangle(x0, y0, x1 - x0, y1 - y0);
            }
        }
        return rects;
    }

    /// <summary>
    /// Returns exactly 4 rectangles (TL, TR, BL, BR order) tiling a w×h client area.
    /// Thin shim over <see cref="Tile"/>; Tile(4,w,h) is element-wise identical to the original 2×2 math.
    /// Guarantees: TL.W+TR.W == w, TL.H+BL.H == h, no gaps, no overflow.
    /// </summary>
    public static Rectangle[] Quadrants(int width, int height) => Tile(4, width, height);
}
