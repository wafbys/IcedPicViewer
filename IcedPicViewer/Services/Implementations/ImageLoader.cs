// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
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

    // Bounded LRU cache for thumbnails. Cap is intentionally modest: a 400px BitmapImage
    // averages ~150-400 KB, so 200 entries ≈ 30-80 MB worst case instead of "unbounded".
    private const int ThumbnailCacheCapacity = 200;
    private readonly Dictionary<string, LinkedListNode<CacheEntry>> _cacheMap = new();
    private readonly LinkedList<CacheEntry> _cacheOrder = new();
    private readonly object _cacheLock = new();

    private readonly record struct CacheEntry(string Key, BitmapImage Image);

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

        var cacheKey = $"{path}|{maxSize}";
        if (TryGetCached(cacheKey, out var cached))
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

            SetCached(cacheKey, bitmapImage);
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

    private bool TryGetCached(string key, out BitmapImage? image)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var node))
            {
                _cacheOrder.Remove(node);
                _cacheOrder.AddLast(node);
                image = node.Value.Image;
                return true;
            }
            image = null;
            return false;
        }
    }

    private void SetCached(string key, BitmapImage image)
    {
        lock (_cacheLock)
        {
            if (_cacheMap.TryGetValue(key, out var existing))
            {
                _cacheOrder.Remove(existing);
                _cacheMap.Remove(key);
            }
            else if (_cacheMap.Count >= ThumbnailCacheCapacity)
            {
                var oldest = _cacheOrder.First;
                if (oldest is not null)
                {
                    _cacheOrder.RemoveFirst();
                    _cacheMap.Remove(oldest.Value.Key);
                }
            }

            var node = new LinkedListNode<CacheEntry>(new CacheEntry(key, image));
            _cacheOrder.AddLast(node);
            _cacheMap[key] = node;
        }
    }
}
