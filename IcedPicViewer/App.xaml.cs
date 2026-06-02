// Copyright (c) IcedPicViewer. All rights reserved.

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

        // Navigation
        services.AddSingleton<INavigationService, NavigationService>();

        // Folder picker (modern Windows App SDK picker)
        services.AddTransient<IFolderPickerService, FolderPickerService>();

        // ViewModels - appropriate lifetimes
        // GalleryViewModel: Singleton (owns current gallery + file watcher + shared collection)
        // ImageViewModel: Singleton (instance prepared in GalleryView with ShowImageAsync
        //   is the same one received in ImageViewerView; this restores single image mode
        //   functionality while we keep the masonry visual).
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
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Activate();
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        // Dispose DI-owned singletons that hold OS resources (file watcher, cts, semaphore, cache).
        // IServiceProvider doesn't auto-dispose singletons, so we do it explicitly here.
        if (_services is null) return;
        (_services.GetService<GalleryViewModel>() as IDisposable)?.Dispose();
        (_services.GetService<ImageViewModel>() as IDisposable)?.Dispose();
    }
}
