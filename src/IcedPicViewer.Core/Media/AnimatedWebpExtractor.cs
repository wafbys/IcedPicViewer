// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using FFmpeg.AutoGen;
using IcedPicViewer.Models;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// One frame of an animated image: tightly packed top-down BGRA8 pixels
/// (<c>Width * Height * 4</c> bytes, no row padding) plus the frame's display
/// duration.
/// </summary>
public readonly record struct AnimatedFrame(byte[] Bgra, int Width, int Height, int DelayMs);

/// <summary>
/// Decodes every frame of an animated WebP through FFmpeg. The platform
/// imaging stack (WIC + XAML) serves WebP as a still frame — verified
/// 2026-10-14 with a two-phase animation fed through BitmapImage — so the
/// viewer needs its own frame source to play these files.
///
/// <para>
/// Scope: regular files only. Archive entries and still images return null;
/// callers keep the existing still-image path. Guards: more than
/// <see cref="MaxFrames"/> frames or more than <see cref="MaxTotalBytes"/>
/// decoded pixels abort to null (a runaway animation must not balloon
/// memory) — the still first frame remains as the fallback.
/// </para>
/// </summary>
public static class AnimatedWebpExtractor
{
    private const int MaxFrames = 300;
    private const long MaxTotalBytes = 512L * 1024 * 1024;
    private const int MaxEdge = 1920; // long-side cap; matches the viewer's full-image budget
    private const int DefaultDelayMs = 100;
    private const int MinDelayMs = 20;
    private const int MaxDelayMs = 10_000;

    /// <summary>
    /// All frames of an animated WebP, or null when the file is a still
    /// image (≤ 1 frame), not a regular file, FFmpeg is unavailable, or a
    /// cap was hit.
    /// </summary>
    public static Task<IReadOnlyList<AnimatedFrame>?> TryExtractAsync(
        MediaRef media, CancellationToken ct = default)
    {
        FFmpegBootstrap.EnsureInitialized();
        if (!FFmpegBootstrap.IsReady || media.IsInArchive || !File.Exists(media.Path))
            return Task.FromResult<IReadOnlyList<AnimatedFrame>?>(null);

        return Task.Run<IReadOnlyList<AnimatedFrame>?>(() => ExtractAllFrames(media.Path, ct), ct);
    }

    private static unsafe List<AnimatedFrame>? ExtractAllFrames(string path, CancellationToken ct)
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVFrame* scaledFrame = null;
        AVPacket* packet = null;
        SwsContext* swsCtx = null;
        var swsSrcFormat = AVPixelFormat.AV_PIX_FMT_NONE;

        try
        {
            if (ffmpeg.avformat_open_input(&fmtCtx, path, null, null) < 0) return null;
            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0) return null;
            if (ct.IsCancellationRequested) return null;

            int videoStreamIdx = -1;
            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIdx = i;
                    break;
                }
            }
            if (videoStreamIdx < 0) return null;

            var stream = fmtCtx->streams[videoStreamIdx];
            var codecParams = stream->codecpar;
            var decoder = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (decoder == null) return null;

            codecCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(codecCtx, codecParams);
            if (ffmpeg.avcodec_open2(codecCtx, decoder, null) < 0) return null;

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            var srcW = codecCtx->width;
            var srcH = codecCtx->height;
            if (srcW <= 0 || srcH <= 0) return null;

            int outW, outH;
            if (srcW >= srcH)
            {
                outW = Math.Min(MaxEdge, srcW);
                outH = Math.Max(1, (int)Math.Round((double)srcH * outW / srcW));
            }
            else
            {
                outH = Math.Min(MaxEdge, srcH);
                outW = Math.Max(1, (int)Math.Round((double)srcW * outH / srcH));
            }

            var frames = new List<AnimatedFrame>();
            var totalBytes = 0L;
            var capped = false;
            var lastDelayMs = DefaultDelayMs;

            while (!capped)
            {
                if (ct.IsCancellationRequested) return null;
                var readRc = ffmpeg.av_read_frame(fmtCtx, packet);
                if (readRc < 0) break;
                if (packet->stream_index != videoStreamIdx)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                var sendRc = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (sendRc < 0) continue; // undecodable frame — skip it

                while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
                {
                    if (!TryAppendFrame(
                            frame, ref scaledFrame, ref swsCtx, ref swsSrcFormat,
                            srcW, srcH, outW, outH, stream,
                            frames, ref totalBytes, ref lastDelayMs))
                    {
                        capped = true;
                        break;
                    }
                }
            }

            // Flush the decoder (the last frame(s) may still be buffered).
            if (!capped)
            {
                ffmpeg.avcodec_send_packet(codecCtx, null);
                while (ffmpeg.avcodec_receive_frame(codecCtx, frame) == 0)
                {
                    if (!TryAppendFrame(
                            frame, ref scaledFrame, ref swsCtx, ref swsSrcFormat,
                            srcW, srcH, outW, outH, stream,
                            frames, ref totalBytes, ref lastDelayMs))
                    {
                        capped = true;
                        break;
                    }
                }
            }

            if (capped)
            {
                Trace.TraceInformation($"AnimatedWebpExtractor: caps exceeded for {path} — keeping the still frame");
                return null;
            }

            // A still WebP decodes to one frame — that is the normal static
            // path; only real animations reach the viewer's frame clock.
            return frames.Count >= 2 ? frames : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"AnimatedWebpExtractor: {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            if (scaledFrame != null) ffmpeg.av_frame_free(&scaledFrame);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (fmtCtx != null)
            {
                var local = fmtCtx;
                ffmpeg.avformat_close_input(&local);
            }
        }
    }

    /// <summary>
    /// Converts + copies the decoder's current frame into a tightly packed
    /// BGRA buffer and appends it. Returns false when a budget cap is hit
    /// (caller aborts to the still-image fallback).
    ///
    /// <para>
    /// The sws context is created lazily from the DECODED frame's actual
    /// pixel format — NOT from <c>codecCtx-&gt;pix_fmt</c>. Lossy WebP with
    /// alpha decodes to yuva420p while the codec context may advertise argb;
    /// building the converter from the advertised format misinterprets the
    /// input and produces garbage pixels (observed 2026-10-14: "three gray
    /// bars" for a solid red/blue animation; the colour assertions in
    /// AnimatedWebpExtractorTests now pin this down).
    /// </para>
    /// </summary>
    private static unsafe bool TryAppendFrame(
        AVFrame* frame,
        ref AVFrame* scaledFrame,
        ref SwsContext* swsCtx,
        ref AVPixelFormat swsSrcFormat,
        int srcW,
        int srcH,
        int outW,
        int outH,
        AVStream* stream,
        List<AnimatedFrame> frames,
        ref long totalBytes,
        ref int lastDelayMs)
    {
        var frameFormat = (AVPixelFormat)frame->format;
        if (swsCtx == null || frameFormat != swsSrcFormat)
        {
            if (swsCtx != null)
            {
                ffmpeg.sws_freeContext(swsCtx);
                swsCtx = null;
            }
            if (scaledFrame != null)
            {
                // av_frame_free takes the address of the pointer; a ref
                // parameter cannot be addressed directly, so free the local
                // copy and null the reference (the caller's finally frees
                // whatever remains — nulling avoids a double free).
                var localFrame = scaledFrame;
                ffmpeg.av_frame_free(&localFrame);
                scaledFrame = null;
            }

            // sws_scale_frame (frame-based API) — the pointer-based
            // sws_scale binding crashed against FFmpeg 8+; see
            // VideoFrameExtractor for the full ABI chronology.
            swsCtx = ffmpeg.sws_getContext(
                srcW, srcH, frameFormat,
                outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_LANCZOS, null, null, null);
            if (swsCtx == null) return false;

            scaledFrame = ffmpeg.av_frame_alloc();
            if (scaledFrame == null) return false;
            scaledFrame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
            scaledFrame->width = outW;
            scaledFrame->height = outH;
            if (ffmpeg.av_frame_get_buffer(scaledFrame, 32) < 0) return false;
            swsSrcFormat = frameFormat;
        }

        // Per-frame display duration. The WebP demuxer fills AVFrame.duration
        // in stream time_base units (1/1000 for WebP — i.e. milliseconds);
        // fall back to the previous frame's delay, then to the default.
        var delayMs = lastDelayMs;
        if (frame->duration > 0)
        {
            delayMs = (int)Math.Round(frame->duration * ffmpeg.av_q2d(stream->time_base) * 1000.0);
        }
        delayMs = Math.Clamp(delayMs <= 0 ? DefaultDelayMs : delayMs, MinDelayMs, MaxDelayMs);
        lastDelayMs = delayMs;

        if (ffmpeg.sws_scale_frame(swsCtx, scaledFrame, frame) < 0) return true;

        // Row-by-row copy honouring linesize (av_frame_get_buffer aligns the
        // pitch past width*4; copying contiguously shears the image — the
        // same trap VideoFrameExtractor documents at length).
        var rowBytes = outW * 4;
        var bgra = new byte[rowBytes * outH];
        fixed (byte* destination = bgra)
        {
            var source = scaledFrame->data[0];
            var pitch = scaledFrame->linesize[0];
            for (var y = 0; y < outH; y++)
            {
                Buffer.MemoryCopy(
                    source + (long)y * pitch,
                    destination + (long)y * rowBytes,
                    rowBytes,
                    rowBytes);
            }
        }

        totalBytes += bgra.Length;
        if (totalBytes > MaxTotalBytes || frames.Count >= MaxFrames) return false;

        frames.Add(new AnimatedFrame(bgra, outW, outH, delayMs));
        return true;
    }
}
