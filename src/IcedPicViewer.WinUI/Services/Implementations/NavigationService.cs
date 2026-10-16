using IcedPicViewer.Services.Interfaces;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace IcedPicViewer.Services.Implementations;

public sealed class NavigationService : INavigationService
{
    private Frame? _frame;

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void Initialize(Frame frame)
    {
        _frame = frame ?? throw new ArgumentNullException(nameof(frame));
    }

    public void NavigateTo<TPage>(NavigationTransitionInfo? transition = null) where TPage : Page
    {
        if (_frame == null)
            throw new InvalidOperationException("NavigationService has not been initialized with a Frame.");

        if (transition is null)
        {
            _frame.Navigate(typeof(TPage));
        }
        else
        {
            _frame.Navigate(typeof(TPage), null, transition);
        }
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
