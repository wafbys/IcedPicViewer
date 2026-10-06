// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Core.Tests.Settings;

public sealed class JsonSettingsServiceIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _settingsPath;

    public JsonSettingsServiceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "IcedPicViewer.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _settingsPath = Path.Combine(_tempDir, "settings.json");
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
    public void SaveAndReload_ShouldPersistValues()
    {
        using var service = new JsonSettingsService(_settingsPath);
        service.Current.SlideshowLoop = true;
        service.Current.SlideshowInterval = 12.5;
        service.SaveNow();

        using var fresh = new JsonSettingsService(_settingsPath);
        Assert.True(fresh.Current.SlideshowLoop);
        Assert.Equal(12.5, fresh.Current.SlideshowInterval);
    }

    [Fact]
    public void Clamping_OutOfRangeValues_ShouldBeCorrected()
    {
        // Write out-of-range values as JSON so Load()'s clamp path is exercised.
        File.WriteAllText(_settingsPath, """
            {
              "SlideshowInterval": -5.0,
              "VideoVolume": 10.0
            }
            """);

        using var service = new JsonSettingsService(_settingsPath);
        Assert.Equal(1.0, service.Current.SlideshowInterval);
        Assert.Equal(1.0, service.Current.VideoVolume);
    }

    [Fact]
    public void MissingFile_ShouldKeepDefaults()
    {
        using var service = new JsonSettingsService(_settingsPath);
        Assert.False(service.Current.SlideshowLoop);
        Assert.Equal(5.0, service.Current.SlideshowInterval);
        Assert.Equal(1.0, service.Current.VideoVolume);
    }
}
