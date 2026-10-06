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
    private readonly IThumbnailDiskCache _thumbnailDiskCache;

    /// <summary>
    /// Read buffer for source files. 4 KB was measurably expensive: WIC reads a
    /// large JPEG through the WinRT stream shim in many small requests, and every
    /// one of them went through FileStream's tiny buffer. Measured on a real
    /// 5.7 MB / 4592x3448 photo (mobile HDD, 6 samples × 2 passes, production
    /// decode path): 4 KB = 408/464 ms, 64 KB = 318/311 ms, 256 KB = 314/257 ms,
    /// 1 MB = 351/338 ms. So ~25-35 % of the per-image cost was this buffer.
    /// It does not change what is decoded — only how the bytes get read.
    /// </summary>
    private const int SourceReadBufferSize = 256 * 1024;

    public MediaLoader(IThumbnailCache thumbnailCache, IThumbnailDiskCache thumbnailDiskCache)
    {
        _thumbnailCache = thumbnailCache;
        _thumbnailDiskCache = thumbnailDiskCache;
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
                bufferSize: SourceReadBufferSize,
                useAsync: true);
            return await Task.FromResult(fileStream);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadImageStreamAsync error for {media}: {ex.Message}");
            return null;
        }
    }

    public async Task<CachedThumb?> LoadThumbnailAsync(MediaRef media, int maxSize, CancellationToken ct = default)
    {
        var cacheKey = $"{media}|{maxSize}|{media.Kind}";
        if (_thumbnailCache.TryGet(cacheKey, out var cached) && cached is not null)
            return cached;

        ct.ThrowIfCancellationRequested();

        // Persisted cache first: on a re-open this decodes a small thumbnail
        // instead of the full source, which is the difference between "cards
        // fill in slowly" and "images are already there".
        var fromDisk = await _thumbnailDiskCache.TryGetAsync(media, maxSize, ct);
        if (fromDisk is { } disk)
        {
            _thumbnailCache.Store(cacheKey, disk);
            return disk;
        }

        CachedThumb? thumb;
        if (media.IsInArchive)
        {
            thumb = await LoadThumbnailFromArchiveAsync(media, maxSize, ct);
        }
        else
        {
            if (!File.Exists(media.Path)) return null;
            thumb = await LoadThumbnailFromFileAsync(media.Path, maxSize, ct);
        }
        if (thumb is { } t)
        {
            _thumbnailCache.Store(cacheKey, t);
            // Persisting returns as soon as the bitmap is cloned: the disk cache
            // owns a bounded encode pool (PNG compression is ~5-10x a JPEG
            // encode), so the gallery is not held back by it, and the clone —
            // not this bitmap — is what the encoder touches.
            await _thumbnailDiskCache.StoreAsync(media, maxSize, t, ct);
        }
        return thumb;
    }

    /// <summary>Top-down BGRA8 → SoftwareBitmap (gallery thumbs, no PNG).</summary>
    public static SoftwareBitmap? CreateSoftwareBitmap(byte[] bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
            return null;
        return SoftwareBitmap.CreateCopyFromBuffer(
            bgra.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            width,
            height,
            BitmapAlphaMode.Premultiplied);
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

    public async Task<WinImageSource?> LoadFullAsync(MediaRef media, int? targetMaxSize = 5120, CancellationToken ct = default)
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
                            bufferSize: SourceReadBufferSize,
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
            Trace.TraceError($"LoadFullAsync error for {media}: {ex.Message}");
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
                InterpolationMode = BitmapInterpolationMode.Fant,
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight
            };

            // GetSoftwareBitmapAsync applies scaling and EXIF orientation in a
            // single WIC pass and hands back a bitmap whose PixelWidth/Height
            // already reflect the final (oriented) size. Encoding that bitmap
            // directly keeps the encoder's declared size in lockstep with the
            // pixel buffer. The previous GetPixelDataAsync + DetachPixelData
            // path could not expose the post-rotation size (PixelDataProvider
            // has no Width/Height), so a 90°-rotated photo was encoded with
            // swapped width/height and came out sheared / garbled.
            var sb = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            ct.ThrowIfCancellationRequested();
            if (sb is null) return null;

            using var pngStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, pngStream);
            encoder.SetSoftwareBitmap(sb);
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

    private static async Task<CachedThumb?> LoadThumbnailFromFileAsync(string path, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var fileStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: SourceReadBufferSize,
                FileOptions.Asynchronous);
            return await DecodeToSoftwareBitmapAsync(fileStream.AsRandomAccessStream(), maxSize, ct);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadThumbnailAsync error for {path}: {ex.Message}");
            return null;
        }
    }

    private static async Task<CachedThumb?> LoadThumbnailFromArchiveAsync(MediaRef media, int maxSize, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var entryStream = ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
            return await DecodeToSoftwareBitmapAsync(entryStream.AsRandomAccessStream(), maxSize, ct);
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
                bufferSize: SourceReadBufferSize,
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

    /// <summary>
    /// Dimensions to hand to <see cref="BitmapTransform.ScaledWidth"/>/<c>ScaledHeight</c>
    /// for the requested longest edge.
    ///
    /// <para>
    /// MUST be computed from the <b>source</b> pixel size (<see cref="BitmapFrame.PixelWidth"/>/
    /// <c>PixelHeight</c>), never the EXIF-oriented size: <see cref="BitmapTransform"/> applies
    /// scale <i>before</i> flip/rotate, so ScaledWidth/ScaledHeight live in the source image's
    /// coordinate space (see the BitmapTransform docs). Using
    /// <see cref="BitmapFrame.OrientedPixelWidth"/>/<c>OrientedPixelHeight</c> here swaps the
    /// axes for EXIF-rotated (portrait phone) photos: the configured scale then squashes the
    /// unrotated image and the post-rotation result has the wrong aspect ratio, and a full-size
    /// encode gets its declared size out of sync with the pixel buffer.
    /// </para>
    /// </summary>
    private static (uint ScaledWidth, uint ScaledHeight) ComputeScaledDimensions(
        BitmapDecoder decoder, int? targetMaxSize)
    {
        var sourceWidth = (int)decoder.PixelWidth;
        var sourceHeight = (int)decoder.PixelHeight;

        if (!targetMaxSize.HasValue)
            return ((uint)sourceWidth, (uint)sourceHeight);

        var longest = Math.Max(sourceWidth, sourceHeight);
        if (longest <= targetMaxSize.Value)
            return ((uint)sourceWidth, (uint)sourceHeight);

        if (sourceWidth >= sourceHeight)
        {
            var w = (uint)targetMaxSize.Value;
            var h = (uint)Math.Max(1, (long)Math.Round((double)sourceHeight * targetMaxSize.Value / sourceWidth));
            return (w, h);
        }
        else
        {
            var h = (uint)targetMaxSize.Value;
            var w = (uint)Math.Max(1, (long)Math.Round((double)sourceWidth * targetMaxSize.Value / sourceHeight));
            return (w, h);
        }
    }

    private static async Task<CachedThumb?> DecodeToSoftwareBitmapAsync(
        IRandomAccessStream stream,
        int? targetMaxSize,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        ct.ThrowIfCancellationRequested();
        try
        {
            var decoder = await BitmapDecoder.CreateAsync(stream);
            if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;
            ct.ThrowIfCancellationRequested();

            var originalWidth = (int)decoder.OrientedPixelWidth;
            var originalHeight = (int)decoder.OrientedPixelHeight;
            var (scaledWidth, scaledHeight) = ComputeScaledDimensions(decoder, targetMaxSize);

            var transform = new BitmapTransform
            {
                InterpolationMode = BitmapInterpolationMode.Fant,
                ScaledWidth = scaledWidth,
                ScaledHeight = scaledHeight
            };

            // GetSoftwareBitmapAsync takes the same transform / EXIF / color arguments
            // as GetPixelDataAsync but hands back a SoftwareBitmap directly. The old
            // GetPixelDataAsync + DetachPixelData() path allocated a managed byte[]
            // (≈1.5 MB for a 768-edge thumb, far more for a large source that had to
            // be scaled) and then CreateCopyFromBuffer copied it a second time. WIC
            // decodes, scales and applies EXIF straight into the SoftwareBitmap's
            // buffer instead, so the intermediate array is gone entirely.
            //
            // scaledWidth/Height are source-space (see ComputeScaledDimensions);
            // the returned bitmap is already EXIF-oriented, so its PixelWidth/Height
            // are the correct display dimensions. originalWidth/Height stay oriented
            // as well and are only used for metadata / clarity decisions.
            var sb = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            ct.ThrowIfCancellationRequested();
            if (sb is null) return null;
            return new CachedThumb(sb, originalWidth, originalHeight);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DecodeToSoftwareBitmapAsync error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            ThumbnailTimings.Add(ThumbnailPhase.ImageDecode, Stopwatch.GetTimestamp() - started);
        }
    }
}
