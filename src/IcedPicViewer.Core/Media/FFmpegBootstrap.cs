// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Locates FFmpeg shared libraries and points <see cref="ffmpeg.RootPath"/>
/// at that folder once per process.
/// Search order: <c>IPV_FFMPEG_ROOT</c> → app <c>runtimes/{rid}/native</c> →
/// common system lib dirs (Homebrew / distro packages).
/// </summary>
public static class FFmpegBootstrap
{
    private static int _initialized;

    public static bool IsReady { get; private set; }

    /// <summary>Resolved library directory, or null if unavailable.</summary>
    public static string? ResolvedRoot { get; private set; }

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
            _ = ffmpeg.av_version_info();

            // Default FFmpeg log level is INFO and dumps to stderr. Complex
            // MKV (PGS subtitles, multi-audio, Dolby Vision) spam probesize /
            // "could not find codec parameters" warnings on every thumb open.
            // Keep ERROR+ only — real open/decode failures still surface.
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);

            IsReady = true;
            Trace.TraceInformation($"FFmpegBootstrap: ready from {dir} ({ffmpeg.av_version_info()})");
        }
        catch (Exception ex)
        {
            IsReady = false;
            ResolvedRoot = null;
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
        var rid = RuntimeInformation.RuntimeIdentifier;

        candidates.Add(Path.Combine(baseDir, "runtimes", rid, "native"));

        // Coarse RID fallbacks (e.g. win10-x64 → win-x64 layout).
        if (rid.Contains("win", StringComparison.OrdinalIgnoreCase) && rid.Contains("x64", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(baseDir, "runtimes", "win-x64", "native"));
        if (rid.Contains("win", StringComparison.OrdinalIgnoreCase) && rid.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(baseDir, "runtimes", "win-arm64", "native"));
        if (rid.Contains("linux", StringComparison.OrdinalIgnoreCase) && rid.Contains("x64", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(baseDir, "runtimes", "linux-x64", "native"));
        if (rid.Contains("linux", StringComparison.OrdinalIgnoreCase) && rid.Contains("arm64", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(baseDir, "runtimes", "linux-arm64", "native"));
        if (rid.Contains("osx", StringComparison.OrdinalIgnoreCase) || rid.Contains("macos", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add(Path.Combine(baseDir, "runtimes", "osx-arm64", "native"));
            candidates.Add(Path.Combine(baseDir, "runtimes", "osx-x64", "native"));
        }

        candidates.Add(Path.Combine(baseDir, "native"));
        candidates.Add(baseDir);

        // System / package-manager installs (shared libs next to each other).
        if (OperatingSystem.IsLinux())
        {
            candidates.Add("/usr/lib/x86_64-linux-gnu");
            candidates.Add("/usr/lib/aarch64-linux-gnu");
            candidates.Add("/usr/lib64");
            candidates.Add("/usr/lib");
            candidates.Add("/usr/local/lib");
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/opt/homebrew/lib");
            candidates.Add("/usr/local/lib");
            candidates.Add("/opt/local/lib");
        }

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
            // Windows: avutil-60.dll; Linux: libavutil.so*; macOS: libavutil*.dylib
            return Directory.EnumerateFiles(dir, "*avutil*", SearchOption.TopDirectoryOnly).Any();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"FFmpegBootstrap.ContainsAvutil probe failed for {dir}: {ex.Message}");
            return false;
        }
    }
}
