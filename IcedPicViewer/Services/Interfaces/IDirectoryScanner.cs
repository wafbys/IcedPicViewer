namespace IcedPicViewer.Services.Interfaces;

public record FileChangeInfo(WatchChangeType ChangeType, string Path);

public enum WatchChangeType
{
    Created,
    Deleted,
    Renamed,
    Modified
}

public interface IDirectoryScanner
{
    IAsyncEnumerable<string> ScanAsync(
        string rootPath,
        bool recursive,
        IEnumerable<string>? extensions = null,
        CancellationToken ct = default);

    IDisposable Watch(string rootPath, bool recursive, Action<FileChangeInfo> onChanged);
}
