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

        // Show the build's commit hash in the title so the running version is
        // unambiguous (matters because dotnet run launches under a debug
        // package identity, so the bin path alone doesn't tell you which
        // commit you have on screen). Both AppTitleBar (visual custom title
        // bar) and AppWindow.Title (native window title — used by taskbar
        // hover, Alt+Tab, screen reader) are updated so a11y and OS shells
        // see the version too.
        var titleWithHash = $"IcedPicViewer ({BuildInfo.CommitShort})";
        AppTitleBar.Title = titleWithHash;
        AppWindow.Title = titleWithHash;

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
        //
        // IMPORTANT: register the subclass in the Activated event, not the
        // constructor. WinUI 3 lazy-creates the window HWND on first show, and
        // WindowNative.GetWindowHandle(this) in the ctor returned IntPtr.Zero
        // on this project (a306722+bf04e5e both still failed because of this
        // — the hook was registered against a non-existent window). The
        // Activated event is the first guarantee that the HWND is real.
        Activated += OnWindowActivated;
    }

    private bool _subclassRegistered;

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_subclassRegistered) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        if (hwnd == IntPtr.Zero)
        {
            // Shouldn't happen by Activated, but be defensive.
            LogKbd($"OnWindowActivated but hwnd still zero; will retry next activation");
            return;
        }
        _subclassProc = SubclassWndProc;
        if (SetWindowSubclass(hwnd, _subclassProc, 0, IntPtr.Zero))
        {
            _subclassRegistered = true;
            LogKbd($"subclass registered on hwnd=0x{hwnd.ToInt64():X}");
        }
        else
        {
            LogKbd($"SetWindowSubclass FAILED on hwnd=0x{hwnd.ToInt64():X}, gle={Marshal.GetLastWin32Error()}");
        }
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
            var key = (Windows.System.VirtualKey)wParam.ToInt32();
            LogKbd($"WM_KEYDOWN vk={key} page={RootFrame.Content?.GetType().Name ?? "<null>"}");
            HandleViewerKey(key);
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    // ---- Diagnostic log for keyboard hook verification ----
    // Written to %LOCALAPPDATA%\IcedPicViewer\kbd.log so the user can cat it
    // and see whether the subclass is firing at all. Delete the file to
    // start fresh; the path is fixed and deterministic.

    private const string KbdLogPath = "kbd.log";

    private static readonly object _logLock = new();
    private static void LogKbd(string msg)
    {
        lock (_logLock)
        {
            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "IcedPicViewer");
                Directory.CreateDirectory(dir);
                File.AppendAllText(
                    Path.Combine(dir, KbdLogPath),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\n");
            }
            catch
            {
                // Logging must never throw out of the WndProc — would crash the app.
            }
        }
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
