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
        var cacheKey = $"{source}|{maxSize}";
        if (TryGetCached(cacheKey, out var cached))
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

            SetCached(cacheKey, bitmapImage);
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

            SetCached(cacheKey, bitmapImage);
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
