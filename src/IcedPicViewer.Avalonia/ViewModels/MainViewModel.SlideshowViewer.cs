// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Avalonia.Services;
using IcedPicViewer.Core.Text;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;

namespace IcedPicViewer.Avalonia.ViewModels;

/// <summary>
/// Viewer navigation, slideshow, video / GIF playback, and full‑image loading.
/// </summary>
public partial class MainViewModel
{
    // ── Slideshow ────────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleSlideshow()
    {
        if (IsSlideshowActive) StopSlideshow();
        else StartSlideshow();
    }

    [RelayCommand]
    private void ToggleSlideshowLoop() => IsSlideshowLooping = !IsSlideshowLooping;

    [RelayCommand]
    private void ToggleSlideshowShuffle() => IsSlideshowShuffling = !IsSlideshowShuffling;

    [RelayCommand(CanExecute = nameof(CanStartGallerySlideshow))]
    private void StartGallerySlideshow()
    {
        if (Items.Count == 0) return;
        var start = SelectedItem is not null && Items.Contains(SelectedItem)
            ? SelectedItem
            : Items.FirstOrDefault(i => !i.IsVideo) ?? Items[0];
        OpenItem(start);
        StartSlideshow();
    }

    private bool CanStartGallerySlideshow() => Items.Count > 0;

    public void StartSlideshow()
    {
        if (Items.Count == 0) return;

        if (SelectedItem is null || !Items.Contains(SelectedItem))
        {
            var first = Items.FirstOrDefault(i => !i.IsVideo) ?? Items[0];
            OpenItem(first);
        }
        else if (!IsViewerOpen)
        {
            OpenItem(SelectedItem);
        }

        // Full reset so restart doesn't leave a stopped timer with a live Tick handler.
        StopSlideshow();
        _slideshowTimer = new DispatcherTimer(
            TimeSpan.FromSeconds(Math.Clamp(SlideshowInterval, 1.0, 30.0)),
            DispatcherPriority.Normal,
            OnSlideshowTick);
        _slideshowTimer.Start();
        IsSlideshowActive = true;
        StatusText = GalleryStatusFormatter.FormatSlideshowActive(
            SlideshowInterval, IsSlideshowLooping, IsSlideshowShuffling);
    }

    public void StopSlideshow()
    {
        if (_slideshowTimer is not null)
        {
            _slideshowTimer.Stop();
            _slideshowTimer.Tick -= OnSlideshowTick;
            _slideshowTimer = null;
        }
        _slideshowLoadMoreInFlight = false;
        IsSlideshowActive = false;
    }

    private async void OnSlideshowTick(object? sender, EventArgs e)
    {
        try
        {
            await AdvanceSlideshowAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"OnSlideshowTick: {ex.Message}");
            StopSlideshow();
        }
    }

    private async Task AdvanceSlideshowAsync()
    {
        if (Items.Count == 0)
        {
            StopSlideshow();
            return;
        }

        var next = IsSlideshowShuffling
            ? PickShuffleNext()
            : PickSequentialNext();

        if (next is null)
        {
            // Soft-wait only while a load-more for slideshow is already in flight.
            // Otherwise we would spin forever if CanLoadMore stays true but nothing advances.
            if (CanLoadMore && _slideshowLoadMoreInFlight)
                return;
            if (CanLoadMore && !IsLoadingMore)
            {
                _ = LoadMoreThenContinueSlideshowAsync();
                return;
            }
            if (CanLoadMore)
                return;
            StopSlideshow();
            StatusText = GalleryStatusFormatter.FormatSlideshowFinished();
            return;
        }

        OpenItem(next);
        if (next.IsVideo && next.FullImage is null && next.Thumbnail is null)
        {
            var skip = IsSlideshowShuffling ? PickShuffleNext() : PickSequentialNext();
            if (skip is not null) OpenItem(skip);
        }

        await Task.CompletedTask;
    }

    private MediaItemViewModel? PickSequentialNext()
    {
        if (Items.Count == 0) return null;

        var current = SelectedItem is null ? -1 : Items.IndexOf(SelectedItem);
        var nextIndex = current + 1;

        if (nextIndex >= Items.Count)
        {
            if (IsSlideshowLooping)
            {
                nextIndex = 0;
            }
            else if (CanLoadMore && !IsLoadingMore && !_slideshowLoadMoreInFlight)
            {
                _ = LoadMoreThenContinueSlideshowAsync();
                return null;
            }
            else
            {
                return null;
            }
        }

        return Items[nextIndex];
    }

    private async Task LoadMoreThenContinueSlideshowAsync()
    {
        if (_slideshowLoadMoreInFlight) return;
        _slideshowLoadMoreInFlight = true;
        var countBefore = Items.Count;
        try
        {
            await LoadMoreAsync().ConfigureAwait(true);
            if (!IsSlideshowActive) return;

            var after = PickSequentialNext();
            if (after is not null)
            {
                OpenItem(after);
                return;
            }

            // Load more finished but we still cannot advance past the current end.
            if (Items.Count <= countBefore || !CanLoadMore)
            {
                StopSlideshow();
                StatusText = GalleryStatusFormatter.FormatSlideshowFinished();
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadMoreThenContinueSlideshowAsync: {ex.Message}");
            if (IsSlideshowActive)
            {
                StopSlideshow();
                StatusText = GalleryStatusFormatter.FormatSlideshowFinished();
            }
        }
        finally
        {
            _slideshowLoadMoreInFlight = false;
        }
    }

    private MediaItemViewModel? PickShuffleNext()
    {
        if (_shuffleQueue.Count == 0)
            RefillShuffleQueue();

        if (_shuffleQueue.Count == 0) return null;

        var idx = _shuffleQueue[0];
        _shuffleQueue.RemoveAt(0);
        _lastShuffleIndex = idx;
        if (idx < 0 || idx >= Items.Count) return PickShuffleNext();
        return Items[idx];
    }

    private void RefillShuffleQueue()
    {
        _shuffleQueue.Clear();
        for (var i = 0; i < Items.Count; i++)
            _shuffleQueue.Add(i);

        for (var i = _shuffleQueue.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (_shuffleQueue[i], _shuffleQueue[j]) = (_shuffleQueue[j], _shuffleQueue[i]);
        }

        if (_shuffleQueue.Count > 1 && _lastShuffleIndex >= 0
            && _shuffleQueue[0] == _lastShuffleIndex)
        {
            var swap = Random.Shared.Next(1, _shuffleQueue.Count);
            (_shuffleQueue[0], _shuffleQueue[swap]) = (_shuffleQueue[swap], _shuffleQueue[0]);
        }
    }

    // ── Viewer navigation ────────────────────────────────────────────

    [RelayCommand]
    private void CloseViewer()
    {
        StopSlideshow();
        StopVideo();
        StopGif();
        var staySelected = SelectedItem;
        IsViewerOpen = false;
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(ShowVideoPoster));
        if (staySelected is not null)
            RequestScrollToItem?.Invoke(staySelected);
    }

    [RelayCommand]
    private void ToggleFitMode() => IsFitMode = !IsFitMode;

    [RelayCommand]
    private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand]
    private void HandleSpace()
    {
        if (!IsViewerOpen) return;
        if (SelectedItem?.IsVideo == true)
            PlayPauseVideo();
        else
            ToggleSlideshow();
    }

    [RelayCommand(CanExecute = nameof(CanNavigatePrevious))]
    private void NavigatePrevious()
    {
        if (SelectedItem is null) return;
        var i = Items.IndexOf(SelectedItem);
        if (i > 0) OpenItem(Items[i - 1]);
    }

    private bool CanNavigatePrevious()
        => SelectedItem is not null && Items.IndexOf(SelectedItem) > 0;

    [RelayCommand(CanExecute = nameof(CanNavigateNext))]
    private async Task NavigateNextAsync()
    {
        if (SelectedItem is null) return;
        var i = Items.IndexOf(SelectedItem);
        if (i < 0) return;
        if (i + 1 < Items.Count)
        {
            OpenItem(Items[i + 1]);
            return;
        }

        if (CanLoadMore && !IsLoadingMore)
        {
            await LoadMoreAsync().ConfigureAwait(true);
            if (i + 1 < Items.Count)
                OpenItem(Items[i + 1]);
        }
    }

    private bool CanNavigateNext()
    {
        if (SelectedItem is null) return false;
        var i = Items.IndexOf(SelectedItem);
        if (i < 0) return false;
        return i + 1 < Items.Count || CanLoadMore;
    }

    [RelayCommand]
    private void RevealItem(MediaItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;
        _shell.RevealInFolder(item.Media.Path);
    }

    [RelayCommand(CanExecute = nameof(CanRevealSelected))]
    private void RevealSelected() => RevealItem(SelectedItem);

    private bool CanRevealSelected() => SelectedItem is not null;

    [RelayCommand]
    private async Task DeleteItemAsync(MediaItemViewModel? item)
    {
        item ??= SelectedItem;
        if (item is null) return;

        if (item.Media.IsInArchive)
        {
            StatusText = GalleryStatusFormatter.FormatArchiveDeleteNotSupported();
            if (ConfirmAsync is not null)
            {
                await ConfirmAsync(
                    UiCopy.CannotDeleteTitle,
                    UiCopy.ArchiveDeleteMessageSimple(),
                    true).ConfigureAwait(true);
            }
            return;
        }

        var path = item.Media.Path;
        var preferTrash = true;
        if (_shell.IsNetworkPath(path))
        {
            preferTrash = false;
            if (ConfirmAsync is not null)
            {
                var ok = await ConfirmAsync(
                    UiCopy.ConfirmDeleteTitle,
                    UiCopy.NetworkPermanentDeleteConfirm(path),
                    false).ConfigureAwait(true);
                if (!ok) return;
            }
        }

        if (!_shell.TryDelete(path, preferTrash, out var error))
        {
            StatusText = GalleryStatusFormatter.FormatDeleteFailed(error ?? "unknown");
            return;
        }

        RemoveItemEverywhere(item);
        StatusText = GalleryStatusFormatter.FormatDeleted(item.Name, preferTrash);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private async Task DeleteSelectedAsync() => await DeleteItemAsync(SelectedItem).ConfigureAwait(true);

    private bool CanDeleteSelected() => SelectedItem is not null && !SelectedItem.Media.IsInArchive;

    // ── Open item & full‑image loading ───────────────────────────────

    public void OpenItem(MediaItemViewModel item)
    {
        if (SelectedItem is not null && !ReferenceEquals(SelectedItem, item))
        {
            StopVideo();
            StopGif();
        }

        SelectedItem = item;
        IsViewerOpen = true;
        OnPropertyChanged(nameof(IsVideoSelected));
        OnPropertyChanged(nameof(ShowVideoPoster));
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RevealSelectedCommand.NotifyCanExecuteChanged();
        StartGallerySlideshowCommand.NotifyCanExecuteChanged();
        _ = LoadFullAsync(item);
        if (item.IsVideo)
            _ = PrepareVideoAsync(item);
    }

    private void StopGif()
    {
        _gifPlayer?.Stop();
        _gifPlayer?.Dispose();
        _gifPlayer = null;
    }

    private static bool IsGifMedia(MediaRef media)
    {
        var name = media.IsInArchive ? media.ArchiveEntry! : media.Path;
        return Path.GetExtension(name).Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private async Task PrepareVideoAsync(MediaItemViewModel item)
    {
        try
        {
            _vlc.EnsureInitialized();
            OnPropertyChanged(nameof(MediaPlayer));
            _vlc.Volume = Volume;
            var ok = await _vlc.LoadAsync(item.Media, _loadCts?.Token ?? CancellationToken.None)
                .ConfigureAwait(true);
            if (!ok)
                StatusText = GalleryStatusFormatter.FormatVideoLoadFailed();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"PrepareVideoAsync: {ex.Message}");
            StatusText = GalleryStatusFormatter.FormatVideoError(ex.Message);
        }
    }

    // ── Video & GIF ──────────────────────────────────────────────────

    [RelayCommand]
    private void PlayPauseVideo()
    {
        if (SelectedItem?.IsVideo != true) return;
        if (IsSlideshowActive) StopSlideshow();
        _vlc.EnsureInitialized();
        OnPropertyChanged(nameof(MediaPlayer));
        _vlc.Volume = Volume;
        _vlc.TogglePlayPause();
        IsVideoPlaying = _vlc.IsPlaying;
        OnPropertyChanged(nameof(ShowVideoPoster));
    }

    private void StopVideo()
    {
        _vlc.Stop();
        IsVideoPlaying = false;
        OnPropertyChanged(nameof(ShowVideoPoster));
    }

    public void SeekVideoToPercent(int percent)
    {
        if (SelectedItem?.IsVideo != true) return;
        _vlc.SeekFraction(percent / 100.0);
        if (!_vlc.IsPlaying)
        {
            _vlc.Play();
            IsVideoPlaying = true;
            OnPropertyChanged(nameof(ShowVideoPoster));
        }
    }

    // ── Full‑image async loading ─────────────────────────────────────

    private async Task LoadThumbnailAsync(MediaItemViewModel item, CancellationToken ct)
    {
        try
        {
            await _thumbnailLoadSemaphore.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var (bmp, ow, oh, duration) = await AvaloniaMediaLoader.LoadThumbnailWithInfoAsync(
                    item.Media, ThumbMaxEdge, ct)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => item.ApplyThumbnail(bmp, ow, oh, duration));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Trace.TraceError($"thumb {item.Media}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => item.IsThumbnailLoading = false);
        }
        finally
        {
            try { _thumbnailLoadSemaphore.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task LoadFullAsync(MediaItemViewModel item)
    {
        var isGif = IsGifMedia(item.Media);
        if (item.FullImage is not null && !isGif && item.Media.Kind != MediaKind.Video)
            return;

        var ct = _loadCts?.Token ?? CancellationToken.None;
        item.IsFullImageLoading = true;
        try
        {
            if (isGif)
            {
                await LoadGifAsync(item, ct).ConfigureAwait(false);
                return;
            }

            var maxEdge = item.Media.Kind == MediaKind.Video ? ThumbMaxEdge * 4 : FullMaxEdge;
            var bmp = await AvaloniaMediaLoader.LoadFullAsync(item.Media, maxEdge, ct)
                .ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (SelectedItem == item)
                    item.FullImage = bmp;
                item.IsFullImageLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"full {item.Media}: {ex.Message}");
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
        }
    }

    private async Task LoadGifAsync(MediaItemViewModel item, CancellationToken ct)
    {
        await using var stream = await OpenMediaStreamAsync(item.Media, ct).ConfigureAwait(false);
        if (stream is null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => item.IsFullImageLoading = false);
            return;
        }

        var player = await GifAnimationPlayer.TryLoadAsync(stream, FullMaxEdge, ct).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (SelectedItem != item)
            {
                player?.Dispose();
                item.IsFullImageLoading = false;
                return;
            }

            StopGif();
            _gifPlayer = player;
            item.IsFullImageLoading = false;

            if (_gifPlayer is null)
                return;

            if (_gifPlayer.HasAnimation)
            {
                _gifPlayer.Start(frame =>
                {
                    if (SelectedItem == item)
                        item.FullImage = frame;
                });
            }
            else
            {
                item.FullImage = _gifPlayer.FirstFrame;
            }
        });
    }

    private static async Task<Stream?> OpenMediaStreamAsync(MediaRef media, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (media.IsInArchive)
        {
            try
            {
                return ArchiveHelper.OpenEntryStream(media.Path, media.ArchiveEntry!);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"OpenMediaStreamAsync archive: {ex.Message}");
                return null;
            }
        }

        if (!File.Exists(media.Path)) return null;
        return new FileStream(media.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
    }
}
