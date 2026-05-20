using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.Generic;
using System.Linq;
using Windows.Foundation;

namespace IcedPicViewer.Controls;

public class MasonryPanel : Panel
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

    protected override Size MeasureOverride(Size availableSize)
    {
        var columnCount = ColumnCount;
        if (columnCount <= 0) columnCount = 3;

        var itemWidth = ItemWidth;
        var spacing = ItemSpacing;
        var totalSpacing = spacing * (columnCount - 1);
        var availableWidth = availableSize.Width - totalSpacing;
        if (availableWidth <= 0) availableWidth = columnCount * itemWidth;

        var actualItemWidth = availableWidth / columnCount;

        var columnHeights = new double[columnCount];

        foreach (var child in Children)
        {
            child.Measure(new Size(actualItemWidth, double.PositiveInfinity));

            var desiredHeight = child.DesiredSize.Height;
            if (double.IsNaN(desiredHeight) || desiredHeight <= 0 || double.IsInfinity(desiredHeight))
            {
                desiredHeight = actualItemWidth;
            }

            var shortestColumn = 0;
            for (int i = 1; i < columnCount; i++)
            {
                if (columnHeights[i] < columnHeights[shortestColumn])
                    shortestColumn = i;
            }

            columnHeights[shortestColumn] += desiredHeight + spacing;
        }

        var maxHeight = columnHeights.Length > 0 ? columnHeights.Max() - spacing : 0;
        return new Size(availableSize.Width, Math.Max(maxHeight, 0));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columnCount = ColumnCount;
        if (columnCount <= 0) columnCount = 3;

        var spacing = ItemSpacing;
        var totalSpacing = spacing * (columnCount - 1);
        var availableWidth = finalSize.Width - totalSpacing;
        if (availableWidth <= 0) availableWidth = ColumnCount * ItemWidth;

        var actualItemWidth = availableWidth / columnCount;

        var columnPositions = new double[columnCount];
        for (int i = 0; i < columnCount; i++)
        {
            columnPositions[i] = i * (actualItemWidth + spacing);
        }

        var columnHeights = new double[columnCount];

        foreach (var child in Children)
        {
            var shortestColumn = 0;
            for (int i = 1; i < columnCount; i++)
            {
                if (columnHeights[i] < columnHeights[shortestColumn])
                    shortestColumn = i;
            }

            var x = columnPositions[shortestColumn];
            var y = columnHeights[shortestColumn];

            var itemHeight = child.DesiredSize.Height;
            if (double.IsNaN(itemHeight) || itemHeight <= 0 || double.IsInfinity(itemHeight))
            {
                itemHeight = actualItemWidth;
            }

            child.Arrange(new Rect(x, y, actualItemWidth, itemHeight));

            columnHeights[shortestColumn] += itemHeight + spacing;
        }

        return finalSize;
    }
}
