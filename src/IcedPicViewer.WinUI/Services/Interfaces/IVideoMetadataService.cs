// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Models;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Metadata for a single video file, as reported by FFmpeg at open time.
/// All fields are best-effort — a corrupt / truncated / non-standard file
/// can yield any subset. Callers should treat zero / TimeSpan.Zero /
/// <c>false</c> as "unknown" rather than as a hard guarantee.
/// </summary>
/// <param name="Width">Encoded frame width in pixels (from the video codec parameters).</param>
/// <param name="Height">Encoded frame height in pixels.</param>
/// <param name="Duration">Total playback length. <see cref="TimeSpan.Zero"/> when the container does not report a duration (e.g. a live HLS stream).</param>
/// <param name="HasAudio">True when the file contains at least one audio stream.</param>
/// <param name="VideoCodec">FFmpeg short name of the primary video codec
/// (e.g. <c>"h264"</c>, <c>"hevc"</c>, <c>"prores"</c>, <c>"vp9"</c>, <c>"av1"</c>).
/// Empty when the file has no video stream or the codec couldn't be
/// identified. Used by the playback error path to surface a
/// codec-specific recovery hint (most common: ProRes in .mov → MF can't
/// decode it regardless of container).</param>
public readonly record struct VideoMetadata(int Width, int Height, TimeSpan Duration, bool HasAudio, string VideoCodec);

/// <summary>
/// Reads video metadata, extracts the first usable frame as a gallery-
/// style thumbnail, exposes a "playable file path" so
/// <c>MediaPlayerElement</c> can play archive entries that don't have
/// a real on-disk file, and provides a transcoding fallback for files
/// whose codec is one Windows Media Foundation cannot decode. Kept
/// deliberately separate from <see cref="IImageLoader"/>: image loading
/// is a pure WinRT path (BitmapDecoder), while video is an FFmpeg
/// AutoGen native-pointer path with very different lifetime and error
/// semantics. Mixing them would mean every image-only call site drags
/// in the FFmpeg DLLs.
/// </summary>
public interface IVideoMetadataService
{
    /// <summary>
    /// Opens the video just long enough to read container-level metadata
    /// (codec params, duration, stream table), then closes. Does NOT
    /// decode any frames, so this is cheap (single disk read + parse).
    /// For archive sources, the entry is extracted to a temp file
    /// (under the service's temp dir) for the duration of the call,
    /// then deleted. Returns null when the file cannot be opened or
    /// contains no decodable video stream.
    /// </summary>
    Task<VideoMetadata?> GetVideoMetadataAsync(ImageSource source, CancellationToken ct = default);

    /// <summary>
    /// Decodes one frame from the video (seeked to ~10% of duration to
    /// avoid black leader frames common at t=0) and returns it as a
    /// <see cref="BitmapImage"/> scaled to fit <paramref name="maxSize"/>
    /// on the longer edge. The returned bitmap has DecodePixelWidth set,
    /// so the WIC pipeline only materialises the scaled pixels in
    /// managed memory. For archive sources, the entry is extracted to a
    /// temp file for the duration of the call, then deleted. Returns
    /// null on any decode / open failure.
    /// </summary>
    Task<BitmapImage?> ExtractVideoThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default);

    /// <summary>
    /// Returns a file path that <c>MediaPlayer</c> / <c>MediaSource</c> can
    /// read. For a loose file, this is just <see cref="ImageSource.Path"/>.
    /// For an archive entry, the entry is extracted to a fresh temp
    /// file (in the service's temp dir) that lives until the caller
    /// invokes <see cref="ReleasePlaybackFilePath"/>. The caller is
    /// responsible for invoking that release exactly once for every
    /// successful GetPlaybackFilePathAsync call.
    /// </summary>
    Task<string> GetPlaybackFilePathAsync(ImageSource source, CancellationToken ct = default);

    /// <summary>
    /// Releases a previously-returned playback file path. For loose
    /// files this is a no-op (the path points at the user's real
    /// file). For archive extracts it deletes the temp file the
    /// service created. Idempotent — calling it with a path that
    /// wasn't returned by <see cref="GetPlaybackFilePathAsync"/> is
    /// a safe no-op, and calling it twice on the same path only
    /// deletes once.
    /// </summary>
    void ReleasePlaybackFilePath(string path);
}
