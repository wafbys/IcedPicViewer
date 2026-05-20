using IcedPicViewer.Models;
using IcedPicViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IcedPicViewer.Views;

public sealed partial class GalleryView : Page
{
    public GalleryViewModel ViewModel { get; }

    private ImageItem? _selectedItemForDelete;
    private int _isNavigatingToViewer;

    public GalleryView()
    {
        this.InitializeComponent();

        ViewModel = App.GetService<GalleryViewModel>();
        DataContext = ViewModel;
    }

    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (ViewModel.LastViewedIndex >= 0 && ViewModel.LastViewedIndex < ViewModel.Images.Count)
        {
            var index = ViewModel.LastViewedIndex;
            var panel = FindMasonryPanel(MainItemsControl);
            if (panel != null)
            {
                var y = panel.GetItemYPosition(index);
                MainScrollViewer.ChangeView(null, y, null, true);
            }
        }
    }

    private static Controls.MasonryPanel? FindMasonryPanel(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Controls.MasonryPanel panel)
                return panel;
            var result = FindMasonryPanel(child);
            if (result != null) return result;
        }
        return null;
    }

    private void ImageItem_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageItem item)
        {
            OpenImageViewer(item);
        }
    }

    private void ImageItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.DataContext is ImageItem item)
        {
            _selectedItemForDelete = item;
        }
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var item = _selectedItemForDelete;
        _selectedItemForDelete = null;

        await ViewModel.DeleteImageAsync(item);
    }

    private void ImageItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.FindName("NameOverlay") is FrameworkElement overlay)
        {
            overlay.Visibility = Visibility.Visible;
        }
    }

    private void ImageItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element && element.FindName("NameOverlay") is FrameworkElement overlay)
        {
            overlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenFileLocation_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedItemForDelete == null) return;

        var filePath = _selectedItemForDelete.Path;
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

    private async void OpenImageViewer(ImageItem item)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _isNavigatingToViewer, 1, 0) != 0) return;

        try
        {
            var imageViewModel = App.GetService<ImageViewModel>();
            await imageViewModel.ShowImageAsync(item);

            var frame = FindFrame();
            if (frame != null)
            {
                frame.Navigate(typeof(ImageViewerView));
            }
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _isNavigatingToViewer, 0);
        }
    }

    private static Frame? FindFrame()
    {
        var window = App.MainWindow;
        if (window?.Content is Frame f)
            return f;

        if (window?.Content is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is Frame frame)
                    return frame;
            }
        }

        return null;
    }
}
