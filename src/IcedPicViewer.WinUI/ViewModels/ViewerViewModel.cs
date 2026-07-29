// Copyright (c) IcedPicViewer. All rights reserved.

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Core.Text;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using WinImageSource = Microsoft.UI.Xaml.Media.ImageSource;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace IcedPicViewer.ViewModels;

public partial class ViewerViewModel : ObservableObject, IDisposable
{
    private readonly GalleryViewModel _galleryViewModel;
    private readonly IImageLoader _imageLoader;
    private readonly IVideoMetadataService _videoMetadataService;
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private const int FullImageMaxSize = 5120;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _preloadCts;
    private CancellationTokenSource? _fullResCts;
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
    [NotifyPropertyChangedFor(nameof(PrePlayStripVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerFitContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerActualSizeContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyPropertyChangedFor(nameof(FitModeBtnVisibility))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial MediaItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualWidth))]
    [NotifyPropertyChangedFor(nameof(ActualHeight))]
    public partial WinImageSource? DisplayImage { get; set; }

    public event EventHandler? DisplayImageChanged;
    public event EventHandler? NavigationChanged;

    // The displayed bitmap's pixel dimensions. ActualWidth/Height fall back
    // to SelectedItem.OriginalWidth/Height until the bitmap has loaded.
    [ObservableProperty]
    public partial int DisplayActualWidth { get; set; }

    [ObservableProperty]
    public partial int DisplayActualHeight { get; set; }

    public int ActualWidth => DisplayActualWidth > 0 ? DisplayActualWidth : SelectedItem?.OriginalWidth ?? 0;

    public int ActualHeight => DisplayActualHeight > 0 ? DisplayActualHeight : SelectedItem?.OriginalHeight ?? 0;

    public string ImagePath => SelectedItem?.Source.ToString() ?? string.Empty;

    /// <summary>
    /// True when the currently-displayed item is a video. Forwarded from
    /// <see cref="MediaItem.IsVideo"/>; bound by the viewer's overlay
    /// chrome (FitMode button visibility, the play overlay, and the
    /// ImageHost/PlayerHost swap).
    /// </summary>
    public bool IsVideo => SelectedItem?.IsVideo ?? false;

    // ----------------------------------------------------------------
    // Slideshow: auto-advance to the next image every interval. The
    // timer fires on the dispatcher, ticks call NavigateNextAsync.
    // Stops on end-of-images (no more items) or when the user closes
    // the viewer / starts a non-slideshow navigation.
    //
    // Two surfaces trigger this: the gallery's "Slideshow" button
    // (opens the viewer at the current item and calls StartSlideshow
    // — see GalleryView.SlideshowBtn_Click) and the viewer's own
    // Slideshow button (toggle). Both go through the same Start /
    // Stop methods so the state is consistent.
    // ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowGlyph))]
    [NotifyPropertyChangedFor(nameof(SlideshowLabel))]
    [NotifyPropertyChangedFor(nameof(SlideshowTooltip))]
    [NotifyCanExecuteChangedFor(nameof(SlideshowCommand))]
    public partial bool IsSlideshowActive { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowLoopGlyph))]
    [NotifyPropertyChangedFor(nameof(SlideshowLoopLabel))]
    [NotifyPropertyChangedFor(nameof(SlideshowLoopTooltip))]
    public partial bool IsSlideshowLooping { get; set; }

    partial void OnIsSlideshowLoopingChanged(bool value)
    {
        // Persist. Same write-back happens for every other preference
        // property in this VM — see OnIsSlideshowShufflingChanged,
        // OnSlideshowIntervalChanged, OnVolumeChanged. The constructor
        // sets the property from the persisted value first, which fires
        // this same partial method and writes the same value back; the
        // duplicate is harmless (ScheduleSave coalesces and the JSON
        // round-trip is idempotent).
        _settingsService.Current.SlideshowLoop = value;
        _settingsService.ScheduleSave();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowShuffleGlyph))]
    [NotifyPropertyChangedFor(nameof(SlideshowShuffleLabel))]
    [NotifyPropertyChangedFor(nameof(SlideshowShuffleTooltip))]
    public partial bool IsSlideshowShuffling { get; set; }

    // Flipping shuffle on clears the queue + the last-shown tracker
    // so the very next tick (which might fire 1s away) starts a
    // fresh cycle. Toggling shuffle off doesn't clear — the queue
    // is unused in sequential mode and the next on-toggle will
    // overwrite it anyway.
    partial void OnIsSlideshowShufflingChanged(bool value)
    {
        if (value)
        {
            _shuffleQueue.Clear();
            _lastShuffleIndex = -1;
        }
        // Persist (same write-back pattern as OnIsSlideshowLoopingChanged).
        _settingsService.Current.SlideshowShuffle = value;
        _settingsService.ScheduleSave();
    }

    public string SlideshowGlyph => IsSlideshowActive ? "\uE71A" /* Stop */ : "\uE768" /* Play */;
    public string SlideshowLabel => IsSlideshowActive ? "停止幻灯片" : "幻灯片";
    public string SlideshowTooltip => IsSlideshowActive
        ? "停止幻灯片"
        : $"开始幻灯片（每 {SlideshowInterval:0.#} 秒）";

    // Loop button — same glyph in both states, but a different
    // background tint would normally distinguish the active one.
    // Tooltip + label flip on/off so the user still gets a visible
    // hint. The "loop" semantic means "wrap to first when the
    // slideshow reaches the end" — without it, the slideshow stops
    // at the last image. Note: ignored in shuffle mode (a random
    // pick at the end can land on any item, so the "end" is never
    // reached in the conventional sense).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind target — the property's value is read by XAML through an instance member access; the static-friendly body is intentional.")]
    public string SlideshowLoopGlyph => "\uE8ED"; // RepeatAll
    public string SlideshowLoopLabel => IsSlideshowLooping ? "循环开" : "循环关";
    public string SlideshowLoopTooltip => IsSlideshowLooping
        ? "循环：到末尾后回到第一张"
        : "不循环：到末尾后停止";

    // Shuffle button — pick a random next index on each tick instead
    // of incrementing CurrentIndex. Single Random instance per VM
    // is sufficient (the slideshow is a singleton; new Random is
    // only needed when many threads contend, which the dispatcher
    // timer pattern doesn't).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind target — the property's value is read by XAML through an instance member access; the static-friendly body is intentional.")]
    public string SlideshowShuffleGlyph => "\uE8B1"; // Shuffle
    public string SlideshowShuffleLabel => IsSlideshowShuffling ? "随机开" : "随机关";
    public string SlideshowShuffleTooltip => IsSlideshowShuffling
        ? "随机：每次随机下一张"
        : "顺序：按列表依次播放";

    /// <summary>
    /// Auto-advance interval in seconds. Stored as <c>double</c> (not
    /// <see cref="TimeSpan"/>) so a WinUI <c>Slider</c> can bind TwoWay
    /// without a converter. The setter also re-arms the running timer
    /// so the user sees the new interval take effect immediately on
    /// the next tick — no need to stop+start the slideshow after
    /// dragging the slider.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SlideshowIntervalText))]
    [NotifyPropertyChangedFor(nameof(SlideshowTooltip))]
    public partial double SlideshowInterval { get; set; } = 5.0;

    partial void OnSlideshowIntervalChanged(double value)
    {
        // Same live-rearm behaviour as the previous TimeSpan-based
        // setter: a running slideshow picks up the new cadence
        // immediately. We don't clamp here — the Slider's Minimum
        // / Maximum on the XAML side enforces the [1, 30] range
        // and the input contract; the VM trusts the binding.
        if (IsSlideshowActive && _slideshowTimer is not null)
        {
            _slideshowTimer.Interval = TimeSpan.FromSeconds(value);
        }
        // Persist. Same write-back pattern as the other preference
        // properties — see OnIsSlideshowLoopingChanged.
        _settingsService.Current.SlideshowInterval = value;
        _settingsService.ScheduleSave();
    }

    /// <summary>
    /// Human-readable "5" / "7.5" text for display next to the
    /// slider. Re-raises on <see cref="SlideshowInterval"/>
    /// changes via the <c>[NotifyPropertyChangedFor]</c> attribute
    /// above, so the label tracks the slider value live.
    /// </summary>
    public string SlideshowIntervalText => SlideshowInterval.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private DispatcherQueueTimer? _slideshowTimer;

    // ----------------------------------------------------------------
    // Pinned error hint (viewer InfoBar).
    //
    // The user wants playback-failure hints to STICK — no auto-dismiss
    // timer, no close button. The hint clears only when the user moves
    // on (back to gallery, Next, Prev, Delete, open a different item)
    // because <see cref="OnSelectedItemChanged"/> sets
    // <see cref="ErrorMessage"/> back to null on every SelectedItem
    // swap. The view renders the message in a read-only TextBox so the
    // user can select and Ctrl+C the codec name / HRESULT / system
    // message for troubleshooting or web search — important for
    // diagnoses like "ProRes needs LAV Filters" where the user wants
    // the exact codec string to copy into a search box.
    // ----------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsErrorBarOpen))]
    [NotifyPropertyChangedFor(nameof(IsErrorBarVisibility))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// True when <see cref="ErrorMessage"/> holds a non-empty hint to
    /// show. Drives the error panel's visibility (via the
    /// <see cref="IsErrorBarVisibility"/> sibling for XAML binding).
    /// Computed (not a separate field) so any path that clears
    /// <c>ErrorMessage</c> automatically collapses the bar — there's
    /// no separate flag that could go out of sync.
    /// </summary>
    public bool IsErrorBarOpen => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Visibility projection of <see cref="IsErrorBarOpen"/> for XAML
    /// binding. The viewer page's pinned error panel binds its
    /// Visibility to this — no converter needed.
    /// </summary>
    public Visibility IsErrorBarVisibility => IsErrorBarOpen
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>
    /// Start the slideshow using the current
    /// <see cref="SlideshowInterval"/>. Parameterless because the
    /// interval is now a bindable property — the gallery's Slideshow
    /// button no longer needs to pass it explicitly (it reads
    /// <c>ViewModel.SlideshowInterval</c> via x:Bind), and the
    /// viewer's own SlideshowCommand has direct access.
    /// </summary>
    public void StartSlideshow()
    {
        IsSlideshowActive = true;
        _slideshowTimer ??= CreateSlideshowTimer();
        _slideshowTimer.Interval = TimeSpan.FromSeconds(SlideshowInterval);
        _slideshowTimer.Start();
    }

    public void StopSlideshow()
    {
        if (!IsSlideshowActive) return;
        _slideshowTimer?.Stop();
        IsSlideshowActive = false;
    }

    /// <summary>
    /// RelayCommand entry point for the viewer's Slideshow button.
    /// Toggles start / stop with the same default interval as the
    /// last start. The gallery's Slideshow button goes through
    /// <see cref="StartSlideshow"/> directly with the gallery's
    /// <see cref="GalleryViewModel.SlideshowInterval"/> — the two
    /// surfaces converge on the same timer.
    /// </summary>
    [RelayCommand]
    private void Slideshow()
    {
        if (IsSlideshowActive)
        {
            StopSlideshow();
        }
        else
        {
            StartSlideshow();
        }
    }

    private DispatcherQueueTimer CreateSlideshowTimer()
    {
        var timer = _dispatcher.CreateTimer();
        timer.IsRepeating = true;
        timer.Tick += OnSlideshowTick;
        return timer;
    }

    private async void OnSlideshowTick(DispatcherQueueTimer sender, object args)
    {
        if (Items.Count == 0) return;

        // End-of-set policy. Skipped in shuffle mode: a shuffle pick
        // can land on any image (including past the current), so the
        // conventional "reached the last image" check doesn't apply
        // and would just falsely stop the slideshow. Sequential mode
        // still respects the loop / no-loop choice.
        if (!IsSlideshowShuffling && CurrentIndex >= Items.Count - 1 && !IsSlideshowLooping)
        {
            StopSlideshow();
            return;
        }

        if (IsSlideshowShuffling && Items.Count > 1)
        {
            // Smart shuffle: a queue of all-but-shuffled indices.
            // Each tick dequeue's the next index, so within one cycle
            // (queue length) the same image is never picked twice
            // (no consecutive repeat — the goal of "smart shuffle").
            // When the queue drains, RefillShuffleQueue builds a new
            // full shuffle of [0, Count) so the next cycle is also
            // repeat-free internally. The boundary case (last image
            // of the previous cycle = first image of the new cycle)
            // is handled inside RefillShuffleQueue by swapping the
            // first element out if it would repeat the just-shown
            // index, so the slideshow never shows the same image
            // back-to-back across a cycle boundary either.
            if (_shuffleQueue.Count == 0)
            {
                RefillShuffleQueue();
            }
            var nextIdx = _shuffleQueue.Dequeue();
            _lastShuffleIndex = nextIdx;
            CurrentIndex = nextIdx;
            // Direct CurrentIndex set bypasses NavigateNextCommand,
            // which is the path that calls ShowSelectedItemAsync and
            // populates DisplayImage for the view. Without this
            // explicit call, the index updates in the UI (DisplayIndex
            // + NavigationChanged) but the bitmap doesn't refresh —
            // the viewer keeps showing the previous image.
            await ShowSelectedItemAsync();
        }
        else if (IsSlideshowLooping && CurrentIndex >= Items.Count - 1)
        {
            // Loop semantics: wrap to the first image rather than
            // calling NavigateNext (which would short-circuit at
            // the end via CanNavigateNext). Same caveat as the shuffle
            // branch: direct CurrentIndex set means we have to drive
            // ShowSelectedItemAsync ourselves.
            CurrentIndex = 0;
            await ShowSelectedItemAsync();
        }
        else
        {
            _ = NavigateNextCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Build a new shuffled permutation of <c>[0, Items.Count)</c>
    /// and enqueue it. Fisher-Yates so the distribution is uniform.
    /// If the first element of the new queue would equal
    /// <c>_lastShuffleIndex</c> (the image shown at the end of the
    /// previous cycle), swap it with a random later position so the
    /// boundary doesn't show the same image twice in a row.
    /// </summary>
    private void RefillShuffleQueue()
    {
        var n = Items.Count;
        _shuffleQueue.Clear();
        var indices = new int[n];
        for (int i = 0; i < n; i++) indices[i] = i;
        for (int i = n - 1; i > 0; i--)
        {
            int j = _slideshowRandom.Next(i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }
        if (n > 1 && indices[0] == _lastShuffleIndex)
        {
            // Swap with a position in [1, n) so the boundary image
            // doesn't repeat. Pick from later in the array to keep
            // the first-cycle image being a "fresh" pick after the
            // gap.
            int swapWith = _slideshowRandom.Next(1, n);
            (indices[0], indices[swapWith]) = (indices[swapWith], indices[0]);
        }
        foreach (var idx in indices) _shuffleQueue.Enqueue(idx);
    }

    private readonly System.Random _slideshowRandom = new();

    // Smart-shuffle state. The queue is the "no consecutive repeat"
    // buffer: enqueue a Fisher-Yates permutation of [0, Count),
    // dequeue one per tick, refill when empty. _lastShuffleIndex is
    // remembered across cycle boundaries so RefillShuffleQueue can
    // avoid putting the just-shown image first in the new cycle.
    // The queue is intentionally NOT cleared on manual nav
    // (Next/Prev clicks) — per the user spec "不做 smart history
    // (只防连续重复)", manual navigation doesn't perturb the cycle.
    private readonly System.Collections.Generic.Queue<int> _shuffleQueue = new();
    private int _lastShuffleIndex = -1;

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
    [NotifyPropertyChangedFor(nameof(PrePlayStripVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerFitContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerActualSizeContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool IsVideoPlaying { get; set; }

    /// <summary>
    /// True while PlayAsync is running its preparation phase (remux /
    /// extract / MediaPlayer init), before the first frame renders.
    /// Used to show a ProgressRing so the user knows the app hasn't
    /// frozen during the potentially slow remux step.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlayOverlayVisibility))]
    [NotifyPropertyChangedFor(nameof(PrePlayStripVisibility))]
    [NotifyPropertyChangedFor(nameof(IsVideoPreparingVisibility))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool IsVideoPreparing { get; set; }

    /// <summary>
    /// Fit-mode toggle, shared between the image and video surfaces. The
    /// view binds each surface to its own visibility / Stretch / ScrollMode
    /// based on this flag plus <see cref="IsVideo"/>. True = fit-to-view
    /// (current default), false = 1:1 native resolution. For images
    /// this swaps between the existing Viewbox and the ScrollViewer-with-
    /// minimap; for videos it swaps between a Grid host (Fit) and a
    /// ScrollViewer host (1:1) — the MediaPlayerElement itself is
    /// reparented between the two via code-behind.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayerFitContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerActualSizeContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyPropertyChangedFor(nameof(PlayerUseBuiltInControls))]
    public partial bool IsFitMode { get; set; } = true;

    partial void OnIsFitModeChanged(bool value)
    {
        if (!value && SelectedItem != null)
        {
            // Entering 1:1: abort any in-flight capped load and force a full-res decode for current item.
            _loadCts?.Cancel();
            _fullResCts?.Cancel();
            _fullResCts?.Dispose();
            _fullResCts = new CancellationTokenSource();
            _ = LoadFullResFor1To1Async(SelectedItem, _fullResCts.Token);
        }
        else if (value)
        {
            // Entering Fit: abort any pending full-res upgrade (keep whatever is currently displayed;
            // a full-res bitmap is acceptable inside Viewbox).
            _fullResCts?.Cancel();
            _fullResCts?.Dispose();
            _fullResCts = null;
        }
    }

    private async Task LoadFullResFor1To1Async(MediaItem item, CancellationToken ct)
    {
        try
        {
            var source = await _imageLoader.LoadFullImageAsync(item.Source, targetMaxSize: null, ct);
            if (ct.IsCancellationRequested) return;

            if (source != null)
            {
                item.FullImage = source;
                if (ReferenceEquals(SelectedItem, item))
                {
                    DisplayImage = source;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadFullResFor1To1Async error: {ex.Message}");
        }
    }

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
    // attributes on SelectedItem and IsVideoPlaying above take care of
    // re-firing PropertyChanged when either input changes.

    public Visibility ImageHostVisibility => !IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility PlayerHostVisibility => IsVideo ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsPlayOverlayVisibility => IsVideo && !IsVideoPlaying && !IsVideoPreparing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Visibility for the bottom pre-playback control strip (▶ + filename
    /// + duration + volume slider). Visible only when the current item
    /// is a video AND playback hasn't started yet — once playback starts
    /// the MediaPlayerElement's own built-in transport controls take over
    /// (in Fit mode) or the 1:1 mode custom strip takes over (when the
    /// user has toggled to native resolution). Showing both at once would
    /// duplicate the chrome and look messy.
    ///
    /// The center Play button (IsPlayOverlayVisibility) is shown in
    /// parallel with this strip — different elements, same gating intent
    /// ("we have a video ready to play, no player yet"). The center
    /// button is the primary entry point; the strip is a secondary
    /// surface for adjusting volume / seeing filename + duration before
    /// committing to playback.
    /// </summary>
    public Visibility PrePlayStripVisibility => IsVideo && !IsVideoPlaying && !IsVideoPreparing ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IsVideoPreparingVisibility => IsVideo && IsVideoPreparing ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The current MediaPlayer volume, on the [0.0, 1.0] scale that
    /// <see cref="MediaPlayer.Volume"/> uses internally. Bound by the
    /// pre-playback strip's volume Slider and pushed into
    /// <see cref="MediaPlayer.Volume"/> at construction time in
    /// <see cref="PlayAsync"/>.
    ///
    /// <para>
    /// Why this lives on the VM rather than directly on the player:
    /// <see cref="MediaPlayer"/> only exists between Play() and
    /// StopAndDisposePlayer(), but the user expects to see and adjust
    /// volume before pressing play. Persisting this value across
    /// sessions (see ISettingsService) means the user picks 50% once,
    /// closes the app, reopens the same .mov — volume stays at 50%.
    /// </para>
    ///
    /// <para>
    /// The set range mirrors MediaPlayer.Volume's contract: values
    /// outside [0.0, 1.0] are clamped to that range so a stuck-key
    /// slider never produces a silent video (< 0) or a "loud enough
    /// to clip the DAC" overdrive (> 1).
    /// </para>
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    public partial double Volume { get; set; } = 1.0;

    /// <summary>
    /// Volume rendered as an integer percentage (e.g. "0" / "50" /
    /// "100") with a "%" suffix. The underlying Volume stays on the
    /// [0.0, 1.0] scale because that's what <see cref="MediaPlayer.Volume"/>
    /// wants; this view is purely for the human-readable label next to
    /// the slider. Binding it with <c>Mode=OneWay</c> (vs. TwoWay on
    /// the underlying Volume) means a stray binding-source edit can't
    /// accidentally re-write through the percentage and lose precision.
    /// </summary>
    public string VolumePercent => $"{(int)Math.Round(Volume * 100.0)}%";

    partial void OnVolumeChanged(double value)
    {
        // Live-update an active player so the user hears the change
        // immediately when adjusting the slider mid-playback. The
        // pre-play strip hides once playback starts, but this still
        // fires if Volume is changed from anywhere else (settings
        // load, programmatic reset). The null check is critical —
        // setting Volume on a disposed player throws.
        if (_mediaPlayer is not null)
        {
            try
            {
                _mediaPlayer.Volume = Math.Clamp(value, 0.0, 1.0);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"ViewerViewModel.OnVolumeChanged: failed to push to MediaPlayer: {ex.GetType().Name}: {ex.Message}");
            }
        }
        // Persist. Same write-back pattern as the other preference
        // properties — see OnIsSlideshowLoopingChanged.
        _settingsService.Current.VideoVolume = value;
        _settingsService.ScheduleSave();
    }

    /// <summary>
    /// "0:42" / "1:23:45" formatted duration for the pre-playback strip.
    /// Empty when the current item isn't a video, so binding it directly
    /// to a TextBlock produces "—" naturally for images.
    /// </summary>
    public string PrePlayDurationText =>
        SelectedItem is VideoItem v && v.Duration > TimeSpan.Zero
            ? FormatDuration(v.Duration)
            : string.Empty;

    /// <summary>
    /// Fractional transcode progress in [0, 1]. Updated via IProgress
    /// callbacks from the FFmpeg.AutoGen DoTranscode loop. Bound to
    /// a ProgressBar in the pre-play strip's transcode state.
    /// </summary>
    private static string FormatDuration(TimeSpan d)
    {
        // hh:mm:ss only when ≥ 1 hour; mm:ss otherwise. Matches the
        // format the existing PlayerControlsStrip uses (it has the same
        // helper inline) — duplicated here rather than shared because
        // the PlayerControlsStrip version is private to its file.
        if (d.TotalHours >= 1.0)
        {
            return $"{(int)d.TotalHours}:{d.Minutes:00}:{d.Seconds:00}";
        }
        return $"{(int)d.TotalMinutes}:{d.Seconds:00}";
    }

    /// <summary>
    /// Visibility for the Grid that hosts the MediaPlayerElement in
    /// Fit mode. Collapsed when not a video, in 1:1 mode (the
    /// ScrollViewer host takes over), or not yet playing (the
    /// "no media" state would otherwise lay out an opaque dead
    /// surface on top of the PlayOverlay button and intercept
    /// clicks). Gating the container (rather than just the
    /// MediaPlayerElement) means even the host's hit-test area
    /// goes away, so the PlayOverlay button sees real pointer
    /// events on the first interaction.
    /// </summary>
    public Visibility PlayerFitContainerVisibility =>
        IsVideo && IsFitMode && IsVideoPlaying ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Visibility for the ScrollViewer that hosts the MediaPlayerElement
    /// in 1:1 mode. Collapsed in Fit mode and when not yet playing
    /// (so the PlayOverlay button can be clicked).
    /// </summary>
    public Visibility PlayerActualSizeContainerVisibility =>
        IsVideo && !IsFitMode && IsVideoPlaying ? Visibility.Visible : Visibility.Collapsed;

    public Visibility FitModeBtnVisibility => !IsVideo ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// <see cref="MediaPlayerElement.Stretch"/> binding. Fit mode =
    /// Uniform (scale to fit the viewport); 1:1 mode = None (native
    /// resolution, scrollable). Toggles when the user clicks the
    /// Fit/1:1 button; the view's OnViewModelPropertyChanged
    /// handler also reparents the element between the two
    /// containers so the layout (Grid vs ScrollViewer) is correct
    /// for the chosen stretch.
    /// </summary>
    public Microsoft.UI.Xaml.Media.Stretch PlayerStretch =>
        IsFitMode ? Microsoft.UI.Xaml.Media.Stretch.Uniform : Microsoft.UI.Xaml.Media.Stretch.None;

    /// <summary>
    /// True when the MediaPlayerElement should use its built-in WinUI
    /// transport controls (Fit mode); false when the view's custom
    /// controls strip should take over (1:1 mode, where the built-in
    /// controls would scroll with the content). The custom strip is
    /// pinned at the bottom of the 1:1 container so the user always
    /// has a way to pause without having to scroll down to the
    /// element's bottom edge.
    /// </summary>
    public bool PlayerUseBuiltInControls => IsFitMode;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayAsync()
    {
        if (SelectedItem is not VideoItem video) return;
        if (_mediaPlayer != null) return;

        string? playbackPath = null;
        IsVideoPreparing = true;
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
            // Push the user-configured volume in BEFORE Source / Play()
            // so the very first decoded frame already plays at the
            // user's level — MediaPlayer defaults to 1.0 (100%) on
            // construction. OnVolumeChanged handles live updates while
            // playback is active, but the initial set has to happen here
            // explicitly because the player didn't exist when the user
            // last dragged the slider.
            player.Volume = Math.Clamp(Volume, 0.0, 1.0);
            player.Source = source;
            // Subscribe before SetSource so we don't miss a fast-failing
            // decode (e.g. unsupported codec → MediaFailed fires within
            // tens of milliseconds on a clean Win10/11 install). The
            // handlers are named so StopAndDisposePlayer can unsubscribe
            // without keeping a lambda capture alive past the player's
            // lifetime — see AGENTS.md "MemFree event handlers" rule.
            player.MediaOpened += OnMediaPlayerOpened;
            player.MediaFailed += OnMediaPlayerFailed;
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
            Trace.TraceError($"ViewerViewModel.PlayAsync error for {SelectedItem?.Id}: {ex.GetType().Name}: {ex.Message}");
            // Surface as a non-playing state so the user can retry. Don't
            // throw — the play overlay stays visible and CanPlay stays true.
            // Order: null MediaPlayer first so CanPlay() sees _mediaPlayer==null
            // when IsVideoPlaying=false fires CanExecuteChanged below.
            MediaPlayer = null;
            IsVideoPlaying = false;
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

            // Surface a user-visible hint for the pre-MediaPlayer failure
            // path. The MediaPlayer.MediaFailed handler covers the case
            // where MediaPlayer.Source is set and MF later refuses to
            // decode, but THIS path fails BEFORE MediaPlayer exists —
            // GetPlaybackFilePathAsync (FFmpeg remux) is the most common
            // culprit: a .mov / .mkv containing a codec MP4 can't carry
            // (ProRes, DNxHD, ...) hits avformat_write_header failure →
            // re-thrown → caught here. Without this hint the user clicks
            // ▶, nothing happens, and they have no idea why.
            //
            // OperationCanceledException = the user navigated away
            // mid-prep (Close / Next / Prev) — no hint in that case, the
            // page is already gone or about to be.
            //
            // Pinned InfoBar: no auto-dismiss, no close button. Cleared
            // only when the user moves to a different item (see
            // OnSelectedItemChanged). The view's TextBox-based content
            // makes the codec / HRESULT / system message selectable +
            // Ctrl+C-able for troubleshooting.
            if (ex is not OperationCanceledException)
            {
                var codec = SelectedItem is VideoItem v ? v.Codec : string.Empty;
                ErrorMessage = BuildPrePlayErrorMessage(ex, codec);
            }
        }
        finally
        {
            IsVideoPreparing = false;
        }
    }

    private bool CanPlay() => IsVideo && !IsVideoPlaying && !IsVideoPreparing && _mediaPlayer == null;

    // ----------------------------------------------------------------
    // MediaPlayer event handlers.
    //
    // MediaOpened is informational — fired after the source's first
    // frame is decoded and ready to render. Useful for "loading" state
    // in the future; right now we only log it so a misbehaving file
    // leaves a breadcrumb in Trace output.
    //
    // MediaFailed is the critical one. Without it, MediaPlayer silently
    // sits there doing nothing when MF rejects the source — this is the
    // bug behind "the .mov remuxes but won't actually play": the remux
    // produces a valid MP4 file, MediaPlayer.Source accepts it, but MF
    // then can't decode the codec inside (typical: HEVC/H.265 on Win10,
    // or any codec the OS doesn't ship a decoder for). The user
    // previously saw "no error + no playback" — we'd swallow the only
    // signal that something went wrong. We now marshal the error to
    // the UI thread, log it via Trace, and surface a user-visible hint.
    // ----------------------------------------------------------------

    private void OnMediaPlayerOpened(MediaPlayer sender, object args)
    {
        Trace.TraceInformation($"ViewerViewModel: MediaPlayer opened for {SelectedItem?.Id}");
    }

    private void OnMediaPlayerFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // MediaFailed fires on a non-UI thread. Capture the failed
        // player and its path right here so the dispatched handler
        // operates on the correct instance even if the user has
        // already navigated away and started a new playback.
        var failedPlayer = sender;
        var failedPath = _currentPlaybackPath;
        if (!_dispatcher.TryEnqueue(() => HandleMediaFailedOnUiThread(failedPlayer, failedPath, args)))
        {
            Trace.TraceWarning($"ViewerViewModel: MediaFailed dropped (dispatcher unavailable): code=0x{args.ExtendedErrorCode:X8}, msg={args.ErrorMessage}");
        }
    }

    private void HandleMediaFailedOnUiThread(MediaPlayer failedPlayer, string? failedPath, MediaPlayerFailedEventArgs args)
    {
        // Only reset VM state if this failed player is still the active
        // one. If the user already navigated away, StopAndDisposePlayer
        // already cleaned up the old player and its temp file — we must
        // NOT kill the new player that replaced it.
        bool isCurrent = ReferenceEquals(failedPlayer, _mediaPlayer);
        if (isCurrent)
        {
            // Detach + release only for the still-current player.
            // Order: null MediaPlayer first so the XAML code-behind
            // detaches the MediaPlayerElement before IsVideoPlaying
            // flips and changes surface visibility.
            MediaPlayer = null;
            _currentPlaybackPath = null;
            IsVideoPlaying = false;

            if (failedPlayer is not null)
            {
                try { failedPlayer.Source = null; failedPlayer.Dispose(); }
                catch (Exception ex) { Trace.TraceWarning($"ViewerViewModel: failed player dispose threw: {ex.GetType().Name}: {ex.Message}"); }
            }
            if (failedPath is not null)
            {
                try { _videoMetadataService.ReleasePlaybackFilePath(failedPath); }
                catch (Exception ex) { Trace.TraceWarning($"ViewerViewModel: failed release threw: {ex.GetType().Name}: {ex.Message}"); }
            }

            // Log + hint only when still on the failed video — if the
            // user navigated away, OnSelectedItemChanged already cleared
            // ErrorMessage and logging a stale player is noise.
            Trace.TraceError($"ViewerViewModel.MediaFailed for {SelectedItem?.Id}: error={args.Error}, hr=0x{args.ExtendedErrorCode:X8}, msg={args.ErrorMessage}");

            // Pinned hint: no auto-dismiss timer, no close button.
            // Cleared by OnSelectedItemChanged when the user navigates to
            // another item (Next / Prev / Close / Delete).
            var codec = SelectedItem is VideoItem v ? v.Codec : string.Empty;
            ErrorMessage = BuildPlaybackErrorMessage(args, codec);
        }
        else
        {
            // Not the active player — StopAndDisposePlayer already
            // disposed it and released its temp file. Skip cleanup
            // (avoid double-release) and skip error hint (the hint
            // belongs to the old video, not the current one).
            Trace.TraceWarning($"ViewerViewModel.MediaFailed for stale player (already navigated away): error={args.Error}, hr=0x{args.ExtendedErrorCode:X8}, msg={args.ErrorMessage}");
        }
    }

    /// <summary>
    /// Turns a MediaPlayer error into a user-friendly explanation. The
    /// raw HRESULT + ErrorMessage is technically accurate but unhelpful
    /// for someone who just wants to know "why doesn't this play?". We
    /// map the most common MF / WinRT error codes to a short explanation
    /// AND, when the codec is known, prepend a codec-specific recovery
    /// hint (e.g. "ProRes needs LAV Filters" or "HEVC needs the Windows
    /// HEVC extension"). Without the codec-aware hint the user gets a
    /// generic "codec not supported" message and has no idea which
    /// specific codec they're dealing with or what to install.
    /// </summary>
    private static string BuildPlaybackErrorMessage(MediaPlayerFailedEventArgs args, string codec)
    {
        var codecHint = VideoPlaybackCopy.GetCodecSpecificHint(codec);
        var categoryHint = args.Error switch
        {
            MediaPlayerError.SourceNotSupported => VideoPlaybackCopy.CategorySourceNotSupported(),
            MediaPlayerError.DecodingError => VideoPlaybackCopy.CategoryDecodingError(),
            MediaPlayerError.NetworkError => VideoPlaybackCopy.CategoryNetworkError(),
            MediaPlayerError.Aborted => VideoPlaybackCopy.CategoryAborted(),
            _ => VideoPlaybackCopy.CategoryUnknown(),
        };

        var codecLine = string.IsNullOrEmpty(codec)
            ? VideoPlaybackCopy.CodecUnknownLine()
            : codec;

        var details = VideoPlaybackCopy.FormatPlaybackDetails(
            $"0x{args.ExtendedErrorCode:X8}",
            args.Error.ToString(),
            codecLine,
            args.ErrorMessage ?? "");

        return VideoPlaybackCopy.ComposeErrorMessage(codecHint, categoryHint, details);
    }

    private static string BuildPrePlayErrorMessage(Exception ex, string codec)
    {
        var codecHint = VideoPlaybackCopy.GetCodecSpecificHint(codec);
        var reason = VideoPlaybackCopy.ClassifyPrePlayException(ex);
        var codecLine = string.IsNullOrEmpty(codec)
            ? VideoPlaybackCopy.CodecUnknownLine()
            : codec;
        var details = VideoPlaybackCopy.FormatPrePlayDetails(ex.GetType().Name, codecLine, ex.Message);
        return VideoPlaybackCopy.ComposeErrorMessage(codecHint, reason, details);
    }

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
        IsVideoPreparing = false;
        if (oldPlayer is null && oldPath is null) return;

        if (oldPlayer is not null)
        {
            try
            {
                // Unsubscribe BEFORE dispose so a synchronous final
                // MediaFailed from the teardown can't reach a half-
                // disposed handler. Named handlers only — lambdas
                // wouldn't unsubscribe cleanly.
                oldPlayer.MediaOpened -= OnMediaPlayerOpened;
                oldPlayer.MediaFailed -= OnMediaPlayerFailed;
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
                Trace.TraceError($"ViewerViewModel.StopAndDisposePlayer error: {ex.GetType().Name}: {ex.Message}");
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
                Trace.TraceError($"ViewerViewModel.StopAndDisposePlayer: release playback path error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    partial void OnDisplayImageChanged(WinImageSource? value)
    {
        DisplayImageChanged?.Invoke(this, EventArgs.Empty);
        if (value is BitmapImage bmp)
        {
            DisplayActualWidth = bmp.PixelWidth;
            DisplayActualHeight = bmp.PixelHeight;
        }
        else
        {
            DisplayActualWidth = SelectedItem?.OriginalWidth ?? 0;
            DisplayActualHeight = SelectedItem?.OriginalHeight ?? 0;
        }
    }

    partial void OnSelectedItemChanged(MediaItem? value)
    {
        DisplayActualWidth = 0;
        DisplayActualHeight = 0;
        // Pinned hint cleanup: a new item means the old error (codec
        // mismatch / decode failure / file error) is no longer
        // relevant. Clearing here covers every navigation path
        // (Next / Prev / Delete / OpenItem / Close — Close sets
        // SelectedItem = null before navigating back to gallery)
        // without sprinkling the same line through each navigation
        // method. Subsequent failures on the new item set
        // ErrorMessage fresh.
        ErrorMessage = null;
    }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial int CurrentIndex { get; set; }

    [ObservableProperty]
    public partial int ItemCount { get; set; }

    private int _displayIndex = 1;
    public int DisplayIndex
    {
        get => _displayIndex;
        private set => SetProperty(ref _displayIndex, value);
    }

    public ObservableCollection<MediaItem> Items => _galleryViewModel.Items;

    public bool CanLoadMore => _galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore;
    public bool IsLoadingMore => _galleryViewModel.IsLoadingMore;
    public Visibility LoadMoreVisibility => _galleryViewModel.CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
    {
        if (_galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore)
        {
            await _galleryViewModel.LoadMoreAsync();
        }
    }

    public ViewerViewModel(GalleryViewModel galleryViewModel, IImageLoader imageLoader, IVideoMetadataService videoMetadataService, INavigationService navigationService, IDialogService dialogService, ISettingsService settingsService)
    {
        _galleryViewModel = galleryViewModel;
        _imageLoader = imageLoader;
        _videoMetadataService = videoMetadataService;
        _navigationService = navigationService;
        _dialogService = dialogService;
        _settingsService = settingsService;

        // Hydrate persisted preferences. Setting the property here runs
        // the OnXxxChanged partial method (because [ObservableProperty]
        // generates a real setter), which writes the same value back to
        // settings — redundant but harmless. The OnXxxChanged methods
        // null-check _settingsService so the partial-method calls during
        // field initialisation (which run before this constructor body)
        // don't NRE.
        IsSlideshowLooping = _settingsService.Current.SlideshowLoop;
        IsSlideshowShuffling = _settingsService.Current.SlideshowShuffle;
        SlideshowInterval = _settingsService.Current.SlideshowInterval;
        Volume = _settingsService.Current.VideoVolume;

        // Named handlers (not lambdas) so Dispose can unsubscribe — avoids lambda
        // captures keeping this singleton alive past the App's lifetime.
        Items.CollectionChanged += OnItemsCollectionChanged;
        _galleryViewModel.PropertyChanged += OnGalleryPropertyChanged;
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        ItemCount = Items.Count;
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
    }

    // 监听 Gallery 的增量加载状态变化，以便单图模式下的 Next 按钮和 Load More 按钮能正确启用/显示
    private void OnGalleryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GalleryViewModel.CanLoadMore) ||
            e.PropertyName == nameof(GalleryViewModel.IsLoadingMore))
        {
            LoadMoreCommand.NotifyCanExecuteChanged();
            NavigateNextCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanLoadMore));
            OnPropertyChanged(nameof(IsLoadingMore));
            OnPropertyChanged(nameof(LoadMoreVisibility));
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

    private bool CanNavigatePrevious() => Items.Count > 0 && CurrentIndex > 0;

    /// <summary>
    /// 在单图模式下支持“到底自动加载更多”：
    /// 当到达当前已加载图片的末尾，但 Gallery 还有更多图片时，Next 按钮仍然可用。
    /// </summary>
    private bool CanNavigateNext() => Items.Count > 0 && (CurrentIndex < Items.Count - 1 || _galleryViewModel.CanLoadMore);

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private async Task NavigatePreviousAsync()
    {
        if (CanNavigatePrevious())
        {
            // Stop any active video playback before switching items — the
            // new image's FullImage is the only thing we want decoded next.
            StopAndDisposePlayer();
            CurrentIndex--;
            await ShowSelectedItemAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NavigateNextAsync()
    {
        // Same navigation-boundary cleanup as NavigatePreviousAsync.
        StopAndDisposePlayer();
        if (CurrentIndex < Items.Count - 1)
        {
            // 正常前进
            CurrentIndex++;
            await ShowSelectedItemAsync();
        }
        else if (_galleryViewModel.CanLoadMore && !_galleryViewModel.IsLoadingMore)
        {
            // 到达当前批次末尾（“到底”）→ 自动触发加载更多（单图模式下的 Load More）
            await _galleryViewModel.LoadMoreAsync();

            // 加载完成后，如果有新图片，则自动前进到下一张
            if (CurrentIndex < Items.Count - 1)
            {
                CurrentIndex++;
                await ShowSelectedItemAsync();
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

        // Stop the slideshow timer so the dispatcher doesn't try to
        // call NavigateNextCommand after the page is gone.
        StopSlideshow();

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = null;

        _fullResCts?.Cancel();
        _fullResCts?.Dispose();
        _fullResCts = null;

        SelectedItem = null;
        DisplayImage = null;

        _navigationService.GoBack();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem == null) return;

        var itemToDelete = SelectedItem;
        var indexToDelete = CurrentIndex;
        var wasLastItem = CurrentIndex >= Items.Count - 1;

        await _galleryViewModel.DeleteItemAsync(itemToDelete);

        if (Items.Count == 0)
        {
            Close();
            return;
        }

        var newIndex = wasLastItem ? Math.Max(0, indexToDelete - 1) : indexToDelete;
        newIndex = Math.Min(newIndex, Items.Count - 1);
        CurrentIndex = newIndex;
        if (Items.Count > 0 && newIndex >= 0 && newIndex < Items.Count)
        {
            SelectedItem = Items[newIndex];
        }
        else
        {
            Close();
            return;
        }
        ItemCount = Items.Count;
        DisplayImage = null;
        // After a delete the navigation target is fresh, so the previous
        // item's player (if any) was already torn down by the gallery
        // deleting the source MediaItem. Re-call for safety in case the
        // VM somehow outlived the index change.
        StopAndDisposePlayer();
        ResetLoadCts();
        var fitTargetSize = IsFitMode ? (int?)FullImageMaxSize : null;
        await LoadFullImageAsync(SelectedItem, fitTargetSize, _loadCts!.Token);
        SchedulePreload();
    }

    public async Task OpenItem(MediaItem item)
    {
        // Navigation boundary: tear down any active player from the
        // previous (gallery) item before binding the new one.
        StopAndDisposePlayer();
        ResetLoadCts();

        // Clear the previous image immediately so the user doesn't see a stale
        // bitmap while the new one is decoding. The ProgressRing overlay in
        // the view covers the brief blank state.
        DisplayImage = null;

        SelectedItem = item;
        CurrentIndex = Items.IndexOf(item);
        _galleryViewModel.LastViewedIndex = CurrentIndex;
        ItemCount = Items.Count;

        var fitTargetSize = IsFitMode ? (int?)FullImageMaxSize : null;
        await LoadFullImageAsync(item, fitTargetSize, _loadCts!.Token);
        SchedulePreload();
    }

    private async Task LoadFullImageAsync(MediaItem item, int? targetMaxSize, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        if (item.FullImage != null && ShouldReuseFullImageCache(item, targetMaxSize))
        {
            DisplayImage = item.FullImage;
            return;
        }

        IsLoading = true;
        try
        {
            var source = await _imageLoader.LoadFullImageAsync(item.Source, targetMaxSize, ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (source != null)
            {
                item.FullImage = source;
                if (ReferenceEquals(SelectedItem, item))
                {
                    DisplayImage = source;
                }
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

    private static bool ShouldReuseFullImageCache(MediaItem item, int? targetMaxSize)
    {
        if (item.FullImage is not BitmapImage) return true;
        if (item.Source.Kind == MediaKind.Video) return true; // Videos reuse the FFmpeg-extracted poster frame (set in gallery); BitmapDecoder path does not work on video files.
        // For Fit (capped request) we happily reuse even a larger (full-res) cache.
        if (targetMaxSize.HasValue) return true;
        // For 1:1 (native request) only reuse if the cached version is essentially native size.
        if (item.OriginalWidth <= 0 || item.OriginalHeight <= 0) return true;
        const double tolerance = 0.99;
        var bmp = (BitmapImage)item.FullImage;
        return bmp.PixelWidth >= item.OriginalWidth * tolerance &&
               bmp.PixelHeight >= item.OriginalHeight * tolerance;
    }

    private async Task ShowSelectedItemAsync()
    {
        if (CurrentIndex >= 0 && CurrentIndex < Items.Count)
        {
            SelectedItem = Items[CurrentIndex];
            ResetLoadCts();
            var fitTargetSize = IsFitMode ? (int?)FullImageMaxSize : null;
            await LoadFullImageAsync(SelectedItem, fitTargetSize, _loadCts!.Token);
            SchedulePreload();
        }
    }

    private void SchedulePreload()
    {
        _preloadCts?.Cancel();
        _preloadCts?.Dispose();
        _preloadCts = new CancellationTokenSource();
        var preloadToken = _preloadCts.Token;
        _ = PreloadAdjacentAsync(preloadToken);
    }

    private async Task PreloadAdjacentAsync(CancellationToken ct)
    {
        var nextIdx = CurrentIndex + 1;
        var prevIdx = CurrentIndex - 1;

        var preloadTasks = new List<Task>(2);
        if (nextIdx < Items.Count)
        {
            preloadTasks.Add(PreloadItemAsync(Items[nextIdx], ct));
        }
        if (prevIdx >= 0)
        {
            preloadTasks.Add(PreloadItemAsync(Items[prevIdx], ct));
        }
        if (preloadTasks.Count == 0) return;

        try
        {
            await Task.WhenAll(preloadTasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"PreloadAdjacentAsync error: {ex.Message}");
        }
    }

    private async Task PreloadItemAsync(MediaItem item, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || item.FullImage != null) return;
        try
        {
            var source = await _imageLoader.LoadFullImageAsync(item.Source, FullImageMaxSize, ct);
            if (!ct.IsCancellationRequested && source != null)
            {
                // Do not overwrite a full-res version (from 1:1 visit) with a capped preload result.
                if (item.FullImage is BitmapImage existing &&
                    item.OriginalWidth > 0 && item.OriginalHeight > 0)
                {
                    const double tolerance = 0.99;
                    if (existing.PixelWidth >= item.OriginalWidth * tolerance &&
                        existing.PixelHeight >= item.OriginalHeight * tolerance)
                    {
                        return;
                    }
                }
                item.FullImage = source;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"PreloadItemAsync error for {item?.Id}: {ex.Message}");
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

        _fullResCts?.Cancel();
        _fullResCts?.Dispose();
        _fullResCts = null;
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
            StopSlideshow();
            // Clear the pinned error hint so the singleton doesn't
            // outlive its window with a stale message. The InfoBar
            // binding collapses as a side effect of ErrorMessage
            // becoming null.
            ErrorMessage = null;

            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = null;

            _preloadCts?.Cancel();
            _preloadCts?.Dispose();
            _preloadCts = null;

            _fullResCts?.Cancel();
            _fullResCts?.Dispose();
            _fullResCts = null;

            // Unsubscribe event handlers to break the reference cycle and let
            // the singleton be collected if the DI container is ever disposed.
            Items.CollectionChanged -= OnItemsCollectionChanged;
            _galleryViewModel.PropertyChanged -= OnGalleryPropertyChanged;
        }
        _disposed = true;
    }
}
