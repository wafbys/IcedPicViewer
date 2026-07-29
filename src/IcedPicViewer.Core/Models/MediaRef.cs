// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

/// <summary>
/// Identifies a single media file (image or video), which may live either
/// on disk as a regular file or inside an archive (zip / rar / tar / gz; not 7z).
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
/// <see cref="Kind"/> is set by the scanner from the file extension. It
/// does NOT change after construction (an <c>.mp4</c> file is always a
/// video), so the value is safe to compare with <c>==</c> and to use as
/// the input to dispatch tables.
/// </para>
///
/// <para>
/// <see cref="ToString"/> produces <c>"path"</c> for regular files and
/// <c>"path!entry"</c> for archive entries. It is used as a unique key for
/// the gallery's path-to-item index and for the thumbnail LRU cache, so it
/// must be stable and round-trippable. <see cref="Kind"/> is intentionally
/// NOT part of the key — two files with the same path and different kinds
/// cannot coexist on a real filesystem, so a single key per path is
/// sufficient.
/// </para>
/// </summary>
public readonly record struct MediaRef(string Path, string? ArchiveEntry, MediaKind Kind = MediaKind.Image)
{
    public bool IsInArchive => ArchiveEntry is not null;

    public static MediaRef FromFile(string path, MediaKind kind = MediaKind.Image)
        => new(path, null, kind);

    public static MediaRef FromArchive(string archivePath, string entryPath, MediaKind kind = MediaKind.Image)
        => new(archivePath, entryPath, kind);

    public override string ToString() => IsInArchive ? $"{Path}!{ArchiveEntry}" : Path;
}
