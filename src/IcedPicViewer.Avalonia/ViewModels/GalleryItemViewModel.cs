// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IcedPicViewer.Models;

namespace IcedPicViewer.Avalonia.ViewModels;

public partial class GalleryItemViewModel : ViewModelBase
{
    public ImageSource Source { get; }

    public string Name { get; }

    /// <summary>Loose file: parent dir; archive entry: archive file name.</summary>
    public string DisplayPath { get; }

    public bool IsVideo => Source.Kind == MediaKind.Video;

    public long FileSize { get; }

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
    /// Preferred tile height for masonry before/after thumbnail decode.
    /// Default square; updated when <see cref="Thumbnail"/> arrives.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TileHeight))]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(InfoLine))]
    public partial double AspectRatio { get; set; } = 1.0;

    public double TileHeight => 200.0 * (AspectRatio > 0 ? AspectRatio : 1.0);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(InfoLine))]
    public partial int PixelWidth { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(InfoLine))]
    public partial int PixelHeight { get; set; }

    /// <summary>e.g. "4032×3024" or empty until known.</summary>
    public string SizeText => PixelWidth > 0 && PixelHeight > 0
        ? $"{PixelWidth}×{PixelHeight}"
        : "";

    /// <summary>Hover / tooltip second line: "W×H · 2.1 MB".</summary>
    public string InfoLine
    {
        get
        {
            if (!string.IsNullOrEmpty(SizeText))
                return $"{SizeText} · {FileSizeText}";
            return FileSizeText;
        }
    }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial Bitmap? FullImage { get; set; }

    [ObservableProperty]
    public partial bool IsThumbnailLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFullImageLoading { get; set; }

    public GalleryItemViewModel(ImageSource source, long fileSize = 0)
    {
        Source = source;
        FileSize = fileSize;
        Name = source.IsInArchive
            ? Path.GetFileName(source.ArchiveEntry!)
            : Path.GetFileName(source.Path);
        DisplayPath = source.IsInArchive
            ? Path.GetFileName(source.Path)
            : (Path.GetDirectoryName(source.Path) ?? source.Path);
    }

    public static GalleryItemViewModel FromSource(ImageSource source)
    {
        long size = 0;
        try
        {
            if (source.IsInArchive)
            {
                // Best-effort: archive file size as stand-in when entry size unknown.
                if (File.Exists(source.Path))
                    size = new FileInfo(source.Path).Length;
            }
            else if (File.Exists(source.Path))
            {
                size = new FileInfo(source.Path).Length;
            }
        }
        catch
        {
            size = 0;
        }

        return new GalleryItemViewModel(source, size);
    }

    public void ApplyThumbnail(Bitmap? bitmap, int originalWidth = 0, int originalHeight = 0)
    {
        Thumbnail = bitmap;
        if (originalWidth > 0 && originalHeight > 0)
        {
            PixelWidth = originalWidth;
            PixelHeight = originalHeight;
            AspectRatio = originalHeight / (double)originalWidth;
        }
        else if (bitmap is not null && bitmap.PixelSize.Width > 0)
        {
            // Fallback aspect from thumb pixels only (not for SizeText if already set).
            AspectRatio = bitmap.PixelSize.Height / (double)bitmap.PixelSize.Width;
            if (PixelWidth <= 0)
            {
                PixelWidth = bitmap.PixelSize.Width;
                PixelHeight = bitmap.PixelSize.Height;
            }
        }
        IsThumbnailLoading = false;
    }
}
