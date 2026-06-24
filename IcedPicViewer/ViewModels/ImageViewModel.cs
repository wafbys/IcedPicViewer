// Copyright (c) IcedPicViewer. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace IcedPicViewer.ViewModels;

public partial class ImageViewModel : ObservableObject, IDisposable
{
    private readonly GalleryViewModel _galleryViewModel;
    private readonly IImageLoader _imageLoader;
    private readonly IVideoMetadataService _videoMetadataService;
    private readonly INavigationService _navigationService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _loadCts;
    private MediaPlayer? _mediaPlayer;
    private string? _currentPlaybackPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualWidth))]
    [NotifyPropertyChangedFor(nameof(ActualHeight))]
    [NotifyPropertyChangedFor(nameof(ImagePath))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
    [NotifyPropertyChangedFor(nameof(ImageHostVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerHostVisibility))]
    [NotifyPropertyChangedFor(nameof(IsPlayOverlayVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerElementVisibility))]
    [NotifyPropertyChangedFor(nameof(FitModeBtnVisibility))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial MediaItem? CurrentImage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualWidth))]
    [NotifyPropertyChangedFor(nameof(ActualHeight))]
    public partial BitmapImage? DisplayImage { get; set; }

    public event EventHandler? DisplayImageChanged;
    public event EventHandler? NavigationChanged;

    // The displayed bitmap's pixel dimensions. ActualWidth/Height fall back
    // to CurrentImage.OriginalWidth/Height until the bitmap has loaded.
    [ObservableProperty]
    public partial int DisplayActualWidth { get; set; }

    [ObservableProperty]
    public partial int DisplayActualHeight { get; set; }

    public int ActualWidth => DisplayActualWidth > 0 ? DisplayActualWidth : CurrentImage?.OriginalWidth ?? 0;

    public int ActualHeight => DisplayActualHeight > 0 ? DisplayActualHeight : CurrentImage?.OriginalHeight ?? 0;

    public string ImagePath => CurrentImage?.Source.ToString() ?? string.Empty;

    /// <summary>
    /// True when the currently-displayed item is a video. Forwarded from
    /// <see cref="MediaItem.IsVideo"/>; bound by the viewer's overlay
    /// chrome (FitMode button visibility, the play overlay, and the
    /// ImageHost/PlayerHost swap).
    /// </summary>
    public bool IsVideo => CurrentImage?.IsVideo ?? false;

    // ----------------------------------------------------------------
    // Video playback state.
    //
    // The viewer has two distinct surface modes: image (existing static
    // Image + Fit/1:1 toggle + minimap) and video (static first frame
    // overlaid with a centered play button that, on click, swaps the
    // surface to a MediaPlayerElement driven by a lazily-created
    // MediaPlayer). Both surfaces share the same Grid.Row so they
    // occupy the same screen real estate; visibility on their parent
    // Grids (ImageHost / PlayerHost) determines which one is on screen.
    //
    // The MediaPlayer is created on the first Play() call and torn
    // down by StopAndDisposePlayer() whenever the viewer navigates
    // away (next / prev / close / dispose). This matches the user
    // spec "点/Space 才创建 MediaPlayerElement 并 dispose" — we don't
    // pay the native player cost until the user actually wants
    // playback, and we never leak a player across navigations.
    // ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerHostVisibility))]
    [NotifyPropertyChangedFor(nameof(IsPlayOverlayVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerElementVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyPropertyChangedFor(nameof(PlayerScrollMode))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool IsVideoPlaying { get; set; }

    /// <summary>
    /// Fit-mode toggle, shared between the image and video surfaces. The
    /// view binds each surface to its own visibility / Stretch / ScrollMode
    /// based on this flag plus <see cref="IsVideo"/>. True = fit-to-view
    /// (current default), false = 1:1 native resolution. For images
    /// this swaps between the existing Viewbox and the ScrollViewer-with-
    /// minimap; for videos it swaps between MediaPlayerElement.Stretch=
    /// Uniform and Stretch=None inside a ScrollViewer (1:1 = scrollable
    /// native resolution).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyPropertyChangedFor(nameof(PlayerScrollMode))]
    public partial bool IsFitMode { get; set; } = true;

    /// <summary>
    /// The active <see cref="MediaPlayer"/> for video playback, or null
    /// when the viewer is showing the static first frame (or an image).
    /// The view's <c>MediaPlayerElement.MediaPlayer</c> binds OneWay to
    /// this property — setting it to null detaches the player from the
    /// surface so the view can safely fall back to the static frame.
    /// </summary>
    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set => SetProperty(ref _mediaPlayer, value);
    }

    // Visibility helpers — bound by the view to swap the image / player
    // surfaces and to hide image-only chrome (Fit/1:1 button) when
    // the current item is a video. All three (plus the helpers below)
    // are computed from IsVideo + IsVideoPlaying; the [Notify...]
    // attributes on CurrentImage and IsVideoPlaying above take care of
    // re-firing PropertyChanged when either input changes.

    public Visibility ImageHostVisibility => !IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlayerHostVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsPlayOverlayVisibility => IsVideo && !IsVideoPlaying ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The MediaPlayerElement is hidden until a <see cref="MediaPlayer"/>
    /// is actually attached. With no player attached, the element
    /// would still lay out and its built-in transport controls would
    /// render in a "no media" state — opaque, on top of the
    /// <see cref="IsPlayOverlayVisibility"/> button, so clicking the
    /// ▶ button hits the dead player surface instead. Collapsing the
    /// element while there's no player lets pointer events reach the
    /// overlay button, and re-shows the element only after
    /// <see cref="PlayAsync"/> has created a live <see cref="MediaPlayer"/>
    /// (so the transport controls are actually functional).
    /// </summary>
    public Visibility PlayerElementVisibility => IsVideo && IsVideoPlaying ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FitModeBtnVisibility => !IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// <see cref="MediaPlayerElement.Stretch"/> binding for the video
    /// surface. Fit mode = Uniform (scale to fit the viewport); 1:1
    /// mode = None (native resolution, scrollable via
    /// <see cref="PlayerScrollMode"/>).
    /// </summary>
    public Microsoft.UI.Xaml.Media.Stretch PlayerStretch =>
        IsFitMode ? Microsoft.UI.Xaml.Media.Stretch.Uniform : Microsoft.UI.Xaml.Media.Stretch.None;

    /// <summary>
    /// <see cref="Microsoft.UI.Xaml.Controls.ScrollViewer"/>'s scroll
    /// mode for the video surface. Disabled in Fit mode (no point
    /// scrolling when the player is already scaled to fit); enabled
    /// in 1:1 mode so the user can scroll past the viewport edges to
    /// see the full native-resolution frame.
    /// </summary>
    public Microsoft.UI.Xaml.Controls.ScrollMode PlayerScrollMode =>
        IsFitMode
            ? Microsoft.UI.Xaml.Controls.ScrollMode.Disabled
            : Microsoft.UI.Xaml.Controls.ScrollMode.Enabled;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (CurrentImage is not VideoItem video) return;
        if (_mediaPlayer != null) return;

        string? playbackPath = null;
        try
        {
            // Get a file path MediaPlayer can read. For loose files this
            // is just source.Path; for archive entries the service
            // extracts to a tracked temp file. The temp file must be
            // released via _videoMetadataService.ReleasePlaybackFilePath
            // when playback ends — StopAndDisposePlayer does that.
            playbackPath = await _videoMetadataService.GetPlaybackFilePathAsync(video.Source);

            // StorageFile is the supported handle for CreateFromStorageFile
            // in MSIX packaged apps. GetFileFromPathAsync works for any path
            // the process can read (it has already read the same file via
            // FileStream in the gallery's thumbnail pipeline, so access is
            // guaranteed). A bare `new Uri(path)` / MediaSource.CreateFromUri
            // would be simpler but hits the file:// sandbox restrictions in
            // some packaged-app configurations.
            var file = await StorageFile.GetFileFromPathAsync(playbackPath);
            var source = MediaSource.CreateFromStorageFile(file);

            var player = new MediaPlayer();
            player.Source = source;
            // Order: set MediaPlayer first so the MediaPlayerElement binds
            // to the live instance, then flip IsVideoPlaying so the player
            // surface becomes visible, then call Play(). Calling Play()
            // before the element is visible still works (the player keeps
            // rendering into the surface even when collapsed) but feels
            // less responsive on slow first frames.
            MediaPlayer = player;
            _currentPlaybackPath = playbackPath;
            IsVideoPlaying = true;
            player.Play();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"ImageViewModel.PlayAsync error for {CurrentImage?.Id}: {ex.GetType().Name}: {ex.Message}");
            // Surface as a non-playing state so the user can retry. Don't
            // throw — the play overlay stays visible and CanPlay stays true.
            IsVideoPlaying = false;
            MediaPlayer = null;
            // If we successfully created a temp file before the
            // MediaPlayer construction failed, release it so we don't
            // leak the extract. If the failure was before
            // GetPlaybackFilePathAsync returned a path, playbackPath
            // is still null and the call below is a no-op.
            if (playbackPath != null)
            {
                _videoMetadataService.ReleasePlaybackFilePath(playbackPath);
                _currentPlaybackPath = null;
            }
        }
    }

    private bool CanPlay() => IsVideo && !IsVideoPlaying && _mediaPlayer == null;

    /// <summary>
    /// Pauses + closes the active MediaPlayer and clears the field so the
    /// view's MediaPlayerElement detaches. Idempotent (no-op when the
    /// player is already null). Called on every navigation boundary
    /// (Next, Previous, Close, Dispose) so we never leak a native
    /// player across item switches.
    /// </summary>
    private void StopAndDisposePlayer()
    {
        var oldPlayer = MediaPlayer;
        var oldPath = _currentPlaybackPath;
        MediaPlayer = null;
        _currentPlaybackPath = null;
        IsVideoPlaying = false;
        if (oldPlayer is null && oldPath is null) return;

        if (oldPlayer is not null)
        {
            try
            {
                oldPlayer.Pause();
                // Drop the MediaSource before Dispose so the source's underlying
                // file handle is released before the player tears down its
                // render pipeline. Order matters: Source = null on a still-
                // playing player can race with Dispose; pausing first makes the
                // release sequence deterministic.
                oldPlayer.Source = null;
                // The WinRT MediaPlayer implements IDisposable (not Close) in
                // the Windows.Media.Playback contract exposed to .NET — Dispose
                // walks the same teardown path as the UWP Close() sequence.
                oldPlayer.Dispose();
            }
            catch (Exception ex)
            {
                // Best-effort cleanup. A leaked native handle here is much
                // less bad than a crash in the disposal path.
                Trace.TraceError($"ImageViewModel.StopAndDisposePlayer error: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Release the temp file the service allocated for archive
        // playback. For loose files the path IS the user's real file
        // and ReleasePlaybackFilePath is a no-op on it. Always
        // unconditional: the VM may have been constructed before
        // _videoMetadataService was wired (e.g. during static init),
        // but the only path that goes through the dependency
        // injection is the constructor above, so by the time
        // playback ever happens _videoMetadataService is non-null.
        if (oldPath is not null)
        {
            try
            {
                _videoMetadataService.ReleasePlaybackFilePath(oldPath);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"ImageViewModel.StopAndDisposePlayer: release playback path error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    partial void OnDisplayImageChanged(BitmapImage? value)
    {
        DisplayImageChanged?.Invoke(this, EventArgs.Empty);
        if (value != null)
        {
            DisplayActualWidth = value.PixelWidth;
            DisplayActualHeight = value.PixelHeight;
        }
    }

    partial void OnCurrentImageChanged(MediaItem? value)
    {
        DisplayActualWidth = 0;
        DisplayActualHeight = 0;
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial int CurrentIndex { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    private int _displayIndex = 1;
    public int DisplayIndex
    {
        get => _displayIndex;
        private set => SetProperty(ref _displayIndex, value);
    }

    public ObservableCollection<MediaItem> Images => _galleryViewModel.Images;

    public bool CanLoadMoreImages => _galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore;
    public bool IsLoadingMoreImages => _galleryViewModel.IsLoadingMore;
    public Visibility LoadMoreImagesVisibility => _galleryViewModel.CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand(CanExecute = nameof(CanLoadMoreImages))]
    private async Task LoadMoreImagesAsync()
    {
        if (_galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore)
        {
            await _galleryViewModel.LoadMoreAsync();
        }
    }

    public ImageViewModel(GalleryViewModel galleryViewModel, IImageLoader imageLoader, IVideoMetadataService videoMetadataService, INavigationService navigationService)
    {
        _galleryViewModel = galleryViewModel;
        _imageLoader = imageLoader;
        _videoMetadataService = videoMetadataService;
        _navigationService = navigationService;

        // Named handlers (not lambdas) so Dispose can unsubscribe — avoids lambda
        // captures keeping this singleton alive past the App's lifetime.
        Images.CollectionChanged += OnImagesCollectionChanged;
        _galleryViewModel.PropertyChanged += OnGalleryPropertyChanged;
    }

    private void OnImagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        TotalCount = Images.Count;
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
    }

    // 监听 Gallery 的增量加载状态变化，以便单图模式下的 Next 按钮和 Load More 按钮能正确启用/显示
    private void OnGalleryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.CanLoadMore) ||
            e.PropertyName == nameof(GalleryViewModel.IsLoadingMore))
        {
            LoadMoreImagesCommand.NotifyCanExecuteChanged();
            NavigateNextCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanLoadMoreImages));
            OnPropertyChanged(nameof(IsLoadingMoreImages));
            OnPropertyChanged(nameof(LoadMoreImagesVisibility));
        }
    }

    partial void OnCurrentIndexChanged(int value)
    {
        DisplayIndex = value + 1;
        _galleryViewModel.LastViewedIndex = value;
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
        NavigationChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanNavigatePrevious() => Images.Count > 0 && CurrentIndex > 0;

    /// <summary>
    /// 在单图模式下支持“到底自动加载更多”：
    /// 当到达当前已加载图片的末尾，但 Gallery 还有更多图片时，Next 按钮仍然可用。
    /// </summary>
    private bool CanNavigateNext() => Images.Count > 0 && (CurrentIndex < Images.Count - 1 || _galleryViewModel.CanLoadMore);

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private async Task NavigatePreviousAsync()
    {
        if (CanNavigatePrevious())
        {
            // Stop any active video playback before switching items — the
            // new image's FullImage is the only thing we want decoded next.
            StopAndDisposePlayer();
            CurrentIndex--;
            await ShowCurrentImageAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NavigateNextAsync()
    {
        // Same navigation-boundary cleanup as NavigatePreviousAsync.
        StopAndDisposePlayer();
        if (CurrentIndex < Images.Count - 1)
        {
            // 正常前进
            CurrentIndex++;
            await ShowCurrentImageAsync();
        }
        else if (_galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore)
        {
            // 到达当前批次末尾（“到底”）→ 自动触发加载更多（单图模式下的 Load More）
            await _galleryViewModel.LoadMoreAsync();

            // 加载完成后，如果有新图片，则自动前进到下一张
            if (CurrentIndex < Images.Count - 1)
            {
                CurrentIndex++;
                await ShowCurrentImageAsync();
            }
        }
    }

    [RelayCommand]
    private void Close()
    {
        // Tear down the video player before the navigation pops the page,
        // otherwise the MediaPlayerElement is unloaded by the frame before
        // we get a chance to release the native handle.
        StopAndDisposePlayer();

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        CurrentImage = null;
        DisplayImage = null;

        _navigationService.GoBack();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (CurrentImage == null) return;

        var imageToDelete = CurrentImage;
        var indexToDelete = CurrentIndex;
        var wasLastImage = CurrentIndex >= Images.Count - 1;

        await _galleryViewModel.DeleteImageAsync(imageToDelete);

        if (Images.Count == 0)
        {
            Close();
            return;
        }

        var newIndex = wasLastImage ? Math.Max(0, indexToDelete - 1) : indexToDelete;
        newIndex = Math.Min(newIndex, Images.Count - 1);
        CurrentIndex = newIndex;
        if (Images.Count > 0 && newIndex >= 0 && newIndex < Images.Count)
        {
            CurrentImage = Images[newIndex];
        }
        else
        {
            Close();
            return;
        }
        TotalCount = Images.Count;
        DisplayImage = null;
        // After a delete the navigation target is fresh, so the previous
        // item's player (if any) was already torn down by the gallery
        // deleting the source MediaItem. Re-call for safety in case the
        // VM somehow outlived the index change.
        StopAndDisposePlayer();
        ResetLoadCts();
        await LoadFullImageAsync(CurrentImage, _loadCts!.Token);
    }

    public async Task ShowImageAsync(MediaItem item)
    {
        // Navigation boundary: tear down any active player from the
        // previous (gallery) item before binding the new one.
        StopAndDisposePlayer();
        ResetLoadCts();

        // Clear the previous image immediately so the user doesn't see a stale
        // bitmap while the new one is decoding. The ProgressRing overlay in
        // the view covers the brief blank state.
        DisplayImage = null;

        CurrentImage = item;
        CurrentIndex = Images.IndexOf(item);
        _galleryViewModel.LastViewedIndex = CurrentIndex;
        TotalCount = Images.Count;

        await LoadFullImageAsync(item, _loadCts!.Token);
    }

    private async Task LoadFullImageAsync(MediaItem item, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        if (item.FullImage != null)
        {
            // For VideoItem the gallery's thumbnail loader already
            // wired item.FullImage to the extracted first frame, so
            // the viewer shows that static first frame as the default
            // "full" view before the user clicks play. The play
            // overlay button is drawn on top by IsPlayOverlayVisibility.
            DisplayImage = item.FullImage;
            return;
        }

        IsLoading = true;
        try
        {
            using var stream = await _imageLoader.LoadImageStreamAsync(item.Source, ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (stream != null)
            {
                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                await bitmapImage.SetSourceAsync(stream.AsRandomAccessStream());
                item.FullImage = bitmapImage;
                DisplayImage = bitmapImage;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadFullImageAsync error for {item?.Id}: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ShowCurrentImageAsync()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Images.Count)
        {
            CurrentImage = Images[CurrentIndex];
            // Always rebuild cts here too: a stale token from a previously
            // cancelled load would either suppress this new load (if the
            // token is cancelled) or leak the old in-flight task (if not).
            ResetLoadCts();
            await LoadFullImageAsync(CurrentImage, _loadCts!.Token);
        }
    }

    /// <summary>
    /// Cancels any in-flight full-image load and swaps in a fresh cts. Callers
    /// must read <c>_loadCts.Token</c> immediately after (use null-forgiving),
    /// then pass it to <see cref="LoadFullImageAsync"/>. <see cref="Close"/>
    /// uses a different destroy-then-null pattern because it tears the VM
    /// down entirely.
    /// </summary>
    private void ResetLoadCts()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            // Last-chance native handle release. The MediaPlayer holds
            // an OS-level render pipeline; if the user closes the app
            // while a video is playing, this is what stops audio output
            // and releases the surface.
            StopAndDisposePlayer();

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;

            // Unsubscribe event handlers to break the reference cycle and let
            // the singleton be collected if the DI container is ever disposed.
            Images.CollectionChanged -= OnImagesCollectionChanged;
            _galleryViewModel.PropertyChanged -= OnGalleryPropertyChanged;
        }
        _disposed = true;
    }
}
