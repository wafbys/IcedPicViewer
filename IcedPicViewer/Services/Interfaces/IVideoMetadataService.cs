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
public readonly record struct VideoMetadata(int Width, int Height, TimeSpan Duration, bool HasAudio);

/// <summary>
/// Reads video metadata and extracts the first usable frame as a
/// gallery-style thumbnail. Kept deliberately separate from
/// <see cref="IImageLoader"/>: image loading is a pure WinRT path
/// (BitmapDecoder), while video is an FFmpeg AutoGen native-pointer
/// path with very different lifetime and error semantics. Mixing them
/// would mean every image-only call site drags in the FFmpeg DLLs.
/// </summary>
public interface IVideoMetadataService
{
    /// <summary>
    /// Opens the video just long enough to read container-level metadata
    /// (codec params, duration, stream table), then closes. Does NOT
    /// decode any frames, so this is cheap (single disk read + parse).
    /// Returns null when the file cannot be opened or contains no
    /// decodable video stream.
    /// </summary>
    Task<VideoMetadata?> GetVideoMetadataAsync(ImageSource source, CancellationToken ct = default);

    /// <summary>
    /// Decodes one frame from the video (seeked to ~10% of duration to
    /// avoid black leader frames common at t=0) and returns it as a
    /// <see cref="BitmapImage"/> scaled to fit <paramref name="maxSize"/>
    /// on the longer edge. The returned bitmap has DecodePixelWidth set,
    /// so the WIC pipeline only materialises the scaled pixels in
    /// managed memory. Returns null on any decode / open failure.
    /// </summary>
    Task<BitmapImage?> ExtractVideoThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default);
}
