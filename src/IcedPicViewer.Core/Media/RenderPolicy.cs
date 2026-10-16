// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Shared rendering policy for scaling small source images.
///
/// <para>
/// Tiny sources (pixel art, icons, 16×16 / 32×32 animations) must scale up
/// with nearest-neighbour sampling — smooth interpolation turns them into
/// blurry mush. The rule applies on both sides of the Core/WinUI split
/// (WIC decode paths in the shell, FFmpeg frame scaling in Core), so the
/// threshold lives here as the single source. Borrowed from the 2026
/// Photos app: "tiny images now zoom in far more to fill the screen and
/// stay crisp instead of looking blurry".
/// </para>
/// </summary>
public static class RenderPolicy
{
    /// <summary>
    /// Longest source edge (px) at or below which upscaled rendering uses
    /// nearest-neighbour sampling.
    /// </summary>
    public const int PixelCrispMaxEdge = 96;
}
