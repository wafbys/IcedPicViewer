// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// Production wrapper around FFmpeg.AutoGen for video metadata + first-frame
/// extraction + archive playback. Four entry points:
///
/// <list type="bullet">
///   <item><see cref="GetVideoMetadataAsync"/> — opens the container, reads
///         codec params + duration, closes. No frame decode. Cheap (single
///         disk read + parse), used by the gallery's pre-fetch pass to
///         fill in pixel dimensions and Duration on every VideoItem before
///         it lands in the grid.</item>
///   <item><see cref="ExtractVideoThumbnailAsync"/> — opens the container,
///         finds the video stream, decodes one frame, scales it down to
///         the requested max size, and returns it as a <see cref="BitmapImage"/>
///         that can be bound directly in XAML like an image thumbnail.</item>
///   <item><see cref="GetPlaybackFilePathAsync"/> — returns a file path
///         that <c>MediaPlayer</c> can read. For loose files this is the
///         real path; for archive entries it materialises the entry to a
///         fresh temp file that lives until the caller releases it.</item>
///   <item><see cref="ReleasePlaybackFilePath"/> — cleans up the temp
///         file a prior <see cref="GetPlaybackFilePathAsync"/> call
///         created (no-op for loose files). Idempotent.</item>
/// </list>
///
/// <para>
/// All native work is offloaded to the thread pool via <c>Task.Run</c>
/// because every FFmpeg P/Invoke in the AutoGen 8.x wrapper is
/// synchronous + CPU-bound (a 1080p H.264 decode pegs a core for
/// ~200 ms on a fast machine; AV1 can take seconds). The WinRT-side
/// conversion (SoftwareBitmap → PNG → BitmapImage) runs on the calling
/// thread, which for the gallery is the UI thread, where BitmapImage
/// is required to live.
/// </para>
///
/// <para>
/// <b>Warm-up:</b> the very first FFmpeg API call costs ~6.5 s (native
/// DLL load + AutoGen wrapper JIT). Doing that on the UI thread when
/// the user first clicks on a video would freeze the window for that
/// whole window. We pre-empt it by calling <c>ffmpeg.av_version_info()</c>
/// in a background task from the constructor — by the time the user
/// actually tries to open a video, the native side is already warm.
/// The call is fire-and-forget so the constructor itself is fast, and
/// idempotent so a future Transient / Scoped registration would still
/// only warm up once.
/// </para>
///
/// <para>
/// <b>Archive support:</b> video entries inside archives are handled
/// by extracting the entry to a fresh temp file in
/// <c>%LOCALAPPDATA%\IcedPicViewer\TempVideo\</c> for the duration of
/// the call (or for the duration of playback, in the playback path).
/// FFmpeg needs a real file path or a custom AVIO context — there's
/// no built-in way to feed it a SharpCompress MemoryStream short of
/// the AVIO read callback, which is significantly more code. Temp
/// files are tracked in <see cref="_playbackTempFiles"/> and deleted
/// by <see cref="ReleasePlaybackFilePath"/> or the service's final
/// cleanup in <see cref="Dispose"/>. Disk cost is the entry's
/// decompressed size; for typical 1080p H.264 this is 1-3 GB during
/// playback. The service also cleans up any stray files left in the
/// temp dir at construction time (handles a previous unclean
/// shutdown).
/// </para>
/// </summary>
public sealed class VideoMetadataService : IVideoMetadataService, IDisposable
{
    // 0 = not warmed up, 1 = warm-up task scheduled. Set via Interlocked
    // so the first instance to run EnsureWarmedUp schedules the work and
    // any later instances short-circuit.
    private static int _warmedUp;

    // Shared with IImageLoader so a video thumb and an image thumb with
    // the same path don't collide (cache key includes MediaKind), and so
    // a 200-cap LRU doesn't get dominated by whichever kind the user
    // opens first.
    private readonly IThumbnailCache _thumbnailCache;

    // Temp files we've created for archive playback paths. The list is
    // mutated on the UI thread (PlayAsync / ReleasePlaybackFilePath) and
    // walked on the UI thread + the service's Dispose, so no lock is
    // strictly needed; the lock is defensive in case a future async
    // path lets two threads race on the same archive video.
    private readonly List<string> _playbackTempFiles = new();
    private readonly object _tempLock = new();

    // %LOCALAPPDATA%\IcedPicViewer\TempVideo\ — created on first use,
    // cleaned (files deleted) at construction + Dispose.
    private readonly string _tempDir;

    public VideoMetadataService(IThumbnailCache thumbnailCache)
    {
        _thumbnailCache = thumbnailCache;
        _tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer",
            "TempVideo");
        Directory.CreateDirectory(_tempDir);

        // Sweep any stale temp files from a previous (possibly crashed)
        // process. Better to lose a couple of MB of old video data than
        // to leave hundreds of GB lying around for months. Each file is
        // deleted in isolation — a locked file (FFmpeg dll still has a
        // handle) just stays put until next launch.
        try
        {
            foreach (var file in Directory.EnumerateFiles(_tempDir, "ipv-video-*"))
            {
                try { File.Delete(file); }
                catch (Exception ex) { Trace.TraceWarning($"VideoMetadataService: stale temp file cleanup failed for {file}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"VideoMetadataService: temp dir enumeration failed: {ex.Message}");
        }

        EnsureWarmedUp();
    }

    public void Dispose()
    {
        // Best-effort cleanup of any temp files the caller forgot to
        // release. Same isolation as the ctor sweep — one locked file
        // doesn't block the others.
        lock (_tempLock)
        {
            foreach (var file in _playbackTempFiles)
            {
                try { if (File.Exists(file)) File.Delete(file); }
                catch (Exception ex) { Trace.TraceWarning($"VideoMetadataService.Dispose: failed to delete {file}: {ex.Message}"); }
            }
            _playbackTempFiles.Clear();
        }
    }

    /// <summary>
    /// Fire-and-forget warm-up. Called from the constructor so that the
    /// first time DI resolves this service, the FFmpeg native side starts
    /// loading in the background. <see cref="App.OnLaunched"/> also
    /// resolves the service explicitly so the warm-up starts during app
    /// startup, not when the user first opens a folder.
    /// </summary>
    private static void EnsureWarmedUp()
    {
        if (Interlocked.CompareExchange(ref _warmedUp, 1, 0) != 0) return;

        _ = Task.Run(() =>
        {
            try
            {
                // ffmpeg.av_version_info() touches the native DLLs through
                // the AutoGen wrapper. On a fresh process this is the call
                // that costs the ~6.5 s — LoadLibrary on avutil-60 /
                // avcodec-62 / avformat-62 + JIT-compile of the static
                // wrapper methods. After this returns, every later
                // avformat_* / avcodec_* call is in the millisecond range.
                var v = ffmpeg.av_version_info();
                Trace.TraceInformation($"VideoMetadataService: FFmpeg warm-up OK ({v})");
            }
            catch (Exception ex)
            {
                // Don't crash on warm-up failure. Actual metadata / frame
                // calls will surface a clearer error to the caller if the
                // DLLs are genuinely missing; warm-up is best-effort.
                Trace.TraceError($"VideoMetadataService: FFmpeg warm-up failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    public async Task<VideoMetadata?> GetVideoMetadataAsync(ImageSource source, CancellationToken ct = default)
    {
        if (source.IsInArchive)
        {
            // Extract to a temp file, then run the metadata read on
            // that. The temp file is untracked — we delete it ourselves
            // after the read, regardless of outcome (success or
            // failure). Playback uses a different, longer-lived path.
            var tempPath = CreateTempFilePathForSource(source);
            try
            {
                await Task.Run(() => ArchiveHelper.ExtractEntryToFile(source.Path, source.ArchiveEntry!, tempPath), ct);
                if (ct.IsCancellationRequested) return null;
                if (!File.Exists(tempPath)) return null;
                return await Task.Run(() => GetMetadataFromFile(tempPath, ct), ct);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VideoMetadataService.GetVideoMetadataAsync archive error for {source}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                TryDeleteTempFileUntracked(tempPath);
            }
        }
        if (!File.Exists(source.Path))
        {
            return null;
        }
        return await Task.Run(() => GetMetadataFromFile(source.Path, ct), ct);
    }

    public async Task<BitmapImage?> ExtractVideoThumbnailAsync(ImageSource source, int maxSize, CancellationToken ct = default)
    {
        if (maxSize < 1)
        {
            return null;
        }

        // Cache key shape matches IImageLoader (path|size|kind) so the
        // shared LRU is one cache, not two parallel ones. We look this
        // up BEFORE doing any file work — a hit on an archive source
        // skips the temp-file extract entirely, and the same thumbnail
        // gets reused across gallery scrolls even though the underlying
        // extraction is expensive.
        var cacheKey = $"{source}|{maxSize}|{source.Kind}";
        if (_thumbnailCache.TryGet(cacheKey, out var cached))
        {
            return cached;
        }

        if (source.IsInArchive)
        {
            // Same extract→decode→delete shape as the metadata path.
            // The temp file is untracked; it's gone before this
            // method returns. The decoded bitmap lives on as a
            // BitmapImage (which is what the gallery binds to), and
            // its source on disk doesn't need to exist past the
            // decode — the BitmapImage's pixel buffer is in managed
            // memory by the time we return.
            var tempPath = CreateTempFilePathForSource(source);
            try
            {
                await Task.Run(() => ArchiveHelper.ExtractEntryToFile(source.Path, source.ArchiveEntry!, tempPath), ct);
                if (ct.IsCancellationRequested) return null;
                if (!File.Exists(tempPath)) return null;
                var (bgra, width, height) = await Task.Run(() => ExtractAndScaleFrame(tempPath, maxSize, ct), ct);
                if (bgra is null) return null;
                if (ct.IsCancellationRequested) return null;
                var bitmapImage = await BgraToBitmapImageAsync(bgra, width, height, ct);
                if (bitmapImage != null)
                {
                    _thumbnailCache.Store(cacheKey, bitmapImage);
                }
                return bitmapImage;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VideoMetadataService.ExtractVideoThumbnailAsync archive error for {source}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                TryDeleteTempFileUntracked(tempPath);
            }
        }

        if (!File.Exists(source.Path))
        {
            return null;
        }

        // Loose file: decode + scale on a worker thread. FFmpeg
        // P/Invoke is sync and CPU-bound; running it inline would
        // block the UI thread for the entire decode.
        var (bgra2, width2, height2) = await Task.Run(() => ExtractAndScaleFrame(source.Path, maxSize, ct), ct);
        if (bgra2 is null)
        {
            return null;
        }
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        // Convert BGRA8 → BitmapImage. Runs on the calling thread
        // (UI thread for the gallery path). BitmapEncoder /
        // BitmapImage are safe here and need the dispatcher's STA
        // affinity for property access in the XAML layer.
        var bitmapImage2 = await BgraToBitmapImageAsync(bgra2, width2, height2, ct);
        if (bitmapImage2 != null)
        {
            _thumbnailCache.Store(cacheKey, bitmapImage2);
        }
        return bitmapImage2;
    }

    public async Task<string> GetPlaybackFilePathAsync(ImageSource source, CancellationToken ct = default)
    {
        if (!source.IsInArchive)
        {
            // Loose file. Fast path: container is one Windows Media
            // Foundation (the engine behind WinUI MediaPlayer) recognizes
            // natively — just hand the path back. The user already paid
            // for the disk allocation; no temp extraction, no tracking.
            if (!NeedsRemuxToMp4(source.Path))
            {
                return source.Path;
            }

            // Slow path: container is one MF can't play (.mov, .mkv,
            // .avi, ...). Remux the loose file into a fresh MP4 temp
            // file via FFmpeg and return that path. The temp file is
            // tracked so ReleasePlaybackFilePath reclaims it when
            // playback ends.
            //
            // Why remux vs transcode: remux is container-copy only
            // (no codec work), so a 1 GB H.264/AAC .mov becomes a
            // ~1 GB H.264/AAC .mp4 in 1-2 s on a fast SSD. Quality is
            // identical because no re-encode happens.
            //
            // Why this is necessary at all: since the 2018 QuickTime
            // CVE cleanup Microsoft removed the .mov container demuxer
            // from Media Foundation; .mkv and .avi are also not in the
            // stock MF source-resolver list. Without remux, MediaPlayer
            // surfaces "Error: Unsupported video type or invalid file
            // path" the moment Play() is called on one of these files.
            // See RemuxToMp4 for the native implementation.
            var remuxedPath = Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}.mp4");
            try
            {
                await Task.Run(() => RemuxToMp4(source.Path, remuxedPath, ct), ct);
            }
            catch
            {
                // Remux aborted partway. The partially-written output (if
                // any) is junk — delete it so the temp dir doesn't
                // accumulate garbage over time. The source loose file is
                // untouched (we never wrote to it).
                TryDeleteTempFileUntracked(remuxedPath);
                throw;
            }
            if (!File.Exists(remuxedPath))
            {
                // Defensive: RemuxToMp4 should always close the output,
                // but if it returned without throwing and without
                // producing a file (shouldn't happen) we still don't
                // want to hand back a path MediaPlayer can't open.
                TryDeleteTempFileUntracked(remuxedPath);
                throw new FileNotFoundException(
                    $"RemuxToMp4 produced no output for {source.Path}", remuxedPath);
            }
            lock (_tempLock)
            {
                _playbackTempFiles.Add(remuxedPath);
            }
            return remuxedPath;
        }

        // Archive entry: extract to a fresh temp file. We pick the
        // extension from the entry key because FFmpeg's container
        // detector uses it as a hint, and on cleanup we want to be
        // able to tell at a glance which file is which. The file
        // lives until the caller invokes ReleasePlaybackFilePath (or
        // the service's Dispose on app shutdown).
        var extractPath = CreateTempFilePathForSource(source);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => ArchiveHelper.ExtractEntryToFile(source.Path, source.ArchiveEntry!, extractPath), ct);
        if (!File.Exists(extractPath))
        {
            // Extraction silently failed (corrupt entry, I/O
            // error, ...). Don't track a non-existent path.
            throw new FileNotFoundException($"Failed to extract archive entry to {extractPath}", extractPath);
        }

        if (!NeedsRemuxToMp4(extractPath))
        {
            // MP4 / M4V archive entry: hand the extracted temp file
            // to MF. Tracking happens here so ReleasePlaybackFilePath
            // finds the entry on cleanup.
            lock (_tempLock)
            {
                _playbackTempFiles.Add(extractPath);
            }
            return extractPath;
        }

        // .mov / .mkv / .avi archive entry: extract gave us a file
        // MF still can't play, so we remux to a fresh .mp4 temp file
        // the same way the loose-file branch does. We deliberately do
        // NOT track the extracted file — it's an intermediate artifact
        // that's deleted as soon as the remux finishes, so the
        // tracking list only ever contains paths the caller can
        // actually hand to ReleasePlaybackFilePath.
        var remuxedArchivePath = Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Run(() => RemuxToMp4(extractPath, remuxedArchivePath, ct), ct);
        }
        catch
        {
            // Both the partial .mp4 and the extracted source are
            // untracked — clean them up so they don't linger in the
            // temp dir between launches.
            TryDeleteTempFileUntracked(remuxedArchivePath);
            TryDeleteTempFileUntracked(extractPath);
            throw;
        }
        if (!File.Exists(remuxedArchivePath))
        {
            TryDeleteTempFileUntracked(remuxedArchivePath);
            TryDeleteTempFileUntracked(extractPath);
            throw new FileNotFoundException(
                $"RemuxToMp4 produced no output for archive entry {source.ArchiveEntry}", remuxedArchivePath);
        }
        TryDeleteTempFileUntracked(extractPath);
        lock (_tempLock)
        {
            _playbackTempFiles.Add(remuxedArchivePath);
        }
        return remuxedArchivePath;
    }

    public void ReleasePlaybackFilePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // Loose files come back to us as their real on-disk path;
        // those are not ours to delete. Only paths we explicitly
        // tracked from GetPlaybackFilePathAsync are removed. The
        // tracked-list membership check is what makes this safe to
        // call on a path the caller obtained from somewhere else.
        bool isTracked;
        lock (_tempLock)
        {
            isTracked = _playbackTempFiles.Remove(path);
        }
        if (!isTracked)
        {
            // Not one of our temp files. Either a loose-file path
            // (no-op) or a stale path from a previous process (the
            // ctor sweep should have caught it, but if it didn't,
            // we'd rather leave a stray file than delete a file the
            // user owns).
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Stale file locks, AV scanning, etc. The next
            // launch's ctor sweep will catch it.
            Trace.TraceWarning($"VideoMetadataService.ReleasePlaybackFilePath: failed to delete {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Build a unique temp file path for an archive source. The
    /// "ipv-video-" prefix lets the ctor sweep (and any operator
    /// looking at the temp dir) identify our files at a glance. The
    /// extension comes from the entry key so FFmpeg's container
    /// detector has a hint; for archive entries that have no
    /// extension (rare, but possible) we fall back to ".bin" which
    /// FFmpeg will still try to parse via the file's magic bytes.
    /// </summary>
    private string CreateTempFilePathForSource(ImageSource source)
    {
        var ext = source.IsInArchive
            ? Path.GetExtension(source.ArchiveEntry ?? string.Empty)
            : Path.GetExtension(source.Path);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        return Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}{ext}");
    }

    /// <summary>
    /// Delete a temp file we just used and no longer need — a
    /// "we own this, but we don't keep a long-term reference"
    /// variant of <see cref="ReleasePlaybackFilePath"/>. Used by
    /// the metadata + thumbnail paths which extract to temp, decode,
    /// and discard in a single call. Idempotent; a missing file is
    /// not an error.
    /// </summary>
    private static void TryDeleteTempFileUntracked(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"VideoMetadataService: failed to delete temp file {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // -----------------------------------------------------------------
    // Native helpers — all `unsafe`, all called only from Task.Run above.
    // -----------------------------------------------------------------

    /// <summary>
    /// Opens the video, reads container-level metadata (codec params for
    /// width / height, stream table for hasAudio, fmtCtx->duration for
    /// playback length), closes immediately. No frame decode, so this is
    /// roughly the cost of one disk read + format parse.
    /// </summary>
    private static unsafe VideoMetadata? GetMetadataFromFile(string path, CancellationToken ct)
    {
        AVFormatContext* fmtCtx = null;
        try
        {
            if (ffmpeg.avformat_open_input(&fmtCtx, path, null, null) < 0)
            {
                Trace.TraceWarning($"VideoMetadataService: avformat_open_input failed for {path}");
                return null;
            }
            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
            {
                Trace.TraceWarning($"VideoMetadataService: avformat_find_stream_info failed for {path}");
                return null;
            }
            if (ct.IsCancellationRequested) return null;

            int width = 0;
            int height = 0;
            var hasAudio = false;
            for (var i = 0; i < (int)fmtCtx->nb_streams; i++)
            {
                var codecParams = fmtCtx->streams[i]->codecpar;
                switch (codecParams->codec_type)
                {
                    case AVMediaType.AVMEDIA_TYPE_VIDEO:
                        // Last video stream wins (handles the rare
                        // multi-video-track file like a director's cut
                        // with a BTS track) — a finer pick would need
                        // a user choice and is out of scope.
                        width = codecParams->width;
                        height = codecParams->height;
                        break;
                    case AVMediaType.AVMEDIA_TYPE_AUDIO:
                        hasAudio = true;
                        break;
                }
            }

            // avformat duration is in AV_TIME_BASE (microseconds) units.
            // A negative / zero value means "unknown" (e.g. a live stream
            // still being recorded); surface as TimeSpan.Zero rather than
            // a negative timespan.
            var duration = fmtCtx->duration > 0
                ? TimeSpan.FromSeconds(fmtCtx->duration / (double)ffmpeg.AV_TIME_BASE)
                : TimeSpan.Zero;

            return new VideoMetadata(width, height, duration, hasAudio);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VideoMetadataService.GetMetadataFromFile error for {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (fmtCtx != null)
            {
                var local = fmtCtx;
                ffmpeg.avformat_close_input(&local);
            }
        }
    }

    /// <summary>
    /// Opens the video, finds the video stream, seeks to ~10% of duration
    /// (avoids black leader frames common at t=0 for many codecs), decodes
    /// one frame, and scales it via <c>sws_scale</c> to the requested
    /// <paramref name="maxSize"/> on the longer edge while preserving
    /// aspect ratio. Output is BGRA8, top-down, in a managed byte[] ready
    /// to be wrapped in a <see cref="WriteableBitmap"/> or
    /// <see cref="SoftwareBitmap"/>.
    /// </summary>
    private static unsafe (byte[]? bgra, int width, int height) ExtractAndScaleFrame(
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
            if (ffmpeg.avformat_open_input(&fmtCtx, path, null, null) < 0) return (null, 0, 0);
            if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0) return (null, 0, 0);
            if (ct.IsCancellationRequested) return (null, 0, 0);

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
            {
                Trace.TraceWarning($"VideoMetadataService: no video stream in {path}");
                return (null, 0, 0);
            }

            var stream = fmtCtx->streams[videoStreamIdx];
            var codecParams = stream->codecpar;
            var decoder = ffmpeg.avcodec_find_decoder(codecParams->codec_id);
            if (decoder == null) return (null, 0, 0);

            codecCtx = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(codecCtx, codecParams);
            if (ffmpeg.avcodec_open2(codecCtx, decoder, null) < 0) return (null, 0, 0);

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            var srcW = codecCtx->width;
            var srcH = codecCtx->height;
            if (srcW <= 0 || srcH <= 0) return (null, 0, 0);

            // Compute scaled output dimensions (preserve aspect ratio).
            // Cap at source dimensions so a maxSize=2000 on a 640x480 clip
            // doesn't try to upscale.
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
            if (outW <= 0 || outH <= 0) return (null, 0, 0);

            // Seek to ~10% — many codecs have black frames at the very start.
            // Skip the seek for files with no reported duration (e.g. a live
            // stream still being recorded); for those we just start from t=0
            // and take whatever the first decoded frame is.
            if (fmtCtx->duration > 0)
            {
                long seekTarget = (long)(0.1 * fmtCtx->duration);
                seekTarget = ffmpeg.av_rescale_q(
                    seekTarget,
                    new AVRational { num = 1, den = ffmpeg.AV_TIME_BASE },
                    stream->time_base);
                ffmpeg.av_seek_frame(fmtCtx, videoStreamIdx, seekTarget, ffmpeg.AVSEEK_FLAG_BACKWARD);
            }

            // Decode one frame. Bounded loop (32 packet reads max) so a
            // file that never produces a valid frame can't hang us here.
            int decodeRc;
            bool gotFrame = false;
            for (int i = 0; i < 32 && !gotFrame; i++)
            {
                if (ct.IsCancellationRequested) return (null, 0, 0);
                decodeRc = ffmpeg.av_read_frame(fmtCtx, packet);
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
            if (!gotFrame)
            {
                Trace.TraceWarning($"VideoMetadataService: no decodable frame in {path}");
                return (null, 0, 0);
            }

            // Scale to BGRA8 in one pass. Bilinear is plenty for a
            // gallery thumbnail; Bicuibic / Lanczos would burn 2-3x the
            // CPU for an imperceptible difference at 400 px.
            swsCtx = ffmpeg.sws_getContext(
                srcW, srcH, codecCtx->pix_fmt,
                outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_BILINEAR, null, null, null);
            if (swsCtx == null) return (null, 0, 0);

            var bgraLineSize = outW * 4;
            var bgraStride = bgraLineSize * outH;
            var bgraManaged = new byte[bgraStride];
            bgraBuffer = (byte*)ffmpeg.av_malloc((ulong)bgraStride);
            var dataPtr = new byte_ptrArray4 { [0] = bgraBuffer };
            var lineSizes = new int_array4 { [0] = bgraLineSize };
            ffmpeg.sws_scale(swsCtx, frame->data, frame->linesize, 0, srcH, dataPtr, lineSizes);

            // Copy out of the native buffer into managed memory so the
            // av_free below is safe to run before the caller consumes the
            // bytes. System.Buffer.MemoryCopy is a single intrinsic —
            // cheap even for a 1280x720 BGRA buffer (~3.5 MB). The
            // explicit System. qualifier is required because the file
            // also imports Windows.Storage.Streams (whose Buffer is a
            // WinRT class, not the BCL static helper).
            fixed (byte* bgraPtr = bgraManaged)
            {
                System.Buffer.MemoryCopy(bgraBuffer, bgraPtr, bgraManaged.Length, bgraManaged.Length);
            }
            ffmpeg.av_free(bgraBuffer);
            bgraBuffer = null;

            return (bgraManaged, outW, outH);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VideoMetadataService.ExtractAndScaleFrame error for {path}: {ex.GetType().Name}: {ex.Message}");
            return (null, 0, 0);
        }
        finally
        {
            // Free order: derived resources first, then the contexts that
            // own them. Each free is null-guarded because any earlier
            // failure path may have aborted before the allocation ran.
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

    /// <summary>
    /// True when the file's container is one Windows Media Foundation
    /// (the engine behind WinUI MediaPlayer) does NOT recognize on a
    /// clean Win10/11 install and therefore must be remuxed to MP4
    /// before playback. False when the container is already MP4-family
    /// (MF plays these natively, so we can hand the original path
    /// straight to MediaPlayer and skip the remux round-trip).
    ///
    /// <para>
    /// Background: since the 2018 QuickTime CVE cleanup Microsoft
    /// removed the .mov container demuxer from Media Foundation; .mkv
    /// and .avi are also not in the stock MF source-resolver list.
    /// The check is intentionally coarse (extension-only) — a finer
    /// "can MF decode this exact file" check would require sniffing
    /// the codec set, which FFmpeg already does at remux time, so
    /// we'd just be duplicating work.
    /// </para>
    /// </summary>
    private static bool NeedsRemuxToMp4(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        // Fast path: MP4 and M4V are containers MF recognizes. .m4v is
        // treated as MP4-with-maybe-AC-3 — MF decodes it identically
        // to .mp4 for any of the codecs FFmpeg can produce into the
        // MP4 container.
        if (ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase)) return false;
        if (ext.Equals(".m4v", StringComparison.OrdinalIgnoreCase)) return false;
        // Everything else (.mov, .mkv, .avi, .webm, .flv, ...) goes
        // through the FFmpeg remux path.
        return true;
    }

    /// <summary>
    /// Remux (container-copy, no transcode) an arbitrary media file
    /// into an MP4 file at <paramref name="destPath"/>. Used for files
    /// whose original container Windows Media Foundation can't decode
    /// — see <see cref="NeedsRemuxToMp4"/>.
    ///
    /// <para>
    /// This is a true remux: packets are copied byte-for-byte, only
    /// the container changes. A 1 GB H.264/AAC .mov becomes a ~1 GB
    /// H.264/AAC .mp4 in roughly 1-2 seconds on a fast SSD (mostly
    /// memory copies, no codec work). Codecs that MP4 can't carry
    /// (e.g. ProRes) fail at <c>avformat_write_header</c> and surface
    /// a meaningful error to the caller.
    /// </para>
    ///
    /// <para>
    /// Native work runs synchronously here — callers must wrap this
    /// in <c>Task.Run</c> (the two call sites in this service do).
    /// The cancellation token is checked between packet writes so
    /// the remux aborts within a packet or two if the user navigates
    /// away mid-remux.
    /// </para>
    /// </summary>
    private static unsafe void RemuxToMp4(string sourcePath, string destPath, CancellationToken ct)
    {
        AVFormatContext* inFmt = null;
        AVFormatContext* outFmt = null;
        AVPacket* packet = null;
        try
        {
            // Open input. avformat_open_input + find_stream_info is the
            // same pair GetMetadataFromFile uses, so a file FFmpeg can
            // parse for metadata is also one we can remux.
            var ret = ffmpeg.avformat_open_input(&inFmt, sourcePath, null, null);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: avformat_open_input failed for {sourcePath} (rc={ret})");
            }
            ret = ffmpeg.avformat_find_stream_info(inFmt, null);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: avformat_find_stream_info failed for {sourcePath} (rc={ret})");
            }
            ct.ThrowIfCancellationRequested();

            // Allocate the output (mp4) context. Passing a filename
            // tells avformat_alloc_output_context2 to derive the format
            // from the extension ("mp4") — we pass "mp4" explicitly
            // anyway so the format choice never depends on the temp
            // filename's extension.
            ret = ffmpeg.avformat_alloc_output_context2(&outFmt, null, "mp4", destPath);
            if (ret < 0 || outFmt == null)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: avformat_alloc_output_context2 failed for {destPath} (rc={ret})");
            }

            // Mirror the input's stream layout onto the output. AVCodecParameters
            // is the modern (FFmpeg 3.1+) way to carry codec info between
            // streams; we don't touch the deprecated `codec` field at all.
            for (uint i = 0; i < inFmt->nb_streams; i++)
            {
                var inStream = inFmt->streams[i];
                var outStream = ffmpeg.avformat_new_stream(outFmt, null);
                if (outStream == null)
                {
                    throw new InvalidOperationException("RemuxToMp4: avformat_new_stream returned null");
                }
                ret = ffmpeg.avcodec_parameters_copy(outStream->codecpar, inStream->codecpar);
                if (ret < 0)
                {
                    throw new InvalidOperationException(
                        $"RemuxToMp4: avcodec_parameters_copy failed for stream {i} (rc={ret})");
                }
                // Reset codec_tag so the MP4 muxer picks its own. Some
                // source files (especially .mov variants) carry a
                // QuickTime-style tag that MP4 doesn't recognize;
                // clearing it forces the muxer to rewrite a valid MP4
                // tag (avc1 / mp4a / ...).
                outStream->codecpar->codec_tag = 0;
            }

            // Open the output file. MP4 always needs avio_open — it's
            // a regular file format, not AVFMT_NOFILE. We hardcode the
            // unconditional open (no AVFMT_NOFILE guard) because we
            // fixed the output format to "mp4" above, so the guard
            // would always be false anyway.
            ret = ffmpeg.avio_open(&outFmt->pb, destPath, ffmpeg.AVIO_FLAG_WRITE);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: avio_open failed for {destPath} (rc={ret})");
            }

            // Write the MP4 header (ftyp/moov boxes). Failure here
            // usually means MP4 can't carry one of the source codecs
            // (e.g. ProRes, DNxHD) — the error message is the user-
            // facing signal that this specific file is unsupported.
            ret = ffmpeg.avformat_write_header(outFmt, null);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: avformat_write_header failed (rc={ret}, source codec likely not MP4-compatible)");
            }

            // Copy packets. av_interleaved_write_frame does timestamp
            // conversion (input timebase → output timebase) AND packet
            // interleaving (B-frame reordering for MP4) in one call,
            // so we just hand it each packet directly without manual
            // rescaling.
            packet = ffmpeg.av_packet_alloc();
            if (packet == null)
            {
                throw new InvalidOperationException("RemuxToMp4: av_packet_alloc returned null");
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                ret = ffmpeg.av_read_frame(inFmt, packet);
                if (ret < 0)
                {
                    // Negative return is end-of-stream on success or
                    // a real error mid-stream. Either way we stop
                    // reading; the trailer write below finalizes what
                    // we have.
                    break;
                }

                ret = ffmpeg.av_interleaved_write_frame(outFmt, packet);
                ffmpeg.av_packet_unref(packet);
                if (ret < 0)
                {
                    throw new InvalidOperationException(
                        $"RemuxToMp4: av_interleaved_write_frame failed (rc={ret})");
                }
            }

            // Finalize the MP4 (write moov trailing box, flush
            // indexes). Errors here are usually due to corrupted
            // last frames and the output is still partially valid,
            // but we surface them anyway so the caller can decide
            // whether to retry.
            ret = ffmpeg.av_write_trailer(outFmt);
            if (ret < 0)
            {
                throw new InvalidOperationException(
                    $"RemuxToMp4: av_write_trailer failed (rc={ret})");
            }
        }
        finally
        {
            // Free order: derived resources first (packet), then the
            // output context (which owns pb), then pb itself, then
            // the input. Each pointer is null-checked because any
            // earlier failure path may have aborted before the
            // allocation.
            //
            // NB: avformat_free_context does NOT close pb — we must
            // call avio_closep separately. Skipping it would leak the
            // file descriptor (and on Windows the lock on the output
            // file) until the process exits.
            if (packet != null)
            {
                var local = packet;
                ffmpeg.av_packet_free(&local);
            }
            if (outFmt != null)
            {
                if (outFmt->pb != null)
                {
                    ffmpeg.avio_closep(&outFmt->pb);
                }
                ffmpeg.avformat_free_context(outFmt);
            }
            if (inFmt != null)
            {
                var local = inFmt;
                ffmpeg.avformat_close_input(&local);
            }
        }
    }

    // -----------------------------------------------------------------
    // Managed-side conversion: BGRA8 byte[] → BitmapImage.
    // -----------------------------------------------------------------

    /// <summary>
    /// Wraps a top-down BGRA8 byte buffer in a <see cref="SoftwareBitmap"/>,
    /// encodes it as PNG into an in-memory stream, and feeds the stream
    /// into a fresh <see cref="BitmapImage"/>. The intermediate PNG is
    /// wasteful (a BMP encode would skip the deflate step) but PNG is
    /// the only encoder BitmapEncoder ships with built-in, and the size
    /// for a 400 px thumbnail is single-digit KB either way.
    /// </summary>
    private static async Task<BitmapImage?> BgraToBitmapImageAsync(
        byte[] bgra, int width, int height, CancellationToken ct)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        try
        {
            // AsBuffer() returns a Windows.Storage.Streams.IBuffer
            // wrapping the same backing memory — no copy here. The
            // SoftwareBitmap takes a strong reference; we keep bgra[]
            // alive until the SoftwareBitmap goes out of scope.
            using var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                bgra.AsBuffer(),
                BitmapPixelFormat.Bgra8,
                width,
                height,
                BitmapAlphaMode.Premultiplied);

            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();
            stream.Seek(0);

            var bitmapImage = new BitmapImage();
            await bitmapImage.SetSourceAsync(stream);
            return bitmapImage;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VideoMetadataService.BgraToBitmapImageAsync error: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
