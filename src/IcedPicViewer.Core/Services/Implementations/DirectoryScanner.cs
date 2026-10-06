// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

public class DirectoryScanner : IDirectoryScanner
{
    private static readonly HashSet<string> _recycleBinNames = new(
        ["$RECYCLE.BIN", "Recycler", "RECYCLED"], StringComparer.OrdinalIgnoreCase);

    private readonly IScanCache? _scanCache;

    /// <summary>
    /// <paramref name="scanCache"/> is optional: when null the scanner always
    /// walks the filesystem (tests, or a shell that opts out). Production
    /// injects a <see cref="FileScanCache"/> via DI.
    /// </summary>
    public DirectoryScanner(IScanCache? scanCache = null) => _scanCache = scanCache;

    public static bool IsRecycleBin(string path)
    {
        var current = path;
        while (current != null)
        {
            if (_recycleBinNames.Contains(Path.GetFileName(current)))
                return true;
            current = Path.GetDirectoryName(current);
        }
        return false;
    }

    public async IAsyncEnumerable<MediaRef> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<(string Extension, MediaKind Kind)>? extensions = null,
        IProgress<ScanError>? errorReporter = null,
        IProgress<int>? discoveredReporter = null,
        IProgress<string>? currentPathReporter = null,
        IProgress<ScanStats>? statsReporter = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Build a (lowercase extension → kind) lookup once, outside the
        // directory loop. The caller passes the combined image+video list
        // (see IMediaLoader.SupportedMedia); null means "no filter, default
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

        var normalizedRoot = NormalizeRoot(rootPath);

        // Cache is used only for the production path, where a media extension
        // filter is always supplied. A null filter means "every file as Image",
        // which a cache built from a media filter cannot represent, so we fall
        // back to a plain walk and do not save.
        var cacheEnabled = _scanCache is not null && extensionMap is not null;
        ScanCacheSnapshot? loadedSnapshot = null;
        ScanCacheSnapshot? saveSnapshot = null;
        if (cacheEnabled)
        {
            var signature = ComputeExtensionsSignature(extensions!);
            // A missing/corrupt/expired cache is normal on the first run; fall
            // back to an empty snapshot so every directory is enumerated.
            loadedSnapshot = _scanCache!.Load(rootPath, signature)
                ?? new ScanCacheSnapshot
                {
                    RootPath = normalizedRoot,
                    ExtensionsSignature = signature,
                };
            // Rebuilt fresh: only directories actually visited this run are
            // emitted, so deleted/moved directories fall out of the cache.
            saveSnapshot = new ScanCacheSnapshot
            {
                RootPath = normalizedRoot,
                ExtensionsSignature = signature,
            };
        }

        var directories = new Queue<string>();
        directories.Enqueue(rootPath);

        // P1 instrumentation counters. ElapsedMs fields are wall-clock and
        // only for relative comparison; see ScanStats.
        var discovered = 0;
        var directoryCount = 0;
        var fileCount = 0;
        var archiveCount = 0;
        long sumDirectoryMs = 0;
        long archiveMs = 0;
        var totalSw = Stopwatch.StartNew();

        while (directories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var currentDir = directories.Dequeue();

            if (IsRecycleBin(currentDir)) continue;

            directoryCount++;

            // Announce the directory before any blocking work so a slow folder
            // does not leave the status bar stuck on the previous one.
            if (currentPathReporter is not null) currentPathReporter.Report(currentDir);

            var relPath = RelativePath(normalizedRoot, currentDir);
            var dirMtimeTicks = GetDirMtimeUtcTicks(currentDir);

            CachedDirectory? cached = null;
            if (cacheEnabled && loadedSnapshot!.Directories.TryGetValue(relPath, out var c))
            {
                cached = c;
            }

            // A directory is reusable when its mtime is unchanged. NTFS bumps a
            // directory's mtime on child add/remove/rename, so this catches the
            // cases that matter for a viewer (new/deleted media).
            var reuse = cached is not null && cached.DirMtimeUtcTicks == dirMtimeTicks;
            var dirEntry = new CachedDirectory { RelativePath = relPath, DirMtimeUtcTicks = dirMtimeTicks };
            var cacheable = true;
            var dirSw = Stopwatch.StartNew();

            if (reuse)
            {
                foreach (var sub in cached!.Subdirectories)
                {
                    dirEntry.Subdirectories.Add(sub);
                    if (recursive)
                    {
                        var full = Path.Combine(currentDir, sub);
                        if (!IsRecycleBin(full)) directories.Enqueue(full);
                    }
                }

                fileCount += cached.Files.Count;
                foreach (var f in cached.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    dirEntry.Files.Add(f);
                    if (extensionMap!.TryGetValue(Path.GetExtension(f.Name), out var kind))
                    {
                        discovered++;
                        if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                        yield return MediaRef.FromFile(Path.Combine(currentDir, f.Name), kind);
                    }
                }

                // Archives are validated per file, not by the containing
                // directory's mtime: an in-place archive edit does not change
                // the directory mtime.
                foreach (var a in cached.Archives)
                {
                    ct.ThrowIfCancellationRequested();
                    var full = Path.Combine(currentDir, a.Name);
                    var (size, ticks) = StatFile(full);
                    if (size == a.Size && ticks == a.MtimeUtcTicks)
                    {
                        dirEntry.Archives.Add(a);
                        foreach (var e in a.Entries)
                        {
                            discovered++;
                            if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                            yield return MediaRef.FromArchive(full, e.Key, e.Kind);
                        }
                    }
                    else
                    {
                        archiveCount++;
                        if (currentPathReporter is not null) currentPathReporter.Report(full);
                        var archiveSw = Stopwatch.StartNew();
                        var listed = await ListArchiveEntriesAsync(full, extensionMap, errorReporter, ct);
                        archiveMs += archiveSw.ElapsedMilliseconds;
                        var ca = new CachedArchive { Name = a.Name, Size = size, MtimeUtcTicks = ticks };
                        foreach (var e in listed)
                        {
                            ca.Entries.Add(e);
                            discovered++;
                            if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                            yield return MediaRef.FromArchive(full, e.Key, e.Kind);
                        }
                        dirEntry.Archives.Add(ca);
                    }
                }
            }
            else
            {
                IEnumerator<FileSystemInfo>? enumerator = null;
                try
                {
                    // P0: enumerate FileSystemInfo, not path strings. The old
                    // Directory.GetFileSystemEntries + Directory.Exists +
                    // File.Exists sequence cost two extra metadata syscalls per
                    // entry; the FileSystemInfo returned here already carries the
                    // attributes from the single FindNextFile pass.
                    enumerator = new DirectoryInfo(currentDir).EnumerateFileSystemInfos().GetEnumerator();
                    while (true)
                    {
                        ct.ThrowIfCancellationRequested();

                        FileSystemInfo info;
                        try
                        {
                            if (!enumerator.MoveNext()) break;
                            info = enumerator.Current;
                        }
                        catch (UnauthorizedAccessException) { cacheable = false; break; }
                        catch (DirectoryNotFoundException) { cacheable = false; break; }
                        catch (IOException) { cacheable = false; break; }

                        bool isDirectory;
                        try
                        {
                            isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
                        {
                            // The entry vanished between enumeration and
                            // attribute read.
                            continue;
                        }

                        if (isDirectory)
                        {
                            dirEntry.Subdirectories.Add(Path.GetFileName(info.FullName));
                            if (recursive && !IsRecycleBin(info.FullName)) directories.Enqueue(info.FullName);
                            continue;
                        }

                        fileCount++;
                        var entry = info.FullName;

                        if (ArchiveHelper.IsArchiveFileName(entry) && ArchiveHelper.IsArchive(entry))
                        {
                            archiveCount++;
                            if (currentPathReporter is not null) currentPathReporter.Report(entry);
                            var archiveSw = Stopwatch.StartNew();
                            var listed = await ListArchiveEntriesAsync(entry, extensionMap, errorReporter, ct);
                            archiveMs += archiveSw.ElapsedMilliseconds;

                            var (size, ticks) = StatFile(entry);
                            var ca = new CachedArchive
                            {
                                Name = Path.GetFileName(entry),
                                Size = size,
                                MtimeUtcTicks = ticks,
                            };
                            foreach (var e in listed)
                            {
                                ca.Entries.Add(e);
                                discovered++;
                                if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                                yield return MediaRef.FromArchive(entry, e.Key, e.Kind);
                            }
                            dirEntry.Archives.Add(ca);
                        }
                        else
                        {
                            // Loose file: classify by extension. With no filter
                            // (null map) every file is included as Image.
                            var ext = Path.GetExtension(entry);
                            MediaKind kind = MediaKind.Image;
                            bool include = true;
                            if (extensionMap is not null)
                            {
                                if (!extensionMap.TryGetValue(ext, out kind))
                                {
                                    include = false;
                                }
                            }
                            if (!include) continue;

                            dirEntry.Files.Add(new CachedFile(Path.GetFileName(entry), kind));
                            discovered++;
                            if (discoveredReporter is not null) discoveredReporter.Report(discovered);
                            yield return MediaRef.FromFile(entry, kind);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        enumerator?.Dispose();
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        Trace.TraceError($"DirectoryScanner: enumerator dispose failed for {currentDir}: {ex.Message}");
                    }
                }
            }

            sumDirectoryMs += dirSw.ElapsedMilliseconds;

            // Only cache a directory whose enumeration completed; a partially
            // enumerated (access-denied / vanished) directory would otherwise
            // be served from cache as if it were empty.
            if (cacheEnabled && cacheable)
            {
                saveSnapshot!.Directories[relPath] = dirEntry;
            }
        }

        // Persist only on a completed scan. Archive listing happens inside the
        // per-directory timing, so subtract it to keep EnumerateMs about pure
        // enumeration.
        if (cacheEnabled && !ct.IsCancellationRequested)
        {
            saveSnapshot!.SavedUtcTicks = DateTime.UtcNow.Ticks;
            _scanCache!.Save(saveSnapshot);
        }

        statsReporter?.Report(new ScanStats(
            DirectoryCount: directoryCount,
            FileCount: fileCount,
            ArchiveCount: archiveCount,
            MediaCount: discovered,
            ElapsedMs: totalSw.ElapsedMilliseconds,
            EnumerateMs: Math.Max(0, sumDirectoryMs - archiveMs),
            ArchiveMs: archiveMs));
    }

    /// <summary>
    /// Lists an archive's media entries, resolving each entry's kind from the
    /// extension map. Returns an empty list (and reports a scan error) when the
    /// archive cannot be read, so one bad archive never aborts the scan.
    /// </summary>
    private static async Task<List<CachedArchiveEntry>> ListArchiveEntriesAsync(
        string archivePath,
        Dictionary<string, MediaKind>? extensionMap,
        IProgress<ScanError>? errorReporter,
        CancellationToken ct)
    {
        HashSet<string>? extensionSet = null;
        if (extensionMap is not null)
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
            // ListEntries is a lazy generator: awaiting the raw IEnumerable out
            // of Task.Run would return before enumerating, and the first
            // MoveNext (which can throw, e.g. SharpCompress choking on a .7z)
            // would happen outside this try/catch and abort the whole scan.
            // .ToList() forces enumeration inside the lambda so one bad archive
            // is reported and skipped instead.
            entries = await Task.Run(() => ArchiveHelper.ListEntries(archivePath, extensionSet).ToList(), ct);
        }
        catch (OperationCanceledException)
        {
            return new List<CachedArchiveEntry>();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"DirectoryScanner: failed to enumerate {archivePath}: {ex.Message}");
            errorReporter?.Report(new ScanError(archivePath, ClassifyArchiveError(ex)));
            return new List<CachedArchiveEntry>();
        }

        var result = new List<CachedArchiveEntry>(entries.Count);
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            // Fall back to Image for an entry whose extension isn't in the map
            // (the record-struct default).
            var ext = Path.GetExtension(entry.Key);
            var kind = extensionMap is not null && extensionMap.TryGetValue(ext, out var k)
                ? k
                : MediaKind.Image;
            result.Add(new CachedArchiveEntry(entry.Key, kind));
        }
        return result;
    }

    private static long GetDirMtimeUtcTicks(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).LastWriteTimeUtc.Ticks;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"DirectoryScanner: directory mtime failed for {dir}: {ex.Message}");
            return -1;
        }
    }

    private static (long Size, long Ticks) StatFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1L, -1L);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (-1L, -1L);
        }
    }

    private static string RelativePath(string normalizedRoot, string dir)
    {
        var normalizedDir = NormalizeRoot(dir);
        if (string.Equals(normalizedRoot, normalizedDir, StringComparison.OrdinalIgnoreCase)) return "";
        return Path.GetRelativePath(normalizedRoot, normalizedDir);
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ComputeExtensionsSignature(IEnumerable<(string Extension, MediaKind Kind)> extensions)
    {
        var parts = extensions
            .Select(e => $"{e.Extension.ToLowerInvariant()}:{(int)e.Kind}")
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join(";", parts);
    }

    /// <summary>
    /// Maps a SharpCompress / IO exception to a short reason suitable for
    /// the status bar. We don't surface the raw exception message because
    /// it's usually technical (e.g. "Cannot determine compressed stream
    /// type") and not actionable.
    /// </summary>
    private static string ClassifyArchiveError(Exception ex) => ex switch
    {
        FileNotFoundException => IcedPicViewer.Core.Text.UiCopy.ArchiveFileMissing,
        IOException => IcedPicViewer.Core.Text.UiCopy.ArchiveIoError,
        UnauthorizedAccessException => IcedPicViewer.Core.Text.UiCopy.ArchiveAccessDenied,
        _ => IcedPicViewer.Core.Text.UiCopy.ArchiveUnsupportedOrCorrupt
    };

    public IDisposable Watch(string rootPath, bool recursive, Action<FileChangeInfo> onChanged)
    {
        var watcher = new FileSystemWatcher(rootPath)
        {
            IncludeSubdirectories = recursive,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
            // Default 8 KB overflows quickly in directories with hundreds
            // of files — each event costs ~16 B + path length.  64 KB
            // handles ~3 000 short-path events simultaneously, which is
            // enough for the burst of Created events during a full scan
            // or a bulk delete in Explorer.
            InternalBufferSize = 65536
        };

        watcher.Created += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Created, e.FullPath));
        watcher.Deleted += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Deleted, e.FullPath));
        watcher.Renamed += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Renamed, e.FullPath, e.OldFullPath));
        watcher.Changed += (_, e) => onChanged(new FileChangeInfo(WatchChangeType.Modified, e.FullPath));
        watcher.Error += (_, e) =>
        {
            var ex = e.GetException();
            Trace.TraceError($"FileSystemWatcher error for {rootPath}: {ex.GetType().Name}: {ex.Message}");
            // Toggle EnableRaisingEvents to restart the internal
            // buffer — without this the watcher stays dead after an
            // InternalBufferOverflowException or similar fatal error.
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex2)
            {
                Trace.TraceError($"FileSystemWatcher restart failed for {rootPath}: {ex2.GetType().Name}: {ex2.Message}");
            }
        };

        watcher.EnableRaisingEvents = true;

        return watcher;
    }
}
