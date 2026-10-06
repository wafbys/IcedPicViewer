// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Core.Tests.Services;

public sealed class FileScanCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileScanCache _cache;
    private const string Sig = ".jpg:0;.mp4:1";

    public FileScanCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ipv_scancache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _cache = new FileScanCache(Path.Combine(_tempDir, "cache"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static ScanCacheSnapshot BuildSnapshot(string root)
    {
        var snapshot = new ScanCacheSnapshot
        {
            RootPath = root,
            ExtensionsSignature = Sig,
            SavedUtcTicks = DateTime.UtcNow.Ticks,
        };

        var rootDir = new CachedDirectory { RelativePath = "", DirMtimeUtcTicks = 123 };
        rootDir.Subdirectories.Add("sub");
        rootDir.Files.Add(new CachedFile("a.jpg", MediaKind.Image));
        rootDir.Files.Add(new CachedFile("clip.mp4", MediaKind.Video));
        var archive = new CachedArchive { Name = "pack.zip", Size = 4096, MtimeUtcTicks = 456 };
        archive.Entries.Add(new CachedArchiveEntry("photos/b.png", MediaKind.Image));
        rootDir.Archives.Add(archive);
        snapshot.Directories[""] = rootDir;

        var subDir = new CachedDirectory { RelativePath = "sub", DirMtimeUtcTicks = 789 };
        subDir.Files.Add(new CachedFile("c.jpg", MediaKind.Image));
        snapshot.Directories["sub"] = subDir;

        return snapshot;
    }

    [Fact]
    public void SaveLoad_RoundTrips()
    {
        var root = Path.Combine(_tempDir, "photos");
        _cache.Save(BuildSnapshot(root));

        var loaded = _cache.Load(root, Sig);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Directories.Count);

        var rootDir = loaded.Directories[""];
        Assert.Equal(123, rootDir.DirMtimeUtcTicks);
        Assert.Equal("sub", Assert.Single(rootDir.Subdirectories));
        Assert.Equal(2, rootDir.Files.Count);
        Assert.Contains(rootDir.Files, f => f.Name == "clip.mp4" && f.Kind == MediaKind.Video);
        var archive = Assert.Single(rootDir.Archives);
        Assert.Equal("pack.zip", archive.Name);
        Assert.Equal(4096, archive.Size);
        var entry = Assert.Single(archive.Entries);
        Assert.Equal("photos/b.png", entry.Key);

        var subDir = loaded.Directories["sub"];
        Assert.Equal(789, subDir.DirMtimeUtcTicks);
        Assert.Single(subDir.Files);
    }

    [Fact]
    public void Load_Missing_ReturnsNull()
    {
        Assert.Null(_cache.Load(Path.Combine(_tempDir, "nope"), Sig));
    }

    [Fact]
    public void Load_ExtensionsSignatureMismatch_ReturnsNull()
    {
        var root = Path.Combine(_tempDir, "photos");
        _cache.Save(BuildSnapshot(root));

        Assert.Null(_cache.Load(root, ".jpg:0"));
    }

    [Fact]
    public void Load_DifferentRoot_ReturnsNull()
    {
        _cache.Save(BuildSnapshot(Path.Combine(_tempDir, "photos")));

        Assert.Null(_cache.Load(Path.Combine(_tempDir, "other"), Sig));
    }

    [Fact]
    public void Load_CorruptFile_ReturnsNull()
    {
        var root = Path.Combine(_tempDir, "photos");
        _cache.Save(BuildSnapshot(root));

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_tempDir, "cache"), "scan_*.bin"));
        File.WriteAllText(file, "not a valid scan cache at all");

        Assert.Null(_cache.Load(root, Sig));
    }

    [Fact]
    public void Invalidate_RemovesSnapshot()
    {
        var root = Path.Combine(_tempDir, "photos");
        _cache.Save(BuildSnapshot(root));
        Assert.NotNull(_cache.Load(root, Sig));

        _cache.Invalidate(root);

        Assert.Null(_cache.Load(root, Sig));
    }
}
