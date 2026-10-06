// Copyright (c) IcedPicViewer. All rights reserved.

using System.Text.Json;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Core.Tests.Settings;

public sealed class AppSettingsTests
{
    [Fact]
    public void Defaults_ShouldHaveSensibleValues()
    {
        var defaults = new AppSettings();

        Assert.False(defaults.SlideshowLoop);
        Assert.False(defaults.SlideshowShuffle);
        Assert.Equal(5.0, defaults.SlideshowInterval);
        Assert.Equal(1.0, defaults.VideoVolume);
        Assert.Equal(double.NaN, defaults.WindowX);
        Assert.Equal(double.NaN, defaults.WindowY);
        Assert.Equal(1100, defaults.WindowWidth);
        Assert.Equal(720, defaults.WindowHeight);
        Assert.False(defaults.WindowMaximized);
        Assert.Equal("", defaults.LastFolderPath);
        Assert.Equal(0, defaults.GalleryFilter);
        Assert.Equal(0, defaults.GallerySortKey);
        Assert.False(defaults.GallerySortDescending);
        Assert.True(defaults.ViewerFitMode);
    }

    [Fact]
    public void Serialize_ShouldProduceValidJson()
    {
        var settings = new AppSettings
        {
            SlideshowLoop = true,
            SlideshowInterval = 10.0,
            VideoVolume = 0.75,
            WindowWidth = 1280,
            WindowHeight = 800,
            WindowX = 100,
            WindowY = 200,
            GalleryFilter = 2,
            GallerySortKey = 3,
            GallerySortDescending = true,
            ViewerFitMode = false,
        };

        var json = JsonSerializer.Serialize(settings);
        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(deserialized.SlideshowLoop);
        Assert.Equal(10.0, deserialized.SlideshowInterval);
        Assert.Equal(0.75, deserialized.VideoVolume);
        Assert.Equal(1280, deserialized.WindowWidth);
        Assert.Equal(2, deserialized.GalleryFilter);
        Assert.Equal(3, deserialized.GallerySortKey);
        Assert.True(deserialized.GallerySortDescending);
        Assert.False(deserialized.ViewerFitMode);
    }

    [Fact]
    public void Deserialize_MissingProperties_ShouldUseDefaults()
    {
        var json = """{"SlideshowLoop":true}""";

        var deserialized = JsonSerializer.Deserialize<AppSettings>(json)!;

        Assert.True(deserialized.SlideshowLoop);
        Assert.Equal(5.0, deserialized.SlideshowInterval); // default
        Assert.Equal(1.0, deserialized.VideoVolume); // default
        Assert.Equal(double.NaN, deserialized.WindowX); // default
        Assert.Equal(0, deserialized.GalleryFilter); // default
        Assert.True(deserialized.ViewerFitMode); // default (old settings.json lacks it)
    }

}
