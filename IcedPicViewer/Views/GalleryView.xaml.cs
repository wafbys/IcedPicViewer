using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;

namespace IcedPicViewer.Views;

public sealed partial class GalleryView : Page
{
    public GalleryViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;

    private ImageItem? _selectedItemForDelete;
    private int _isNavigatingToViewer;
    private ImageViewModel? _currentImageViewModel;

    // 用于实现“滚动到底部自动加载更多”
    // 采用 debounce 机制避免快速滚动时频繁触发（符合性能要求）
    private DispatcherQueueTimer? _loadMoreDebounceTimer;
    private bool _isAutoLoadingMore;
    private const double LoadMoreThreshold = 200.0; // 距离底部多少像素触发自动加载
    private const int LoadMoreDebounceMs = 180;     // 滚动停止后延迟触发时间（毫秒）

    public GalleryView()
    {
        this.InitializeComponent();

        ViewModel = App.GetService<GalleryViewModel>();
        _navigationService = App.GetService<INavigationService>();
        DataContext = ViewModel;

        // 滚动到底部自动触发 LoadMore（带 debounce）。保留原“Load More”按钮作为手动后备。
        MainScrollViewer.ViewChanged += OnMainScrollViewerViewChanged;

        var dq = DispatcherQueue.GetForCurrentThread();
        _loadMoreDebounceTimer = dq.CreateTimer();
        _loadMoreDebounceTimer.Interval = TimeSpan.FromMilliseconds(LoadMoreDebounceMs);
        _loadMoreDebounceTimer.IsRepeating = false;
        _loadMoreDebounceTimer.Tick += OnLoadMoreDebounceTimerTick;

        Unloaded += OnGalleryViewUnloaded;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_currentImageViewModel != null)
        {
            _currentImageViewModel.NavigationChanged -= OnImageViewModelNavigationChanged;
            _currentImageViewModel = null;
        }

        var offset = ViewModel.LastViewedYOffset;
        if (offset > 0)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                MainScrollViewer.UpdateLayout();
                MainScrollViewer.ChangeView(null, offset, null, true);
            });
        }
    }

    private static Controls.MasonryPanel? FindMasonryPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Controls.MasonryPanel panel)
                return panel;
            var result = FindMasonryPanel(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ImageItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageItem item)
        {
            OpenImageViewer(item);
        }
    }

    private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageItem item)
        {
            _selectedItemForDelete = item;
        }
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var item = _selectedItemForDelete;
        _selectedItemForDelete = null;

        await ViewModel.DeleteImageAsync(item);
    }

    private void ImageItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.FindName("NameOverlay") is FrameworkElement overlay)
        {
            overlay.Visibility = Visibility.Visible;
        }
    }

    private void ImageItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.FindName("NameOverlay") is FrameworkElement overlay)
        {
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var filePath = _selectedItemForDelete.Path;
        if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(startInfo);
        }
    }

    private async void OpenImageViewer(ImageItem item)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isNavigatingToViewer, 1, 0) != 0) return;

        try
        {
            var index = ViewModel.Images.IndexOf(item);
            ViewModel.LastViewedIndex = index;

            var panel = FindMasonryPanel(MainScrollViewer);
            if (panel != null)
            {
                ViewModel.LastViewedYOffset = panel.GetItemYPosition(index);
            }

            _currentImageViewModel = App.GetService<ImageViewModel>();
            _currentImageViewModel.NavigationChanged += OnImageViewModelNavigationChanged;
            await _currentImageViewModel.ShowImageAsync(item);

            _navigationService.NavigateTo<ImageViewerView>();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isNavigatingToViewer, 0);
        }
    }

    private void OnImageViewModelNavigationChanged(object? sender, EventArgs e)
    {
        if (_currentImageViewModel == null) return;

        var panel = FindMasonryPanel(MainScrollViewer);
        if (panel != null)
        {
            ViewModel.LastViewedYOffset = panel.GetItemYPosition(_currentImageViewModel.CurrentIndex);
        }
    }

    private void OnMainScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!ViewModel.CanLoadMore || ViewModel.IsLoadingMore || _isAutoLoadingMore)
            return;

        var sv = MainScrollViewer;
        if (sv.ExtentHeight <= 0 || sv.ViewportHeight <= 0)
            return;

        // 距离底部在阈值内时，启动一次性 debounce 定时器
        // 只有用户停止滚动一段时间后才真正执行加载，避免性能问题
        double distanceFromBottom = sv.ExtentHeight - (sv.VerticalOffset + sv.ViewportHeight);
        if (distanceFromBottom <= LoadMoreThreshold)
        {
            _loadMoreDebounceTimer?.Start();
        }
    }

    private async void OnLoadMoreDebounceTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (!ViewModel.CanLoadMore || ViewModel.IsLoadingMore || _isAutoLoadingMore)
            return;

        _isAutoLoadingMore = true;
        try
        {
            await ViewModel.LoadMoreAsync();
        }
        finally
        {
            _isAutoLoadingMore = false;
        }
    }

    /// <summary>
    /// 页面卸载时清理事件订阅和定时器，避免内存泄漏。
    /// </summary>
    private void OnGalleryViewUnloaded(object sender, RoutedEventArgs e)
    {
        MainScrollViewer.ViewChanged -= OnMainScrollViewerViewChanged;

        if (_loadMoreDebounceTimer != null)
        {
            _loadMoreDebounceTimer.Tick -= OnLoadMoreDebounceTimerTick;
            _loadMoreDebounceTimer.Stop();
            _loadMoreDebounceTimer = null;
        }

        Unloaded -= OnGalleryViewUnloaded;
    }
}
