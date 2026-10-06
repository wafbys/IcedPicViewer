// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Core.Tests.Media;

public sealed class ThumbnailCacheFileTests
{
    [Fact]
    public void RoundTrip_Image_PreservesHeaderAndPayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        var header = new ThumbnailCacheHeader(1920, 1080, null, ThumbnailImageFormat.Jpeg);

        using var stream = new MemoryStream();
        ThumbnailCacheFile.Write(stream, header, payload);
        stream.Position = 0;

        Assert.True(ThumbnailCacheFile.TryRead(stream, out var read, out var readPayload));
        Assert.Equal(1920, read.OriginalWidth);
        Assert.Equal(1080, read.OriginalHeight);
        Assert.Null(read.Duration);
        Assert.Equal(ThumbnailImageFormat.Jpeg, read.Format);
        Assert.Equal(payload, readPayload);
    }

    [Fact]
    public void RoundTrip_Video_PreservesDuration()
    {
        var header = new ThumbnailCacheHeader(1280, 720, TimeSpan.FromSeconds(12.5), ThumbnailImageFormat.Png);

        using var stream = new MemoryStream();
        ThumbnailCacheFile.Write(stream, header, new byte[] { 9 });
        stream.Position = 0;

        Assert.True(ThumbnailCacheFile.TryRead(stream, out var read, out _));
        Assert.Equal(TimeSpan.FromSeconds(12.5), read.Duration);
        Assert.Equal(ThumbnailImageFormat.Png, read.Format);
    }

    [Fact]
    public void TryRead_BadMagic_ReturnsFalse()
    {
        using var stream = new MemoryStream(new byte[] { (byte)'X', (byte)'X', (byte)'X', (byte)'X', 0, 0, 0, 0 });
        Assert.False(ThumbnailCacheFile.TryRead(stream, out _, out _));
    }

    [Fact]
    public void TryRead_Truncated_ReturnsFalse()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var header = new ThumbnailCacheHeader(10, 10, null, ThumbnailImageFormat.Png);
        using var full = new MemoryStream();
        ThumbnailCacheFile.Write(full, header, payload);

        var truncated = full.ToArray()[..((int)full.Length - 3)];
        using var stream = new MemoryStream(truncated);

        Assert.False(ThumbnailCacheFile.TryRead(stream, out _, out _));
    }

    [Fact]
    public void TryRead_UnknownFormat_ReturnsFalse()
    {
        var header = new ThumbnailCacheHeader(10, 10, null, ThumbnailImageFormat.Png);
        using var stream = new MemoryStream();
        ThumbnailCacheFile.Write(stream, header, new byte[] { 1 });
        var bytes = stream.ToArray();
        bytes[^2] = 99; // clobber the format byte
        using var corrupt = new MemoryStream(bytes);

        Assert.False(ThumbnailCacheFile.TryRead(corrupt, out _, out _));
    }

    [Fact]
    public void TryRead_EmptyStream_ReturnsFalse()
    {
        using var stream = new MemoryStream();
        Assert.False(ThumbnailCacheFile.TryRead(stream, out _, out _));
    }
}
