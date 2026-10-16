// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Tests.Media;

/// <summary>
/// Animated WebP extraction. The platform imaging stack (WIC + XAML) serves
/// WebP as a still frame — verified 2026-10-14 — so these decoded frames
/// feed the viewer's own frame clock. Clips are synthesised with the bundled
/// ffmpeg.exe; the natives are not in git, so every test returns early when
/// FFmpeg is unavailable instead of failing.
/// </summary>
public sealed class AnimatedWebpExtractorTests : IDisposable
{
    private readonly string _dir;

    public AnimatedWebpExtractorTests()
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
            // Best-effort temp cleanup, same convention as the other tests.
        }
    }

    /// <summary>
    /// Two-phase red → blue animation. libwebp writes an animated file as
    /// soon as it gets multiple frames plus <c>-loop</c>.
    /// </summary>
    private string? TryMakeAnimatedWebp(string name) => TryRunFfmpeg(name, AnimatedWebpArgs);

    private string? TryMakeStillWebp(string name) => TryRunFfmpeg(name, StillWebpArgs);

    // Argument lists as static readonly fields (CA1861): TryRunFfmpeg only reads
    // them (each call appends to its own ArgumentList, never to the array).
    private static readonly string[] AnimatedWebpArgs =
    {
        "-y", "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=c=red:s=160x120:d=0.3",
        "-f", "lavfi", "-i", "color=c=blue:s=160x120:d=0.3",
        "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
        "-map", "[v]", "-c:v", "libwebp", "-loop", "0",
    };

    private static readonly string[] StillWebpArgs =
    {
        "-y", "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=c=green:s=160x120",
        "-frames:v", "1", "-c:v", "libwebp",
    };

    private string? TryRunFfmpeg(string name, string[] args)
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
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(outPath);

        using var proc = Process.Start(psi);
        if (proc is null) return null;
        proc.StandardError.ReadToEnd();
        proc.StandardOutput.ReadToEnd();
        if (!proc.WaitForExit(120_000) || proc.ExitCode != 0) return null;
        return File.Exists(outPath) ? outPath : null;
    }

    [Fact]
    public async Task TryExtractAsync_AnimatedWebp_ReturnsAllFramesWithDelays()
    {
        var webp = TryMakeAnimatedWebp("anim.webp");
        if (webp is null) return; // FFmpeg natives unavailable.

        var frames = await AnimatedWebpExtractor.TryExtractAsync(MediaRef.FromFile(webp));

        Assert.NotNull(frames);
        Assert.True(frames!.Count >= 2, $"expected an animation, got {frames.Count} frame(s)");

        foreach (var frame in frames)
        {
            // Tightly packed BGRA8 at the source size — catches stride bugs.
            Assert.Equal(160, frame.Width);
            Assert.Equal(120, frame.Height);
            Assert.Equal(160 * 120 * 4, frame.Bgra.Length);
            Assert.InRange(frame.DelayMs, 20, 10_000);
        }

        // The red phase must not equal the blue phase — proves real frames,
        // not N copies of the first.
        Assert.False(
            frames[0].Bgra.AsSpan().SequenceEqual(frames[^1].Bgra),
            "first and last frame are identical — extraction returned a still");

        // Colour spot-checks — the regression guard for the format bug of
        // 2026-10-14: the scaler was built from codecCtx->pix_fmt (argb)
        // while lossy WebP decodes to yuva420p, so every frame came out as
        // garbage ("three gray bars") that still differed between frames.
        // A mere "frames differ" assertion happily passed that build; these
        // assertions pin the actual pixels. BGRA byte order: [0]=B, [2]=R.
        static (int R, int B) CenterRgb(AnimatedFrame f)
        {
            var i = ((f.Height / 2) * f.Width + f.Width / 2) * 4;
            return (f.Bgra[i + 2], f.Bgra[i]);
        }

        var (firstR, firstB) = CenterRgb(frames[0]);
        Assert.True(
            firstR > 200 && firstB < 60,
            $"first frame should be solid red, got R={firstR} B={firstB}");

        var (lastR, lastB) = CenterRgb(frames[^1]);
        Assert.True(
            lastB > 200 && lastR < 60,
            $"last frame should be solid blue, got R={lastR} B={lastB}");
    }

    [Fact]
    public async Task TryExtractAsync_StillWebp_ReturnsNull()
    {
        var webp = TryMakeStillWebp("still.webp");
        if (webp is null) return;

        var frames = await AnimatedWebpExtractor.TryExtractAsync(MediaRef.FromFile(webp));

        Assert.Null(frames);
    }

    [Fact]
    public async Task TryExtractAsync_TinyAnimation_UpscalesToCrispSize()
    {
        // 16×16 pixel-art animation ≤ RenderPolicy.PixelCrispMaxEdge → the
        // extractor upscales it (integer factor, nearest-neighbour), so the
        // viewer shows crisp blocks instead of interpolated mush.
        var webp = TryRunFfmpeg("tiny.webp", new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "color=c=red:s=16x16:d=0.3",
            "-f", "lavfi", "-i", "color=c=blue:s=16x16:d=0.3",
            "-filter_complex", "[0:v][1:v]concat=n=2:v=1:a=0[v]",
            "-map", "[v]", "-c:v", "libwebp", "-loop", "0",
        });
        if (webp is null) return; // FFmpeg natives unavailable.

        var frames = await AnimatedWebpExtractor.TryExtractAsync(MediaRef.FromFile(webp));

        Assert.NotNull(frames);
        Assert.True(frames!.Count >= 2);
        // 16 × 16 upscaled by factor 16 (256 / 16) → 256×256.
        Assert.Equal(256, frames[0].Width);
        Assert.Equal(256, frames[0].Height);
        Assert.Equal(256 * 256 * 4, frames[0].Bgra.Length);

        static (int R, int B) Pixel(AnimatedFrame f, int x, int y)
        {
            var i = (y * f.Width + x) * 4;
            return (f.Bgra[i + 2], f.Bgra[i]);
        }

        var (midR, midB) = Pixel(frames[0], frames[0].Width / 2, frames[0].Height / 2);
        Assert.True(midR > 200 && midB < 60, $"first frame should stay solid red after upscale, got R={midR} B={midB}");

        var (lastR, lastB) = Pixel(frames[^1], frames[^1].Width / 2, frames[^1].Height / 2);
        Assert.True(lastB > 200 && lastR < 60, $"last frame should stay solid blue after upscale, got R={lastR} B={lastB}");
    }

    [Fact]
    public async Task TryExtractAsync_MissingFile_ReturnsNull()
    {
        var missing = Path.Combine(_dir, "does-not-exist.webp");
        var frames = await AnimatedWebpExtractor.TryExtractAsync(MediaRef.FromFile(missing));
        Assert.Null(frames);
    }
}
