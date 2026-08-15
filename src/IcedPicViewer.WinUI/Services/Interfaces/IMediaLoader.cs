// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using WinImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace IcedPicViewer.Services.Interfaces;

public interface IMediaLoader
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
    /// <remarks>
    /// Prefer <see cref="LoadFullAsync"/> over this method for
    /// actual rendering — this raw stream does not apply EXIF
    /// orientation, so a portrait photo shot with EXIF Rotation=6
    /// would be returned sideways. Kept for callers that need the
    /// un-rotated pixel data (e.g., for hashing or file copy).
    /// </remarks>
    Task<Stream?> LoadImageStreamAsync(MediaRef media, CancellationToken ct = default);

    /// <summary>
    /// Loads a full-resolution <see cref="BitmapImage"/> for the source,
    /// with EXIF orientation applied at the pixel level. The returned
    /// bitmap's <c>PixelWidth</c> / <c>PixelHeight</c> reflect the
    /// oriented (post-rotation) dimensions, so a 4000x3000 EXIF-6
    /// portrait photo comes back as a 3000x4000 bitmap — viewer
    /// layout uses this bitmap. Gallery InfoLine W×H comes from the
    /// thumbnail decode (oriented original), not this possibly-capped
    /// bitmap. Returns null if the source can't be opened or decoded.
    /// </summary>
    Task<WinImageSource?> LoadFullAsync(MediaRef media, int? targetMaxSize = 5120, CancellationToken ct = default);

    /// <summary>
    /// Masonry thumbnail as a <see cref="CachedThumb"/> (SoftwareBitmap +
    /// oriented original size). No PNG re-encode.
    /// </summary>
    Task<CachedThumb?> LoadThumbnailAsync(MediaRef media, int maxSize, CancellationToken ct = default);

    /// <summary>
    /// Returns the (oriented) pixel dimensions of the source. EXIF
    /// rotation is applied: a 4000x3000 EXIF-6 photo reports 3000x4000
    /// here. Returns null if the source can't be opened or decoded.
    /// </summary>
    Task<(int Width, int Height)?> GetImageSizeAsync(MediaRef media, CancellationToken ct = default);

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
