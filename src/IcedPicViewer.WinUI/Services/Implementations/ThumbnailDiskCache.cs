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
/// decoder — yields a new key and the old entry is never served.
///
/// <para>
/// Writes are atomic (temp file + <see cref="File.Move(string,string,bool)"/>).
/// The size budget is enforced by a sweep that deletes the oldest entries; it
/// runs when the running total says the budget is exceeded (plus a rare safety
/// interval), instead of walking the whole tree every N writes.
/// </para>
///
/// <para>
/// <b>Thumbnail quality is not traded for cache lifetime.</b> Every entry is
/// stored losslessly (PNG), including .jpg/.bmp sources: a thumbnail is the
/// user's picture, so the cache never re-encodes it into a lossy format just to
/// fit more of them. The consequence is a large per-entry footprint (a
/// 768-edge photo is ~0.5 MB as PNG versus ~0.1 MB as JPEG) and a slow encode
/// (~137 ms vs ~11 ms measured, dominated by photo-like content), which is why
/// capacity comes from the budget below and never from the encoder.
/// </para>
/// </summary>
public sealed class ThumbnailDiskCache : IThumbnailDiskCache
{
    private const string Extension = ".thumb";
    private const string TempSuffix = ".tmp";

    /// <summary>
    /// Generation tag of the thumbnail-producing code. Bump it whenever a change
    /// makes previously persisted pixels wrong: old entries then have a
    /// different identity, so they are never served (and the sweep reclaims
    /// them oldest-first, since a dead entry can never be touched).
    ///
    /// <para>v2: <c>VideoFrameExtractor</c> honours FFmpeg's row pitch (v1 video
    /// thumbnails whose width is not a multiple of 8 are sheared).</para>
    /// <para>v3: thumbnails are stored losslessly (PNG) for every source kind;
    /// v2 stored .jpg/.bmp thumbnails as JPEG.</para>
    /// </summary>
    private const int GenerationVersion = 3;

    /// <summary>
    /// A cache hit refreshes the entry's write time at most this often. The
    /// sweep orders by write time, so this is what makes eviction usage-aware;
    /// coalescing keeps it to one metadata write per entry per hour even when
    /// the user re-scrolls the same folder repeatedly.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Sweep anyway after this many stores, in case the running total drifted
    /// (a store racing the startup measurement, files removed behind our back).
    /// Large enough that a correct running total is what normally triggers.
    /// </summary>
    private const int SafetySweepStores = 4096;

    /// <summary>Keep the sweep's result this far below the budget, so it does not re-trigger immediately.</summary>
    private const double PruneTargetFraction = 0.9;

    private readonly string _root;
    private readonly long _budgetBytes;

    /// <summary>
    /// Running total of live entry bytes, maintained on store. <c>-1</c> means
    /// "not measured yet"; the startup sweep sets the real value.
    /// </summary>
    private long _approxTotalBytes = -1;

    private int _storesSinceSweep;

    public ThumbnailDiskCache(string cacheRoot)
    {
        _root = cacheRoot;
        _budgetBytes = ComputeBudgetBytes(cacheRoot);

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

            // The entry is valid and about to be used: mark it as recently used
            // so the sweep's oldest-first ordering behaves like an LRU.
            TouchIfStale(file);

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

            var payload = await EncodePngAsync(thumb.Bitmap, ct).ConfigureAwait(false);
            if (payload is null) return;

            var file = PathFor(media, maxSize, token.Value);
            var dir = Path.GetDirectoryName(file)!;
            Directory.CreateDirectory(dir);

            // Re-writing an existing entry replaces it; only the size delta may
            // be added to the running total, or a re-decode of an already-cached
            // folder would inflate the estimate and trigger pointless sweeps.
            var prior = new FileInfo(file);
            var priorLength = prior.Exists ? prior.Length : 0L;

            using var container = new MemoryStream();
            ThumbnailCacheFile.Write(
                container,
                new ThumbnailCacheHeader(thumb.OriginalWidth, thumb.OriginalHeight, thumb.Duration, ThumbnailImageFormat.Png),
                payload);

            var data = container.ToArray();
            var temp = file + TempSuffix;
            await File.WriteAllBytesAsync(temp, data, ct).ConfigureAwait(false);
            File.Move(temp, file, overwrite: true);

            // Running total (see _approxTotalBytes): sweep as soon as the budget
            // is exceeded, and otherwise only at the safety interval.
            var total = Interlocked.Add(ref _approxTotalBytes, data.Length - priorLength);
            if (total > _budgetBytes || Interlocked.Increment(ref _storesSinceSweep) >= SafetySweepStores)
            {
                Interlocked.Exchange(ref _storesSinceSweep, 0);
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

    /// <summary>
    /// Budget for the whole cache. 1 GB is the historical flat cap and stays the
    /// floor; 4 GB the ceiling. In between: 0.5 % of the volume's free space,
    /// never more than an eighth of it, so thumbnails cannot fill a disk that is
    /// close to full.
    ///
    /// <para>
    /// This is the lever that makes the cache last: every entry is lossless and
    /// therefore large (~0.5 MB for a 768-edge photo), so more capacity is
    /// bought with disk space rather than with quality.
    /// </para>
    /// </summary>
    private static long ComputeBudgetBytes(string cacheRoot)
    {
        const long Floor = 1L * 1024 * 1024 * 1024;
        const long Ceiling = 4L * 1024 * 1024 * 1024;
        const double FreeSpaceFraction = 0.005;

        try
        {
            var rootPath = Path.GetPathRoot(Path.GetFullPath(cacheRoot));
            if (string.IsNullOrEmpty(rootPath)) return Floor;

            var free = new DriveInfo(rootPath).AvailableFreeSpace;
            if (free <= 0) return Floor;

            var target = Math.Clamp((long)(free * FreeSpaceFraction), Floor, Ceiling);
            return Math.Min(target, free / 8);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"ThumbnailDiskCache: free-space probe failed, using 1 GB: {ex.Message}");
            return Floor;
        }
    }

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
    /// Refreshes the entry's write time when it is older than
    /// <see cref="TouchInterval"/>. Best-effort: a failed touch only costs
    /// eviction accuracy.
    /// </summary>
    private static void TouchIfStale(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return;
            var now = DateTime.UtcNow;
            if (now - info.LastWriteTimeUtc < TouchInterval) return;
            info.LastWriteTimeUtc = now;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"ThumbnailDiskCache: touch {file} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Lossless PNG for every entry. Deliberately the only encoder: a thumbnail
    /// is the user's picture, and none of them may be re-encoded lossily just to
    /// make the cache hold more. (Measured on real entries: a 768-edge photo is
    /// ~0.5 MB as PNG, ~0.1 MB as JPEG.)
    /// </summary>
    private static async Task<byte[]?> EncodePngAsync(SoftwareBitmap bitmap, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
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
            Trace.TraceError($"ThumbnailDiskCache PNG encode failed: {ex.GetType().Name}: {ex.Message}");
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

    /// <summary>
    /// Single-pass bounded sweep: drop interrupted-store temp files, measure what
    /// is live, and — if that exceeds the budget — delete the oldest until it is
    /// back under <see cref="PruneTargetFraction"/> of it. "Oldest" is the
    /// read-refreshed write time, so entries the user keeps looking at survive
    /// and entries from a previous <see cref="GenerationVersion"/> (never
    /// served, never touched) are reclaimed first.
    /// </summary>
    private void PruneIfNeeded()
    {
        try
        {
            if (!Directory.Exists(_root)) return;

            var all = new DirectoryInfo(_root).GetFiles("*", SearchOption.AllDirectories);
            var live = new List<FileInfo>(all.Length);
            long total = 0;
            long deletedBytes = 0;
            var deletedFiles = 0;

            foreach (var file in all)
            {
                if (file.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // A store interrupted between the temp write and the rename.
                    deletedBytes += file.Length;
                    deletedFiles++;
                    TryDelete(file.FullName);
                    continue;
                }

                if (!file.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;

                live.Add(file);
                total += file.Length;
            }

            if (total > _budgetBytes)
            {
                var target = (long)(_budgetBytes * PruneTargetFraction);
                foreach (var file in live.OrderBy(f => f.LastWriteTimeUtc))
                {
                    if (total <= target) break;
                    var length = file.Length;
                    TryDelete(file.FullName);
                    total -= length;
                    deletedBytes += length;
                    deletedFiles++;
                }
            }

            // Re-sync the running total with what is actually on disk.
            Interlocked.Exchange(ref _approxTotalBytes, total);

            if (deletedFiles > 0)
            {
                Trace.TraceInformation(
                    $"ThumbnailDiskCache: reclaimed {deletedFiles} files, {deletedBytes / (1024.0 * 1024):F1} MB " +
                    $"(live {total / (1024.0 * 1024):F1} MB, budget {_budgetBytes / (1024.0 * 1024):F0} MB)");
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
