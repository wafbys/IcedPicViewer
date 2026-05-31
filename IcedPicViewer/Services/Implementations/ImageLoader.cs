// Copyright (c) IcedPicViewer. All rights reserved.

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace IcedPicViewer.Services.Implementations;

public class ImageLoader : IImageLoader
{
    private static readonly HashSet<string> _supportedExtensions = [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif", ".ico", ".avif", ".heic"];

    // Simple in-memory cache for thumbnails to avoid re-decoding the same images repeatedly
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapImage> _thumbnailCache = new();

    public IEnumerable<string> SupportedExtensions => _supportedExtensions;

    public bool IsSupportedFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return _supportedExtensions.Contains(ext);
    }

    public async Task<byte[]?> LoadImageAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var memoryStream = new MemoryStream();
            await fileStream.CopyToAsync(memoryStream, ct);
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"LoadImageAsync error for {path}: {ex}");
            return null;
        }
    }

    public async Task<BitmapImage?> LoadThumbnailAsync(string path, int maxSize, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        // Use cached thumbnail if available and file hasn't changed
        var cacheKey = $"{path}|{maxSize}";
        if (_thumbnailCache.TryGetValue(cacheKey, out var cached) && cached != null)
        {
            return cached;
        }

        try
        {
            var bitmapImage = new BitmapImage();
            bitmapImage.DecodePixelWidth = maxSize;

            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await fileStream.CopyToAsync(randomAccessStream.AsStreamForWrite(), ct);
            randomAccessStream.Seek(0);

            await bitmapImage.SetSourceAsync(randomAccessStream);

            // Store in cache (simple unbounded cache for now; can be improved with LRU later)
            _thumbnailCache[cacheKey] = bitmapImage;

            return bitmapImage;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"LoadThumbnailAsync error for {path}: {ex}");
            return null;
        }
    }

    public async Task<(int Width, int Height)?> GetImageSizeAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var bitmapImage = new BitmapImage();
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            using var randomAccessStream = new InMemoryRandomAccessStream();
            await fileStream.CopyToAsync(randomAccessStream.AsStreamForWrite(), ct);
            randomAccessStream.Seek(0);
            await bitmapImage.SetSourceAsync(randomAccessStream);

            return (bitmapImage.PixelWidth, bitmapImage.PixelHeight);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"GetImageSizeAsync error for {path}: {ex}");
            return null;
        }
    }
}
