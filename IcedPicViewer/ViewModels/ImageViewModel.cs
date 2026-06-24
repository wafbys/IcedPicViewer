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
    private readonly IDialogService _dialogService;
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
    [NotifyPropertyChangedFor(nameof(PlayerFitContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerActualSizeContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
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
    }

    public string SlideshowGlyph => IsSlideshowActive ? "\uE71A" /* Stop */ : "\uE768" /* Play */;
    public string SlideshowLabel => IsSlideshowActive ? "Stop Slideshow" : "Start Slideshow";
    public string SlideshowTooltip => IsSlideshowActive
        ? "Stop slideshow"
        : $"Start slideshow (auto-advance every {SlideshowInterval:0.#}s)";

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
    public string SlideshowLoopLabel => IsSlideshowLooping ? "Loop On" : "Loop Off";
    public string SlideshowLoopTooltip => IsSlideshowLooping
        ? "Looping: slideshow wraps from last image back to first"
        : "Off: slideshow stops at the last image";

    // Shuffle button — pick a random next index on each tick instead
    // of incrementing CurrentIndex. Single Random instance per VM
    // is sufficient (the slideshow is a singleton; new Random is
    // only needed when many threads contend, which the dispatcher
    // timer pattern doesn't).
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "x:Bind target — the property's value is read by XAML through an instance member access; the static-friendly body is intentional.")]
    public string SlideshowShuffleGlyph => "\uE8B1"; // Shuffle
    public string SlideshowShuffleLabel => IsSlideshowShuffling ? "Shuffle On" : "Shuffle Off";
    public string SlideshowShuffleTooltip => IsSlideshowShuffling
        ? "Shuffling: slideshow picks a random next image each tick"
        : "Sequential: slideshow advances in order";

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
    }

    /// <summary>
    /// Human-readable "5" / "7.5" text for display next to the
    /// slider. Re-raises on <see cref="SlideshowInterval"/>
    /// changes via the <c>[NotifyPropertyChangedFor]</c> attribute
    /// above, so the label tracks the slider value live.
    /// </summary>
    public string SlideshowIntervalText => SlideshowInterval.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

    private DispatcherQueueTimer? _slideshowTimer;

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
        if (Images.Count == 0) return;

        // End-of-set policy. Skipped in shuffle mode: a shuffle pick
        // can land on any image (including past the current), so the
        // conventional "reached the last image" check doesn't apply
        // and would just falsely stop the slideshow. Sequential mode
        // still respects the loop / no-loop choice.
        if (!IsSlideshowShuffling && CurrentIndex >= Images.Count - 1 && !IsSlideshowLooping)
        {
            StopSlideshow();
            return;
        }

        if (IsSlideshowShuffling && Images.Count > 1)
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
            // which is the path that calls ShowCurrentImageAsync and
            // populates DisplayImage for the view. Without this
            // explicit call, the index updates in the UI (DisplayIndex
            // + NavigationChanged) but the bitmap doesn't refresh —
            // the viewer keeps showing the previous image.
            await ShowCurrentImageAsync();
        }
        else if (IsSlideshowLooping && CurrentIndex >= Images.Count - 1)
        {
            // Loop semantics: wrap to the first image rather than
            // calling NavigateNext (which would short-circuit at
            // the end via CanNavigateNext). Same caveat as the shuffle
            // branch: direct CurrentIndex set means we have to drive
            // ShowCurrentImageAsync ourselves.
            CurrentIndex = 0;
            await ShowCurrentImageAsync();
        }
        else
        {
            _ = NavigateNextCommand.ExecuteAsync(null);
        }
    }

    /// <summary>
    /// Build a new shuffled permutation of <c>[0, Images.Count)</c>
    /// and enqueue it. Fisher-Yates so the distribution is uniform.
    /// If the first element of the new queue would equal
    /// <c>_lastShuffleIndex</c> (the image shown at the end of the
    /// previous cycle), swap it with a random later position so the
    /// boundary doesn't show the same image twice in a row.
    /// </summary>
    private void RefillShuffleQueue()
    {
        var n = Images.Count;
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
    [NotifyPropertyChangedFor(nameof(PlayerFitContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerActualSizeContainerVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayerStretch))]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool IsVideoPlaying { get; set; }

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
    public Visibility PrePlayStripVisibility => IsVideo && !IsVideoPlaying ? Visibility.Visible : Visibility.Collapsed;

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
    public partial double Volume { get; set; } = 1.0;

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
                Trace.TraceWarning($"ImageViewModel.OnVolumeChanged: failed to push to MediaPlayer: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// "0:42" / "1:23:45" formatted duration for the pre-playback strip.
    /// Empty when the current item isn't a video, so binding it directly
    /// to a TextBlock produces "—" naturally for images.
    /// </summary>
    public string PrePlayDurationText =>
        CurrentImage is VideoItem v && v.Duration > TimeSpan.Zero
            ? FormatDuration(v.Duration)
            : string.Empty;

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
    // the UI thread, log it (Trace + the same kbd.log-style diagnostics
    // path the rest of the VM uses), and show a ContentDialog so the
    // user actually knows what happened.
    // ----------------------------------------------------------------

    private void OnMediaPlayerOpened(MediaPlayer sender, object args)
    {
        Trace.TraceInformation($"ImageViewModel: MediaPlayer opened for {CurrentImage?.Id}");
    }

    private void OnMediaPlayerFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // MediaFailed fires on a non-UI thread. The dispatcher's
        // TryEnqueue queues a work item; if the VM is already disposed
        // (player torn down mid-shutdown) the call returns false and we
        // bail — no point showing a dialog to a window that's gone.
        if (!_dispatcher.TryEnqueue(() => HandleMediaFailedOnUiThread(args)))
        {
            Trace.TraceWarning($"ImageViewModel: MediaFailed dropped (dispatcher unavailable): code=0x{args.ExtendedErrorCode:X8}, msg={args.ErrorMessage}");
        }
    }

    private void HandleMediaFailedOnUiThread(MediaPlayerFailedEventArgs args)
    {
        // Detach from the failed player — the player is unusable past
        // this point. Calling StopAndDisposePlayer here would re-enter
        // the same path (and double-dispose if the failure happened
        // during Dispose itself), so we do a minimal reset: clear the
        // VM's MediaPlayer reference, release the temp file, flip
        // IsVideoPlaying back so the PlayOverlay button reappears for
        // retry. PlayAsync's catch block handles the temp-file cleanup
        // pattern; we mirror it here because the MediaFailed path
        // doesn't go through that catch.
        var failedPlayer = MediaPlayer;
        var failedPath = _currentPlaybackPath;
        MediaPlayer = null;
        _currentPlaybackPath = null;
        IsVideoPlaying = false;

        if (failedPlayer is not null)
        {
            try { failedPlayer.Source = null; failedPlayer.Dispose(); }
            catch (Exception ex) { Trace.TraceWarning($"ImageViewModel: failed player dispose threw: {ex.GetType().Name}: {ex.Message}"); }
        }
        if (failedPath is not null)
        {
            try { _videoMetadataService.ReleasePlaybackFilePath(failedPath); }
            catch (Exception ex) { Trace.TraceWarning($"ImageViewModel: failed release threw: {ex.GetType().Name}: {ex.Message}"); }
        }

        // Log the full diagnostic first — Trace is always safe, the
        // dialog below may fail to show (no XamlRoot, etc.) but the
        // log entry still gives us a breadcrumb for postmortem.
        Trace.TraceError($"ImageViewModel.MediaFailed for {CurrentImage?.Id}: error={args.Error}, hr=0x{args.ExtendedErrorCode:X8}, msg={args.ErrorMessage}");

        // Best-effort user-visible message. ShowInfoAsync swallows
        // "no XamlRoot" silently so we don't need a try/catch here.
        _ = _dialogService.ShowInfoAsync(
            "视频播放失败",
            BuildPlaybackErrorMessage(args),
            "关闭");
    }

    /// <summary>
    /// Turns a MediaPlayer error into a user-friendly explanation. The
    /// raw HRESULT + ErrorMessage is technically accurate but unhelpful
    /// for someone who just wants to know "why doesn't this play?". We
    /// map the most common MF / WinRT error codes to a short explanation
    /// and append the raw details so power users (or future debugging)
    /// can still see the underlying cause.
    /// </summary>
    private static string BuildPlaybackErrorMessage(MediaPlayerFailedEventArgs args)
    {
        var hint = args.Error switch
        {
            MediaPlayerError.SourceNotSupported =>
                "Media Foundation 不支持此视频格式 (SourceNotSupported)。\n\n" +
                "常见原因:\n" +
                "• 视频 codec 不被当前 Windows 版本识别 (例如 Win10 默认不带 HEVC/H.265 decoder)\n" +
                "• 容器虽正确但 codec 头损坏或非标\n\n" +
                "可尝试:用 FFmpeg / HandBrake 转封装成 H.264/AAC 的 mp4 后再播放。",
            MediaPlayerError.DecodingError =>
                "解码时发生错误 (DecodingError)。视频文件可能在传输中断、不完整,或 codec 参数异常。",
            MediaPlayerError.NetworkError =>
                "网络错误 (NetworkError)。此项目仅播放本地文件,不应触发此错误 — 可能是文件被外部 AV 扫描拦截。",
            MediaPlayerError.Aborted =>
                "播放被中止 (Aborted)。通常是用户切到下一张 / 关闭 viewer 时正在解码。",
            _ => "未知播放错误。",
        };

        var details = $"HRESULT: 0x{args.ExtendedErrorCode:X8}\n" +
                      $"类别: {args.Error}\n" +
                      $"系统消息: {args.ErrorMessage}";
        return hint + "\n\n— 详细信息 —\n" + details;
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

    public ImageViewModel(GalleryViewModel galleryViewModel, IImageLoader imageLoader, IVideoMetadataService videoMetadataService, INavigationService navigationService, IDialogService dialogService)
    {
        _galleryViewModel = galleryViewModel;
        _imageLoader = imageLoader;
        _videoMetadataService = videoMetadataService;
        _navigationService = navigationService;
        _dialogService = dialogService;

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

        // Stop the slideshow timer so the dispatcher doesn't try to
        // call NavigateNextCommand after the page is gone.
        StopSlideshow();

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
            // LoadFullImageAsync returns a BitmapImage with EXIF
            // orientation already applied at the pixel level, so a
            // 4000x3000 EXIF-6 portrait photo is decoded as a
            // 3000x4000 bitmap (PixelWidth/Height match the visible
            // orientation). The W×H text in the viewer's CommandBar
            // reads DisplayActualWidth/Height which derive from the
            // bitmap's PixelWidth/Height, so it shows the same
            // numbers the user sees on screen. The bitmap is cached
            // in item.FullImage so re-navigating to the same item
            // (e.g., after viewing a video, back to the image)
            // skips the decode + PNG-encode round-trip.
            var bitmapImage = await _imageLoader.LoadFullImageAsync(item.Source, ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (bitmapImage != null)
            {
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
            StopSlideshow();

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
