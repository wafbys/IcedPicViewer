using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using IcedPicViewer;

namespace IcedPicViewer.ViewModels;

public partial class ImageViewModel : ObservableObject
{
    private readonly GalleryViewModel _galleryViewModel;
    private readonly IImageLoader _imageLoader;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _loadCts;

    [ObservableProperty]
    private ImageItem? _currentImage;

    [ObservableProperty]
    private BitmapImage? _displayImage;

    public event EventHandler? DisplayImageChanged;

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

    public ImageViewModel(GalleryViewModel galleryViewModel, IImageLoader imageLoader)
    {
        _galleryViewModel = galleryViewModel;
        _imageLoader = imageLoader;
        Images.CollectionChanged += (_, _) => TotalCount = Images.Count;
    }

    partial void OnCurrentIndexChanged(int value)
    {
        DisplayIndex = value + 1;
    }

    [RelayCommand]
    private void NavigatePrevious()
    {
        if (CurrentIndex > 0)
        {
            CurrentIndex--;
            ShowCurrentImage();
        }
    }

    [RelayCommand]
    private void NavigateNext()
    {
        if (CurrentIndex < Images.Count - 1)
        {
            CurrentIndex++;
            ShowCurrentImage();
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

        var frame = FindFrame();
        if (frame != null && frame.CanGoBack)
        {
            frame.GoBack();
        }
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
        CurrentImage = Images[newIndex];
        TotalCount = Images.Count;
        DisplayImage = null;
        await LoadFullImageAsync(CurrentImage, _loadCts?.Token ?? CancellationToken.None);
    }

    private static Frame? FindFrame()
    {
        var window = App.MainWindow;
        if (window?.Content is Frame frame)
            return frame;

        if (window?.Content is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is Frame f)
                    return f;
            }
        }

        return null;
    }

    public async void ShowImage(ImageItem item)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();

        CurrentImage = item;
        CurrentIndex = Images.IndexOf(item);
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

    private async void ShowCurrentImage()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Images.Count)
        {
            CurrentImage = Images[CurrentIndex];
            ZoomLevel = 1.0;
            await LoadFullImageAsync(CurrentImage, _loadCts?.Token ?? CancellationToken.None);
        }
    }
}
