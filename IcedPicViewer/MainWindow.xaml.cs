// Copyright (c) IcedPicViewer. All rights reserved.

using IcedPicViewer.Services.Interfaces;
using IcedPicViewer.ViewModels;
using IcedPicViewer.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

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

        // Native WM_KEYDOWN hook for ImageViewerView shortcuts (Left/Right/Delete/Escape).
        // All XAML-layer mechanisms (AddHandler(KeyDownEvent), KeyboardAccelerator)
        // ultimately depend on a focused element to start the routed event pipeline,
        // and in MSIX packaged mode after Frame.Navigate the focus state is
        // unreliable (verified empirically: a306722 KeyboardAccelerator + earlier
        // AddHandler attempts both fail to invoke). The only path guaranteed to
        // fire regardless of focus is a native WndProc subclass on the window's
        // HWND — the OS delivers WM_KEYDOWN to the window unconditionally.
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclassProc = SubclassWndProc;
        SetWindowSubclass(hwnd, _subclassProc, 0, IntPtr.Zero);
    }

    // ---- Native window subclass for window-level keyboard shortcuts ----

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    private const int WM_KEYDOWN = 0x0100;

    // Hold a strong reference so the GC doesn't collect the delegate while
    // the native side still has the function pointer.
    private SubclassProc? _subclassProc;

    private IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_KEYDOWN)
        {
            // We don't suppress the message — even when we act on it, we let
            // DefSubclassProc run so the XAML framework still receives the
            // keystroke. Our XAML layer has no other handler that would
            // double-fire on these specific keys, so this is safe.
            HandleViewerKey((Windows.System.VirtualKey)wParam.ToInt32());
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void HandleViewerKey(Windows.System.VirtualKey key)
    {
        if (RootFrame.Content is not ImageViewerView viewer || viewer.DataContext is not ImageViewModel vm)
            return;

        switch (key)
        {
            case Windows.System.VirtualKey.Left:
                if (vm.NavigatePreviousCommand.CanExecute(null))
                    vm.NavigatePreviousCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Right:
                if (vm.NavigateNextCommand.CanExecute(null))
                    vm.NavigateNextCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Delete:
                if (vm.DeleteCommand.CanExecute(null))
                    vm.DeleteCommand.Execute(null);
                break;
            case Windows.System.VirtualKey.Escape:
                vm.CloseCommand.Execute(null);
                break;
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

            Trace.TraceInformation($"Saved window state: {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"SaveWindowState error: {ex}");
        }
    }

    private void RestoreWindowState()
    {
        try
        {
            var file = GetSettingsPath();
            if (!File.Exists(file))
            {
                Trace.TraceWarning("RestoreWindowState: file not found");
                return;
            }

            var content = File.ReadAllText(file);
            var parts = content.Split(',');
            if (parts.Length != 4)
            {
                Trace.TraceWarning($"RestoreWindowState: invalid parts count {parts.Length}");
                return;
            }

            if (!double.TryParse(parts[0], out var x) ||
                !double.TryParse(parts[1], out var y) ||
                !double.TryParse(parts[2], out var width) ||
                !double.TryParse(parts[3], out var height))
            {
                Trace.TraceWarning($"RestoreWindowState: failed to parse values");
                return;
            }

            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)x, (int)y, (int)width, (int)height));
            Trace.TraceInformation($"Restored window state: {content}");
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RestoreWindowState error: {ex}");
        }
    }
}
