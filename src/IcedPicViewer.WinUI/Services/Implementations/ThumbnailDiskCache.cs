// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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
/// <b>Every entry is stored losslessly (PNG)</b>, for every source kind: a
/// thumbnail is the user's picture, so the cache never re-encodes it into a
/// lossy format just to fit more of them. The consequence is a large per-entry
/// footprint (a 768-edge photo is ~0.5 MB as PNG versus ~0.1 MB as JPEG) and a
/// slow encode (~60 ms idle, ~137 ms under load, versus ~11 ms for JPEG), which
/// drives two decisions below: capacity comes from the budget and never from
/// the encoder, and the encode never runs on the path that shows a tile.
/// </para>
///
/// <para>
/// <b>Persistence is decoupled from display.</b> <see cref="StoreAsync"/> clones
/// the pixels (~3 ms measured) and hands the clone to a bounded encode pool, so
/// the gallery can show the bitmap immediately while PNG compression runs in
/// the background. Cloning is what keeps the old "encoder and UI never touch
/// the same bitmap at once" rule intact without making the UI wait for the
/// encode — the caller keeps the original, the encoder owns the copy. Writes are
/// atomic (temp file + <see cref="File.Move(string,string,bool)"/>).
/// </para>
///
/// <para>
/// The size budget is enforced by a sweep that deletes the oldest entries; it
/// runs when the running total says the budget is exceeded (plus a rare safety
/// interval) and is single-flight, instead of walking the whole tree every N
/// writes.
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
    /// How many PNG encodes may run at once. Each one is CPU-bound, so this is a
    /// core budget, not an I/O one — it is deliberately lower than the gallery's
    /// 6-way decode semaphore so encodes and decodes together do not oversubscribe
    /// the machine.
    /// </summary>
    private const int MaxConcurrentEncodes = 4;

    /// <summary>
    /// Upper bound on thumbnails waiting to be encoded. Each pending item holds a
    /// cloned bitmap (~0.5–1.7 MB), so an unbounded queue would be a memory leak
    /// by construction. Measured, the pool drains faster than the gallery
    /// decodes, so this is only reached on very fast browsing — items beyond it
    /// are simply not persisted (the tile is still shown; the entry is
    /// regenerated next session).
    /// </summary>
    private const int MaxPendingStores = 64;

    /// <summary>
    /// A cache hit refreshes the entry's write time at most this often. The
    /// sweep orders by write time, so this is what makes eviction usage-aware;
    /// coalescing keeps it to one metadata write per entry per hour even when
    /// the user re-scrolls the same folder repeatedly.
    /// </summary>
    private static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// Sweep anyway after this many writes, in case the running total drifted (a
    /// write racing the startup measurement, files removed behind our back).
    /// Large enough that a correct running total is what normally triggers.
    /// </summary>
    private const int SafetySweepStores = 4096;

    /// <summary>Keep the sweep's result this far below the budget, so it does not re-trigger immediately.</summary>
    private const double PruneTargetFraction = 0.9;

    /// <summary>Log every Nth dropped store instead of every one (drops come in bursts).</summary>
    private const int DropLogInterval = 128;

    private readonly string _root;
    private readonly long _budgetBytes;

    /// <summary>
    /// Bounded work queue of thumbnails waiting to be encoded, drained by
    /// <see cref="MaxConcurrentEncodes"/> consumers. Bounded because each pending
    /// item holds a cloned bitmap (~0.5–1.7 MB): unbounded would be a memory leak
    /// by construction.
    /// </summary>
    private readonly Channel<PendingThumb> _queue =
        Channel.CreateBounded<PendingThumb>(new BoundedChannelOptions(MaxPendingStores)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });

    /// <summary>Stores dropped because the queue was full (rate-limited logging).</summary>
    private int _droppedStores;

    /// <summary>
    /// Running total of live entry bytes, maintained on write. <c>-1</c> means
    /// "not measured yet"; the startup sweep sets the real value.
    /// </summary>
    private long _approxTotalBytes = -1;

    private int _storesSinceSweep;

    /// <summary>Single-flight guard: only one sweep may walk the tree at a time.</summary>
    private int _sweepRunning;

    /// <summary>
    /// Bumped by <see cref="Clear"/> so encodes queued before a "clear cache"
    /// cannot repopulate it right after the user emptied it.
    /// </summary>
    private int _clearEpoch;

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

        for (var i = 0; i < MaxConcurrentEncodes; i++)
        {
            _ = Task.Run(ConsumeAsync);
        }

        // Bound whatever a previous session left behind, off the UI thread.
        ScheduleSweep();
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

    /// <summary>
    /// Clones <paramref name="thumb"/>'s pixels and queues the PNG encode/write
    /// on the background pool. Returns as soon as the clone is done: the caller
    /// may hand the original bitmap to the UI without waiting for compression.
    /// Never throws; a failure just means the entry is not persisted.
    /// </summary>
    public Task StoreAsync(MediaRef media, int maxSize, CachedThumb thumb, CancellationToken ct = default)
    {
        try
        {
            if (ct.IsCancellationRequested) return Task.CompletedTask;

            var token = SourceToken(media.Path);
            if (token is null) return Task.CompletedTask; // source gone: nothing to key on

            var copy = Clone(thumb.Bitmap);
            if (copy is null) return Task.CompletedTask;

            var item = new PendingThumb(
                media,
                maxSize,
                token.Value.Size,
                token.Value.Ticks,
                new ThumbnailCacheHeader(
                    thumb.OriginalWidth, thumb.OriginalHeight, thumb.Duration, ThumbnailImageFormat.Png),
                Volatile.Read(ref _clearEpoch),
                copy);

            if (!_queue.Writer.TryWrite(item))
            {
                // Queue full: skip persisting (the tile is still shown from
                // memory; the entry is regenerated next session). Blocking here
                // would put the encode back on the tile path, and queueing
                // without bound would hold hundreds of MB of cloned bitmaps.
                copy.Dispose();
                if (Interlocked.Increment(ref _droppedStores) % DropLogInterval == 1)
                {
                    Trace.TraceWarning(
                        $"ThumbnailDiskCache: encode queue full ({MaxPendingStores}), dropped {media} " +
                        $"(dropped {_droppedStores} so far)");
                }
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache.StoreAsync failed for {media}: {ex.GetType().Name}: {ex.Message}");
            return Task.CompletedTask;
        }
    }

    // -----------------------------------------------------------------

    /// <summary>Snapshot of on-disk usage. Walks the tree: call it off the UI thread.</summary>
    public ThumbnailCacheStats GetStats()
    {
        try
        {
            if (!Directory.Exists(_root)) return new ThumbnailCacheStats(0, 0, _budgetBytes);

            var count = 0;
            long total = 0;
            foreach (var file in new DirectoryInfo(_root).GetFiles("*", SearchOption.AllDirectories))
            {
                if (!file.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
                count++;
                total += file.Length;
            }

            return new ThumbnailCacheStats(count, total, _budgetBytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache.GetStats failed: {ex.GetType().Name}: {ex.Message}");
            return new ThumbnailCacheStats(0, 0, _budgetBytes);
        }
    }

    /// <summary>Deletes every persisted entry (and stray temp files), returning the bytes freed.</summary>
    public long Clear()
    {
        try
        {
            // Bump the epoch first: encodes already queued when the user cleared
            // the cache must not repopulate it a second later.
            Interlocked.Increment(ref _clearEpoch);

            if (!Directory.Exists(_root))
            {
                Interlocked.Exchange(ref _approxTotalBytes, 0);
                return 0;
            }

            long freed = 0;
            foreach (var file in new DirectoryInfo(_root).GetFiles("*", SearchOption.AllDirectories))
            {
                if (!file.Name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)
                    && !file.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var length = file.Length;
                try
                {
                    File.Delete(file.FullName);
                    freed += length;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Locked / in use: leave it, report only what actually went.
                    Trace.TraceWarning($"ThumbnailDiskCache.Clear could not delete {file.FullName}: {ex.Message}");
                }
            }

            // Whatever survived a failed delete is re-measured by the next sweep.
            Interlocked.Exchange(ref _approxTotalBytes, 0);
            Trace.TraceInformation(
                $"ThumbnailDiskCache: cleared {freed / (1024.0 * 1024):F1} MB of thumbnails");

            return freed;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache.Clear failed: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    // -----------------------------------------------------------------

    /// <summary>One of the encode consumers: encode, then write. Never exits unless the queue completes.</summary>
    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    // A "clear cache" happened after this item was queued: writing
                    // it now would immediately undo what the user just asked for.
                    if (item.Epoch != Volatile.Read(ref _clearEpoch)) continue;

                    var payload = await EncodePngAsync(item.Bitmap, CancellationToken.None).ConfigureAwait(false);
                    if (payload is not null)
                    {
                        await WriteAsync(
                            item.Media,
                            item.MaxSize,
                            (item.SourceSize, item.SourceTicks),
                            item.Header,
                            payload).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Trace.TraceError(
                        $"ThumbnailDiskCache: persisting {item.Media} failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    item.Bitmap.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"ThumbnailDiskCache: encode consumer stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task WriteAsync(
        MediaRef media,
        int maxSize,
        (long Size, long Ticks) token,
        ThumbnailCacheHeader header,
        byte[] payload)
    {
        var file = PathFor(media, maxSize, token);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // Overwriting an existing entry replaces it; only the size delta may be
        // added to the running total, or re-decoding an already-cached folder
        // would inflate the estimate and trigger pointless sweeps.
        var prior = new FileInfo(file);
        var priorLength = prior.Exists ? prior.Length : 0L;

        using var container = new MemoryStream();
        ThumbnailCacheFile.Write(container, header, payload);
        var data = container.ToArray();

        var temp = file + TempSuffix;
        await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
        File.Move(temp, file, overwrite: true);

        var total = Interlocked.Add(ref _approxTotalBytes, data.Length - priorLength);
        if (total > _budgetBytes || Interlocked.Increment(ref _storesSinceSweep) >= SafetySweepStores)
        {
            Interlocked.Exchange(ref _storesSinceSweep, 0);
            ScheduleSweep();
        }
    }

    /// <summary>
    /// Copies a bitmap so the encoder can own its pixels while the original goes
    /// to the UI. ~3 ms measured for a 768-edge photo (versus ~60 ms to encode
    /// it), and it produces a tightly packed single-plane bitmap.
    ///
    /// <para>
    /// Returns null — i.e. "do not persist" — when the plane layout is not the
    /// tight one this app produces everywhere, rather than risk encoding
    /// something that is not what was displayed.
    /// </para>
    /// </summary>
    private static SoftwareBitmap? Clone(SoftwareBitmap bitmap)
    {
        try
        {
            BitmapPlaneDescription plane;
            var buffer = bitmap.LockBuffer(BitmapBufferAccessMode.Read);
            try
            {
                plane = buffer.GetPlaneDescription(0);
            }
            finally
            {
                buffer.Dispose();
            }

            var size = plane.Stride * plane.Height;
            if (plane.StartIndex != 0 || plane.Stride != bitmap.PixelWidth * 4 || plane.Height != bitmap.PixelHeight || size <= 0)
            {
                return null;
            }

            var destination = new Windows.Storage.Streams.Buffer((uint)size) { Length = (uint)size };
            bitmap.CopyToBuffer(destination);
            return SoftwareBitmap.CreateCopyFromBuffer(
                destination,
                BitmapPixelFormat.Bgra8,
                bitmap.PixelWidth,
                bitmap.PixelHeight,
                BitmapAlphaMode.Premultiplied);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceWarning($"ThumbnailDiskCache: clone failed, not persisting: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Single-flight sweep scheduler. Without the guard, every write while the
    /// cache is over budget starts its own full-tree walk (and its own deletion
    /// pass), which thrashes the disk exactly when the cache is largest.
    /// </summary>
    private void ScheduleSweep()
    {
        if (Interlocked.CompareExchange(ref _sweepRunning, 1, 0) != 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                PruneIfNeeded();
            }
            finally
            {
                Volatile.Write(ref _sweepRunning, 0);
            }
        });
    }

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
    /// ~0.5 MB as PNG, ~0.1 MB as JPEG; WIC's PNG encoder ignores ImageQuality,
    /// so there is no cheaper lossless setting to use either.)
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

    /// <summary>
    /// One queued thumbnail: everything needed to write the entry, plus the
    /// cloned bitmap the encoder owns.
    /// </summary>
    private readonly record struct PendingThumb(
        MediaRef Media,
        int MaxSize,
        long SourceSize,
        long SourceTicks,
        ThumbnailCacheHeader Header,
        int Epoch,
        SoftwareBitmap Bitmap);
}
