// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Core.Tests.Text;

public sealed class GalleryStatusFormatterTests
{
    [Fact]
    public void FormatItemBreakdown_ImagesOnly()
        => Assert.Equal("3 张图片", GalleryStatusFormatter.FormatItemBreakdown(3, 0));

    [Fact]
    public void FormatItemBreakdown_WithVideos()
        => Assert.Equal("2 张图片 · 1 个视频", GalleryStatusFormatter.FormatItemBreakdown(2, 1));

    [Fact]
    public void FormatScanning_WithoutPath()
        => Assert.Equal(
            "扫描中… 已发现 10，显示 3 张图片",
            GalleryStatusFormatter.FormatScanning(10, "3 张图片"));

    [Fact]
    public void FormatScanning_WithPath()
        => Assert.Equal(
            "扫描中：/photos（已发现 42）",
            GalleryStatusFormatter.FormatScanning(42, "1 张图片", currentPath: "/photos"));

    [Fact]
    public void FormatGallery_WithRemaining()
        => Assert.Equal(
            "显示 5 张图片 / 100（还可加载 95）",
            GalleryStatusFormatter.FormatGallery("5 张图片", 100, 95));

    [Fact]
    public void FormatGallery_Complete()
        => Assert.Equal(
            "已加载 5 张图片",
            GalleryStatusFormatter.FormatGallery("5 张图片", 5, 0));

    [Fact]
    public void FormatErrorSuffix_CountOnly()
        => Assert.Equal(" · 2 个扫描错误", GalleryStatusFormatter.FormatErrorSuffix(2));

    [Fact]
    public void FormatErrorSuffix_SingleWithReason()
        => Assert.Equal(
            " — 跳过 1 个文件（bad.zip：corrupt）",
            GalleryStatusFormatter.FormatErrorSuffix(1, "bad.zip", "corrupt"));

    [Fact]
    public void FormatSlideshowActive_WithFlags()
        => Assert.Equal(
            "幻灯片 每 5 秒 · 循环 · 随机",
            GalleryStatusFormatter.FormatSlideshowActive(5, looping: true, shuffling: true));

    [Fact]
    public void FormatDeleted_TrashAndPermanent()
    {
        Assert.Equal("已移至回收站：a.jpg", GalleryStatusFormatter.FormatDeleted("a.jpg", movedToTrash: true));
        Assert.Equal("已删除：a.jpg", GalleryStatusFormatter.FormatDeleted("a.jpg", movedToTrash: false));
    }

    [Fact]
    public void FormatScanningStarted()
        => Assert.Equal("扫描中…", GalleryStatusFormatter.FormatScanningStarted());

    [Fact]
    public void UiCopy_ArchiveAndConfirmTitles()
    {
        Assert.Equal("无法删除", UiCopy.CannotDeleteTitle);
        Assert.Equal("确认删除", UiCopy.ConfirmDeleteTitle);
        Assert.Equal("加载更多", UiCopy.LoadMore);
    }
}
