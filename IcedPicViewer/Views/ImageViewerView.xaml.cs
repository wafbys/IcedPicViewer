// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace IcedPicViewer.Views;

public sealed partial class ImageViewerView : Page
{
    // Exposed for x:Bind in the page-level markup. Constructor-assigned so
    // it's safe to read in OnLoaded (which fires after the ctor). DI gives
    // the same singleton that GalleryView prepared via ShowImageAsync, so
    // state (CurrentImage, CurrentIndex, ...) survives across navigations.
    public ImageViewModel ViewModel { get; }

    private bool _isFitMode = true;
    private double _minimapWidth = 150;
    private double _minimapHeight = 120;
    private Rectangle? _viewportRect;

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentImage != null)
        {
            var source = ViewModel.CurrentImage.Source;
            // For archive entries we can't select the entry itself
            // (Explorer doesn't understand zip/rar/7z contents), so
            // highlight the containing archive file instead. The user
            // immediately sees which .zip / .rar / .7z the image is
            // inside, which is the actionable "where is this file"
            // answer.
            var filePath = source.Path;
            if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                };
                System.Diagnostics.Process.Start(startInfo);
            }
        }
    }

    public ImageViewerView()
    {
        this.InitializeComponent();
        ViewModel = App.GetService<ImageViewModel>();

        // Named handlers (not lambdas) so Unloaded can unsubscribe — otherwise
        // the view would be kept alive by the lambda capture past navigation,
        // and subsequent DisplayImageChanged firings would hit a defunct visual tree.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.DisplayImageChanged += OnDisplayImageChanged;
        ViewModel.NavigatePreviousCommand.NotifyCanExecuteChanged();
        ViewModel.NavigateNextCommand.NotifyCanExecuteChanged();

        // 键盘处理统一在 MainWindow.RootGrid_KeyDown,见 MainWindow.xaml.cs。
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.DisplayImageChanged -= OnDisplayImageChanged;
    }

    private void OnDisplayImageChanged(object? sender, EventArgs e)
    {
        UpdateMinimapDeferred();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 关键修复:进入单图模式时请求键盘焦点。
        // MainWindow.RootGrid 是用 AddHandler(KeyDownEvent, ..., handledEventsToo=true)
        // 注册的,handler 收到事件的前提是 Page 子树里有 focused element 作为路由源;
        // 没焦点时 KeyDown 根本不路由,handler 不会被调用。
        // Navigate 后焦点不会自动进入新页面,直接 Focus() 又常因 MainContentGrid 还没
        // 完成 layout 而变 silent no-op,所以用 DispatcherQueue 延迟一帧(~16ms @ 60fps)
        // 等 layout 完再设焦点,既稳又快。
        // Frame 默认会缓存 page instance,后续 navigate 同一 Page 时 Loaded 不会
        // 重触,但 OnNavigatedTo 每次 navigate 都触发,这里作为唯一入口最稳。
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            MainContentGrid.Focus(FocusState.Programmatic);
        });
    }

    private void UpdateMinimapDeferred()
    {
        if (!_isFitMode)
        {
            // Defer one dispatcher tick so the new image's layout pass
            // completes before we read ViewportWidth/Offset from the
            // ScrollViewer. Replaces the older Task.Delay(50) hack —
            // dispatcher tick (~16 ms at 60 fps) is faster and correct
            // (it fires AFTER layout, not on a wall-clock guess).
            DispatcherQueue.GetForCurrentThread().TryEnqueue(UpdateMinimap);
        }
    }

    private void FitModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isFitMode = !_isFitMode;
        FitModeBtn.Content = _isFitMode ? "Fit" : "1:1";

        if (_isFitMode)
        {
            FitContainer.Visibility = Visibility.Visible;
            ActualSizeContainer.Visibility = Visibility.Collapsed;
            MinimapOverlay.Visibility = Visibility.Collapsed;
        }
        else
        {
            FitContainer.Visibility = Visibility.Collapsed;
            ActualSizeContainer.Visibility = Visibility.Visible;
            MinimapOverlay.Visibility = Visibility.Visible;
            // UpdateMinimap 会在 ActualSizeImage.ImageOpened / ActualSizeContainer.SizeChanged
            // 事件里被自动触发;此处无需再手动调用,更不应用 Task.Delay 猜布局时机。
        }
    }

    private void ActualSizeContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateMinimapViewport();
    }

    private void ActualSizeImage_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_isFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        if (!_isFitMode)
        {
            MinimapImage.Source = null;
            UpdateMinimap();
        }
    }

    private void UpdateMinimap()
    {
        double width = 0;
        double height = 0;

        if (ActualSizeImage?.Source is not BitmapImage bmp)
        {
            return;
        }

        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
        }
        else if (ActualSizeImage!.ActualWidth > 0 && ActualSizeImage!.ActualHeight > 0)
        {
            width = ActualSizeImage!.ActualWidth;
            height = ActualSizeImage!.ActualHeight;
        }
        else
        {
            return;
        }

        if (width > 0 && height > 0)
        {
            MinimapImage.Source = null;
            MinimapImage.Source = bmp;
            MinimapImage.Width = _minimapWidth;
            MinimapImage.Height = _minimapHeight;

            MinimapViewport.Children.Clear();
            _viewportRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
                StrokeThickness = 1,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 120, 215))
            };
            Canvas.SetLeft(_viewportRect, 0);
            Canvas.SetTop(_viewportRect, 0);
            MinimapViewport.Children.Add(_viewportRect);

            MinimapViewport.Width = _minimapWidth;
            MinimapViewport.Height = _minimapHeight;

            UpdateMinimapViewport();
        }
    }

    private void UpdateMinimapViewport()
    {
        if (_viewportRect == null || ActualSizeImage.Source == null) return;

        if (ActualSizeImage.Source is BitmapImage bmp && bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            var imageWidth = bmp.PixelWidth;
            var imageHeight = bmp.PixelHeight;

            var viewWidth = ActualSizeContainer.ViewportWidth;
            var viewHeight = ActualSizeContainer.ViewportHeight;
            var scrollX = ActualSizeContainer.HorizontalOffset;
            var scrollY = ActualSizeContainer.VerticalOffset;

            var scaleX = _minimapWidth / imageWidth;
            var scaleY = _minimapHeight / imageHeight;

            var rectWidth = viewWidth * scaleX;
            var rectHeight = viewHeight * scaleY;
            var rectX = scrollX * scaleX;
            var rectY = scrollY * scaleY;

            _viewportRect.Width = Math.Max(rectWidth, 4);
            _viewportRect.Height = Math.Max(rectHeight, 4);
            Canvas.SetLeft(_viewportRect, rectX);
            Canvas.SetTop(_viewportRect, rectY);
        }
    }

    private void MinimapImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pos = e.GetCurrentPoint(MinimapViewport).Position;
        ScrollToMinimapPosition(pos);
        MinimapViewport.CapturePointer(e.Pointer);
    }

    private void MinimapImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (MinimapViewport.PointerCaptures != null && MinimapViewport.PointerCaptures.Count > 0)
        {
            var pos = e.GetCurrentPoint(MinimapViewport).Position;
            ScrollToMinimapPosition(pos);
        }
    }

    private void MinimapImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        MinimapViewport.ReleasePointerCapture(e.Pointer);
    }

    private void ScrollToMinimapPosition(Point pos)
    {
        if (ActualSizeImage.Source is not BitmapImage bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
            return;

        var imageWidth = bmp.PixelWidth;
        var imageHeight = bmp.PixelHeight;

        var scaleX = imageWidth / _minimapWidth;
        var scaleY = imageHeight / _minimapHeight;

        var targetX = pos.X * scaleX;
        var targetY = pos.Y * scaleY;

        var viewWidth = ActualSizeContainer.ViewportWidth;
        var viewHeight = ActualSizeContainer.ViewportHeight;

        targetX = Math.Max(0, Math.Min(targetX - viewWidth / 2, imageWidth - viewWidth));
        targetY = Math.Max(0, Math.Min(targetY - viewHeight / 2, imageHeight - viewHeight));

        ActualSizeContainer.ChangeView(targetX, targetY, null);
    }
}
