// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IcedPicViewer.Services.Implementations;

public class ImageLoader : IImageLoader
{
    private static readonly HashSet<string> _supportedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
         ".tiff", ".tif", ".ico", ".avif", ".heic"];

    // Video formats the gallery can surface. Decoded via FFmpeg (not
    // BitmapDecoder), but listed here so the scanner / FileSystemWatcher
    // "is this a file I care about" check has a single source of truth.
    // FFmpeg actually covers far more than this list (mkv/mov/mp4/m4v
    // family, plus avi, flv, webm, ts, m2ts, wmv, asf...), but for the
    // first pass we keep the surface narrow and add formats as users
    // actually have them on disk. The user spec calls out this exact set.
    private static readonly HashSet<string> _supportedVideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv"];

    // Thumbnail LRU is injected as IThumbnailCache so the video pipeline
    // (VideoMetadataService) shares the same backing store. Capacity
    // tuning lives in the ThumbnailCache implementation.
    private readonly IThumbnailCache _thumbnailCache;

    public ImageLoader(IThumbnailCache thumbnailCache)
    {
        _thumbnailCache = thumbnailCache;
    }

    public IEnumerable<string> SupportedExtensions => _supportedExtensions;

    public IEnumerable<string> SupportedVideoExtensions => _supportedVideoExtensions;

    public IEnumerable<(string Extension, MediaKind Kind)> SupportedMedia { get; } =
        BuildSupportedMedia(_supportedExtensions, _supportedVideoExtensions);

    private static IEnumerable<(string Extension, MediaKind Kind)> BuildSupportedMedia(
        HashSet<string> images, HashSet<string> videos)
    {
        // Emitted in a stable order (image first, then video) so unit tests
        // and any diagnostic dump see a deterministic sequence.
        foreach (var ext in images)
        {
            yield return (ext, MediaKind.Image);
        }
        foreach (var ext in videos)
        {
            yield return (ext, MediaKind.Video);
        }
    }

    public bool IsSupportedFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return _supportedExtensions.Contains(ext) || _supportedVideoExtensions.Contains(ext);
    }

    public MediaKind GetKindForFile(string path)
    {
        // Video check first: a path with a known video extension is
        // unambiguously a video. Everything else (image, unknown,
        // no extension) falls through to Image — consistent with
        // ImageSource's record-struct default.
        var ext = Path.GetExtension(path);
        if (_supportedVideoExtensions.Contains(ext)) return MediaKind.Video;
        return MediaKind.Image;
    }

    public async Task<Stream?> LoadImageStreamAsync(ImageSource source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (source.IsInArchive)
        {
            try
            {
                return ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"LoadImageStreamAsync archive error for {source}: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(source.Path)) return null;
        try
        {
            // Caller owns the stream and is responsible for disposing it.
            // No intermediate byte[] buffer — keeps large images (e.g. RAW) off the LOH.
            var fileStream = new FileStream(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            return await Task.FromResult(fileStream);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadImageStreamAsync error for {source}: {ex.Message}");
            return null;
        }
    }

    public async Task<BitmapImage?> LoadThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default)
    {
        // Cache key includes the source's MediaKind so a path that's
        // been decoded as an image doesn't collide with a (rare) future
        // decode of the same path as a video. Source.ToString() (the
        // path/!entry id) doesn't include the kind tag by design — two
        // files with the same path and different kinds can't coexist
        // on a real filesystem, but a thumbnail cache is per-process
        // and the kind is part of the cache's identity, not the
        // filesystem's.
        var cacheKey = $"{source}|{maxSize}|{source.Kind}";
        if (_thumbnailCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        ct.ThrowIfCancellationRequested();
        BitmapImage? bitmapImage;
        if (source.IsInArchive)
        {
            bitmapImage = await LoadThumbnailFromArchiveAsync(source, maxSize, ct);
        }
        else
        {
            if (!File.Exists(source.Path)) return null;
            bitmapImage = await LoadThumbnailFromFileAsync(source.Path, maxSize, ct);
        }
        if (bitmapImage != null)
        {
            _thumbnailCache.Store(cacheKey, bitmapImage);
        }
        return bitmapImage;
    }

    public async Task<(int Width, int Height)?> GetImageSizeAsync(ImageSource source, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (source.IsInArchive)
        {
            return await GetSizeFromArchiveAsync(source, ct);
        }

        if (!File.Exists(source.Path)) return null;
        return await GetSizeFromFileAsync(source.Path, ct);
    }

    public async Task<BitmapImage?> LoadFullImageAsync(ImageSource source, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            if (source.IsInArchive)
            {
                // OpenEntryStream materialises the entry into a
                // MemoryStream (the whole point of the helper). For a
                // 50 MB JPEG that's still small; for a 4K video frame
                // we'd be talking 30+ MB but the archive is the wrong
                // place for video anyway.
                using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
                return await DecodeToBitmapImageAsync(entryStream.AsRandomAccessStream(), targetMaxSize: null, ct);
            }
            if (!File.Exists(source.Path)) return null;
            using var fileStream = new FileStream(
                source.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            return await DecodeToBitmapImageAsync(fileStream.AsRandomAccessStream(), targetMaxSize: null, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadFullImageAsync error for {source}: {ex.Message}");
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadThumbnailFromFileAsync(string path, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            return await DecodeToBitmapImageAsync(fileStream.AsRandomAccessStream(), maxSize, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadThumbnailAsync error for {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task<BitmapImage?> LoadThumbnailFromArchiveAsync(ImageSource source, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            return await DecodeToBitmapImageAsync(entryStream.AsRandomAccessStream(), maxSize, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadThumbnailAsync archive error for {source}: {ex.Message}");
            return null;
        }
    }

    private static async Task<(int Width, int Height)?> GetSizeFromFileAsync(string path, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous);
            var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
            // OrientedPixelWidth/Height accounts for EXIF rotation, so
            // a 4000x3000 EXIF-6 photo reports 3000x4000 here — same
            // as the gallery overlay's W×H text and the masonry card's
            // aspect ratio. Without this, portrait photos display
            // sideways and the card height is wrong.
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"GetImageSizeAsync error for {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task<(int Width, int Height)?> GetSizeFromArchiveAsync(ImageSource source, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            var decoder = await BitmapDecoder.CreateAsync(entryStream.AsRandomAccessStream());
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"GetImageSizeAsync archive error for {source}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Common EXIF-aware decode path shared by the thumbnail and the
    /// full-image loaders. The pipeline is:
    /// 1. Open a <see cref="BitmapDecoder"/> over the input stream.
    /// 2. Compute target dimensions — if <paramref name="targetMaxSize"/>
    ///    is null we keep the oriented resolution at full quality
    ///    (full-image path); otherwise we scale to the longer-edge
    ///    bound using the oriented aspect ratio (so a 4000x3000 EXIF-6
    ///    thumbnail ends up 300x225, not 300x400).
    /// 3. Pull oriented pixel data via
    ///    <see cref="BitmapDecoder.GetPixelDataAsync"/> with
    ///    <see cref="ExifOrientationMode.RespectExifOrientation"/> — this
    ///    is what rotates the pixels for us, no manual EXIF read.
    /// 4. Re-encode as PNG into an in-memory stream and feed that
    ///    stream to a fresh <see cref="BitmapImage"/>.
    ///
    /// <para>
    /// The PNG round-trip is the trade-off: <c>BitmapImage.SetSourceAsync</c>
    /// takes a stream (not pixel data), so we have to encode. For
    /// thumbnails the cost is sub-millisecond on the gallery's
    /// 6-wide semaphore; for the full image the user expects a brief
    /// load and the encode time is dominated by the disk read anyway.
    /// </para>
    /// </summary>
    private static async Task<BitmapImage?> DecodeToBitmapImageAsync(
        IRandomAccessStream stream,
        int? targetMaxSize,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;
            ct.ThrowIfCancellationRequested();

            // Use oriented dimensions for layout math. The decoder
            // applies the EXIF rotation in GetPixelDataAsync below, so
            // the output dimensions are also oriented — everything
            // downstream (masonry card height, W×H text, viewer
            // viewport) sees the post-rotation values.
            var orientedWidth = (int)decoder.OrientedPixelWidth;
            var orientedHeight = (int)decoder.OrientedPixelHeight;

            uint scaledWidth;
            uint scaledHeight;
            if (targetMaxSize.HasValue)
            {
                // Scale to fit on the longer edge, preserving the
                // oriented aspect ratio. Math.Max(1, ...) guards the
                // degenerate 1xN / Nx1 case where the rounding would
                // otherwise produce zero.
                if (orientedWidth >= orientedHeight)
                {
                    scaledWidth = (uint)targetMaxSize.Value;
                    scaledHeight = (uint)Math.Max(1, (long)Math.Round((double)orientedHeight * targetMaxSize.Value / orientedWidth));
                }
                else
                {
                    scaledHeight = (uint)targetMaxSize.Value;
                    scaledWidth = (uint)Math.Max(1, (long)Math.Round((double)orientedWidth * targetMaxSize.Value / orientedHeight));
                }
            }
            else
            {
                scaledWidth = (uint)orientedWidth;
                scaledHeight = (uint)orientedHeight;
            }

            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Linear,
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight
            };

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            ct.ThrowIfCancellationRequested();
            var bytes = pixelData.DetachPixelData();

            // PNG-encode back into a stream so BitmapImage can ingest
            // it via SetSourceAsync. The encoder is configured for the
            // already-oriented BGRA8 pixels we just decoded.
            var bitmapImage = new BitmapImage();
            using var pngStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                scaledWidth,
                scaledHeight,
                96.0,
                96.0,
                bytes);
            await encoder.FlushAsync();
            pngStream.Seek(0);
            await bitmapImage.SetSourceAsync(pngStream);
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DecodeToBitmapImageAsync error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
