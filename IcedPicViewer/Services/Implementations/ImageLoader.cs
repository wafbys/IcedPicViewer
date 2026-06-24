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

        if (source.IsInArchive)
        {
            return await LoadThumbnailFromArchiveAsync(source, maxSize, cacheKey, ct);
        }

        if (!File.Exists(source.Path)) return null;
        return await LoadThumbnailFromFileAsync(source.Path, maxSize, cacheKey, ct);
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

    private async Task<BitmapImage?> LoadThumbnailFromFileAsync(string path, int maxSize, string cacheKey, CancellationToken ct)
    {
        try
        {
            var bitmapImage = new BitmapImage { DecodePixelWidth = maxSize };

            // Stream the file directly to the WIC decoder. With DecodePixelWidth
            // set, the decoder pulls only enough bytes to produce the target
            // resolution — no need to buffer the whole image into RAM.
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            await bitmapImage.SetSourceAsync(fileStream.AsRandomAccessStream());

            _thumbnailCache.Store(cacheKey, bitmapImage);
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadThumbnailAsync error for {path}: {ex.Message}");
            return null;
        }
    }

    private async Task<BitmapImage?> LoadThumbnailFromArchiveAsync(ImageSource source, int maxSize, string cacheKey, CancellationToken ct)
    {
        try
        {
            var bitmapImage = new BitmapImage { DecodePixelWidth = maxSize };

            // ArchiveHelper.OpenEntryStream already materialises the entry into
            // a seekable MemoryStream (SharpCompress's stream is forward-only
            // and a non-seekable IRandomAccessStream makes BitmapImage render
            // black). Pass it straight through; DecodePixelWidth keeps WIC
            // from decoding the full-resolution image into managed memory.
            using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            await bitmapImage.SetSourceAsync(entryStream.AsRandomAccessStream());

            _thumbnailCache.Store(cacheKey, bitmapImage);
            return bitmapImage;
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
            // Use BitmapDecoder (not BitmapImage) so we only read the image header —
            // for a 4000x3000 photo this is ~few KB / few ms instead of decoding
            // 36 MB of pixel data. We need the *original* dimensions for the
            // info overlay; the thumbnail loader still does its own (scaled) decode.
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var decoder = await BitmapDecoder.CreateAsync(fileStream.AsRandomAccessStream());
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
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
            using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            var decoder = await BitmapDecoder.CreateAsync(entryStream.AsRandomAccessStream());
            return ((int)decoder.PixelWidth, (int)decoder.PixelHeight);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"GetImageSizeAsync archive error for {source}: {ex.Message}");
            return null;
        }
    }
}
