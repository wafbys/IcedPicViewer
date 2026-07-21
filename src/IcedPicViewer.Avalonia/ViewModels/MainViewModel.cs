// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Avalonia.Services;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using LibVLCSharp.Shared;
// FileChangeInfo / WatchChangeType live in Interfaces (Core).

namespace IcedPicViewer.Avalonia.ViewModels;

/// <summary>
/// Avalonia gallery: progressive scan fill, masonry, viewer, shell ops,
/// and directory watching.
/// </summary>
public partial class MainViewModel : ViewModelBase, IDisposable
{
    private const int AutoCap = 200;
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
    private readonly SemaphoreSlim _thumbSemaphore = new(ThumbConcurrency, ThumbConcurrency);
    private readonly object _remainingLock = new();
    private List<ImageSource> _remaining = new();
    private IDisposable? _watcher;
    private DispatcherTimer? _slideshowTimer;
    private readonly List<int> _shuffleQueue = new();
    private int _lastShuffleIndex = -1;
    private bool _pageFillInFlight;
    private bool _scanComplete;
    private int _scanErrors;
    private bool _disposed;

    public ObservableCollection<GalleryItemViewModel> Items { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial string StatusText { get; set; } = "Open a folder to start";

    [ObservableProperty]
    public partial string FolderPath { get; set; } = "";

    [ObservableProperty]
    public partial int DiscoveredCount { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    public partial bool IsBusy { get; set; }

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

    /// <summary>
    /// Whether chrome should accept hits (false when fully faded out in fullscreen).
    /// </summary>
    [ObservableProperty]
    public partial bool IsChromeVisible { get; set; } = true;

    /// <summary>0–1 opacity for smooth chrome fade (always animatable).</summary>
    [ObservableProperty]
    public partial double ChromeOpacity { get; set; } = 1.0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseLabel))]
    [NotifyPropertyChangedFor(nameof(ShowVideoPoster))]
    public partial bool IsVideoPlaying { get; set; }

    [ObservableProperty]
    public partial double Volume { get; set; } = 1.0;

    /// <summary>Bound to LibVLCSharp Avalonia VideoView.</summary>
    public MediaPlayer? MediaPlayer => _vlc.Player;

    public string PlayPauseLabel => IsVideoPlaying ? "Pause" : "Play";

    /// <summary>Show still frame overlay until the user starts playback.</summary>
    public bool ShowVideoPoster =>
        SelectedItem?.IsVideo == true && !IsVideoPlaying;

    public bool IsVideoSelected => SelectedItem?.IsVideo == true;

    /// <summary>Wired by the view for folder picker.</summary>
    public Func<CancellationToken, Task<string?>>? PickFolderAsync { get; set; }

    /// <summary>Wired by the view for yes/no confirmation dialogs.</summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>Wired by the view to apply OS window fullscreen.</summary>
    public Action<bool>? ApplyFullscreen { get; set; }

    public MainViewModel()
    {
        IsSlideshowLooping = _settings.Current.SlideshowLoop;
        IsSlideshowShuffling = _settings.Current.SlideshowShuffle;
        SlideshowInterval = Math.Clamp(_settings.Current.SlideshowInterval, 1.0, 30.0);
        Volume = Math.Clamp(_settings.Current.VideoVolume, 0.0, 1.0);
        _vlc.PlayingChanged += OnVlcPlayingChanged;
    }

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

    // Fullscreen chrome: only edge hot-zones show bars (not every mouse move / flip).
    private const double FullscreenChromeHideMs = 650;
    private bool _pointerInChromeHotZone;

    partial void OnIsFullscreenChanged(bool value)
    {
        ApplyFullscreen?.Invoke(value);
        if (value)
        {
            // Enter fullscreen: show briefly then fade out (unless pointer is in edge zone).
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

    /// <summary>
    /// Fullscreen: only top/bottom hot-zones show chrome. Middle movement and
    /// image navigation must NOT re-show the toolbar.
    /// </summary>
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

    /// <summary>Explicit peek only (e.g. enter fullscreen) — never call on flip.</summary>
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
            // Hit-test off after fade starts; opacity animates via XAML transition.
            IsChromeVisible = false;
        }
    }

    private void ScheduleChromeHide()
    {
        StopChromeHideTimer();
        if (!IsFullscreen) return;

        _chromeHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(FullscreenChromeHideMs),
        };
        _chromeHideTimer.Tick += OnChromeHideTick;
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

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private async Task OpenFolderAsync()
    {
        if (PickFolderAsync is null)
        {
            StatusText = "Folder picker not wired";
            return;
        }

        var path = await PickFolderAsync(CancellationToken.None).ConfigureAwait(true);
        if (string.IsNullOrEmpty(path)) return;

        await LoadDirectoryAsync(path).ConfigureAwait(true);
    }

    private bool CanOpenFolder() => !IsBusy;

    /// <summary>Shared settings store (window chrome also reads/writes geometry).</summary>
    public JsonSettingsService Settings => _settings;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(FolderPath) || !Directory.Exists(FolderPath)) return;
        await LoadDirectoryAsync(FolderPath).ConfigureAwait(true);
    }

    private bool CanRefresh() => !IsBusy && !string.IsNullOrEmpty(FolderPath);

    [RelayCommand]
    private async Task ShowAboutAsync()
    {
        if (ConfirmAsync is null) return;
        await ConfirmAsync(
            "About IcedPicViewer",
            "IcedPicViewer (Avalonia)\n\n" +
            "Cross-platform gallery: masonry, archives, EXIF, GIF, slideshow,\n" +
            "video thumbs (FFmpeg) + playback (LibVLC).\n\n" +
            "WinUI shell remains in-repo as the original Windows baseline.\n" +
            "Settings: %LOCALAPPDATA%\\IcedPicViewer\\settings.json").ConfigureAwait(true);
    }

    /// <summary>View scrolls the gallery list to this item after leaving the viewer.</summary>
    public Action<GalleryItemViewModel>? RequestScrollToItem { get; set; }

    [RelayCommand]
    private void CloseViewer()
    {
        StopSlideshow();
        StopVideo();
        StopGif();
        var staySelected = SelectedItem;
        IsViewerOpen = false;
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(ShowVideoPoster));
        // Keep SelectedItem so the gallery can highlight / scroll to it.
        if (staySelected is not null)
            RequestScrollToItem?.Invoke(staySelected);
    }

    [RelayCommand]
    private void ToggleFitMode() => IsFitMode = !IsFitMode;

    [RelayCommand]
    private void PlayPauseVideo()
    {
        if (SelectedItem?.IsVideo != true) return;
        // Space while slideshow is active and viewing a video prefers video control.
        if (IsSlideshowActive) StopSlideshow();
        _vlc.EnsureInitialized();
        OnPropertyChanged(nameof(MediaPlayer));
        _vlc.Volume = Volume;
        _vlc.TogglePlayPause();
        IsVideoPlaying = _vlc.IsPlaying;
        OnPropertyChanged(nameof(ShowVideoPoster));
    }

    [RelayCommand]
    private void HandleSpace()
    {
        if (!IsViewerOpen) return;
        if (SelectedItem?.IsVideo == true)
            PlayPauseVideo();
        else
            ToggleSlideshow();
    }

    private void StopVideo()
    {
        _vlc.Stop();
        IsVideoPlaying = false;
        OnPropertyChanged(nameof(ShowVideoPoster));
    }

    /// <summary>0–100 percent seek (VLC / mpv digit-key habit).</summary>
    public void SeekVideoToPercent(int percent)
    {
        if (SelectedItem?.IsVideo != true) return;
        _vlc.SeekFraction(percent / 100.0);
        if (!_vlc.IsPlaying)
        {
            _vlc.Play();
            IsVideoPlaying = true;
            OnPropertyChanged(nameof(ShowVideoPoster));
        }
    }

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void ToggleSlideshow()
    {
        if (IsSlideshowActive) StopSlideshow();
        else StartSlideshow();
    }

    [RelayCommand]
    private void ToggleSlideshowLoop() => IsSlideshowLooping = !IsSlideshowLooping;

    [RelayCommand]
    private void ToggleSlideshowShuffle() => IsSlideshowShuffling = !IsSlideshowShuffling;

    /// <summary>
    /// Gallery toolbar entry: open viewer at current/first image and start slideshow.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanStartGallerySlideshow))]
    private void StartGallerySlideshow()
    {
        if (Items.Count == 0) return;
        var start = SelectedItem is not null && Items.Contains(SelectedItem)
            ? SelectedItem
            : Items.FirstOrDefault(i => !i.IsVideo) ?? Items[0];
        OpenItem(start);
        StartSlideshow();
    }

    private bool CanStartGallerySlideshow() => Items.Count > 0;

    public void StartSlideshow()
    {
        if (Items.Count == 0) return;

        if (SelectedItem is null || !Items.Contains(SelectedItem))
        {
            var first = Items.FirstOrDefault(i => !i.IsVideo) ?? Items[0];
            OpenItem(first);
        }
        else if (!IsViewerOpen)
        {
            OpenItem(SelectedItem);
        }

        _slideshowTimer?.Stop();
        _slideshowTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Clamp(SlideshowInterval, 1.0, 30.0)),
        };
        _slideshowTimer.Tick += OnSlideshowTick;
        _slideshowTimer.Start();
        IsSlideshowActive = true;
        StatusText = $"Slideshow every {SlideshowInterval:0.#}s"
            + (IsSlideshowLooping ? " · loop" : "")
            + (IsSlideshowShuffling ? " · shuffle" : "");
    }

    public void StopSlideshow()
    {
        if (_slideshowTimer is not null)
        {
            _slideshowTimer.Stop();
            _slideshowTimer.Tick -= OnSlideshowTick;
            _slideshowTimer = null;
        }
        IsSlideshowActive = false;
    }

    private async void OnSlideshowTick(object? sender, EventArgs e)
    {
        try
        {
            await AdvanceSlideshowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"OnSlideshowTick: {ex.Message}");
            StopSlideshow();
        }
    }

    private async Task AdvanceSlideshowAsync()
    {
        if (Items.Count == 0)
        {
            StopSlideshow();
            return;
        }

        var next = IsSlideshowShuffling
            ? PickShuffleNext()
            : PickSequentialNext();

        if (next is null)
        {
            StopSlideshow();
            StatusText = "Slideshow finished";
            return;
        }

        OpenItem(next);
        // If we landed on a video with no still to show, skip ahead once more.
        if (next.IsVideo && next.FullImage is null && next.Thumbnail is null)
        {
            var skip = IsSlideshowShuffling ? PickShuffleNext() : PickSequentialNext();
            if (skip is not null) OpenItem(skip);
        }

        await Task.CompletedTask;
    }

    private GalleryItemViewModel? PickSequentialNext()
    {
        if (Items.Count == 0) return null;

        var current = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        var nextIndex = current + 1;

        if (nextIndex >= Items.Count)
        {
            if (IsSlideshowLooping)
            {
                nextIndex = 0;
            }
            else if (CanLoadMore && !IsLoadingMore)
            {
                // Stay on current frame; kick Load More then advance when ready.
                _ = LoadMoreThenContinueSlideshowAsync();
                return SelectedItem;
            }
            else
            {
                return null;
            }
        }

        return Items[nextIndex];
    }

    private async Task LoadMoreThenContinueSlideshowAsync()
    {
        try
        {
            await LoadMoreAsync().ConfigureAwait(true);
            if (!IsSlideshowActive) return;
            var after = PickSequentialNext();
            if (after is not null)
                OpenItem(after);
            else
                StopSlideshow();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadMoreThenContinueSlideshowAsync: {ex.Message}");
        }
    }

    private GalleryItemViewModel? PickShuffleNext()
    {
        if (_shuffleQueue.Count == 0)
            RefillShuffleQueue();

        if (_shuffleQueue.Count == 0) return null;

        var idx = _shuffleQueue[0];
        _shuffleQueue.RemoveAt(0);
        _lastShuffleIndex = idx;
        if (idx < 0 || idx >= Items.Count) return PickShuffleNext();
        return Items[idx];
    }

    private void RefillShuffleQueue()
    {
        _shuffleQueue.Clear();
        for (var i = 0; i < Items.Count; i++)
            _shuffleQueue.Add(i);

        // Fisher–Yates
        for (var i = _shuffleQueue.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_shuffleQueue[i], _shuffleQueue[j]) = (_shuffleQueue[j], _shuffleQueue[i]);
        }

        // Avoid immediately replaying the last shown item at cycle start.
        if (_shuffleQueue.Count > 1 && _lastShuffleIndex >= 0
            && _shuffleQueue[0] == _lastShuffleIndex)
        {
            var swap = Random.Shared.Next(1, _shuffleQueue.Count);
            (_shuffleQueue[0], _shuffleQueue[swap]) = (_shuffleQueue[swap], _shuffleQueue[0]);
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private void NavigatePrevious()
    {
        if (SelectedItem is null) return;
        var i = Items.IndexOf(SelectedItem);
        if (i > 0) OpenItem(Items[i - 1]);
    }

    private bool CanNavigatePrevious()
        => SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NavigateNextAsync()
    {
        if (SelectedItem is null) return;
        var i = Items.IndexOf(SelectedItem);
        if (i < 0) return;
        if (i + 1 < Items.Count)
        {
            OpenItem(Items[i + 1]);
            return;
        }

        if (CanLoadMore && !IsLoadingMore)
        {
            await LoadMoreAsync().ConfigureAwait(true);
            if (i + 1 < Items.Count)
                OpenItem(Items[i + 1]);
        }
    }

    private bool CanNavigateNext()
    {
        if (SelectedItem is null) return false;
        var i = Items.IndexOf(SelectedItem);
        if (i < 0) return false;
        return i + 1 < Items.Count || CanLoadMore;
    }

    [RelayCommand]
    private void RevealItem(GalleryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        // Always reveal the on-disk path (archive file for entries).
        _shell.RevealInFolder(item.Source.Path);
    }

    [RelayCommand(CanExecute = nameof(CanRevealSelected))]
    private void RevealSelected() => RevealItem(SelectedItem);

    private bool CanRevealSelected() => SelectedItem is not null;

    [RelayCommand]
    private async Task DeleteItemAsync(GalleryItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        if (item.Source.IsInArchive)
        {
            StatusText = "无法删除：压缩包内文件不支持删除";
            if (ConfirmAsync is not null)
                await ConfirmAsync("无法删除", "压缩包内的媒体不能从本应用删除。请在资源管理器中处理整个压缩包。").ConfigureAwait(true);
            return;
        }

        var path = item.Source.Path;
        var preferTrash = true;
        if (_shell.IsNetworkPath(path))
        {
            preferTrash = false;
            if (ConfirmAsync is not null)
            {
                var ok = await ConfirmAsync(
                    "确认删除",
                    $"网络路径文件将永久删除，无法进回收站：\n{path}\n\n确定删除？").ConfigureAwait(true);
                if (!ok) return;
            }
        }

        if (!_shell.TryDelete(path, preferTrash, out var error))
        {
            StatusText = $"Delete failed: {error}";
            return;
        }

        RemoveItemEverywhere(item);
        StatusText = preferTrash ? $"Moved to trash: {item.Name}" : $"Deleted: {item.Name}";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync() => await DeleteItemAsync(SelectedItem).ConfigureAwait(true);

    private bool CanDeleteSelected() => SelectedItem is not null && !SelectedItem.Source.IsInArchive;

    public void OpenItem(GalleryItemViewModel item)
    {
        // Leaving a video/gif stops playback resources.
        if (SelectedItem is not null && !ReferenceEquals(SelectedItem, item))
        {
            StopVideo();
            StopGif();
        }

        SelectedItem = item;
        IsViewerOpen = true;
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(ShowVideoPoster));
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RevealSelectedCommand.NotifyCanExecuteChanged();
        StartGallerySlideshowCommand.NotifyCanExecuteChanged();
        // Do NOT PeekChrome on flip — fullscreen navigation must stay immersive.
        _ = LoadFullImageAsync(item);
        if (item.IsVideo)
            _ = PrepareVideoAsync(item);
    }

    private void StopGif()
    {
        _gifPlayer?.Stop();
        _gifPlayer?.Dispose();
        _gifPlayer = null;
    }

    private static bool IsGifSource(ImageSource source)
    {
        var name = source.IsInArchive ? source.ArchiveEntry! : source.Path;
        return Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PrepareVideoAsync(GalleryItemViewModel item)
    {
        try
        {
            _vlc.EnsureInitialized();
            OnPropertyChanged(nameof(MediaPlayer));
            _vlc.Volume = Volume;
            var ok = await _vlc.LoadAsync(item.Source, _loadCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            if (!ok)
                StatusText = "Video load failed (codec / path)";
        }
        catch (Exception ex)
        {
            Trace.TraceError($"PrepareVideoAsync: {ex.Message}");
            StatusText = $"Video error: {ex.Message}";
        }
    }

    public async Task LoadDirectoryAsync(string path)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        StopSlideshow();
        StopGif();
        StopVideo();
        StopWatcher();

        Items.Clear();
        SelectedItem = null;
        IsViewerOpen = false;
        FolderPath = path;
        DiscoveredCount = 0;
        CanLoadMore = false;
        _scanComplete = false;
        _scanErrors = 0;
        lock (_remainingLock) _remaining = new List<ImageSource>();
        IsBusy = true;
        StatusText = "Scanning…";
        RefreshCommand.NotifyCanExecuteChanged();

        // Do not persist last folder — user re-picks every launch.

        StartWatcher(path);
        _ = Task.Run(() => RunScanAndBatchAsync(path, ct), ct);
        await Task.CompletedTask;
    }

    private void StartWatcher(string path)
    {
        try
        {
            _watcher = _scanner.Watch(path, recursive: true, OnFileChanged);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"StartWatcher: {ex.Message}");
        }
    }

    private void StopWatcher()
    {
        try { _watcher?.Dispose(); }
        catch (Exception ex) { Trace.TraceError($"StopWatcher: {ex.Message}"); }
        _watcher = null;
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        if (DirectoryScanner.IsRecycleBin(info.Path)) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                HandleFileChangeOnUi(info);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"HandleFileChangeOnUi: {ex.Message}");
            }
        });
    }

    private void HandleFileChangeOnUi(FileChangeInfo info)
    {
        switch (info.ChangeType)
        {
            case WatchChangeType.Deleted:
                RemoveByPath(info.Path);
                break;

            case WatchChangeType.Created:
                if (ArchiveHelper.IsArchiveFileName(info.Path))
                {
                    // New archive: enqueue entries (best-effort, image+video).
                    _ = IngestNewArchiveAsync(info.Path);
                    break;
                }
                if (!MediaCatalog.IsSupported(info.Path)) return;
                EnqueueNewSource(ImageSource.FromFile(info.Path, MediaCatalog.GetKind(info.Path)));
                break;

            case WatchChangeType.Renamed:
                if (!string.IsNullOrEmpty(info.OldPath))
                    RemoveByPath(info.OldPath);
                if (MediaCatalog.IsSupported(info.Path) && File.Exists(info.Path))
                    EnqueueNewSource(ImageSource.FromFile(info.Path, MediaCatalog.GetKind(info.Path)));
                break;

            case WatchChangeType.Modified:
                // Ignore content modifications for MVP (would need thumb invalidate).
                break;
        }
    }

    private async Task IngestNewArchiveAsync(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath) || !ArchiveHelper.IsArchive(archivePath)) return;
            var extSet = new HashSet<string>(MediaCatalog.ImageExtensions.Concat(MediaCatalog.VideoExtensions), StringComparer.OrdinalIgnoreCase);
            var entries = await Task.Run(() => ArchiveHelper.ListEntries(archivePath, extSet).ToList()).ConfigureAwait(true);
            foreach (var e in entries)
            {
                var kind = MediaCatalog.GetKind(e.Key);
                EnqueueNewSource(ImageSource.FromArchive(archivePath, e.Key, kind));
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"IngestNewArchiveAsync {archivePath}: {ex.Message}");
        }
    }

    private void EnqueueNewSource(ImageSource source)
    {
        var id = source.ToString();
        if (Items.Any(i => i.Source.ToString() == id)) return;
        lock (_remainingLock)
        {
            if (_remaining.Any(s => s.ToString() == id)) return;
            _remaining.Add(source);
        }
        DiscoveredCount++;
        UpdateCanLoadMore();
        var ct = _loadCts?.Token ?? CancellationToken.None;
        _ = DrainPageFillAsync(ct);
        UpdateStatus();
    }

    private void RemoveByPath(string path)
    {
        // Loose file id == path; archive path prefix matches archive entries.
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var s = Items[i].Source;
            if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ToString(), path, StringComparison.OrdinalIgnoreCase))
            {
                var item = Items[i];
                Items.RemoveAt(i);
                if (SelectedItem == item)
                {
                    SelectedItem = null;
                    IsViewerOpen = false;
                }
            }
        }

        lock (_remainingLock)
        {
            _remaining.RemoveAll(s =>
                string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ToString(), path, StringComparison.OrdinalIgnoreCase));
        }

        UpdateCanLoadMore();
        UpdateStatus();
    }

    private void RemoveItemEverywhere(GalleryItemViewModel item)
    {
        var idx = Items.IndexOf(item);
        if (idx >= 0) Items.RemoveAt(idx);

        var id = item.Source.ToString();
        lock (_remainingLock)
            _remaining.RemoveAll(s => s.ToString() == id);

        if (SelectedItem == item)
        {
            // Advance to next if possible, else previous, else close.
            if (idx >= 0 && idx < Items.Count)
                OpenItem(Items[idx]);
            else if (idx > 0 && idx - 1 < Items.Count)
                OpenItem(Items[idx - 1]);
            else
            {
                SelectedItem = null;
                IsViewerOpen = false;
            }
        }

        UpdateCanLoadMore();
        UpdateStatus();
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
    }

    private async Task RunScanAndBatchAsync(string path, CancellationToken ct)
    {
        var batch = new List<ImageSource>(ScanBatchSize);
        long batchStartTick = 0;
        var discovered = 0;

        try
        {
            await foreach (var source in _scanner.ScanAsync(
                               path,
                               recursive: true,
                               extensions: MediaCatalog.SupportedMedia,
                               errorReporter: new Progress<ScanError>(_ => Interlocked.Increment(ref _scanErrors)),
                               discoveredReporter: null,
                               currentPathReporter: null,
                               ct: ct).ConfigureAwait(false))
            {
                if (batch.Count == 0)
                    batchStartTick = Environment.TickCount64;

                batch.Add(source);
                discovered++;

                var elapsed = Environment.TickCount64 - batchStartTick;
                if (batch.Count >= ScanBatchSize || elapsed >= ScanBatchMs)
                {
                    FlushBatch(batch, discovered, ct);
                    batch = new List<ImageSource>(ScanBatchSize);
                }
            }

            if (batch.Count > 0)
                FlushBatch(batch, discovered, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _scanComplete = true;
                IsBusy = false;
                UpdateStatus();
                RefreshCommand.NotifyCanExecuteChanged();
                _ = DrainPageFillAsync(ct);
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                StatusText = "Cancelled";
                RefreshCommand.NotifyCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RunScanAndBatchAsync: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                StatusText = $"Error: {ex.Message}";
                RefreshCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void FlushBatch(List<ImageSource> batch, int discovered, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || batch.Count == 0) return;

        var snapshot = batch.ToList();
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;

            lock (_remainingLock)
                _remaining.AddRange(snapshot);

            DiscoveredCount = discovered;
            UpdateCanLoadMore();
            UpdateStatus();
            _ = DrainPageFillAsync(ct);
        });
    }

    private async Task DrainPageFillAsync(CancellationToken ct)
    {
        if (_pageFillInFlight) return;
        _pageFillInFlight = true;
        try
        {
            while (!ct.IsCancellationRequested && Items.Count < AutoCap)
            {
                List<ImageSource> chunk;
                lock (_remainingLock)
                {
                    if (_remaining.Count == 0) break;
                    var take = Math.Min(ScanPageSize, Math.Min(AutoCap - Items.Count, _remaining.Count));
                    chunk = _remaining.GetRange(0, take);
                    _remaining.RemoveRange(0, take);
                }

                foreach (var source in chunk)
                {
                    var item = GalleryItemViewModel.FromSource(source);
                    Items.Add(item);
                    _ = LoadThumbnailFireAndForgetAsync(item, ct);
                }

                UpdateCanLoadMore();
                UpdateStatus();
                StartGallerySlideshowCommand.NotifyCanExecuteChanged();
                await Task.Yield();
            }
        }
        finally
        {
            _pageFillInFlight = false;
            UpdateCanLoadMore();
            UpdateStatus();
            NavigateNextCommand.NotifyCanExecuteChanged();
            StartGallerySlideshowCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreCommand))]
    private async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoadingMore) return;
        IsLoadingMore = true;
        try
        {
            List<ImageSource> chunk;
            lock (_remainingLock)
            {
                if (_remaining.Count == 0) return;
                var take = Math.Min(PageSize, _remaining.Count);
                chunk = _remaining.GetRange(0, take);
                _remaining.RemoveRange(0, take);
            }

            var ct = _loadCts?.Token ?? CancellationToken.None;
            foreach (var source in chunk)
            {
                var item = GalleryItemViewModel.FromSource(source);
                Items.Add(item);
                _ = LoadThumbnailFireAndForgetAsync(item, ct);
            }

            UpdateCanLoadMore();
            UpdateStatus();
            NavigateNextCommand.NotifyCanExecuteChanged();
            await Task.CompletedTask;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private bool CanLoadMoreCommand() => CanLoadMore && !IsLoadingMore;

    private void UpdateCanLoadMore()
    {
        int remaining;
        lock (_remainingLock) remaining = _remaining.Count;
        CanLoadMore = remaining > 0;
    }

    private void UpdateStatus()
    {
        int remaining;
        lock (_remainingLock) remaining = _remaining.Count;
        var err = _scanErrors > 0 ? $" · {_scanErrors} scan error(s)" : "";

        if (IsBusy || !_scanComplete)
        {
            StatusText = $"Scanning… found {DiscoveredCount}, showing {Items.Count}{err}";
            return;
        }

        StatusText = remaining > 0
            ? $"Showing {Items.Count} / {DiscoveredCount} ({remaining} more){err}"
            : $"Loaded {Items.Count} item(s){err}";
    }

    private async Task LoadThumbnailFireAndForgetAsync(GalleryItemViewModel item, CancellationToken ct)
    {
        try
        {
            await _thumbSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Images + videos share loader; original W×H used for hover info.
            var (bmp, ow, oh) = await AvaloniaImageLoader.LoadThumbnailWithInfoAsync(
                    item.Source, ThumbMaxEdge, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => item.ApplyThumbnail(bmp, ow, oh));
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Trace.TraceError($"thumb {item.Source}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => item.IsThumbnailLoading = false);
        }
        finally
        {
            try { _thumbSemaphore.Release(); } catch (ObjectDisposedException) { /* shutdown */ }
        }
    }

    private async Task LoadFullImageAsync(GalleryItemViewModel item)
    {
        // Still images that already have a decoded frame can skip — except GIF,
        // which must (re)start the frame timer every time we open them.
        var isGif = IsGifSource(item.Source);
        if (item.FullImage is not null && !isGif && item.Source.Kind != MediaKind.Video)
            return;

        var ct = _loadCts?.Token ?? CancellationToken.None;
        item.IsFullImageLoading = true;
        try
        {
            if (isGif)
            {
                await LoadGifAsync(item, ct).ConfigureAwait(false);
                return;
            }

            // Videos: poster still via FFmpeg; playback is LibVLC.
            var maxEdge = item.Source.Kind == MediaKind.Video ? ThumbMaxEdge * 4 : FullMaxEdge;
            var bmp = await AvaloniaImageLoader.LoadFullAsync(item.Source, maxEdge, ct)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedItem == item)
                    item.FullImage = bmp;
                item.IsFullImageLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"full {item.Source}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
        }
    }

    private async Task LoadGifAsync(GalleryItemViewModel item, CancellationToken ct)
    {
        await using var stream = await OpenSourceStreamAsync(item.Source, ct).ConfigureAwait(false);
        if (stream is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
            return;
        }

        var player = await GifAnimationPlayer.TryLoadAsync(stream, FullMaxEdge, ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedItem != item)
            {
                player?.Dispose();
                item.IsFullImageLoading = false;
                return;
            }

            StopGif();
            _gifPlayer = player;
            item.IsFullImageLoading = false;

            if (_gifPlayer is null)
                return;

            if (_gifPlayer.HasAnimation)
            {
                _gifPlayer.Start(frame =>
                {
                    if (SelectedItem == item)
                        item.FullImage = frame;
                });
            }
            else
            {
                item.FullImage = _gifPlayer.FirstFrame;
            }
        });
    }

    private static async Task<Stream?> OpenSourceStreamAsync(ImageSource source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (source.IsInArchive)
        {
            try
            {
                return ArchiveHelper.OpenEntryStream(source.Path, source.ArchiveEntry!);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"OpenSourceStreamAsync archive: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(source.Path)) return null;
        return new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
    }

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
        _thumbSemaphore.Dispose();
        _settings.Dispose();
        GC.SuppressFinalize(this);
    }
}
