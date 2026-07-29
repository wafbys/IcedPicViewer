// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Tests.Models;

public sealed class ImageSourceTests
{
    [Fact]
    public void FromFile_ShouldSetPathAndNullArchiveEntry()
    {
        var source = ImageSource.FromFile("/home/user/photo.jpg", MediaKind.Image);

        Assert.Equal("/home/user/photo.jpg", source.Path);
        Assert.Null(source.ArchiveEntry);
        Assert.Equal(MediaKind.Image, source.Kind);
        Assert.False(source.IsInArchive);
    }

    [Fact]
    public void FromFile_ShouldDefaultToImageKind()
    {
        var source = ImageSource.FromFile("/home/user/video.mp4");

        Assert.Equal(MediaKind.Image, source.Kind);
    }

    [Fact]
    public void FromFile_ShouldSetVideoKind()
    {
        var source = ImageSource.FromFile("/home/user/video.mp4", MediaKind.Video);

        Assert.Equal(MediaKind.Video, source.Kind);
    }

    [Fact]
    public void FromArchive_ShouldSetPathAndArchiveEntry()
    {
        var source = ImageSource.FromArchive("/archive.zip", "photos/sunset.jpg", MediaKind.Video);

        Assert.Equal("/archive.zip", source.Path);
        Assert.Equal("photos/sunset.jpg", source.ArchiveEntry);
        Assert.Equal(MediaKind.Video, source.Kind);
        Assert.True(source.IsInArchive);
    }

    [Fact]
    public void ToString_File_ShouldReturnPathOnly()
    {
        var source = ImageSource.FromFile("/home/user/photo.jpg");

        Assert.Equal("/home/user/photo.jpg", source.ToString());
    }

    [Fact]
    public void ToString_Archive_ShouldReturnPathExclaimEntry()
    {
        var source = ImageSource.FromArchive("/archive.zip", "photos/sunset.jpg");

        Assert.Equal("/archive.zip!photos/sunset.jpg", source.ToString());
    }

    [Fact]
    public void ToString_IsRoundTrippableAsKey()
    {
        var a = ImageSource.FromFile("/a/b/c.jpg");
        var b = ImageSource.FromFile("/a/b/c.jpg");
        var c = ImageSource.FromArchive("/a.zip", "x/y.jpg");

        Assert.Equal(a.ToString(), b.ToString());
        Assert.NotEqual(a.ToString(), c.ToString());
    }

    [Fact]
    public void IsInArchive_ShouldBeFalseForFile()
    {
        var source = ImageSource.FromFile("/test.jpg");
        Assert.False(source.IsInArchive);
    }

    [Fact]
    public void IsInArchive_ShouldBeTrueForArchiveEntry()
    {
        var source = ImageSource.FromArchive("/test.zip", "entry.jpg");
        Assert.True(source.IsInArchive);
    }
}
