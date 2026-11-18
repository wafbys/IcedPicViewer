// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;

namespace IcedPicViewer.Core.Settings;

/// <summary>
/// Single source of truth for where the app keeps mutable state
/// (settings.json, window geometry, crash log, scan/thumbnail caches,
/// FFmpeg temp video).
///
/// State lives next to the executable under <c>Data\</c>, so the whole
/// application folder is portable: copy it (or deploy it to
/// <c>%USERPROFILE%\App\IcedPicViewer</c>) and the settings travel with it.
/// Set <c>IPV_DATA_DIR</c> to override the location (tests, temporary runs).
/// Resolved once per process, so callers can treat <see cref="Root"/> as a
/// constant.
/// </summary>
public static class AppDataPaths
{
    private const string AppFolderName = "IcedPicViewer";
    private const string DataDirectoryEnvironmentVariable = "IPV_DATA_DIR";
    private const string MigrationMarkerFileName = ".migrated-from-localappdata";

    private static readonly string Resolved = ResolveRoot();

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

    /// <summary>
    /// One-time move of the two preference files from the pre-portable
    /// <c>%LOCALAPPDATA%\IcedPicViewer</c> location into <see cref="Root"/>.
    /// Only files that do not exist yet are copied; caches and logs are left
    /// behind because they are regenerable. The old directory is not deleted.
    /// Called once from <c>App</c> before any service reads settings, so tests
    /// that use explicit temp paths are unaffected.
    /// </summary>
    public static void MigrateLegacySettings()
    {
        var legacyRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);

        if (!Directory.Exists(legacyRoot))
        {
            return;
        }

        EnsureRoot();

        // The marker makes this a one-time operation: deleting settings.json
        // later must reset the app, not resurrect the legacy file on the next
        // launch.
        var markerPath = Path.Combine(Root, MigrationMarkerFileName);
        if (File.Exists(markerPath))
        {
            return;
        }

        foreach (var fileName in new[] { "settings.json", "window_settings.txt" })
        {
            var source = Path.Combine(legacyRoot, fileName);
            var target = Path.Combine(Root, fileName);

            if (!File.Exists(source) || File.Exists(target))
            {
                continue;
            }

            try
            {
                File.Copy(source, target);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning(
                    $"AppDataPaths.MigrateLegacySettings: could not copy {fileName} " +
                    $"from {legacyRoot} to {Root}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        try
        {
            File.WriteAllText(markerPath, $"migrated {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                $"AppDataPaths.MigrateLegacySettings: could not write marker {markerPath}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string ResolveRoot()
    {
        var overridden = Environment.GetEnvironmentVariable(DataDirectoryEnvironmentVariable);
        return string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.GetFullPath(overridden);
    }
}
