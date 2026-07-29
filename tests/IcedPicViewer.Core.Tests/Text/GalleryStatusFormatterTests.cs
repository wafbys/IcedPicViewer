// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Core.Tests.Text;

public sealed class GalleryStatusFormatterTests
{
    [Fact]
    public void FormatItemBreakdown_ImagesOnly()
        => Assert.Equal("3 image(s)", GalleryStatusFormatter.FormatItemBreakdown(3, 0));

    [Fact]
    public void FormatItemBreakdown_WithVideos()
        => Assert.Equal("2 images, 1 videos", GalleryStatusFormatter.FormatItemBreakdown(2, 1));

    [Fact]
    public void FormatScanning_WithoutPath()
        => Assert.Equal(
            "Scanning… found 10, showing 3 image(s)",
            GalleryStatusFormatter.FormatScanning(10, "3 image(s)"));

    [Fact]
    public void FormatScanning_WithPath()
        => Assert.Equal(
            "Scanning: /photos  (42 found)",
            GalleryStatusFormatter.FormatScanning(42, "1 image(s)", currentPath: "/photos"));

    [Fact]
    public void FormatGallery_WithRemaining()
        => Assert.Equal(
            "Showing 5 image(s) / 100 (95 more)",
            GalleryStatusFormatter.FormatGallery("5 image(s)", 100, 95));

    [Fact]
    public void FormatGallery_Complete()
        => Assert.Equal(
            "Loaded 5 image(s)",
            GalleryStatusFormatter.FormatGallery("5 image(s)", 5, 0));

    [Fact]
    public void FormatErrorSuffix_CountOnly()
        => Assert.Equal(" · 2 scan error(s)", GalleryStatusFormatter.FormatErrorSuffix(2));

    [Fact]
    public void FormatErrorSuffix_SingleWithReason()
        => Assert.Equal(
            " — 1 file skipped (bad.zip: corrupt)",
            GalleryStatusFormatter.FormatErrorSuffix(1, "bad.zip", "corrupt"));

    [Fact]
    public void FormatSlideshowActive_WithFlags()
        => Assert.Equal(
            "Slideshow every 5s · loop · shuffle",
            GalleryStatusFormatter.FormatSlideshowActive(5, looping: true, shuffling: true));

    [Fact]
    public void FormatDeleted_TrashAndPermanent()
    {
        Assert.Equal("Moved to trash: a.jpg", GalleryStatusFormatter.FormatDeleted("a.jpg", movedToTrash: true));
        Assert.Equal("Deleted: a.jpg", GalleryStatusFormatter.FormatDeleted("a.jpg", movedToTrash: false));
    }

    [Fact]
    public void FormatScanningStarted()
        => Assert.Equal("Scanning…", GalleryStatusFormatter.FormatScanningStarted());
}
