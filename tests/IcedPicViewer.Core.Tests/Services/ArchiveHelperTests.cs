// Copyright (c) IcedPicViewer. All rights reserved.

using System.IO.Compression;
using IcedPicViewer.Services.Implementations;

namespace IcedPicViewer.Core.Tests.Services;

public sealed class ArchiveHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _zipPath;
    private readonly string _rarPath;
    private readonly string _emptyDir;

    public ArchiveHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"IcedPicViewerTests_{Guid.NewGuid():N}");
        _emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(_emptyDir);

        _zipPath = Path.Combine(_tempDir, "test.zip");
        _rarPath = Path.Combine(_tempDir, "test.rar");
        CreateTestZip();
    }

    private void CreateTestZip()
    {
        using var archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create);
        var entry = archive.CreateEntry("photos/sunset.jpg");
        using (var writer = new StreamWriter(entry.Open()))
            writer.Write("fake jpeg content");
        entry = archive.CreateEntry("videos/clip.mp4");
        using (var writer2 = new StreamWriter(entry.Open()))
            writer2.Write("fake mp4 content");
        entry = archive.CreateEntry("readme.txt");
        using (var writer3 = new StreamWriter(entry.Open()))
            writer3.Write("not media");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("archive.zip", true)]
    [InlineData("archive.ZIP", true)]
    [InlineData("archive.rar", true)]
    [InlineData("archive.7z", true)]
    [InlineData("archive.tar", true)]
    [InlineData("archive.tgz", true)]
    [InlineData("archive.tar.gz", true)]
    [InlineData("archive.tar.bz2", true)]
    [InlineData("archive.tar.xz", true)]
    [InlineData("archive.tar.zst", true)]
    [InlineData("document.txt", false)]
    [InlineData("image.jpg", false)]
    [InlineData("video.mp4", false)]
    [InlineData("", false)]
    public void IsArchiveFileName_ShouldDetectArchives(string fileName, bool expected)
    {
        Assert.Equal(expected, ArchiveHelper.IsArchiveFileName(fileName));
    }

    [Fact]
    public void IsArchive_ValidZip_ShouldReturnTrue()
    {
        Assert.True(ArchiveHelper.IsArchive(_zipPath));
    }

    [Fact]
    public void IsArchive_MissingFile_ShouldReturnFalse()
    {
        Assert.False(ArchiveHelper.IsArchive(Path.Combine(_tempDir, "nonexistent.zip")));
    }

    [Fact]
    public void IsArchive_NonArchiveFile_ShouldReturnFalse()
    {
        var txtPath = Path.Combine(_tempDir, "readme.txt");
        File.WriteAllText(txtPath, "hello");
        Assert.False(ArchiveHelper.IsArchive(txtPath));
    }

    [Fact]
    public void ListEntries_WithImageExtensionFilter_ShouldReturnOnlyImageEntries()
    {
        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".png" };
        var entries = ArchiveHelper.ListEntries(_zipPath, filter).ToList();

        Assert.Single(entries);
        Assert.Equal("photos/sunset.jpg", entries[0].Key);
    }

    [Fact]
    public void ListEntries_WithVideoExtensionFilter_ShouldReturnOnlyVideoEntries()
    {
        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4" };
        var entries = ArchiveHelper.ListEntries(_zipPath, filter).ToList();

        Assert.Single(entries);
        Assert.Equal("videos/clip.mp4", entries[0].Key);
    }

    [Fact]
    public void ListEntries_WithBothFilters_ShouldReturnAllMedia()
    {
        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".mp4" };
        var entries = ArchiveHelper.ListEntries(_zipPath, filter).ToList();

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Key == "photos/sunset.jpg");
        Assert.Contains(entries, e => e.Key == "videos/clip.mp4");
    }

    [Fact]
    public void ListEntries_WithoutFilter_ShouldReturnAllEntries()
    {
        var entries = ArchiveHelper.ListEntries(_zipPath, null).ToList();
        Assert.Equal(3, entries.Count);
    }

    [Fact]
    public void GetEntryUncompressedSize_ShouldMatchListEntriesSize()
    {
        var listed = ArchiveHelper.ListEntries(_zipPath, extensionFilter: null)
            .Single(e => e.Key == "photos/sunset.jpg");

        var size = ArchiveHelper.GetEntryUncompressedSize(_zipPath, "photos/sunset.jpg");

        Assert.True(listed.UncompressedSize > 0);
        Assert.Equal(listed.UncompressedSize, size);
        Assert.Equal(listed.UncompressedSize, ArchiveHelper.GetEntryUncompressedSize(_zipPath, "photos\\sunset.jpg"));
    }

    [Fact]
    public void GetEntryUncompressedSize_MissingEntry_ShouldReturnZero()
    {
        Assert.Equal(0, ArchiveHelper.GetEntryUncompressedSize(_zipPath, "no-such.jpg"));
        Assert.Equal(0, ArchiveHelper.GetEntryUncompressedSize(_zipPath, ""));
    }

    [Fact]
    public void OpenEntryStream_ShouldReturnStreamForExistingEntry()
    {
        using var stream = ArchiveHelper.OpenEntryStream(_zipPath, "photos/sunset.jpg");

        Assert.NotNull(stream);
        Assert.True(stream.Length > 0);
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void OpenEntryStream_MissingEntry_ShouldThrowFileNotFoundException()
    {
        Assert.Throws<FileNotFoundException>(() =>
            ArchiveHelper.OpenEntryStream(_zipPath, "nonexistent.jpg"));
    }

    [Fact]
    public void ExtractEntryToFile_ShouldWriteToDisk()
    {
        var dest = Path.Combine(_tempDir, "extracted.jpg");

        ArchiveHelper.ExtractEntryToFile(_zipPath, "photos/sunset.jpg", dest);

        Assert.True(File.Exists(dest));
        Assert.True(new FileInfo(dest).Length > 0);
    }

    [Fact]
    public void OpenEntryStream_ReadMultipleTimes_ShouldNotDisposeUnderlyingStreamEarly()
    {
        // Regression: verifying ReaderFactory.OpenReader with LeaveStreamOpen = true
        // does not double-dispose the file stream, allowing subsequent reads.
        var stream1 = ArchiveHelper.OpenEntryStream(_zipPath, "photos/sunset.jpg");
        stream1.Dispose();

        using var stream2 = ArchiveHelper.OpenEntryStream(_zipPath, "videos/clip.mp4");
        Assert.True(stream2.Length > 0);
    }

    [Fact]
    public void ArchiveExtensions_ShouldBeReadOnly()
    {
        var exts = ArchiveHelper.ArchiveExtensions;
        Assert.True(exts.Contains(".zip"));
        Assert.True(exts.Contains(".rar"));
    }
}
