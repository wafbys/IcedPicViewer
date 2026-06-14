// Copyright (c) IcedPicViewer. All rights reserved.

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Models;

public partial class ImageItem : ObservableObject
{
    public ImageSource Source { get; private set; }
    public string Id { get; private set; }
    public string Name { get; private set; }
    public long FileSize { get; }
    public DateTime ModifiedTime { get; }
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }

    [ObservableProperty]
    private BitmapImage? _thumbnail;

    [ObservableProperty]
    private BitmapImage? _fullImage;

    public ImageItem(
        ImageSource source,
        long fileSize,
        DateTime modifiedTime,
        int originalWidth,
        int originalHeight)
    {
        Source = source;
        Id = source.ToString();
        Name = source.IsInArchive
            ? Path.GetFileName(source.ArchiveEntry!)
            : Path.GetFileName(source.Path);
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
    /// Original pixel dimensions formatted as "WIDTH×HEIGHT", or "Unknown" when
    /// the size could not be determined (corrupt file, unsupported format, etc.).
    /// Avoids the misleading "0×0" you would get from binding OriginalWidth/Height
    /// directly when the metadata is missing.
    /// </summary>
    public string OriginalSizeText => OriginalWidth > 0 && OriginalHeight > 0
        ? $"{OriginalWidth}×{OriginalHeight}"
        : "Unknown";

    /// <summary>
    /// Where this image physically lives, formatted for the masonry overlay:
    ///   - loose file: parent directory (e.g. <c>C:\Users\foo\photos\sub</c>)
    ///   - archive entry: the archive's own filename (e.g. <c>photos.zip</c>),
    ///     since the entry path inside the archive is not a real on-disk path
    ///     and showing the archive's full path makes the overlay too long
    ///     to be useful in a small tile.
    /// </summary>
    public string DisplayLocation
    {
        get
        {
            if (Source.IsInArchive)
            {
                return Path.GetFileName(Source.Path);
            }
            var dir = Path.GetDirectoryName(Source.Path);
            return string.IsNullOrEmpty(dir) ? "" : dir;
        }
    }

    /// <summary>
    /// Rebind this item to a new on-disk path. Used when the FileSystemWatcher
    /// reports a rename — keeps the same ImageItem instance but updates its
    /// identity, name, and source. Callers are responsible for re-inserting
    /// the item into any collection so indexes refresh.
    /// </summary>
    public void UpdateSource(ImageSource newSource)
    {
        Source = newSource;
        Id = newSource.ToString();
        Name = newSource.IsInArchive
            ? Path.GetFileName(newSource.ArchiveEntry!)
            : Path.GetFileName(newSource.Path);
    }
}
