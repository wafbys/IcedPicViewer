using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcedPicViewer.Helpers;
using IcedPicViewer.Models;
using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace IcedPicViewer.ViewModels;

public partial class GalleryViewModel : ObservableObject
{
    private readonly IDirectoryScanner _scanner;
    private readonly IImageLoader _imageLoader;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    private CancellationTokenSource? _loadCts;
    private int _loadedThumbnailCount;
    private IDisposable? _fileWatcher;

    [ObservableProperty]
    private LoadingState _loadingState;

    [ObservableProperty]
    private string _statusText = "Select a folder to start";

    [ObservableProperty]
    private int _totalCount;

    public ObservableCollection<ImageItem> Images { get; } = new();

    public GalleryViewModel(
        IDirectoryScanner scanner,
        IImageLoader imageLoader)
    {
        _scanner = scanner;
        _imageLoader = imageLoader;
    }

    [RelayCommand]
    private async Task OpenFolderAsync()
    {
        var folderPath = FolderBrowserHelper.SelectFolder("Select Image Folder");
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
            TotalCount = Images.Count;
            StatusText = $"Loaded {Images.Count} images";
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
            System.Diagnostics.Debug.WriteLine($"DeleteImageAsync error: {ex.Message}");
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
            _loadedThumbnailCount = 0;
            TotalCount = 0;

            var itemCount = 0;

            await foreach (var filePath in _scanner.ScanAsync(path, recursive: true, extensions: _imageLoader.SupportedExtensions, ct: linkedCts.Token))
            {
                if (linkedCts.Token.IsCancellationRequested) break;

                var fileInfo = new FileInfo(filePath);
                var size = await _imageLoader.GetImageSizeAsync(filePath, linkedCts.Token);
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
                    TotalCount = ++itemCount;
                    StatusText = $"Found {TotalCount} images...";
                });

                _ = LoadThumbnailAsync(item, linkedCts.Token);
            }

            StartWatching(path);
            LoadingState = LoadingState.Completed;
            StatusText = $"Loaded {Images.Count} images";
            linkedCts.Dispose();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"LoadDirectoryAsync error: {ex}");
            LoadingState = LoadingState.Completed;
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            currentCts?.Dispose();
        }
    }

    private void StartWatching(string path)
    {
        _fileWatcher?.Dispose();
        _fileWatcher = _scanner.Watch(path, recursive: true, OnFileChanged);
    }

    private void OnFileChanged(FileChangeInfo info)
    {
        var watchCts = new CancellationTokenSource();
        _dispatcher.TryEnqueue(async () =>
        {
            try
            {
                switch (info.ChangeType)
                {
                    case WatchChangeType.Created:
                        if (!_imageLoader.IsSupportedFormat(info.Path)) return;
                        if (Images.Any(i => i.Path == info.Path)) return;
                        var fileInfo = new FileInfo(info.Path);
                        if (!fileInfo.Exists) return;
                        var size = await _imageLoader.GetImageSizeAsync(info.Path, watchCts.Token);
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
                        TotalCount = Images.Count;
                        StatusText = $"Loaded {Images.Count} images";
                        await LoadThumbnailAsync(newItem, watchCts.Token);
                        break;

                    case WatchChangeType.Deleted:
                        var itemToRemove = Images.FirstOrDefault(i => i.Path == info.Path);
                        if (itemToRemove != null)
                        {
                            Images.Remove(itemToRemove);
                            TotalCount = Images.Count;
                            StatusText = $"Loaded {Images.Count} images";
                        }
                        break;

                    case WatchChangeType.Modified:
                    case WatchChangeType.Renamed:
                        var item = Images.FirstOrDefault(i => i.Path == info.Path);
                        if (item != null)
                        {
                            item.Thumbnail = null;
                            item.FullImage = null;
                            await LoadThumbnailAsync(item, watchCts.Token);
                        }
                        break;
                }
            }
            finally
            {
                watchCts.Dispose();
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

        try
        {
            var thumbnail = await _imageLoader.LoadThumbnailAsync(item.Path, 400, ct);
            if (thumbnail != null)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    item.Thumbnail = thumbnail;
                    item.IsLoading = false;
                    _loadedThumbnailCount++;
                });
            }
        }
        catch
        {
            _dispatcher.TryEnqueue(() =>
            {
                item.IsLoading = false;
                _loadedThumbnailCount++;
            });
        }
    }
}
