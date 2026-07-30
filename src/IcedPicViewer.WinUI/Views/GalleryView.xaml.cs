using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;

namespace IcedPicViewer.Views;

public sealed partial class GalleryView : Page, System.ComponentModel.INotifyPropertyChanged
{
    public GalleryViewModel ViewModel { get; }

    private readonly INavigationService _navigationService;

    private MediaItem? _selectedItemForDelete;
    private int _isNavigatingToViewer;
    private ViewerViewModel? _viewerViewModel;

    // 用于实现"滚动到底部自动加载更多"
    // 采用 debounce 机制避免快速滚动时频繁触发（符合性能要求）
    private DispatcherQueueTimer? _loadMoreDebounceTimer;
    private bool _isAutoLoadingMore;
    // Preload zone. 1000 px is wide enough that a 200-item page (~ one
    // viewport's worth on a 1080p window with default card width) finishes
    // loading BEFORE the user actually reaches the bottom — they see
    // continuous content instead of "scroll, wait, scroll, wait". 200 px
    // (the prior value) was the opposite trade-off: never preload, but
    // also never feel snappy. The viewport is 200-ish cards tall × ~16 px
    // average row spacing ≈ a few thousand px, so 1000 px covers ~25 %
    // of the viewport — a comfortable "early warning" without going so
    // far that the user hasn't started scrolling toward the bottom yet.
    private const double LoadMoreThreshold = 1000.0;
    private const int LoadMoreDebounceMs = 100;     // 滚动停止后延迟触发时间（毫秒）

    // MasonryPanel ref cached after the first layout pass. The panel
    // lives inside an ItemsPanelTemplate, so x:Name doesn't reach the
    // page's name scope; the existing FindMasonryPanel helper does a
    // visual tree walk. Caching the result avoids re-walking on every
    // SizeChanged tick during a window drag (the handler fires
    // continuously while the user resizes).
    private Controls.MasonryPanel? _masonryPanel;

    // Card width bounds. Below the floor the thumbnails become too
    // small to be useful (text labels and the ▶ overlay lose
    // legibility); above the ceiling the masonry layout degenerates
    // into a single column with very tall cards.
    private const double MinCardWidth = 110.0;
    private const double MaxCardWidth = 420.0;
    private const double ItemSpacing = 8.0;
    // Target cards-per-row at the most common desktop width (1280 px
    // content area) — gives ~250 px cards at that width, which is the
    // historical hand-tuned value. The function below maps the actual
    // content width to a card width that lands in roughly that range
    // across the full spectrum from 600 px to 4K.
    private const double TargetCardsPerRow = 4.0;

    /// <summary>
    /// PageUp/PageDown: jump the masonry <see cref="MainScrollViewer"/> by
    /// roughly one viewport (small overlap so the user keeps context).
    /// Called from MainWindow WH_KEYBOARD when the gallery page is active.
    /// </summary>
    public void ScrollByPage(bool down)
    {
        var sv = MainScrollViewer;
        var viewport = sv.ViewportHeight;
        if (viewport <= 0) return;

        var delta = viewport * 0.9;
        var maxOffset = Math.Max(0, sv.ExtentHeight - viewport);
        var target = down
            ? Math.Min(maxOffset, sv.VerticalOffset + delta)
            : Math.Max(0, sv.VerticalOffset - delta);
        sv.ChangeView(null, target, null, disableAnimation: false);
    }

    public GalleryView()
    {
        this.InitializeComponent();

        ViewModel = App.GetService<GalleryViewModel>();
        _navigationService = App.GetService<INavigationService>();
        DataContext = ViewModel;

        // 滚动到底部自动触发 LoadMore（带 debounce）。保留原“Load More”按钮作为手动后备。
        MainScrollViewer.ViewChanged += OnMainScrollViewerViewChanged;

        // Auto-size thumbnail cards to the viewport. The first
        // SizeChanged after the page is measured and laid out sets
        // the initial card width; subsequent ticks (during a window
        // drag) update it as the user resizes. The cost is one
        // property assignment per tick — the MasonryPanel invalidates
        // its measure, which is what we want.
        MainScrollViewer.SizeChanged += OnMainScrollViewerSizeChanged;

        var dq = DispatcherQueue.GetForCurrentThread();
        _loadMoreDebounceTimer = dq.CreateTimer();
        _loadMoreDebounceTimer.Interval = TimeSpan.FromMilliseconds(LoadMoreDebounceMs);
        _loadMoreDebounceTimer.IsRepeating = false;
        _loadMoreDebounceTimer.Tick += OnLoadMoreDebounceTimerTick;

        // Floating chrome: subscribe to MainWindow.IsFullscreen so the
        // header + status bar flip to hidden when the window enters
        // fullscreen, and pin back to visible when it leaves. The
        // hover-to-reveal logic in RootGrid_PointerMoved owns the
        // moment-to-moment visibility inside fullscreen — this is
        // only the entry/exit transition.
        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged += OnMainWindowPropertyChanged;
            // Sync initial state. The PropertyChanged subscription
            // above only fires when IsFullscreen CHANGES, not when the
            // window is already fullscreen at subscribe time. The
            // common case this covers: user enters fullscreen in
            // ViewerView, presses Esc / Close → navigates back to
            // gallery, the window is still in FullScreen presenter,
            // but the freshly-loaded GalleryView would otherwise see
            // IsHeaderVisible=true (its ctor default) and render the
            // chrome visible — defeating the whole "chrome is hidden
            // in fullscreen" contract. Forcing the sync handler to run
            // once with a synthetic IsFullscreen arg puts the page in
            // the correct state on first paint, no race window.
            OnMainWindowPropertyChanged(
                App.MainWindow,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindow.IsFullscreen)));
        }

        Unloaded += OnGalleryViewUnloaded;
    }

    // ----------------------------------------------------------------
    // Floating chrome (header + status bar).
    //
    // The chrome Grids live as overlays in the root grid
    // (RowSpan="3" + VerticalAlignment="Top" / "Bottom") so they
    // never participate in layout — the masonry ScrollViewer fills
    // the entire page, and the chrome floats above it. Showing or
    // hiding the chrome therefore does NOT resize the content area;
    // the user's thumbnail layout stays exactly where it was.
    //
    // Outside fullscreen the chrome is always Visible. Inside fullscreen:
    // large top/bottom edge hot-zones reveal header/status; leave-edge
    // schedules a short delayed hide (media-player style).
    //
    // HeaderVisibility / StatusVisibility derive from both
    // IsFullscreenStatic() (windowed = always Visible) and the
    // per-bar IsHeaderVisible / IsStatusVisible flag. The XAML
    // OneWay bind to the chrome Grid's Visibility is what actually
    // shows / hides the overlay.
    // ----------------------------------------------------------------

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind requires instance member; the body reads only static-friendly inputs but the binding surface is the instance form.")]
    public Visibility HeaderVisibility => IsFullscreenStatic()
        ? (IsHeaderVisible ? Visibility.Visible : Visibility.Collapsed)
        : Visibility.Visible;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind requires instance member; the body reads only static-friendly inputs but the binding surface is the instance form.")]
    public Visibility StatusVisibility => IsFullscreenStatic()
        ? (IsStatusVisible ? Visibility.Visible : Visibility.Collapsed)
        : Visibility.Visible;

    private bool _isHeaderVisible = true;
    public bool IsHeaderVisible
    {
        get => _isHeaderVisible;
        set
        {
            if (_isHeaderVisible == value) return;
            _isHeaderVisible = value;
            OnPropertyChanged(nameof(IsHeaderVisible));
            // HeaderVisibility derives from this + IsFullscreenStatic();
            // x:Bind on Visibility needs the explicit notification to
            // re-evaluate. Setters never raise PropertyChanged for
            // computed properties on their own.
            OnPropertyChanged(nameof(HeaderVisibility));
        }
    }

    private bool _isStatusVisible = true;
    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        set
        {
            if (_isStatusVisible == value) return;
            _isStatusVisible = value;
            OnPropertyChanged(nameof(IsStatusVisible));
            OnPropertyChanged(nameof(StatusVisibility));
        }
    }

    // Mainstream edge hot-zones (DIP / height fraction). Larger than the
    // chrome so "nudge to edge" works; expand further while a bar is open
    // so moving across buttons does not collapse it.
    private const double ChromeTopZoneMinPx = 96;
    private const double ChromeBottomZoneMinPx = 72;
    private const double ChromeTopZoneHeightFraction = 0.12;
    private const double ChromeBottomZoneHeightFraction = 0.08;
    private const int ChromeHideDelayMs = 900;

    private bool _pointerInTopHotZone;
    private bool _pointerInBottomHotZone;
    private DispatcherQueueTimer? _chromeHideTimer;

    private static bool IsFullscreenStatic() => App.MainWindow?.IsFullscreen ?? false;

    private void OnMainWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindow.IsFullscreen))
        {
            var fs = IsFullscreenStatic();
            if (fs)
            {
                // Brief show then auto-hide (cue that edges work).
                _pointerInTopHotZone = false;
                _pointerInBottomHotZone = false;
                IsHeaderVisible = true;
                IsStatusVisible = true;
                StopChromeHideTimer();
                ScheduleChromeHide();
            }
            else
            {
                StopChromeHideTimer();
                _pointerInTopHotZone = false;
                _pointerInBottomHotZone = false;
                IsHeaderVisible = true;
                IsStatusVisible = true;
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (_viewerViewModel != null)
        {
            _viewerViewModel.NavigationChanged -= OnViewerNavigationChanged;
            _viewerViewModel = null;
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

        // SizeChanged might not fire on the first navigation if the
        // window keeps its previous size (e.g., the user navigated
        // back to the gallery after a viewer roundtrip and the
        // window hasn't been resized). Force an initial sizing pass
        // here so the cards always reflect the actual viewport width
        // — otherwise the cached value from the previous sizing would
        // be used and the cards could be too wide / narrow after a
        // window resize that happened in another view.
        ApplyThumbnailCardWidth(MainScrollViewer.ActualWidth);
    }

    private void OnMainScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyThumbnailCardWidth(e.NewSize.Width);
    }

    /// <summary>
    /// Fullscreen edge hot-zones (Photos / media-player style).
    /// Top band → header; bottom band → status. Leaving both schedules
    /// a single delayed hide (not re-armed by mid-screen scroll moves).
    /// </summary>
    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsFullscreenStatic())
            return;

        var pos = e.GetCurrentPoint(RootGrid).Position;
        var height = RootGrid.ActualHeight;
        if (height <= 0) return;

        var topZone = Math.Max(ChromeTopZoneMinPx, height * ChromeTopZoneHeightFraction);
        if (IsHeaderVisible)
            topZone = Math.Max(topZone, HeaderChrome.ActualHeight > 0 ? HeaderChrome.ActualHeight + 40 : 120);

        var bottomZone = Math.Max(ChromeBottomZoneMinPx, height * ChromeBottomZoneHeightFraction);
        if (IsStatusVisible)
            bottomZone = Math.Max(bottomZone, StatusChrome.ActualHeight > 0 ? StatusChrome.ActualHeight + 32 : 96);

        var inTop = pos.Y <= topZone;
        var inBottom = pos.Y >= height - bottomZone;

        if (inTop || inBottom)
        {
            StopChromeHideTimer();
            _pointerInTopHotZone = inTop;
            _pointerInBottomHotZone = inBottom;
            if (inTop) IsHeaderVisible = true;
            if (inBottom) IsStatusVisible = true;
            // Independent: leaving one edge while staying on the other
            // should hide only the unused bar after delay — when only
            // one edge is active, clear the other immediately.
            if (!inTop) IsHeaderVisible = false;
            if (!inBottom) IsStatusVisible = false;
            return;
        }

        // Completely outside both edges — arm hide once, not on every scroll move.
        var wasInEdge = _pointerInTopHotZone || _pointerInBottomHotZone;
        _pointerInTopHotZone = false;
        _pointerInBottomHotZone = false;
        if (wasInEdge)
            ScheduleChromeHide();
        else if ((IsHeaderVisible || IsStatusVisible) && _chromeHideTimer is null)
            ScheduleChromeHide();
    }

    private void ScheduleChromeHide()
    {
        if (!IsFullscreenStatic()) return;
        if (_chromeHideTimer is not null) return;
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(ChromeHideDelayMs);
        t.Tick += OnChromeHideTick;
        _chromeHideTimer = t;
        t.Start();
    }

    private void OnChromeHideTick(DispatcherQueueTimer sender, object args)
    {
        StopChromeHideTimer();
        if (!IsFullscreenStatic()) return;
        if (_pointerInTopHotZone || _pointerInBottomHotZone) return;
        IsHeaderVisible = false;
        IsStatusVisible = false;
    }

    private void StopChromeHideTimer()
    {
        if (_chromeHideTimer is null) return;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Tick -= OnChromeHideTick;
        _chromeHideTimer = null;
    }

    private void ApplyThumbnailCardWidth(double availableWidth)
    {
        if (availableWidth <= 0) return;

        // Lazy-resolve the MasonryPanel. The first call walks the
        // visual tree (the panel is inside an ItemsPanelTemplate and
        // isn't reachable via x:Name); later calls hit the cache.
        _masonryPanel ??= FindMasonryPanel(MainScrollViewer);
        if (_masonryPanel == null) return;

        _masonryPanel.ItemWidth = ComputeCardWidth(availableWidth);
    }

    /// <summary>
    /// Map the available ScrollViewer width to a card width that
    /// keeps the visual density roughly constant across viewport
    /// sizes — the user sees "about 4-5 cards per row" whether the
    /// window is 800 px or 2560 px wide, instead of "1 card per row"
    /// on a 4K display or "20 cards per row" on a phone-sized window.
    /// The formula: pick an integer cards-per-row count, then back-
    /// compute the per-card width as (available - one spacing) / N
    /// minus the per-card right spacing, clamped to the min/max
    /// bounds. The result is a smooth-ish piecewise linear function
    /// (the rounding to int cards-per-row is the only discontinuity)
    /// that lands inside the visually-sensible card-width range.
    /// </summary>
    private static double ComputeCardWidth(double availableWidth)
    {
        // 1 card minimum (very narrow viewports — the bounds below
        // will still let it render at MinCardWidth), 10 cards maximum
        // (wide 4K-ish windows). The (double) cast on round() is a
        // no-op but makes the conversion to double explicit.
        var cardsPerRow = (double)Math.Round(availableWidth / (MinCardWidth + ItemSpacing * 2));
        cardsPerRow = Math.Clamp(cardsPerRow, 1, 10);

        // Per-card width = (total width - one inter-card gap at the
        // row's right edge) / cards per row, minus the gap on the
        // card's own right edge. The MasonryPanel itself adds the
        // inter-card gaps via ItemSpacing, so we only need to leave
        // room for them here.
        var rawWidth = (availableWidth - ItemSpacing) / cardsPerRow - ItemSpacing;
        return Math.Clamp(rawWidth, MinCardWidth, MaxCardWidth);
    }

    /// <summary>
    /// Walk the visual tree to find the MasonryPanel inside the
    /// ItemsControl's ItemsPanelTemplate. The panel doesn't get a
    /// page-level x:Name because it lives inside a templated subtree;
    /// callers either use this helper or rely on the cached
    /// <see cref="_masonryPanel"/> field. Caching the result on the
    /// hot path (SizeChanged) avoids re-walking the tree on every
    /// window-resize tick.
    /// </summary>
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
        if (sender is FrameworkElement element && element.DataContext is MediaItem item)
        {
            OpenViewer(item);
        }
    }

    private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MediaItem item)
        {
            _selectedItemForDelete = item;
        }
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var item = _selectedItemForDelete;
        _selectedItemForDelete = null;

        await ViewModel.DeleteItemAsync(item);
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var source = _selectedItemForDelete.Media;
        // For archive entries we can't select the entry itself (Explorer
        // doesn't understand zip/rar/7z contents), so highlight the
        // containing archive file instead. The user immediately sees
        // which .zip / .rar / .7z the image is inside, which is the
        // actionable "where is this file" answer.
        var filePath = source.Path;
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

    private async void OpenViewer(MediaItem item)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isNavigatingToViewer, 1, 0) != 0) return;

        try
        {
            var index = ViewModel.Items.IndexOf(item);
            ViewModel.LastViewedIndex = index;

            var panel = FindMasonryPanel(MainScrollViewer);
            if (panel != null)
            {
                ViewModel.LastViewedYOffset = panel.GetItemYPosition(index);
            }

            // ViewerViewModel is a Singleton — only subscribe the first time. The
            // OnNavigatedTo unsubscribe paired with this guard keeps it 1:1.
            if (_viewerViewModel == null)
            {
                _viewerViewModel = App.GetService<ViewerViewModel>();
                _viewerViewModel.NavigationChanged += OnViewerNavigationChanged;
            }
            await _viewerViewModel.OpenItem(item);

            _navigationService.NavigateTo<ViewerView>();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isNavigatingToViewer, 0);
        }
    }

    private void OnViewerNavigationChanged(object? sender, EventArgs e)
    {
        if (_viewerViewModel == null) return;

        var panel = FindMasonryPanel(MainScrollViewer);
        if (panel != null)
        {
            ViewModel.LastViewedYOffset = panel.GetItemYPosition(_viewerViewModel.CurrentIndex);
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
        // RootFrame 在本项目里没有配置 CacheSize，所以页面不会被 Frame 缓存复用
        // —— 一旦 Unloaded 触发就应该彻底解订，避免 GalleryView 自身被 Frame
        // 通过 Unloaded 事件保持强引用，连带泄漏 ViewModel 和 DispatcherQueueTimer。
        MainScrollViewer.ViewChanged -= OnMainScrollViewerViewChanged;

        if (_loadMoreDebounceTimer != null)
        {
            _loadMoreDebounceTimer.Tick -= OnLoadMoreDebounceTimerTick;
            _loadMoreDebounceTimer.Stop();
            _loadMoreDebounceTimer = null;
        }

        StopChromeHideTimer();

        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        }

        Unloaded -= OnGalleryViewUnloaded;
    }

    /// <summary>
    /// Top-bar About button. Navigates to <see cref="AboutPage"/>, which
    /// surfaces the FFmpeg attribution + LGPL license link (LGPL 2.1+
    /// requires the user be able to find both). Frame's GoBack returns
    /// the user to the gallery.
    /// </summary>
    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        _navigationService.NavigateTo<AboutPage>();
    }

    /// <summary>
    /// Top-bar Slideshow button. Opens the viewer at the last-viewed
    /// item (or the first if none yet) and asks the singleton
    /// <see cref="ViewerViewModel"/> to start the auto-advance timer.
    /// The viewer's own Slideshow button (in the CommandBar) is the
    /// toggle that stops it — same state, two surfaces.
    /// </summary>
    private async void SlideshowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Items.Count == 0) return;

        var viewerViewModel = App.GetService<ViewerViewModel>();
        var startIndex = ViewModel.LastViewedIndex >= 0 && ViewModel.LastViewedIndex < ViewModel.Items.Count
            ? ViewModel.LastViewedIndex
            : 0;
        var startItem = ViewModel.Items[startIndex];
        await viewerViewModel.OpenItem(startItem);
        // StartSlideshow is parameterless now — the viewer's own
        // slider writes to ViewerViewModel.SlideshowInterval directly,
        // and the gallery's value is the initial seed (before the
        // user has a chance to touch the viewer's slider).
        viewerViewModel.SlideshowInterval = ViewModel.SlideshowInterval;
        viewerViewModel.StartSlideshow();
        _navigationService.NavigateTo<ViewerView>();
    }
}
