// Copyright (c) IcedPicViewer. All rights reserved.

using Windows.Graphics.Imaging;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Oriented original size plus a decoded thumbnail bitmap (no PNG wrap).
/// <see cref="Duration"/> is set for videos.
/// </summary>
public readonly record struct CachedThumb(
    SoftwareBitmap Bitmap,
    int OriginalWidth,
    int OriginalHeight,
    TimeSpan? Duration = null);

/// <summary>
/// Bounded LRU cache for decoded thumbnails. Shared between the image
/// and video pipelines. Safe for concurrent workers (gallery semaphore).
/// </summary>
public interface IThumbnailCache
{
    bool TryGet(string key, out CachedThumb? thumb);

    /// <remarks>
    /// Named <c>Store</c> rather than <c>Set</c> because CA1716 flags
    /// <c>Set</c> as a VB.NET reserved keyword.
    /// </remarks>
    void Store(string key, CachedThumb thumb);
}
