// Copyright (c) IcedPicViewer. All rights reserved.

using System.ComponentModel;
using IcedPicViewer.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Media.Playback;

namespace IcedPicViewer.Views;

public sealed partial class ViewerView : Page, System.ComponentModel.INotifyPropertyChanged
{
    // Exposed for x:Bind in the page-level markup. Constructor-assigned so
    // it's safe to read in OnLoaded (which fires after the ctor). DI gives
    // the same singleton that GalleryView prepared via OpenItem, so
    // state (SelectedItem, CurrentIndex, ...) survives across navigations.
    public ViewerViewModel ViewModel { get; }

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
    public string IsFullscreenLabel => IsFullscreen
        ? IcedPicViewer.Core.Text.UiCopy.ExitFullscreen
        : IcedPicViewer.Core.Text.UiCopy.Fullscreen;
    public string IsFullscreenTooltip => IsFullscreen
        ? IcedPicViewer.Core.Text.UiCopy.ExitFullscreenTooltip
        : IcedPicViewer.Core.Text.UiCopy.FullscreenTooltip;

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
    // Floating chrome (edge hot-zone, mainstream media-player style).
    // Applies in BOTH presenters: in fullscreen the bar would cover the
    // immersive canvas, and in windowed mode it permanently covered the
    // top band of the image — so the same auto-hide applies either way.
    //
    // Reveal: pointer in a generous top band (max of ~10% height / 96px,
    // and the command bar's own height + slack when already shown).
    // Hide: only after leaving the zone for ChromeHideDelayMs — not on
    // every mid-screen move (that used to restart a timer forever while
    // scrolling) — and never while the user is still interacting with
    // the bar (overflow menu open / focus inside it; see
    // IsChromeInteractionActive). Entering the viewer or switching
    // presenters briefly shows chrome then auto-hides so users learn the
    // edge cue.
    // ----------------------------------------------------------------
    private bool _isCommandBarVisible = true;
    private bool _pointerInChromeHotZone;
    private DispatcherQueueTimer? _chromeHideTimer;

    /// <summary>Delay after leaving the edge before chrome collapses.</summary>
    private const int ChromeHideDelayMs = 2000;

    /// <summary>Minimum top edge band when chrome is hidden (DIP).</summary>
    private const double ChromeTopZoneMinPx = 96;

    /// <summary>Fraction of window height for top edge band when hidden.</summary>
    private const double ChromeTopZoneHeightFraction = 0.12;

    public bool IsCommandBarVisible
    {
        get => _isCommandBarVisible;
        set
        {
            if (_isCommandBarVisible == value) return;
            _isCommandBarVisible = value;
            OnPropertyChanged(nameof(IsCommandBarVisible));
            OnPropertyChanged(nameof(CommandBarVisibility));
        }
    }

    /// <summary>
    /// Bound by the CommandBar's Visibility. The auto-hide state applies in
    /// both presenters now — in windowed mode the bar used to be pinned
    /// visible and permanently covered the top of the image.
    /// </summary>
    public Visibility CommandBarVisibility =>
        IsCommandBarVisible ? Visibility.Visible : Visibility.Collapsed;

    private double ComputeTopHotZoneHeight(double viewportHeight)
    {
        // Hidden: large edge strip so "nudge to top" works (Photos / players).
        var baseZone = Math.Max(ChromeTopZoneMinPx, viewportHeight * ChromeTopZoneHeightFraction);
        if (!IsCommandBarVisible)
            return baseZone;

        // Shown: keep entire bar (+ slack) inside the zone so moving across
        // buttons doesn't collapse chrome mid-click.
        var barH = ViewerCommandBar.ActualHeight;
        if (barH <= 0) barH = 56;
        return Math.Max(baseZone, barH + 40);
    }

    private void MainContentGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        => HandleChromePointer(e);

    // Touch and pen taps produce PointerPressed but often no PointerMoved
    // frames at all, so a contact that lands in the top zone must count as
    // a reveal gesture too — otherwise touch users could never bring the
    // auto-hidden bar back (there is no hover on touch).
    private void MainContentGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        => HandleChromePointer(e);

    // Keyboard-only users cannot hover the top edge. Tab is the "I want
    // to reach the controls" gesture, so it reveals the bar; focus then
    // lands inside it and IsChromeInteractionActive keeps it up. The key
    // itself is not handled — focus navigation proceeds normally.
    private void MainContentGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Tab) return;
        StopChromeHideTimer();
        IsCommandBarVisible = true;
    }

    private void HandleChromePointer(PointerRoutedEventArgs e)
    {
        var y = e.GetCurrentPoint(MainContentGrid).Position.Y;
        var h = MainContentGrid.ActualHeight;
        if (h <= 0) return;

        var topZone = ComputeTopHotZoneHeight(h);
        var inZone = y <= topZone;
        if (inZone)
        {
            _pointerInChromeHotZone = true;
            StopChromeHideTimer();
            IsCommandBarVisible = true;
            return;
        }

        // Left the edge: arm hide once. Mid-screen moves must not re-arm
        // the timer (that kept chrome stuck open while scrolling).
        if (_pointerInChromeHotZone)
        {
            _pointerInChromeHotZone = false;
            ScheduleChromeHide();
        }
        else if (IsCommandBarVisible && _chromeHideTimer is null)
        {
            ScheduleChromeHide();
        }
    }

    private void ScheduleChromeHide()
    {
        if (_chromeHideTimer is not null) return; // already counting down
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
        if (_pointerInChromeHotZone) return;
        if (IsChromeInteractionActive()) return;
        IsCommandBarVisible = false;
    }

    /// <summary>
    /// True while the user is interacting with the command bar itself, in
    /// which case the countdown must not collapse it: hiding would yank an
    /// open overflow menu off screen, or drop keyboard focus out from
    /// under the user (the slideshow-interval slider lives in the bar).
    /// The countdown is abandoned rather than deferred — the next pointer
    /// move out of the top zone re-arms it.
    /// </summary>
    private bool IsChromeInteractionActive()
    {
        if (ViewerCommandBar.IsOpen)
        {
            return true;
        }

        if (MainContentGrid.XamlRoot is { } root &&
            FocusManager.GetFocusedElement(root) is DependencyObject focused)
        {
            return IsElementInsideCommandBar(focused);
        }

        return false;
    }

    private bool IsElementInsideCommandBar(DependencyObject element)
    {
        // The bar is a direct child of MainContentGrid, so this walk is at
        // most a couple of hops. The walk accepts any DependencyObject so
        // a future focusable non-control can't crash it.
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, ViewerCommandBar))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the pressed key belongs to whatever control currently has
    /// keyboard focus, and must NOT also be treated as a viewer shortcut.
    ///
    /// The WH_KEYBOARD hook deliberately calls CallNextHookEx, so the key
    /// still reaches the focused control — without this guard a single
    /// press did two things at once:
    ///   - ←/→ while a slider was focused stepped the slider AND flipped
    ///     to the previous/next image;
    ///   - Space while a button was focused activated the button AND
    ///     toggled playback.
    /// Only the keys a control actually owns are yielded: sliders own
    /// ←/→ (value stepping), buttons own Space (activation). Everything
    /// else — including ←/→ while a non-slider control has focus — stays
    /// a viewer shortcut.
    /// </summary>
    public bool IsKeyOwnedByFocusedControl(Windows.System.VirtualKey key)
    {
        if (MainContentGrid.XamlRoot is not { } root ||
            FocusManager.GetFocusedElement(root) is not DependencyObject focused)
        {
            return false;
        }

        return key switch
        {
            Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right =>
                IsInside<Slider>(focused),
            Windows.System.VirtualKey.Space =>
                IsInside<ButtonBase>(focused),
            _ => false,
        };
    }

    private static bool IsInside<T>(DependencyObject element) where T : DependencyObject
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is T)
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

    private void OnMainWindowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindow.IsFullscreen))
        {
            OnPropertyChanged(nameof(IsFullscreen));
            OnPropertyChanged(nameof(IsFullscreenGlyph));
            OnPropertyChanged(nameof(IsFullscreenLabel));
            OnPropertyChanged(nameof(IsFullscreenTooltip));

            // Both directions behave the same now that windowed chrome
            // auto-hides too: show the bar briefly (entering fullscreen
            // must not look like the bar vanished for no reason, and
            // leaving it must not pin the bar open), then let the
            // countdown collapse it again.
            _pointerInChromeHotZone = false;
            IsCommandBarVisible = true;
            StopChromeHideTimer();
            ScheduleChromeHide();
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedItem != null)
        {
            var source = ViewModel.SelectedItem.Media;
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

    public ViewerView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<ViewerViewModel>();

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
        if (e.PropertyName == nameof(ViewerViewModel.MediaPlayer))
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
        else if (e.PropertyName == nameof(ViewerViewModel.IsFitMode))
        {
            // Image surface: existing FitContainer/ActualSizeContainer/
            // MinimapOverlay swap (code-managed because the minimap
            // needs explicit UpdateMinimap calls).
            ApplyImageFitMode(ViewModel.IsFitMode);

            // Only reparent the MediaPlayerElement when viewing a video.
            // For images, leave the Player element alone.
            if (ViewModel.IsVideo)
            {
                ApplyVideoFitMode(ViewModel.IsFitMode);
            }
        }
        else if (e.PropertyName == nameof(ViewerViewModel.SelectedItem))
        {
            // Only adjust the Player container for video items.
            if (ViewModel.IsVideo)
            {
                ApplyVideoFitMode(ViewModel.IsFitMode);
            }
        }
    }

    private void ApplyVideoFitMode(bool isFitMode)
    {
        if (Player == null) return;

        // Only manage when relevant to video.
        if (!ViewModel.IsVideo && ViewModel.MediaPlayer == null)
        {
            return;
        }

        // Defer reparenting to avoid modifying the visual tree synchronously from
        // property change or click handlers.
        DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
        {
            DoReparentPlayer(isFitMode);
        });
    }

    private void DoReparentPlayer(bool isFitMode)
    {
        if (Player == null) return;

        var attached = Player.MediaPlayer;
        if (attached != null)
        {
            Player.SetMediaPlayer(null);
        }

        try
        {
            if (isFitMode)
            {
                if (Player.Parent == PlayerFitContainer)
                {
                    return;
                }
                DetachPlayerFromCurrentParent();
                PlayerFitContainer.Children.Add(Player);
            }
            else
            {
                if (Player.Parent == PlayerScrollViewerInner)
                {
                    return;
                }
                DetachPlayerFromCurrentParent();
                PlayerScrollViewerInner.Content = Player;
            }
        }
        finally
        {
            if (attached != null)
            {
                Player.SetMediaPlayer(attached);
            }
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

    // ----------------------------------------------------------------
    // Pinned-error InfoBar copy button.
    //
    // The user's primary path for getting the error text out: one
    // click on this button dumps the full hint (codec hint + reason
    // + exception details) to the Windows clipboard. Drag-selection
    // on the TextBox works in theory but the wrapped multi-line
    // layout makes it fiddly, so the explicit button is the
    // affordance that guarantees success.
    //
    // Feedback: button glyph + label swap to "已复制 ✓" for 1.5 s so
    // the user knows the click actually fired (without feedback, a
    // single click on a quiet button can leave doubt about whether
    // anything happened). The reset is a non-repeating
    // DispatcherQueueTimer — Stop+Start on every click so a rapid
    // double-click resets the dwell rather than firing two reverts
    // back-to-back.
    // ----------------------------------------------------------------
    private DispatcherQueueTimer? _copyResetTimer;

    private void CopyErrorBtn_Click(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.ErrorMessage;
        if (string.IsNullOrEmpty(text)) return;

        var dataPackage = new DataPackage();
        dataPackage.SetText(text);
        Clipboard.SetContent(dataPackage);

        // Swap to "copied" affordance for 1.5 s. Glyph: E73E CheckMark
        // (Segoe Fluent Icons). Doing both glyph AND label change makes
        // the state change unmistakable even at a glance.
        CopyBtnGlyph.Glyph = "\uE73E";
        CopyBtnLabel.Text = IcedPicViewer.Core.Text.UiCopy.Copied;

        _copyResetTimer ??= CreateCopyResetTimer();
        _copyResetTimer.Stop();
        _copyResetTimer.Start();
    }

    private DispatcherQueueTimer CreateCopyResetTimer()
    {
        var timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(1500);
        timer.Tick += (_, _) =>
        {
            CopyBtnGlyph.Glyph = "\uE8C8"; // Copy
            CopyBtnLabel.Text = IcedPicViewer.Core.Text.UiCopy.Copy;
        };
        return timer;
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

        // Apply Fit/1:1 state after the visual tree is fully constructed and Loaded.
        ApplyImageFitMode(ViewModel.IsFitMode);

        if (ViewModel.IsVideo)
        {
            ApplyVideoFitMode(ViewModel.IsFitMode);
        }

        if (ViewModel?.IsFitMode == false)
        {
            UpdateMinimapDeferred();
        }

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

        // Stop the copy-button feedback timer so a pending tick can't
        // fire after the page is torn down and try to mutate disposed
        // XAML elements (which would throw or silently no-op). The
        // timer holds a delegate capturing this page instance, so
        // leaving it running would also keep the page alive past
        // navigation.
        _copyResetTimer?.Stop();
        _copyResetTimer = null;

        // Same reasoning for the chrome auto-hide timer — and now that
        // windowed chrome counts down too, this is no longer fullscreen
        // only: a tick firing after teardown would flip visibility on a
        // defunct page and keep it alive through the timer's delegate.
        StopChromeHideTimer();
    }

    private void OnDisplayImageChanged(object? sender, EventArgs e)
    {
        UpdateMinimapDeferred();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // 键盘快捷键统一由 MainWindow 的 WH_KEYBOARD thread-scope hook 处理,
        // 不依赖焦点,无需在 Page 进入时手动 Focus。
    }

    private void UpdateMinimapDeferred()
    {
        if (ViewModel?.IsFitMode == false)
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
        FitModeBtn.Label = ViewModel.IsFitMode
            ? IcedPicViewer.Core.Text.UiCopy.Fit
            : IcedPicViewer.Core.Text.UiCopy.FitOneToOne;
    }

    private void ActualSizeContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (ViewModel?.IsFitMode == false)
        {
            UpdateMinimapViewport();
        }
    }

    private void ActualSizeImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsFitMode == false)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ViewModel?.IsFitMode == false)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.IsFitMode == false)
        {
            MinimapImage.Source = null;
            UpdateMinimap();
        }
    }

    /// <summary>
    /// Connected-animation key used by the gallery → viewer thumbnail
    /// transition. GalleryView prepares it with the clicked card's image
    /// (see GalleryView.PrepareViewerConnectedAnimation); the destination
    /// side consumes it in <see cref="FitImage_ImageOpened"/>.
    /// </summary>
    internal const string ConnectedAnimationKey = "gallery-to-viewer-image";

    /// <summary>
    /// Destination side of the gallery → viewer connected animation: once
    /// the fit image has actually decoded and is on screen, fly the card's
    /// thumbnail into place. No-op when the viewer was opened without a
    /// card click (slideshow, activation, …) — GetAnimation returns null
    /// and the page simply appears as before.
    /// </summary>
    private void FitImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        try
        {
            var animation = ConnectedAnimationService.GetForCurrentView().GetAnimation(ConnectedAnimationKey);
            animation?.TryStart(FitImage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"FitImage connected animation error: {ex.Message}");
        }
    }

    private void UpdateMinimap()
    {
        if (ViewModel?.IsFitMode != false) return;
        if (ActualSizeImage?.Source == null) return;
        var width = (double)ViewModel.DisplayActualWidth;
        var height = (double)ViewModel.DisplayActualHeight;
        if (width <= 0 || height <= 0) return;

        MinimapImage.Source = null;
        MinimapImage.Source = ActualSizeImage.Source;
        MinimapImage.Width = _minimapWidth;
        MinimapImage.Height = _minimapHeight;

        MinimapViewport.Children.Clear();
        // Stroke/fill derive from the system accent color, so the viewport
        // indicator follows the user's accent (and the High Contrast
        // system highlight) instead of a hardcoded blue. The translucent
        // fill keeps the minimap thumbnail readable underneath.
        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        _viewportRect = new Rectangle
        {
            Stroke = new SolidColorBrush(accent),
            StrokeThickness = 1,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, accent.R, accent.G, accent.B))
        };
        Canvas.SetLeft(_viewportRect, 0);
        Canvas.SetTop(_viewportRect, 0);
        MinimapViewport.Children.Add(_viewportRect);

        MinimapViewport.Width = _minimapWidth;
        MinimapViewport.Height = _minimapHeight;

        UpdateMinimapViewport();
    }

    private void UpdateMinimapViewport()
    {
        if (ViewModel?.IsFitMode != false) return;
        if (_viewportRect == null || ActualSizeImage.Source == null) return;

        var imageWidth = (double)ViewModel.DisplayActualWidth;
        var imageHeight = (double)ViewModel.DisplayActualHeight;
        if (imageWidth > 0 && imageHeight > 0)
        {

            var viewWidth = ActualSizeContainer.ViewportWidth;
            var viewHeight = ActualSizeContainer.ViewportHeight;
            var scrollX = ActualSizeContainer.HorizontalOffset;
            var scrollY = ActualSizeContainer.VerticalOffset;

            var scaleX = _minimapWidth / imageWidth;
            var scaleY = _minimapHeight / imageHeight;

            var rectWidth = (viewWidth > 0 ? viewWidth * scaleX : 0);
            var rectHeight = (viewHeight > 0 ? viewHeight * scaleY : 0);
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
        var imageWidth = (double)ViewModel.DisplayActualWidth;
        var imageHeight = (double)ViewModel.DisplayActualHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
            return;

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
