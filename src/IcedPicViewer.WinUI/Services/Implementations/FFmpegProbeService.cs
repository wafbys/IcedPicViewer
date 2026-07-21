// Copyright (c) IcedPicViewer. All rights reserved.

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Diagnostic probe that verifies FFmpeg native libraries load correctly and
/// can extract metadata + a thumbnail frame from a real video file.
///
/// This is gated by env var (see <see cref="IsProbeRequested"/>) and runs
/// only on explicit demand — by default the app boots without touching FFmpeg
/// at all, so production users are unaffected. Output is written to
/// %LOCALAPPDATA%\IcedPicViewer\ffmpeg-probe.log for post-mortem inspection.
///
/// Three phases, all best-effort (each catches and logs independently):
///   Phase 1 — Library load: call av_version_info() / avformat_version().
///   Phase 2 — File probe:   if IPV_FFMPEG_PROBE_VIDEO is set, avformat_open_input
///                            + avformat_find_stream_info, log dimensions/duration.
///   Phase 3 — Frame decode: if IPV_FFMPEG_PROBE_THUMBNAIL is set, decode one
///                            frame and save as JPEG to %LOCALAPPDATA%\IcedPicViewer\probe-thumb.jpg.
///
/// This service is intentionally throw-free — every phase is wrapped in
/// try/catch because we never want a probe failure to crash the app.
/// </summary>
public sealed class FFmpegProbeService
{
    private const string LogFileName = "ffmpeg-probe.log";
    private const string ThumbFileName = "probe-thumb.ppm";
    private const string EnableEnvVar = "IPV_FFMPEG_PROBE";
    private const string VideoEnvVar = "IPV_FFMPEG_PROBE_VIDEO";
    private const string ThumbnailEnvVar = "IPV_FFMPEG_PROBE_THUMBNAIL";

    private readonly string _logDir;
    private readonly string _logPath;

    public FFmpegProbeService()
    {
        _logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer");
        Directory.CreateDirectory(_logDir);
        _logPath = Path.Combine(_logDir, LogFileName);
    }

    /// <summary>
    /// True when the user opted in via env var (IPV_FFMPEG_PROBE=1).
    ///
    /// NOTE: MSIX packaged launch (via `winapp.exe launch`) does NOT inherit
    /// env vars from the parent `dotnet run` shell. To run the probe in MSIX
    /// packaged context you must either:
    ///   1. Set the env var inside App.OnLaunched before _window.Activate()
    ///      (compile-time override), or
    ///   2. Place a flag file at %LOCALAPPDATA%\IcedPicViewer\ffmpeg-probe.flag
    ///      (probe also checks for this — see RunAsync), or
    ///   3. Temporarily flip _forceRunForDiagnostic below.
    /// </summary>
    public static bool IsProbeRequested =>
        string.Equals(
            Environment.GetEnvironmentVariable(EnableEnvVar),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Compile-time diagnostic override. Set to <c>true</c> to bypass the
    /// env-var gate when investigating FFmpeg issues in MSIX packaged
    /// context. Default: <c>false</c> (env-var only).
    /// </summary>
    public static bool ForceRunForDiagnostic { get; set; }

    /// <summary>
    /// Runs the full probe (Phase 1 + 2 + 3 depending on env vars). Returns
    /// immediately if <see cref="IsProbeRequested"/> and
    /// <see cref="ForceRunForDiagnostic"/> are both false.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var envRequested = IsProbeRequested;
        var flagFilePath = Path.Combine(_logDir, "ffmpeg-probe.flag");
        var flagFileRequested = File.Exists(flagFilePath);
        var forceRequested = ForceRunForDiagnostic;

        if (!envRequested && !flagFileRequested && !forceRequested)
            return;

        // Truncate the previous log so each run starts fresh.
        try
        {
            File.WriteAllText(_logPath,
                $"=== FFmpeg probe started {DateTime.Now:O} ==={Environment.NewLine}" +
                $"pid={Environment.ProcessId}, os={RuntimeInformation.OSDescription}{Environment.NewLine}" +
                $"ffmpeg.autogen v{(typeof(ffmpeg).Assembly.GetName().Version?.ToString() ?? "<unknown>")}{Environment.NewLine}" +
                $"triggers: env={envRequested}, flagFile={flagFileRequested}, force={forceRequested}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Trace.TraceError($"FFmpegProbeService: failed to reset log: {ex.Message}");
            return;
        }

        Log($"pid={Environment.ProcessId}, os={RuntimeInformation.OSDescription}");
        Log($"ffmpeg.autogen v{(typeof(ffmpeg).Assembly.GetName().Version?.ToString() ?? "<unknown>")}");

        // Run all phases on a worker thread — Phase 2 + 3 do synchronous IO
        // and CPU-bound decode. They can take several seconds for a real
        // video; we MUST NOT block the UI thread (OnLaunched dispatcher).
        await Task.Run(() => RunAllPhases(cancellationToken), cancellationToken)
            .ConfigureAwait(false);

        Log($"=== FFmpeg probe finished {DateTime.Now:O} ===");
    }

    private void RunAllPhases(CancellationToken ct)
    {
        RunPhase1();
        ct.ThrowIfCancellationRequested();

        var videoPath = Environment.GetEnvironmentVariable(VideoEnvVar);
        if (!string.IsNullOrEmpty(videoPath))
        {
            RunPhase2(videoPath!);
            ct.ThrowIfCancellationRequested();

            var wantThumbnail = string.Equals(
                Environment.GetEnvironmentVariable(ThumbnailEnvVar),
                "1",
                StringComparison.Ordinal);
            if (wantThumbnail)
                RunPhase3(videoPath!);
        }
        else
        {
            Log($"phase 2/3 skipped: {VideoEnvVar}=<null> (set it to a video path to enable)");
        }
    }

    private void RunPhase1()
    {
        try
        {
            // FFmpeg.AutoGen 8.x: ffmpeg.av_version_info() is a static thin
            // wrapper around av_version_info() in libavutil. If the native
            // DLLs failed to load, the very first P/Invoke into them throws
            // DllNotFoundException or EntryPointNotFoundException.
            var versionInfo = ffmpeg.av_version_info();
            var formatVersion = ffmpeg.avformat_version();
            var codecVersion = ffmpeg.avcodec_version();

            Log("phase 1 OK (native libraries loaded)");
            Log($"  av_version_info      = {versionInfo}");
            Log($"  avformat_version()   = {formatVersion} ({(formatVersion >> 16) & 0xff}.{(formatVersion >> 8) & 0xff}.{formatVersion & 0xff})");
            Log($"  avcodec_version()    = {codecVersion} ({(codecVersion >> 16) & 0xff}.{(codecVersion >> 8) & 0xff}.{codecVersion & 0xff})");
        }
        catch (DllNotFoundException ex)
        {
            Log($"phase 1 FAIL: DllNotFoundException — {ex.Message}");
            Log("  hint: native DLLs not found at runtime. Check that");
            Log("        runtimes\\win-x64\\native\\*.dll ended up inside the AppX");
            Log("        package next to the .exe.");
            Log($"  hint detail: {ex}");
        }
        catch (Exception ex)
        {
            Log($"phase 1 FAIL: {ex.GetType().Name}: {ex.Message}");
            Log($"  {ex}");
        }
    }

    private unsafe void RunPhase2(string videoPath)
    {
        Log($"phase 2: opening {videoPath}");
        if (!File.Exists(videoPath))
        {
            Log($"phase 2 FAIL: file not found");
            return;
        }

        AVFormatContext* fmtCtx = null;
        try
        {
            var rc = ffmpeg.avformat_open_input(&fmtCtx, videoPath, null, null);
            if (rc < 0)
            {
                Log($"phase 2 FAIL: avformat_open_input returned {rc} ({FFmpegErrStr(rc)})");
                return;
            }

            rc = ffmpeg.avformat_find_stream_info(fmtCtx, null);
            if (rc < 0)
            {
                Log($"phase 2 FAIL: avformat_find_stream_info returned {rc} ({FFmpegErrStr(rc)})");
                return;
            }

            var durationSec = (fmtCtx->duration) / (double)ffmpeg.AV_TIME_BASE;
            var bitRate = fmtCtx->bit_rate;
            var formatName = fmtCtx->iformat != null
                ? Marshal.PtrToStringUTF8((IntPtr)fmtCtx->iformat->long_name) ?? "?"
                : "?";
            Log($"phase 2 OK: format={formatName}");
            Log($"  duration = {durationSec:F2}s");
            Log($"  bit_rate = {bitRate} bps");
            Log($"  nb_streams = {fmtCtx->nb_streams}");

            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var stream = fmtCtx->streams[i];
                var codecParams = stream->codecpar;
                var codecType = codecParams->codec_type;
                // FFmpeg.AutoGen 8.x: avcodec_get_name returns managed string.
                var codecName = ffmpeg.avcodec_get_name(codecParams->codec_id);
                Log($"  stream[{i}]: type={codecType} codec={codecName} "
                    + (codecType == AVMediaType.AVMEDIA_TYPE_VIDEO
                        ? $"{codecParams->width}x{codecParams->height}"
                        : ""));
            }
        }
        catch (Exception ex)
        {
            Log($"phase 2 FAIL: {ex.GetType().Name}: {ex.Message}");
            Log($"  {ex}");
        }
        finally
        {
            if (fmtCtx != null)
            {
                var fmtCtxLocal = fmtCtx;
                ffmpeg.avformat_close_input(&fmtCtxLocal);
            }
        }
    }

    private void RunPhase3(string videoPath)
    {
        Log($"phase 3: extracting first frame from {videoPath}");
        var outPath = Path.Combine(_logDir, ThumbFileName);

        try
        {
            var (rgbBytes, w, h) = ExtractFirstFrame(videoPath);
            if (rgbBytes is null)
            {
                Log($"phase 3 FAIL: frame extraction returned null");
                return;
            }

            // Output as PPM (P6 binary). PPM is trivial — header + raw RGB
            // bytes. No dependency on System.Drawing.Common or WinRT
            // BitmapEncoder for this throwaway probe. The user can convert
            // to PNG/JPEG with ImageMagick or any viewer that accepts PPM.
            using (var fs = File.Create(outPath))
            using (var writer = new StreamWriter(fs, new UTF8Encoding(false)))
            {
                writer.NewLine = "\n";
                writer.WriteLine("P6");
                writer.WriteLine($"{w} {h}");
                writer.WriteLine("255");
                writer.Flush();
                fs.Write(rgbBytes, 0, rgbBytes.Length);
            }
            Log($"phase 3 OK: wrote {w}x{h} RGB24 to {outPath} ({rgbBytes.Length:N0} bytes)");
        }
        catch (Exception ex)
        {
            Log($"phase 3 FAIL: {ex.GetType().Name}: {ex.Message}");
            Log($"  {ex}");
        }
    }

    /// <summary>
    /// Opens the video, seeks to ~10% (avoids black frames at very start for
    /// many codecs), decodes one frame as RGB24 and returns the raw pixel
    /// bytes (managed byte[]). Returns null on any error.
    ///
    /// This is intentionally a single-frame path. The eventual
    /// VideoMetadataService will wrap this + sized thumbnail generation +
    /// proper cleanup, but for the probe we want minimum surface area and
    /// zero extra deps — output is PPM (raw RGB24).
    /// </summary>
    private static unsafe (byte[]? rgb, int w, int h) ExtractFirstFrame(string videoPath)
    {
        AVFormatContext* fmtCtx = null;
        AVCodecContext* codecCtx = null;
        AVFrame* frame = null;
        AVPacket* packet = null;
        SwsContext* swsCtx = null;
        byte* rgbBuffer = null;

        try
        {
            if (ffmpeg.avformat_open_input(&fmtCtx, videoPath, null, null) < 0)
                return (null, 0, 0);
            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
                return (null, 0, 0);

            int videoStreamIdx = -1;
            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    videoStreamIdx = i;
                    break;
                }
            }
            if (videoStreamIdx < 0)
                return (null, 0, 0);

            var stream = fmtCtx->streams[videoStreamIdx];
            var codecParams = stream->codecpar;
            var decoder = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (decoder == null)
                return (null, 0, 0);

            codecCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(codecCtx, codecParams);

            if (ffmpeg.avcodec_open2(codecCtx, decoder, null) < 0)
                return (null, 0, 0);

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            var w = codecCtx->width;
            var h = codecCtx->height;

            // Seek to ~10% — many codecs have black frames at the very start.
            long seekTarget = (long)(0.1 * fmtCtx->duration);
            seekTarget = ffmpeg.av_rescale_q(seekTarget, new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE }, stream->time_base);
            ffmpeg.av_seek_frame(fmtCtx, videoStreamIdx, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);

            int decodeRc;
            bool gotFrame = false;
            for (int i = 0; i < 32 && !gotFrame; i++)
            {
                decodeRc = ffmpeg.av_read_frame(fmtCtx, packet);
                if (decodeRc < 0)
                    break;
                if (packet->stream_index != videoStreamIdx)
                {
                    ffmpeg.av_packet_unref(packet);
                    continue;
                }
                decodeRc = ffmpeg.avcodec_send_packet(codecCtx, packet);
                ffmpeg.av_packet_unref(packet);
                if (decodeRc < 0)
                    break;
                decodeRc = ffmpeg.avcodec_receive_frame(codecCtx, frame);
                if (decodeRc == 0)
                    gotFrame = true;
            }
            if (!gotFrame)
                return (null, 0, 0);

            // Convert decoded frame (typically YUV420P) to RGB24 into a
            // managed byte[] we own outright. sws_scale requires raw
            // pointers, so we round-trip through a transient native buffer
            // and copy back to managed before returning.
            swsCtx = ffmpeg.sws_getContext(
                w, h, codecCtx->pix_fmt,
                w, h, AVPixelFormat.AV_PIX_FMT_RGB24,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);

            var rgbLineSize = w * 3;
            var rgbManaged = new byte[rgbLineSize * h];
            rgbBuffer = (byte*)ffmpeg.av_malloc((ulong)(rgbLineSize * h));
            var dataPtr = new byte_ptrArray4 { [0] = rgbBuffer };
            var lineSizes = new int_array4 { [0] = rgbLineSize };
            ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, h, dataPtr, lineSizes);

            fixed (byte* rgbManagedPtr = rgbManaged)
            {
                Buffer.MemoryCopy(rgbBuffer, rgbManagedPtr, rgbManaged.Length, rgbManaged.Length);
            }
            ffmpeg.av_free(rgbBuffer);
            rgbBuffer = null;

            return (rgbManaged, w, h);
        }
        finally
        {
            if (swsCtx != null) ffmpeg.sws_freeContext(swsCtx);
            if (rgbBuffer != null) ffmpeg.av_free(rgbBuffer);
            if (packet != null) ffmpeg.av_packet_free(&packet);
            if (frame != null) ffmpeg.av_frame_free(&frame);
            if (codecCtx != null) ffmpeg.avcodec_free_context(&codecCtx);
            if (fmtCtx != null)
            {
                var localFmtCtx = fmtCtx;
                ffmpeg.avformat_close_input(&localFmtCtx);
            }
        }
    }

    private void Log(string line)
    {
        var stamp = $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}";
        try
        {
            // UTF-8 (no BOM) — File.AppendAllText default on Windows is the
            // system codepage (GBK / CP1252 / etc.), which corrupts non-ASCII
            // paths like C:\Users\??\... Probe logs need to round-trip
            // arbitrary Unicode paths, so write UTF-8 explicitly.
            File.AppendAllText(_logPath, stamp, new UTF8Encoding(false));
        }
        catch
        {
            // Best-effort logging; never crash the probe.
        }
        Trace.Write(stamp);
    }

    private static unsafe string FFmpegErrStr(int err)
    {
        const int bufSize = 128;
        byte* buf = stackalloc byte[bufSize];
        ffmpeg.av_strerror(err, buf, (ulong)bufSize);
        return Marshal.PtrToStringUTF8((IntPtr)buf) ?? $"code={err}";
    }
}