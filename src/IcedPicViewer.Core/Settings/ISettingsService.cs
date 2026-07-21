// Copyright (c) IcedPicViewer. All rights reserved.

namespace IcedPicViewer.Services.Interfaces;

/// <summary>
/// Persistent user-preferences store.
///
/// <para>
/// Read at construction time (load failures fall back to <see cref="AppSettings"/>
/// defaults — a corrupt settings file shouldn't break the app). Writes are
/// debounced through <see cref="ScheduleSave"/> so rapid changes (dragging
/// the volume slider) coalesce into a single disk write.
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
    /// right now. A missing or unreadable file is not an error — defaults
    /// stay in effect.
    /// </summary>
    void Load();

    /// <summary>
    /// Schedules a save to disk after a short debounce window. Multiple
    /// calls within the window coalesce into a single write. Safe to
    /// call from any thread; the actual disk I/O runs on a background
    /// task so the UI never blocks.
    /// </summary>
    void ScheduleSave();

    /// <summary>
    /// Cancels any pending debounce and writes <see cref="Current"/> to
    /// disk immediately. Use on app shutdown so the last window geometry
    /// is not lost.
    /// </summary>
    void SaveNow();
}
