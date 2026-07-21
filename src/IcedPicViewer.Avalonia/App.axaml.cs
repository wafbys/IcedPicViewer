using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using IcedPicViewer.Avalonia.ViewModels;
using IcedPicViewer.Avalonia.Views;
using IcedPicViewer.Core.Media;

namespace IcedPicViewer.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Warm FFmpeg off the UI thread so the first video thumb is snappy.
        _ = Task.Run(FFmpegBootstrap.EnsureInitialized);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}