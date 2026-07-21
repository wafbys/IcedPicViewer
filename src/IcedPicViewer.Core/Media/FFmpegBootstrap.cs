// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace IcedPicViewer.Core.Media;

/// <summary>
/// Locates FFmpeg shared libraries next to the app (or under
/// <c>runtimes/{rid}/native</c>) and points <see cref="ffmpeg.RootPath"/>
/// at that folder once per process.
/// </summary>
public static class FFmpegBootstrap
{
    private static int _initialized;

    public static bool IsReady { get; private set; }

    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
            return;

        try
        {
            var dir = ResolveNativeDirectory();
            if (dir is null)
            {
                Trace.TraceWarning("FFmpegBootstrap: native directory not found — video thumbs disabled");
                IsReady = false;
                return;
            }

            ffmpeg.RootPath = dir;
            // Touch a cheap API so load failures surface at bootstrap, not mid-scan.
            _ = ffmpeg.av_version_info();
            IsReady = true;
            Trace.TraceInformation($"FFmpegBootstrap: ready from {dir} ({ffmpeg.av_version_info()})");
        }
        catch (Exception ex)
        {
            IsReady = false;
            Trace.TraceError($"FFmpegBootstrap: init failed: {ex.Message}");
        }
    }

    private static string? ResolveNativeDirectory()
    {
        var baseDir = AppContext.BaseDirectory;
        var rid = RuntimeInformation.RuntimeIdentifier; // e.g. win-x64
        var candidates = new[]
        {
            Path.Combine(baseDir, "runtimes", rid, "native"),
            Path.Combine(baseDir, "runtimes", "win-x64", "native"),
            Path.Combine(baseDir, "native"),
            baseDir,
        };

        foreach (var dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            // avutil is the core dependency all others need.
            if (Directory.EnumerateFiles(dir, "avutil*").Any())
                return dir;
        }

        return null;
    }
}
