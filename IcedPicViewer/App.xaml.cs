using IcedPicViewer.Services.Implementations;
using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace IcedPicViewer;

public partial class App : Application
{
    private static IServiceProvider? _services;
    private Window? _window;

    public static Window? MainWindow => ((App)Current)._window;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        ConfigureServices(services);
        _services = services.BuildServiceProvider();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IDirectoryScanner, DirectoryScanner>();
        services.AddSingleton<IImageLoader, ImageLoader>();

        services.AddSingleton<MainViewModel>();
        services.AddSingleton<GalleryViewModel>();
        services.AddSingleton<ImageViewModel>();
    }

    public static T GetService<T>() where T : class
    {
        if (_services == null)
            throw new InvalidOperationException("Services not configured");
        return _services.GetRequiredService<T>();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
