using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace RemoteFlow.UI.Views.Terminal;

/// <summary>
/// Lays session tiles out in balanced rows, and gives a single visible tile the whole area.
/// </summary>
/// <remarks>
/// One panel serves both workspace layouts: the tab layout is this grid with one visible child. That is
/// deliberate — swapping the <see cref="ItemsControl.ItemsPanel" /> to change layout would rebuild the item
/// containers, and an embedded remote desktop cannot survive being re-hosted.
/// </remarks>
public sealed class WorkspaceSessionTilePanel : Panel
{
    public static readonly StyledProperty<int> MaxColumnsProperty =
        AvaloniaProperty.Register<WorkspaceSessionTilePanel, int>(
            nameof(MaxColumns),
            defaultValue: 1,
            coerce: static (_, value) => Math.Max(1, value));

    public static readonly StyledProperty<double> TileSpacingProperty =
        AvaloniaProperty.Register<WorkspaceSessionTilePanel, double>(nameof(TileSpacing));

    /// <summary>
    /// Whether this child holds a cell of its own.
    /// </summary>
    /// <remarks>
    /// A child that does not is still measured and arranged with the whole area, because its content stays
    /// realized and attached whether the user is looking at it or not — that is what lets a remote desktop
    /// keep the one native window it can ever have. Its own content is what hides it.
    /// </remarks>
    public static readonly AttachedProperty<bool> IsTileShownProperty =
        AvaloniaProperty.RegisterAttached<WorkspaceSessionTilePanel, Control, bool>(
            "IsTileShown",
            defaultValue: true);

    /// <summary>
    /// A child that holds no cell is still arranged over the whole area, so in the tab layout every session's
    /// container covers the one the user is looking at. A container always paints its own tile chrome and
    /// always answers a hit test — only its content honours <c>IsContentVisible</c> — so without a z-order the
    /// last session in the list buries every other one: the selected terminal was neither drawn nor clickable
    /// unless it happened to be the last tab. The tile being shown is therefore lifted above the rest.
    /// </summary>
    private const int _tileZIndex = 1;

    private const int _hiddenZIndex = 0;

    private readonly HashSet<Control> _observedChildren = [];

    static WorkspaceSessionTilePanel()
    {
        AffectsMeasure<WorkspaceSessionTilePanel>(MaxColumnsProperty, TileSpacingProperty);
        AffectsParentMeasure<WorkspaceSessionTilePanel>(IsTileShownProperty);
    }

    public WorkspaceSessionTilePanel()
    {
        Children.CollectionChanged += OnChildrenChanged;
    }

    /// <summary>How many tiles a row may hold before the next one starts a new row. Never below one.</summary>
    public int MaxColumns
    {
        get => GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    /// <summary>The gutter between neighbouring tiles.</summary>
    public double TileSpacing
    {
        get => GetValue(TileSpacingProperty);
        set => SetValue(TileSpacingProperty, value);
    }

    public static bool GetIsTileShown(Control child)
    {
        ArgumentNullException.ThrowIfNull(child);
        return child.GetValue(IsTileShownProperty);
    }

    public static void SetIsTileShown(Control child, bool value)
    {
        ArgumentNullException.ThrowIfNull(child);
        _ = child.SetValue(IsTileShownProperty, value);
    }

    /// <summary>
    /// How many tiles each row holds: as many rows as the column limit demands, then the tiles spread evenly
    /// across them. Four tiles under a limit of three are two rows of two rather than three and a lone one.
    /// </summary>
    internal static int[] RowCounts(int count, int maxColumns)
    {
        if (count <= 0)
        {
            return [];
        }

        var columns = Math.Max(1, maxColumns);
        var rows = ((count - 1) / columns) + 1;
        var perRow = count / rows;
        var remainder = count % rows;
        var counts = new int[rows];
        for (var row = 0; row < rows; row++)
        {
            counts[row] = perRow + (row < remainder ? 1 : 0);
        }

        return counts;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var offScreen = default(Size);
        foreach (var child in Children)
        {
            if (!IsTile(child))
            {
                child.Measure(availableSize);
                offScreen = new Size(
                    Math.Max(offScreen.Width, child.DesiredSize.Width),
                    Math.Max(offScreen.Height, child.DesiredSize.Height));
            }
        }

        var visible = TileChildren();
        var rows = RowCounts(visible.Count, MaxColumns);
        if (rows.Length == 0)
        {
            return Finite(availableSize, offScreen);
        }

        var spacing = TileSpacing;
        var index = 0;
        var desiredWidth = 0d;
        var desiredHeight = 0d;
        for (var row = 0; row < rows.Length; row++)
        {
            var count = rows[row];
            var (_, bandHeight) = Slice(availableSize.Height, rows.Length, spacing, row);
            var rowWidth = 0d;
            var tallest = 0d;
            for (var column = 0; column < count; column++)
            {
                var (_, tileWidth) = Slice(availableSize.Width, count, spacing, column);
                var child = visible[index++];
                child.Measure(new Size(tileWidth, bandHeight));
                rowWidth += child.DesiredSize.Width;
                tallest = Math.Max(tallest, child.DesiredSize.Height);
            }

            desiredWidth = Math.Max(desiredWidth, rowWidth + ((count - 1) * spacing));
            desiredHeight += tallest;
        }

        desiredHeight += (rows.Length - 1) * spacing;

        return Finite(
            availableSize,
            new Size(Math.Max(desiredWidth, offScreen.Width), Math.Max(desiredHeight, offScreen.Height)));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var whole = new Rect(finalSize);
        foreach (var child in Children)
        {
            if (!IsTile(child))
            {
                child.ZIndex = _hiddenZIndex;
                child.Arrange(whole);
            }
        }

        var visible = TileChildren();
        var rows = RowCounts(visible.Count, MaxColumns);
        var spacing = TileSpacing;
        var index = 0;
        for (var row = 0; row < rows.Length; row++)
        {
            var (top, height) = Slice(finalSize.Height, rows.Length, spacing, row);
            for (var column = 0; column < rows[row]; column++)
            {
                var (left, width) = Slice(finalSize.Width, rows[row], spacing, column);
                var child = visible[index++];
                child.ZIndex = _tileZIndex;
                child.Arrange(new Rect(left, top, width, height));
            }
        }

        return finalSize;
    }

    /// <summary>
    /// One slice of a row or column band. Both edges are rounded before the length is taken from them, so
    /// that neighbouring tiles share an edge exactly: a per-tile fractional width accumulates into hairline
    /// seams, and into a visibly misplaced window for a native surface positioned at whole pixels.
    /// </summary>
    private static (double Offset, double Length) Slice(double total, int count, double spacing, int index)
    {
        if (double.IsInfinity(total))
        {
            return (0, double.PositiveInfinity);
        }

        var content = Math.Max(0, total - ((count - 1) * spacing));
        var start = Math.Round(index * content / count, MidpointRounding.AwayFromZero);
        var end = Math.Round((index + 1) * content / count, MidpointRounding.AwayFromZero);
        return (start + (index * spacing), Math.Max(0, end - start));
    }

    /// <summary>Measure may not answer with an infinite size, whatever it was asked with. Everywhere the
    /// workspace actually hosts this panel both dimensions are finite; a preview or a test may not be.</summary>
    private static Size Finite(Size availableSize, Size desired)
    {
        return new Size(
            double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? desired.Height : availableSize.Height);
    }

    private static bool IsTile(Control child)
    {
        return child.IsVisible && GetIsTileShown(child);
    }

    private List<Control> TileChildren()
    {
        var visible = new List<Control>(Children.Count);
        foreach (var child in Children)
        {
            if (IsTile(child))
            {
                visible.Add(child);
            }
        }

        return visible;
    }

    /// <summary>
    /// A tile appearing or disappearing changes how every other tile is sized, and a child's own
    /// invalidation does not reach this panel when its desired size has not changed — which is exactly the
    /// case for a terminal that was hidden. So the panel watches visibility itself.
    /// </summary>
    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var child in _observedChildren)
        {
            child.PropertyChanged -= OnChildPropertyChanged;
        }

        _observedChildren.Clear();
        foreach (var child in Children)
        {
            child.PropertyChanged += OnChildPropertyChanged;
            _ = _observedChildren.Add(child);
        }

        InvalidateMeasure();
    }

    private void OnChildPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty)
        {
            InvalidateMeasure();
        }
    }
}
