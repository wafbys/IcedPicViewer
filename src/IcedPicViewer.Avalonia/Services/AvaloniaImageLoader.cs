// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;
using SkiaImage = SixLabors.ImageSharp.Image;
using AvBitmap = Avalonia.Media.Imaging.Bitmap;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Image decode via ImageSharp (EXIF) and video first-frame via
/// <see cref="VideoFrameExtractor"/> (FFmpeg).
/// </summary>
public static class AvaloniaImageLoader
{
    public static async Task<AvBitmap?> LoadThumbnailAsync(ImageSource source, int maxEdge, CancellationToken ct)
    {
        var (bmp, _, _, _) = await LoadThumbnailWithInfoAsync(source, maxEdge, ct).ConfigureAwait(false);
        return bmp;
    }

    /// <summary>
    /// Thumbnail plus original pixel size and optional video duration for gallery hover info.
    /// </summary>
    public static async Task<(AvBitmap? Bitmap, int OriginalWidth, int OriginalHeight, TimeSpan? Duration)> LoadThumbnailWithInfoAsync(
        ImageSource source, int maxEdge, CancellationToken ct)
    {
        if (source.Kind == MediaKind.Video)
        {
            var frame = await VideoFrameExtractor.ExtractAsync(source, maxEdge, ct).ConfigureAwait(false);
            if (frame is null) return (null, 0, 0, null);
            var f = frame.Value;
            var bmp = await Task.Run(() => BgraToBitmap(f.Bgra, f.FrameWidth, f.FrameHeight), ct)
                .ConfigureAwait(false);
            // Prefer container source size for InfoLine; fall back to scaled frame.
            var ow = f.SourceWidth > 0 ? f.SourceWidth : f.FrameWidth;
            var oh = f.SourceHeight > 0 ? f.SourceHeight : f.FrameHeight;
            return (bmp, ow, oh, f.Duration);
        }

        ct.ThrowIfCancellationRequested();
        try
        {
            await using var stream = await OpenStreamAsync(source, ct).ConfigureAwait(false);
            if (stream is null) return (null, 0, 0, null);

            return await Task.Run(() =>
            {
                if (stream.CanSeek) stream.Position = 0;
                var info = SkiaImage.Identify(stream);
                var ow = info?.Width ?? 0;
                var oh = info?.Height ?? 0;
                if (stream.CanSeek) stream.Position = 0;
                var bmp = DecodeWithExif(stream, maxEdge);
                return (bmp, ow, oh, (TimeSpan?)null);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AvaloniaImageLoader thumb+info: {source}: {ex.Message}");
            return (null, 0, 0, null);
        }
    }

    public static Task<AvBitmap?> LoadFullAsync(ImageSource source, int maxEdge, CancellationToken ct)
    {
        if (source.Kind == MediaKind.Video)
            return LoadVideoFrameAsync(source, maxEdge, ct);
        return LoadScaledAsync(source, maxEdge, ct);
    }

    private static async Task<AvBitmap?> LoadVideoFrameAsync(ImageSource source, int maxEdge, CancellationToken ct)
    {
        try
        {
            var frame = await VideoFrameExtractor.ExtractAsync(source, maxEdge, ct).ConfigureAwait(false);
            if (frame is null) return null;
            var f = frame.Value;
            return await Task.Run(() => BgraToBitmap(f.Bgra, f.FrameWidth, f.FrameHeight), ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AvaloniaImageLoader video: {source}: {ex.Message}");
            return null;
        }
    }

    private static async Task<AvBitmap?> LoadScaledAsync(ImageSource source, int maxEdge, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await using var stream = await OpenStreamAsync(source, ct).ConfigureAwait(false);
            if (stream is null) return null;

            return await Task.Run(() => DecodeWithExif(stream, maxEdge), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AvaloniaImageLoader: {source}: {ex.Message}");
            return null;
        }
    }

    private static Task<Stream?> OpenStreamAsync(ImageSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (source.IsInArchive)
        {
            try
            {
                Stream ms = ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
                return Task.FromResult<Stream?>(ms);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"AvaloniaImageLoader archive open: {source}: {ex.Message}");
                return Task.FromResult<Stream?>(null);
            }
        }

        if (!File.Exists(source.Path)) return Task.FromResult<Stream?>(null);

        Stream fs = new FileStream(
            source.Path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return Task.FromResult<Stream?>(fs);
    }

    private static AvBitmap DecodeWithExif(Stream stream, int maxEdge)
    {
        if (stream.CanSeek) stream.Position = 0;

        using var image = SkiaImage.Load(stream);
        image.Mutate(ctx => ctx.AutoOrient());

        var w = image.Width;
        var h = image.Height;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("Invalid image dimensions");

        var longest = Math.Max(w, h);
        if (longest > maxEdge)
        {
            var scale = maxEdge / (double)longest;
            var tw = Math.Max(1, (int)Math.Round(w * scale));
            var th = Math.Max(1, (int)Math.Round(h * scale));
            image.Mutate(ctx => ctx.Resize(tw, th));
        }

        var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        ms.Position = 0;
        return new AvBitmap(ms);
    }

    /// <summary>Top-down BGRA8 → Avalonia WriteableBitmap.</summary>
    public static AvBitmap BgraToBitmap(byte[] bgra, int width, int height)
    {
        var wb = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var fb = wb.Lock())
        {
            var srcStride = width * 4;
            var dstStride = fb.RowBytes;
            if (srcStride == dstStride)
            {
                Marshal.Copy(bgra, 0, fb.Address, Math.Min(bgra.Length, dstStride * height));
            }
            else
            {
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(
                        bgra,
                        y * srcStride,
                        fb.Address + y * dstStride,
                        srcStride);
                }
            }
        }

        return wb;
    }
}
