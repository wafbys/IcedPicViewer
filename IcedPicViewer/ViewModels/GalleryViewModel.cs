// Copyright (c) IcedPicViewer. All rights reserved.

using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IcedPicViewer.ViewModels;

public partial class GalleryViewModel : ObservableObject, IDisposable
{
    private readonly IDirectoryScanner _scanner;
    private readonly IImageLoader _imageLoader;
    private readonly IFolderPickerService _folderPicker;
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
    private List<string> _remainingFilePaths = new();
    private const int PageSize = 150;

    // O(1) path → item lookup. Mirrors the Images collection; maintained via
    // the CollectionChanged handler in the constructor. Windows paths are
    // case-insensitive, so use OrdinalIgnoreCase.
    private readonly Dictionary<string, ImageItem> _imageIndex = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    private LoadingState _loadingState;

    [ObservableProperty]
    private string _statusText = "Select a folder to start";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _lastViewedIndex = -1;

    [ObservableProperty]
    private double _lastViewedYOffset = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LoadMoreVisibility))]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    private bool _canLoadMore;

    public Visibility LoadMoreVisibility => CanLoadMore ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool _isLoadingMore;

    partial void OnIsLoadingMoreChanged(bool value)
    {
        LoadMoreCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<ImageItem> Images { get; } = new();

    public GalleryViewModel(
        IDirectoryScanner scanner,
        IImageLoader imageLoader,
        IFolderPickerService folderPicker)
    {
        _scanner = scanner;
        _imageLoader = imageLoader;
        _folderPicker = folderPicker;

        // Keep the path → item index in sync with the observable collection.
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
                _imageIndex.Remove(item.Path);
            }
        }

        if (e.NewItems != null)
        {
            foreach (ImageItem item in e.NewItems)
            {
                _imageIndex[item.Path] = item;
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
            _remainingFilePaths.Remove(item.Path);
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
        if (!File.Exists(item.Path)) return;

        bool useRecycleBin;
        try
        {
            useRecycleBin = DriveInfo.GetDrives()
                .FirstOrDefault(d => d.Name == Path.GetPathRoot(item.Path))?.DriveType != DriveType.Network;
        }
        catch
        {
            useRecycleBin = true;
        }

        if (!useRecycleBin)
        {
            var dialog = new ContentDialog
            {
                Title = "确认删除",
                Content = $"确定要永久删除 \"{item.Name}\" 吗？此操作无法撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };

            dialog.XamlRoot = App.MainWindow?.Content.XamlRoot;
            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;
        }

        try
        {
            if (useRecycleBin)
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    item.Path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            }
            else
            {
                File.Delete(item.Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"DeleteImageAsync error: {ex}");
            StatusText = $"Delete failed: {ex.Message}";
            return;
        }

        RemoveImage(item);
    }

    public async Task LoadDirectoryAsync(string path, CancellationToken ct = default)
    {
        var currentCts = new CancellationTokenSource();
        try
        {
            _loadCts?.Cancel();
            _loadCts?.Dispose();
            _loadCts = currentCts;
            currentCts = null;

            var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _loadCts.Token);

            LoadingState = LoadingState.Scanning;
            Images.Clear();
            lock (_remainingLock)
            {
                _remainingFilePaths.Clear();
            }
            TotalCount = 0;
            CanLoadMore = false;

            // First pass: discover all files (usually fast)
            var allPaths = new List<string>();
            await foreach (var filePath in _scanner.ScanAsync(path, recursive: true, extensions: _imageLoader.SupportedExtensions, ct: linkedCts.Token))
            {
                if (linkedCts.Token.IsCancellationRequested) break;
                allPaths.Add(filePath);
            }

            if (linkedCts.Token.IsCancellationRequested)
            {
                return;
            }

            lock (_remainingLock)
            {
                _remainingFilePaths = allPaths;
            }
            TotalCount = allPaths.Count;

            // Load first page
            await LoadNextPageAsync(linkedCts.Token);

            StartWatching(path);
            LoadingState = LoadingState.Completed;
            UpdateStatusText();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"LoadDirectoryAsync error: {ex}");
            LoadingState = LoadingState.Completed;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            currentCts?.Dispose();
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

    private async Task LoadNextPageAsync(CancellationToken ct)
    {
        List<string> batch;
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

        var itemCount = Images.Count;

        foreach (var filePath in batch)
        {
            if (ct.IsCancellationRequested) break;

            var fileInfo = new FileInfo(filePath);
            var size = await _imageLoader.GetImageSizeAsync(filePath, ct);

            var item = new ImageItem(
                id: filePath,
                name: Path.GetFileName(filePath),
                path: filePath,
                fileSize: fileInfo.Exists ? fileInfo.Length : 0,
                modifiedTime: fileInfo.Exists ? fileInfo.LastWriteTime : DateTime.MinValue,
                originalWidth: size?.Width ?? 0,
                originalHeight: size?.Height ?? 0
            );

            _dispatcher.TryEnqueue(() =>
            {
                Images.Add(item);
                itemCount++;
                StatusText = $"Loading {itemCount} / {TotalCount}...";
            });

            _ = LoadThumbnailAsync(item, ct);
        }
    }

    /// <summary>
    /// Centralized status text formatter used after collection mutations so the
    /// "Loaded X / Y images" wording stays consistent across Created / Deleted /
    /// LoadDirectory / LoadMore paths.
    /// </summary>
    private void UpdateStatusText()
    {
        var loaded = Images.Count;
        StatusText = (TotalCount > 0 && TotalCount != loaded)
            ? $"Loaded {loaded} / {TotalCount} images"
            : $"Loaded {loaded} images";
    }

    private void StartWatching(string path)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = _scanner.Watch(path, recursive: true, OnFileChanged);
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                switch (info.ChangeType)
                {
                    case WatchChangeType.Created:
                        if (!_imageLoader.IsSupportedFormat(info.Path)) return;
                        if (_imageIndex.ContainsKey(info.Path)) return;
                        var fileInfo = new FileInfo(info.Path);
                        if (!fileInfo.Exists) return;
                        var size = await _imageLoader.GetImageSizeAsync(info.Path, CancellationToken.None);
                        var newItem = new ImageItem(
                            id: info.Path,
                            name: Path.GetFileName(info.Path),
                            path: info.Path,
                            fileSize: fileInfo.Length,
                            modifiedTime: fileInfo.LastWriteTime,
                            originalWidth: size?.Width ?? 0,
                            originalHeight: size?.Height ?? 0
                        );
                        Images.Add(newItem);
                        TotalCount++;
                        UpdateStatusText();
                        await LoadThumbnailAsync(newItem, CancellationToken.None);
                        break;

                    case WatchChangeType.Deleted:
                        if (_imageIndex.TryGetValue(info.Path, out var itemToRemove))
                        {
                            Images.Remove(itemToRemove);
                            if (TotalCount > 0)
                            {
                                TotalCount--;
                            }
                            lock (_remainingLock)
                            {
                                _remainingFilePaths.Remove(info.Path);
                            }
                            CanLoadMore = _remainingFilePaths.Count > 0;
                            UpdateStatusText();
                        }
                        break;

                    case WatchChangeType.Modified:
                        if (_imageIndex.TryGetValue(info.Path, out var modifiedItem))
                        {
                            modifiedItem.Thumbnail = null;
                            modifiedItem.FullImage = null;
                            await LoadThumbnailAsync(modifiedItem, CancellationToken.None);
                        }
                        break;

                    case WatchChangeType.Renamed:
                        // The watcher delivers Renamed with NewPath = info.Path, OldPath = info.OldPath.
                        // We need to rebind the existing item to the new path so the index stays consistent.
                        if (info.OldPath != null && _imageIndex.TryGetValue(info.OldPath, out var renamedItem))
                        {
                            Images.Remove(renamedItem);  // triggers index removal on OldPath
                            renamedItem.UpdatePath(info.Path, Path.GetFileName(info.Path));
                            Images.Add(renamedItem);     // triggers index insertion on NewPath
                            renamedItem.Thumbnail = null;
                            renamedItem.FullImage = null;
                            await LoadThumbnailAsync(renamedItem, CancellationToken.None);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"OnFileChanged error for {info.Path} ({info.ChangeType}): {ex}");
            }
        });
    }

    private async Task LoadThumbnailAsync(ImageItem item, CancellationToken ct)
    {
        if (item.Thumbnail != null)
        {
            var fileInfo = new FileInfo(item.Path);
            if (fileInfo.Exists && fileInfo.LastWriteTime == item.ModifiedTime)
            {
                return;
            }
            item.Thumbnail = null;
        }

        await _thumbnailLoadSemaphore.WaitAsync(ct);
        try
        {
            var thumbnail = await _imageLoader.LoadThumbnailAsync(item.Path, 400, ct);
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
            System.Diagnostics.Trace.TraceError($"LoadThumbnailAsync error for {item.Path}: {ex}");
        }
        finally
        {
            _thumbnailLoadSemaphore.Release();
        }
    }
}
