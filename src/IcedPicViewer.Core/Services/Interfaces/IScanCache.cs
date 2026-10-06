// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Persists the directory scanner's per-directory results so re-opening a
/// folder can skip enumerating unchanged directories and skip re-listing
/// unchanged archives. Validation model (see <c>FileScanCache</c>):
///   - a directory is reused when its <c>LastWriteTimeUtc</c> matches the
///     cached value (NTFS updates a directory's mtime on child add/remove/rename);
///   - an archive's cached entry list is reused only when the archive file's
///     (size, mtime) still matches, because an in-place archive edit does not
///     change the containing directory's mtime.
/// </summary>
public interface IScanCache
{
    /// <summary>
    /// Loads a snapshot for <paramref name="rootPath"/>. Returns null when
    /// there is no usable cache (missing, expired, corrupt, or the extension
    /// signature no longer matches). Never throws.
    /// </summary>
    ScanCacheSnapshot? Load(string rootPath, string extensionsSignature);

    /// <summary>Atomically writes the snapshot. Never throws.</summary>
    void Save(ScanCacheSnapshot snapshot);

    /// <summary>Deletes the snapshot for <paramref name="rootPath"/>, if any.</summary>
    void Invalidate(string rootPath);
}

/// <summary>A loose media file inside a cached directory.</summary>
public readonly record struct CachedFile(string Name, MediaKind Kind);

/// <summary>An archive entry inside a cached archive.</summary>
public readonly record struct CachedArchiveEntry(string Key, MediaKind Kind);

/// <summary>A cached archive file and its entry list.</summary>
public sealed class CachedArchive
{
    public string Name { get; set; } = "";

    /// <summary>Archive file size in bytes; part of the validity token.</summary>
    public long Size { get; set; }

    /// <summary>Archive <c>LastWriteTimeUtc.Ticks</c>; part of the validity token.</summary>
    public long MtimeUtcTicks { get; set; }

    public List<CachedArchiveEntry> Entries { get; } = new();
}

/// <summary>Everything the scanner found in one directory.</summary>
public sealed class CachedDirectory
{
    /// <summary>Path relative to the scan root; "" for the root itself.</summary>
    public string RelativePath { get; set; } = "";

    public long DirMtimeUtcTicks { get; set; }

    public List<string> Subdirectories { get; } = new();

    /// <summary>Loose media files only (non-media files are not cached).</summary>
    public List<CachedFile> Files { get; } = new();

    public List<CachedArchive> Archives { get; } = new();
}

/// <summary>One root's full per-directory scan index.</summary>
public sealed class ScanCacheSnapshot
{
    public string RootPath { get; set; } = "";

    /// <summary>
    /// Sorted signature of the (extension, kind) set used for the scan. The
    /// snapshot is discarded if the app's supported-media set changes, so a
    /// newly supported extension can never be hidden by an old cache.
    /// </summary>
    public string ExtensionsSignature { get; set; } = "";

    public long SavedUtcTicks { get; set; }

    public Dictionary<string, CachedDirectory> Directories { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}
