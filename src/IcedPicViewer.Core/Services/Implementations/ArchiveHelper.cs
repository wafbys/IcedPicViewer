// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Lightweight wrapper around SharpCompress for reading image entries out of
/// ZIP / RAR / TAR archives. Extension list still includes <c>.7z</c> for
/// discovery, but SharpCompress cannot open real 7z containers (scan skips /
/// errors). Deliberately read-only: we never modify the archive on disk.
///
/// <para>
/// All public methods are synchronous I/O — callers are expected to be on a
/// background thread (the gallery's thumbnail loader runs each call on a
/// thread-pool worker via the SemaphoreSlim in
/// <c>GalleryViewModel</c>).
/// </para>
/// </summary>
public static class ArchiveHelper
{
    private static readonly HashSet<string> _archiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".tgz"
    };

    // Compound extensions checked separately because Path.GetExtension only
    // returns the last component (".gz" for "foo.tar.gz"). We still treat
    // these as a single "archive" for the FileWatcher and scanner.
    private static readonly string[] _compoundArchiveSuffixes =
    {
        ".tar.gz", ".tar.bz2", ".tar.xz", ".tar.zst"
    };

    public static IReadOnlySet<string> ArchiveExtensions => _archiveExtensions;

    public static bool IsArchiveFileName(string path)
    {
        var name = Path.GetFileName(path);
        foreach (var suffix in _compoundArchiveSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return _archiveExtensions.Contains(Path.GetExtension(path));
    }

    /// <summary>
    /// Cheaply tests whether <paramref name="path"/> is a supported archive by
    /// reading the file's magic bytes via <see cref="ArchiveFactory"/>.
    /// Returns false for missing files or any I/O error — callers treat
    /// unrecognised files as "not an archive" and skip them.
    /// </summary>
    public static bool IsArchive(string path)
    {
        try
        {
            return ArchiveFactory.IsArchive(path, out _);
        }
        catch (IOException ex)
        {
            Trace.TraceError($"ArchiveHelper.IsArchive probe failed for {path}: {ex.Message}");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceError($"ArchiveHelper.IsArchive probe failed for {path}: {ex.Message}");
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // SharpCompress may throw NotSupportedException, InvalidDataException, etc.
            Trace.TraceError($"ArchiveHelper.IsArchive probe failed for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Enumerates non-directory image entries in <paramref name="archivePath"/>.
    /// Entries that fail to be parsed (corrupt header, encrypted, etc.) are
    /// skipped silently — a single bad entry must not abort the whole scan.
    /// </summary>
    /// <param name="extensionFilter">
    /// When non-null, only entries whose <c>Path.GetExtension</c> is in this
    /// set are yielded. Comparison is ordinal, case-insensitive.
    /// </param>
    public static IEnumerable<ArchiveEntryInfo> ListEntries(
        string archivePath,
        HashSet<string>? extensionFilter)
    {
        using var fileStream = OpenArchiveFile(archivePath);
        using var reader = ReaderFactory.OpenReader(fileStream, new ReaderOptions { LeaveStreamOpen = true });
        while (reader.MoveToNextEntry())
        {
            ArchiveEntryInfo? info = null;
            try
            {
                var entry = reader.Entry;
                if (entry is null) continue;
                if (entry.IsDirectory) continue;

                var key = entry.Key;
                if (string.IsNullOrEmpty(key)) continue;

                if (extensionFilter is not null &&
                    !extensionFilter.Contains(Path.GetExtension(key)))
                {
                    continue;
                }

                info = new ArchiveEntryInfo(key, entry.Size);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"ArchiveHelper.ListEntries skipped entry in {archivePath}: {ex.Message}");
                continue;
            }
            if (info is not null) yield return info;
        }
    }

    /// <summary>
    /// Reads the given archive entry into memory and returns a seekable
    /// <see cref="MemoryStream"/> over the decompressed bytes. The caller
    /// owns the returned stream and is responsible for disposing it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SharpCompress's <c>OpenEntryStream</c> is forward-only. Wrapping it
    /// in a custom non-seekable <see cref="Stream"/> subclass and exposing
    /// it as <c>IRandomAccessStream</c> caused
    /// <c>Microsoft.UI.Xaml.Media.Imaging.BitmapImage</c> to render black
    /// (the decoder reads forward-only and ends up in a stuck loading
    /// state). Materialising into a <see cref="MemoryStream"/> here
    /// guarantees a seekable source that the WIC-based decoder handles
    /// correctly.
    /// </para>
    /// <para>
    /// The memory cost is the decompressed entry size; for image formats
    /// (jpg, png, webp, ...) this is typically 1-20 MB, which is fine for
    /// the gallery's per-image loading pattern.
    /// </para>
    /// </remarks>
    /// <exception cref="FileNotFoundException">
    /// The entry was not found, or the entry is a directory marker.
    /// </exception>
    public static MemoryStream OpenEntryStream(string archivePath, string entryKey)
    {
        var fileStream = OpenArchiveFile(archivePath);
        try
        {
            using var reader = ReaderFactory.OpenReader(fileStream, new ReaderOptions { LeaveStreamOpen = true });
            while (reader.MoveToNextEntry())
            {
                if (reader.Entry is null) continue;
                if (reader.Entry.Key == entryKey && !reader.Entry.IsDirectory)
                {
                    using var entryStream = reader.OpenEntryStream();
                    var memory = new MemoryStream();
                    entryStream.CopyTo(memory);
                    memory.Position = 0;
                    return memory;
                }
            }
        }
        finally
        {
            fileStream.Dispose();
        }

        throw new FileNotFoundException(
            $"Entry '{entryKey}' was not found in archive '{archivePath}'.");
    }

    /// <summary>
    /// Extracts a single archive entry directly to a file on disk. Used
    /// by <c>VideoMetadataService</c> to give FFmpeg a seekable file path
    /// (FFmpeg's <c>avformat_open_input</c> only accepts file paths or
    /// custom AVIO contexts; there's no built-in way to feed it a
    /// SharpCompress MemoryStream short of an AVIO read callback, which
    /// is significantly more code). The caller's
    /// <paramref name="destinationPath"/> is overwritten if it exists.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="OpenEntryStream"/>, this is not seekable-friendly
    /// for WIC (FFmpeg seeks internally) but is exactly what FFmpeg
    /// needs: a real file path. Disk cost equals the entry's
    /// decompressed size; for typical video this is the file itself,
    /// 100 MB-2 GB. The caller is expected to clean up the temp file
    /// after the consumer (FFmpeg / MediaPlayer) is done with it.
    /// </remarks>
    /// <exception cref="FileNotFoundException">
    /// The entry was not found, or the entry is a directory marker.
    /// </exception>
    public static void ExtractEntryToFile(string archivePath, string entryKey, string destinationPath)
    {
        using var entryStream = OpenEntryStream(archivePath, entryKey);
        // FileMode.Create overwrites; FileShare.Read keeps the file
        // readable by FFmpeg (which opens it with its own FileStream)
        // after this method returns. The destination's parent directory
        // must already exist — the caller picks the path and is
        // responsible for ensuring its directory tree.
        using var destStream = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: false);
        entryStream.CopyTo(destStream);
    }

    private static FileStream OpenArchiveFile(string archivePath) =>
        new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
}

public sealed record ArchiveEntryInfo(string Key, long UncompressedSize);
