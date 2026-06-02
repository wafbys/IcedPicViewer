// Copyright (c) IcedPicViewer. All rights reserved.

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

    public async IAsyncEnumerable<string> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<string>? extensions = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var extensionSet = extensions != null ? new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase) : null;
        var directories = new Queue<string>();
        directories.Enqueue(rootPath);

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
                    if (extensionSet == null || extensionSet.Contains(Path.GetExtension(entry)))
                    {
                        yield return entry;
                    }
                }
            }
        }
    }

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
