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
    /// Enumerates every supported image in <paramref name="rootPath"/>, both
    /// loose files and entries inside any archives (.zip / .rar / .7z / .tar*).
    /// The caller-supplied <paramref name="extensions"/> filter is applied
    /// uniformly to both loose files and archive entries.
    /// </summary>
    /// <param name="errorReporter">
    /// Optional sink for files that looked like a candidate (matched extension
    /// or magic bytes) but failed to be read. The scanner skips them and
    /// continues — it never aborts the whole scan because of one bad file.
    /// All reports are dispatched through the captured
    /// <see cref="IProgress{T}"/> synchronisation context (typically the UI
    /// thread for VM callers), so the VM does not need its own locking.
    /// </param>
    IAsyncEnumerable<ImageSource> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<string>? extensions = null,
        IProgress<ScanError>? errorReporter = null,
        CancellationToken ct = default);

    IDisposable Watch(string rootPath, bool recursive, Action<FileChangeInfo> onChanged);
}
