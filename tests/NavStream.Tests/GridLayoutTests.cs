using System.Drawing;
using Xunit;

namespace NavStream.Tests;

public class LayoutSpecTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("  auto  ")]
    public void TryParse_Auto(string s)
    {
        Assert.True(LayoutSpec.TryParse(s, out var spec));
        Assert.True(spec.IsAuto);
        Assert.Equal(16, spec.Capacity);
        Assert.Equal("auto", spec.ToString());
    }

    [Theory]
    [InlineData("2x2", 2, 2)]
    [InlineData("3X2", 3, 2)]
    [InlineData(" 1 x 2 ", 1, 2)]
    [InlineData("8x2", 8, 2)]   // 16 cells = exactly the cap
    [InlineData("4x4", 4, 4)]
    public void TryParse_Fixed(string s, int cols, int rows)
    {
        Assert.True(LayoutSpec.TryParse(s, out var spec));
        Assert.False(spec.IsAuto);
        Assert.Equal(cols, spec.Cols);
        Assert.Equal(rows, spec.Rows);
        Assert.Equal(cols * rows, spec.Capacity);
        Assert.Equal($"{cols}x{rows}", spec.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x2")]
    [InlineData("2x")]
    [InlineData("axb")]
    [InlineData("0x2")]
    [InlineData("9x1")]    // cols > 8
    [InlineData("1x9")]    // rows > 8
    [InlineData("5x4")]    // 20 cells > 16 cap
    [InlineData("2x2x2")]
    [InlineData("-1x2")]
    public void TryParse_Invalid(string? s)
    {
        Assert.False(LayoutSpec.TryParse(s, out _));
        Assert.True(LayoutSpec.Parse(s).IsAuto);   // lenient parse falls back to Auto
    }

    [Fact]
    public void Equality_OverAllThreeFields()
    {
        Assert.Equal(LayoutSpec.Parse("2x2"), LayoutSpec.Fixed(2, 2));
        Assert.NotEqual(LayoutSpec.Fixed(2, 1), LayoutSpec.Fixed(1, 2));
        Assert.Equal(LayoutSpec.Auto, LayoutSpec.Parse("garbage"));
    }
}

public class GridLayoutTests
{
    /// <summary>Σ area == w*h, no pairwise overlap, every rect within bounds — the tiling invariants.</summary>
    private static void AssertTiles(Rectangle[] rects, int width, int height, bool fullCoverage)
    {
        long area = 0;
        var bounds = new Rectangle(0, 0, width, height);
        for (int i = 0; i < rects.Length; i++)
        {
            Assert.True(bounds.Contains(rects[i]), $"rect {i} {rects[i]} escapes {bounds}");
            area += (long)rects[i].Width * rects[i].Height;
            for (int j = i + 1; j < rects.Length; j++)
                Assert.False(rects[i].IntersectsWith(rects[j]), $"rects {i} and {j} overlap");
        }
        if (fullCoverage)
            Assert.Equal((long)width * height, area);
    }

    public static TheoryData<int> Counts1To16()
    {
        var data = new TheoryData<int>();
        for (int n = 1; n <= 16; n++) data.Add(n);
        return data;
    }

    [Theory]
    [MemberData(nameof(Counts1To16))]
    public void Tile_FullCoverage_EvenAndOddSizes(int n)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (1919, 1079), (1366, 768) })
        {
            var rects = GridLayout.Tile(n, w, h);
            Assert.Equal(n, rects.Length);
            AssertTiles(rects, w, h, fullCoverage: true);
        }
    }

    [Fact]
    public void Tile_ClampsCountTo1And16()
    {
        Assert.Single(GridLayout.Tile(0, 100, 100));
        Assert.Single(GridLayout.Tile(-5, 100, 100));
        Assert.Equal(16, GridLayout.Tile(99, 100, 100).Length);
    }

    [Fact]
    public void Tile4_ReproducesQuadrants()
    {
        Assert.Equal(GridLayout.Quadrants(1919, 1079), GridLayout.Tile(4, 1919, 1079));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TileFixed_NonPositiveCount_Empty(int count)
    {
        Assert.Empty(GridLayout.TileFixed(count, 2, 2, 1920, 1080));
    }

    [Fact]
    public void TileFixed_FullFrame_CoversExactly()
    {
        foreach (var (w, h) in new[] { (1920, 1080), (1919, 1079) })
        {
            var rects = GridLayout.TileFixed(6, 3, 2, w, h);
            Assert.Equal(6, rects.Length);
            AssertTiles(rects, w, h, fullCoverage: true);
        }
    }

    [Fact]
    public void TileFixed_ClampsCountToCapacity()
    {
        Assert.Equal(4, GridLayout.TileFixed(9, 2, 2, 1920, 1080).Length);
    }

    [Fact]
    public void TileFixed_Underfill_NoReflow()
    {
        // The fixed frame never reflows: fewer feeds = the row-major prefix of the full frame's cells,
        // identical geometry (the trailing cells just stay unoccupied / black).
        var full = GridLayout.TileFixed(6, 3, 2, 1919, 1079);
        var part = GridLayout.TileFixed(4, 3, 2, 1919, 1079);
        Assert.Equal(4, part.Length);
        for (int i = 0; i < part.Length; i++)
            Assert.Equal(full[i], part[i]);
        AssertTiles(part, 1919, 1079, fullCoverage: false);
    }

    [Fact]
    public void TileFixed_RowMajorOrder()
    {
        var rects = GridLayout.TileFixed(4, 2, 2, 1000, 1000);
        Assert.Equal(new Point(0, 0), rects[0].Location);       // TL
        Assert.Equal(new Point(500, 0), rects[1].Location);     // TR
        Assert.Equal(new Point(0, 500), rects[2].Location);     // BL
        Assert.Equal(new Point(500, 500), rects[3].Location);   // BR
    }

    [Fact]
    public void Compute_Dispatch()
    {
        Assert.Empty(GridLayout.Compute(LayoutSpec.Auto, 0, 1920, 1080));
        Assert.Empty(GridLayout.Compute(LayoutSpec.Fixed(2, 2), -1, 1920, 1080));
        Assert.Equal(GridLayout.Tile(5, 1920, 1080), GridLayout.Compute(LayoutSpec.Auto, 5, 1920, 1080));
        Assert.Equal(GridLayout.TileFixed(3, 2, 2, 1920, 1080), GridLayout.Compute(LayoutSpec.Fixed(2, 2), 3, 1920, 1080));
    }
}
