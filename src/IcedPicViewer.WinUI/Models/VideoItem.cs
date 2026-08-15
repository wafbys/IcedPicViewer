// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Text;

namespace IcedPicViewer.Models;

public sealed partial class VideoItem : MediaItem
{
    public TimeSpan Duration { get; private set; }
    public bool HasAudio { get; private set; }

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
    public string Codec { get; private set; }

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
            var size = MediaDisplay.FormatPixelSize(OriginalWidth, OriginalHeight);
            if (string.IsNullOrEmpty(size)) size = UiCopy.UnknownSize;
            if (Duration > TimeSpan.Zero)
                return $"{size} · {MediaDisplay.FormatDuration(Duration)}";
            return size;
        }
    }

    public string DurationText => MediaDisplay.FormatDuration(Duration);

    protected override string InfoDurationText => DurationText;

    public void ApplyDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration == Duration) return;
        Duration = duration;
        OnPropertyChanged(nameof(Duration));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(InfoLine));
        OnPropertyChanged(nameof(OriginalSizeText));
    }

    public void ApplyCodec(string codec, bool hasAudio)
    {
        if (!string.IsNullOrEmpty(codec))
        {
            Codec = codec;
            OnPropertyChanged(nameof(Codec));
        }
        HasAudio = hasAudio;
        OnPropertyChanged(nameof(HasAudio));
    }
}
