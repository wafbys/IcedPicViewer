// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>
/// Shared status-bar copy for WinUI and Avalonia.
/// Keeps product wording consistent; shells only supply counts / paths / names.
/// </summary>
public static class GalleryStatusFormatter
{
    public const string IdleDefault = "Open a folder to start";

    /// <summary>e.g. "12 image(s)" or "10 images, 2 videos".</summary>
    public static string FormatItemBreakdown(int imageCount, int videoCount)
    {
        if (videoCount > 0)
            return $"{imageCount} images, {videoCount} videos";
        return $"{imageCount} image(s)";
    }

    /// <summary>LoadDirectory just started (before first batch lands).</summary>
    public static string FormatScanningStarted()
        => "Scanning…";

    /// <summary>
    /// While scanning. Prefer path form when <paramref name="currentPath"/> is set
    /// (WinUI live path); otherwise count + already-shown breakdown (Avalonia).
    /// </summary>
    public static string FormatScanning(
        int discoveredCount,
        string itemBreakdown,
        string? currentPath = null,
        int scanErrorCount = 0,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null)
    {
        var err = FormatErrorSuffix(scanErrorCount, firstSkippedFileName, firstSkippedReason);
        if (!string.IsNullOrEmpty(currentPath))
            return $"Scanning: {currentPath}  ({discoveredCount} found){err}";
        return $"Scanning… found {discoveredCount}, showing {itemBreakdown}{err}";
    }

    /// <summary>After scan settle (or between Load More waves).</summary>
    public static string FormatGallery(
        string itemBreakdown,
        int discoveredCount,
        int remainingCount,
        int scanErrorCount = 0,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null)
    {
        var err = FormatErrorSuffix(scanErrorCount, firstSkippedFileName, firstSkippedReason);
        if (remainingCount > 0)
            return $"Showing {itemBreakdown} / {discoveredCount} ({remainingCount} more){err}";
        return $"Loaded {itemBreakdown}{err}";
    }

    /// <summary>In-flight Load More.</summary>
    public static string FormatLoadingMore(int loadedCount, int discoveredCount)
        => $"Loading {loadedCount} / {discoveredCount}…";

    public static string FormatError(string message)
        => $"Error: {message}";

    public static string FormatCancelled()
        => "Cancelled";

    public static string FormatFolderPickerUnavailable()
        => "Folder picker not wired";

    public static string FormatWatchUnavailable(string message)
        => $"File monitoring unavailable: {message}";

    public static string FormatDeleteFailed(string error)
        => $"Delete failed: {error}";

    public static string FormatDeleted(string name, bool movedToTrash)
        => movedToTrash ? $"Moved to trash: {name}" : $"Deleted: {name}";

    public static string FormatArchiveDeleteNotSupported()
        => "Cannot delete: media inside archives is not supported";

    public static string FormatSlideshowActive(double intervalSeconds, bool looping, bool shuffling)
    {
        var text = $"Slideshow every {intervalSeconds:0.#}s";
        if (looping) text += " · loop";
        if (shuffling) text += " · shuffle";
        return text;
    }

    public static string FormatSlideshowFinished()
        => "Slideshow finished";

    public static string FormatVideoLoadFailed()
        => "Video load failed (codec / path)";

    public static string FormatVideoError(string message)
        => $"Video error: {message}";

    public static string FormatErrorSuffix(
        int scanErrorCount,
        string? firstSkippedFileName = null,
        string? firstSkippedReason = null)
    {
        if (scanErrorCount <= 0)
            return "";

        if (scanErrorCount == 1 && !string.IsNullOrEmpty(firstSkippedFileName))
        {
            return string.IsNullOrEmpty(firstSkippedReason)
                ? $" — 1 file skipped ({firstSkippedFileName})"
                : $" — 1 file skipped ({firstSkippedFileName}: {firstSkippedReason})";
        }

        if (!string.IsNullOrEmpty(firstSkippedFileName))
            return $" — {scanErrorCount} files skipped (first: {firstSkippedFileName})";

        return $" · {scanErrorCount} scan error(s)";
    }
}
