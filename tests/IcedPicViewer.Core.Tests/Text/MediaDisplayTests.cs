// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Core.Tests.Text;

public sealed class MediaDisplayTests
{
    [Theory]
    [InlineData(500, "500 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(2 * 1024 * 1024, "2.0 MB")]
    public void FormatFileSize(long bytes, string expected)
        => Assert.Equal(expected, MediaDisplay.FormatFileSize(bytes));

    [Fact]
    public void FormatDuration_Minutes()
        => Assert.Equal("1:05", MediaDisplay.FormatDuration(TimeSpan.FromSeconds(65)));

    [Fact]
    public void FormatDuration_Hours()
        => Assert.Equal("1:01:01", MediaDisplay.FormatDuration(TimeSpan.FromHours(1) + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1)));

    [Fact]
    public void FormatPixelSize()
        => Assert.Equal("1920×1080", MediaDisplay.FormatPixelSize(1920, 1080));

    [Fact]
    public void FormatInfoLine()
        => Assert.Equal(
            "1920×1080 · 1:05 · 2.0 MB",
            MediaDisplay.FormatInfoLine("1920×1080", "1:05", "2.0 MB"));
}
