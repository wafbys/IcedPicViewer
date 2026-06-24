// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// JSON-backed implementation of <see cref="ISettingsService"/>. Persists
/// to <c>%LOCALAPPDATA%\IcedPicViewer\settings.json</c> with a debounced
/// write so dragging a slider doesn't generate dozens of disk writes.
///
/// <para><b>File format</b></para>
/// Pretty-printed UTF-8 JSON (no BOM). A small schema so the file is
/// human-editable: a user who wants to reset a single value can open
/// it in Notepad, change the number, save. A user who wants to
/// "factory reset" deletes the file — next launch loads defaults and
/// the next save overwrites the file from scratch.
///
/// <para><b>Thread safety</b></para>
/// <see cref="Current"/> is read by VMs on the UI thread and mutated
/// (property assignment on a POCO) from any thread. The JSON
/// serialiser works on a snapshot at the moment of the call — it
/// can never see a half-mutated object because POCO property
/// assignment is atomic on .NET. The actual disk write runs on a
/// Task.Run continuation so UI never blocks on file I/O.
///
/// <para><b>Load failure handling</b></para>
/// A missing or unreadable file is a no-op — defaults stay in effect.
/// A present-but-corrupt file is logged at Warning level and the
/// defaults stay in effect; the corrupt file is NOT deleted or
/// renamed, so the user can recover by hand. Next successful save
/// overwrites the file from scratch.
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    /// <summary>
    /// How long after the most recent <see cref="ScheduleSave"/> call to
    /// wait before actually writing. Long enough that a continuous slider
    /// drag (which fires dozens of setter calls per second) coalesces
    /// into one write; short enough that closing the app right after a
    /// change still captures the value (the flush-on-finalizer path
    /// below covers a longer-than-debounce window).
    /// </summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        // Tolerate missing fields — older settings.json files lacking a
        // newly-introduced property deserialize to the C# default for
        // that property, which matches our declared defaults. Without
        // this, adding a property would invalidate every existing file.
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private CancellationTokenSource? _pendingSave;
    private bool _disposed;

    public AppSettings Current { get; private set; } = new();

    public JsonSettingsService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "settings.json");

        Load();
    }

    public void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            // First launch (or after user deleted the file to reset).
            // Defaults stay in effect; first ScheduleSave will create
            // the file.
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                // Empty file — treat as missing. Don't overwrite here
                // (a user might have been mid-edit); next save will
                // populate it from current in-memory state.
                return;
            }
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            if (loaded != null)
            {
                // Defensive clamp on values that have a valid range. A
                // hand-edited settings.json with SlideshowInterval = 999
                // would otherwise leak through to the VM and surface as
                // an absurd slider value on next launch.
                if (loaded.SlideshowInterval < 1.0 || loaded.SlideshowInterval > 30.0)
                {
                    Trace.TraceWarning($"JsonSettingsService: persisted SlideshowInterval={loaded.SlideshowInterval} out of [1, 30] range, clamped");
                    loaded.SlideshowInterval = Math.Clamp(loaded.SlideshowInterval, 1.0, 30.0);
                }
                if (loaded.VideoVolume < 0.0 || loaded.VideoVolume > 1.0)
                {
                    Trace.TraceWarning($"JsonSettingsService: persisted VideoVolume={loaded.VideoVolume} out of [0, 1] range, clamped");
                    loaded.VideoVolume = Math.Clamp(loaded.VideoVolume, 0.0, 1.0);
                }
                Current = loaded;
            }
        }
        catch (Exception ex)
        {
            // Corrupt JSON, file locked by another process, etc. Don't
            // crash the app — fall back to defaults and let the user
            // fix the file by hand if they care. Next save will rewrite
            // it cleanly.
            Trace.TraceWarning($"JsonSettingsService: failed to load {_settingsPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void ScheduleSave()
    {
        if (_disposed) return;

        CancellationToken token;
        lock (_saveLock)
        {
            // Cancel + dispose any pending save. The new token
            // supersedes it; the old Task.Delay continuation will see
            // IsCanceled and bail without writing. This is the debounce:
            // each call resets the 1 s window, so a continuous slider
            // drag never lands a write until the user pauses for 1 s.
            // Disposing the old CTS immediately reclaims its kernel
            // WaitHandle — without this, every rapid change would leak
            // one CTS for the lifetime of the singleton (the WaitHandle
            // GC reclaims via finalizer, but that's slow + unbounded).
            var oldCts = _pendingSave;
            _pendingSave = new CancellationTokenSource();
            if (oldCts is not null)
            {
                try { oldCts.Cancel(); } catch { /* already disposed by something else */ }
                oldCts.Dispose();
            }
            token = _pendingSave.Token;
        }

        _ = Task.Delay(SaveDebounce, token).ContinueWith(
            t =>
            {
                if (t.IsCanceled) return;
                try
                {
                    var snapshot = Current;
                    var json = JsonSerializer.Serialize(snapshot, _jsonOptions);
                    File.WriteAllText(_settingsPath, json);
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"JsonSettingsService: failed to save {_settingsPath}: {ex.GetType().Name}: {ex.Message}");
                }
            },
            TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels and disposes any pending debounced save. App shutdown
    /// path calls this if it ever runs through a container-managed
    /// dispose; in practice the process exit between ScheduleSave and
    /// the debounce firing means the final write may or may not
    /// happen, which is fine — the next launch reads the last
    /// successful save from disk.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_saveLock)
        {
            if (_pendingSave is not null)
            {
                try { _pendingSave.Cancel(); } catch { }
                _pendingSave.Dispose();
                _pendingSave = null;
            }
        }
    }
}