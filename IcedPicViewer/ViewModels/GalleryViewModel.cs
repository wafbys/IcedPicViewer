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

namespace IcedPicViewer.ViewModels;

public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly IDirectoryScanner _scanner;
    private readonly IImageLoader _imageLoader;
    private readonly IFolderPickerService _folderPicker;
    private readonly IDialogService _dialogService;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? _loadCts;
    private IDisposable? _fileWatcher;
    private bool _disposed;

    // Limit concurrent thumbnail loads
    private readonly SemaphoreSlim _thumbnailLoadSemaphore = new(initialCount: 6, maxCount: 6);

    // For incremental loading while keeping masonry visual.
    // Both LoadNextPageAsync (worker thread) and OnFileChanged (dispatcher thread) read/write
    // this list, so guard it with _remainingLock to avoid race conditions.
    private readonly object _remainingLock = new();
    private List<ImageSource> _remainingFilePaths = new();
    private const int PageSize = 150;

    // O(1) source-id → item lookup. Mirrors the Images collection; maintained
    // via the CollectionChanged handler in the constructor. Keys are
    // ImageSource.ToString(), which is case-sensitive for archive entries
    // (archive keys preserve the original case) and case-insensitive-friendly
    // for loose files (Windows paths are case-insensitive, but the conflict
    // would only occur if two files differ only in case, which FileSystem
    // itself disallows on Windows).
    private readonly Dictionary<string, ImageItem> _imageIndex = new(StringComparer.Ordinal);

    // Files that the scanner encountered but could not read (e.g. a corrupt
    // .zip with a valid extension). The scanner is fire-and-forget for
    // these — we surface the count + first-failure in the status bar so the
    // user can identify which file is the problem.
    private readonly List<ScanError> _scanErrors = new();
    private readonly IProgress<ScanError> _scanErrorProgress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial LoadingState LoadingState { get; set; }

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

    public ObservableCollection<ImageItem> Images { get; } = new();

    public GalleryViewModel(
        IDirectoryScanner scanner,
        IImageLoader imageLoader,
        IFolderPickerService folderPicker,
        IDialogService dialogService)
    {
        _scanner = scanner;
        _imageLoader = imageLoader;
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
            foreach (ImageItem item in e.OldItems)
            {
                _imageIndex.Remove(item.Id);
            }
        }

        if (e.NewItems != null)
        {
            foreach (ImageItem item in e.NewItems)
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

    public bool RemoveImage(ImageItem item)
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

    public async Task DeleteImageAsync(ImageItem item)
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

            LoadingState = LoadingState.Scanning;
            Images.Clear();
            lock (_remainingLock)
            {
                _remainingFilePaths.Clear();
            }
            TotalCount = 0;
            CanLoadMore = false;
            _scanErrors.Clear();

            // First pass: discover all images (usually fast)
            var allSources = new List<ImageSource>();
            await foreach (var source in _scanner.ScanAsync(
                path,
                recursive: true,
                extensions: _imageLoader.SupportedExtensions,
                errorReporter: _scanErrorProgress,
                ct: linkedCts.Token))
            {
                if (linkedCts.Token.IsCancellationRequested) break;
                allSources.Add(source);
            }

            if (linkedCts.Token.IsCancellationRequested)
            {
                return;
            }

            lock (_remainingLock)
            {
                _remainingFilePaths = allSources;
            }
            TotalCount = allSources.Count;

            // Load first page
            await LoadNextPageAsync(linkedCts.Token);

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

    private async Task LoadNextPageAsync(CancellationToken ct)
    {
        List<ImageSource> batch;
        lock (_remainingLock)
        {
            if (_remainingFilePaths.Count == 0)
            {
                CanLoadMore = false;
                return;
            }
            batch = _remainingFilePaths.Take(PageSize).ToList();
            _remainingFilePaths.RemoveRange(0, batch.Count);
            CanLoadMore = _remainingFilePaths.Count > 0;
        }

        foreach (var source in batch)
        {
            if (ct.IsCancellationRequested) break;

            (long size, DateTime mtime) = await GetSourceMetadataAsync(source, ct);
            // GetImageSizeAsync reads only the image header (BitmapDecoder, ~ms even
            // for multi-MP files). Used to populate ImageItem.OriginalWidth/Height
            // so the gallery overlay and the image viewer info bar can show real
            // dimensions instead of "Unknown".
            var dimensions = await _imageLoader.GetImageSizeAsync(source, ct);

            // If a new LoadDirectoryAsync fired while we were awaiting above,
            // ct is now cancelled — abandon the rest of the batch. Items already
            // enqueued below will see the cancelled token when they run and early-exit.
            if (ct.IsCancellationRequested) break;

            var item = new ImageItem(
                source: source,
                fileSize: size,
                modifiedTime: mtime,
                originalWidth: dimensions?.Width ?? 0,
                originalHeight: dimensions?.Height ?? 0);

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
        string baseText = (TotalCount > 0 && TotalCount != loaded)
            ? $"Loaded {loaded} / {TotalCount} images"
            : $"Loaded {loaded} images";

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
        if (_imageIndex.ContainsKey(ImageSource.FromFile(info.Path).ToString())) return;

        var (size, mtime) = await GetSourceMetadataAsync(ImageSource.FromFile(info.Path), token);
        if (token.IsCancellationRequested) return;
        var source = ImageSource.FromFile(info.Path);
        var dimensions = await _imageLoader.GetImageSizeAsync(source, token);
        if (token.IsCancellationRequested) return;

        var item = new ImageItem(
            source: source,
            fileSize: size,
            modifiedTime: mtime,
            originalWidth: dimensions?.Width ?? 0,
            originalHeight: dimensions?.Height ?? 0);

        Images.Add(item);
        TotalCount++;
        UpdateStatusText();
        await LoadThumbnailAsync(item, CancellationToken.None);
    }

    /// <summary>
    /// Enumerates image entries in the given archive and creates an
    /// <see cref="ImageItem"/> for each. Returns the list of items plus an
    /// optional <see cref="ScanError"/> if the archive could not be read at
    /// all (caller surfaces it in the status bar alongside initial scan
    /// errors).
    /// </summary>
    private async Task<(List<ImageItem> Items, ScanError? Error)> AddArchiveEntriesAsync(
        string archivePath, CancellationToken token)
    {
        var result = new List<ImageItem>();
        try
        {
            var archiveInfo = new FileInfo(archivePath);
            var mtime = archiveInfo.Exists ? archiveInfo.LastWriteTime : DateTime.MinValue;

            await Task.Run(async () =>
            {
                var entries = ArchiveHelper.ListEntries(archivePath, _imageLoader.SupportedExtensions
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;
                    var source = ImageSource.FromArchive(archivePath, entry.Key);
                    if (_imageIndex.ContainsKey(source.ToString())) continue;
                    // GetImageSizeAsync uses BitmapDecoder, which only reads the
                    // header (few KB) — cheap enough to do for every entry. This
                    // also gives the gallery overlay a real WxH instead of "Unknown".
                    var dims = await _imageLoader.GetImageSizeAsync(source, token);

                    result.Add(new ImageItem(
                        source: source,
                        fileSize: entry.UncompressedSize,
                        modifiedTime: mtime,
                        originalWidth: dims?.Width ?? 0,
                        originalHeight: dims?.Height ?? 0));
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
        // Direct image deletion (e.g. a loose .jpg removed): match by id.
        var directId = ImageSource.FromFile(info.Path).ToString();
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

        var id = ImageSource.FromFile(info.Path).ToString();
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
        var oldId = ImageSource.FromFile(info.OldPath).ToString();
        if (!_imageIndex.TryGetValue(oldId, out var renamedItem)) return;

        Images.Remove(renamedItem);  // triggers index removal on OldPath
        renamedItem.UpdateSource(ImageSource.FromFile(info.Path));
        Images.Add(renamedItem);     // triggers index insertion on NewPath
        renamedItem.Thumbnail = null;
        renamedItem.FullImage = null;
        await LoadThumbnailAsync(renamedItem, CancellationToken.None);
        if (token.IsCancellationRequested) return;
        UpdateStatusText();
    }

    private async Task LoadThumbnailAsync(ImageItem item, CancellationToken ct)
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
            var thumbnail = await _imageLoader.LoadThumbnailAsync(item.Source, 400, ct);
            if (thumbnail != null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    item.Thumbnail = thumbnail;
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
}
