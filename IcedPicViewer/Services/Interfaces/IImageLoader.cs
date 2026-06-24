using IcedPicViewer.Models;
using Microsoft.UI.Xaml.Media.Imaging;

// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

public interface IImageLoader
{
    /// <summary>
    /// Image file extensions (with leading dot, lowercase) the gallery
    /// can decode. Used by <c>ArchiveHelper</c> to enumerate image entries
    /// inside archives — videos inside archives are out of scope for the
    /// current release, so the archive listing path stays on this image-only
    /// list. The directory scanner uses <see cref="SupportedMedia"/>
    /// instead so it picks up videos on the loose-file path.
    /// </summary>
    IEnumerable<string> SupportedExtensions { get; }

    /// <summary>
    /// Video file extensions (with leading dot, lowercase) the gallery
    /// surfaces. Decoded by <c>IVideoMetadataService</c> via FFmpeg, not
    /// by this loader. Exposed here so the directory scanner can build
    /// a single (extension, kind) filter that covers both image and video
    /// files in one pass.
    /// </summary>
    IEnumerable<string> SupportedVideoExtensions { get; }

    /// <summary>
    /// Combined (extension, <see cref="MediaKind"/>) list used by the
    /// directory scanner as the single source of truth for "which files
    /// does this app know how to show, and how should each be classified".
    /// Order is not significant; the scanner builds an ordinal-ignore-case
    /// hash set for O(1) membership testing.
    /// </summary>
    IEnumerable<(string Extension, MediaKind Kind)> SupportedMedia { get; }

    /// <summary>
    /// Opens a read stream over the image. The caller takes ownership of
    /// the returned stream and is responsible for disposing it.
    /// Returns null if the source does not exist or cannot be opened.
    /// </summary>
    Task<Stream?> LoadImageStreamAsync(ImageSource source, CancellationToken ct = default);

    Task<BitmapImage?> LoadThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default);

    Task<(int Width, int Height)?> GetImageSizeAsync(ImageSource source, CancellationToken ct = default);

    bool IsSupportedFormat(string path);

    /// <summary>
    /// Classifies a file path by its extension. Returns
    /// <see cref="MediaKind.Video"/> for known video extensions and
    /// <see cref="MediaKind.Image"/> otherwise. Defaulting to
    /// <c>Image</c> (rather than throwing) for unknown / unsupported
    /// extensions means the FileSystemWatcher Created path can call
    /// this unconditionally after an <see cref="IsSupportedFormat"/>
    /// check has already confirmed the file is something we care about.
    /// </summary>
    MediaKind GetKindForFile(string path);
}
