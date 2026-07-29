// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using IcedPicViewer.Core.Text;
using IcedPicViewer.Models;

namespace IcedPicViewer.Avalonia.ViewModels;

/// <summary>
/// Avalonia gallery row. Implements shared <see cref="IMediaEntry"/>;
/// bitmaps stay Avalonia-specific.
/// </summary>
public partial class MediaItemViewModel : ViewModelBase, IMediaEntry
{
    public MediaRef Media { get; }

    public string Id => Media.ToString();

    public string Name { get; }

    /// <summary>Loose file: parent dir; archive entry: archive file name.</summary>
    public string DisplayPath { get; }

    public bool IsVideo => Media.Kind == MediaKind.Video;

    public long FileSize { get; }

    public string FileSizeText => MediaDisplay.FormatFileSize(FileSize);

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

    /// <summary>Video duration from FFmpeg container metadata (null for images / unknown).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InfoLine))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    public partial TimeSpan? Duration { get; set; }

    public string SizeText => MediaDisplay.FormatPixelSize(PixelWidth, PixelHeight);

    public string DurationText => MediaDisplay.FormatDuration(Duration);

    public string InfoLine => MediaDisplay.FormatInfoLine(SizeText, DurationText, FileSizeText);

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial Bitmap? FullImage { get; set; }

    [ObservableProperty]
    public partial bool IsThumbnailLoading { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFullImageLoading { get; set; }

    public MediaItemViewModel(MediaRef media, long fileSize = 0)
    {
        Media = media;
        FileSize = fileSize;
        Name = media.IsInArchive
            ? Path.GetFileName(media.ArchiveEntry!)
            : Path.GetFileName(media.Path);
        DisplayPath = media.IsInArchive
            ? Path.GetFileName(media.Path)
            : (Path.GetDirectoryName(media.Path) ?? media.Path);
    }

    public static MediaItemViewModel FromMedia(MediaRef media)
    {
        long size = 0;
        try
        {
            if (media.IsInArchive)
            {
                if (File.Exists(media.Path))
                    size = new FileInfo(media.Path).Length;
            }
            else if (File.Exists(media.Path))
            {
                size = new FileInfo(media.Path).Length;
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"MediaItemViewModel.FromMedia file size probe failed: {ex.Message}");
            size = 0;
        }

        return new MediaItemViewModel(media, size);
    }

    public void ApplyThumbnail(
        Bitmap? bitmap,
        int originalWidth = 0,
        int originalHeight = 0,
        TimeSpan? duration = null)
    {
        Thumbnail = bitmap;
        if (duration is { } d && d > TimeSpan.Zero)
            Duration = d;

        if (originalWidth > 0 && originalHeight > 0)
        {
            PixelWidth = originalWidth;
            PixelHeight = originalHeight;
            AspectRatio = originalHeight / (double)originalWidth;
        }
        else if (bitmap is not null && bitmap.PixelSize.Width > 0)
        {
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
