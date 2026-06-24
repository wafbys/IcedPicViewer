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
                        // are not actionable for the user). Archive entries
                        // are still image-only in this release (videos inside
                        // archives are out of scope) so EnumerateArchiveAsync
                        // only takes the image-only extension set.
                        if (currentPathReporter is not null) currentPathReporter.Report(entry);
                        await foreach (var imageSource in EnumerateArchiveAsync(entry, GetImageOnlyExtensions(extensions), errorReporter, ct))
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
            // Archive entries are image-only in this release — Kind is left
            // at the record-struct default (Image). When/if we add video
            // archive support, mirror the loose-file lookup here.
            yield return ImageSource.FromArchive(archivePath, entry.Key);
        }
    }

    /// <summary>
    /// Pulls the image-only extension list out of the (extension, kind)
    /// tuple list passed to <see cref="ScanAsync"/>. Used by the archive
    /// path because video entries inside archives are out of scope for
    /// this release. Returns null when the caller passed no filter
    /// (scanner falls back to "list every entry in the archive").
    /// </summary>
    private static HashSet<string>? GetImageOnlyExtensions(
        IEnumerable<(string Extension, MediaKind Kind)>? extensions)
    {
        if (extensions is null) return null;
        HashSet<string>? result = null;
        foreach (var (ext, kind) in extensions)
        {
            if (kind != MediaKind.Image) continue;
            result ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            result.Add(ext);
        }
        return result;
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
