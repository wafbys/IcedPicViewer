// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SkiaImage = SixLabors.ImageSharp.Image;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Plays multi-frame GIF (and other animated ImageSharp formats) by swapping
/// <see cref="Bitmap"/> frames on a <see cref="DispatcherTimer"/>.
/// </summary>
public sealed class GifAnimationPlayer : IDisposable
{
    private readonly List<(Bitmap Frame, int DelayMs)> _frames = new();
    private DispatcherTimer? _timer;
    private int _index;
    private bool _disposed;
    private Action<Bitmap?>? _onFrame;

    /// <summary>Hard cap to prevent unbounded memory use on pathological GIFs.</summary>
    private const int MaxFrames = 200;

    public bool HasAnimation => _frames.Count > 1;

    public Bitmap? FirstFrame => _frames.Count > 0 ? _frames[0].Frame : null;

    /// <summary>
    /// Decode all frames from <paramref name="stream"/>. Caps pixel long edge
    /// at <paramref name="maxEdge"/> (same policy as still full-image load).
    /// </summary>
    public static async Task<GifAnimationPlayer?> TryLoadAsync(Stream stream, int maxEdge, CancellationToken ct)
    {
        try
        {
            return await Task.Run(() => LoadCore(stream, maxEdge), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"GifAnimationPlayer.Load: {ex.Message}");
            return null;
        }
    }

    private static GifAnimationPlayer LoadCore(Stream stream, int maxEdge)
    {
        if (stream.CanSeek) stream.Position = 0;

        using var image = SkiaImage.Load<Rgba32>(stream);
        image.Mutate(ctx => ctx.AutoOrient());

        var w = image.Width;
        var h = image.Height;
        if (w <= 0 || h <= 0) return new GifAnimationPlayer();

        var longest = Math.Max(w, h);
        var scale = longest > maxEdge ? maxEdge / (double)longest : 1.0;
        var tw = Math.Max(1, (int)Math.Round(w * scale));
        var th = Math.Max(1, (int)Math.Round(h * scale));

        var player = new GifAnimationPlayer();
        var frameCount = image.Frames.Count;

        // Single frame — treat as still (caller can use FullImage only).
        if (frameCount <= 1)
        {
            if (scale < 1.0)
                image.Mutate(ctx => ctx.Resize(tw, th));
            player._frames.Add((ToBitmap(image), 100));
            return player;
        }

        // Decimate when frame count exceeds the safety cap.
        var step = 1;
        if (frameCount > MaxFrames)
        {
            step = (int)Math.Ceiling((double)frameCount / MaxFrames);
            Trace.TraceWarning($"GifAnimationPlayer: {frameCount} frames, decimating by {step}x to stay under {MaxFrames}");
        }

        // When decimating, accumulate delays of skipped frames into the next kept
        // frame so total playback duration stays roughly correct (not N× faster).
        var pendingDelayMs = 0;
        for (var i = 0; i < frameCount; i++)
        {
            var frameDelayMs = 100;
            try
            {
                // Read delay from the source frame metadata without cloning when skipped.
                var metaFrame = image.Frames[i];
                var gif = metaFrame.Metadata.GetGifMetadata();
                // FrameDelay is in hundredths of a second.
                if (gif.FrameDelay > 0)
                    frameDelayMs = Math.Max(20, gif.FrameDelay * 10);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"GifAnimationPlayer frame metadata: {ex.Message}");
            }

            if (i % step != 0)
            {
                pendingDelayMs += frameDelayMs;
                continue;
            }

            using var frameImage = image.Frames.CloneFrame(i);
            if (scale < 1.0)
                frameImage.Mutate(ctx => ctx.Resize(tw, th));

            var delay = Math.Max(20, frameDelayMs + pendingDelayMs);
            pendingDelayMs = 0;
            player._frames.Add((ToBitmap(frameImage), delay));
            if (player._frames.Count >= MaxFrames) break;
        }

        return player;
    }

    private static Bitmap ToBitmap(SkiaImage image)
    {
        var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        ms.Position = 0;
        return new Bitmap(ms);
    }

    /// <summary>
    /// Begin cycling frames; each frame is pushed via <paramref name="onFrame"/>
    /// on the UI thread.
    /// </summary>
    public void Start(Action<Bitmap?> onFrame)
    {
        Stop();
        _onFrame = onFrame;
        if (_frames.Count == 0) return;

        _index = 0;
        onFrame(_frames[0].Frame);
        if (_frames.Count == 1) return;

        _timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(_frames[0].DelayMs),
            DispatcherPriority.Normal,
            OnTick);
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_frames.Count == 0 || _onFrame is null) return;
        _index = (_index + 1) % _frames.Count;
        var (frame, delay) = _frames[_index];
        _onFrame(frame);
        if (_timer is not null)
            _timer.Interval = TimeSpan.FromMilliseconds(delay);
    }

    public void Stop()
    {
        if (_timer is not null)
        {
            _timer.Stop();
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        foreach (var (frame, _) in _frames)
            frame.Dispose();
        _frames.Clear();
        _onFrame = null;
    }
}
