// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Single source of truth for which file extensions the app treats as
/// images or videos. Both shells and the directory scanner use this list;
/// platform-specific loaders may still fail at decode time (e.g. missing
/// HEIC support) without changing membership here.
/// </summary>
public static class MediaCatalog
{
    private static readonly HashSet<string> ImageExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".tiff", ".tif", ".ico", ".avif", ".heic",
    };

    private static readonly HashSet<string> VideoExtensionSet = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi", ".webm", ".flv",
    };

    public static IReadOnlyCollection<string> ImageExtensions => ImageExtensionSet;

    public static IReadOnlyCollection<string> VideoExtensions => VideoExtensionSet;

    /// <summary>
    /// Combined (extension, kind) pairs for <see cref="IcedPicViewer.Services.Interfaces.IDirectoryScanner.ScanAsync"/>.
    /// </summary>
    public static IEnumerable<(string Extension, MediaKind Kind)> SupportedMedia
    {
        get
        {
            foreach (var ext in ImageExtensionSet)
                yield return (ext, MediaKind.Image);
            foreach (var ext in VideoExtensionSet)
                yield return (ext, MediaKind.Video);
        }
    }

    public static bool IsSupported(string path)
    {
        var ext = Path.GetExtension(path);
        return ImageExtensionSet.Contains(ext) || VideoExtensionSet.Contains(ext);
    }

    public static MediaKind GetKind(string path)
    {
        var ext = Path.GetExtension(path);
        if (VideoExtensionSet.Contains(ext)) return MediaKind.Video;
        return MediaKind.Image;
    }
}
