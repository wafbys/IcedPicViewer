// Copyright (c) IcedPicViewer. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;

namespace IcedPicViewer.ViewModels;

public partial class ImageViewModel : ObservableObject
{
    private readonly GalleryViewModel _galleryViewModel;
    private readonly IImageLoader _imageLoader;
    private readonly INavigationService _navigationService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private ImageItem? _currentImage;

    [ObservableProperty]
    private BitmapImage? _displayImage;

    public event EventHandler? DisplayImageChanged;
    public event EventHandler? NavigationChanged;

    private int _actualWidth;
    public int ActualWidth => _actualWidth > 0 ? _actualWidth : CurrentImage?.OriginalWidth ?? 0;

    private int _actualHeight;
    public int ActualHeight => _actualHeight > 0 ? _actualHeight : CurrentImage?.OriginalHeight ?? 0;

    public string ImagePath => CurrentImage?.Path ?? string.Empty;

    partial void OnDisplayImageChanged(BitmapImage? value)
    {
        DisplayImageChanged?.Invoke(this, EventArgs.Empty);
        if (value != null)
        {
            _actualWidth = value.PixelWidth;
            _actualHeight = value.PixelHeight;
            OnPropertyChanged(nameof(ActualWidth));
            OnPropertyChanged(nameof(ActualHeight));
        }
    }

    partial void OnCurrentImageChanged(ImageItem? value)
    {
        _actualWidth = 0;
        _actualHeight = 0;
        OnPropertyChanged(nameof(ActualWidth));
        OnPropertyChanged(nameof(ActualHeight));
        OnPropertyChanged(nameof(ImagePath));
    }

    [ObservableProperty]
    private double _zoomLevel = 1.0;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _currentIndex;

    [ObservableProperty]
    private int _totalCount;

    private int _displayIndex = 1;
    public int DisplayIndex
    {
        get => _displayIndex;
        private set => SetProperty(ref _displayIndex, value);
    }

    public ObservableCollection<ImageItem> Images => _galleryViewModel.Images;

    public bool CanLoadMoreImages => _galleryViewModel.CanLoadMore;
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

        Images.CollectionChanged += (_, _) =>
        {
            TotalCount = Images.Count;
            NavigatePreviousCommand.NotifyCanExecuteChanged();
            NavigateNextCommand.NotifyCanExecuteChanged();
        };

        // 监听 Gallery 的增量加载状态变化，以便单图模式下的 Next 按钮和 Load More 按钮能正确启用/显示
        _galleryViewModel.PropertyChanged += (s, e) =>
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
        };
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
        await LoadFullImageAsync(CurrentImage, _loadCts?.Token ?? CancellationToken.None);
    }

    public async Task ShowImageAsync(ImageItem item)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        CurrentImage = item;
        CurrentIndex = Images.IndexOf(item);
        _galleryViewModel.LastViewedIndex = CurrentIndex;
        TotalCount = Images.Count;
        ZoomLevel = 1.0;

        await LoadFullImageAsync(item, _loadCts.Token);
    }

    private async Task LoadFullImageAsync(ImageItem item, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        if (item.FullImage != null)
        {
            DisplayImage = item.FullImage;
            return;
        }

        IsLoading = true;
        try
        {
            byte[]? data = await _imageLoader.LoadImageAsync(item.Path, ct);

            if (ct.IsCancellationRequested) return;

            if (data != null)
            {
                var bitmapImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                using var stream = new MemoryStream(data);
                bitmapImage.SetSource(stream.AsRandomAccessStream());
                item.FullImage = bitmapImage;
                DisplayImage = bitmapImage;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
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
            ZoomLevel = 1.0;
            await LoadFullImageAsync(CurrentImage, _loadCts?.Token ?? CancellationToken.None);
        }
    }
}
