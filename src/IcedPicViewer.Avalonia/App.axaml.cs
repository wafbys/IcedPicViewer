using System;
using System.Diagnostics;
using System.IO;
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
        // Best-effort cleanup of orphaned temp files from previous crashes.
        _ = Task.Run(CleanupOrphanedTempFiles);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void CleanupOrphanedTempFiles()
    {
        try
        {
            var tempDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "IcedPicViewer", "TempVideo");
            if (!Directory.Exists(tempDir)) return;

            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var file in Directory.EnumerateFiles(tempDir))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (Exception ex)
                {
                    Trace.TraceWarning($"CleanupOrphanedTempFiles: skip {file}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"CleanupOrphanedTempFiles: {ex.Message}");
        }
    }
}
