// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Settings;

/// <summary>
/// Single source of truth for where the app keeps mutable state
/// (settings.json, window geometry, crash log, FFmpeg temp video).
///
/// All state lives under <c>%LOCALAPPDATA%\IcedPicViewer</c>. Resolved once
/// per process, so callers can treat <see cref="Root"/> as a constant.
/// </summary>
public static class AppDataPaths
{
    private const string AppFolderName = "IcedPicViewer";

    private static readonly string Resolved = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    /// <summary>Directory holding all mutable state. Created by <see cref="EnsureRoot"/>.</summary>
    public static string Root => Resolved;

    /// <summary><c>&lt;root&gt;\settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Scratch folder for videos extracted out of archives.</summary>
    public static string TempVideoDir => Path.Combine(Root, "TempVideo");

    /// <summary><c>&lt;root&gt;\crash.log</c>.</summary>
    public static string CrashLogFile => Path.Combine(Root, "crash.log");

    /// <summary>
    /// <c>&lt;root&gt;\ScanCache</c>. Persisted per-directory scan indexes
    /// (see <c>IScanCache</c> / <c>FileScanCache</c>) so re-opening a folder
    /// reuses the previous walk instead of enumerating every directory.
    /// </summary>
    public static string ScanCacheDir => Path.Combine(Root, "ScanCache");

    /// <summary>
    /// <c>&lt;root&gt;\ThumbCache</c>. Persisted encoded gallery thumbnails so
    /// re-opening a folder shows images without re-decoding the full source.
    /// </summary>
    public static string ThumbCacheDir => Path.Combine(Root, "ThumbCache");

    /// <summary>Creates <see cref="Root"/> if needed and returns it.</summary>
    public static string EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }
}
