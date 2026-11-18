// Copyright (c) IcedPicViewer. All rights reserved.

using System.Reflection;

namespace IcedPicViewer;

/// <summary>
/// Build-time identity shown in the window title and the About page.
/// Everything comes from the assembly itself, so the displayed value can
/// never disagree with the running binary:
/// <list type="bullet">
///   <item><c>Version</c> — <see cref="AssemblyName.Version"/>, three parts.</item>
///   <item><c>CommitShort</c> — the <c>+&lt;hash&gt;</c> suffix the .NET SDK
///   appends to <see cref="AssemblyInformationalVersionAttribute"/>.</item>
///   <item><c>BuildDateTime</c> / <c>WindowsAppSdkVersion</c> —
///   <see cref="AssemblyMetadataAttribute"/> values emitted from the csproj.</item>
/// </list>
/// SavorReader carries the same class with the same output format.
/// </summary>
internal static class BuildInfo
{
    private const string Unknown = "unknown";
    private const string NoGit = "nogit";

    public static string ApplicationName { get; } =
        Assembly.GetExecutingAssembly().GetName().Name ?? "IcedPicViewer";

    public static string Version { get; } = ResolveVersion();

    public static string Configuration { get; } =
#if DEBUG
        "Debug";
#else
        "Release";
#endif

    public static string CommitShort { get; } = ResolveCommitShort();

    public static string BuildDateTime { get; } = ResolveMetadata("BuildDateTime");

    public static string WindowsAppSdkVersion { get; } = ResolveMetadata("WindowsAppSdkVersion");

    /// <summary>
    /// Shared title/About label, e.g.
    /// <c>v0.24.2 · Debug · 77ef97b · 2026-11-18 09:30</c>.
    /// </summary>
    public static string DisplayVersion =>
        $"v{Version} · {Configuration} · {CommitShort} · {BuildDateTime}";

    /// <summary>Full window title: <c>&lt;app name&gt; &lt;DisplayVersion&gt;</c>.</summary>
    public static string WindowTitle => $"{ApplicationName} {DisplayVersion}";

    private static string ResolveVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? Unknown : version.ToString(3);
    }

    private static string ResolveCommitShort()
    {
        var informationalVersion = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return NoGit;
        }

        var separator = informationalVersion.LastIndexOf('+');
        if (separator < 0 || separator == informationalVersion.Length - 1)
        {
            return NoGit;
        }

        var revision = informationalVersion[(separator + 1)..];
        var hash = new string(revision.TakeWhile(char.IsAsciiHexDigit).ToArray());
        return hash.Length >= 7 ? hash[..7] : NoGit;
    }

    private static string ResolveMetadata(string key)
    {
        var value = Assembly
            .GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == key)
            ?.Value;

        return string.IsNullOrWhiteSpace(value) ? Unknown : value;
    }
}
