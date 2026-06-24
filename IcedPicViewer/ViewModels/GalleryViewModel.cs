// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IcedPicViewer.ViewModels;

public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly IDirectoryScanner _scanner;
    private readonly IImageLoader _imageLoader;
    private readonly IVideoMetadataService _videoMetadataService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IDialogService _dialogService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? _loadCts;
    private IDisposable? _fileWatcher;
    private bool _disposed;

    // Re-entry guard for the scan-time, fire-and-forget page fill. The
    // IngestScanBatch callback fires roughly every 50 ms during a scan and
    // each call would otherwise start a new LoadNextPageAsync — that path
    // never sets IsLoadingMore (that flag is owned by LoadMoreAsync), so
    // without this guard a whole-drive scan would end up with N concurrent
    // LoadNextPageAsync tasks, each spawning 6 GetImageSizeAsync fetchahead
    // calls, and the WinRT STA marshal back to the UI thread (60+ in
    // flight) would freeze the dispatcher. The result is a "window stops
    // updating" symptom even though the process is still alive.
    //
    // We deliberately use a private flag instead of IsLoadingMore because
    // the latter is what the "Load More" button observes — the button must
    // stay enabled when the scan is auto-filling pages in the background.
    // The flag is only ever mutated on the UI thread (IngestScanBatch runs
    // there; DrainPageFillAsync's finally runs there too), so a plain bool
    // is sufficient — no Interlocked needed.
    private bool _pageFillInFlight;

    // Limit concurrent thumbnail loads
    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(initialCount: 6, maxCount: 6);

    // Same 6-wide cap, but for the metadata + size fetch in LoadNextPageAsync.
    // Without this, 150 sequential awaits on GetImageSizeAsync would freeze
    // the perceived responsiveness for ~5 s on a populated page. The bound
    // is intentionally equal to the thumbnail cap so the two phases share
    // roughly the same number of in-flight WinRT marshal calls.
    private readonly SemaphoreSlim _sizeFetchSemaphore = new(initialCount: 6, maxCount: 6);

    // For incremental loading while keeping masonry visual.
    // Both LoadNextPageAsync (worker thread) and OnFileChanged (dispatcher thread) read/write
    // this list, so guard it with _remainingLock to avoid race conditions.
    private readonly object _remainingLock = new();
    private List<ImageSource> _remainingFilePaths = new();
    // Page size for user-triggered Load More (button click OR auto-load
    // when scrolling near the bottom). Each source is added to Images
    // individually, so PageSize items ≈ PageSize MasonryPanel layout
    // passes — at 200 that's ~75 ms on a typical machine, well under
    // the user's "feels sluggish" threshold. Going to 300 would push
    // that toward 150 ms and make aggressive scrolling visibly stutter;
    // 200 is the sweet spot for "fewer Load More clicks" without
    // paying a perceptible per-click cost. ScanPageSize stays at 30
    // because the scan-time drain is the steady-state path that
    // happens automatically while the scanner is running — see
    // DrainPageFillAsync.
    private const int PageSize = 200;

    // The scanner yields sources one at a time on a worker thread. We
    // batch up to ScanBatchSize items before dispatching to the UI thread
    // (size cap) OR flush every ~50 ms (time cap) — see RunScanAndBatchAsync
    // for why both triggers are needed.
    private const int ScanBatchSize = 100;

    // Page size used while the scan-time page fill is feeding the gallery.
    // Deliberately small so a single LoadNextPageAsync only adds 30 items
    // to the ItemsControl (≈30 layout passes on the non-virtualising
    // MasonryPanel) instead of the full 150 that a manual "Load More"
    // uses — the trade-off is "feel responsive" vs "load big chunks on
    // demand". The page fill is driven directly by IngestScanBatch (no
    // timer), so a steady stream of small pages appears as continuous
    // growth instead of a multi-second freeze.
    private const int ScanPageSize = 30;

    // O(1) source-id → item lookup. Mirrors the Images collection; maintained
    // via the CollectionChanged handler in the constructor. Keys are
    // ImageSource.ToString(), which is case-sensitive for archive entries
    // (archive keys preserve the original case) and case-insensitive-friendly
    // for loose files (Windows paths are case-insensitive, but the conflict
    // would only occur if two files differ only in case, which FileSystem
    // itself disallows on Windows). Value is MediaItem (base) because the
    // collection also holds VideoItem — a single source id maps to one
    // concrete subtype, but the lookup is per-id, not per-type.
    private readonly Dictionary<string, MediaItem> _imageIndex = new(StringComparer.Ordinal);

    // Files that the scanner encountered but could not read (e.g. a corrupt
    // .zip with a valid extension). The scanner is fire-and-forget for
    // these — we surface the count + first-failure in the status bar so the
    // user can identify which file is the problem.
    private readonly List<ScanError> _scanErrors = new();
    private readonly IProgress<ScanError> _scanErrorProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial LoadingState LoadingState { get; set; }

    // UI-friendly derivatives of LoadingState. We expose these as plain
    // computed properties (not separate [ObservableProperty] fields) so the
    // source of truth is the enum and we never have to keep two booleans in
    // sync. IsScanning powers the scan progress ring in the status bar; it
    // is true while LoadDirectoryAsync is enumerating the filesystem and
    // false once the first page starts loading. The IsScanning change
    // notification is raised from OnLoadingStateChanged below.
    public bool IsScanning => LoadingState == LoadingState.Scanning;
    public Visibility IsScanningVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    // Live count of image sources the scanner has discovered so far. Used
    // by the status bar to show "Scanning... N images found" while a
    // whole-drive scan is in progress. Updated via a throttled IProgress
    // sink (see LoadDirectoryAsync) so a scan of tens of thousands of files
    // does not raise a property change per file.
    [ObservableProperty]
    public partial int DiscoveredCount { get; set; }

    // Path the scanner is currently working on. Reported by the scanner
    // before entering each directory (or before enumerating each archive),
    // throttled in the VM to ~10 Hz so the status bar text does not flicker
    // wildly on a whole-drive scan where the queue churns through hundreds
    // of folders per second.
    [ObservableProperty]
    public partial string CurrentScanningPath { get; set; } = "";

    partial void OnLoadingStateChanged(LoadingState value)
    {
        OnPropertyChanged(nameof(IsScanning));
        OnPropertyChanged(nameof(IsScanningVisibility));
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Select a folder to start";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial string? CurrentFolderPath { get; set; }

    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [ObservableProperty]
    public partial int LastViewedIndex { get; set; } = -1;

    [ObservableProperty]
    public partial double LastViewedYOffset { get; set; } = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMoreVisibility))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool CanLoadMore { get; set; }

    public Visibility LoadMoreVisibility => CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    partial void OnIsLoadingMoreChanged(bool value)
    {
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Interval the viewer's slideshow waits between auto-advances,
    /// in seconds. Stored as <c>double</c> so the gallery's
    /// (future) slider and the viewer's slider share the same
    /// type. Mirrored to <c>ImageViewModel.SlideshowInterval</c> at
    /// gallery's Slideshow-button click time — the viewer's own
    /// slider writes directly to the ImageViewModel.
    /// </summary>
    public double SlideshowInterval { get; set; } = 5.0;

    public ObservableCollection<MediaItem> Images { get; } = new();

    public GalleryViewModel(
        IDirectoryScanner scanner,
        IImageLoader imageLoader,
        IVideoMetadataService videoMetadataService,
        IFolderPickerService folderPicker,
        IDialogService dialogService)
    {
        _scanner = scanner;
        _imageLoader = imageLoader;
        _videoMetadataService = videoMetadataService;
        _folderPicker = folderPicker;
        _dialogService = dialogService;

        // Progress<T> captures the sync context of the thread that created
        // it (the UI thread here), so the callback is auto-dispatched back
        // to the UI thread — safe to mutate StatusText / _scanErrors
        // without explicit marshalling.
        _scanErrorProgress = new Progress<ScanError>(err => _scanErrors.Add(err));

        // Keep the source-id → item index in sync with the observable collection.
        // Avoids O(n) FirstOrDefault scans in OnFileChanged when collections grow.
        Images.CollectionChanged += OnImagesCollectionChanged;
    }

    private void OnImagesCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
        {
            _imageIndex.Clear();
        }

        if (e.OldItems != null)
        {
            foreach (MediaItem item in e.OldItems)
            {
                _imageIndex.Remove(item.Id);
            }
        }

        if (e.NewItems != null)
        {
            foreach (MediaItem item in e.NewItems)
            {
                _imageIndex[item.Id] = item;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _fileWatcher?.Dispose();
                _loadCts?.Cancel();
                _loadCts?.Dispose();
                _thumbnailLoadSemaphore.Dispose();
                _sizeFetchSemaphore.Dispose();
                Images.CollectionChanged -= OnImagesCollectionChanged;
            }
            _disposed = true;
        }
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folderPath = await _folderPicker.PickFolderAsync("Select Image Folder");
        if (!string.IsNullOrEmpty(folderPath))
        {
            await LoadDirectoryAsync(folderPath);
        }
    }

    public bool RemoveImage(MediaItem item)
    {
        var index = Images.IndexOf(item);
        if (index >= 0)
        {
            Images.RemoveAt(index);
            if (TotalCount > 0)
            {
                TotalCount--;
            }
            _remainingFilePaths.RemoveAll(s => s.ToString() == item.Id);
            CanLoadMore = _remainingFilePaths.Count > 0;

            var loaded = Images.Count;
            StatusText = (TotalCount > 0 && TotalCount != loaded)
                ? $"Loaded {loaded} / {TotalCount} images"
                : $"Loaded {loaded} images";
            return true;
        }
        return false;
    }

    public async Task DeleteImageAsync(MediaItem item)
    {
        // Refuse to delete entries that live inside an archive: doing so would
        // require rewriting the entire archive, and we do not implement that.
        // Surface a real dialog rather than a silent status-bar message so the
        // user understands why the click had no effect.
        if (item.Source.IsInArchive)
        {
            await _dialogService.ShowInfoAsync(
                "无法删除",
                $"压缩包内的图片 \"{item.Name}\" 不支持删除。\n\n" +
                $"如需删除，请在文件资源管理器中处理整个压缩包（{Path.GetFileName(item.Source.Path)}）。",
                closeButtonText: "确定");
            return;
        }

        var filePath = item.Source.Path;
        if (!File.Exists(filePath)) return;

        bool useRecycleBin;
        try
        {
            useRecycleBin = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.Name == Path.GetPathRoot(filePath))?.DriveType != DriveType.Network;
        }
        catch
        {
            useRecycleBin = true;
        }

        if (!useRecycleBin)
        {
            var confirmed = await _dialogService.ShowConfirmAsync(
                "确认删除",
                $"确定要永久删除 \"{item.Name}\" 吗？此操作无法撤销。",
                primaryButtonText: "删除",
                closeButtonText: "取消",
                defaultIsPrimary: false);
            if (!confirmed) return;
        }

        try
        {
            if (useRecycleBin)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    filePath,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                File.Delete(filePath);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"DeleteImageAsync error: {ex}");
            StatusText = $"Delete failed: {ex.Message}";
            return;
        }

        RemoveImage(item);
    }

    public async Task LoadDirectoryAsync(string path, CancellationToken ct = default)
    {
        // Atomically swap in a fresh cts; cancel + dispose the previous one so
        // any in-flight LoadNextPageAsync / OnFileChanged lambdas will see the
        // cancellation and early-exit.
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        oldCts?.Cancel();
        oldCts?.Dispose();

        CurrentFolderPath = path;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, newCts.Token);
            var token = linkedCts.Token;

            LoadingState = LoadingState.Scanning;
            Images.Clear();
            lock (_remainingLock)
            {
                _remainingFilePaths.Clear();
            }
            TotalCount = 0;
            CanLoadMore = false;
            _scanErrors.Clear();
            DiscoveredCount = 0;
            CurrentScanningPath = "";

            // Throttled progress sinks for the scan. A whole-drive scan can
            // yield tens of thousands of sources in a few seconds and churn
            // through hundreds of folders per second; reporting every single
            // one of either would cause a property change (and a status bar
            // redraw) per file. Each lambda therefore coalesces by both
            // count/identity delta and wall clock — whichever trips first
            // wins. Closure variables are safe: Progress<T> dispatches to
            // the captured sync context (UI thread here), so the throttlers
            // always run single-threaded. Both call into the same
            // UpdateScanningStatusText() so the displayed text is always
            // self-consistent regardless of which progress source fired last.
            int lastReportedCount = 0;
            long lastDiscoveredTick = 0;
            var throttledScanProgress = new Progress<int>(count =>
            {
                var now = Environment.TickCount64;
                if (count - lastReportedCount < 50 && now - lastDiscoveredTick < 200)
                {
                    return;
                }
                lastReportedCount = count;
                lastDiscoveredTick = now;
                DiscoveredCount = count;
                UpdateScanningStatusText();
            });

            long lastPathTick = 0;
            var throttledPathProgress = new Progress<string>(currentPath =>
            {
                var now = Environment.TickCount64;
                // Path changes can fire hundreds of times per second on a
                // whole-drive scan. ~10 Hz is enough to feel live without
                // the status bar text flickering on each directory.
                if (now - lastPathTick < 100)
                {
                    return;
                }
                lastPathTick = now;
                CurrentScanningPath = currentPath;
                UpdateScanningStatusText();
            });

            // The scan below runs on a worker thread and the page fill is
            // driven directly by IngestScanBatch — no timer is needed.
            // First image appears in the gallery within ~50 ms of being
            // discovered (the time-based flush in RunScanAndBatchAsync).

            // Run the scan on a worker thread so the UI thread can render
            // the first page as soon as the scanner yields PageSize sources.
            // The scanner runs to completion; while it runs, the page-fill
            // timer is feeding _remainingFilePaths into the gallery.
            //
            // Implemented as a named async method rather than a Task.Run
            // lambda because C# does not allow `yield return` / `yield break`
            // inside an anonymous method (CS1621). The actual `break` out
            // of the await foreach is plain; only the original `yield break`
            // would need replacing.
            var scanTask = Task.Run(
                () => RunScanAndBatchAsync(
                    path,
                    _scanErrorProgress,
                    throttledScanProgress,
                    throttledPathProgress,
                    token),
                token);

            await scanTask;

            if (token.IsCancellationRequested)
            {
                return;
            }

            // Scan is done. Wait for the page-fill timer to drain the
            // remaining sources before declaring the load complete. Poll
            // lightly — 50 ms is fast enough to feel snappy on small
            // folders and cheap enough not to matter on big ones.
            // Wait for the scan-time page fill and any user-triggered
            // "Load More" to settle before declaring Completed. We do NOT
            // require _remainingFilePaths to be empty here — the hybrid
            // mode (边扫边灌到 150 张停) leaves the rest of the queue
            // untouched for the user to pull manually. If the scan is
            // still in flight we are also fine to declare Completed: the
            // user has a full first page and a working Load More button.
            while (_pageFillInFlight || IsLoadingMore)
            {
                if (token.IsCancellationRequested) break;
                await Task.Delay(50, token);
            }

            StartWatching(path);
            LoadingState = LoadingState.Completed;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadDirectoryAsync error: {ex}");
            LoadingState = LoadingState.Completed;
            StatusText = $"Error: {ex.Message}";
        }
    }

    /// <summary>
    /// Background scan runner. Enumerates the scanner, buffering sources
    /// into batches of up to <see cref="ScanBatchSize"/> and dispatching
    /// each batch to the UI thread for ingestion. Two flush triggers keep
    /// latency low even on slow scans:
    ///   - size: <see cref="ScanBatchSize"/> sources accumulated (caps the
    ///     dispatcher queue depth on a whole-drive scan);
    ///   - time: 50 ms since the last flush (guarantees the very first
    ///     source is on screen within ~50 ms even if the scanner is slow).
    /// Without the time-based flush the user would only see the first
    /// image once the scanner had already discovered ~100 sources, which
    /// is fine on a fast SSD but unacceptable on a slow network share.
    /// </summary>
    private async Task RunScanAndBatchAsync(
        string path,
        IProgress<ScanError> errorReporter,
        IProgress<int> discoveredReporter,
        IProgress<string> currentPathReporter,
        CancellationToken token)
    {
        var batch = new List<ImageSource>(ScanBatchSize);
        // Anchor of "the first source in the current batch was just added
        // at this tick". We reset it to 0 (== "no batch") when the batch
        // is empty so the next source starts a fresh 50 ms window. The
        // size cap is unchanged — 100 sources in a single dispatcher
        // post — but the time cap is measured from the *first* source in
        // the batch, not from scan start, so a slow scanner that yields
        // one source per 5 s still flushes within 50 ms of each yield.
        long batchStartTick = 0;
        await foreach (var source in _scanner.ScanAsync(
            path,
            recursive: true,
            extensions: _imageLoader.SupportedMedia,
            errorReporter: errorReporter,
            discoveredReporter: discoveredReporter,
            currentPathReporter: currentPathReporter,
            ct: token))
        {
            if (token.IsCancellationRequested) break;
            if (batch.Count == 0) batchStartTick = Environment.TickCount64;
            batch.Add(source);
            var now = Environment.TickCount64;
            if (batch.Count >= ScanBatchSize || now - batchStartTick >= 50)
            {
                FlushScanBatch(batch, token);
                batch = new List<ImageSource>(ScanBatchSize);
                batchStartTick = 0;
            }
        }
        if (batch.Count > 0)
        {
            FlushScanBatch(batch, token);
        }
    }

    /// <summary>
    /// Hands a batch of newly-discovered image sources to the UI thread for
    /// ingestion. Called from the background scan task — never on the UI
    /// thread directly — so the work item is marshalled through the
    /// captured DispatcherQueue. The batch is captured by value into the
    /// lambda, so the caller's local list can be reused for the next batch
    /// immediately after this call returns.
    /// </summary>
    private void FlushScanBatch(List<ImageSource> batch, CancellationToken ct)
    {
        var b = batch;
        _dispatcher.TryEnqueue(() => IngestScanBatch(b, ct));
    }

    /// <summary>
    /// Runs on the UI thread. Adds the batched sources to the pending queue
    /// (under <c>_remainingLock</c>) and bumps <see cref="TotalCount"/>.
    /// The first call after a quiescent period starts
    /// <see cref="DrainPageFillAsync"/>; subsequent calls while the drain
    /// loop is still running are no-ops (the drain loop polls the queue
    /// itself). This is the only sane re-entry strategy: the fire-and-forget
    /// path never sets <see cref="IsLoadingMore"/>, which is owned by
    /// <c>LoadMoreAsync</c> and observed by the "Load More" button.
    ///
    /// We also gate the start on <c>Images.Count &lt; PageSize</c>: the
    /// auto-fill only ever fills the first page (150 items by default).
    /// Past that, the user pulls more manually via "Load More". This
    /// restores the classic "first 150 + Load More" control feel that the
    /// "边扫边灌到 150 张停" hybrid mode is built around — without it the
    /// drain would auto-inject items into the gallery for the entire scan
    /// and the user would never get a moment of stillness to look at what
    /// they have. The scanner keeps running in the background and
    /// continues to accumulate sources into <c>_remainingFilePaths</c>;
    /// only the auto-injection into <see cref="Images"/> stops.
    /// </summary>
    private void IngestScanBatch(List<ImageSource> batch, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        lock (_remainingLock)
        {
            _remainingFilePaths.AddRange(batch);
        }
        TotalCount += batch.Count;
        UpdateScanningStatusText();

        if (!_pageFillInFlight && Images.Count < PageSize)
        {
            _pageFillInFlight = true;
            _ = DrainPageFillAsync(ct);
        }
    }

    /// <summary>
    /// Single-consumer page-fill loop. Started by the first IngestScanBatch
    /// after a quiescent period; runs until the first page
    /// (<see cref="PageSize"/> items) is in the gallery, the queue is
    /// drained, or the load cts is cancelled. Each iteration awaits one
    /// <c>LoadNextPageAsync</c> — yielding up to <see cref="ScanPageSize"/>
    /// items into the gallery — then loops. The loop body is a single
    /// async sequence, so there is never more than one LoadNextPageAsync
    /// in flight even when IngestScanBatch fires every 50 ms.
    ///
    /// The page size is clamped to <c>PageSize - Images.Count</c> on each
    /// iteration so the loop never overshoots the first page: if the
    /// previous iteration left 120 items visible, the next take is at most
    /// 30 — exactly enough to hit 150. Without this clamp the gallery
    /// could land on 160 or 180 items on a fast scan.
    /// </summary>
    private async Task DrainPageFillAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int remaining;
                lock (_remainingLock)
                {
                    remaining = _remainingFilePaths.Count;
                }
                if (remaining == 0) break;
                int target = PageSize - Images.Count;
                if (target <= 0) break;
                int pageSize = Math.Min(ScanPageSize, target);
                await LoadNextPageAsync(ct, pageSize);
            }
        }
        finally
        {
            // Always release the re-entry guard, even on cancellation or
            // exception. If the load cts was cancelled the next
            // LoadDirectoryAsync will replace it and set up a fresh drain
            // loop; if it was a transient fault, leaving the flag set would
            // permanently disable the auto-fill.
            _pageFillInFlight = false;
        }
    }

    /// <summary>
    /// Starts the repeating timer that drains <c>_remainingFilePaths</c>
    /// into the gallery while the scan is in progress. A no-op if a timer
    /// from a previous load is somehow still alive (defensive — LoadDirectoryAsync
    /// already stops the old one).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreCommand))]
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!CanLoadMore || IsLoadingMore) return;

        IsLoadingMore = true;
        try
        {
            await LoadNextPageAsync(ct);
            UpdateStatusText();
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    private bool CanLoadMoreCommand() => CanLoadMore && !IsLoadingMore;

    [RelayCommand(CanExecute = nameof(CanRefreshCommand))]
    public async Task RefreshAsync()
    {
        if (string.IsNullOrEmpty(CurrentFolderPath)) return;
        LastViewedYOffset = 0;
        await LoadDirectoryAsync(CurrentFolderPath);
    }

    private bool CanRefreshCommand() => !string.IsNullOrEmpty(CurrentFolderPath)
        && LoadingState != LoadingState.Scanning;

    private async Task LoadNextPageAsync(CancellationToken ct, int pageSize = -1)
    {
        if (pageSize < 0) pageSize = PageSize;

        List<ImageSource> batch;
        lock (_remainingLock)
        {
            if (_remainingFilePaths.Count == 0)
            {
                CanLoadMore = false;
                return;
            }
            batch = _remainingFilePaths.Take(pageSize).ToList();
            _remainingFilePaths.RemoveRange(0, batch.Count);
            CanLoadMore = _remainingFilePaths.Count > 0;
        }

        // Fetchahead: pre-fetch (size, mtime, WxH [+ duration / hasAudio
        // for videos]) for the whole batch with bounded concurrency before
        // producing any MediaItem. Two reasons:
        //   1. GetImageSizeAsync opens a FileStream + BitmapDecoder for
        //      each image, and GetVideoMetadataAsync opens a format
        //      context for each video; on a 30-item batch that is several
        //      seconds of sequential I/O. Running them with a 6-wide cap
        //      drops the wall time by ~5x.
        //   2. Each `Images.Add` triggers a MasonryPanel layout pass, and
        //      the panel is non-virtualising — emitting 150 Add work items
        //      in one Tick freezes the UI for the entire layout burst. With
        //      pageSize=30 the burst is small enough to feel incremental.
        //
        // The dispatch on source.Kind happens inside the per-source async
        // lambda so videos and images can hit their own metadata path
        // (BitmapDecoder vs FFmpeg) without sharing a "one method does
        // both" abstraction. The sizeFetchSemaphore applies to both so
        // the total in-flight count is bounded the same way regardless
        // of mix — important on a folder that's 80% videos, where a naive
        // dispatcher would fire 24 FFmpeg opens at once and saturate the
        // disk.
        var fetched = await Task.WhenAll(batch.Select(async source =>
        {
            var meta = await GetSourceMetadataAsync(source, ct);
            await _sizeFetchSemaphore.WaitAsync(ct);
            try
            {
                if (source.Kind == MediaKind.Video)
                {
                    // Reads only the container header (~ms even for large
                    // .mkv files). Populates VideoItem.OriginalWidth /
                    // OriginalHeight / Duration / HasAudio so the gallery
                    // overlay shows the WxH and the m:ss duration instead
                    // of "Unknown".
                    var videoMeta = await _videoMetadataService.GetVideoMetadataAsync(source, ct);
                    return new FetchedMedia(source, meta, videoMeta, null);
                }
                else
                {
                    // Reads only the image header (BitmapDecoder, ~ms even
                    // for multi-MP files). Populates ImageItem.OriginalWidth
                    // / OriginalHeight so the gallery overlay and the
                    // image viewer info bar show real dimensions instead
                    // of "Unknown".
                    var dimensions = await _imageLoader.GetImageSizeAsync(source, ct);
                    return new FetchedMedia(source, meta, null, dimensions);
                }
            }
            finally
            {
                _sizeFetchSemaphore.Release();
            }
        }));

        foreach (var entry in fetched)
        {
            // If a new LoadDirectoryAsync fired while we were awaiting the
            // batch, ct is now cancelled — abandon the rest. Items already
            // enqueued below will see the cancelled token when they run and
            // early-exit.
            if (ct.IsCancellationRequested) break;

            var source = entry.Source;
            var (size, mtime) = entry.Meta;

            // Construct the right concrete subtype. Both share the same
            // (size, mtime) source info and the same OriginalWidth /
            // OriginalHeight slots — the video path additionally needs
            // Duration and HasAudio. The Kind tag tells us which arm
            // of FetchedMedia was populated; the other arm is null.
            MediaItem item;
            if (source.Kind == MediaKind.Video)
            {
                var videoMeta = entry.VideoMeta;
                item = new VideoItem(
                    source: source,
                    fileSize: size,
                    modifiedTime: mtime,
                    originalWidth: videoMeta?.Width ?? 0,
                    originalHeight: videoMeta?.Height ?? 0,
                    duration: videoMeta?.Duration ?? TimeSpan.Zero,
                    hasAudio: videoMeta?.HasAudio ?? false);
            }
            else
            {
                var dimensions = entry.ImageDimensions;
                item = new ImageItem(
                    source: source,
                    fileSize: size,
                    modifiedTime: mtime,
                    originalWidth: dimensions?.Width ?? 0,
                    originalHeight: dimensions?.Height ?? 0);
            }

            _dispatcher.TryEnqueue(() =>
            {
                // Guard against running after cancellation: prevents stale items
                // from a previous folder sneaking into the new Images collection.
                if (ct.IsCancellationRequested) return;
                Images.Add(item);
                StatusText = $"Loading {Images.Count} / {TotalCount}...";
            });

            _ = LoadThumbnailAsync(item, ct);
        }
    }

    /// <summary>
    /// Per-source metadata result from the LoadNextPageAsync fetchahead.
    /// Either <see cref="VideoMeta"/> or <see cref="ImageDimensions"/>
    /// is populated (the other is null) — which one is determined by
    /// <see cref="Source"/>.<see cref="ImageSource.Kind"/>. Using a
    /// single record for both arms (instead of two separate tuples)
    /// keeps Task.WhenAll's inference happy and avoids the awkward
    /// union-type access in the consumer loop.
    /// </summary>
    private sealed record FetchedMedia(
        ImageSource Source,
        (long Size, DateTime Mtime) Meta,
        VideoMetadata? VideoMeta,
        (int Width, int Height)? ImageDimensions);

    /// <summary>
    /// Returns the (uncompressed size, modification time) for the given
    /// source. For loose files this is just FileInfo. For archive entries
    /// the size comes from the archive's central directory; the modification
    /// time is the archive file's mtime (entry-level mtime is not reliably
    /// exposed by SharpCompress and is uninteresting for cache invalidation
    /// when the archive file itself is the source of truth).
    /// </summary>
    private static Task<(long Size, DateTime ModifiedTime)> GetSourceMetadataAsync(ImageSource source, CancellationToken ct)
    {
        if (!source.IsInArchive)
        {
            var info = new FileInfo(source.Path);
            return Task.FromResult(
                (info.Exists ? info.Length : 0L,
                 info.Exists ? info.LastWriteTime : DateTime.MinValue));
        }

        return Task.Run(() =>
        {
            try
            {
                var archiveInfo = new FileInfo(source.Path);
                var entries = ArchiveHelper.ListEntries(source.Path, extensionFilter: null);
                foreach (var entry in entries)
                {
                    if (entry.Key == source.ArchiveEntry)
                    {
                        return (entry.UncompressedSize, archiveInfo.Exists ? archiveInfo.LastWriteTime : DateTime.MinValue);
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"GetSourceMetadataAsync archive error for {source}: {ex.Message}");
            }
            return (0L, DateTime.MinValue);
        }, ct);
    }

    /// <summary>
    /// Centralized status text formatter used after collection mutations so the
    /// "Loaded X / Y images" wording stays consistent across Created / Deleted /
    /// LoadDirectory / LoadMore paths. If the scanner reported any unreadable
    /// files (typically a corrupt or unsupported archive), append a short
    /// summary so the user knows which file to investigate.
    /// </summary>
    private void UpdateStatusText()
    {
        var loaded = Images.Count;
        // Video count out of the loaded set. We don't have a separate
        // VideoTotalCount because the scanner reports a single combined
        // discovered count (extensions are filtered per-source as we go,
        // not pre-bucketed into image-vs-video totals). Showing only
        // "N videos loaded" — without a total denominator — is the
        // honest version. A user scanning for "how many videos are in
        // this folder" can read the number once Load More has caught
        // up to the scanner's progress.
        var videos = loaded > 0 ? Images.Count(i => i.IsVideo) : 0;
        var imageText = (TotalCount > 0 && TotalCount != loaded)
            ? $"Loaded {loaded} / {TotalCount} images"
            : $"Loaded {loaded} images";
        // Append the video count only when non-zero — a pure-photo
        // folder shouldn't grow the status text with a ", 0 videos"
        // suffix.
        string baseText = videos > 0
            ? $"{imageText}, {videos} videos"
            : imageText;

        if (_scanErrors.Count == 0)
        {
            StatusText = baseText;
            return;
        }

        // Show the count + the first offender. Filename only — the full path
        // is usually too long for the status bar.
        var first = Path.GetFileName(_scanErrors[0].Path);
        StatusText = _scanErrors.Count == 1
            ? $"{baseText} — 1 file skipped ({first}: {_scanErrors[0].Reason})"
            : $"{baseText} — {_scanErrors.Count} files skipped (first: {first})";
    }

    /// <summary>
    /// Formats the "Scanning: ..." status text shown while the directory
    /// scanner is running. Called from both the discovered-count and
    /// current-path throttled progress sinks so the rendered text is always
    /// self-consistent — the most recent path + the most recent count,
    /// regardless of which progress source fired last.
    /// </summary>
    private void UpdateScanningStatusText()
    {
        if (string.IsNullOrEmpty(CurrentScanningPath))
        {
            // Pre-first-throttle window: no path reported yet.
            StatusText = $"Scanning... {DiscoveredCount} images found";
        }
        else
        {
            StatusText = $"Scanning: {CurrentScanningPath}  ({DiscoveredCount} found)";
        }
    }

    private void StartWatching(string path)
    {
        _fileWatcher?.Dispose();
        try
        {
            _fileWatcher = _scanner.Watch(path, recursive: true, OnFileChanged);
        }
        catch (Exception ex)
        {
            // Don't crash the app if the folder can't be watched (no permission,
            // network share dropped, etc.) — just surface the failure to the user
            // and keep the gallery working.
            _fileWatcher = null;
            Trace.TraceError($"StartWatching failed for {path}: {ex.Message}");
            StatusText = $"File monitoring unavailable: {ex.Message}";
        }
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        // Capture the load cts token. When LoadDirectoryAsync starts a new
        // folder it cancels and replaces _loadCts, so any lambdas already
        // enqueued will see the cancelled token and early-exit instead of
        // mutating the new Images collection.
        var token = _loadCts?.Token ?? CancellationToken.None;
        _dispatcher.TryEnqueue(async () =>
        {
            if (token.IsCancellationRequested) return;
            try
            {
                switch (info.ChangeType)
                {
                    case WatchChangeType.Created:
                        await HandleCreatedAsync(info, token);
                        break;

                    case WatchChangeType.Deleted:
                        HandleDeleted(info);
                        break;

                    case WatchChangeType.Modified:
                        await HandleModifiedAsync(info, token);
                        break;

                    case WatchChangeType.Renamed:
                        await HandleRenamedAsync(info, token);
                        break;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"OnFileChanged error for {info.Path} ({info.ChangeType}): {ex.Message}");
            }
        });
    }

    private async Task HandleCreatedAsync(FileChangeInfo info, CancellationToken token)
    {
        if (!File.Exists(info.Path)) return;

        if (ArchiveHelper.IsArchiveFileName(info.Path) && ArchiveHelper.IsArchive(info.Path))
        {
            // A new archive appeared: open it and add every image entry to the gallery.
            var (newItems, error) = await AddArchiveEntriesAsync(info.Path, token);
            if (error is not null)
            {
                _scanErrors.Add(error);
                UpdateStatusText();
            }
            if (newItems.Count > 0)
            {
                TotalCount += newItems.Count;
                UpdateStatusText();
                foreach (var newItem in newItems)
                {
                    _ = LoadThumbnailAsync(newItem, CancellationToken.None);
                }
            }
            return;
        }

        if (!_imageLoader.IsSupportedFormat(info.Path)) return;
        var kind = _imageLoader.GetKindForFile(info.Path);
        var source = ImageSource.FromFile(info.Path, kind);
        if (_imageIndex.ContainsKey(source.ToString())) return;

        var (size, mtime) = await GetSourceMetadataAsync(source, token);
        if (token.IsCancellationRequested) return;

        // Dispatch on kind for the metadata fetch + ctor — same shape as
        // LoadNextPageAsync but for a single new file from the watcher.
        MediaItem item;
        if (kind == MediaKind.Video)
        {
            var videoMeta = await _videoMetadataService.GetVideoMetadataAsync(source, token);
            if (token.IsCancellationRequested) return;
            item = new VideoItem(
                source: source,
                fileSize: size,
                modifiedTime: mtime,
                originalWidth: videoMeta?.Width ?? 0,
                originalHeight: videoMeta?.Height ?? 0,
                duration: videoMeta?.Duration ?? TimeSpan.Zero,
                hasAudio: videoMeta?.HasAudio ?? false);
        }
        else
        {
            var dimensions = await _imageLoader.GetImageSizeAsync(source, token);
            if (token.IsCancellationRequested) return;
            item = new ImageItem(
                source: source,
                fileSize: size,
                modifiedTime: mtime,
                originalWidth: dimensions?.Width ?? 0,
                originalHeight: dimensions?.Height ?? 0);
        }

        Images.Add(item);
        TotalCount++;
        UpdateStatusText();
        await LoadThumbnailAsync(item, CancellationToken.None);
    }

    /// <summary>
    /// Enumerates entries in the given archive and creates a
    /// <see cref="MediaItem"/> for each (image or video, dispatched by
    /// the entry's extension). Returns the list of items plus an
    /// optional <see cref="ScanError"/> if the archive could not be
    /// read at all (caller surfaces it in the status bar alongside
    /// initial scan errors).
    /// </summary>
    private async Task<(List<MediaItem> Items, ScanError? Error)> AddArchiveEntriesAsync(
        string archivePath, CancellationToken token)
    {
        var result = new List<MediaItem>();
        try
        {
            var archiveInfo = new FileInfo(archivePath);
            var mtime = archiveInfo.Exists ? archiveInfo.LastWriteTime : DateTime.MinValue;

            // Build the extension → kind lookup once and use it both
            // for ListEntries (extension set) and per-entry kind
            // stamping. The full media list covers both image and
            // video; an archive's entries go through the same
            // dispatch as loose files.
            var extensionMap = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, kind) in _imageLoader.SupportedMedia)
            {
                extensionMap[ext] = kind;
            }
            var extensionSet = new HashSet<string>(extensionMap.Keys, StringComparer.OrdinalIgnoreCase);

            await Task.Run(async () =>
            {
                var entries = ArchiveHelper.ListEntries(archivePath, extensionSet);
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;
                    var ext = Path.GetExtension(entry.Key);
                    if (!extensionMap.TryGetValue(ext, out var kind)) continue;
                    var source = ImageSource.FromArchive(archivePath, entry.Key, kind);
                    if (_imageIndex.ContainsKey(source.ToString())) continue;

                    // Dispatch on kind: image goes through the cheap
                    // BitmapDecoder header read, video through
                    // FFmpeg's container parse. Both are bounded by
                    // the per-entry 6-wide _sizeFetchSemaphore
                    // indirectly (LoadNextPageAsync is the only
                    // concurrent caller; AddArchiveEntriesAsync is
                    // called serially from the watcher event handler).
                    if (kind == MediaKind.Video)
                    {
                        var videoMeta = await _videoMetadataService.GetVideoMetadataAsync(source, token);
                        result.Add(new VideoItem(
                            source: source,
                            fileSize: entry.UncompressedSize,
                            modifiedTime: mtime,
                            originalWidth: videoMeta?.Width ?? 0,
                            originalHeight: videoMeta?.Height ?? 0,
                            duration: videoMeta?.Duration ?? TimeSpan.Zero,
                            hasAudio: videoMeta?.HasAudio ?? false));
                    }
                    else
                    {
                        var dims = await _imageLoader.GetImageSizeAsync(source, token);
                        result.Add(new ImageItem(
                            source: source,
                            fileSize: entry.UncompressedSize,
                            modifiedTime: mtime,
                            originalWidth: dims?.Width ?? 0,
                            originalHeight: dims?.Height ?? 0));
                    }
                }
            }, token);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"AddArchiveEntriesAsync error for {archivePath}: {ex.Message}");
            return (result, new ScanError(archivePath, ClassifyArchiveError(ex)));
        }
        return (result, null);
    }

    /// <summary>
    /// Maps a SharpCompress / IO exception to a short reason suitable for
    /// the status bar. Same logic as the scanner-side classifier; duplicated
    /// here because this code path (FileSystemWatcher → add new archive)
    /// never goes through the scanner.
    /// </summary>
    private static string ClassifyArchiveError(Exception ex) => ex switch
    {
        FileNotFoundException => "file missing",
        IOException => "I/O error",
        UnauthorizedAccessException => "access denied",
        _ => "unsupported or corrupt archive"
    };

    private void HandleDeleted(FileChangeInfo info)
    {
        // Direct file deletion (e.g. a loose .jpg or .mp4 removed): match
        // by id. Use the kind from the extension so the id matches the
        // kind-tagged id we created when the item was added — without
        // this, a deleted .mp4 would never find its VideoItem in the
        // index because FromFile() defaults to Image.
        var directId = ImageSource.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path)).ToString();
        if (_imageIndex.TryGetValue(directId, out var directItem))
        {
            Images.Remove(directItem);
            if (TotalCount > 0) TotalCount--;
            lock (_remainingLock)
            {
                _remainingFilePaths.RemoveAll(s => s.ToString() == directId);
            }
            CanLoadMore = _remainingFilePaths.Count > 0;
            UpdateStatusText();
            return;
        }

        // Archive deletion: remove every entry whose source points at the
        // gone archive. Match the Source.Path (case-insensitive to mirror
        // Windows file lookup).
        var toRemove = _imageIndex.Values
            .Where(item => string.Equals(item.Source.Path, info.Path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (toRemove.Count == 0) return;

        foreach (var item in toRemove)
        {
            Images.Remove(item);
        }
        if (TotalCount >= toRemove.Count) TotalCount -= toRemove.Count;
        UpdateStatusText();
    }

    private async Task HandleModifiedAsync(FileChangeInfo info, CancellationToken token)
    {
        // The watcher only reports the file path, not which archive entry
        // changed, so Modified on an archive is too coarse to act on — the
        // safest behaviour is to leave the existing thumbnails alone.
        if (ArchiveHelper.IsArchiveFileName(info.Path)) return;

        // Include the file's kind in the lookup id — a .mp4's id was
        // created with Kind=Video, so a Kind=Image id won't find it.
        var id = ImageSource.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path)).ToString();
        if (!_imageIndex.TryGetValue(id, out var modifiedItem)) return;
        modifiedItem.Thumbnail = null;
        modifiedItem.FullImage = null;
        await LoadThumbnailAsync(modifiedItem, CancellationToken.None);
        if (token.IsCancellationRequested) return;
    }

    private async Task HandleRenamedAsync(FileChangeInfo info, CancellationToken token)
    {
        // Archive renames: the new archive may contain the same entries
        // (likely, if it was a simple rename) — easiest correct behaviour is
        // to drop the entries from the old path and re-add from the new path.
        if (info.OldPath != null && ArchiveHelper.IsArchiveFileName(info.OldPath))
        {
            var oldPathItems = _imageIndex.Values
                .Where(item => string.Equals(item.Source.Path, info.OldPath, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var item in oldPathItems)
            {
                Images.Remove(item);
            }
            if (TotalCount >= oldPathItems.Count) TotalCount -= oldPathItems.Count;

            if (File.Exists(info.Path) && ArchiveHelper.IsArchive(info.Path))
            {
                var (newItems, error) = await AddArchiveEntriesAsync(info.Path, token);
                if (error is not null)
                {
                    _scanErrors.Add(error);
                }
                TotalCount += newItems.Count;
                foreach (var newItem in newItems)
                {
                    _ = LoadThumbnailAsync(newItem, CancellationToken.None);
                }
            }
            UpdateStatusText();
            return;
        }

        if (info.OldPath == null) return;
        // Same kind-aware lookup as HandleModified/HandleDeleted: the
        // item's id was created with Kind=Video for .mp4/.mkv files,
        // so we have to look up under the same Kind.
        var oldId = ImageSource.FromFile(info.OldPath, _imageLoader.GetKindForFile(info.OldPath)).ToString();
        if (!_imageIndex.TryGetValue(oldId, out var renamedItem)) return;

        // UpdateSource carries the new path. Preserve the existing kind
        // because the rename didn't change the file type — if the user
        // renames foo.mp4 to bar.mp4 the new id must still match a
        // video, not an image. The new path's GetKindForFile would
        // return the same thing anyway, but using Source.Kind here
        // is explicit about the intent.
        Images.Remove(renamedItem);  // triggers index removal on OldPath
        renamedItem.UpdateSource(ImageSource.FromFile(info.Path, renamedItem.Source.Kind));
        Images.Add(renamedItem);     // triggers index insertion on NewPath
        renamedItem.Thumbnail = null;
        renamedItem.FullImage = null;
        await LoadThumbnailAsync(renamedItem, CancellationToken.None);
        if (token.IsCancellationRequested) return;
        UpdateStatusText();
    }

    private async Task LoadThumbnailAsync(MediaItem item, CancellationToken ct)
    {
        // Outer try/finally so the gallery template's "thumbnail loading"
        // spinner is always cleared — regardless of whether we hit the
        // cache, succeeded, failed to decode, or were cancelled while
        // waiting on the semaphore. The setter must be marshalled to the
        // UI thread (Progress<T> / PropertyChanged notifications expect
        // a single thread) so it goes through the dispatcher.
        try
        {
            if (item.Thumbnail != null)
            {
                if (!item.Source.IsInArchive && File.Exists(item.Source.Path))
                {
                    var fileInfo = new FileInfo(item.Source.Path);
                    if (fileInfo.LastWriteTime == item.ModifiedTime)
                    {
                        return;
                    }
                }
                item.Thumbnail = null;
            }

            await _thumbnailLoadSemaphore.WaitAsync(ct);
            try
            {
                // Dispatch on Kind: image thumbnails go through the
                // BitmapDecoder path (with the IImageLoader LRU), videos
                // through the FFmpeg first-frame path. The mtime check
                // above is intentionally only on the loose-file image
                // path — videos don't share the IImageLoader LRU so the
                // "is this cached" check is implicit on the item itself
                // (we got here because Thumbnail was null), and the cost
                // of re-decoding a video is high enough that we don't
                // pretend to cache invalidation across file changes.
                BitmapImage? thumbnail = item.Source.Kind == MediaKind.Video
                    ? await _videoMetadataService.ExtractVideoThumbnailAsync(item.Source, 400, ct)
                    : await _imageLoader.LoadThumbnailAsync(item.Source, 400, ct);
                if (thumbnail != null)
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        item.Thumbnail = thumbnail;
                        if (item.Source.Kind == MediaKind.Video)
                        {
                            // VideoItem has no separate "full" decode —
                            // the first frame IS the gallery-quality
                            // preview and the viewer's default static
                            // preview. Wire FullImage to the same
                            // BitmapImage so the viewer's existing
                            // FullImage-short-circuit kicks in and
                            // shows the first frame without re-decoding.
                            // The future MediaPlayerElement session
                            // will replace this with a real player.
                            item.FullImage = thumbnail;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Trace.TraceError($"LoadThumbnailAsync error for {item.Id}: {ex.Message}");
            }
            finally
            {
                _thumbnailLoadSemaphore.Release();
            }
        }
        finally
        {
            _dispatcher.TryEnqueue(() => item.IsThumbnailLoading = false);
        }
    }
}
