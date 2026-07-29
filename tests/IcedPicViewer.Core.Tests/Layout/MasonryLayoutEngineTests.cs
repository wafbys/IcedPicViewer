// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Layout;

namespace IcedPicViewer.Core.Tests.Layout;

public sealed class MasonryLayoutEngineTests
{
    private static readonly double[] _heightsAscending = [0.0, 100.0, 200.0];
    private static readonly double[] _heightsMidMin = [100.0, 50.0, 200.0];
    private static readonly double[] _heightsLastMin = [100.0, 200.0, 50.0];
    private static readonly double[] _heightsFirstOfTied = [50.0, 50.0, 100.0];

    [Fact]
    public void FindShortestColumn_FirstMin_ReturnsZero()
        => Assert.Equal(0, MasonryLayoutEngine.FindShortestColumn(_heightsAscending));

    [Fact]
    public void FindShortestColumn_SecondMin_ReturnsOne()
        => Assert.Equal(1, MasonryLayoutEngine.FindShortestColumn(_heightsMidMin));

    [Fact]
    public void FindShortestColumn_ThirdMin_ReturnsTwo()
        => Assert.Equal(2, MasonryLayoutEngine.FindShortestColumn(_heightsLastMin));

    [Fact]
    public void FindShortestColumn_TiedFirstWins_ReturnsZero()
        => Assert.Equal(0, MasonryLayoutEngine.FindShortestColumn(_heightsFirstOfTied));

    [Fact]
    public void FindShortestColumn_EmptyArray_ShouldReturnZero()
        => Assert.Equal(0, MasonryLayoutEngine.FindShortestColumn([]));

    [Fact]
    public void FindShortestColumn_SingleElement_ShouldReturnZero()
        => Assert.Equal(0, MasonryLayoutEngine.FindShortestColumn([42.0]));

    [Fact]
    public void FindShortestColumn_AllTied_ShouldReturnZero()
        => Assert.Equal(0, MasonryLayoutEngine.FindShortestColumn([10.0, 10.0, 10.0]));

    [Theory]
    [InlineData(800, 200, 8, 3)]
    [InlineData(800, 250, 8, 3)]
    [InlineData(400, 200, 0, 2)]
    [InlineData(100, 200, 8, 1)]
    [InlineData(0, 200, 8, 3)]
    [InlineData(-1, 200, 8, 3)]
    public void ComputeColumnCount_ShouldReturnCorrectColumns(double width, double itemWidth, double spacing, int expected)
        => Assert.Equal(expected, MasonryLayoutEngine.ComputeColumnCount(width, itemWidth, spacing));

    [Fact]
    public void ComputeColumnCount_NegativeItemWidth_ShouldUseDefault()
        => Assert.Equal(3, MasonryLayoutEngine.ComputeColumnCount(800, -10, 8));

    [Fact]
    public void ComputeColumnCount_NaNWidth_ShouldReturnDefault()
        => Assert.Equal(3, MasonryLayoutEngine.ComputeColumnCount(double.NaN, 200, 8));
}
