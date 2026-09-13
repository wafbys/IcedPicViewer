// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Core.Settings;

namespace IcedPicViewer.Core.Tests.Settings;

public sealed class AppDataPathsTests : IDisposable
{
    private readonly string _tempDir;

    public AppDataPathsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IcedPicViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort temp cleanup in tests.
        }
    }

    [Fact]
    public void Resolve_WithoutMarker_UsesLocalAppData()
    {
        var (root, isPortable) = AppDataPaths.Resolve(_tempDir, envOverride: null);

        Assert.False(isPortable);
        Assert.Equal("IcedPicViewer", Path.GetFileName(root));
        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            root,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_WithPortableMarker_UsesDataFolderNextToExe()
    {
        File.WriteAllText(Path.Combine(_tempDir, AppDataPaths.PortableMarkerFileName), string.Empty);

        var (root, isPortable) = AppDataPaths.Resolve(_tempDir, envOverride: null);

        Assert.True(isPortable);
        Assert.Equal(Path.Combine(_tempDir, "data"), root);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WithBlankEnvOverride_IgnoresIt(string? envOverride)
    {
        var (root, _) = AppDataPaths.Resolve(_tempDir, envOverride);

        Assert.DoesNotContain(Path.Combine(_tempDir, "data"), root);
    }

    [Fact]
    public void Resolve_EnvOverride_WinsOverMarker()
    {
        File.WriteAllText(Path.Combine(_tempDir, AppDataPaths.PortableMarkerFileName), string.Empty);
        var overrideDir = Path.Combine(_tempDir, "explicit");

        var (root, isPortable) = AppDataPaths.Resolve(_tempDir, overrideDir);

        Assert.Equal(overrideDir, root);
        Assert.False(isPortable);
    }

    [Fact]
    public void FileAndFolderHelpers_HangOffRoot()
    {
        var root = AppDataPaths.Root;

        Assert.Equal(Path.Combine(root, "settings.json"), AppDataPaths.SettingsFile);
        Assert.Equal(Path.Combine(root, "TempVideo"), AppDataPaths.TempVideoDir);
        Assert.Equal(Path.Combine(root, "crash.log"), AppDataPaths.CrashLogFile);
    }
}
