// Copyright (c) IcedPicViewer. All rights reserved.

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using IcedPicViewer.Core.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace IcedPicViewer.Models;

/// <summary>
/// Base class for everything the gallery can render: an image
/// (<see cref="ImageItem"/>) or a video (<see cref="VideoItem"/>).
///
/// <para>
/// Holds everything that is identical between the two: the media identity
/// (<see cref="Media"/>, <see cref="Id"/>, <see cref="Name"/>), the on-disk
/// metadata (<see cref="FileSize"/>, <see cref="ModifiedTime"/>), the
/// displayable thumbnails (<see cref="Thumbnail"/>, <see cref="FullImage"/>),
/// and the loading state (<see cref="IsThumbnailLoading"/>). Subclasses add
/// the per-kind extras — pixel dimensions for images, duration / audio flag
/// for videos.
/// </para>
///
/// <para>
/// The class is <c>abstract</c> because there is no meaningful "generic
/// media" — every concrete item has either image-specific or video-specific
/// metadata that has to be filled in by the gallery pipeline. Use
/// <see cref="Media"/>.<see cref="MediaRef.Kind"/> to dispatch on the
/// concrete type when needed; for the gallery template, prefer binding to
/// the properties exposed here (and on the concrete type) and using
/// <see cref="IsVideoVisibility"/> to toggle the &gt; overlay.
/// </para>
/// </summary>
public abstract partial class MediaItem : ObservableObject, IMediaEntry
{
    public MediaRef Media { get; protected set; }
    public string Id { get; protected set; }
    public string Name { get; protected set; }
    public long FileSize { get; }
    public DateTime ModifiedTime { get; }

    // Pixel dimensions of the underlying media, populated from each
    // concrete kind's metadata extractor: BitmapDecoder for images
    // (~ms), FFmpeg codec params for videos (~ms too — no frame
    // decode). Lives on the base because the gallery template binds
    // OriginalSizeText and the image viewer info bar binds width /
    // height directly, and both need to work regardless of which
    // concrete subtype is in the collection. 0 / 0 means "unknown"
    // (corrupt file, format that didn't yield codec params, etc.).
    public int OriginalWidth { get; }
    public int OriginalHeight { get; }

    // True when the current concrete type represents a video. Pure
    // function of Media.Kind (which is immutable after construction),
    // so this is safe to evaluate at any time and to bind with Mode=OneTime.
    public bool IsVideo => Media.Kind == MediaKind.Video;
    public Visibility IsVideoVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial BitmapImage? Thumbnail { get; set; }

    [ObservableProperty]
    public partial WinImageSource? FullImage { get; set; }

    // True while the thumbnail is being decoded on a worker thread. The
    // gallery template overlays a ProgressRing on top of the empty Image
    // while this is true, replacing what would otherwise be a blank /
    // light-coloured placeholder card. The flag is set on construction
    // (the caller is about to schedule LoadThumbnailAsync) and cleared
    // by GalleryViewModel.LoadThumbnailAsync's outer finally — that way
    // every exit path (cache hit, success, decode failure, cancellation)
    // reliably hides the spinner.
    [ObservableProperty]
    public partial bool IsThumbnailLoading { get; set; }

    public Visibility IsThumbnailLoadingVisibility => IsThumbnailLoading
        ? Visibility.Visible
        : Visibility.Collapsed;

    protected MediaItem(MediaRef media, long fileSize, DateTime modifiedTime, int originalWidth, int originalHeight)
    {
        Media = media;
        Id = media.ToString();
        Name = media.IsInArchive
            ? Path.GetFileName(media.ArchiveEntry!)
            : Path.GetFileName(media.Path);
        FileSize = fileSize;
        ModifiedTime = modifiedTime;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        // Construction time = "thumbnail not yet decoded". The gallery
        // template renders a ProgressRing for every item that has this
        // bit set, and GalleryViewModel.LoadThumbnailAsync's finally
        // block clears it. Items that find a valid cached thumbnail in
        // the LRU still go through LoadThumbnailAsync — the finally
        // there clears the bit even on the early-return cache-hit path.
        IsThumbnailLoading = true;
    }

    public string FileSizeText => MediaDisplay.FormatFileSize(FileSize);

    /// <summary>
    /// Subtitle text shown on the gallery card — for images this is
    /// "WIDTH×HEIGHT" (or "Unknown" when the size could not be determined
    /// from a corrupt file), for videos it adds the duration so the user
    /// sees at a glance which videos are short clips vs feature-length
    /// recordings. Kept as a single bound string (not two TextBlocks with
    /// Visibility bindings) so x:Bind on a base <c>MediaItem</c> data
    /// context stays type-safe.
    /// </summary>
    public abstract string OriginalSizeText { get; }

    /// <summary>
    /// Where this item physically lives, formatted for the masonry overlay:
    ///   - loose file: parent directory (e.g. <c>C:\Users\foo\photos\sub</c>)
    ///   - archive entry: the archive's own filename (e.g. <c>photos.zip</c>),
    ///     since the entry path inside the archive is not a real on-disk path
    ///     and showing the archive's full path makes the overlay too long
    ///     to be useful in a small tile.
    ///
    /// <see cref="ImageItem"/> and <see cref="VideoItem"/> both want the
    /// same "where does this live" answer, so the implementation lives on
    /// the base class — subclasses only override the parts that differ.
    /// </summary>
    public string DisplayLocation
    {
        get
        {
            if (Media.IsInArchive)
            {
                return Path.GetFileName(Media.Path);
            }
            var dir = Path.GetDirectoryName(Media.Path);
            return string.IsNullOrEmpty(dir) ? "" : dir;
        }
    }

    /// <summary>
    /// Rebind this item to a new on-disk path. Used when the FileSystemWatcher
    /// reports a rename — keeps the same MediaItem instance but updates its
    /// identity, name, and media. Callers are responsible for re-inserting
    /// the item into any collection so indexes refresh.
    /// </summary>
    public virtual void UpdateMedia(MediaRef media)
    {
        var kindChanged = Media.Kind != media.Kind;
        Media = media;
        Id = media.ToString();
        Name = media.IsInArchive
            ? Path.GetFileName(media.ArchiveEntry!)
            : Path.GetFileName(media.Path);
        if (kindChanged)
        {
            OnPropertyChanged(nameof(IsVideo));
            OnPropertyChanged(nameof(IsVideoVisibility));
        }
    }
}
