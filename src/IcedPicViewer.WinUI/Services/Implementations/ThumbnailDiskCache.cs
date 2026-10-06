// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// On-disk <see cref="IThumbnailDiskCache"/>. One framed file per thumbnail
/// (<see cref="ThumbnailCacheFile"/>), sharded by the first two hex chars of a
/// SHA-256 of the identity+validity key. The key embeds the source's
/// <c>(size, last-write)</c> and <see cref="GenerationVersion"/> (the
/// thumbnail algorithm's generation), so an edited/replaced file — or a fixed
/// decoder — yields a new key and the old entry is reclaimed by the size cap
/// instead of being served.
///
/// <para>
/// Writes are atomic (temp file + <see cref="File.Move(string,string,bool)"/>).
/// A periodic size-cap sweep deletes the oldest thumbnails; it is O(files) and
/// therefore amortised (every <see cref="StoresPerPrune"/> stores) rather than
/// run per write.
/// </para>
/// </summary>
public sealed class ThumbnailDiskCache : IThumbnailDiskCache
{
    // ~1 GB. Thumbnails are small enough that this is thousands of entries; a
    // photo library that fits in memory will not thrash.
    private const long MaxTotalBytes = 1024L * 1024 * 1024;
    private const int StoresPerPrune = 256;
    private const string Extension = ".thumb";

    /// <summary>
    /// Generation tag of the thumbnail-producing code, part of the on-disk
    /// identity. Bump it whenever a change makes previously persisted pixels
    /// wrong: old entries then become unreachable (never served) and are
    /// reclaimed by the size cap. Without this the user keeps seeing the bug
    /// that was already fixed, because the bad pixels are on disk.
    ///
    /// <para>v2: <c>VideoFrameExtractor</c> now honours FFmpeg's row pitch.
    /// v1 video thumbnails whose output width is not a multiple of 8 are
    /// sheared ("乱斜纹") and must be regenerated.</para>
    /// </summary>
    private const int GenerationVersion = 2;

    private readonly string _root;
    private int _storesSincePrune;

    public ThumbnailDiskCache(string cacheRoot)
    {
        _root = cacheRoot;
        try
        {
            Directory.CreateDirectory(cacheRoot);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache: create {cacheRoot} failed: {ex.Message}");
        }

        // Bound whatever a previous session left behind, off the UI thread.
        _ = Task.Run(PruneIfNeeded);
    }

    public async Task<CachedThumb?> TryGetAsync(MediaRef media, int maxSize, CancellationToken ct = default)
    {
        try
        {
            var token = SourceToken(media.Path);
            if (token is null) return null;

            var file = PathFor(media, maxSize, token.Value);
            if (!File.Exists(file)) return null;

            var bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            using var container = new MemoryStream(bytes);
            if (!ThumbnailCacheFile.TryRead(container, out var header, out var payload))
            {
                // Corrupt entry: drop it so the next run doesn't pay again.
                TryDelete(file);
                return null;
            }

            var bitmap = await DecodeAsync(payload, ct).ConfigureAwait(false);
            if (bitmap is null) return null;

            return new CachedThumb(bitmap, header.OriginalWidth, header.OriginalHeight, header.Duration);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache.TryGetAsync failed for {media}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task StoreAsync(MediaRef media, int maxSize, CachedThumb thumb, CancellationToken ct = default)
    {
        try
        {
            var token = SourceToken(media.Path);
            if (token is null) return; // source gone / unreadable: nothing to key on

            var format = ChooseFormat(media.Path);
            var payload = await EncodeAsync(thumb.Bitmap, format, ct).ConfigureAwait(false);
            if (payload is null) return;

            var file = PathFor(media, maxSize, token.Value);
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);

            using var container = new MemoryStream();
            ThumbnailCacheFile.Write(
                container,
                new ThumbnailCacheHeader(thumb.OriginalWidth, thumb.OriginalHeight, thumb.Duration, format),
                payload);

            var temp = file + ".tmp";
            await File.WriteAllBytesAsync(temp, container.ToArray(), ct).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);

            if (Interlocked.Increment(ref _storesSincePrune) >= StoresPerPrune)
            {
                Interlocked.Exchange(ref _storesSincePrune, 0);
                _ = Task.Run(PruneIfNeeded, CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache.StoreAsync failed for {media}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------

    private string PathFor(MediaRef media, int maxSize, (long Size, long Ticks) token)
    {
        var identity = $"{media}|{maxSize}|{media.Kind}|{token.Size}|{token.Ticks}|gen{GenerationVersion}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var shard = hash[..2];
        return Path.Combine(_root, shard, hash + Extension);
    }

    private static (long Size, long Ticks)? SourceToken(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            return (info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>
    /// JPEG for opaque sources (photos) — much smaller and faster to encode;
    /// PNG for anything that may carry alpha. The store path falls back to PNG
    /// if a JPEG encode fails.
    /// </summary>
    private static ThumbnailImageFormat ChooseFormat(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".bmp" => ThumbnailImageFormat.Jpeg,
            _ => ThumbnailImageFormat.Png,
        };

    private static async Task<byte[]?> EncodeAsync(SoftwareBitmap bitmap, ThumbnailImageFormat format, CancellationToken ct)
    {
        if (format == ThumbnailImageFormat.Jpeg)
        {
            var jpeg = await TryEncodeAsync(bitmap, ThumbnailImageFormat.Jpeg, ct).ConfigureAwait(false);
            if (jpeg is not null) return jpeg;
            // Fall through to PNG (e.g. the encoder rejected the alpha mode).
        }

        return await TryEncodeAsync(bitmap, ThumbnailImageFormat.Png, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]?> TryEncodeAsync(SoftwareBitmap bitmap, ThumbnailImageFormat format, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            var encoderId = format == ThumbnailImageFormat.Jpeg
                ? BitmapEncoder.JpegEncoderId
                : BitmapEncoder.PngEncoderId;
            var encoder = await BitmapEncoder.CreateAsync(encoderId, stream);

            if (format == ThumbnailImageFormat.Jpeg)
            {
                var properties = new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single) },
                };
                await encoder.BitmapProperties.SetPropertiesAsync(properties);
            }

            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync();

            var length = (uint)stream.Size;
            var bytes = new byte[length];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(length);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache encode ({format}) failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static async Task<SoftwareBitmap?> DecodeAsync(byte[] payload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            writer.DetachStream();
        }
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    private void PruneIfNeeded()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            var files = new DirectoryInfo(_root).GetFiles("*" + Extension, SearchOption.AllDirectories);
            var total = files.Sum(f => f.Length);
            if (total <= MaxTotalBytes) return;

            // Delete oldest until comfortably under the cap (hysteresis so a
            // full sweep is not triggered again immediately).
            var target = (long)(MaxTotalBytes * 0.9);
            foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                var length = file.Length;
                TryDelete(file.FullName);
                total -= length;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache prune failed: {ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache delete failed for {path}: {ex.Message}");
        }
    }
}
