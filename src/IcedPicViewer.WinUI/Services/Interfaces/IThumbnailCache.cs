// Copyright (c) IcedPicViewer. All rights reserved.

using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Bounded LRU cache for <see cref="BitmapImage"/> thumbnails. Shared
/// between the image and video thumbnail pipelines so a path that's
/// been decoded as one kind doesn't collide with a future decode of
/// the same path as the other kind (and so a folder that's 80% videos
/// doesn't evict every image thumb just because the video thumbs are
/// dominating recency).
///
/// <para>
/// Implementations must be safe to call from multiple worker threads
/// concurrently — the gallery's <c>LoadThumbnailAsync</c> runs under a
/// 6-wide semaphore and the video path is hit from the same code path,
/// so a single lock inside the implementation is fine.
/// </para>
/// </summary>
public interface IThumbnailCache
{
    /// <summary>
    /// Look up a thumbnail by its cache key. On hit, the entry is
    /// promoted to the most-recently-used position; on miss, returns
    /// false and <paramref name="image"/> is set to null.
    /// </summary>
    bool TryGet(string key, out BitmapImage? image);

    /// <summary>
    /// Insert (or replace) a thumbnail. If the cache is at capacity the
    /// least-recently-used entry is evicted.
    /// </summary>
    /// <remarks>
    /// Named <c>Store</c> rather than <c>Set</c> because CA1716 flags
    /// <c>Set</c> as a VB.NET reserved keyword — and the codebase already
    /// pulls in the VB namespace for the recycle-bin delete path, so the
    /// conflict is real, not theoretical.
    /// </remarks>
    void Store(string key, BitmapImage image);
}
