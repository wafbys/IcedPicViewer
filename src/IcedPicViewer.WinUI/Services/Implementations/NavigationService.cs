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

        // Re-navigating to the page that is already showing would push a
        // duplicate back-stack entry (and rebuild the page for nothing) —
        // treat it as a no-op. A card double-click used to do exactly
        // that: two ViewerView entries in a row, so the first back click
        // landed on the other viewer instance and read as a dead click.
        if (_frame.Content is TPage) return;

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
