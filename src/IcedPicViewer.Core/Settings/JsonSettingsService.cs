// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Services.Implementations;

/// <summary>
/// JSON-backed implementation of <see cref="ISettingsService"/>. Persists
/// to <c>{LocalApplicationData}/IcedPicViewer/settings.json</c> with a
/// debounced write so dragging a slider doesn't generate dozens of disk writes.
///
/// <para><b>File format</b></para>
/// Pretty-printed UTF-8 JSON (no BOM). Human-editable; delete the file
/// to factory-reset (next launch loads defaults).
///
/// <para><b>Thread safety</b></para>
/// <see cref="Current"/> is read by VMs on the UI thread and mutated
/// (property assignment on a POCO) from any thread. The JSON
/// serialiser works on a snapshot at the moment of the call. The actual
/// disk write runs on a Task continuation so UI never blocks on file I/O.
///
/// <para><b>Load failure handling</b></para>
/// A missing or unreadable file is a no-op — defaults stay in effect.
/// A present-but-corrupt file is logged at Warning level; the corrupt
/// file is NOT deleted so the user can recover by hand.
/// </summary>
public sealed class JsonSettingsService : ISettingsService, IDisposable
{
    /// <summary>
    /// How long after the most recent <see cref="ScheduleSave"/> call to
    /// wait before actually writing.
    /// </summary>
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // AppSettings defaults WindowX/Y to NaN ("use platform default"); without this,
        // SaveNow throws and the catch in WriteToDisk silently drops the entire save.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _settingsPath;
    private readonly object _saveLock = new();
    private CancellationTokenSource? _pendingSave;
    private bool _disposed;

    public AppSettings Current { get; private set; } = new();

    /// <summary>Production path: <c>%LocalAppData%/IcedPicViewer/settings.json</c>.</summary>
    public JsonSettingsService()
        : this(GetDefaultSettingsPath())
    {
    }

    /// <summary>
    /// Allows tests (and alternate hosts) to point at an isolated settings file
    /// instead of the real user profile path.
    /// </summary>
    public JsonSettingsService(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        var dir = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _settingsPath = settingsPath;
        Load();
    }

    private static string GetDefaultSettingsPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "settings.json");
    }

    public void Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded != null)
            {
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
            Trace.TraceWarning($"JsonSettingsService: failed to load {_settingsPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void ScheduleSave()
    {
        if (_disposed) return;

        CancellationToken token;
        lock (_saveLock)
        {
            var oldCts = _pendingSave;
            _pendingSave = new CancellationTokenSource();
            if (oldCts is not null)
            {
                try { oldCts.Cancel(); } catch (Exception ex) { Trace.TraceError($"JsonSettingsService: cancel old pending save failed: {ex.Message}"); }
                oldCts.Dispose();
            }
            token = _pendingSave.Token;
        }

        _ = Task.Delay(SaveDebounce, token).ContinueWith(
            t =>
            {
                if (t.IsCanceled) return;
                WriteToDisk();
            },
            TaskScheduler.Default);
    }

    public void SaveNow()
    {
        if (_disposed) return;
        lock (_saveLock)
        {
            if (_pendingSave is not null)
            {
                try { _pendingSave.Cancel(); } catch (Exception ex) { Trace.TraceError($"JsonSettingsService: cancel pending save failed: {ex.Message}"); }
                _pendingSave.Dispose();
                _pendingSave = null;
            }
        }
        WriteToDisk();
    }

    private void WriteToDisk()
    {
        try
        {
            var snapshot = Current;
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"JsonSettingsService: failed to save {_settingsPath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        // Flush so window geometry / last prefs are not lost on exit.
        try { SaveNow(); } catch (Exception ex) { Trace.TraceError($"JsonSettingsService: dispose SaveNow failed: {ex.Message}"); }
        _disposed = true;
        lock (_saveLock)
        {
            if (_pendingSave is not null)
            {
                try { _pendingSave.Cancel(); } catch (Exception ex) { Trace.TraceError($"JsonSettingsService: dispose Cancel failed: {ex.Message}"); }
                _pendingSave.Dispose();
                _pendingSave = null;
            }
        }
    }
}
