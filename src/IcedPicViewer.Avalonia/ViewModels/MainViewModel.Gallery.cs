// Copyright (c) IcedPicViewer. All rights reserved.

using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Avalonia.ViewModels;

/// <summary>
/// Gallery scanning, batching, draining, directory watching, and item removal.
/// </summary>
public partial class MainViewModel
{
    // ── Directory load ──────────────────────────────────────────────

    public async Task LoadDirectoryAsync(string path)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        StopSlideshow();
        StopGif();
        StopVideo();
        StopWatcher();

        Items.Clear();
        SelectedItem = null;
        IsViewerOpen = false;
        FolderPath = path;
        DiscoveredCount = 0;
        CanLoadMore = false;
        _scanComplete = false;
        _scanErrors = 0;
        lock (_remainingLock) _remaining = new List<ImageSource>();
        IsBusy = true;
        StatusText = "Scanning…";
        RefreshCommand.NotifyCanExecuteChanged();

        StartWatcher(path);
        _ = Task.Run(() => RunScanAndBatchAsync(path, ct), ct);
        await Task.CompletedTask;
    }

    // ── File watcher ─────────────────────────────────────────────────

    private void StartWatcher(string path)
    {
        try
        {
            _watcher = _scanner.Watch(path, recursive: true, OnFileChanged);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"StartWatcher: {ex.Message}");
        }
    }

    private void StopWatcher()
    {
        try { _watcher?.Dispose(); }
        catch (Exception ex) { Trace.TraceError($"StopWatcher: {ex.Message}"); }
        _watcher = null;
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        if (DirectoryScanner.IsRecycleBin(info.Path)) return;

        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                HandleFileChangeOnUi(info);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"HandleFileChangeOnUi: {ex.Message}");
            }
        });
    }

    private void HandleFileChangeOnUi(FileChangeInfo info)
    {
        switch (info.ChangeType)
        {
            case WatchChangeType.Deleted:
                RemoveByPath(info.Path);
                break;

            case WatchChangeType.Created:
                if (ArchiveHelper.IsArchiveFileName(info.Path))
                {
                    _ = IngestNewArchiveAsync(info.Path);
                    break;
                }
                if (!MediaCatalog.IsSupported(info.Path)) return;
                EnqueueNewSource(ImageSource.FromFile(info.Path, MediaCatalog.GetKind(info.Path)));
                break;

            case WatchChangeType.Renamed:
                if (!string.IsNullOrEmpty(info.OldPath))
                    RemoveByPath(info.OldPath);
                if (MediaCatalog.IsSupported(info.Path) && File.Exists(info.Path))
                    EnqueueNewSource(ImageSource.FromFile(info.Path, MediaCatalog.GetKind(info.Path)));
                break;

            case WatchChangeType.Modified:
                break;
        }
    }

    private async Task IngestNewArchiveAsync(string archivePath)
    {
        try
        {
            if (!File.Exists(archivePath) || !ArchiveHelper.IsArchive(archivePath)) return;
            var extSet = new HashSet<string>(MediaCatalog.ImageExtensions.Concat(MediaCatalog.VideoExtensions), StringComparer.OrdinalIgnoreCase);
            var entries = await Task.Run(() => ArchiveHelper.ListEntries(archivePath, extSet).ToList()).ConfigureAwait(true);
            foreach (var e in entries)
            {
                var kind = MediaCatalog.GetKind(e.Key);
                EnqueueNewSource(ImageSource.FromArchive(archivePath, e.Key, kind));
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"IngestNewArchiveAsync {archivePath}: {ex.Message}");
        }
    }

    // ── Enqueue / removal ────────────────────────────────────────────

    private void EnqueueNewSource(ImageSource source)
    {
        var id = source.ToString();
        if (Items.Any(i => i.Source.ToString() == id)) return;
        lock (_remainingLock)
        {
            if (_remaining.Any(s => s.ToString() == id)) return;
            _remaining.Add(source);
        }
        DiscoveredCount++;
        UpdateCanLoadMore();
        var ct = _loadCts?.Token ?? CancellationToken.None;
        _ = DrainPageFillAsync(ct);
        UpdateStatus();
    }

    private void RemoveByPath(string path)
    {
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            var s = Items[i].Source;
            if (string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ToString(), path, StringComparison.OrdinalIgnoreCase))
            {
                var item = Items[i];
                Items.RemoveAt(i);
                if (SelectedItem == item)
                {
                    SelectedItem = null;
                    IsViewerOpen = false;
                }
            }
        }

        lock (_remainingLock)
        {
            _remaining.RemoveAll(s =>
                string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)
                || string.Equals(s.ToString(), path, StringComparison.OrdinalIgnoreCase));
        }

        UpdateCanLoadMore();
        UpdateStatus();
        _shuffleQueue.Clear();
        _lastShuffleIndex = -1;
    }

    private void RemoveItemEverywhere(GalleryItemViewModel item)
    {
        var idx = Items.IndexOf(item);
        if (idx >= 0) Items.RemoveAt(idx);

        var id = item.Source.ToString();
        lock (_remainingLock)
            _remaining.RemoveAll(s => s.ToString() == id);

        if (SelectedItem == item)
        {
            if (idx >= 0 && idx < Items.Count)
                OpenItem(Items[idx]);
            else if (idx > 0 && idx - 1 < Items.Count)
                OpenItem(Items[idx - 1]);
            else
            {
                SelectedItem = null;
                IsViewerOpen = false;
            }
        }

        UpdateCanLoadMore();
        UpdateStatus();
        NavigatePreviousCommand.NotifyCanExecuteChanged();
        NavigateNextCommand.NotifyCanExecuteChanged();
        _shuffleQueue.Clear();
        _lastShuffleIndex = -1;
    }

    // ── Scan & batch ─────────────────────────────────────────────────

    private async Task RunScanAndBatchAsync(string path, CancellationToken ct)
    {
        var batch = new List<ImageSource>(ScanBatchSize);
        long batchStartTick = 0;
        var discovered = 0;

        try
        {
            await foreach (var source in _scanner.ScanAsync(
                               path,
                               recursive: true,
                               extensions: MediaCatalog.SupportedMedia,
                               errorReporter: new Progress<ScanError>(_ => Interlocked.Increment(ref _scanErrors)),
                               discoveredReporter: null,
                               currentPathReporter: null,
                               ct: ct).ConfigureAwait(false))
            {
                if (batch.Count == 0)
                    batchStartTick = Environment.TickCount64;

                batch.Add(source);
                discovered++;

                var elapsed = Environment.TickCount64 - batchStartTick;
                if (batch.Count >= ScanBatchSize || elapsed >= ScanBatchMs)
                {
                    FlushBatch(batch, discovered, ct);
                    batch = new List<ImageSource>(ScanBatchSize);
                }
            }

            if (batch.Count > 0)
                FlushBatch(batch, discovered, ct);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ct.IsCancellationRequested) return;
                _scanComplete = true;
                IsBusy = false;
                UpdateStatus();
                RefreshCommand.NotifyCanExecuteChanged();
                _ = DrainPageFillAsync(ct);
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                StatusText = "Cancelled";
                RefreshCommand.NotifyCanExecuteChanged();
            });
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RunScanAndBatchAsync: {ex}");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBusy = false;
                StatusText = $"Error: {ex.Message}";
                RefreshCommand.NotifyCanExecuteChanged();
            });
        }
    }

    private void FlushBatch(List<ImageSource> batch, int discovered, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || batch.Count == 0) return;

        var snapshot = batch.ToList();
        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;

            lock (_remainingLock)
                _remaining.AddRange(snapshot);

            DiscoveredCount = discovered;
            UpdateCanLoadMore();
            UpdateStatus();
            _ = DrainPageFillAsync(ct);
        });
    }

    // ── Drain & Load More ────────────────────────────────────────────

    private async Task DrainPageFillAsync(CancellationToken ct)
    {
        if (_pageFillInFlight) return;
        _pageFillInFlight = true;
        try
        {
            while (!ct.IsCancellationRequested && Items.Count < AutoCap)
            {
                List<ImageSource> chunk;
                lock (_remainingLock)
                {
                    if (_remaining.Count == 0) break;
                    var take = Math.Min(ScanPageSize, Math.Min(AutoCap - Items.Count, _remaining.Count));
                    chunk = _remaining.GetRange(0, take);
                    _remaining.RemoveRange(0, take);
                }

                foreach (var source in chunk)
                {
                    // Directory switch cancels ct and clears Items; stop adding stale sources.
                    if (ct.IsCancellationRequested) return;
                    var item = GalleryItemViewModel.FromSource(source);
                    Items.Add(item);
                    _ = LoadThumbnailFireAndForgetAsync(item, ct);
                }

                UpdateCanLoadMore();
                UpdateStatus();
                StartGallerySlideshowCommand.NotifyCanExecuteChanged();
                await Task.Yield();
            }
        }
        finally
        {
            _pageFillInFlight = false;
            if (!ct.IsCancellationRequested)
            {
                UpdateCanLoadMore();
                UpdateStatus();
                NavigateNextCommand.NotifyCanExecuteChanged();
                StartGallerySlideshowCommand.NotifyCanExecuteChanged();
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoadMoreCommand))]
    private async Task LoadMoreAsync()
    {
        if (!CanLoadMore || IsLoadingMore) return;
        IsLoadingMore = true;
        try
        {
            List<ImageSource> chunk;
            lock (_remainingLock)
            {
                if (_remaining.Count == 0) return;
                var take = Math.Min(PageSize, _remaining.Count);
                chunk = _remaining.GetRange(0, take);
                _remaining.RemoveRange(0, take);
            }

            var ct = _loadCts?.Token ?? CancellationToken.None;
            foreach (var source in chunk)
            {
                if (ct.IsCancellationRequested) return;
                var item = GalleryItemViewModel.FromSource(source);
                Items.Add(item);
                _ = LoadThumbnailFireAndForgetAsync(item, ct);
            }

            UpdateCanLoadMore();
            UpdateStatus();
            NavigateNextCommand.NotifyCanExecuteChanged();
            await Task.CompletedTask;
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private bool CanLoadMoreCommand() => CanLoadMore && !IsLoadingMore;

    // ── Status helpers ───────────────────────────────────────────────

    private void UpdateCanLoadMore()
    {
        int remaining;
        lock (_remainingLock) remaining = _remaining.Count;
        CanLoadMore = remaining > 0;
    }

    private void UpdateStatus()
    {
        int remaining;
        lock (_remainingLock) remaining = _remaining.Count;
        var err = _scanErrors > 0 ? $" · {_scanErrors} scan error(s)" : "";
        var videos = Items.Count(i => i.IsVideo);
        var images = Items.Count - videos;
        var loaded = videos > 0
            ? $"{images} images, {videos} videos"
            : $"{images} image(s)";

        if (IsBusy || !_scanComplete)
        {
            StatusText = $"Scanning… found {DiscoveredCount}, showing {loaded}{err}";
            return;
        }

        StatusText = remaining > 0
            ? $"Showing {loaded} / {DiscoveredCount} ({remaining} more){err}"
            : $"Loaded {loaded}{err}";
    }
}
