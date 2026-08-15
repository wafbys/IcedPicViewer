// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using FFmpeg.AutoGen;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// One scaled BGRA8 frame plus media metadata from a single FFmpeg open.
/// </summary>
public readonly record struct VideoFrameExtract(
    byte[] Bgra,
    int FrameWidth,
    int FrameHeight,
    int SourceWidth,
    int SourceHeight,
    TimeSpan? Duration);

/// <summary>
/// Platform-agnostic first-frame extraction via FFmpeg.AutoGen.
/// Returns top-down BGRA8 pixels suitable for any UI shell.
/// </summary>
public static class VideoFrameExtractor
{
    /// <summary>
    /// Extracts one scaled frame. Returns null when FFmpeg is unavailable
    /// or the file cannot be decoded. Duration is filled when the container
    /// reports a positive <c>fmtCtx-&gt;duration</c> (same open as the frame).
    /// </summary>
    public static Task<VideoFrameExtract?> ExtractAsync(
        MediaRef media, int maxEdge, CancellationToken ct = default)
    {
        FFmpegBootstrap.EnsureInitialized();
        if (!FFmpegBootstrap.IsReady || maxEdge < 1)
            return Task.FromResult<VideoFrameExtract?>(null);

        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (media.IsInArchive)
            {
                var tempDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IcedPicViewer", "TempVideo");
                Directory.CreateDirectory(tempDir);
                var ext = Path.GetExtension(media.ArchiveEntry ?? ".mp4");
                if (string.IsNullOrEmpty(ext)) ext = ".mp4";
                var tempPath = Path.Combine(tempDir, $"ipv-thumb-{Guid.NewGuid():N}{ext}");
                try
                {
                    ArchiveHelper.ExtractEntryToFile(media.Path, media.ArchiveEntry!, tempPath);
                    if (!File.Exists(tempPath)) return null;
                    return ExtractAndScaleFrame(tempPath, maxEdge, ct);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"VideoFrameExtractor archive: {media}: {ex.Message}");
                    return null;
                }
                finally
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch (Exception ex) { Trace.TraceError($"VideoFrameExtractor temp cleanup: {ex.Message}"); }
                }
            }

            if (!File.Exists(media.Path)) return null;
            return ExtractAndScaleFrame(media.Path, maxEdge, ct);
        }, ct);
    }

    private static unsafe VideoFrameExtract? ExtractAndScaleFrame(
        string path, int maxSize, CancellationToken ct)
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* swsCtx = null;
        byte* bgraBuffer = null;

        try
        {
            if (ffmpeg.avformat_open_input(&fmtCtx, path, null, null) < 0) return null;
            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0) return null;
            if (ct.IsCancellationRequested) return null;

            TimeSpan? duration = null;
            if (fmtCtx->duration > 0)
            {
                // AV_TIME_BASE ticks (microseconds-scale).
                duration = TimeSpan.FromSeconds(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE);
            }

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
                outW = Math.Min(maxSize, srcW);
                outH = Math.Max(1, (int)Math.Round((double)srcH * outW / srcW));
            }
            else
            {
                outH = Math.Min(maxSize, srcH);
                outW = Math.Max(1, (int)Math.Round((double)srcW * outH / srcH));
            }
            if (outW <= 0 || outH <= 0) return null;

            // Seek ~10% to skip common black leader frames.
            if (fmtCtx->duration > 0)
            {
                long seekTarget = (long)(0.1 * fmtCtx->duration);
                seekTarget = ffmpeg.av_rescale_q(
                    seekTarget,
                    new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
                    stream->time_base);
                ffmpeg.av_seek_frame(fmtCtx, videoStreamIdx, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
            }

            bool gotFrame = false;
            for (int i = 0; i < 32 && !gotFrame; i++)
            {
                if (ct.IsCancellationRequested) return null;
                var decodeRc = ffmpeg.av_read_frame(fmtCtx, packet);
                if (decodeRc < 0) break;
                if (packet->stream_index != videoStreamIdx)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                decodeRc = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (decodeRc < 0) break;
                decodeRc = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                if (decodeRc == 0) gotFrame = true;
            }
            if (!gotFrame) return null;

            swsCtx = ffmpeg.sws_getContext(
                srcW, srcH, codecCtx->pix_fmt,
                outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_LANCZOS, null, null, null);
            if (swsCtx == null) return null;

            var bgraLineSize = outW * 4;
            var bgraStride = bgraLineSize * outH;
            var bgraManaged = new byte[bgraStride];
            bgraBuffer = (byte*)ffmpeg.av_malloc((ulong)bgraStride);
            var dataPtr = new byte_ptrArray4 { [0] = bgraBuffer };
            var lineSizes = new int_array4 { [0] = bgraLineSize };
            ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, srcH, dataPtr, lineSizes);

            fixed (byte* bgraPtr = bgraManaged)
            {
                Buffer.MemoryCopy(bgraBuffer, bgraPtr, bgraManaged.Length, bgraManaged.Length);
            }
            ffmpeg.av_free(bgraBuffer);
            bgraBuffer = null;

            return new VideoFrameExtract(bgraManaged, outW, outH, srcW, srcH, duration);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Trace.TraceError($"VideoFrameExtractor: {path}: {ex.Message}");
            return null;
        }
        finally
        {
            if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            if (bgraBuffer != null) ffmpeg.av_free(bgraBuffer);
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
}
