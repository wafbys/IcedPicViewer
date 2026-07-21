// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using IcedPicViewer.Core.Layout;

namespace IcedPicViewer.Controls;

/// <summary>
/// Compatibility façade over <see cref="Core.Layout.MasonryLayoutEngine"/> so
/// existing WinUI call sites keep the <c>IcedPicViewer.Controls</c> namespace.
/// </summary>
public static class MasonryLayoutEngine
{
    public static int FindShortestColumn(double[] columnHeights)
        => Core.Layout.MasonryLayoutEngine.FindShortestColumn(columnHeights);

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
