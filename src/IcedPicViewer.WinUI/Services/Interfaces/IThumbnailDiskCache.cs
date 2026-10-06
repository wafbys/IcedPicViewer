// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Persisted gallery-thumbnail cache: the encoded thumbnail plus the oriented
/// original pixel size (and video duration), keyed by the source's identity
/// AND its (size, mtime). Because the token is part of the key, a changed
/// source can never be served a stale thumbnail — it simply misses.
///
/// <para>
/// Best-effort: every method swallows its own errors and a read that fails or
/// looks corrupt is treated as a miss, so the gallery always falls back to a
/// real decode.
/// </para>
/// </summary>
public interface IThumbnailDiskCache
{
    /// <summary>Returns a cached thumbnail, or null on miss / any error.</summary>
    Task<CachedThumb?> TryGetAsync(MediaRef media, int maxSize, CancellationToken ct = default);

    /// <summary>
    /// Clones <paramref name="thumb"/>'s pixels and queues the encode + write
    /// (returns once the clone is done — the implementation owns its own bounded
    /// encode pool, because encoding is far slower than the caller's decode loop
    /// can wait for). Safe to await: never throws, and a failure simply means the
    /// entry is not persisted.
    /// </summary>
    Task StoreAsync(MediaRef media, int maxSize, CachedThumb thumb, CancellationToken ct = default);

    /// <summary>
    /// Current on-disk usage (entry count, bytes, budget). Walks the cache tree,
    /// so call it off the UI thread; never throws.
    /// </summary>
    ThumbnailCacheStats GetStats();

    /// <summary>
    /// Deletes every persisted thumbnail, plus any temp file left by an
    /// interrupted store, and returns the bytes freed. Thumbnails are
    /// regenerated on the next open; originals are untouched. Never throws.
    /// Call it off the UI thread.
    /// </summary>
    long Clear();
}
