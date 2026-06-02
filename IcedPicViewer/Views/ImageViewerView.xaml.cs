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
    private bool _isFitMode = true;
    private double _minimapWidth = 150;
    private double _minimapHeight = 120;
    private Rectangle? _viewportRect;

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm && vm.CurrentImage != null)
        {
            var filePath = vm.CurrentImage.Path;
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
        DataContext = App.GetService<ImageViewModel>();

        // Named handlers (not lambdas) so Unloaded can unsubscribe — otherwise
        // the view would be kept alive by the lambda capture past navigation,
        // and subsequent DisplayImageChanged firings would hit a defunct visual tree.
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm)
        {
            vm.DisplayImageChanged += OnDisplayImageChanged;
            vm.NavigatePreviousCommand.NotifyCanExecuteChanged();
            vm.NavigateNextCommand.NotifyCanExecuteChanged();
        }
        // Keyboard handling is centralized in MainWindow.RootGrid_KeyDown
        // to avoid duplication. See MainWindow.xaml.cs for viewer navigation keys.
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is ImageViewModel vm)
        {
            vm.DisplayImageChanged -= OnDisplayImageChanged;
        }
    }

    private void OnDisplayImageChanged(object? sender, EventArgs e)
    {
        UpdateMinimapDeferred();
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 关键修复：进入单图模式时请求键盘焦点。
        // RootFrame.KeyDown 只有在 RootFrame 或其子元素拥有焦点时才会收到按键事件。
        // Navigate 后焦点不会自动进入新页面，导致左右键初始不响应（必须先点按钮）。
        // 使用 DispatcherQueue 延迟一帧确保视觉树完全就绪。
        DispatcherQueue.GetForCurrentThread().TryEnqueue(() =>
        {
            // Focus the main content container so that arrow keys are reliably
            // delivered to the RootGrid handler in MainWindow.
            MainContentGrid?.Focus(FocusState.Programmatic);
        });
    }

    private async void UpdateMinimapDeferred()
    {
        if (!_isFitMode)
        {
            await Task.Delay(50);
            UpdateMinimap();
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
