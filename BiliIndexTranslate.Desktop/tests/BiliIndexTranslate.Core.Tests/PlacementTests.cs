using BiliIndexTranslate.Core.Geometry;
using BiliIndexTranslate.Core.Selection;
using Xunit;

namespace BiliIndexTranslate.Core.Tests;

public class PlacementTests
{
    private static readonly MonitorArea Primary = new(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1032), 1.0, true, "primary");
    private static readonly PxInsets NoInsets = PxInsets.Uniform(0);

    private static AnchorRect Line(int left, int top, int width, int height = 20)
    {
        var r = PxRect.FromSize(left, top, width, height);
        return new AnchorRect(r, r, 1, height);
    }

    [Fact]
    public void SingleLine_IsCenteredAbove_WithGap()
    {
        var anchor = Line(800, 500, 200);
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary);

        Assert.Equal(PlacementSide.Above, p.Side);
        Assert.Equal(900 - 150, p.CardRect.Left);
        Assert.Equal(500 - 10 - 100, p.CardRect.Top);
    }

    [Fact]
    public void NoRoomAbove_FallsBelow()
    {
        var anchor = Line(800, 30, 200);
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary);

        Assert.Equal(PlacementSide.Below, p.Side);
        Assert.Equal(50 + 10, p.CardRect.Top);
    }

    [Fact]
    public void LeftEdge_ClampsIntoWorkArea()
    {
        var anchor = Line(5, 500, 40);
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary);
        Assert.Equal(8, p.CardRect.Left);
    }

    [Fact]
    public void RightEdge_ClampsIntoWorkArea()
    {
        var anchor = Line(1880, 500, 30);
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary);
        Assert.Equal(1920 - 8 - 300, p.CardRect.Left);
        Assert.True(p.WindowRect.Right <= 1920);
    }

    [Fact]
    public void BottomOfScreen_StaysAboveTaskbar()
    {
        var anchor = Line(800, 1000, 200, 20); // near the taskbar; room above
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary);
        Assert.Equal(PlacementSide.Above, p.Side);
        Assert.True(p.CardRect.Bottom <= 1032 - 8);
    }

    [Fact]
    public void NeitherSideFits_ClampsInsideWorkArea()
    {
        var tall = new PxRect(100, 40, 1800, 1000);
        var anchor = new AnchorRect(new PxRect(100, 40, 1800, 60), tall, 40, 20);
        var p = PopupPlacer.Place(anchor, 400, 300, NoInsets, Primary);
        Assert.True(p.CardRect.Top >= 8);
        Assert.True(p.CardRect.Bottom <= 1032 - 8);
    }

    [Fact]
    public void SecondaryMonitor_WithNegativeCoordinates_And_HigherDpi()
    {
        var left = new MonitorArea(new PxRect(-2560, -200, 0, 1240), new PxRect(-2560, -200, 0, 1200), 2.0, false, "left");
        var anchor = Line(-1500, 400, 200, 32);
        var p = PopupPlacer.Place(anchor, 600, 200, NoInsets, left);

        Assert.Equal(PlacementSide.Above, p.Side);
        Assert.Equal(-1400 - 300, p.CardRect.Left);
        Assert.Equal(400 - 20 - 200, p.CardRect.Top); // gap is 10 logical = 20 physical at 200 %
        Assert.True(p.CardRect.Left >= left.WorkArea.Left);
    }

    [Fact]
    public void MixedDpi_MaxCardSizeScalesWithMonitor()
    {
        var hi = new MonitorArea(new PxRect(0, 0, 3840, 2160), new PxRect(0, 0, 3840, 2100), 2.0, true, "4k");
        var (w, h) = PopupPlacer.MaxCardSize(hi);
        Assert.Equal(880, w);
        Assert.Equal((int)(2100 * 0.45), h);

        var small = new MonitorArea(new PxRect(0, 0, 400, 480), new PxRect(0, 0, 400, 440), 1.0, true, "tiny");
        var (w2, _) = PopupPlacer.MaxCardSize(small);
        Assert.Equal(400 - 16, w2);
    }

    [Fact]
    public void MultiLine_AnchorsAboveTopLine_CenteredOnFirstLine()
    {
        var lines = new List<PxRect>
        {
            PxRect.FromSize(100, 300, 600, 20),
            PxRect.FromSize(100, 324, 580, 20),
            PxRect.FromSize(100, 348, 200, 20),
        };
        var anchor = SelectionAnchor.Compute(lines)!;
        Assert.Equal(3, anchor.LineCount);
        Assert.Equal(20, anchor.LineHeight);
        Assert.Equal(300, anchor.FirstLine.Top);
        Assert.Equal(368, anchor.Union.Bottom);

        var p = PopupPlacer.Place(anchor, 400, 120, NoInsets, Primary);
        Assert.Equal(PlacementSide.Above, p.Side);
        Assert.Equal(400 - 200, p.CardRect.Left);
        Assert.Equal(300 - 10 - 120, p.CardRect.Top);
    }

    [Fact]
    public void Insets_ExpandWindowRect_NotCard()
    {
        var anchor = Line(800, 500, 200);
        var p = PopupPlacer.Place(anchor, 300, 100, PxInsets.Uniform(24), Primary);
        Assert.Equal(p.CardRect.Left - 24, p.WindowRect.Left);
        Assert.Equal(p.CardRect.Bottom + 24, p.WindowRect.Bottom);
    }

    [Fact]
    public void PreferredSide_IsHonoredWhenItFits()
    {
        var anchor = Line(800, 500, 200);
        var p = PopupPlacer.Place(anchor, 300, 100, NoInsets, Primary, PlacementSide.Below);
        Assert.Equal(PlacementSide.Below, p.Side);
    }
}

public class SelectionAnchorTests
{
    [Fact]
    public void EmptyAndDegenerateRects_AreDropped()
    {
        var anchor = SelectionAnchor.Compute(new[] { PxRect.Empty, new PxRect(10, 10, 10, 30), PxRect.FromSize(50, 50, 100, 18) });
        Assert.NotNull(anchor);
        Assert.Equal(1, anchor!.LineCount);
        Assert.Equal(50, anchor.FirstLine.Left);
    }

    [Fact]
    public void AllEmpty_ReturnsNull()
    {
        Assert.Null(SelectionAnchor.Compute(new[] { PxRect.Empty }));
        Assert.Null(SelectionAnchor.Compute(Array.Empty<PxRect>()));
    }

    [Fact]
    public void Clip_RemovesLinesOutsideWindow_SoAnchorTracksVisibleText()
    {
        var clip = new PxRect(0, 200, 1000, 600);
        var lines = new[] { PxRect.FromSize(10, 150, 500, 20), PxRect.FromSize(10, 210, 500, 20), PxRect.FromSize(10, 234, 500, 20) };
        var anchor = SelectionAnchor.Compute(lines, clip)!;
        Assert.Equal(2, anchor.LineCount);
        Assert.Equal(210, anchor.FirstLine.Top);
    }

    [Fact]
    public void FullyClipped_ReturnsNull()
    {
        var clip = new PxRect(0, 200, 1000, 600);
        Assert.Null(SelectionAnchor.Compute(new[] { PxRect.FromSize(10, 100, 500, 20) }, clip));
    }

    [Fact]
    public void UnorderedFragments_OnFirstLine_AreMerged()
    {
        var lines = new[] { PxRect.FromSize(300, 100, 100, 20), PxRect.FromSize(100, 124, 300, 20), PxRect.FromSize(100, 100, 190, 20) };
        var anchor = SelectionAnchor.Compute(lines)!;
        Assert.Equal(2, anchor.LineCount);
        Assert.Equal(100, anchor.FirstLine.Left);
        Assert.Equal(400, anchor.FirstLine.Right);
    }

    [Fact]
    public void FromUiaArray_ParsesQuads_AndIgnoresTrailingGarbage()
    {
        var rects = SelectionAnchor.FromUiaArray(new double[] { 10.4, 20.6, 100.2, 18.1, 5, 5, 5 });
        Assert.Single(rects);
        Assert.Equal(new PxRect(10, 20, 111, 39), rects[0]);
        Assert.Empty(SelectionAnchor.FromUiaArray(null));
    }
}

public class MonitorLocatorTests
{
    [Fact]
    public void PicksMonitorWithLargestOverlap_ThenNearest()
    {
        var a = new MonitorArea(new PxRect(0, 0, 1920, 1080), new PxRect(0, 0, 1920, 1040), 1.0, true, "a");
        var b = new MonitorArea(new PxRect(1920, 0, 3840, 1080), new PxRect(1920, 0, 3840, 1040), 1.5, false, "b");
        var loc = new StaticMonitorLocator(a, b);

        Assert.Equal("b", loc.FromRect(new PxRect(1900, 100, 2100, 120)).Name);
        Assert.Equal("a", loc.FromRect(new PxRect(1800, 100, 1950, 120)).Name);
        Assert.Equal("b", loc.FromPoint(new PxPoint(5000, 100)).Name);
        Assert.Equal("a", loc.FromPoint(new PxPoint(-50, -50)).Name);
    }
}

public class ReadingLayoutTests
{
    [Theory]
    [InlineData("hello", "你好", ReadingLayout.Compact)]
    [InlineData("serendipity", "机缘巧合", ReadingLayout.Compact)]
    [InlineData("hello", null, ReadingLayout.Compact)]
    [InlineData("The quick brown fox jumps over the lazy dog near the river bank.", null, ReadingLayout.Paragraph)]
    [InlineData("short", "这是一个相当长的翻译结果用于测试布局", ReadingLayout.Paragraph)]
    [InlineData("a\nb", "甲\n乙", ReadingLayout.Paragraph)]
    public void Classifies(string source, string? translation, ReadingLayout expected)
        => Assert.Equal(expected, ReadingLayoutClassifier.Classify(source, translation));
}
