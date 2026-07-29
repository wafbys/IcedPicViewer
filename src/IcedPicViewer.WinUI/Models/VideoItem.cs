// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Models;

public sealed partial class VideoItem : MediaItem
{
    public TimeSpan Duration { get; }
    public bool HasAudio { get; }

    /// <summary>
    /// FFmpeg short name of the video codec (e.g. <c>"h264"</c>,
    /// <c>"hevc"</c>, <c>"prores"</c>, <c>"vp9"</c>, <c>"av1"</c>).
    /// Empty when the codec couldn't be identified at scan time.
    /// Used by the playback error path to surface a codec-specific
    /// recovery hint — most commonly: ProRes in .mov files can't be
    /// decoded by stock Windows Media Foundation regardless of
    /// container, so the user needs either LAV Filters or to convert
    /// the file to H.264/AAC with FFmpeg / HandBrake.
    /// </summary>
    public string Codec { get; }

    public VideoItem(
        MediaRef media,
        long fileSize,
        DateTime modifiedTime,
        int originalWidth,
        int originalHeight,
        TimeSpan duration,
        bool hasAudio,
        string codec)
        : base(media, fileSize, modifiedTime, originalWidth, originalHeight)
    {
        Duration = duration;
        HasAudio = hasAudio;
        Codec = codec;
    }

    /// <summary>
    /// "WIDTH×HEIGHT · M:SS" for videos with a known duration, falling back
    /// to "WIDTH×HEIGHT" (or "Unknown") when dimensions are missing. The
    /// duration is part of the gallery overlay because the user scanning a
    /// folder of mixed clips needs the runtime at a glance to decide which
    /// video to open — a 30-second clip and a 2-hour recording both render
    /// the same way in the static first-frame thumbnail otherwise.
    /// </summary>
    public override string OriginalSizeText
    {
        get
        {
            var size = OriginalWidth > 0 && OriginalHeight > 0
                ? $"{OriginalWidth}×{OriginalHeight}"
                : "Unknown";
            if (Duration > TimeSpan.Zero)
            {
                return $"{size} · {FormatDuration(Duration)}";
            }
            return size;
        }
    }

    public string DurationText => FormatDuration(Duration);

    private static string FormatDuration(TimeSpan d)
    {
        // m:ss for < 1h, h:mm:ss otherwise. Avoids Locale formatting
        // surprises (commas / decimal points) — these tiles are too small
        // for an invariant-only format to look out of place.
        if (d.TotalHours >= 1)
        {
            return $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}";
        }
        return $"{(int)d.TotalMinutes}:{d.Seconds:D2}";
    }
}
