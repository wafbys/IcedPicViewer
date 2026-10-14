using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

        // The status bar flashes on every StatusText change (scan
        // progress, delete feedback, …). StatusText is a VM property, so
        // the subscription must be torn down in OnGalleryViewUnloaded —
        // the VM is a singleton and would otherwise keep dead pages alive
        // across navigations.
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

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
    // Header: auto-hides in BOTH presenters now — windowed mode used to
    // pin it over the top of the masonry. The top edge hot-zone reveals
    // it; leaving the zone schedules the delayed hide. The first gallery
    // entry of a session dwells longer (InitialChromeDwellMs) so the
    // toolbar has a chance to be discovered; later entries (including
    // coming back from the viewer) use the normal countdown. Hiding is
    // suppressed while the user is working inside the header (combo
    // open, suggestions open, keyboard focus inside — see
    // IsChromeInteractionActive).
    //
    // Status bar: windowed mode shows it only as a short "flash" after
    // the status text changes (StatusFlashDwellMs) — live progress
    // re-arms the flash on every update, so it stays up while a scan /
    // backfill is actually running, and delete feedback stays readable.
    // Fullscreen keeps the bottom hot-zone behaviour (reveal near the
    // bottom edge, hide with the rest of the chrome).
    // ----------------------------------------------------------------

    /// <summary>
    /// Bound by the header chrome's Visibility. The auto-hide contract
    /// applies in both presenters now; the status bar keeps its windowed
    /// exception (see the region comment above).
    /// </summary>
    public Visibility HeaderVisibility =>
        IsHeaderVisible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Bound by the status bar's Visibility. Visibility is driven by the
    /// flash timer in windowed mode and by the bottom hot-zone in
    /// fullscreen; the value is always the raw <see cref="IsStatusVisible"/>
    /// flag either way.
    /// </summary>
    public Visibility StatusVisibility =>
        IsStatusVisible ? Visibility.Visible : Visibility.Collapsed;

    private bool _isHeaderVisible = true;
    public bool IsHeaderVisible
    {
        get => _isHeaderVisible;
        set
        {
            if (_isHeaderVisible == value) return;
            _isHeaderVisible = value;
            OnPropertyChanged(nameof(IsHeaderVisible));
            // HeaderVisibility derives from this flag; x:Bind on
            // Visibility needs the explicit notification to re-evaluate.
            // Setters never raise PropertyChanged for computed properties
            // on their own.
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
    private const int ChromeHideDelayMs = 2000;

    /// <summary>
    /// The first gallery entry of a session keeps the header up this long
    /// before the normal auto-hide rhythm starts, so the toolbar is
    /// discoverable on a cold start. One-shot per process: every later
    /// visit (including coming back from the viewer) uses
    /// <see cref="ChromeHideDelayMs"/>.
    /// </summary>
    private const int InitialChromeDwellMs = 5000;
    private static bool s_initialChromeDwellPending = true;

    private static int TakeInitialDwellDelay()
    {
        if (!s_initialChromeDwellPending) return ChromeHideDelayMs;
        s_initialChromeDwellPending = false;
        return InitialChromeDwellMs;
    }

    /// <summary>
    /// How long the status bar stays visible after its text changed.
    /// Windowed mode is collapsed otherwise; fullscreen uses the same
    /// dwell after a reveal-by-change. Long enough to read a delete
    /// confirmation, short enough to stay out of the way.
    /// </summary>
    private const int StatusFlashDwellMs = 4000;

    private bool _pointerInTopHotZone;
    private bool _pointerInBottomHotZone;
    private DispatcherQueueTimer? _chromeHideTimer;
    private DispatcherQueueTimer? _statusHideTimer;

    private static bool IsFullscreenStatic() => App.MainWindow?.IsFullscreen ?? false;

    private void OnMainWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindow.IsFullscreen))
        {
            // Both presenters now auto-hide the header, so every path
            // (the initial sync from the ctor, entering fullscreen,
            // leaving fullscreen) behaves the same: show the chrome,
            // then let the countdown collapse it again. Leaving
            // fullscreen used to pin the chrome visible; without this
            // the header would stay open after a fullscreen roundtrip.
            _pointerInTopHotZone = false;
            _pointerInBottomHotZone = false;
            IsHeaderVisible = true;
            StopChromeHideTimer();
            ScheduleChromeHide(TakeInitialDwellDelay());

            if (IsFullscreenStatic())
            {
                // Fullscreen: brief status cue on entry (same as before),
                // then the flash timer hides it again.
                FlashStatusBar();
            }
            else
            {
                // Windowed: the status bar is collapsed until its text
                // next changes — no permanent bar at the bottom edge.
                StopStatusHideTimer();
                IsStatusVisible = false;
            }
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // SizeChanged might not fire on the first navigation if the
        // window keeps its previous size (e.g., returning to the gallery
        // after a viewer roundtrip and the window hasn't been resized).
        // Force an initial sizing pass here so the cards always reflect
        // the actual viewport width.
        ApplyThumbnailCardWidth(MainScrollViewer.ActualWidth);

        // Restore scroll so the item the viewer was showing is visible. The
        // masonry positions are only valid after a layout pass, and a saved Y
        // is often stale (0) because the viewer opened before the gallery had
        // arranged the newly loaded page — so recompute from the index.
        var index = ViewModel.LastViewedIndex;
        DispatcherQueue.TryEnqueue(() =>
        {
            MainScrollViewer.UpdateLayout();
            ScrollToGalleryItem(index);
        });
    }

    /// <summary>
    /// Scrolls the masonry so the item at <paramref name="index"/> is visible
    /// (roughly a third of the way down the viewport for context). No-op when
    /// the index is out of range or the panel is not laid out yet.
    /// </summary>
    private void ScrollToGalleryItem(int index)
    {
        if (index < 0 || index >= ViewModel.Items.Count) return;

        var panel = _masonryPanel ?? FindMasonryPanel(MainScrollViewer);
        if (panel is null) return;

        var y = panel.GetItemYPosition(index);
        var viewport = MainScrollViewer.ViewportHeight;
        var target = Math.Max(0, y - viewport * 0.33);
        MainScrollViewer.ChangeView(null, target, null, disableAnimation: true);
    }

    private void OnMainScrollViewerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyThumbnailCardWidth(e.NewSize.Width);
    }

    /// <summary>
    /// Chrome edge hot-zones (Photos / media-player style).
    /// Top band → header (both presenters); bottom band → status
    /// (fullscreen only — the status bar is pinned visible in windowed
    /// mode). Leaving the edges schedules a single delayed hide (not
    /// re-armed by mid-screen scroll moves).
    /// </summary>
    private void RootGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        => HandleChromePointer(e);

    // Touch and pen taps produce PointerPressed but often no PointerMoved
    // frames, so a contact inside the top zone must count as a reveal
    // gesture too — otherwise touch users could never bring the
    // auto-hidden header back (there is no hover on touch).
    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        => HandleChromePointer(e);

    // Keyboard-only users have no pointer to wave at the top edge, and
    // the header owns the gallery's primary actions (open folder /
    // refresh). Tab is the "I want to reach the controls" gesture, so it
    // reveals the header; focus then lands inside it, which keeps it up
    // through IsChromeInteractionActive. The key itself is not handled —
    // focus navigation proceeds normally.
    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Tab) return;
        StopChromeHideTimer();
        IsHeaderVisible = true;
    }

    private void HandleChromePointer(PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(RootGrid).Position;
        var height = RootGrid.ActualHeight;
        if (height <= 0) return;

        var fullscreen = IsFullscreenStatic();

        var topZone = Math.Max(ChromeTopZoneMinPx, height * ChromeTopZoneHeightFraction);
        if (IsHeaderVisible)
            topZone = Math.Max(topZone, HeaderChrome.ActualHeight > 0 ? HeaderChrome.ActualHeight + 40 : 120);

        var bottomZone = Math.Max(ChromeBottomZoneMinPx, height * ChromeBottomZoneHeightFraction);
        if (IsStatusVisible)
            bottomZone = Math.Max(bottomZone, StatusChrome.ActualHeight > 0 ? StatusChrome.ActualHeight + 32 : 96);

        var inTop = pos.Y <= topZone;
        // The bottom hot-zone only exists in fullscreen: there it reveals
        // the status bar, but in windowed mode the status bar never
        // hides, so counting a bottom-edge move as "edge activity" would
        // bypass the normal countdown and snap the header away instantly.
        var inBottom = fullscreen && pos.Y >= height - bottomZone;

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
            // (Fullscreen only: in windowed mode the status bar is
            // pinned and the header follows the normal countdown.)
            if (!inTop) IsHeaderVisible = false;
            if (fullscreen && !inBottom) IsStatusVisible = false;
            return;
        }

        // Completely outside both edges — arm hide once, not on every scroll move.
        var wasInEdge = _pointerInTopHotZone || _pointerInBottomHotZone;
        _pointerInTopHotZone = false;
        _pointerInBottomHotZone = false;
        if (wasInEdge)
            ScheduleChromeHide();
        else if (IsAnyHideableChromeVisible() && _chromeHideTimer is null)
            ScheduleChromeHide();
    }

    /// <summary>
    /// True when any chrome bar that participates in the *header*
    /// countdown for the current presenter is visible. The status bar
    /// counts only in fullscreen: in windowed mode its visibility is
    /// owned by the flash timer, and counting it here would re-arm a
    /// do-nothing header countdown on every mid-screen move.
    /// </summary>
    private bool IsAnyHideableChromeVisible()
        => IsHeaderVisible || (IsFullscreenStatic() && IsStatusVisible);

    private void ScheduleChromeHide(int delayMs = ChromeHideDelayMs)
    {
        if (_chromeHideTimer is not null) return;
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(delayMs);
        t.Tick += OnChromeHideTick;
        _chromeHideTimer = t;
        t.Start();
    }

    private void OnChromeHideTick(DispatcherQueueTimer sender, object args)
    {
        StopChromeHideTimer();
        if (_pointerInTopHotZone || _pointerInBottomHotZone) return;
        if (IsChromeInteractionActive()) return;
        IsHeaderVisible = false;
        // Fullscreen: the zone-driven hide also collapses the status bar —
        // unless a flash is in flight, in which case the flash timer owns
        // its visibility.
        if (IsFullscreenStatic() && _statusHideTimer is null) IsStatusVisible = false;
    }

    /// <summary>
    /// True while the user is working inside the header toolbar — a combo
    /// box dropdown is open, or keyboard focus sits inside the header.
    /// Collapsing the header then would yank a dropdown off screen or
    /// strand focus inside a hidden element. The countdown is abandoned,
    /// not deferred: the next pointer move outside the top zone re-arms it.
    /// (The search box moved to the title bar, so it no longer participates.)
    /// </summary>
    private bool IsChromeInteractionActive()
    {
        if (FilterCombo.IsDropDownOpen || SortCombo.IsDropDownOpen)
        {
            return true;
        }

        if (RootGrid.XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is DependencyObject focused)
        {
            return IsElementInsideHeader(focused);
        }

        return false;
    }

    private bool IsElementInsideHeader(DependencyObject element)
    {
        // The header is a direct child of RootGrid, so this walk is at
        // most a couple of hops.
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, HeaderChrome))
            {
                return true;
            }
        }
        return false;
    }

    private void StopChromeHideTimer()
    {
        if (_chromeHideTimer is null) return;
        _chromeHideTimer.Stop();
        _chromeHideTimer.Tick -= OnChromeHideTick;
        _chromeHideTimer = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.StatusText))
        {
            FlashStatusBar();
        }
    }

    /// <summary>
    /// Shows the status bar for <see cref="StatusFlashDwellMs"/> after its
    /// text changed. Re-armed on every change, so continuous progress
    /// (scan found-count, backfill percent) keeps it visible for as long
    /// as something is actually happening; the flash then expires and the
    /// bar collapses again.
    /// </summary>
    private void FlashStatusBar()
    {
        IsStatusVisible = true;
        StopStatusHideTimer();
        var t = DispatcherQueue.CreateTimer();
        t.IsRepeating = false;
        t.Interval = TimeSpan.FromMilliseconds(StatusFlashDwellMs);
        t.Tick += OnStatusFlashTick;
        _statusHideTimer = t;
        t.Start();
    }

    private void OnStatusFlashTick(DispatcherQueueTimer sender, object args)
    {
        StopStatusHideTimer();
        // Fullscreen: a pointer parked on the bottom edge keeps the bar
        // up; the zone logic hides it once the pointer leaves again.
        if (IsFullscreenStatic() && _pointerInBottomHotZone) return;
        IsStatusVisible = false;
    }

    private void StopStatusHideTimer()
    {
        if (_statusHideTimer is null) return;
        _statusHideTimer.Stop();
        _statusHideTimer.Tick -= OnStatusFlashTick;
        _statusHideTimer = null;
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

    private void ImageItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MediaItem item)
        {
            PrepareViewerConnectedAnimation(element);
            OpenViewer(item);
        }
    }

    /// <summary>
    /// Source side of the gallery → viewer connected animation: hand the
    /// clicked card's already-loaded thumbnail to the animation service so
    /// ViewerView can fly it into place once its full image is on screen.
    /// Cards without a laid-out thumbnail (still decoding, zero size) are
    /// skipped — animating a placeholder rectangle reads worse than no
    /// animation at all.
    /// </summary>
    private static void PrepareViewerConnectedAnimation(FrameworkElement card)
    {
        try
        {
            var image = FindDescendant<Image>(card);
            if (image?.Source is null || image.ActualWidth <= 0) return;
            ConnectedAnimationService.GetForCurrentView()
                .PrepareToAnimate(ViewerView.ConnectedAnimationKey, image);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"PrepareViewerConnectedAnimation error: {ex.Message}");
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deep) return deep;
        }
        return null;
    }

    private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is MediaItem item)
        {
            _selectedItemForDelete = item;
        }
    }

    private void Tile_PointerEntered(object sender, PointerRoutedEventArgs e)
        => SetCardHover(sender, visible: true);

    private void Tile_PointerExited(object sender, PointerRoutedEventArgs e)
        => SetCardHover(sender, visible: false);

    private static void SetCardHover(object sender, bool visible)
    {
        var grid = sender switch
        {
            Border border => border.Child as Grid,
            Button button => button.Content as Grid,
            _ => null
        };
        if (grid is null) return;
        var opacity = visible ? 1.0 : 0.0;
        foreach (var child in grid.Children)
        {
            if (child is FrameworkElement fe && fe.Tag as string == "hover-overlay")
                fe.Opacity = opacity;
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

            // ViewerViewModel (singleton) keeps GalleryViewModel.LastViewedIndex
            // in sync via OnCurrentIndexChanged — no page-side subscription is
            // needed (and that subscription used to leak across viewer visits,
            // since each visit creates a fresh GalleryView).
            await App.GetService<ViewerViewModel>().OpenItem(item);

            _navigationService.NavigateTo<ViewerView>();
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isNavigatingToViewer, 0);
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

        // The status flash timer holds the page through its delegate and
        // would tick against a torn-down tree after navigation.
        StopStatusHideTimer();

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;

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
    /// Top-bar "打开文件": pick one file, open its containing folder, then jump
    /// straight into the viewer at that file.
    /// </summary>
    private async void OpenFileBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var item = await ViewModel.OpenFileAsync();
            if (item is not null)
            {
                OpenViewer(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"OpenFileBtn_Click error: {ex}");
        }
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
