// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using IcedPicViewer.Core.Layout;

namespace IcedPicViewer.Avalonia.Controls;

/// <summary>
/// Non-virtualizing masonry panel aligned with the WinUI <c>MasonryPanel</c>:
/// fixed <see cref="ColumnCount"/> (default 3), columns flex to fill width.
/// Tracks per-child Y for gallery scroll-into-view after leaving the viewer.
/// </summary>
public class MasonryPanel : Panel
{
    public static readonly StyledProperty<int> ColumnCountProperty =
        AvaloniaProperty.Register<MasonryPanel, int>(nameof(ColumnCount), 3);

    public static readonly StyledProperty<double> ItemWidthProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ItemWidth), 220.0);

    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<MasonryPanel, double>(nameof(ItemSpacing), 8.0);

    private readonly List<double> _itemTops = new();
    private readonly List<double> _itemHeights = new();

    static MasonryPanel()
    {
        AffectsMeasure<MasonryPanel>(ColumnCountProperty, ItemWidthProperty, ItemSpacingProperty);
        AffectsArrange<MasonryPanel>(ColumnCountProperty, ItemWidthProperty, ItemSpacingProperty);
    }

    public int ColumnCount
    {
        get => GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public double ItemWidth
    {
        get => GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    public double ItemSpacing
    {
        get => GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    /// <summary>Top Y of the child at <paramref name="index"/> after the last arrange.</summary>
    public bool TryGetItemTop(int index, out double top, out double height)
    {
        if (index < 0 || index >= _itemTops.Count)
        {
            top = 0;
            height = 0;
            return false;
        }

        top = _itemTops[index];
        height = index < _itemHeights.Count ? _itemHeights[index] : 0;
        return true;
    }

    private (int columns, double actualItemWidth, double spacing) GetLayoutParams(double availableWidth)
    {
        var spacing = ItemSpacing < 0 ? 0 : ItemSpacing;
        var columns = ColumnCount > 0 ? ColumnCount : 3;
        var preferred = ItemWidth > 0 ? ItemWidth : 220;

        var width = availableWidth;
        if (double.IsInfinity(width) || double.IsNaN(width) || width <= 0)
            width = columns * preferred + spacing * (columns - 1);

        var totalSpacing = spacing * (columns - 1);
        var actualItemWidth = Math.Max(1, (width - totalSpacing) / columns);
        return (columns, actualItemWidth, spacing);
    }

    private static double SanitizeHeight(double height, double fallback)
    {
        if (double.IsNaN(height) || height <= 0 || double.IsInfinity(height))
            return fallback;
        return height;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (columns, actualItemWidth, spacing) = GetLayoutParams(availableSize.Width);
        var columnHeights = new double[columns];

        foreach (var child in Children)
        {
            child.Measure(new Size(actualItemWidth, double.PositiveInfinity));
            var desiredHeight = SanitizeHeight(child.DesiredSize.Height, actualItemWidth);
            var col = MasonryLayoutEngine.FindShortestColumn(columnHeights);
            columnHeights[col] += desiredHeight + spacing;
        }

        var maxHeight = columnHeights.Length > 0 ? columnHeights.Max() - spacing : 0;
        var width = double.IsInfinity(availableSize.Width)
            ? columns * actualItemWidth + spacing * (columns - 1)
            : availableSize.Width;
        return new Size(width, Math.Max(maxHeight, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (columns, actualItemWidth, spacing) = GetLayoutParams(finalSize.Width);
        var columnHeights = new double[columns];
        var columnX = new double[columns];
        for (var i = 0; i < columns; i++)
            columnX[i] = i * (actualItemWidth + spacing);

        _itemTops.Clear();
        _itemHeights.Clear();
        if (_itemTops.Capacity < Children.Count)
        {
            _itemTops.Capacity = Children.Count;
            _itemHeights.Capacity = Children.Count;
        }

        foreach (var child in Children)
        {
            var col = MasonryLayoutEngine.FindShortestColumn(columnHeights);
            var x = columnX[col];
            var y = columnHeights[col];
            var itemHeight = SanitizeHeight(child.DesiredSize.Height, actualItemWidth);

            child.Arrange(new Rect(x, y, actualItemWidth, itemHeight));
            _itemTops.Add(y);
            _itemHeights.Add(itemHeight);
            columnHeights[col] += itemHeight + spacing;
        }

        return finalSize;
    }
}
