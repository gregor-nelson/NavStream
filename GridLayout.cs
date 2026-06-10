using System.Drawing;

namespace NavStream;

/// <summary>
/// A display's layout preset (multi-monitor): either the near-square auto tiling (<see cref="Tile"/>)
/// or a fixed cols×rows frame (<see cref="GridLayout.TileFixed"/>) where empty trailing cells stay black
/// and cells never resize as feeds come and go. Lives beside GridLayout so Config/GridForm/OverlayForm
/// share one parse + capacity definition and the grid and overlay can never disagree.
/// </summary>
internal readonly struct LayoutSpec : IEquatable<LayoutSpec>
{
    public bool IsAuto { get; }
    public int Cols { get; }
    public int Rows { get; }

    private LayoutSpec(bool isAuto, int cols, int rows)
    {
        IsAuto = isAuto;
        Cols = cols;
        Rows = rows;
    }

    public static LayoutSpec Auto { get; } = new(true, 0, 0);
    public static LayoutSpec Fixed(int cols, int rows) => new(false, cols, rows);

    /// <summary>How many feeds this layout can host: auto grows to the 16-feed cap; fixed is its frame.</summary>
    public int Capacity => IsAuto ? 16 : Cols * Rows;

    /// <summary>Accepts "auto" or "CxR" (case/whitespace tolerant, 1≤C,R≤8, C*R≤16). The parser accepts any
    /// valid CxR (forward-compatible) — the dashboard offers only its preset list.</summary>
    public static bool TryParse(string? s, out LayoutSpec spec)
    {
        spec = Auto;
        s = (s ?? "").Trim();
        if (s.Length == 0) return false;
        if (s.Equals("auto", StringComparison.OrdinalIgnoreCase)) return true;

        int sep = s.IndexOfAny(new[] { 'x', 'X' });
        if (sep <= 0 || sep >= s.Length - 1) return false;
        if (!int.TryParse(s[..sep].Trim(), out int cols)) return false;
        if (!int.TryParse(s[(sep + 1)..].Trim(), out int rows)) return false;
        if (cols < 1 || cols > 8 || rows < 1 || rows > 8 || cols * rows > 16) return false;

        spec = Fixed(cols, rows);
        return true;
    }

    /// <summary>Lenient parse: anything invalid falls back to <see cref="Auto"/>.</summary>
    public static LayoutSpec Parse(string? s) => TryParse(s, out var spec) ? spec : Auto;

    public override string ToString() => IsAuto ? "auto" : $"{Cols}x{Rows}";

    public bool Equals(LayoutSpec other) => IsAuto == other.IsAuto && Cols == other.Cols && Rows == other.Rows;
    public override bool Equals(object? obj) => obj is LayoutSpec other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(IsAuto, Cols, Rows);
}

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

    /// <summary>
    /// Fixed cols×rows frame (layout presets): same integer-boundary math as <see cref="Tile"/> but the
    /// frame never reflows — no last-row stretch, so fewer feeds than cells leaves the trailing cells
    /// unoccupied (the form's black background shows through). Returns min(count, cols*rows) rects,
    /// row-major; count ≤ 0 → empty array.
    /// </summary>
    public static Rectangle[] TileFixed(int count, int cols, int rows, int width, int height)
    {
        if (cols < 1) cols = 1;
        if (rows < 1) rows = 1;
        if (width < 0) width = 0;
        if (height < 0) height = 0;

        int n = Math.Min(count, cols * rows);
        if (n <= 0) return Array.Empty<Rectangle>();

        var rects = new Rectangle[n];
        for (int i = 0; i < n; i++)
        {
            int r = i / cols, c = i % cols;
            int y0 = r * height / rows;
            int y1 = (r + 1) * height / rows;
            int x0 = c * width / cols;
            int x1 = (c + 1) * width / cols;
            rects[i] = new Rectangle(x0, y0, x1 - x0, y1 - y0);
        }
        return rects;
    }

    /// <summary>
    /// Single dispatch point for a display's tiling so the grid and overlay can never disagree:
    /// count ≤ 0 → empty array; auto → <see cref="Tile"/>; fixed → <see cref="TileFixed"/>.
    /// </summary>
    public static Rectangle[] Compute(LayoutSpec spec, int count, int width, int height)
    {
        if (count <= 0) return Array.Empty<Rectangle>();
        return spec.IsAuto
            ? Tile(count, width, height)
            : TileFixed(count, spec.Cols, spec.Rows, width, height);
    }
}
