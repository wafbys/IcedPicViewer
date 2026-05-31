// Copyright (c) IcedPicViewer. All rights reserved.

using Microsoft.VisualStudio.TestTools.UnitTesting;
using IcedPicViewer.Controls;

namespace IcedPicViewer.Tests.Controls;

[TestClass]
public class MasonryLayoutEngineTests
{
    [TestMethod]
    public void FindShortestColumn_ReturnsCorrectIndex()
    {
        var heights = new[] { 100.0, 50.0, 200.0 };
        var result = MasonryLayoutEngine.FindShortestColumn(heights);
        Assert.AreEqual(1, result);
    }

    [TestMethod]
    public void CalculateColumnHeights_BalancesItems()
    {
        var itemHeights = new[] { 100.0, 100.0, 100.0, 100.0 };
        var result = MasonryLayoutEngine.CalculateColumnHeights(itemHeights, 2, 0);

        // With 4 equal items in 2 columns, both columns should end up at 200
        Assert.AreEqual(200, result[0]);
        Assert.AreEqual(200, result[1]);
    }
}
