// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using IcedPicViewer.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.IO;

namespace IcedPicViewer;

public sealed partial class MainWindow : Window
{
    private const string SettingsFile = "window_settings.txt";
    private const string AppDataFolder = "IcedPicViewer";

    private static string GetSettingsPath()
    {
        // Unpackaged WinUI apps should not write next to the exe (Program Files may be
        // read-only or flagged by AV). Use %LOCALAPPDATA%\<AppName>\ instead.
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            AppDataFolder);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, SettingsFile);
    }

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        RestoreWindowState();

        // Initialize navigation service with the root frame
        var navigationService = App.GetService<INavigationService>();
        navigationService.Initialize(RootFrame);

        RootFrame.Navigate(typeof(GalleryView));

        AppWindow.Closing += AppWindow_Closing;

        // Centralized keyboard handling for ImageViewerView (Left/Right/Delete/Escape).
        // Attached to RootGrid (higher in tree) + handledEventsToo=true to ensure
        // arrow keys are caught reliably even after Frame navigation.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(RootGrid_KeyDown), true);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Centralized keyboard handling for ImageViewerView.
        // We check RootFrame.Content because that's where the current page lives.
        if (RootFrame.Content is ImageViewerView viewer && viewer.DataContext is ImageViewModel vm)
        {
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                    if (vm.NavigatePreviousCommand.CanExecute(null))
                        vm.NavigatePreviousCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Right:
                    if (vm.NavigateNextCommand.CanExecute(null))
                        vm.NavigateNextCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Delete:
                    if (vm.DeleteCommand.CanExecute(null))
                        vm.DeleteCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    vm.CloseCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }
    }

    private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        SaveWindowState();
    }

    private void SaveWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            var tempFile = file + ".tmp";
            var content = $"{AppWindow.Position.X},{AppWindow.Position.Y},{AppWindow.Size.Width},{AppWindow.Size.Height}";

            File.WriteAllText(tempFile, content);
            File.Move(tempFile, file, overwrite: true);

            System.Diagnostics.Trace.TraceInformation($"Saved window state: {content}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"SaveWindowState error: {ex}");
        }
    }

    private void RestoreWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            if (!File.Exists(file))
            {
                System.Diagnostics.Trace.TraceWarning("RestoreWindowState: file not found");
                return;
            }

            var content = File.ReadAllText(file);
            var parts = content.Split(',');
            if (parts.Length != 4)
            {
                System.Diagnostics.Trace.TraceWarning($"RestoreWindowState: invalid parts count {parts.Length}");
                return;
            }

            if (!double.TryParse(parts[0], out var x) ||
                !double.TryParse(parts[1], out var y) ||
                !double.TryParse(parts[2], out var width) ||
                !double.TryParse(parts[3], out var height))
            {
                System.Diagnostics.Trace.TraceWarning($"RestoreWindowState: failed to parse values");
                return;
            }

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)x, (int)y, (int)width, (int)height));
            System.Diagnostics.Trace.TraceInformation($"Restored window state: {content}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"RestoreWindowState error: {ex}");
        }
    }
}
