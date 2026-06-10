using Xunit;

namespace NavStream.Tests;

/// <summary>
/// Normalize's multi-monitor partition reconciliation (NormalizeDisplays rules 1–7) + GetDisplayPartition.
/// Built around the rule numbering in the multi-monitor plan: canonicalize each entry, synthesize a legacy
/// display, shrink Σ Cells from the tail / grow the last display, mirror the legacy Monitor key.
/// </summary>
public class ConfigDisplaysTests
{
    private static Config Cfg(int streamCount, params DisplayConfig[] displays)
    {
        var c = new Config
        {
            Streams = Enumerable.Range(0, streamCount).Select(i => $"rtsp://cam{i}/h264").ToList(),
            Displays = displays.Length == 0 ? null : displays.ToList(),
        };
        c.Normalize();
        return c;
    }

    // ---- rule 3: legacy file (no Displays key) synthesizes one display from the Monitor key ----

    [Fact]
    public void LegacyFile_SynthesizesOneAutoDisplay()
    {
        var c = new Config { Monitor = 1, Streams = new() { "rtsp://a", "rtsp://b", "rtsp://c" } };
        c.Normalize();

        var d = Assert.Single(c.Displays!);
        Assert.Equal(1, d.Monitor);
        Assert.Equal("auto", d.Layout);
        Assert.Equal(3, d.Cells);
        Assert.Equal(1, c.Monitor);   // legacy key untouched
    }

    [Fact]
    public void ZeroStreams_StillOneDisplayWithOneCell()
    {
        var c = new Config { Streams = new() };
        c.Normalize();

        var d = Assert.Single(c.Displays!);
        Assert.Equal(1, d.Cells);   // N clamps to 1 (existing 0-stream behavior: 1 host, 0 feeds)
    }

    // ---- rule 2: per-entry canonicalization ----

    [Fact]
    public void GarbageEntry_IsCanonicalized()
    {
        var c = Cfg(4, new DisplayConfig { Monitor = -3, Layout = "9x9", Cells = 99 });

        var d = Assert.Single(c.Displays!);
        Assert.Equal(0, d.Monitor);          // negative → 0
        Assert.Equal("auto", d.Layout);      // invalid spec → auto
        Assert.Equal(4, d.Cells);            // 99 → capacity clamp, then Σ reconciles to the stream count
    }

    [Fact]
    public void Layout_IsCanonicalizedToLowercaseForm()
    {
        var c = Cfg(4, new DisplayConfig { Monitor = 0, Layout = " 2 X 2 ", Cells = 4 });
        Assert.Equal("2x2", c.Displays![0].Layout);
    }

    [Fact]
    public void Cells_ClampedToLayoutCapacity()
    {
        var c = Cfg(2, new DisplayConfig { Monitor = 0, Layout = "1x1", Cells = 5 });
        Assert.Equal(1, c.Displays![0].Cells);   // 1x1 capacity is 1
    }

    // ---- rule 1: display cap ----

    [Fact]
    public void MoreThanEightDisplays_Truncated()
    {
        var many = Enumerable.Range(0, 10)
            .Select(i => new DisplayConfig { Monitor = i, Layout = "1x1", Cells = 1 })
            .ToArray();
        var c = Cfg(16, many);
        Assert.Equal(8, c.Displays!.Count);
    }

    // ---- rule 5: Σ Cells > N shrinks from the tail, dropping emptied trailing displays ----

    [Fact]
    public void SumOverCount_TailDisplayShrinks()
    {
        var c = Cfg(6,
            new DisplayConfig { Monitor = 0, Layout = "2x2", Cells = 4 },
            new DisplayConfig { Monitor = 1, Layout = "2x2", Cells = 4 });

        Assert.Equal(2, c.Displays!.Count);
        Assert.Equal(4, c.Displays[0].Cells);
        Assert.Equal(2, c.Displays[1].Cells);
    }

    [Fact]
    public void SumOverCount_EmptiedTrailingDisplayIsDropped()
    {
        var c = Cfg(4,
            new DisplayConfig { Monitor = 0, Layout = "2x2", Cells = 4 },
            new DisplayConfig { Monitor = 1, Layout = "2x2", Cells = 4 });

        var d = Assert.Single(c.Displays!);
        Assert.Equal(4, d.Cells);
        Assert.Equal(0, d.Monitor);
    }

    [Fact]
    public void ShrinkAcrossMultipleTrailingDisplays()
    {
        var c = Cfg(3,
            new DisplayConfig { Monitor = 0, Layout = "2x2", Cells = 4 },
            new DisplayConfig { Monitor = 1, Layout = "1x2", Cells = 2 },
            new DisplayConfig { Monitor = 2, Layout = "1x2", Cells = 2 });

        var d = Assert.Single(c.Displays!);
        Assert.Equal(3, d.Cells);
    }

    // ---- rule 6: Σ Cells < N grows the LAST display up to its capacity; residual stays unassigned ----

    [Fact]
    public void SumUnderCount_LastDisplayGrows()
    {
        var c = Cfg(6,
            new DisplayConfig { Monitor = 0, Layout = "auto", Cells = 2 },
            new DisplayConfig { Monitor = 1, Layout = "auto", Cells = 2 });

        Assert.Equal(2, c.Displays![0].Cells);
        Assert.Equal(4, c.Displays[1].Cells);   // grew by the shortfall — auto has headroom
    }

    [Fact]
    public void SumUnderCount_GrowthCapsAtCapacity_ResidualUnassigned()
    {
        var c = Cfg(6,
            new DisplayConfig { Monitor = 0, Layout = "2x2", Cells = 2 },
            new DisplayConfig { Monitor = 1, Layout = "1x2", Cells = 1 });

        Assert.Equal(2, c.Displays![0].Cells);
        Assert.Equal(2, c.Displays[1].Cells);                       // 1x2 caps at 2
        int assigned = c.Displays.Sum(d => d.Cells);
        Assert.Equal(2, c.Streams.Count - assigned);                // 2 residual streams stay configured…
        Assert.Equal(6, c.Streams.Count);                           // …never deleted to fit the layout
    }

    // ---- rule 7: legacy Monitor key mirrors Displays[0] ----

    [Fact]
    public void MonitorKey_MirrorsFirstDisplay()
    {
        var c = Cfg(4, new DisplayConfig { Monitor = 2, Layout = "auto", Cells = 4 });
        Assert.Equal(2, c.Monitor);
    }

    // ---- idempotence: a second Normalize must change nothing ----

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var c = Cfg(6,
            new DisplayConfig { Monitor = 0, Layout = "9x9", Cells = 99 },
            new DisplayConfig { Monitor = 1, Layout = "1x2", Cells = 2 });

        var before = c.Displays!.Select(d => (d.Monitor, d.Layout, d.Cells)).ToList();
        c.Normalize();
        var after = c.Displays!.Select(d => (d.Monitor, d.Layout, d.Cells)).ToList();
        Assert.Equal(before, after);
    }

    // ---- GetDisplayPartition: running-sum offsets over the flat stream list ----

    [Fact]
    public void Partition_RunningSumOffsets()
    {
        var c = Cfg(6,
            new DisplayConfig { Monitor = 0, Layout = "2x2", Cells = 4 },
            new DisplayConfig { Monitor = 1, Layout = "1x2", Cells = 2 });

        var parts = c.GetDisplayPartition();
        Assert.Equal(2, parts.Count);
        Assert.Equal((0, 4), (parts[0].Offset, parts[0].Count));
        Assert.Equal((4, 2), (parts[1].Offset, parts[1].Count));
    }

    [Fact]
    public void StreamsCapAt16_PartitionMatches()
    {
        var c = Cfg(20);   // Normalize takes 16
        Assert.Equal(16, c.Streams.Count);
        var d = Assert.Single(c.Displays!);
        Assert.Equal(16, d.Cells);
    }
}
