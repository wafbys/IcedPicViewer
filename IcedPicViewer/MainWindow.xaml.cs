using IcedPicViewer.Views;
using Microsoft.UI.Xaml;
using System.IO;

namespace IcedPicViewer;

public sealed partial class MainWindow : Window
{
    private const string SettingsFile = "window_settings.txt";

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RestoreWindowState();

        RootFrame.Navigate(typeof(GalleryView));

        AppWindow.Closing += AppWindow_Closing;
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        SaveWindowState();
    }

    private void SaveWindowState()
    {
        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var file = Path.Combine(exeDir, SettingsFile);
            var content = $"{AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}";
            File.WriteAllText(file, content);
            System.Diagnostics.Debug.WriteLine($"Saved window state: {content}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"SaveWindowState error: {ex}");
        }
    }

    private void RestoreWindowState()
    {
        try
        {
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            var file = Path.Combine(exeDir, SettingsFile);
            if (!File.Exists(file))
            {
                System.Diagnostics.Debug.WriteLine("RestoreWindowState: file not found");
                return;
            }

            var content = File.ReadAllText(file);
            var parts = content.Split(',');
            if (parts.Length != 4)
            {
                System.Diagnostics.Debug.WriteLine($"RestoreWindowState: invalid parts count {parts.Length}");
                return;
            }

            if (!double.TryParse(parts[0], out var x) ||
                !double.TryParse(parts[1], out var y) ||
                !double.TryParse(parts[2], out var width) ||
                !double.TryParse(parts[3], out var height))
            {
                System.Diagnostics.Debug.WriteLine($"RestoreWindowState: failed to parse values");
                return;
            }

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)x, (int)y, (int)width, (int)height));
            System.Diagnostics.Debug.WriteLine($"Restored window state: {content}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"RestoreWindowState error: {ex}");
        }
    }
}
