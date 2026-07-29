// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Avalonia.Services;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Core.Text;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using LibVLCSharp.Shared;

namespace IcedPicViewer.Avalonia.ViewModels;

/// <summary>
/// Avalonia gallery: progressive scan fill, masonry, viewer, shell ops,
/// and directory watching.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    // Auto-fill cap == PageSize (same as WinUI Load More chunk).
    // Drain stops when Items.Count >= PageSize.
    private const int ScanPageSize = 30;
    private const int PageSize = 200;
    private const int ScanBatchSize = 100;
    private const int ScanBatchMs = 50;
    private const int ThumbMaxEdge = 256;
    private const int FullMaxEdge = 5120;
    private const int ThumbConcurrency = 6;

    private readonly DirectoryScanner _scanner = new();
    private readonly DesktopShellService _shell = new();
    private readonly JsonSettingsService _settings = new();
    private readonly VlcPlaybackService _vlc = new();
    private GifAnimationPlayer? _gifPlayer;
    private DispatcherTimer? _chromeHideTimer;
    private CancellationTokenSource? _loadCts;
    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(ThumbConcurrency, ThumbConcurrency);
    private readonly object _remainingLock = new();
    private List<ImageSource> _remainingSources = new();
    private IDisposable? _watcher;
    private DispatcherTimer? _slideshowTimer;
    private readonly List<int> _shuffleQueue = new();
    private int _lastShuffleIndex = -1;
    private bool _slideshowLoadMoreInFlight;
    private bool _pageFillInFlight;
    private bool _scanComplete;
    private int _scanErrors;
    private bool _disposed;

    // ── Properties ───────────────────────────────────────────────────

    public ObservableCollection<GalleryItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial string StatusText { get; set; } = GalleryStatusFormatter.IdleDefault;

    [ObservableProperty]
    public partial string FolderPath { get; set; } = "";

    [ObservableProperty]
    public partial int DiscoveredCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    [NotifyPropertyChangedFor(nameof(IsScanning))]
    public partial LoadingState LoadingState { get; set; } = LoadingState.Idle;

    /// <summary>True while a directory scan is in progress (same meaning as WinUI).</summary>
    public bool IsScanning => LoadingState == LoadingState.Scanning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NavigatePreviousCommand))]
    [NotifyCanExecuteChangedFor(nameof(NavigateNextCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealSelectedCommand))]
    public partial GalleryItemViewModel? SelectedItem { get; set; }

    [ObservableProperty]
    public partial bool IsViewerOpen { get; set; }

    [ObservableProperty]
    public partial bool IsFitMode { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowButtonLabel))]
    public partial bool IsSlideshowActive { get; set; }

    [ObservableProperty]
    public partial bool IsSlideshowLooping { get; set; }

    [ObservableProperty]
    public partial bool IsSlideshowShuffling { get; set; }

    [ObservableProperty]
    public partial double SlideshowInterval { get; set; } = 5.0;

    public string SlideshowButtonLabel => IsSlideshowActive ? "Stop Slideshow" : "Slideshow";

    [ObservableProperty]
    public partial bool IsFullscreen { get; set; }

    [ObservableProperty]
    public partial bool IsChromeVisible { get; set; } = true;

    [ObservableProperty]
    public partial double ChromeOpacity { get; set; } = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(ShowVideoPoster))]
    public partial bool IsVideoPlaying { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 1.0;

    public MediaPlayer? MediaPlayer => _vlc.Player;

    public string PlayPauseLabel => IsVideoPlaying ? "Pause" : "Play";

    public bool ShowVideoPoster =>
        SelectedItem?.IsVideo == true && !IsVideoPlaying;

    public bool IsVideoSelected => SelectedItem?.IsVideo == true;

    public Func<CancellationToken, Task<string?>>? PickFolderAsync { get; set; }

    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    public Action<bool>? ApplyFullscreen { get; set; }

    public Action<GalleryItemViewModel>? RequestScrollToItem { get; set; }

    public JsonSettingsService Settings => _settings;

    public MainViewModel()
    {
        IsSlideshowLooping = _settings.Current.SlideshowLoop;
        IsSlideshowShuffling = _settings.Current.SlideshowShuffle;
        SlideshowInterval = Math.Clamp(_settings.Current.SlideshowInterval, 1.0, 30.0);
        Volume = Math.Clamp(_settings.Current.VideoVolume, 0.0, 1.0);
        _vlc.PlayingChanged += OnVlcPlayingChanged;
    }

    // ── Setting change handlers ──────────────────────────────────────

    private void OnVlcPlayingChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsVideoPlaying = _vlc.IsPlaying;
            OnPropertyChanged(nameof(ShowVideoPoster));
        });
    }

    partial void OnVolumeChanged(double value)
    {
        var v = Math.Clamp(value, 0.0, 1.0);
        if (Math.Abs(v - value) > 0.0001)
        {
            Volume = v;
            return;
        }
        _vlc.Volume = v;
        _settings.Current.VideoVolume = v;
        _settings.ScheduleSave();
    }

    partial void OnIsSlideshowLoopingChanged(bool value)
    {
        _settings.Current.SlideshowLoop = value;
        _settings.ScheduleSave();
    }

    partial void OnIsSlideshowShufflingChanged(bool value)
    {
        if (value)
        {
            _shuffleQueue.Clear();
            _lastShuffleIndex = -1;
        }
        _settings.Current.SlideshowShuffle = value;
        _settings.ScheduleSave();
    }

    partial void OnSlideshowIntervalChanged(double value)
    {
        var clamped = Math.Clamp(value, 1.0, 30.0);
        if (Math.Abs(clamped - value) > 0.001)
        {
            SlideshowInterval = clamped;
            return;
        }

        _settings.Current.SlideshowInterval = clamped;
        _settings.ScheduleSave();
        if (_slideshowTimer is not null)
            _slideshowTimer.Interval = TimeSpan.FromSeconds(clamped);
    }

    // ── Fullscreen chrome ────────────────────────────────────────────

    private const double FullscreenChromeHideMs = 650;
    private bool _pointerInChromeHotZone;

    partial void OnIsFullscreenChanged(bool value)
    {
        ApplyFullscreen?.Invoke(value);
        if (value)
        {
            SetChromeShown(true);
            _pointerInChromeHotZone = false;
            ScheduleChromeHide();
        }
        else
        {
            StopChromeHideTimer();
            _pointerInChromeHotZone = false;
            SetChromeShown(true);
        }
    }

    public void NotifyPointerHotZone(bool inHotZone)
    {
        if (!IsFullscreen)
        {
            StopChromeHideTimer();
            SetChromeShown(true);
            _pointerInChromeHotZone = false;
            return;
        }

        if (inHotZone)
        {
            _pointerInChromeHotZone = true;
            StopChromeHideTimer();
            SetChromeShown(true);
            return;
        }

        _pointerInChromeHotZone = false;
        if (IsChromeVisible || ChromeOpacity > 0.01)
            ScheduleChromeHide();
    }

    public void PeekChrome()
    {
        if (!IsFullscreen)
        {
            SetChromeShown(true);
            return;
        }

        SetChromeShown(true);
        if (!_pointerInChromeHotZone)
            ScheduleChromeHide();
    }

    private void SetChromeShown(bool shown)
    {
        if (shown)
        {
            IsChromeVisible = true;
            ChromeOpacity = 1.0;
        }
        else
        {
            ChromeOpacity = 0.0;
            IsChromeVisible = false;
        }
    }

    private void ScheduleChromeHide()
    {
        StopChromeHideTimer();
        if (!IsFullscreen) return;

        _chromeHideTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(FullscreenChromeHideMs),
            DispatcherPriority.Normal,
            OnChromeHideTick);
        _chromeHideTimer.Start();
    }

    private void OnChromeHideTick(object? sender, EventArgs e)
    {
        StopChromeHideTimer();
        if (_pointerInChromeHotZone) return;
        if (IsFullscreen)
            SetChromeShown(false);
    }

    private void StopChromeHideTimer()
    {
        if (_chromeHideTimer is null) return;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Tick -= OnChromeHideTick;
        _chromeHideTimer = null;
    }

    // ── Shell commands ───────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            StatusText = GalleryStatusFormatter.FormatFolderPickerUnavailable();
            return;
        }

        var path = await PickFolderAsync(CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;

        await LoadDirectoryAsync(path).ConfigureAwait(true);
    }

    private bool CanOpenFolder() => !IsScanning;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(FolderPath) || !Directory.Exists(FolderPath)) return;
        await LoadDirectoryAsync(FolderPath).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsScanning && !string.IsNullOrEmpty(FolderPath);

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (ConfirmAsync is null) return;

        var settingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IcedPicViewer");
        var licensePath = Path.Combine(AppContext.BaseDirectory, "License", "ffmpeg-LGPL.txt");
        var licenseLine = File.Exists(licensePath)
            ? licensePath
            : "License/ffmpeg-LGPL.txt (next to the executable after build)";

        var body =
            $"IcedPicViewer (Avalonia)  v0.15.0  ({IcedPicViewer.Avalonia.BuildInfo.CommitShort})\n\n" +
            "Cross-platform gallery: masonry, archives (ZIP/RAR/tar.*), EXIF, GIF,\n" +
            "slideshow, video thumbs (FFmpeg LGPL shared) + playback (LibVLC).\n\n" +
            "Formats: see README — HEIC/AVIF need platform codecs; 7z is not supported.\n" +
            "FFmpeg is LGPL 2.1+ (replaceable natives under runtimes/<rid>/native).\n" +
            $"LGPL text:\n{licenseLine}\n\n" +
            $"Settings:\n{Path.Combine(settingsDir, "settings.json")}";

        await ConfirmAsync("About IcedPicViewer", body).ConfigureAwait(true);
    }

    // ── Dispose ──────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopSlideshow();
        StopGif();
        StopChromeHideTimer();
        _vlc.PlayingChanged -= OnVlcPlayingChanged;
        _vlc.Dispose();
        StopWatcher();
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _thumbnailLoadSemaphore.Dispose();
        _settings.Dispose();
        GC.SuppressFinalize(this);
    }
}
