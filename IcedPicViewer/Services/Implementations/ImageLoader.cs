// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace IcedPicViewer.Services.Implementations;

public class ImageLoader : IImageLoader
{
    private static readonly HashSet<string> _supportedExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
         ".tiff", ".tif", ".ico", ".avif", ".heic"];

    private static readonly HashSet<string> _supportedVideoExtensions =
        [".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv"];

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

    public async Task<WinImageSource?> LoadFullImageAsync(ImageSource source, int? targetMaxSize = 5120, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            SoftwareBitmap? softwareBitmap;
            if (source.IsInArchive)
            {
                softwareBitmap = await Task.Run(
                    async () =>
                    {
                        using var entryStream = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
                        return await DecodeToSoftwareBitmapAsync(entryStream.AsRandomAccessStream(), targetMaxSize, ct);
                    },
                    ct);
            }
            else
            {
                if (!File.Exists(source.Path)) return null;
                softwareBitmap = await Task.Run(
                    async () =>
                    {
                        using var fileStream = new FileStream(
                            source.Path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 4096,
                            FileOptions.Asynchronous);
                        return await DecodeToSoftwareBitmapAsync(fileStream.AsRandomAccessStream(), targetMaxSize, ct);
                    },
                    ct);
            }
            if (softwareBitmap == null) return null;
            if (ct.IsCancellationRequested) return null;

            var sourceBitmap = new SoftwareBitmapSource();
            await sourceBitmap.SetBitmapAsync(softwareBitmap);
            return sourceBitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadFullImageAsync error for {source}: {ex.Message}");
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> DecodeToSoftwareBitmapAsync(
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

            var (scaledWidth, scaledHeight) = ComputeScaledDimensions(decoder, targetMaxSize);

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

            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8,
                (int)scaledWidth,
                (int)scaledHeight,
                BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixelData.DetachPixelData().AsBuffer());
            return softwareBitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DecodeToSoftwareBitmapAsync error: {ex.GetType().Name}: {ex.Message}");
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

    private static (uint ScaledWidth, uint ScaledHeight) ComputeScaledDimensions(
        BitmapDecoder decoder, int? targetMaxSize)
    {
        var orientedWidth = (int)decoder.OrientedPixelWidth;
        var orientedHeight = (int)decoder.OrientedPixelHeight;

        if (!targetMaxSize.HasValue)
            return ((uint)orientedWidth, (uint)orientedHeight);

        if (orientedWidth >= orientedHeight)
        {
            var w = (uint)targetMaxSize.Value;
            var h = (uint)Math.Max(1, (long)Math.Round((double)orientedHeight * targetMaxSize.Value / orientedWidth));
            return (w, h);
        }
        else
        {
            var h = (uint)targetMaxSize.Value;
            var w = (uint)Math.Max(1, (long)Math.Round((double)orientedWidth * targetMaxSize.Value / orientedHeight));
            return (w, h);
        }
    }

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

            var (scaledWidth, scaledHeight) = ComputeScaledDimensions(decoder, targetMaxSize);

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
