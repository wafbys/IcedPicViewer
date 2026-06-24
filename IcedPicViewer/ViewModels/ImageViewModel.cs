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

namespace IcedPicViewer.ViewModels;

public partial class ImageViewModel : ObservableObject, IDisposable
{
    private readonly GalleryViewModel _galleryViewModel;
    private readonly IImageLoader _imageLoader;
    private readonly INavigationService _navigationService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualWidth))]
    [NotifyPropertyChangedFor(nameof(ActualHeight))]
    [NotifyPropertyChangedFor(nameof(ImagePath))]
    [NotifyPropertyChangedFor(nameof(IsVideo))]
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
    /// chrome (the future &gt; button + play / pause affordance).
    /// The current viewer still shows the static first frame
    /// (<see cref="DisplayImage"/> = item.FullImage = item.Thumbnail
    /// for videos), so a true here only affects what the chrome draws
    /// on top, not what the Image element shows.
    /// </summary>
    public bool IsVideo => CurrentImage?.IsVideo ?? false;

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

    public ImageViewModel(GalleryViewModel galleryViewModel, IImageLoader imageLoader, INavigationService navigationService)
    {
        _galleryViewModel = galleryViewModel;
        _imageLoader = imageLoader;
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
            CurrentIndex--;
            await ShowCurrentImageAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NavigateNextAsync()
    {
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
        ResetLoadCts();
        await LoadFullImageAsync(CurrentImage, _loadCts!.Token);
    }

    public async Task ShowImageAsync(MediaItem item)
    {
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
            // the viewer shows that static first frame as the "full"
            // image. The future MediaPlayerElement session will
            // replace this path with a real player surface.
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
