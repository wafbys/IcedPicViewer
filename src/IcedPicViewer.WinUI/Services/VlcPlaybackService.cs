// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using LibVLCSharp.Shared;

namespace IcedPicViewer.Services;

/// <summary>
/// Process-wide LibVLC + one <see cref="MediaPlayer"/> for WinUI fallback when
/// Windows Media Foundation cannot decode the codec (VP8/WebM, ProRes, …).
/// Mirrors Avalonia <c>VlcPlaybackService</c> (Windows natives via NuGet).
/// </summary>
public sealed class VlcPlaybackService : IDisposable
{
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private bool _disposed;
    private bool _initialized;

    public MediaPlayer? Player
    {
        get
        {
            EnsureInitialized();
            return _player;
        }
    }

    public bool IsPlaying => _player?.IsPlaying == true;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return _player is not null;
        }
    }

    public event EventHandler? PlayingChanged;

    public void EnsureInitialized()
    {
        if (_initialized || _disposed) return;
        lock (_gate)
        {
            if (_initialized || _disposed) return;
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                _libVlc = new LibVLC("--no-video-title-show", "--quiet");
                _player = new MediaPlayer(_libVlc);
                _player.Playing += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.Paused += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.Stopped += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.EndReached += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _initialized = true;
                Trace.TraceInformation("VlcPlaybackService (WinUI): LibVLC ready");
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcPlaybackService (WinUI) init failed: {ex.Message}");
            }
        }
    }

    public double Volume
    {
        get => (_player?.Volume ?? 100) / 100.0;
        set
        {
            EnsureInitialized();
            if (_player is null) return;
            _player.Volume = (int)Math.Clamp(Math.Round(value * 100), 0, 100);
        }
    }

    public bool LoadPath(string path)
    {
        EnsureInitialized();
        if (_libVlc is null || _player is null) return false;
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

        try
        {
            StopInternal();
            _media = new Media(_libVlc, path, FromType.FromPath);
            _player.Media = _media;
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService.LoadPath: {ex.Message}");
            return false;
        }
    }

    public void Play()
    {
        EnsureInitialized();
        _player?.Play();
    }

    public void Pause()
    {
        if (_player?.IsPlaying == true)
            _player.Pause();
    }

    public void TogglePlayPause()
    {
        EnsureInitialized();
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    public void Stop() => StopInternal();

    public void SeekFraction(double fraction)
    {
        if (_player is null || !_player.IsSeekable) return;
        fraction = Math.Clamp(fraction, 0, 1);
        var len = _player.Length;
        if (len <= 0) return;
        _player.Time = (long)(len * fraction);
    }

    private void StopInternal()
    {
        try
        {
            if (_player is not null)
            {
                if (_player.IsPlaying) _player.Stop();
                _player.Media = null;
            }
            _media?.Dispose();
            _media = null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService.Stop: {ex.Message}");
        }

        PlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal();
        try
        {
            _player?.Dispose();
            _libVlc?.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService dispose: {ex.Message}");
        }
        _player = null;
        _libVlc = null;
    }
}
