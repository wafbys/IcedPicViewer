// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

/// <summary>
/// Identifies a single image, which may live either on disk as a regular file
/// or inside an archive (zip / rar / 7z / tar / gz).
///
/// <para>
/// <see cref="Path"/> is always the on-disk path: for a regular file, that's
/// the file itself; for an archive entry, that's the archive file that
/// contains the entry.
/// </para>
///
/// <para>
/// <see cref="ArchiveEntry"/> is <c>null</c> for regular files, and the
/// entry's path inside the archive (as reported by SharpCompress via
/// <c>IEntry.Key</c>) for archive entries.
/// </para>
///
/// <para>
/// <see cref="ToString"/> produces <c>"path"</c> for regular files and
/// <c>"path!entry"</c> for archive entries. It is used as a unique key for
/// the gallery's path-to-item index and for the thumbnail LRU cache, so it
/// must be stable and round-trippable.
/// </para>
/// </summary>
public readonly record struct ImageSource(string Path, string? ArchiveEntry)
{
    public bool IsInArchive => ArchiveEntry is not null;

    public static ImageSource FromFile(string path) => new(path, null);

    public static ImageSource FromArchive(string archivePath, string entryPath)
        => new(archivePath, entryPath);

    public override string ToString() => IsInArchive ? $"{Path}!{ArchiveEntry}" : Path;
}
