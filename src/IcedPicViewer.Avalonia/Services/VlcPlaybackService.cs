// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using System.Runtime.InteropServices;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using LibVLCSharp.Shared;

namespace IcedPicViewer.Avalonia.Services;

/// <summary>
/// Owns process-wide LibVLC + a single <see cref="MediaPlayer"/> for the
/// viewer. Resolves archive entries to temp files for playback.
/// <para>
/// Natives: Windows/macOS via NuGet (<c>VideoLAN.LibVLC.*</c>); Linux via
/// distro packages (<c>libvlc</c>) or <c>IPV_LIBVLC_ROOT</c>.
/// </para>
/// </summary>
public sealed class VlcPlaybackService : IDisposable
{
    private readonly object _gate = new();
    private LibVLC? _libVlc;
    private MediaPlayer? _player;
    private Media? _media;
    private string? _tempPlaybackPath;
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
                InitializeLibVlcCore();
                _libVlc = new LibVLC(
                    "--no-video-title-show",
                    "--quiet");
                _player = new MediaPlayer(_libVlc);
                _player.Playing += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.Paused += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.Stopped += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _player.EndReached += (_, _) => PlayingChanged?.Invoke(this, EventArgs.Empty);
                _initialized = true;
                Trace.TraceInformation("VlcPlaybackService: LibVLC ready");
            }
            catch (Exception ex)
            {
                Trace.TraceError($"VlcPlaybackService init failed: {ex.Message}");
                // Leave _initialized false so a later retry can succeed if libs appear.
            }
        }
    }

    /// <summary>
    /// Windows/macOS: NuGet packs natives and <see cref="Core.Initialize()"/> finds them.
    /// Linux: prefer <c>IPV_LIBVLC_ROOT</c>, then common multiarch paths.
    /// </summary>
    private static void InitializeLibVlcCore()
    {
        var env = Environment.GetEnvironmentVariable("IPV_LIBVLC_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
        {
            LibVLCSharp.Shared.Core.Initialize(env.Trim());
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (var dir in LinuxLibVlcCandidates())
            {
                if (LooksLikeLibVlcDir(dir))
                {
                    LibVLCSharp.Shared.Core.Initialize(dir);
                    Trace.TraceInformation($"VlcPlaybackService: using system LibVLC at {dir}");
                    return;
                }
            }
        }

        // NuGet VideoLAN.LibVLC.Windows / .Mac, or default probe paths.
        LibVLCSharp.Shared.Core.Initialize();
    }

    private static IEnumerable<string> LinuxLibVlcCandidates()
    {
        yield return "/usr/lib/x86_64-linux-gnu";
        yield return "/usr/lib/aarch64-linux-gnu";
        yield return "/usr/lib64";
        yield return "/usr/lib";
        yield return "/usr/local/lib";
        // Flatpak / custom
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            yield return Path.Combine(home, ".local", "lib");
    }

    private static bool LooksLikeLibVlcDir(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try
        {
            return File.Exists(Path.Combine(dir, "libvlc.so"))
                || File.Exists(Path.Combine(dir, "libvlc.so.5"))
                || Directory.EnumerateFiles(dir, "libvlc.so*").Any();
        }
        catch
        {
            return false;
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

    public async Task<bool> LoadAsync(ImageSource source, CancellationToken ct = default)
    {
        EnsureInitialized();
        if (_libVlc is null || _player is null) return false;

        StopInternal(keepPlayer: true);
        CleanupTemp();

        string path;
        try
        {
            path = await ResolvePathAsync(source, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService.Load path: {ex.Message}");
            return false;
        }

        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        try
        {
            _media = new Media(_libVlc, path, FromType.FromPath);
            _player.Media = _media;
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService.Load media: {ex.Message}");
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

    public void Stop()
    {
        StopInternal(keepPlayer: true);
        CleanupTemp();
    }

    public void SeekFraction(double fraction)
    {
        if (_player is null || !_player.IsSeekable) return;
        fraction = Math.Clamp(fraction, 0, 1);
        var len = _player.Length;
        if (len <= 0) return;
        _player.Time = (long)(len * fraction);
    }

    private async Task<string> ResolvePathAsync(ImageSource source, CancellationToken ct)
    {
        if (!source.IsInArchive)
            return source.Path;

        var tempDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer", "TempVideo");
        Directory.CreateDirectory(tempDir);
        var ext = Path.GetExtension(source.ArchiveEntry ?? ".mp4");
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";
        var tempPath = Path.Combine(tempDir, $"ipv-play-{Guid.NewGuid():N}{ext}");
        await Task.Run(() => ArchiveHelper.ExtractEntryToFile(source.Path, source.ArchiveEntry!, tempPath), ct)
            .ConfigureAwait(false);
        _tempPlaybackPath = tempPath;
        return tempPath;
    }

    private void StopInternal(bool keepPlayer)
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

        if (!keepPlayer) return;
        PlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void CleanupTemp()
    {
        var path = _tempPlaybackPath;
        _tempPlaybackPath = null;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"VlcPlaybackService temp cleanup: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopInternal(keepPlayer: false);
        CleanupTemp();
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
