// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Tests.Media;

public sealed class MediaCatalogTests
{
    [Theory]
    [InlineData("photo.jpg", MediaKind.Image)]
    [InlineData("photo.jpeg", MediaKind.Image)]
    [InlineData("photo.png", MediaKind.Image)]
    [InlineData("photo.gif", MediaKind.Image)]
    [InlineData("photo.bmp", MediaKind.Image)]
    [InlineData("photo.webp", MediaKind.Image)]
    [InlineData("photo.tiff", MediaKind.Image)]
    [InlineData("photo.tif", MediaKind.Image)]
    [InlineData("photo.ico", MediaKind.Image)]
    [InlineData("photo.avif", MediaKind.Image)]
    [InlineData("photo.heic", MediaKind.Image)]
    [InlineData("video.mp4", MediaKind.Video)]
    [InlineData("video.mkv", MediaKind.Video)]
    [InlineData("video.mov", MediaKind.Video)]
    [InlineData("video.avi", MediaKind.Video)]
    [InlineData("video.webm", MediaKind.Video)]
    [InlineData("video.flv", MediaKind.Video)]
    public void GetKind_ShouldReturnCorrectKind(string path, MediaKind expected)
    {
        var kind = MediaCatalog.GetKind(path);
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("photo.jpg", true)]
    [InlineData("video.mp4", true)]
    [InlineData("readme.txt", false)]
    [InlineData("script.js", false)]
    [InlineData("photo.psd", false)]
    [InlineData("", false)]
    public void IsSupported_ShouldDetectSupportedFormats(string path, bool expected)
    {
        var supported = MediaCatalog.IsSupported(path);
        Assert.Equal(expected, supported);
    }

    [Fact]
    public void SupportedMedia_ShouldContainAllImageAndVideoExtensions()
    {
        var supported = MediaCatalog.SupportedMedia.ToList();

        Assert.Contains((".jpg", MediaKind.Image), supported);
        Assert.Contains((".mp4", MediaKind.Video), supported);
        Assert.Contains((".webp", MediaKind.Image), supported);
        Assert.Contains((".mkv", MediaKind.Video), supported);
    }

    [Fact]
    public void SupportedMedia_ShouldBeConsistentWithImageAndVideoLists()
    {
        var supported = MediaCatalog.SupportedMedia.ToList();
        var images = supported.Where(s => s.Kind == MediaKind.Image).Select(s => s.Extension);
        var videos = supported.Where(s => s.Kind == MediaKind.Video).Select(s => s.Extension);

        Assert.Equal(MediaCatalog.ImageExtensions.OrderBy(x => x), images.OrderBy(x => x));
        Assert.Equal(MediaCatalog.VideoExtensions.OrderBy(x => x), videos.OrderBy(x => x));
    }

    [Fact]
    public void GetKind_CaseInsensitive()
    {
        Assert.Equal(MediaKind.Image, MediaCatalog.GetKind("PHOTO.JPG"));
        Assert.Equal(MediaKind.Video, MediaCatalog.GetKind("VIDEO.MP4"));
        Assert.Equal(MediaKind.Image, MediaCatalog.GetKind("/path/to/Photo.WebP"));
    }
}
