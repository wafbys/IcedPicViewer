// Copyright (c) IcedPicViewer. All rights reserved.

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

    /// <summary>Encodes and persists <paramref name="thumb"/>. Fire-and-forget friendly.</summary>
    Task StoreAsync(MediaRef media, int maxSize, CachedThumb thumb, CancellationToken ct = default);
}
