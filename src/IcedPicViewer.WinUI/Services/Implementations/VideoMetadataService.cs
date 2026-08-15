// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;

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
///   <item><see cref="ExtractVideoThumbnailAsync"/> — one FFmpeg open
///         via <c>VideoFrameExtractor</c>, returns a SoftwareBitmap thumb
///         plus source size and duration.</item>
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
/// conversion to SoftwareBitmapSource runs on the UI thread.
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

    // Shared with IMediaLoader so a video thumb and an image thumb with
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

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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

    public async Task<VideoMetadata?> GetVideoMetadataAsync(MediaRef media, CancellationToken ct = default)
    {
        if (media.IsInArchive)
        {
            // Extract to a temp file, then run the metadata read on
            // that. The temp file is untracked — we delete it ourselves
            // after the read, regardless of outcome (success or
            // failure). Playback uses a different, longer-lived path.
            var tempPath = CreateTempFilePathForSource(media);
            try
            {
                await Task.Run(() => ArchiveHelper.ExtractEntryToFile(media.Path, media.ArchiveEntry!, tempPath), ct);
                if (ct.IsCancellationRequested) return null;
                if (!File.Exists(tempPath)) return null;
                return await Task.Run(() => GetMetadataFromFile(tempPath, ct), ct);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VideoMetadataService.GetVideoMetadataAsync archive error for {media}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                TryDeleteTempFileUntracked(tempPath);
            }
        }
        if (!File.Exists(media.Path))
        {
            return null;
        }
        return await Task.Run(() => GetMetadataFromFile(media.Path, ct), ct);
    }

    public async Task<CachedThumb?> ExtractVideoThumbnailAsync(MediaRef media, int maxSize, CancellationToken ct = default)
    {
        if (maxSize < 1) return null;

        var cacheKey = $"{media}|{maxSize}|{media.Kind}";
        if (_thumbnailCache.TryGet(cacheKey, out var cached) && cached is not null)
            return cached;

        try
        {
            var frame = await VideoFrameExtractor.ExtractAsync(media, maxSize, ct).ConfigureAwait(false);
            if (frame is null) return null;
            var f = frame.Value;
            var sb = MediaLoader.CreateSoftwareBitmap(f.Bgra, f.FrameWidth, f.FrameHeight);
            if (sb is null) return null;

            var ow = f.SourceWidth > 0 ? f.SourceWidth : 0;
            var oh = f.SourceHeight > 0 ? f.SourceHeight : 0;
            var thumb = new CachedThumb(sb, ow, oh, f.Duration);
            _thumbnailCache.Store(cacheKey, thumb);
            return thumb;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VideoMetadataService.ExtractVideoThumbnailAsync {media}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public async Task<string> GetPlaybackFilePathAsync(
        MediaRef media,
        bool remuxIfNeeded = true,
        CancellationToken ct = default)
    {
        if (!media.IsInArchive)
        {
            // Loose file. Fast path: already MP4-family, or caller asked
            // for original container (LibVLC can play WebM/VP8 natively).
            if (!remuxIfNeeded || !NeedsRemuxToMp4(media.Path))
            {
                return media.Path;
            }

            // Slow path: container is one MF can't play (.mov, .mkv,
            // .avi, .webm...). Remux into MP4 via FFmpeg (stream copy —
            // fails when the codec cannot live in MP4, e.g. VP8/VP9/ProRes).
            var remuxedPath = Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}.mp4");
            try
            {
                await Task.Run(() => RemuxToMp4(media.Path, remuxedPath, ct), ct);
            }
            catch
            {
                TryDeleteTempFileUntracked(remuxedPath);
                throw;
            }
            if (!File.Exists(remuxedPath))
            {
                TryDeleteTempFileUntracked(remuxedPath);
                throw new FileNotFoundException(
                    $"RemuxToMp4 produced no output for {media.Path}", remuxedPath);
            }
            lock (_tempLock)
            {
                _playbackTempFiles.Add(remuxedPath);
            }
            return remuxedPath;
        }

        // Archive entry: always extract first.
        var extractPath = CreateTempFilePathForSource(media);
        ct.ThrowIfCancellationRequested();
        await Task.Run(() => ArchiveHelper.ExtractEntryToFile(media.Path, media.ArchiveEntry!, extractPath), ct);
        if (!File.Exists(extractPath))
        {
            throw new FileNotFoundException($"Failed to extract archive entry to {extractPath}", extractPath);
        }

        if (!remuxIfNeeded || !NeedsRemuxToMp4(extractPath))
        {
            lock (_tempLock)
            {
                _playbackTempFiles.Add(extractPath);
            }
            return extractPath;
        }

        var remuxedArchivePath = Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}.mp4");
        try
        {
            await Task.Run(() => RemuxToMp4(extractPath, remuxedArchivePath, ct), ct);
        }
        catch
        {
            TryDeleteTempFileUntracked(remuxedArchivePath);
            TryDeleteTempFileUntracked(extractPath);
            throw;
        }
        if (!File.Exists(remuxedArchivePath))
        {
            TryDeleteTempFileUntracked(remuxedArchivePath);
            TryDeleteTempFileUntracked(extractPath);
            throw new FileNotFoundException(
                $"RemuxToMp4 produced no output for archive entry {media.ArchiveEntry}", remuxedArchivePath);
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
    /// Delete a temp file we just used and no longer need — a
    /// "we own this, but we don't keep a long-term reference"
    /// variant of <see cref="ReleasePlaybackFilePath"/>. Used by
    /// the metadata + thumbnail + remux paths which extract or
    /// remux to temp and discard in a single call. Idempotent; a
    /// missing file is not an error.
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
    /// <summary>
    /// Build a unique temp file path for an archive media. The
    /// "ipv-video-" prefix lets the ctor sweep (and any operator
    /// looking at the temp dir) identify our files at a glance. The
    /// extension comes from the entry key so FFmpeg's container
    /// detector has a hint; for archive entries that have no
    /// extension (rare, but possible) we fall back to ".bin" which
    /// FFmpeg will still try to parse via the file's magic bytes.
    /// </summary>
    private string CreateTempFilePathForSource(MediaRef media)
    {
        var ext = media.IsInArchive
            ? Path.GetExtension(media.ArchiveEntry ?? string.Empty)
            : Path.GetExtension(media.Path);
        if (string.IsNullOrEmpty(ext)) ext = ".bin";
        return Path.Combine(_tempDir, $"ipv-video-{Guid.NewGuid():N}{ext}");
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
    /// and .avi are also not in the stock MF media-resolver list.
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
                // media files (especially .mov variants) carry a
                // QuickTime-style tag that MP4 doesn't recognize;
                // clearing it forces the muxer to rewrite a valid MP4
                // tag (avc1 / mp4a / ...).
                outStream->codecpar->codec_tag = 0;

                // Copy the input stream timebase to the output so
                // av_interleaved_write_frame's timestamp conversion
                // preserves the original A/V sync. Without this the
                // MP4 muxer uses its default timescale (e.g. 1/90000)
                // which can drift from the media's timing, especially
                // for VFR content or files with non-standard timebases.
                outStream->time_base = inStream->time_base;
            }

            // Tell the MP4 muxer to avoid negative timestamps by
            // shifting all streams to start at 0, instead of creating
            // edit lists (elst). Media Foundation historically has poor
            // edit-list support, which causes A/V desync when the
            // media has non-zero start PTS (common with screen
            // recordings, webcam captures, and some .mov files).
            outFmt->avoid_negative_ts = ffmpeg.AVFMT_AVOID_NEG_TS_MAKE_ZERO;

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
            string videoCodec = string.Empty;
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
                        // FFmpeg.AutoGen 8.x: avcodec_get_name returns a
                        // managed string ("h264", "hevc", "prores",
                        // "vp9", "av1", ...). Empty when the codec id
                        // is unknown to the linked FFmpeg build.
                        videoCodec = ffmpeg.avcodec_get_name(codecParams->codec_id) ?? string.Empty;
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

            return new VideoMetadata(width, height, duration, hasAudio, videoCodec);
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
}
