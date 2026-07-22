// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;

namespace IcedPicViewer.Services.Interfaces;

public record FileChangeInfo(WatchChangeType ChangeType, string Path, string? OldPath = null);

public enum WatchChangeType
{
    Created,
    Deleted,
    Renamed,
    Modified
}

public interface IDirectoryScanner
{
    /// <summary>
    /// Enumerates every supported media file in <paramref name="rootPath"/>,
    /// both loose files and entries inside archives (.zip / .rar / .tar / tar.*).
    /// The caller-supplied <paramref name="extensions"/> filter is applied
    /// uniformly to both loose files and archive entries (archive entries
    /// are currently image-only — see <c>IImageLoader.SupportedExtensions</c>).
    /// </summary>
    /// <param name="extensions">
    /// Optional list of (extension, kind) pairs to include. When non-null
    /// only files whose extension matches an entry in the list are yielded,
    /// and the yielded <see cref="ImageSource"/> carries the matching
    /// <see cref="MediaKind"/>. When null, every regular file is yielded
    /// with <see cref="MediaKind.Image"/> (the record-struct default).
    /// </param>
    /// <param name="errorReporter">
    /// Optional sink for files that looked like a candidate (matched extension
    /// or magic bytes) but failed to be read. The scanner skips them and
    /// continues — it never aborts the whole scan because of one bad file.
    /// All reports are dispatched through the captured
    /// <see cref="IProgress{T}"/> synchronisation context (typically the UI
    /// thread for VM callers), so the VM does not need its own locking.
    /// </param>
    /// <param name="discoveredReporter">
    /// Optional sink for the running count of media sources the scanner has
    /// yielded so far. Reported roughly once per yielded source; the
    /// Progress&lt;T&gt; post is fire-and-forget on the captured sync context,
    /// so a slow reporter cannot stall the scan loop. The VM is expected to
    /// throttle before raising property change so the UI does not redraw
    /// thousands of times per second on a whole-drive scan.
    /// </param>
    /// <param name="currentPathReporter">
    /// Optional sink for the path the scanner is currently working on.
    /// Reported before entering each directory (so the UI sees it even if
    /// <c>Directory.GetFileSystemEntries</c> blocks for several seconds on
    /// a slow NTFS folder) and before enumerating each archive's contents
    /// (the archive file path is reported, not the entry key — entry keys
    /// are archive-internal and meaningless to the user). The VM is
    /// expected to throttle; without throttling a whole-drive scan would
    /// produce thousands of path reports per second.
    /// </param>
    IAsyncEnumerable<ImageSource> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<(string Extension, MediaKind Kind)>? extensions = null,
        IProgress<ScanError>? errorReporter = null,
        IProgress<int>? discoveredReporter = null,
        IProgress<string>? currentPathReporter = null,
        CancellationToken ct = default);

    IDisposable Watch(string rootPath, bool recursive, Action<FileChangeInfo> onChanged);
}
