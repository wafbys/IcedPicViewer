// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Core.Layout;
using IcedPicViewer.Core.Media;
using IcedPicViewer.Core.Text;
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
    private readonly IMediaLoader _imageLoader;
    private readonly IVideoMetadataService _videoMetadataService;
    private readonly IFolderPickerService _folderPicker;
    private readonly IFilePickerService _filePicker;
    private readonly IDialogService _dialogService;
    private readonly ISettingsService _settingsService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    // P1: one Trace line per completed scan so scan cost can be attributed
    // without a profiler (see ScanStats). Progress<T> marshals back to the
    // captured (UI) context; the report fires once at scan end.
    private static readonly IProgress<ScanStats> ScanStatsReporter = new Progress<ScanStats>(stats =>
        Trace.TraceInformation(
            $"scan stats: dirs={stats.DirectoryCount} files={stats.FileCount} " +
            $"archives={stats.ArchiveCount} media={stats.MediaCount} " +
            $"total={stats.ElapsedMs}ms enum={stats.EnumerateMs}ms archive={stats.ArchiveMs}ms"));

    private CancellationTokenSource? _loadCts;
    private IDisposable? _fileWatcher;
    private bool _disposed;

    // Master list of every source discovered for the current folder, in scan
    // order. The visible gallery is a filtered / searched / sorted VIEW derived
    // from this (see RebuildViewAsync). Guarded by _remainingLock because the
    // scanner's worker and the UI thread both touch it.
    private readonly List<MediaRef> _allSources = new();

    // O(1) id set mirroring _allSources (dedupe on watcher adds).
    private readonly HashSet<string> _allSourceIds = new(StringComparer.Ordinal);

    // (size, mtime) per media id, so sorting by Date/Size over the whole set
    // does not re-stat every file on every rebuild. Populated at page load and
    // by EnsureMetadataAsync; cleared when the folder changes.
    private readonly ConcurrentDictionary<string, (long Size, DateTime Mtime)> _metadataCache =
        new(StringComparer.Ordinal);

    // Bumped on every view rebuild. In-flight page loads capture it and discard
    // their batch if it changed, so items from a superseded filter/sort can't
    // leak into the new collection.
    private int _viewGeneration;

    // Debounces query changes (typing in the search box fires per keystroke).
    private CancellationTokenSource? _queryDebounceCts;

    // Set while OpenFileAsync mutates Filter/SearchText before it explicitly
    // reloads, so those setters don't schedule their own rebuilds.
    private bool _suppressRebuild;

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
    private const int ThumbConcurrency = 6;

    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(initialCount: ThumbConcurrency, maxCount: ThumbConcurrency);

    // For incremental loading while keeping masonry visual.
    // Both LoadNextPageAsync (worker thread) and OnFileChanged (dispatcher thread) read/write
    // this list, so guard it with _remainingLock to avoid race conditions.
    private readonly object _remainingLock = new();
    private List<MediaRef> _remainingSources = new();
    // Page size for user-triggered Load More (button click OR auto-load
    // when scrolling near the bottom). Each media is added to Items
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

    /// <summary>Time-based batch flush (ms) from first media in the batch.</summary>
    private const int ScanBatchMs = 50;

    // Page size used while the scan-time page fill is feeding the gallery.
    // Deliberately small so a single LoadNextPageAsync only adds 30 items
    // to the ItemsControl (≈30 layout passes on the non-virtualising
    // MasonryPanel) instead of the full 150 that a manual "Load More"
    // uses — the trade-off is "feel responsive" vs "load big chunks on
    // demand". The page fill is driven directly by IngestScanBatch (no
    // timer), so a steady stream of small pages appears as continuous
    // growth instead of a multi-second freeze.
    private const int ScanPageSize = 30;

    // O(1) media-id → item lookup. Mirrors the Items collection; maintained
    // via the CollectionChanged handler in the constructor. Keys are
    // MediaRef.ToString(), which is case-sensitive for archive entries
    // (archive keys preserve the original case) and case-insensitive-friendly
    // for loose files (Windows paths are case-insensitive, but the conflict
    // would only occur if two files differ only in case, which FileSystem
    // itself disallows on Windows). Value is MediaItem (base) because the
    // collection also holds VideoItem — a single media id maps to one
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
    // media of truth is the enum and we never have to keep two booleans in
    // sync. IsScanning powers the scan progress ring in the status bar; it
    // is true while LoadDirectoryAsync is enumerating the filesystem and
    // false once the first page starts loading. The IsScanning change
    // notification is raised from OnLoadingStateChanged below.
    public bool IsScanning => LoadingState == LoadingState.Scanning;
    public Visibility IsScanningVisibility => IsScanning ? Visibility.Visible : Visibility.Collapsed;

    // Total media sources discovered for this folder session (absolute).
    // Single writer during scan: IngestScanBatch sets DiscoveredCount = discovered.
    // Watcher add/remove adjusts after scan. Do not also
    // assign from scanner Progress — that double-counted with ingest.
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
    public partial string StatusText { get; set; } = GalleryStatusFormatter.IdleDefault;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial string? FolderPath { get; set; }

    // ── 筛选 / 排序 / 查找 ──────────────────────────────────────────────
    // Applied over the WHOLE discovered set (not just the loaded page); a
    // change rebuilds the view and refills from the first page.

    [ObservableProperty]
    public partial MediaFilter Filter { get; set; } = MediaFilter.All;

    [ObservableProperty]
    public partial MediaSortKey SortKey { get; set; } = MediaSortKey.Name;

    [ObservableProperty]
    public partial bool SortDescending { get; set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    partial void OnFilterChanged(MediaFilter value)
    {
        OnPropertyChanged(nameof(FilterIndex));
        OnPropertyChanged(nameof(IsQueryActive));
        PersistGalleryQuerySettings();
        ScheduleViewRebuild();
    }

    partial void OnSortKeyChanged(MediaSortKey value)
    {
        OnPropertyChanged(nameof(SortIndex));
        PersistGalleryQuerySettings();
        ScheduleViewRebuild();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionLabel));
        PersistGalleryQuerySettings();
        ScheduleViewRebuild();
    }

    /// <summary>Writes the gallery filter/sort toggles through to settings.json.</summary>
    private void PersistGalleryQuerySettings()
    {
        var settings = _settingsService.Current;
        settings.GalleryFilter = (int)Filter;
        settings.GallerySortKey = (int)SortKey;
        settings.GallerySortDescending = SortDescending;
        _settingsService.ScheduleSave();
    }

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsQueryActive));
        ScheduleViewRebuild();
    }

    // ComboBox.SelectedIndex binds to these (enums have no XAML-friendly
    // binding), and setting them routes back through the enum properties.
    public int FilterIndex
    {
        get => (int)Filter;
        set { if (value >= 0) Filter = (MediaFilter)value; }
    }

    public int SortIndex
    {
        get => (int)SortKey;
        set { if (value >= 0) SortKey = (MediaSortKey)value; }
    }

    public bool IsQueryActive => Filter != MediaFilter.All || !string.IsNullOrWhiteSpace(SearchText);

    public string SortDirectionLabel => SortDescending ? UiCopy.SortDescending : UiCopy.SortAscending;


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
    /// type. Mirrored to <c>ViewerViewModel.SlideshowInterval</c> at
    /// gallery's Slideshow-button click time — the viewer's own
    /// slider writes directly to the ViewerViewModel.
    ///
    /// <para>
    /// Persisted: the setter writes through to
    /// <see cref="ISettingsService"/> so the next launch starts at
    /// the same cadence. The user controls the value via the
    /// viewer's slider (which writes to <c>ViewerViewModel.SlideshowInterval</c>,
    /// a different property that mirrors back here through the
    /// gallery's Slideshow-button click handler).
    /// </para>
    /// </summary>
    public double SlideshowInterval
    {
        get => _slideshowInterval;
        set
        {
            if (_slideshowInterval == value) return;
            _slideshowInterval = value;
            // Persist. Same write-back pattern as ViewerViewModel's
            // preference setters — see OnIsSlideshowLoopingChanged there.
            _settingsService.Current.SlideshowInterval = value;
            _settingsService.ScheduleSave();
        }
    }
    private double _slideshowInterval = 5.0;

    public ObservableCollection<MediaItem> Items { get; } = new();

    public GalleryViewModel(
        IDirectoryScanner scanner,
        IMediaLoader imageLoader,
        IVideoMetadataService videoMetadataService,
        IFolderPickerService folderPicker,
        IFilePickerService filePicker,
        IDialogService dialogService,
        ISettingsService settingsService)
    {
        _scanner = scanner;
        _imageLoader = imageLoader;
        _videoMetadataService = videoMetadataService;
        _folderPicker = folderPicker;
        _filePicker = filePicker;
        _dialogService = dialogService;
        _settingsService = settingsService;

        // Hydrate the persisted slideshow interval. The setter is
        // not used here because the backing field is private and the
        // setter would re-trigger ScheduleSave for the value we just
        // read — assignment to the field is enough.
        _slideshowInterval = _settingsService.Current.SlideshowInterval;

        // Hydrate persisted gallery toggles. Validate the enum-backed ints so a
        // hand-edited settings.json cannot select an invalid combo index.
        _suppressRebuild = true;
        var savedFilter = _settingsService.Current.GalleryFilter;
        if (savedFilter is >= 0 and <= 2) Filter = (MediaFilter)savedFilter;
        var savedSort = _settingsService.Current.GallerySortKey;
        if (savedSort is >= 0 and <= 3) SortKey = (MediaSortKey)savedSort;
        SortDescending = _settingsService.Current.GallerySortDescending;
        _suppressRebuild = false;

        // Progress<T> captures the sync context of the thread that created
        // it (the UI thread here), so the callback is auto-dispatched back
        // to the UI thread — safe to mutate StatusText / _scanErrors
        // without explicit marshalling.
        _scanErrorProgress = new Progress<ScanError>(err => _scanErrors.Add(err));

        // Keep the media-id → item index in sync with the observable collection.
        // Avoids O(n) FirstOrDefault scans in OnFileChanged when collections grow.
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    private void OnItemsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
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
                _queryDebounceCts?.Cancel();
                _queryDebounceCts?.Dispose();
                _thumbnailLoadSemaphore.Dispose();
                Items.CollectionChanged -= OnItemsCollectionChanged;
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

    /// <summary>Clears the current folder (items, watcher, cts) and returns to idle.</summary>
    [RelayCommand]
    private void CloseFolder()
    {
        var old = Interlocked.Exchange(ref _loadCts, null);
        old?.Cancel();
        old?.Dispose();

        _fileWatcher?.Dispose();
        _fileWatcher = null;

        _queryDebounceCts?.Cancel();

        FolderPath = null;
        Items.Clear();
        lock (_remainingLock)
        {
            _allSources.Clear();
            _allSourceIds.Clear();
            _remainingSources.Clear();
            CanLoadMore = false;
        }
        _metadataCache.Clear();
        Interlocked.Increment(ref _viewGeneration);
        DiscoveredCount = 0;
        IsLoadingMore = false;
        _scanErrors.Clear();
        CurrentScanningPath = "";
        LastViewedIndex = -1;
        LastViewedYOffset = 0;
        LoadingState = LoadingState.Idle;
        StatusText = GalleryStatusFormatter.IdleDefault;
    }

    /// <summary>
    /// Picks a single file, opens its containing folder, and returns the item
    /// so the view can jump straight into the viewer. Null on cancel.
    /// </summary>
    public async Task<MediaItem?> OpenFileAsync()
    {
        var extensions = _imageLoader.SupportedMedia
            .Select(m => m.Extension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var path = await _filePicker.PickFileAsync(UiCopy.OpenFile, extensions);
        if (string.IsNullOrEmpty(path)) return null;

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return null;

        // Make sure the target is visible regardless of the active query.
        _suppressRebuild = true;
        Filter = MediaFilter.All;
        SearchText = "";
        _suppressRebuild = false;

        await LoadDirectoryAsync(dir);
        return await LocateAndMaterializeAsync(path);
    }

    private async Task<MediaItem?> LocateAndMaterializeAsync(string path)
    {
        var token = _loadCts?.Token ?? CancellationToken.None;
        var id = MediaRef.FromFile(path, _imageLoader.GetKindForFile(path)).ToString();
        if (_imageIndex.TryGetValue(id, out var alreadyLoaded)) return alreadyLoaded;

        // The target can sit far down the sorted view. Materialising every
        // preceding page would decode the whole folder's thumbnails and hang
        // the UI, so SEEK: recompute the view, jump the pending queue to the
        // page that contains the target, and materialise only that page.
        var view = await BuildViewAsync(token);
        if (token.IsCancellationRequested) return null;

        var index = view.FindIndex(m => string.Equals(m.ToString(), id, StringComparison.Ordinal));
        if (index < 0) return null; // not in the current view at all

        var pageStart = index / PageSize * PageSize;
        await RunOnUiAsync(() =>
        {
            lock (_remainingLock)
            {
                _remainingSources = view.Skip(pageStart).ToList();
                CanLoadMore = _remainingSources.Count > 0;
            }
            Items.Clear();
        });

        if (token.IsCancellationRequested) return null;
        await LoadNextPageAsync(token);
        return _imageIndex.TryGetValue(id, out var item) ? item : null;
    }

    public bool RemoveItem(MediaItem item)
    {
        var wasLoaded = false;
        var index = Items.IndexOf(item);
        if (index >= 0)
        {
            Items.RemoveAt(index);
            wasLoaded = true;
        }

        lock (_remainingLock)
        {
            _allSourceIds.Remove(item.Id);
            _allSources.RemoveAll(s => s.ToString() == item.Id);
            _remainingSources.RemoveAll(s => s.ToString() == item.Id);
            DiscoveredCount = _allSources.Count;
            CanLoadMore = _remainingSources.Count > 0;
        }
        _metadataCache.TryRemove(item.Id, out _);

        if (wasLoaded)
        {
            UpdateStatus();
        }
        return wasLoaded;
    }

    public async Task DeleteItemAsync(MediaItem item)
    {
        // Refuse to delete entries that live inside an archive: doing so would
        // require rewriting the entire archive, and we do not implement that.
        // Surface a real dialog rather than a silent status-bar message so the
        // user understands why the click had no effect.
        if (item.Media.IsInArchive)
        {
            await _dialogService.ShowInfoAsync(
                UiCopy.CannotDeleteTitle,
                UiCopy.ArchiveDeleteMessage(item.Name, Path.GetFileName(item.Media.Path)),
                closeButtonText: UiCopy.Ok);
            return;
        }

        var filePath = item.Media.Path;
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
                UiCopy.ConfirmDeleteTitle,
                UiCopy.PermanentDeleteConfirm(item.Name),
                primaryButtonText: UiCopy.Delete,
                closeButtonText: UiCopy.Cancel,
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
            Trace.TraceError($"DeleteItemAsync error: {ex}");
            StatusText = GalleryStatusFormatter.FormatDeleteFailed(ex.Message);
            return;
        }

        RemoveItem(item);
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

        FolderPath = path;

        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, newCts.Token);
            var token = linkedCts.Token;

            LoadingState = LoadingState.Scanning;
            StatusText = GalleryStatusFormatter.FormatScanningStarted();
            Items.Clear();
            lock (_remainingLock)
            {
                _allSources.Clear();
                _allSourceIds.Clear();
                _remainingSources.Clear();
            }
            DiscoveredCount = 0;
            CanLoadMore = false;
            _scanErrors.Clear();
            CurrentScanningPath = "";
            _metadataCache.Clear();
            Interlocked.Increment(ref _viewGeneration);

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
            // self-consistent regardless of which progress media fired last.
            // DiscoveredCount is owned by IngestScanBatch (absolute assign), not
            // scanner Progress — Progress used to set DiscoveredCount = count while
            // ingest did += batch.Count and double-counted.

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
            // IngestScanBatch is feeding _remainingSources into the gallery.
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
                    currentPathReporter: throttledPathProgress,
                    token: token),
                token);

            await scanTask;

            if (token.IsCancellationRequested)
            {
                return;
            }

            // Scan is done. Wait for scan-time drain and any in-flight
            // Load More to settle before declaring Completed. We do NOT
            // require _remainingSources to be empty — hybrid mode
            // (边扫边灌到 200 张停) leaves the rest for the user to pull.
            // Poll 50 ms: snappy on small folders, cheap on large ones.
            while (_pageFillInFlight || IsLoadingMore)
            {
                if (token.IsCancellationRequested) break;
                await Task.Delay(50, token);
            }

            if (token.IsCancellationRequested) return;

            // The scan streamed items in discovery order. Re-apply the current
            // filter / search / sort over the now-complete set so the first
            // page reflects it (thumbnails reload from cache, so this is cheap).
            await RebuildViewAsync();
            // Let the post-rebuild first page settle so the gallery isn't
            // momentarily empty while the "completed" status is shown.
            while (_pageFillInFlight)
            {
                if (token.IsCancellationRequested) break;
                await Task.Delay(20, token);
            }
            if (token.IsCancellationRequested) return;

            StartWatching(path);
            LoadingState = LoadingState.Completed;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"LoadDirectoryAsync error: {ex}");
            LoadingState = LoadingState.Error;
            StatusText = GalleryStatusFormatter.FormatError(ex.Message);
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
    ///     media is on screen within ~50 ms even if the scanner is slow).
    /// Without the time-based flush the user would only see the first
    /// image once the scanner had already discovered ~100 sources, which
    /// is fine on a fast SSD but unacceptable on a slow network share.
    /// </summary>
    private async Task RunScanAndBatchAsync(
        string path,
        IProgress<ScanError> errorReporter,
        IProgress<string>? currentPathReporter,
        CancellationToken token)
    {
        var batch = new List<MediaRef>(ScanBatchSize);
        // Anchor of "the first media in the current batch was just added
        // at this tick". We reset it to 0 (== "no batch") when the batch
        // is empty so the next media starts a fresh 50 ms window. The
        // size cap is unchanged — 100 sources in a single dispatcher
        // post — but the time cap is measured from the *first* media in
        // the batch, not from scan start, so a slow scanner that yields
        // one media per 5 s still flushes within 50 ms of each yield.
        long batchStartTick = 0;
        var discovered = 0;
        await foreach (var media in _scanner.ScanAsync(
            path,
            recursive: true,
            extensions: _imageLoader.SupportedMedia,
            errorReporter: errorReporter,
            discoveredReporter: null,
            currentPathReporter: currentPathReporter,
            ct: token,
            statsReporter: ScanStatsReporter))
        {
            if (token.IsCancellationRequested) break;
            if (batch.Count == 0) batchStartTick = Environment.TickCount64;
            batch.Add(media);
            discovered++;
            var now = Environment.TickCount64;
            if (batch.Count >= ScanBatchSize || now - batchStartTick >= ScanBatchMs)
            {
                FlushScanBatch(batch, discovered, token);
                batch = new List<MediaRef>(ScanBatchSize);
                batchStartTick = 0;
            }
        }
        if (batch.Count > 0)
        {
            FlushScanBatch(batch, discovered, token);
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
    private void FlushScanBatch(List<MediaRef> batch, int discovered, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || batch.Count == 0) return;
        var snapshot = batch.ToList();
        _dispatcher.TryEnqueue(() => IngestScanBatch(snapshot, discovered, ct));
    }

    /// <summary>
    /// UI thread: enqueue sources and set <see cref="DiscoveredCount"/> to the
    /// absolute scan total. Starts
    /// <see cref="DrainPageFillAsync"/> when under <see cref="PageSize"/>.
    /// </summary>
    private void IngestScanBatch(List<MediaRef> batch, int discovered, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;

        var filter = Filter;
        var search = SearchText;
        var matches = new List<MediaRef>(batch.Count);
        foreach (var media in batch)
        {
            if (MatchesQuery(media, filter, search)) matches.Add(media);
        }

        lock (_remainingLock)
        {
            foreach (var media in batch)
            {
                if (_allSourceIds.Add(media.ToString()))
                {
                    _allSources.Add(media);
                    if (MatchesQuery(media, filter, search)) matches.Add(media);
                }
            }
            DiscoveredCount = _allSources.Count;
            // Stream matching items in discovery order; the final ordering is
            // applied by RebuildViewAsync once the scan completes.
            _remainingSources.AddRange(matches);
        }

        UpdateScanningStatusText();
        StartPageFill(ct);
    }

    private static bool MatchesQuery(MediaRef media, MediaFilter filter, string? search)
        => MediaQuery.MatchesFilter(filter, media.Kind)
           && MediaQuery.MatchesSearch(MediaQuery.GetDisplayName(media), search);

    /// <summary>
    /// Starts the single-consumer page fill if the first page is not full and
    /// there is pending work. Safe to call repeatedly; the
    /// <see cref="_pageFillInFlight"/> guard makes it a no-op while a fill runs.
    /// </summary>
    private void StartPageFill(CancellationToken ct)
    {
        if (_pageFillInFlight || Items.Count >= PageSize) return;

        int remaining;
        lock (_remainingLock) remaining = _remainingSources.Count;
        if (remaining == 0) return;

        _pageFillInFlight = true;
        _ = DrainPageFillAsync(ct);
    }

    // ── 视图重建（筛选 / 排序 / 查找）────────────────────────────────────

    private void ScheduleViewRebuild()
    {
        if (_disposed || _suppressRebuild) return;

        _queryDebounceCts?.Cancel();
        _queryDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _queryDebounceCts = cts;
        _ = DebounceRebuildAsync(cts.Token);
    }

    private async Task DebounceRebuildAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(250, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (ct.IsCancellationRequested || _disposed) return;
        await RebuildViewAsync();
    }

    /// <summary>
    /// Rebuilds <see cref="Items"/> from the filtered / searched / sorted view
    /// of the whole discovered set and refills the first page. Thumbnails are
    /// served from cache, so the reload is cheap.
    /// </summary>
    private async Task RebuildViewAsync()
    {
        var token = _loadCts?.Token ?? CancellationToken.None;
        var generation = Interlocked.Increment(ref _viewGeneration);

        List<MediaRef> view;
        try
        {
            view = await BuildViewAsync(token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || generation != Volatile.Read(ref _viewGeneration)) return;

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _viewGeneration)) return;
            lock (_remainingLock)
            {
                _remainingSources = view;
                CanLoadMore = view.Count > 0;
            }
            Items.Clear();
        });

        if (token.IsCancellationRequested) return;
        UpdateStatus();
        StartPageFill(token);
    }

    private async Task<List<MediaRef>> BuildViewAsync(CancellationToken ct)
    {
        List<MediaRef> all;
        lock (_remainingLock) all = _allSources.ToList();

        IEnumerable<MediaRef> query = all;
        var filter = Filter;
        if (filter != MediaFilter.All)
            query = query.Where(m => MediaQuery.MatchesFilter(filter, m.Kind));
        var search = SearchText;
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(m => MediaQuery.MatchesSearch(MediaQuery.GetDisplayName(m), search));

        var view = query.ToList();

        switch (SortKey)
        {
            case MediaSortKey.Name:
                view.Sort((a, b) => SortDescending ? MediaQuery.CompareName(b, a) : MediaQuery.CompareName(a, b));
                break;

            case MediaSortKey.Extension:
                view.Sort((a, b) => SortDescending ? MediaQuery.CompareExtension(b, a) : MediaQuery.CompareExtension(a, b));
                break;

            case MediaSortKey.Date:
            case MediaSortKey.Size:
                await EnsureMetadataAsync(view, ct);
                ct.ThrowIfCancellationRequested();
                var cache = _metadataCache;
                var byDate = SortKey == MediaSortKey.Date;
                view.Sort((a, b) =>
                {
                    var (aSize, aDate) = cache.TryGetValue(a.ToString(), out var ma) ? ma : (0L, DateTime.MinValue);
                    var (bSize, bDate) = cache.TryGetValue(b.ToString(), out var mb) ? mb : (0L, DateTime.MinValue);
                    var result = byDate ? aDate.CompareTo(bDate) : aSize.CompareTo(bSize);
                    if (result == 0) result = MediaQuery.CompareName(a, b);
                    return SortDescending ? -result : result;
                });
                break;
        }

        return view;
    }

    /// <summary>Fills (size, mtime) for every media in the list into the cache.</summary>
    private async Task EnsureMetadataAsync(List<MediaRef> list, CancellationToken ct)
    {
        var missing = list.Where(m => !_metadataCache.ContainsKey(m.ToString())).ToList();
        if (missing.Count == 0) return;

        await Task.Run(async () =>
        {
            foreach (var media in missing)
            {
                ct.ThrowIfCancellationRequested();
                var (size, mtime) = await GetSourceMetadataAsync(media, ct).ConfigureAwait(false);
                _metadataCache[media.ToString()] = (size, mtime);
            }
        }, ct).ConfigureAwait(false);
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
    /// The page size is clamped to <c>PageSize - Items.Count</c> on each
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
                    remaining = _remainingSources.Count;
                }
                if (remaining == 0) break;
                int target = PageSize - Items.Count;
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
    /// Loads more items from <c>_remainingSources</c>, up to <see cref="PageSize"/>.
    /// Links the caller's token with the current scan's CTS so that switching
    /// folders cancels in-flight Load More.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanLoadMoreCommand))]
    public async Task LoadMoreAsync(CancellationToken ct = default)
    {
        if (!CanLoadMore || IsLoadingMore) return;

        var loadCts = Volatile.Read(ref _loadCts);
        using var linkedCts = loadCts is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, loadCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        IsLoadingMore = true;
        try
        {
            await LoadNextPageAsync(linkedCts.Token);
            UpdateStatus();
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
        if (string.IsNullOrEmpty(FolderPath)) return;
        LastViewedYOffset = 0;
        await LoadDirectoryAsync(FolderPath);
    }

    private bool CanRefreshCommand() => !string.IsNullOrEmpty(FolderPath)
        && LoadingState != LoadingState.Scanning;

    private async Task LoadNextPageAsync(CancellationToken ct, int pageSize = -1)
    {
        if (pageSize < 0) pageSize = PageSize;

        // Capture the view generation so a batch started before a filter/sort
        // change is discarded rather than mixed into the rebuilt collection.
        var generation = Volatile.Read(ref _viewGeneration);

        List<MediaRef> batch;
        lock (_remainingLock)
        {
            if (_remainingSources.Count == 0)
            {
                CanLoadMore = false;
                return;
            }
            batch = _remainingSources.Take(pageSize).ToList();
            _remainingSources.RemoveRange(0, batch.Count);
            CanLoadMore = _remainingSources.Count > 0;
        }

        // FileInfo (or archive entry size) then add
        // placeholders immediately. Oriented W×H / duration arrive with
        // the thumbnail — no extra BitmapDecoder / FFmpeg open first.
        var created = new List<MediaItem>(batch.Count);
        foreach (var media in batch)
        {
            if (ct.IsCancellationRequested) return;
            var (size, mtime) = await GetSourceMetadataAsync(media, ct);
            _metadataCache[media.ToString()] = (size, mtime);
            created.Add(CreatePlaceholderItem(media, size, mtime));
        }

        await RunOnUiAsync(() =>
        {
            if (generation != Volatile.Read(ref _viewGeneration)) return;
            foreach (var item in created)
            {
                if (ct.IsCancellationRequested) return;
                Items.Add(item);
                _ = LoadThumbnailAsync(item, ct);
            }
            StatusText = GalleryStatusFormatter.FormatLoadingMore(Items.Count, DiscoveredCount);
        });
    }

    private static MediaItem CreatePlaceholderItem(MediaRef media, long size, DateTime mtime)
    {
        if (media.Kind == MediaKind.Video)
        {
            return new VideoItem(
                media: media,
                fileSize: size,
                modifiedTime: mtime,
                originalWidth: 0,
                originalHeight: 0,
                duration: TimeSpan.Zero,
                hasAudio: false,
                codec: string.Empty);
        }

        return new ImageItem(
            media: media,
            fileSize: size,
            modifiedTime: mtime,
            originalWidth: 0,
            originalHeight: 0);
    }

    private Task RunOnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }
        return tcs.Task;
    }

    /// <summary>
    /// Returns the (uncompressed size, modification time) for the given
    /// media. For loose files this is just FileInfo. For archive entries
    /// the size comes from the archive's central directory; the modification
    /// time is the archive file's mtime (entry-level mtime is not reliably
    /// exposed by SharpCompress and is uninteresting for cache invalidation
    /// when the archive file itself is the media of truth).
    /// </summary>
    private static Task<(long Size, DateTime ModifiedTime)> GetSourceMetadataAsync(MediaRef media, CancellationToken ct)
    {
        if (!media.IsInArchive)
        {
            var info = new FileInfo(media.Path);
            return Task.FromResult(
                (info.Exists ? info.Length : 0L,
                 info.Exists ? info.LastWriteTime : DateTime.MinValue));
        }

        return Task.Run(() =>
        {
            try
            {
                var archiveInfo = new FileInfo(media.Path);
                var size = ArchiveHelper.GetEntryUncompressedSize(media.Path, media.ArchiveEntry!);
                return (size, archiveInfo.Exists ? archiveInfo.LastWriteTime : DateTime.MinValue);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"GetSourceMetadataAsync archive error for {media}: {ex.Message}");
            }
            return (0L, DateTime.MinValue);
        }, ct);
    }

    /// <summary>
    /// Status bar after settle / Load More / watcher mutations.
    /// Wording from shared <see cref="GalleryStatusFormatter"/>.
    /// </summary>
    private void UpdateStatus()
    {
        int remaining;
        lock (_remainingLock) remaining = _remainingSources.Count;
        var videos = Items.Count(i => i.IsVideo);
        var images = Items.Count - videos;
        var breakdown = GalleryStatusFormatter.FormatItemBreakdown(images, videos);
        string? firstName = null;
        string? firstReason = null;
        if (_scanErrors.Count > 0)
        {
            firstName = Path.GetFileName(_scanErrors[0].Path);
            firstReason = _scanErrors[0].Reason;
        }

        var queryLabel = GalleryStatusFormatter.FormatQueryLabel(Filter, SearchText);
        StatusText = GalleryStatusFormatter.FormatGallery(
            breakdown,
            DiscoveredCount,
            remaining,
            scanErrorCount: _scanErrors.Count,
            firstSkippedFileName: firstName,
            firstSkippedReason: firstReason,
            queryLabel: queryLabel,
            matchedCount: queryLabel is null ? null : Items.Count + remaining);
    }

    /// <summary>
    /// Status while scanner is running. Uses path form when
    /// <see cref="CurrentScanningPath"/> is available (WinUI-only live path UI).
    /// </summary>
    private void UpdateScanningStatusText()
    {
        var videos = Items.Count(i => i.IsVideo);
        var images = Items.Count - videos;
        var breakdown = GalleryStatusFormatter.FormatItemBreakdown(images, videos);
        string? firstName = null;
        string? firstReason = null;
        if (_scanErrors.Count > 0)
        {
            firstName = Path.GetFileName(_scanErrors[0].Path);
            firstReason = _scanErrors[0].Reason;
        }

        StatusText = GalleryStatusFormatter.FormatScanning(
            DiscoveredCount,
            breakdown,
            currentPath: string.IsNullOrEmpty(CurrentScanningPath) ? null : CurrentScanningPath,
            scanErrorCount: _scanErrors.Count,
            firstSkippedFileName: firstName,
            firstSkippedReason: firstReason,
            queryLabel: GalleryStatusFormatter.FormatQueryLabel(Filter, SearchText));
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
            StatusText = GalleryStatusFormatter.FormatWatchUnavailable(ex.Message);
        }
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        // Capture the load cts token. When LoadDirectoryAsync starts a new
        // folder it cancels and replaces _loadCts, so any lambdas already
        // enqueued will see the cancelled token and early-exit instead of
        // mutating the new Items collection.
        var token = _loadCts?.Token ?? CancellationToken.None;
        if (DirectoryScanner.IsRecycleBin(info.Path)) return;

        var enqueued = _dispatcher.TryEnqueue(async () =>
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

        if (!enqueued)
        {
            Trace.TraceWarning($"OnFileChanged dropped (dispatcher unavailable): {info.ChangeType} {info.Path}");
            return;
        }
    }

    private async Task HandleCreatedAsync(FileChangeInfo info, CancellationToken token)
    {
        if (!File.Exists(info.Path)) return;

        if (ArchiveHelper.IsArchiveFileName(info.Path) && ArchiveHelper.IsArchive(info.Path))
        {
            // A new archive appeared: enumerate its entries into the master
            // list, then rebuild the (filtered/sorted) view.
            var (refs, error) = await AddArchiveEntriesAsync(info.Path, token);
            if (error is not null) _scanErrors.Add(error);
            if (AddSourcesToMaster(refs)) ScheduleViewRebuild();
            else UpdateStatus();
            return;
        }

        if (!_imageLoader.IsSupportedFormat(info.Path)) return;
        var media = MediaRef.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path));
        if (!AddSourcesToMaster(new[] { media })) return;

        var (size, mtime) = await GetSourceMetadataAsync(media, token);
        if (token.IsCancellationRequested) return;
        _metadataCache[media.ToString()] = (size, mtime);
        ScheduleViewRebuild();
    }

    /// <summary>
    /// Enumerates media entries in the given archive. Returns the refs plus an
    /// optional <see cref="ScanError"/> if the archive could not be read at all
    /// (caller surfaces it in the status bar alongside scan errors).
    /// </summary>
    private async Task<(List<MediaRef> Refs, ScanError? Error)> AddArchiveEntriesAsync(
        string archivePath, CancellationToken token)
    {
        var result = new List<MediaRef>();
        try
        {
            // Build the extension → kind lookup once; the full media list
            // covers both image and video, so an archive's entries go through
            // the same dispatch as loose files.
            var extensionMap = new Dictionary<string, MediaKind>(StringComparer.OrdinalIgnoreCase);
            foreach (var (ext, kind) in _imageLoader.SupportedMedia)
            {
                extensionMap[ext] = kind;
            }
            var extensionSet = new HashSet<string>(extensionMap.Keys, StringComparer.OrdinalIgnoreCase);

            await Task.Run(() =>
            {
                foreach (var entry in ArchiveHelper.ListEntries(archivePath, extensionSet))
                {
                    if (token.IsCancellationRequested) break;
                    var ext = Path.GetExtension(entry.Key);
                    if (!extensionMap.TryGetValue(ext, out var kind)) continue;
                    result.Add(MediaRef.FromArchive(archivePath, entry.Key, kind));
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

    /// <summary>Adds refs not already known to the master list. Returns true if anything was added.</summary>
    private bool AddSourcesToMaster(IReadOnlyList<MediaRef> refs)
    {
        var added = false;
        lock (_remainingLock)
        {
            foreach (var media in refs)
            {
                if (_allSourceIds.Add(media.ToString()))
                {
                    _allSources.Add(media);
                    added = true;
                }
            }
            if (added) DiscoveredCount = _allSources.Count;
        }
        return added;
    }

    /// <summary>
    /// Removes a source from the master list, the pending view and the loaded
    /// collection (if present). Returns true if it was known.
    /// </summary>
    private bool RemoveSourceById(string id)
    {
        bool known;
        lock (_remainingLock)
        {
            known = _allSourceIds.Remove(id);
            if (known)
            {
                _allSources.RemoveAll(s => s.ToString() == id);
                _remainingSources.RemoveAll(s => s.ToString() == id);
                DiscoveredCount = _allSources.Count;
                CanLoadMore = _remainingSources.Count > 0;
            }
        }
        if (!known) return false;

        _metadataCache.TryRemove(id, out _);
        if (_imageIndex.TryGetValue(id, out var loaded))
        {
            Items.Remove(loaded);
        }
        return true;
    }

    /// <summary>
    /// Maps a SharpCompress / IO exception to a short reason suitable for
    /// the status bar. Same logic as the scanner-side classifier; duplicated
    /// here because this code path (FileSystemWatcher → add new archive)
    /// never goes through the scanner.
    /// </summary>
    private static string ClassifyArchiveError(Exception ex) => ex switch
    {
        FileNotFoundException => UiCopy.ArchiveFileMissing,
        IOException => UiCopy.ArchiveIoError,
        UnauthorizedAccessException => UiCopy.ArchiveAccessDenied,
        _ => UiCopy.ArchiveUnsupportedOrCorrupt
    };

    private void HandleDeleted(FileChangeInfo info)
    {
        // Direct file deletion (loose .jpg / .mp4): match by id, using the
        // kind from the extension so a .mp4 finds its VideoItem.
        var directId = MediaRef.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path)).ToString();
        if (!RemoveSourceById(directId))
        {
            // Archive deletion: remove every entry whose media points at the
            // gone archive (case-insensitive to mirror Windows file lookup).
            List<string> archiveIds;
            lock (_remainingLock)
            {
                archiveIds = _allSources
                    .Where(m => string.Equals(m.Path, info.Path, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToString())
                    .ToList();
            }
            foreach (var id in archiveIds) RemoveSourceById(id);
        }
        UpdateStatus();
    }

    private async Task HandleModifiedAsync(FileChangeInfo info, CancellationToken token)
    {
        // The watcher only reports the file path, not which archive entry
        // changed, so Modified on an archive is too coarse to act on — the
        // safest behaviour is to leave the existing thumbnails alone.
        if (ArchiveHelper.IsArchiveFileName(info.Path)) return;

        // Include the file's kind in the lookup id — a .mp4's id was
        // created with Kind=Video, so a Kind=Image id won't find it.
        var id = MediaRef.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path)).ToString();
        if (!_imageIndex.TryGetValue(id, out var modifiedItem)) return;
        modifiedItem.Thumbnail = null;
        modifiedItem.FullImage = null;
        await LoadThumbnailAsync(modifiedItem, CancellationToken.None);
        if (token.IsCancellationRequested) return;

        // size/mtime may have changed, which affects date/size ordering.
        var (size, mtime) = await GetSourceMetadataAsync(modifiedItem.Media, token);
        _metadataCache[id] = (size, mtime);
        ScheduleViewRebuild();
    }

    private async Task HandleRenamedAsync(FileChangeInfo info, CancellationToken token)
    {
        // Archive renames: drop the old entries, re-add from the new path.
        if (info.OldPath != null && ArchiveHelper.IsArchiveFileName(info.OldPath))
        {
            List<string> oldIds;
            lock (_remainingLock)
            {
                oldIds = _allSources
                    .Where(m => string.Equals(m.Path, info.OldPath, StringComparison.OrdinalIgnoreCase))
                    .Select(m => m.ToString())
                    .ToList();
            }
            foreach (var id in oldIds) RemoveSourceById(id);

            if (File.Exists(info.Path) && ArchiveHelper.IsArchive(info.Path))
            {
                var (refs, error) = await AddArchiveEntriesAsync(info.Path, token);
                if (error is not null) _scanErrors.Add(error);
                AddSourcesToMaster(refs);
            }
            ScheduleViewRebuild();
            return;
        }

        if (info.OldPath == null) return;
        // Same kind-aware lookup as HandleModified/HandleDeleted.
        var oldId = MediaRef.FromFile(info.OldPath, _imageLoader.GetKindForFile(info.OldPath)).ToString();
        var newMedia = MediaRef.FromFile(info.Path, _imageLoader.GetKindForFile(info.Path));

        // Keep the same MediaItem instance when it is loaded (preserves state);
        // the remove/add pair refreshes the id index. Otherwise only the master
        // list changes and the debounced rebuild picks it up.
        if (_imageIndex.TryGetValue(oldId, out var renamedItem))
        {
            Items.Remove(renamedItem);   // index removal on OldPath
            renamedItem.UpdateMedia(newMedia);
            Items.Add(renamedItem);      // index insertion on NewPath
            renamedItem.Thumbnail = null;
            renamedItem.FullImage = null;
            await LoadThumbnailAsync(renamedItem, CancellationToken.None);
            if (token.IsCancellationRequested) return;
        }

        lock (_remainingLock)
        {
            _allSourceIds.Remove(oldId);
            _allSources.RemoveAll(s => s.ToString() == oldId);
            if (_allSourceIds.Add(newMedia.ToString())) _allSources.Add(newMedia);
            DiscoveredCount = _allSources.Count;
        }
        _metadataCache.TryRemove(oldId, out _);
        UpdateStatus();
        ScheduleViewRebuild();
    }

    private async Task LoadThumbnailAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            if (item.Thumbnail != null)
            {
                if (!item.Media.IsInArchive && File.Exists(item.Media.Path))
                {
                    var fileInfo = new FileInfo(item.Media.Path);
                    if (fileInfo.LastWriteTime == item.ModifiedTime)
                        return;
                }
                item.Thumbnail = null;
            }

            await _thumbnailLoadSemaphore.WaitAsync(ct);
            try
            {
                var thumb = item.Media.Kind == MediaKind.Video
                    ? await _videoMetadataService.ExtractVideoThumbnailAsync(item.Media, GalleryMetrics.ThumbMaxEdge, ct)
                    : await _imageLoader.LoadThumbnailAsync(item.Media, GalleryMetrics.ThumbMaxEdge, ct);

                if (thumb is { } t)
                {
                    await RunOnUiAsync(async () =>
                    {
                        if (ct.IsCancellationRequested) return;
                        var src = new SoftwareBitmapSource();
                        await src.SetBitmapAsync(t.Bitmap);
                        item.Thumbnail = src;
                        item.ApplyOriginalSize(t.OriginalWidth, t.OriginalHeight);
                        if (item.Media.Kind == MediaKind.Video)
                        {
                            item.FullImage = src;
                            if (item is VideoItem video && t.Duration is { } d)
                                video.ApplyDuration(d);
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

    private Task RunOnUiAsync(Func<Task> action)
    {
        var tcs = new TaskCompletionSource();
        if (!_dispatcher.TryEnqueue(async () =>
        {
            try
            {
                await action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        }))
        {
            tcs.TrySetCanceled();
        }
        return tcs.Task;
    }
}
