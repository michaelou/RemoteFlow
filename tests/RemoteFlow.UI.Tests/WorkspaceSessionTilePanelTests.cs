using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using RemoteFlow.UI.Views.Terminal;
using Xunit;

namespace RemoteFlow.UI.Tests;

/// <summary>
/// The rule the workspace grid follows: as many rows as the column limit demands, and then the tiles spread
/// evenly across them. Four sessions under a limit of three are two rows of two — not three tiles and a lone
/// one beside two empty cells, which is the layout this panel exists to avoid.
/// </summary>
public sealed class WorkspaceSessionTilePanelTests
{
    [Theory]
    [InlineData(1, 3, "1")]
    [InlineData(2, 3, "2")]
    [InlineData(3, 3, "3")]
    [InlineData(4, 3, "2,2")]
    [InlineData(5, 3, "3,2")]
    [InlineData(6, 3, "3,3")]
    [InlineData(7, 3, "3,2,2")]
    [InlineData(4, 2, "2,2")]
    [InlineData(5, 2, "2,2,1")]
    [InlineData(4, 1, "1,1,1,1")]
    [InlineData(10, 4, "4,3,3")]
    [InlineData(0, 3, "")]
    public void TilesSpreadEvenlyAcrossAsFewRowsAsTheColumnLimitAllows(
        int count,
        int maxColumns,
        string expected)
    {
        var rows = WorkspaceSessionTilePanel.RowCounts(count, maxColumns);

        Assert.Equal(expected, string.Join(',', rows));
        Assert.Equal(count, rows.Sum());
    }

    [AvaloniaFact]
    public void ASingleVisibleTileTakesTheWholeArea()
    {
        var tiles = Arrange(visible: 1, maxColumns: 3, spacing: 6);

        Assert.Equal(new Rect(0, 0, 900, 600), tiles[0]);
    }

    /// <summary>
    /// Four tiles under a limit of three: two rows of two, each tile half the width and half the height, with
    /// the gutter taken out of the middle rather than off the end.
    /// </summary>
    [AvaloniaFact]
    public void FourTilesUnderALimitOfThreeFormTwoRowsOfTwo()
    {
        var tiles = Arrange(visible: 4, maxColumns: 3, spacing: 6);

        Assert.Equal(new Rect(0, 0, 447, 297), tiles[0]);
        Assert.Equal(new Rect(453, 0, 447, 297), tiles[1]);
        Assert.Equal(new Rect(0, 303, 447, 297), tiles[2]);
        Assert.Equal(new Rect(453, 303, 447, 297), tiles[3]);
    }

    /// <summary>Rounding is taken from the edges, not from each tile, so neighbours share an edge exactly. A
    /// native remote desktop window sits at whole pixels and shows accumulated drift as a visible offset.</summary>
    [AvaloniaFact]
    public void RowsOfThreeOverAnAwkwardWidthLeaveNoSeamsOrOverlaps()
    {
        var tiles = Arrange(visible: 3, maxColumns: 3, spacing: 5, width: 1001);

        Assert.Equal(0, tiles[0].Left);
        Assert.Equal(tiles[0].Right + 5, tiles[1].Left);
        Assert.Equal(tiles[1].Right + 5, tiles[2].Left);
        Assert.Equal(1001, tiles[2].Right);
    }

    /// <summary>A hidden session still exists, still owns its content and is still a child of the panel — it
    /// just may not hold a cell, or the grid would keep a gap for every session the user is not watching.</summary>
    [AvaloniaFact]
    public void AHiddenTileKeepsNoCell()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3 };
        var hidden = new Border { IsVisible = false };
        var visible = new Border();
        panel.Children.Add(hidden);
        panel.Children.Add(visible);

        Layout(panel, 900, 600);

        Assert.Equal(new Rect(0, 0, 900, 600), visible.Bounds);
        Assert.Equal(default, hidden.Bounds);
    }

    /// <summary>
    /// A session that is not tiled still gets the whole area, the way a background tab does: its content
    /// stays realized and attached, and it is the content that hides itself. An embedded remote desktop only
    /// ever has one native window, so nothing may be built again on the way back into view.
    /// </summary>
    [AvaloniaFact]
    public void ASessionThatIsNotTiledStillGetsTheWholeArea()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3, TileSpacing = 6 };
        var shown = new Border();
        var offScreen = new Border();
        WorkspaceSessionTilePanel.SetIsTileShown(offScreen, false);
        panel.Children.Add(shown);
        panel.Children.Add(offScreen);

        Layout(panel, 900, 600);

        Assert.Equal(new Rect(0, 0, 900, 600), shown.Bounds);
        Assert.Equal(new Rect(0, 0, 900, 600), offScreen.Bounds);
    }

    /// <summary>
    /// The tile being shown paints and answers a hit test above every session that holds no cell.
    /// </summary>
    /// <remarks>
    /// A container that is not a tile still covers the whole area, and it always paints its own tile chrome —
    /// only its content honours <c>IsContentVisible</c>. Without a z-order the containers stack in list order,
    /// so the last session buried every other one: in the tab layout the selected terminal was drawn over by
    /// an unselected session's opaque chrome, and clicks landed on it, unless the last tab happened to be the
    /// one selected. That is why only the last terminal worked.
    /// </remarks>
    [AvaloniaFact]
    public void TheShownTileSitsAboveEverySessionThatHoldsNoCell()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3, TileSpacing = 6 };
        var first = new Border();
        var shown = new Border();
        var last = new Border();
        WorkspaceSessionTilePanel.SetIsTileShown(first, false);
        WorkspaceSessionTilePanel.SetIsTileShown(last, false);
        panel.Children.Add(first);
        panel.Children.Add(shown);
        panel.Children.Add(last);

        Layout(panel, 900, 600);

        Assert.True(shown.ZIndex > first.ZIndex);
        Assert.True(shown.ZIndex > last.ZIndex);
    }

    /// <summary>Every tile of a grid is equal, so none of them may cover a neighbour.</summary>
    [AvaloniaFact]
    public void EveryTileOfAGridSharesOneZOrder()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3, TileSpacing = 6 };
        var tiles = new[] { new Border(), new Border(), new Border() };
        foreach (var tile in tiles)
        {
            panel.Children.Add(tile);
        }

        Layout(panel, 900, 600);

        _ = Assert.Single(tiles.Select(tile => tile.ZIndex).Distinct());
    }

    [AvaloniaFact]
    public void TilingASessionThatWasOffScreenResizesTheRest()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3 };
        var first = new Border();
        var second = new Border();
        WorkspaceSessionTilePanel.SetIsTileShown(second, false);
        panel.Children.Add(first);
        panel.Children.Add(second);
        Layout(panel, 900, 600);
        Assert.Equal(900, first.Bounds.Width);

        WorkspaceSessionTilePanel.SetIsTileShown(second, true);

        Assert.False(panel.IsMeasureValid);
        Layout(panel, 900, 600);
        Assert.Equal(450, first.Bounds.Width);
        Assert.Equal(450, second.Bounds.Width);
    }

    /// <summary>
    /// Showing or hiding a tile changes the size of every other tile, and a child's own invalidation does not
    /// reach the panel when its desired size has not changed. Without this the tab layout would keep showing
    /// the previous session's tile after another was selected.
    /// </summary>
    [AvaloniaFact]
    public void ShowingATileInvalidatesTheLayoutOfTheRest()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 3 };
        var first = new Border();
        var second = new Border { IsVisible = false };
        panel.Children.Add(first);
        panel.Children.Add(second);
        Layout(panel, 900, 600);
        Assert.True(panel.IsMeasureValid);

        second.IsVisible = true;

        Assert.False(panel.IsMeasureValid);
        Layout(panel, 900, 600);
        Assert.Equal(450, first.Bounds.Width);
        Assert.Equal(450, second.Bounds.Width);
    }

    [AvaloniaFact]
    public void AColumnLimitBelowOneIsStillOneColumn()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 0 };

        Assert.Equal(1, panel.MaxColumns);
    }

    /// <summary>Measure may not answer with an infinite size, whatever it was asked with.</summary>
    [AvaloniaFact]
    public void AnUnconstrainedMeasureReturnsAFiniteSize()
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = 2 };
        panel.Children.Add(new Border { Width = 100, Height = 50 });
        panel.Children.Add(new Border { Width = 100, Height = 50 });

        panel.Measure(Size.Infinity);

        Assert.Equal(new Size(200, 50), panel.DesiredSize);
    }

    private static Rect[] Arrange(
        int visible,
        int maxColumns,
        double spacing,
        double width = 900,
        double height = 600)
    {
        var panel = new WorkspaceSessionTilePanel { MaxColumns = maxColumns, TileSpacing = spacing };
        var tiles = new List<Border>();
        for (var index = 0; index < visible; index++)
        {
            var tile = new Border();
            tiles.Add(tile);
            panel.Children.Add(tile);
        }

        Layout(panel, width, height);
        return [.. tiles.Select(tile => tile.Bounds)];
    }

    private static void Layout(Control panel, double width, double height)
    {
        panel.Measure(new Size(width, height));
        panel.Arrange(new Rect(0, 0, width, height));
    }
}
