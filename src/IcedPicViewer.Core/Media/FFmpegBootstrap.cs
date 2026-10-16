// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using FFmpeg.AutoGen;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Locates the FFmpeg shared libraries shipped with the app and points
/// <see cref="ffmpeg.RootPath"/> at that folder once per process. The
/// product is Windows-only (x64): the search is limited to the packaged
/// layout — <c>IPV_FFMPEG_ROOT</c> → the app output root (= MSIX install
/// root) → <c>runtimes\win-x64\native</c> → <c>native</c>.
/// </summary>
public static class FFmpegBootstrap
{
    private static int _initialized;

    public static bool IsReady { get; private set; }

    /// <summary>Resolved library directory, or null if unavailable.</summary>
    public static string? ResolvedRoot { get; private set; }

    /// <summary>
    /// Version string reported by FFmpeg itself (<c>av_version_info()</c>, e.g.
    /// <c>n9.0.2-27-g7e93014c5b-20261014</c>), or null when the native libraries
    /// never loaded. Filled by the same call that decides <see cref="IsReady"/>,
    /// so null means "nothing was bound" rather than "not asked yet".
    /// </summary>
    public static string? Version { get; private set; }

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        try
        {
            var dir = ResolveNativeDirectory();
            if (dir is null)
            {
                Trace.TraceWarning(
                    "FFmpegBootstrap: native directory not found — video thumbs disabled. " +
                    "Set IPV_FFMPEG_ROOT, run tools/Fetch-FFmpegNatives, or install ffmpeg shared libs.");
                IsReady = false;
                return;
            }

            ffmpeg.RootPath = dir;
            ResolvedRoot = dir;
            var version = ffmpeg.av_version_info();

            // Default FFmpeg log level is INFO and dumps to stderr. Complex
            // MKV (PGS subtitles, multi-audio, Dolby Vision) spam probesize /
            // "could not find codec parameters" warnings on every thumb open.
            // Keep ERROR+ only — real open/decode failures still surface.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            Version = version;
            IsReady = true;
            Trace.TraceInformation($"FFmpegBootstrap: ready from {dir} ({version})");
        }
        catch (Exception ex)
        {
            IsReady = false;
            ResolvedRoot = null;
            Version = null;
            Trace.TraceError($"FFmpegBootstrap: init failed: {ex.Message}");
        }
    }

    private static string? ResolveNativeDirectory()
    {
        var candidates = new List<string?>();

        var env = Environment.GetEnvironmentVariable("IPV_FFMPEG_ROOT");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env.Trim());

        var baseDir = AppContext.BaseDirectory;

        // The packaged/loose layouts put the DLLs flat in the output root
        // (which is also the MSIX install root). The runtimes\win-x64\native
        // convention stays as a fallback for framework-style layouts.
        candidates.Add(baseDir);
        candidates.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native"));
        candidates.Add(Path.Combine(baseDir, "native"));

        foreach (var dir in candidates)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                continue;
            if (ContainsAvutil(dir))
                return dir;
        }

        return null;
    }

    private static bool ContainsAvutil(string dir)
    {
        try
        {
            // Windows shared library naming, e.g. avutil-61.dll.
            return Directory.EnumerateFiles(dir, "*avutil*", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"FFmpegBootstrap.ContainsAvutil probe failed for {dir}: {ex.Message}");
            return false;
        }
    }
}
