// Copyright (c) IcedPicViewer. All rights reserved.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using IcedPicViewer.Avalonia.Controls;
using IcedPicViewer.Avalonia.Services;
using IcedPicViewer.Avalonia.ViewModels;
using IcedPicViewer.Services.Interfaces;

namespace IcedPicViewer.Avalonia.Views;

public partial class MainWindow : Window
{
    private bool _autoLoadArmed = true;
    private bool _restoredBounds;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"IcedPicViewer ({IcedPicViewer.Avalonia.BuildInfo.CommitShort})";
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.PickFolderAsync = PickFolderAsync;
        vm.ConfirmAsync = (title, message, alertOnly) =>
            ConfirmDialog.ShowAsync(this, title, message, alertOnly);
        vm.ApplyFullscreen = full =>
        {
            // Don't overwrite saved normal bounds while fullscreen.
            WindowState = full ? WindowState.FullScreen : WindowState.Normal;
        };
        vm.RequestScrollToItem = ScrollGalleryToItem;

        RestoreWindowBounds(vm.Settings.Current);
        // No auto-open of last folder — user picks a folder each session.
    }

    /// <summary>
    /// After leaving the viewer, scroll the masonry gallery so the last-viewed
    /// card is near the top of the viewport (WinUI-like resume).
    /// </summary>
    private void ScrollGalleryToItem(MediaItemViewModel item)
    {
        if (DataContext is not MainViewModel vm) return;
        var index = vm.Items.IndexOf(item);
        if (index < 0) return;

        // Wait one layout pass so the gallery is visible and arranged again.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var masonry = GalleryItems.GetVisualDescendants()
                    .OfType<MasonryPanel>()
                    .FirstOrDefault();
                if (masonry is not null && masonry.TryGetItemTop(index, out var top, out _))
                {
                    var margin = 12.0;
                    var y = Math.Max(0, top - margin);
                    GalleryScroll.Offset = new Vector(GalleryScroll.Offset.X, y);
                    return;
                }

                // Fallback: bring the container into view if we can find it.
                if (GalleryItems.ContainerFromIndex(index) is Control container)
                    container.BringIntoView();
            }
            catch
            {
                // Non-fatal — user can still scroll manually.
            }
        }, DispatcherPriority.Loaded);
    }

    private void OneToOneScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // Keep 1:1 host at least as large as the viewport so small images center
        // instead of sticking to the top-left.
        if (OneToOneScroll is null || OneToOneHost is null) return;
        var vp = OneToOneScroll.Viewport;
        if (vp.Width <= 0 || vp.Height <= 0) return;
        OneToOneHost.MinWidth = vp.Width;
        OneToOneHost.MinHeight = vp.Height;
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        PersistWindowBounds(vm.Settings.Current);
        vm.Settings.SaveNow();
        vm.Dispose();
    }

    private void RestoreWindowBounds(AppSettings s)
    {
        if (_restoredBounds) return;
        _restoredBounds = true;

        try
        {
            var w = s.WindowWidth > 200 ? s.WindowWidth : 1100;
            var h = s.WindowHeight > 200 ? s.WindowHeight : 720;
            Width = w;
            Height = h;

            if (!double.IsNaN(s.WindowX) && !double.IsNaN(s.WindowY)
                && !double.IsInfinity(s.WindowX) && !double.IsInfinity(s.WindowY))
            {
                // Position after show is more reliable on some platforms.
                Position = new PixelPoint((int)Math.Round(s.WindowX), (int)Math.Round(s.WindowY));
            }

            if (s.WindowMaximized)
                WindowState = WindowState.Maximized;
        }
        catch
        {
            // Keep template defaults.
        }
    }

    private void PersistWindowBounds(AppSettings s)
    {
        try
        {
            // If fullscreen, keep previous normal/max geometry already in settings.
            if (WindowState == WindowState.FullScreen)
            {
                return;
            }

            if (WindowState == WindowState.Maximized)
            {
                s.WindowMaximized = true;
                // Position/Width while maximized are platform-dependent; leave last normal size.
                return;
            }

            s.WindowMaximized = false;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
            s.WindowX = Position.X;
            s.WindowY = Position.Y;
        }
        catch
        {
            // Ignore geometry write failures.
        }
    }

    private async Task<string?> PickFolderAsync(CancellationToken ct)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = IcedPicViewer.Core.Text.UiCopy.OpenFolder,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        if (folders.Count == 0) return null;
        return folders[0].TryGetLocalPath();
    }

    private void Tile_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: MediaItemViewModel item })
            return;
        if (DataContext is not MainViewModel vm)
            return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            vm.OpenItem(item);
            e.Handled = true;
        }
    }

    private void RevealMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && TryGetMenuItem(sender) is { } item)
            vm.RevealItemCommand.Execute(item);
    }

    private async void DeleteMenu_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && TryGetMenuItem(sender) is { } item)
            await vm.DeleteItemCommand.ExecuteAsync(item).ConfigureAwait(true);
    }

    private static MediaItemViewModel? TryGetMenuItem(object? sender)
    {
        if (sender is not Control start) return null;
        for (Control? c = start; c is not null; c = c.Parent as Control)
        {
            if (c.DataContext is MediaItemViewModel item)
                return item;
        }
        return null;
    }

    // Fullscreen edge hot-zones (media-player style): large edge bands,
    // expand while chrome is open so toolbar interaction stays stable.
    private const double ChromeTopZoneMinPx = 96;
    private const double ChromeBottomZoneMinPx = 72;
    private const double ChromeTopZoneHeightFraction = 0.12;
    private const double ChromeBottomZoneHeightFraction = 0.08;

    private void Window_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        if (!vm.IsFullscreen)
        {
            vm.NotifyPointerHotZone(inHotZone: false);
            return;
        }

        var p = e.GetPosition(this);
        var h = Bounds.Height;
        if (h <= 0) return;

        var topZone = Math.Max(ChromeTopZoneMinPx, h * ChromeTopZoneHeightFraction);
        var bottomZone = Math.Max(ChromeBottomZoneMinPx, h * ChromeBottomZoneHeightFraction);
        if (vm.IsChromeVisible)
        {
            // Keep open while the pointer stays over the chrome strips.
            topZone = Math.Max(topZone, 140);
            bottomZone = Math.Max(bottomZone, 96);
        }

        var inHotZone = p.Y <= topZone || p.Y >= h - bottomZone;
        vm.NotifyPointerHotZone(inHotZone);
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        // Keys do not re-show chrome on every press (was too sticky).
        // Only navigation-related peeks when already in viewer/fullscreen.

        switch (e.Key)
        {
            case Key.F5:
                if (vm.RefreshCommand.CanExecute(null))
                    vm.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F11:
                vm.ToggleFullscreenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape when vm.IsFullscreen:
                vm.IsFullscreen = false;
                e.Handled = true;
                break;
            // Optional: press Up to peek chrome in fullscreen without moving mouse.
            case Key.Up when vm.IsFullscreen && !vm.IsViewerOpen:
                vm.PeekChrome();
                e.Handled = true;
                break;
            case Key.Escape when vm.IsViewerOpen:
                vm.CloseViewerCommand.Execute(null);
                e.Handled = true;
                break;
            // Masonry gallery: page scroll (not prev/next image — those stay ← →).
            case Key.PageUp when !vm.IsViewerOpen:
                ScrollGalleryByPage(up: true);
                e.Handled = true;
                break;
            case Key.PageDown when !vm.IsViewerOpen:
                ScrollGalleryByPage(up: false);
                e.Handled = true;
                break;
            case Key.Left when vm.IsViewerOpen:
                if (vm.NavigatePreviousCommand.CanExecute(null))
                    vm.NavigatePreviousCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Right when vm.IsViewerOpen:
                if (vm.NavigateNextCommand.CanExecute(null))
                    vm.NavigateNextCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F when vm.IsViewerOpen:
                vm.ToggleFitModeCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Space when vm.IsViewerOpen:
                vm.HandleSpaceCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.D0 or Key.NumPad0 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D1 or Key.NumPad1 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D2 or Key.NumPad2 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D3 or Key.NumPad3 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D4 or Key.NumPad4 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D5 or Key.NumPad5 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D6 or Key.NumPad6 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D7 or Key.NumPad7 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D8 or Key.NumPad8 when vm.IsViewerOpen && vm.IsVideoSelected:
            case Key.D9 or Key.NumPad9 when vm.IsViewerOpen && vm.IsVideoSelected:
                SeekFromDigitKey(vm, e.Key);
                e.Handled = true;
                break;
            case Key.Delete:
                if (vm.DeleteSelectedCommand.CanExecute(null))
                    vm.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static void SeekFromDigitKey(MainViewModel vm, Key key)
    {
        var digit = key switch
        {
            Key.D0 or Key.NumPad0 => 0,
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => -1,
        };
        if (digit < 0) return;
        vm.SeekVideoToPercent(digit * 10);
    }

    /// <summary>
    /// PageUp/PageDown: jump the masonry gallery by ~one viewport (small overlap).
    /// </summary>
    private void ScrollGalleryByPage(bool up)
    {
        var sv = GalleryScroll;
        var viewport = sv.Viewport.Height;
        if (viewport <= 0) return;

        var delta = viewport * 0.9;
        var maxOffset = Math.Max(0, sv.Extent.Height - viewport);
        var y = up
            ? Math.Max(0, sv.Offset.Y - delta)
            : Math.Min(maxOffset, sv.Offset.Y + delta);
        sv.Offset = new Vector(sv.Offset.X, y);
    }

    private async void GalleryScroll_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (DataContext is not MainViewModel vm) return;
        if (!vm.CanLoadMore || vm.IsLoadingMore) return;

        var extent = sv.Extent.Height;
        var viewport = sv.Viewport.Height;
        var offset = sv.Offset.Y;
        if (extent <= viewport) return;

        var distanceFromBottom = extent - (offset + viewport);
        if (distanceFromBottom < 800)
        {
            if (!_autoLoadArmed) return;
            _autoLoadArmed = false;
            if (vm.LoadMoreCommand.CanExecute(null))
                await vm.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        else
        {
            _autoLoadArmed = true;
        }
    }
}
