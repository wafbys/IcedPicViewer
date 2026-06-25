// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

public class DirectoryScanner : IDirectoryScanner
{
    private static readonly HashSet<string> _recycleBinNames = new(
        ["$RECYCLE.BIN", "Recycler", "RECYCLED"], StringComparer.OrdinalIgnoreCase);

    private static bool IsRecycleBin(string path)
    {
        return _recycleBinNames.Contains(Path.GetFileName(path));
    }

    public async IAsyncEnumerable<ImageSource> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<(string Extension, MediaKind Kind)>? extensions = null,
        IProgress<ScanError>? errorReporter = null,
        IProgress<int>? discoveredReporter = null,
        IProgress<string>? currentPathReporter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build a (lowercase extension → kind) lookup once, outside the
        // directory loop. The caller passes the combined image+video list
        // (see IImageLoader.SupportedMedia); null means "no filter, default
        // everything to Image". The dictionary avoids per-file string
        // allocations and lets the inner loop use a single hash lookup.
        Dictionary<string, MediaKind>? extensionMap = null;
        if (extensions != null)
        {
            extensionMap = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, kind) in extensions)
            {
                extensionMap[ext] = kind;
            }
        }

        var directories = new Queue<string>();
        directories.Enqueue(rootPath);

        // Running count of media sources yielded so far. Reported through
        // discoveredReporter (when supplied) so callers can show a live scan
        // progress, e.g. when opening a whole drive where the scan can run
        // for tens of seconds. IProgress<T>.Report is fire-and-forget on the
        // captured sync context, so it does not stall the scan loop.
        var discovered = 0;

        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var currentDir = directories.Dequeue();

            if (IsRecycleBin(currentDir)) continue;

            // Announce the directory *before* the blocking GetFileSystemEntries
            // call. On a slow NTFS folder that call can take several seconds,
            // and reporting after it would leave the status bar stuck on the
            // previous folder during that window.
            if (currentPathReporter is not null) currentPathReporter.Report(currentDir);

            string[] entries;
            try
            {
                entries = await Task.Run(() => Directory.GetFileSystemEntries(currentDir), ct);
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (Directory.Exists(entry))
                {
                    if (recursive && !IsRecycleBin(entry))
                    {
                        directories.Enqueue(entry);
                    }
                }
                else if (File.Exists(entry))
                {
                    if (ArchiveHelper.IsArchiveFileName(entry) && ArchiveHelper.IsArchive(entry))
                    {
                        // For archive enumeration we report the archive's own
                        // path (not the entry key — entry keys are
                        // archive-internal paths like "folder/img.jpg" and
                        // are not actionable for the user). v0.14.2+ archive
                        // entries include both image and video kinds; the
                        // full (image+video) extension map is passed through
                        // so EnumerateArchiveAsync can stamp the right Kind
                        // on each yielded source. The downstream VM +
                        // services dispatch on Kind for metadata + thumbnail
                        // extraction; VideoMetadataService handles the
                        // archive case by extracting to a temp file.
                        if (currentPathReporter is not null) currentPathReporter.Report(entry);
                        await foreach (var imageSource in EnumerateArchiveAsync(entry, extensionMap, errorReporter, ct))
                        {
                            discovered++;
                            if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                            yield return imageSource;
                        }
                    }
                    else
                    {
                        // Loose file: classify by extension. If no filter
                        // was passed, default to Image (the record-struct
                        // default kind). The lookup is O(1) once the map
                        // is built, and Path.GetExtension is the only per-
                        // file allocation.
                        var ext = Path.GetExtension(entry);
                        MediaKind kind = MediaKind.Image;
                        bool include = true;
                        if (extensionMap != null)
                        {
                            if (!extensionMap.TryGetValue(ext, out kind))
                            {
                                include = false;
                            }
                        }
                        if (!include) continue;

                        discovered++;
                        if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                        yield return ImageSource.FromFile(entry, kind);
                    }
                }
            }
        }
    }

    private static async IAsyncEnumerable<ImageSource> EnumerateArchiveAsync(
        string archivePath,
        Dictionary<string, MediaKind>? extensionMap,
        IProgress<ScanError>? errorReporter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // Translate the (extension → kind) map into the
        // extension-only hash set that ArchiveHelper.ListEntries
        // expects for its own filter parameter. ListEntries is
        // deliberately kind-agnostic — the kind lives in the
        // caller's dispatch, not in the archive helper. We do the
        // kind lookup here per-entry to keep the archive helper
        // simple and to make the scanner's contract explicit: "give
        // me entries whose extension is in this set, I'll tag them
        // with the right kind on the way out".
        HashSet<string>? extensionSet = null;
        if (extensionMap != null)
        {
            extensionSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ext in extensionMap.Keys)
            {
                extensionSet.Add(ext);
            }
        }

        List<ArchiveEntryInfo> entries;
        try
        {
            // ListEntries returns a lazy IEnumerable (it's a generator). If we
            // await the raw IEnumerable out of Task.Run, the lambda finishes
            // without ever enumerating — the first MoveNext happens later in
            // the foreach below, OUTSIDE this try/catch, so any exception
            // (e.g. "Cannot determine compressed stream type" on a .7z file
            // that SharpCompress 0.49.1 doesn't support) escapes the generator
            // and bubbles all the way up to LoadDirectoryAsync's outer catch,
            // which sets StatusText = "Error: ..." and leaves the gallery
            // empty — even files after the bad archive are dropped.
            //
            // .ToList() forces enumeration inside the lambda, so the exception
            // (if any) is raised on the await line and caught here. One bad
            // archive → reported to status bar + skipped, scan continues.
            entries = await Task.Run(() => ArchiveHelper.ListEntries(archivePath, extensionSet).ToList(), ct);
        }
        catch (OperationCanceledException)
        {
            yield break;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DirectoryScanner: failed to enumerate {archivePath}: {ex.Message}");
            errorReporter?.Report(new ScanError(archivePath, ClassifyArchiveError(ex)));
            yield break;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            // Look up the kind from the entry's extension. Falls back to
            // Image (the record-struct default) for an entry whose
            // extension isn't in the map — a defensive default in case
            // the filter was set to a different superset than what
            // ListEntries saw (e.g., if a future caller passes a
            // permissive filter).
            var ext = Path.GetExtension(entry.Key);
            var kind = extensionMap != null && extensionMap.TryGetValue(ext, out var k)
                ? k
                : MediaKind.Image;
            yield return ImageSource.FromArchive(archivePath, entry.Key, kind);
        }
    }

    /// <summary>
    /// Maps a SharpCompress / IO exception to a short reason suitable for
    /// the status bar. We don't surface the raw exception message because
    /// it's usually technical (e.g. "Cannot determine compressed stream
    /// type") and not actionable.
    /// </summary>
    private static string ClassifyArchiveError(Exception ex) => ex switch
    {
        FileNotFoundException => "file missing",
        IOException => "I/O error",
        UnauthorizedAccessException => "access denied",
        _ => "unsupported or corrupt archive"
    };

    public IDisposable Watch(string rootPath, bool recursive, Action<FileChangeInfo> onChanged)
    {
        var watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };

        watcher.Created += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Created, e.FullPath));
        watcher.Deleted += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Deleted, e.FullPath));
        watcher.Renamed += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Renamed, e.FullPath, e.OldFullPath));
        watcher.Changed += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Modified, e.FullPath));

        watcher.EnableRaisingEvents = true;

        return watcher;
    }
}
