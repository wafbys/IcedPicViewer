// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Text;

/// <summary>Shared display formatting for media metadata (both shells).</summary>
public static class MediaDisplay
{
    public static string FormatFileSize(long fileSize)
    {
        if (fileSize < 1024) return $"{fileSize} B";
        if (fileSize < 1024 * 1024) return $"{fileSize / 1024.0:F1} KB";
        return $"{fileSize / (1024.0 * 1024.0):F1} MB";
    }

    /// <summary>VLC/mpv-style short duration (m:ss or h:mm:ss).</summary>
    public static string FormatDuration(TimeSpan? duration)
    {
        if (duration is not { } d || d <= TimeSpan.Zero) return "";
        if (d.TotalHours >= 1)
            return $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}";
        return $"{(int)d.TotalMinutes}:{d.Seconds:D2}";
    }

    public static string FormatPixelSize(int width, int height)
        => width > 0 && height > 0 ? $"{width}×{height}" : "";

    /// <summary>Hover / tooltip second line: "W×H · m:ss · 2.1 MB".</summary>
    public static string FormatInfoLine(string sizeText, string durationText, string fileSizeText)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(sizeText))
            parts.Add(sizeText);
        if (!string.IsNullOrEmpty(durationText))
            parts.Add(durationText);
        parts.Add(fileSizeText);
        return string.Join(" · ", parts);
    }
}
