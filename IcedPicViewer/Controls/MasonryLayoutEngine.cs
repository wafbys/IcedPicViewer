// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Controls;

/// <summary>
/// Reusable static helpers for masonry (waterfall) layout calculations.
/// This can be used by both the legacy MasonryPanel and future custom Layout implementations.
/// </summary>
public static class MasonryLayoutEngine
{
    public static int FindShortestColumn(double[] columnHeights)
    {
        if (columnHeights == null || columnHeights.Length == 0)
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
    /// Calculates column heights for a list of item heights using masonry layout.
    /// </summary>
    public static double[] CalculateColumnHeights(IReadOnlyList<double> itemHeights, int columnCount, double spacing)
    {
        if (columnCount <= 0) columnCount = 1;
        var columnHeights = new double[columnCount];

        foreach (var height in itemHeights)
        {
            int col = FindShortestColumn(columnHeights);
            columnHeights[col] += height + spacing;
        }

        return columnHeights;
    }
}
