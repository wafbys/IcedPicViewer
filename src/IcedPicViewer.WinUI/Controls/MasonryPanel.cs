using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Controls;

/// <summary>
/// Masonry (waterfall) layout panel used by the main gallery.
/// 
/// This is a non-virtualizing Panel; for large collections it is paired with
/// incremental loading (see GalleryViewModel.PageSize) to keep initial render
/// and memory pressure manageable. A virtualizing alternative (ItemsRepeater +
/// custom layout) was explored but traded the desired waterfall visual for
/// modest perf wins — kept here intentionally until that's worth revisiting.
/// </summary>
public partial class MasonryPanel : Panel
{
    public static readonly DependencyProperty ColumnCountProperty =
        DependencyProperty.Register(nameof(ColumnCount), typeof(int), typeof(MasonryPanel),
            new PropertyMetadata(3, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemSpacingProperty =
        DependencyProperty.Register(nameof(ItemSpacing), typeof(double), typeof(MasonryPanel),
            new PropertyMetadata(8.0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ItemWidthProperty =
        DependencyProperty.Register(nameof(ItemWidth), typeof(double), typeof(MasonryPanel),
            new PropertyMetadata(200.0, OnLayoutPropertyChanged));

    private readonly Dictionary<UIElement, double> _itemYPositions = new();

    public int ColumnCount
    {
        get => (int)GetValue(ColumnCountProperty);
        set => SetValue(ColumnCountProperty, value);
    }

    public double ItemSpacing
    {
        get => (double)GetValue(ItemSpacingProperty);
        set => SetValue(ItemSpacingProperty, value);
    }

    public double ItemWidth
    {
        get => (double)GetValue(ItemWidthProperty);
        set => SetValue(ItemWidthProperty, value);
    }

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MasonryPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    private (double actualItemWidth, double spacing) GetLayoutParams(Size availableSize)
    {
        var columnCount = ColumnCount;
        if (columnCount <= 0) columnCount = 3;

        var itemWidth = ItemWidth;
        var spacing = ItemSpacing;
        var totalSpacing = spacing * (columnCount - 1);
        var availableWidth = availableSize.Width - totalSpacing;
        if (availableWidth <= 0) availableWidth = columnCount * itemWidth;

        return (availableWidth / columnCount, spacing);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (actualItemWidth, spacing) = GetLayoutParams(availableSize);

        var columnHeights = new double[ColumnCount > 0 ? ColumnCount : 3];

        foreach (var child in Children)
        {
            child.Measure(new Size(actualItemWidth, double.PositiveInfinity));

            var desiredHeight = child.DesiredSize.Height;
            if (double.IsNaN(desiredHeight) || desiredHeight <= 0 || double.IsInfinity(desiredHeight))
            {
                desiredHeight = actualItemWidth;
            }

            var shortestColumn = MasonryLayoutEngine.FindShortestColumn(columnHeights);
            columnHeights[shortestColumn] += desiredHeight + spacing;
        }

        var maxHeight = columnHeights.Length > 0 ? columnHeights.Max() - spacing : 0;
        return new Size(availableSize.Width, Math.Max(maxHeight, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var (actualItemWidth, spacing) = GetLayoutParams(finalSize);
        var columnCount = ColumnCount > 0 ? ColumnCount : 3;

        var columnPositions = new double[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            columnPositions[i] = i * (actualItemWidth + spacing);
        }

        var columnHeights = new double[columnCount];
        _itemYPositions.Clear();

        int index = 0;
        foreach (var child in Children)
        {
            var shortestColumn = MasonryLayoutEngine.FindShortestColumn(columnHeights);

            var x = columnPositions[shortestColumn];
            var y = columnHeights[shortestColumn];

            _itemYPositions[child] = y;

            var itemHeight = child.DesiredSize.Height;
            if (double.IsNaN(itemHeight) || itemHeight <= 0 || double.IsInfinity(itemHeight))
            {
                itemHeight = actualItemWidth;
            }

            child.Arrange(new Rect(x, y, actualItemWidth, itemHeight));

            columnHeights[shortestColumn] += itemHeight + spacing;
            index++;
        }

        return finalSize;
    }

    public double GetItemYPosition(int index)
    {
        if (index < 0 || index >= Children.Count) return 0;
        var child = Children[index];
        return _itemYPositions.TryGetValue(child, out var y) ? y : 0;
    }
}
