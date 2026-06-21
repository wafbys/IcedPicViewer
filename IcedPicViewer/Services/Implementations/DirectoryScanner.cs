// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

public class DirectoryScanner : IDirectoryScanner
{
    private static readonly HashSet<string> _recycleBinNames = ["$RECYCLE.BIN", "Recycler", ".trash"];

    private static bool IsRecycleBin(string path)
    {
        var dirName = Path.GetFileName(path);
        return _recycleBinNames.Contains(dirName, StringComparer.OrdinalIgnoreCase);
    }

    public async IAsyncEnumerable<ImageSource> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<string>? extensions = null,
        IProgress<ScanError>? errorReporter = null,
        IProgress<int>? discoveredReporter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var extensionSet = extensions != null
            ? new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase)
            : null;
        var directories = new Queue<string>();
        directories.Enqueue(rootPath);

        // Running count of image sources yielded so far. Reported through
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
                        await foreach (var imageSource in EnumerateArchiveAsync(entry, extensionSet, errorReporter, ct))
                        {
                            discovered++;
                            if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                            yield return imageSource;
                        }
                    }
                    else if (extensionSet == null || extensionSet.Contains(Path.GetExtension(entry)))
                    {
                        discovered++;
                        if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                        yield return ImageSource.FromFile(entry);
                    }
                }
            }
        }
    }

    private static async IAsyncEnumerable<ImageSource> EnumerateArchiveAsync(
        string archivePath,
        HashSet<string>? extensionSet,
        IProgress<ScanError>? errorReporter,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
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
            yield return ImageSource.FromArchive(archivePath, entry.Key);
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
