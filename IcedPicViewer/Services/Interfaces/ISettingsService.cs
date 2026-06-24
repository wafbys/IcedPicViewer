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
/// Why JSON and not the WinRT <c>ApplicationData.LocalSettings</c>:
/// the latter is WinRT-only and ties us to the framework's storage
/// model (a flat string-keyed dictionary, no strong typing, no
/// versioning). A plain JSON file in <c>%LOCALAPPDATA%\IcedPicViewer\</c>
/// is inspectable by the user with any text editor (matches the
/// "delete settings.json = reset to defaults" affordance the user
/// asked for), and can be migrated by editing if we ever change
/// the shape.
/// </para>
/// </summary>
public class AppSettings
{
    /// <summary>
    /// True when the slideshow wraps from the last image back to the first.
    /// False when the slideshow stops at the last image. Mirrors
    /// <c>ImageViewModel.IsSlideshowLooping</c>; written there on app start
    /// and any toggle writes back here.
    /// </summary>
    public bool SlideshowLoop { get; set; }

    /// <summary>
    /// True when the slideshow picks a random next image each tick.
    /// False when it advances sequentially. Mirrors
    /// <c>ImageViewModel.IsSlideshowShuffling</c>.
    /// </summary>
    public bool SlideshowShuffle { get; set; }

    /// <summary>
    /// Auto-advance interval in seconds. The Slider on the XAML side
    /// clamps to [1, 30], so persisted values outside that range are
    /// theoretical — we still validate on load to guard against hand-
    /// edited settings.json files.
    /// </summary>
    public double SlideshowInterval { get; set; } = 5.0;

    /// <summary>
    /// MediaPlayer volume on the [0.0, 1.0] scale. Mirrors
    /// <c>ImageViewModel.Volume</c>; pushed into <c>MediaPlayer.Volume</c>
    /// at construction time so the user's chosen level is in effect from
    /// the very first frame of the very first video.
    /// </summary>
    public double VideoVolume { get; set; } = 1.0;
}

/// <summary>
/// Persistent user-preferences store backed by a JSON file at
/// <c>%LOCALAPPDATA%\IcedPicViewer\settings.json</c>.
///
/// <para>
/// Read at construction time (load failures fall back to <see cref="AppSettings"/>
/// defaults — a corrupt settings file shouldn't break the app). Writes are
/// debounced through <see cref="ScheduleSave"/> so rapid changes (dragging
/// the volume slider) coalesce into a single disk write. The debounce
/// window is short (1 s) so a normal quit-then-relaunch cycle still sees
/// the latest value.
/// </para>
///
/// <para>
/// Singleton lifetime — registered in <c>App.xaml.cs</c> as
/// <c>AddSingleton&lt;ISettingsService, JsonSettingsService&gt;()</c>.
/// Holding the in-memory <see cref="Current"/> reference across the
/// app's lifetime means VMs and views read it directly without
/// round-tripping through disk on every property access.
/// </para>
/// </summary>
public interface ISettingsService
{
    /// <summary>
    /// The in-memory settings snapshot. Mutate properties directly
    /// (e.g. <c>settings.Current.SlideshowLoop = true</c>) and follow up
    /// with <see cref="ScheduleSave"/> to persist the change. Reading is
    /// unconditional and never throws.
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    /// Loads settings from disk synchronously. Called once at construction;
    /// subsequent calls reset <see cref="Current"/> to whatever's on disk
    /// right now (useful for tests, harmless in production). A missing or
    /// unreadable file is not an error — defaults stay in effect.
    /// </summary>
    void Load();

    /// <summary>
    /// Schedules a save to disk after a short debounce window. Multiple
    /// calls within the window coalesce into a single write. Safe to
    /// call from any thread; the actual disk I/O runs on a background
    /// task so the UI never blocks.
    /// </summary>
    void ScheduleSave();
}