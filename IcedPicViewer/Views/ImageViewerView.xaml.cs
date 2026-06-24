// Copyright (c) IcedPicViewer. All rights reserved.

using System.ComponentModel;
using IcedPicViewer.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Media.Playback;

namespace IcedPicViewer.Views;

public sealed partial class ImageViewerView : Page, System.ComponentModel.INotifyPropertyChanged
{
    // Exposed for x:Bind in the page-level markup. Constructor-assigned so
    // it's safe to read in OnLoaded (which fires after the ctor). DI gives
    // the same singleton that GalleryView prepared via ShowImageAsync, so
    // state (CurrentImage, CurrentIndex, ...) survives across navigations.
    public ImageViewModel ViewModel { get; }

    private double _minimapWidth = 150;
    private double _minimapHeight = 120;
    private Rectangle? _viewportRect;

    // Fullscreen button bindings. Read from App.MainWindow.IsFullscreen
    // (which is itself a thin wrapper over AppWindow.Presenter.Kind) so
    // the button glyph/label/tooltip reflect whatever the user did last
    // — button click, F11, or anything else that toggles the presenter.
    //
    // IsFullscreen MUST stay an instance property: x:Bind compiles to
    // an instance-member access on the page root, not a static call.
    // CA1822 thinks the property is static-able because the body
    // doesn't touch any instance state, but suppressing the warning
    // is the correct call here — the page's x:Bind contract
    // requires the instance form. The static helper below is the
    // actual reader; the instance property is the XAML binding
    // surface.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind requires instance member; reading is delegated to the static helper below.")]
    public bool IsFullscreen => IsFullscreenStatic();
    public string IsFullscreenGlyph => IsFullscreen ? "\uE93C" : "\uE827"; // BackToWindow / FullScreen
    public string IsFullscreenLabel => IsFullscreen ? "Exit Fullscreen" : "Fullscreen";
    public string IsFullscreenTooltip => IsFullscreen
        ? "Exit fullscreen (F11)"
        : "Fullscreen (F11)";

    private static bool IsFullscreenStatic() => App.MainWindow?.IsFullscreen ?? false;

    /// <summary>
    /// Centered play button on the static first frame. The handler
    /// delegates to the VM's <c>PlayCommand</c> so the create-and-start
    /// logic lives in one place (also reachable from the WH_KEYBOARD
    /// Space hook in MainWindow.xaml.cs).
    /// </summary>
    private void PlayOverlay_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PlayCommand.CanExecute(null))
        {
            ViewModel.PlayCommand.Execute(null);
        }
    }

    /// <summary>
    /// Fullscreen toggle. Delegates to the MainWindow's
    /// <c>ToggleFullscreen</c> method (which swaps the AppWindow
    /// presenter) and the bound IsFullscreen property drives the
    /// button's glyph / label / tooltip update. Click handler
    /// rather than Command because the toggle mutates a
    /// window-level state that the VM has no business knowing
    /// about (the VM is gallery / viewer-scoped, the window is
    /// app-scoped).
    /// </summary>
    private void FullscreenBtn_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.ToggleFullscreen();
    }

    // Slideshow loop / shuffle clicks are handled by the
    // AppBarToggleButton's IsChecked TwoWay binding in XAML — the
    // click flips IsChecked, the binding writes through to
    // IsSlideshowLooping / IsSlideshowShuffling, and the
    // OnIsSlideshowShufflingChanged partial method on the VM
    // (which clears the shuffle queue) fires as a side effect of
    // the property change. No Click handler is needed (and adding
    // one would risk a double-toggle from button + handler both
    // mutating the same flag).

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));

    // ----------------------------------------------------------------
    // Auto-hide chrome (fullscreen only)
    //
    // While the window is in fullscreen the top CommandBar collapses
    // to give the image / video the full screen. Mouse motion in the
    // top 60 px of the window reveals it; 3 s of no activity below
    // that line hides it again. Outside fullscreen the bar is always
    // Visible — IsCommandBarVisible is irrelevant there and the timer
    // never runs.
    //
    // Why a stateful timer + property instead of two Storyboards:
    // - Reveal needs to be instant (no fade-in feels sluggish for a
    //   user-driven action). Hide has a 3s grace period that
    //   animation can't express on its own.
    // - DispatcherQueueTimer.Start() on a non-repeating timer
    //   restarts the countdown each time the mouse moves, so the
    //   bar stays visible as long as the mouse is moving below the
    //   reveal line and hides only after 3s of stillness. That's
    //   the behaviour users expect from photo apps.
    // ----------------------------------------------------------------
    private bool _isCommandBarVisible = true;
    public bool IsCommandBarVisible
    {
        get => _isCommandBarVisible;
        set
        {
            if (_isCommandBarVisible == value) return;
            _isCommandBarVisible = value;
            OnPropertyChanged(nameof(IsCommandBarVisible));
            // CommandBarVisibility depends on IsFullscreen + this,
            // so re-raise it here too — the XAML OneWay bind to
            // CommandBar's Visibility needs a fresh notification
            // whenever either input changes.
            OnPropertyChanged(nameof(CommandBarVisibility));
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind requires instance member; the body reads only static / state-free inputs but the binding surface is the instance form.")]
    public Visibility CommandBarVisibility => IsFullscreenStatic()
        ? (IsCommandBarVisible ? Visibility.Visible : Visibility.Collapsed)
        : Visibility.Visible;

    // Reveal strip height in DIPs. 60 px is large enough to be a
    // forgiving landing zone on a HiDPI screen (the bar itself is
    // 48 px tall — adding a few px of slack above and below means
    // the user doesn't have to land precisely on the bar's bottom
    // edge to make it appear).
    private const double AutoHideTopHitHeight = 60.0;

    /// <summary>
    /// Mouse motion on the page-level Grid. In fullscreen, mouse in
    /// the top 60 px reveals the CommandBar; mouse below collapses
    /// it immediately. Outside fullscreen this is a no-op — the
    /// bar is always Visible there. Same A-mode contract as
    /// GalleryView.RootGrid_PointerMoved: the earlier 3 s "hide
    /// after stillness" timer was dropped because PointerMoved
    /// firing on every motion kept resetting the countdown and
    /// prevented it from ever ticking.
    /// </summary>
    private void MainContentGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsFullscreenStatic())
        {
            return;
        }

        // GetCurrentPoint(MainContentGrid) returns the pointer's
        // position in the Grid's own coordinate space, so Y < 60
        // means "in the top strip" without needing to subtract
        // margins / chrome offsets manually.
        var y = e.GetCurrentPoint(MainContentGrid).Position.Y;
        var inTopZone = y < AutoHideTopHitHeight;
        IsCommandBarVisible = inTopZone;
    }

    private void OnMainWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindow.IsFullscreen))
        {
            // Re-raise the IsFullscreen-derived properties so the
            // button's glyph/label/tooltip follow F11 toggles
            // (which route through MainWindow.HandleViewerKey and
            // never call our click handler). Without this, the
            // x:Bind OneWay would freeze on the first-render value
            // and the button would lie about the window state.
            OnPropertyChanged(nameof(IsFullscreen));
            OnPropertyChanged(nameof(IsFullscreenGlyph));
            OnPropertyChanged(nameof(IsFullscreenLabel));
            OnPropertyChanged(nameof(IsFullscreenTooltip));

            // Auto-hide chrome: when entering fullscreen the bar
            // disappears (immersive view) and the reveal-on-mouse-
            // near-top logic in MainContentGrid_PointerMoved takes
            // over. When leaving, force-show and stop the pending
            // hide timer so the bar doesn't blink back to hidden
            // right after exit.
            if (IsFullscreen)
            {
                IsCommandBarVisible = false;
            }
            else
            {
                IsCommandBarVisible = true;
            }
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentImage != null)
        {
            var source = ViewModel.CurrentImage.Source;
            // For archive entries we can't select the entry itself
            // (Explorer doesn't understand zip/rar/7z contents), so
            // highlight the containing archive file instead. The user
            // immediately sees which .zip / .rar / .7z the image is
            // inside, which is the actionable "where is this file"
            // answer.
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
    }

    public ImageViewerView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<ImageViewModel>();

        // Mirror the VM's MediaPlayer into the MediaPlayerElement. The
        // XAML can't bind the element's MediaPlayer property directly
        // because it's read-only in WinUI 3 — the documented way to
        // attach / detach a player is the SetMediaPlayer method. We
        // watch the VM's MediaPlayer property change and call it here.
        // The same subscription also handles the "set to null on
        // navigate away" path (PlayerHost stops rendering and
        // StopAndDisposePlayer in the VM closes the native handle).
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Wire the fullscreen button to the MainWindow's
        // PropertyChanged so F11 (which routes through the WH_KEYBOARD
        // hook and never enters our click handler) still updates the
        // button's glyph / label / tooltip. Subscribed once in the
        // ctor; unsubscribed in Unloaded to avoid keeping the page
        // alive past navigation.
        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged += OnMainWindowPropertyChanged;
            // Sync initial state. Same reasoning as GalleryView: when
            // the window is already in FullScreen presenter at the
            // time we subscribe (e.g. user F11'd in gallery, then
            // opened a viewer), the IsCommandBarVisible field would
            // stay at its ctor-default `true` value and CommandBar
            // would render visible on first paint — defeating the
            // "command bar is hidden in fullscreen until the user
            // hovers near the top" contract. Forcing the handler to
            // run once with a synthetic event flushes the page into
            // the correct state immediately.
            OnMainWindowPropertyChanged(
                App.MainWindow,
                new System.ComponentModel.PropertyChangedEventArgs(nameof(MainWindow.IsFullscreen)));
        }

        // Named handlers (not lambdas) so Unloaded can unsubscribe — otherwise
        // the view would be kept alive by the lambda capture past navigation,
        // and subsequent DisplayImageChanged firings would hit a defunct visual tree.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImageViewModel.MediaPlayer))
        {
            // Detach any previous player (SetMediaPlayer(null) is the
            // safe detach path) and attach the new one. Order matters
            // only when the VM swaps from one non-null player to
            // another; on the first null→player transition there is
            // nothing to detach, and on the player→null transition the
            // detach is the whole point.
            Player.SetMediaPlayer(ViewModel.MediaPlayer);

            // Start the custom-controls timer (1:1 mode) only when
            // there's a live player. The timer is a no-op when the
            // Player is null — the MediaPlayer reference is checked
            // on every tick so disposing the player mid-tick is safe.
            if (ViewModel.MediaPlayer != null)
            {
                StartControlsTimer();
            }
            else
            {
                StopControlsTimer();
            }
        }
        else if (e.PropertyName == nameof(ImageViewModel.IsFitMode))
        {
            // Image surface: existing FitContainer/ActualSizeContainer/
            // MinimapOverlay swap (code-managed because the minimap
            // needs explicit UpdateMinimap calls).
            ApplyImageFitMode(ViewModel.IsFitMode);

            // Video surface: reparent the single MediaPlayerElement
            // between the Fit container (Grid) and the 1:1 container
            // (ScrollViewer). The element's parent is what tells it
            // how to lay out — a Grid gives a finite slot (Uniform
            // works), a ScrollViewer offers infinite space (None works
            // for the user's native-resolution scroll). One element
            // + two hosts keeps the MediaPlayer reference stable
            // across the toggle and avoids the v0.14.2 "ScrollViewer
            // swallows PlayOverlay clicks" problem by hiding the
            // ScrollViewer container itself when there's no player.
            ApplyVideoFitMode(ViewModel.IsFitMode);
        }
        else if (e.PropertyName == nameof(ImageViewModel.CurrentImage))
        {
            // The IsFitMode state is per-VM, not per-item, so when
            // the user navigates from an image (where 1:1 = the
            // minimap) to a video (where 1:1 = the scrollable
            // player) the IsFitMode property doesn't change but the
            // Player still needs to be in the right container. The
            // IsFitMode=true default + the XAML's initial Player
            // placement in PlayerFitContainer covers the common path
            // (Fit mode from the start), but if the user was in 1:1
            // mode when navigating to a video, reparent now. This is
            // a no-op when the Player is already in the right
            // container (the early-return inside ApplyVideoFitMode).
            ApplyVideoFitMode(ViewModel.IsFitMode);
        }
    }

    private void ApplyVideoFitMode(bool isFitMode)
    {
        if (isFitMode)
        {
            // No-op if the element is already in the Fit host
            // (the common case — the user just toggled back from 1:1
            // and the element was last moved there).
            if (Player.Parent == PlayerFitContainer) return;
            DetachPlayerFromCurrentParent();
            PlayerFitContainer.Children.Add(Player);
        }
        else
        {
            // In 1:1 mode the Player lives inside PlayerScrollViewerInner
            // (the inner ScrollViewer of the PlayerActualSizeContainer
            // Grid). The outer Grid also has the pinned transport-
            // controls strip in row 1, but the Player itself only
            // occupies the ScrollViewer.
            if (Player.Parent == PlayerScrollViewerInner) return;
            DetachPlayerFromCurrentParent();
            PlayerScrollViewerInner.Content = Player;
        }
    }

    /// <summary>
    /// Removes the MediaPlayerElement from whichever container is
    /// currently holding it. Both host types are Panel / ContentControl
    /// variants in WinUI 3: the Grid is a <see cref="Panel"/> with
    /// a <c>Children</c> collection, and the ScrollViewer exposes its
    /// single child via <c>Content</c>. Trying to add the element to
    /// a new host while it's still attached to the old one fails
    /// (UIElement can only have one parent), so the detach is
    /// mandatory before the attach.
    /// </summary>
    private void DetachPlayerFromCurrentParent()
    {
        switch (Player.Parent)
        {
            case Panel oldPanel:
                oldPanel.Children.Remove(Player);
                break;
            case ScrollViewer oldScrollViewer:
                oldScrollViewer.Content = null;
                break;
            // null = never attached; null-parent means we're in the
            // XAML default state. Nothing to do.
        }
    }

    // ----------------------------------------------------------------
    // Custom transport-controls strip (1:1 mode). The built-in
    // MediaPlayerElement transport controls (AreTransportControlsEnabled
    // = true) work fine in Fit mode but scroll with the video content
    // in 1:1 mode, so we hide them in 1:1 and own the chrome here.
    //
    // State management:
    // - A 200 ms dispatcher timer polls the MediaPlayer position +
    //   duration. The MediaPlayer reference can be null (between
    //   navigations) — every tick null-checks before reading.
    // - _isDraggingSlider is set by the slider's pointer events to
    //   distinguish "user is scrubbing" (programmatic slider updates
    //   must not fire) from "timer is updating" (user drag must not
    //   reposition MediaPlayer).
    // - PlayPauseBtn's glyph + the timer both read MediaPlayer.
    //   PlaybackSession.PlaybackState — the MediaPlayer doesn't expose
    //   a public IsPlaying property in WinUI 3.
    // ----------------------------------------------------------------

    private DispatcherQueueTimer? _controlsTimer;
    private bool _isDraggingSlider;

    private void StartControlsTimer()
    {
        if (_controlsTimer != null) return;
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        timer.Tick += OnControlsTimerTick;
        _controlsTimer = timer;
        timer.Start();
    }

    private void StopControlsTimer()
    {
        _controlsTimer?.Stop();
        _controlsTimer = null;
        // Reset the UI to a known-empty state so a stale
        // "0:42 / 1:23" doesn't linger after the player goes away.
        if (PositionSlider != null)
        {
            PositionSlider.Value = 0;
            PositionSlider.Maximum = 100;
        }
        if (TimeText != null) TimeText.Text = "0:00 / 0:00";
        if (PlayPauseGlyph != null) PlayPauseGlyph.Glyph = "\uE768"; // Play
    }

    private void OnControlsTimerTick(DispatcherQueueTimer sender, object args)
    {
        var player = ViewModel.MediaPlayer;
        if (player == null) return;

        var pos = player.PlaybackSession.Position;
        var dur = player.PlaybackSession.NaturalDuration;
        if (dur == TimeSpan.Zero) dur = TimeSpan.Zero;

        // Only push a new slider Value when the user isn't actively
        // scrubbing. Without this guard the slider would jump back
        // to the live position mid-drag and the user's seek would
        // feel broken.
        if (!_isDraggingSlider)
        {
            // Maximum is updated each tick because NaturalDuration
            // changes from TimeSpan.Zero to the real value once
            // MediaOpened fires — pinning Maximum at the XAML default
            // (100) would make the slider behave like a 0–100 %
            // indicator rather than a 0–duration-seconds seek bar.
            if (PositionSlider.Maximum != dur.TotalSeconds)
            {
                PositionSlider.Maximum = dur.TotalSeconds;
            }
            if (Math.Abs(PositionSlider.Value - pos.TotalSeconds) > 0.1)
            {
                PositionSlider.Value = pos.TotalSeconds;
            }
        }

        TimeText.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";

        // Update the play/pause glyph to match the current playback
        // state. Doing it from the timer (rather than a state-change
        // event subscription) means the glyph is correct after
        // any pause/play transition regardless of the trigger
        // (Space, transport controls, our own button, MediaEnded).
        var isPlaying = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
        PlayPauseGlyph.Glyph = isPlaying ? "\uE769" /* Pause */ : "\uE768" /* Play */;
    }

    private static string FormatTime(TimeSpan t)
    {
        // m:ss for < 1 h, h:mm:ss otherwise. Matches the convention
        // already used in VideoItem.DurationText so the user sees a
        // consistent format across the gallery's overlay and the
        // viewer's time readout.
        if (t.TotalHours >= 1)
        {
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        }
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        var player = ViewModel.MediaPlayer;
        if (player == null) return;
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
        }
        else
        {
            player.Play();
        }
        // No need to manually update the glyph — the controls timer
        // refreshes it on the next tick.
    }

    private void PositionSlider_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        // Slider thumb enter = the user is about to drag. Block
        // the timer from overwriting the slider value while the
        // pointer is down.
        _isDraggingSlider = true;
    }

    private void PositionSlider_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isDraggingSlider = false;
    }

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Ignore the programmatic updates from the timer — only act
        // when the user is dragging the slider.
        if (!_isDraggingSlider) return;
        var player = ViewModel.MediaPlayer;
        if (player == null) return;
        // Pause while seeking: a video that's playing during a
        // seek often stutters on the native side, and pausing lets
        // the user re-position accurately. Play() resumes when the
        // user clicks the play button (or hits Space, which the WH_KEYBOARD
        // hook handles).
        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
        {
            player.Pause();
        }
        player.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
    }

    private void ApplyImageFitMode(bool isFitMode)
    {
        if (isFitMode)
        {
            FitContainer.Visibility = Visibility.Visible;
            ActualSizeContainer.Visibility = Visibility.Collapsed;
            MinimapOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            FitContainer.Visibility = Visibility.Collapsed;
            ActualSizeContainer.Visibility = Visibility.Visible;
            MinimapOverlay.Visibility = Visibility.Visible;
            // UpdateMinimap 会在 ActualSizeImage.ImageOpened / ActualSizeContainer.SizeChanged
            // 事件里被自动触发;此处无需再手动调用,更不应用 Task.Delay 猜布局时机。
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.DisplayImageChanged += OnDisplayImageChanged;
        ViewModel.NavigatePreviousCommand.NotifyCanExecuteChanged();
        ViewModel.NavigateNextCommand.NotifyCanExecuteChanged();

        // 键盘处理统一在 MainWindow.RootGrid_KeyDown,见 MainWindow.xaml.cs。
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.DisplayImageChanged -= OnDisplayImageChanged;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (App.MainWindow is not null)
        {
            App.MainWindow.PropertyChanged -= OnMainWindowPropertyChanged;
        }

        // Make sure the native player is detached from the visual tree
        // before the page is torn down. The VM also tears it down on
        // Close, but if the user navigates back via a different path
        // (e.g. back gesture before Close runs) we still want the
        // element to release its reference. The actual Close() is the
        // VM's job; this is just the XAML-side detach.
        Player.SetMediaPlayer(null);
    }

    private void OnDisplayImageChanged(object? sender, EventArgs e)
    {
        UpdateMinimapDeferred();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 键盘快捷键统一在 MainWindow 的 KeyboardAccelerators 处理,window-scope,
        // 不依赖焦点,无需在 Page 进入时手动 Focus。
    }

    private void UpdateMinimapDeferred()
    {
        if (!ViewModel.IsFitMode)
        {
            // Defer one dispatcher tick so the new image's layout pass
            // completes before we read ViewportWidth/Offset from the
            // ScrollViewer. Replaces the older Task.Delay(50) hack —
            // dispatcher tick (~16 ms at 60 fps) is faster and correct
            // (it fires AFTER layout, not on a wall-clock guess).
            DispatcherQueue.GetForCurrentThread().TryEnqueue(UpdateMinimap);
        }
    }

    private void FitModeBtn_Click(object sender, RoutedEventArgs e)
    {
        // The state now lives on the VM (IsFitMode). Toggling it fires
        // PropertyChanged, which OnViewModelPropertyChanged translates
        // into the actual UI mutations: ImageHost swaps FitContainer /
        // ActualSizeContainer (with minimap), PlayerHost's
        // MediaPlayerElement.Stretch + ScrollViewer modes update
        // automatically through x:Bind. The view's only job here is
        // to flip the state and keep the button label in sync.
        ViewModel.IsFitMode = !ViewModel.IsFitMode;
        FitModeBtn.Content = ViewModel.IsFitMode ? "Fit" : "1:1";
    }

    private void ActualSizeContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateMinimapViewport();
    }

    private void ActualSizeImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ViewModel.IsFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsFitMode)
        {
            MinimapImage.Source = null;
            UpdateMinimap();
        }
    }

    private void UpdateMinimap()
    {
        double width = 0;
        double height = 0;

        if (ActualSizeImage?.Source is not BitmapImage bmp)
        {
            return;
        }

        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
        }
        else if (ActualSizeImage!.ActualWidth > 0 && ActualSizeImage!.ActualHeight > 0)
        {
            width = ActualSizeImage!.ActualWidth;
            height = ActualSizeImage!.ActualHeight;
        }
        else
        {
            return;
        }

        if (width > 0 && height > 0)
        {
            MinimapImage.Source = null;
            MinimapImage.Source = bmp;
            MinimapImage.Width = _minimapWidth;
            MinimapImage.Height = _minimapHeight;

            MinimapViewport.Children.Clear();
            _viewportRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 120, 215))
            };
            Canvas.SetLeft(_viewportRect, 0);
            Canvas.SetTop(_viewportRect, 0);
            MinimapViewport.Children.Add(_viewportRect);

            MinimapViewport.Width = _minimapWidth;
            MinimapViewport.Height = _minimapHeight;

            UpdateMinimapViewport();
        }
    }

    private void UpdateMinimapViewport()
    {
        if (_viewportRect == null || ActualSizeImage.Source == null) return;

        if (ActualSizeImage.Source is BitmapImage bmp && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            var imageWidth = bmp.PixelWidth;
            var imageHeight = bmp.PixelHeight;

            var viewWidth = ActualSizeContainer.ViewportWidth;
            var viewHeight = ActualSizeContainer.ViewportHeight;
            var scrollX = ActualSizeContainer.HorizontalOffset;
            var scrollY = ActualSizeContainer.VerticalOffset;

            var scaleX = _minimapWidth / imageWidth;
            var scaleY = _minimapHeight / imageHeight;

            var rectWidth = viewWidth * scaleX;
            var rectHeight = viewHeight * scaleY;
            var rectX = scrollX * scaleX;
            var rectY = scrollY * scaleY;

            _viewportRect.Width = Math.Max(rectWidth, 4);
            _viewportRect.Height = Math.Max(rectHeight, 4);
            Canvas.SetLeft(_viewportRect, rectX);
            Canvas.SetTop(_viewportRect, rectY);
        }
    }

    private void MinimapImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(MinimapViewport).Position;
        ScrollToMinimapPosition(pos);
        MinimapViewport.CapturePointer(e.Pointer);
    }

    private void MinimapImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (MinimapViewport.PointerCaptures != null && MinimapViewport.PointerCaptures.Count > 0)
        {
            var pos = e.GetCurrentPoint(MinimapViewport).Position;
            ScrollToMinimapPosition(pos);
        }
    }

    private void MinimapImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        MinimapViewport.ReleasePointerCapture(e.Pointer);
    }

    private void ScrollToMinimapPosition(Point pos)
    {
        if (ActualSizeImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
            return;

        var imageWidth = bmp.PixelWidth;
        var imageHeight = bmp.PixelHeight;

        var scaleX = imageWidth / _minimapWidth;
        var scaleY = imageHeight / _minimapHeight;

        var targetX = pos.X * scaleX;
        var targetY = pos.Y * scaleY;

        var viewWidth = ActualSizeContainer.ViewportWidth;
        var viewHeight = ActualSizeContainer.ViewportHeight;

        targetX = Math.Max(0, Math.Min(targetX - viewWidth / 2, imageWidth - viewWidth));
        targetY = Math.Max(0, Math.Min(targetY - viewHeight / 2, imageHeight - viewHeight));

        ActualSizeContainer.ChangeView(targetX, targetY, null);
    }
}
