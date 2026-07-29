// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinImageSource = Microsoft.UI.Xaml.Media.ImageSource;

namespace IcedPicViewer.Services.Implementations;

public class MediaLoader : IMediaLoader
{
    private readonly IThumbnailCache _thumbnailCache;

    public MediaLoader(IThumbnailCache thumbnailCache)
    {
        _thumbnailCache = thumbnailCache;
    }

    public IEnumerable<string> SupportedExtensions => MediaCatalog.ImageExtensions;

    public IEnumerable<string> SupportedVideoExtensions => MediaCatalog.VideoExtensions;

    public IEnumerable<(string Extension, MediaKind Kind)> SupportedMedia => MediaCatalog.SupportedMedia;

    public bool IsSupportedFormat(string path) => MediaCatalog.IsSupported(path);

    public MediaKind GetKindForFile(string path) => MediaCatalog.GetKind(path);

    public async Task<Stream?> LoadImageStreamAsync(MediaRef media, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (media.IsInArchive)
        {
            try
            {
                return ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"LoadImageStreamAsync archive error for {media}: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(media.Path)) return null;
        try
        {
            var fileStream = new FileStream(
                media.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            return await Task.FromResult(fileStream);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadImageStreamAsync error for {media}: {ex.Message}");
            return null;
        }
    }

    public async Task<BitmapImage?> LoadThumbnailAsync(MediaRef media, int maxSize, CancellationToken ct = default)
    {
        var cacheKey = $"{media}|{maxSize}|{media.Kind}";
        if (_thumbnailCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        ct.ThrowIfCancellationRequested();
        BitmapImage? bitmapImage;
        if (media.IsInArchive)
        {
            bitmapImage = await LoadThumbnailFromArchiveAsync(media, maxSize, ct);
        }
        else
        {
            if (!File.Exists(media.Path)) return null;
            bitmapImage = await LoadThumbnailFromFileAsync(media.Path, maxSize, ct);
        }
        if (bitmapImage != null)
        {
            _thumbnailCache.Store(cacheKey, bitmapImage);
        }
        return bitmapImage;
    }

    public async Task<(int Width, int Height)?> GetImageSizeAsync(MediaRef media, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (media.IsInArchive)
        {
            return await GetSizeFromArchiveAsync(media, ct);
        }

        if (!File.Exists(media.Path)) return null;
        return await GetSizeFromFileAsync(media.Path, ct);
    }

    public async Task<WinImageSource?> LoadFullImageAsync(MediaRef media, int? targetMaxSize = 5120, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            byte[]? pngBytes;
            if (media.IsInArchive)
            {
                pngBytes = await Task.Run(
                    async () =>
                    {
                        using var entryStream = ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
                        return await EncodeToPngBytesAsync(entryStream.AsRandomAccessStream(), targetMaxSize, ct);
                    },
                    ct);
            }
            else
            {
                if (!File.Exists(media.Path)) return null;
                pngBytes = await Task.Run(
                    async () =>
                    {
                        using var fileStream = new FileStream(
                            media.Path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read,
                            bufferSize: 4096,
                            FileOptions.Asynchronous);
                        return await EncodeToPngBytesAsync(fileStream.AsRandomAccessStream(), targetMaxSize, ct);
                    },
                    ct);
            }
            if (pngBytes == null) return null;

            var pngStream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(pngStream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(pngBytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            pngStream.Seek(0);

            var bitmapImage = new BitmapImage();
            await bitmapImage.SetSourceAsync(pngStream);
            return bitmapImage;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadFullImageAsync error for {media}: {ex.Message}");
            return null;
        }
    }

    private static async Task<byte[]?> EncodeToPngBytesAsync(
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

            using var reader = new DataReader(pngStream.GetInputStreamAt(0));
            var length = (uint)pngStream.Size;
            await reader.LoadAsync(length);
            var pngBytes = new byte[length];
            reader.ReadBytes(pngBytes);
            return pngBytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"EncodeToPngBytesAsync error: {ex.GetType().Name}: {ex.Message}");
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

    private static async Task<BitmapImage?> LoadThumbnailFromArchiveAsync(MediaRef media, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var entryStream = ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
            return await DecodeToBitmapImageAsync(entryStream.AsRandomAccessStream(), maxSize, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadThumbnailAsync archive error for {media}: {ex.Message}");
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

    private static async Task<(int Width, int Height)?> GetSizeFromArchiveAsync(MediaRef media, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var entryStream = ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
            var decoder = await BitmapDecoder.CreateAsync(entryStream.AsRandomAccessStream());
            return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"GetImageSizeAsync archive error for {media}: {ex.Message}");
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
