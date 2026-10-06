// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Tests.Media;

/// <summary>
/// Regression guard for the swscale ABI trap.
///
/// <para>
/// FFmpeg.AutoGen 8.1.0's legacy <c>sws_scale(SwsContext*, byte**, int*, …)</c>
/// binding is ABI-incompatible with FFmpeg 8.x (SwsContext internals changed in
/// 8.0). Calling it killed the process with an access violation (0xC0000005) —
/// not a managed exception, so no catch block could ever see it, and video
/// thumbnails silently never worked. The fix moved to the frame-based
/// <c>sws_scale_frame</c>, whose interface is stable.
/// </para>
///
/// <para>
/// These tests therefore assert on <em>survival and output shape</em>, not just
/// on a returned value: a regression back to the pointer-based API fails the
/// whole test host rather than a single assertion. Natives are fetched by
/// <c>tools/Fetch-FFmpegNatives.ps1</c> and are not in git, so every test
/// returns early when FFmpeg is unavailable instead of failing.
/// </para>
/// </summary>
public sealed class VideoFrameExtractorTests : IDisposable
{
    private readonly string _dir;

    public VideoFrameExtractorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "IcedPicViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup, same convention as AppDataPathsTests.
        }
    }

    /// <summary>
    /// Generates a real H.264 clip with the bundled ffmpeg.exe. Returns null when
    /// FFmpeg natives (and therefore the encoder) are not present.
    /// </summary>
    private string? TryMakeClip(string name, string size, int seconds, int fps)
    {
        FFmpegBootstrap.EnsureInitialized();
        if (!FFmpegBootstrap.IsReady) return null;

        var exe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(exe)) return null;

        var outPath = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", $"testsrc2=size={size}:rate={fps}:duration={seconds}",
                     "-c:v", "libopenh264", "-pix_fmt", "yuv420p", outPath,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(120_000) || proc.ExitCode != 0) return null;
        return File.Exists(outPath) ? outPath : null;
    }

    /// <summary>
    /// Generates a clip whose every row is a left-to-right red ramp
    /// (R = x/W*255, G = B = 0), i.e. the image is identical on every row.
    /// Any row shear in the extractor shows up as a row-to-row difference.
    /// </summary>
    private string? TryMakeGradientClip(string name, string size, int seconds, int fps)
    {
        FFmpegBootstrap.EnsureInitialized();
        if (!FFmpegBootstrap.IsReady) return null;

        var exe = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!File.Exists(exe)) return null;

        var outPath = Path.Combine(_dir, name);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "-y", "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", $"color=c=black:s={size}:rate={fps}",
                     "-vf", "geq=r='X/W*255':g=0:b=0",
                     "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                     "-c:v", "libopenh264", "-pix_fmt", "yuv420p", outPath,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(120_000) || proc.ExitCode != 0) return null;
        return File.Exists(outPath) ? outPath : null;
    }

    [Fact]
    public async Task ExtractAsync_NonAlignedOutputWidth_IsNotSheared()
    {
        // Regression: av_frame_get_buffer aligns linesize to 32 bytes, so for an
        // output width that is not a multiple of 8 the pitch exceeds width*4.
        // The old contiguous copy interleaved that padding and sheared the frame
        // (diagonal stripes) on every row. 1920x1080 at maxEdge 500 yields an
        // output width of exactly 500 (pitch 2016 vs 2000) — a clean trigger.
        // All pre-existing cases happened to land on aligned widths (256, 128),
        // which is why this went unnoticed.
        var clip = TryMakeGradientClip("gradient.mp4", "1920x1080", seconds: 1, fps: 10);
        if (clip is null) return; // FFmpeg natives unavailable.

        var result = await VideoFrameExtractor.ExtractAsync(
            MediaRef.FromFile(clip, MediaKind.Video), maxEdge: 500);

        Assert.NotNull(result);
        var frame = result!.Value;
        Assert.Equal(500, frame.FrameWidth);
        Assert.Equal(281, frame.FrameHeight);
        Assert.Equal(500 * 281 * 4, frame.Bgra.Length);

        // Every row must carry the same ramp. A sheared copy shifts each row by
        // (2016 - 2000) / 4 = 4 px, so the mean |R(x,0) - R(x,y)| climbs to ~20
        // (measured 20.8); a correct copy stays at the codec's noise floor.
        byte Red(int x, int y) => frame.Bgra[(y * frame.FrameWidth + x) * 4 + 2];

        long diffSum = 0;
        long diffCount = 0;
        foreach (var y in new[] { 1, frame.FrameHeight / 4, frame.FrameHeight / 2, frame.FrameHeight - 1 })
        {
            for (var x = 0; x < frame.FrameWidth; x++)
            {
                diffSum += Math.Abs(Red(x, 0) - Red(x, y));
                diffCount++;
            }
        }

        var meanRowDiff = (double)diffSum / diffCount;
        Assert.True(
            meanRowDiff < 6.0,
            $"rows differ by {meanRowDiff:F2} — the buffer is sheared (row pitch not honoured)");
    }

    [Fact]
    public async Task ExtractAsync_ScaledVideo_ReturnsRequestedSizeAndSurvivesSwscale()
    {
        var clip = TryMakeClip("clip.mp4", "1920x1080", seconds: 2, fps: 15);
        if (clip is null) return; // FFmpeg natives unavailable.

        const int maxEdge = 256;
        var result = await VideoFrameExtractor.ExtractAsync(
            MediaRef.FromFile(clip, MediaKind.Video), maxEdge);

        Assert.NotNull(result);
        var frame = result!.Value;

        // Longest edge clamped to maxEdge, aspect preserved (1920x1080 -> 256x144).
        Assert.Equal(256, frame.FrameWidth);
        Assert.Equal(144, frame.FrameHeight);
        Assert.Equal(1920, frame.SourceWidth);
        Assert.Equal(1080, frame.SourceHeight);

        // Exactly one tightly packed BGRA8 buffer — catches stride/pitch bugs.
        Assert.Equal(frame.FrameWidth * frame.FrameHeight * 4, frame.Bgra.Length);

        // Not a flat/blank buffer: the swscale path actually wrote pixels.
        Assert.True(frame.Bgra.Distinct().Count() > 16, "frame looks blank");
    }

    [Fact]
    public async Task ExtractAsync_MultipleVideosInSequence_AllSucceed()
    {
        // The original bug surfaced on the *second* call in a process (the first
        // one sometimes survived), so a single-call test would have missed it.
        var a = TryMakeClip("a.mp4", "640x360", seconds: 1, fps: 10);
        var b = TryMakeClip("b.mp4", "1280x720", seconds: 1, fps: 10);
        if (a is null || b is null) return; // FFmpeg natives unavailable.

        foreach (var path in new[] { a, b, a, b })
        {
            var frame = await VideoFrameExtractor.ExtractAsync(
                MediaRef.FromFile(path, MediaKind.Video), 128);
            Assert.NotNull(frame);
            Assert.Equal(frame!.Value.FrameWidth * frame.Value.FrameHeight * 4, frame.Value.Bgra.Length);
        }
    }

    [Fact]
    public async Task ExtractAsync_DurationIsReported()
    {        var clip = TryMakeClip("dur.mp4", "320x240", seconds: 3, fps: 10);
        if (clip is null) return; // FFmpeg natives unavailable.

        var result = await VideoFrameExtractor.ExtractAsync(
            MediaRef.FromFile(clip, MediaKind.Video), 128);

        Assert.NotNull(result);
        var duration = result!.Value.Duration;
        Assert.NotNull(duration);
        Assert.InRange(duration!.Value.TotalSeconds, 2.0, 4.0);
    }

    [Fact]
    public async Task ExtractAsync_MissingFile_ReturnsNull()
    {
        // No natives needed: the extractor bails before touching FFmpeg.
        var missing = Path.Combine(_dir, "does-not-exist.mp4");
        var result = await VideoFrameExtractor.ExtractAsync(
            MediaRef.FromFile(missing, MediaKind.Video), 128);

        Assert.Null(result);
    }
}
