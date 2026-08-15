// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Layout;

/// <summary>Shared gallery decode numbers. Both shells must use the same thumb edge.</summary>
public static class GalleryMetrics
{
    /// <summary>
    /// Longest edge of a masonry thumbnail, in pixels.
    /// Cards commonly land around 400–800 CSS px (3-column fill, 150% DPI);
    /// 256 / 400 forced a large upscale and made high-res photos look soft.
    /// </summary>
    public const int ThumbMaxEdge = 768;
}
