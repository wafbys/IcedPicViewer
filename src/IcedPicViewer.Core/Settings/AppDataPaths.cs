// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Core.Settings;

/// <summary>
/// Single source of truth for where the app keeps mutable state
/// (settings.json, window geometry, crash log, FFmpeg temp video).
///
/// Resolution order:
/// <list type="number">
/// <item><c>IPV_DATA_ROOT</c> env var — explicit override (tests / CI).</item>
/// <item>A <see cref="PortableMarkerFileName"/> file next to the executable —
/// portable ("green") build: everything goes to <c>&lt;exe&gt;\data</c>, so the
/// folder can be copied anywhere and leaves nothing behind in the user
/// profile. Dropped by <c>tools/Build-Portable.ps1</c>.</item>
/// <item>Otherwise <c>%LOCALAPPDATA%\IcedPicViewer</c> (packaged / dev builds).</item>
/// </list>
///
/// Resolved once per process: the marker and the env var are read at first
/// touch, so callers can treat <see cref="Root"/> as a constant.
/// </summary>
public static class AppDataPaths
{
    /// <summary>Marker file dropped next to the exe by tools/Build-Portable.ps1.</summary>
    public const string PortableMarkerFileName = "portable.marker";

    private const string AppFolderName = "IcedPicViewer";
    private const string DataRootEnvVar = "IPV_DATA_ROOT";
    private const string PortableDataFolderName = "data";

    private static readonly (string Root, bool IsPortable) Resolved =
        Resolve(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(DataRootEnvVar));

    /// <summary>Directory holding all mutable state. Created by <see cref="EnsureRoot"/>.</summary>
    public static string Root => Resolved.Root;

    /// <summary>True when <see cref="Root"/> lives next to the executable.</summary>
    public static bool IsPortable => Resolved.IsPortable;

    /// <summary><c>&lt;root&gt;\settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>Scratch folder for videos extracted out of archives.</summary>
    public static string TempVideoDir => Path.Combine(Root, "TempVideo");

    /// <summary><c>&lt;root&gt;\crash.log</c>.</summary>
    public static string CrashLogFile => Path.Combine(Root, "crash.log");

    /// <summary>Creates <see cref="Root"/> if needed and returns it.</summary>
    public static string EnsureRoot()
    {
        Directory.CreateDirectory(Root);
        return Root;
    }

    /// <summary>
    /// Pure resolution shared by <see cref="Root"/> and the tests. Inputs are
    /// explicit so every branch is testable without mutating process state
    /// (the static root is resolved exactly once).
    /// </summary>
    public static (string Root, bool IsPortable) Resolve(string appBaseDir, string? envOverride)
    {
        if (!string.IsNullOrWhiteSpace(envOverride))
            return (envOverride.Trim(), false);

        if (!string.IsNullOrEmpty(appBaseDir) &&
            File.Exists(Path.Combine(appBaseDir, PortableMarkerFileName)))
        {
            return (Path.Combine(appBaseDir, PortableDataFolderName), true);
        }

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppFolderName);
        return (local, false);
    }
}
