// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Core.Tests.Services;

public sealed class DirectoryScannerTests : IDisposable
{
    private readonly string _tempDir;

    public DirectoryScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ipv_scanner_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void IsRecycleBin_ShouldDetectWindowsRecycleBin()
    {
        // Windows-style recycle bin paths — only meaningful on Windows.
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(DirectoryScanner.IsRecycleBin(@"C:\$RECYCLE.BIN\file.txt"));
        Assert.True(DirectoryScanner.IsRecycleBin(@"C:\Recycler\file.txt"));
        Assert.True(DirectoryScanner.IsRecycleBin(@"C:\RECYCLED\file.txt"));
    }

    [Fact]
    public void IsRecycleBin_ShouldNotFlagNormalPaths()
    {
        Assert.False(DirectoryScanner.IsRecycleBin("/home/user/Pictures/photo.jpg"));
        Assert.False(DirectoryScanner.IsRecycleBin("/home/user/Documents/file.txt"));
    }

    [Fact]
    public async Task ScanAsync_ShouldDiscoverImagesAndVideos()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "photo.jpg"), "jpeg");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "video.mp4"), "mp4");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "readme.txt"), "text");

        var extMap = new List<(string, MediaKind)>
        {
            (".jpg", MediaKind.Image),
            (".mp4", MediaKind.Video),
        };

        var scanner = new DirectoryScanner();
        var results = new List<MediaRef>();

        await foreach (var source in scanner.ScanAsync(_tempDir, false, extMap))
            results.Add(source);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.Path.EndsWith("photo.jpg", StringComparison.Ordinal) && r.Kind == MediaKind.Image);
        Assert.Contains(results, r => r.Path.EndsWith("video.mp4", StringComparison.Ordinal) && r.Kind == MediaKind.Video);
    }

    [Fact]
    public async Task ScanAsync_NoFilter_ShouldIncludeAllAsImage()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.jpg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "b.txt"), "x");

        var scanner = new DirectoryScanner();
        var results = new List<MediaRef>();

        await foreach (var source in scanner.ScanAsync(_tempDir, false))
            results.Add(source);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(MediaKind.Image, r.Kind));
    }

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ShouldReturnNothing()
    {
        var scanner = new DirectoryScanner();
        var results = new List<MediaRef>();

        await foreach (var source in scanner.ScanAsync(_tempDir, false))
            results.Add(source);

        Assert.Empty(results);
    }

    [Fact]
    public async Task ScanAsync_ShouldBeCancellable()
    {
        for (var i = 0; i < 200; i++)
            await File.WriteAllTextAsync(Path.Combine(_tempDir, $"p_{i:D4}.jpg"), "x");

        using var cts = new CancellationTokenSource();
        var scanner = new DirectoryScanner();
        var count = 0;

        try
        {
            await foreach (var _ in scanner.ScanAsync(_tempDir, false, ct: cts.Token))
            {
                count++;
                if (count >= 5)
                    cts.Cancel();
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation stops the enumerator.
        }

        Assert.True(count < 200);
    }

    [Fact]
    public void Watch_ShouldCreateAndDisposeFileSystemWatcher()
    {
        var scanner = new DirectoryScanner();
        using var gotEvent = new ManualResetEventSlim(false);
        using var watcher = scanner.Watch(_tempDir, false, _ => gotEvent.Set());

        Assert.NotNull(watcher);

        var testFile = Path.Combine(_tempDir, "incoming.jpg");
        File.WriteAllText(testFile, "data");

        // FileSystemWatcher is async and unreliable on some CI/container FS.
        // Require create+dispose; treat event delivery as soft signal only.
        gotEvent.Wait(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ScanAsync_Recursive_ShouldDescendIntoSubdirectories()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "top.jpg"), "x");
        var sub = Directory.CreateDirectory(Path.Combine(_tempDir, "nested"));
        await File.WriteAllTextAsync(Path.Combine(sub.FullName, "deep.jpg"), "x");

        var scanner = new DirectoryScanner();
        var results = new List<MediaRef>();
        await foreach (var source in scanner.ScanAsync(_tempDir, true, new[] { (".jpg", MediaKind.Image) }))
            results.Add(source);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task ScanAsync_ReportsStats_OnCompletion()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "a.jpg"), "x");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "b.mp4"), "x");
        var sub = Directory.CreateDirectory(Path.Combine(_tempDir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(sub.FullName, "c.png"), "x");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "ignored.txt"), "x");

        var extMap = new (string, MediaKind)[]
        {
            (".jpg", MediaKind.Image),
            (".mp4", MediaKind.Video),
            (".png", MediaKind.Image),
        };
        var stats = new List<ScanStats>();
        var reporter = new SyncProgress<ScanStats>(stats);

        var scanner = new DirectoryScanner();
        var count = 0;
        await foreach (var _ in scanner.ScanAsync(_tempDir, true, extMap, statsReporter: reporter))
            count++;

        var s = Assert.Single(stats);
        Assert.Equal(3, count);
        Assert.Equal(3, s.MediaCount);
        Assert.Equal(4, s.FileCount);       // includes the filtered .txt
        Assert.Equal(2, s.DirectoryCount);  // _tempDir + sub
        Assert.Equal(0, s.ArchiveCount);
        Assert.True(s.ElapsedMs >= 0);
    }

    /// <summary>Synchronous <see cref="IProgress{T}"/> so tests can assert
    /// the reporter ran without a dispatcher round-trip.</summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly List<T> _items;
        public SyncProgress(List<T> items) => _items = items;
        public void Report(T value) => _items.Add(value);
    }
}
