// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Layout;

/// <summary>
/// Platform-agnostic masonry (waterfall) helpers. UI panels on WinUI /
/// Avalonia call into this for shortest-column selection so layout math
/// stays identical across shells.
/// </summary>
public static class MasonryLayoutEngine
{
    public static int FindShortestColumn(double[] columnHeights)
    {
        if (columnHeights.Length == 0)
            return 0;

        int shortest = 0;
        for (int i = 1; i < columnHeights.Length; i++)
        {
            if (columnHeights[i] < columnHeights[shortest])
                shortest = i;
        }
        return shortest;
    }

    /// <summary>
    /// How many columns fit in <paramref name="availableWidth"/> given a
    /// preferred item width and spacing. At least 1.
    /// </summary>
    public static int ComputeColumnCount(double availableWidth, double itemWidth, double spacing)
    {
        if (itemWidth <= 0) itemWidth = 200;
        if (spacing < 0) spacing = 0;
        if (availableWidth <= 0 || double.IsInfinity(availableWidth) || double.IsNaN(availableWidth))
            return 3;

        var cols = (int)Math.Floor((availableWidth + spacing) / (itemWidth + spacing));
        return Math.Max(1, cols);
    }
}
