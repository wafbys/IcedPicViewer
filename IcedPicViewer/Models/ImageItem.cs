using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Models;

public partial class ImageItem : ObservableObject
{
    public string Id { get; private set; }
    public string Name { get; private set; }
    public string Path { get; private set; }
    public long FileSize { get; }
    public DateTime ModifiedTime { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private BitmapImage? _fullImage;

    public ImageItem(
        string id,
        string name,
        string path,
        long fileSize,
        DateTime modifiedTime,
        int originalWidth,
        int originalHeight)
    {
        Id = id;
        Name = name;
        Path = path;
        FileSize = fileSize;
        ModifiedTime = modifiedTime;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
    }

    public string FileSizeText
    {
        get
        {
            if (FileSize < 1024) return $"{FileSize} B";
            if (FileSize < 1024 * 1024) return $"{FileSize / 1024.0:F1} KB";
            return $"{FileSize / (1024.0 * 1024.0):F1} MB";
        }
    }

    /// <summary>
    /// Rebind this item to a new on-disk path. Used when the FileSystemWatcher
    /// reports a rename — keeps the same ImageItem instance but updates its
    /// identity, name, and path. Callers are responsible for re-inserting the
    /// item into any collection so indexes refresh.
    /// </summary>
    public void UpdatePath(string newPath, string newName)
    {
        Id = newPath;
        Name = newName;
        Path = newPath;
    }
}
