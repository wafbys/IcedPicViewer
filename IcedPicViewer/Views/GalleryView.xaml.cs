using IcedPicViewer.Models;
using IcedPicViewer.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace IcedPicViewer.Views;

public sealed partial class GalleryView : Page
{
    public GalleryViewModel ViewModel { get; }

    private ImageItem? _selectedItemForDelete;

    public GalleryView()
    {
        this.InitializeComponent();

        ViewModel = App.GetService<GalleryViewModel>();
        DataContext = ViewModel;
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
        if (!string.IsNullOrEmpty(filePath))
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
        }
    }

    private async void OpenImageViewer(ImageItem item)
    {
        var imageViewModel = App.GetService<ImageViewModel>();
        await imageViewModel.ShowImageAsync(item);

        var frame = FindFrame();
        if (frame != null)
        {
            frame.Navigate(typeof(ImageViewerView));
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
