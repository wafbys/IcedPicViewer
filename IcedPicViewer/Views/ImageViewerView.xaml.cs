using IcedPicViewer.ViewModels;
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
        Loaded += (_, _) =>
        {
            if (DataContext is ImageViewModel vm)
            {
                vm.DisplayImageChanged += (_, _) => UpdateMinimapDeferred();
                vm.NavigatePreviousCommand.NotifyCanExecuteChanged();
                vm.NavigateNextCommand.NotifyCanExecuteChanged();
            }
        };
    }

    private async void UpdateMinimapDeferred()
    {
        if (!_isFitMode)
        {
            await Task.Delay(50);
            UpdateMinimap();
        }
    }

    private async void FitModeBtn_Click(object sender, RoutedEventArgs e)
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
            await Task.Delay(100);
            UpdateMinimap();
            await Task.Delay(200);
            UpdateMinimap();
        }
    }

    private void ActualSizeContainer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateMinimapViewport();
    }

    private void ActualSizeImage_Loaded(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"ActualSizeImage_Loaded: IsFitMode={_isFitMode}");
        if (!_isFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeContainer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"SizeChanged: IsFitMode={_isFitMode}, NewSize={e.NewSize}");
        if (!_isFitMode)
        {
            UpdateMinimap();
        }
    }

    private void ActualSizeImage_ImageOpened(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"ImageOpened triggered: IsFitMode={_isFitMode}, Source Tag={ActualSizeImage?.Source?.GetHashCode()}");
        if (!_isFitMode)
        {
            MinimapImage.Source = null;
            System.Diagnostics.Debug.WriteLine("Calling UpdateMinimap");
            UpdateMinimap();
            System.Diagnostics.Debug.WriteLine($"MinimapImage.Source after update: {MinimapImage?.Source?.GetHashCode()}");
        }
    }

    private void UpdateMinimap()
    {
        System.Diagnostics.Debug.WriteLine($"UpdateMinimap: ActualSizeImage.Source={ActualSizeImage?.Source}");
        double width = 0;
        double height = 0;

        if (ActualSizeImage?.Source is not BitmapImage bmp)
        {
            System.Diagnostics.Debug.WriteLine("Source is not BitmapImage or is null");
            return;
        }

        if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
        {
            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
            System.Diagnostics.Debug.WriteLine($"Using PixelWidth/Height: {width}x{height}");
        }
        else if (ActualSizeImage!.ActualWidth > 0 && ActualSizeImage!.ActualHeight > 0)
        {
            width = ActualSizeImage!.ActualWidth;
            height = ActualSizeImage!.ActualHeight;
            System.Diagnostics.Debug.WriteLine($"Using ActualWidth/Height: {width}x{height}");
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"No valid dimensions: Pixel={bmp.PixelWidth}x{bmp.PixelHeight}, Actual={ActualSizeImage!.ActualWidth}x{ActualSizeImage!.ActualHeight}");
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
                Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 0, 120, 215)),
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
