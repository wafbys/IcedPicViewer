// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// User preferences that persist across application launches. Each property
/// has the same default as the ViewModel's own field initialiser, so a
/// user who has never opened the settings file gets exactly the same
/// behaviour they had before persistence existed.
///
/// <para>
/// The shape is intentionally flat — every persisted setting is one
/// line of code (a property + a default). New settings get added by
/// adding a new property here and wiring it into the relevant VM;
/// older settings.json files lacking the new property deserialize
/// fine because System.Text.Json populates missing fields with
/// the C# default (which matches our declared property default).
/// </para>
///
/// <para>
/// Backed by a plain JSON file under the platform app-data directory
/// (e.g. <c>%LOCALAPPDATA%\IcedPicViewer\settings.json</c> on Windows).
/// Inspectable by the user; delete the file to reset to defaults.
/// </para>
/// </summary>
public class AppSettings
{
    /// <summary>
    /// True when the slideshow wraps from the last image back to the first.
    /// False when the slideshow stops at the last image.
    /// </summary>
    public bool SlideshowLoop { get; set; }

    /// <summary>
    /// True when the slideshow picks a random next image each tick.
    /// False when it advances sequentially.
    /// </summary>
    public bool SlideshowShuffle { get; set; }

    /// <summary>
    /// Auto-advance interval in seconds. UI clamps to [1, 30]; load path
    /// also validates against hand-edited settings.json values.
    /// </summary>
    public double SlideshowInterval { get; set; } = 5.0;

    /// <summary>
    /// Playback volume on the [0.0, 1.0] scale.
    /// </summary>
    public double VideoVolume { get; set; } = 1.0;

    /// <summary>Last window left (device-independent pixels). NaN = use default.</summary>
    public double WindowX { get; set; } = double.NaN;

    /// <summary>Last window top.</summary>
    public double WindowY { get; set; } = double.NaN;

    /// <summary>Last window width.</summary>
    public double WindowWidth { get; set; } = 1100;

    /// <summary>Last window height.</summary>
    public double WindowHeight { get; set; } = 720;

    /// <summary>True when the window was maximized (not fullscreen).</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>Last folder successfully opened (empty = none).</summary>
    public string LastFolderPath { get; set; } = "";
}
