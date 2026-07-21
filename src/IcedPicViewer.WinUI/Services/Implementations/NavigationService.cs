using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Controls;

namespace IcedPicViewer.Services.Implementations;

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public void NavigateTo<TPage>() where TPage : Page
    {
        if (_frame == null)
            throw new InvalidOperationException("NavigationService has not been initialized with a Frame.");

        _frame.Navigate(typeof(TPage));
    }

    public void GoBack()
    {
        if (_frame == null)
            throw new InvalidOperationException("NavigationService has not been initialized with a Frame.");

        if (_frame.CanGoBack)
        {
            _frame.GoBack();
        }
    }
}
